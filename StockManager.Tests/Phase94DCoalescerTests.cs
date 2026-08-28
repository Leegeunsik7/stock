// ============================================================================
// Phase94DCoalescerTests — v11.3.0 CrossBrokerDispatchCoalescer 적대적 검증(100% 단언, Think-Beyond)
//   이종 브로커(키움 Pipe + 토스 REST) 동시 UI 폭풍의 Dispatcher Stalling 격리 엔진을 지상 검증:
//     ① 코얼레싱: 2000 마크 → 1 렌더(프레임당 1회 압축)
//     ② Safe-Hold 헬스 래치: 전이 1회 보고·sticky·Clear 재전이
//     ③ 멱등: 동일 헬스 재설정 → 팬텀 전이 0
//     ④ 기아방지 로터: 배수 시작 브로커 순환
//     ⑤ 범위 밖 fail-closed(무throw)
//     ⑥ 경합: 다중 라이터 Mark 무손실(Interlocked 정확)
//     ⑦ δ=0: Mark/TryDrain 핫패스 힙 0
// ============================================================================
using System;
using System.Threading.Tasks;
using MS2E.UI.Portable.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94DCoalescerTests
    {
        // ══ ① 코얼레싱: 다중 피드 폭풍 → 프레임당 1 렌더 ═══════════════════════
        [Fact]
        public void Coalesce_DualFeed_Storm_Single_Render()
        {
            var c = new CrossBrokerDispatchCoalescer(4);
            Span<BrokerFrame> dest = stackalloc BrokerFrame[4];

            for (int i = 0; i < 1000; i++) { c.Mark(0); c.Mark(1); }   // 키움+토스 2000틱 폭풍

            int n = c.TryDrain(dest);
            Assert.Equal(2, n);                       // 두 브로커 프레임
            long m0 = 0, m1 = 0;
            for (int i = 0; i < n; i++)
            {
                if (dest[i].Broker == 0) m0 = dest[i].CoalescedMarks;
                if (dest[i].Broker == 1) m1 = dest[i].CoalescedMarks;
            }
            Assert.Equal(1000, m0);
            Assert.Equal(1000, m1);
            Assert.Equal(1L, c.RenderTriggers);       // ★2000 마크 → 단 1회 RenderTrigger
            Assert.Equal(2000L, c.TotalMarks);
            Assert.True(c.Coalesced >= 1998);         // 압축 효과

            Assert.Equal(0, c.TryDrain(dest));        // 재배수 → 0(dirty 소비됨)
            Assert.False(c.HasPending);
        }

        // ══ ② Safe-Hold 래치: 전이 1회 · sticky · Clear 재전이 ════════════════
        [Fact]
        public void SafeHold_Latches_Reports_Transition_Once_Sticky()
        {
            var c = new CrossBrokerDispatchCoalescer(4);
            Span<BrokerFrame> dest = stackalloc BrokerFrame[4];

            c.SafeHold(2);   // 키움 파이프 단선 시뮬 — 라인 격리
            int n = c.TryDrain(dest);
            Assert.Equal(1, n);
            Assert.Equal(2, dest[0].Broker);
            Assert.True(dest[0].HealthChanged);                 // ★전이 보고
            Assert.Equal(BrokerHealth.SafeHold, dest[0].Health);

            Assert.Equal(0, c.TryDrain(dest));                  // 변경 없음 → 렌더 0(idle-quiet)
            Assert.Equal(BrokerHealth.SafeHold, c.HealthOf(2)); // ★sticky — 여전히 격리

            c.Clear(2);                                         // 회복 → Online
            Assert.Equal(1, c.TryDrain(dest));                  // 재전이 보고
            Assert.Equal(BrokerHealth.Online, c.HealthOf(2));
        }

        // ══ ③ 멱등: 동일 헬스 재설정 → 팬텀 전이 0 ════════════════════════════
        [Fact]
        public void SetHealth_Idempotent_No_Phantom_Transition()
        {
            var c = new CrossBrokerDispatchCoalescer(4);
            Span<BrokerFrame> dest = stackalloc BrokerFrame[4];

            c.SetHealth(1, BrokerHealth.Degraded);
            Assert.Equal(1, c.TryDrain(dest));      // 최초 전이
            c.SetHealth(1, BrokerHealth.Degraded);  // 동일 값
            Assert.Equal(0, c.TryDrain(dest));      // ★팬텀 전이 없음
        }

        // ══ ④ 기아방지 로터: 배수 시작 브로커 순환 ════════════════════════════
        [Fact]
        public void Fairness_Rotor_Rotates_Start_Broker()
        {
            var c = new CrossBrokerDispatchCoalescer(4);
            Span<BrokerFrame> dest = stackalloc BrokerFrame[4];

            for (int b = 0; b < 4; b++) c.Mark(b);
            c.TryDrain(dest);
            Assert.Equal(0, dest[0].Broker);        // 1회차: 브로커 0부터

            for (int b = 0; b < 4; b++) c.Mark(b);
            c.TryDrain(dest);
            Assert.Equal(1, dest[0].Broker);        // ★2회차: 브로커 1부터(로터 전진)

            for (int b = 0; b < 4; b++) c.Mark(b);
            c.TryDrain(dest);
            Assert.Equal(2, dest[0].Broker);        // 3회차: 브로커 2부터
        }

        // ══ ⑤ 범위 밖 fail-closed(무throw) ════════════════════════════════════
        [Fact]
        public void OutOfRange_FailClosed_No_Throw()
        {
            var c = new CrossBrokerDispatchCoalescer(4);
            c.Mark(-1); c.Mark(4); c.Mark(999);                    // 무시(무throw)
            c.SafeHold(99); c.SetHealth(-5, BrokerHealth.Offline); // 무시
            Assert.Equal(0L, c.TotalMarks);                        // 유효 마크 0
            Assert.Equal(BrokerHealth.Offline, c.HealthOf(99));    // 범위밖 조회 → Offline(fail-closed)
        }

        // ══ ⑥ 경합: 다중 라이터 Mark 무손실(Interlocked 정확성) ═══════════════
        [Fact]
        public void Concurrent_Marks_Exact_No_Loss()
        {
            var c = new CrossBrokerDispatchCoalescer(4);
            const int T = 8, per = 50_000;
            Parallel.For(0, T, t => { for (int i = 0; i < per; i++) c.Mark(t & 3); });

            Assert.Equal((long)T * per, c.TotalMarks);   // 누적 정확

            Span<BrokerFrame> dest = stackalloc BrokerFrame[4];
            long summed = 0; int n;
            while ((n = c.TryDrain(dest)) > 0)
                for (int i = 0; i < n; i++) summed += dest[i].CoalescedMarks;
            Assert.Equal((long)T * per, summed);         // ★모든 마크가 정확히 배수됨(경합 손실 0)
        }

        // ══ ⑦ δ=0: Mark/TryDrain 핫패스 힙 0 ═════════════════════════════════
        [Fact]
        public void Mark_And_Drain_ZeroAllocation()
        {
            var c = new CrossBrokerDispatchCoalescer(8);
            // 워밍업
            {
                Span<BrokerFrame> warm = stackalloc BrokerFrame[8];
                for (int i = 0; i < 50_000; i++) { c.Mark(i & 7); if ((i & 63) == 0) c.TryDrain(warm); }
            }
            long Measure()
            {
                Span<BrokerFrame> dest = stackalloc BrokerFrame[8];
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++)
                {
                    c.Mark(i & 7);
                    if ((i & 31) == 0) { c.SetHealth(i & 7, ((i & 64) == 0) ? BrokerHealth.Online : BrokerHealth.Degraded); c.TryDrain(dest); }
                }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★마크/헬스/배수 핫패스 힙 0(Interlocked/Volatile·스팬 배수)
        }
    }
}
