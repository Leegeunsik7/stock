// ============================================================================
// KrxArenaPortAdapter — v12.18.59-DEV: IKrxArenaPort 구현(KrxArenaEngine seqlock 판독 → 순수 DTO 투영)
//
//   UI 는 엔진을 new 하지 않는다 — 이 어댑터가 DI 싱글톤 엔진을 감싸 스냅샷/근거 피드를 DTO 로 내보낸다(콜드 경로 · 할당 허용).
//   ★liveArmed:false — 주문 경로 = 엔진 휴먼 가상 원장 큐(SubmitHuman)뿐. ★Fed=false(정산 0회) = 피드 미결선 정직 렌더.
//   [v12.18.60-DEV] 시세 주입기(KrxArenaQuoteFeeder) 결속: 출처 선택 · 공급 상태 · 수신 호가 · 진영 보유 투영.
//     주입기 미결속(null) = 출처 선택 거부 · Pumped=false(정직 렌더).
//   [v12.18.64-DEV] Catalyst(symbol) = 엔진 결속 플루토 버퍼 → ArenaCatalystMapper(마지막 정산 KST 기준 감쇄) — UI 뉴스 촉매 배지.
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;

namespace StockManager.Adapters
{
    public sealed class KrxArenaPortAdapter : IKrxArenaPort
    {
        private readonly KrxArenaEngine _engine;
        private readonly KrxArenaQuoteFeeder? _feeder;

        public KrxArenaPortAdapter(KrxArenaEngine engine, KrxArenaQuoteFeeder? feeder = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            if (feeder is not null && !ReferenceEquals(feeder.Engine, engine)) throw new ArgumentException("주입기 엔진 불일치", nameof(feeder));
            _feeder = feeder;
        }

        public string ModeName => _engine.ModeName;

        public bool Fed => _engine.TryReadSnapshot(out var s) && s.Ticks > 0;

        public long Version => _engine.Version;

        public KrxArenaFeedStatus Feed => _feeder is null
            ? new KrxArenaFeedStatus(KrxArenaFeed.Bridge, KrxArenaFeed.Bridge, false, 0, 0, 0)
            : new KrxArenaFeedStatus(_feeder.RequestedSource, _feeder.AppliedSource, _feeder.Pumps > 0, _feeder.Pumps, _feeder.BridgeUpdates, _feeder.DroppedBooks);

        public bool SelectSource(int source) => _feeder is not null && _feeder.SelectSource(source);

        public bool TryRead(out KrxArenaView view)
        {
            if (!_engine.TryReadSnapshot(out var s)) { view = default; return false; }
            DateTime? kst = s.LastKstTicks > 0 ? new DateTime(s.LastKstTicks, DateTimeKind.Unspecified) : null;
            view = new KrxArenaView(_engine.Version, s.Ticks, s.SessionOpen, s.MacroBp, s.RationaleCount, kst, s.DroppedQuotes, Map(s.Ai), Map(s.Human));
            return true;
        }

        public IReadOnlyList<KrxRationaleView> RecentRationales(int max)
        {
            if (max <= 0) return Array.Empty<KrxRationaleView>();
            var buf = new AiTradeRationale[Math.Min(max, KrxArenaEngine.RationaleCapacity)];
            int n = _engine.CopyRationales(buf);
            var list = new List<KrxRationaleView>(n);
            for (int i = n - 1; i >= 0; i--)
            {
                ref readonly var r = ref buf[i];
                list.Add(new KrxRationaleView(r.Seq, r.Symbol, r.Side, r.Qty, r.PriceWon, r.AtKst, ArenaTriggerText.Label(r.TriggerType),
                    r.SentimentScore, r.ConfidenceScore, r.TargetPrice, r.StopLossPrice, r.RiskRewardRatio, r.RealizedPnlWon, r.HumanReadableSummary));
            }
            return list;
        }

        public IReadOnlyList<KrxArenaQuoteView> Quotes()
        {
            var buf = new DuelQuote[KrxArenaEngine.MaxSymbols];
            int n = _engine.CopyQuotes(buf);
            var list = new List<KrxArenaQuoteView>(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly var q = ref buf[i];
                if (q.Symbol > 0 && q.BidWon > 0) list.Add(new KrxArenaQuoteView(q.Symbol, q.BidWon, q.AskWon, q.BidQty, q.AskQty));
            }
            list.Sort(static (a, b) => a.Symbol.CompareTo(b.Symbol));
            return list;
        }

        public IReadOnlyList<KrxArenaPositionView> Positions(int side)
        {
            var buf = new DuelPosition[KrxArenaEngine.MaxPositions];
            int n = _engine.CopyPositions(side, buf);
            var list = new List<KrxArenaPositionView>(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly var p = ref buf[i];
                list.Add(new KrxArenaPositionView(p.Symbol, p.Qty, p.AvgPriceWon, p.CostWon, p.MarkWon, p.TargetPrice, p.StopLossPrice));
            }
            list.Sort(static (a, b) => a.Symbol.CompareTo(b.Symbol));
            return list;
        }

        public int SubmitHuman(long symbol, int side, long qty) => _engine.SubmitHuman(symbol, side, qty);

        public ArenaCatalystView? Catalyst(long symbol)
            => _engine.TryReadSnapshot(out var s) ? ArenaCatalystMapper.Project(_engine.PlutoBuffer, symbol, s.LastKstTicks) : null;

        private static KrxArenaBookView Map(in DuelBookStats b) => new(
            b.CapitalWon, b.CashWon, b.EquityWon, b.RealizedWon, b.UnrealizedWon, b.FeesWon, b.TaxWon,
            b.ReturnBp, b.MddBp, b.WinRateBp, b.Fills, b.Rejects, b.Wins, b.Losses, b.OpenPositions);
    }
}
