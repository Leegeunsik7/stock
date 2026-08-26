// ============================================================================
// HypothesisMiner - 자동 가설 발굴 엔진 🄶 "전략 유전자 풀" (외부 의존 0) (v3.27.0)
//  사람이 규칙을 정의하지 않아도, 조건 빌딩블록을 조합 생성 → 🄲 EventStudy로 엣지 측정 →
//  과거를 train/OOS로 갈라 '진짜 보류표본(OOS)'에서도 살아남는 패턴만 유전자 풀에 등재.
//  🄲 EventStudy + 🅖 워크포워드 원리를 재사용.
//
//  ⚠️ 과최적화/데이터마이닝 방어가 설계의 본질:
//   · in-sample만 좋은 건 버리고 OOS 엣지 > 0 필수(진짜 게이트)
//   · 효과크기 바 = baseEdgeBar × (1 + ln(후보수)/5)  — 다중검정 보정(많이 볼수록 더 엄격)
//   · OOS 최소 발생 수·승률 하한 동시 충족해야 생존
//  순수·결정적. 생존 규칙은 사용자가 검토 후 복합알림으로 채택(자동 적용 없음).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public class Hypothesis
    {
        public string Label { get; set; } = "";
        public CompositeRule Rule { get; set; } = new();
        public int Horizon { get; set; }
        public double TrainEdgePct { get; set; }
        public double OosEdgePct { get; set; }
        public double OosWinRatePct { get; set; }
        public int TrainN { get; set; }
        public int OosN { get; set; }
        public bool Survived { get; set; }
    }

    public class MiningResult
    {
        public bool IsValid { get; set; }
        public string Note { get; set; } = "";
        public int Tested { get; set; }
        public int Survivors { get; set; }
        public double SignificanceBar { get; set; }
        public List<Hypothesis> Pool { get; set; } = new();   // 생존자(OOS 엣지 내림차순)
    }

    public static class HypothesisMiner
    {
        /// <summary>조건 빌딩블록 + 선별된 AND 페어로 후보 규칙 생성.</summary>
        public static List<CompositeRule> GenerateCandidates()
        {
            CompositeCondition C(CondMetric m, int p, CondOp op, decimal thr) =>
                new() { Metric = m, Param = p, Op = op, Threshold = thr };
            CompositeRule R(string name, params CompositeCondition[] cs) =>
                new() { Name = name, Logic = RuleLogic.And, Conditions = cs.ToList() };

            var rsiLow = C(CondMetric.Rsi, 14, CondOp.Lte, 30);
            var rsiHigh = C(CondMetric.Rsi, 14, CondOp.Gte, 70);
            var gap20Lo = C(CondMetric.MaGapPct, 20, CondOp.Lte, -7);
            var gap20Hi = C(CondMetric.MaGapPct, 20, CondOp.Gte, 7);
            var gap60Lo = C(CondMetric.MaGapPct, 60, CondOp.Lte, -10);
            var volSurge = C(CondMetric.VolumeSurgePct, 20, CondOp.Gte, 100);
            var bigDrop = C(CondMetric.ChangePct, 1, CondOp.Lte, -3);
            var bigPop = C(CondMetric.ChangePct, 1, CondOp.Gte, 3);

            return new List<CompositeRule>
            {
                R("RSI<30 과매도", rsiLow),
                R("RSI>70 과매수", rsiHigh),
                R("MA20 이격 ≤-7%", gap20Lo),
                R("MA20 이격 ≥+7%", gap20Hi),
                R("MA60 이격 ≤-10%", gap60Lo),
                R("거래량 급증 ≥100%", volSurge),
                R("당일 -3% 급락", bigDrop),
                R("당일 +3% 급등", bigPop),
                R("과매도 + 거래량급증", rsiLow, volSurge),
                R("과매도 + 급락", rsiLow, bigDrop),
                R("낙폭과대 + 거래량급증", gap20Lo, volSurge),
                R("과열 + 거래량급증", rsiHigh, volSurge),
                R("MA60낙폭 + 과매도", gap60Lo, rsiLow),
                R("급등 + 거래량급증", bigPop, volSurge),
            };
        }

        public static MiningResult Mine(List<Candle> candles, int horizon = 10,
            double trainFrac = 0.7, int minOccurrences = 8, double baseEdgeBar = 0.5)
        {
            var res = new MiningResult();
            if (candles == null || candles.Count < 120) { res.Note = "캔들 부족(120+ 권장)."; return res; }

            var cs = candles.OrderBy(c => c.Date).ToList();
            int split = (int)(cs.Count * Math.Clamp(trainFrac, 0.4, 0.8));
            var train = cs.Take(split).ToList();
            var oos = cs.Skip(split).ToList();
            if (train.Count < 80 || oos.Count < 50) { res.Note = "train/OOS 구간이 짧습니다."; return res; }

            var candidates = GenerateCandidates();
            res.Tested = candidates.Count;
            // 다중검정 보정: 많이 볼수록 더 큰 효과크기를 요구
            double bar = baseEdgeBar * (1 + Math.Log(Math.Max(1, candidates.Count)) / 5.0);
            res.SignificanceBar = bar;

            var hz = new[] { horizon };
            foreach (var rule in candidates)
            {
                var tr = EventStudy.Analyze(rule, train, hz);
                var os = EventStudy.Analyze(rule, oos, hz);
                if (!tr.IsValid || !os.IsValid) continue;
                var ts = tr.Stats.FirstOrDefault(s => s.Days == horizon);
                var oss = os.Stats.FirstOrDefault(s => s.Days == horizon);
                if (ts == null || oss == null) continue;

                var h = new Hypothesis
                {
                    Label = rule.Name, Rule = rule, Horizon = horizon,
                    TrainEdgePct = (double)ts.EdgePct, OosEdgePct = (double)oss.EdgePct,
                    OosWinRatePct = (double)oss.WinRatePct, TrainN = ts.N, OosN = oss.N
                };
                // 생존 조건: 양 구간 발생 + OOS 표본 충분 + train 양 + OOS 엣지 ≥ 바 + OOS 승률 ≥ 50
                h.Survived = h.TrainN > 0 && h.OosN >= minOccurrences &&
                             h.TrainEdgePct > 0 && h.OosEdgePct >= bar && h.OosWinRatePct >= 50;
                if (h.Survived) res.Pool.Add(h);
            }

            res.Pool = res.Pool.OrderByDescending(h => h.OosEdgePct).ToList();
            res.Survivors = res.Pool.Count;
            res.IsValid = true;
            res.Note = res.Survivors == 0
                ? $"{res.Tested}개 검정 · 생존 0 (OOS 게이트 통과 없음 — 과최적화 함정을 피했습니다)"
                : $"{res.Tested}개 검정 · 생존 {res.Survivors} (효과크기 바 {bar:0.0}%p · OOS 검증 통과)";
            return res;
        }
    }
}
