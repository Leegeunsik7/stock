// ============================================================================
// InstallerBuildService — v10.1.0 Phase 94-A: 원클릭 배포 설치관리자 빌드 서비스
//
//   사이드 메뉴 도구(DistributePage)가 호출. 개발 환경(build-installer.ps1 + .NET SDK) 을 감지하고, 감지되면
//   build-installer.ps1 을 자식 프로세스로 실행해 자체 포함 퍼블리시 → Inno Setup 컴파일을 수행하며 출력을
//   실시간 스트리밍한다. 엔드유저(배포본) 환경에서는 스크립트/소스가 없으므로 개발 전용임을 안내한다.
//
//   ★안전: 이 서비스는 '개발 PC 에서만' 동작하는 빌드 도우미다 — 배포된 앱에는 스크립트/SDK 가 없어 감지가 실패하고
//     빌드 버튼이 비활성화된다(엔드유저에게 무해). 실 거래/발주와 무관(liveArmed 계층 미접촉).
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed class InstallerBuildService
    {
        /// <summary>배포 빌드 전제 조건 감지 결과.</summary>
        public sealed record Prereqs(
            bool IsDevEnvironment,   // build-installer.ps1 + 소스가 존재하는 개발 PC 인가
            string? RepoRoot,        // 저장소 루트(스크립트 위치)
            bool DotnetSdk,          // .NET SDK(dotnet) 존재
            bool InnoSetup,          // Inno Setup(iscc.exe) 존재
            string? IsccPath,        // iscc.exe 경로(있으면)
            string? DistDir);        // 산출물 폴더(dist)

        /// <summary>개발 환경·전제 조건 감지(빠른 파일/경로 검사).</summary>
        public Prereqs Detect()
        {
            string? repoRoot = FindRepoRoot();
            bool devEnv = repoRoot is not null;
            bool sdk = DetectDotnetSdk();
            string? iscc = DetectIscc();
            string? dist = repoRoot is null ? null : Path.Combine(repoRoot, "dist");
            return new Prereqs(devEnv, repoRoot, sdk, iscc is not null, iscc, dist);
        }

        /// <summary>build-installer.ps1 실행(출력 실시간 콜백). 반환=프로세스 종료코드(0=성공).</summary>
        public async Task<int> RunBuildAsync(string repoRoot, string version, Action<string> onLine, CancellationToken ct)
        {
            string script = Path.Combine(repoRoot, "build-installer.ps1");
            if (!File.Exists(script)) { onLine($"[오류] 스크립트 없음: {script}"); return -1; }

            string shell = ResolvePowerShell();
            // ★Korean UTF-8: 자식 콘솔 출력 인코딩을 UTF-8 로 강제(powershell.exe 기본 CP949 깨짐 방지) 후 스크립트 실행.
            string safeVersion = SanitizeVersion(version);
            string command = $"[Console]::OutputEncoding=[Text.Encoding]::UTF8; & '{script.Replace("'", "''")}' -Version '{safeVersion}'";

            var psi = new ProcessStartInfo
            {
                FileName = shell,
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(command);

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLine(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLine("⚠ " + e.Data); };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
                return proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { /* 종료 경합 무해 */ }
                onLine("[취소] 빌드가 취소되었습니다.");
                return -2;
            }
            catch (Exception ex)
            {
                onLine($"[오류] 실행 실패: {ex.Message}");
                return -3;
            }
        }

        /// <summary>탐색기로 폴더 열기(빌드 산출물 확인용). 실패 무해.</summary>
        public void OpenFolder(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch { /* 탐색기 실행 실패 무해 */ }
        }

        // ── 감지 헬퍼 ──
        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "build-installer.ps1")))
                    return dir.FullName;
            }
            return null;
        }

        private static bool DetectDotnetSdk()
        {
            // PATH 상의 dotnet.exe 존재만 가볍게 확인(SDK vs 런타임은 스크립트가 publish 시 확정).
            string? paths = Environment.GetEnvironmentVariable("PATH");
            if (paths is not null)
                foreach (var p in paths.Split(Path.PathSeparator))
                {
                    try { if (!string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p.Trim(), "dotnet.exe"))) return true; }
                    catch { /* 잘못된 PATH 항목 무시 */ }
                }
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return File.Exists(Path.Combine(pf, "dotnet", "dotnet.exe"));
        }

        private static string? DetectIscc()
        {
            string pf    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var c in new[]
            {
                Path.Combine(local, "Programs", "Inno Setup 6", "ISCC.exe"),   // winget 사용자 스코프 설치(기본)
                Path.Combine(pf86, "Inno Setup 6", "ISCC.exe"),
                Path.Combine(pf,   "Inno Setup 6", "ISCC.exe"),
                Path.Combine(pf86, "Inno Setup 5", "ISCC.exe"),
                Path.Combine(pf,   "Inno Setup 5", "ISCC.exe"),
            })
            {
                try { if (File.Exists(c)) return c; } catch { /* 무시 */ }
            }
            return null;
        }

        private static string ResolvePowerShell()
        {
            // pwsh(PowerShell 7·UTF-8 친화) 우선, 없으면 Windows PowerShell.
            string? paths = Environment.GetEnvironmentVariable("PATH");
            if (paths is not null)
                foreach (var p in paths.Split(Path.PathSeparator))
                {
                    try { if (!string.IsNullOrWhiteSpace(p) && File.Exists(Path.Combine(p.Trim(), "pwsh.exe"))) return "pwsh.exe"; }
                    catch { /* 무시 */ }
                }
            return "powershell.exe";
        }

        // 버전 문자열 새니타이즈(명령 주입 방지 — 숫자/점/하이픈만 허용).
        private static string SanitizeVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return "10.1.0";
            var sb = new StringBuilder(v.Length);
            foreach (char c in v)
                if (char.IsAsciiDigit(c) || c == '.' || c == '-') sb.Append(c);
            string s = sb.ToString();
            return s.Length == 0 ? "10.1.0" : s;
        }
    }
}
