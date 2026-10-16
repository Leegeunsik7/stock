// ============================================================================
// ArenaProConsoleTests — [v12.18.14-DEV] 아레나 프로 콘솔(핫키 · DOM 래더) · 사후 복기 분석 적대적 검증
//   §1 핫키 매핑: code 우선(한글 IME key 'ㅠ/ㄴ/ㅊ' 무관) · key 폴백 · 자동 반복 = 발주/청산/취소 무시 · 비중 4종 · 수량 공식
//   §2 디스패치 → 휴먼 큐: B = 최우선 매도호가 지정가(즉시 체결) · S = 매도 가능 상한 · C = 전량 취소 · Space = 지연 1회 후 미체결(지연 대기 포함) 취소 + 전량 시장가 청산 ·
//      래더 클릭 = 그 가격 지정가 + 래더 내 주문/앞 잔량 투영 · 래더 밖 가격 · 매도 열 무보유 거부
//   §3 MAE/MFE: 정확값(진입 원가 · 최우선 매수 평가 · 청산 실현) · 부분 매도 누적 · 보유 시간(시장 시각) · 포착률 · 슬리피지 드래그 · 복기 사본(종료 후)
//   §4 틸트: 평온 기준선 → 연패 2회 후 주기 급단축 · 규모 급증 경보 · 연패 1회 기준선 동결 · 승리 시 해제
//   §5 δ=0 · GC 0: 핫키 분류 + 디스패치 + 정산/래더/복기 게시 루프 · 체결/시가평가 분석 추적 루프
//   §6 Fail-Closed: ArenaHotkeys IL 참조 = System + Portable 만 · 포트 멤버만 호출 · 정적 브로커 경로 부재
//   §7 스트레스: bUnit 헤드리스 핫키 연타 + 래더 고속 클릭 + 동시 정산 스레드 — 예외 0 · 교착 0 · 원장 불변식(현금 · 예약 ≥ 0)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaProConsoleTests
    {
        private const long Sym = 5930, Ms = 10_000, Sec = 10_000_000;
        private readonly ITestOutputHelper _out;
        private long _now = 638_000_000_000_000_000;

        public ArenaProConsoleTests(ITestOutputHelper o) => _out = o;

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

        private ArenaMatchGovernor Running(long capital = 10_000_000, long latency = 0)
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: latency, latencyMaxTicks: latency);
            Assert.True(g.Start(capital));
            Step(g);
            g.SetFocus(Sym);
            return g;
        }

        private void Step(ArenaMatchGovernor g, long dt = Ms) { _now += dt; g.Step(_now); }

        private static ArenaDepth Depth(ArenaMatchGovernor g) { Assert.True(g.TryReadDepth(out var d)); return d; }
        private static ArenaSnapshot Snap(ArenaMatchGovernor g) { Assert.True(g.TryRead(out var s)); return s; }

        // ═══ §1 핫키 매핑 ═══

        [Theory]
        [InlineData("Digit1", "1", false, ArenaHotkeys.Weight, 1_000)]
        [InlineData("Numpad2", "2", false, ArenaHotkeys.Weight, 2_500)]
        [InlineData("Digit3", "#", false, ArenaHotkeys.Weight, 5_000)]
        [InlineData("Digit4", "4", true, ArenaHotkeys.Weight, 10_000)]          // 비중 = 반복 허용
        [InlineData("KeyB", "ㅠ", false, ArenaHotkeys.Buy, 0)]                   // 한글 IME — code 판정
        [InlineData("KeyS", "ㄴ", false, ArenaHotkeys.Sell, 0)]
        [InlineData("KeyC", "ㅊ", false, ArenaHotkeys.CancelAll, 0)]
        [InlineData("Space", " ", false, ArenaHotkeys.Flatten, 0)]
        [InlineData("KeyB", "b", true, ArenaHotkeys.None, 0)]                    // 자동 반복 발주 = 무시
        [InlineData("Space", " ", true, ArenaHotkeys.None, 0)]
        [InlineData("", "B", false, ArenaHotkeys.Buy, 0)]                        // code 미제공 = key 폴백
        [InlineData(null, "c", false, ArenaHotkeys.CancelAll, 0)]
        [InlineData("KeyX", "x", false, ArenaHotkeys.None, 0)]
        [InlineData("Enter", "Enter", false, ArenaHotkeys.None, 0)]
        public void Classify_Maps_By_Code_With_Key_Fallback_And_Ignores_Repeat(string? code, string key, bool repeat, int action, int weightBp)
        {
            Assert.Equal(action, ArenaHotkeys.Classify(code, key, repeat, out int w));
            Assert.Equal(weightBp, w);
        }

        [Fact]
        public void Qty_Formula_Floors_And_Guards()
        {
            Assert.Equal(14, ArenaHotkeys.QtyFor(10_000_000, 1_000, 70_000));    // 1,000,000 ÷ 70,000
            Assert.Equal(35, ArenaHotkeys.QtyFor(10_000_000, 2_500, 70_000));
            Assert.Equal(142, ArenaHotkeys.QtyFor(10_000_000, 10_000, 70_000));
            Assert.Equal(0, ArenaHotkeys.QtyFor(10_000_000, 1_000, 0));
            Assert.Equal(1_000_000_000L, ArenaHotkeys.QtyFor(10_000_000_000, 10_000, 10));   // Int128 중간값
        }

        // ═══ §2 디스패치 → 휴먼 큐 ═══

        [Fact]
        public void B_Buys_At_Best_Ask_S_Sells_Capped_By_Sellable_C_Cancels()
        {
            var g = Running();
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Step(g);
            var d = Depth(g);
            Assert.Equal(70_000, d.BestAsk);
            Assert.Equal(ArenaReject.InsufficientPosition, ArenaHotkeys.Execute(g, ArenaHotkeys.Sell, in d, 1_000, 10_000_000, out _));   // 무보유 매도
            Assert.Equal(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.Buy, in d, 2_500, 10_000_000, out long q));
            Assert.Equal(35, q);
            Step(g);
            d = Depth(g);
            Assert.Equal(35, d.PositionQty);
            Assert.Equal(10_000_000 - 2_450_000 - 367, Snap(g).Human.CashWon); // 최우선 매도호가 즉시 체결(지정가 = 슬리피지 0)
            Assert.Equal(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.Sell, in d, 10_000, 10_000_000, out q));
            Assert.Equal(35, q);                                                 // 100% 비중이라도 매도 가능 35주 상한
            Step(g);
            Assert.Equal(0, Depth(g).PositionQty);

            Assert.Equal(ArenaReject.None, ArenaHotkeys.ClickLadder(g, Depth(g), isAsk: false, level: 3, 1_000, 10_000_000, out q));
            Step(g);
            Assert.Equal(1, Snap(g).Human.WorkingOrders);
            Assert.Equal(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.CancelAll, Depth(g), 1_000, 10_000_000, out _));
            Step(g);
            Assert.Equal((0, 1L), (Snap(g).Human.WorkingOrders, Snap(g).Human.Cancels));
        }

        [Fact]
        public void Ladder_Click_Places_Limit_At_Level_And_Projects_My_Queue_Badge()
        {
            var g = Running(100_000_000);
            g.OnQuote(Book(70_000, 800, 69_900, 500));
            Step(g);
            Assert.Equal(ArenaReject.None, ArenaHotkeys.ClickLadder(g, Depth(g), isAsk: false, level: 2, 1_000, 100_000_000, out long q));
            Assert.Equal(143, q);                                                // 10,000,000 ÷ 69,700
            Assert.Equal(ArenaReject.None, ArenaHotkeys.ClickLadder(g, Depth(g), isAsk: false, level: 2, 1_000, 100_000_000, out _));
            Step(g);
            var d = Depth(g);
            Assert.Equal(286, d.MyBidQty[2]);
            Assert.Equal(500, d.MyBidAhead[2]);                                  // 맨 앞 내 주문의 앞 잔량 = 표시 잔량
            Assert.Equal(-1, d.MyBidAhead[0]);
            Assert.Equal(0, d.MyBidQty[0]);
            Assert.Equal(ArenaReject.InsufficientPosition, ArenaHotkeys.ClickLadder(g, in d, isAsk: true, level: 0, 1_000, 100_000_000, out _));
            Assert.Equal(ArenaReject.NoQuote, ArenaHotkeys.ClickLadder(g, in d, isAsk: false, level: 10, 1_000, 100_000_000, out _));
            g.OnQuote(Book(72_500, 800, 72_400, 500));                           // 호가 급등 = 내 가격(69,700) 래더(72,400 ~ 71,500) 밖
            Step(g);
            d = Depth(g);
            Assert.Equal(286, d.MyOffLadderQty);
            Assert.Equal(0, d.MyBidQty[2]);
            g.SetFocus(660);
            Step(g);
            Assert.False(Depth(g).HasBook);                                      // 포커스 종목 호가 없음
            g.SetFocus(0);
            Step(g);
            Assert.False(g.TryReadDepth(out _));
        }

        [Fact]
        public void Space_Flatten_After_Latency_Cancels_Everything_Including_Pending_And_Sells_All()
        {
            var g = Running(100_000_000, latency: 40 * Ms);
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            g.OnQuote(Book(180_000, 1_000, 179_900, 1_000, symbol: 660));
            g.SubmitHuman(Sym, 1, 30);
            g.SubmitHuman(660, 1, 10);
            Step(g); Step(g, 40 * Ms);
            Assert.Equal(2, Snap(g).Human.OpenPositions);
            g.SubmitHumanLimit(Sym, -1, 10, 71_000);                             // 매도 대기(수량 예약)
            g.SubmitHumanLimit(Sym, 1, 5, 69_000);                               // 매수 대기(현금 예약)
            Step(g); Step(g, 40 * Ms);
            Assert.Equal(2, Snap(g).Human.WorkingOrders);
            Assert.Equal(ArenaReject.None, ArenaHotkeys.Execute(g, ArenaHotkeys.Flatten, Depth(g), 1_000, 100_000_000, out _));
            g.SubmitHuman(Sym, 1, 100);                                          // 청산 뒤 접수 = 청산 판단 시점에 아직 지연 대기 → 함께 취소
            Step(g);
            Assert.Equal(2, Snap(g).Human.OpenPositions);                         // 청산도 지연 경유
            Step(g, 40 * Ms);
            var s = Snap(g);
            Assert.Equal(0, s.Human.OpenPositions);
            Assert.Equal((0, 0), (s.Human.PendingOrders, s.Human.WorkingOrders));
            Assert.Equal(0, s.Human.ReservedCashWon);
            Assert.Equal(3, s.Human.Cancels);                                    // 매도 대기 · 매수 대기 · 지연 중 매수
            Assert.Equal(s.Human.CashWon, s.Human.EquityWon);
        }

        // ═══ §3 MAE/MFE ═══

        private static DualBookSimulator Sim(long capital)
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(capital);
            return sim;
        }

        [Fact]
        public void Mae_Mfe_Hold_And_Capture_Are_Exact_For_Round_Trip()
        {
            var sim = Sim(10_000_000);
            long t = 1_000 * Sec;
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            sim.Submit(ArenaSide.Human, Sym, 1, 10, ArenaOrderKind.Market, 0, t);
            sim.Process(t);                                                      // 원가 700,105
            sim.OnQuote(Book(69_100, 1_000, 69_000, 1_000)); sim.Process(t + Sec); sim.MarkToMarket(ArenaSide.Human, out _);   // 690,000 − 700,105 = −10,105
            sim.OnQuote(Book(71_600, 1_000, 71_500, 1_000)); sim.Process(t + 2 * Sec); sim.MarkToMarket(ArenaSide.Human, out _); // 715,000 − 700,105 = 14,895
            sim.OnQuote(Book(71_100, 1_000, 71_000, 1_000)); sim.Process(t + 3 * Sec);
            sim.Submit(ArenaSide.Human, Sym, -1, 10, ArenaOrderKind.Market, 0, t + 3 * Sec);
            sim.Process(t + 5 * Sec);                                            // 실현 710,000 − 106 − 1,278 − 700,105 = 8,511
            var b = sim.Book(ArenaSide.Human).Analytics.Snapshot(sim.Book(ArenaSide.Human).SlippageWon);
            Assert.Equal((1, 1, 0, 0), (b.Trips, b.Wins, b.Losses, b.OpenTrips));
            Assert.Equal(-10_105, b.MaeWorstWon);
            Assert.Equal(14_895, b.MfeBestWon);
            Assert.Equal(8_511, b.RealizedWon);
            Assert.Equal(5_000, b.AvgHoldMs);                                    // 진입 t → 청산 t + 5s(시장 시각)
            Assert.Equal(8_511L * 10_000 / 14_895, b.CaptureBp);
            Assert.Equal(0, b.SlippageDragWon);                                  // 1호가 내 체결 = 슬리피지 0
        }

        [Fact]
        public void Partial_Sells_Accumulate_In_Trip_And_Slippage_Drag_Counts_Market_Walks()
        {
            var sim = Sim(100_000_000);
            long t = 1_000 * Sec;
            sim.OnQuote(Book(10_000, 10, 9_990, 10, tick: 10));
            sim.Submit(ArenaSide.Human, Sym, 1, 30, ArenaOrderKind.Market, 0, t);   // 10 × 10,000 + 10 × 10,010 + 10 × 10,020 → 슬리피지 300
            sim.Process(t);
            var h = sim.Book(ArenaSide.Human);
            Assert.Equal(300, h.SlippageWon);
            sim.OnQuote(Book(10_510, 100, 10_500, 100, tick: 10));
            sim.Submit(ArenaSide.Human, Sym, -1, 10, ArenaOrderKind.Market, 0, t + Sec);
            sim.Process(t + Sec);
            sim.MarkToMarket(ArenaSide.Human, out _);
            var mid = h.Analytics.Snapshot(h.SlippageWon);
            Assert.Equal((0, 1), (mid.Trips, mid.OpenTrips));
            Assert.True(mid.OpenMfeWon > 0);
            sim.Submit(ArenaSide.Human, Sym, -1, 20, ArenaOrderKind.Market, 0, t + 2 * Sec);
            sim.Process(t + 2 * Sec);
            var b = h.Analytics.Snapshot(h.SlippageWon);
            Assert.Equal((1, 1, 0), (b.Trips, b.Wins, b.OpenTrips));
            Assert.Equal(h.RealizedWon, b.RealizedWon);                          // 부분 매도 2건 실현 합 = trip 실현
            Assert.Equal(300, b.TripSlippageWon);
            Assert.Equal(300, b.SlippageDragWon);
            Assert.Equal(2_000, b.AvgHoldMs);
        }

        [Fact]
        public void Debrief_Is_Published_Through_Governor_And_Survives_Finish()
        {
            var g = Running();
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            g.SubmitHuman(Sym, 1, 10);
            Step(g);
            g.OnQuote(Book(68_100, 1_000, 68_000, 1_000));
            Step(g, Sec);
            g.SubmitHuman(Sym, -1, 10);
            Step(g);
            g.Stop();
            Step(g);
            Assert.Equal(ArenaState.Finished, Snap(g).State);
            Assert.True(g.TryReadDebrief(ArenaSide.Human, out var b));
            Assert.Equal((1, 0, 1, 1), (b.Trips, b.Wins, b.Losses, b.MaxLossStreak));
            Assert.True(b.MaeWorstWon <= b.RealizedWon && b.RealizedWon < 0);
            Assert.Equal(0, b.MfeBestWon);
            Assert.True(g.TryReadDebrief(ArenaSide.Ai, out var ai));
            Assert.Equal(0, ai.Trips);
            g.Start(10_000_000); Step(g);                                        // 새 매치 = 분석 초기화
            Assert.True(g.TryReadDebrief(ArenaSide.Human, out b));
            Assert.Equal(0, b.Trips);
        }

        // ═══ §4 틸트 ═══

        [Fact]
        public void Tilt_Alerts_On_Frequency_And_Size_After_Two_Losses_Only()
        {
            var a = new ArenaTradeAnalytics();
            long t = 1_000 * Sec;
            for (int i = 0; i < 4; i++) a.OnSubmit(t += Sec, 1_000_000);            // 평온 기준선: 1초 · 100만 원
            void Lose(long sym) { a.OnFill(sym, 1, 0, 10, 0, t, 0, 0); a.OnFill(sym, -1, 10, 0, -5_000, t + Ms, 0, 0); }
            Lose(1);
            a.OnSubmit(t += Ms, 9_000_000);                                     // 연패 1회 = 경보 · 기준선 갱신 모두 없음
            Assert.Equal(0, a.TiltAlerts);
            Lose(2);
            Assert.Equal(2, a.LossStreak);
            a.OnSubmit(t += 2 * Sec, 1_000_000);                                 // 정상 간격 · 정상 규모
            Assert.Equal(0, a.TiltAlerts);
            a.OnSubmit(t += 300 * Ms, 1_000_000);                                // 0.3초 < 기준 1초의 ½ = 빈도 급단축
            Assert.Equal(1, a.TiltAlerts);
            Assert.Equal(ArenaTilt.Frequency, a.Snapshot(0).LastTiltReason);
            a.OnSubmit(t += 3 * Sec, 2_500_000);                                 // 규모 2.5배
            Assert.Equal(ArenaTilt.Size, a.Snapshot(0).LastTiltReason);
            a.OnSubmit(t += 100 * Ms, 5_000_000);
            Assert.Equal((3, ArenaTilt.Frequency | ArenaTilt.Size), (a.TiltAlerts, a.Snapshot(0).LastTiltReason));
            a.OnFill(3, 1, 0, 10, 0, t, 0, 0); a.OnFill(3, -1, 10, 0, 7_000, t + Ms, 0, 0);   // 승리 = 연패 해제
            a.OnSubmit(t += 10 * Ms, 9_000_000);
            Assert.Equal(3, a.TiltAlerts);
            var b = a.Snapshot(0);
            Assert.Equal((3, 1, 2, 2), (b.Trips, b.Wins, b.Losses, b.MaxLossStreak));
        }

        [Fact]
        public void Tilt_Needs_Baseline_And_Excludes_Cancel_And_Flatten()
        {
            var a = new ArenaTradeAnalytics();
            long t = 1_000 * Sec;
            a.OnFill(1, 1, 0, 1, 0, t, 0, 0); a.OnFill(1, -1, 1, 0, -1, t, 0, 0);
            a.OnFill(2, 1, 0, 1, 0, t, 0, 0); a.OnFill(2, -1, 1, 0, -1, t, 0, 0);
            a.OnSubmit(t += Ms, 100_000_000);
            a.OnSubmit(t += Ms, 100_000_000);
            Assert.Equal(0, a.TiltAlerts);                                       // 기준선 표본 부족 = 판정 보류
            var sim = Sim(10_000_000);
            sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.CancelAll, 0, t);
            sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.Flatten, 0, t);
            Assert.Equal(0, sim.Book(ArenaSide.Human).Analytics.TiltAlerts);
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
        public void Hotkey_Dispatch_And_Console_Publish_Loop_Is_Zero_Alloc_With_No_Gc()
        {
            var g = Running(10_000_000_000);
            var quote = Book(70_000, 1_000_000, 69_900, 1_000_000);
            g.OnQuote(in quote);
            Step(g);
            string[] codes = { "Digit1", "KeyB", "Digit2", "KeyS", "KeyC", "Digit4", "KeyB", "Space" };
            long sink = 0;
            int weight = 1_000, i = 0;
            void Loop()
            {
                for (int k = 0; k < 2_000; k++, i++)
                {
                    int a = ArenaHotkeys.Classify(codes[i & 7], null, false, out int w);
                    if (a == ArenaHotkeys.Weight) weight = w;
                    else if (g.TryRead(out var s) && g.TryReadDepth(out var d))
                    {
                        sink += ArenaHotkeys.Execute(g, a, in d, weight, s.CapitalWon, out long q) + q;
                        sink += ArenaHotkeys.ClickLadder(g, in d, isAsk: false, level: i % 10, 100, s.CapitalWon, out q);
                    }
                    quote.BidQty[0] = 900_000 + (i & 1023);
                    g.OnQuote(in quote);
                    _now += Ms;
                    g.Step(_now);                                                // 정산 + 래더 투영 + 복기 게시
                    if (g.TryReadDebrief(ArenaSide.Human, out var b)) sink += b.Trips;
                }
            }
            Loop();
            var gc0 = Gcs();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, Gcs());
            var snap = Snap(g);
            Assert.True(snap.Human.Fills >= 1_000 && snap.Human.Cancels > 0, $"fills={snap.Human.Fills} cancels={snap.Human.Cancels}");
            Assert.True(g.TryReadDebrief(ArenaSide.Human, out var deb) && deb.Trips > 100, "trips " + deb.Trips);
            Assert.True(sink != 0);
        }

        [Fact]
        public void Mae_Mfe_Tracking_Through_Fills_And_Marks_Is_Zero_Alloc_With_No_Gc()
        {
            var sim = Sim(10_000_000_000);
            var q = Book(70_000, 1_000_000, 69_900, 1_000_000);
            long t = 1_000 * Sec;
            void Loop()
            {
                for (int i = 0; i < 5_000; i++)
                {
                    t += Ms;
                    q.BidPrice[0] = 69_900 + (i % 41 - 20) * 100;
                    sim.OnQuote(in q);
                    long sym = 1 + (i & 7);
                    var b2 = q; b2.SymbolCode = sym;
                    sim.OnQuote(in b2);
                    sim.Submit(ArenaSide.Human, sym, (i & 8) == 0 ? 1 : -1, 5, ArenaOrderKind.Market, 0, t);
                    sim.Process(t);
                    sim.MarkToMarket(ArenaSide.Human, out _);
                    sim.Book(ArenaSide.Human).Analytics.Snapshot(0);
                }
            }
            Loop();
            var gc0 = Gcs();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, Gcs());
            var deb = sim.Book(ArenaSide.Human).Analytics.Snapshot(0);
            Assert.True(deb.Trips > 100 && deb.MfeBestWon > 0 && deb.MaeWorstWon < 0, $"trips={deb.Trips}");
        }

        // ═══ §6 Fail-Closed ═══

        [Fact]
        public void Hotkey_Dispatcher_Il_References_Only_System_And_Portable_Port()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            var portCalls = new HashSet<string>();
            foreach (var m in typeof(ArenaHotkeys).GetMethods(All))
                foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                {
                    var owner = r as Type ?? r.DeclaringType;
                    if (owner is null) continue;
                    string ns = owner.Namespace ?? "";
                    if (owner == typeof(IArenaPort)) portCalls.Add(r.Name);
                    if (owner.FullName == "<PrivateImplementationDetails>") continue;   // InlineArray 인덱서 컴파일러 도우미
                    if (!(ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal) || ns.StartsWith("MS2E.UI.Portable", StringComparison.Ordinal))
                        || owner.Assembly != typeof(ArenaHotkeys).Assembly && ns.StartsWith("MS2E", StringComparison.Ordinal))
                        bad.Add(m.Name + " → " + owner.FullName + "::" + r.Name);
                }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.Equal(new[] { "CancelHuman", "FlattenHuman", "SubmitHumanLimit" }, portCalls.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(typeof(IArenaPort).GetMembers().Select(x => x.Name), n => n.Contains("Arm", StringComparison.OrdinalIgnoreCase) || n.Contains("Broker", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(typeof(ArenaHotkeys).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("MS2E.StockSuite", StringComparison.Ordinal));
        }

        // ═══ §7 스트레스 ═══

        [Fact]
        public async System.Threading.Tasks.Task Headless_Hotkey_Mash_And_Ladder_Clicks_With_Concurrent_Settlement_Have_No_Fault_Or_Lock()
        {
            var g = Running(1_000_000_000);
            g.OnQuote(Book(70_000, 5_000, 69_900, 5_000));
            Step(g);
            using var replay = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(100), ownThread: false);
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_console_" + Guid.NewGuid().ToString("N"));
            using var rec = new ArenaTapeRecorder(dir, 10);
            using var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IArenaPort>(g);
            ctx.Services.AddSingleton<IArenaReplayPort>(replay);
            ctx.Services.AddSingleton<IArenaTapePort>(rec);

            using var stop = new CancellationTokenSource();
            Exception? pumpFault = null;
            long pumps = 0;
            var pump = new Thread(() =>                                          // 정산 = 별도 스레드(원장 단일 작성자) · UI 는 큐 적재/사본 판독만
            {
                try
                {
                    var rnd = new Random(7);
                    while (!stop.IsCancellationRequested)
                    {
                        g.OnQuote(Book(70_000 + rnd.Next(-3, 4) * 100, 5_000, 69_900 + rnd.Next(-3, 4) * 100, 5_000));
                        g.Step(Interlocked.Add(ref _now, Ms));
                        Interlocked.Increment(ref pumps);
                        Thread.Sleep(1);
                    }
                }
                catch (Exception ex) { pumpFault = ex; }
            }) { IsBackground = true };
            pump.Start();

            var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
            cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("td.px")), TimeSpan.FromSeconds(10));
            string[] codes = { "Digit1", "KeyB", "KeyB", "Digit3", "KeyS", "KeyC", "Digit2", "KeyB", "Space", "KeyS" };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int keys = 0, clicks = 0;
            for (int i = 0; i < 300; i++)
            {
                // [v12.18.14-GM 하네스 교정] 조회 → 발화를 렌더러 디스패처 안에서 원자적으로 — 뷰의 250ms 폴링 타이머 재렌더가 그 사이에 끼면
                // bUnit 이 오래된 핸들러 ID 로 UnknownEventHandlerIdException(하네스 경합 · 제품 결함 아님 — 출하 [3/10] 에서 1회 관측).
                int k = i;
                await cut.InvokeAsync(() => cut.Find(".ar-console").KeyDown(new KeyboardEventArgs { Code = codes[k % codes.Length], Key = "x", Repeat = k % 7 == 0 }));
                keys++;
                bool clicked = await cut.InvokeAsync(() =>
                {
                    var cells = cut.FindAll("td.px");
                    if (cells.Count == 0) return false;
                    cells[k % cells.Count].Click();
                    return true;
                });
                if (clicked) clicks++;
            }
            sw.Stop();
            stop.Cancel();
            Assert.True(pump.Join(5_000), "정산 스레드 교착");
            Assert.Null(pumpFault);
            Step(g); Step(g, 100 * Ms);
            var s = Snap(g);
            _out.WriteLine($"[스트레스] 키 {keys} · 클릭 {clicks} · {sw.ElapsedMilliseconds}ms · 정산 {pumps} · 체결 {s.Human.Fills} · 취소 {s.Human.Cancels} · 거부 {s.Human.Rejects} · 유실 {s.OrdersLost}");
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), "UI 스레드 정체");
            Assert.True(s.Human.Fills > 0 && clicks > 200);
            Assert.True(s.Human.CashWon >= 0 && s.Human.ReservedCashWon >= 0);
            Assert.True(s.Human.CashWon >= s.Human.ReservedCashWon);
            Assert.Contains("ar-console", cut.Markup);
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.DirectoryNotFoundException) { }
        }
    }
}
