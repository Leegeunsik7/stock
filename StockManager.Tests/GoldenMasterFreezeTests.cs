// ============================================================================
// GoldenMasterFreezeTests — [Phase 4] 크로스 감사 · 배포 파이프라인 · Production Golden Master 최종 동결
//
//   재현(Red → Green, 수정 전 실측):
//     · 버전 단일 원천 붕괴 — csproj <Version>10.1.0 고착 → 실 아티팩트 "release-v10.1.0-GM.zip" 발행(v12.14.x 제품)
//     · v1 봉인 이름 미결속 — 봉인 후 아티팩트 파일명을 바꿔도 VerifyReleaseSeal 통과(매니페스트 ≠ 디스크)
//     · release-package.ps1 — release\ 삭제 후 manifest.sig 존재 검사(구조상 영구 미동봉) · liveArmed 소스 가드 fail-open
//       (false 호출 1개만 있으면 true 호출이 추가돼도 통과) · zip 사후 검증 부재 → 스크립트 교정 + 본 스위트의 파이프라인 모드
//   파이프라인 모드: release-package.ps1 이 MS2E_RELEASE_STAGE(스테이징 폴더)·MS2E_RELEASE_MANIFEST·MS2E_RELEASE_ZIP 을 주입하면
//   "실제 출하 바이너리"에 대해 동일 검증을 수행한다(미주입 시 로컬 Release 산출물로 동일 경로 검증).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    internal static class GmPaths
    {
        public static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln 루트 탐색 실패");
        }

        /// <summary>(host, proxy) 출하 폴더 — 파이프라인 스테이징 우선, 없으면 로컬 Release 산출물.</summary>
        public static (string Host, string Proxy) ShipDirs()
        {
            string? stage = Environment.GetEnvironmentVariable("MS2E_RELEASE_STAGE");
            if (!string.IsNullOrWhiteSpace(stage)) return (Path.Combine(stage, "host"), Path.Combine(stage, "proxy"));
            string root = RepoRoot();
            return (Path.Combine(root, "StockManager", "bin", "Release", "net8.0-windows"),
                    Path.Combine(root, "StockManager.KiwoomProxy", "bin", "Release", "net8.0-windows"));
        }
    }

    /// <summary>릴리스 zip 사후 검증기 — 빈 zip·해시 리스트 불일치·매니페스트 누락/봉인 불일치를 전부 거부.</summary>
    internal static class ReleaseZipVerifier
    {
        public static bool Verify(string zipPath, out string reason)
        {
            reason = "";
            string tmp = Path.Combine(Path.GetTempPath(), "gm_zip_" + Guid.NewGuid().ToString("N"));
            try
            {
                using var zip = ZipFile.OpenRead(zipPath);
                static string Norm(string n) => n.Replace('\\', '/');
                var bins = zip.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
                if (bins.Count == 0) { reason = "바이너리 0건(빈 릴리스)"; return false; }
                var list = zip.Entries.FirstOrDefault(e => Norm(e.FullName) == "artifacts.sha256.txt");
                var manifest = zip.Entries.FirstOrDefault(e => Norm(e.FullName) == "manifest.sig");
                if (list is null) { reason = "artifacts.sha256.txt 누락"; return false; }
                if (manifest is null) { reason = "manifest.sig 누락"; return false; }

                var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                using (var r = new StreamReader(list.Open()))
                    for (string? line; (line = r.ReadLine()) is not null;)
                    {
                        if (line.StartsWith('#') || line.Trim().Length == 0) continue;
                        int sp = line.IndexOf("  ", StringComparison.Ordinal);
                        if (sp != 64) { reason = "해시 리스트 형식 오류: " + line; return false; }
                        expected[Norm(line[(sp + 2)..].Trim())] = line[..64];
                    }
                if (expected.Count != bins.Count) { reason = $"해시 리스트 {expected.Count}건 ≠ zip 바이너리 {bins.Count}건"; return false; }
                foreach (var e in bins)
                {
                    string dest = Path.Combine(tmp, Norm(e.FullName).Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    e.ExtractToFile(dest);
                    string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dest)));
                    if (!expected.TryGetValue(Norm(e.FullName), out var want) || !string.Equals(want, actual, StringComparison.OrdinalIgnoreCase))
                    { reason = "해시 불일치: " + e.FullName; return false; }
                }
                string mpath = Path.Combine(tmp, "manifest.sig");
                manifest.ExtractToFile(mpath);
                string mtext = File.ReadAllText(mpath);
                if (!mtext.Contains("\"schema\": \"ms2e-release-manifest/2\"", StringComparison.Ordinal)) { reason = "manifest 스키마 v2 아님"; return false; }
                string host = Path.Combine(tmp, "host"), proxy = Path.Combine(tmp, "proxy");
                Directory.CreateDirectory(host); Directory.CreateDirectory(proxy);
                if (!AssemblySealGenerator.VerifyReleaseSeal(mpath, host, proxy)) { reason = "masterSeal 불일치"; return false; }

                return true;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                reason = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally { try { Directory.Delete(tmp, true); } catch { /* 임시 정리 실패 무관 */ } }
        }
    }

    public sealed class GoldenMasterFreezeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gm_freeze_" + Guid.NewGuid().ToString("N"));
        public GoldenMasterFreezeTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 무관 */ } }

        private (string Host, string Proxy) FakeShip()
        {
            string host = Path.Combine(_root, "host"), proxy = Path.Combine(_root, "proxy");
            Directory.CreateDirectory(host); Directory.CreateDirectory(proxy);
            File.WriteAllBytes(Path.Combine(host, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(host, "MS2E.StockSuite.exe"), new byte[] { 5, 6, 7 });
            File.WriteAllBytes(Path.Combine(proxy, "MS2E.KiwoomProxyHost.dll"), new byte[] { 9, 9, 9 });
            return (host, proxy);
        }

        // ═══ §1 버전·봉인 무결성 ═══

        [Fact]
        public void Host_Version_Matches_Golden_Master_Milestone()
        {
            var v = typeof(StockManager.Blazor.BlazorHomeForm).Assembly.GetName().Version!;
            Assert.Equal(new Version(12, 18, 6, 0), v);                  // 수정 전: 10.1.0.0 → release-v10.1.0-GM.zip 발행
            Assert.Equal("12.18.6", ReleasePackager.DefaultVersion);
        }

        [Fact]
        public void V2_Seal_Binds_Artifact_Names_Rename_Is_Detected()
        {
            var (host, proxy) = FakeShip();
            string m = Path.Combine(_root, "manifest.sig");
            AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", host, proxy, m);
            Assert.True(AssemblySealGenerator.VerifyReleaseSeal(m, host, proxy));
            // 봉인 후 파일명 변경(정렬 위치 보존) — 수정 전 v1 은 이름이 봉인 입력에 없어 그대로 통과(매니페스트 ≠ 디스크)
            File.Move(Path.Combine(host, "MS2E.StockSuite.exe"), Path.Combine(host, "MS2E.StockSuitf.exe"));
            Assert.False(AssemblySealGenerator.VerifyReleaseSeal(m, host, proxy));
        }

        [Fact]
        public void V2_Seal_Detects_Cross_Role_Swap_Of_Same_Named_Binary()
        {
            var (host, proxy) = FakeShip();
            File.WriteAllBytes(Path.Combine(host, "Shared.dll"), new byte[] { 0xAA });
            File.WriteAllBytes(Path.Combine(proxy, "Shared.dll"), new byte[] { 0xBB });
            string m = Path.Combine(_root, "manifest.sig");
            string json = AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", host, proxy, m);
            Assert.Contains("\"host/Shared.dll\"", json);
            Assert.Contains("\"proxy/Shared.dll\"", json);
            File.WriteAllBytes(Path.Combine(host, "Shared.dll"), new byte[] { 0xBB });   // x64↔x86 동명 치환
            File.WriteAllBytes(Path.Combine(proxy, "Shared.dll"), new byte[] { 0xAA });
            Assert.False(AssemblySealGenerator.VerifyReleaseSeal(m, host, proxy));
        }

        [Fact]
        public void V1_Path_Seal_Rejects_Duplicate_File_Names_Instead_Of_Nondeterministic_Order()
        {
            var (host, proxy) = FakeShip();
            File.WriteAllBytes(Path.Combine(proxy, "MS2E.StockSuite.dll"), new byte[] { 7 });
            var paths = new[] { Path.Combine(host, "MS2E.StockSuite.dll"), Path.Combine(proxy, "MS2E.StockSuite.dll") };
            Assert.Throws<InvalidOperationException>(() => ReleasePackager.ComputeMasterSealHex(paths));
        }

        [Fact]
        public void Local_Ship_Artifacts_Seal_And_Verify_Roundtrip()
        {
            var (host, proxy) = GmPaths.ShipDirs();
            Assert.True(Directory.Exists(host) && Directory.Exists(proxy), $"출하 폴더 없음: {host} | {proxy}");
            string m = Environment.GetEnvironmentVariable("MS2E_RELEASE_MANIFEST") is { Length: > 0 } pm ? pm : Path.Combine(_root, "manifest.sig");
            string json = AssemblySealGenerator.GenerateReleaseSeal(ReleasePackager.DefaultVersion + "-GM", host, proxy, m);
            Assert.Contains("\"host/MS2E.StockSuite.dll\"", json);
            Assert.Contains("\"proxy/MS2E.KiwoomProxyHost.exe\"", json);
            Assert.True(AssemblySealGenerator.VerifyReleaseSeal(m, host, proxy));
        }

        [Fact]
        public void Shipped_Host_Binary_Is_The_Binary_Under_Test()
        {
            // IL 가드(아래)가 검사하는 어셈블리 = 실제 출하 바이너리임을 해시로 결속(다른 빌드를 검사하고 통과하는 착시 차단).
            var (host, _) = GmPaths.ShipDirs();
            string shipped = Path.Combine(host, "MS2E.StockSuite.dll");
            string loaded = typeof(StockManager.Blazor.BlazorHomeForm).Assembly.Location;
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaded))),
                         Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(shipped))));
        }

        // ═══ §2 liveArmed:false — 컴파일된 바이너리 IL 가드 ═══

        private static readonly OpCode[] One = new OpCode[0x100], Two = new OpCode[0x100];
        static GoldenMasterFreezeTests()
        {
            foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (f.GetValue(null) is not OpCode op) continue;
                ushort v = unchecked((ushort)op.Value);
                if (v < 0x100) One[v] = op; else if ((v & 0xFF00) == 0xFE00) Two[v & 0xFF] = op;
            }
        }

        private static List<(OpCode Op, int Operand)> Decode(byte[] il)
        {
            var list = new List<(OpCode, int)>();
            int i = 0;
            while (i < il.Length)
            {
                OpCode op = il[i] == 0xFE ? Two[il[++i]] : One[il[i]];
                i++;
                int size = op.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                    _ => 4,
                };
                int operand = size == 4 ? BitConverter.ToInt32(il, i) : size == 1 ? il[i] : 0;
                list.Add((op, operand));
                i += size;
            }
            return list;
        }

        /// <summary>호스트 컴포지션 루트(및 람다 본문)에서 물리 이중키 설정 조회의 "기본값 인자"를 전수 판독.</summary>
        internal static (int Sites, List<string> Violations) ScanDualKeyDefaults(Type root)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var methods = new List<MethodBase>();
            void Walk(Type t) { methods.AddRange(t.GetMethods(All)); methods.AddRange(t.GetConstructors(All)); foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) Walk(n); }
            Walk(root);
            int sites = 0;
            var violations = new List<string>();
            foreach (var m in methods)
            {
                byte[]? il = m.GetMethodBody()?.GetILAsByteArray();
                if (il is null) continue;
                var ins = Decode(il);
                for (int k = 0; k < ins.Count; k++)
                {
                    if (ins[k].Op != OpCodes.Ldstr) continue;
                    string s = m.Module.ResolveString(ins[k].Operand);
                    if (s is not ("Dispatch.ValveWired" or "Dispatch.LiveArmingWired")) continue;
                    sites++;
                    // 기대 형태: ldstr key → ldc.i4.0 → call(virt) RuntimeConfig.GetBool(string,bool)
                    bool literalFalse = k + 2 < ins.Count && ins[k + 1].Op == OpCodes.Ldc_I4_0
                        && (ins[k + 2].Op == OpCodes.Call || ins[k + 2].Op == OpCodes.Callvirt)
                        && m.Module.ResolveMethod(ins[k + 2].Operand) is MethodInfo { Name: "GetBool" };
                    if (!literalFalse) violations.Add($"{m.DeclaringType?.Name}.{m.Name}: \"{s}\" 기본값이 리터럴 false 가 아님(다음 옵코드 {ins[k + 1].Op})");
                }
            }
            return (sites, violations);
        }

        [Fact]
        public void Compiled_Host_Hardcodes_Dual_Physical_Key_Defaults_To_False()
        {
            var (sites, violations) = ScanDualKeyDefaults(typeof(StockManager.Blazor.BlazorHomeForm));
            Assert.True(sites >= 5, $"sites={sites}");   // 비공허성(감사 시점 실측 ≥7)
            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void Dual_Key_Il_Guard_Detects_A_True_Default()
        {
            var (_, violations) = ScanDualKeyDefaults(typeof(DualKeyTrueDefaultProbe));   // 양성 대조군(오탐 아닌 실탐지 증명)
            Assert.Single(violations);
        }

        [Fact]
        public void Unconfigured_Runtime_Resolves_Both_Physical_Keys_To_False()
        {
            var cfg = new RuntimeConfig(_ => null);   // 설정 원천 전무(신규 설치) — 물리 키 미결속
            Assert.False(cfg.GetBool("Dispatch.ValveWired", false));
            Assert.False(cfg.GetBool("Dispatch.LiveArmingWired", false));
        }

        // ═══ §2 릴리스 zip 사후 검증 ═══

        private string BuildZip(bool withManifest, bool tamper, bool binaries = true)
        {
            var (host, proxy) = FakeShip();
            string stage = Path.Combine(_root, "zipstage");
            Directory.CreateDirectory(Path.Combine(stage, "host")); Directory.CreateDirectory(Path.Combine(stage, "proxy"));
            var lines = new List<string> { "# test" };
            if (binaries)
                foreach (var (dir, role) in new[] { (host, "host"), (proxy, "proxy") })
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        string rel = role + "\\" + Path.GetFileName(f);
                        File.Copy(f, Path.Combine(stage, rel));
                        lines.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))) + "  " + rel);
                    }
            File.WriteAllLines(Path.Combine(stage, "artifacts.sha256.txt"), lines);
            if (withManifest) AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", host, proxy, Path.Combine(stage, "manifest.sig"));
            if (tamper) File.WriteAllBytes(Path.Combine(stage, "host", "MS2E.StockSuite.dll"), new byte[] { 6, 6, 6 });
            string zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
            ZipFile.CreateFromDirectory(stage, zip);
            Directory.Delete(stage, true);
            return zip;
        }

        [Fact]
        public void Release_Zip_Verifier_Accepts_Complete_Package()
            => Assert.True(ReleaseZipVerifier.Verify(BuildZip(withManifest: true, tamper: false), out var why), why);

        [Fact]
        public void Release_Zip_Verifier_Rejects_Empty_Missing_Manifest_And_Tampered()
        {
            Assert.False(ReleaseZipVerifier.Verify(BuildZip(withManifest: true, tamper: false, binaries: false), out var a)); Assert.Contains("빈 릴리스", a);
            Assert.False(ReleaseZipVerifier.Verify(BuildZip(withManifest: false, tamper: false), out var b)); Assert.Contains("manifest.sig", b);
            Assert.False(ReleaseZipVerifier.Verify(BuildZip(withManifest: true, tamper: true), out var c)); Assert.Contains("해시 불일치", c);
        }

        [Fact]
        public void Pipeline_Release_Zip_Is_Complete_When_Provided()
        {
            string? zip = Environment.GetEnvironmentVariable("MS2E_RELEASE_ZIP");
            if (string.IsNullOrWhiteSpace(zip)) return;   // 파이프라인 [8/8] 단계 전용(로컬 실행은 위 합성 zip 테스트가 동일 검증기를 증명)
            Assert.True(ReleaseZipVerifier.Verify(zip, out var why), why);
        }
    }

    /// <summary>IL 가드 양성 대조군 — 물리 키 기본값을 true 로 둔 가상의 회귀.</summary>
    internal static class DualKeyTrueDefaultProbe
    {
        internal static bool Probe(RuntimeConfig cfg) => cfg.GetBool("Dispatch.ValveWired", true);
    }

    // ═══ §3 방향 A+B — Zero-Alloc Cold-Boot SHA-256 Handshake + Runtime Anti-Tamper Guard ═══
    [Collection("ConcurrencyStress")]
    public sealed class AssemblyAntiTamperHandshakeTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gm_tamper_" + Guid.NewGuid().ToString("N"));
        public AssemblyAntiTamperHandshakeTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 무관 */ } }

        private (string Path, byte[] Hash) Artifact(string name, int size)
        {
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            string p = Path.Combine(_root, name);
            File.WriteAllBytes(p, bytes);
            return (p, SHA256.HashData(bytes));
        }

        [Fact]
        public void Handshake_Passes_On_Untouched_Artifacts_And_Regions_Then_Unblocks()
        {
            using var g = new AssemblyAntiTamperHandshake(new[] { Artifact("a.dll", 200_000), Artifact("b.exe", 1) });
            g.RegisterRegion(GC.AllocateArray<byte>(4096, pinned: true));
            Assert.True(g.ArmingBlocked);                     // 첫 핸드셰이크 전 fail-closed
            var v = g.Verify();
            Assert.True(v.Passed, v.ToString());
            Assert.False(g.ArmingBlocked);
        }

        [Fact]
        public void Real_Shipped_Binaries_Pass_The_Handshake()
        {
            var (host, proxy) = GmPaths.ShipDirs();
            var arts = Directory.GetFiles(host, "*.dll").Concat(Directory.GetFiles(proxy, "*.exe"))
                .Select(p => (p, SHA256.HashData(File.ReadAllBytes(p)))).ToList();
            Assert.True(arts.Count >= 10, $"artifacts={arts.Count}");
            using var g = new AssemblyAntiTamperHandshake(arts);
            Assert.True(g.Verify().Passed);
        }

        [Fact]
        public void Wrong_Expected_Hash_Trips_And_Zeroizes_Order_Tokens_Sticky()
        {
            var (p, h) = Artifact("c.dll", 1000);
            h[0] ^= 1;   // 매니페스트와 다른 디스크 바이너리(사전 변조) 모사
            var token = new PoisonedSecretBuffer(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            var purger = new VolatileMemoryPurger();
            var apiSecret = new byte[] { 1, 2, 3 };
            purger.Register(apiSecret);
            TamperReason seen = TamperReason.None;
            using var g = new AssemblyAntiTamperHandshake(new[] { (p, h) }, new[] { token }, purger, r => seen = r);
            var v = g.Verify();
            Assert.False(v.Passed);
            Assert.Equal(TamperReason.ArtifactHashMismatch, v.Reason);
            Assert.Equal(TamperReason.ArtifactHashMismatch, seen);
            Assert.True(token.AllZero());
            Assert.Throws<ObjectDisposedException>(() => token.ReadByte(0));   // 뒤늦은 발주 토큰 재참조 차단
            Assert.True(purger.AllZero);
            Assert.True(g.ArmingBlocked);
            Assert.True(g.Verify().Tripped);                                   // sticky — 재검증으로 복구 불가
        }

        [Fact]
        public void Held_Handle_Blocks_Runtime_Binary_Replacement()
        {
            var a = Artifact("d.dll", 512);
            using var g = new AssemblyAntiTamperHandshake(new[] { a });
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(a.Path, new byte[] { 0 }));   // 공유 위반 — 교체 오픈 거부
            Assert.True(g.Verify().Passed);
        }

        [Fact]
        public void Poh_Region_Single_Bit_Flip_Trips()
        {
            var region = GC.AllocateArray<byte>(64 * 1024, pinned: true);
            new Random(3).NextBytes(region);
            var token = new PoisonedSecretBuffer(new byte[] { 9, 9 });
            using var g = new AssemblyAntiTamperHandshake(Array.Empty<(string, byte[])>(), new[] { token });
            int idx = g.RegisterRegion(region);
            Assert.True(g.Verify().Passed);
            region[40_000] ^= 0x01;                            // 외부 프로세스 메모리 쓰기 모사(1비트)
            var v = g.Verify();
            Assert.Equal(TamperReason.RegionHashMismatch, v.Reason);
            Assert.Equal(idx, v.Index);
            Assert.True(token.IsPoisoned);
        }

        [Fact]
        public void Steady_State_Verify_Is_Zero_Alloc()
        {
            using var g = new AssemblyAntiTamperHandshake(new[] { Artifact("e.dll", 300_000), Artifact("f.dll", 70_000) });
            g.RegisterRegion(GC.AllocateArray<byte>(128 * 1024, pinned: true));
            for (int i = 0; i < 5; i++) Assert.True(g.Verify().Passed);   // 워밍업(JIT·티어링)
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50; i++) g.Verify();
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, delta);
        }

        [Fact]
        public void Background_Monitor_Detects_Tamper_And_Zeroizes_Within_Deadline()
        {
            var region = GC.AllocateArray<byte>(8192, pinned: true);
            var token = new PoisonedSecretBuffer(new byte[] { 0x55, 0x66 });
            using var tripped = new ManualResetEventSlim(false);
            using var g = new AssemblyAntiTamperHandshake(Array.Empty<(string, byte[])>(), new[] { token }, onTrip: _ => tripped.Set());
            g.RegisterRegion(region);
            Assert.True(g.Verify().Passed);
            g.StartMonitor(TimeSpan.FromMilliseconds(10));
            region[7] = 0xFF;
            Assert.True(tripped.Wait(TimeSpan.FromSeconds(10)), "모니터가 변조를 탐지하지 못함");
            Assert.True(token.AllZero());
            Assert.True(g.ArmingBlocked);
        }

        [Fact]
        public void Normal_Dispose_Is_Not_A_Tamper_Trip_But_Keeps_Arming_Blocked()
        {
            var token = new PoisonedSecretBuffer(new byte[] { 1 });
            var g = new AssemblyAntiTamperHandshake(new[] { Artifact("g.dll", 100) }, new[] { token });
            Assert.True(g.Verify().Passed);
            g.StartMonitor(TimeSpan.FromMilliseconds(5));
            g.Dispose();
            Assert.False(g.IsTripped);
            Assert.False(token.IsPoisoned);
            Assert.True(g.ArmingBlocked);
            Assert.False(g.Verify().Passed);
        }

        [Fact]
        public void Invalid_Expected_Hash_Length_Rejected_Without_Leaking_Handles()
        {
            var a = Artifact("h.dll", 10);
            Assert.Throws<ArgumentException>(() => new AssemblyAntiTamperHandshake(new[] { a, (a.Path, new byte[16]) }));
            File.WriteAllBytes(a.Path, new byte[] { 1 });   // 핸들이 누수됐다면 공유 위반
        }
    }
}
