// ============================================================================
// QueuePositionEstimator — v9.0.0 큐 위치 추정기(Price-Time Priority)
//
//  내 지정가 주문의 호가 우선순위(가격-시간 우선순위 큐 내 위치)를 무할당으로 추정한다. 주문 배치 시 '내 앞의
//  잔량(aheadQty)'을 기록하고, 같은 가격에 체결이 발생할 때마다(FIFO 소진) 앞 잔량을 줄여 내 위치를 전진시킨다.
//  취소로 앞 잔량이 줄면 유리하게, 신규 유입으로 늘면(내 뒤에 붙으므로 무영향) 위치는 그대로다. 체결 임접률
//  (FillImminencePermille, 1000=바로 앞)을 관제 패널에 동적 프로젝션한다.
//
//  ★불변: δ=0(OnTrade/RemainingAhead/FillImminence 힙 0·Volatile/Interlocked) · 결정론(순수 산술) ·
//    liveArmed:false(순수 큐 관측 — IBroker·소켓·onSend 참조 0) · fail-closed(음수/오버플로우 → 안전 포화).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>지정가 주문 큐 위치 추정기(δ=0·FIFO 가격-시간 우선순위). Place→OnTrade(체결 소진)→FillImminence 프로젝션.</summary>
    public sealed class QueuePositionEstimator
    {
        private long _initialAheadRaw;   // 배치 시점 내 앞 잔량(raw 10^8)
        private long _aheadRaw;          // 현재 내 앞 잔량
        private long _placedTicks;
        private long _trades;
        private long _ver;               // ★seqlock — Place 의 (init,ahead) 원자 스냅샷 보장(홀=쓰는 중·짝=안정)
        private int  _active;

        public bool Active            => Volatile.Read(ref _active) == 1;
        public long RemainingAheadRaw => Interlocked.Read(ref _aheadRaw);
        public long InitialAheadRaw   => Interlocked.Read(ref _initialAheadRaw);
        public long Trades            => Interlocked.Read(ref _trades);

        /// <summary>내 지정가 배치 — 이 가격 큐에서 '내 앞의 잔량'을 기록(δ=0·콜드). aheadQtyRaw=배치 시점 표시 잔량.</summary>
        public void Place(long aheadQtyRaw, long ticks)
        {
            long a = aheadQtyRaw < 0 ? 0 : aheadQtyRaw;
            Interlocked.Increment(ref _ver);                 // 홀수 — (init,ahead) 쓰기 시작(getter 재시도/안전반환)
            Volatile.Write(ref _initialAheadRaw, a);
            Volatile.Write(ref _aheadRaw, a);
            Volatile.Write(ref _placedTicks, ticks);
            Volatile.Write(ref _active, 1);
            Interlocked.Increment(ref _ver);                 // 짝수 — 쓰기 완료(일관 스냅샷)
        }

        /// <summary>같은 가격 체결 발생(δ=0·핫패스). FIFO 로 내 앞 잔량 소진 → 위치 전진. 앞이 0 이면 다음 체결이 내 것.</summary>
        public void OnTrade(long tradedQtyRaw)
        {
            if (Volatile.Read(ref _active) == 0 || tradedQtyRaw <= 0) return;
            long cur = Interlocked.Read(ref _aheadRaw);
            long next = cur - tradedQtyRaw;
            Volatile.Write(ref _aheadRaw, next < 0 ? 0 : next);
            Interlocked.Increment(ref _trades);
        }

        /// <summary>취소로 내 앞 잔량 감소(δ=0). 유리한 전진(내 앞 취소분만큼).</summary>
        public void OnCancelAhead(long canceledQtyRaw)
        {
            if (Volatile.Read(ref _active) == 0 || canceledQtyRaw <= 0) return;
            long cur = Interlocked.Read(ref _aheadRaw);
            long next = cur - canceledQtyRaw;
            Volatile.Write(ref _aheadRaw, next < 0 ? 0 : next);
        }

        /// <summary>체결 임접률(‰·δ=0·UI 프로젝션). 1000=내 앞 0(다음 체결이 내 것). 0=아직 전량 앞. 미배치 0.</summary>
        public long FillImminencePermille
        {
            get
            {
                if (Volatile.Read(ref _active) == 0) return 0L;
                // ★seqlock 독출(δ=0·bounded) — Place 중간의 (init,ahead) 불일치 쌍은 fail-closed(0) 로 안전 처리(재시도 1회).
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    long v1 = Interlocked.Read(ref _ver);
                    if ((v1 & 1L) != 0L) continue;                    // Place 쓰는 중 → 재시도
                    long init = Interlocked.Read(ref _initialAheadRaw);
                    long ahead = Interlocked.Read(ref _aheadRaw);
                    long v2 = Interlocked.Read(ref _ver);
                    if (v1 != v2) continue;                           // Place 개입(에폭 변경) → 재시도
                    if (init <= 0) return 1000L;                      // 앞이 애초에 0 → 즉시 체결 임접
                    if (ahead <= 0) return 1000L;
                    long consumed = init - ahead;
                    // ★오버플로 안전: consumed·init(≤ long.Max) 곱을 Int128 로(랩→음수 방지). consumed≤init 보장이라 결과 ≤1000.
                    return consumed <= 0 ? 0L : (long)((System.Int128)consumed * 1000 / init);
                }
                return 0L;   // 경합 지속 → fail-closed(안전측 0)
            }
        }

        /// <summary>주문 소멸(체결완료/취소) — 추정 종료.</summary>
        public void Clear() { Volatile.Write(ref _active, 0); }
    }
}
