// ============================================================================
// PlutoRecommendationPortAdapter — v12.18.78-DEV: 스크리너 불변 스냅샷 → IPlutoRecommendationPort DTO 투영(콜드 경로 · 판독 전용)
//   같은 스냅샷 참조 = 직전 투영 재사용(렌더 펌프 100ms 폴링에도 재매핑 0). 종목명 = StockDisplayNameResolver(미등록 = 코드 그대로).
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Cognitive.Screening;

namespace StockManager.Adapters
{
    public sealed class PlutoRecommendationPortAdapter : IPlutoRecommendationPort
    {
        private sealed record Cache(PlutoRecommendationSnapshot Source, bool Running, PlutoRecoBoardView View);

        private readonly PlutoMarketScreener _screener;
        private Cache? _cache;

        public PlutoRecommendationPortAdapter(PlutoMarketScreener screener) => _screener = screener ?? throw new ArgumentNullException(nameof(screener));

        public bool IsWired => true;

        public PlutoRecoBoardView Current
        {
            get
            {
                var snap = _screener.Board.Current;
                bool running = _screener.Running;
                var c = System.Threading.Volatile.Read(ref _cache);
                if (c is not null && ReferenceEquals(c.Source, snap) && c.Running == running) return c.View;
                var view = c is not null && ReferenceEquals(c.Source, snap) ? c.View with { Running = running } : Map(snap, running);
                System.Threading.Volatile.Write(ref _cache, new Cache(snap, running, view));
                return view;
            }
        }

        public static PlutoRecoBoardView Map(PlutoRecommendationSnapshot s, bool running)
        {
            ArgumentNullException.ThrowIfNull(s);
            return new PlutoRecoBoardView(running, s.Generation,
                s.Generation == 0 ? null : new DateTime(s.UpdatedUtcTicks, DateTimeKind.Utc),
                MapMarket(s, PlutoScreenMarket.Krx), MapMarket(s, PlutoScreenMarket.Us));
        }

        private static PlutoRecoMarketView MapMarket(PlutoRecommendationSnapshot s, PlutoScreenMarket m)
        {
            var src = s.Of(m);
            var cards = new List<PlutoRecommendationCard>(src.Count);
            foreach (var r in src) cards.Add(Map(r));
            var t = s.TallyOf(m);
            return new PlutoRecoMarketView(cards.AsReadOnly(), t.Scanned, t.Vetoed, t.Insufficient, t.BelowThreshold);
        }

        public static PlutoRecommendationCard Map(PlutoRecommendation r)
        {
            ArgumentNullException.ThrowIfNull(r);
            int market = r.Market == PlutoScreenMarket.Us ? PlutoRecoMarket.Us : PlutoRecoMarket.Krx;
            string name = StockDisplayNameResolver.NameOrCode(r.Code);
            var sc = r.Score;
            return new PlutoRecommendationCard(market, r.Rank, r.Symbol, r.Code, name, sc.Total,
                sc.ValuationPts, sc.TechnicalPts, sc.MicroPts, sc.MacroPts, r.Reasons,
                r.HasValuation, r.MarginOfSafetyBps, PlutoDiscoveryScorer.PriceText(r.Market, r.Price),
                r.HasValuation ? PlutoDiscoveryScorer.PriceText(r.Market, r.FairPrice) : "", r.SeedProfile,
                r.RecommendedQty, sc.RiskCount, r.RiskText);
        }
    }
}
