// ============================================================================
// AdaptiveOrderThrottle — v5.2.0 과제2: 실시간 적응형 오더 스로틀 가드
//
//  초고속 분할 슬라이싱이 단기간에 과도한 주문 패킷을 방출하면 증권사 방화벽의 '초당 주문 한도 초과(Max Orders Per Second
//  Violation)'로 IP 영구 밴/전역 거부 낙인 위험이 있다. 최근 1초·10초 창(Rolling Window)의 송출 패킷 수를 무할당 시간버킷
//  링으로 추적해, 브로커/거래소 상한의 90% 지점 도달 찰나 핫패스 집행을 마이크로 스로틀(Hold)하는 락프리 센티널.
//
//  ── ★원자 패킹 버킷(적대검증 교정) ──────────────────────────────────────────
//  각 버킷은 (초 세대 << 24 | 카운트) 를 단일 long 에 패킹해 CAS 로만 갱신한다. 초 경계 리셋(세대 교체)과 카운트 증가가
//  '하나의 워드 CAS'로 원자화되어, 리셋이 동시 증가를 덮어써 카운트가 유실(언더카운트→fail-open)되는 lost-update 를 봉쇄.
//  (count 24비트=최대 16M/초·sec 40비트=수만 년 무랩.) 시간창 rate 카운터 자체가 신규(기존 롤링 링은 표본 카운트).
//
//  ── liveArmed:false / δ=0 / 결정론 ─────────────────────────────────────────
//  스로틀은 오직 '주문 흐름을 줄이는(Hold)' 방향만 — 발주 증가·무장 게이트 우회 없음. TryAcquire 는 고정 long[10] + CAS 만
//  (힙 0). 시각은 주입 tick(Stopwatch 단조·UtcNow 금지)으로 초 인덱스 계산(결정론). Rate 조회도 tick 인자로 stale-aware.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>1s/10s 시간창 송출 패킷 수를 무할당 원자 패킹 버킷으로 추적해 상한 90% 도달 시 Hold 하는 락프리 rate 센티널.</summary>
    public sealed class AdaptiveOrderThrottle
    {
        private const int  Buckets   = 10;                    // 10×1초 버킷 = 10초 창
        private const int  CountBits = 24;                    // 카운트 하위 24비트(≤16M/초)
        private const long CountMask = (1L << CountBits) - 1;

        private readonly long _ticksPerSec;
        private readonly long _limit1s;    // 90% 임계(초당)
        private readonly long _limit10s;   // 90% 임계(10초)
        private readonly long[] _slot = new long[Buckets];    // 각 (sec << 24 | count) 패킹 — CAS 전용

        private long _sends, _throttled;

        public AdaptiveOrderThrottle(long maxPerSec = 20, long maxPer10Sec = 0, long tickFrequency = 0)
        {
            _ticksPerSec = tickFrequency > 0 ? tickFrequency : System.Diagnostics.Stopwatch.Frequency;
            long m1 = maxPerSec < 1 ? 1 : maxPerSec;
            long m10 = maxPer10Sec > 0 ? maxPer10Sec : m1 * 10;
            _limit1s  = Math.Max(1, m1 * 9 / 10);    // 90%
            _limit10s = Math.Max(1, m10 * 9 / 10);
            for (int i = 0; i < Buckets; i++) _slot[i] = -1L;   // -1 = 미사용(세대 없음)
        }

        public long Sends       => Interlocked.Read(ref _sends);
        public long Throttled   => Interlocked.Read(ref _throttled);
        public long Limit1s     => _limit1s;

        private static long BucketSec(long packed) => packed < 0 ? -1L : packed >> CountBits;
        private static long BucketCount(long packed) => packed < 0 ? 0L : packed & CountMask;

        /// <summary>지정 tick 시점의 1초 창 송출 수(관제·stale-aware). 흐름 정지 후엔 0(현재 초와 버킷 세대 불일치).</summary>
        public long Rate1s(long nowTicks)
        {
            long sec = (nowTicks < 0 ? 0 : nowTicks) / _ticksPerSec;
            long w = Volatile.Read(ref _slot[(int)(((sec % Buckets) + Buckets) % Buckets)]);
            return BucketSec(w) == sec ? BucketCount(w) : 0L;
        }

        /// <summary>지정 tick 시점의 10초 창 송출 수(stale-aware).</summary>
        public long Rate10s(long nowTicks)
        {
            long sec = (nowTicks < 0 ? 0 : nowTicks) / _ticksPerSec;
            long c = 0;
            for (int j = 0; j < Buckets; j++) { long w = Volatile.Read(ref _slot[j]); long s = BucketSec(w); if (s >= 0 && s > sec - Buckets && s <= sec) c += BucketCount(w); }
            return c;
        }

        /// <summary>
        /// ★핫 판정(δ=0·락프리) — 이 tick 시점에 1건 송출 여지가 있으면 원자 예약(패킹 CAS 범프)+true, 상한 90% 도달이면 Hold(false).
        /// 세대 교체(초 경계)와 카운트 증가가 단일 워드 CAS 로 원자화 — 리셋-vs-증가 lost-update 봉쇄(정확 카운트).
        /// </summary>
        public bool TryAcquire(long nowTicks)
        {
            if (nowTicks < 0) nowTicks = 0;
            long sec = nowTicks / _ticksPerSec;
            int idx = (int)(((sec % Buckets) + Buckets) % Buckets);

            // 1초 창 = 현재 버킷(세대 일치 시), 10초 창 = 최근 10초 유효 버킷 합.
            long c1 = Rate1s(nowTicks);
            long c10 = Rate10s(nowTicks);
            if (c1 >= _limit1s || c10 >= _limit10s)
            {
                Interlocked.Increment(ref _throttled);
                return false;   // ★90% 도달 — Hold(핫패스 마이크로 스로틀)
            }

            // 원자 패킹 범프 — 세대 일치면 count+1, 아니면 이 초로 세대 교체+count=1. CAS 재시도로 유실 0.
            var spin = new SpinWait();
            while (true)
            {
                long w = Volatile.Read(ref _slot[idx]);
                long wSec = BucketSec(w);
                long cnt = wSec == sec ? BucketCount(w) : 0L;
                if (cnt >= CountMask) break;   // 포화(비현실적) — 더 안 셈(안전측)
                long nw = (sec << CountBits) | (cnt + 1);
                if (Interlocked.CompareExchange(ref _slot[idx], nw, w) == w) { Interlocked.Increment(ref _sends); return true; }
                spin.SpinOnce();
            }
            Interlocked.Increment(ref _sends);
            return true;
        }
    }
}
