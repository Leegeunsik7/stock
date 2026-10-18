// ============================================================================
// ScalperStrategy — [v12.18.15-DEV] 페르소나 1: 호가 불균형 극초단타(δ=0)
//
//  진입: 10호가 매수 총잔량 비율 ≥ 65% + 최우선 스프레드 ≤ 1틱(압축) → 시장가 매수 · 목표 = 매입가 + 2틱 · 손절 = 매입가 − 2틱(매수 1호가 1틱 후퇴).
//  청산(먼저 충족한 것): 매수 1호가 ≥ 목표(익절) · ≤ 손절 · 불균형 ≤ 35%(역전) · 보유 3초 경과(목표 미도달 = 무체결 시간 초과).
//  ≤ 35% 는 공매도 불가 원장에서 매도 진입이 아닌 '즉시 청산' 트리거로만 쓴다.
// ============================================================================
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Strategy
{
    public sealed class ScalperStrategy : IArenaStrategy
    {
        public const int EnterImbalanceBp = 6_500, ExitImbalanceBp = 3_500, TargetTicks = 2, StopTicks = 2;
        public const long TimeoutTicks = 3 * 10_000_000L;

        private int _imbalanceBp = 5_000;
        private long _spread, _tick = 1;
        private bool _hasQuote, _inPosition;
        private long _entryTicks, _target, _stop;

        public int Persona => ArenaPersona.Scalper;
        public int ImbalanceBp => _imbalanceBp;

        public void OnQuote(in OrderBookPayload quote)
        {
            long ask = quote.AskPrice[0], bid = quote.BidPrice[0];
            _hasQuote = ask > 0 && bid > 0 && ask > bid;
            if (!_hasQuote) return;
            _imbalanceBp = ArenaBookMath.ImbalanceBp(in quote);
            _spread = ask - bid;
            _tick = ArenaBookMath.TickOf(in quote);
        }

        public void OnTrade(long price, long volume, TradeSide side) { }

        public StrategySignal Evaluate(in ArenaMarketState state)
        {
            if (!_hasQuote) return StrategySignal.Hold(ArenaAiReason.Warmup);
            if (state.PositionQty > 0)
            {
                if (!_inPosition)
                {
                    long entry = state.AvgCostWon;
                    _inPosition = true;
                    _entryTicks = state.NowTicks;
                    _target = entry + TargetTicks * _tick;
                    _stop = entry - StopTicks * _tick;
                }
                if (state.Busy) return StrategySignal.Hold(ArenaAiReason.Waiting, _target, _stop);
                if (state.BestBid >= _target) return StrategySignal.Exit(ArenaAiReason.TakeProfit, _target, _stop);
                if (state.BestBid <= _stop) return StrategySignal.Exit(ArenaAiReason.StopLoss, _target, _stop);
                if (_imbalanceBp <= ExitImbalanceBp) return StrategySignal.Exit(ArenaAiReason.ImbalanceFlip, _target, _stop);
                if (state.NowTicks - _entryTicks >= TimeoutTicks) return StrategySignal.Exit(ArenaAiReason.Timeout, _target, _stop);
                return StrategySignal.Hold(ArenaAiReason.Holding, _target, _stop);
            }
            _inPosition = false;
            if (state.Busy) return StrategySignal.Hold(ArenaAiReason.Waiting);
            if (_imbalanceBp >= EnterImbalanceBp && _spread <= _tick)
                return StrategySignal.Enter(ArenaAiReason.ImbalanceBuy, state.BestAsk + TargetTicks * _tick, state.BestAsk - StopTicks * _tick);
            return StrategySignal.Hold(ArenaAiReason.None);
        }

        public void Reset()
        {
            _imbalanceBp = 5_000; _spread = 0; _tick = 1;
            _hasQuote = _inPosition = false;
            _entryTicks = _target = _stop = 0;
        }
    }
}
