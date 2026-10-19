// ============================================================================
// SyntheticShortingTests — [v12.18.23-DEV] 아레나 가상 공매도(숏 개시 · 숏커버 · 담보 · 시가평가 · −3% 마진콜 강제 커버)
//
//  §1 회계 — 숏 개시(순매도 대금 · 거래세 · 담보 = 대금 × 150% 잠금 · 가용 현금) · 시가평가(매도호가 · 부채) · 숏커버 익절/손절 실현손익 · 부분 커버 비례 ·
//     전량 커버 후 잠금 0 · 현금 보존 항등식
//  §2 규칙 — 증거금 부족 = InsufficientCash · 커버 > 숏 = InsufficientPosition(0 교차 금지) · 롱 청산 매도 > 보유 = 거부 · 롱/숏 동시 개시 = OppositeExposure ·
//     숏 지정가 대기 + 수동 체결 · 비상 청산(Space) = 숏커버
//  §3 마진콜 — 숏 → 매도호가 상승 → 자산 ≤ 자본금 × 97% 같은 회차 동결 + 시장가 강제 커버(현금 한도 무시) · 직전 1틱은 미격발 · 동결 후 발주 RiskLocked
//  §4 AI — 평균회귀 상단 숏 진입 → VWAP 회귀 커버(거버너 경유) · 포식자 휴먼 숏 손실 = 스퀴즈(공격성 가산 · 매복 임계 · 허수 활성)
//  §5 핫키/래더 — 무포지션 S = 숏 진입 · 숏 중 B = 커버(커버 가능 상한) · 숏 중 매도호가 클릭 = 추가 숏 · 래더 Coverable · 스냅샷 방향
//  §6 δ=0 · Fail-Closed — 10만 회 숏 개시/커버 회계 0B · IL 허용목록(브로커/디스패처/무장 형식 0)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using StockManager.Engines.Arena.Strategy;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class SyntheticShortingTests
    {
        private const long Sym = 5930, T0 = 638_000_000_000_000_000, Ms = 10_000;
        private long _now = T0;

        private static OrderBookPayload Book(long ask, long askQty, long bid, long bidQty, long tick = 100)
        {
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * tick; b.AskQty[l] = askQty;
                b.BidPrice[l] = bid - l * tick; b.BidQty[l] = bidQty;
            }
            return b;
        }

        private static DualBookSimulator Sim(long capital = 10_000_000)
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(capital);
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            return sim;
        }

        private static void Do(DualBookSimulator sim, int dir, long qty, int kind = ArenaOrderKind.Market, long px = 0)
        {
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, dir, qty, kind, px, T0));
            sim.Process(T0);
        }

        // 숏 100 @ 69,900: 대금 6,990,000 · 수수료 1,048 · 세금 12,582 · 순매도 6,976,370 · 담보 10,485,000
        private const long Proceeds = 6_976_370, Lock100 = 10_485_000;

        // ════════════ §1 회계 ════════════

        [Fact]
        public void ShortOpen_Accounting_Collateral_And_MarkToMarket()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            Do(sim, -1, 100);
            Assert.Equal(-100, h.Position(Sym));
            Assert.Equal(10_000_000 + Proceeds, h.CashWon);
            Assert.Equal(Lock100, h.ShortLockedWon);
            Assert.Equal(10_000_000 + Proceeds - Lock100, h.AvailableCashWon);
            Assert.Equal((1_048L, 12_582L, 1L, 0L), (h.FeesWon, h.TaxWon, h.ShortOpenFills, h.ReservedCashWon));
            Assert.Equal((0, 1), (h.LongPositions, h.ShortPositions));
            Assert.Equal((0L, 100L), (h.Sellable(Sym), h.Coverable(Sym)));
            long eq = sim.MarkToMarket(ArenaSide.Human, out long u);
            Assert.Equal(10_000_000 + Proceeds - 7_000_000, eq);                  // 숏 = 매도호가(되사는 값) 부채
            Assert.Equal(Proceeds - 7_000_000, u);                                // −23,630
        }

        [Fact]
        public void Cover_TakeProfit_And_StopLoss_Realized_And_LockReleased()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            Do(sim, -1, 100);
            sim.OnQuote(Book(69_000, 1_000, 68_900, 1_000));
            Do(sim, 1, 40);                                                        // 부분 커버 40 @ 69,000
            long cost40 = 2_760_000 + 414;
            long basis40 = Proceeds * 40 / 100;
            Assert.Equal(basis40 - cost40, h.RealizedWon);
            Assert.Equal((-60L, 1, 0), (h.Position(Sym), h.Wins, h.Losses));
            Assert.Equal(Lock100 - Lock100 * 40 / 100, h.ShortLockedWon);
            sim.OnQuote(Book(71_000, 1_000, 70_900, 1_000));
            Do(sim, 1, 60);                                                        // 잔여 60 @ 71,000 = 손절
            long cost60 = 4_260_000 + 639;
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal((0L, 0, 0), (h.ShortLockedWon, h.LongPositions, h.ShortPositions));
            Assert.Equal(basis40 - cost40 + (Proceeds - basis40) - cost60, h.RealizedWon);
            Assert.Equal((1, 1, 2L), (h.Wins, h.Losses, h.ShortCoverFills));
            Assert.Equal(10_000_000 + h.RealizedWon, h.CashWon);                  // 현금 보존: 무포지션 = 자본 + 실현손익
            Assert.Equal(h.CashWon, h.AvailableCashWon);
        }

        // ════════════ §2 규칙 ════════════

        [Fact]
        public void Margin_And_Crossing_And_Opposite_Rules()
        {
            var sim = Sim(1_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.Submit(ArenaSide.Human, Sym, -1, 30, ArenaOrderKind.Market, 0, T0);  // 증거금 1,048,500 + 비용 > 100만
            sim.Process(T0);
            Assert.Equal(ArenaReject.InsufficientCash, h.LastReject);
            Do(sim, -1, 10);
            sim.Submit(ArenaSide.Human, Sym, 1, 11, ArenaOrderKind.Market, 0, T0);   // 커버 > 숏 = 0 교차 금지
            sim.Process(T0);
            Assert.Equal(ArenaReject.InsufficientPosition, h.LastReject);
            Assert.Equal(-10, h.Position(Sym));

            var s2 = Sim();
            var b2 = s2.Book(ArenaSide.Human);
            Do(s2, 1, 5, ArenaOrderKind.Limit, 69_000);                            // 롱 개시 지정가 대기
            s2.Submit(ArenaSide.Human, Sym, -1, 5, ArenaOrderKind.Market, 0, T0);    // 숏 개시 = 반대 방향 진입 진행 중
            s2.Process(T0);
            Assert.Equal(ArenaReject.OppositeExposure, b2.LastReject);

            var s3 = Sim();
            var b3 = s3.Book(ArenaSide.Human);
            Do(s3, 1, 5);
            s3.Submit(ArenaSide.Human, Sym, -1, 6, ArenaOrderKind.Market, 0, T0);    // 롱 5 에 매도 6 = 거부(숏 전환 없음)
            s3.Process(T0);
            Assert.Equal(ArenaReject.InsufficientPosition, b3.LastReject);
            Assert.Equal(5, b3.Position(Sym));
        }

        [Fact]
        public void ShortLimit_Queues_Then_PassiveFill_And_Flatten_Covers()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            Do(sim, -1, 20, ArenaOrderKind.Limit, 70_000);
            Assert.Equal(0, h.Position(Sym));
            Assert.True(h.ReservedCashWon > 0);
            sim.OnTrade(new ArenaTrade { Symbol = Sym, PriceWon = 70_000, Qty = 1_020, Side = 1 });   // 앞 1,000 소진 + 20 체결
            Assert.Equal(-20, h.Position(Sym));
            Assert.Equal(0, h.ReservedCashWon);
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.Flatten, 0, T0));
            sim.Process(T0);
            Assert.Equal(0, h.Position(Sym));                                     // 비상 청산 = 시장가 숏커버
            Assert.Equal(0, h.ShortLockedWon);
        }

        // ════════════ §3 마진콜 ════════════

        private ArenaMatchGovernor Running(long capital = 10_000_000)
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.True(g.Start(capital));
            Step(g);
            g.SetFocus(Sym);
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Step(g);
            return g;
        }

        private void Step(ArenaMatchGovernor g) { _now += Ms; g.Step(_now); }
        private static ArenaSnapshot Snap(ArenaMatchGovernor g) { Assert.True(g.TryRead(out var s)); return s; }

        [Fact]
        public void Short_PriceRises_MarginCall_At_Minus3Pct_ForcedCover_Atomic()
        {
            var g = Running();
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, -1, 100));
            Step(g);
            var s = Snap(g);
            Assert.Equal((1, ArenaPositionSide.Short), (s.Human.ShortPositions, s.HumanFocusSide));
            Assert.Equal(10_000_000 + Proceeds - 7_000_000, s.Human.EquityWon);
            g.OnQuote(Book(72_700, 1_000, 72_600, 1_000));                          // 자산 = 16,976,370 − 7,270,000 = 9,706,370 > 9,700,000
            Step(g);
            s = Snap(g);
            Assert.False(s.HumanRiskLocked);
            Assert.True(s.Human.UnrealizedWon < 0);
            g.OnQuote(Book(72_800, 1_000, 72_700, 1_000));                          // 9,696,370 ≤ 하한 → 같은 회차 동결 + 강제 커버
            Step(g);
            s = Snap(g);
            Assert.True(s.HumanRiskLocked);
            Assert.Equal(9_696_370, s.RiskTripEquityWon);                         // 발동 시 자산 = 16,976,370 − 7,280,000
            Assert.Equal((0, 0, 0L), (s.Human.OpenPositions, s.Human.ShortPositions, s.Human.ShortLockedWon));
            Assert.Equal(Proceeds - (7_280_000 + 1_092), s.Human.RealizedWon);   // −304,722
            Assert.Equal(10_000_000 + Proceeds - 7_281_092, s.Human.EquityWon);
            Assert.Equal(ArenaPositionSide.Flat, s.HumanFocusSide);
            Assert.Equal(ArenaReject.RiskLocked, g.SubmitHuman(Sym, -1, 1));
        }

        [Fact]
        public void ForcedCover_Ignores_CashLimit_NegativeCash_Is_RealLoss()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            Do(sim, -1, 100);
            var spike = Book(210_000, 1_000, 209_900, 1_000);                       // 3배 급등 — 커버 비용 21,003,150 > 현금 16,976,370
            sim.OnQuote(in spike);
            Assert.True(sim.MarginCall(ArenaSide.Human));
            Assert.Equal(0, h.Position(Sym));
            Assert.True(h.CashWon < 0, "강제 커버 = 현금 한도 무시(음수 = 실제 손실)");
            Assert.Equal(10_000_000 + h.RealizedWon, h.CashWon);
            Assert.True(h.Frozen);
        }

        // ════════════ §4 AI ════════════

        [Fact]
        public void MeanReversion_Upper_Band_Shorts_Then_Covers_On_Vwap_Revert()
        {
            var mr = new MeanReversionStrategy();
            var wall = Book(101_700, 300, 101_600, 100);
            mr.OnQuote(in wall);
            static ArenaMarketState St(long bid, long ask, long pos, long cost, long dev)
                => new(Sym, T0, bid, ask, bid, 100_000, dev, 500, 50, pos, cost, busy: false);
            Assert.Equal(ArenaAiAction.EnterShort, mr.Evaluate(St(101_600, 101_700, 0, 0, 165)).Action);
            var hold = mr.Evaluate(St(101_000, 101_100, -10, 1_016_000, 105));
            Assert.Equal((ArenaAiReason.Holding, 102_000L), (hold.Reason, hold.StopPx));   // 첫 숏 판단 이격 105 → 손절 이격 = max(105, 150) + 50 = +2.00%
            Assert.Equal(ArenaAiReason.BandBreak, mr.Evaluate(St(102_100, 102_200, -10, 1_016_000, 215)).Reason);
            var mr2 = new MeanReversionStrategy();
            mr2.OnQuote(in wall);
            Assert.Equal(ArenaAiAction.EnterShort, mr2.Evaluate(St(101_600, 101_700, 0, 0, 165)).Action);
            var exit = mr2.Evaluate(St(99_900, 100_000, -10, 1_016_000, -5));
            Assert.Equal((ArenaAiAction.Exit, ArenaAiReason.VwapReverted), (exit.Action, exit.Reason));
        }

        [Fact]
        public void Governor_Executes_Ai_Short_Entry_And_Cover()
        {
            var g = Running(100_000_000);
            Assert.True(g.SetPersona(ArenaPersona.MeanReversion));
            Step(g);
            for (int i = 0; i < 25; i++) g.OnTradeQty(Sym, 100_000, 10, 0);   // VWAP 100,000 예열
            g.OnQuote(Book(101_700, 300, 101_600, 100));                            // +1.65% · 매도벽 300 ≥ 100 × 2
            Step(g); Step(g);
            var s = Snap(g);
            Assert.Equal(ArenaPositionSide.Short, s.AiTargetSide);
            Assert.Equal(1, s.Ai.ShortPositions);
            g.OnQuote(Book(100_000, 1_000, 99_900, 1_000));                         // 매도 1호가 ≤ VWAP = 회귀
            Step(g); Step(g);
            s = Snap(g);
            Assert.Equal(ArenaPositionSide.Flat, s.AiTargetSide);
            Assert.Equal((0, 0L), (s.Ai.OpenPositions, s.Ai.ShortLockedWon));
            Assert.True(s.Ai.RealizedWon > 0, "상단 숏 → 회귀 커버 = 익절 " + s.Ai.RealizedWon);
        }

        [Fact]
        public void Predator_Detects_Human_Losing_Short_Squeeze()
        {
            var p = new PredatorStrategy();
            p.ObservePrey(0, 0, 0, 0, 10_000_000, T0);
            int baseAggr = p.AggressionBp;
            int stalk = p.EntryImbalanceBp;
            p.ObservePreyShort(100, 50_000);                                       // 숏 수익 중 = 스퀴즈 아님
            Assert.False(p.SqueezeActive);
            p.ObservePrey(0, 0, 0, 0, 10_000_000, T0);
            p.ObservePreyShort(100, -30_000);                                      // 숏 평가손 = 스퀴즈
            Assert.True(p.SqueezeActive && p.PhantomActive);
            Assert.Equal(baseAggr + PredatorStrategy.SqueezeBoostBp, p.AggressionBp);
            Assert.Equal(PredatorStrategy.AmbushImbalanceBp, p.EntryImbalanceBp);
            Assert.True(p.EntryImbalanceBp < stalk);
            var q = Book(70_000, 100, 69_900, 120);                                 // 매수 비율 54.5% ≥ 매복 45%
            p.OnQuote(in q);
            var sig = p.Evaluate(new ArenaMarketState(Sym, T0, 69_900, 70_000, 69_950, 0, 0, 0, 0, 0, 0, busy: false));
            Assert.Equal((ArenaAiAction.Enter, ArenaAiReason.PreySqueeze), (sig.Action, sig.Reason));
            p.ObservePrey(0, 0, 0, 0, 10_000_000, T0);
            p.ObservePreyShort(0, 0);
            Assert.False(p.SqueezeActive);
        }

        // ════════════ §5 핫키 · 래더 ════════════

        [Fact]
        public void Hotkeys_FlatSell_Shorts_ShortBuy_Covers_Capped_AskClick_AddsShort()
        {
            var g = Running();
            Assert.True(g.TryReadDepth(out var d));
            Assert.Equal(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.Sell, in d, 1_000, 10_000_000, out long q));   // 무포지션 S = 숏 진입
            Assert.Equal(14, q);                                                   // 1,000,000 ÷ 69,900
            Step(g);
            Assert.True(g.TryReadDepth(out d));
            Assert.Equal((-14L, 14L, 0L), (d.PositionQty, d.Coverable, d.Sellable));
            Assert.Equal(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.Buy, in d, 10_000, 10_000_000, out q));     // 숏 중 B = 커버(상한 14)
            Assert.Equal(14, q);
            Step(g);
            Assert.True(g.TryReadDepth(out d));
            Assert.Equal(0, d.PositionQty);
            Assert.Equal(ArenaReject.None, ArenaHotkeys.ClickLadder(g, in d, isAsk: true, level: 0, 1_000, 10_000_000, out q));   // 매도호가 클릭 = 숏 지정가
            Assert.Equal(14, q);
            var flat = new ArenaDepth { HasBook = true, Symbol = Sym, PositionQty = -5, Coverable = 0 };
            flat.AskPx[0] = 70_000; flat.BidPx[0] = 69_900;
            Assert.Equal(ArenaReject.InsufficientPosition, ArenaHotkeys.Execute(g, ArenaHotkeys.Buy, in flat, 1_000, 10_000_000, out _));   // 커버 예약 소진
        }

        // ════════════ §6 δ=0 · Fail-Closed ════════════

        [Fact]
        public void HundredThousand_ShortOpen_Cover_Accounting_ZeroAlloc()
        {
            var sim = Sim(1_000_000_000_000);
            var h = sim.Book(ArenaSide.Human);
            var q = Book(70_000, 1_000, 69_900, 1_000);
            sim.Submit(ArenaSide.Human, Sym, -1, 1, ArenaOrderKind.Market, 0, T0); sim.Process(T0);
            sim.Submit(ArenaSide.Human, Sym, 1, 1, ArenaOrderKind.Market, 0, T0); sim.Process(T0);   // 예열
            long min = long.MaxValue;
            for (int w = 0; w < 3 && min != 0; w++)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 50_000; i++)
                {
                    sim.OnQuote(in q);                                                 // 새 스냅샷 = 충격 오버레이 초기화(잔량 고갈 방지)
                    sim.Submit(ArenaSide.Human, Sym, -1, 2, ArenaOrderKind.Market, 0, T0); sim.Process(T0);   // 숏 개시
                    sim.Submit(ArenaSide.Human, Sym, 1, 2, ArenaOrderKind.Market, 0, T0); sim.Process(T0);    // 숏커버
                    if ((i & 1023) == 0) sim.MarkToMarket(ArenaSide.Human, out _);
                }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
            }
            Assert.Equal(0, min);
            Assert.True(h.ShortOpenFills >= 50_000 && h.ShortCoverFills >= 50_000);   // 측정 창 1회 이상 × 5만 왕복
            Assert.Equal(0, h.ShortLockedWon);
        }

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
        public void Short_Paths_Il_References_No_Broker_Dispatcher_Or_Arming_Types()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            foreach (var type in new[] { typeof(DualBookSimulator), typeof(ArenaBook), typeof(ArenaWorkingOrder), typeof(MeanReversionStrategy), typeof(PredatorStrategy),
                                         typeof(ArenaTradeAnalytics), typeof(ArenaHotkeys) })
            {
                foreach (var f in type.GetFields(All)) if (!AllowedNs(f.FieldType)) bad.Add(type.Name + "." + f.Name + " : " + f.FieldType.FullName);
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = r as Type ?? r.DeclaringType;
                        if (owner is not null && !AllowedNs(owner) && !(type == typeof(ArenaHotkeys) && owner.Namespace == "MS2E.UI.Portable.Helpers"))
                            bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + r.Name);
                    }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
