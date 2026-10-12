// ============================================================================
// UiSwitchVerifierCli — [자율 UI 검증 · 추천 A] 헤드리스 UI 스위치 퍼징 수검 CLI
//
//  사용(호스트 실행 파일 · UI/DB/로그/뮤텍스 초기화 이전에 분기 — 운영 상태 무접촉):
//    MS2E.StockSuite.exe --verify-ui-switches [--iterations <N ≥ 1, 기본 10000>] [--seed <정수, 기본 20261012>]
//                        [--storm-ms <스톰 길이, 기본 1000 · 0 = 생략>] [--out <보고서 파일>]
//  종료 코드: 0 = 퍼징 + 스톰 전부 PASS · 1 = 사용법 오류 · 2 = 불변식 위반(불법 무장 · 인터락 오염 · UI 예외 · 상태 꼬임 · 스톰 격리 실패)
//
//  ★운영 격리: 운영 DB(LocalDatabase.DefaultPath)를 열지 않는다 — %TEMP% 의 일회용 SQLite 를 만들어 실 설정 어댑터
//    (AdminSettingWidgetAdapter → UiSettingWriteGuard)를 결속하고 종료 시 삭제. 브로커 · 소켓(LAN 재기동은 하네스가 생략) · 키움 프록시
//    · 실 발주 경로 참조 0. 무장 판정 설정 precedence 는 호스트와 동일(환경변수 → DB) — 이 머신 환경변수에 이중 키가 박혀 있으면
//    기준선부터 위반으로 보고된다(정직한 fail-closed 신호).
//  ★포렌식: 프로브 집계어의 모든 비트 전이는 InterlockForensicRecorder 해시 체인에 각인 — 보고서에 raised/cleared/faults 를 사출.
// ============================================================================
using System;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Infrastructure;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Ports.Shell;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Simulation;

namespace StockManager.Tools
{
    /// <summary>
    /// 퍼징 백엔드 조립(일회용 DB + 실 설정 어댑터 + 무장 프로브 + 실 대시보드 허브). 클릭 표면은 호출자가 선택
    /// (CLI = CreateHeadlessSurface · 테스트 = bUnit). Dispose 시 허브 정지 + 임시 DB 삭제.
    /// </summary>
    public sealed class UiFuzzRig : IDisposable
    {
        private readonly string _dbPath;

        public LocalDatabase Db { get; }
        public HarnessSettingsPort Settings { get; }
        public HarnessShellPort Shell { get; } = new();
        public UiArmingInvariantProbe Probe { get; }
        public DashboardSnapshotHub Hub { get; }
        public StateAwareRenderGovernor RenderGovernor { get; } = new();

        public UiFuzzRig()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), "ms2e_uifuzz_" + Guid.NewGuid().ToString("N") + ".db");
            Db = new LocalDatabase(_dbPath);
            Settings = new HarnessSettingsPort(new AdminSettingWidgetAdapter(Db));
            var db = Db;
            Probe = new UiArmingInvariantProbe(key => Environment.GetEnvironmentVariable(key) ?? db.GetSetting(key));
            Hub = new DashboardSnapshotHub(Probe.Mask);
            Probe.Recorder.Chain(Hub);                     // 집계어 전이 → 포렌식(먼저) → 대시보드 LED
            Hub.Start();
        }

        /// <summary>UI 포트 계약 그대로의 DI(RCL 컴포넌트가 @inject 로 받는 것만).</summary>
        public void Register(IServiceCollection services)
        {
            services.AddSingleton<IAdminSettingWidgetPort>(Settings);
            services.AddSingleton<IMainShellNavigationPort>(Shell);
            services.AddSingleton<IPaperDashboardPort>(Hub);
            services.AddSingleton(RenderGovernor);
        }

        /// <summary>CLI 용 헤드리스 표면: 설정 · 내비 메뉴 · 대시보드(변동성 루트) 3 루트 렌더.</summary>
        public HeadlessUiSurface CreateHeadlessSurface()
        {
            var nav = new HeadlessNavigationManager();
            var services = new ServiceCollection();
            Register(services);
            services.AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(nav);
            var surface = new HeadlessUiSurface(services.BuildServiceProvider(), nav);
            surface.Mount<MS2E.UI.Portable.Pages.Settings.SettingsPage>();
            surface.Mount<MS2E.UI.Portable.Components.NavMenu>();
            surface.Mount<MS2E.UI.Portable.Pages.Trading.PaperTradingDashboardView>(volatileRoot: true);
            return surface;
        }

        public void Dispose()
        {
            Hub.Dispose();
            Db.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (var p in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
            {
                try { if (File.Exists(p)) File.Delete(p); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static class UiSwitchVerifierCli
    {
        public const string Flag = "--verify-ui-switches";
        public const int ExitOk = 0, ExitUsage = 1, ExitViolation = 2;
        public const int DefaultIterations = 10_000, DefaultSeed = 20261012, DefaultStormMs = 1000;

        public static bool IsInvocation(string[]? args) => args is { Length: > 0 } && string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase);

        public static int Run(string[] args, TextWriter output)
        {
            var report = new StringBuilder(2048);
            int code;
            string? outFile = null;
            try
            {
                if (!TryParseArgs(args, out int iterations, out int seed, out int stormMs, out outFile, out string usageError))
                {
                    report.AppendLine("[verify-ui-switches] 사용법 오류: " + usageError);
                    report.AppendLine("  " + Flag + " [--iterations <N>] [--seed <S>] [--storm-ms <ms>] [--out <파일>]");
                    code = ExitUsage;
                }
                else code = Verify(iterations, seed, stormMs, report);
            }
            catch (Exception ex)
            {
                report.AppendLine("[verify-ui-switches] 수검 예외(fail-closed): " + ex.GetType().Name + ": " + ex.Message);
                code = ExitViolation;
            }
            report.AppendLine("[verify-ui-switches] exit=" + code);
            string text = report.ToString();
            try { output.Write(text); output.Flush(); } catch (IOException) { /* 콘솔 미연결(WinExe) — 파일/종료코드로 전달 */ }
            if (outFile is not null)
            {
                try { File.WriteAllText(outFile, text, new UTF8Encoding(true)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (code == ExitOk) code = ExitUsage; }
            }
            return code;
        }

        /// <summary>퍼징(iterations) → 스톰(stormMs) 수검. report 에 판정 기록.</summary>
        public static int Verify(int iterations, int seed, int stormMs, StringBuilder report)
        {
            using var rig = new UiFuzzRig();
            using var surface = rig.CreateHeadlessSurface();
            report.AppendLine("[verify-ui-switches] 일회용 DB · 실 설정 어댑터(UiSettingWriteGuard) · 실 안전 코어 · 헤드리스 렌더러(설정/내비/대시보드)");

            var guard = SwitchStormIsolationGuard.PerMilliseconds(200, 100);   // 퍼징 페이싱 2,000/s
            var fuzz = AutonomousUiSwitchFuzzer.Run(surface, rig.Probe, guard, iterations, seed, rig.Settings.SetSetting, hostileEvery: 50, readBack: rig.Db.GetSetting);
            fuzz.AppendTo(report);
            if (surface.LastFault is { } lf) report.AppendLine("[ui-fuzz] 최초 UI 예외: " + lf);
            report.AppendLine("[ui-fuzz] lanRestarts(생략)=" + rig.Settings.LanRestarts + " navigations=" + surface.Navigation.Navigations + " palette=" + rig.Shell.PaletteRequests);
            bool ok = fuzz.Passed;

            if (stormMs > 0)
            {
                var stormGuard = SwitchStormIsolationGuard.PerMilliseconds(100, 100);   // 스톰 상한 1,000/s
                using var core = new CoreTickIsolationMonitor(rig.Hub, rig.Probe.Mask);
                var storm = AutonomousUiSwitchFuzzer.RunStorm(surface, rig.Probe, stormGuard, core, stormMs, seed + 1);
                storm.AppendTo(report);
                ok &= storm.Passed;
            }
            report.AppendLine("[verify-ui-switches] liveArmed=" + rig.Probe.LiveArmed + " ignition=" + rig.Probe.IgnitionStage + " canaryArmed=" + rig.Probe.Canary.IsArmed);
            return ok ? ExitOk : ExitViolation;
        }

        private static bool TryParseArgs(string[] args, out int iterations, out int seed, out int stormMs, out string? outFile, out string error)
        {
            iterations = DefaultIterations; seed = DefaultSeed; stormMs = DefaultStormMs; outFile = null; error = "";
            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                string? v = i + 1 < args.Length ? args[i + 1] : null;
                if (v is null) { error = a + " 값 누락"; return false; }
                switch (a.ToLowerInvariant())
                {
                    case "--iterations": if (!int.TryParse(v, out iterations) || iterations < 1) { error = "--iterations 는 1 이상 정수"; return false; } break;
                    case "--seed": if (!int.TryParse(v, out seed)) { error = "--seed 는 정수"; return false; } break;
                    case "--storm-ms": if (!int.TryParse(v, out stormMs) || stormMs < 0 || stormMs > 60_000) { error = "--storm-ms 는 0..60000"; return false; } break;
                    case "--out": outFile = v; break;
                    default: error = "알 수 없는 인수 " + a; return false;
                }
                i++;
            }
            return true;
        }
    }
}
