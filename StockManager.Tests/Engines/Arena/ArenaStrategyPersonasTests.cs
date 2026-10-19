// ============================================================================
// ArenaStrategyPersonasTests — [v12.18.15-DEV] 아레나 AI 전략 페르소나 3종 · 실시간 VWAP 누적기 적대적 검증
//   §1 VWAP: 정확값(Int128 누적 · 반올림) · 가중 σ · 이격률 · 밴드 · 일시정지 무시 · 초기화 · 체크포인트 왕복 · 1M 틱 × 1억 원 × 100만 주 오버플로 0
//   §2 페르소나 단위: 스캘퍼(불균형 65% 경계 · 스프레드 압축 · 목표/손절/역전/3초) · 추세(예열 · 정배열 + 폭증 · 매도 주도 폭증 무시 · 손익비 2.5 · 트레일링)
//      · 평균회귀(−1.5% 경계 · 지지벽 · 상단 관측만 · VWAP 회귀 익절 · 밴드 추가 이탈 손절)
//   §3 거버너 결속: 페르소나 발주 → AI 가상 원장 체결 · 시그널 엔진 결정 무시 · HUD 투영 · 교체 = AI 전량 청산 + 큐 동기화(교체 전 결정 폐기) · 리플레이 3종 독립 발주
//   §4 δ=0 · GC 0: 1M 틱 VWAP 누적 · 1M 틱 재생(체결 → VWAP → 전략 평가 → 가상 큐 디스패치 · 25만 틱마다 페르소나 교체)
//   §5 Fail-Closed: 전략 · VWAP IL 참조 허용목록 · 런타임 객체 그래프(거버너에서 도달 가능한 전 인스턴스) 브로커 타입 0
//   §6 스트레스: bUnit 헤드리스 페르소나 탭 연타 + 최대 배속 틱 주입 정산 스레드 — 스냅샷 버전 전진(동결 0) · 예외 0 · 교착 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines.Arena;
using StockManager.Engines.Arena.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaStrategyPersonasTests
    {
        private const long Sym = 5930, Ms = 10_000, Sec = 10_000_000;
        private readonly ITestOutputHelper _out;
        private long _now = 638_000_000_000_000_000;

        public ArenaStrategyPersonasTests(ITestOutputHelper o) => _out = o;

        private static OrderBookPayload Book(long ask, long askQty, long bid, long bidQty, long tick = 100, long symbol = Sym)
        {
            var b = new OrderBookPayload { SymbolCode = symbol };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * tick; b.AskQty[l] = askQty;
                b.BidPrice[l] = bid - l * tick; b.BidQty[l] = bidQty;
            }
            return b;
        }

        private static ArenaMarketState State(long bid, long ask, long qty = 0, long cost = 0, long now = 0, long vwap = 0, long devBp = 0, long vwapTrades = 0, bool busy = false)
            => new(Sym, now, bid, ask, 0, vwap, devBp, 0, vwapTrades, qty, cost, busy);

        // 리플레이식 단일 작성자 구동(지연 0 = 같은 Advance 안에서 접수 → 체결)
        private ArenaMatchGovernor Rig(long capital = 100_000_000)
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            g.Rearm(capital, running: true, 1, _now);
            return g;
        }

        private void Tick(ArenaMatchGovernor g, in OrderBookPayload q, long dt = Ms) { _now += dt; g.Advance(in q, hasQuote: true, _now, _now); }

        private void Trade(ArenaMatchGovernor g, long px, long qty, int side = 1) { _now += Ms; g.ApplyTrade(new ArenaTrade { Symbol = Sym, PriceWon = px, Qty = qty, Side = side }, _now); }

        private static ArenaSnapshot Snap(ArenaMatchGovernor g, long now)
        {
            g.PublishNow(now);
            Assert.True(g.TryRead(out var s));
            return s;
        }

        // ═══ §1 VWAP ═══

        [Fact]
        public void Vwap_Exact_Sigma_Deviation_And_Bands()
        {
            var v = new ArenaVwapTracker();
            Assert.Equal(0, v.VwapWon);
            Assert.Equal(0, v.DeviationBp(100));
            v.OnTrade(100, 10);
            v.OnTrade(110, 30);
            Assert.Equal(108, v.VwapWon);                                          // (1000 + 3300) / 40 = 107.5 → 반올림 108
            Assert.Equal((Int128)4_300, v.SumPriceVolume);
            Assert.Equal(40, v.SumVolume);
            Assert.Equal(4, v.SigmaWon);                                            // 가중분산 (10×56.25 + 30×6.25)/40 = 18.75 → σ 4.33
            Assert.Equal(185, v.DeviationBp(110));                                  // (110 − 108) × 10000 / 108
            Assert.Equal(-370, v.DeviationBp(104));
            Assert.Equal(116, v.BandWon(200, upper: true));
            Assert.Equal(100, v.BandWon(200, upper: false));
            v.OnTrade(0, 5); v.OnTrade(100, 0); v.OnTrade(-1, 5);                   // 무효 입력 = 무시
            Assert.Equal(2, v.Trades);
        }

        [Fact]
        public void Vwap_Pause_Reset_And_Checkpoint_Restore_Are_Exact()
        {
            var a = new ArenaVwapTracker();
            var b = new ArenaVwapTracker();
            for (int i = 0; i < 1_000; i++) { a.OnTrade(70_000 + i % 17 * 100, 1 + i % 300); b.OnTrade(70_000 + i % 17 * 100, 1 + i % 300); }
            var cp = a.Capture();
            a.Pause();
            a.OnTrade(1_000_000, 1_000_000);                                       // 일시정지 중 유입 = 누적 오염 0
            Assert.Equal(b.VwapWon, a.VwapWon);
            Assert.Equal(b.Trades, a.Trades);
            a.Resume();
            a.Reset();
            Assert.Equal(0, a.VwapWon);
            Assert.Equal(0, a.SigmaWon);
            a.Restore(in cp);                                                       // 테이프 동기화 체크포인트 → 이후 누적이 원본과 비트 동일
            for (int i = 0; i < 500; i++) { a.OnTrade(69_000 + i % 23 * 100, 7 + i % 50); b.OnTrade(69_000 + i % 23 * 100, 7 + i % 50); }
            Assert.Equal(b.SumPriceVolume, a.SumPriceVolume);
            Assert.Equal(b.VwapWon, a.VwapWon);
            Assert.Equal(b.SigmaWon, a.SigmaWon);
            Assert.Equal(b.LastPrice, a.LastPrice);
        }

        [Fact]
        public void Vwap_Int128_Accumulator_Survives_1M_Ticks_At_Extreme_Notional()
        {
            var v = new ArenaVwapTracker();
            for (int i = 0; i < 1_000_000; i++) v.OnTrade(DualBookSimulator.MaxPriceWon, DualBookSimulator.MaxOrderQty);
            Assert.True(v.SumPriceVolume > long.MaxValue);                          // 1e20 > 9.2e18 — long 이면 오버플로
            Assert.Equal(DualBookSimulator.MaxPriceWon, v.VwapWon);
            Assert.Equal(0, v.SigmaWon);
            Assert.Equal(0, v.DeviationBp(DualBookSimulator.MaxPriceWon));
        }

        // ═══ §2 페르소나 단위 ═══

        [Fact]
        public void Scalper_Imbalance_Threshold_And_Spread_Compression_Gate_Entry()
        {
            var s = new ScalperStrategy();
            Assert.Equal(ArenaAiReason.Warmup, s.Evaluate(State(69_900, 70_000)).Reason);
            var q = Book(70_000, 350, 69_900, 650);                                 // 매수 65.00%
            s.OnQuote(in q);
            Assert.Equal(6_500, s.ImbalanceBp);
            var sig = s.Evaluate(State(69_900, 70_000));
            Assert.Equal(ArenaAiAction.Enter, sig.Action);
            Assert.Equal(ArenaAiReason.ImbalanceBuy, sig.Reason);
            Assert.Equal(70_200, sig.TargetPx);
            q = Book(70_000, 351, 69_900, 649);                                     // 64.9% = 미달
            s.OnQuote(in q);
            Assert.Equal(ArenaAiAction.Hold, s.Evaluate(State(69_900, 70_000)).Action);
            q = Book(70_100, 300, 69_900, 700);                                     // 70% 지만 스프레드 2틱 = 압축 아님
            s.OnQuote(in q);
            Assert.Equal(ArenaAiAction.Hold, s.Evaluate(State(69_900, 70_100)).Action);
            q = Book(70_000, 300, 69_900, 700);
            s.OnQuote(in q);
            Assert.Equal(ArenaAiReason.Waiting, s.Evaluate(State(69_900, 70_000, busy: true)).Reason);   // 미완료 주문 = 대기
        }

        [Fact]
        public void Scalper_Exits_On_Target_Stop_Imbalance_Flip_And_Three_Second_Timeout()
        {
            long t0 = 1_000 * Sec;
            StrategySignal Held(ScalperStrategy s, long bid, long now) => s.Evaluate(State(bid, bid + 100, 10, 700_000, now));
            ScalperStrategy Fresh()
            {
                var s = new ScalperStrategy();
                var q = Book(70_000, 300, 69_900, 700);
                s.OnQuote(in q);
                Assert.Equal(ArenaAiReason.Holding, Held(s, 69_900, t0).Reason);    // 매입가 70,000 → 목표 70,200 · 손절 69,800
                return s;
            }
            var a = Fresh();
            Assert.Equal(ArenaAiReason.Holding, Held(a, 70_100, t0 + Sec).Reason);
            var tp = Held(a, 70_200, t0 + Sec);
            Assert.Equal((ArenaAiAction.Exit, ArenaAiReason.TakeProfit, 70_200L, 69_800L), (tp.Action, tp.Reason, tp.TargetPx, tp.StopPx));
            Assert.Equal(ArenaAiReason.StopLoss, Held(Fresh(), 69_800, t0 + Sec).Reason);
            var c = Fresh();
            var flip = Book(70_000, 650, 69_900, 350);                              // 35% = 역전
            c.OnQuote(in flip);
            Assert.Equal(ArenaAiReason.ImbalanceFlip, Held(c, 69_900, t0 + Sec).Reason);
            var d = Fresh();
            Assert.Equal(ArenaAiReason.Holding, Held(d, 69_900, t0 + ScalperStrategy.TimeoutTicks - 1).Reason);
            Assert.Equal(ArenaAiReason.Timeout, Held(d, 69_900, t0 + ScalperStrategy.TimeoutTicks).Reason);
        }

        private static TrendStrategy WarmTrend(int trades = 40, long start = 70_000, long step = 10, long vol = 100)
        {
            var s = new TrendStrategy();
            var q = Book(70_000, 500, 69_900, 500);
            s.OnQuote(in q);
            for (int i = 0; i < trades; i++) s.OnTrade(start + i * step, vol, TradeSide.Buy);
            return s;
        }

        [Fact]
        public void Trend_Golden_Alignment_Plus_Volume_Surge_Enters_With_RewardRisk_2_5()
        {
            var cold = new TrendStrategy();
            for (int i = 0; i < TrendStrategy.SlowPeriod - 1; i++) cold.OnTrade(70_000 + i * 10, 100, TradeSide.Buy);
            Assert.Equal(ArenaAiReason.Warmup, cold.Evaluate(State(70_300, 70_400)).Reason);

            var calm = WarmTrend();                                                 // 정배열이지만 폭증 없음
            Assert.True(calm.EmaFast > calm.EmaSlow);
            Assert.Equal(ArenaAiAction.Hold, calm.Evaluate(State(70_300, 70_400)).Action);

            var s = WarmTrend();
            s.OnTrade(70_400, 200, TradeSide.Buy);                                  // 직전 100 대비 200% = 폭증
            Assert.True(s.SurgeLatched);
            var sig = s.Evaluate(State(70_300, 70_400));
            Assert.Equal((ArenaAiAction.Enter, ArenaAiReason.TrendBreakout), (sig.Action, sig.Reason));
            long risk = 70_400 - sig.StopPx, reward = sig.TargetPx - 70_400;
            Assert.Equal(Math.Max(3 * 100, 70_400 * TrendStrategy.RiskBp / 10_000), risk);
            Assert.True(reward * 100 >= risk * 250, $"R/R {reward}/{risk}");
            Assert.False(s.SurgeLatched);                                           // 래치 1회 소비
            Assert.Equal(ArenaAiAction.Hold, s.Evaluate(State(70_300, 70_400)).Action);

            var weak = WarmTrend();
            weak.OnTrade(70_400, 199, TradeSide.Buy);                               // 199% = 미달
            Assert.Equal(ArenaAiAction.Hold, weak.Evaluate(State(70_300, 70_400)).Action);
            var dump = WarmTrend();
            dump.OnTrade(70_400, 500, TradeSide.Sell);                              // 매도 주도 폭증 = 추세 진입 근거 아님
            Assert.Equal(ArenaAiAction.Hold, dump.Evaluate(State(70_300, 70_400)).Action);
            var down = WarmTrend(step: -10);                                        // 역배열 + 폭증 = 진입 0
            down.OnTrade(69_500, 500, TradeSide.Buy);
            Assert.Equal(ArenaAiAction.Hold, down.Evaluate(State(69_400, 69_500)).Action);
        }

        [Fact]
        public void Trend_Trailing_Stop_Ratchets_Up_With_New_Highs_And_Exits()
        {
            var s = WarmTrend();
            long entry = 70_400, risk = 300;
            var h = s.Evaluate(State(70_300, 70_400, 10, entry * 10));
            Assert.Equal((ArenaAiReason.Holding, entry - risk, entry + risk * 250 / 100), (h.Reason, h.StopPx, h.TargetPx));
            h = s.Evaluate(State(71_500, 71_600, 10, entry * 10));                  // 고점 71,500 → 스탑 71,200
            Assert.Equal(71_200, h.StopPx);
            h = s.Evaluate(State(71_300, 71_400, 10, entry * 10));                  // 후퇴해도 스탑은 내려가지 않음
            Assert.Equal((ArenaAiReason.Holding, 71_200L), (h.Reason, h.StopPx));
            h = s.Evaluate(State(71_200, 71_300, 10, entry * 10));
            Assert.Equal((ArenaAiAction.Exit, ArenaAiReason.TrailingStop), (h.Action, h.Reason));
            Assert.True(h.StopPx > entry, "트레일링 스탑이 이익 구간을 잠가야 함");
        }

        [Fact]
        public void MeanReversion_Lower_Band_With_Bid_Wall_Enters_And_Reverts_Or_Stops()
        {
            var s = new MeanReversionStrategy();
            var wall = Book(98_400, 100, 98_300, 300);                              // 매수 3단 300 ≥ 매도 3단 100 × 200%
            s.OnQuote(in wall);
            Assert.True(s.BidWall);
            Assert.Equal(ArenaAiReason.Warmup, s.Evaluate(State(98_300, 98_400, vwap: 100_000, devBp: -165, vwapTrades: 5)).Reason);
            Assert.Equal(ArenaAiAction.Hold, s.Evaluate(State(98_300, 98_400, vwap: 100_000, devBp: -149, vwapTrades: 50)).Action);
            var e = s.Evaluate(State(98_300, 98_400, vwap: 100_000, devBp: -150, vwapTrades: 50));
            Assert.Equal((ArenaAiAction.Enter, ArenaAiReason.VwapLowerRevert, 100_000L, 98_000L), (e.Action, e.Reason, e.TargetPx, e.StopPx));

            var thin = new MeanReversionStrategy();
            var noWall = Book(98_400, 200, 98_300, 300);
            thin.OnQuote(in noWall);
            Assert.Equal(ArenaAiAction.Hold, thin.Evaluate(State(98_300, 98_400, vwap: 100_000, devBp: -300, vwapTrades: 50)).Action);

            var hot = new MeanReversionStrategy();
            var askWall = Book(101_700, 300, 101_600, 100);
            hot.OnQuote(in askWall);
            var up = hot.Evaluate(State(101_600, 101_700, vwap: 100_000, devBp: 165, vwapTrades: 50));
            Assert.Equal((ArenaAiAction.EnterShort, ArenaAiReason.VwapUpperShort), (up.Action, up.Reason));   // [v12.18.23-DEV] 상단 과열 + 매도벽 = 가상 공매도
            Assert.Equal(100_000, up.TargetPx);                                                                  // 목표 = VWAP 회귀
            Assert.Equal(102_150, up.StopPx);                                                                    // 손절 이격 = +165 + 50 = +2.15%

            var h = s.Evaluate(State(98_300, 98_400, 10, 984_000, vwap: 100_000, devBp: -165, vwapTrades: 50));
            Assert.Equal((ArenaAiReason.Holding, 97_850L), (h.Reason, h.StopPx));  // 손절 이격 = −165 − 50 = −2.15%
            Assert.Equal(ArenaAiReason.Holding, s.Evaluate(State(97_900, 98_000, 10, 984_000, vwap: 100_000, devBp: -205, vwapTrades: 50)).Reason);
            Assert.Equal(ArenaAiReason.BandBreak, s.Evaluate(State(97_800, 97_900, 10, 984_000, vwap: 100_000, devBp: -215, vwapTrades: 50)).Reason);
            var s2 = new MeanReversionStrategy();
            s2.OnQuote(in wall);
            s2.Evaluate(State(98_300, 98_400, 10, 984_000, vwap: 100_000, devBp: -165, vwapTrades: 50));
            Assert.Equal(ArenaAiReason.VwapReverted, s2.Evaluate(State(100_000, 100_100, 10, 984_000, vwap: 100_000, devBp: 5, vwapTrades: 50)).Reason);
        }

        // ═══ §3 거버너 결속 ═══

        [Fact]
        public void Scalper_Persona_Trades_Ai_Virtual_Book_And_Ignores_Signal_Engine()
        {
            var g = Rig();
            Assert.False(g.SetPersona(ArenaPersona.Count));
            Assert.False(g.SetPersona(-1));
            Assert.True(g.SetPersona(ArenaPersona.Scalper));
            var flat = Book(70_000, 5_000, 69_900, 5_000);
            Tick(g, in flat);
            Assert.Equal(ArenaPersona.Scalper, g.ActivePersona);
            g.OnAiDecision(DashDecisionKind.Entry, Sym);                            // 시그널 엔진 결정 = 무시(두 두뇌 동시 발주 0)
            Tick(g, in flat);
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Ai).Position(Sym));
            var hud = Snap(g, _now).AiHud;
            Assert.Equal((ArenaPersona.Scalper, Sym, 5_000, 100L), (hud.Persona, hud.TargetSymbol, hud.ImbalanceBp, hud.SpreadWon));

            var heavy = Book(70_000, 3_000, 69_900, 7_000);                         // 70% + 1틱 → 진입 → 지연 0 같은 틱 체결
            Tick(g, in heavy);
            long held = g.Simulator.Book(ArenaSide.Ai).Position(Sym);
            Assert.Equal(100_000_000L * 1_000 / 10_000 / 70_000, held);             // 자본금 10% ÷ 최우선 매도호가
            hud = Snap(g, _now).AiHud;
            Assert.Equal((ArenaAiAction.Enter, ArenaAiReason.ImbalanceBuy, 1L, 7_000), (hud.Action, hud.Reason, hud.Decisions, hud.ImbalanceBp));
            Tick(g, in heavy);
            Assert.Equal(ArenaAiReason.Holding, Snap(g, _now).AiHud.Reason);
            var flip = Book(70_000, 7_000, 69_900, 3_000);
            Tick(g, in flip);
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Ai).Position(Sym));
            var s = Snap(g, _now);
            Assert.Equal((ArenaAiAction.Exit, ArenaAiReason.ImbalanceFlip, 2L), (s.AiHud.Action, s.AiHud.Reason, s.AiHud.Decisions));
            Assert.Equal(2, s.Ai.Fills);
            Assert.Equal(0, s.Human.Fills);
        }

        [Fact]
        public void MeanReversion_Persona_Buys_Vwap_Dislocation_And_Takes_Profit_On_Reversion()
        {
            var g = Rig();
            g.SetPersona(ArenaPersona.MeanReversion);
            var mid = Book(100_100, 1_000, 100_000, 1_000);
            Tick(g, in mid);
            for (int i = 0; i < 30; i++) Trade(g, 100_000, 100);
            Tick(g, in mid);
            var s = Snap(g, _now);
            Assert.Equal((100_000L, 30L), (s.AiHud.VwapWon, s.AiHud.VwapTrades));
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Ai).Position(Sym));

            var dip = Book(98_400, 100, 98_300, 300);                               // 중간가 98,350 → −1.65% + 매수벽
            Tick(g, in dip);
            Assert.True(g.Simulator.Book(ArenaSide.Ai).Position(Sym) > 0);
            s = Snap(g, _now);
            Assert.Equal((ArenaAiReason.VwapLowerRevert, 100_000L, -165L), (s.AiHud.Reason, s.AiHud.TargetPx, s.AiHud.VwapDevBp));
            var back = Book(100_100, 1_000, 100_000, 1_000);
            Tick(g, in back);
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Ai).Position(Sym));
            s = Snap(g, _now);
            Assert.Equal(ArenaAiReason.VwapReverted, s.AiHud.Reason);
            Assert.True(s.Ai.RealizedWon > 0, "realized " + s.Ai.RealizedWon);
        }

        [Fact]
        public void Trend_Persona_Enters_On_Ema_Alignment_And_Volume_Surge_Through_Governor()
        {
            var g = Rig();
            g.SetPersona(ArenaPersona.Trend);
            var q = Book(70_100, 1_000, 70_000, 1_000);
            Tick(g, in q);
            for (int i = 0; i < 40; i++) Trade(g, 69_600 + i * 10, 100);
            Tick(g, in q);
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Ai).Position(Sym));
            Trade(g, 70_100, 300);
            Tick(g, in q);
            Assert.True(g.Simulator.Book(ArenaSide.Ai).Position(Sym) > 0);
            var s = Snap(g, _now);
            Assert.Equal(ArenaAiReason.TrendBreakout, s.AiHud.Reason);
            Assert.True((s.AiHud.TargetPx - 70_100) * 100 >= (70_100 - s.AiHud.StopPx) * 250);
        }

        [Fact]
        public void Persona_Switch_Flattens_Ai_Book_And_Drops_Stale_Queue()
        {
            var g = Rig();
            g.SetPersona(ArenaPersona.Scalper);
            var heavy = Book(70_000, 3_000, 69_900, 7_000);
            Tick(g, in heavy);
            Assert.True(g.Simulator.Book(ArenaSide.Ai).Position(Sym) > 0);

            g.SetPersona(ArenaPersona.Trend);
            Tick(g, in heavy);                                                      // 교체 회차: 비상 청산 접수(지연 0 → 같은 회차 정산)
            Tick(g, in heavy);
            var ai = g.Simulator.Book(ArenaSide.Ai);
            Assert.Equal(0, ai.Position(Sym));
            Assert.Equal(0, ai.PendingCount + ai.WorkingCount);
            var s = Snap(g, _now);
            Assert.Equal((ArenaPersona.Trend, 2, ArenaAiReason.Warmup), (s.AiHud.Persona, s.AiHud.Switches, s.AiHud.Reason));
            Assert.Equal(0, s.Ai.OpenPositions);
            Assert.Equal(s.Ai.EquityWon, s.Ai.CashWon);                             // 정산 완료 = 평가자산 전부 현금
            Assert.Equal((ArenaPersona.Trend, ArenaAiReason.Warmup), (g.ActivePersona, g.LastSignal.Reason));

            // 시그널 엔진 복귀 → 결정 적재 → 처리 전 다시 교체 = 교체 전 결정 폐기(큐 동기화)
            g.SetPersona(ArenaPersona.Signal);
            Tick(g, in heavy);
            g.OnAiDecision(DashDecisionKind.Entry, Sym);
            g.SetPersona(ArenaPersona.MeanReversion);
            Tick(g, in heavy);
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Ai).Position(Sym));
            Assert.Equal(4, Snap(g, _now).AiHud.Switches);                            // 개시 후 첫 선택(스캘퍼) 포함

            // 대기 상태 교체 = 즉시 반영 · 매치 재개시해도 페르소나 유지 · 교체 카운터 초기화
            var idle = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            idle.SetPersona(ArenaPersona.Trend);
            idle.Rearm(10_000_000, running: false, 1, _now);
            Tick(idle, in heavy);
            Assert.Equal(ArenaPersona.Trend, idle.ActivePersona);
            idle.Rearm(10_000_000, running: true, 1, _now);
            Assert.Equal((ArenaPersona.Trend, 0), (Snap(idle, _now).AiHud.Persona, Snap(idle, _now).AiHud.Switches));
        }

        [Fact]
        public void Replay_Each_Persona_Trades_Independently_With_Distinct_Logic()
        {
            var tape = SyntheticTickSource.Create(200_000);
            var results = new (long fills, long decisions, long realized, int reason)[ArenaPersona.Count];
            for (int p = ArenaPersona.Scalper; p < ArenaPersona.Count; p++)
            {
                using var r = new ArenaTickReplayEngine(() => tape, ownThread: false);
                r.Arena.SetPersona(p);
                Assert.True(r.SetSpeed(ArenaReplaySpeed.Max));
                Assert.True(r.Play(100_000_000));
                long now = 0;
                for (int k = 0; k < 1_000 && r.Status.State != ArenaReplayState.Finished; k++) r.Pump(now += 1_000);
                Assert.Equal(ArenaReplayState.Finished, r.Status.State);
                Assert.True(r.Arena.TryRead(out var s));
                Assert.Equal(p, s.AiHud.Persona);
                Assert.Equal(0, s.Human.Fills);
                results[p] = (s.Ai.Fills, s.AiHud.Decisions, s.Ai.RealizedWon, s.AiHud.Reason);
                _out.WriteLine($"[{ArenaPersona.Label(p)}] 체결 {s.Ai.Fills} · 발주 {s.AiHud.Decisions} · 실현 {s.Ai.RealizedWon:N0} · 승/패 {s.Ai.Wins}/{s.Ai.Losses} · VWAP {s.AiHud.VwapWon:N0} · 마지막 {ArenaAiReason.Label(s.AiHud.Reason)}");
                Assert.True(s.AiHud.Decisions > 0 && s.Ai.Fills > 0, ArenaPersona.Label(p) + " 발주 0");
            }
            var fills = results.Skip(1).Select(x => x.fills).ToArray();
            Assert.Equal(fills.Length, fills.Distinct().Count());                   // 같은 테이프 · 서로 다른 로직 = 서로 다른 매매 궤적
        }

        // ═══ §4 δ=0 · GC 0 ═══

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        private static (int, int, int) Gcs() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        [Fact]
        public void Vwap_1M_Tick_Accumulation_Is_Zero_Alloc_With_No_Gc()
        {
            var v = new ArenaVwapTracker();
            long sink = 0;
            void Loop()
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    v.OnTrade(70_000 + (i % 41 - 20) * 100, 1 + i % 300);
                    sink += v.VwapWon + v.SigmaWon + v.DeviationBp(70_000 + (i & 7) * 100);
                }
            }
            Loop();
            var gc0 = Gcs();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, Gcs());
            Assert.True(sink != 0 && v.Trades >= 2_000_000);
        }

        [Fact]
        public void Replay_1M_Ticks_Vwap_Strategy_And_Dispatch_Loop_Is_Zero_Alloc_With_No_Gc()
        {
            var tape = SyntheticTickSource.Create(1_000_000);
            var g = new ArenaMatchGovernor(() => _now, 4_096);
            long sink = 0;
            int persona = ArenaPersona.Scalper;
            void Loop()
            {
                g.Rearm(1_000_000_000, running: true, 4_096, _now);
                for (int i = 0; i < tape.Count; i++)
                {
                    if (i % 250_000 == 0) { g.SetPersona(persona); persona = persona % 3 + 1; }   // 25만 틱마다 교체(비상 청산 포함)
                    long ts = tape.TimestampOf(i);
                    bool hasBook = tape.Read(i, out var book);
                    if (tape.TryReadTrade(i, out var t)) g.ApplyTrade(in t, ts);
                    g.Advance(in book, hasBook, ts, ts);
                    if ((i & 1_023) == 0) { g.PublishNow(ts); if (g.TryRead(out var s)) sink += s.AiHud.Decisions + s.AiHud.VwapWon; }
                }
            }
            Loop();
            var gc0 = Gcs();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, Gcs());
            g.PublishNow(_now);
            Assert.True(g.TryRead(out var fin));
            _out.WriteLine($"[1M δ=0] AI 체결 {fin.Ai.Fills} · 발주 {fin.AiHud.Decisions} · 교체 {fin.AiHud.Switches} · VWAP 체결 {fin.AiHud.VwapTrades}");
            Assert.True(fin.Ai.Fills > 0 && fin.AiHud.Switches >= 3 && sink != 0);
        }

        // ═══ §5 Fail-Closed ═══

        private static readonly Type[] PersonaTypes =
        {
            typeof(ArenaVwapTracker), typeof(ArenaVwapState), typeof(IArenaStrategy), typeof(ScalperStrategy), typeof(TrendStrategy),
            typeof(MeanReversionStrategy), typeof(ArenaMarketState), typeof(StrategySignal), typeof(ArenaBookMath), typeof(TradeSide),
        };

        private static bool AllowedNs(Type t)
        {
            while (t.HasElementType) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) return AllowedNs(t.GetGenericTypeDefinition()) && t.GetGenericArguments().All(AllowedNs);
            if (t.IsGenericParameter || t.FullName == "<PrivateImplementationDetails>") return true;
            if (t == typeof(OrderBookPayload) || t == typeof(OrderBookLevels10) || t == typeof(TelemetryRing<>)) return true;
            if (t.DeclaringType is { } d && AllowedNs(d.IsGenericType ? d.GetGenericTypeDefinition() : d)) return true;   // 허용 타입의 중첩(링 슬롯 · 컴파일러 생성)
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Threading", StringComparison.Ordinal) || ns.StartsWith("System.Runtime", StringComparison.Ordinal)
                || ns.StartsWith("System.Numerics", StringComparison.Ordinal)
                || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arena" || ns == "StockManager.Engines.Arena.Strategy";
        }

        [Fact]
        public void Strategy_And_Vwap_Il_References_Only_Allowlisted_Types()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            foreach (var type in PersonaTypes)
            {
                foreach (var f in type.GetFields(All)) if (!AllowedNs(f.FieldType)) bad.Add(type.Name + "." + f.Name + " : " + f.FieldType.FullName);
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = r as Type ?? r.DeclaringType;
                        if (owner is not null && !AllowedNs(owner)) bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + r.Name);
                    }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            var personaMembers = typeof(IArenaStrategy).GetMembers().Select(m => m.Name).Concat(typeof(ArenaPersona).GetMembers().Select(m => m.Name));
            Assert.DoesNotContain(personaMembers, n => n.Contains("Arm", StringComparison.OrdinalIgnoreCase) || n.Contains("Broker", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Runtime_Object_Graph_From_Governor_Reaches_No_Broker_Or_Dispatch_Type()
        {
            var g = Rig();
            g.SetPersona(ArenaPersona.MeanReversion);
            var q = Book(70_000, 3_000, 69_900, 7_000);
            Tick(g, in q);
            const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(g);
            var types = new HashSet<Type>();
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (!seen.Add(o)) continue;
                var t = o.GetType();
                types.Add(t);
                if (o is Array arr)
                {
                    if (!t.GetElementType()!.IsValueType) foreach (var e in arr) if (e is not null) stack.Push(e);
                    continue;
                }
                if (o is Delegate or string || t.IsPrimitive) continue;
                for (var c = t; c is not null && c != typeof(object); c = c.BaseType)
                    foreach (var f in c.GetFields(Inst | BindingFlags.DeclaredOnly))
                        if (!f.FieldType.IsValueType && f.GetValue(o) is { } v) stack.Push(v);
            }
            Assert.Contains(typeof(MeanReversionStrategy), types);
            Assert.Contains(typeof(ArenaVwapTracker), types);
            var bad = types.Where(t => !t.IsSubclassOf(typeof(Delegate)) && !AllowedNs(t)).Select(t => t.FullName).ToArray();
            Assert.True(bad.Length == 0, string.Join("\n", bad));
            Assert.DoesNotContain(types, t => (t.FullName ?? "").Contains("Broker", StringComparison.OrdinalIgnoreCase)
                                             || (t.FullName ?? "").Contains("Dispatcher", StringComparison.OrdinalIgnoreCase)
                                             || (t.FullName ?? "").Contains("Kiwoom", StringComparison.OrdinalIgnoreCase));
        }

        // ═══ §6 스트레스 ═══

        [Fact]
        public async System.Threading.Tasks.Task Headless_Persona_Tab_Mash_With_Max_Speed_Tick_Injection_Never_Freezes_Snapshot()
        {
            var g = new ArenaMatchGovernor(() => Interlocked.Read(ref _now), 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.True(g.Start(1_000_000_000));
            g.Step(Interlocked.Add(ref _now, Ms));
            using var replay = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(100), ownThread: false);
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_persona_" + Guid.NewGuid().ToString("N"));
            using var rec = new ArenaTapeRecorder(dir, 10);
            using var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IArenaPort>(g);
            ctx.Services.AddSingleton<IArenaReplayPort>(replay);
            ctx.Services.AddSingleton<IArenaTapePort>(rec);
            ctx.Services.AddSingleton<IKisFeedLatencyPort>(new StockManager.Adapters.Kis.KisTimeSkewGuard());   // [v12.18.16-DEV] 상단 KIS 지연 뱃지

            var tape = SyntheticTickSource.Create(20_000);
            using var stop = new CancellationTokenSource();
            Exception? pumpFault = null;
            long pumps = 0, injected = 0;
            var pump = new Thread(() =>                                              // 정산 단일 작성자 · 회차당 100틱 주입(100x) · 지연 대기 0
            {
                try
                {
                    int i = 0;
                    while (!stop.IsCancellationRequested)
                    {
                        for (int k = 0; k < 100; k++, i = (i + 1) % tape.Count)
                        {
                            tape.Read(i, out var b);
                            g.OnQuote(in b);
                            if (tape.TryReadTrade(i, out var t)) g.OnTrade(t.Symbol, t.PriceWon, ++injected * 50);
                        }
                        g.Step(Interlocked.Add(ref _now, Ms));
                        Interlocked.Increment(ref pumps);
                    }
                }
                catch (Exception ex) { pumpFault = ex; }
            }) { IsBackground = true };
            pump.Start();

            var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ar-persona .ar-tab")), TimeSpan.FromSeconds(10));
            long v0 = g.Version;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int clicks = 0;
            for (int i = 0; i < 200; i++)
            {
                int k = i;
                bool ok = await cut.InvokeAsync(() =>                               // 조회 → 발화 원자화(오래된 핸들러 ID 하네스 경합 회피)
                {
                    var tabs = cut.FindAll(".ar-persona .ar-tab");
                    if (tabs.Count != ArenaPersona.Count) return false;
                    tabs[k % tabs.Count].Click();
                    return true;
                });
                if (ok) clicks++;
            }
            sw.Stop();
            long v1 = g.Version;
            stop.Cancel();
            Assert.True(pump.Join(5_000), "정산 스레드 교착");
            Assert.Null(pumpFault);
            Assert.True(g.SetPersona(ArenaPersona.MeanReversion));
            g.Step(Interlocked.Add(ref _now, Ms));
            Assert.True(g.TryRead(out var s));
            _out.WriteLine($"[페르소나 스트레스] 탭 {clicks} · {sw.ElapsedMilliseconds}ms · 정산 {pumps} · 주입 {injected} · 버전 {v0}→{v1} · 교체 {s.AiHud.Switches} · AI 체결 {s.Ai.Fills}");
            Assert.True(clicks >= 190, "clicks " + clicks);
            Assert.True(v1 > v0, "스냅샷 동결");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "UI 스레드 정체");
            Assert.Equal(ArenaPersona.MeanReversion, s.AiHud.Persona);
            Assert.True(s.AiHud.Switches > 0);
            Assert.True(s.Ai.CashWon >= 0 && s.Ai.CashWon >= s.Ai.ReservedCashWon);
            cut.WaitForAssertion(() => Assert.Contains("🤖 " + ArenaPersona.Label(ArenaPersona.MeanReversion) + " —", cut.Markup), TimeSpan.FromSeconds(5));
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.DirectoryNotFoundException) { }
        }
    }
}
