// ============================================================================
// MultiRepoManifestAuditTests — [다중 저장소 봉인 감사 · 추천 A]
//
//  §1 [실결함] 무대조 필드 위조 후 재봉인(무키 SHA-256 manifestHash 재계산) — genesisHeadHash · interlocks · peer 경로/이름이
//     수정 전에는 핸드셰이크를 통과했다 → GenesisMismatch · InterlockSetMismatch · PeerIdentityMismatch · 관측 부재 = 거부.
//  §2 태그 소스 인터락 열거 판독(git show) — HEAD 소스 = 실행 바이너리 Enum.GetNames · 형식 이탈 = null(fail-closed).
//  §3 [방향 A] ManifestSealStreamer — 기준 구현과 정규화 해시 동치(무작위 200건 · 대소문자 · 정렬 무관) · 1비트 변조 매트릭스 ·
//     중복 키/이스케이프 = 판독기 차이 거부(ParserDifferential) · δ=0 · 1ms 이내.
//  §4 실 출하 기록(release/ — git 무시 폴더 · 존재할 때만) v12.14.1-GM: 매니페스트 ↔ manifest.sig ↔ master-seal.txt ↔ zip ↔ 태그 소스 인터락.
//  §5 release-package.ps1 [0/10] 피어 사전 점검(빌드 전 fail-fast · 읽기 전용) · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MS2E.Core;
using StockManager.Tools;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MultiRepoManifestAuditTests
    {
        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        private static string FlipBit(string hex) => (hex[0] == '0' ? '1' : '0') + hex[1..];

        private const string Src = "9f3b7ef08456c9b30a1d29aaa2233a9f28116d43";
        private const string PeerHead = "6023a30aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string QvPath = "release/offline-bundle/RELEASE_MANIFEST.json";
        private static readonly string Seal = new('7', 64), Genesis = new('6', 64), SigSha = new('5', 64), ZipSha = new('8', 64), ShimSha = new('C', 64), PeerSha = new('2', 64);
        private static readonly string[] Locks = { "BootIntegrity", "CredentialLeak", "AccountDivergence", "StartupSanity" };

        private static ReleaseManifestInput Input() => new(
            "12.14.1-GM", "v12.14.1-GM", Src, SigSha, Seal, Genesis,
            new List<(string, string)> { ("host/MS2E.StockSuite.dll", new string('1', 64)), ("proxy/MS2E.KiwoomProxyHost.exe", new string('3', 64)), ("host/A.dll", new string('4', 64)) },
            "release-v12.14.1-GM.zip", ZipSha, ShimSha, false, Locks, new PeerRecord("QuantumVault", PeerHead, QvPath, PeerSha, "2018f926"));

        private static HandshakeObservation Obs(string? genesis = "", IReadOnlyList<string>? locks = null, bool nullLocks = false, PeerObservation? peer = null)
            => new(new RepoObservation(Src, Src, Array.Empty<string>(), Array.Empty<string>()), SigSha, "12.14.1-GM", Seal, Input().Artifacts, ZipSha, Seal, ShimSha,
                   peer ?? new PeerObservation(PeerHead, Array.Empty<string>(), PeerSha, "2018f926", QvPath, "QuantumVault"),
                   genesis == "" ? Genesis : genesis, nullLocks ? null : locks ?? Locks);

        private static ReleaseHandshakeVerdict V(string json, HandshakeObservation o) => MultiRepoReleaseManifest.Verify(json, o, out _);

        // ════════════ §1 무대조 필드 위조 후 재봉인 ════════════

        [Fact]
        public void ForgedAndResealed_UnanchoredFields_NowRejected()
        {
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(MultiRepoReleaseManifest.Build(Input()), Obs()));
            // 제네시스 계보 위조 + manifestHash 재계산(공격자도 계산 가능한 무키 해시) — 자기 봉인은 통과하지만 manifest.sig 와 불일치
            string g = MultiRepoReleaseManifest.Build(Input() with { GenesisHeadHash = FlipBit(Genesis) });
            Assert.Equal(ReleaseHandshakeVerdict.GenesisMismatch, V(g, Obs()));
            // 인터락 하나 삭제(차단 회로가 출하본에 있다고 허위 주장/누락 은폐) — 태그 소스 열거와 불일치
            string l = MultiRepoReleaseManifest.Build(Input() with { Interlocks = new[] { "BootIntegrity", "CredentialLeak", "StartupSanity" } });
            Assert.Equal(ReleaseHandshakeVerdict.InterlockSetMismatch, V(l, Obs()));
            string order = MultiRepoReleaseManifest.Build(Input() with { Interlocks = new[] { "CredentialLeak", "BootIntegrity", "AccountDivergence", "StartupSanity" } });
            Assert.Equal(ReleaseHandshakeVerdict.InterlockSetMismatch, V(order, Obs()));   // 순서 = 비트 배정 — 순서 위조도 거부
            // 피어 매니페스트 경로 바꿔치기(다른 파일 해시를 기록) · 피어 이름 위조
            string pp = MultiRepoReleaseManifest.Build(Input() with { Peer = new PeerRecord("QuantumVault", PeerHead, "release/RELEASE_MANIFEST.v2.5.0.superseded.json", PeerSha, "2018f926") });
            Assert.Equal(ReleaseHandshakeVerdict.PeerIdentityMismatch, V(pp, Obs()));
            string pn = MultiRepoReleaseManifest.Build(Input() with { Peer = new PeerRecord("QuantumVaultX", PeerHead, QvPath, PeerSha, "2018f926") });
            Assert.Equal(ReleaseHandshakeVerdict.PeerIdentityMismatch, V(pn, Obs()));
            // 관측 부재 = 거부(fail-closed)
            string ok = MultiRepoReleaseManifest.Build(Input());
            Assert.Equal(ReleaseHandshakeVerdict.GenesisMismatch, V(ok, Obs(genesis: null)));
            Assert.Equal(ReleaseHandshakeVerdict.InterlockSetMismatch, V(ok, Obs(nullLocks: true)));
            Assert.Equal(ReleaseHandshakeVerdict.PeerIdentityMismatch, V(ok, Obs(peer: new PeerObservation(PeerHead, Array.Empty<string>(), PeerSha, "2018f926"))));
        }

        // ════════════ §2 태그 소스 인터락 열거 ════════════

        [Fact]
        public void InterlockEnum_SourceParse_EqualsRuntimeNames_FailClosedOnMalformed()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "01_Core", "ArmingInterlockBitmask.cs"));
            var parsed = GitRepoProbe.ParseInterlockEnum(src);
            Assert.NotNull(parsed);
            Assert.Equal(Enum.GetNames<ArmingInterlockSource>(), parsed!);         // 값 오름차순 = Enum.GetNames
            Assert.Equal("OpenOrderDivergence", parsed[22]);
            Assert.Equal(30, parsed.Count);                                         // 비트 0~28(29개 · v12.17.6 OrderPathLatency) + StartupSanity(63)
            Assert.Null(GitRepoProbe.ParseInterlockEnum("class X {}"));
            Assert.Null(GitRepoProbe.ParseInterlockEnum("enum ArmingInterlockSource { A = 0, B = 0, }"));     // 값 중복
            Assert.Null(GitRepoProbe.ParseInterlockEnum("enum ArmingInterlockSource { A, B }"));               // 명시 값 없음
            Assert.Null(GitRepoProbe.ParseInterlockEnum("enum ArmingInterlockSource { }"));
            Assert.Equal(new[] { "A", "C", "B" }, GitRepoProbe.ParseInterlockEnum("enum ArmingInterlockSource : byte\n{\n  B = 9, // x = 1\n  A = 0,\n  C = 3\n}")!);
            Assert.Null(GitRepoProbe.TagInterlockNames(RepoRoot(), "not-a-commit"));
            Assert.Null(GitRepoProbe.TagInterlockNames(RepoRoot(), null));
        }

        [Fact]
        public void GitProbe_Utf8Output_KoreanCommentsAndPaths_NotMerged()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ms2e_mr_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "src", "01_Core"));
            try
            {
                Assert.NotNull(GitRepoProbe.Git(dir, "init", "-q"));
                GitRepoProbe.Git(dir, "config", "user.email", "ci@example.invalid"); GitRepoProbe.Git(dir, "config", "user.name", "ci"); GitRepoProbe.Git(dir, "config", "commit.gpgsign", "false");
                // 한글 주석 끝 바이트 + 줄바꿈 — CP949 디코딩이면 다음 멤버가 주석 줄에 병합돼 소실된다
                File.WriteAllText(Path.Combine(dir, "src", "01_Core", "ArmingInterlockBitmask.cs"),
                    "enum ArmingInterlockSource : byte\n{\n    RouterFeed      = 6,   // [피드 연동 · 방향 B] 시세/체결 피드 단절 회로 차단기\n    OrderSlotAge = 7, // 응답 타임아웃 슬롯 회수\n    StartupSanity = 63,\n}\n",
                    new UTF8Encoding(false));
                Assert.NotNull(GitRepoProbe.Git(dir, "add", "-A"));
                Assert.NotNull(GitRepoProbe.Git(dir, "commit", "-q", "-m", "init"));
                string head = GitRepoProbe.Git(dir, "rev-parse", "HEAD")!;
                Assert.Equal(new[] { "RouterFeed", "OrderSlotAge", "StartupSanity" }, GitRepoProbe.TagInterlockNames(dir, head)!);
                File.WriteAllText(Path.Combine(dir, "주문경로.cs"), "class K {}\n");
                Assert.Contains("주문경로.cs", GitRepoProbe.StatusPaths(dir));            // porcelain 한글 경로 무손상
            }
            finally
            {
                try { foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ════════════ §3 [방향 A] ManifestSealStreamer ════════════

        private static byte[] Hex(string h) => Convert.FromHexString(h);

        private static ManifestStreamVerdict Stream(string json, string? anchor = null, string? zip = null, string? genesis = null)
        {
            var st = new ManifestSealStreamer(Math.Max(1024, json.Length * 2));
            Assert.True(st.Load(Encoding.UTF8.GetBytes(json)));
            return st.Verify(anchor is null ? default : Hex(anchor), zip is null ? default : Hex(zip), genesis is null ? default : Hex(genesis));
        }

        [Fact]
        public void Streamer_EquivalentToReference_Random200_CaseAndOrderInsensitive()
        {
            var rng = new Random(20261006);
            for (int n = 0; n < 200; n++)
            {
                var arts = new List<(string, string)>();
                int count = rng.Next(0, 40);
                for (int i = 0; i < count; i++) arts.Add(($"d{rng.Next(5)}/f{rng.Next(1_000_000)}_{i}.dll", Convert.ToHexString(RandomNumberGenerator.GetBytes(32))));
                var locks = new List<string>();
                for (int i = 0; i < rng.Next(0, 30); i++) locks.Add("L" + rng.Next(1000));
                var m = new ReleaseManifestInput("1." + n, "v1." + n, Convert.ToHexString(RandomNumberGenerator.GetBytes(20)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), arts, "z.zip",
                    Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), false, locks,
                    rng.Next(2) == 0 ? null : new PeerRecord("QuantumVault", Convert.ToHexString(RandomNumberGenerator.GetBytes(20)), QvPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), "abc"));
                string json = MultiRepoReleaseManifest.Build(m);
                var st = new ManifestSealStreamer();
                Assert.True(st.Load(Encoding.UTF8.GetBytes(json)));
                Assert.Equal(ManifestStreamVerdict.Ok, st.Verify(Hex(m.MasterSeal), Hex(m.ZipSha256), Hex(m.GenesisHeadHash)));
                Assert.Equal(MultiRepoReleaseManifest.CanonicalHash(m), Convert.ToHexString(st.ManifestHash));
                Assert.Equal((count, locks.Count), (st.ArtifactCount, st.InterlockCount));
            }
            // 소문자 해시 · 아티팩트 순서 뒤섞음 — 정규화(대문자 · 서수 정렬) 후 같은 해시 = 두 판독기 모두 Ok
            var b = Input();
            string j = MultiRepoReleaseManifest.Build(b).Replace(new string('1', 64), new string('1', 64).ToLowerInvariant()).Replace("\"CCCC", "\"cccc");
            var doc = JsonDocument.Parse(j);
            Assert.Equal(ManifestStreamVerdict.Ok, Stream(j));
            string swapped = SwapFirstTwoArtifacts(MultiRepoReleaseManifest.Build(b));
            Assert.NotEqual(MultiRepoReleaseManifest.Build(b), swapped);
            Assert.Equal(ManifestStreamVerdict.Ok, Stream(swapped));
            Assert.Equal(ReleaseHandshakeVerdict.Ok, V(swapped, Obs()));
            doc.Dispose();
        }

        private static string SwapFirstTwoArtifacts(string json)
        {
            int a0 = json.IndexOf("\"artifacts\"", StringComparison.Ordinal);
            int s1 = json.IndexOf('{', a0), e1 = json.IndexOf('}', s1), s2 = json.IndexOf('{', e1), e2 = json.IndexOf('}', s2);
            string o1 = json[s1..(e1 + 1)], o2 = json[s2..(e2 + 1)];
            return json[..s1] + o2 + json[(e1 + 1)..s2] + o1 + json[(e2 + 1)..];
        }

        [Fact]
        public void Streamer_TamperMatrix_OneBit_LiveArmed_Anchors_Malformed()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            Assert.Equal(ManifestStreamVerdict.Ok, Stream(json, Seal, ZipSha, Genesis));
            foreach (string field in new[] { ZipSha, ShimSha, SigSha, new string('3', 64) })
                Assert.Equal(ManifestStreamVerdict.SelfHashMismatch, Stream(json.Replace(field, FlipBit(field))));   // 본문 1비트(재봉인 없음)
            Assert.Equal(ManifestStreamVerdict.SelfHashMismatch, Stream(json.Replace("\"AccountDivergence\"", "\"AccountDivergencf\"")));
            Assert.Equal(ManifestStreamVerdict.SelfHashMismatch, Stream(json.Replace(PeerHead, FlipBit(PeerHead))));
            Assert.Equal(ManifestStreamVerdict.LiveArmedViolation, Stream(MultiRepoReleaseManifest.Build(Input() with { LiveArmedDefault = true })));
            Assert.Equal(ManifestStreamVerdict.SealMismatch, Stream(json, FlipBit(Seal)));
            Assert.Equal(ManifestStreamVerdict.ZipMismatch, Stream(json, Seal, FlipBit(ZipSha)));
            Assert.Equal(ManifestStreamVerdict.GenesisMismatch, Stream(json, Seal, ZipSha, FlipBit(Genesis)));
            Assert.Equal(ManifestStreamVerdict.Malformed, Stream(json[..(json.Length / 2)]));
            Assert.Equal(ManifestStreamVerdict.Malformed, Stream(json.Replace("\"artifactCount\": 3", "\"artifactCount\": 2")));
            Assert.Equal(ManifestStreamVerdict.Malformed, Stream(json + " {}"));
            Assert.Equal(ManifestStreamVerdict.Malformed, Stream(json.Replace("ms2e-multirepo-release/1", "ms2e-multirepo-release/2")));
            Assert.Equal(ManifestStreamVerdict.NonCanonical, Stream(json.Replace("host/A.dll", "host/Ä.dll")));
            var tiny = new ManifestSealStreamer(1024);
            Assert.False(tiny.Load(new byte[2048]));
            Assert.Equal(ManifestStreamVerdict.Malformed, tiny.Verify(default));
        }

        [Fact]
        public void ParserDifferential_DuplicateKeyOrEscape_RejectedEvenWhenReferenceAccepts()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            // 같은 값의 중복 키 — 기준 판독(JsonDocument)은 통과하는 형태지만 판독기마다 '어느 값'을 볼지 달라질 수 있는 구조 → 거부
            string dup = json.Replace("\"tag\": \"v12.14.1-GM\",", "\"tag\": \"v12.14.1-GM\",\n  \"version\": \"12.14.1-GM\",");
            Assert.True(MultiRepoReleaseManifest.TryParse(dup, out var dm, out string dh, out _) && dh == MultiRepoReleaseManifest.CanonicalHash(dm));
            Assert.Equal(ManifestStreamVerdict.NonCanonical, Stream(dup));
            Assert.Equal(ReleaseHandshakeVerdict.ParserDifferential, V(dup, Obs()));
            // 이스케이프로 같은 문자열 표기
            string esc = json.Replace("\"zipName\": \"release-v12.14.1-GM.zip\"", "\"zipName\": \"release-v12.14.1-GM\\u002Ezip\"");
            Assert.True(MultiRepoReleaseManifest.TryParse(esc, out var em, out string eh, out _) && eh == MultiRepoReleaseManifest.CanonicalHash(em));
            Assert.Equal(ReleaseHandshakeVerdict.ParserDifferential, V(esc, Obs()));
        }

        [Fact]
        public void Streamer_ZeroAlloc_And_UnderOneMillisecond()
        {
            var b = Input();
            var arts = new List<(string, string)>();
            for (int i = 0; i < 38; i++) arts.Add(($"host/Artifact{i:D2}.dll", Convert.ToHexString(SHA256.HashData(BitConverter.GetBytes(i)))));
            var m = b with { Artifacts = arts, Interlocks = Enum.GetNames<ArmingInterlockSource>() };
            byte[] bytes = Encoding.UTF8.GetBytes(MultiRepoReleaseManifest.Build(m));
            var st = new ManifestSealStreamer();
            byte[] anchor = Hex(Seal), zip = Hex(ZipSha), gen = Hex(Genesis);
            Assert.True(st.Load(bytes));
            Assert.Equal(ManifestStreamVerdict.Ok, st.Verify(anchor, zip, gen));
            Assert.Equal(0, MinAllocated(() => { st.Load(bytes); st.Verify(anchor, zip, gen); }));   // ★적재 + 판독 + 정규화 + SHA-256 + 대조 δ=0
            long best = long.MaxValue;
            for (int i = 0; i < 50; i++)
            {
                long t0 = Stopwatch.GetTimestamp();
                st.Load(bytes); st.Verify(anchor, zip, gen);
                best = Math.Min(best, Stopwatch.GetTimestamp() - t0);
            }
            Assert.True(best * 1000.0 / Stopwatch.Frequency < 1.0, $"핸드셰이크 {best * 1000.0 / Stopwatch.Frequency:F3}ms ≥ 1ms");
        }

        // ════════════ §4 실 출하 기록(v12.14.1-GM) ════════════

        [Fact]
        public void ShippedRelease_v12_14_1_GM_ManifestSigSealZipAndTagInterlocks_Agree()
        {
            string root = RepoRoot(), rel = Path.Combine(root, "release");
            string mf = Path.Combine(rel, "RELEASE_MANIFEST.json");
            if (!File.Exists(mf) || !File.Exists(Path.Combine(rel, "release-v12.14.1-GM.zip"))) return;   // release/ 는 git 무시 폴더 — 출하 산출물이 있는 작업 트리에서만 수검
            string json = File.ReadAllText(mf);
            Assert.True(MultiRepoReleaseManifest.TryParse(json, out var m, out string stored, out string why), why);
            Assert.Equal(stored, MultiRepoReleaseManifest.CanonicalHash(m));
            Assert.Equal(38, m.Artifacts.Count);
            string anchor = File.ReadAllText(Path.Combine(rel, "master-seal.txt")).Trim();
            using var sig = JsonDocument.Parse(File.ReadAllText(Path.Combine(rel, "manifest.sig")));
            string sigGenesis = sig.RootElement.GetProperty("genesisHeadHash").GetString()!;
            string zipSha = ReleaseHandshakeCli.FileSha(Path.Combine(rel, m.ZipName));
            Assert.Equal(m.ManifestSigSha256, ReleaseHandshakeCli.FileSha(Path.Combine(rel, "manifest.sig")));
            var st = new ManifestSealStreamer();
            using (var fs = File.OpenRead(mf)) Assert.True(st.Load(fs));
            byte[] an = Hex(anchor), zp = Hex(zipSha), gn = Hex(sigGenesis);
            Assert.Equal(ManifestStreamVerdict.Ok, st.Verify(an, zp, gn));         // ★매니페스트 ↔ master-seal.txt ↔ zip ↔ manifest.sig 제네시스
            Assert.Equal(0, MinAllocated(() => st.Verify(an, zp, gn)));
            var tagLocks = GitRepoProbe.TagInterlockNames(root, m.SourceCommit);
            Assert.NotNull(tagLocks);
            Assert.Equal(m.Interlocks, tagLocks!);                                 // ★기록 인터락 17종 = 태그 소스 열거(현 HEAD 24종과는 다름 — 출하본 기준)
            Assert.Equal(17, m.Interlocks.Count);
        }

        // ════════════ §5 패키징 스크립트 · 오중 빌드 ════════════

        [Fact]
        public void PackageScript_PeerPreflightBeforeBuild_ReadOnly_FiveBuild()
        {
            string root = RepoRoot();
            string s = File.ReadAllText(Path.Combine(root, "release-package.ps1"));
            int step0 = s.IndexOf("Step 0 ", StringComparison.Ordinal), pre = s.IndexOf("[10/10] 핸드셰이크 거부 예정, 빌드 전 중단", StringComparison.Ordinal), step1 = s.IndexOf("Step 1 ", StringComparison.Ordinal);
            Assert.True(step0 > 0 && step0 < pre && pre < step1, "피어 사전 점검은 [0/10] 안 · 빌드 전");
            Assert.Contains("if ($peerDirty.Count -gt 0) { Fail", s);
            Assert.Contains("if (-not (Test-Path -LiteralPath $peerManifest)) { Fail", s);
            Assert.DoesNotMatch(@"(Set-Content|Out-File|Copy-Item|Move-Item|Remove-Item|New-Item|Add-Content)[^\r\n]*\$(PeerRepo|peerManifest)", s);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.Equal((17, 18, 19, 20), ((int)ReleaseHandshakeVerdict.GenesisMismatch, (int)ReleaseHandshakeVerdict.InterlockSetMismatch,
                                            (int)ReleaseHandshakeVerdict.PeerIdentityMismatch, (int)ReleaseHandshakeVerdict.ParserDifferential));
        }
    }
}
