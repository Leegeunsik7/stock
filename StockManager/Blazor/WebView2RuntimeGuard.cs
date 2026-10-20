// WebView2RuntimeGuard — [v12.18.29-DEV] Blazor 호스트 사전 비행(Pre-Flight) WebView2 Evergreen 런타임 가드
//
//  문제: BlazorWebView 는 런타임 미설치 PC 에서 생성자 예외 없이 비동기 초기화만 실패 → 빈 화면(White Screen).
//        Program.cs 의 try/catch 클래식 폴백은 이 경로를 잡지 못한다.
//  해법: UI·DB·브로커 구성 '이전'에 CoreWebView2Environment.GetAvailableBrowserVersionString() 으로 탐침.
//        Present        → Blazor 기동 진행
//        MissingOnline  → 공식 Evergreen 부트스트래퍼 다운로드 안내(예 = 브라우저로 다운로드 트리거) → 안전 종료(0)
//        MissingOffline → 오프라인 안내 메시지 → 안전 종료(0)
//  불변: 탐침 실패(미설치 예외 · 로더 DLL 부재 · 미분류 예외 · 빈 버전) = 전부 '미설치'(fail-safe — 빈 화면 원천 차단).
//        네트워크 탐침 예외 = 오프라인. 다운로드 트리거 실패 = 수동 안내로 강등(크래시 0).
//        브로커·DB·피어 저장소 접점 0 — 실 발주 경로 미접촉(liveArmed:false 무관).
//  ※ 무인 백그라운드 설치(내려받은 exe 직접 실행)는 Authenticode 체인 검증 없이 원격 바이너리를 실행하게 되므로 미채택 —
//    설치형 배포본(Inno Setup)은 동봉 부트스트래퍼로 이미 무인 설치(/silent /install)한다.
//
//  [v12.18.34-DEV] 결함 주입 — 설치 PC 에서 미설치 경로를 실기 재현(MS2E_FORCE_WEBVIEW2_MISSING):
//        "1"      → 탐침 강제 미설치(WebView2RuntimeNotFoundException) · 실 안내 대화상자(운용자 리허설)
//        "silent" → 탐침 강제 미설치 · 무대화 로그 UI(다운로드 트리거 0 · 프로세스 수준 자동 검증용)
//        그 외    → 주입 없음. 주입은 '미설치' 방향만 강제 — 미설치 PC 를 설치로 위장하는 값은 존재하지 않는다(우회 0).
using System;
using System.Windows.Forms;

namespace StockManager.Blazor
{
    internal enum WebView2RuntimeState { Present, MissingOnline, MissingOffline }

    internal enum WebView2GuardOutcome
    {
        ProceedBlazor,           // 런타임 확인 → Blazor 기동
        BootstrapperOpened,      // 다운로드 트리거 성공 → 종료(0)
        BootstrapperDeclined,    // 운용자 거부 → 종료(0)
        BootstrapperOpenFailed,  // 트리거 실패 → 수동 안내 후 종료(0)
        OfflineExit,             // 오프라인 안내 → 종료(0)
    }

    internal enum WebView2FaultInjection { None, ForceMissing, ForceMissingSilent }

    /// <summary>가드의 사용자 상호작용 봉합선 — 단위 테스트는 모의, 실 호스트는 MessageBox/셸 실행.</summary>
    internal interface IWebView2GuardUi
    {
        bool AskOpenBootstrapper(string url);
        void OpenUrl(string url);
        void ShowOffline(string url);
        void ShowOpenFailed(string url);
    }

    internal static class WebView2RuntimeGuard
    {
        /// <summary>Microsoft 공식 Evergreen 부트스트래퍼(MicrosoftEdgeWebview2Setup.exe) — build-installer.ps1 · installer/*.iss 와 동일 링크.</summary>
        public const string EvergreenBootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

        public const int SafeExitCode = 0;

        /// <summary>실 탐침 — 미설치 시 WebView2RuntimeNotFoundException.</summary>
        public static string? DefaultProbe() => Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();

        public static bool DefaultNetworkProbe() => System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable();

        public const string ForceMissingEnvVar = "MS2E_FORCE_WEBVIEW2_MISSING";

        /// <summary>결함 주입 값 해석 — "1" · "silent"(대소문자 무시 · 앞뒤 공백 무시) 외 전부 None.</summary>
        public static WebView2FaultInjection ParseFaultInjection(string? raw)
        {
            var v = raw?.Trim();
            if (v == "1") return WebView2FaultInjection.ForceMissing;
            if (string.Equals(v, "silent", StringComparison.OrdinalIgnoreCase)) return WebView2FaultInjection.ForceMissingSilent;
            return WebView2FaultInjection.None;
        }

        public static WebView2FaultInjection ReadFaultInjection() =>
            ParseFaultInjection(Environment.GetEnvironmentVariable(ForceMissingEnvVar));

        /// <summary>주입 탐침 — 실 미설치 PC 와 동일한 예외 형식으로 실패.</summary>
        public static string? ForcedMissingProbe() => throw new Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException();

        /// <summary>설치 버전 문자열 — 어떤 실패든 null(미설치 취급).</summary>
        public static string? ProbeVersion(Func<string?> probe)
        {
            try
            {
                var v = probe();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
            catch (Exception) { return null; }   // 미설치 · 로더 DLL 부재 · 미분류 = fail-safe(빈 화면 차단)
        }

        public static WebView2RuntimeState Classify(Func<string?> probe, Func<bool> isNetworkAvailable)
        {
            if (ProbeVersion(probe) is not null) return WebView2RuntimeState.Present;
            bool online;
            try { online = isNetworkAvailable(); } catch (Exception) { online = false; }
            return online ? WebView2RuntimeState.MissingOnline : WebView2RuntimeState.MissingOffline;
        }

        public static WebView2GuardOutcome Resolve(WebView2RuntimeState state, IWebView2GuardUi ui)
        {
            switch (state)
            {
                case WebView2RuntimeState.Present:
                    return WebView2GuardOutcome.ProceedBlazor;
                case WebView2RuntimeState.MissingOnline:
                    if (!ui.AskOpenBootstrapper(EvergreenBootstrapperUrl)) return WebView2GuardOutcome.BootstrapperDeclined;
                    try
                    {
                        ui.OpenUrl(EvergreenBootstrapperUrl);
                        return WebView2GuardOutcome.BootstrapperOpened;
                    }
                    catch (Exception)
                    {
                        try { ui.ShowOpenFailed(EvergreenBootstrapperUrl); } catch (Exception) { }
                        return WebView2GuardOutcome.BootstrapperOpenFailed;
                    }
                default:
                    try { ui.ShowOffline(EvergreenBootstrapperUrl); } catch (Exception) { }
                    return WebView2GuardOutcome.OfflineExit;
            }
        }

        /// <summary>호스트 진입점 결선 — true = Blazor 기동 진행 · false = 안내 완료 → 호출자는 exit 0 으로 종료.</summary>
        public static bool EnsureRuntime(Func<string?> probe, Func<bool> isNetworkAvailable, IWebView2GuardUi ui, out WebView2GuardOutcome outcome)
        {
            outcome = Resolve(Classify(probe, isNetworkAvailable), ui);
            return outcome == WebView2GuardOutcome.ProceedBlazor;
        }

        /// <summary>[v12.18.34] 무대화 UI(silent 주입 전용) — 대화상자 · 브라우저 실행 0, 안내 문구는 로그 싱크로만.</summary>
        internal sealed class SilentUi : IWebView2GuardUi
        {
            private readonly Action<string> _sink;
            public SilentUi(Action<string> sink) => _sink = sink;

            public bool AskOpenBootstrapper(string url) { _sink("[WebView2] 미설치(온라인) · silent → 부트스트래퍼 트리거 생략: " + url); return false; }
            public void OpenUrl(string url) => _sink("[WebView2] silent → 브라우저 실행 생략: " + url);
            public void ShowOffline(string url) => _sink("[WebView2] 미설치(오프라인) · silent → 안내 생략: " + url);
            public void ShowOpenFailed(string url) => _sink("[WebView2] 트리거 실패 · silent → 안내 생략: " + url);
        }

        /// <summary>실 호스트 UI — MessageBox + 셸 실행(기본 브라우저가 부트스트래퍼 다운로드).</summary>
        internal sealed class MessageBoxUi : IWebView2GuardUi
        {
            private const string Title = "MS2-E Stock Suite — WebView2 런타임 필요";
            private const string ClassicHint = "WebView2 없이 사용하려면 클래식 모드(--classic) 또는 헤드리스 모드(--headless)로 실행할 수 있습니다.";

            public bool AskOpenBootstrapper(string url) =>
                MessageBox.Show(
                    "화면 표시에 필요한 Microsoft Edge WebView2 런타임이 이 PC에 설치되어 있지 않습니다.\n\n" +
                    "[예]를 누르면 Microsoft 공식 Evergreen 부트스트래퍼(MicrosoftEdgeWebview2Setup.exe) 다운로드를 시작합니다.\n" +
                    "다운로드한 파일을 실행해 설치를 마친 뒤 프로그램을 다시 실행하세요.\n\n" + ClassicHint,
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes;

            public void OpenUrl(string url) =>
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            public void ShowOffline(string url) =>
                MessageBox.Show(
                    "화면 표시에 필요한 Microsoft Edge WebView2 런타임이 이 PC에 설치되어 있지 않으며, 네트워크 연결도 확인되지 않습니다.\n\n" +
                    "인터넷이 연결된 환경에서 다음 주소의 공식 설치 파일을 받아 설치한 뒤 다시 실행하세요.\n" + url + "\n\n" + ClassicHint,
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);

            public void ShowOpenFailed(string url) =>
                MessageBox.Show(
                    "다운로드 페이지를 자동으로 열지 못했습니다. 브라우저에서 다음 주소를 직접 열어 설치한 뒤 다시 실행하세요.\n\n" + url + "\n\n" + ClassicHint,
                    Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
