// ============================================================================
// IntelAdapters — Phase 94-O: IntelHub Port 호스트 어댑터(AI/Compliance/Journal/Debate)
//   AiService·ComplianceGate·CausalJournal·DebateService/DebateLog 를 격리. ★liveArmed:false: 분석/정보 전용.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Intel;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket

namespace MS2E.Infrastructure
{
    // ── ① AI 어시스턴트 ───────────────────────────────────────────────────────
    public sealed class AiAssistantAdapter : IAiAssistantPort
    {
        private readonly AiService _ai;
        private readonly LocalDatabase _db;
        public AiAssistantAdapter(AiService ai, LocalDatabase db) { _ai = ai; _db = db; }

        public bool HasKey => _ai.HasKey;
        public bool HasLlm => _ai.HasLlm;
        public string ActiveLlm => _ai.ActiveLlm;
        public Task<string> AnalyzeAsync(string symbol, string market, string model) => _ai.AnalyzeAsync(symbol, market, model);
        public Task<string> AskAsync(string question, decimal usdKrw) => _ai.AskAsync(question, usdKrw);
        public async Task<IMemoryAnswerView> AskWithMemoryAsync(string question, decimal usdKrw)
        {
            var r = await _ai.AskWithMemoryAsync(question, usdKrw);
            return new MemAnswer(r.Answer, r.Snippets);
        }
        public Task<string> MorningBriefAsync(decimal usdKrw) => _ai.MorningBriefAsync(usdKrw, pushTelegram: false);
        public Task<string> ExplainDayAsync(string dayContext, string model) => _ai.ExplainDayAsync(dayContext, model);

        public bool GetBriefEnabled() => _db.GetSetting("Brief.Enabled") == "true";
        public void SetBriefEnabled(bool on) => _db.SetSetting("Brief.Enabled", on ? "true" : "false");
        public int GetBriefHour() => int.TryParse(_db.GetSetting("Brief.Hour"), out var h) ? Math.Clamp(h, 0, 23) : 8;
        public void SetBriefHour(int hour) => _db.SetSetting("Brief.Hour", Math.Clamp(hour, 0, 23).ToString());

        private sealed record MemAnswer(string Answer, IReadOnlyList<string> Snippets) : IMemoryAnswerView;
    }

    // ── ② 컴플라이언스 게이트(세션 sticky 락 — 싱글턴) ─────────────────────────
    public sealed class ComplianceGateAdapter : IComplianceGatePort
    {
        private readonly ComplianceGate _gate;
        public ComplianceGateAdapter(ComplianceGate gate) => _gate = gate;

        public bool IsLocked => _gate.IsLocked;
        public bool AdmitQuestion(string question) => _gate.Admit(ComplianceClassifier.Classify(question));
        public string RefusalNotice => ComplianceGate.RefusalNotice;
    }

    // ── ③ 인과 저널 ───────────────────────────────────────────────────────────
    public sealed class JournalAdapter : IJournalPort
    {
        private readonly LocalDatabase _db;
        private readonly AiService _ai;
        public JournalAdapter(LocalDatabase db, AiService ai) { _db = db; _ai = ai; }

        public IReadOnlyList<IJournalDayView> BuildTimeline(int rangeDays, string? symbol)
        {
            var to = DateTime.Now;
            var from = to.AddDays(-Math.Max(1, rangeDays));
            var sym = string.IsNullOrWhiteSpace(symbol) ? null : symbol.Trim();
            var events = CausalJournal.BuildTimeline(_db, from, to, sym);
            return CausalJournal.GroupByDay(_db, events);   // List<JournalDay> → 공변
        }

        public Task<string> ExplainDayAsync(IJournalDayView day, string model)
        {
            var jd = (JournalDay)day;   // 뷰는 BuildTimeline 이 반환한 JournalDay 인스턴스
            var ctx = CausalJournal.BuildDayContext(_db, jd);
            return _ai.ExplainDayAsync(ctx, model);
        }
    }

    // ── ④ 멀티에이전트 토론 ────────────────────────────────────────────────────
    public sealed class DebateAdapter : IDebatePort
    {
        private readonly AiService _ai;
        private readonly IQuoteProvider _prov;
        private readonly LocalDatabase _db;
        public DebateAdapter(AiService ai, IQuoteProvider prov, LocalDatabase db) { _ai = ai; _prov = prov; _db = db; }

        public int PendingCount() => DebateLog.PendingCount(_db);
        public IBullBearRecordView Stats() => DebateLog.Stats(_db);

        public async Task<IDebateResultView> RunAsync(string symbol, PfMarket market, string model)
        {
            var candles = await _prov.GetHistoryAsync(symbol, (Market)(int)market, DateTime.Now.AddMonths(-6), DateTime.Now);
            if (candles.Count < 21) return new DebateResult { Ok = false, Error = "히스토리가 부족합니다(시세 제공자 확인)." };
            var rec = DebateLog.Stats(_db);
            var res = await DebateService.RunAsync(_ai, symbol, candles, rec, model);
            if (res.Ok)
                DebateLog.Record(_db, new LiveDebate
                {
                    IssuedAt = DateTime.Now.ToString("o"),
                    ResolveAt = DateTime.Now.AddDays(14).ToString("o"),
                    Symbol = symbol, Market = (int)market, Stance = res.Stance,
                    BasePrice = (double)candles[^1].Close
                });
            return res;
        }

        public async Task ResolveAsync()
        {
            var due = DebateLog.Load(_db).Where(d => !d.Resolved && DateTime.TryParse(d.ResolveAt, out var ra) && ra <= DateTime.Now).ToList();
            var px = new Dictionary<(string, int), decimal>();
            foreach (var grp in due.GroupBy(d => (d.Symbol, d.Market)))
            {
                try
                {
                    var q = await _prov.GetQuotesAsync(new[] { grp.Key.Symbol }, (Market)grp.Key.Market);
                    var pr = q.FirstOrDefault()?.Price ?? 0m;
                    if (pr > 0) px[(grp.Key.Symbol, grp.Key.Market)] = pr;
                }
                catch { }
            }
            DebateLog.ResolveDue(_db, (s, m) => px.TryGetValue((s, (int)m), out var p) ? p : 0m, DateTime.Now);
        }
    }
}
