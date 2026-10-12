// ============================================================================
// PeerReadinessProbeTests — [단독 운용 · 방향 B] 피어 정돈 완료 비파괴 판정 · 피어 오염 격리
//
//  §1 판정: 임시 git 저장소로 Ready(깨끗 · 키 없음 · HEAD 이후 재봉인) · 미커밋 코드(문서/산출물 제외) · 키 원문 의심 파일(미추적 · gitignore 안 ·
//     내용 미열람) · 매니페스트 누락/무효/낡음 · 저장소 아님 — CLI 종료 코드 0/2/64.
//  §2 ★비파괴: 추적 파일 mtime 만 바꿔 'git status 가 인덱스를 다시 쓰고 싶은' 상태를 만든 뒤 판정 → .git/index 바이트 불변
//     (대조군: 일반 git status 는 인덱스를 다시 쓴다 — 메커니즘 실증). 스크립트 피어 status · GitRepoProbe.Git 에 --no-optional-locks.
//  §3 피어 오염 격리: StockManager 빌드 정의(sln · csproj · props · targets)에 QuantumVault 경로 참조 0 · 출하 매니페스트 아티팩트에 피어 파일 0.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StockManager.Tools;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class PeerReadinessProbeTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ms2e_peer_" + Guid.NewGuid().ToString("N"));
        public PeerReadinessProbeTests(ITestOutputHelper output) { _out = output; Directory.CreateDirectory(_root); }

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

        private static void Manifest(string repo, DateTimeOffset sealedAt, string? rootHash = null)
        {
            string dir = Path.Combine(repo, "release", "offline-bundle");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "RELEASE_MANIFEST.json"),
                "{\"rootHash\":\"" + (rootHash ?? new string('a', 64)) + "\",\"sealedAtUtc\":\"" + sealedAt.UtcDateTime.ToString("O") + "\"}");
        }

        /// <summary>커밋 1개 + gitignore(release/offline-bundle/ · secrets/) + HEAD 이후 봉인 매니페스트 = Ready 피어.</summary>
        private string ReadyPeer(string name)
        {
            string repo = Path.Combine(_root, name);
            Directory.CreateDirectory(repo);
            Assert.NotNull(GitRepoProbe.Git(repo, "init", "-q"));
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "release/offline-bundle/\nsecrets/\n");
            File.WriteAllText(Path.Combine(repo, "Core.cs"), "class Core {}\n");
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "init");
            Manifest(repo, DateTimeOffset.UtcNow.AddMinutes(5));
            return repo;
        }

        // ════════════ §1 판정 ════════════

        [Fact]
        public void Ready_Clean_NoKeys_ResealedAfterHead_Cli0()
        {
            string repo = ReadyPeer("ready");
            File.WriteAllText(Path.Combine(repo, "release", "notes.md"), "산출물 메모");               // release/ · *.md = 코드 아님
            var r = PeerReadinessProbe.Probe(repo);
            Assert.True(r.Ready, string.Join(" | ", r.Issues));
            Assert.Equal(40, r.Head!.Length);
            Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerReadinessFlag, repo }, TextWriter.Null));
            Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { ReleaseHandshakeCli.PeerReadinessFlag, repo }));
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerReadinessFlag }, TextWriter.Null));
        }

        [Fact]
        public void UncommittedCode_And_KeyMaterial_NamesOnly_NotReady()
        {
            string repo = ReadyPeer("dirty");
            File.WriteAllText(Path.Combine(repo, "Core.cs"), "class Core { int x; }\n");                 // 수정
            File.WriteAllText(Path.Combine(repo, "auto_seal.ps1"), "# 봉인 도구\n");                   // 미추적 코드
            const string secret = "SECRET-KEY-MATERIAL-DO-NOT-LEAK";
            File.WriteAllText(Path.Combine(repo, "key_out.txt"), secret);                                // 미추적 키
            Directory.CreateDirectory(Path.Combine(repo, "secrets"));
            File.WriteAllText(Path.Combine(repo, "secrets", "signer.pem"), secret);                     // gitignore 안 키(status 미표시 · 탐지돼야)
            var r = PeerReadinessProbe.Probe(repo);
            foreach (var i in r.Issues) _out.WriteLine($"[probe] {i.Issue}: {i.Detail}");
            Assert.False(r.Ready);
            Assert.Equal(3, r.DirtyCodePaths);                                                           // Core.cs · auto_seal.ps1 · key_out.txt(스크립트 [0/10] 과 같은 규칙 — 비문서 미추적) · secrets/ 는 gitignore
            Assert.Contains(r.Issues, i => i.Issue == PeerReadinessIssue.UncommittedCode);
            Assert.Equal(2, r.KeyFiles);
            Assert.Contains(r.Issues, i => i.Issue == PeerReadinessIssue.KeyMaterialPresent && i.Detail.StartsWith("key_out.txt", StringComparison.Ordinal));
            Assert.Contains(r.Issues, i => i.Issue == PeerReadinessIssue.KeyMaterialPresent && i.Detail.StartsWith("secrets/signer.pem", StringComparison.Ordinal));
            var sw = new StringWriter();
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerReadinessFlag, repo }, sw));
            Assert.DoesNotContain(secret, sw.ToString());                                                // ★내용 미열람 · 미출력
            Assert.DoesNotContain(r.Issues, i => i.Detail.Contains(secret, StringComparison.Ordinal));
            Assert.True(PeerReadinessProbe.IsKeyMaterialName("a/b/KEY_OUT.bin") && PeerReadinessProbe.IsKeyMaterialName("x.PFX") && PeerReadinessProbe.IsKeyMaterialName("id_rsa"));
            Assert.False(PeerReadinessProbe.IsKeyMaterialName("keyboard.cs") || PeerReadinessProbe.IsKeyMaterialName("monkey.txt"));
        }

        [Fact]
        public void Manifest_Missing_Invalid_Stale_NotRepo_AreNotReady()
        {
            string miss = ReadyPeer("miss");
            File.Delete(Path.Combine(miss, "release", "offline-bundle", "RELEASE_MANIFEST.json"));
            Assert.Contains(PeerReadinessProbe.Probe(miss).Issues, i => i.Issue == PeerReadinessIssue.ManifestMissing);

            string bad = ReadyPeer("bad");
            File.WriteAllText(Path.Combine(bad, "release", "offline-bundle", "RELEASE_MANIFEST.json"), "{\"rootHash\":");
            Assert.Contains(PeerReadinessProbe.Probe(bad).Issues, i => i.Issue == PeerReadinessIssue.ManifestInvalid);
            string noRoot = ReadyPeer("noroot");
            Manifest(noRoot, DateTimeOffset.UtcNow.AddMinutes(5), rootHash: "short");
            Assert.Contains(PeerReadinessProbe.Probe(noRoot).Issues, i => i.Issue == PeerReadinessIssue.ManifestInvalid);

            string stale = ReadyPeer("stale");
            Manifest(stale, DateTimeOffset.UtcNow.AddDays(-1));                                          // 현재 HEAD '이전' 봉인
            var s = PeerReadinessProbe.Probe(stale);
            Assert.False(s.Ready);
            Assert.Contains(s.Issues, i => i.Issue == PeerReadinessIssue.ManifestStale);

            string notRepo = Path.Combine(_root, "plain");
            Directory.CreateDirectory(notRepo);
            var n = PeerReadinessProbe.Probe(notRepo);
            Assert.False(n.Ready);
            Assert.Null(n.Head);
            Assert.Contains(n.Issues, i => i.Issue == PeerReadinessIssue.NotARepository);
            // 감싸는 저장소 안의 하위 폴더(저장소 최상위 아님) = 저장소 아님 — 상위 저장소를 판정하지 않는다
            string nested = Path.Combine(stale, "src");
            Directory.CreateDirectory(nested);
            var ns = PeerReadinessProbe.Probe(nested);
            Assert.Null(ns.Head);
            Assert.Contains(ns.Issues, i => i.Issue == PeerReadinessIssue.NotARepository && i.Detail.Contains("감싸는 저장소", StringComparison.Ordinal));
            Assert.False(PeerReadinessProbe.Probe(Path.Combine(_root, "does-not-exist")).Ready);
        }

        // ════════════ §2 ★비파괴 ════════════

        private static string IndexSha(string repo) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(repo, ".git", "index"))));

        private static void StaleStat(string repo)
        {
            string f = Path.Combine(repo, "Core.cs");
            File.SetLastWriteTimeUtc(f, File.GetLastWriteTimeUtc(f).AddMinutes(7));                    // 내용 동일 · stat 만 변경 → 일반 status 는 인덱스 재기록
        }

        [Fact]
        public void Probe_NeverRewritesPeerIndex_WhilePlainStatusDoes()
        {
            string repo = ReadyPeer("nondestructive");
            StaleStat(repo);
            string before = IndexSha(repo);
            var r = PeerReadinessProbe.Probe(repo);
            Assert.True(r.Ready, string.Join(" | ", r.Issues));
            Assert.Equal(before, IndexSha(repo));                                                        // ★판정 후 .git/index 바이트 불변
            Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { ReleaseHandshakeCli.PeerReadinessFlag, repo }, TextWriter.Null));
            Assert.Equal(before, IndexSha(repo));
            // 대조군: 선택적 잠금을 허용한 일반 status 는 같은 상태에서 인덱스를 다시 쓴다(메커니즘 실증 · 이 git 에서 관측)
            var psi = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            psi.ArgumentList.Add("status"); psi.ArgumentList.Add("--porcelain");
            using (var p = System.Diagnostics.Process.Start(psi)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(30_000); }
            bool plainRewrote = IndexSha(repo) != before;
            _out.WriteLine($"[대조군] 일반 git status 인덱스 재기록 = {plainRewrote}");
            Assert.True(plainRewrote, "대조군: 이 환경의 git status 가 인덱스를 다시 쓰지 않음(메커니즘 미관측)");
        }

        [Fact]
        public void ScriptAndProbeHelper_UseNoOptionalLocks_ForPeerReads()
        {
            string root = RepoRoot();
            string script = File.ReadAllText(Path.Combine(root, "release-package.ps1"));
            Assert.Contains("$peerDirty = @(& git -C $PeerRepo --no-optional-locks -c core.quotepath=false status", script);
            string hs = File.ReadAllText(Path.Combine(root, "StockManager", "Tools", "ReleaseHandshake.cs"));
            int lockFlag = hs.IndexOf("psi.ArgumentList.Add(\"--no-optional-locks\");", StringComparison.Ordinal);
            int quote = hs.IndexOf("psi.ArgumentList.Add(\"-c\"); psi.ArgumentList.Add(\"core.quotepath=false\");", StringComparison.Ordinal);
            Assert.True(lockFlag > 0 && quote > lockFlag, "GitRepoProbe.Git 이 모든 호출에 --no-optional-locks 를 먼저 부여해야 함");
        }

        // ════════════ §3 피어 오염 격리 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void BuildDefinitions_HaveNoPeerPathReference_ShippedManifestHasNoPeerFiles()
        {
            string root = RepoRoot();
            var peerPath = new Regex(@"(\.\.[\\/]+QuantumVault)|([\\/]QuantumVault[\\/])", RegexOptions.IgnoreCase);
            var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(f => (f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".props", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".targets", StringComparison.OrdinalIgnoreCase))
                            && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            Assert.True(files.Length >= 5);
            foreach (var f in files) Assert.False(peerPath.IsMatch(File.ReadAllText(f)), "빌드 정의에 피어 경로 참조: " + f);   // ★피어 작업 트리는 빌드 입력이 아니다
            string mf = Path.Combine(root, "release", "RELEASE_MANIFEST.json");
            if (!File.Exists(mf)) { _out.WriteLine("[격리] release/ 산출물 없음 — 매니페스트 대조 생략"); return; }
            Assert.True(MultiRepoReleaseManifest.TryParse(File.ReadAllText(mf), out var m, out _, out string why), why);
            Assert.DoesNotContain(m.Artifacts, a => a.Name.Contains("QuantumVault", StringComparison.OrdinalIgnoreCase));
            Assert.Null(m.Peer);                                                                          // 단독 출하(-NoPeer)
        }
    }
}
