// ============================================================================
// PredatorStrategy — [v12.18.18-DEV] 페르소나 4: 적대적 포식자(휴먼 약점 역추적 · δ=0)
//
//  역추적(ObservePrey · 거버너 정산 스레드가 휴먼 원장 사후 분석에서 매 회차 주입): 휴먼 연패 · 틸트 경보(최근 30초) · 미청산 MAE(원 ≤ 0).
//   공격성(bp) = 기본 2500 + 연패 × 2000(3연패 상한) + 최근 틸트 2000 + MAE 가중(자본금 대비 손실 1%당 500 · 상한 1500) → [1000, 10000].
//  진입: 10호가 매수 잔량 비율 ≥ 임계(공격성 기본 58% → 최대 45% 로 선형 하향) → 선매수. 목표 = 매입가 + (1 + 공격성/2500) 틱 · 손절 = −2틱.
//   공격성 &lt; 5000 = 은밀(Stealth) — 거버너가 TWAP 분할 시장가로 집행 · 이상 = 즉시 시장가.
//  청산: 목표(추격 물량에 매도) · 손절 · 불균형 ≤ 40% · 보유 5초 경과.
//  허수 잔량(PhantomActive = 휴먼 연패 ≥ 2): 거버너가 휴먼 DOM 래더 표시의 1호가에만 얹는 가상 잔량(매수측 = 기준 × 공격성 × 3 · 매도측 = ⅓) —
//   시뮬레이터 호가 · 체결 · 대기열 계산에는 존재하지 않는다(체결 불가 · 외부 송출 0 · HUD 에 수량 공개).
//  [v12.18.23-DEV] 숏 스퀴즈(ObservePreyShort): 휴먼이 대상 종목을 숏 보유 중이고 평가손(되사는 값 &gt; 순매도 대금)이면 공격성 +2000 · 허수 매수벽 활성 ·
//   진입 임계 = 매복(45%)으로 즉시 하향 → 선매수(PreySqueeze). ★진영 간 체결 간섭 0 원칙상 AI 매수는 휴먼 체결가를 움직이지 않는다 —
//   '스퀴즈 유도'는 추격 선매수 + 휴먼 래더 1호가 허수 매수 잔량(표시 전용 · 체결 불가)로만 구현(정직). 진입 = 매수 · 청산 = 보유 전량 매도.
//   브로커 · 디스패처 · 무장 키 참조 0(IL 허용목록 테스트).
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Strategy
{
    public sealed class PredatorStrategy : IArenaStrategy
    {
        public const int BaseAggressionBp = 2_500, StreakStepBp = 2_000, MaxStreakSteps = 3, TiltBoostBp = 2_000, MaeBoostMaxBp = 1_500,
                         MinAggressionBp = 1_000, MaxAggressionBp = 10_000, StealthBelowBp = 5_000;
        public const int StalkImbalanceBp = 5_800, AmbushImbalanceBp = 4_500, ExitImbalanceBp = 4_000, StopTicks = 2, PhantomStreak = 2, PhantomMultiplier = 3;
        public const long TimeoutTicks = 5 * 10_000_000L, TiltMemoryTicks = 30 * 10_000_000L;
        public const int SqueezeBoostBp = 2_000;

        private int _imbalanceBp = 5_000, _aggr = BaseAggressionBp, _preyStreak, _preyTilts;
        private long _preyShortQty, _preyShortPnl;
        private long _tick = 1, _preyMae;
        private bool _hasQuote, _inPosition;
        private long _entryTicks, _target, _stop;

        public int Persona => ArenaPersona.Predator;
        public int AggressionBp => _aggr;
        public int PreyLossStreak => _preyStreak;
        public int PreyTiltAlerts => _preyTilts;
        public long PreyMaeWon => _preyMae;
        /// <summary>은밀 집행(TWAP 분할) 여부.</summary>
        public bool Stealth => _aggr < StealthBelowBp;
        /// <summary>[v12.18.23-DEV] 휴먼 숏 평가손 국면(숏 보유 ∧ 평가손익 &lt; 0) — 스퀴즈 추격.</summary>
        public bool SqueezeActive => _preyShortQty > 0 && _preyShortPnl < 0;
        public long PreyShortQty => _preyShortQty;
        public long PreyShortPnlWon => _preyShortPnl;
        /// <summary>휴먼 연패 ≥ 2 또는 숏 스퀴즈 국면 = 허수 잔량 주입 국면.</summary>
        public bool PhantomActive => _preyStreak >= PhantomStreak || SqueezeActive;
        /// <summary>현재 진입 임계(매수 잔량 비율 bp) — 공격성이 높을수록 낮은 우위에도 선매수 · 스퀴즈 = 매복 임계.</summary>
        public int EntryImbalanceBp => SqueezeActive ? AmbushImbalanceBp
            : StalkImbalanceBp - (int)((long)(Math.Max(_aggr, BaseAggressionBp) - BaseAggressionBp) * (StalkImbalanceBp - AmbushImbalanceBp) / (MaxAggressionBp - BaseAggressionBp));

        /// <summary>[v12.18.23-DEV] 휴먼 대상 종목 숏(수량 · 평가손익 원) 역추적 — ObservePrey 뒤 같은 회차 호출. 스퀴즈 = 공격성 가산.</summary>
        public void ObservePreyShort(long shortQty, long unrealizedWon)
        {
            _preyShortQty = Math.Max(0, shortQty);
            _preyShortPnl = _preyShortQty > 0 ? unrealizedWon : 0;
            if (SqueezeActive) _aggr = Math.Min(MaxAggressionBp, _aggr + SqueezeBoostBp);
        }
        public int TargetTicks => 1 + _aggr / 2_500;

        /// <summary>
        /// 휴먼 상태 역추적(정산 스레드 · δ=0). lossStreak = 휴먼 왕복 연패 · tiltAlerts = 틸트 경보 누적 · lastTiltTicks = 마지막 경보 시각 ·
        /// openMaeWon = 휴먼 미청산 최대 역행(≤ 0) · capitalWon = 매치 자본금.
        /// </summary>
        public void ObservePrey(int lossStreak, int tiltAlerts, long lastTiltTicks, long openMaeWon, long capitalWon, long nowTicks)
        {
            _preyStreak = Math.Max(0, lossStreak);
            _preyTilts = Math.Max(0, tiltAlerts);
            _preyMae = Math.Min(0, openMaeWon);
            long a = BaseAggressionBp + (long)Math.Min(_preyStreak, MaxStreakSteps) * StreakStepBp;
            if (_preyTilts > 0 && lastTiltTicks > 0 && nowTicks - lastTiltTicks < TiltMemoryTicks) a += TiltBoostBp;
            if (capitalWon > 0 && _preyMae < 0) a += Math.Min(MaeBoostMaxBp, (long)((Int128)(-_preyMae) * 10_000 / capitalWon) * 5);   // 손실 1%(100bp) = 500bp
            _aggr = (int)Math.Clamp(a, MinAggressionBp, MaxAggressionBp);
        }

        /// <summary>휴먼 래더 1호가에 얹을 허수 잔량(매수측) — baseQty = 그 종목 1호가 표시 잔량 평균. 비활성 = 0.</summary>
        public long PhantomBidQty(long baseQty) => PhantomActive && baseQty > 0 ? (long)((Int128)baseQty * _aggr * PhantomMultiplier / 10_000) : 0;

        /// <summary>매도측 허수 = 매수측 ⅓(양방향 · 매수 우위 착시).</summary>
        public long PhantomAskQty(long baseQty) => PhantomBidQty(baseQty) / 3;

        public void OnQuote(in OrderBookPayload quote)
        {
            long ask = quote.AskPrice[0], bid = quote.BidPrice[0];
            _hasQuote = ask > 0 && bid > 0 && ask > bid;
            if (!_hasQuote) return;
            _imbalanceBp = ArenaBookMath.ImbalanceBp(in quote);
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
            if (_imbalanceBp >= EntryImbalanceBp)
                return StrategySignal.Enter(SqueezeActive ? ArenaAiReason.PreySqueeze : PhantomActive ? ArenaAiReason.PreyAmbush : ArenaAiReason.PreyStalk,
                                            state.BestAsk + TargetTicks * _tick, state.BestAsk - StopTicks * _tick);
            return StrategySignal.Hold(ArenaAiReason.None);
        }

        public void Reset()
        {
            _imbalanceBp = 5_000; _tick = 1; _aggr = BaseAggressionBp;
            _preyStreak = _preyTilts = 0; _preyMae = 0; _preyShortQty = _preyShortPnl = 0;
            _hasQuote = _inPosition = false;
            _entryTicks = _target = _stop = 0;
        }
    }
}
