// ============================================================================
// PeerReadinessProbe — [단독 운용 · 방향 B] QuantumVault 피어 '정돈 완료' 비파괴 판정(이중 저장소 정식 봉인 집행 전 go/no-go)
//
//  배경: v12.17.1-GM-FULL(이중 저장소 상호 봉인)은 피어 작업 트리가 정돈될 때까지 보류됐다 — 2026-10-08 실측 시 QuantumVault HEAD bdb8bb2 위
//    미커밋 41건(GrpcHost · 테스트 · 봉인 스크립트) + 미추적 key_out.txt(키 원문 가능성). release-package.ps1 [0/10] 은 피어 미커밋 코드를
//    빌드 전에 거부하지만, '키 원문 파일'과 '매니페스트가 현재 HEAD 이후에 재봉인됐는가'는 보지 않는다.
//  판정(전부 읽기 전용 · 피어에 쓰기 0):
//   ① 미커밋 코드 0 — GitRepoProbe.IsCodePath 규칙(스크립트 [0/10] 과 동일 · release/ docs/ *.md 제외).
//   ② 키 원문 의심 파일 0 — 작업 트리 전체(추적 · 미추적 · gitignore 포함)의 '파일 이름'만 대조(key_out* · *.pem · *.key · *.pfx · *.p12 ·
//      *.snk · id_rsa*). ★내용은 열지 않는다(비밀값을 읽거나 로그로 흘리지 않음 — 경로만 보고).
//   ③ 피어 매니페스트(release/offline-bundle/RELEASE_MANIFEST.json · gitignore 산출물) 존재 · rootHash 존재.
//   ④ 매니페스트 sealedAtUtc ≥ 피어 HEAD 커밋 시각 — 현재 HEAD '이후'에 재봉인됐는가(그 전 봉인 = 낡음).
//  ★비파괴: git 은 '--no-optional-locks'로 호출(GitRepoProbe.Git 이 모든 호출에 부여) — 일반 git status 는 인덱스 stat 캐시를 기회적으로 갱신하며 .git/index 를 다시 쓴다
//    (읽기 전용 저장소에 쓰기). 같은 이유로 GitRepoProbe.Git · release-package.ps1 의 피어 status 도 이 옵션으로 교정했다(실결함).
//  CLI: --peer-readiness <피어 루트> [--peer-manifest <상대 경로>] → 0 = Ready(정식 모드 집행 가능) · 2 = NotReady(사유 목록) · 64 = 사용 오류.
//  콜드 경로(운용 점검) — 할당 무관.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace StockManager.Tools
{
    public enum PeerReadinessIssue : byte { NotARepository = 1, UncommittedCode = 2, KeyMaterialPresent = 3, ManifestMissing = 4, ManifestInvalid = 5, ManifestStale = 6 }

    public sealed record PeerReadinessReport(string? Head, IReadOnlyList<(PeerReadinessIssue Issue, string Detail)> Issues, int DirtyCodePaths, int KeyFiles)
    {
        public bool Ready => Head is not null && Issues.Count == 0;
    }

    public static class PeerReadinessProbe
    {
        public const string DefaultManifest = "release/offline-bundle/RELEASE_MANIFEST.json";

        /// <summary>키 원문 의심 파일 이름(경로 마지막 요소 · 대소문자 무시). 내용은 읽지 않는다.</summary>
        public static bool IsKeyMaterialName(string path)
        {
            string n = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
            if (n.Length == 0) return false;
            if (n.StartsWith("key_out", StringComparison.OrdinalIgnoreCase) || n.StartsWith("id_rsa", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("id_ed25519", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (var ext in new[] { ".pem", ".key", ".pfx", ".p12", ".snk" })
                if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool SamePath(string a, string b)
            => string.Equals(Path.GetFullPath(a.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar),
                             Path.GetFullPath(b.Replace('/', Path.DirectorySeparatorChar)).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

        public static PeerReadinessReport Probe(string peerRoot, string manifestRel = DefaultManifest)
        {
            var issues = new List<(PeerReadinessIssue, string)>();
            // ★저장소 최상위 = 지정 경로여야 한다 — 아니면 git 이 상위 디렉터리로 올라가 '감싸는 다른 저장소'(예: 사용자 홈)를 판정한다
            //   (실측: 비저장소 임시 폴더에서 상위 저장소 전체 status --ignored 7분 38초 + 엉뚱한 HEAD). 오타 · 이동된 피어 경로도 같은 함정.
            string? top = Directory.Exists(peerRoot) ? GitRepoProbe.Git(peerRoot, "rev-parse", "--show-toplevel") : null;
            string? head = top is not null && SamePath(top, peerRoot) ? GitRepoProbe.Git(peerRoot, "rev-parse", "HEAD") : null;
            if (head is null)
            {
                issues.Add((PeerReadinessIssue.NotARepository, "git 저장소 최상위 아님/판독 실패: " + peerRoot + (top is null ? "" : " (감싸는 저장소: " + top + ")")));
                return new PeerReadinessReport(null, issues, 0, 0);
            }
            // ① 미커밋 코드
            var dirty = GitRepoProbe.StatusPaths(peerRoot);
            if (dirty.Count > 0) issues.Add((PeerReadinessIssue.UncommittedCode, dirty.Count + "건(예: " + dirty[0] + ")"));
            // ② 키 원문 의심 파일(추적 · 미추적 · 무시 포함 — 이름만)
            int keys = 0;
            string? all = GitRepoProbe.Git(peerRoot, "status", "--porcelain", "--untracked-files=all", "--ignored");
            string? tracked = GitRepoProbe.Git(peerRoot, "ls-files");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var src in new[] { all, tracked })
            {
                if (src is null) continue;
                foreach (var line in src.Split('\n'))
                {
                    string raw = line.TrimEnd('\r');
                    string p = src == all ? (raw.Length > 3 ? raw[3..].Trim().Trim('"') : "") : raw.Trim().Trim('"');
                    int arrow = p.IndexOf(" -> ", StringComparison.Ordinal);
                    if (arrow >= 0) p = p[(arrow + 4)..];
                    if (p.Length == 0 || p.EndsWith("/", StringComparison.Ordinal) || !IsKeyMaterialName(p) || !seen.Add(p)) continue;
                    keys++;
                    if (keys <= 3) issues.Add((PeerReadinessIssue.KeyMaterialPresent, p + " (이름 기준 · 내용 미열람 — 저장소 밖으로 이관 필요)"));
                }
            }
            if (all is null) issues.Add((PeerReadinessIssue.KeyMaterialPresent, "작업 트리 열거 실패 — 키 파일 부재를 확인할 수 없음(fail-closed)"));
            // ③ ④ 매니페스트
            string mp = Path.Combine(peerRoot, manifestRel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(mp)) issues.Add((PeerReadinessIssue.ManifestMissing, manifestRel));
            else
            {
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(mp));
                    var r = d.RootElement;
                    bool hasRoot = r.TryGetProperty("rootHash", out var rh) && rh.ValueKind == JsonValueKind.String && rh.GetString()!.Length >= 64;
                    bool hasSealed = r.TryGetProperty("sealedAtUtc", out var sa) && sa.ValueKind == JsonValueKind.String
                                     && DateTimeOffset.TryParse(sa.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
                    if (!hasRoot || !hasSealed) issues.Add((PeerReadinessIssue.ManifestInvalid, "rootHash/sealedAtUtc 누락"));
                    else
                    {
                        var sealedAt = DateTimeOffset.Parse(sa.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                        string? ct = GitRepoProbe.Git(peerRoot, "log", "-1", "--format=%cI", "HEAD");
                        if (ct is null || !DateTimeOffset.TryParse(ct, CultureInfo.InvariantCulture, DateTimeStyles.None, out var headAt))
                            issues.Add((PeerReadinessIssue.ManifestStale, "HEAD 커밋 시각 판독 실패(fail-closed)"));
                        else if (sealedAt < headAt)
                            issues.Add((PeerReadinessIssue.ManifestStale, $"봉인 {sealedAt:u} < HEAD 커밋 {headAt.ToUniversalTime():u} — 현재 HEAD 기준 재봉인 필요"));
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    issues.Add((PeerReadinessIssue.ManifestInvalid, ex.GetType().Name));
                }
            }
            return new PeerReadinessReport(head.Trim(), issues, dirty.Count, keys);
        }
    }
}
