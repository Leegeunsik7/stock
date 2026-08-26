// ============================================================================
// NanoSlaFinalPackagingTests — v10.1.0 Final Release Packaging & Nano SLA 적대적 검증(100% 단언)
//   🛡️ ReleaseArtifactSha256Attestor: 자가-봉인 산출·매니페스트 대조·변조 fail-closed·δ=0 상태 조회
//   🌊 SlaJitterRegressionGuard(Think Beyond): 디스케줄 아티팩트 배제·회귀 래치·결정론·δ=0 Admit 핫패스
//   ⏱️ LatencySlaGatekeeper 융합 교정 검증: 2패스 백분위 일관·버킷 상계 off-by-one 교정
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // δ=0/타이밍 — 전역 직렬(TestCollectionBehavior)과 무관하게 방어적으로 격리
    public sealed class NanoSlaFinalPackagingTests
    {
        // ══ 🛡️ ReleaseArtifactSha256Attestor ════════════════════════════════
        [Fact]
        public void Attestor_SelfSeal_Computed_Unverified_FailClosed()
        {
            var a = new ReleaseArtifactSha256Attestor();   // self = 실행 어셈블리
            Assert.False(string.IsNullOrEmpty(a.SealHex));  // 실행 바이너리 SHA-256 산출(64 hex)
            Assert.Equal(64, a.SealHex.Length);
            Assert.NotEqual(0L, a.SealHex64);
            Assert.False(a.SealValid);                      // ★기대 봉인 미주입 → 미검증(fail-closed)
            Assert.False(a.Verified);
            Assert.True(a.AttestationBlocking);             // 유효 봉인 없으면 릴리스 인증 차단
        }

        [Fact]
        public void Attestor_Attest_Match_And_Mismatch()
        {
            var a = new ReleaseArtifactSha256Attestor();
            Assert.True(a.Attest(a.SealHex));               // 자기 봉인 일치 → 검증 성공
            Assert.True(a.SealValid);
            Assert.True(a.Verified);
            Assert.False(a.AttestationBlocking);

            Assert.False(a.Attest("DEADBEEFDEADBEEF"));     // ★불일치 → 검증 실패(fail-closed)
            Assert.False(a.SealValid);
            Assert.True(a.AttestationBlocking);
        }

        [Fact]
        public void Attestor_AttestArtifacts_Tamper_FailClosed()
        {
            string f = Path.Combine(Path.GetTempPath(), "ms2e_att_" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(f, new byte[] { 1, 2, 3, 4 });
            var paths = new List<string> { f };
            try
            {
                string seal = ReleasePackager.ComputeMasterSealHex(paths);
                var a = new ReleaseArtifactSha256Attestor();
                Assert.True(a.AttestArtifacts(paths, seal));    // 서명 매니페스트 정합 → 성공
                Assert.True(a.SealValid);
                Assert.Equal(1L, a.AttestedArtifacts);

                File.WriteAllBytes(f, new byte[] { 1, 2, 3, 5 });   // 1바이트 변조
                Assert.False(a.AttestArtifacts(paths, seal));   // ★변조 감지 → SealValid=0(fail-closed)
                Assert.False(a.SealValid);
            }
            finally { File.Delete(f); }
        }

        [Fact]
        public void Attestor_ComputeFileSealHex_Deterministic()
        {
            string f = Path.Combine(Path.GetTempPath(), "ms2e_att_" + Guid.NewGuid().ToString("N") + ".bin");
            File.WriteAllBytes(f, new byte[] { 9, 8, 7, 6, 5 });
            try
            {
                string h1 = ReleaseArtifactSha256Attestor.ComputeFileSealHex(f);
                Assert.Equal(64, h1.Length);
                Assert.Equal(h1, ReleaseArtifactSha256Attestor.ComputeFileSealHex(f));   // 결정론
            }
            finally { File.Delete(f); }
        }

        [Fact]
        public void Attestor_RuntimeReads_ZeroAllocation()
        {
            var a = new ReleaseArtifactSha256Attestor();
            long acc = 0;
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) { if (a.SealValid) acc++; acc += a.SealHex64; if (a.Verified) acc++; }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★런타임 상태 조회 핫패스 힙 0(콜드 산출 결과 캐시 읽기)
            Assert.True(acc >= long.MinValue);   // keep-alive(JIT 제거 방지)·오버플로 무관(SealHex64 누적이 랩해도 항상 참)
        }

        // ══ 🌊 SlaJitterRegressionGuard (Think Beyond) ═════════════════════
        [Fact]
        public void Jitter_Admits_Clean_Rejects_Deschedule_And_BackwardClock()
        {
            var g = new SlaJitterRegressionGuard(absDescheduleCeilingNanos: 50_000L);
            for (int i = 0; i < 1_000; i++) Assert.True(g.Admit(2_000L));   // 2µs 정상 → baseline 확립·admit
            Assert.True(g.Admitted >= 1_000L);
            Assert.Equal(0L, g.JitterRejects);

            Assert.False(g.Admit(0L));            // ★역행/영 시계 → 배제
            Assert.False(g.Admit(-5L));           // ★음수 → 배제
            Assert.False(g.Admit(100_000_000L));  // ★100ms ≫ 천장 → 디스케줄 아티팩트 배제
            Assert.True(g.Admit(3_000L));         // 정상 → admit
            Assert.True(g.JitterRejects >= 3L);
        }

        [Fact]
        public void Jitter_Regression_Trips_On_Sustained_Jitter_But_Not_On_Clean()
        {
            // ★클린 스트림 → 회귀 없음.
            var clean = new SlaJitterRegressionGuard(absDescheduleCeilingNanos: 50_000L, regressionThresholdPermille: 100L, warmupSamples: 10L);
            for (int i = 0; i < 5_000; i++) clean.Admit(1_000L + (i % 100));
            Assert.False(clean.RegressionTripped);
            Assert.Equal(0L, clean.JitterRejects);

            // ★지속 디스케줄 아티팩트 → 지터율 회귀 래치(측정 신뢰불가 → 인증/무장 차단).
            var noisy = new SlaJitterRegressionGuard(absDescheduleCeilingNanos: 1_000L, regressionThresholdPermille: 100L, warmupSamples: 10L);
            for (int i = 0; i < 5_000; i++) noisy.Admit(10_000_000L);   // 10ms ≫ 1µs 천장 → 전부 배제
            Assert.True(noisy.RegressionTripped);
            Assert.True(noisy.RegressionBlocking);
            Assert.True(noisy.JitterRejects > 0L);
        }

        [Fact]
        public void Jitter_Regression_Trips_On_Moderate_Jitter_QuantizationFixed()
        {
            // ★양자화 바이어스 교정 회귀: 20% 지속 배제(200‰·임계 100‰의 2배) → 회귀 래치.
            //   교정 전 정수 EWMA(>>8)는 reject 증분이 3(≈3.9 손실)로 캡되어 reject율>25%에서만 상승 → 200‰가 미트립(fail-open) 했다.
            var g = new SlaJitterRegressionGuard(absDescheduleCeilingNanos: 1_000L, regressionThresholdPermille: 100L, warmupSamples: 10L);
            for (int i = 0; i < 20_000; i++) g.Admit((i % 5 == 0) ? 10_000_000L : 500L);   // 20% 디스케줄
            Assert.True(g.RegressionTripped);              // ★200‰ > 100‰ → 트립(교정 전엔 미트립)
            Assert.True(g.JitterRatePermille >= 100L);     // 지터율 정확 추종(~200‰·교정 전엔 ~3‰)

            // 5% 배제(50‰·임계 미만) → 회귀 없음(오탐 방지).
            var q = new SlaJitterRegressionGuard(absDescheduleCeilingNanos: 1_000L, regressionThresholdPermille: 100L, warmupSamples: 10L);
            for (int i = 0; i < 20_000; i++) q.Admit((i % 20 == 0) ? 10_000_000L : 500L);   // 5% 디스케줄
            Assert.False(q.RegressionTripped);
            Assert.True(q.JitterRatePermille < 100L);
        }

        [Fact]
        public void Jitter_Deterministic()
        {
            var a = new SlaJitterRegressionGuard();
            var b = new SlaJitterRegressionGuard();
            for (int i = 0; i < 10_000; i++)
            {
                long s = 500L + (i * 37L % 2_000L);
                Assert.Equal(a.Admit(s), b.Admit(s));   // 동일 입력 → 동일 판정(순수 정수·RNG/벽시계 무참조)
            }
            Assert.Equal(a.Admitted, b.Admitted);
            Assert.Equal(a.JitterRejects, b.JitterRejects);
            Assert.Equal(a.BaselineNanos, b.BaselineNanos);
            Assert.Equal(a.JitterRatePermille, b.JitterRatePermille);
        }

        [Fact]
        public void Jitter_Admit_ZeroAllocation()
        {
            var g = new SlaJitterRegressionGuard();
            for (int i = 0; i < 50_000; i++) g.Admit(1_000L + (i & 255));   // 워밍업
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) g.Admit(1_000L + (i & 255));
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★Admit 핫패스 힙 0(Int128 값타입·CAS·Interlocked)
        }

        // ══ ⏱️ LatencySlaGatekeeper 융합 교정 검증 ══════════════════════════
        [Fact]
        public void Sla_TwoPassPercentile_And_BucketBound_Consistent()
        {
            var g = new LatencySlaGatekeeper(slaP9999Nanos: 1_000_000L);
            for (int i = 0; i < 100_000; i++) g.Record(64L);
            for (int i = 0; i < 100; i++)     g.Record(20_000L);   // 0.1% 꼬리
            Assert.Equal(100_100L, g.Count);
            Assert.True(g.Percentile(0.50) >= 32L && g.Percentile(0.50) <= 128L);   // 본체 밴드
            Assert.True(g.Percentile(0.9999) >= 16_384L);                            // ★꼬리 포착(2패스 일관)

            // ★off-by-one 교정: 소값 버킷 상계가 하계로 과소보고되지 않음(v∈{1,2,3} 상계 ≥ 값).
            var s = new LatencySlaGatekeeper(slaP9999Nanos: 1_000_000L);
            s.Record(3L);
            Assert.True(s.Percentile(1.0) >= 3L);   // 상계가 3 이상(교정 전엔 2 로 과소보고)
        }

        [Fact]
        public void Sla_Percentile_CeilRank_TailBreach_Trips_FailClosed()
        {
            // ★floor→ceil 랭크 교정: 꼬리 1건이 버킷 경계를 넘는 정확한 케이스(모듈 존재 이유) — floor 랭크면 미트립(fail-open) 이었음.
            var att = new OrderAttestationService();
            var g = new LatencySlaGatekeeper(slaP9999Nanos: 100L, attestation: att);
            for (int i = 0; i < 4_999; i++) g.Record(50L);   // 본체 50ns
            g.Record(200L);                                   // 꼬리 1건 @200ns — P99.99 = ceil(0.9999×5000)=5000 → 200ns
            Assert.False(g.EvaluateSla());                    // ★실 꼬리 이탈 → 트립(floor 랭크면 55ns 반환해 미트립)
            Assert.True(g.IsBreached);
            Assert.True(g.LastP9999Nanos > 100L);
        }
    }
}
