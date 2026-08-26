// ============================================================================
// ReplayService - 결정 품질(Decision Quality) 리플레이 (외부 의존 0, 로컬 DB만)
//  · 실제 자산 추이: Dashboard.Snapshots(일별 총자산) → equity 시계열
//  · 시그널 추종 분석: Signals(매수 시그널) vs Transactions(실제 매수)
//    - 시그널 후 7일 내 같은 종목을 실제 매수했으면 'Followed'
//    - 진입가 추정: Buy 시그널의 TargetPrice = 종가×1.05 → 종가 ≈ Target/1.05
//  · '무시한 시그널의 가상 수익률'은 페이지에서 현재가를 조회해 채운다(HypoReturnPct).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    public record EquityPoint(string Date, decimal Total);

    public class SignalReview
    {
        public DateTime At;
        public string Symbol = "", Name = "", Strategy = "";
        public SignalType Type;
        public Market Market;
        public decimal EntryEst;     // 시그널 시점 추정 진입가
        public bool Followed;        // 시그널 후 7일 내 실제 매수 존재
        public decimal Current;      // 현재가(페이지에서 시세 조회로 채움), 0=미상
        public decimal HypoReturnPct => EntryEst > 0 && Current > 0 ? (Current - EntryEst) / EntryEst * 100m : 0m;
    }

    public class ReplayReport
    {
        public List<EquityPoint> Equity = new();
        public List<SignalReview> Signals = new();
        public int BuyCount, FollowedCount;
        public decimal FollowRate => BuyCount > 0 ? (decimal)FollowedCount / BuyCount * 100m : 0m;
    }

    public static class ReplayService
    {
        public static ReplayReport Build(LocalDatabase db)
        {
            var rep = new ReplayReport();

            // 실제 자산 추이 (백그라운드/대시보드가 적재한 일별 스냅샷)
            try
            {
                var j = db.GetSetting("Dashboard.Snapshots");
                if (!string.IsNullOrWhiteSpace(j))
                {
                    var snaps = JsonSerializer.Deserialize<Dictionary<string, decimal>>(j) ?? new();
                    rep.Equity = snaps.OrderBy(k => k.Key).Select(k => new EquityPoint(k.Key, k.Value)).ToList();
                }
            }
            catch { }

            // 실제 매수 거래 목록
            var buys = new List<(string sym, DateTime at)>();
            try
            {
                var txDt = db.Query("SELECT Symbol, ExecutedAt FROM Transactions WHERE Type=0");   // Buy=0
                buys = txDt.Rows.Cast<DataRow>()
                    .Select(r => (Safe.Str(r["Symbol"]), Safe.DT(r["ExecutedAt"]))).ToList();
            }
            catch { }

            // 매수 시그널(StrongBuy=0, Buy=1) 리뷰
            try
            {
                var sigDt = db.Query("SELECT * FROM Signals WHERE Signal IN (0,1) ORDER BY GeneratedAt DESC LIMIT 50");
                foreach (DataRow r in sigDt.Rows)
                {
                    var sym = Safe.Str(r["Symbol"]);
                    var at = Safe.DT(r["GeneratedAt"]);
                    var target = Safe.Dec(r["TargetPrice"]);
                    var review = new SignalReview
                    {
                        At = at, Symbol = sym, Name = Safe.Str(r["Name"]), Strategy = Safe.Str(r["Strategy"]),
                        Type = (SignalType)Safe.Int(r["Signal"]),
                        EntryEst = target > 0 ? target / 1.05m : 0m,
                        Market = sym.Length == 6 && sym.Length > 0 && char.IsDigit(sym[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ,
                        Followed = buys.Any(b => string.Equals(b.sym, sym, StringComparison.OrdinalIgnoreCase)
                                                 && b.at >= at && (b.at - at).TotalDays <= 7)
                    };
                    rep.Signals.Add(review);
                    rep.BuyCount++;
                    if (review.Followed) rep.FollowedCount++;
                }
            }
            catch { }

            return rep;
        }
    }
}
