// ============================================================================
// CryptoArenaPortAdapter — v12.18.66-DEV: ICryptoArenaPort 구현(UpbitCryptoArenaEngine seqlock 판독 → 순수 DTO 투영)
//
//   UI 는 엔진을 new 하지 않는다 — 이 어댑터가 DI 싱글톤 엔진(+시세 주입기)을 감싸 스냅샷/근거 피드/호가/보유를 DTO 로 내보낸다(콜드 경로 · 할당 허용).
//   마켓 코드 문자열 ↔ 엔진 long 키 변환은 여기서만(CryptoArenaUniverse). 표 밖 마켓 주문 = Invalid(엔진 미도달).
//   ★liveArmed:false — 주문 경로 = 엔진 휴먼 가상 원장 큐(SubmitHuman)뿐(업비트 주문 API 송출 0). ★Fed=false(정산 0회) = 피드 미결선 정직 렌더.
//   주입기 미결속(null) = 출처 선택 거부 · Pumped=false(정직 렌더).
//   [v12.18.68-DEV] 플루토 프로 거버너: SetGovernanceEnabled → 엔진 EnableGovernance(Volatile 플래그 · 다음 정산부터 · 핫패스 무변경) ·
//     Governance/스냅샷 = 거버너 계수 + 수신 종목 국면 대표값(패닉 우선) · 근거 Governance = ArenaGovernanceMapper.Card(감점 태그 · 자가 회고).
//   [v12.18.70-DEV] GetClosedTrades = 근거 링 매도 근거 → ArenaClosedTradeMapper(청산 저널 · 진입가 · 수익률 · 한글 종목명).
//   [v12.18.71-DEV] 카피 트레이딩(FollowAiTrade → 엔진 SubmitFollow · SetAutoCopyEnabled · CopyTrading) = ArenaCopyMapper(24/7 시장 = 개장 게이트 없음 · 기본 구현).
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;

namespace StockManager.Adapters
{
    public sealed class CryptoArenaPortAdapter : ICryptoArenaPort
    {
        private static readonly IReadOnlyList<string> s_markets = Array.AsReadOnly(BuildMarkets());

        private readonly UpbitCryptoArenaEngine _engine;
        private readonly CryptoArenaQuoteFeeder? _feeder;

        public CryptoArenaPortAdapter(UpbitCryptoArenaEngine engine, CryptoArenaQuoteFeeder? feeder = null)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            if (feeder is not null && !ReferenceEquals(feeder.Engine, engine)) throw new ArgumentException("주입기 엔진 불일치", nameof(feeder));
            _feeder = feeder;
        }

        public string ModeName => _engine.ModeName;

        public bool Fed => _engine.TryReadSnapshot(out var s) && s.Ticks > 0;

        public long Version => _engine.Version;

        public IReadOnlyList<string> Markets => s_markets;

        public CryptoArenaFeedStatus Feed => _feeder is null
            ? new CryptoArenaFeedStatus(CryptoArenaFeed.UpbitStream, CryptoArenaFeed.UpbitStream, false, 0, false, false, 0, 0)
            : new CryptoArenaFeedStatus(_feeder.RequestedSource, _feeder.AppliedSource, _feeder.Pumps > 0, _feeder.Pumps, _feeder.StreamAttached,
                _feeder.StreamConnected, _feeder.StreamUpdates, _feeder.DroppedQuotes);

        public bool SelectSource(int source) => _feeder is not null && _feeder.SelectSource(source);

        public bool TryRead(out CryptoArenaView view)
        {
            if (!_engine.TryReadSnapshot(out var s)) { view = default; return false; }
            DateTime? kst = s.LastKstTicks > 0 ? new DateTime(s.LastKstTicks, DateTimeKind.Unspecified) : null;
            view = new CryptoArenaView(_engine.Version, s.Ticks, s.Trades, s.Tradable, s.KimchiPremiumBp, s.GlobalChangeBp, s.RationaleCount, kst,
                s.DroppedQuotes, Map(s.Ai), Map(s.Human), _engine.GovernanceEnabled, CurrentRegime());
            return true;
        }

        public IReadOnlyList<CryptoRationaleView> RecentRationales(int max)
        {
            if (max <= 0) return Array.Empty<CryptoRationaleView>();
            var buf = new AiTradeRationale[Math.Min(max, UpbitCryptoArenaEngine.RationaleCapacity)];
            int n = _engine.CopyRationales(buf);
            var list = new List<CryptoRationaleView>(n);
            for (int i = n - 1; i >= 0; i--)
            {
                ref readonly var r = ref buf[i];
                list.Add(new CryptoRationaleView(r.Seq, CryptoArenaUniverse.Label(r.Symbol), CryptoArenaUniverse.CoinOf(r.Symbol), r.Side, r.Qty, r.PriceWon, r.AtKst,
                    ArenaTriggerText.Label(r.TriggerType), r.SentimentScore, r.ConfidenceScore, r.TargetPrice, r.StopLossPrice, r.RiskRewardRatio, r.RealizedPnlWon,
                    r.ImbalanceBp, r.MomentumBp, r.SurgeBp, r.MacroBp, r.BidDepthWon, r.AskDepthWon, r.HumanReadableSummary,
                    ArenaGovernanceMapper.Card(in r.Governance)));
            }
            return list;
        }

        public IReadOnlyList<ArenaClosedTradeView> GetClosedTrades(int max = 50)
        {
            if (max <= 0) return Array.Empty<ArenaClosedTradeView>();
            var buf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            int n = _engine.CopyRationales(buf);
            return ArenaClosedTradeMapper.Collect(buf.AsSpan(0, n), max, static s => CryptoArenaUniverse.Label(s), CryptoArenaUnits.Scale);
        }

        public ArenaCopyResult FollowAiTrade(long rationaleSeq, decimal copyRatio = 1.0m)
        {
            var buf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            int n = _engine.CopyRationales(buf);
            bool ok = _engine.TryReadSnapshot(out var s);
            return ArenaCopyMapper.Follow(buf.AsSpan(0, n), rationaleSeq, copyRatio, ok, in s.Ai, in s.Human, _engine.SubmitFollow, UpbitCryptoArenaEngine.CopyCode);
        }

        public bool SetAutoCopyEnabled(bool enabled, decimal ratio = 1.0m)
            => ArenaCopyMath.TryRatioBp(ratio, out int bp) && _engine.SetAutoCopy(enabled, bp);

        public ArenaCopyTradingView CopyTrading
            => ArenaCopyMapper.View(_engine.TryReadCopy(out var l), in l, _engine.AutoCopyEnabled, _engine.AutoCopyRatioBp, CryptoArenaUniverse.Label);

        public IReadOnlyList<CryptoArenaQuoteView> Quotes()
        {
            var buf = new DuelQuote[UpbitCryptoArenaEngine.MaxSymbols];
            int n = _engine.CopyQuotes(buf);
            var list = new List<CryptoArenaQuoteView>(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly var q = ref buf[i];
                int idx = CryptoArenaUniverse.IndexOf(q.Symbol);
                if (idx < 0 || q.BidWon <= 0) continue;
                list.Add(new CryptoArenaQuoteView(CryptoArenaUniverse.Market(idx), CryptoArenaUniverse.Coin(idx), q.BidWon, q.AskWon, q.BidQty, q.AskQty));
            }
            return list;
        }

        public IReadOnlyList<CryptoArenaPositionView> Positions(int side)
        {
            var buf = new DuelPosition[UpbitCryptoArenaEngine.MaxPositions];
            int n = _engine.CopyPositions(side, buf);
            var list = new List<CryptoArenaPositionView>(n);
            for (int i = 0; i < n; i++)
            {
                ref readonly var p = ref buf[i];
                list.Add(new CryptoArenaPositionView(CryptoArenaUniverse.Label(p.Symbol), CryptoArenaUniverse.CoinOf(p.Symbol), p.Qty, p.AvgPriceWon, p.CostWon,
                    p.MarkWon, p.TargetPrice, p.StopLossPrice));
            }
            list.Sort(static (a, b) => CryptoArenaUniverse.IndexOf(CryptoArenaUniverse.Pack(a.Market)).CompareTo(CryptoArenaUniverse.IndexOf(CryptoArenaUniverse.Pack(b.Market))));
            return list;
        }

        public int SubmitHuman(string market, int side, long qtyRaw)
        {
            long symbol = string.IsNullOrEmpty(market) ? 0 : CryptoArenaUniverse.Pack(market.AsSpan());
            return symbol == 0 ? CryptoArenaReject.Invalid : _engine.SubmitHuman(symbol, side, qtyRaw);
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
            var buf = new DuelQuote[UpbitCryptoArenaEngine.MaxSymbols];
            int n = _engine.CopyQuotes(buf);
            var symbols = new List<long>(n);
            for (int i = 0; i < n; i++) if (buf[i].Symbol > 0 && buf[i].BidWon > 0) symbols.Add(buf[i].Symbol);
            return ArenaGovernanceMapper.Aggregate(symbols, _engine.RegimeOf);
        }

        // ReturnBp = Int128 재계산(원 raw 10^8 × 10000 = long 범위 초과 — DuelBookStats.ReturnBp 는 원 단위 모드 전용).
        private static CryptoArenaBookView Map(in DuelBookStats b) => new(
            b.CapitalWon, b.CashWon, b.EquityWon, b.RealizedWon, b.UnrealizedWon, b.FeesWon,
            ReturnBp(b.EquityWon, b.CapitalWon), b.MddBp, b.WinRateBp, b.Fills, b.Rejects, b.Wins, b.Losses, b.OpenPositions);

        public static long ReturnBp(long equityRaw, long capitalRaw)
            => capitalRaw <= 0 ? 0 : (long)((Int128)(equityRaw - capitalRaw) * 10000 / capitalRaw);

        private static string[] BuildMarkets()
        {
            var t = new string[CryptoArenaUniverse.Count];
            for (int i = 0; i < t.Length; i++) t[i] = CryptoArenaUniverse.Market(i);
            return t;
        }

        public ArenaCouncilVetoFeed CouncilVetoes(int max = 20)
        {
            var buf = new ArenaVetoRecord[Math.Clamp(max, 0, ArenaCouncilVetoGate.Capacity)];
            int n = _engine.CopyCouncilVetoes(buf, out long blocked);
            return ArenaGovernanceMapper.CouncilFeed(_engine.CouncilAttached, buf.AsSpan(0, n), blocked, CryptoArenaUniverse.Label);
        }
    }
}
