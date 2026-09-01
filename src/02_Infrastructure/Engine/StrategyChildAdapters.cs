// ============================================================================
// StrategyChildAdapters — Phase 94-O: StrategyHub 잔여 자식 compute Port 호스트 어댑터
//   PositionSizer/EventStudy/HypothesisMiner 를 시세 fetch + 엔진 계산으로 격리(94-C Quant 패턴).
//   ★liveArmed:false: 정보 제공 전용(실 발주 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket
using MS2E.UI.Portable.Ports.Strategy;

namespace MS2E.Infrastructure
{
    public sealed class PositionSizerAdapter : IPositionSizerPort
    {
        private readonly IQuoteProvider _prov;
        public PositionSizerAdapter(IQuoteProvider prov) => _prov = prov;

        public async Task<ISizeResultView> SuggestAsync(string symbol, PfMarket market, int horizon, int targetVolPct, double kellyFrac, int maxWPct)
        {
            var candles = await _prov.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddMonths(-24), DateTime.Now);
            var closes = candles.Select(c => (double)c.Close).ToList();
            if (closes.Count < horizon + 10)
                return new SizeResult { IsValid = false, Note = "히스토리가 부족합니다(시세 제공자 확인)." };

            // horizon 미래수익 표본 + 일수익률 변동성(연환산) — 원본 SizerPage.RunAsync 계산 이관.
            var fwd = new List<double>();
            for (int i = 0; i + horizon < closes.Count; i++)
                if (closes[i] > 0) fwd.Add((closes[i + horizon] - closes[i]) / closes[i] * 100.0);
            var daily = new List<double>();
            for (int i = 1; i < closes.Count; i++)
                if (closes[i - 1] > 0) daily.Add((closes[i] - closes[i - 1]) / closes[i - 1] * 100.0);
            double mean = daily.Average();
            double vol = Math.Sqrt(daily.Select(r => (r - mean) * (r - mean)).Sum() / Math.Max(1, daily.Count - 1)) * Math.Sqrt(252.0);

            return PositionSizer.Suggest(fwd, vol, targetVolPct, Math.Clamp(kellyFrac, 0.1, 1.0), Math.Clamp(maxWPct, 5, 100) / 100.0);
        }
    }

    // ── ② 이벤트 스터디 어댑터(프리셋 소유 + 규칙 해석 + Analyze) ─────────────────
    public sealed class EventStudyAdapter : IEventStudyPort
    {
        private readonly IQuoteProvider _prov;
        private readonly LocalDatabase _db;
        private List<CompositeRule> _saved = new();

        public EventStudyAdapter(IQuoteProvider prov, LocalDatabase db) { _prov = prov; _db = db; }

        // 원본 EventStudyPage._presets 이관.
        private static readonly (string label, CompositeRule rule)[] Presets =
        {
            ("RSI(14) < 30 (과매도)", One(CondMetric.Rsi, 14, CondOp.Lte, 30)),
            ("RSI(14) > 70 (과매수)", One(CondMetric.Rsi, 14, CondOp.Gte, 70)),
            ("MA20 이격 ≤ -7% (낙폭과대)", One(CondMetric.MaGapPct, 20, CondOp.Lte, -7)),
            ("MA20 이격 ≥ +7% (과열)", One(CondMetric.MaGapPct, 20, CondOp.Gte, 7)),
            ("거래량 급증 ≥ 100%", One(CondMetric.VolumeSurgePct, 20, CondOp.Gte, 100)),
        };

        private static CompositeRule One(CondMetric m, int param, CondOp op, decimal thr) => new()
        {
            Name = "프리셋", Logic = RuleLogic.And,
            Conditions = new() { new CompositeCondition { Metric = m, Param = param, Op = op, Threshold = thr } }
        };

        private static string Describe(CompositeRule r) =>
            r == null || r.Conditions.Count == 0 ? "" :
            string.Join(r.Logic == RuleLogic.And ? " 그리고 " : " 또는 ", r.Conditions.Select(c => c.Describe()));

        public IReadOnlyList<IRuleChoiceView> GetRuleChoices()
        {
            _saved = CompositeAlertEngine.Load(_db);
            var choices = new List<RuleChoice>();
            for (int i = 0; i < Presets.Length; i++)
                choices.Add(new RuleChoice($"preset:{i}", Presets[i].label, Describe(Presets[i].rule), true));
            foreach (var r in _saved)
                choices.Add(new RuleChoice($"saved:{r.Id}", r.Name, Describe(r), false));
            return choices;
        }

        private CompositeRule? Resolve(string ruleId)
        {
            if (ruleId.StartsWith("preset:") && int.TryParse(ruleId.AsSpan(7), out var i) && i >= 0 && i < Presets.Length)
                return Presets[i].rule;
            if (ruleId.StartsWith("saved:"))
            {
                var id = ruleId.Substring(6);
                var hit = _saved.FirstOrDefault(r => r.Id == id);
                if (hit == null) { _saved = CompositeAlertEngine.Load(_db); hit = _saved.FirstOrDefault(r => r.Id == id); }
                return hit;
            }
            return null;
        }

        public async Task<IEventStudyResultView> AnalyzeAsync(string ruleId, string symbol, PfMarket market, int months)
        {
            var rule = Resolve(ruleId);
            if (rule == null) return new EventStudyResult { Note = "패턴을 선택하세요." };
            var candles = await _prov.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddMonths(-Math.Max(6, months)), DateTime.Now);
            return EventStudy.Analyze(rule, candles, new[] { 5, 10, 20 });
        }

        private sealed record RuleChoice(string Id, string Label, string Description, bool IsPreset) : IRuleChoiceView;
    }

    // ── ③ 가설 발굴 어댑터(발굴 + 채택[복합규칙 저장]) ─────────────────────────────
    public sealed class HypothesisMinerAdapter : IHypothesisMinerPort
    {
        private readonly IQuoteProvider _prov;
        private readonly LocalDatabase _db;
        private List<Hypothesis> _lastPool = new();   // Adopt 대상(최근 발굴). scoped 어댑터=회로별 격리.

        public HypothesisMinerAdapter(IQuoteProvider prov, LocalDatabase db) { _prov = prov; _db = db; }

        public async Task<IMiningResultView> MineAsync(string symbol, PfMarket market, int months, int horizon)
        {
            var candles = await _prov.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddMonths(-Math.Max(12, months)), DateTime.Now);
            var res = HypothesisMiner.Mine(candles, horizon);
            _lastPool = res.Pool;   // 채택용 캐시
            var pool = new List<HypRow>();
            for (int i = 0; i < res.Pool.Count; i++)
            {
                var h = res.Pool[i];
                pool.Add(new HypRow(i.ToString(), h.Label, h.TrainEdgePct, h.OosEdgePct, h.OosWinRatePct, h.OosN));
            }
            return new MiningResultView(res.IsValid, res.Tested, res.Survivors, res.SignificanceBar, res.Note, pool);
        }

        public (string title, string message) Adopt(int hypothesisIndex, string symbol)
        {
            if (hypothesisIndex < 0 || hypothesisIndex >= _lastPool.Count)
                return ("채택 실패", "가설을 찾을 수 없습니다(다시 발굴하세요).");
            var h = _lastPool[hypothesisIndex];
            var sym = (symbol ?? "").Trim().Split(' ')[0];
            var rules = CompositeAlertEngine.Load(_db);
            rules.Add(new CompositeRule
            {
                Name = $"🧬 {h.Label} ({sym})",
                AssetKind = "stock", Target = sym, Logic = h.Rule.Logic,
                Conditions = h.Rule.Conditions.Select(c => new CompositeCondition
                { Metric = c.Metric, Param = c.Param, Op = c.Op, Threshold = c.Threshold }).ToList()
            });
            CompositeAlertEngine.Save(_db, rules);
            return ("복합 알림 채택됨", $"{h.Label} → {sym} (OOS 엣지 {h.OosEdgePct:+0.0;-0.0}%p) · /composite 에서 확인");
        }

        private sealed record HypRow(string Id, string Label, double TrainEdgePct, double OosEdgePct,
            double OosWinRatePct, int OosN) : IHypothesisView;

        private sealed record MiningResultView(bool IsValid, int Tested, int Survivors, double SignificanceBar,
            string Note, IReadOnlyList<IHypothesisView> Pool) : IMiningResultView;
    }
}
