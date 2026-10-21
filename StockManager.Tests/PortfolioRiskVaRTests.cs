// ============================================================================
// PortfolioRiskVaRTests — v7.2.0 적대적 검증(100% 단언)
//   📊 PortfolioRiskVaREngine: parametric/historical/MonteCarlo/stress VaR · δ=0 · 오라클 정합
//   🛡️ CovarianceSingularityGuard: Gershgorin 하한 · 최소 적응형 리지 PD 보장 · NaN fail-closed
//   🔒 liveArmed:false: 리스크 밸브 tighten-only(발주 경로 무접촉)
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public sealed class PortfolioRiskVaRTests
    {
        private const double Z95 = 1.6448536269514722;
        private const double Z99 = 2.3263478740408408;

        // 3자산·6일 — 활성 구간이 겹치지 않아 공분산이 대각(=10·I)·완전 조건화(오라클 검증 용이).
        private static double[][] DiagReturns() => new[]
        {
            new[]{ 5.0, -5.0,  0.0,  0.0,  0.0,  0.0 },
            new[]{ 0.0,  0.0,  5.0, -5.0,  0.0,  0.0 },
            new[]{ 0.0,  0.0,  0.0,  0.0,  5.0, -5.0 },
        };
        private static readonly double[] EqualW = { 1.0 / 3, 1.0 / 3, 1.0 / 3 };

        // ── 1) Parametric σ_p = √(wᵀΣw) 가 MarkowitzOptimizer.Covariance 오라클과 정합(리지 0·무-왜곡) ──
        [Fact]
        public void Parametric_MatchesCovarianceOracle_NoRidge()
        {
            var R = DiagReturns();
            var covO = MarkowitzOptimizer.Covariance(R);
            double varO = 0;
            for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) varO += EqualW[i] * EqualW[j] * covO[i][j];
            double sigmaO = Math.Sqrt(varO);

            var eng = new PortfolioRiskVaREngine();
            Assert.True(eng.LoadReturns(R, EqualW));
            var r = eng.ComputeVar(100_000_000m, 0, StressScenario.Crash10(0));

            Assert.Equal(0.0, r.RidgeApplied);                    // 완전 조건화 → 무-개입(무-왜곡)
            Assert.Equal((int)CovGuardState.Nominal, r.CovGuardStateCode);
            Assert.Equal(sigmaO, r.SigmaDailyPct, 9);             // ★공분산·wᵀΣw 오라클 정합
            Assert.False(r.Conservative);
        }

        // ── 2) Parametric VaR = z·σ_p, 원화 환산 정합 ──
        [Fact]
        public void Parametric_ZScaling_And_KrwConversion()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);
            decimal value = 100_000_000m;
            var r = eng.ComputeVar(value, 0, StressScenario.Crash10(0));

            Assert.Equal(Z95 * r.SigmaDailyPct, r.Var95Pct, 12);
            Assert.Equal(Z99 * r.SigmaDailyPct, r.Var99Pct, 12);
            Assert.True(r.Var99Pct > r.Var95Pct);                            // 99% > 95%
            Assert.Equal((long)Math.Round(r.Var95Pct / 100.0 * (double)value), r.Var95Krw);
            Assert.Equal((long)Math.Round(r.Var99Pct / 100.0 * (double)value), r.Var99Krw);
        }

        // ── 3) Historical VaR = 경험 포트 수익률 분포의 5%/1% 손실 분위수 ──
        [Fact]
        public void Historical_Var_FromEmpiricalDistribution()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);
            var r = eng.ComputeVar(100_000_000m, 0, StressScenario.Crash10(0));
            // 포트 일수익률 = ±5/3 교대 → 손실 꼬리 = 5/3 ≈ 1.6667%
            Assert.Equal(5.0 / 3.0, r.HistVar95Pct, 6);
            Assert.Equal(5.0 / 3.0, r.HistVar99Pct, 6);
        }

        // ── 4) Gershgorin 최소 고유값 하한: min_i(a_ii − Σ_{j≠i}|a_ij|) ──
        [Fact]
        public void Gershgorin_LowerBound_Correct()
        {
            double[] diagDom = { 10, 1, 1, 1, 10, 1, 1, 1, 10 };   // 대각우세 → 하한 10-2=8
            Assert.Equal(8.0, CovarianceSingularityGuard.GershgorinLowerBound(diagDom, 3), 9);

            double[] npd = { 1, 2, 2, 1 };                          // 고유값 3,-1(NPD) → 하한 1-2=-1
            Assert.Equal(-1.0, CovarianceSingularityGuard.GershgorinLowerBound(npd, 2), 9);

            double[] nan = { 1, double.NaN, double.NaN, 1 };
            Assert.True(double.IsNaN(CovarianceSingularityGuard.GershgorinLowerBound(nan, 2)));
        }

        // ── 5) ★Think Beyond: NPD 행렬에 '최소' 적응형 리지 주입 → PD 복원(하한 정확히 ε) ──
        [Fact]
        public void AdaptiveRidge_NpdMatrix_RestoresPD_Minimally()
        {
            const double eps = 1e-9;
            double[] cov = { 1, 2, 2, 1 };                          // NPD(고유값 3,-1)
            var g = new CovarianceSingularityGuard(eps);

            Assert.Equal(-1.0, CovarianceSingularityGuard.GershgorinLowerBound(cov, 2), 9);
            Assert.True(g.Regularize(cov, 2));                      // PD 사용가능
            Assert.Equal(CovGuardState.Regularized, g.State);
            Assert.Equal(1L, g.RidgeInjections);
            Assert.Equal(eps - (-1.0), g.LastRidge, 12);            // ★최소 로딩 λ = ε − 하한(정확)

            double after = CovarianceSingularityGuard.GershgorinLowerBound(cov, 2);
            Assert.Equal(eps, after, 15);                          // ★하한을 '정확히 ε'로(과-정규화 없음·PD 보장)
            Assert.True(after > 0);
            // PD 검증: 대각 2+ε, 고유값 (2+ε)±2 = {4+ε, ε} 모두 양수.
            Assert.Equal(2.0 + eps, cov[0], 12);
        }

        // ── 6) NaN/Inf(데이터 파열) → 퓨즈 트립 + 엔진 무상관 보수 모델 fail-closed ──
        [Fact]
        public void Guard_NaN_TripsFuse_And_Engine_ConservativeFailClosed()
        {
            var att = new OrderAttestationService();
            var g = new CovarianceSingularityGuard(1e-9, att);
            double[] bad = { 1, double.NaN, double.NaN, 1 };
            Assert.False(g.Regularize(bad, 2));                     // 퓨즈 → 사용 불가
            Assert.True(g.FuseTripped);
            Assert.Equal(CovGuardState.FuseTripped, g.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);     // 이중 봉인 착지

            // 엔진: NaN 수익률 → 공분산 NaN → 보수(무상관) VaR + 밸브 조임.
            var R = new[] { new[]{ 1.0, double.NaN, 2.0, -1.0, 0.5 }, new[]{ 0.5, 1.0, -1.0, 0.5, 0.2 } };
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(R, new[]{ 0.5, 0.5 });
            var r = eng.ComputeVar(100_000_000m, 1_000_000, StressScenario.Crash10(0));
            Assert.True(r.Conservative);
            Assert.Equal((int)CovGuardState.FuseTripped, r.CovGuardStateCode);
            Assert.True(r.Var99Pct > 0);                           // 보수 σ 산출
            Assert.True(r.LimitBreached);                          // ★보수 → fail-closed 밸브 조임
        }

        // ── 7) ComputeVar 핫패스 δ=0(공분산 가드+parametric+historical+stress 힙 0) ──
        [Fact]
        public void ComputeVar_ZeroAllocation_HotPath()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);
            var stress = StressScenario.Crash10(0.3);
            for (int i = 0; i < 10_000; i++) eng.ComputeVar(100_000_000m, 5_000_000, stress);   // 워밍업

            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) eng.ComputeVar(100_000_000m, 5_000_000, stress);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();                        // OSR/tier-up 흡수
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));          // ★VaR 스캔 힙 0바이트
        }

        // ── 8) Monte Carlo VaR: 결정론(동일 시드 동일 결과) + 핫패스 δ=0 ──
        [Fact]
        public void MonteCarloVar_Deterministic_And_ZeroAllocation()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);

            var (a95, a99) = eng.MonteCarloVarPct(4000, seed: 42);
            var (b95, b99) = eng.MonteCarloVarPct(4000, seed: 42);
            Assert.Equal(a95, b95, 12);           // ★결정론(재현)
            Assert.Equal(a99, b99, 12);
            Assert.True(a99 >= a95);              // 99% ≥ 95%
            Assert.True(a95 > 0);

            eng.MonteCarloVarPct(2000, 7);        // 워밍업(_mc 성장 + tier-up)
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 50; i++) eng.MonteCarloVarPct(2000, 7);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));          // ★MC 시뮬 힙 0(사전할당 _mc/_z/_chol 재사용)
        }

        // ── 9) 리스크 밸브: VaR/스트레스 한도 초과 시 LimitBreached, 여유 시 해제 ──
        [Fact]
        public void RiskValve_BreachAndClear()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);

            eng.ComputeVar(100_000_000m, 1_000.0, StressScenario.Crash10(0.3));   // 극소 한도 → 초과
            Assert.True(eng.LimitBreached);

            eng.ComputeVar(100_000_000m, 5_000_000_000L, StressScenario.Crash10(0.3));   // 거대 한도 → 여유
            Assert.False(eng.LimitBreached);
        }

        // ── 10) 🔒 liveArmed:false — 밸브는 blockWhile 1항으로 '조이기만'(발주 유발 경로 부재) ──
        [Fact]
        public void LiveArmedFalse_ValveTightensOnly()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);

            var att = new OrderAttestationService();
            var ign = new IgnitionLock(attestation: att);
            var gate = new LiveArmingGate(ign, att, brokerArmed: () => true);   // 점화 Locked → IsArmed()=false(기본 미무장)

            // 호스트가 쓰는 '바로 그' 팩토리로 밸브를 blockWhile 에 배선.
            var pred = DispatchArming.ResolvePredicate(wired: true, gate, blockWhile: () => eng.LimitBreached);

            // 밸브 걸림(한도 초과) → 무조건 미무장(발주 불가).
            eng.ComputeVar(100_000_000m, 1_000.0, StressScenario.Crash10(0.3));
            Assert.True(eng.LimitBreached);
            Assert.False(pred());

            // 밸브 미걸림 → 밸브가 gate 를 '느슨하게' 만들지 않음(pred ⇒ gate.IsArmed 함의 보존).
            eng.ComputeVar(100_000_000m, 5_000_000_000L, StressScenario.Crash10(0.3));
            Assert.False(eng.LimitBreached);
            Assert.Equal(gate.IsArmed(), pred());     // 밸브 무영향 시 gate 판정 그대로
            Assert.False(pred());                     // (기본 미무장이므로 여전히 false — 밸브가 발주를 '유발'하지 않음)

            // 미배선(wired:false) = 수학적 0(완전 격리).
            Assert.False(DispatchArming.ResolvePredicate(false, gate, () => eng.LimitBreached)());
        }

        // ── 11) 스트레스 시나리오: 지수·환율·금리 복합 불리 낙폭 합성 ──
        [Fact]
        public void Stress_Crash10_LossComposition()
        {
            var eng = new PortfolioRiskVaREngine();
            eng.LoadReturns(DiagReturns(), EqualW);
            var stress = StressScenario.Crash10(0.26);   // 지수 -10 + 환율 7×0.26 + 금리 3
            double expectedPct = 10.0 + 7.0 * 0.26 + 3.0;
            Assert.Equal(expectedPct, stress.LossPct, 9);

            var r = eng.ComputeVar(100_000_000m, 0, stress);
            Assert.Equal(expectedPct, r.StressLossPct, 9);
            Assert.Equal((long)Math.Round(expectedPct / 100.0 * 100_000_000.0), r.StressLossKrw);
        }
    }
}
