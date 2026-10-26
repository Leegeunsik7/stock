// ============================================================================
// UsArenaPortAdapter — v12.18.61-DEV: IUsArenaPort 구현(UsTechArenaEngine seqlock 판독 → 순수 DTO 투영)
//
//   UI 는 엔진을 new 하지 않는다 — 이 어댑터가 DI 싱글톤 엔진(+시세 주입기)을 감싸 스냅샷/근거 피드/호가/보유를 DTO 로 내보낸다(콜드 경로 · 할당 허용).
//   티커 문자열 ↔ 엔진 long 키 변환은 여기서만(UsTechUniverse). 표 밖 티커 주문 = Invalid(엔진 미도달).
//   ★liveArmed:false — 주문 경로 = 엔진 휴먼 가상 원장 큐(SubmitHuman)뿐(해외 주문 API 송출 0). ★Fed=false(정산 0회) = 피드 미결선 정직 렌더.
//   주입기 미결속(null) = 출처 선택 거부 · Pumped=false(정직 렌더).
//   [v12.18.64-DEV] Catalyst(ticker) = 엔진 결속 플루토 버퍼 → ArenaCatalystMapper(마지막 정산 KST 기준 감쇄) — UI 뉴스 촉매 배지.
//   [v12.18.68-DEV] 플루토 프로 거버너: SetGovernanceEnabled → 엔진 EnableGovernance(Volatile 플래그 · 다음 정산부터 · 핫패스 무변경) ·
//     Governance/스냅샷 = 거버너 계수 + 수신 종목 국면 대표값(패닉 우선) · 근거 Governance = ArenaGovernanceMapper.Card(감점 태그 · 자가 회고).
//   [v12.18.70-DEV] GetClosedTrades = 근거 링 매도 근거 → ArenaClosedTradeMapper(청산 저널 · 진입가 · 수익률 · 한글 종목명).
//   [v12.18.71-DEV] 카피 트레이딩(FollowAiTrade → 엔진 SubmitFollow · SetAutoCopyEnabled · CopyTrading) · 개장 게이트(OpeningAuction · OpeningGaps) = ArenaCopyMapper.
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Brokers.Kis;

namespace StockManager.Adapters
{
    public sealed class UsArenaPortAdapter : IUsArenaPort
    {
        private static readonly IReadOnlyList<string> s_tickers = Array.AsReadOnly(BuildTickers());

        private readonly UsTechArenaEngine _engine;
        private readonly UsArenaQuoteFeeder? _feeder;

        public UsArenaPortAdapter(UsTechArenaEngine engine, UsArenaQuoteFeeder? feeder = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            if (feeder is not null && !ReferenceEquals(feeder.Engine, engine)) throw new ArgumentException("주입기 엔진 불일치", nameof(feeder));
            _feeder = feeder;
        }

        public string ModeName => _engine.ModeName;

        public bool Fed => _engine.TryReadSnapshot(out var s) && s.Ticks > 0;

        public long Version => _engine.Version;

        public IReadOnlyList<string> Tickers => s_tickers;

        public UsArenaFeedStatus Feed => _feeder is null
            ? new UsArenaFeedStatus(UsArenaFeed.KisOverseas, UsArenaFeed.KisOverseas, false, 0, 0, 0)
            : new UsArenaFeedStatus(_feeder.RequestedSource, _feeder.AppliedSource, _feeder.Pumps > 0, _feeder.Pumps, _feeder.KisUpdates, _feeder.DroppedQuotes);

        public bool SelectSource(int source) => _feeder is not null && _feeder.SelectSource(source);

        public bool TryRead(out UsArenaView view)
        {
            if (!_engine.TryReadSnapshot(out var s)) { view = default; return false; }
            DateTime? kst = s.LastKstTicks > 0 ? new DateTime(s.LastKstTicks, DateTimeKind.Unspecified) : null;
            view = new UsArenaView(_engine.Version, s.Ticks, (int)s.Phase, s.Tradable, s.QqqBp, s.SoxxBp, s.Us10yChangeBp, s.RationaleCount, kst,
                s.DroppedQuotes, Map(s.Ai), Map(s.Human), _engine.GovernanceEnabled, CurrentRegime());
            return true;
        }

        public IReadOnlyList<UsRationaleView> RecentRationales(int max)
        {
            if (max <= 0) return Array.Empty<UsRationaleView>();
            var buf = new AiTradeRationale[Math.Min(max, UsTechArenaEngine.RationaleCapacity)];
            int n = _engine.CopyRationales(buf);
            var list = new List<UsRationaleView>(n);
            for (int i = n - 1; i >= 0; i--)
            {
                ref readonly var r = ref buf[i];
                list.Add(new UsRationaleView(r.Seq, UsTechUniverse.Unpack(r.Symbol), r.Side, r.Qty, r.PriceWon, r.AtKst, ArenaTriggerText.Label(r.TriggerType),
                    r.SentimentScore, r.ConfidenceScore, r.TargetPrice, r.StopLossPrice, r.RiskRewardRatio, r.RealizedPnlWon,
                    r.SurgeBp, r.MacroBp, r.ImbalanceBp, r.MomentumBp, r.HumanReadableSummary, ArenaGovernanceMapper.Card(in r.Governance)));
            }
            return list;
        }

        public IReadOnlyList<ArenaClosedTradeView> GetClosedTrades(int max = 50)
        {
            if (max <= 0) return Array.Empty<ArenaClosedTradeView>();
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            int n = _engine.CopyRationales(buf);
            return ArenaClosedTradeMapper.Collect(buf.AsSpan(0, n), max, static s => UsTechUniverse.Unpack(s), 1);
        }

        public ArenaCopyResult FollowAiTrade(long rationaleSeq, decimal copyRatio = 1.0m)
        {
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            int n = _engine.CopyRationales(buf);
            bool ok = _engine.TryReadSnapshot(out var s);
            return ArenaCopyMapper.Follow(buf.AsSpan(0, n), rationaleSeq, copyRatio, ok, in s.Ai, in s.Human, _engine.SubmitFollow, UsTechArenaEngine.CopyCode);
        }

        public bool SetAutoCopyEnabled(bool enabled, decimal ratio = 1.0m)
            => ArenaCopyMath.TryRatioBp(ratio, out int bp) && _engine.SetAutoCopy(enabled, bp);

        public ArenaCopyTradingView CopyTrading
            => ArenaCopyMapper.View(_engine.TryReadCopy(out var l), in l, _engine.AutoCopyEnabled, _engine.AutoCopyRatioBp, UsTechUniverse.Unpack);

        public ArenaOpeningAuctionView OpeningAuction => ArenaCopyMapper.Auction(_engine.TryReadAuction(out var a), in a);

        public IReadOnlyList<ArenaOpenGapView> OpeningGaps()
        {
            var list = new List<ArenaOpenGapView>(UsTechArenaEngine.MaxSymbols);
            for (int i = 0; i < UsTechArenaEngine.MaxSymbols; i++)
            {
                long sym = UsTechUniverse.Symbol(i);
                if (_engine.TryReadOpenGap(sym, out var g) && ArenaCopyMapper.TryGap(UsTechUniverse.Unpack(sym), in g, out var view)) list.Add(view);
            }
            return list;
        }

        public IReadOnlyList<UsArenaQuoteView> Quotes()
        {
            var buf = new DuelQuote[UsTechArenaEngine.MaxSymbols];
            int n = _engine.CopyQuotes(buf);
            var list = new List<UsArenaQuoteView>(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly var q = ref buf[i];
                int idx = UsTechUniverse.IndexOf(q.Symbol);
                if (idx < 0 || q.BidWon <= 0) continue;
                list.Add(new UsArenaQuoteView(UsTechUniverse.Ticker(idx), UsExchangeCode.TradeCode(UsTechUniverse.Exchange(idx)), q.BidWon, q.AskWon, q.BidQty, q.AskQty));
            }
            return list;
        }

        public IReadOnlyList<UsArenaPositionView> Positions(int side)
        {
            var buf = new DuelPosition[UsTechArenaEngine.MaxPositions];
            int n = _engine.CopyPositions(side, buf);
            var list = new List<UsArenaPositionView>(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly var p = ref buf[i];
                list.Add(new UsArenaPositionView(UsTechUniverse.Unpack(p.Symbol), p.Qty, p.AvgPriceWon, p.CostWon, p.MarkWon, p.TargetPrice, p.StopLossPrice));
            }
            list.Sort(static (a, b) => UsTechUniverse.IndexOf(UsTechUniverse.Pack(a.Ticker)).CompareTo(UsTechUniverse.IndexOf(UsTechUniverse.Pack(b.Ticker))));
            return list;
        }

        public int SubmitHuman(string ticker, int side, long qty)
        {
            long symbol = string.IsNullOrEmpty(ticker) ? 0 : UsTechUniverse.Pack(ticker.AsSpan());
            return symbol == 0 ? UsArenaReject.Invalid : _engine.SubmitHuman(symbol, side, qty);
        }

        public ArenaCatalystView? Catalyst(string ticker)
        {
            long symbol = string.IsNullOrEmpty(ticker) ? 0 : UsTechUniverse.Pack(ticker.AsSpan());
            if (symbol == 0 || !_engine.TryReadSnapshot(out var s)) return null;
            return ArenaCatalystMapper.Project(_engine.PlutoBuffer, symbol, s.LastKstTicks);
        }

        public bool SetGovernanceEnabled(bool enabled)
        {
            _engine.EnableGovernance(enabled);
            return true;
        }

        public ArenaGovernanceView Governance
            => ArenaGovernanceMapper.View(_engine.TryReadGovernor(out var g), in g, _engine.GovernanceEnabled, CurrentRegime(), LastKstTicks());

        private long LastKstTicks() => _engine.TryReadSnapshot(out var s) ? s.LastKstTicks : 0;

        /// <summary>수신 종목 국면 대표값(콜드 · 종목 표 순회 · 패닉 우선).</summary>
        private int CurrentRegime()
        {
            var buf = new DuelQuote[UsTechArenaEngine.MaxSymbols];
            int n = _engine.CopyQuotes(buf);
            var symbols = new List<long>(n);
            for (int i = 0; i < n; i++) if (buf[i].Symbol > 0 && buf[i].BidWon > 0) symbols.Add(buf[i].Symbol);
            return ArenaGovernanceMapper.Aggregate(symbols, _engine.RegimeOf);
        }

        private static UsArenaBookView Map(in DuelBookStats b) => new(
            b.CapitalWon, b.CashWon, b.EquityWon, b.RealizedWon, b.UnrealizedWon, b.FeesWon, b.TaxWon,
            b.ReturnBp, b.MddBp, b.WinRateBp, b.Fills, b.Rejects, b.Wins, b.Losses, b.OpenPositions);

        private static string[] BuildTickers()
        {
            var t = new string[UsTechUniverse.Count];
            for (int i = 0; i < t.Length; i++) t[i] = UsTechUniverse.Ticker(i);
            return t;
        }

        public ArenaCouncilVetoFeed CouncilVetoes(int max = 20)
        {
            var buf = new ArenaVetoRecord[Math.Clamp(max, 0, ArenaCouncilVetoGate.Capacity)];
            int n = _engine.CopyCouncilVetoes(buf, out long blocked);
            return ArenaGovernanceMapper.CouncilFeed(_engine.CouncilAttached, buf.AsSpan(0, n), blocked, UsTechUniverse.Unpack);
        }
    }
}
