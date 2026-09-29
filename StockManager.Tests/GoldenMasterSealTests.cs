// ============================================================================
// GoldenMasterSealTests — v12.14.0-GM Production Golden Master 봉인 & 배포 패키징 자동화
//   Section 1: AssemblySealGenerator(genesis-봉인 manifest.sig 조립기) 회귀 검증.
//   Think Beyond(독자 설계): ColdBootPohRingIntegritySentinel(δ=0 POH 체크섬 + 이중 키 콜드부팅 판정) 검증.
//   ★DI 무장 술어(liveArmed) 릴리스 봉인 결속(releaseAttestorRef.AttestationBlocking)은 이 리포의 기존 정책상
//   BlazorHomeForm.cs 는 단위테스트로 재구성하지 않고 사중 빌드+전체 스위트 그린으로 실증한다(GoldenMasterAuditTests.cs
//   기존 주석과 동일 원칙 — 단위테스트로 ServiceProvider 를 흉내 내 검증 중복/드리프트를 만들지 않는다).
// ============================================================================
using System;
using System.IO;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class GoldenMasterSealTests : IDisposable
    {
        private readonly string _root;
        private readonly string _hostDir;
        private readonly string _proxyDir;

        public GoldenMasterSealTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gm_seal_test_" + Guid.NewGuid().ToString("N"));
            _hostDir = Path.Combine(_root, "host");
            _proxyDir = Path.Combine(_root, "proxy");
            Directory.CreateDirectory(_hostDir);
            Directory.CreateDirectory(_proxyDir);
            File.WriteAllBytes(Path.Combine(_hostDir, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(_hostDir, "MS2E.StockSuite.exe"), new byte[] { 5, 6, 7 });
            File.WriteAllText(Path.Combine(_hostDir, "MS2E.StockSuite.pdb"), "not-a-binary-seal-target");
            File.WriteAllBytes(Path.Combine(_proxyDir, "MS2E.KiwoomProxyHost.dll"), new byte[] { 9, 9, 9 });
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* 임시 폴더 정리 실패는 테스트 결과 무관 */ }
        }

        // ══ AssemblySealGenerator ══════════════════════════════════════════════
        [Fact]
        public void ResolveHostArtifacts_OnlyDllAndExe_ExcludesPdb()
        {
            var artifacts = AssemblySealGenerator.ResolveHostArtifacts(_hostDir);
            Assert.Equal(2, artifacts.Count);
            Assert.DoesNotContain(artifacts, p => p.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ResolveHostArtifacts_MissingDir_ThrowsFailClosed()
            => Assert.Throws<DirectoryNotFoundException>(() =>
                AssemblySealGenerator.ResolveHostArtifacts(Path.Combine(_root, "ghost")));

        [Fact]
        public void GenerateReleaseSeal_WritesManifest_ContainingMasterSeal()
        {
            string manifestPath = Path.Combine(_root, "manifest.sig");
            string json = AssemblySealGenerator.GenerateReleaseSeal("12.14.0-GM", _hostDir, _proxyDir, manifestPath);
            Assert.True(File.Exists(manifestPath));
            Assert.Contains("\"masterSeal\"", json);
            Assert.Contains("\"artifactCount\": 3", json);   // host 2 + proxy 1
        }

        [Fact]
        public void VerifyReleaseSeal_UnmodifiedArtifacts_Passes()
        {
            string manifestPath = Path.Combine(_root, "manifest.sig");
            AssemblySealGenerator.GenerateReleaseSeal("12.14.0-GM", _hostDir, _proxyDir, manifestPath);
            Assert.True(AssemblySealGenerator.VerifyReleaseSeal(manifestPath, _hostDir, _proxyDir));
        }

        [Fact]
        public void VerifyReleaseSeal_TamperedArtifact_FailsClosed()
        {
            string manifestPath = Path.Combine(_root, "manifest.sig");
            AssemblySealGenerator.GenerateReleaseSeal("12.14.0-GM", _hostDir, _proxyDir, manifestPath);

            // 봉인 이후 단 1바이트 변조(위·변조 시뮬레이션).
            File.WriteAllBytes(Path.Combine(_hostDir, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 99 });

            Assert.False(AssemblySealGenerator.VerifyReleaseSeal(manifestPath, _hostDir, _proxyDir));
        }

        [Fact]
        public void VerifyReleaseSeal_MissingManifest_FailsClosed_NoThrow()
            => Assert.False(AssemblySealGenerator.VerifyReleaseSeal(
                Path.Combine(_root, "no-such-manifest.sig"), _hostDir, _proxyDir));

        [Fact]
        public void GenerateReleaseSeal_EmptyArtifactSet_Throws()
        {
            string emptyHost = Path.Combine(_root, "empty_host");
            string emptyProxy = Path.Combine(_root, "empty_proxy");
            Directory.CreateDirectory(emptyHost);
            Directory.CreateDirectory(emptyProxy);
            Assert.Throws<InvalidOperationException>(() =>
                AssemblySealGenerator.GenerateReleaseSeal("12.14.0-GM", emptyHost, emptyProxy,
                    Path.Combine(_root, "empty_manifest.sig")));
        }

        // ══ ColdBootPohRingIntegritySentinel(Think Beyond) ══════════════════════
        [Fact]
        public void ComputeChecksum_Deterministic_ForSameBytes()
        {
            byte[] region = { 10, 20, 30, 40, 50 };
            Assert.Equal(ColdBootPohRingIntegritySentinel.ComputeChecksum(region),
                         ColdBootPohRingIntegritySentinel.ComputeChecksum(region));
        }

        [Fact]
        public void ComputeChecksum_DiffersOnSingleByteFlip()
        {
            byte[] a = { 10, 20, 30, 40, 50 };
            byte[] b = { 10, 20, 30, 40, 51 };
            Assert.NotEqual(ColdBootPohRingIntegritySentinel.ComputeChecksum(a),
                            ColdBootPohRingIntegritySentinel.ComputeChecksum(b));
        }

        [Fact]
        public void Verify_MatchingChecksum_DualKeyAbsent_Passes()
        {
            byte[] region = { 1, 2, 3, 4, 5, 6, 7, 8 };
            ulong expected = ColdBootPohRingIntegritySentinel.ComputeChecksum(region);
            var v = ColdBootPohRingIntegritySentinel.Verify(region, expected, valveWired: false, liveArmingWired: false);
            Assert.True(v.ChecksumMatch);
            Assert.True(v.DualKeyPhysicallyAbsent);
            Assert.True(v.Passed);
        }

        [Fact]
        public void Verify_CorruptedRegion_FailsClosed()
        {
            byte[] region = { 1, 2, 3, 4 };
            ulong expected = ColdBootPohRingIntegritySentinel.ComputeChecksum(region);
            byte[] corrupted = { 1, 2, 3, 99 };
            var v = ColdBootPohRingIntegritySentinel.Verify(corrupted, expected, valveWired: false, liveArmingWired: false);
            Assert.False(v.ChecksumMatch);
            Assert.False(v.Passed);
        }

        [Fact]
        public void Verify_DualKeyAlreadyWiredAtColdBoot_FailsClosed_EvenIfChecksumMatches()
        {
            byte[] region = { 7, 7, 7 };
            ulong expected = ColdBootPohRingIntegritySentinel.ComputeChecksum(region);
            var v = ColdBootPohRingIntegritySentinel.Verify(region, expected, valveWired: true, liveArmingWired: true);
            Assert.True(v.ChecksumMatch);
            Assert.False(v.DualKeyPhysicallyAbsent);
            Assert.False(v.Passed);   // 물리적으로 불가능한 상태(부팅 찰나에 이미 결선) → 거부
        }

        [Fact]
        public void Verify_EmptyRegion_DeterministicNonZeroFnvBasis()
            => Assert.True(ColdBootPohRingIntegritySentinel.ComputeChecksum(ReadOnlySpan<byte>.Empty) != 0UL);
    }
}
