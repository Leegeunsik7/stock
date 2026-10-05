// ============================================================================
// ReleaseVerificationCliTests — [봉인 운영 · 추천 A] ExpectedMasterSeal 외부 앵커 · 봉인 수검 CLI · POH 해시 스트리머 · 변조 증거
//
//  §1 [실결함] 앵커 교착 — 수정 전 호스트는 같은 Release.ExpectedMasterSeal 을 Attestor(실행 DLL 단일 해시)와
//     BootIntegrityGate(manifest masterSeal)에 동시 주입 → 어떤 값으로도 둘 다 통과 불가(무장 영구 불가). 위임 후 단일 권위.
//  §2 엄격 앵커 — 미주입 = Unanchored · 1비트 불일치 = 차단+소거+킬 · 형식 오류 = 차단 · 동시 변조(바이너리+매니페스트 재작성) 차단.
//  §3 [방향 B] 변조 증거 — 아티팩트·기대/실측 SHA-256 · 포렌식 해시 체인 각인(결속 전 증거 재생).
//  §4 [방향 A] PinnedSha256Streamer — SHA-256 정합 · 할당이 파일 크기와 무관(수정 전 ReadAllBytes = 파일 크기만큼 LOH) · δ=0 청크 루프.
//  §5 CLI — 폴더/zip · 앵커 · 미등재 바이너리 · 사용법 · 보고서 · 종료 코드.
// ============================================================================
using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Tools;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ReleaseVerificationCliTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gm_cli_" + Guid.NewGuid().ToString("N"));
        private readonly string _scratch = Path.Combine(Path.GetTempPath(), "gm_cli_s_" + Guid.NewGuid().ToString("N"));
        private string Host => Path.Combine(_root, "host");
        private string Proxy => Path.Combine(_root, "proxy");
        private string Manifest => Path.Combine(_root, BootIntegrityGate.ManifestFileName);
        private string HostDll => Path.Combine(Host, "MS2E.StockSuite.dll");

        public ReleaseVerificationCliTests()
        {
            Directory.CreateDirectory(Host); Directory.CreateDirectory(Proxy); Directory.CreateDirectory(_scratch);
            File.WriteAllBytes(HostDll, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.exe"), new byte[] { 5, 6 });
            File.WriteAllBytes(Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe"), new byte[] { 7 });
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* 무관 */ }
            try { Directory.Delete(_scratch, true); } catch { /* 무관 */ }
        }

        private string Seal()
        {
            AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", Host, Proxy, Manifest);
            BootIntegrityGate.TryParse(File.ReadAllText(Manifest), out _, out var seal, out _);
            return seal;
        }

        private static string FlipLastNibble(string hex)
        {
            char c = hex[^1];
            char f = c == '0' ? '1' : '0';
            return hex[..^1] + f;
        }

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

        // ════════════ §1 앵커 교착(실결함) ════════════

        [Fact]
        public void Legacy_SingleKey_TwoIncompatibleChecks_NoValuePassesBoth()
        {
            string masterSeal = Seal();
            string dllHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(HostDll)));

            // 값 A = manifest masterSeal → 게이트 통과 · Attestor(실행 DLL 해시 대조) 차단
            using (var g = BootIntegrityGate.Start(Host, masterSeal, runAsync: false))
                Assert.False(g.ArmingBlocked);
            Assert.True(new ReleaseArtifactSha256Attestor(selfPath: HostDll, expectedSealHex: masterSeal).AttestationBlocking);

            // 값 B = 실행 DLL 해시 → Attestor 통과 · 게이트 차단
            Assert.False(new ReleaseArtifactSha256Attestor(selfPath: HostDll, expectedSealHex: dllHash).AttestationBlocking);
            using (var g = BootIntegrityGate.Start(Host, dllHash, runAsync: false))
                Assert.True(g.ArmingBlocked);

            // 값 없음 → Attestor 차단(미검증). ∴ 수정 전 호스트 무장 술어(둘 다 blockWhile)는 어떤 설정으로도 무장 불가였다.
            Assert.True(new ReleaseArtifactSha256Attestor(selfPath: HostDll).AttestationBlocking);
        }

        [Fact]
        public void Delegated_Attestor_FollowsManifestGate_SingleAuthority()
        {
            string masterSeal = Seal();
            var att = new ReleaseArtifactSha256Attestor(selfPath: HostDll);
            Assert.True(att.AttestationBlocking);                        // 위임 전 = 차단(fail-closed)
            using (var ok = BootIntegrityGate.Start(Host, masterSeal, runAsync: false, requireExternalAnchor: true))
            {
                att.DelegateToManifestGate(ok);
                Assert.True(att.SealValid);
                Assert.False(att.AttestationBlocking);
                Assert.True(att.Verified);
            }
            var att2 = new ReleaseArtifactSha256Attestor(selfPath: HostDll);
            using var unanchored = BootIntegrityGate.Start(Host, "", runAsync: false, requireExternalAnchor: true);
            att2.DelegateToManifestGate(unanchored);
            Assert.True(att2.AttestationBlocking);                        // 앵커 미주입 = 차단
        }

        [Fact]
        public void Host_Delegates_Attestor_And_Attaches_Forensics()
        {
            Assert.True(HostCalls(typeof(ReleaseArtifactSha256Attestor).GetMethod(nameof(ReleaseArtifactSha256Attestor.DelegateToManifestGate))!),
                        "BlazorHomeForm 이 Attestor 판정을 manifest 게이트에 위임하지 않음(교착 재발)");
            Assert.True(HostCalls(typeof(BootIntegrityGate).GetMethod(nameof(BootIntegrityGate.AttachForensicStream))!),
                        "봉인 변조 증거가 포렌식 체인에 결속되지 않음");
            Assert.True(HostCalls(typeof(StockManager.Tools.ReleaseVerificationCli).GetMethod(nameof(ReleaseVerificationCli.Run))!, typeof(StockManager.Program)),
                        "Program.Main 이 --verify-seal CLI 로 분기하지 않음");
        }

        private static bool HostCalls(MethodBase target, Type? hostType = null)
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = hostType ?? typeof(StockManager.Blazor.BlazorHomeForm);
            var types = new System.Collections.Generic.List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var t in types)
            {
                var all = new System.Collections.Generic.List<MethodBase>(t.GetMethods(AllF));
                all.AddRange(t.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try { if (m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == target) return true; } catch (ArgumentException) { }
                }
            }
            return false;
        }

        // ════════════ §2 엄격 앵커 ════════════

        [Fact]
        public void RequireAnchor_Missing_IsUnanchored_Blocked_NoZeroize()
        {
            Seal();
            var purger = new VolatileMemoryPurger();
            purger.Register(new byte[] { 9, 9 });
            using var g = BootIntegrityGate.Start(Host, "", purger: purger, runAsync: false, requireExternalAnchor: true);
            Assert.Equal(BootIntegrityStatus.Unanchored, g.Status);
            Assert.True(g.ArmingBlocked);
            Assert.False(g.AnchorVerified);
            Assert.False(purger.IsPurged);                                // 미주입은 차단만(오탐 소거 없음)
        }

        [Fact]
        public void Anchor_Exact_Seals_And_Unblocks()
        {
            string seal = Seal();
            using var g = BootIntegrityGate.Start(Host, "  " + seal.ToLowerInvariant() + " ", runAsync: false, requireExternalAnchor: true);
            Assert.Equal(BootIntegrityStatus.Sealed, g.Status);
            Assert.True(g.AnchorVerified);
            Assert.False(g.ArmingBlocked);
            Assert.Empty(g.TamperEvidence);
        }

        [Fact]
        public void Anchor_OneBitOff_Blocks_Zeroizes_Kills_RecordsEvidence()
        {
            string seal = Seal();
            var purger = new VolatileMemoryPurger();
            var secret = new byte[] { 0xAB, 0xCD };
            purger.Register(secret);
            int kills = 0;
            using var g = BootIntegrityGate.Start(Host, FlipLastNibble(seal), purger: purger, onTrip: _ => kills++, runAsync: false, requireExternalAnchor: true);
            Assert.Equal(BootIntegrityStatus.ManifestInvalid, g.Status);
            Assert.True(g.ArmingBlocked);
            Assert.True(purger.IsPurged && purger.AllZero);               // ★API Secret 0x00
            Assert.Equal(1, kills);                                       // ★킬 스위치
            var e = Assert.Single(g.TamperEvidence);
            Assert.Equal("manifest.sig#masterSeal", e.Artifact);
            Assert.Equal(FlipLastNibble(seal).ToUpperInvariant(), e.ExpectedSha256);
            Assert.Equal(seal.ToUpperInvariant(), e.ActualSha256);
        }

        [Theory]
        [InlineData("abc")]
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]     // 63
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeG")]    // 비hex
        [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00")]  // 66
        public void Anchor_Malformed_Blocked_WithoutComparison(string anchor)
        {
            Seal();
            var purger = new VolatileMemoryPurger();
            purger.Register(new byte[] { 1 });
            using var g = BootIntegrityGate.Start(Host, anchor, purger: purger, runAsync: false, requireExternalAnchor: true);
            Assert.Equal(BootIntegrityStatus.ManifestInvalid, g.Status);
            Assert.True(g.ArmingBlocked);
            Assert.False(purger.IsPurged);                                // 형식 오류 = 설정 실수 → 차단만
            Assert.Contains("형식", g.Detail);
        }

        [Fact]
        public void Insider_CoTamper_BinaryAndManifestRewritten_BlockedOnlyByAnchor()
        {
            string original = Seal();
            File.WriteAllBytes(HostDll, new byte[] { 0xEE, 0xEE, 0xEE });   // 바이너리 교체
            Seal();                                                         // 매니페스트까지 재작성(자기 일관)
            using (var selfConsistent = BootIntegrityGate.Start(Host, "", runAsync: false))
                Assert.Equal(BootIntegrityStatus.Sealed, selfConsistent.Status);   // 앵커 없이는 통과(위협 실증)

            var purger = new VolatileMemoryPurger();
            purger.Register(new byte[] { 7, 7 });
            using var anchored = BootIntegrityGate.Start(Host, original, purger: purger, runAsync: false, requireExternalAnchor: true);
            Assert.Equal(BootIntegrityStatus.ManifestInvalid, anchored.Status);
            Assert.True(anchored.ArmingBlocked);
            Assert.True(purger.IsPurged);
        }

        [Fact]
        public void SealGenerator_AnchoredOverload_Strict()
        {
            string seal = Seal();
            Assert.True(AssemblySealGenerator.VerifyReleaseSeal(Manifest, Host, Proxy, seal));
            Assert.False(AssemblySealGenerator.VerifyReleaseSeal(Manifest, Host, Proxy, FlipLastNibble(seal)));
            Assert.False(AssemblySealGenerator.VerifyReleaseSeal(Manifest, Host, Proxy, "abc"));
            File.WriteAllBytes(HostDll, new byte[] { 1, 2, 3, 5 });
            Assert.False(AssemblySealGenerator.VerifyReleaseSeal(Manifest, Host, Proxy, seal));
        }

        // ════════════ §3 변조 증거(방향 B) ════════════

        [Fact]
        public void TamperedBinary_Evidence_ExpectedVsActual_ChainedIntoForensics()
        {
            Seal();
            BootIntegrityGate.TryParse(File.ReadAllText(Manifest), out var entries, out _, out _);
            byte[] expected = entries.Find(x => x.Name == "host/MS2E.StockSuite.dll").Sha;
            var tampered = new byte[] { 1, 2, 3, 5 };
            File.WriteAllBytes(HostDll, tampered);

            using var g = BootIntegrityGate.Start(Host, runAsync: false);
            Assert.Equal(BootIntegrityStatus.Tampered, g.Status);
            var e = Assert.Single(g.TamperEvidence);
            Assert.Equal("host/MS2E.StockSuite.dll", e.Artifact);
            Assert.Equal(Convert.ToHexString(expected), e.ExpectedSha256);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(tampered)), e.ActualSha256);
            Assert.True(e.UtcTicks > 0);

            var stream = new ZeroAllocForensicAuditStream(64);
            g.AttachForensicStream(stream);                               // 결속 전 증거 재생
            Assert.Equal(2L, stream.WriteHead);                           // (기대 lo64, 실측 lo64) 쌍
            Assert.True(stream.VerifyDetailed().Intact);
        }

        [Fact]
        public void MissingArtifact_Evidence_Recorded()
        {
            Seal();
            File.Delete(Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe"));
            using var g = BootIntegrityGate.Start(Host, runAsync: false);
            Assert.Equal(BootIntegrityStatus.Incomplete, g.Status);
            var e = Assert.Single(g.TamperEvidence);
            Assert.Equal("proxy/MS2E.KiwoomProxyHost.exe", e.Artifact);
            Assert.Equal("MISSING", e.ActualSha256);
        }

        // ════════════ §4 POH 해시 스트리머(방향 A) ════════════

        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(1_048_577)] [InlineData(5_000_000)]
        public void Streamer_MatchesSha256(int size)
        {
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            string p = Path.Combine(_scratch, $"s{size}.bin");
            File.WriteAllBytes(p, bytes);
            Span<byte> h = stackalloc byte[32];
            Assert.Equal(size, PinnedSha256Streamer.HashFile(p, h));
            Assert.True(h.SequenceEqual(SHA256.HashData(bytes)));
        }

        [Fact]
        public void Streamer_Allocation_IndependentOfFileSize_NoLoh()
        {
            // [실결함 회귀] 수정 전 ReleasePackager 는 ReadAllBytes — 32MB 아티팩트 = 32MB(LOH) 할당. 이제 파일당 핸들 상수.
            string small = Path.Combine(_scratch, "small.bin"), big = Path.Combine(_scratch, "big.bin");
            File.WriteAllBytes(small, new byte[1 << 20]);
            File.WriteAllBytes(big, new byte[32 << 20]);
            var named = new[] { new ReleasePackager.ReleaseArtifact("host/big.bin", big) };
            _ = ReleasePackager.ComputeMasterSealHex(named);              // 스레드 POH 청크·해시 예열
            long sealAlloc = MinAllocated(() => ReleasePackager.ComputeMasterSealHex(named));
            Assert.True(sealAlloc < 64 * 1024, $"32MB 아티팩트 봉인 산출 할당 {sealAlloc}B — LOH 적재 회귀");

            byte[] hash = new byte[32];
            long aSmall = MinAllocated(() => PinnedSha256Streamer.HashFile(small, hash));
            long aBig = MinAllocated(() => PinnedSha256Streamer.HashFile(big, hash));
            Assert.True(Math.Abs(aBig - aSmall) < 1024, $"할당이 파일 크기에 비례(small={aSmall} big={aBig})");
        }

        [Fact]
        public void Streamer_ChunkLoop_ZeroAlloc_OnOpenHandle()
        {
            string p = Path.Combine(_scratch, "h.bin");
            File.WriteAllBytes(p, new byte[3 << 20]);
            using SafeFileHandle fh = File.OpenHandle(p);
            byte[] hash = new byte[32];
            PinnedSha256Streamer.HashHandle(fh, hash);
            Assert.Equal(0L, MinAllocated(() => { for (int i = 0; i < 4; i++) PinnedSha256Streamer.HashHandle(fh, hash); }));
        }

        // ════════════ §5 CLI ════════════

        private (int Code, string Report) Cli(params string[] args)
        {
            var sw = new StringWriter();
            int code = ReleaseVerificationCli.Run(args, sw);
            return (code, sw.ToString());
        }

        [Fact]
        public void Cli_Folder_Anchored_Ok()
        {
            string seal = Seal();
            var (code, report) = Cli("--verify-seal", _root, "--expected", seal, "--require-anchor");
            Assert.Equal(ReleaseVerificationCli.ExitOk, code);
            Assert.Contains("anchor=일치", report);
            Assert.Contains("artifacts=3", report);
        }

        [Fact]
        public void Cli_Folder_RequireAnchor_Missing_Exit3()
        {
            Seal();
            Assert.Equal(ReleaseVerificationCli.ExitUnanchored, Cli("--verify-seal", _root, "--require-anchor").Code);
            Assert.Equal(ReleaseVerificationCli.ExitOk, Cli("--verify-seal", _root).Code);   // 자기 일관성만(앵커 비요구)
        }

        [Fact]
        public void Cli_Folder_Tampered_Exit2_WithEvidence()
        {
            string seal = Seal();
            File.WriteAllBytes(HostDll, new byte[] { 9 });
            var (code, report) = Cli("--verify-seal", _root, "--expected", seal);
            Assert.Equal(ReleaseVerificationCli.ExitTampered, code);
            Assert.Contains("증거:", report);
            Assert.Contains("host/MS2E.StockSuite.dll", report);
        }

        [Fact]
        public void Cli_Folder_WrongAnchor_Exit2()
        {
            string seal = Seal();
            Assert.Equal(ReleaseVerificationCli.ExitTampered, Cli("--verify-seal", _root, "--expected", FlipLastNibble(seal)).Code);
        }

        [Fact]
        public void Cli_Folder_UnlistedBinary_Exit4()
        {
            string seal = Seal();
            File.WriteAllBytes(Path.Combine(Host, "version.dll"), new byte[] { 0x4D, 0x5A });   // DLL 하이재킹 표면
            var (code, report) = Cli("--verify-seal", _root, "--expected", seal);
            Assert.Equal(ReleaseVerificationCli.ExitUnlisted, code);
            Assert.Contains("미등재 바이너리: host/version.dll", report);
        }

        [Fact]
        public void Cli_Zip_Ok_And_Tampered()
        {
            string seal = Seal();
            string zip = Path.Combine(_scratch, "release-test.zip");
            ZipFile.CreateFromDirectory(_root, zip);
            Assert.Equal(ReleaseVerificationCli.ExitOk, Cli("--verify-seal", zip, "--expected", seal, "--require-anchor").Code);

            File.WriteAllBytes(Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe"), new byte[] { 8 });
            string zip2 = Path.Combine(_scratch, "release-tampered.zip");
            ZipFile.CreateFromDirectory(_root, zip2);
            Assert.Equal(ReleaseVerificationCli.ExitTampered, Cli("--verify-seal", zip2, "--expected", seal).Code);

            File.WriteAllBytes(Path.Combine(_scratch, "corrupt.zip"), new byte[] { 1, 2, 3 });
            Assert.Equal(ReleaseVerificationCli.ExitTampered, Cli("--verify-seal", Path.Combine(_scratch, "corrupt.zip")).Code);
        }

        [Fact]
        public void Cli_Usage_Exit1()
        {
            Assert.Equal(ReleaseVerificationCli.ExitUsage, Cli("--verify-seal").Code);
            Assert.Equal(ReleaseVerificationCli.ExitUsage, Cli("--verify-seal", "--expected").Code);
            Assert.Equal(ReleaseVerificationCli.ExitUsage, Cli("--verify-seal", _root, "--bogus").Code);
            Assert.Equal(ReleaseVerificationCli.ExitUsage, Cli("--verify-seal", _root, "--expected").Code);
        }

        [Fact]
        public void Cli_BadExpected_Or_MissingTarget_Exit1_And_NoManifest_Exit2()
        {
            Seal();
            Assert.Equal(ReleaseVerificationCli.ExitUsage, Cli("--verify-seal", _root, "--expected", "abc").Code);
            Assert.Equal(ReleaseVerificationCli.ExitUsage, Cli("--verify-seal", Path.Combine(_scratch, "nope")).Code);
            Assert.Equal(ReleaseVerificationCli.ExitTampered, Cli("--verify-seal", _scratch).Code);   // manifest.sig 없음
            Assert.False(ReleaseVerificationCli.IsInvocation(new[] { "--headless" }));
            Assert.True(ReleaseVerificationCli.IsInvocation(new[] { "--VERIFY-SEAL", "x" }));
        }

        [Fact]
        public void Cli_OutFile_Utf8Report()
        {
            string seal = Seal();
            string outFile = Path.Combine(_scratch, "report.txt");
            Assert.Equal(0, Cli("--verify-seal", _root, "--expected", seal, "--out", outFile).Code);
            string text = File.ReadAllText(outFile, Encoding.UTF8);
            Assert.Contains("status=Sealed", text);
            Assert.Contains("exit=0", text);
        }
    }
}
