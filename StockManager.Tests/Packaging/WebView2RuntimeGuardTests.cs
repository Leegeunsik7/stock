// ============================================================================
// WebView2RuntimeGuardTests — [v12.18.29-DEV] 호스트 사전 비행 WebView2 Evergreen 런타임 가드
//
//  §1 탐침 분류 — 버전 존재 = Present · 미설치 예외/로더 부재/미분류 예외/빈 버전 = 미설치(fail-safe) · 네트워크 탐침 예외 = 오프라인
//  §2 상태 전이 — Present = Blazor 진행(UI 상호작용 0) · MissingOnline 수락 = 공식 부트스트래퍼 트리거 · 거부 = 트리거 0 ·
//     트리거 실패 = 수동 안내 강등 · MissingOffline = 오프라인 안내만 · 미설치 전 경로 = 진행 거부(안전 종료 0)
//  §3 진입점 결선 — Program.cs: 가드가 DB/브로커/Blazor 홈 구성 '이전' · 클래식/헤드리스 제외 · 종료 코드 0 · 가드 소스 피어/브로커 접점 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
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
    }
}
