// ============================================================================
// MultiverseEngine — 멀티버스 포트폴리오 시뮬레이터 (v3.74)
//  · 순수 C# 결정론, 외부 의존 0 (FutureCone·BlackSwan 재사용).
//  · 4개 거시경제 시나리오 → μ 이동 + σ 배율 → FutureCone 블록 부트스트랩.
//  · DEEP_RECESSION 전용: GFC 2008 위기 충격 오버레이 (BlackSwan.Inject).
//  · ScenarioTable은 완전 결정론 — UI/테스트에서 직접 참조 가능.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public enum MultiverseScenario { Live = 0, BullRun = 1, Stagflation = 2, DeepRecession = 3 }

    public sealed record ScenarioParams(
        MultiverseScenario Scenario,
        double MuShift,
        double SigmaScale,
        string Label,
        string AccentHex,
        string Description);

    public sealed record AssetProjection(
        string Symbol,
        string Name,
        double WeightPct,
        double ProjectedAnnualPct);

    public sealed record MultiverseResult(
        bool IsValid,
        MultiverseScenario Scenario,
        IReadOnlyList<ConeBand> Bands,
        double ProjectedNavPct,
        double ProjectedMddPct,
        double AnnualizedSharpe,
        BlackSwanResult? CrisisOverlay,
        IReadOnlyList<AssetProjection> Assets,
        string Note);

    public sealed record MultiverseAsset(
        string Symbol,
        string Name,
        double WeightPct,
        IReadOnlyList<double> DailyReturns);

    public static class MultiverseEngine
    {
        private const double RfDaily = 0.035 / 252.0;

        // ── 시나리오 파라미터 테이블 ─────────────────────────────────────────────
        public static readonly IReadOnlyList<ScenarioParams> ScenarioTable =
        new ScenarioParams[]
        {
            new(MultiverseScenario.Live, 0.0, 1.0,
                "현재 (LIVE)", "#4f5bd5",
                "역사적 수익률·변동성 그대로 — 중립 기준선"),
            new(MultiverseScenario.BullRun, +0.0003, 0.75,
                "강세장 · 골디락스", "#0f9d6b",
                "성장 가속 +7.6%↑/년  |  변동성 25%↓  |  공급망 안정 · 저금리"),
            new(MultiverseScenario.Stagflation, -0.0002, 1.50,
                "스태그플레이션", "#9933cc",
                "실질수익 −5%↓/년  |  변동성 50%↑  |  고물가 · 저성장 압박"),
            new(MultiverseScenario.DeepRecession, -0.0008, 2.50,
                "대공황 · 블랙스완", "#e34d4d",
                "연간 −20%↓  |  변동성 2.5배  |  상관 1 수렴 · GFC 2008 위기 충격 오버레이"),
        };

        // ── 메인 진입점 ──────────────────────────────────────────────────────────
        public static MultiverseResult Project(
            IReadOnlyList<MultiverseAsset> assets,
            MultiverseScenario scenario,
            int horizonDays = 252,
            int sims = 80,
            int seed = 42)
        {
            if (assets == null || assets.Count == 0)
                return Invalid(scenario, "보유 종목 없음");
            if (sims < 10) sims = 10;

            var sp = ScenarioTable[(int)scenario];

            // 1. 공통 기간 최솟값으로 정렬 (보유 종목 간 날짜 정합 단순화)
            int minLen = assets.Min(a => a.DailyReturns.Count);
            if (minLen < 30)
                return Invalid(scenario, $"역사 데이터 부족 ({minLen}일, 최소 30일 필요)");

            // 2. 비중 가중 포트폴리오 일별 수익률
            var pfReturns = new double[minLen];
            foreach (var a in assets)
            {
                double w = a.WeightPct / 100.0;
                int offset = a.DailyReturns.Count - minLen;
                for (int i = 0; i < minLen; i++)
                    pfReturns[i] += w * a.DailyReturns[offset + i];
            }

            // 3. 시나리오 이동: 편차 스케일링(σ) + 평균 이동(μ)
            double muOrig = Mean(pfReturns);
            for (int i = 0; i < pfReturns.Length; i++)
                pfReturns[i] = (pfReturns[i] - muOrig) * sp.SigmaScale + muOrig + sp.MuShift;

            // 4. 블록 부트스트랩 (FutureCone 재사용, 시나리오별 시드 분리)
            int sceneSeed = (int)scenario * 1000 + seed;
            var cone = FutureCone.Project(pfReturns, horizonDays, sims, sceneSeed);
            if (!cone.IsValid)
                return Invalid(scenario, "FutureCone: " + cone.Note);

            // 5. 연환산 샤프 (이동된 수익률 분포 기반)
            double muShifted = Mean(pfReturns);
            double sigma = StdDev(pfReturns);
            double sharpe = sigma > 1e-10
                ? (muShifted - RfDaily) / sigma * Math.Sqrt(252.0)
                : 0.0;

            // 6. 종목별 예상 연수익 (MuShift 적용 독립 추정)
            var assetProj = assets.Select(a =>
            {
                double mu_i = Mean(a.DailyReturns.ToArray());
                double annPct = (Math.Pow(1.0 + mu_i + sp.MuShift, 252) - 1.0) * 100.0;
                return new AssetProjection(a.Symbol, a.Name, a.WeightPct, annPct);
            }).ToList();

            // 7. BlackSwan 위기 오버레이 (DeepRecession 전용, GFC 2008)
            BlackSwanResult? crisis = null;
            if (scenario == MultiverseScenario.DeepRecession)
            {
                var prof = BlackSwan.ById("gfc08");
                if (prof != null)
                {
                    var bsH = assets.Select(a =>
                        new BlackSwanHolding(a.Symbol, a.Name, a.WeightPct / 100.0, 1.0))
                        .ToList();
                    crisis = BlackSwan.Inject(prof, bsH);
                }
            }

            return new MultiverseResult(
                true, scenario, cone.Bands,
                cone.MedianReturnPct,
                cone.MedianMaxDrawdownPct,  // magnitude (양수), UI에서 "-" 붙임
                sharpe,
                crisis,
                assetProj,
                sp.Label);
        }

        // ── 통계 헬퍼 ────────────────────────────────────────────────────────────
        private static double Mean(double[] xs)
        {
            if (xs.Length == 0) return 0.0;
            double s = 0.0;
            foreach (var v in xs) s += v;
            return s / xs.Length;
        }

        private static double StdDev(double[] xs)
        {
            if (xs.Length < 2) return 0.0;
            double m = Mean(xs), s = 0.0;
            foreach (var v in xs) s += (v - m) * (v - m);
            return Math.Sqrt(s / (xs.Length - 1));
        }

        private static MultiverseResult Invalid(MultiverseScenario s, string note) =>
            new(false, s, Array.Empty<ConeBand>(), 0.0, 0.0, 0.0, null,
                Array.Empty<AssetProjection>(), note);
    }
}
