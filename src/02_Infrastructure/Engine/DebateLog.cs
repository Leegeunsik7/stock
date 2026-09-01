// ============================================================================
// DebateLog / DebateScore - 토론 판정 기록·승률 채점 (Season 4 ②) (v3.35.0)
//  심판 판정(stance)을 발행 시 저장하고 만기에 실현으로 채점 → '승률 가중'의 근거.
//  DebateScore(순수): 강세 판정/약세 판정 각각의 적중률 계산(심판 자기보정용).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    public class LiveDebate
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public string IssuedAt { get; set; } = "";
        public string ResolveAt { get; set; } = "";
        public string Symbol { get; set; } = "";
        public int Market { get; set; }
        public int Stance { get; set; }          // +1/0/-1
        public double BasePrice { get; set; }
        public bool Resolved { get; set; }
        public double RealizedPct { get; set; }
    }

    /// <summary>강세/약세 판정별 적중 기록(승률 가중에 주입).</summary>
    public class BullBearRecord : MS2E.UI.Portable.Ports.Intel.IBullBearRecordView   // Phase 94-O: 공변(BullishN/BearishN/HitPct 암시)
    {
        public int BullishN { get; set; }
        public int BullishHit { get; set; }
        public int BearishN { get; set; }
        public int BearishHit { get; set; }
        public double BullishHitPct => BullishN > 0 ? (double)BullishHit / BullishN * 100 : 0;
        public double BearishHitPct => BearishN > 0 ? (double)BearishHit / BearishN * 100 : 0;
        public int ResolvedN => BullishN + BearishN;
    }

    public static class DebateScore
    {
        /// <summary>해결된 (판정, 실현수익) 표본 → 방향별 적중률. 순수.</summary>
        public static BullBearRecord Compute(IEnumerable<(int stance, double realizedPct)> resolved)
        {
            var r = new BullBearRecord();
            foreach (var (stance, realized) in resolved ?? Enumerable.Empty<(int, double)>())
            {
                if (stance > 0) { r.BullishN++; if (realized > 0) r.BullishHit++; }
                else if (stance < 0) { r.BearishN++; if (realized < 0) r.BearishHit++; }
            }
            return r;
        }
    }

    public static class DebateLog
    {
        private const string Key = "Debate.Log";

        public static List<LiveDebate> Load(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting(Key);
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<List<LiveDebate>>(j) ?? new();
            }
            catch { }
            return new();
        }

        public static void Save(LocalDatabase db, List<LiveDebate> list)
        {
            try { if (list.Count > 300) list = list.Skip(list.Count - 300).ToList(); db.SetSetting(Key, JsonSerializer.Serialize(list)); }
            catch { }
        }

        public static void Record(LocalDatabase db, LiveDebate d) { var l = Load(db); l.Add(d); Save(db, l); }

        public static int ResolveDue(LocalDatabase db, Func<string, Market, decimal> priceNow, DateTime now)
        {
            var l = Load(db); int n = 0;
            foreach (var d in l.Where(x => !x.Resolved))
            {
                if (!DateTime.TryParse(d.ResolveAt, out var ra) || ra > now) continue;
                decimal px = 0; try { px = priceNow(d.Symbol, (Market)d.Market); } catch { }
                if (px > 0 && d.BasePrice > 0) { d.RealizedPct = ((double)px - d.BasePrice) / d.BasePrice * 100.0; d.Resolved = true; n++; }
            }
            if (n > 0) Save(db, l);
            return n;
        }

        public static BullBearRecord Stats(LocalDatabase db) =>
            DebateScore.Compute(Load(db).Where(d => d.Resolved).Select(d => (d.Stance, d.RealizedPct)));

        public static int PendingCount(LocalDatabase db) => Load(db).Count(d => !d.Resolved);
    }
}
