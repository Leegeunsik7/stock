// ============================================================================
// ReleaseVerificationCli — [봉인 운영 · 추천 A] 릴리스 패키지/설치본 봉인 수검 CLI
//
//  사용(호스트 실행 파일 · UI/DB/로그 초기화 이전에 분기 — 부작용 0):
//    MS2E.StockSuite.exe --verify-seal <설치 폴더 | 스테이징 폴더 | release-*.zip>
//                        [--expected <64hex masterSeal>] [--require-anchor] [--out <보고서 파일>]
//  종료 코드: 0 = 봉인 일치(앵커 지정 시 앵커 일치까지) · 1 = 사용법/입력 오류 · 2 = 변조/누락/위조/파싱 실패
//            3 = 외부 앵커 미주입(--require-anchor) · 4 = 매니페스트에 없는 바이너리 동봉(DLL 하이재킹 표면)
//
//  판정 권위는 런타임과 동일한 BootIntegrityGate(단일 코드 경로 — 패키징 수검 = 부팅 수검). 여기서만 추가하는 것:
//   · zip 입력: 임시 폴더로 추출(.NET ZipFile 은 경로 이탈 항목을 거부) 후 같은 경로로 수검 · 종료 시 삭제.
//   · 미등재 바이너리 검사: 역할 폴더(host/·proxy/, 평면 레이아웃이면 루트)의 *.dll/*.exe 중 매니페스트에 없는 파일 = 실패.
//     (런타임 게이트는 설치기 부산물 오탐을 피하려 이 검사를 하지 않는다 — 레이아웃이 확정된 패키징 단계에서 강제.)
//  해시는 PinnedSha256Streamer/AssemblyAntiTamperHandshake 의 POH 청크 루프(파일 크기와 무관한 상수 할당).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using MS2E.Infrastructure;

namespace StockManager.Tools
{
    public static class ReleaseVerificationCli
    {
        public const string Flag = "--verify-seal";
        public const int ExitOk = 0, ExitUsage = 1, ExitTampered = 2, ExitUnanchored = 3, ExitUnlisted = 4;

        public static bool IsInvocation(string[]? args) => args is { Length: > 0 } && string.Equals(args[0], Flag, StringComparison.OrdinalIgnoreCase);

        /// <summary>CLI 진입(호스트 Main 최상단에서 호출). 보고서는 output 과(지정 시) --out 파일에 기록.</summary>
        public static int Run(string[] args, TextWriter output)
        {
            var report = new StringBuilder(1024);
            int code;
            string? outFile = null;
            try
            {
                if (!TryParseArgs(args, out string target, out string expected, out bool requireAnchor, out outFile, out string usageError))
                {
                    report.AppendLine("[verify-seal] 사용법 오류: " + usageError);
                    report.AppendLine("  " + Flag + " <폴더|zip> [--expected <64hex>] [--require-anchor] [--out <파일>]");
                    code = ExitUsage;
                }
                else code = Verify(target, expected, requireAnchor, report);
            }
            catch (Exception ex)
            {
                report.AppendLine("[verify-seal] 수검 예외(fail-closed): " + ex.GetType().Name + ": " + ex.Message);
                code = ExitTampered;
            }
            report.AppendLine("[verify-seal] exit=" + code);
            string text = report.ToString();
            try { output.Write(text); output.Flush(); } catch (IOException) { /* 콘솔 미연결(WinExe) — 파일/종료코드로 전달 */ }
            if (outFile is not null)
            {
                try { File.WriteAllText(outFile, text, new UTF8Encoding(true)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (code == ExitOk) code = ExitUsage; }
            }
            return code;
        }

        /// <summary>폴더 또는 zip 수검. report 에 사람이 읽는 판정 기록.</summary>
        public static int Verify(string target, string? expected, bool requireAnchor, StringBuilder report)
        {
            if (File.Exists(target) && target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string tmp = Path.Combine(Path.GetTempPath(), "ms2e_verify_" + Guid.NewGuid().ToString("N"));
                try
                {
                    ZipFile.ExtractToDirectory(target, tmp);
                    report.AppendLine("[verify-seal] zip 추출: " + Path.GetFileName(target));
                    return VerifyFolder(tmp, expected, requireAnchor, report);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    report.AppendLine("[verify-seal] zip 손상/경로 이탈 항목: " + ex.Message);
                    return ExitTampered;
                }
                finally { try { Directory.Delete(tmp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
            }
            if (Directory.Exists(target)) return VerifyFolder(target, expected, requireAnchor, report);
            report.AppendLine("[verify-seal] 대상 없음(폴더 또는 .zip): " + target);
            return ExitUsage;
        }

        private static int VerifyFolder(string root, string? expected, bool requireAnchor, StringBuilder report)
        {
            var sw = Stopwatch.StartNew();
            string? manifest = BootIntegrityGate.Locate(root);
            if (manifest is null)
            {
                report.AppendLine("[verify-seal] manifest.sig 없음 — 비봉인 패키지");
                return ExitTampered;
            }
            bool anchorGiven = !string.IsNullOrWhiteSpace(expected);
            using var gate = BootIntegrityGate.Start(Path.GetDirectoryName(manifest)!, expected ?? "", runAsync: false,
                                                     requireExternalAnchor: requireAnchor || anchorGiven,
                                                     monitorInterval: TimeSpan.FromDays(1));
            var unlisted = FindUnlistedBinaries(manifest);
            sw.Stop();

            report.AppendLine($"[verify-seal] status={gate.Status} artifacts={gate.ArtifactCount} anchor={(gate.AnchorVerified ? "일치" : anchorGiven ? "불일치/형식오류" : "미주입")} elapsed={sw.ElapsedMilliseconds}ms");
            report.AppendLine("[verify-seal] detail: " + gate.Detail);
            foreach (var e in gate.TamperEvidence)
                report.AppendLine($"[verify-seal] 증거: {e.Status} {e.Artifact} 기대={Short(e.ExpectedSha256)} 실측={Short(e.ActualSha256)} — {e.Detail}");
            foreach (var u in unlisted)
                report.AppendLine("[verify-seal] 미등재 바이너리: " + u);

            if (gate.Status == BootIntegrityStatus.Unanchored) return ExitUnanchored;
            if (gate.ArmingBlocked) return ExitTampered;
            return unlisted.Count > 0 ? ExitUnlisted : ExitOk;
        }

        /// <summary>역할 폴더의 *.dll/*.exe 중 매니페스트 미등재 파일(역할 한정 이름). 평면 레이아웃이면 루트를 양 역할로 본다.</summary>
        internal static List<string> FindUnlistedBinaries(string manifestPath)
        {
            var result = new List<string>();
            if (!BootIntegrityGate.TryParse(File.ReadAllText(manifestPath), out var entries, out _, out _)) return result;
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var listedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, _) in entries)
            {
                listed.Add(name);
                int slash = name.IndexOf('/');
                if (slash > 0) listedFiles.Add(name[(slash + 1)..]);
            }
            string root = Path.GetDirectoryName(manifestPath)!;
            bool layered = false;
            foreach (string role in new[] { "host", "proxy" })
            {
                string dir = Path.Combine(root, role);
                if (!Directory.Exists(dir)) continue;
                layered = true;
                foreach (string f in Binaries(dir))
                {
                    string n = role + "/" + Path.GetFileName(f);
                    if (!listed.Contains(n)) result.Add(n);
                }
            }
            if (!layered)
                foreach (string f in Binaries(root))
                    if (!listedFiles.Contains(Path.GetFileName(f))) result.Add(Path.GetFileName(f));
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static IEnumerable<string> Binaries(string dir)
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.dll")) yield return f;
            foreach (string f in Directory.EnumerateFiles(dir, "*.exe")) yield return f;
        }

        private static string Short(string hex) => string.IsNullOrEmpty(hex) ? "-" : hex.Length > 16 ? hex[..16] + "…" : hex;

        private static bool TryParseArgs(string[] args, out string target, out string expected, out bool requireAnchor, out string? outFile, out string error)
        {
            target = ""; expected = ""; requireAnchor = false; outFile = null; error = "";
            if (!IsInvocation(args) || args.Length < 2 || args[1].StartsWith("--", StringComparison.Ordinal)) { error = "대상 경로 누락"; return false; }
            target = args[1];
            for (int i = 2; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--expected":
                        if (i + 1 >= args.Length) { error = "--expected 값 누락"; return false; }
                        expected = args[++i];
                        if (!BootIntegrityGate.IsStrictSealHex(expected)) { error = "--expected 는 64 hex(masterSeal)"; return false; }
                        break;
                    case "--require-anchor": requireAnchor = true; break;
                    case "--out":
                        if (i + 1 >= args.Length) { error = "--out 값 누락"; return false; }
                        outFile = args[++i];
                        break;
                    default: error = "알 수 없는 인자: " + args[i]; return false;
                }
            }
            return true;
        }
    }
}
