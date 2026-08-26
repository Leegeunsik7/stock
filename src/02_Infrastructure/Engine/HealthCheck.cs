// ============================================================================
// HealthCheck - 종합 포트폴리오 건강검진 점수 (Season 9 #1) · 순수 결정론 (외부 의존 0)
//  흩어진 결정론 엔진의 출력 지표를 0~100 하위점수로 환산하고 '실측된 축만' 평균해 종합 점수·등급·권고를 만든다.
//   · 분산(상관·집중·종목수) · 위기방어(블랙스완 최악 낙폭) · 미래전망(확률미래 손실확률) · 행동규율(회고 편향 수).
//   · 정직성: 데이터 없는 축은 IsNeutral=중립(거짓 점수 금지)이며 종합 평균에서 제외한다. 실측 축이 2개 미만이면
//     문자 등급을 매기지 않고 '평가 불가(데이터 부족)'로 표기한다.
//   · 난수·시계 없음(결정론). 진단 보조·정보 제공이며 투자 권유가 아님.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public sealed record HealthInputs(
        int Holdings, int PricedHoldings, double AvgCorrelation, double TopRiskConcentrationPct, double EvenSharePct,
        double WorstCrisisMddPct, double ProbLossPct, int BiasWarnings,
        bool HasRisk, bool HasCrisis, bool HasFuture, bool HasBehavior);

    public sealed record HealthSub(string Key, string Label, double Score, string Detail, bool IsNeutral);

    public sealed record HealthScoreResult(double Overall, string Grade, string GradeLabel,
        IReadOnlyList<HealthSub> Subs, IReadOnlyList<string> Recommendations, int RealAxes);

    public static class HealthCheck
    {
        public static double Clamp(double v) => Math.Max(0, Math.Min(100, v));

        public static HealthScoreResult Compute(HealthInputs x)
        {
            var subs = new List<HealthSub>();

            // ── 분산 ── (측정 가능 종목 2개 이상일 때만 실측; 단일 종목은 '집중'으로 알려진 저득점)
            double corrScore = Clamp(100 * (1 - Math.Max(0, Math.Min(1, x.AvgCorrelation))));
            double countScore = Clamp(x.Holdings / 8.0 * 100);
            if (x.Holdings <= 1)
                subs.Add(new("diversification", "분산", 20, "단일 종목 — 분산 없음", false));
            else if (x.PricedHoldings < 2)
                subs.Add(new("diversification", "분산", 50, $"측정 가능 종목 부족(시세 {x.PricedHoldings}/{x.Holdings}) — 중립", true));
            else if (x.HasRisk)
            {
                double conc = Clamp(100 - Math.Max(0, x.TopRiskConcentrationPct - x.EvenSharePct) * 1.5);
                double sc = Clamp(0.45 * corrScore + 0.30 * conc + 0.25 * countScore);
                subs.Add(new("diversification", "분산", sc,
                    $"평균 상관 {x.AvgCorrelation:0.00} · 최대 위험집중 {x.TopRiskConcentrationPct:0.0}%(균등 {x.EvenSharePct:0.0}%) · {x.Holdings}종목", false));
            }
            else   // 상관은 있으나 리스크 분해 미가용 → 집중 항 제외(가중 재정규화)
            {
                double sc = Clamp(0.6 * corrScore + 0.4 * countScore);
                subs.Add(new("diversification", "분산", sc, $"평균 상관 {x.AvgCorrelation:0.00} · {x.Holdings}종목(리스크 분해 미가용)", false));
            }

            // ── 위기 방어 ──
            subs.Add(x.HasCrisis
                ? new("crisis", "위기 방어", Clamp(100 + x.WorstCrisisMddPct * 1.4), $"역사적 위기 최악 낙폭 {x.WorstCrisisMddPct:0.0}%", false)
                : new("crisis", "위기 방어", 50, "위기 시뮬 데이터 부족 — 중립", true));

            // ── 미래 전망 ──
            subs.Add(x.HasFuture
                ? new("future", "미래 전망", Clamp(100 - x.ProbLossPct), $"1개월 손실확률 {x.ProbLossPct:0.0}%", false)
                : new("future", "미래 전망", 50, "확률 미래 표본 부족 — 중립", true));

            // ── 행동 규율 ── (데이터 없으면 중립 50 — 70으로 띄우지 않음)
            subs.Add(x.HasBehavior
                ? new("behavior", "행동 규율", Clamp(100 - x.BiasWarnings * 18), $"행동 편향 신호 {x.BiasWarnings}건", false)
                : new("behavior", "행동 규율", 50, "거래 표본 부족 — 중립", true));

            // 종합: '실측된(비중립) 축'만 평균. 실측 축 2개 미만이면 문자 등급 없음.
            var real = subs.Where(s => !s.IsNeutral).ToList();
            int realAxes = real.Count;
            double overall = Clamp(realAxes > 0 ? real.Average(s => s.Score) : 50);
            var (grade, label) = realAxes >= 2 ? Grade(overall) : ("—", "평가 불가(데이터 부족)");

            var recs = new List<string>();
            if (realAxes < 2)
                recs.Add("ℹ️ 평가에 필요한 데이터가 부족합니다 — 보유 종목 시세 또는 거래 이력이 더 필요합니다.");
            else
            {
                foreach (var s in real.Where(s => s.Score < 55).OrderBy(s => s.Score))
                    recs.Add(s.Key switch
                    {
                        "diversification" => "🧩 분산 강화 — 종목 수 확대 또는 상관이 낮은 자산 편입, 위험 집중 종목 비중 축소.",
                        "crisis" => "🛡 위기 방어 — 고베타·집중 포지션을 줄이고 헷지/현금 비중을 점검하세요. (/blackswan)",
                        "future" => "🔮 손실확률 높음 — 포지션 축소·목표 재점검을 검토하세요. (/future)",
                        "behavior" => "🪞 행동 편향 신호 — 매매 규칙(목표·손절)을 사전에 정하고 복기하세요. (/coach)",
                        _ => s.Label
                    });
                if (recs.Count == 0) recs.Add("✅ 뚜렷한 취약 신호가 없습니다. 현재의 분산·규율을 유지하세요.");
            }

            return new HealthScoreResult(Math.Round(overall, 1), grade, label, subs, recs, realAxes);
        }

        private static (string, string) Grade(double s) =>
            s >= 80 ? ("A", "우수") : s >= 65 ? ("B", "양호") : s >= 50 ? ("C", "보통") : s >= 35 ? ("D", "주의") : ("F", "취약");
    }
}
