// ============================================================================
// AlertCalibrator - 알림 신뢰도 캘리브레이터 🄱 (외부 의존 0, 입력 in-memory) (v3.22.0)
//  알림이 "쏘기"만 하던 것을 넘어, 각 알림 이후 실제로 의미 있는 움직임이 있었는지
//  캔들로 대조해 유형별 정보성(informativeness)을 채점한다. 시스템이 자기평가하게 만든다.
//   · 각 알림: 발생 시점 종가 → horizon일 뒤 종가의 forward return
//   · 적중(hit) = |forward return| ≥ 임계치 (노이즈 vs 신호)
//   · 유형(Source)별 적중률·평균 |이동|·평균 부호이동 집계 → 권고(유지/강등 검토)
//  순수·결정적. 읽기전용 — 점수만 낸다(자동 음소거는 위험하므로 권고까지만; 사용자 판단).
//
//  설계 노트 / 숨은 룰:
//   · entry = 알림시각 이하 마지막 캔들, exit = entry 인덱스 + horizon봉. 범위 밖이면 '평가불가'
//   · Symbol 없거나 캔들 없으면 평가불가(집계 모수에서 제외) — 거짓 점수 금지
//   · 표본 < 5면 권고 보류('표본 부족')
//   · 정보성 ≠ 수익(방향 무관 |이동| 기준) — UI에 정직 표기
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public record AlertRecord(DateTime When, string Category, string Symbol);

    public class AlertGrade
    {
        public string Category { get; set; } = "";
        public int Count { get; set; }
        public int Evaluable { get; set; }
        public int Hits { get; set; }
        public double HitRatePct { get; set; }
        public double AvgAbsMovePct { get; set; }
        public double AvgSignedMovePct { get; set; }
        public string Recommendation { get; set; } = "";
    }

    public class CalibrationReport
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public int Total { get; set; }
        public int Evaluable { get; set; }
        public int HorizonDays { get; set; }
        public double MoveThresholdPct { get; set; }
        public double OverallHitRatePct { get; set; }
        public List<AlertGrade> Grades { get; set; } = new();
    }

    public static class AlertCalibrator
    {
        public static CalibrationReport Score(IEnumerable<AlertRecord> alerts,
            IReadOnlyDictionary<string, List<Candle>> candlesBySymbol,
            int horizonDays = 5, double moveThresholdPct = 3.0)
        {
            var rep = new CalibrationReport { HorizonDays = Math.Max(1, horizonDays), MoveThresholdPct = moveThresholdPct };
            var list = (alerts ?? Enumerable.Empty<AlertRecord>()).ToList();
            rep.Total = list.Count;
            if (list.Count == 0) { rep.Note = "채점할 알림이 없습니다."; return rep; }

            // 심볼별 캔들 정렬 캐시
            var sorted = new Dictionary<string, List<Candle>>(StringComparer.OrdinalIgnoreCase);
            if (candlesBySymbol != null)
                foreach (var kv in candlesBySymbol)
                    if (kv.Value is { Count: > 0 })
                        sorted[kv.Key] = kv.Value.OrderBy(c => c.Date).ToList();

            var byCat = new Dictionary<string, AlertGrade>();
            int totalHits = 0, totalEval = 0;

            foreach (var a in list)
            {
                var cat = string.IsNullOrWhiteSpace(a.Category) ? "기타" : a.Category;
                if (!byCat.TryGetValue(cat, out var g)) { g = new AlertGrade { Category = cat }; byCat[cat] = g; }
                g.Count++;

                if (string.IsNullOrWhiteSpace(a.Symbol) || !sorted.TryGetValue(a.Symbol, out var cs)) continue;
                int entry = LastIndexOnOrBefore(cs, a.When);
                if (entry < 0) continue;
                int exit = entry + rep.HorizonDays;
                if (exit >= cs.Count) continue;                       // 너무 최근 → 평가 불가

                decimal e0 = cs[entry].Close, e1 = cs[exit].Close;
                if (e0 <= 0) continue;
                double fwd = (double)((e1 - e0) / e0 * 100m);

                g.Evaluable++; totalEval++;
                g.AvgSignedMovePct += fwd;
                g.AvgAbsMovePct += Math.Abs(fwd);
                if (Math.Abs(fwd) >= moveThresholdPct) { g.Hits++; totalHits++; }
            }

            foreach (var g in byCat.Values)
            {
                if (g.Evaluable > 0)
                {
                    g.AvgSignedMovePct /= g.Evaluable;
                    g.AvgAbsMovePct /= g.Evaluable;
                    g.HitRatePct = (double)g.Hits / g.Evaluable * 100.0;
                }
                g.Recommendation = Recommend(g);
            }

            rep.Grades = byCat.Values.OrderByDescending(g => g.Count).ToList();
            rep.Evaluable = totalEval;
            rep.OverallHitRatePct = totalEval > 0 ? (double)totalHits / totalEval * 100.0 : 0;
            rep.IsValid = true;
            rep.Note = $"{rep.Total}건 중 {totalEval}건 평가 · horizon {rep.HorizonDays}일 · 임계 {moveThresholdPct:0.#}%";
            return rep;
        }

        private static string Recommend(AlertGrade g)
        {
            if (g.Evaluable < 5) return "표본 부족";
            if (g.HitRatePct < 33) return "강등 검토(노이즈↑)";
            if (g.HitRatePct >= 60) return "신뢰 높음";
            return "보통";
        }

        // 정렬된 캔들에서 When 이하 마지막 인덱스(없으면 -1)
        private static int LastIndexOnOrBefore(List<Candle> cs, DateTime when)
        {
            int lo = 0, hi = cs.Count - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (cs[mid].Date <= when) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            return ans;
        }
    }
}
