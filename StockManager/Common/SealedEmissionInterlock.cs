// ============================================================================
// SealedEmissionInterlock — v10.0.0 ★Think Beyond Gemini: 봉인 사출 인터락
//   "실물 자본이 브로커 소켓으로 나가는 그 찰나 — exactly-once ∧ atomic-payload 를 '하나의 봉인'으로 물리 보장."
//
//  ── 왜 제미나이의 A/B 를 초월하는가 ────────────────────────────────────────
//  실자본 사출 순간의 두 파멸적 리스크:
//    · 이중 사출(Catastrophic Double-Dispatch) — 두 스레드가 같은 주문을 동시에 소켓으로 → 실물 2배 체결.
//    · 찢어진 페이로드(Torn Payload Emission) — 페이로드 조립 중 스레드 중단 → 반쪽/오염 주문 사출.
//  제미나이는 이를 각각 막자 제안했다(A: 티켓 락, B: 커밋-동결 토큰). 그러나 이 둘은 '하나의 봉인'의 두 얼굴이다:
//  **사출은 소켓 접촉 이전에 '봉인'되어야 한다** — 봉인 = ①주문별 exactly-once 원자 래치(CAS) + ②불변 페이로드
//  토큰 동결 + ③FNV-1a-64 무결성 seal. 봉인이 완결된 뒤에야 onSend 가 '불변 토큰'을 받는다. ∴ 두 스레드가 같은
//  주문을 봉인할 수 없고(exactly-once), 봉인 후엔 어떤 스레드 중단도 반쪽 페이로드를 노출할 수 없다(불변 토큰).
//  A(이중사출)와 B(torn)를 각각이 아니라 '봉인'이라는 단일 원시로 통합·초월.
//
//  ★불변: δ=0(TrySealAndEmit 핫패스 힙 0·Interlocked·값타입 토큰·onSend 는 in-ref 커스텀 델리게이트) · 결정론
//    (FNV-1a-64 순수·벽시계 무참조) · liveArmed:false(래치/봉인만 — 실 소켓 접촉은 주입 onSend 전담·미배선 시 0) ·
//    fail-closed(중복/torn/미봉인 → 미사출·계수). ★단조 orderId 계약(OrderDispatchRing._nextOrderId).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>불변 봉인 페이로드(blittable 값타입·δ=0). 소켓 접촉 이전 동결 — 조립 중 중단이 반쪽을 노출 못함.</summary>
    public readonly struct SealedOrderToken
    {
        public readonly long OrderId;
        public readonly long SymbolCode;
        public readonly byte SideByte;
        public readonly byte KindByte;
        public readonly long PriceRaw;
        public readonly long QtyRaw;
        public readonly long Seal;      // FNV-1a-64 무결성 봉인(전송 경계 재검용)
        public SealedOrderToken(long orderId, long symbolCode, byte side, byte kind, long priceRaw, long qtyRaw, long seal)
        { OrderId = orderId; SymbolCode = symbolCode; SideByte = side; KindByte = kind; PriceRaw = priceRaw; QtyRaw = qtyRaw; Seal = seal; }
    }

    /// <summary>봉인 사출 델리게이트(δ=0·in-ref — 박싱/복사 회피). 불변 토큰만 받아 실 소켓 사출.</summary>
    public delegate void SealedSend(in SealedOrderToken token);

    /// <summary>봉인 사출 인터락(δ=0·exactly-once ∧ atomic-payload). 이중 사출·torn 페이로드를 단일 봉인으로 물리 봉쇄.</summary>
    public sealed class SealedEmissionInterlock
    {
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime  = 1099511628211UL;

        private readonly long[] _dispatched;   // 슬롯별 마지막 사출 orderId(exactly-once 래치·slot=orderId&mask)
        private readonly int _mask;
        private long _doubleDispatchBlocks, _tornPayloadBlocks, _sealedEmissions;

        public SealedEmissionInterlock(int capacity = 8192)
        {
            int cap = NextPow2(capacity < 16 ? 16 : capacity);
            _dispatched = new long[cap];
            _mask = cap - 1;
        }

        // ── 읽기(δ=0·락프리) ──
        public long DoubleDispatchBlocks => Interlocked.Read(ref _doubleDispatchBlocks);
        public long TornPayloadBlocks    => Interlocked.Read(ref _tornPayloadBlocks);
        public long SealedEmissions      => Interlocked.Read(ref _sealedEmissions);
        public int  Capacity             => _mask + 1;

        /// <summary>FNV-1a-64 무결성 봉인(δ=0·순수). 필드 직결 — 테이블/할당 없음.</summary>
        public static long ComputeSeal(long orderId, long symbolCode, byte side, byte kind, long priceRaw, long qtyRaw)
        {
            unchecked
            {
                ulong h = FnvOffset;
                h = (h ^ (ulong)orderId)    * FnvPrime;
                h = (h ^ (ulong)symbolCode) * FnvPrime;
                h = (h ^ side)              * FnvPrime;
                h = (h ^ kind)              * FnvPrime;
                h = (h ^ (ulong)priceRaw)   * FnvPrime;
                h = (h ^ (ulong)qtyRaw)     * FnvPrime;
                return (long)h;
            }
        }

        /// <summary>토큰 자기 무결성 재검(δ=0). 전송 경계에서 소켓 계층이 호출 — 필드-seal 불일치면 사출 중단.</summary>
        public static bool VerifySeal(in SealedOrderToken t)
            => ComputeSeal(t.OrderId, t.SymbolCode, t.SideByte, t.KindByte, t.PriceRaw, t.QtyRaw) == t.Seal;

        /// <summary>
        /// ★봉인 사출(δ=0). ①주문별 exactly-once CAS 래치(double-dispatch 0%) → ②불변 페이로드 토큰 동결+seal
        /// (torn 0%) → ③seal 재검 통과 시에만 onSend(불변 토큰). 중복/torn/경합 패배 → 미사출·계수·false.
        /// </summary>
        public bool TrySealAndEmit(long orderId, long symbolCode, byte side, byte kind, long priceRaw, long qtyRaw, SealedSend onSend)
        {
            if (orderId <= 0 || onSend is null) return false;
            int slot = (int)(orderId & _mask);
            // ① exactly-once: 슬롯의 마지막 사출 orderId 가 이번 이상이면 중복(또는 랩) → 이중 사출 차단.
            long cur = Volatile.Read(ref _dispatched[slot]);
            if (cur >= orderId) { Interlocked.Increment(ref _doubleDispatchBlocks); return false; }
            if (Interlocked.CompareExchange(ref _dispatched[slot], orderId, cur) != cur)
            {                                                       // 동시 스레드가 먼저 봉인 — 경합 패배
                Interlocked.Increment(ref _doubleDispatchBlocks);
                return false;
            }
            // ② 페이로드 커밋-동결: 불변 토큰 스냅샷 + seal(이 지점 이후 어떤 중단도 반쪽을 노출 못함).
            long seal = ComputeSeal(orderId, symbolCode, side, kind, priceRaw, qtyRaw);
            var token = new SealedOrderToken(orderId, symbolCode, side, kind, priceRaw, qtyRaw, seal);
            // ③ 봉인 재검(자기 무결성) 후에만 사출.
            if (!VerifySeal(in token)) { Interlocked.Increment(ref _tornPayloadBlocks); return false; }
            onSend(in token);                                       // 불변 토큰만 소켓으로(예외는 상류 Mission-A 벽 전파)
            Interlocked.Increment(ref _sealedEmissions);
            return true;
        }

        /// <summary>세션 전환 리셋(δ=0·테스트/재무장). 래치 슬롯 영점 — 카운터는 유지.</summary>
        public void Reset() { for (int i = 0; i <= _mask; i++) Volatile.Write(ref _dispatched[i], 0L); }

        private static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }
    }
}
