// ============================================================================
// MeanReversionStrategy — [v12.18.15-DEV] 페르소나 3: VWAP 이탈 역추세(δ=0)
//
//  진입: VWAP 예열(20체결) 후 (중간가 − VWAP) ÷ VWAP ≤ −1.5% + 매수 지지벽(상위 3단 매수 잔량 ≥ 매도 3단 × 200%) → 시장가 매수.
//  청산: 매수 1호가 ≥ VWAP(회귀 · 전량 익절) · 이격률 ≤ min(진입 이격률, −1.5%) − 0.5%(밴드 추가 이탈 · 타이트 손절).
//  상단(+1.5% · 매도벽) 과열은 공매도 불가 원장이라 진입하지 않고 HUD 에 '관측만' 사유로 노출(정직).
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Strategy
{
    public sealed class MeanReversionStrategy : IArenaStrategy
    {
        public const int BandBp = 150, StopExtraBp = 50, WallPct = 200, WallLevels = 3;
        public const long MinVwapTrades = 20;

        private bool _hasQuote, _bidWall, _askWall, _inPosition;
        private long _stopDevBp;

        public int Persona => ArenaPersona.MeanReversion;
        public bool BidWall => _bidWall;

        public void OnQuote(in OrderBookPayload quote)
        {
            _hasQuote = quote.AskPrice[0] > 0 && quote.BidPrice[0] > 0;
            if (!_hasQuote) return;
            long b = ArenaBookMath.TopQty(in quote, bid: true, WallLevels), a = ArenaBookMath.TopQty(in quote, bid: false, WallLevels);
            _bidWall = b * 100 >= a * WallPct && b > 0;
            _askWall = a * 100 >= b * WallPct && a > 0;
        }

        public void OnTrade(long price, long volume, TradeSide side) { }

        public StrategySignal Evaluate(in ArenaMarketState state)
        {
            if (!_hasQuote || state.VwapWon <= 0 || state.VwapTrades < MinVwapTrades) return StrategySignal.Hold(ArenaAiReason.Warmup);
            if (state.PositionQty > 0)
            {
                if (!_inPosition)
                {
                    _inPosition = true;
                    _stopDevBp = Math.Min(state.VwapDevBp, -BandBp) - StopExtraBp;
                }
                long stopPx = state.VwapWon + state.VwapWon * _stopDevBp / 10_000;
                if (state.Busy) return StrategySignal.Hold(ArenaAiReason.Waiting, state.VwapWon, stopPx);
                if (state.BestBid >= state.VwapWon) return StrategySignal.Exit(ArenaAiReason.VwapReverted, state.VwapWon, stopPx);
                if (state.VwapDevBp <= _stopDevBp) return StrategySignal.Exit(ArenaAiReason.BandBreak, state.VwapWon, stopPx);
                return StrategySignal.Hold(ArenaAiReason.Holding, state.VwapWon, stopPx);
            }
            _inPosition = false;
            if (state.Busy) return StrategySignal.Hold(ArenaAiReason.Waiting);
            if (state.VwapDevBp <= -BandBp && _bidWall)
            {
                long stopDev = Math.Min(state.VwapDevBp, -BandBp) - StopExtraBp;
                return StrategySignal.Enter(ArenaAiReason.VwapLowerRevert, state.VwapWon, state.VwapWon + state.VwapWon * stopDev / 10_000);
            }
            if (state.VwapDevBp >= BandBp && _askWall) return StrategySignal.Hold(ArenaAiReason.UpperBandNoShort, state.VwapWon);
            return StrategySignal.Hold(ArenaAiReason.None);
        }

        public void Reset()
        {
            _hasQuote = _bidWall = _askWall = _inPosition = false;
            _stopDevBp = 0;
        }
    }
}
