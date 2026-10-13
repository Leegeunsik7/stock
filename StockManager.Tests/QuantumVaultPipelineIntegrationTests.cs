// ============================================================================
// QuantumVaultPipelineIntegrationTests — [릴리스 CI · 추천 A] 오중 빌드 COM 쉼 게이트 + 출하 zip 무추출 수검(방향 A)
//                                       + 다중 저장소 핸드셰이크(방향 B · 피어 QuantumVault 읽기 전용)
//
//  §1 COM 쉼 CI 게이트 — 솔루션 등재 · KIWOOM_COM/경고=오류/x86 · 실 프록시 소스 링크 · 테스트 빌드 연동(경로 실재) ·
//     ProductionKiwoomApi #if 블록이 쓰는 _ax 멤버 100% 형상 선언 · 컴파일 산출물에 KIWOOM_COM 전용 형식(AxOcx) 존재.
//  §2 [방향 A] ZipSealDryRunVerifier — 정상 · 1비트 변조 · 앵커 1비트 · 미등재/누락 · 경로 이탈 · 봉인 필드 위조 · 비봉인 · 손상.
//  §3 [방향 B] MultiRepoReleaseManifest — 정상 · 판정별 변조(1비트 해시 · 태그 이동 · 코드 이탈 · 미커밋 · 쉼 · 피어 HEAD/매니페스트) ·
//     JSON 본문 위조 = 자기 봉인 불일치 · 공백 재배열 = 정규화 불변.
//  §4 GitRepoProbe — 임시 저장소 실측(태그·미커밋·문서 제외·이탈) · ★porcelain 첫 줄 앞 공백 보존 회귀 · 참조 이름 주입 거부.
//  §5 δ=0(해시 루프·봉인 재계산·hex 대조) · CLI 종료 코드 · release-package.ps1 게이트 순서/피어 무쓰기 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MS2E.Infrastructure;
using StockManager.Tools;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class QuantumVaultPipelineIntegrationTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "qvpipe_" + Guid.NewGuid().ToString("N"));

        public QuantumVaultPipelineIntegrationTests() => Directory.CreateDirectory(_tmp);

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(_tmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);   // .git 읽기 전용 객체
                Directory.Delete(_tmp, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        private static string Read(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        // ════════════ §1 COM 쉼 CI 게이트 ════════════

        [Fact]
        public void Shim_RegisteredInSolution_GateConfig_TestBuildLinked()
        {
            string sln = Read("StockManager.sln");
            Assert.Contains(@"tools\KiwoomComShim\KiwoomComShim.csproj", sln);
            string csproj = Read("tools", "KiwoomComShim", "KiwoomComShim.csproj");
            Assert.Contains("<DefineConstants>$(DefineConstants);KIWOOM_COM</DefineConstants>", csproj);
            Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", csproj);
            Assert.Contains("<PlatformTarget>x86</PlatformTarget>", csproj);
            foreach (var linked in new[] { @"StockManager.KiwoomProxy\ProductionKiwoomApi.cs", @"StockManager.KiwoomProxy\IKiwoomApi.cs", @"src\02_Infrastructure\Engine\KiwoomComBridgeCore.cs" })
                Assert.Contains(linked, csproj);
            // 테스트 빌드 연동 — 경로가 실재해야(수정 중 실측: 이스케이프 오류로 '..<TAB>ools' 가 되자 MSBuild 는 '경고'만 내고 쉼을 건너뜀)
            string tests = Read("StockManager.Tests", "StockManager.Tests.csproj");
            var m = Regex.Match(tests, "<ProjectReference Include=\"([^\"]*KiwoomComShim\\.csproj)\"[^>]*ReferenceOutputAssembly=\"false\"");
            Assert.True(m.Success, "테스트 프로젝트 → 쉼 빌드 순서 참조 없음");
            Assert.True(File.Exists(Path.GetFullPath(Path.Combine(RepoRoot(), "StockManager.Tests", m.Groups[1].Value))), "쉼 참조 경로 실재하지 않음: " + m.Groups[1].Value);
        }

        [Fact]
        public void Shim_DeclaresEveryAxMemberUsedByKiwoomComBlock()
        {
            string prod = Read("StockManager.KiwoomProxy", "ProductionKiwoomApi.cs");
            int a = prod.IndexOf("#if KIWOOM_COM", StringComparison.Ordinal), b = prod.IndexOf("#endif", a, StringComparison.Ordinal);
            Assert.True(a >= 0 && b > a);
            var used = Regex.Matches(prod[a..b], @"_ax\??\.(\w+)").Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            Assert.True(used.Count >= 15, "COM 블록 _ax 사용 멤버 수 이상: " + used.Count);
            string shim = Read("tools", "KiwoomComShim", "AxKHOpenAPIShim.cs");
            foreach (var name in used)
                Assert.True(Regex.IsMatch(shim, @"public\s+(event\s+\S+\s+" + name + @"\s*;|[\w<>\[\]?]+\s+" + name + @"\s*\()"), "쉼 형상 누락: AxKHOpenAPI." + name);
        }

        [Fact]
        public void Shim_CompiledOutput_ContainsKiwoomComOnlyTypes()
        {
            string cfg = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? "Debug" : "Release";
            string dll = Path.Combine(RepoRoot(), "tools", "KiwoomComShim", "bin", cfg, "net8.0-windows", "MS2E.KiwoomComShim.dll");
            Assert.True(File.Exists(dll), "쉼 산출물 없음(테스트 빌드가 쉼을 컴파일하지 않음): " + dll);
            using var pe = new PEReader(File.OpenRead(dll));
            var md = pe.GetMetadataReader();
            var names = md.TypeDefinitions.Select(h => md.GetString(md.GetTypeDefinition(h).Name)).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("AxOcx", names);            // ProductionKiwoomApi 의 #if KIWOOM_COM 전용 중첩 형식 = 블록이 실제 컴파일됨
            Assert.Contains("ProductionKiwoomApi", names);
            Assert.Contains("KiwoomComEventCore", names);
            Assert.True(pe.PEHeaders.CorHeader!.Flags.HasFlag(CorFlags.Requires32Bit) || pe.PEHeaders.CoffHeader.Machine == Machine.I386, "쉼은 x86(32비트 COM) 대상이어야 함");
        }

        // ════════════ §2 [방향 A] zip 무추출 수검 ════════════

        private sealed record Pkg(string Zip, string Seal, string ManifestJson);

        private Pkg MakePackage(string name = "pkg", Action<ZipArchive>? extra = null, Func<string, string>? editManifest = null, string? tamperEntry = null, string? dropEntry = null)
        {
            string dir = Path.Combine(_tmp, name);
            Directory.CreateDirectory(Path.Combine(dir, "host")); Directory.CreateDirectory(Path.Combine(dir, "proxy"));
            var files = new (string Rel, int Size)[] { ("host/MS2E.StockSuite.dll", 70_000), ("host/MS2E.StockSuite.exe", 9_000), ("proxy/MS2E.KiwoomProxyHost.exe", 120_000) };
            var arts = new List<ReleasePackager.ReleaseArtifact>();
            var rng = new Random(42);
            foreach (var (rel, size) in files)
            {
                var bytes = new byte[size]; rng.NextBytes(bytes);
                string path = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                File.WriteAllBytes(path, bytes);
                arts.Add(new ReleasePackager.ReleaseArtifact(rel, path));
            }
            string manifest = ReleasePackager.GenerateManifestJson("12.14.1-GM", arts);
            Assert.True(BootIntegrityGate.TryParse(manifest, out _, out string seal, out _));
            if (editManifest is not null) manifest = editManifest(manifest);
            string zip = Path.Combine(_tmp, name + ".zip");
            using (var za = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(za.CreateEntry("manifest.sig").Open(), new UTF8Encoding(false))) w.Write(manifest);
                foreach (var (rel, _) in files)
                {
                    if (rel == dropEntry) continue;
                    byte[] bytes = File.ReadAllBytes(Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (rel == tamperEntry) bytes[bytes.Length / 2] ^= 0x01;   // ★1비트 변조
                    using var s = za.CreateEntry(rel).Open();
                    s.Write(bytes);
                }
                extra?.Invoke(za);
            }
            return new Pkg(zip, seal, manifest);
        }

        private static string FlipBit(string hex)
        {
            char c = hex[^1];
            int v = Convert.ToInt32(c.ToString(), 16) ^ 1;
            return hex[..^1] + v.ToString("X");
        }

        [Fact]
        public void Zip_Genuine_Ok_NonBinaryExtrasAllowed()
        {
            var p = MakePackage(extra: za => { using var w = new StreamWriter(za.CreateEntry("host/README.txt").Open()); w.Write("문서"); });
            Assert.Equal(ZipSealVerdict.Ok, ZipSealDryRunVerifier.Verify(p.Zip, p.Seal, out string d));
            Assert.Contains("artifacts=3", d);
            Assert.Equal(ZipSealVerdict.Ok, ZipSealDryRunVerifier.Verify(p.Zip, " " + p.Seal.ToLowerInvariant() + "\r\n", out _));   // master-seal.txt 줄바꿈·소문자
        }

        [Fact]
        public void Zip_OneBitTamper_And_AnchorOneBit_Rejected()
        {
            var t = MakePackage("tamper", tamperEntry: "proxy/MS2E.KiwoomProxyHost.exe");
            Assert.Equal(ZipSealVerdict.ArtifactTampered, ZipSealDryRunVerifier.Verify(t.Zip, t.Seal, out string d));
            Assert.Contains("MS2E.KiwoomProxyHost.exe", d);
            var g = MakePackage("anchor");
            Assert.Equal(ZipSealVerdict.AnchorMismatch, ZipSealDryRunVerifier.Verify(g.Zip, FlipBit(g.Seal), out _));
            Assert.Equal(ZipSealVerdict.AnchorMalformed, ZipSealDryRunVerifier.Verify(g.Zip, "xyz", out _));
            Assert.Equal(ZipSealVerdict.AnchorMalformed, ZipSealDryRunVerifier.Verify(g.Zip, new string('G', 64), out _));
        }

        [Fact]
        public void Zip_UnlistedMissingEscapeDuplicate_Rejected()
        {
            var u = MakePackage("unlisted", extra: za => { using var s = za.CreateEntry("host/Evil.dll").Open(); s.WriteByte(1); });
            Assert.Equal(ZipSealVerdict.UnlistedBinary, ZipSealDryRunVerifier.Verify(u.Zip, u.Seal, out _));
            var m = MakePackage("missing", dropEntry: "host/MS2E.StockSuite.exe");
            Assert.Equal(ZipSealVerdict.ArtifactMissing, ZipSealDryRunVerifier.Verify(m.Zip, m.Seal, out _));
            var e = MakePackage("escape", extra: za => { using var s = za.CreateEntry("../outside.txt").Open(); s.WriteByte(1); });
            Assert.Equal(ZipSealVerdict.PathEscape, ZipSealDryRunVerifier.Verify(e.Zip, e.Seal, out _));
            var dup = MakePackage("dup", extra: za => { using var s = za.CreateEntry("host/MS2E.StockSuite.exe").Open(); s.WriteByte(1); });
            Assert.Equal(ZipSealVerdict.DuplicateEntry, ZipSealDryRunVerifier.Verify(dup.Zip, dup.Seal, out _));
        }

        [Fact]
        public void Zip_ForgedManifestHashes_KeptSealField_SealMismatch()
        {
            // 공격: 바이너리를 바꾸고 manifest 의 항목 해시만 새 값으로 고치되 masterSeal 필드(=앵커)는 그대로 → 항목 대조는 통과 · 재계산 봉인이 거부
            var basePkg = MakePackage("base");
            byte[] evil = new byte[9_000]; new Random(7).NextBytes(evil);
            string evilSha = Convert.ToHexString(SHA256.HashData(evil));
            string forged = Regex.Replace(basePkg.ManifestJson, "(\"name\": \"host/MS2E.StockSuite.exe\"[^}]*\"sha256\": \")[0-9A-F]{64}", "${1}" + evilSha);
            Assert.NotEqual(basePkg.ManifestJson, forged);
            string zip = Path.Combine(_tmp, "forged.zip");
            using (var src = ZipFile.OpenRead(basePkg.Zip))
            using (var dst = ZipFile.Open(zip, ZipArchiveMode.Create))
                foreach (var en in src.Entries)
                {
                    using var o = dst.CreateEntry(en.FullName).Open();
                    if (en.FullName == "manifest.sig") o.Write(Encoding.UTF8.GetBytes(forged));
                    else if (en.FullName == "host/MS2E.StockSuite.exe") o.Write(evil);
                    else { using var i = en.Open(); i.CopyTo(o); }
                }
            Assert.Equal(ZipSealVerdict.SealMismatch, ZipSealDryRunVerifier.Verify(zip, basePkg.Seal, out _));
        }

        [Fact]
        public void Zip_Unsealed_Corrupt_Rejected()
        {
            string noManifest = Path.Combine(_tmp, "nomanifest.zip");
            using (var za = ZipFile.Open(noManifest, ZipArchiveMode.Create)) { using var s = za.CreateEntry("host/a.dll").Open(); s.WriteByte(1); }
            string seal = new string('A', 64);
            Assert.Equal(ZipSealVerdict.ManifestMissing, ZipSealDryRunVerifier.Verify(noManifest, seal, out _));
            string junk = Path.Combine(_tmp, "junk.zip");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3, 4 });
            Assert.Equal(ZipSealVerdict.ZipUnreadable, ZipSealDryRunVerifier.Verify(junk, seal, out _));
            Assert.Equal(ZipSealVerdict.ZipUnreadable, ZipSealDryRunVerifier.Verify(Path.Combine(_tmp, "absent.zip"), seal, out _));
            var bad = MakePackage("badmanifest", editManifest: j => j.Replace("ms2e-release-manifest/2", "ms2e-release-manifest/1"));
            Assert.Equal(ZipSealVerdict.ManifestInvalid, ZipSealDryRunVerifier.Verify(bad.Zip, bad.Seal, out _));
        }

        // ════════════ §3 [방향 B] 다중 저장소 핸드셰이크 ════════════

        private const string Src = "9f3b7ef08456c9b30a1d29aaa2233a9f28116d43";
        private const string PeerHead = "6023a30aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static readonly string Seal = new string('7', 64);
        private const string QvPath = "release/offline-bundle/RELEASE_MANIFEST.json";
        private static readonly string SigSha = new string('5', 64), ZipSha = new string('8', 64), ShimSha = new string('C', 64), PeerSha = new string('2', 64);

        private static ReleaseManifestInput Input(PeerRecord? peer = null) => new(
            "12.14.1-GM", "v12.14.1-GM", Src, SigSha, Seal, new string('6', 64),
            new List<(string, string)> { ("host/MS2E.StockSuite.dll", new string('1', 64)), ("proxy/MS2E.KiwoomProxyHost.exe", new string('3', 64)) },
            "release-v12.14.1-GM.zip", ZipSha, ShimSha, false, new[] { "BootIntegrity", "AccountDivergence" },
            peer ?? new PeerRecord("QuantumVault", PeerHead, "release/offline-bundle/RELEASE_MANIFEST.json", PeerSha, "2018f926"));

        private static HandshakeObservation Obs(RepoObservation? repo = null, string? sigSha = null, string? version = "12.14.1-GM", string? sigSeal = null,
                                                IReadOnlyList<(string, string)>? arts = null, string? zip = null, string? anchor = null, string? shim = null, PeerObservation? peer = null, bool noPeer = false)
            => new(repo ?? new RepoObservation(Src, Src, Array.Empty<string>(), Array.Empty<string>()), sigSha ?? SigSha, version, sigSeal ?? Seal,
                   arts ?? Input().Artifacts, zip ?? ZipSha, anchor ?? Seal, shim ?? ShimSha,
                   noPeer ? null : peer ?? new PeerObservation(PeerHead, Array.Empty<string>(), PeerSha, "2018f926", QvPath, "QuantumVault"),
                   new string('6', 64), Input().Interlocks);   // [다중 저장소 봉인 감사] manifest.sig 제네시스 · 태그 소스 인터락 관측

        [Fact]
        public void Handshake_Genuine_Ok_CanonicalUnderReformat()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            Assert.Equal(ReleaseHandshakeVerdict.Ok, MultiRepoReleaseManifest.Verify(json, Obs(), out string d));
            Assert.Contains("peer QuantumVault", d);
            string compact = Regex.Replace(json, @"\s+(?=(?:[^""]*""[^""]*"")*[^""]*$)", "");   // 문자열 밖 공백 제거
            Assert.Equal(ReleaseHandshakeVerdict.Ok, MultiRepoReleaseManifest.Verify(compact, Obs(), out _));
            Assert.Equal(ReleaseHandshakeVerdict.Ok, MultiRepoReleaseManifest.Verify(json, Obs(sigSha: SigSha.ToLowerInvariant()), out _));   // 대소문자 무관
        }

        [Fact]
        public void Handshake_TamperMatrix_EachVerdict()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            void V(ReleaseHandshakeVerdict expected, HandshakeObservation o) => Assert.Equal(expected, MultiRepoReleaseManifest.Verify(json, o, out _));
            V(ReleaseHandshakeVerdict.TagMissing, Obs(new RepoObservation(Src, null, Array.Empty<string>(), Array.Empty<string>())));
            V(ReleaseHandshakeVerdict.SourceCommitMismatch, Obs(new RepoObservation("abc", "abcdef0123", Array.Empty<string>(), Array.Empty<string>())));   // 태그 이동
            V(ReleaseHandshakeVerdict.HeadDrift, Obs(new RepoObservation("ffff", Src, Array.Empty<string>(), new[] { "src/x.cs" })));
            V(ReleaseHandshakeVerdict.DirtyTree, Obs(new RepoObservation(Src, Src, new[] { "StockManager.KiwoomProxy/IKiwoomApi.cs" }, Array.Empty<string>())));
            V(ReleaseHandshakeVerdict.ManifestSigMismatch, Obs(sigSha: FlipBit(SigSha)));                                   // 1비트
            V(ReleaseHandshakeVerdict.ManifestSigMismatch, Obs(version: "12.14.2-GM"));
            V(ReleaseHandshakeVerdict.MasterSealMismatch, Obs(sigSeal: FlipBit(Seal)));
            V(ReleaseHandshakeVerdict.MasterSealMismatch, Obs(anchor: FlipBit(Seal)));                                      // master-seal.txt 1비트
            V(ReleaseHandshakeVerdict.ArtifactSetMismatch, Obs(arts: new List<(string, string)> { ("host/MS2E.StockSuite.dll", new string('1', 64)), ("proxy/MS2E.KiwoomProxyHost.exe", FlipBit(new string('3', 64))) }));
            V(ReleaseHandshakeVerdict.ArtifactSetMismatch, Obs(arts: new List<(string, string)> { ("host/MS2E.StockSuite.dll", new string('1', 64)) }));
            V(ReleaseHandshakeVerdict.ZipMismatch, Obs(zip: FlipBit(ZipSha)));
            V(ReleaseHandshakeVerdict.ShimMismatch, Obs(shim: FlipBit(ShimSha)));
            V(ReleaseHandshakeVerdict.PeerMissing, Obs(noPeer: true));
            V(ReleaseHandshakeVerdict.PeerDirty, Obs(peer: new PeerObservation(PeerHead, new[] { "Core/x.cs" }, PeerSha, "2018f926", QvPath, "QuantumVault")));
            V(ReleaseHandshakeVerdict.PeerCommitMismatch, Obs(peer: new PeerObservation("1111111", Array.Empty<string>(), PeerSha, "2018f926", QvPath, "QuantumVault")));
            V(ReleaseHandshakeVerdict.PeerManifestMismatch, Obs(peer: new PeerObservation(PeerHead, Array.Empty<string>(), FlipBit(PeerSha), "2018f926", QvPath, "QuantumVault")));
            V(ReleaseHandshakeVerdict.PeerManifestMismatch, Obs(peer: new PeerObservation(PeerHead, Array.Empty<string>(), PeerSha, "deadbeef", QvPath, "QuantumVault")));
        }

        [Fact]
        public void Handshake_ManifestBodyForged_SelfHashMismatch_LiveArmedViolation()
        {
            string json = MultiRepoReleaseManifest.Build(Input());
            string forged = json.Replace(ZipSha, FlipBit(ZipSha));                 // 본문 zip 해시만 위조(실측과 맞추려는 시도)
            Assert.Equal(ReleaseHandshakeVerdict.SelfHashMismatch, MultiRepoReleaseManifest.Verify(forged, Obs(zip: FlipBit(ZipSha)), out _));
            string armed = MultiRepoReleaseManifest.Build(Input() with { LiveArmedDefault = true });
            Assert.Equal(ReleaseHandshakeVerdict.LiveArmedViolation, MultiRepoReleaseManifest.Verify(armed, Obs(), out _));
            Assert.Equal(ReleaseHandshakeVerdict.Malformed, MultiRepoReleaseManifest.Verify("{ \"schema\": \"x\" }", Obs(), out _));
            Assert.Equal(ReleaseHandshakeVerdict.Malformed, MultiRepoReleaseManifest.Verify("not json", Obs(), out _));
            string noPeer = MultiRepoReleaseManifest.Build(Input() with { Peer = null });
            Assert.Equal(ReleaseHandshakeVerdict.Ok, MultiRepoReleaseManifest.Verify(noPeer, Obs(noPeer: true), out _));   // 피어 미기록(-NoPeer) = 피어 대조 생략
        }

        // ════════════ §4 git 관측 ════════════

        private static string? Git(string dir, params string[] args) => GitRepoProbe.Git(dir, args);

        private string InitRepo()
        {
            string r = Path.Combine(_tmp, "repo");
            Directory.CreateDirectory(r);
            Assert.NotNull(Git(r, "init", "-q"));
            Git(r, "config", "user.email", "ci@example.invalid"); Git(r, "config", "user.name", "ci"); Git(r, "config", "commit.gpgsign", "false");
            File.WriteAllText(Path.Combine(r, "a.cs"), "class A {}\n");
            File.WriteAllText(Path.Combine(r, "z.cs"), "class Z {}\n");
            File.WriteAllText(Path.Combine(r, "README.md"), "# r\n");
            Assert.NotNull(Git(r, "add", "-A"));
            Assert.NotNull(Git(r, "commit", "-q", "-m", "init"));
            Assert.NotNull(Git(r, "tag", "v1.0.0-GM"));
            return r;
        }

        [Fact]
        public void GitProbe_TempRepo_Tag_Dirty_DocsExcluded_Drift()
        {
            string r = InitRepo();
            var o = GitRepoProbe.Observe(r, "v1.0.0-GM");
            Assert.NotNull(o.Head);
            Assert.Equal(o.Head, o.TagCommit);
            Assert.Empty(o.DirtyPaths);
            Assert.Empty(o.DriftPaths);

            File.WriteAllText(Path.Combine(r, "README.md"), "# 문서만 변경\n");
            Assert.Empty(GitRepoProbe.Observe(r, "v1.0.0-GM").DirtyPaths);       // 문서 = 출하 무영향
            File.AppendAllText(Path.Combine(r, "a.cs"), "// 변경\n");
            var d = GitRepoProbe.Observe(r, "v1.0.0-GM");
            Assert.Equal(new[] { "a.cs" }, d.DirtyPaths.ToArray());               // ★porcelain 첫 줄 " M a.cs" — 앞 공백 보존(수정 전 ".cs" 로 잘림)
            File.WriteAllText(Path.Combine(r, "new.cs"), "class N {}\n");
            Assert.Contains("new.cs", GitRepoProbe.Observe(r, "v1.0.0-GM").DirtyPaths);   // 미추적 코드도 출하 오염

            Assert.NotNull(Git(r, "add", "-A"));
            Assert.NotNull(Git(r, "commit", "-q", "-m", "code after tag"));
            var drift = GitRepoProbe.Observe(r, "v1.0.0-GM");
            Assert.NotEqual(drift.Head, drift.TagCommit);
            Assert.Contains("a.cs", drift.DriftPaths);
            Assert.DoesNotContain("README.md", drift.DriftPaths);
            Assert.Null(GitRepoProbe.Observe(r, "v9.9.9-GM").TagCommit);
        }

        [Theory]
        [InlineData("v12.14.1-GM", true)]
        [InlineData("-c", false)]
        [InlineData("v1;rm -rf", false)]
        [InlineData("../x", false)]
        [InlineData("", false)]
        public void GitProbe_RefNameInjectionRejected(string tag, bool ok) => Assert.Equal(ok, GitRepoProbe.IsSafeRef(tag));

        [Theory]
        [InlineData(".claude/settings.json", false)]
        [InlineData("STATUS.md", false)]
        [InlineData("docs/x.cs", false)]
        [InlineData("release/manifest.sig", false)]
        [InlineData("StockManager.KiwoomProxy/IKiwoomApi.cs", true)]
        [InlineData("StockManager/Blazor/Pages/InstallerBuildPanel.razor", true)]
        public void GitProbe_CodePathRule(string path, bool code) => Assert.Equal(code, GitRepoProbe.IsCodePath(path));

        // ════════════ §5 δ=0 · CLI · 스크립트 · 오중 빌드 ════════════

        [Fact]
        public void ZeroAlloc_HashLoop_SealRecompute_HexCompare()
        {
            var payload = new byte[300_000]; new Random(3).NextBytes(payload);
            var ms = new MemoryStream(payload, writable: false);
            var buffer = new byte[81_920];
            using var ih = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var genesis = new byte[32]; var names = new byte[38 * 32]; var hashes = new byte[38 * 32]; var scratch = new byte[32 + 38 * 64];
            var dst = new byte[32]; var seal = new byte[32];
            string hex = Convert.ToHexString(SHA256.HashData(payload));
            bool ok = true;
            Action body = () =>
            {
                for (int k = 0; k < 20; k++)
                {
                    ms.Position = 0;
                    ZipSealDryRunVerifier.HashStreamInto(ms, ih, buffer, dst);
                    ZipSealDryRunVerifier.ComputeSealInto(genesis, names, hashes, scratch, seal);
                    ok &= ZipSealDryRunVerifier.HexEquals(hex, dst);
                    ok &= ZipSealDryRunVerifier.TryDecodeHex(hex, hashes.AsSpan(0, 32));
                }
            };
            body();   // 워밍업
            Assert.Equal(0, MinAllocated(body));
            Assert.True(ok);
            Assert.Equal(SHA256.HashData(payload), dst);
        }

        [Fact]
        public void Cli_VerifyZip_ExitCodes_And_SyncRequiresLiveArmedGuard()
        {
            var p = MakePackage("cli");
            string sealFile = Path.Combine(_tmp, "master-seal.txt");
            File.WriteAllText(sealFile, p.Seal + Environment.NewLine);
            string report = Path.Combine(_tmp, "rep.txt");
            Assert.Equal(ReleaseHandshakeCli.ExitOk, ReleaseHandshakeCli.Run(new[] { "--verify-zip", p.Zip, "--seal-file", sealFile, "--report", report }, TextWriter.Null));
            Assert.Contains("Ok", File.ReadAllText(report));
            File.WriteAllText(sealFile, FlipBit(p.Seal));
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { "--verify-zip", p.Zip, "--seal-file", sealFile }, TextWriter.Null));
            Assert.Equal(ReleaseHandshakeCli.ExitUsage, ReleaseHandshakeCli.Run(new[] { "--verify-zip", p.Zip }, TextWriter.Null));
            string outJson = Path.Combine(_tmp, "RM.json");
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { "--sync-release-manifest", "--release-dir", _tmp, "--repo", _tmp, "--shim", sealFile, "--out", outJson }, TextWriter.Null));
            Assert.False(File.Exists(outJson));                                    // liveArmed 가드 표지 없으면 기록 0
            Assert.Equal(ReleaseHandshakeCli.ExitRejected, ReleaseHandshakeCli.Run(new[] { "--release-handshake", "--release-dir", _tmp, "--repo", _tmp, "--shim", sealFile }, TextWriter.Null));   // 매니페스트 없음
            Assert.True(ReleaseHandshakeCli.IsInvocation(new[] { "--release-handshake" }));
            Assert.False(ReleaseHandshakeCli.IsInvocation(new[] { "--verify-seal" }));
        }

        [Fact]
        public void ReleasePackageScript_GateOrder_FailClosed_PeerReadOnly()
        {
            string path = Path.Combine(RepoRoot(), "release-package.ps1");
            byte[] raw = File.ReadAllBytes(path);
            Assert.True(raw.Length > 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF, "한글 .ps1 UTF-8 BOM 필수(PS 5.1 CP949 파싱)");
            string s = File.ReadAllText(path);
            int pre = s.IndexOf("Step 0 ", StringComparison.Ordinal), b1 = s.IndexOf("Step 1 ", StringComparison.Ordinal), b2 = s.IndexOf("Step 2 ", StringComparison.Ordinal);
            int live = s.IndexOf("Step 6 ", StringComparison.Ordinal), zip = s.IndexOf("Compress-Archive", StringComparison.Ordinal);
            int vz = s.IndexOf("'--verify-zip'", StringComparison.Ordinal), sync = s.IndexOf("'--sync-release-manifest'", StringComparison.Ordinal), hs = s.IndexOf("'--release-handshake'", StringComparison.Ordinal);
            int done = s.IndexOf("== 완료:", StringComparison.Ordinal);
            Assert.True(pre > 0 && pre < b1 && b1 < b2, "소스 무결성 사전 검사가 빌드보다 먼저여야 함");
            Assert.True(live < zip && zip < vz && vz < sync && sync < hs && hs < done, "zip → 무추출 수검 → 동기화 → 핸드셰이크 → 완료 순서");
            Assert.Contains("rev-list -n 1 $tag", s);
            // 단계 선언이 주석 처리되지 않았는지(행 시작) + 사전 검사의 거부 분기가 실제 조건인지(무력화 `if ($false)` 차단)
            foreach (int n in new[] { 0, 2, 9, 10 })
                Assert.Matches(new Regex(@"^Step " + n + @" """, RegexOptions.Multiline), s);
            Assert.Contains("if ($dirty.Count -gt 0) { Fail", s);
            Assert.Contains("if ($drift.Count -gt 0) { Fail", s);
            Assert.Contains("if ($LASTEXITCODE -ne 0 -or -not $tagCommit) { Fail", s);
            Assert.DoesNotMatch(@"if \(\$false\)", s);
            Assert.Contains("MS2E.KiwoomComShim.dll", s);
            Assert.Equal(7, Regex.Matches(s, "Remove-ReleaseOutputs; Fail").Count);   // v12.18.1-GM 강화 전용 행렬 대조 · 무추출 수검·바이너리 liveArmed 판독·피어 부재·동기화·핸드셰이크·인터락 봉인 대조 실패 = 산출물 전량 폐기
            // 피어 저장소(QuantumVault)에는 쓰지 않는다 — $PeerRepo 를 대상으로 한 쓰기 cmdlet 0건
            Assert.DoesNotMatch(@"(Set-Content|Out-File|Copy-Item|Move-Item|Remove-Item|New-Item|Add-Content)[^\r\n]*\$PeerRepo", s);
        }

        [Fact]
        public void FiveBuild_Regression_SolutionProjects_And_CliDispatch()
        {
            string sln = Read("StockManager.sln");
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.Tests\StockManager.Tests.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.Contains("TreatWarningsAsErrors", Read("StockManager.KiwoomProxy", "StockManager.KiwoomProxy.csproj"));
            string program = Read("StockManager", "Program.cs");
            int ui = program.IndexOf("ApplicationConfiguration.Initialize", StringComparison.Ordinal);
            int cli = program.IndexOf("ReleaseHandshakeCli.IsInvocation", StringComparison.Ordinal);
            Assert.True(cli > 0 && cli < ui, "핸드셰이크 CLI 는 UI 초기화 이전 분기");
        }
    }
}
