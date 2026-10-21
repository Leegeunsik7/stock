// ============================================================================
// AutonomousWatchdogTests — v5.1.0 자율 무정지 복구 & 실전 라이브 감시망
//
//  §1 LiveSessionWatchdog — 연속 3회 데드라인 초과 Coma·리셋·멱등.
//  §2 AutonomousFailoverController — 페일오버·양채널 다운 fail-closed·복구·exactly-once dedup.
//  §3 ThreadStarvationSensor — 아사 프로비저닝·회복 복원.
//  §4 AdaptiveOrphanReaper — 고아 진상 3분기(회수/확정/동결)·적응형 데드라인.
//  §5 ★적대검증 — 감시견/페일오버 가동 중 핫패스 δ=0·페일오버 백만회에도 이중키 없이 실주문 0.
// ============================================================================
using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class AutonomousWatchdogTests
    {
        // ════════════ §1 LiveSessionWatchdog ════════════
        [Fact]
        public void Watchdog_ThreeConsecutiveMisses_DiagnosesComa()
        {
            bool reset = false;
            var wd = new LiveSessionWatchdog(new IgnitionLock(), resetSocket: () => reset = true, deadlineTicks: 100, comaThreshold: 3);
            Assert.False(wd.Observe(50));   // 정상
            Assert.False(wd.Observe(200));  // miss 1
            Assert.False(wd.Observe(200));  // miss 2
            Assert.True(wd.Observe(200));   // miss 3 → Coma
            Assert.Equal(SessionHealth.Coma, wd.Health);
            Assert.True(reset);
            Assert.Equal(1L, wd.ComaCount);
        }

        [Fact]
        public void Watchdog_RecoveryResetsConsecutive_NoComa()
        {
            var wd = new LiveSessionWatchdog(deadlineTicks: 100, comaThreshold: 3);
            wd.Observe(200); wd.Observe(200);   // miss 2
            wd.Observe(50);                      // 정상 → 리셋
            wd.Observe(200); wd.Observe(200);   // miss 2 again
            Assert.Equal(SessionHealth.Healthy, wd.Health);   // 연속 3 미도달
            Assert.Equal(0L, wd.ComaCount);
        }

        [Fact]
        public void Watchdog_RepeatedMiss_IdempotentSingleComa()
        {
            var wd = new LiveSessionWatchdog(deadlineTicks: 100, comaThreshold: 3);
            for (int i = 0; i < 100; i++) wd.Observe(500);
            Assert.Equal(SessionHealth.Coma, wd.Health);
            Assert.Equal(1L, wd.ComaCount);   // ★CAS 로 1회만
        }

        // ════════════ §2 AutonomousFailoverController ════════════
        [Fact]
        public void Failover_PrimaryDown_RoutesToStandby()
        {
            var fo = new AutonomousFailoverController(new OrderAttestationService());
            Assert.Equal(ActiveChannel.Primary, fo.Active);
            fo.ReportChannel(ActiveChannel.Primary, false);
            Assert.Equal(ActiveChannel.Standby, fo.Active);
            Assert.Equal(1L, fo.Failovers);
        }

        [Fact]
        public void Failover_BothDown_FailClosedFreeze()
        {
            var att = new OrderAttestationService();
            var fo = new AutonomousFailoverController(att);
            fo.ReportChannel(ActiveChannel.Primary, false);
            fo.ReportChannel(ActiveChannel.Standby, false);
            Assert.Equal(ActiveChannel.None, fo.Active);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★양채널 다운 → fail-closed
        }

        [Fact]
        public void Failover_PrimaryRecovers_RoutesBack()
        {
            var fo = new AutonomousFailoverController(new OrderAttestationService());
            fo.ReportChannel(ActiveChannel.Primary, false);   // → Standby
            fo.ReportChannel(ActiveChannel.Primary, true);    // 복구 → Primary
            Assert.Equal(ActiveChannel.Primary, fo.Active);
            Assert.Equal(1L, fo.Recoveries);
        }

        [Fact]
        public void Failover_ExactlyOnce_DedupSameOrderId()
        {
            var fo = new AutonomousFailoverController(new OrderAttestationService());
            Assert.True(fo.TryClaimDispatch(42));    // 최초 클레임
            Assert.False(fo.TryClaimDispatch(42));   // ★중복 — dedup 차단
            Assert.Equal(1L, fo.DedupBlocks);
            Assert.True(fo.TryClaimDispatch(43));    // 다른 ID — 통과
        }

        // ════════════ §3 ThreadStarvationSensor ════════════
        [Fact]
        public void Starvation_LowAvailability_ProvisionsAndBoosts()
        {
            bool prov = false, boost = false, restore = false;
            var ts = new ThreadStarvationSensor(
                provisionThreads: () => prov = true, boostHotPath: () => boost = true, restoreHotPath: () => restore = true,
                minAvailabilityScore: 0.15);
            Assert.True(ts.Observe(0, 0.05));   // 가용성 5% < 15% → 아사
            Assert.Equal(ThreadPoolHealth.Starved, ts.Health);
            Assert.True(prov && boost);
            // 회복.
            Assert.False(ts.Observe(0, 0.90));
            Assert.Equal(ThreadPoolHealth.Healthy, ts.Health);
            Assert.True(restore);
        }

        // ════════════ §4 AdaptiveOrphanReaper ════════════
        [Fact]
        public void Reaper_OrphanUnknown_PastReapDeadline_Freezes()
        {
            long clock = 0;
            var att = new OrderAttestationService();
            var reaper = new AdaptiveOrphanReaper(att, reconcile: _ => OrphanVerdict.Unknown, reclaim: null,
                nowTicks: () => clock, tickFrequency: 1_000_000L, reapMultiple: 3, fallbackDeadlineMs: 100);
            // 데드라인 100ms=100_000 ticks·재수확 300_000.
            Assert.True(reaper.Register(1, dispatchTicks: 0));
            clock = 400_000L;                    // 재수확 데드라인 초과
            reaper.SweepOnce();
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★불확정+재수확 초과 → fail-closed
            Assert.True(reaper.Freezes > 0);
        }

        [Fact]
        public void Reaper_OrphanNotFilled_ReclaimsHeal_NoFreeze()
        {
            long clock = 0; bool reclaimed = false;
            var att = new OrderAttestationService();
            var reaper = new AdaptiveOrphanReaper(att, reconcile: _ => OrphanVerdict.NotFilled, reclaim: _ => reclaimed = true,
                nowTicks: () => clock, tickFrequency: 1_000_000L, reapMultiple: 3, fallbackDeadlineMs: 100);
            reaper.Register(1, 0);
            clock = 150_000L;                    // 데드라인(100_000) 초과·재수확(300_000) 이내
            reaper.SweepOnce();
            Assert.True(reclaimed);              // ★예약 자본 회수(heal)
            Assert.Equal(1L, reaper.Reclaimed);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 동결 없음
            Assert.Equal(0, reaper.InFlightCount);       // 슬롯 해제
        }

        [Fact]
        public void Reaper_ResolvedBeforeDeadline_NoOrphan()
        {
            long clock = 0;
            var reaper = new AdaptiveOrphanReaper(nowTicks: () => clock, tickFrequency: 1_000_000L, fallbackDeadlineMs: 100);
            reaper.Register(1, 0);
            clock = 50_000L;
            reaper.Resolve(1);                   // 데드라인 전 종단 응답
            clock = 400_000L;
            reaper.SweepOnce();
            Assert.Equal(0L, reaper.Orphans);    // 고아 아님
            Assert.Equal(1L, reaper.Resolved);
        }

        [Fact]
        public void Reaper_SlotReuse_NoStaleIdentity()
        {
            // ★적대검증 회귀: 슬롯 재사용 시 이전 점유자의 stale OrderId 로 오고아 처리하면 안 됨(publish-after-populate 상태기계).
            long clock = 0; long reconciledId = -1;
            var reaper = new AdaptiveOrphanReaper(reconcile: id => { reconciledId = id; return OrphanVerdict.Filled; },
                nowTicks: () => clock, tickFrequency: 1_000_000L, capacity: 8, fallbackDeadlineMs: 100);
            reaper.Register(100, 0);
            clock = 10_000L; reaper.Resolve(100);   // 슬롯 해제(필드엔 stale 100 잔존)
            clock = 0; reaper.Register(200, 0);      // 같은 슬롯 재사용 — 200 발행
            clock = 200_000L; reaper.SweepOnce();    // 데드라인 초과 → 고아
            Assert.Equal(200L, reconciledId);        // ★stale 100 아님 — 발행 후에만 필드 유효
        }

        [Fact]
        public void Starvation_FullStarvation_ReportsZeroNot1000()
        {
            // ★적대검증 회귀: 완전 아사(availW=0→score 0)를 '미측정 1000'으로 오표시하면 안 됨.
            var ts = new ThreadStarvationSensor(minAvailabilityScore: 0.15);
            Assert.Equal(1000L, ts.AvailabilityScoreMilli);   // 미측정 → 중립 1000
            ts.Observe(0, 0.0);                                // 완전 아사 측정
            Assert.Equal(0L, ts.AvailabilityScoreMilli);       // ★진짜 0(100% 건강 오표시 봉쇄)
        }

        [Fact]
        public void Reaper_AdaptiveDeadline_ReflectsRttDistribution()
        {
            long clock = 0;
            var reaper = new AdaptiveOrphanReaper(nowTicks: () => clock, tickFrequency: 1_000_000L, kSigma: 4, fallbackDeadlineMs: 100);
            long fallback = reaper.AdaptiveDeadlineTicks();   // 표본 부족 → 폴백
            // RTT 표본 다수 적재(mean~1000, 분산 존재).
            for (long i = 0; i < 20; i++) { clock = 0; reaper.Register(100 + i, 0); clock = 900 + (i % 2) * 200; reaper.Resolve(100 + i); }
            long adaptive = reaper.AdaptiveDeadlineTicks();
            Assert.NotEqual(fallback, adaptive);              // ★고정이 아닌 실측 분포 기반
            Assert.True(adaptive >= 1000);                    // mean+Kσ ≳ mean
        }
    }

    // ── §5 ★적대검증(부하/타이밍 → 직렬화) ──────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class AutonomousWatchdogConcurrencyTests
    {
        [Fact]
        public void HotPath_WatchdogAndFailover_ZeroAllocation()
        {
            // ★실시간 감시견 + 페일오버 엔진의 핫패스 판정이 δ=0(GC 0바이트) — 50ms 드레인 핫패스에 할당 미추가 실증.
            var wd = new LiveSessionWatchdog(deadlineTicks: 100, comaThreshold: 1_000_000_000);   // 임계 크게(Coma 미격발)
            var fo = new AutonomousFailoverController(new OrderAttestationService(), seenCapacity: 8192);
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { wd.Observe(i & 63); fo.TryClaimDispatch(i); fo.ResolveChannel(); }   // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) { wd.Observe(i & 63); fo.TryClaimDispatch(i); fo.ResolveChannel(); }
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★감시견 Observe + 페일오버 클레임/해석 힙 할당 0
        }

        [Fact]
        public void Failover_ConcurrentSameId_ExactlyOneClaim()
        {
            // ★적대검증 회귀: 같은 OrderId 를 여러 스레드가 동시 클레임해도 정확히 1회만 true(dedup false-negative 봉쇄·이중발주 차단).
            var fo = new AutonomousFailoverController(new OrderAttestationService());
            const int threads = 8;
            long trueCount = 0;
            var barrier = new Barrier(threads);
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
                ths[t] = new Thread(() => { barrier.SignalAndWait(); if (fo.TryClaimDispatch(777)) Interlocked.Increment(ref trueCount); });
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Equal(1L, Interlocked.Read(ref trueCount));   // ★정확히 1회만 클레임 성공
        }

        [Fact]
        public void LiveArmed_FailoverMillionTimes_NoRealOrderWithoutDualKey()
        {
            // ★페일오버/세션 리셋이 백만 번 일어나도 이중키 없이는 실주문 0바이트 — 자율 복구가 무장 게이트를 우회하지 못함.
            var foAtt = new OrderAttestationService();
            var fo = new AutonomousFailoverController(foAtt);
            for (int i = 0; i < 1_000_000; i++)
            {
                fo.ReportChannel(ActiveChannel.Primary, (i & 1) == 0);   // Primary up/down 반복(Standby 상시 up → 양채널 다운 없음)
                fo.TryClaimDispatch(i);
            }
            Assert.True(fo.Failovers > 0);                    // 실제로 페일오버가 다수 발생
            Assert.Equal(GatingMode.Normal, foAtt.Mode);      // 양채널 다운 없어 동결 없음

            // 별도 게이트웨이(실전 flip·이중키 미충족) — 백만 페일오버와 무관하게 실 라우팅 0.
            var gwAtt = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: gwAtt);
            gw.EnterLive();
            var order = new ShadowRouteIntent(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(1m).Raw, 0L);
            Assert.Equal(RouteVerdict.Blocked, gw.TryRoute(order, dualKeyArmed: false, IgnitionStage.MasterIgnited));
            Assert.Equal(0L, gw.LiveRoutedCount);             // ★이중키 없이 실주문 0바이트
            Assert.Equal(GatingMode.EmergencyFreeze, gwAtt.Mode);   // fail-closed 동결
        }
    }
}
