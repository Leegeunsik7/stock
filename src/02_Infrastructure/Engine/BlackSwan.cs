// ============================================================================
// BlackSwan - 역사적 위기 주입 시뮬레이터 (Season 8 #1) · 순수 결정론, 외부 의존 0
//  단순 수치 하락이 아니라 과거 실제 위기 구간의 '대표(양식화) 일별 충격 경로'를 현재 포트폴리오에
//  강제 주입해 생존력을 본다. 위기엔 상관이 1로 붕괴(전 종목 동반 하락)한다는 가정으로,
//  각 종목 수익 = beta × 시장충격. 포트폴리오 수익 = Σ(비중×beta)×시장충격.
//   · 결정론(난수 없음) → 동일 입력 동일 결과(테스트 가능).
//   · 프로파일은 대표 경로(형태·낙폭 재현)이며 실제 일별과 정확히 일치하지 않음.
//  ⚠️ 정보 제공·스트레스 테스트이며 예측/투자 권유가 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public sealed record CrisisProfile(string Id, string Name, string Period, IReadOnlyList<double> MarketDailyReturns, string Note);

    public sealed record BlackSwanHolding(string Symbol, string Name, double Weight, double Beta);

    public sealed record HoldingImpact(string Symbol, string Name, double Weight, double Beta, double ContribPct, double OwnReturnPct);

    public sealed record BlackSwanResult(
        bool IsValid, string CrisisName, int Days, double TerminalPct, double MaxDrawdownPct, int TroughDay,
        double PortfolioBeta, IReadOnlyList<double> EquityCurve, IReadOnlyList<HoldingImpact> Worst, string Note);

    public static class BlackSwan
    {
        // 대표(양식화) 일별 시장 충격 경로(%). 형태·누적 낙폭을 재현하되 실제 일별과 일치하지 않음.
        public static IReadOnlyList<CrisisProfile> Library { get; } = new[]
        {
            new CrisisProfile("covid20", "코로나 팬데믹 급락", "2020-02~03",
                new[] { -1.0, -3.0, -0.5, 0.5, -4.4, -1.7, -3.4, 4.6, -1.5, -2.5, -7.6, 4.9, -9.5, -5.2, 6.0, -12.0, -4.3, 9.4, -5.2, -4.4, 1.2, -2.9 },
                "23거래일 만에 약 -34% — 사상 최단 베어마켓"),
            new CrisisProfile("gfc08", "2008 금융위기", "2008-09~11",
                new[] { -1.5, -4.7, 1.2, -8.8, -3.0, -4.0, -1.1, -3.5, -9.0, -1.5, 11.6, -7.6, -9.0, 4.2, -5.6, -3.0, -6.1, 1.0, -5.0, -2.5, -3.2, -1.8, -4.0, 2.5, -3.3 },
                "약 두 달간 -40%대 — 신용·상관 붕괴"),
            new CrisisProfile("rate22", "2022 금리 충격", "2022(압축)",
                new[] { -1.2, -0.8, -2.0, 0.6, -1.5, -1.0, -2.4, -1.1, 0.9, -1.8, -2.2, -0.7, -1.6, 1.0, -2.0, -1.3, -1.9, -0.6, -1.7, -1.2, -2.1, -0.9, -1.4, -1.0 },
                "고강도 긴축 — 완만하지만 끈질긴 -25%급 하락(압축 재현)"),
            new CrisisProfile("bm87", "1987 블랙먼데이", "1987-10",
                new[] { -1.0, -2.5, -4.6, -22.6, 5.3, -8.0, 9.1, -3.9 },
                "단 하루 -20%대 폭락 — 변동성 충격"),
        };

        public static CrisisProfile? ById(string id) => Library.FirstOrDefault(c => c.Id == id);

        public static BlackSwanResult Inject(CrisisProfile profile, IReadOnlyList<BlackSwanHolding> holdings)
        {
            if (profile == null || profile.MarketDailyReturns.Count < 2)
                return Invalid("위기 프로파일이 비었습니다.");
            if (holdings == null || holdings.Count == 0)
                return Invalid("보유 종목이 없습니다.");

            double wsum = holdings.Sum(h => h.Weight);
            var norm = wsum > 0
                ? holdings.Select(h => h with { Weight = h.Weight / wsum }).ToList()
                : holdings.Select(h => h with { Weight = 1.0 / holdings.Count }).ToList();   // 비중 정보 없으면 균등

            double portBeta = norm.Sum(h => h.Weight * h.Beta);

            var eq = new List<double>(profile.MarketDailyReturns.Count + 1) { 1.0 };
            double v = 1.0, peak = 1.0, maxdd = 0; int trough = 0;
            for (int t = 0; t < profile.MarketDailyReturns.Count; t++)
            {
                v *= 1.0 + profile.MarketDailyReturns[t] / 100.0 * portBeta;
                eq.Add(v);
                if (v > peak) peak = v;
                double dd = peak > 0 ? (peak - v) / peak : 0;
                if (dd > maxdd) { maxdd = dd; trough = t + 1; }
            }
            double terminal = (v - 1.0) * 100.0;

            var impacts = new List<HoldingImpact>(norm.Count);
            foreach (var h in norm)
            {
                double hv = 1.0;
                foreach (var m in profile.MarketDailyReturns) hv *= 1.0 + m / 100.0 * h.Beta;
                double ownRet = (hv - 1.0) * 100.0;
                impacts.Add(new HoldingImpact(h.Symbol, h.Name, h.Weight, h.Beta, h.Weight * ownRet, ownRet));
            }
            var worst = impacts.OrderBy(x => x.ContribPct).Take(8).ToList();

            return new BlackSwanResult(true, profile.Name, profile.MarketDailyReturns.Count,
                terminal, -maxdd * 100.0, trough, portBeta, eq, worst,
                $"{profile.Period} · 포트폴리오 β {portBeta:0.00} · 상관 붕괴 가정(전 종목 동반 하락)");
        }

        private static BlackSwanResult Invalid(string note) =>
            new BlackSwanResult(false, "", 0, 0, 0, 0, 0, Array.Empty<double>(), Array.Empty<HoldingImpact>(), note);
    }
}
