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
    }
}
