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
        [ZeroAllocHotPath]
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
    /// BrokerFailoverSentinel 내부 수정 없음).
    ///
    /// [Phase 1 감사 교정] 이전 구현은 두 결함이 있었다(CoreEngineAuditTests 에서 Red 재현):
    ///   ① 순환 인덱스(s % cap)로 슬롯을 배정해, 용량보다 많은 주문이 쌓이면 아직 미체결인 슬롯을 덮어썼다 — 추적에서
    ///      사라진 주문은 페일오버 캐스케이드 취소에서 누락(이 엔진이 막으려던 바로 그 fail-open).
    ///   ② 브로커 ID 와 취소 플래그가 서로 다른 워드라, 슬롯 해제·재사용 사이에 옛 핸들로 MarkFilled/CAS 가 들어오면 새 주문을
    ///      잘못 해제/취소했다(ABA).
    /// 교정: 슬롯 상태를 단일 long 워드 [세대 32bit | 취소 1bit | 브로커ID+1 31bit] 로 합쳐 모든 전이를 1회 CAS 로 수행.
    /// 등록은 빈 슬롯을 CAS 로 점유하고, 빈 슬롯이 없으면 -1(fail-closed — 추적 불가 주문은 호출측이 발주를 거부해야 함).
    /// 핸들 = (세대 &lt;&lt; 32) | 슬롯 — 세대가 다르면 옛 핸들은 아무것도 바꾸지 못한다.</summary>
    /// <summary>[라우터 하드닝] 체결 통보 판정 — 호출측이 "유실 없이" 회계하도록 세 경우를 구분한다.</summary>
    public enum FillOutcome : byte
    {
        Filled = 0,             // 정상 체결 — 슬롯 해제
        FilledAfterCancel = 1,  // 페일오버 캐스케이드로 취소 전환된 뒤 도착한 체결 — 실체결이므로 반드시 포지션 반영 + 대사(reconcile)
        Stale = 2,              // 세대가 다른(이미 해제·재사용된 슬롯의) 옛 통보 — 무시(새 주문 오해제 차단)
    }

    public sealed class PendingOrderCascadeCanceller
    {
        private const long OwnerMask = 0x7FFF_FFFFL;       // bit 0..30: brokerId + 1 (0 = 빈 슬롯)
        private const long CancelledBit = 1L << 31;
        private const int GenerationShift = 32;
        public const int MaxBrokerId = 0x7FFF_FFFE;

        private readonly long[] _slot;
        private readonly int _cap;
        private int _probeStart;
        private long _lateFills, _staleNotices;

        /// <summary>취소 전환 후 도착한 실체결 누계(대사 필요 건수 — 0 이 아니면 페일오버 중 체결이 있었다).</summary>
        public long LateFillsAfterCancel => Interlocked.Read(ref _lateFills);
        /// <summary>세대 불일치로 무시된 옛 통보 누계.</summary>
        public long StaleNotices => Interlocked.Read(ref _staleNotices);

        public PendingOrderCascadeCanceller(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _cap = capacity;
            _slot = new long[capacity];
        }

        public int Capacity => _cap;

        /// <summary>미체결 주문 1건 등록(δ=0·락프리 CAS 점유). 반환=핸들(체결/취소 조회에 사용). 빈 슬롯이 없으면 -1
        /// (★fail-closed: 기존 미체결 추적을 절대 밀어내지 않는다).</summary>
        [ZeroAllocHotPath]
        public long Register(int brokerId)
        {
            if ((uint)brokerId > MaxBrokerId) throw new ArgumentOutOfRangeException(nameof(brokerId));
            int start = (int)((uint)Interlocked.Increment(ref _probeStart) % (uint)_cap);
            for (int k = 0; k < _cap; k++)
            {
                int idx = start + k;
                if (idx >= _cap) idx -= _cap;
                while (true)
                {
                    long w = Volatile.Read(ref _slot[idx]);
                    if ((w & OwnerMask) != 0) break;                       // 점유 중 → 다음 슬롯
                    long gen = ((w >> GenerationShift) + 1) & 0x7FFF_FFFFL;   // 31비트 — 핸들이 항상 음이 아니게(-1 = 등록 실패와 구분)
                    long claimed = (gen << GenerationShift) | ((long)brokerId + 1);
                    if (Interlocked.CompareExchange(ref _slot[idx], claimed, w) == w)
                        return (gen << GenerationShift) | (uint)idx;
                }
            }
            return -1;
        }

        /// <summary>체결 통보 — 슬롯 해제 + 판정 반환. 세대가 다른 옛 핸들은 Stale(무변경).
        /// [라우터 하드닝] 이전엔 void 라 ① 취소 전환 뒤 도착한 실체결(페일오버 경합)과 ② 옛 세대 통보를 호출측이 구분할 수 없어,
        /// ①을 "취소됐으니 무시"로 처리하면 실체결이 회계에서 유실됐다. 이제 FilledAfterCancel 로 명시(대사 대상) · 누계 계수.</summary>
        [ZeroAllocHotPath]
        public FillOutcome MarkFilled(long handle)
        {
            if (!TryDecode(handle, out int idx, out long gen)) { Interlocked.Increment(ref _staleNotices); return FillOutcome.Stale; }
            while (true)
            {
                long w = Volatile.Read(ref _slot[idx]);
                if ((w & OwnerMask) == 0 || (w >> GenerationShift) != gen)
                {
                    Interlocked.Increment(ref _staleNotices);
                    return FillOutcome.Stale;                                         // 이미 해제 또는 재사용됨
                }
                long freed = gen << GenerationShift;                                  // 세대 보존 · 소유/취소 비트 0
                if (Interlocked.CompareExchange(ref _slot[idx], freed, w) == w)
                {
                    if ((w & CancelledBit) == 0) return FillOutcome.Filled;
                    Interlocked.Increment(ref _lateFills);
                    return FillOutcome.FilledAfterCancel;
                }
            }
        }

        /// <summary>[피드 연동] 추적 포기 해제 — 송출 전 중단(브로커 미도달) 또는 응답 타임아웃 회수 시 슬롯을 체결/취소 계수 없이 반납.
        /// 세대 불일치·이미 해제 = false(무변경). 반환 true 시 wasCancelled = 취소 전환 상태였는지.</summary>
        [ZeroAllocHotPath]
        public bool Release(long handle, out bool wasCancelled)
        {
            wasCancelled = false;
            if (!TryDecode(handle, out int idx, out long gen)) return false;
            while (true)
            {
                long w = Volatile.Read(ref _slot[idx]);
                if ((w & OwnerMask) == 0 || (w >> GenerationShift) != gen) return false;
                if (Interlocked.CompareExchange(ref _slot[idx], gen << GenerationShift, w) == w)
                {
                    wasCancelled = (w & CancelledBit) != 0;
                    return true;
                }
            }
        }

        /// <summary>[라우터 하드닝] 브로커의 취소 확인 수신 — "취소 전환된" 슬롯만 해제(아직 취소 요청 전인 슬롯은 무변경 false).
        /// 이미 체결로 해제됐거나 옛 세대면 false.</summary>
        [ZeroAllocHotPath]
        public bool ConfirmCancelled(long handle)
        {
            if (!TryDecode(handle, out int idx, out long gen)) return false;
            while (true)
            {
                long w = Volatile.Read(ref _slot[idx]);
                if ((w & OwnerMask) == 0 || (w >> GenerationShift) != gen || (w & CancelledBit) == 0) return false;
                if (Interlocked.CompareExchange(ref _slot[idx], gen << GenerationShift, w) == w) return true;
            }
        }

        /// <summary>★지정 브로커의 모든 미체결 슬롯을 원자적으로 취소 전환(슬롯당 1회만 성공 — 중복 카운트 불가).
        /// 반환=이번 호출로 새로 취소된 건수.</summary>
        [ZeroAllocHotPath]
        public int CancelAllForBroker(long brokerId)
        {
            if ((ulong)brokerId > MaxBrokerId) return 0;
            long owner = brokerId + 1;
            int n = 0;
            for (int i = 0; i < _cap; i++)
            {
                while (true)
                {
                    long w = Volatile.Read(ref _slot[i]);
                    if ((w & OwnerMask) != owner || (w & CancelledBit) != 0) break;
                    if (Interlocked.CompareExchange(ref _slot[i], w | CancelledBit, w) == w) { n++; break; }
                }
            }
            return n;
        }

        public bool IsCancelled(long handle)
        {
            if (!TryDecode(handle, out int idx, out long gen)) return false;
            long w = Volatile.Read(ref _slot[idx]);
            return (w >> GenerationShift) == gen && (w & OwnerMask) != 0 && (w & CancelledBit) != 0;
        }

        public long PendingCount(long brokerId)
        {
            if ((ulong)brokerId > MaxBrokerId) return 0;
            long owner = brokerId + 1, n = 0;
            for (int i = 0; i < _cap; i++)
            {
                long w = Volatile.Read(ref _slot[i]);
                if ((w & OwnerMask) == owner && (w & CancelledBit) == 0) n++;
            }
            return n;
        }

        private bool TryDecode(long handle, out int idx, out long gen)
        {
            idx = (int)(handle & 0xFFFF_FFFFL);
            gen = (handle >> GenerationShift) & 0x7FFF_FFFFL;
            return handle >= 0 && (uint)idx < (uint)_cap;
        }
    }
}
