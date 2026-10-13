// ============================================================================
// AutonomousUiVerificationTests — [자율 UI 검증 · 추천 A]
//
//  §1 [방향 A] 지문 — blazor:* 렌더러 내부 속성 무시 · 내용 민감 · δ=0(HashMarkup · Fnv64 · UiStateMatrix.Observe).
//  §2 지문 집합 — 고유/중복 판정 · 부하율 3/4 포화 계수(진단).
//  §3 [방향 B] 스톰 가드 — 주입 시계 결정론(창당 상한 · 초과 폐기 · 창 교체) · 다중 스레드 상한 · δ=0.
//  §4 ★UiSettingWriteGuard — 무장/안전 키 · 위장 변형 · 미분류 거부 / RCL 리터럴 키 전수 허용 / 실 어댑터 2종 + 임시 DB 종단.
//  §5 무장 프로브 — 29 비트 · 기준선 무결 · ★비공허성(물리 이중 키를 DB 에 직접 넣으면 위반 검출).
//  §6 bUnit 자율 퍼저 — 실 RCL 3 화면 순차+무작위 클릭 · 적대 기입 · 불법 무장 0 · UI 예외 0 · 토글 대칭 · DOM↔프레임 탐색 교차 일치.
//  §7 CLI 종단 — --verify-ui-switches 10,000 상호작용 + 1초 스톰 exit 0 · 사용법 오류 exit 1 · 결정론(같은 시드 = 같은 보고).
//  §8 [방향 B] 스톰 실측 — 1,000+/s 난사 · 상한 초과 폐기 · 코어 틱 무할당 · 예산 초과 비율 ≤ 0.1% · 무장 위반 0.
//  §9 ★음성 대조 — 가드 없는 포트(우회 기입) 표면이면 퍼저가 불법 무장을 잡아 FAIL.
//  §10 결선 · 격리 · 래칫 — Program.cs 조기 분기 · bUnit 은 테스트 프로젝트만 · Pure Shell 래칫 ≤ 40 · 오중 빌드 구성.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Simulation;
using StockManager.Tests.Simulation;
using StockManager.Tools;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class AutonomousUiVerificationTests
    {
        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        // ════════════ §1 지문 ════════════

        [Fact]
        public void Fingerprint_IgnoresBlazorInternals_SensitiveToContent_ZeroAlloc()
        {
            ulong a = UiStateFingerprint.HashMarkup("<button blazor:onclick=\"3\" class=\"x\">적용</button><input type=\"checkbox\" checked blazor:onchange=\"4\">");
            ulong b = UiStateFingerprint.HashMarkup("<button blazor:onclick=\"91\" class=\"x\">적용</button><input type=\"checkbox\" checked blazor:onchange=\"92\">");
            ulong c = UiStateFingerprint.HashMarkup("<button blazor:onclick=\"3\" class=\"x\">적용</button><input type=\"checkbox\" blazor:onchange=\"4\">");
            Assert.Equal(a, b);                                                                // 핸들러 id 변동 = 같은 상태
            Assert.NotEqual(a, c);                                                             // checked 소실 = 다른 상태
            Assert.NotEqual(UiStateFingerprint.HashMarkup("ab"), UiStateFingerprint.HashMarkup("ba"));
            Assert.NotEqual(0UL, UiStateFingerprint.HashMarkup(""));                          // 0 = 빈 슬롯 예약

            string markup = string.Concat(Enumerable.Repeat("<span blazor:onclick=\"7\" class=\"pill r\">💸 실거래</span>", 200));
            var matrix = new UiStateMatrix(1024);
            ulong sink = 0;
            UiStateFingerprint.HashMarkup(markup); matrix.Observe(1);                          // 워밍업
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 100; i++) sink ^= UiStateFingerprint.HashMarkup(markup);
                var h = Fnv64.Create(); h.Add(markup.AsSpan(0, 64)); h.Add('x'); h.Separator(); sink ^= h.Value;
                for (ulong k = 0; k < 500; k++) matrix.Observe(k * 0x9E3779B97F4A7C15UL);
            }));
            Assert.NotEqual(0UL, sink);
        }

        // ════════════ §2 지문 집합 ════════════

        [Fact]
        public void StateMatrix_Distinct_Duplicates_SaturatesAtThreeQuarters()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UiStateMatrix(100));
            var m = new UiStateMatrix(16);
            Assert.True(m.Observe(42));
            Assert.False(m.Observe(42));
            Assert.True(m.Observe(0));                                                         // 0 → 1 사상(유효 상태)
            Assert.False(m.Observe(1));                                                        // 0 과 동일 사상
            for (ulong k = 100; k < 200; k++) m.Observe(k);
            Assert.Equal(12, m.Distinct);                                                      // 16 × 3/4
            Assert.True(m.Saturated > 0);
            Assert.Equal(104, m.Observations);
        }

        // ════════════ §3 스톰 가드 ════════════

        [Fact]
        public void StormGuard_InjectedClock_CapPerWindow_DropExcess_WindowRollover_ZeroAlloc()
        {
            long now = 1_000;
            var g = new SwitchStormIsolationGuard(5, 100, () => now);
            int admitted = 0;
            for (int i = 0; i < 20; i++) if (g.TryAdmit()) admitted++;
            Assert.Equal(5, admitted);
            Assert.Equal(15, g.Dropped);
            now += 99;
            Assert.False(g.TryAdmit());                                                        // 같은 창
            now += 1;
            Assert.True(g.TryAdmit());                                                         // 새 창
            Assert.Equal(1, g.Windows);
            Assert.Equal(6, g.Admitted);

            var real = SwitchStormIsolationGuard.PerMilliseconds(100, 100);
            real.TryAdmit();
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) real.TryAdmit(); }));
        }

        [Fact]
        public void StormGuard_MultiThread_AdmittedBoundedPerWindow()
        {
            var g = new SwitchStormIsolationGuard(50, long.MaxValue / 4, () => 0);           // 창 교체 없음 = 정확히 50
            Parallel.For(0, 8, _ => { for (int i = 0; i < 5_000; i++) g.TryAdmit(); });
            Assert.Equal(50, g.Admitted);
            Assert.Equal(8 * 5_000 - 50, g.Dropped);
        }

        // ════════════ §4 UiSettingWriteGuard ════════════

        [Theory]
        [InlineData("Lan.Port", true)]
        [InlineData("Email.Pass", true)]
        [InlineData("Optimizer.IntervalHours", true)]
        [InlineData("Dispatch.ValveWired", false)]
        [InlineData("Dispatch.LiveArmingWired", false)]
        [InlineData("Broker.LiveArmed", false)]
        [InlineData("Broker.Mode", false)]
        [InlineData("Canary.CapBps", false)]
        [InlineData("Risk.MaxDailyLossPct", false)]
        [InlineData("Security.AntiDebug", false)]
        [InlineData("dispatch.valvewired", false)]
        [InlineData("lan.Port", false)]                                                        // 대소문자 위장 = 미분류
        [InlineData(" Lan.Port", false)]
        [InlineData("Lan.", false)]                                                            // 접두사만
        [InlineData("LanX.Port", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void WriteGuard_Allowlist(string? key, bool allowed) => Assert.Equal(allowed, UiSettingWriteGuard.IsUiWritable(key));

        [Fact]
        public void WriteGuard_AllowsEveryLiteralKeyTheRclWrites()
        {
            // 가드가 정당한 UI 기능을 끊지 않음: RCL 소스의 SetSetting/SetSecret 리터럴 키 전수가 허용 목록 안
            string rcl = Path.Combine(RepoRoot(), "MS2E.UI.Portable");
            var rx = new Regex("\\.Set(?:Setting|Secret)\\(\\s*\"([^\"]+)\"");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in Directory.EnumerateFiles(rcl, "*.razor", SearchOption.AllDirectories)
                                       .Concat(Directory.EnumerateFiles(rcl, "*.cs", SearchOption.AllDirectories)))
            {
                if (f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) || f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) continue;
                foreach (Match m in rx.Matches(File.ReadAllText(f))) keys.Add(m.Groups[1].Value);
            }
            Assert.True(keys.Count >= 20, "RCL 리터럴 키 수집 " + keys.Count);
            Assert.All(keys, k => Assert.True(UiSettingWriteGuard.IsUiWritable(k), k));
            // 동적 키(리터럴 아닌 인자) 기입 0 — 새 동적 기입이 생기면 허용 목록 판정을 다시 해야 한다
            foreach (var f in Directory.EnumerateFiles(rcl, "*.razor", SearchOption.AllDirectories))
                Assert.DoesNotMatch("\\.Set(?:Setting|Secret)\\(\\s*[^\"\\s]", File.ReadAllText(f));
        }

        [Fact]
        public void WriteGuard_RealAdapters_TempDb_DropArmingKeys_KeepUiKeys()
        {
            string path = Path.Combine(Path.GetTempPath(), "ms2e_uiguard_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var db = new LocalDatabase(path);
                var admin = new AdminSettingWidgetAdapter(db);
                var hub = new StrategyHubInfraAdapter(null!, db, new StockManager.Blazor.ToastService(), new SymbolContext());
                long r0 = UiSettingWriteGuard.Rejected;
                foreach (var k in UiArmingInvariantProbe.SafetyKeys)
                {
                    admin.SetSetting(k, "1"); admin.SetSecret(k, "1"); hub.SetSetting(k, "1");
                    Assert.Null(db.GetSetting(k));
                }
                Assert.True(UiSettingWriteGuard.Rejected - r0 >= 3 * UiArmingInvariantProbe.SafetyKeys.Length);
                admin.SetSetting("Lan.Port", "8123");
                hub.SetSetting("Optimizer.Enabled", "true");
                admin.SetLive(true);                                                           // 모드 플래그 = 무장 아님(별도 경로 · 허용)
                Assert.Equal("8123", db.GetSetting("Lan.Port"));
                Assert.Equal("true", db.GetSetting("Optimizer.Enabled"));
                Assert.True(admin.IsLive);
                Assert.False(BrokerHub.IsArmed(db));
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try { File.Delete(path); } catch (IOException) { }
            }
        }

        // ════════════ §5 무장 프로브 ════════════

        [Fact]
        public void Probe_29Bits_CleanBaseline_And_NonVacuous_PhysicalKeysDetected()
        {
            var store = new Dictionary<string, string>();
            var probe = new UiArmingInvariantProbe(k => store.TryGetValue(k, out var v) ? v : null);
            Assert.Equal(30, probe.BitCount);                                                     // 0~28 + StartupSanity(v12.17.6)
            Assert.Equal(UiArmingInvariantProbe.InterlockBitCount, probe.BitCount);
            Assert.True(probe.Mask.IsSealed);
            Assert.Equal(0, probe.Mask.Mask);                                                 // 최악의 출발점: 차단 비트 0
            Assert.False(probe.LiveArmed);
            Assert.True(probe.Check("baseline"));

            store["Dispatch.ValveWired"] = "1";                                                // 한 키만 = 무장 불가지만 안전 설정 변조로 검출
            Assert.False(probe.Check("valve only"));
            Assert.False(probe.Canary.IsArmed);
            store["Dispatch.LiveArmingWired"] = "1";                                           // 물리 이중 키 충족 → 카나리 Arm 성립 = 위반
            Assert.False(probe.Check("double key"));
            Assert.Equal(2, probe.Violations);
            Assert.StartsWith("안전 설정 변조 Dispatch.ValveWired", probe.FirstViolation);
            Assert.False(probe.Canary.IsArmed);                                                // 위반 시 즉시 Disarm
        }

        // ════════════ §6 bUnit 자율 퍼저 ════════════

        [Fact]
        public void Bunit_Fuzz_RealRclScreens_NoIllegalArming_NoFaults_ToggleSymmetry()
        {
            using var rig = new UiFuzzRig();
            using var surface = BunitClickSurface.CreateStandard(rig);
            var guard = SwitchStormIsolationGuard.PerMilliseconds(10_000, 100);
            var r = AutonomousUiSwitchFuzzer.Run(surface, rig.Probe, guard, 2_000, seed: 7, rig.Settings.SetSetting, hostileEvery: 25, readBack: rig.Db.GetSetting);

            Assert.True(r.Passed, Report(r) + " lastFault=" + surface.LastFault);
            Assert.True(r.Executed >= 2_000);
            Assert.True(r.Clicks > 0 && r.Checkboxes > 0 && r.NavLinks > 0, Report(r));
            Assert.True(r.SymmetryChecks > 0);
            Assert.Equal(0, r.ArmingViolations);
            Assert.Equal(0, r.UiFaults);
            Assert.Equal(80, r.HostileWrites);
            Assert.Equal(0, r.HostileAdmitted);
            Assert.False(r.LiveArmedFinal);
            Assert.Equal(0, r.InterlockMaskFinal);
            Assert.Equal(0, rig.Probe.Recorder.Faults);
            Assert.Null(rig.Db.GetSetting("Dispatch.ValveWired"));
            Assert.Null(rig.Db.GetSetting("Broker.LiveArmed"));
            Assert.True(rig.Settings.LanRestarts > 0);                                        // '적용' 버튼 도달(소켓은 하네스가 생략)
        }

        [Fact]
        public void Bunit_DomDiscovery_Matches_HeadlessFrameDiscovery()
        {
            using var rig = new UiFuzzRig();
            using var dom = BunitClickSurface.CreateStandard(rig);
            using var frames = rig.CreateHeadlessSurface();
            int nd = dom.Refresh(), nf = frames.Refresh();
            Assert.True(nd >= 25, "DOM 탐색 " + nd);
            Assert.Equal(nd, nf);
            for (int i = 0; i < nd; i++) Assert.Equal(dom.KindOf(i), frames.KindOf(i));        // 문서 순서 · 종류 1:1

            // 내비 그룹 펼침(같은 인덱스 조작) 후에도 두 탐색기 일치 — 동적으로 늘어난 하위 링크까지
            int head = Enumerable.Range(0, nd).Last(i => dom.Describe(i).StartsWith("Click<div> root=1", StringComparison.Ordinal));   // 첫 div = 팔레트 바 · 이후 = 그룹 헤드
            dom.Activate(head); frames.Activate(head);
            int nd2 = dom.Refresh(), nf2 = frames.Refresh();
            Assert.True(nd2 > nd, "펼침 후 " + nd2);
            Assert.Equal(nd2, nf2);
            for (int i = 0; i < nd2; i++) Assert.Equal(dom.KindOf(i), frames.KindOf(i));
        }

        // ════════════ §7 CLI 종단 ════════════

        [Fact]
        public void Cli_VerifyUiSwitches_10k_Interactions_Storm_Pass_Deterministic()
        {
            Assert.True(UiSwitchVerifierCli.IsInvocation(new[] { "--verify-ui-switches" }));
            Assert.False(UiSwitchVerifierCli.IsInvocation(new[] { "--headless" }));
            string outFile = Path.Combine(Path.GetTempPath(), "ms2e_uifuzz_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var sw = new StringWriter();
                int code = UiSwitchVerifierCli.Run(new[] { "--verify-ui-switches", "--iterations", "10000", "--storm-ms", "1000", "--out", outFile }, sw);
                string text = sw.ToString();
                Assert.True(code == UiSwitchVerifierCli.ExitOk, text);
                Assert.Contains("[ui-fuzz] verdict=PASS", text);
                Assert.Contains("[ui-storm]", text);
                Assert.Contains("liveArmed=False ignition=Locked canaryArmed=False", text);
                Assert.Contains("interlockBits=30 maskFinal=0x0", text);
                var m = Regex.Match(text, "executed=(\\d+)");
                Assert.True(m.Success && long.Parse(m.Groups[1].Value) >= 10_000, text);
                Assert.Equal(text, File.ReadAllText(outFile));

                var a = new StringWriter(); var b = new StringWriter();
                UiSwitchVerifierCli.Run(new[] { "--verify-ui-switches", "--iterations", "300", "--storm-ms", "0", "--seed", "99" }, a);
                UiSwitchVerifierCli.Run(new[] { "--verify-ui-switches", "--iterations", "300", "--storm-ms", "0", "--seed", "99" }, b);
                static string Stable(string s) => Regex.Replace(s, "throttleWaits=\\d+", "");
                Assert.Equal(Stable(a.ToString()), Stable(b.ToString()));                     // 같은 시드 = 같은 조작 · 같은 보고
            }
            finally { try { File.Delete(outFile); } catch (IOException) { } }
        }

        [Theory]
        [InlineData("--iterations", "0")]
        [InlineData("--iterations", "x")]
        [InlineData("--storm-ms", "-1")]
        [InlineData("--bogus", "1")]
        [InlineData("--seed", null)]
        public void Cli_UsageErrors_Exit1(string flag, string? value)
        {
            var args = value is null ? new[] { "--verify-ui-switches", flag } : new[] { "--verify-ui-switches", flag, value };
            var sw = new StringWriter();
            Assert.Equal(UiSwitchVerifierCli.ExitUsage, UiSwitchVerifierCli.Run(args, sw));
            Assert.Contains("사용법 오류", sw.ToString());
        }

        // ════════════ §8 스톰 실측 ════════════

        [Fact]
        public void Storm_1kPlusPerSecond_Throttled_CoreTickIsolated_NoArming()
        {
            using var rig = new UiFuzzRig();
            using var surface = rig.CreateHeadlessSurface();
            var guard = SwitchStormIsolationGuard.PerMilliseconds(100, 100);
            using var core = new CoreTickIsolationMonitor(rig.Hub, rig.Probe.Mask);
            var s = AutonomousUiSwitchFuzzer.RunStorm(surface, rig.Probe, guard, core, 1000, seed: 3);

            Assert.True(s.Passed, Text(s));
            Assert.True(s.AttemptsPerSecond >= 1_000, Text(s));
            Assert.True(s.Dropped > 0);
            Assert.True(s.Admitted <= guard.MaxPerWindow * (guard.Windows + 1), Text(s));      // 창당 상한
            Assert.Equal(0, s.CoreAllocatedBytes);                                            // 코어 틱 δ=0
            Assert.True(s.CoreTicks > 1_000, Text(s));
            Assert.True(s.OverBudgetRatio <= 1e-3, Text(s));
            Assert.Equal(0, s.ArmingViolations);
            Assert.Equal(0, core.BlockedSeen);                                                // 집계어 0 유지(오염 0)
            Assert.False(rig.Probe.LiveArmed);
        }

        // ════════════ §9 음성 대조 ════════════

        private sealed class BypassSurface : IUiClickSurface
        {
            private readonly LocalDatabase _db;
            public BypassSurface(LocalDatabase db) => _db = db;
            public int Refresh() => 1;
            public UiControlKind KindOf(int index) => UiControlKind.Click;
            public void Activate(int index)                                                   // 가드 없는 포트를 흉내: 무장 키를 DB 에 직접
            {
                _db.SetSetting("Dispatch.ValveWired", "1");
                _db.SetSetting("Dispatch.LiveArmingWired", "1");
            }
            public ulong Fingerprint() => 1;
            public ulong RootFingerprint(int index) => 1;
            public long Faults => 0;
            public string Describe(int index) => "bypass";
        }

        [Fact]
        public void NegativeControl_UnguardedPort_IsCaught_AsIllegalArming()
        {
            using var rig = new UiFuzzRig();
            var r = AutonomousUiSwitchFuzzer.Run(new BypassSurface(rig.Db), rig.Probe, SwitchStormIsolationGuard.PerMilliseconds(1000, 100), 5, seed: 1);
            Assert.False(r.Passed);
            Assert.True(r.ArmingViolations > 0);
            Assert.StartsWith("카나리 무장 성립", r.FirstViolation);                              // 이중 키 충족 → 호스트 재무장 경로가 성립 = 검출
            Assert.False(rig.Probe.Canary.IsArmed);                                           // 검출 즉시 Disarm
        }

        // ════════════ §10 결선 · 격리 · 래칫 ════════════

        [Fact]
        public void Wiring_EarlyCliBranch_BunitTestOnly_RatchetHeld_FiveBuildConfig()
        {
            string root = RepoRoot();
            string program = File.ReadAllText(Path.Combine(root, "StockManager", "Program.cs"));
            int cli = program.IndexOf("UiSwitchVerifierCli.IsInvocation(args)", StringComparison.Ordinal);
            Assert.True(cli > 0);
            Assert.True(cli < program.IndexOf("ApplicationConfiguration.Initialize()", StringComparison.Ordinal));   // UI 초기화 이전
            Assert.True(cli < program.IndexOf("new LocalDatabase()", StringComparison.Ordinal));                     // 운영 DB 이전

            Assert.Contains("Include=\"bunit\"", File.ReadAllText(Path.Combine(root, "StockManager.Tests", "StockManager.Tests.csproj")));
            Assert.DoesNotContain("bunit", File.ReadAllText(Path.Combine(root, "StockManager", "StockManager.csproj")), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("bunit", File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "MS2E.UI.Portable.csproj")), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(typeof(CanaryHealthSentinel).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("Bunit", StringComparison.OrdinalIgnoreCase));

            var r = PureShellFitnessSentinel.Scan(typeof(CanaryHealthSentinel).Assembly);
            Assert.True(r.WithinRatchet, $"coupled {r.HostCoupledUi} > ratchet {r.Ratchet}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);

            foreach (var a in new[] { "AdminSettingWidgetAdapter.cs", "StrategyHubInfraAdapter.cs" })
                Assert.Contains("UiSettingWriteGuard.Admit(key)", File.ReadAllText(Path.Combine(root, "src", "02_Infrastructure", "Engine", a)));

            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
        }

        private static string Report(UiFuzzReport r) { var sb = new StringBuilder(); r.AppendTo(sb); return sb.ToString(); }
        private static string Text(UiStormReport s) { var sb = new StringBuilder(); s.AppendTo(sb); return sb.ToString(); }
    }
}
