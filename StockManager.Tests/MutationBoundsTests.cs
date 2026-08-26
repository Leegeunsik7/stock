// ============================================================================
// MutationBoundsTests — S19 #2: 유전자 방화벽 순수 코어 검증 (38개)
//
//  §1 MutationBoundsMatrix (20) — B1 이평역전·B2 주기클램프·B3 손익비
//      ·B4 복잡도상한·B5 중복/모순·Ok 판정·결정론·B6 표본하한
//  §2 FitnessEvaluator (18)     — 보상 정규화(P/W/O)·거부 게이트(R/G/N/OOS)
//      ·곱셈 소멸·가중합·결정론·커스텀 경계
// ============================================================================
using System.Collections.Generic;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MutationBoundsTests
    {
        // ── 빌더 헬퍼 ─────────────────────────────────────────────────────────────
        private static SpecTerm Term(SpecKind k, int p) => new SpecTerm(k, p);

        private static SpecCond MaPair(int shortP, int longP, SpecOp op = SpecOp.CrossAbove,
            SpecKind lk = SpecKind.Sma, SpecKind rk = SpecKind.Sma)
            => new SpecCond(Term(lk, shortP), op, false, 0.0, Term(rk, longP));

        private static SpecCond ConstCond(SpecKind k, int p, SpecOp op, double konst)
            => new SpecCond(Term(k, p), op, true, konst, null);

        private static StrategySpec Spec(
            List<SpecCond> entry, List<SpecCond>? exit = null,
            double sl = 0, double tp = 0,
            SpecLogic enLogic = SpecLogic.And, SpecLogic exLogic = SpecLogic.And)
            => new StrategySpec("", "테스트", entry, enLogic,
                                exit ?? new List<SpecCond>(), exLogic, sl, tp);

        // ════════════════════════ §1 MutationBoundsMatrix ════════════════════════

        [Fact]
        public void B1_MaInversion_SwapsToShortLeft()
        {
            // SMA20 CrossAbove SMA5 (역전) → 단기(5)=Left, 장기(20)=Right
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { MaPair(20, 5) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(5,  c.Left.Param);
            Assert.Equal(20, c.Right!.Param);
            Assert.True(r.RepairCount >= 1);
        }

        [Fact]
        public void B1_AlreadyCanonical_NoSwap()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { MaPair(5, 20) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(5,  c.Left.Param);
            Assert.Equal(20, c.Right!.Param);
        }

        [Fact]
        public void B1_EqualPeriods_Separated()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { MaPair(10, 10) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(10, c.Left.Param);
            Assert.Equal(11, c.Right!.Param);   // 자기교차 → 분리
        }

        [Fact]
        public void B1_EqualPeriods_AtMax_SeparatesDownward()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { MaPair(200, 200) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(199, c.Left.Param);    // 상한이라 아래로 분리
            Assert.Equal(200, c.Right!.Param);
        }

        [Fact]
        public void B1_CrossKind_EmaVsSma_Normalized()
        {
            // EMA50 vs SMA10 → 단기(10)=Left, 장기(50)=Right (종류는 보존)
            var r = MutationBoundsMatrix.Reconcile(Spec(new()
                { MaPair(50, 10, SpecOp.CrossAbove, SpecKind.Ema, SpecKind.Sma) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(10, c.Left.Param);
            Assert.Equal(SpecKind.Ema, c.Left.Kind);
            Assert.Equal(50, c.Right!.Param);
            Assert.Equal(SpecKind.Sma, c.Right.Kind);
        }

        [Fact]
        public void B1_NotApplied_WhenRightIsConst()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { ConstCond(SpecKind.Sma, 20, SpecOp.Gt, 5) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(20, c.Left.Param);     // 상수 비교 → B1 미적용
            Assert.True(c.RightIsConst);
        }

        [Fact]
        public void B1_NotApplied_RsiVsRsi()
        {
            // RSI 는 이평이 아님 → 역전 정준화 미적용
            var r = MutationBoundsMatrix.Reconcile(Spec(new()
                { MaPair(20, 5, SpecOp.Gt, SpecKind.Rsi, SpecKind.Rsi) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(20, c.Left.Param);
            Assert.Equal(5,  c.Right!.Param);
        }

        [Fact]
        public void B2_PeriodBelowFloor_ClampedUp()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { ConstCond(SpecKind.Sma, 1, SpecOp.Gt, 5) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(3, c.Left.Param);      // MinEffectivePeriod=3
        }

        [Fact]
        public void B2_PeriodAboveMax_ClampedDown()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { ConstCond(SpecKind.Rsi, 500, SpecOp.Gt, 5) }));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(200, c.Left.Param);    // MaxPeriod=200
        }

        [Fact]
        public void B2_PriceAndMacdHist_Exempt()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new()
            {
                ConstCond(SpecKind.Price,    0, SpecOp.Gt, 0),
                ConstCond(SpecKind.MacdHist, 0, SpecOp.Lt, 0),
            }));
            Assert.Equal(2, r.Repaired.Entry.Count);
            Assert.Equal(0, r.Repaired.Entry[0].Left.Param);   // 주기 무의미 → 클램프 안 함
            Assert.Equal(0, r.Repaired.Entry[1].Left.Param);
        }

        [Fact]
        public void B3_TakeProfitBelowRR_RaisedToMinRR()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30) }, sl: 10, tp: 5));
            Assert.Equal(10.0, r.Repaired.TakeProfitPct, 6);   // RR=1 → TP ≥ SL
            Assert.Equal(10.0, r.Repaired.StopLossPct, 6);
        }

        [Fact]
        public void B3_StopLossZero_RrGuardSkipped()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30) }, sl: 0, tp: 5));
            Assert.Equal(5.0, r.Repaired.TakeProfitPct, 6);    // SL=0 → RR 가드 생략
        }

        [Fact]
        public void B3_OutOfRange_Clamped()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30) }, sl: 80, tp: 150));
            Assert.Equal(50.0,  r.Repaired.StopLossPct, 6);    // [0,50]
            Assert.Equal(100.0, r.Repaired.TakeProfitPct, 6);  // [0,100]
        }

        [Fact]
        public void B4_TooManyConditions_CappedPerSide()
        {
            var entry = new List<SpecCond>
            {
                ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30),
                ConstCond(SpecKind.Rsi, 21, SpecOp.Lt, 35),
                ConstCond(SpecKind.Rsi, 28, SpecOp.Lt, 40),
                ConstCond(SpecKind.Sma, 50, SpecOp.Gt, 10),
                ConstCond(SpecKind.Ema, 60, SpecOp.Gt, 20),
            };
            var r = MutationBoundsMatrix.Reconcile(Spec(entry));
            Assert.Equal(3, r.Repaired.Entry.Count);           // MaxConditionsPerSide=3
        }

        [Fact]
        public void B5_ExactDuplicates_Removed()
        {
            var entry = new List<SpecCond>
            {
                ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30),
                ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30),   // 완전 중복
            };
            var r = MutationBoundsMatrix.Reconcile(Spec(entry));
            Assert.Single(r.Repaired.Entry);
        }

        [Fact]
        public void B5_Contradiction_AndLogic_EmptyInterval_Removed()
        {
            // RSI14 > 50 AND RSI14 < 30 → 공집합 → Lt 제거(Gt 보존)
            var entry = new List<SpecCond>
            {
                ConstCond(SpecKind.Rsi, 14, SpecOp.Gt, 50),
                ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30),
            };
            var r = MutationBoundsMatrix.Reconcile(Spec(entry, enLogic: SpecLogic.And));
            var c = Assert.Single(r.Repaired.Entry);
            Assert.Equal(SpecOp.Gt, c.Op);
        }

        [Fact]
        public void B5_Contradiction_OrLogic_Kept()
        {
            // OR 이면 X>50 OR X<30 은 모순이 아님 → 둘 다 보존
            var entry = new List<SpecCond>
            {
                ConstCond(SpecKind.Rsi, 14, SpecOp.Gt, 50),
                ConstCond(SpecKind.Rsi, 14, SpecOp.Lt, 30),
            };
            var r = MutationBoundsMatrix.Reconcile(Spec(entry, enLogic: SpecLogic.Or));
            Assert.Equal(2, r.Repaired.Entry.Count);
        }

        [Fact]
        public void Reconcile_EmptyEntry_OkFalse()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new List<SpecCond>()));
            Assert.False(r.Ok);
            Assert.Contains("진입 조건 없음", r.Reason);
        }

        [Fact]
        public void Reconcile_CleanSpec_NoRepair_OkTrue()
        {
            var r = MutationBoundsMatrix.Reconcile(Spec(new() { MaPair(5, 20) }, sl: 5, tp: 10));
            Assert.True(r.Ok);
            Assert.Equal(0, r.RepairCount);
            Assert.Equal("무변경", r.Reason);
        }

        [Fact]
        public void Reconcile_Deterministic_SameInputSameOutput()
        {
            var input = Spec(new() { MaPair(20, 5) }, sl: 3, tp: 1);
            var a = MutationBoundsMatrix.Reconcile(input);
            var b = MutationBoundsMatrix.Reconcile(input);
            Assert.Equal(a.Repaired.Id, b.Repaired.Id);
            Assert.Equal(a.Repaired.Entry[0].Left.Param, b.Repaired.Entry[0].Left.Param);
            Assert.Equal(a.Reason, b.Reason);
        }

        [Fact]
        public void B6_CanEvaluate_RespectsCandleFloor()
        {
            Assert.False(MutationBoundsMatrix.CanEvaluate(59, warmupNeeded: 10));   // < 60
            Assert.True(MutationBoundsMatrix.CanEvaluate(60, warmupNeeded: 10));
            Assert.False(MutationBoundsMatrix.CanEvaluate(60, warmupNeeded: 80));   // 워밍업 부족
        }

        // ════════════════════════ §2 FitnessEvaluator ════════════════════════════

        private static BacktestSummary BT(double sharpe, double mddPct, double winPct, int trades)
            => new BacktestSummary
            {
                Sharpe = (decimal)sharpe, MaxDrawdown = (decimal)mddPct,
                WinRate = (decimal)winPct, TradeCount = trades,
            };

        [Fact]
        public void Score_StrongStrategy_HighFitness()
        {
            var v = FitnessEvaluator.Score(BT(2, 10, 70, 40), meanAbsZ: 0.2, exposure: 0.5);
            Assert.True(v.F > 0.5, $"F={v.F}");
            Assert.True(v.F < 1.0);
        }

        [Fact]
        public void Score_DeepDrawdown_GateRZerosFitness()
        {
            // 샤프가 높아도 80% 낙폭이면 gate_R=0 → F=0
            var v = FitnessEvaluator.Score(BT(3, 80, 80, 50), meanAbsZ: 0, exposure: 0.5);
            Assert.Equal(0.0, v.GateR, 6);
            Assert.Equal(0.0, v.F, 6);
        }

        [Fact]
        public void Score_HighFriction_GateGFloors()
        {
            var v = FitnessEvaluator.Score(BT(2, 10, 70, 40), meanAbsZ: 10.0, exposure: 0.5);
            Assert.Equal(0.0001, v.GateG, 6);   // 마찰 게이트 하한(ε)
            Assert.True(v.F < 0.01);
        }

        [Fact]
        public void Score_UnderTraded_GateNPenalty()
        {
            var v = FitnessEvaluator.Score(BT(2, 0, 70, 5), meanAbsZ: 0, exposure: 0.5);
            Assert.Equal(0.25, v.GateN, 6);     // 5/20
        }

        [Fact]
        public void Score_ZeroTrades_FitnessZero()
        {
            var v = FitnessEvaluator.Score(BT(2, 0, 70, 0), meanAbsZ: 0, exposure: 0.5);
            Assert.Equal(0.0, v.GateN, 6);
            Assert.Equal(0.0, v.F, 6);
        }

        [Fact]
        public void P_Normalization_Linear()
        {
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(0, 0, 70, 40), 0, 0.5).P, 6);
            Assert.Equal(0.5, FitnessEvaluator.Score(BT(1, 0, 70, 40), 0, 0.5).P, 6);
            Assert.Equal(1.0, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 0.5).P, 6);
        }

        [Fact]
        public void P_Clamp_NegativeAndOver()
        {
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(-1, 0, 70, 40), 0, 0.5).P, 6);
            Assert.Equal(1.0, FitnessEvaluator.Score(BT(5,  0, 70, 40), 0, 0.5).P, 6);
        }

        [Fact]
        public void W_Normalization()
        {
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(2, 0, 40, 40), 0, 0.5).W, 6);
            Assert.Equal(0.5, FitnessEvaluator.Score(BT(2, 0, 55, 40), 0, 0.5).W, 6);
            Assert.Equal(1.0, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 0.5).W, 6);
        }

        [Fact]
        public void O_ExposureTent()
        {
            Assert.Equal(1.0, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 0.5).O,  6);
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 0.0).O,  6);
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 1.0).O,  6);
            Assert.Equal(0.5, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 0.25).O, 6);
        }

        [Fact]
        public void GateR_Linear()
        {
            Assert.Equal(1.0, FitnessEvaluator.Score(BT(2, 0,  70, 40), 0, 0.5).GateR, 6);
            Assert.Equal(0.5, FitnessEvaluator.Score(BT(2, 20, 70, 40), 0, 0.5).GateR, 6);
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(2, 40, 70, 40), 0, 0.5).GateR, 6);
            Assert.Equal(0.0, FitnessEvaluator.Score(BT(2, 60, 70, 40), 0, 0.5).GateR, 6);
        }

        [Fact]
        public void GateN_Clamp()
        {
            Assert.Equal(0.5, FitnessEvaluator.Score(BT(2, 0, 70, 10), 0, 0.5).GateN, 6);
            Assert.Equal(1.0, FitnessEvaluator.Score(BT(2, 0, 70, 40), 0, 0.5).GateN, 6);
        }

        [Fact]
        public void WeightedSum_AllGatesOne_EqualsWeightedReward()
        {
            // 전 게이트=1, P=W=O=1 → F = wP+wW+wO = 1.0
            var v = FitnessEvaluator.Score(BT(2, 0, 70, 40), meanAbsZ: 0, exposure: 0.5);
            Assert.Equal(1.0, v.F, 6);

            // 혼합: P=0.5, W=0.5, O=1 → 0.55*0.5 + 0.25*0.5 + 0.20*1 = 0.6
            var m = FitnessEvaluator.Score(BT(1, 0, 55, 40), meanAbsZ: 0, exposure: 0.5);
            Assert.Equal(0.6, m.F, 6);
        }

        [Fact]
        public void Oos_Matching_GateOne()
        {
            var bt = BT(2, 10, 70, 40);
            double baseF = FitnessEvaluator.BaseFitness(bt, 0.2, 0.5);
            var v = FitnessEvaluator.Score(bt, 0.2, 0.5, oosBaseFitness: baseF);
            Assert.Equal(1.0, v.GateOos, 6);
            Assert.Equal(baseF, v.F, 6);
        }

        [Fact]
        public void Oos_Collapse_KillsFitness()
        {
            // 표본외에서 적응도 붕괴(0) → 과적합 → gate_OOS→0 → F→0
            var bt = BT(2, 10, 70, 40);
            var v = FitnessEvaluator.Score(bt, 0.2, 0.5, oosBaseFitness: 0.0);
            Assert.Equal(0.0, v.GateOos, 6);
            Assert.Equal(0.0, v.F, 6);
        }

        [Fact]
        public void Oos_Null_GateOne()
        {
            var v = FitnessEvaluator.Score(BT(2, 10, 70, 40), 0.2, 0.5, oosBaseFitness: null);
            Assert.Equal(1.0, v.GateOos, 6);
        }

        [Fact]
        public void BaseFitness_ConsistentWithScore_WhenOosNull()
        {
            var bt = BT(2, 15, 65, 30);
            double baseF = FitnessEvaluator.BaseFitness(bt, 0.5, 0.4);
            var v = FitnessEvaluator.Score(bt, 0.5, 0.4);
            Assert.Equal(baseF, v.F, 6);
        }

        [Fact]
        public void Score_Deterministic_SameInputs()
        {
            var a = FitnessEvaluator.Score(BT(1.7, 12, 62, 33), 0.6, 0.45);
            var b = FitnessEvaluator.Score(BT(1.7, 12, 62, 33), 0.6, 0.45);
            Assert.Equal(a, b);   // readonly record struct 값 동등성
        }

        [Fact]
        public void Score_CustomBounds_TighterMddTol_LowersFitness()
        {
            var bt = BT(2, 20, 70, 40);
            var def    = FitnessEvaluator.Score(bt, 0, 0.5);                                   // MddTol=40 → gateR=0.5
            var tight  = FitnessEvaluator.Score(bt, 0, 0.5, new FitnessBounds { MddTolPct = 20 }); // gateR=0
            Assert.True(tight.F < def.F, $"tight={tight.F} def={def.F}");
            Assert.Equal(0.0, tight.GateR, 6);
        }
    }
}
