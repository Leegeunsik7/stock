// ============================================================================
// PlutoRecommendationPortAdapter — v12.18.78-DEV: 스크리너 불변 스냅샷 → IPlutoRecommendationPort DTO 투영(콜드 경로 · 판독 전용)
//   같은 스냅샷 참조 = 직전 투영 재사용(렌더 펌프 100ms 폴링에도 재매핑 0). 종목명 = StockDisplayNameResolver(미등록 = 코드 그대로).
//   [v12.18.80-DEV] 병렬 학습 스웜(선택) = 레인 상태 투영(Swarm · 판독마다 갱신 — 수집 진행률은 스냅샷 세대와 무관하게 변함).
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
        private readonly PlutoParallelLearningSwarm? _swarm;
        private Cache? _cache;

        public PlutoRecommendationPortAdapter(PlutoMarketScreener screener, PlutoParallelLearningSwarm? swarm = null)
        {
            _screener = screener ?? throw new ArgumentNullException(nameof(screener));
            _swarm = swarm;
        }

        public bool IsWired => true;

        public PlutoRecoBoardView Current
        {
            get
            {
                var snap = _screener.Board.Current;
                bool running = _screener.Running;
                var c = System.Threading.Volatile.Read(ref _cache);
                PlutoRecoBoardView view;
                if (c is not null && ReferenceEquals(c.Source, snap) && c.Running == running) view = c.View;
                else
                {
                    view = c is not null && ReferenceEquals(c.Source, snap) ? c.View with { Running = running } : Map(snap, running);
                    System.Threading.Volatile.Write(ref _cache, new Cache(snap, running, view));
                }
                if (_swarm is null) return view;
                var sw = MapSwarm(_swarm);
                return view.Swarm == sw ? view : view with { Swarm = sw };
            }
        }

        public static PlutoRecoBoardView Map(PlutoRecommendationSnapshot s, bool running)
        {
            ArgumentNullException.ThrowIfNull(s);
            return new PlutoRecoBoardView(running, s.Generation,
                s.Generation == 0 ? null : new DateTime(s.UpdatedUtcTicks, DateTimeKind.Utc),
                MapMarket(s, PlutoScreenMarket.Krx), MapMarket(s, PlutoScreenMarket.Us)) { Crypto = MapMarket(s, PlutoScreenMarket.Crypto) };
        }

        private static PlutoRecoMarketView MapMarket(PlutoRecommendationSnapshot s, PlutoScreenMarket m)
        {
            var src = s.Of(m);
            var cards = new List<PlutoRecommendationCard>(src.Count);
            foreach (var r in src) cards.Add(Map(r));
            var t = s.TallyOf(m);
            return new PlutoRecoMarketView(cards.AsReadOnly(), t.Scanned, t.Vetoed, t.Insufficient, t.BelowThreshold, t.Monitored, t.Passed);
        }

        public static PlutoSwarmView MapSwarm(PlutoParallelLearningSwarm swarm)
        {
            ArgumentNullException.ThrowIfNull(swarm);
            return new PlutoSwarmView(true, swarm.Running, swarm.DutyPercent,
                MapLane(swarm.StatusOf(PlutoScreenMarket.Krx)), MapLane(swarm.StatusOf(PlutoScreenMarket.Us)), MapLane(swarm.StatusOf(PlutoScreenMarket.Crypto)));
        }

        private static PlutoSwarmLaneView MapLane(in PlutoSwarmLaneStatus s)
        {
            int market = s.Market switch { PlutoScreenMarket.Us => PlutoRecoMarket.Us, PlutoScreenMarket.Crypto => PlutoRecoMarket.Crypto, _ => PlutoRecoMarket.Krx };
            return new PlutoSwarmLaneView(market, s.Connected, s.Label, s.Monitored, s.Live, s.Awaiting, s.NoData, s.Faulted, s.Sweeping, s.CoolingDown,
                s.Stats.Fetches, s.Stats.RateLimited);
        }

        public static PlutoRecommendationCard Map(PlutoRecommendation r)
        {
            ArgumentNullException.ThrowIfNull(r);
            int market = r.Market switch { PlutoScreenMarket.Us => PlutoRecoMarket.Us, PlutoScreenMarket.Crypto => PlutoRecoMarket.Crypto, _ => PlutoRecoMarket.Krx };
            string name = StockDisplayNameResolver.Name(r.Code) ?? PlutoMultiAssetUniverseCatalog.NameOf(r.Market, r.Symbol) ?? r.Code;   // 미등록 = 코드(날조 0)
            var sc = r.Score;
            return new PlutoRecommendationCard(market, r.Rank, r.Symbol, r.Code, name, sc.Total,
                sc.ValuationPts, sc.TechnicalPts, sc.MicroPts, sc.MacroPts, r.Reasons,
                r.HasValuation, r.MarginOfSafetyBps, PlutoDiscoveryScorer.PriceText(r.Market, r.Price),
                r.HasValuation ? PlutoDiscoveryScorer.PriceText(r.Market, r.FairPrice) : "", r.SeedProfile,
                r.RecommendedQty, sc.RiskCount, r.RiskText);
        }
    }
}
