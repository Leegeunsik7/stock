// ============================================================================
// EventStudy - 개인 이벤트 스터디 엔진 🄲 (외부 의존 0) (v3.23.0)
//  복합규칙(CompositeRule) 조건을 과거 캔들에 전수 적용해, "이 패턴이 떴던 모든 시점"
//  이후의 미래수익 경험분포를 계산한다. 복합알림 빌더를 '~때 알려줘'에서
//  '~뒤 역사적으로 무슨 일이 있었나'로 격상 — 규칙을 신뢰하기 전에 검증.
//  CompositeAlertEngine.Evaluate를 그대로 재사용(같은 지표·논리). 순수·결정적.
//
//  설계 노트 / 숨은 룰:
//   · 각 시점 i에 Closes/Volumes를 i까지 잘라 MarketCtx 구성 → as-of 평가(미래 누수 없음)
//   · warmup = max(20, 최대 조건 Param + 2) — RSI/MA 룩백 확보
//   · horizon별: 표본수·평균·중앙값·승률(>0)·P5/P95 + 무조건 베이스라인 평균 대비 '엣지'
//   · 겹치는 신호 포함(쿨다운 없음) — 표본 독립성 한계는 UI에 표기
//   · 발생 0회도 유효 결과(엣지 판단 불가로 표기). 캔들 부족만 무효
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class HorizonStat : MS2E.UI.Portable.Ports.Strategy.IEventStatView   // Phase 94-O: 공변(전 멤버 암시·EdgePct 계산)
    {
        public int Days { get; set; }
        public int N { get; set; }
        public decimal MeanPct { get; set; }
        public decimal MedianPct { get; set; }
        public decimal WinRatePct { get; set; }
        public decimal P5 { get; set; }
        public decimal P95 { get; set; }
        public decimal BaselineMeanPct { get; set; }   // 무조건(전 구간) 평균 미래수익
        public decimal EdgePct => MeanPct - BaselineMeanPct;
    }

    public class EventStudyResult : MS2E.UI.Portable.Ports.Strategy.IEventStudyResultView   // Phase 94-O: 공변
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public int Occurrences { get; set; }
        public int Scanned { get; set; }
        public List<string> RecentDates { get; set; } = new();   // 최근 발생일 표본(최대 8)
        public List<HorizonStat> Stats { get; set; } = new();

        // 동명 반환형 충돌(List → IReadOnlyList) 회피: 명시적 공변 구현.
        IReadOnlyList<string> MS2E.UI.Portable.Ports.Strategy.IEventStudyResultView.RecentDates => RecentDates;
        IReadOnlyList<MS2E.UI.Portable.Ports.Strategy.IEventStatView>
            MS2E.UI.Portable.Ports.Strategy.IEventStudyResultView.Stats => Stats;
    }

    public static class EventStudy
    {
        public static EventStudyResult Analyze(CompositeRule rule, List<Candle> candles, int[]? horizons = null)
        {
            horizons ??= new[] { 5, 10, 20 };
            var res = new EventStudyResult();
            if (rule == null || rule.Conditions.Count == 0) { res.Note = "조건이 없는 규칙입니다."; return res; }
            if (candles == null || candles.Count < 30) { res.Note = "캔들이 부족합니다(30+ 권장)."; return res; }

            var cs = candles.OrderBy(c => c.Date).ToList();
            int maxParam = rule.Conditions.Max(c => c.Param);
            int warmup = Math.Max(20, maxParam + 2);
            int maxH = horizons.Max();
            if (cs.Count <= warmup + Math.Min(horizons.Min(), maxH) + 1)
            { res.Note = $"평가 구간 부족(warmup {warmup}+horizon)."; return res; }

            var closes = cs.Select(c => c.Close).ToList();
            var vols = cs.Select(c => (decimal)c.Volume).ToList();

            // horizon별 패턴-조건부 / 무조건(baseline) 미래수익 표본
            var hit = horizons.ToDictionary(h => h, _ => new List<decimal>());
            var baseAll = horizons.ToDictionary(h => h, _ => new List<decimal>());

            for (int i = warmup; i < cs.Count; i++)
            {
                res.Scanned++;
                // baseline: 가능한 horizon마다 미래수익 적재
                foreach (var h in horizons)
                    if (i + h < cs.Count && closes[i] > 0)
                        baseAll[h].Add((closes[i + h] - closes[i]) / closes[i] * 100m);

                var ctx = new MarketCtx
                {
                    Market = rule.Target,
                    Price = closes[i],
                    ChangePct = i > 0 && closes[i - 1] > 0 ? (closes[i] - closes[i - 1]) / closes[i - 1] * 100m : 0m,
                    Closes = closes.Take(i + 1).ToList(),
                    Volumes = vols.Take(i + 1).ToList()
                };
                if (!CompositeAlertEngine.Evaluate(rule, ctx)) continue;

                res.Occurrences++;
                if (res.RecentDates.Count < 8) res.RecentDates.Add(cs[i].Date.ToString("yyyy-MM-dd"));
                foreach (var h in horizons)
                    if (i + h < cs.Count && closes[i] > 0)
                        hit[h].Add((closes[i + h] - closes[i]) / closes[i] * 100m);
            }
            // 최근 발생일이 오래된→최신이 아니라 최신 8개가 되도록 뒤집어 정리
            res.RecentDates = res.RecentDates.AsEnumerable().Reverse().Take(8).ToList();

            foreach (var h in horizons)
            {
                var s = hit[h];
                var stat = new HorizonStat { Days = h, N = s.Count, BaselineMeanPct = Mean(baseAll[h]) };
                if (s.Count > 0)
                {
                    stat.MeanPct = Mean(s);
                    stat.MedianPct = Percentile(s, 50);
                    stat.WinRatePct = (decimal)s.Count(x => x > 0) / s.Count * 100m;
                    stat.P5 = Percentile(s, 5);
                    stat.P95 = Percentile(s, 95);
                }
                res.Stats.Add(stat);
            }

            res.IsValid = true;
            res.Note = res.Occurrences == 0
                ? $"{res.Scanned}봉 스캔 · 패턴 발생 0회(엣지 판단 불가) — 조건을 완화해 보세요."
                : $"{res.Scanned}봉 스캔 · 패턴 {res.Occurrences}회 발생";
            return res;
        }

        // ── 통계 헬퍼 ──
        private static decimal Mean(List<decimal> v) => v.Count == 0 ? 0 : v.Average();

        private static decimal Percentile(List<decimal> v, double p)
        {
            if (v.Count == 0) return 0;
            var sorted = v.OrderBy(x => x).ToList();
            int idx = (int)Math.Ceiling(p / 100.0 * sorted.Count) - 1;
            idx = Math.Clamp(idx, 0, sorted.Count - 1);
            return sorted[idx];
        }
    }
}
