// ============================================================================
// KrxArenaPortAdapter — v12.18.59-DEV: IKrxArenaPort 구현(KrxArenaEngine seqlock 판독 → 순수 DTO 투영)
//
//   UI 는 엔진을 new 하지 않는다 — 이 어댑터가 DI 싱글톤 엔진을 감싸 스냅샷/근거 피드를 DTO 로 내보낸다(콜드 경로 · 할당 허용).
//   ★liveArmed:false — 주문 경로 = 엔진 휴먼 가상 원장 큐(SubmitHuman)뿐. ★Fed=false(정산 0회) = 피드 미결선 정직 렌더.
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

        public KrxArenaPortAdapter(KrxArenaEngine engine) => _engine = engine ?? throw new ArgumentNullException(nameof(engine));

        public string ModeName => _engine.ModeName;

        public bool Fed => _engine.TryReadSnapshot(out var s) && s.Ticks > 0;

        public bool TryRead(out KrxArenaView view)
        {
            if (!_engine.TryReadSnapshot(out var s)) { view = default; return false; }
            view = new KrxArenaView(_engine.Version, s.Ticks, s.SessionOpen, s.MacroBp, s.RationaleCount, Map(s.Ai), Map(s.Human));
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

        public int SubmitHuman(long symbol, int side, long qty) => _engine.SubmitHuman(symbol, side, qty);

        private static KrxArenaBookView Map(in DuelBookStats b) => new(
            b.CapitalWon, b.CashWon, b.EquityWon, b.RealizedWon, b.UnrealizedWon, b.FeesWon, b.TaxWon,
            b.ReturnBp, b.MddBp, b.WinRateBp, b.Fills, b.Rejects, b.Wins, b.Losses, b.OpenPositions);
    }
}
