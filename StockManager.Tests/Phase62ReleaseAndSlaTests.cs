// ============================================================================
// Phase62ReleaseAndSlaTests — v6.2.0 적대적 검증
//   🦅 LatencySlaGatekeeper: 백분위 근사·P99.99 SLA 비가역 트립·δ=0 기록
//   📦 ReleasePackager: SHA-256 매니페스트·제네시스 봉인·변조 감지·결정론
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase62ReleaseAndSlaTests
    {
        // ══ 🦅 LatencySlaGatekeeper ══════════════════════════════════════════
        [Fact]
        public void Sla_Percentiles_ApproximatelyCorrect()
        {
            var g = new LatencySlaGatekeeper(slaP9999Nanos: 1_000_000);   // 트립 안 되게 높게
            for (int i = 0; i < 999_000; i++) g.Record(50);              // 본체 50ns
            for (int i = 0; i < 1_000; i++)   g.Record(8_000);           // 0.1% 꼬리 8µs

            Assert.Equal(1_000_000L, g.Count);
            Assert.InRange(g.Percentile(0.50), 32, 64);      // P50 ≈ 50ns 밴드
            Assert.InRange(g.Percentile(0.99), 32, 64);      // P99 도 본체(꼬리 0.1%)
            Assert.InRange(g.Percentile(0.9999), 4_096, 8_192);   // ★P99.99 는 8µs 꼬리를 포착
            Assert.Equal(8_000L, g.MaxNanos);
            Assert.InRange(g.MeanNanos, 50, 100);
        }

        [Fact]
        public void Sla_Breach_TripsIrreversibly_ForceFreeze()
        {
            var att = new OrderAttestationService();
            var g = new LatencySlaGatekeeper(slaP9999Nanos: 200, attestation: att);
            for (int i = 0; i < 999_000; i++) g.Record(50);
            for (int i = 0; i < 1_000; i++)   g.Record(8_000);   // P99.99 ≈ 8µs ≫ 200ns

            Assert.True(g.IsWithinSla);                 // 아직 미평가
            Assert.False(g.EvaluateSla());              // ★SLA 이탈 → 트립
            Assert.True(g.IsBreached);
            Assert.False(g.IsWithinSla);                // 발주 게이트 차단
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // fail-closed 봉인
            Assert.True(g.LastP9999Nanos > 200);

            // ★비가역: 이후 평가에도 봉인 유지(자동복구 없음).
            Assert.False(g.EvaluateSla());
            Assert.True(g.IsBreached);
        }

        [Fact]
        public void Sla_WithinBudget_NoTrip()
        {
            var g = new LatencySlaGatekeeper(slaP9999Nanos: 200);
            for (int i = 0; i < 1_000_000; i++) g.Record(50);   // 전량 본체
            Assert.True(g.EvaluateSla());       // SLA 이내
            Assert.False(g.IsBreached);
            Assert.True(g.IsWithinSla);
        }

        [Fact]
        public void Sla_Record_ZeroAllocation()
        {
            var g = new LatencySlaGatekeeper(1_000_000);
            for (int i = 0; i < 50_000; i++) g.Record(50 + (i & 63));   // 워밍업

            long Measure()   // Double-Measure: 1차 웜업(OSR 흡수), 2차 안정 측정
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) g.Record(50 + (i & 63));
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // 기록 핫패스 힙 0
        }

        // ══ 📦 ReleasePackager ═══════════════════════════════════════════════
        private static string TempFile(byte[] content)
        {
            string p = Path.Combine(Path.GetTempPath(), "ms2e_rel_" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(p, content);
            return p;
        }

        [Fact]
        public void Packager_Manifest_HashesAndSeal_GenesisBound()
        {
            string a = TempFile(new byte[] { 1, 2, 3, 4, 5 });
            string b = TempFile(new byte[] { 9, 8, 7 });
            var paths = new List<string> { a, b };
            try
            {
                string json = ReleasePackager.GenerateManifestJson("6.2.0", paths);

                Assert.Contains("\"version\": \"6.2.0\"", json);
                Assert.Contains("MS2E.StockSuite", json);

                // 제네시스 헤드 해시(결정론) 가 매니페스트에 봉인됨.
                string genesisHex = Convert.ToHexString(ReleasePackager.GenesisHeadHash());
                Assert.Contains(genesisHex, json);

                // 각 아티팩트 SHA-256 이 정확.
                Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a))), json);
                Assert.Contains(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(b))), json);

                // masterSeal 정합 + 검증 API 통과.
                string seal = ReleasePackager.ComputeMasterSealHex(paths);
                Assert.Contains(seal, json);
                Assert.True(ReleasePackager.VerifyArtifacts(paths, seal));
            }
            finally { File.Delete(a); File.Delete(b); }
        }

        [Fact]
        public void Packager_Tamper_ChangesSeal_VerifyFails()
        {
            string a = TempFile(new byte[] { 10, 20, 30 });
            var paths = new List<string> { a };
            try
            {
                string seal = ReleasePackager.ComputeMasterSealHex(paths);
                Assert.True(ReleasePackager.VerifyArtifacts(paths, seal));

                File.WriteAllBytes(a, new byte[] { 10, 20, 31 });   // 1바이트 변조
                Assert.False(ReleasePackager.VerifyArtifacts(paths, seal));   // ★변조 감지
                Assert.NotEqual(seal, ReleasePackager.ComputeMasterSealHex(paths));
            }
            finally { File.Delete(a); }
        }

        [Fact]
        public void Packager_Deterministic_And_GenesisStable()
        {
            string a = TempFile(new byte[] { 42, 42, 42, 42 });
            var paths = new List<string> { a };
            try
            {
                Assert.Equal(ReleasePackager.ComputeMasterSealHex(paths), ReleasePackager.ComputeMasterSealHex(paths));
                Assert.Equal(ReleasePackager.GenerateManifestJson("6.2.0", paths), ReleasePackager.GenerateManifestJson("6.2.0", paths));
                Assert.Equal(Convert.ToHexString(ReleasePackager.GenesisHeadHash()), Convert.ToHexString(ReleasePackager.GenesisHeadHash()));
            }
            finally { File.Delete(a); }
        }
    }
}
