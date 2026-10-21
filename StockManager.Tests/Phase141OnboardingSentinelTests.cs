// ============================================================================
// Phase141OnboardingSentinelTests — v11.4.1 OnboardingHealthSentinel 적대적 검증(Think-Beyond)
//   ★환경 진단 + 이관 Port DI 배선 + liveArmed:false 부팅 안전 자세를 결정론·δ=0·fail-closed 로 봉인함을 실증.
// ============================================================================
using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase141OnboardingSentinelTests
    {
        // ── 결정론 커널 Evaluate ────────────────────────────────────────────────
        private static OnboardingCheck Ok(OnboardingCheckId id, OnboardingSeverity sev = OnboardingSeverity.Blocking)
            => new(id, sev, true);
        private static OnboardingCheck Fail(OnboardingCheckId id, OnboardingSeverity sev = OnboardingSeverity.Blocking)
            => new(id, sev, false);

        [Fact]
        public void Evaluate_AllPass_Ready()
        {
            var checks = new[]
            {
                Ok(OnboardingCheckId.DotnetRuntime),
                Ok(OnboardingCheckId.DataDirectoryWritable),
                Ok(OnboardingCheckId.KiwoomProxyPresent, OnboardingSeverity.Advisory),
            };
            var r = OnboardingHealthSentinel.Evaluate(checks);
            Assert.Equal(OnboardingReadiness.Ready, r.Readiness);
            Assert.Equal(0, r.Failed);
            Assert.Equal(0, r.BlockingFailed);
            Assert.Equal(OnboardingCheckId.None, r.FirstBlocker);
        }

        [Fact]
        public void Evaluate_BlockingFail_Blocked_WithFirstBlocker()
        {
            var checks = new[]
            {
                Ok(OnboardingCheckId.DotnetRuntime),
                Fail(OnboardingCheckId.DpapiAvailable),                 // Blocking 실패
                Fail(OnboardingCheckId.KiwoomProxyPresent, OnboardingSeverity.Advisory),
            };
            var r = OnboardingHealthSentinel.Evaluate(checks);
            Assert.Equal(OnboardingReadiness.Blocked, r.Readiness);
            Assert.Equal(2, r.Failed);
            Assert.Equal(1, r.BlockingFailed);
            Assert.Equal(OnboardingCheckId.DpapiAvailable, r.FirstBlocker);   // 스캔 순서상 최초 Blocking 실패
        }

        [Fact]
        public void Evaluate_OnlyAdvisoryFail_Degraded_NotBlocked()
        {
            var checks = new[]
            {
                Ok(OnboardingCheckId.DotnetRuntime),
                Fail(OnboardingCheckId.KiwoomProxyPresent, OnboardingSeverity.Advisory),   // Advisory 실패만
            };
            var r = OnboardingHealthSentinel.Evaluate(checks);
            Assert.Equal(OnboardingReadiness.Degraded, r.Readiness);
            Assert.Equal(1, r.Failed);
            Assert.Equal(0, r.BlockingFailed);
            Assert.Equal(OnboardingCheckId.None, r.FirstBlocker);
        }

        [Fact]
        public void Evaluate_Empty_FailClosed_Blocked()
        {
            var r = OnboardingHealthSentinel.Evaluate(ReadOnlySpan<OnboardingCheck>.Empty);
            Assert.Equal(OnboardingReadiness.Blocked, r.Readiness);   // ★미상 = 불안전(fail-closed)
            Assert.Equal(0, r.Total);
        }

        [Fact]
        public void Evaluate_MalformedNoneId_TreatedAsBlocking()
        {
            // Id=None 은 오조립 — 침묵 통과 금지(fail-closed → Blocking 실패로 취급).
            var checks = new[] { new OnboardingCheck(OnboardingCheckId.None, OnboardingSeverity.Ok, true) };
            var r = OnboardingHealthSentinel.Evaluate(checks);
            Assert.Equal(OnboardingReadiness.Blocked, r.Readiness);
            Assert.Equal(1, r.BlockingFailed);
        }

        [Fact]
        public void Evaluate_FirstBlocker_Is_ScanOrder_Deterministic()
        {
            var checks = new[]
            {
                Fail(OnboardingCheckId.DataDirectoryWritable),   // 첫 Blocking 실패
                Fail(OnboardingCheckId.LiveArmingSafePosture),   // 두 번째
            };
            var r = OnboardingHealthSentinel.Evaluate(checks);
            Assert.Equal(2, r.BlockingFailed);
            Assert.Equal(OnboardingCheckId.DataDirectoryWritable, r.FirstBlocker);   // 결정론적 = 스캔 순서 첫 실패
        }

        // ── 콜드 조립기 Diagnose (스텁 probe) ───────────────────────────────────
        private sealed class StubProbe : IOnboardingProbe
        {
            public bool RuntimeMeetsMinimum { get; init; } = true;
            public bool DataDirectoryWritable { get; init; } = true;
            public bool DpapiAvailable { get; init; } = true;
            public bool KiwoomProxyPresent { get; init; } = true;
            public bool LiveArmingSafeAtBoot { get; init; } = true;
            public bool MigratedPortsBound { get; init; } = true;
        }

        [Fact]
        public void Diagnose_AllGood_Ready()
        {
            var r = OnboardingHealthSentinel.Diagnose(new StubProbe());
            Assert.Equal(OnboardingReadiness.Ready, r.Readiness);
            Assert.Equal(OnboardingHealthSentinel.CheckCount, r.Total);
        }

        [Fact]
        public void Diagnose_LiveArmingUnsafe_Blocked()   // ★liveArmed:false 불변 실증
        {
            var r = OnboardingHealthSentinel.Diagnose(new StubProbe { LiveArmingSafeAtBoot = false });
            Assert.Equal(OnboardingReadiness.Blocked, r.Readiness);
            Assert.Equal(OnboardingCheckId.LiveArmingSafePosture, r.FirstBlocker);
            Assert.Contains("liveArmed:false", OnboardingHealthSentinel.FixHint(r.FirstBlocker));
        }

        [Fact]
        public void Diagnose_MigratedPortUnbound_Blocked()   // ★이관 컴포넌트 런타임 화이트스크린 방어
        {
            var r = OnboardingHealthSentinel.Diagnose(new StubProbe { MigratedPortsBound = false });
            Assert.Equal(OnboardingReadiness.Blocked, r.Readiness);
            Assert.Equal(OnboardingCheckId.MigratedPortBindings, r.FirstBlocker);
        }

        [Fact]
        public void Diagnose_OnlyKiwoomMissing_Degraded_NotBlocked()
        {
            var r = OnboardingHealthSentinel.Diagnose(new StubProbe { KiwoomProxyPresent = false });
            Assert.Equal(OnboardingReadiness.Degraded, r.Readiness);   // x86 프록시는 Advisory — 부팅 차단 아님
            Assert.Equal(0, r.BlockingFailed);
        }

        [Fact]
        public void Diagnose_NullProbe_FailClosed_Blocked()
        {
            var r = OnboardingHealthSentinel.Diagnose(null!);
            Assert.Equal(OnboardingReadiness.Blocked, r.Readiness);
        }

        [Fact]
        public void Run_Caches_LastReport_And_HasRun()
        {
            var s = new OnboardingHealthSentinel();
            Assert.False(s.HasRun);
            Assert.Equal(OnboardingReadiness.Blocked, s.LastReport.Readiness);   // Run 전 fail-closed
            var r = s.Run(new StubProbe());
            Assert.True(s.HasRun);
            Assert.Equal(OnboardingReadiness.Ready, r.Readiness);
            Assert.Equal(r, s.LastReport);
        }

        [Theory]
        [InlineData(OnboardingCheckId.DotnetRuntime)]
        [InlineData(OnboardingCheckId.DataDirectoryWritable)]
        [InlineData(OnboardingCheckId.DpapiAvailable)]
        [InlineData(OnboardingCheckId.KiwoomProxyPresent)]
        [InlineData(OnboardingCheckId.LiveArmingSafePosture)]
        [InlineData(OnboardingCheckId.MigratedPortBindings)]
        public void FixHint_KnownIds_NonEmpty(OnboardingCheckId id)
        {
            Assert.False(string.IsNullOrWhiteSpace(OnboardingHealthSentinel.FixHint(id)));
        }

        // ── DefaultOnboardingProbe: 실제 DI 컬렉션 Port 배선 스캔 ─────────────────
        [Fact]
        public void DefaultProbe_EmptyServices_PortsUnbound()
        {
            var probe = new DefaultOnboardingProbe(new ServiceCollection(), canaryArmedAtBoot: false);
            Assert.False(probe.MigratedPortsBound);   // 대표 Port 미등록 → 미배선(fail-closed)
        }

        [Fact]
        public void DefaultProbe_AllRequiredPortsRegistered_PortsBound()
        {
            var services = new ServiceCollection();
            // 디스크립터만 필요(팩토리는 스캔 시 호출되지 않음) — ServiceType 등록 사실만 검증.
            services.AddSingleton(typeof(MS2E.UI.Portable.Ports.Alerts.IAlertFeedPort), _ => null!);
            services.AddSingleton(typeof(MS2E.UI.Portable.Ports.Strategy.IStrategyHubInfraPort), _ => null!);
            services.AddSingleton(typeof(MS2E.UI.Portable.Ports.Strategy.ISignalsRepositoryPort), _ => null!);
            var probe = new DefaultOnboardingProbe(services, canaryArmedAtBoot: false);
            Assert.True(probe.MigratedPortsBound);
        }

        [Fact]
        public void DefaultProbe_CanaryArmedAtBoot_LiveArmingUnsafe()   // ★부팅 시 무장 흔적 = 안전 자세 위반
        {
            var probe = new DefaultOnboardingProbe(new ServiceCollection(), canaryArmedAtBoot: true);
            Assert.False(probe.LiveArmingSafeAtBoot);
        }
    }

    // ── δ=0 핫검증(부하 → 전용 스레드 직렬화) ──────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Phase141OnboardingSentinelZeroAllocTests
    {
        [Fact]
        public void Evaluate_Kernel_ZeroAllocation()
        {
            // ★결정론 커널 Evaluate 가 δ=0(GC 0바이트) — 스팬 폴드에 힙 할당 미추가 실증.
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Span<OnboardingCheck> buf = stackalloc OnboardingCheck[OnboardingHealthSentinel.CheckCount];
                    buf[0] = new(OnboardingCheckId.DotnetRuntime,         OnboardingSeverity.Blocking, true);
                    buf[1] = new(OnboardingCheckId.DataDirectoryWritable, OnboardingSeverity.Blocking, true);
                    buf[2] = new(OnboardingCheckId.DpapiAvailable,        OnboardingSeverity.Blocking, true);
                    buf[3] = new(OnboardingCheckId.KiwoomProxyPresent,    OnboardingSeverity.Advisory, false);   // 혼합(Degraded 경로)
                    buf[4] = new(OnboardingCheckId.LiveArmingSafePosture, OnboardingSeverity.Blocking, true);
                    buf[5] = new(OnboardingCheckId.MigratedPortBindings,  OnboardingSeverity.Blocking, true);

                    for (int i = 0; i < 300_000; i++) { var r = OnboardingHealthSentinel.Evaluate(buf); if (r.Total < 0) throw new Exception("warm"); }
                    long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
                    for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
                    {
                        long zb = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 100_000; i++) { var r = OnboardingHealthSentinel.Evaluate(buf); if (r.Total < 0) throw new Exception("hot"); }
                        minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
                    }
                    alloc = minAlloc;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        [Fact]
        public void Diagnose_WithNonAllocatingProbe_ZeroAllocation()
        {
            // ★콜드 조립기 Diagnose 도 무할당 probe 와 결합 시 δ=0(stackalloc + 순수 읽기).
            long alloc = 0; Exception? err = null;
            var probe = new NonAllocProbe();
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { var r = OnboardingHealthSentinel.Diagnose(probe); if (r.Total < 0) throw new Exception("warm"); }
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) { var r = OnboardingHealthSentinel.Diagnose(probe); if (r.Total < 0) throw new Exception("hot"); }
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        // 게터가 캐시된 bool 만 반환(무할당) — 콜드 I/O 없음.
        private sealed class NonAllocProbe : IOnboardingProbe
        {
            public bool RuntimeMeetsMinimum => true;
            public bool DataDirectoryWritable => true;
            public bool DpapiAvailable => true;
            public bool KiwoomProxyPresent => true;
            public bool LiveArmingSafeAtBoot => true;
            public bool MigratedPortsBound => true;
        }
    }
}
