// ============================================================================
// OrderParameterValidator + PendingOrderCascadeCanceller — v12.10.0 [Step 4/7] Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 폼 검증'이 다루지 않는 발주 파이프라인 파멸 리스크):
//     ① "호가단위(Tick Size)/최소·최대 발주금액 검증이 UI 폼마다 제각각 산발적으로 구현" — 산발 구현은 누락되기
//        쉽다(실측: ShadowBroker.Place 의 노이즈 활성 경로가 가격 검증을 누락했던 것과 동일 계열 위험 — 감사에서
//        발견·수정됨). 단일 δ=0 초크포인트로 통일하면 신규 UI/브로커 어댑터가 늘어도 검증이 빠질 수 없다.
//     ② "브로커 페일오버 순간 이전 브로커에 남은 미체결 주문이 방치" — BrokerFailoverSentinel(v12.0.4)은 신규
//        주문 경로를 0ms Disarm 하고 자격증명을 소거하지만, ★이미 접수된 미체결 잔량 자체를 취소 처리하는
//        개념이 없다(실측 확인 — 전용 데이터 구조 부재). 방치된 미체결 주문은 장애 복구 후 예기치 않게 체결될 위험.
//
//   ── Think Beyond (이중 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Order Parameter Struct Validator(Idea A) — 호가단위 배수 검사 + 최소/최대 발주금액(notional)
//        범위 검사를 값타입 반환으로 O(1) 산출. notional = price×qty 곱은 ★Int128 경유로 오버플로 안전.
//     ② Atomic Pending Order Cascade Canceller(Idea B) — 브로커별 미체결 슬롯을 추적, 페일오버 통보 시 해당
//        브로커의 전량을 원자적으로 Cancelled 전환(CAS, 슬롯당 1회만 성공 — 이중 취소/중복 카운트 불가). ★임시 인증
//        토큰 소거는 BrokerFailoverSentinel.PoisonedSecretBuffer 가 이미 담당 — 이 엔진은 '미체결 잔량' 만 보완한다.
//
//   불변: 결정론(순수 값 반환) · δ=0(Validate/ValidateBatch/Register/CancelAllForBroker 힙 0) · fail-closed(qty/price
//        ≤0 → 즉시 거부) · liveArmed:false(수치 검증·주문 슬롯 상태 관리일 뿐 — 발주 인가·네트워크 접촉 0).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Zero-Alloc Order Parameter Struct Validator
    // ════════════════════════════════════════════════════════════════════════
    public enum OrderValidationResult : byte
    {
        Ok = 0, InvalidQty = 1, InvalidPrice = 2, TickSizeViolation = 3,
        BelowMinNotional = 4, AboveMaxNotional = 5,
    }

    public static class OrderParameterValidator
    {
        /// <summary>단일 주문 파라미터 O(1) 검증(δ=0·값타입). tickSizeRaw≤0 이면 호가단위 검사 생략(자유소수 자산).
        /// minNotionalRaw/maxNotionalRaw≤0 이면 해당 축 비활성.</summary>
        public static OrderValidationResult Validate(
            long priceRaw, long qtyRaw, long tickSizeRaw, long minNotionalRaw, long maxNotionalRaw)
        {
            if (qtyRaw <= 0) return OrderValidationResult.InvalidQty;
            if (priceRaw <= 0) return OrderValidationResult.InvalidPrice;
            if (tickSizeRaw > 0 && priceRaw % tickSizeRaw != 0) return OrderValidationResult.TickSizeViolation;

            Int128 notional = (Int128)priceRaw * qtyRaw;   // ★Int128 경유 — 대형 qty×price 오버플로 안전
            if (minNotionalRaw > 0 && notional < minNotionalRaw) return OrderValidationResult.BelowMinNotional;
            if (maxNotionalRaw > 0 && notional > maxNotionalRaw) return OrderValidationResult.AboveMaxNotional;
            return OrderValidationResult.Ok;
        }

        /// <summary>배치 검증(δ=0 — 호출자 제공 결과 버퍼). price/qty span 은 동일 길이, 결과 span 도 동일 길이여야 한다.</summary>
        public static void ValidateBatch(
            ReadOnlySpan<long> priceRaw, ReadOnlySpan<long> qtyRaw,
            long tickSizeRaw, long minNotionalRaw, long maxNotionalRaw,
            Span<OrderValidationResult> resultsOut)
        {
            int n = priceRaw.Length;
            if (qtyRaw.Length != n || resultsOut.Length != n)
                throw new ArgumentException("price/qty/results span 길이가 모두 일치해야 합니다.");
            for (int i = 0; i < n; i++)
                resultsOut[i] = Validate(priceRaw[i], qtyRaw[i], tickSizeRaw, minNotionalRaw, maxNotionalRaw);
        }

        public static string Describe(OrderValidationResult r) => r switch
        {
            OrderValidationResult.Ok => "정상",
            OrderValidationResult.InvalidQty => "수량이 올바르지 않습니다.",
            OrderValidationResult.InvalidPrice => "가격이 올바르지 않습니다.",
            OrderValidationResult.TickSizeViolation => "호가단위에 맞지 않는 가격입니다.",
            OrderValidationResult.BelowMinNotional => "최소 발주금액 미만입니다.",
            OrderValidationResult.AboveMaxNotional => "최대 발주금액을 초과했습니다.",
            _ => "알 수 없는 오류",
        };
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — Atomic Pending Order Cascade Canceller
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>브로커별 미체결 주문 슬롯을 추적하고, 페일오버 통보 시 해당 브로커 전량을 원자적으로 취소 전환하는
    /// δ=0 캐스케이드 취소기. BrokerFailoverSentinel.ReportFault 콜백에서 CancelAllForBroker 호출로 결속(합성 —
    /// BrokerFailoverSentinel 내부 수정 없음).</summary>
    public sealed class PendingOrderCascadeCanceller
    {
        private readonly long[] _brokerOfSlot;   // -1=빈 슬롯, 그외=등록된 브로커 id
        private readonly long[] _cancelled;      // 0=대기, 1=취소됨
        private readonly int _cap;
        private long _nextSlot;

        public PendingOrderCascadeCanceller(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _cap = capacity;
            _brokerOfSlot = new long[capacity];
            _cancelled = new long[capacity];
            for (int i = 0; i < capacity; i++) _brokerOfSlot[i] = -1;
        }

        /// <summary>미체결 주문 1건 등록(★단일 라이터 계약·δ=0). 반환=슬롯 인덱스(체결/취소 시 참조).</summary>
        public int Register(int brokerId)
        {
            long s = Interlocked.Increment(ref _nextSlot) - 1;
            int idx = (int)(s % _cap);
            Volatile.Write(ref _cancelled[idx], 0);
            Volatile.Write(ref _brokerOfSlot[idx], brokerId);
            return idx;
        }

        /// <summary>체결 완료 — 추적 해제(캐스케이드 취소 대상에서 제외).</summary>
        public void MarkFilled(int slot)
        {
            if ((uint)slot < (uint)_cap) Volatile.Write(ref _brokerOfSlot[slot], -1);
        }

        /// <summary>★지정 브로커의 모든 미체결 슬롯을 원자적으로 취소 전환(CAS — 슬롯당 1회만 성공, 중복 취소 카운트
        /// 불가). 반환=이번 호출로 새로 취소된 건수.</summary>
        public int CancelAllForBroker(long brokerId)
        {
            int n = 0;
            for (int i = 0; i < _cap; i++)
            {
                if (Volatile.Read(ref _brokerOfSlot[i]) == brokerId
                    && Interlocked.CompareExchange(ref _cancelled[i], 1, 0) == 0)
                    n++;
            }
            return n;
        }

        public bool IsCancelled(int slot) => (uint)slot < (uint)_cap && Volatile.Read(ref _cancelled[slot]) == 1;
        public long PendingCount(long brokerId)
        {
            long n = 0;
            for (int i = 0; i < _cap; i++)
                if (Volatile.Read(ref _brokerOfSlot[i]) == brokerId && Volatile.Read(ref _cancelled[i]) == 0) n++;
            return n;
        }
    }
}
