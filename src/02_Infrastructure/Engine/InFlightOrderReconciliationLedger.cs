// ============================================================================
// InFlightOrderReconciliationLedger — v11.0.0: ★Think Beyond Gemini — 인플라이트 주문 정산 원장
//   "실 자본이 API 를 타는 순간의 두 파멸(타임아웃 중복 발주 · 부분체결 후 단선 유령 미체결)을 '정산 확증 전 신규 발주
//    차단'이라는 단일 불변식으로 통합 격리."
//
//  ── 왜 제미나이 A(ClOrdID 중복제거)/B(백프레셔 드레인)를 초월하는가 ─────────────────────────
//  SealedEmissionInterlock 은 '같은 orderId 재사출' 을 exactly-once 로 막지만, 타임아웃 복구 시 클라이언트가 '실패했다'
//  오판하여 '새 orderId' 로 재발주하면 여전히 중복이다(서버는 첫 주문을 받았을 수 있음). 또 부분 체결 후 단선되면 잔여
//  수량이 체결됐는지 안 됐는지 '불확정' 이다 — '미체결로 간주'하면 재발주로 초과 매수, '체결로 간주'하면 미실현 유령.
//  ★근본 불변식은 'ClOrdID 를 유니크하게' 도 '소켓을 드레인' 도 아니라 '해당 종목의 직전 주문 운명이 확증(reconcile)되기
//  전엔 그 종목에 신규 발주를 절대 허용하지 않는다' 이다. 원장은 종목별 pending 슬롯을 두고, 단선 시 잔여를 phantom(불확정)
//  으로 래치하여 reconcile 전까지 그 종목 신규 발주를 fail-closed 로 거부한다. 중복 발주(A)와 유령 미체결(B)을 '확증 전
//  차단' 하나로 동시 봉쇄 — 자본이 '모르는 상태'로 두 번 나가는 경로를 원천 제거.
//
//  ★불변: δ=0(TryBeginOrder/Reconcile 힙 0 — lock + 고정 슬롯 배열·할당 0) · 결정론 · fail-closed(미해소 pending/phantom/
//    슬롯 만원 → 신규 발주 거부) · liveArmed:false(순수 정산 상태 — 실 발주는 어댑터가 게이트 경유).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class InFlightOrderReconciliationLedger
    {
        private struct Slot { public long SymbolCode; public long ClOrdId; public long SentQty; public long FilledQty; public byte State; }
        // State: 0=free, 1=pending(확증 대기), 2=phantom(단선·잔여 불확정)

        private readonly Slot[] _slots;
        private readonly object _lock = new();
        private long _beginRejects, _phantoms, _reconciled, _begun, _globalSeq;

        public InFlightOrderReconciliationLedger(int capacity = 256)
        {
            _slots = new Slot[Math.Max(8, capacity)];
        }

        public long BeginRejects => Interlocked.Read(ref _beginRejects);
        public long Phantoms     => Interlocked.Read(ref _phantoms);
        public long Reconciled   => Interlocked.Read(ref _reconciled);
        public long Begun        => Interlocked.Read(ref _begun);

        /// <summary>
        /// ★신규 주문 시작 시도(δ=0). 동일 종목 미해소(pending/phantom) 존재 → 거부(타임아웃 중복/유령 재발주 봉쇄). 빈 슬롯
        /// 없으면 거부(fail-closed). 성공 시 ★전역 유니크 clOrdId 를 발급·반환(다중 어댑터 횡단 슬롯 오매칭 봉쇄). 실패 → -1.
        /// </summary>
        public long TryBeginOrder(long symbolCode, long qtyRaw)
        {
            lock (_lock)
            {
                int free = -1;
                for (int i = 0; i < _slots.Length; i++)
                {
                    if (_slots[i].State != 0 && _slots[i].SymbolCode == symbolCode)   // ★미해소 pending/phantom → 거부
                    {
                        Interlocked.Increment(ref _beginRejects);
                        return -1L;
                    }
                    if (_slots[i].State == 0 && free < 0) free = i;
                }
                if (free < 0) { Interlocked.Increment(ref _beginRejects); return -1L; }   // 만원 → fail-closed
                long id = ++_globalSeq;   // ★전역 유니크 clOrdId(어댑터 인스턴스 무관·슬롯 오매칭 원천 봉쇄)
                _slots[free] = new Slot { SymbolCode = symbolCode, ClOrdId = id, SentQty = qtyRaw, FilledQty = 0L, State = 1 };
                Interlocked.Increment(ref _begun);
                return id;
            }
        }

        /// <summary>veto/롤백 시 pending 해제(δ=0).</summary>
        public void AbortOrder(long clOrdId)
        {
            lock (_lock)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].State != 0 && _slots[i].ClOrdId == clOrdId) { _slots[i] = default; return; }
        }

        /// <summary>부분 체결 누적 기록(δ=0).</summary>
        public void RecordFill(long clOrdId, long filledQty)
        {
            lock (_lock)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].State != 0 && _slots[i].ClOrdId == clOrdId) { _slots[i].FilledQty += filledQty; return; }
        }

        /// <summary>★네트워크 단선/타임아웃 — 잔여 미체결을 phantom(불확정)으로 래치. reconcile 전까지 그 종목 신규 발주 차단(fail-closed).</summary>
        public void NotePhantom(long clOrdId)
        {
            lock (_lock)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].ClOrdId == clOrdId && _slots[i].State == 1)
                    {
                        _slots[i].State = 2;
                        Interlocked.Increment(ref _phantoms);
                        return;
                    }
        }

        /// <summary>확증 정산(체결/거부/취소 완료·잔여 확인) → pending 해제. 반환=해제됨.</summary>
        public bool Reconcile(long clOrdId)
        {
            lock (_lock)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].State != 0 && _slots[i].ClOrdId == clOrdId)
                    {
                        _slots[i] = default;
                        Interlocked.Increment(ref _reconciled);
                        return true;
                    }
            return false;
        }

        /// <summary>종목에 미해소 pending/phantom 존재 여부(δ=0).</summary>
        public bool HasUnresolved(long symbolCode)
        {
            lock (_lock)
                for (int i = 0; i < _slots.Length; i++)
                    if (_slots[i].State != 0 && _slots[i].SymbolCode == symbolCode) return true;
            return false;
        }
    }
}
