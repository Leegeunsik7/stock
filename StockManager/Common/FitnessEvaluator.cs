// ============================================================================
// FitnessEvaluator — S19 #2: 다차원 적응도 평가기 (유전자 방화벽 평가 코어)
//
//  단일 Sharpe 추종의 함정(깊은 낙폭·고마찰·과소표본 행운)을 차단하기 위해,
//  "가산 보상 코어 × 곱셈 거부 게이트" 구조로 적응도를 산출한다:
//
//    F = ( wP·P + wW·W + wO·O ) · gate_R(MDD) · gate_G(Z̄) · gate_N(N) · gate_OOS
//        └──── 보상(가산) ────┘  └─────────── 거부 게이트(곱셈, 0~1) ───────────┘
//
//  거부 게이트가 0에 수렴하면 수익이 아무리 높아도 F→0 → 파멸 가능 개체는 리더보드 진입 불가.
//  "시장에 상처를 덜 내는 견고한 알파" = gate_R(낙폭)·gate_G(마찰)의 곱셈 거부가 정확한 표현.
//
//  스케일(StrategyCompiler.Backtest 기준): WinRate·MaxDrawdown 은 퍼센트(0~100),
//  MaxDrawdown 은 양수 크기, Sharpe 는 연율화 비율(음수 가능).
//
//  설계: 사이드 이펙트 0 · 결정론(초월함수 회피, clamp/min/max/abs 만) · 외부 의존성 0.
// ============================================================================
using System;

namespace StockManager.Common
{
    // ── 적응도 성분 벡터 (zero-alloc readonly record struct) ─────────────────────
    public readonly record struct FitnessVector(
        double P,        // 수익성 (Sharpe 정규화)
        double W,        // 일관성 (승률 정규화)
        double O,        // 자본효율/기회비용 (노출 적정성)
        double GateR,    // 견고성 게이트 (낙폭)
        double GateG,    // 마찰 게이트 (슬리피지 Z̄)
        double GateN,    // 표본 신뢰 게이트 (거래 수)
        double GateOos,  // 표본외 일관성 게이트 (과적합 거부)
        double F);       // 최종 복합 적응도 [0,1]

    // ── 정규화·가중 상수 (전부 init — 환경별 튜닝, 기본값 보수적) ─────────────────
    public sealed record FitnessBounds
    {
        public double SharpeFloor    { get; init; } = 0.0;
        public double SharpeRef      { get; init; } = 2.0;
        public double WinRateFloorPct{ get; init; } = 40.0;  // 퍼센트
        public double WinRateRefPct  { get; init; } = 70.0;  // 퍼센트
        public double ExposureTarget { get; init; } = 0.50;  // 노출 비율 [0,1] 목표
        public double MddTolPct      { get; init; } = 40.0;  // 퍼센트 — 이 낙폭에서 gate_R=0
        public double ZTol           { get; init; } = 2.0;   // 이 마찰에서 gate_G=floor
        public int    TradeFloor     { get; init; } = 20;    // 이 미만이면 gate_N 패널티

        public double WP { get; init; } = 0.55;
        public double WW { get; init; } = 0.25;
        public double WO { get; init; } = 0.20;

        public double GateGFloor { get; init; } = 0.0001;    // 마찰 게이트 하한(ε) — 완전 0 회피
        public double EpsF       { get; init; } = 0.01;      // OOS 게이트 분모 하한

        public static FitnessBounds Default { get; } = new();
    }

    public static class FitnessEvaluator
    {
        /// <summary>
        /// 복합 적응도 벡터를 산출한다. oosBaseFitness 가 주어지면 표본외 분기(과적합) 게이트를 적용.
        /// </summary>
        /// <param name="bt">인-샘플 백테스트 요약.</param>
        /// <param name="meanAbsZ">섀도우 집행 평균 |Z| (시장 마찰 잔차). 음수 부호 무시.</param>
        /// <param name="exposure">시장 노출 비율 [0,1] (포지션 보유 봉 / 전체 봉) — 호출자 산출.</param>
        /// <param name="bounds">정규화 상수 (null이면 Default).</param>
        /// <param name="oosBaseFitness">표본외 폴드의 BaseFitness. null이면 OOS 게이트=1.</param>
        public static FitnessVector Score(
            BacktestSummary bt,
            double          meanAbsZ,
            double          exposure,
            FitnessBounds?  bounds = null,
            double?         oosBaseFitness = null)
        {
            if (bt is null) throw new ArgumentNullException(nameof(bt));
            var b = bounds ?? FitnessBounds.Default;

            var (p, w, o, gR, gG, gN, baseF) = Components(bt, meanAbsZ, exposure, b);

            double gOos = oosBaseFitness.HasValue
                ? Clamp01(1.0 - SafeDiv(Math.Abs(baseF - oosBaseFitness.Value), Math.Max(baseF, b.EpsF)))
                : 1.0;

            double f = baseF * gOos;
            return new FitnessVector(p, w, o, gR, gG, gN, gOos, f);
        }

        /// <summary>
        /// OOS 게이트를 제외한 기저 적응도(보상×게이트R·G·N). 폴드별 평가 + OOS 분기 계산용.
        /// </summary>
        public static double BaseFitness(
            BacktestSummary bt, double meanAbsZ, double exposure, FitnessBounds? bounds = null)
        {
            if (bt is null) throw new ArgumentNullException(nameof(bt));
            return Components(bt, meanAbsZ, exposure, bounds ?? FitnessBounds.Default).baseF;
        }

        // ── 성분 계산 (단일 진실 원천) ───────────────────────────────────────────
        private static (double p, double w, double o, double gR, double gG, double gN, double baseF)
            Components(BacktestSummary bt, double meanAbsZ, double exposure, FitnessBounds b)
        {
            double sharpe = (double)bt.Sharpe;
            double winPct = (double)bt.WinRate;       // 0~100
            double mddPct = (double)bt.MaxDrawdown;   // 0~100, 양수 크기
            int    trades = bt.TradeCount;

            // 보상 항 [0,1]
            double p = Norm(sharpe, b.SharpeFloor,     b.SharpeRef);
            double w = Norm(winPct, b.WinRateFloorPct, b.WinRateRefPct);
            double o = ExposureScore(exposure, b.ExposureTarget);

            // 거부 게이트 [0,1]
            double gR = Clamp01(1.0 - SafeDiv(mddPct, b.MddTolPct));
            double gG = Math.Max(b.GateGFloor, Clamp01(1.0 - SafeDiv(Math.Abs(meanAbsZ), b.ZTol)));
            double gN = Clamp01(trades / (double)Math.Max(1, b.TradeFloor));

            double reward = b.WP * p + b.WW * w + b.WO * o;
            double baseF  = reward * gR * gG * gN;
            return (p, w, o, gR, gG, gN, baseF);
        }

        // ── 정규화 헬퍼 (결정론 — clamp/min/max/abs 만) ──────────────────────────

        // 선형 정규화: floor↦0, ref↦1, 그 외 클램프. ref<=floor 방어.
        private static double Norm(double x, double floor, double reference)
        {
            double denom = reference - floor;
            if (denom <= 1e-12) return x >= reference ? 1.0 : 0.0;
            return Clamp01((x - floor) / denom);
        }

        // 노출 적정성: target 에서 1, 0 또는 2·target 에서 0 (대칭 텐트).
        private static double ExposureScore(double exposure, double target)
        {
            if (target <= 1e-12) return 0.0;
            return Clamp01(1.0 - Math.Abs(exposure - target) / target);
        }

        private static double Clamp01(double v) => v < 0.0 ? 0.0 : (v > 1.0 ? 1.0 : v);

        // 0 이하 분모 방어: 분모≤0 이면 분자>0 일 때 큰 값(게이트 0 수렴), 아니면 0.
        private static double SafeDiv(double a, double bb)
            => bb > 1e-12 ? a / bb : (a > 1e-12 ? 1e9 : 0.0);
    }
}
