// ============================================================================
// WebView2RuntimeGuardTests — [v12.18.29-DEV] 호스트 사전 비행 WebView2 Evergreen 런타임 가드
//
//  §1 탐침 분류 — 버전 존재 = Present · 미설치 예외/로더 부재/미분류 예외/빈 버전 = 미설치(fail-safe) · 네트워크 탐침 예외 = 오프라인
//  §2 상태 전이 — Present = Blazor 진행(UI 상호작용 0) · MissingOnline 수락 = 공식 부트스트래퍼 트리거 · 거부 = 트리거 0 ·
//     트리거 실패 = 수동 안내 강등 · MissingOffline = 오프라인 안내만 · 미설치 전 경로 = 진행 거부(안전 종료 0)
//  §3 진입점 결선 — Program.cs: 가드가 DB/브로커/Blazor 홈 구성 '이전' · 클래식/헤드리스 제외 · 종료 코드 0 · 가드 소스 피어/브로커 접점 0
//  §4 [v12.18.34-DEV] 결함 주입 — MS2E_FORCE_WEBVIEW2_MISSING 해석("1" · "silent" 외 None) · 주입 탐침 = 실 미설치 예외 · 설치 위장 값 0 ·
//     silent UI = 대화상자/브라우저 0 · 진입점 결선(주입 시 DefaultProbe 미사용)
//  §5 [v12.18.34-DEV] 실 프로세스 — 설치 PC 에서 silent 주입 호스트 기동 = 주 창 생성 0 · 제한 시간 내 exit 0(빈 화면 원천 차단 실증)
//  §6 [v12.18.35-DEV] 초기화 사전 탐침 · UserDataFolder 폴백 — 기본 통과 = 대체 미탐침 · 실 ACL 쓰기 거부/파일 잠금/경로 충돌 =
//     Temp 대체 실생성 전이 · 환경 탐침 예외/false = 후보 거부 · 전면 실패 = 복구 UI(예 = 클래식 · 아니요/UI 예외 = 안전 종료) ·
//     MS2E_FORCE_WEBVIEW2_CORRUPT 해석 · silent 복구 UI = 대화상자 0 · 진입점/홈 폼 결선 · 실 환경 탐침 무예외
//  §7 [v12.18.35-DEV] 실 프로세스 — 설치 PC 에서 손상 silent 주입 호스트 기동 = 주 창 생성 0 · 제한 시간 내 exit 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using StockManager.Blazor;
using Xunit;

namespace StockManager.Tests.Packaging
{
    public sealed class WebView2RuntimeGuardTests
    {
        private sealed class FakeUi : IWebView2GuardUi
        {
            public bool Accept;
            public bool ThrowOnOpen;
            public readonly List<string> Calls = new();

            public bool AskOpenBootstrapper(string url) { Calls.Add("ask:" + url); return Accept; }
            public void OpenUrl(string url)
            {
                Calls.Add("open:" + url);
                if (ThrowOnOpen) throw new System.ComponentModel.Win32Exception("기본 브라우저 없음");
            }
            public void ShowOffline(string url) => Calls.Add("offline:" + url);
            public void ShowOpenFailed(string url) => Calls.Add("openFailed:" + url);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln 루트 탐색 실패");
        }

        private static readonly Func<string?> Installed = () => "130.0.2849.80";
        private static readonly Func<string?> NotFound = () => throw new WebView2RuntimeNotFoundException();
        private const string Url = WebView2RuntimeGuard.EvergreenBootstrapperUrl;

        // ── §1 탐침 분류 ────────────────────────────────────────────────

        [Fact]
        public void Probe_VersionPresent_IsPresent_AndNetworkNotConsulted()
        {
            bool netAsked = false;
            Assert.Equal(WebView2RuntimeState.Present, WebView2RuntimeGuard.Classify(Installed, () => { netAsked = true; return true; }));
            Assert.False(netAsked);
            Assert.Equal("130.0.2849.80", WebView2RuntimeGuard.ProbeVersion(Installed));
        }

        public static IEnumerable<object[]> MissingProbes() => new[]
        {
            new object[] { "runtime-not-found", (Func<string?>)(() => throw new WebView2RuntimeNotFoundException()) },
            new object[] { "loader-dll-missing", (Func<string?>)(() => throw new DllNotFoundException("WebView2Loader.dll")) },
            new object[] { "unclassified", (Func<string?>)(() => throw new InvalidOperationException("미분류")) },
            new object[] { "null", (Func<string?>)(() => null) },
            new object[] { "empty", (Func<string?>)(() => "") },
            new object[] { "whitespace", (Func<string?>)(() => "  ") },
        };

        [Theory]
        [MemberData(nameof(MissingProbes))]
        public void Probe_AnyFailure_IsMissing_FailSafe(string _, Func<string?> probe)
        {
            Assert.Null(WebView2RuntimeGuard.ProbeVersion(probe));
            Assert.Equal(WebView2RuntimeState.MissingOnline, WebView2RuntimeGuard.Classify(probe, () => true));
            Assert.Equal(WebView2RuntimeState.MissingOffline, WebView2RuntimeGuard.Classify(probe, () => false));
        }

        [Fact]
        public void NetworkProbeThrows_TreatedAsOffline()
        {
            Assert.Equal(WebView2RuntimeState.MissingOffline,
                WebView2RuntimeGuard.Classify(NotFound, () => throw new System.Net.NetworkInformation.NetworkInformationException()));
        }

        [Fact]
        public void RealProbe_OnHost_NeverThrows()
        {
            // 개발/CI 호스트의 설치 여부와 무관하게 가드 탐침은 예외를 밖으로 내지 않는다.
            var ex = Record.Exception(() => WebView2RuntimeGuard.ProbeVersion(WebView2RuntimeGuard.DefaultProbe));
            Assert.Null(ex);
            Assert.Null(Record.Exception(() => WebView2RuntimeGuard.DefaultNetworkProbe()));
        }

        // ── §2 상태 전이 ────────────────────────────────────────────────

        [Fact]
        public void Present_ProceedsToBlazor_WithoutAnyUi()
        {
            var ui = new FakeUi();
            Assert.True(WebView2RuntimeGuard.EnsureRuntime(Installed, () => true, ui, out var outcome));
            Assert.Equal(WebView2GuardOutcome.ProceedBlazor, outcome);
            Assert.Empty(ui.Calls);
        }

        [Fact]
        public void MissingOnline_Accepted_TriggersOfficialBootstrapper_ThenSafeExit()
        {
            var ui = new FakeUi { Accept = true };
            Assert.False(WebView2RuntimeGuard.EnsureRuntime(NotFound, () => true, ui, out var outcome));
            Assert.Equal(WebView2GuardOutcome.BootstrapperOpened, outcome);
            Assert.Equal(new[] { "ask:" + Url, "open:" + Url }, ui.Calls);
            Assert.Equal("https://go.microsoft.com/fwlink/p/?LinkId=2124703", Url);
        }

        [Fact]
        public void MissingOnline_Declined_NoDownload_SafeExit()
        {
            var ui = new FakeUi { Accept = false };
            Assert.False(WebView2RuntimeGuard.EnsureRuntime(NotFound, () => true, ui, out var outcome));
            Assert.Equal(WebView2GuardOutcome.BootstrapperDeclined, outcome);
            Assert.Equal(new[] { "ask:" + Url }, ui.Calls);
        }

        [Fact]
        public void MissingOnline_OpenFails_DegradesToManualGuide_NoCrash()
        {
            var ui = new FakeUi { Accept = true, ThrowOnOpen = true };
            var ex = Record.Exception(() =>
            {
                Assert.False(WebView2RuntimeGuard.EnsureRuntime(NotFound, () => true, ui, out var outcome));
                Assert.Equal(WebView2GuardOutcome.BootstrapperOpenFailed, outcome);
            });
            Assert.Null(ex);
            Assert.Equal(new[] { "ask:" + Url, "open:" + Url, "openFailed:" + Url }, ui.Calls);
        }

        [Fact]
        public void MissingOffline_ShowsGuideOnly_NoAskNoDownload_SafeExit()
        {
            var ui = new FakeUi { Accept = true };
            Assert.False(WebView2RuntimeGuard.EnsureRuntime(NotFound, () => false, ui, out var outcome));
            Assert.Equal(WebView2GuardOutcome.OfflineExit, outcome);
            Assert.Equal(new[] { "offline:" + Url }, ui.Calls);
            Assert.Equal(0, WebView2RuntimeGuard.SafeExitCode);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EveryMissingState_NeverProceedsToBlazor(bool online)
        {
            var state = online ? WebView2RuntimeState.MissingOnline : WebView2RuntimeState.MissingOffline;
            foreach (var accept in new[] { true, false })
                foreach (var throwOnOpen in new[] { true, false })
                    Assert.NotEqual(WebView2GuardOutcome.ProceedBlazor,
                        WebView2RuntimeGuard.Resolve(state, new FakeUi { Accept = accept, ThrowOnOpen = throwOnOpen }));
        }

        // ── §3 진입점 결선 ──────────────────────────────────────────────

        [Fact]
        public void ProgramEntry_GuardRunsBeforeDbBrokerAndBlazorHome_ExitsZero()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Program.cs"));
            int guard = s.IndexOf("WebView2RuntimeGuard.EnsureRuntime(", StringComparison.Ordinal);
            Assert.True(guard > 0, "Program.cs 에 가드 결선 없음");
            Assert.True(guard < s.IndexOf("new LocalDatabase()", StringComparison.Ordinal), "가드가 DB 초기화 이후");
            Assert.True(guard < s.IndexOf("new KisProvider(", StringComparison.Ordinal), "가드가 브로커 구성 이후");
            Assert.True(guard < s.IndexOf("new StockManager.Blazor.BlazorHomeForm(", StringComparison.Ordinal), "가드가 Blazor 홈 이후");
            Assert.True(guard > s.IndexOf("ReleaseVerificationCli.IsInvocation(args)", StringComparison.Ordinal), "봉인 수검 CLI 분기보다 앞섬");
            int start = s.LastIndexOf("[v12.18.29] WebView2", guard, StringComparison.Ordinal);
            int end = s.IndexOf("// QuestPDF", guard, StringComparison.Ordinal);
            Assert.True(start > 0 && end > guard, "가드 블록 경계 탐색 실패");
            string window = s.Substring(start, end - start);
            Assert.Contains("!Has(\"--classic\") && !Has(\"--headless\")", window);
            Assert.Contains("WebView2RuntimeGuard.SafeExitCode", window);
            Assert.Contains("WebView2RuntimeGuard.DefaultProbe", window);
        }

        [Fact]
        public void GuardSource_UsesOfficialProbe_NoPeerNoBrokerNoRemoteExecution()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "WebView2RuntimeGuard.cs"));
            Assert.Contains("CoreWebView2Environment.GetAvailableBrowserVersionString()", s);
            foreach (var banned in new[] { "QuantumVault", "IBroker", "KisProvider", "LocalDatabase", "HttpClient", "WebClient", "/silent" + " /install\"" })
                Assert.DoesNotContain(banned, s);
        }

        // ── §4 결함 주입 ────────────────────────────────────────────────

        [Theory]
        [InlineData("1", "ForceMissing")]
        [InlineData(" 1 ", "ForceMissing")]
        [InlineData("silent", "ForceMissingSilent")]
        [InlineData("SILENT", "ForceMissingSilent")]
        [InlineData(null, "None")]
        [InlineData("", "None")]
        [InlineData("0", "None")]
        [InlineData("true", "None")]
        [InlineData("present", "None")]
        [InlineData("11", "None")]
        public void FaultInjection_Parse_OnlyExactValues(string? raw, string expected)
        {
            Assert.Equal(Enum.Parse<WebView2FaultInjection>(expected), WebView2RuntimeGuard.ParseFaultInjection(raw));
        }

        [Fact]
        public void FaultInjection_OnlyForcesMissing_NoBypassValue()
        {
            Assert.Equal(new[] { "None", "ForceMissing", "ForceMissingSilent" }, Enum.GetNames<WebView2FaultInjection>());
            Assert.Equal("MS2E_FORCE_WEBVIEW2_MISSING", WebView2RuntimeGuard.ForceMissingEnvVar);
            Assert.Throws<WebView2RuntimeNotFoundException>(() => WebView2RuntimeGuard.ForcedMissingProbe());
            foreach (bool online in new[] { true, false })
                Assert.NotEqual(WebView2RuntimeState.Present, WebView2RuntimeGuard.Classify(WebView2RuntimeGuard.ForcedMissingProbe, () => online));
        }

        [Theory]
        [InlineData(true, "BootstrapperDeclined")]
        [InlineData(false, "OfflineExit")]
        public void SilentUi_ForcedMissing_NoDialogNoDownload_SafeExit(bool online, string expected)
        {
            var lines = new List<string>();
            var ui = new WebView2RuntimeGuard.SilentUi(lines.Add);
            Assert.False(WebView2RuntimeGuard.EnsureRuntime(WebView2RuntimeGuard.ForcedMissingProbe, () => online, ui, out var outcome));
            Assert.Equal(Enum.Parse<WebView2GuardOutcome>(expected), outcome);
            Assert.Single(lines);
            Assert.Contains(Url, lines[0]);
            Assert.DoesNotContain(lines, l => l.Contains("브라우저 실행", StringComparison.Ordinal));
        }

        [Fact]
        public void ProgramEntry_FaultInjection_ReplacesProbeAndUi_BeforeAnyEngineWiring()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Program.cs"));
            int read = s.IndexOf("WebView2RuntimeGuard.ReadFaultInjection()", StringComparison.Ordinal);
            int ensure = s.IndexOf("WebView2RuntimeGuard.EnsureRuntime(", StringComparison.Ordinal);
            int exit = s.IndexOf("WebView2RuntimeGuard.SafeExitCode", StringComparison.Ordinal);
            Assert.True(read > 0 && read < ensure && ensure < exit, "주입 판독 → 가드 → 안전 종료 순서 위반");
            string window = s.Substring(read, s.IndexOf("return;", exit, StringComparison.Ordinal) - read);
            Assert.Contains("WebView2RuntimeGuard.ForcedMissingProbe", window);
            Assert.Contains("WebView2FaultInjection.None", window);
            Assert.Contains("new StockManager.Blazor.WebView2RuntimeGuard.SilentUi(", window);
            // 가드 이전(Main 진입 ~ 안전 종료) 구간에 브로커 · DB · 피어 · 무장 접점 0
            int main = s.IndexOf("static void Main(", StringComparison.Ordinal);
            string pre = s.Substring(main, s.IndexOf("return;", exit, StringComparison.Ordinal) - main);
            foreach (var banned in new[] { "QuantumVault", "KisProvider", "LocalDatabase", "IBroker", "BlazorHomeForm", "LiveArm", "ValveWired" })
                Assert.DoesNotContain(banned, pre);
        }

        // ── §6 초기화 사전 탐침 · UserDataFolder 폴백 ───────────────────────

        private sealed class FakeRecoveryUi : IWebView2RecoveryUi
        {
            public bool Classic;
            public bool Throw;
            public readonly List<string> Calls = new();

            public bool AskClassicFallback(string primaryFolder, string fallbackFolder)
            {
                Calls.Add(primaryFolder + "|" + fallbackFolder);
                if (Throw) throw new InvalidOperationException("대화상자 실패");
                return Classic;
            }
        }

        private static string NewTempDir(string tag) =>
            Path.Combine(Path.GetTempPath(), "ms2e_wv2_test_" + tag + "_" + Guid.NewGuid().ToString("N"));

        private static void TryDelete(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); else if (File.Exists(path)) File.Delete(path); }
            catch (Exception) { }
        }

        [Theory]
        [InlineData("1", "ForceCorrupt")]
        [InlineData(" 1 ", "ForceCorrupt")]
        [InlineData("silent", "ForceCorruptSilent")]
        [InlineData("Silent", "ForceCorruptSilent")]
        [InlineData(null, "None")]
        [InlineData("", "None")]
        [InlineData("0", "None")]
        [InlineData("ok", "None")]
        [InlineData("healthy", "None")]
        public void CorruptInjection_Parse_OnlyExactValues(string? raw, string expected)
        {
            Assert.Equal(Enum.Parse<WebView2CorruptInjection>(expected), WebView2RuntimeGuard.ParseCorruptInjection(raw));
        }

        [Fact]
        public void CorruptInjection_OnlyForcesFailure_NoBypassValue()
        {
            Assert.Equal(new[] { "None", "ForceCorrupt", "ForceCorruptSilent" }, Enum.GetNames<WebView2CorruptInjection>());
            Assert.Equal("MS2E_FORCE_WEBVIEW2_CORRUPT", WebView2RuntimeGuard.ForceCorruptEnvVar);
            Assert.Throws<System.Runtime.InteropServices.COMException>(() => WebView2RuntimeGuard.ForcedCorruptEnvironmentProbe("x"));
            Assert.False(WebView2RuntimeGuard.ProbeCandidate(Path.GetTempPath(), _ => true, WebView2RuntimeGuard.ForcedCorruptEnvironmentProbe));
        }

        [Fact]
        public void ProfileFolders_PrimaryMatchesBlazorDefault_FallbackIsTempStockManagerWv2()
        {
            string primary = WebView2RuntimeGuard.DefaultUserDataFolder();
            Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), primary, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(".WebView2", primary, StringComparison.Ordinal);
            Assert.Equal(Path.Combine(Path.GetTempPath(), "StockManager_WV2"), WebView2RuntimeGuard.TempFallbackUserDataFolder());
        }

        [Fact]
        public void PrimaryHealthy_SelectsPrimary_FallbackNeverProbed_NoUi()
        {
            var probed = new List<string>();
            var ui = new FakeRecoveryUi();
            var outcome = WebView2RuntimeGuard.EnsureProfile(f => { probed.Add(f); return true; }, _ => true, "P", "F", ui, out var udf);
            Assert.Equal(WebView2ProfileOutcome.Primary, outcome);
            Assert.Equal("P", udf);
            Assert.Equal(new[] { "P" }, probed);
            Assert.Empty(ui.Calls);
        }

        [Fact]
        public void PrimaryUnauthorized_TransitionsToTempFallback_FolderActuallyCreated()
        {
            string fallback = NewTempDir("fb");
            try
            {
                var ui = new FakeRecoveryUi();
                var outcome = WebView2RuntimeGuard.EnsureProfile(
                    f => f == "P" ? throw new UnauthorizedAccessException("기본 프로필 접근 거부") : WebView2RuntimeGuard.DefaultFolderWritableProbe(f),
                    _ => true, "P", fallback, ui, out var udf);
                Assert.Equal(WebView2ProfileOutcome.TempFallback, outcome);
                Assert.Equal(fallback, udf);
                Assert.True(Directory.Exists(fallback), "Temp 대체 프로필 폴더 미생성");
                Assert.False(File.Exists(Path.Combine(fallback, WebView2RuntimeGuard.WriteProbeFileName)), "쓰기 프로브 파일 잔존");
                Assert.Empty(ui.Calls);
            }
            finally { TryDelete(fallback); }
        }

        [Fact]
        public void RealAclWriteDenied_Primary_IsRejected_TempFallbackSelected()
        {
            string primary = NewTempDir("acl");
            string fallback = NewTempDir("fb");
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            var deny = new System.Security.AccessControl.FileSystemAccessRule(sid,
                System.Security.AccessControl.FileSystemRights.CreateFiles | System.Security.AccessControl.FileSystemRights.CreateDirectories,
                System.Security.AccessControl.AccessControlType.Deny);
            var di = Directory.CreateDirectory(primary);
            var acl = di.GetAccessControl();
            acl.AddAccessRule(deny);
            di.SetAccessControl(acl);
            try
            {
                Assert.Throws<UnauthorizedAccessException>(() => WebView2RuntimeGuard.DefaultFolderWritableProbe(primary));
                var outcome = WebView2RuntimeGuard.EnsureProfile(WebView2RuntimeGuard.DefaultFolderWritableProbe, _ => true,
                    primary, fallback, new FakeRecoveryUi(), out var udf);
                Assert.Equal(WebView2ProfileOutcome.TempFallback, outcome);
                Assert.Equal(fallback, udf);
                Assert.True(Directory.Exists(fallback));
            }
            finally
            {
                acl = di.GetAccessControl();
                acl.RemoveAccessRule(deny);
                di.SetAccessControl(acl);
                TryDelete(primary);
                TryDelete(fallback);
            }
        }

        [Fact]
        public void LockedProbeFile_Primary_IsRejected_TempFallbackSelected()
        {
            string primary = NewTempDir("lock");
            string fallback = NewTempDir("fb");
            Directory.CreateDirectory(primary);
            try
            {
                using (new FileStream(Path.Combine(primary, WebView2RuntimeGuard.WriteProbeFileName), FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Assert.ThrowsAny<IOException>(() => WebView2RuntimeGuard.DefaultFolderWritableProbe(primary));
                    var outcome = WebView2RuntimeGuard.EnsureProfile(WebView2RuntimeGuard.DefaultFolderWritableProbe, _ => true,
                        primary, fallback, new FakeRecoveryUi(), out var udf);
                    Assert.Equal(WebView2ProfileOutcome.TempFallback, outcome);
                    Assert.Equal(fallback, udf);
                }
            }
            finally { TryDelete(primary); TryDelete(fallback); }
        }

        [Fact]
        public void PrimaryPathIsAFile_IsRejected_TempFallbackSelected()
        {
            string primary = NewTempDir("file");
            string fallback = NewTempDir("fb");
            File.WriteAllText(primary, "not a directory");
            try
            {
                var outcome = WebView2RuntimeGuard.EnsureProfile(WebView2RuntimeGuard.DefaultFolderWritableProbe, _ => true,
                    primary, fallback, new FakeRecoveryUi(), out var udf);
                Assert.Equal(WebView2ProfileOutcome.TempFallback, outcome);
                Assert.Equal(fallback, udf);
            }
            finally { TryDelete(primary); TryDelete(fallback); }
        }

        public static IEnumerable<object[]> FailingEnvironmentProbes() => new[]
        {
            new object[] { "com-corrupt", (Func<string, bool>)WebView2RuntimeGuard.ForcedCorruptEnvironmentProbe },
            new object[] { "runtime-not-found", (Func<string, bool>)(_ => throw new WebView2RuntimeNotFoundException()) },
            new object[] { "unauthorized", (Func<string, bool>)(_ => throw new UnauthorizedAccessException()) },
            new object[] { "unclassified", (Func<string, bool>)(_ => throw new InvalidOperationException("미분류")) },
            new object[] { "timeout-or-null", (Func<string, bool>)(_ => false) },
        };

        [Theory]
        [MemberData(nameof(FailingEnvironmentProbes))]
        public void EnvironmentFailure_OnBothCandidates_NeverSelectsAFolder(string _, Func<string, bool> envProbe)
        {
            foreach (var classic in new[] { true, false })
            {
                var ui = new FakeRecoveryUi { Classic = classic };
                var outcome = WebView2RuntimeGuard.EnsureProfile(f => true, envProbe, "P", "F", ui, out var udf);
                Assert.Null(udf);
                Assert.Equal(classic ? WebView2ProfileOutcome.FallbackClassic : WebView2ProfileOutcome.CorruptExit, outcome);
                Assert.Equal(new[] { "P|F" }, ui.Calls);
            }
        }

        [Fact]
        public void PrimaryEnvironmentCorrupt_FallbackHealthy_SelectsFallback()
        {
            var outcome = WebView2RuntimeGuard.EnsureProfile(_ => true, f => f == "F", "P", "F", new FakeRecoveryUi(), out var udf);
            Assert.Equal(WebView2ProfileOutcome.TempFallback, outcome);
            Assert.Equal("F", udf);
        }

        [Fact]
        public void RecoveryUiThrows_DegradesToSafeExit_NoCrash()
        {
            var ui = new FakeRecoveryUi { Classic = true, Throw = true };
            WebView2ProfileOutcome outcome = default;
            string? udf = "x";
            Assert.Null(Record.Exception(() =>
                outcome = WebView2RuntimeGuard.EnsureProfile(_ => true, _ => false, "P", "F", ui, out udf)));
            Assert.Equal(WebView2ProfileOutcome.CorruptExit, outcome);
            Assert.Null(udf);
        }

        [Fact]
        public void SilentRecoveryUi_ForcedCorrupt_NoDialog_SafeExit()
        {
            var lines = new List<string>();
            var outcome = WebView2RuntimeGuard.EnsureProfile(_ => true, WebView2RuntimeGuard.ForcedCorruptEnvironmentProbe, "P", "F",
                new WebView2RuntimeGuard.SilentRecoveryUi(lines.Add), out var udf);
            Assert.Equal(WebView2ProfileOutcome.CorruptExit, outcome);
            Assert.Null(udf);
            Assert.Single(lines);
            Assert.Contains("P", lines[0]);
            Assert.Contains("F", lines[0]);
        }

        [Fact]
        public void RealEnvironmentProbe_OnStaThread_NeverThrows_PresentRuntimeSelectsPrimary()
        {
            string primary = NewTempDir("real");
            string fallback = NewTempDir("fb");
            Exception? ex = null;
            WebView2ProfileOutcome outcome = default;
            var t = new Thread(() =>
            {
                try
                {
                    outcome = WebView2RuntimeGuard.EnsureProfile(WebView2RuntimeGuard.DefaultFolderWritableProbe,
                        WebView2RuntimeGuard.DefaultEnvironmentProbe, primary, fallback, new FakeRecoveryUi(), out _);
                }
                catch (Exception e) { ex = e; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            try
            {
                Assert.True(t.Join(TimeSpan.FromSeconds(60)), "실 환경 탐침이 제한 시간 내 반환하지 않음");
                Assert.Null(ex);
                if (WebView2RuntimeGuard.ProbeVersion(WebView2RuntimeGuard.DefaultProbe) is not null)
                    Assert.Equal(WebView2ProfileOutcome.Primary, outcome);
            }
            finally { TryDelete(primary); TryDelete(fallback); }
        }

        [Fact]
        public void ProgramEntry_ProfileProbe_AfterRuntimeGuard_BeforeEngineWiring_ExitsZero()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Program.cs"));
            int runtime = s.IndexOf("WebView2RuntimeGuard.EnsureRuntime(", StringComparison.Ordinal);
            int corrupt = s.IndexOf("WebView2RuntimeGuard.ReadCorruptInjection()", StringComparison.Ordinal);
            int profile = s.IndexOf("WebView2RuntimeGuard.EnsureProfile(", StringComparison.Ordinal);
            int exitCorrupt = s.IndexOf("WebView2ProfileOutcome.CorruptExit", StringComparison.Ordinal);
            Assert.True(runtime > 0 && runtime < corrupt && corrupt < profile && profile < exitCorrupt, "런타임 가드 → 주입 판독 → 프로필 탐침 → 안전 종료 순서 위반");
            Assert.True(profile < s.IndexOf("new LocalDatabase()", StringComparison.Ordinal), "프로필 탐침이 DB 초기화 이후");
            Assert.True(profile < s.IndexOf("new KisProvider(", StringComparison.Ordinal), "프로필 탐침이 브로커 구성 이후");
            string window = s.Substring(corrupt, s.IndexOf("// QuestPDF", corrupt, StringComparison.Ordinal) - corrupt);
            Assert.Contains("WebView2RuntimeGuard.DefaultEnvironmentProbe", window);
            Assert.Contains("WebView2RuntimeGuard.ForcedCorruptEnvironmentProbe", window);
            Assert.Contains("WebView2RuntimeGuard.TempFallbackUserDataFolder()", window);
            Assert.Contains("new StockManager.Blazor.WebView2RuntimeGuard.SilentRecoveryUi(", window);
            Assert.Contains("WebView2RuntimeGuard.SafeExitCode", window);
            foreach (var banned in new[] { "QuantumVault", "KisProvider", "LocalDatabase", "IBroker", "BlazorHomeForm", "LiveArm", "ValveWired" })
                Assert.DoesNotContain(banned, window);
            Assert.Contains("bool classic = Has(\"--classic\") || wv2ForceClassic;", s);
            Assert.Contains("new StockManager.Blazor.BlazorHomeForm(provider, db, wv2UserDataFolder)", s);
        }

        [Fact]
        public void HomeForm_BindsProbedProfile_AndAttachesInitializationWatchdog()
        {
            string s = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            int view = s.IndexOf("var view = new BlazorWebView", StringComparison.Ordinal);
            int add = s.IndexOf("Controls.Add(view);", view, StringComparison.Ordinal);
            Assert.True(view > 0 && add > view);
            string window = s.Substring(view, add - view);
            Assert.Contains("view.BlazorWebViewInitializing += (_, e) => e.UserDataFolder = webView2UserDataFolder;", window);
            Assert.Contains("WebView2RuntimeGuard.AttachInitializationWatchdog(view.WebView, this,", window);
        }
    }

    // ── §5 실 프로세스 — silent 주입 호스트 기동 ───────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class WebView2MissingRuntimeProcessTests
    {
        [Fact]
        public void HostProcess_ForcedMissingSilent_NoMainWindow_ExitsZero()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "MS2E.StockSuite.exe");
            Assert.True(File.Exists(exe), "테스트 출력에 호스트 apphost 없음: " + exe);

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            psi.Environment[WebView2RuntimeGuard.ForceMissingEnvVar] = "silent";
            psi.Environment.Remove("MS2E_LIVE_ARM");

            using var p = Process.Start(psi)!;
            bool windowSeen = false;
            var sw = Stopwatch.StartNew();
            try
            {
                while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(30))
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero) windowSeen = true;
                    Thread.Sleep(25);
                }
                Assert.True(p.HasExited, "가드 미작동 — 30초 내 종료되지 않음(Blazor 기동 진행 의심)");
                p.WaitForExit();
                Assert.False(windowSeen, "주 창 생성 관측 — 미설치 강제 하에서 호스트 윈도우가 만들어짐");
                Assert.Equal(WebView2RuntimeGuard.SafeExitCode, p.ExitCode);
            }
            finally
            {
                if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }
            }
        }

        // ── §7 [v12.18.35] 손상 silent 주입 — 런타임 탐침 통과 후 환경 생성 손상 → 빈 화면 없이 안전 종료 ──
        [Fact]
        public void HostProcess_ForcedCorruptSilent_NoMainWindow_ExitsZero()
        {
            string exe = Path.Combine(AppContext.BaseDirectory, "MS2E.StockSuite.exe");
            Assert.True(File.Exists(exe), "테스트 출력에 호스트 apphost 없음: " + exe);
            Assert.True(WebView2RuntimeGuard.ProbeVersion(WebView2RuntimeGuard.DefaultProbe) is not null,
                "호스트에 WebView2 런타임 미설치 — 손상 주입 경로(런타임 탐침 통과 후 단계) 실증 불가");

            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
            psi.Environment[WebView2RuntimeGuard.ForceCorruptEnvVar] = "silent";
            psi.Environment.Remove(WebView2RuntimeGuard.ForceMissingEnvVar);
            psi.Environment.Remove("MS2E_LIVE_ARM");

            using var p = Process.Start(psi)!;
            bool windowSeen = false;
            var sw = Stopwatch.StartNew();
            try
            {
                while (!p.HasExited && sw.Elapsed < TimeSpan.FromSeconds(30))
                {
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero) windowSeen = true;
                    Thread.Sleep(25);
                }
                Assert.True(p.HasExited, "프로필 가드 미작동 — 30초 내 종료되지 않음(Blazor 기동 진행 의심)");
                p.WaitForExit();
                Assert.False(windowSeen, "주 창 생성 관측 — 손상 강제 하에서 호스트 윈도우가 만들어짐");
                Assert.Equal(WebView2RuntimeGuard.SafeExitCode, p.ExitCode);
            }
            finally
            {
                if (!p.HasExited) { try { p.Kill(entireProcessTree: true); } catch (Exception) { } }
            }
        }
    }
}
