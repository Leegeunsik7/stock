// ============================================================================
// NightlyForensicWatchdogTests — v12.0.2 Part 2 CI/CD 야간 포렌식 회귀 게이트
//   ★NightlyForensicWatchdog(4대 축 정적 스캔) + ZeroAllocMemoryProfiler(실 핫패스 측정)를
//     실 어셈블리 위에서 구동해 시스템 전역 무결을 지상 실증한다.
//       ① RclZeroEngineRef        — MS2E.UI.Portable 0 엔진참조(순수성).
//       ② HostCoupledWithinRatchet — 호스트 결합 UI ≤ 래칫.
//       ③ RatchetCeilingHeld      — RatchetCeiling ≤ 40(감축선 상향 회귀 차단).
//       ④ Delta0HotPath           — 실 핫패스 힙 할당 0(프로파일러 측정값 주입).
//   ★fail-closed: 단 1축 위반 → AllClean=false + 포렌식 위반 로그 → 빌드 중단(이 Assert 가 게이트).
//   ※실 핫패스 δ=0 측정 → [Collection("ConcurrencyStress")](§4).
// ============================================================================
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class NightlyForensicWatchdogTests
    {
        private readonly ITestOutputHelper _out;
        public NightlyForensicWatchdogTests(ITestOutputHelper o) => _out = o;

        private static Assembly Host => typeof(CanaryHealthSentinel).Assembly;                                    // MS2E.StockSuite
        private static Assembly Rcl  => typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly; // MS2E.UI.Portable

        // ── §1 청정 시스템(핫패스 누수 0 주입) → 전축 통과·위반 로그 공백 ──────────────
        [Fact]
        public void Scan_CleanSystem_AllClean_NoLog()
        {
            var report = NightlyForensicWatchdog.Scan(Host, Rcl, delta0LeakBytes: 0);

            _out.WriteLine($"[Watchdog] allClean={report.AllClean}");
            foreach (var f in report.Findings)
                _out.WriteLine($"  {(f.Passed ? "PASS" : "FAIL")} {f.Axis} val={f.Value} :: {f.Detail}");

            Assert.True(report.AllClean);                 // ★4대 축 전통과
            Assert.All(report.Findings, f => Assert.True(f.Passed));
            Assert.Equal("", report.ViolationLog);        // 청정 → 위반 로그 없음
        }

        // ── §2 4대 축 전부 포함(축 누락 없이 완결) ──────────────────────────────────
        [Fact]
        public void Scan_Covers_All_Four_Axes()
        {
            var report = NightlyForensicWatchdog.Scan(Host, Rcl, 0);
            Assert.Equal(4, report.Findings.Count);
            var axes = report.Findings.Select(f => f.Axis).ToHashSet();
            Assert.Contains(ForensicAxis.RclZeroEngineRef, axes);
            Assert.Contains(ForensicAxis.HostCoupledWithinRatchet, axes);
            Assert.Contains(ForensicAxis.RatchetCeilingHeld, axes);
            Assert.Contains(ForensicAxis.Delta0HotPath, axes);
        }

        // ── §3 핫패스 누수 주입 → fail-closed(AllClean false)+포렌식 위반 로그 ─────────
        [Fact]
        public void Scan_Delta0Breach_FailsClosed_WithForensicLog()
        {
            var report = NightlyForensicWatchdog.Scan(Host, Rcl, delta0LeakBytes: 64);   // ★핫패스 64B 누수 모사

            Assert.False(report.AllClean);   // ★단 1축 위반 → 전체 fail-closed
            var d0 = report.Findings.Single(f => f.Axis == ForensicAxis.Delta0HotPath);
            Assert.False(d0.Passed);
            Assert.Equal(64L, d0.Value);

            // 나머지 3축은 여전히 통과(격리된 단일 위반).
            Assert.True(report.Findings.Single(f => f.Axis == ForensicAxis.RclZeroEngineRef).Passed);
            Assert.True(report.Findings.Single(f => f.Axis == ForensicAxis.HostCoupledWithinRatchet).Passed);
            Assert.True(report.Findings.Single(f => f.Axis == ForensicAxis.RatchetCeilingHeld).Passed);

            // 포렌식 위반 로그 — 비공백 + 위반 축·사유 각인.
            Assert.False(string.IsNullOrEmpty(report.ViolationLog));
            Assert.Contains("FORENSIC WATCHDOG", report.ViolationLog);
            Assert.Contains("Delta0HotPath", report.ViolationLog);
            Assert.Contains("64", report.ViolationLog);
            _out.WriteLine(report.ViolationLog);
        }

        // ── §4 실 어셈블리 축 물증 — RCL 순수·결합≤래칫·천장 감축선 ──────────────────
        [Fact]
        public void Scan_RealAssemblies_Axes_Reflect_GroundTruth()
        {
            var report = NightlyForensicWatchdog.Scan(Host, Rcl, 0);

            var purity = report.Findings.Single(f => f.Axis == ForensicAxis.RclZeroEngineRef);
            Assert.True(purity.Passed);   // MS2E.UI.Portable → 호스트/엔진 역참조 0

            var coupled = report.Findings.Single(f => f.Axis == ForensicAxis.HostCoupledWithinRatchet);
            Assert.True(coupled.Passed);
            Assert.True(coupled.Value <= PureShellFitnessSentinel.RatchetCeiling);   // 결합 UI ≤ 래칫

            var ceiling = report.Findings.Single(f => f.Axis == ForensicAxis.RatchetCeilingHeld);
            Assert.True(ceiling.Passed);
            Assert.Equal(PureShellFitnessSentinel.RatchetCeiling, (int)ceiling.Value);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= NightlyForensicWatchdog.MaxRatchetCeiling);   // ★천장 ≤ 40 감축선
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §5 End-to-End — 실 핫패스를 프로파일러로 측정(δ=0) → 워치독에 주입 → AllClean
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class NightlyForensicWatchdogE2ETests
    {
        private readonly ITestOutputHelper _out;
        public NightlyForensicWatchdogE2ETests(ITestOutputHelper o) => _out = o;

        private static Assembly Host => typeof(CanaryHealthSentinel).Assembly;
        private static Assembly Rcl  => typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly;

        // 워치독이 지키는 실 핫패스(HardwarePulseGuard.Evaluate) — 정적 상태·정적 델리게이트(캡처 0).
        private static readonly HardwarePulseGuard _guard = new(1);
        private static long _seq;
        private static void GuardedHotPath() { long s = Interlocked.Increment(ref _seq); _guard.Evaluate(s, s * 10L); }

        [Fact]
        public void RealHotPath_MeasuredZeroLeak_FeedsCleanWatchdog()
        {
            long leak = -1; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Volatile.Write(ref _seq, 0); _guard.Reset();
                    // ★프로파일러로 실 핫패스 δ=0 측정(워밍업 300k / 측정 100k).
                    leak = ZeroAllocMemoryProfiler.MeasureLeak(300_000, 100_000, GuardedHotPath);
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, leak);   // 실 핫패스 힙 0

            // ★측정값을 워치독에 주입 → 전 시스템 청정 판정 + 포렌식 무위반.
            var report = NightlyForensicWatchdog.Scan(Host, Rcl, leak);
            Assert.True(report.AllClean);
            Assert.Equal("", report.ViolationLog);
            _out.WriteLine($"[Watchdog E2E] hotpath leak={leak}B → allClean={report.AllClean}");
        }
    }
}
