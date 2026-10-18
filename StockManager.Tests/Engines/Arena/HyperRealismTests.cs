// ============================================================================
// HyperRealismTests — [v12.18.18-DEV] 아레나 초현실 고도화 적대적 검증(기관급 집행 · 유동성 진공 · 포식자 AI · 하드 리스크 동결)
//   §1 빙산: 노출분만 대기열 · 리필 시 같은 가격 대기열 최하위 재배치(뒤에 있던 주문이 먼저 체결) · 시장성/관통 = 은닉분 포함 전량 · 전량 예약 · 포트 검증
//   §2 유동성 진공: 70% 경계(69% 미발동) · 후속 시장가 (2^k − 1)틱 지수 페널티 · 상한 6 · 창 경과 해소 · 진영/방향 독립 · TWAP 방출
//   §3 포식자: 공격성 산식(연패 · 최근 틸트 · MAE · 상한) · 허수 = 휴먼 래더 사본에만(시뮬레이터 호가 · 체결 불변) · HUD 공개 · 은밀 TWAP 진입
//   §4 하드 리스크: −3% 도달 같은 회차 전량 시장가 청산 + 동결 · 모든 우회(퀵 티켓 · 지정가 · 빙산 · 취소 · 청산 · 핫키 · 래더 · 엔진 직접) 거부 ·
//      지연 대기 취소가 강제 청산을 지우지 못함 · 잔량 이월 청산 · 다음 라운드 해제 · 생산자 경합 중 동결 이후 체결 0
//   §5 δ=0 · GC 0: 진공 · 빙산 리필 · 포식자 허수 · TWAP 정산 루프 · 마진콜 경로
//   §6 Fail-Closed · UI: IL 참조 허용목록 · 헤드리스 동결 오버레이/버튼 비활성 · 빙산 체크박스 → 포트 → 원장 Peak
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using StockManager.Engines.Arena.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class HyperRealismTests
    {
        private const long Sym = 5930, Ms = 10_000, T0 = 638_000_000_000_000_000;
        private readonly ITestOutputHelper _out;
        private long _now = T0;

        public HyperRealismTests(ITestOutputHelper o) => _out = o;

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

        private static ArenaTrade Trade(long px, long qty, int side) => new() { Symbol = Sym, PriceWon = px, Qty = qty, Side = side };

        private static DualBookSimulator Sim(long capital)
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(capital);
            return sim;
        }

        private static ArenaWorkingOrder[] Orders(ArenaBook book)
        {
            var buf = new ArenaWorkingOrder[ArenaBook.MaxOrders];
            int n = book.CopyOrders(buf);
            return buf.Take(n).ToArray();
        }

        private void Tick(ArenaMatchGovernor g, in OrderBookPayload q, long dt = Ms) { _now += dt; g.Advance(in q, hasQuote: true, _now, _now); }

        private void Step(ArenaMatchGovernor g) { _now += Ms; g.Step(_now); }

        private static ArenaSnapshot Snap(ArenaMatchGovernor g, long now)
        {
            g.PublishNow(now);
            Assert.True(g.TryRead(out var s));
            return s;
        }

        // ═══ §1 빙산 ═══

        [Fact]
        public void Iceberg_Refill_Requeues_To_Bottom_Of_L2_Queue()
        {
            var sim = Sim(100_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 100, 69_900, 100));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 30, ArenaOrderKind.Limit, 69_900, T0, peakQty: 10));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 5, ArenaOrderKind.Limit, 69_900, T0));
            sim.Process(T0);
            var o = Orders(h);
            Assert.Equal(2, o.Length);
            Assert.Equal((10L, 10L, 30L, 100L), (o[0].Peak, o[0].Visible, o[0].Qty, o[0].QueueAhead));   // 대기열에는 노출 10 만
            Assert.Equal(100 + 10, o[1].QueueAhead);                                 // 뒤 주문의 앞 잔량 = 표시 100 + 빙산 노출분(은닉 20 제외)
            long need = 30 * 69_900 + DualBookSimulator.Fee(30 * 69_900) + 5 * 69_900 + DualBookSimulator.Fee(5 * 69_900);
            Assert.Equal(need, h.ReservedCashWon);                                    // 은닉분 포함 전량 예약(이중 집행 0)
            var d = default(ArenaDepth);
            sim.FillDepth(Sym, ref d);
            Assert.Equal((15L, 20L, 100L), (d.MyBidQty[0], d.MyHiddenQty, d.MyBidAhead[0]));

            sim.OnTrade(Trade(69_900, 110, -1));                                      // 앞 100 소진 → 빙산 노출 10 체결 → 리필
            o = Orders(h);
            Assert.Equal(10, h.Position(Sym));
            Assert.Equal((20L, 10L, 10L, 1L), (o[0].Qty, o[0].Filled, o[0].Visible, o[0].Refills));
            Assert.Equal(100 + 5, o[0].QueueAhead);                                  // 최하위 재배치: 표시 잔량 + 뒤에 있던 내 주문 5 가 이제 앞
            Assert.Equal(0, o[1].QueueAhead);
            Assert.Equal(1, h.IcebergRefills);

            sim.OnTrade(Trade(69_900, 5, -1));                                        // 원래 뒤였던 주문이 먼저 체결
            o = Orders(h);
            Assert.Single(o);
            Assert.Equal(15, h.Position(Sym));
            Assert.Equal((20L, 100L), (o[0].Qty, o[0].QueueAhead));
        }

        [Fact]
        public void Iceberg_Marketable_And_Trade_Through_Execute_Hidden_Quantity()
        {
            var sim = Sim(100_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 100, 69_900, 100));
            sim.Submit(ArenaSide.Human, Sym, 1, 50, ArenaOrderKind.Limit, 70_000, T0, peakQty: 10);
            sim.Process(T0);
            Assert.Equal(50, h.Position(Sym));                                       // 시장성 순회 = 은닉분 포함 전량
            Assert.Equal(0, h.WorkingCount + h.PendingCount);
            sim.Submit(ArenaSide.Human, Sym, 1, 40, ArenaOrderKind.Limit, 69_800, T0, peakQty: 10);
            sim.Process(T0);
            Assert.Equal(1, h.WorkingCount);
            sim.OnTrade(Trade(69_700, 1, -1));                                        // 내 가격 관통 = 전량
            Assert.Equal(90, h.Position(Sym));
            Assert.Equal(0, h.WorkingCount);
            Assert.Equal(0, h.ReservedCashWon);
            Assert.Equal(0, h.IcebergRefills);
        }

        [Fact]
        public void Iceberg_Port_Validates_And_Reaches_Human_Book_With_Peak()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.Equal(ArenaReject.NotRunning, g.SubmitHumanIceberg(Sym, 1, 30, 69_900, 10));
            g.Rearm(100_000_000, running: true, 1, _now);
            var q = Book(70_000, 100, 69_900, 100);
            Tick(g, in q);
            Assert.Equal(ArenaReject.Invalid, g.SubmitHumanIceberg(Sym, 1, 30, 69_900, 0));
            Assert.Equal(ArenaReject.Invalid, g.SubmitHumanIceberg(Sym, 1, 30, 0, 10));
            Assert.Equal(ArenaReject.None, g.SubmitHumanIceberg(Sym, 1, 30, 69_900, 10));
            Tick(g, in q);
            var o = Orders(g.Simulator.Book(ArenaSide.Human));
            Assert.Single(o);
            Assert.Equal((10L, 10L, 30L), (o[0].Peak, o[0].Visible, o[0].Qty));
            Assert.Equal(ArenaReject.Invalid, g.Simulator.Submit(ArenaSide.Human, Sym, 1, 30, ArenaOrderKind.Limit, 69_900, _now, peakQty: -1));
            Assert.Equal(ArenaReject.Invalid, g.Simulator.Submit(ArenaSide.Human, Sym, 1, 30, ArenaOrderKind.Limit, 69_900, _now, peakQty: 10, intervalTicks: Ms));
            Assert.Equal(ArenaReject.Invalid, g.Simulator.Submit(ArenaSide.Human, Sym, 1, 30, ArenaOrderKind.Market, 0, _now, peakQty: 10));   // TWAP 간격 누락
        }

        // ═══ §2 유동성 진공 · TWAP ═══

        [Fact]
        public void Vacuum_Triggers_At_70_Percent_Is_Per_Camp_And_Side_And_Decays()
        {
            var sim = Sim(1_000_000_000);
            var h = sim.Book(ArenaSide.Human);
            var a = sim.Book(ArenaSide.Ai);
            sim.OnQuote(Book(70_000, 100, 69_900, 100));
            long t = T0;
            void Mkt(int side, int dir, long qty) { sim.Submit(side, Sym, dir, qty, ArenaOrderKind.Market, 0, t); sim.Process(t); }

            Mkt(ArenaSide.Human, 1, 69);                                              // 69% = 미발동
            Assert.Equal(0, h.VacuumEvents);
            Mkt(ArenaSide.Human, 1, 1);                                               // 누적 70% = 발동(이 순회 자체는 페널티 0)
            Assert.Equal((1L, 1, 0L), (h.VacuumEvents, sim.VacuumLevel(ArenaSide.Human, Sym, 1), h.SlippageWon));
            Mkt(ArenaSide.Ai, 1, 10);                                                 // 다른 진영 = 간섭 0
            Assert.Equal((0L, 0L, 0), (a.VacuumPenaltyWon, a.SlippageWon, sim.VacuumLevel(ArenaSide.Ai, Sym, 1)));
            Mkt(ArenaSide.Human, 1, 30);                                              // 1단계 = +1틱 · 단 100% 소진 = 2단계
            Assert.Equal((3_000L, 3_000L, 2), (h.VacuumPenaltyWon, h.SlippageWon, sim.VacuumLevel(ArenaSide.Human, Sym, 1)));
            sim.OnQuote(Book(70_000, 100, 69_900, 100));                              // 새 호가 = 오버레이 초기화 · 진공은 유지(창 기준)
            t += Ms;
            Mkt(ArenaSide.Human, 1, 10);                                              // 2단계 = (2² − 1) = 3틱
            Assert.Equal(3_000 + 10 * 300, h.VacuumPenaltyWon);
            Assert.Equal(0, sim.VacuumLevel(ArenaSide.Human, Sym, -1));
            long cash0 = h.CashWon;
            Mkt(ArenaSide.Human, -1, 10);                                             // 매도측 = 독립(페널티 0)
            Assert.Equal(10 * 69_900 - DualBookSimulator.Fee(699_000) - DualBookSimulator.Tax(699_000), h.CashWon - cash0);
            t += DualBookSimulator.VacuumWindowTicks;                                 // 창 경과 = 해소
            sim.OnQuote(Book(70_000, 100, 69_900, 100));
            long pen = h.VacuumPenaltyWon;
            Mkt(ArenaSide.Human, 1, 10);
            Assert.Equal(pen, h.VacuumPenaltyWon);
            Assert.Equal(0, sim.VacuumLevel(ArenaSide.Human, Sym, 1));
        }

        [Fact]
        public void Vacuum_Penalty_Escalates_Exponentially_To_Cap()
        {
            var sim = Sim(10_000_000_000);
            var h = sim.Book(ArenaSide.Human);
            long prev = 0;
            for (int k = 0; k < 9; k++)
            {
                long t = T0 + k * Ms;
                sim.OnQuote(Book(70_000, 10, 69_900, 10));
                sim.Submit(ArenaSide.Human, Sym, 1, 10, ArenaOrderKind.Market, 0, t);
                sim.Process(t);                                                       // 매 회 1호가 100% 소진 = 단계 +1
                long ticks = (1L << Math.Min(k, DualBookSimulator.MaxVacuumLevel)) - 1;
                Assert.Equal(ticks * 100 * 10, h.VacuumPenaltyWon - prev);
                prev = h.VacuumPenaltyWon;
                _out.WriteLine($"k={k} 단계 {sim.VacuumLevel(ArenaSide.Human, Sym, 1)} · 이번 페널티 {ticks}틱");
            }
            Assert.Equal(DualBookSimulator.MaxVacuumLevel, sim.VacuumLevel(ArenaSide.Human, Sym, 1));
            Assert.Equal(h.VacuumPenaltyWon, h.SlippageWon);                          // 페널티 = 슬리피지에 정직 반영
            Assert.Equal(9, h.VacuumEvents);
        }

        [Fact]
        public void Twap_Releases_Slices_On_Interval_Without_Quote_Update()
        {
            var sim = Sim(100_000_000);
            var a = sim.Book(ArenaSide.Ai);
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Ai, Sym, 1, 40, ArenaOrderKind.Market, 0, T0, peakQty: 10, intervalTicks: 100 * Ms));
            sim.Process(T0);
            Assert.Equal(10, a.Position(Sym));
            sim.Process(T0 + 50 * Ms);
            Assert.Equal(10, a.Position(Sym));                                       // 간격 전 = 방출 0
            for (int k = 1; k <= 3; k++) { sim.Process(T0 + k * 100 * Ms); Assert.Equal(10 + 10 * k, a.Position(Sym)); }
            Assert.Equal(0, a.WorkingCount);
            Assert.Equal(0, a.VacuumEvents);                                          // 슬라이스 1% = 시장 충격 회피
        }

        // ═══ §3 포식자 ═══

        [Fact]
        public void Predator_Aggression_Tracks_Prey_Streak_Tilt_And_Mae()
        {
            var p = new PredatorStrategy();
            const long Cap = 10_000_000, Now = T0;
            p.ObservePrey(0, 0, 0, 0, Cap, Now);
            Assert.Equal((2_500, true, false, 5_800, 2), (p.AggressionBp, p.Stealth, p.PhantomActive, p.EntryImbalanceBp, p.TargetTicks));
            p.ObservePrey(1, 0, 0, 0, Cap, Now);
            Assert.Equal((4_500, true), (p.AggressionBp, p.Stealth));
            p.ObservePrey(2, 0, 0, 0, Cap, Now);
            Assert.Equal((6_500, false, true), (p.AggressionBp, p.Stealth, p.PhantomActive));
            Assert.InRange(p.EntryImbalanceBp, 4_501, 5_799);
            p.ObservePrey(2, 1, Now - 10_000_000, 0, Cap, Now);                       // 1초 전 틸트 = +20%
            Assert.Equal(8_500, p.AggressionBp);
            p.ObservePrey(2, 1, Now - PredatorStrategy.TiltMemoryTicks, 0, Cap, Now);  // 30초 경과 = 망각
            Assert.Equal(6_500, p.AggressionBp);
            p.ObservePrey(0, 0, 0, -100_000, Cap, Now);                               // MAE 1% = +5%
            Assert.Equal(3_000, p.AggressionBp);
            p.ObservePrey(5, 3, Now, -900_000, Cap, Now);                             // 상한
            Assert.Equal((10_000, 4_500, 5), (p.AggressionBp, p.EntryImbalanceBp, p.TargetTicks));
            Assert.Equal((300L, 100L), (p.PhantomBidQty(100), p.PhantomAskQty(100)));       // 100 × 100% × 3 · ⅓
            p.ObservePrey(1, 0, 0, 0, Cap, Now);
            Assert.Equal(0, p.PhantomBidQty(100));                                    // 연패 1 = 허수 0
            p.Reset();
            Assert.Equal((2_500, 0), (p.AggressionBp, p.PreyLossStreak));
        }

        [Fact]
        public void Predator_Phantom_Lives_Only_On_Human_Ladder_Copy_And_Hud_Not_In_Fills()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0, riskLimitBp: 0);
            g.Rearm(100_000_000, running: true, 1, _now);
            g.SetFocus(Sym);
            var q = Book(70_000, 1_000, 69_900, 1_000);
            Tick(g, in q);
            for (int k = 0; k < 2; k++)                                               // 휴먼 2연패(매수 → 스프레드 손실 매도)
            {
                Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, 1, 10));
                Tick(g, in q);
                Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, -1, 10));
                Tick(g, in q);
            }
            Assert.Equal(2, g.Simulator.Book(ArenaSide.Human).Analytics.LossStreak);
            g.PublishNow(_now);
            Assert.True(g.TryReadDepth(out var before) && before.PhantomBidQty == 0);  // 시그널 엔진 페르소나 = 허수 0
            Assert.True(g.SetPersona(ArenaPersona.Predator));
            Tick(g, in q);
            var s = Snap(g, _now);
            Assert.True(g.TryReadDepth(out var d));
            Assert.Equal((6_500, 2), (s.AiHud.AggressionBp, s.AiHud.PreyLossStreak));
            Assert.Equal((1_950L, 650L), (d.PhantomBidQty, d.PhantomAskQty));         // 1,000 × 65% × 3 · ⅓
            Assert.Equal((1_000 + 1_950L, 1_000 + 650L), (d.BidQty[0], d.AskQty[0]));
            Assert.Equal((d.PhantomBidQty, d.PhantomAskQty), (s.AiHud.PhantomBidQty, s.AiHud.PhantomAskQty));   // HUD 공개 = 래더 가산량
            var raw = default(ArenaDepth);
            g.Simulator.FillDepth(Sym, ref raw);
            Assert.Equal((1_000L, 1_000L, 0L), (raw.BidQty[0], raw.AskQty[0], raw.PhantomBidQty));   // 시뮬레이터 호가 불변

            var h = g.Simulator.Book(ArenaSide.Human);
            long slip0 = h.SlippageWon;
            g.SubmitHuman(Sym, 1, 1_200);                                             // 표시 1,650 이어도 실 1호가는 1,000 → 200 은 2호가
            Tick(g, in q);
            Assert.Equal(1_200, h.Position(Sym));
            Assert.Equal(200 * 100, h.SlippageWon - slip0);
        }

        [Fact]
        public void Predator_Stealth_Entry_Executes_As_Twap_In_Ai_Book()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            g.Rearm(100_000_000, running: true, 1, _now);
            g.SetFocus(Sym);
            Assert.True(g.SetPersona(ArenaPersona.Predator));
            var q = Book(70_000, 300, 69_900, 700);                                   // 매수 잔량 70% ≥ 은밀 임계 58%
            Tick(g, in q);
            var ai = g.Simulator.Book(ArenaSide.Ai);
            var o = Orders(ai);
            Assert.Single(o);
            long lot = 10_000_000 / 70_000;
            Assert.Equal((lot / ArenaMatchGovernor.PredatorTwapSlices, ArenaMatchGovernor.PredatorTwapIntervalTicks), (o[0].Peak, o[0].IntervalTicks));
            Assert.Equal(lot / 4, ai.Position(Sym));                                   // 첫 슬라이스만
            var s = Snap(g, _now);
            Assert.Equal((ArenaPersona.Predator, ArenaAiReason.PreyStalk, 1L), (s.AiHud.Persona, s.AiHud.Reason, s.AiHud.Decisions));
            Assert.Equal(0, s.Human.Fills);
            Tick(g, in q, ArenaMatchGovernor.PredatorTwapIntervalTicks);
            Assert.Equal(2 * (lot / 4), ai.Position(Sym));
        }

        // ═══ §4 하드 리스크 동결 ═══

        private ArenaMatchGovernor LockedLive(out long tripEquity)
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.True(g.Start(100_000_000));
            Step(g);
            g.SetFocus(Sym);
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Step(g);
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, 1, 1_000));
            Step(g);
            Assert.Equal(1_000, g.Simulator.Book(ArenaSide.Human).Position(Sym));
            Assert.False(Snap(g, _now).HumanRiskLocked);                              // −0.11% = 한도 안
            g.OnQuote(Book(67_000, 1_000, 66_900, 1_000));
            Step(g);                                                                  // −3.11% → 같은 회차 강제 청산
            tripEquity = 100_000_000 - (70_000_000 + DualBookSimulator.Fee(70_000_000)) + 66_900_000;
            return g;
        }

        [Fact]
        public void Risk_Limit_Liquidates_All_At_Market_And_Locks_Every_Order_Route()
        {
            var g = LockedLive(out long tripEquity);
            var h = g.Simulator.Book(ArenaSide.Human);
            var s = Snap(g, _now);
            Assert.True(s.HumanRiskLocked && g.HumanRiskLocked && h.Frozen);
            Assert.Equal((300, tripEquity), (s.RiskLimitBp, s.RiskTripEquityWon));
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(0, s.Human.OpenPositions);
            long cash = 100_000_000 - (70_000_000 + DualBookSimulator.Fee(70_000_000)) + 66_900_000 - DualBookSimulator.Fee(66_900_000) - DualBookSimulator.Tax(66_900_000);
            Assert.Equal((cash, cash), (h.CashWon, s.Human.EquityWon));

            Assert.Equal(ArenaReject.RiskLocked, g.SubmitHuman(Sym, 1, 1));
            Assert.Equal(ArenaReject.RiskLocked, g.SubmitHumanLimit(Sym, 1, 1, 66_000));
            Assert.Equal(ArenaReject.RiskLocked, g.SubmitHumanIceberg(Sym, 1, 100, 66_000, 10));
            Assert.Equal(ArenaReject.RiskLocked, g.CancelHuman());
            Assert.Equal(ArenaReject.RiskLocked, g.FlattenHuman());
            Assert.True(g.TryReadDepth(out var d));
            Assert.Equal(ArenaReject.RiskLocked, ArenaHotkeys.Execute(g, ArenaHotkeys.Buy, in d, 10_000, s.CapitalWon, out _));
            Assert.Equal(ArenaReject.RiskLocked, ArenaHotkeys.Execute(g, ArenaHotkeys.Flatten, in d, 10_000, s.CapitalWon, out _));
            Assert.Equal(ArenaReject.RiskLocked, ArenaHotkeys.Execute(g, ArenaHotkeys.CancelAll, in d, 10_000, s.CapitalWon, out _));
            Assert.NotEqual(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.Sell, in d, 10_000, s.CapitalWon, out _));
            for (int l = 0; l < ArenaDepth.Levels; l++)
                Assert.Equal(ArenaReject.RiskLocked, ArenaHotkeys.ClickLadder(g, in d, isAsk: false, l, 1_000, s.CapitalWon, out _));
            Assert.Equal(ArenaReject.RiskLocked, g.Simulator.Submit(ArenaSide.Human, Sym, 1, 1, ArenaOrderKind.Market, 0, _now));   // 엔진 직접 = 원장 동결
            Assert.Equal(ArenaReject.RiskLocked, g.Simulator.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.CancelAll, 0, _now));
            Assert.False(g.Simulator.MarginCall(ArenaSide.Human));                   // 이중 발동 = 무동작

            long fills = h.Fills;
            for (int i = 0; i < 20; i++) { g.OnQuote(Book(66_000 + i * 100, 1_000, 65_900 + i * 100, 1_000)); Step(g); }
            Assert.Equal((fills, 0L, 0, 0), (h.Fills, h.Position(Sym), h.PendingCount, h.WorkingCount));
            Assert.Equal(ArenaReject.None, g.Simulator.Submit(ArenaSide.Ai, Sym, 1, 1, ArenaOrderKind.Market, 0, _now));   // AI 원장 = 무관

            Assert.True(g.Start(100_000_000));                                        // 다음 라운드 = 해제
            Step(g);
            Assert.False(g.HumanRiskLocked || h.Frozen || Snap(g, _now).HumanRiskLocked);
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, 1, 1));
        }

        [Fact]
        public void Margin_Call_Purges_Pending_Cancel_And_Carries_Liquidation_Until_Flat()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 40 * Ms, latencyMaxTicks: 40 * Ms);
            g.Rearm(100_000_000, running: true, 1, _now);
            var h = g.Simulator.Book(ArenaSide.Human);
            var q = Book(70_000, 1_000, 69_900, 50);                                  // 매수 잔량 얇음(10단 × 50 = 500)
            Tick(g, in q);
            g.SubmitHuman(Sym, 1, 1_000);
            Tick(g, in q);                                                            // 접수(지연 40ms)
            Tick(g, in q, 50 * Ms);
            Assert.Equal(1_000, h.Position(Sym));
            g.SubmitHumanLimit(Sym, -1, 400, 80_000);
            Tick(g, in q);
            Tick(g, in q, 50 * Ms);
            Assert.Equal((1, 400L), (h.WorkingCount, h.Position(Sym) - h.Sellable(Sym)));
            Assert.Equal(ArenaReject.None, g.CancelHuman());                          // 지연 중 취소(활성화되면 강제 청산 매도를 지울 수 있는 경로)
            Assert.Equal(ArenaReject.None, g.SubmitHumanLimit(Sym, 1, 10, 60_000));
            Tick(g, in q, 10 * Ms);
            Assert.Equal(2, h.PendingCount);
            var drop = Book(67_000, 1_000, 66_900, 50);
            Tick(g, in drop, 10 * Ms);                                                // 지연 미경과 상태에서 −3% 도달
            Assert.True(g.HumanRiskLocked);
            Assert.Equal((0, 1, 500L), (h.PendingCount, h.WorkingCount, h.Position(Sym)));   // 잔량 500 = 작동 시장가 매도로 이월
            var refill = Book(66_800, 1_000, 66_700, 1_000);
            Tick(g, in refill, 50 * Ms);                                              // 원래 취소가 활성화됐을 시점 이후
            Assert.Equal((0L, 0, 0), (h.Position(Sym), h.WorkingCount, h.PendingCount));
            Assert.True(Snap(g, _now).HumanRiskLocked);
        }

        [Fact]
        public void Producer_Hammer_During_Trip_Gets_Zero_Fills_After_Lock()
        {
            var g = new ArenaMatchGovernor(() => Interlocked.Read(ref _now), 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.True(g.Start(100_000_000));
            g.Step(Interlocked.Add(ref _now, Ms));
            g.OnQuote(Book(70_000, 100_000, 69_900, 100_000));
            g.Step(Interlocked.Add(ref _now, Ms));
            g.SubmitHuman(Sym, 1, 1_000);
            g.Step(Interlocked.Add(ref _now, Ms));
            using var stop = new CancellationTokenSource();
            using var joined = new CountdownEvent(1);
            int leaked = 0;
            long calls = 0;
            new Thread(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        bool locked = g.HumanRiskLocked;                               // 래치는 0 → 1 단조 — 관측 후 접수 성공 = 누출
                        int r1 = g.SubmitHuman(Sym, 1, 1), r2 = g.SubmitHumanLimit(Sym, 1, 1, 69_000), r3 = g.SubmitHumanIceberg(Sym, 1, 5, 69_000, 1), r4 = g.CancelHuman();
                        Interlocked.Increment(ref calls);
                        if (locked && (r1 | r2 | r3 | r4) != ArenaReject.RiskLocked) Interlocked.Increment(ref leaked);
                    }
                }
                finally { joined.Signal(); }
            }) { IsBackground = true }.Start();
            var h = g.Simulator.Book(ArenaSide.Human);
            long fillsAtTrip = -1;
            for (int i = 0; i < 400; i++)
            {
                long c0 = Interlocked.Read(ref calls);
                SpinWait.SpinUntil(() => Interlocked.Read(ref calls) > c0, 1_000);   // 회차마다 생산자 진행 보장(경합 실재)
                if (i == 100) g.OnQuote(Book(67_000, 100_000, 66_900, 100_000));
                g.Step(Interlocked.Add(ref _now, Ms));
                if (fillsAtTrip < 0 && g.HumanRiskLocked) fillsAtTrip = h.Fills;
            }
            stop.Cancel();
            Assert.True(joined.Wait(TimeSpan.FromSeconds(10)));
            _out.WriteLine($"[동결 경합] 생산자 호출 {calls} · 동결 시 체결 {fillsAtTrip} · 최종 체결 {h.Fills} · 거부 {h.Rejects}");
            Assert.True(calls > 0);
            Assert.True(fillsAtTrip > 0, "미발동");
            Assert.Equal(0, leaked);
            Assert.Equal(fillsAtTrip, h.Fills);                                       // 동결 회차 이후 휴먼 체결 0
            Assert.Equal((0L, 0, 0), (h.Position(Sym), h.WorkingCount, h.PendingCount));
        }

        // ═══ §5 δ=0 · GC 0 ═══

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
        public void Market_Impact_Iceberg_Predator_Twap_Loop_Is_Zero_Alloc_With_No_Gc()
        {
            var g = new ArenaMatchGovernor(() => _now, 4_096, latencyMinTicks: 0, latencyMaxTicks: 0, riskLimitBp: 0);
            g.SetFocus(Sym);
            var q = Book(70_000, 40, 69_900, 120);                                    // 얇은 매도 1호가 = 시장가 진공 · 매수 우위 = 포식자 진입
            long sink = 0;
            void Loop()
            {
                g.Rearm(1_000_000_000, running: true, 4_096, _now);
                g.SetPersona(ArenaPersona.Predator);
                for (int i = 0; i < 20_000; i++)
                {
                    q.AskQty[0] = 30 + (i & 15);
                    switch (i & 7)
                    {
                        case 0: g.SubmitHuman(Sym, 1, 40); break;                     // 1호가 70%+ 소진 → 진공
                        case 1: g.SubmitHuman(Sym, 1, 20); break;                     // 진공 페널티 체결
                        case 2: g.SubmitHumanIceberg(Sym, 1, 60, q.BidPrice[0], 5); break;
                        case 4: g.SubmitHuman(Sym, -1, 60); break;                    // 스프레드 손실 = 휴먼 연패 → 포식자 허수
                        case 6: g.CancelHuman(); break;
                    }
                    _now += Ms;
                    g.ApplyTrade(new ArenaTrade { Symbol = Sym, PriceWon = q.BidPrice[0], Qty = 125, Side = -1 }, _now);   // 빙산 노출분 소진 → 리필
                    g.Advance(in q, hasQuote: true, _now, _now);
                    if ((i & 63) == 0)
                    {
                        g.PublishNow(_now);
                        if (g.TryRead(out var s)) sink += s.Human.VacuumEvents + s.Human.IcebergRefills + s.AiHud.AggressionBp + s.AiHud.PhantomBidQty;
                        if (g.TryReadDepth(out var d)) sink += d.PhantomBidQty + d.MyHiddenQty;
                    }
                }
            }
            Loop();
            var gc0 = Gcs();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, Gcs());
            var fin = Snap(g, _now);
            _out.WriteLine($"[δ=0] 휴먼 체결 {fin.Human.Fills} · 진공 {fin.Human.VacuumEvents} · 페널티 {fin.Human.VacuumPenaltyWon:N0} · 리필 {fin.Human.IcebergRefills} · AI 체결 {fin.Ai.Fills} · 공격성 {fin.AiHud.AggressionBp} · sink {sink}");
            Assert.True(fin.Human.VacuumEvents > 0 && fin.Human.VacuumPenaltyWon > 0 && fin.Human.IcebergRefills > 0 && fin.Ai.Fills > 0);
            Assert.True(sink != 0);
        }

        [Fact]
        public void Margin_Call_Path_Is_Zero_Alloc()
        {
            var g = new ArenaMatchGovernor(() => _now, 4_096, latencyMinTicks: 0, latencyMaxTicks: 0);
            var up = Book(70_000, 1_000, 69_900, 1_000);
            var down = Book(67_000, 1_000, 66_900, 1_000);
            int trips = 0;
            void Loop()
            {
                for (int i = 0; i < 2_000; i++)
                {
                    g.Rearm(100_000_000, running: true, 4_096, _now);
                    _now += Ms; g.Advance(in up, hasQuote: true, _now, _now);
                    g.SubmitHuman(Sym, 1, 1_000);
                    _now += Ms; g.Advance(in up, hasQuote: true, _now, _now);
                    _now += Ms; g.Advance(in down, hasQuote: true, _now, _now);        // 마진콜
                    if (g.HumanRiskLocked) trips++;
                }
            }
            Loop();
            var gc0 = Gcs();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, Gcs());
            Assert.True(trips >= 4_000);
        }

        // ═══ §6 Fail-Closed · UI ═══

        private static bool AllowedNs(Type t)
        {
            while (t.HasElementType) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) return AllowedNs(t.GetGenericTypeDefinition()) && t.GetGenericArguments().All(AllowedNs);
            if (t.IsGenericParameter || t.FullName == "<PrivateImplementationDetails>") return true;
            if (t == typeof(OrderBookPayload) || t == typeof(OrderBookLevels10)) return true;
            if (t.DeclaringType is { } d && AllowedNs(d.IsGenericType ? d.GetGenericTypeDefinition() : d)) return true;
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal) || ns.StartsWith("System.Numerics", StringComparison.Ordinal)
                || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arena" || ns == "StockManager.Engines.Arena.Strategy";
        }

        [Fact]
        public void Predator_Simulator_And_Analytics_Il_References_Only_Allowlisted_Types()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            foreach (var type in new[] { typeof(PredatorStrategy), typeof(DualBookSimulator), typeof(ArenaBook), typeof(ArenaWorkingOrder), typeof(ArenaTradeAnalytics) })
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
            var members = typeof(IArenaPort).GetMembers().Select(m => m.Name).Concat(typeof(PredatorStrategy).GetMembers().Select(m => m.Name));
            Assert.DoesNotContain(members, n => n.Contains("Arm", StringComparison.OrdinalIgnoreCase) || n.Contains("Broker", StringComparison.OrdinalIgnoreCase)
                                             || n.Contains("Live", StringComparison.OrdinalIgnoreCase));
        }

        private static Bunit.TestContext Ctx(ArenaMatchGovernor g, ArenaTickReplayEngine replay, ArenaTapeRecorder rec)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IArenaPort>(g);
            ctx.Services.AddSingleton<IArenaReplayPort>(replay);
            ctx.Services.AddSingleton<IArenaTapePort>(rec);
            ctx.Services.AddSingleton<IKisFeedLatencyPort>(new StockManager.Adapters.Kis.KisTimeSkewGuard());
            return ctx;
        }

        [Fact]
        public void Headless_Lock_Overlay_Disables_Tickets_And_Iceberg_Checkbox_Reaches_Book()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_hyper_" + Guid.NewGuid().ToString("N"));
            using var replay = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(100), ownThread: false);
            using var rec = new ArenaTapeRecorder(dir, 10);
            try
            {
                var locked = LockedLive(out _);
                using (var ctx = Ctx(locked, replay, rec))
                {
                    var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
                    Assert.Contains("계좌 동결 (Risk Limit Exceeded)", cut.Markup);
                    Assert.Single(cut.FindAll(".ar-freeze"));
                    Assert.All(cut.FindAll(".ar-ticket button"), b => Assert.True(b.HasAttribute("disabled")));
                    Assert.True(cut.Find(".ar-ice-on").HasAttribute("disabled"));
                    Assert.Contains("locked", cut.Find(".ar-console").ClassName);
                }

                var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
                Assert.True(g.Start(100_000_000));
                Step(g);
                g.OnQuote(Book(70_000, 100, 69_900, 100));
                Step(g);
                using (var ctx = Ctx(g, replay, rec))
                {
                    var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
                    Assert.Empty(cut.FindAll(".ar-freeze"));
                    cut.Find(".ar-ice-on").Change(true);
                    cut.Find(".ar-ice-peak").Change("10");
                    cut.FindAll(".ar-ticket input")[1].Change("30");                 // 수량(렌더마다 재조회 — 오래된 핸들러 ID 회피)
                    cut.FindAll(".ar-ticket input")[2].Change("69900");              // 지정가
                    cut.FindAll(".ar-ticket button").Single(b => b.TextContent.Contains("빙산 매수")).Click();
                    Step(g);
                    var o = Orders(g.Simulator.Book(ArenaSide.Human));
                    Assert.Single(o);
                    Assert.Equal((10L, 10L, 30L, 69_900L), (o[0].Peak, o[0].Visible, o[0].Qty, o[0].LimitPx));
                }
            }
            finally { try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.DirectoryNotFoundException) { } }
        }
    }
}
