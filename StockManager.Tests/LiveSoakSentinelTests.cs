// ============================================================================
// LiveSoakSentinelTests — v12.5.0 Unattended Live-Soak Memory Sentinel 적대검증
//   Section 2: LiveSoakSentinel(GC압력 재사용+베이스라인드리프트+Zombie+OOM임박+GC정지추세 5원 0ms Disarm).
//   Think Beyond Idea B: GcPauseTrendGuard(연속상승 추세 선제 감지).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class LiveSoakSentinelTests
    {
        private readonly ITestOutputHelper _out;
        public LiveSoakSentinelTests(ITestOutputHelper o) => _out = o;

        // ══ ThreadHeartbeatMonitor — Zombie 스레드 감지 ═══════════════════════
        [Fact]
        public void HeartbeatMonitor_DetectsStaleSlots_AsZombies()
        {
            var m = new ThreadHeartbeatMonitor(4);
            m.Beat(0, 1000); m.Beat(1, 1000); m.Beat(2, 1000);
            // 슬롯3은 하트비트 없음(0) → CountZombies 대상 아님(관측된 적 없는 슬롯은 제외)
            int zombies = m.CountZombies(nowTicks: 1000 + 5000, timeoutTicks: 3000);
            Assert.Equal(3, zombies);   // 0,1,2 모두 5000>3000 초과

            m.Beat(0, 1000 + 4500);   // 슬롯0 갱신 — 더는 정체 아님
            zombies = m.CountZombies(nowTicks: 1000 + 5000, timeoutTicks: 3000);
            Assert.Equal(2, zombies);
        }

        // ══ OomImminentGuard ══════════════════════════════════════════════════
        [Fact]
        public void OomGuard_TripsAt90Percent_StickyUntilReset()
        {
            var g = new OomImminentGuard(loadRatioThresholdPpm: 900_000);
            Assert.False(g.Sample(memoryLoadBytes: 800_000_000, highMemoryLoadThresholdBytes: 1_000_000_000));
            Assert.True(g.Sample(memoryLoadBytes: 950_000_000, highMemoryLoadThresholdBytes: 1_000_000_000));

            Assert.True(g.Sample(memoryLoadBytes: 100_000_000, highMemoryLoadThresholdBytes: 1_000_000_000));   // sticky — 낮아져도 유지
            g.Reset();
            Assert.False(g.IsImminent);
        }

        [Fact]
        public void OomGuard_UnknownThreshold_SkipsJudgement()
        {
            var g = new OomImminentGuard();
            g.Sample(memoryLoadBytes: 999_999_999, highMemoryLoadThresholdBytes: 0);   // 미상 임계 → fail-closed 생략
            Assert.False(g.IsImminent);
        }

        // ══ GcPauseTrendGuard(Think Beyond Idea B) ═══════════════════════════
        [Fact]
        public void GcPauseTrend_ConsecutiveRising_TripsAfterThreshold()
        {
            var g = new GcPauseTrendGuard(consecutiveRisingThreshold: 3);
            g.Sample(1000);          // 베이스라인
            Assert.False(g.Sample(1100));   // 상승 1
            Assert.False(g.Sample(1250));   // 상승 2
            Assert.True(g.Sample(1500));    // 상승 3 → 트립
        }

        [Fact]
        public void GcPauseTrend_NonRisingSample_ResetsConsecutiveCount()
        {
            var g = new GcPauseTrendGuard(consecutiveRisingThreshold: 3);
            g.Sample(1000);
            g.Sample(1100);   // 상승 1
            g.Sample(1100);   // 증분 0 → 리셋
            g.Sample(1200);   // 상승 1(재시작)
            g.Sample(1300);   // 상승 2
            Assert.False(g.IsTripped);   // 아직 3연속 아님
        }

        // ══ LiveSoakSentinel — 5원 합성 0ms Disarm ════════════════════════════
        [Fact]
        public void Sentinel_GcPressureBreach_ViaExistingLongRunningSentinel_Trips()
        {
            var gc = new LongRunningMemoryGcSentinel(gen2PerMinThreshold: 60, allocBytesPerSecThreshold: 500_000_000);
            var sentinel = new LiveSoakSentinel(gc);
            long t0 = TimeSpan.TicksPerSecond * 0;
            sentinel.SampleMemory(t0, gen2Count: 0, allocBytes: 0, currentTotalBytes: 100_000_000);   // 베이스라인 샘플
            long t1 = TimeSpan.TicksPerSecond * 1;
            // 1초에 Gen2 100회 발생 → 6000/min ≫ 60/min 임계
            Assert.True(sentinel.SampleMemory(t1, gen2Count: 100, allocBytes: 0, currentTotalBytes: 100_000_000));
            Assert.True(sentinel.IsDisarmed);
            Assert.True(sentinel.BreachedSource(LiveSoakBreachSource.GcPressure));
        }

        [Fact]
        public void Sentinel_BaselineDrift_RequiresSustainedThreeSamples()
        {
            var gc = new LongRunningMemoryGcSentinel(1_000_000, 1_000_000_000_000);   // 사실상 비활성(GC압력 미간섭)
            int breachCount = 0;
            var sentinel = new LiveSoakSentinel(gc, _ => breachCount++);
            sentinel.SetBaseline(1_000_000_000);   // 1GB 베이스라인

            // 6% 팽창(500bps=5% 초과) — 1회차는 트립 안 함(연속 3표본 필요)
            Assert.False(sentinel.SampleMemory(1, 0, 0, currentTotalBytes: 1_060_000_000));
            Assert.False(sentinel.SampleMemory(2, 0, 0, currentTotalBytes: 1_060_000_000));
            Assert.True(sentinel.SampleMemory(3, 0, 0, currentTotalBytes: 1_060_000_000));   // 3연속 → 트립

            Assert.True(sentinel.BreachedSource(LiveSoakBreachSource.BaselineDrift));
            Assert.Equal(1, breachCount);
        }

        [Fact]
        public void Sentinel_BaselineDrift_TransientSpike_DoesNotTrip()
        {
            var gc = new LongRunningMemoryGcSentinel(1_000_000, 1_000_000_000_000);
            var sentinel = new LiveSoakSentinel(gc);
            sentinel.SetBaseline(1_000_000_000);

            sentinel.SampleMemory(1, 0, 0, currentTotalBytes: 1_060_000_000);   // 팽창 1
            sentinel.SampleMemory(2, 0, 0, currentTotalBytes: 1_010_000_000);   // 정상 복귀 → 카운터 리셋
            Assert.False(sentinel.SampleMemory(3, 0, 0, currentTotalBytes: 1_060_000_000));   // 다시 팽창 1(누적 아님)
            Assert.False(sentinel.IsDisarmed);
        }

        [Fact]
        public void Sentinel_ZombieThread_Breach_Trips()
        {
            var gc = new LongRunningMemoryGcSentinel();
            var sentinel = new LiveSoakSentinel(gc);
            Assert.False(sentinel.NoteZombieThread(0));
            Assert.True(sentinel.NoteZombieThread(2));
            Assert.True(sentinel.BreachedSource(LiveSoakBreachSource.ZombieThread));
        }

        [Fact]
        public void Sentinel_OomImminent_Breach_Trips()
        {
            var gc = new LongRunningMemoryGcSentinel();
            var sentinel = new LiveSoakSentinel(gc);
            Assert.True(sentinel.NoteOomImminent(true));
            Assert.True(sentinel.BreachedSource(LiveSoakBreachSource.OomImminent));
        }

        [Fact]
        public void Sentinel_GcPauseTrend_Breach_Trips()
        {
            var gc = new LongRunningMemoryGcSentinel();
            var sentinel = new LiveSoakSentinel(gc);
            Assert.True(sentinel.NoteGcPauseTrend(true));
            Assert.True(sentinel.BreachedSource(LiveSoakBreachSource.GcPauseTrend));
        }

        [Fact]
        public void Sentinel_ReArm_ResetsState_But_BaselineNeedsResetManually()
        {
            var gc = new LongRunningMemoryGcSentinel();
            var sentinel = new LiveSoakSentinel(gc);
            sentinel.NoteZombieThread(1);
            Assert.True(sentinel.IsDisarmed);

            sentinel.ReArm();
            Assert.False(sentinel.IsDisarmed);
            Assert.True(sentinel.CanEmit);
        }

        [Fact]
        public void Sentinel_OnBreach_Callback_FansInto_DumpTrigger()
        {
            int dumpCalls = 0;
            var gc = new LongRunningMemoryGcSentinel();
            var sentinel = new LiveSoakSentinel(gc, source => dumpCalls++);
            sentinel.NoteOomImminent(true);
            sentinel.NoteZombieThread(1);   // 이미 Disarm 상태에서도 추가 브리치는 계속 콜백(재차 덤프 트리거 — DumpOnce 멱등이 중복 방지)
            Assert.Equal(2, dumpCalls);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §적대검증 — δ=0 핫패스 + 락프리 다중스레드
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class LiveSoakSentinelConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public LiveSoakSentinelConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly ThreadHeartbeatMonitor _hotHeartbeat = new(16);
        private static void BeatHot() => _hotHeartbeat.Beat(0, DateTime.UtcNow.Ticks);

        [Fact]
        public void HeartbeatMonitor_Beat_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) BeatHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) BeatHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly LongRunningMemoryGcSentinel _hotGc = new(1_000_000, 1_000_000_000_000);
        private static readonly LiveSoakSentinel _hotSentinel = new(_hotGc);
        private static long _hotTick;
        private static void SampleHot()
        {
            long t = Interlocked.Increment(ref _hotTick);
            _hotSentinel.SampleMemory(t, 0, 0, 100_000_000);
        }

        [Fact]
        public void Sentinel_SampleMemory_ZeroAllocation_NoBreach()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) SampleHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) SampleHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
