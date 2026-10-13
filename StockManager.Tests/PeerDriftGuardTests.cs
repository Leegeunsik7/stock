// ============================================================================
// PeerDriftGuardTests — [v12.18.4-GM · 방향 B] 피어 철회 드리프트 방어(브랜치 이탈 · HEAD 1커밋 전진 · 봉인 매니페스트 변경 · 미커밋 코드)
//
//  §1 판정: 임시 git 저장소(실 git)로 고정 그대로 = Ok · 같은 커밋 다른 브랜치 전환 = BranchSwitched(HEAD 해시 동일 — 기존 고정이 못 잡던 경우) ·
//     분리 HEAD = BranchDetached · 1커밋 전진 = HeadDrift · 재봉인/삭제 = ManifestDrift · 미커밋 코드 = UncommittedCode(문서 · release/ 는 무관) ·
//     감싸는 저장소 하위 폴더 = NotARepository. CLI 종료 코드 0/2/64(고정값 형식 이탈 · 브랜치 이름 주입 = 사용 오류).
//  §2 비파괴: 판정 전후 피어 .git/index · HEAD · refs 바이트 불변(--no-optional-locks).
//  §3 스크립트 결선: [0/10] 브랜치 고정(분리 HEAD = 중단) · Assert-PeerPinned 3지점(zip 직전 · 동기화 전 · 완료 직전) 순서 · 실패 = 스테이징 + 산출물 폐기.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class PeerDriftGuardTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ms2e_drift_" + Guid.NewGuid().ToString("N"));
        public PeerDriftGuardTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_root); }

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);   // .git 객체 읽기 전용
                Directory.Delete(_root, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static void Git(string dir, params string[] args)
            => Assert.NotNull(GitRepoProbe.Git(dir, new[] { "-c", "user.name=probe", "-c", "user.email=probe@local", "-c", "commit.gpgsign=false" }.Concat(args).ToArray()));

        private static string ManifestPath(string repo) => Path.Combine(repo, "release", "offline-bundle", "RELEASE_MANIFEST.json");

        /// <summary>브랜치 'master' · 커밋 1개 · gitignore 봉인 매니페스트 = 고정 대상 피어. 반환 = (경로, 브랜치, HEAD, 매니페스트 SHA-256).</summary>
        private (string Repo, string Branch, string Head, string Sha) PinnedPeer(string name)
        {
            string repo = Path.Combine(_root, name);
            Directory.CreateDirectory(repo);
            Assert.NotNull(GitRepoProbe.Git(repo, "init", "-q", "-b", "master"));
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "release/offline-bundle/\n");
            File.WriteAllText(Path.Combine(repo, "Core.cs"), "class Core {}\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "init");
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath(repo))!);
            File.WriteAllText(ManifestPath(repo), "{\"rootHash\":\"" + new string('a', 64) + "\",\"sealedAtUtc\":\"2026-10-13T00:00:00Z\"}");
            var (b, h, s) = PeerDriftGuard.Observe(repo);
            Assert.Equal("master", b);
            Assert.NotNull(h);
            Assert.NotNull(s);
            return (repo, b!, h!, s!);
        }

        private static int Cli(params string[] args) => ReleaseHandshakeCli.Run(args, TextWriter.Null);

        // ════════════ §1 판정 ════════════

        [Fact]
        public void Pinned_Unchanged_Ok_Cli0_DocsAndReleaseNotesIgnored()
        {
            var (repo, b, h, s) = PinnedPeer("ok");
            File.WriteAllText(Path.Combine(repo, "NOTES.md"), "문서");                                   // *.md = 코드 아님
            var r = PeerDriftGuard.Check(repo, b, h, s);
            _out.WriteLine($"[드리프트] {r.Verdict} · {r.Detail}");
            Assert.True(r.Ok, r.Detail);
            Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { PeerDriftGuard.DriftFlag, repo }));
            Assert.Equal(ReleaseHandshakeCli.ExitOk, Cli(PeerDriftGuard.DriftFlag, repo, "--pin-peer-branch", b, "--pin-peer-head", h.ToUpperInvariant(), "--pin-peer-manifest", s.ToLowerInvariant()));
        }

        [Fact]
        public void BranchSwitch_SameCommit_Rejected_ExistingHeadPinWouldPass()
        {
            var (repo, b, h, s) = PinnedPeer("switch");
            Git(repo, "checkout", "-q", "-b", "hotfix");                                             // 같은 커밋 · 다른 브랜치
            var (nb, nh, _) = PeerDriftGuard.Observe(repo);
            Assert.Equal(h, nh);                                                                     // ★HEAD 고정만으로는 통과하던 경우
            Assert.Equal("hotfix", nb);
            var r = PeerDriftGuard.Check(repo, b, h, s);
            _out.WriteLine($"[드리프트] 브랜치 전환 → {r.Verdict} · {r.Detail}");
            Assert.Equal(PeerDriftVerdict.BranchSwitched, r.Verdict);
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, Cli(PeerDriftGuard.DriftFlag, repo, "--pin-peer-branch", b, "--pin-peer-head", h, "--pin-peer-manifest", s));
        }

        [Fact]
        public void DetachedHead_Rejected()
        {
            var (repo, b, h, s) = PinnedPeer("detached");
            Git(repo, "checkout", "-q", "--detach");
            Assert.Null(PeerDriftGuard.Observe(repo).Branch);
            Assert.Equal(PeerDriftVerdict.BranchDetached, PeerDriftGuard.Check(repo, b, h, s).Verdict);
        }

        [Fact]
        public void OneCommitAdvance_Rejected_EvenDocsOnlyCommit()
        {
            var (repo, b, h, s) = PinnedPeer("advance");
            File.WriteAllText(Path.Combine(repo, "CHANGELOG.md"), "1커밋 전진(문서만)");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "docs");
            var r = PeerDriftGuard.Check(repo, b, h, s);
            _out.WriteLine($"[드리프트] 1커밋 전진 → {r.Verdict} · {r.Detail}");
            Assert.Equal(PeerDriftVerdict.HeadDrift, r.Verdict);
        }

        [Fact]
        public void ManifestResealedOrDeleted_Rejected()
        {
            var (repo, b, h, s) = PinnedPeer("reseal");
            byte[] orig = File.ReadAllBytes(ManifestPath(repo));
            var b2 = (byte[])orig.Clone();
            b2[^3] ^= 0x01;                                                                          // 1비트
            File.WriteAllBytes(ManifestPath(repo), b2);
            Assert.Equal(PeerDriftVerdict.ManifestDrift, PeerDriftGuard.Check(repo, b, h, s).Verdict);
            File.Delete(ManifestPath(repo));
            Assert.Equal(PeerDriftVerdict.ManifestDrift, PeerDriftGuard.Check(repo, b, h, s).Verdict);
            File.WriteAllBytes(ManifestPath(repo), orig);                                            // 원복 = 다시 Ok
            Assert.True(PeerDriftGuard.Check(repo, b, h, s).Ok);
        }

        [Fact]
        public void UncommittedCode_Rejected()
        {
            var (repo, b, h, s) = PinnedPeer("dirty");
            File.AppendAllText(Path.Combine(repo, "Core.cs"), "// edit\n");
            var r = PeerDriftGuard.Check(repo, b, h, s);
            Assert.Equal(PeerDriftVerdict.UncommittedCode, r.Verdict);
            Assert.Contains("Core.cs", r.Detail);
        }

        [Fact]
        public void SubfolderOfRepo_Or_Missing_NotARepository()
        {
            var (repo, b, h, s) = PinnedPeer("sub");
            string sub = Path.Combine(repo, "release");
            Assert.Equal(PeerDriftVerdict.NotARepository, PeerDriftGuard.Check(sub, b, h, s).Verdict);       // 감싸는 저장소 오판 차단
            Assert.Equal(PeerDriftVerdict.NotARepository, PeerDriftGuard.Check(Path.Combine(_root, "none"), b, h, s).Verdict);
        }

        [Fact]
        public void Cli_PinFormat_Invalid_Usage64()
        {
            var (repo, b, h, s) = PinnedPeer("usage");
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Cli(PeerDriftGuard.DriftFlag, repo, "--pin-peer-branch", b, "--pin-peer-head", h[..39], "--pin-peer-manifest", s));
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Cli(PeerDriftGuard.DriftFlag, repo, "--pin-peer-branch", b, "--pin-peer-head", h, "--pin-peer-manifest", s[..63] + "G"));
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Cli(PeerDriftGuard.DriftFlag, repo, "--pin-peer-head", h, "--pin-peer-manifest", s));
            foreach (var bad in new[] { "-c", "a..b", "a b", "a;rm", "x.lock", "a/" }) Assert.False(PeerDriftGuard.IsSafeBranch(bad), bad);
            foreach (var good in new[] { "master", "release/v2.8", "feat_x-1" }) Assert.True(PeerDriftGuard.IsSafeBranch(good), good);
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, Cli(PeerDriftGuard.DriftFlag, repo, "--pin-peer-branch", "a..b", "--pin-peer-head", h, "--pin-peer-manifest", s));
        }

        // ════════════ §2 비파괴 ════════════

        [Fact]
        public void Check_DoesNotWritePeerGitState()
        {
            var (repo, b, h, s) = PinnedPeer("nondestructive");
            string core = Path.Combine(repo, "Core.cs");
            File.SetLastWriteTimeUtc(core, DateTime.UtcNow.AddMinutes(-3));                         // 인덱스 stat 갱신 유인
            string git = Path.Combine(repo, ".git");
            byte[] Snap(string f) => File.ReadAllBytes(Path.Combine(git, f));
            byte[] idx = Snap("index"), head = Snap("HEAD"), refb = Snap(Path.Combine("refs", "heads", "master"));
            for (int i = 0; i < 3; i++) Assert.True(PeerDriftGuard.Check(repo, b, h, s).Ok);
            Assert.Equal(idx, Snap("index"));
            Assert.Equal(head, Snap("HEAD"));
            Assert.Equal(refb, Snap(Path.Combine("refs", "heads", "master")));
        }

        // ════════════ §3 스크립트 결선 ════════════

        [Fact]
        public void PackageScript_PinsBranch_ChecksDriftBeforeZip_BeforeSync_AndAtEnd_PurgesStage()
        {
            string dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "release-package.ps1"))) dir = Path.GetDirectoryName(dir)!;
            Assert.NotNull(dir);
            string s = File.ReadAllText(Path.Combine(dir!, "release-package.ps1"));
            Assert.Contains("symbolic-ref --short -q HEAD", s);
            Assert.Contains("'--pin-peer-branch', $peerPinBranch, '--pin-peer-head', $peerPinHead, '--pin-peer-manifest', $peerPinSha", s);
            var fn = Regex.Match(s, @"function Assert-PeerPinned\(\[string\]\$when\) \{(?<b>[\s\S]*?)\n\}").Groups["b"].Value;
            Assert.Contains("if ($NoPeer) { return }", fn);
            Assert.Contains("Remove-Item -LiteralPath $stage -Recurse -Force", fn);
            Assert.Contains("Remove-ReleaseOutputs; Fail", fn);
            int pin = s.IndexOf("$peerPinBranch = \"$peerPinBranch\".Trim()", StringComparison.Ordinal);
            int preZip = s.IndexOf("Assert-PeerPinned \"pre-zip\"", StringComparison.Ordinal);
            int zip = s.IndexOf("Compress-Archive", StringComparison.Ordinal);
            int preSync = s.IndexOf("Assert-PeerPinned \"pre-sync\"", StringComparison.Ordinal);
            int sync = s.IndexOf("'--sync-release-manifest'", StringComparison.Ordinal);
            int tight = s.IndexOf("\"verify-tighten\"", StringComparison.Ordinal);
            int final = s.IndexOf("Assert-PeerPinned \"final\"", StringComparison.Ordinal);
            int done = s.IndexOf("== 완료:", StringComparison.Ordinal);
            _out.WriteLine($"[결선] 고정 {pin} < zip 직전 {preZip} < zip {zip} < 동기화 전 {preSync} < 동기화 {sync} < 행렬 {tight} < 최종 {final} < 완료 {done}");
            Assert.True(pin > 0 && pin < preZip && preZip < zip && zip < preSync && preSync < sync && sync < tight && tight < final && final < done, "드리프트 판정 순서");
        }
    }
}
