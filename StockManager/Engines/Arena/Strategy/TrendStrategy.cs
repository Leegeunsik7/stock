// ============================================================================
// TrendStrategy — [v12.18.15-DEV] 페르소나 2: 추세 추종 모멘텀(δ=0)
//
//  지표(체결 틱 기준): EMA(8) · EMA(34) 체결가 · 체결량 평균(EMA 34). 폭증 = 이번 체결량 ≥ 직전 체결량 × 200% 이고 평균 이상(잡음 컷) · 매도 주도 아님.
//  진입: 예열(34체결) 후 EMA(8) > EMA(34)(정배열) + 최근 체결가 > EMA(8) + 폭증 래치 → 시장가 매수.
//        위험폭 R = max(3틱, 가격 × 0.4%) · 초기 손절 = 진입 − R · 목표(표시) = 진입 + 2.5R(손익비 ≥ 2.5).
//  홀딩: 고점 갱신마다 트레일링 스탑 = max(초기 손절, 고점 − R) → 매수 1호가 ≤ 스탑 = 청산 · EMA 역배열 = 청산. 고정 익절 없음(추세 극대화).
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Strategy
{
    public sealed class TrendStrategy : IArenaStrategy
    {
        public const int FastPeriod = 8, SlowPeriod = 34, SurgePct = 200, RiskBp = 40, RiskTicks = 3, RewardRiskCenti = 250;
        private const double FastAlpha = 2.0 / (FastPeriod + 1), SlowAlpha = 2.0 / (SlowPeriod + 1);

        private double _fast, _slow, _volAvg;
        private long _n, _lastPx, _prevVol, _tick = 1;
        private bool _surge, _inPosition;
        private long _risk, _high, _stop0, _target;

        public int Persona => ArenaPersona.Trend;
        public double EmaFast => _fast;
        public double EmaSlow => _slow;
        public bool SurgeLatched => _surge;

        public void OnQuote(in OrderBookPayload quote)
        {
            if (quote.AskPrice[0] > 0 && quote.BidPrice[0] > 0) _tick = ArenaBookMath.TickOf(in quote);
        }

        public void OnTrade(long price, long volume, TradeSide side)
        {
            if (price <= 0 || volume <= 0) return;
            if (_n == 0) { _fast = _slow = price; _volAvg = volume; }
            else
            {
                if (_n >= SlowPeriod && side != TradeSide.Sell && volume * 100 >= _prevVol * SurgePct && volume >= _volAvg) _surge = true;
                _fast += FastAlpha * (price - _fast);
                _slow += SlowAlpha * (price - _slow);
                _volAvg += SlowAlpha * (volume - _volAvg);
            }
            _prevVol = volume;
            _lastPx = price;
            _n++;
        }

        public StrategySignal Evaluate(in ArenaMarketState state)
        {
            if (_n < SlowPeriod) { _surge = false; return StrategySignal.Hold(ArenaAiReason.Warmup); }
            if (state.PositionQty > 0)
            {
                if (!_inPosition)
                {
                    long entry = state.AvgCostWon;
                    _inPosition = true;
                    _risk = Risk(entry);
                    _high = entry;
                    _stop0 = entry - _risk;
                    _target = entry + _risk * RewardRiskCenti / 100;
                }
                _surge = false;
                if (state.BestBid > _high) _high = state.BestBid;
                long trail = Math.Max(_stop0, _high - _risk);
                if (state.Busy) return StrategySignal.Hold(ArenaAiReason.Waiting, _target, trail);
                if (state.BestBid <= trail) return StrategySignal.Exit(ArenaAiReason.TrailingStop, _target, trail);
                if (_fast < _slow) return StrategySignal.Exit(ArenaAiReason.DeadCross, _target, trail);
                return StrategySignal.Hold(ArenaAiReason.Holding, _target, trail);
            }
            _inPosition = false;
            bool surge = _surge;
            _surge = false;                                                          // 래치 1회 소비(지난 폭증으로 늦은 진입 0)
            if (state.Busy) return StrategySignal.Hold(ArenaAiReason.Waiting);
            if (_fast > _slow && _lastPx > _fast && surge)
            {
                long r = Risk(state.BestAsk);
                return StrategySignal.Enter(ArenaAiReason.TrendBreakout, state.BestAsk + r * RewardRiskCenti / 100, state.BestAsk - r);
            }
            return StrategySignal.Hold(ArenaAiReason.None);
        }

        private long Risk(long price) => Math.Max(RiskTicks * _tick, price * RiskBp / 10_000);

        public void Reset()
        {
            _fast = _slow = _volAvg = 0;
            _n = _lastPx = _prevVol = 0; _tick = 1;
            _surge = _inPosition = false;
            _risk = _high = _stop0 = _target = 0;
        }
    }
}
