// ============================================================================
// MicrostructureExecutionTests — [v12.18.13-DEV] 미시구조 체결 모델 · 바이너리 테이프 적대적 검증
//   §1 대기열: 앞 잔량 미소진 = 체결 보류 · 같은 가격 체결 거래량이 앞 잔량을 먼저 소진 · 매수 주도 체결 무시 · 관통 체결 = 잔량 전부 · 예약 현금 정확값 ·
//      표시 잔량 축소 = min · 최우선보다 좋은 가격 = 0 · 방향 미상 = 호가 추정 · 내 앞선 주문 누적 · 시장성 지정가 부분 체결 후 대기
//   §2 지연: 지연 전 판단 0 · 판단 = 지연 경과 시점 호가(접수 시점 아님) · 30 ~ 50ms 분포 · 취소도 지연(그 사이 관통 체결 경합) · 리플레이 시장 시각 기준
//   §3 소진 · 충격: 잔량 초과 시장가 = 다음 호가 이월 분할 체결 · 같은 스냅샷 재매수 = 오버레이로 잔량 0 · 조각 현금 부족 = 부분 후 잔량 취소 · 예약 = 이중 집행 0 · AI 중복 접수 0
//   §4 라이브 체결 경로: 누적 거래량 차분 → 대기열 소진 · 누적 감소 = 기준선 재설정
//   §5 테이프: 무손실 왕복 · 헤더 검증 fail-closed · 비정상 종료 시 1,024 단위 count 판독 · 봉인 중 동시 기록 유실 0 · 녹화기 ON/OFF · 리플레이 .tape 로드 → 지정가 체결
//   §6 δ=0 · GC 0: 대기열/체결 핫패스 · 1M 레코드 기록 · 1M 레코드 판독 · 1M 테이프 리플레이(최대 배속)
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class MicrostructureExecutionTests
    {
        private const long Sym = 5930, Ms = 10_000, T0 = 638_000_000_000_000_000;
        private readonly ITestOutputHelper _out;
        private long _now = T0;

        public MicrostructureExecutionTests(ITestOutputHelper o) => _out = o;

        private static OrderBookPayload Book(long ask, long askQty, long bid, long bidQty, long tick = 100, long symbol = Sym, long ts = 0)
        {
            var b = new OrderBookPayload { SymbolCode = symbol, TimestampTicks = ts };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * tick; b.AskQty[l] = askQty;
                b.BidPrice[l] = bid - l * tick; b.BidQty[l] = bidQty;
            }
            return b;
        }

        private static ArenaTrade Trade(long px, long qty, int side, long symbol = Sym) => new() { Symbol = symbol, PriceWon = px, Qty = qty, Side = side };

        private static DualBookSimulator Sim(long capital, long latency = 0)
        {
            var sim = new DualBookSimulator(latency, latency);
            sim.Reset(capital);
            return sim;
        }

        private static ArenaWorkingOrder Only(ArenaBook book)
        {
            Span<ArenaWorkingOrder> buf = stackalloc ArenaWorkingOrder[ArenaBook.MaxOrders];
            int n = book.CopyOrders(buf);
            Assert.Equal(1, n);
            return buf[0];
        }

        private static string TempTape() => Path.Combine(Path.GetTempPath(), "ms2e_tape_" + Guid.NewGuid().ToString("N") + ArenaTapeFormat.Extension);

        private static void Delete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        // ═══ §1 대기열 ═══

        [Fact]
        public void Limit_Waits_Until_Trade_Volume_Exhausts_Queue_Ahead_Then_Trade_Through_Fills_Rest()
        {
            var sim = Sim(100_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 300, ArenaOrderKind.Limit, 69_900, T0));
            sim.Process(T0);
            var o = Only(h);
            Assert.True(o.Active);
            Assert.Equal(1_000, o.QueueAhead);                                   // 같은 가격 표시 잔량 뒤
            Assert.Equal(20_970_000 + 3_145, h.ReservedCashWon);                 // 수량 × 지정가 + 수수료 예약
            Assert.Equal(100_000_000 - 20_973_145, h.AvailableCashWon);

            sim.OnTrade(Trade(69_900, 600, -1));                                 // 앞 잔량만 소진
            Assert.Equal(0, h.Fills);
            Assert.Equal(400, Only(h).QueueAhead);
            sim.OnTrade(Trade(70_000, 5_000, 1));                                // 매수 주도(매도호가) — 무관
            sim.OnTrade(Trade(69_900, 50, 1));                                   // 내 가격이지만 매수 주도 — 대기 매수 소진 아님
            Assert.Equal(400, Only(h).QueueAhead);
            sim.OnTrade(Trade(69_900, 500, -1));                                 // 400 소진 + 100 체결
            Assert.Equal(1, h.Fills);
            Assert.Equal(100, h.Position(Sym));
            Assert.Equal(100_000_000 - 6_991_048, h.CashWon);                     // 6,990,000 + 수수료 1,048
            Assert.Equal(20_973_145 - 6_991_048, h.ReservedCashWon);              // 비례 해제
            Assert.Equal(0, Only(h).QueueAhead);
            sim.OnTrade(Trade(69_800, 1, -1));                                   // 관통 = 잔량 200 전부(내 가격)
            Assert.Equal(300, h.Position(Sym));
            Assert.Equal(100_000_000 - 6_991_048 - 13_982_097, h.CashWon);
            Assert.Equal(0, h.ReservedCashWon);
            Assert.Equal(0, h.CopyOrders(new ArenaWorkingOrder[4]));
            Assert.Equal(0, h.SlippageWon);                                      // 지정가 체결 = 슬리피지 0
        }

        [Fact]
        public void Queue_Shrinks_To_Displayed_Goes_Zero_When_Better_Than_Best_And_Unknown_Side_Is_Inferred()
        {
            var sim = Sim(100_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            sim.Submit(ArenaSide.Human, Sym, 1, 50, ArenaOrderKind.Limit, 69_900, T0);
            sim.Process(T0);
            var b = Book(70_000, 1_000, 69_900, 1_000);
            b.BidQty[0] = 250;                                                   // 표시 잔량 감소(취소 · 체결)
            sim.OnQuote(b);
            sim.Process(T0);
            Assert.Equal(250, Only(h).QueueAhead);
            b.BidQty[0] = 900;                                                   // 다시 늘어도(뒤에 쌓임) 앞 잔량은 늘지 않는다
            sim.OnQuote(b);
            sim.Process(T0);
            Assert.Equal(250, Only(h).QueueAhead);
            sim.OnQuote(Book(70_000, 1_000, 69_800, 1_000));                      // 내 가격 > 최우선 매수 = 앞 0
            sim.Process(T0);
            Assert.Equal(0, Only(h).QueueAhead);
            sim.OnTrade(Trade(69_900, 30, 0));                                   // 스프레드 안 체결(방향 미상) → 대기 매수 소진 대상
            Assert.Equal(30, h.Position(Sym));
            sim.OnQuote(Book(69_900, 1_000, 69_800, 1_000));                      // 반대 호가가 내 가격까지 → 시장성 체결
            sim.Process(T0);
            Assert.Equal(50, h.Position(Sym));
        }

        [Fact]
        public void Own_Earlier_Orders_Count_In_Queue_And_Marketable_Limit_Fills_To_Limit_Then_Rests()
        {
            var sim = Sim(100_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 10, 69_900, 1_000));
            sim.Submit(ArenaSide.Human, Sym, 1, 40, ArenaOrderKind.Limit, 69_900, T0);
            sim.Submit(ArenaSide.Human, Sym, 1, 60, ArenaOrderKind.Limit, 69_900, T0);
            sim.Process(T0);
            var buf = new ArenaWorkingOrder[4];
            Assert.Equal(2, h.CopyOrders(buf));
            Assert.Equal(1_000, buf[0].QueueAhead);
            Assert.Equal(1_040, buf[1].QueueAhead);                              // 표시 1,000 + 내 앞선 40

            sim.Submit(ArenaSide.Human, Sym, 1, 25, ArenaOrderKind.Limit, 70_100, T0);   // 시장성: 70,000 × 10 + 70,100 × 10 → 잔량 5 대기
            sim.Process(T0);
            Assert.Equal(20, h.Position(Sym));
            Assert.Equal(3, h.CopyOrders(buf));
            Assert.Equal(5, buf[2].Qty);
            Assert.Equal(0, buf[2].QueueAhead);                                  // 70,100 > 최우선 매수 = 새 최우선(앞 0)
            sim.OnTrade(Trade(70_100, 3, -1));
            Assert.Equal(23, h.Position(Sym));
        }

        // ═══ §2 지연 ═══

        [Fact]
        public void Latency_Defers_Decision_And_Fills_At_Book_Current_When_Latency_Elapses()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 40 * Ms, latencyMaxTicks: 40 * Ms);
            Assert.True(g.Start(10_000_000));
            g.Step(_now);
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, 1, 10));
            _now += Ms; g.Step(_now);                                            // 접수(지연 버퍼)
            Assert.True(g.TryRead(out var s));
            Assert.Equal((1, 0, 0L), (s.Human.PendingOrders, s.Human.WorkingOrders, s.Human.Fills));
            g.OnQuote(Book(71_000, 1_000, 70_900, 1_000));                       // 지연 중 호가 급변(틱 레이스)
            _now += 20 * Ms; g.Step(_now);
            Assert.True(g.TryRead(out s));
            Assert.Equal(0, s.Human.Fills);                                      // 39ms 경과 = 미판단
            _now += 19 * Ms; g.Step(_now);
            Assert.True(g.TryRead(out s));
            Assert.Equal(0, s.Human.Fills);
            _now += Ms; g.Step(_now);                                            // 40ms = 판단 → 그 시점 호가 71,000
            Assert.True(g.TryRead(out s));
            Assert.Equal(1, s.Human.Fills);
            Assert.Equal(10_000_000 - 710_000 - 106, s.Human.CashWon);
            Assert.Equal(0, s.Human.SlippageWon);                                // 기준 = 판단 시점 최우선(지연 손실은 가격 자체에 반영)
        }

        [Fact]
        public void Latency_Is_Within_30_To_50ms_Deterministic_And_Varies()
        {
            var sim = Sim(100_000_000, latency: 0);
            var sim2 = new DualBookSimulator();                                  // 기본 30 ~ 50ms
            sim2.Reset(100_000_000);
            var due = new long[ArenaBook.MaxOrders];
            for (int i = 0; i < ArenaBook.MaxOrders; i++) Assert.Equal(ArenaReject.None, sim2.Submit(ArenaSide.Human, Sym, 1, 1, ArenaOrderKind.Market, 0, T0));
            var buf = new ArenaWorkingOrder[ArenaBook.MaxOrders];
            Assert.Equal(ArenaBook.MaxOrders, sim2.Book(ArenaSide.Human).CopyOrders(buf));
            for (int i = 0; i < buf.Length; i++) due[i] = buf[i].DueTicks - T0;
            Assert.All(due, d => Assert.InRange(d, 30 * Ms, 50 * Ms));
            Assert.True(due.Distinct().Count() > 10, "지연 분포가 단조");
            Assert.Equal(ArenaReject.OrdersFull, sim2.Submit(ArenaSide.Human, Sym, 1, 1, ArenaOrderKind.Market, 0, T0));
            sim2.Process(T0 + 30 * Ms - 1);
            Assert.Equal(ArenaBook.MaxOrders, sim2.Book(ArenaSide.Human).PendingCount);     // 30ms 전 = 전부 대기
            var again = new DualBookSimulator();
            again.Reset(100_000_000);
            again.Submit(ArenaSide.Human, Sym, 1, 1, ArenaOrderKind.Market, 0, T0);
            again.Book(ArenaSide.Human).CopyOrders(buf.AsSpan(0, 1));
            Assert.Equal(due[0], buf[0].DueTicks - T0);                          // 매치(Reset) 결정론
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 1, ArenaOrderKind.Market, 0, T0));
        }

        [Fact]
        public void Cancel_Also_Waits_Latency_And_Trade_Through_Can_Win_The_Race()
        {
            var sim = Sim(100_000_000, latency: 40 * Ms);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            sim.Submit(ArenaSide.Human, Sym, 1, 100, ArenaOrderKind.Limit, 69_900, T0);
            sim.Process(T0 + 40 * Ms);
            sim.OnTrade(Trade(69_900, 1_030, -1));                               // 앞 1,000 소진 + 30 체결
            sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.CancelAll, 0, T0 + 50 * Ms);   // 취소 판단 = +90ms
            sim.Process(T0 + 60 * Ms);
            sim.OnTrade(Trade(69_900, 20, -1));                                  // 취소 도착 전 추가 체결
            Assert.Equal(50, h.Position(Sym));
            sim.Process(T0 + 90 * Ms);
            Assert.Equal(1, h.Cancels);
            Assert.Equal(0, h.ReservedCashWon);                                  // 잔량 50 예약 전부 해제
            Assert.Equal(0, h.WorkingCount);
            sim.OnTrade(Trade(69_800, 1_000, -1));                               // 취소 후 관통 = 무체결
            Assert.Equal(50, h.Position(Sym));
        }

        [Fact]
        public void Replay_Latency_Uses_Tape_Clock_Fills_At_First_Tick_After_Due()
        {
            var books = new OrderBookPayload[12];
            for (int i = 0; i < books.Length; i++) books[i] = Book(70_000 + i * 100, 1_000, 69_900 + i * 100, 1_000, ts: T0 + i * 10 * Ms);
            long now = 0;
            using var e = new ArenaTickReplayEngine(() => new RecordedTickSource(books), () => now, 10_000_000, ownThread: false,
                                                    latencyMinTicks: 40 * Ms, latencyMaxTicks: 40 * Ms);
            e.SetSpeed(1);
            e.Play(10_000_000);
            e.Pump(now);                                                         // 틱 0
            e.Arena.SubmitHuman(Sym, 1, 1);
            now += 10 * Ms; e.Pump(now);                                         // 틱 1 에서 접수(due = 틱 1 + 40ms = 틱 5)
            for (int k = 0; k < 3; k++) { now += 10 * Ms; e.Pump(now); }         // 틱 2 ~ 4
            e.OnHubPump(); e.Pump(now);
            Assert.True(e.Arena.TryRead(out var s));
            Assert.Equal(0, s.Human.Fills);
            now += 10 * Ms; e.Pump(now);                                         // 틱 5 = 판단
            e.OnHubPump(); e.Pump(now);
            Assert.True(e.Arena.TryRead(out s));
            Assert.Equal(1, s.Human.Fills);
            Assert.Equal(10_000_000 - 70_500 - DualBookSimulator.Fee(70_500), s.Human.CashWon);   // 틱 5 매도 1호가
        }

        // ═══ §3 소진 · 충격 ═══

        [Fact]
        public void Market_Exceeding_Depth_Carries_To_Next_Quote_And_Same_Snapshot_Is_Depleted()
        {
            var sim = Sim(100_000_000);
            var h = sim.Book(ArenaSide.Human);
            var a = sim.Book(ArenaSide.Ai);
            sim.OnQuote(Book(70_000, 10, 69_900, 10));
            sim.Submit(ArenaSide.Human, Sym, 1, 150, ArenaOrderKind.Market, 0, T0);
            sim.Process(T0);
            Assert.Equal(100, h.Position(Sym));                                  // 10호가 × 10주 전부 · 잔량 50 이월
            Assert.Equal(45_000, h.SlippageWon);
            Assert.Equal(1, h.WorkingCount);
            sim.Submit(ArenaSide.Ai, Sym, 1, 10, ArenaOrderKind.Market, 0, T0);    // 다른 진영 = 오버레이 무관(공정성)
            sim.Submit(ArenaSide.Human, Sym, 1, 5, ArenaOrderKind.Market, 0, T0);  // 같은 진영 · 같은 스냅샷 = 잔량 0
            sim.Process(T0);
            Assert.Equal(10, a.Position(Sym));
            Assert.Equal(100, h.Position(Sym));
            Assert.Equal(2, h.WorkingCount);
            sim.Process(T0 + Ms);                                                // 새 호가 없음 = 재순회 0
            Assert.Equal(100, h.Position(Sym));
            sim.OnQuote(Book(70_500, 10, 70_400, 10));
            sim.Process(T0 + 2 * Ms);                                            // 접수 순서대로 분할 체결
            Assert.Equal(155, h.Position(Sym));
            Assert.Equal(45_000 + 35_000 + 5_000, h.SlippageWon);                // 활성화 시 최우선(70,000) 대비 누적 = 충격
            Assert.Equal(3, h.Fills);
            Assert.Equal(0, h.WorkingCount);
        }

        [Fact]
        public void Cash_Shortfall_Mid_Carry_Fills_Affordable_Then_Cancels_Remainder()
        {
            var sim = Sim(1_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(10_000, 5, 9_000, 5, tick: 1_000));
            sim.Submit(ArenaSide.Human, Sym, 1, 60, ArenaOrderKind.Market, 0, T0);   // 추정 600,090 ≤ 100만 → 접수
            sim.Process(T0);
            Assert.Equal(50, h.Position(Sym));
            Assert.Equal(1_000_000 - 725_108, h.CashWon);
            sim.OnQuote(Book(30_000, 5, 29_000, 5, tick: 1_000));
            sim.Process(T0 + Ms);
            Assert.Equal(59, h.Position(Sym));                                   // 30,000 × 5 + 31,000 × 4(남은 현금 한도)
            Assert.Equal(851, h.CashWon);
            Assert.Equal(ArenaReject.CashExhausted, h.LastReject);
            Assert.Equal(1, h.Cancels);
            Assert.Equal(0, h.WorkingCount);
            Assert.True(h.CashWon >= 0);
        }

        [Fact]
        public void Reservations_Prevent_Overcommit_And_Double_Sell()
        {
            var sim = Sim(10_000_000);
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_100, 1_000, 70_000, 1_000));
            sim.Submit(ArenaSide.Human, Sym, 1, 140, ArenaOrderKind.Limit, 70_000, T0);  // 9,801,470 예약
            sim.Process(T0);
            Assert.Equal(198_530, h.AvailableCashWon);
            sim.Submit(ArenaSide.Human, Sym, 1, 3, ArenaOrderKind.Market, 0, T0);
            sim.Process(T0);
            Assert.Equal(ArenaReject.InsufficientCash, h.LastReject);
            sim.OnTrade(Trade(69_900, 1, -1));                                   // 관통 → 140 체결
            Assert.Equal(140, h.Position(Sym));
            sim.Submit(ArenaSide.Human, Sym, -1, 140, ArenaOrderKind.Limit, 70_500, T0);
            sim.Process(T0);
            Assert.Equal(0, h.Sellable(Sym));
            sim.Submit(ArenaSide.Human, Sym, -1, 1, ArenaOrderKind.Market, 0, T0);
            sim.Process(T0);
            Assert.Equal(ArenaReject.InsufficientPosition, h.LastReject);
            sim.OnTrade(Trade(70_500, 2_000, 1));                                // 매도 대기: 앞 1,000(70,500 표시) 소진 + 140 체결
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(1, h.Wins + h.Losses);                                  // 매도 체결 1건(집계)
        }

        [Fact]
        public void Ai_Does_Not_Double_Submit_While_Order_Is_In_Latency()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, aiLotBp: 700, latencyMinTicks: 40 * Ms, latencyMaxTicks: 40 * Ms);
            g.Start(10_000_000);
            g.Step(_now);
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            g.OnAiDecision(DashDecisionKind.Entry, Sym);
            _now += Ms; g.Step(_now);
            g.OnAiDecision(DashDecisionKind.Entry, Sym);                         // 지연 중 재신호 = 무시
            g.OnAiDecision(DashDecisionKind.Exit, Sym);                          // 미체결 중 청산 = 무시
            _now += Ms; g.Step(_now);
            _now += 40 * Ms; g.Step(_now);
            Assert.True(g.TryRead(out var s));
            Assert.Equal(1, s.Ai.Fills);
            Assert.Equal(10, g.Simulator.Book(ArenaSide.Ai).Position(Sym));
            Assert.Equal(3, s.AiSignals);
            Assert.Equal(0, s.Ai.Rejects);
        }

        // ═══ §4 라이브 체결 경로 ═══

        [Fact]
        public void Live_Cumulative_Volume_Delta_Consumes_Queue_And_Decrease_Rebaselines()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            g.Start(100_000_000);
            g.Step(_now);
            g.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            g.SubmitHumanLimit(Sym, 1, 50, 69_900);
            g.OnTrade(Sym, 69_900, 5_000);                                       // 첫 관측 = 기준선
            _now += Ms; g.Step(_now);
            Assert.True(g.TryRead(out var s));
            Assert.Equal(1_000, s.Human.QueueAheadQty);
            g.OnTrade(Sym, 69_900, 5_600);                                       // +600(매수 1호가 체결 = 매도 주도 추정)
            _now += Ms; g.Step(_now);
            Assert.True(g.TryRead(out s));
            Assert.Equal(400, s.Human.QueueAheadQty);
            g.OnTrade(Sym, 69_900, 100);                                         // 누적 감소 = 기준선 재설정(체결 0)
            _now += Ms; g.Step(_now);
            Assert.True(g.TryRead(out s));
            Assert.Equal(400, s.Human.QueueAheadQty);
            g.OnTrade(Sym, 69_900, 550);                                         // +450 → 400 소진 + 50 체결
            _now += Ms; g.Step(_now);
            Assert.True(g.TryRead(out s));
            Assert.Equal(1, s.Human.Fills);
            Assert.Equal(50, g.Simulator.Book(ArenaSide.Human).Position(Sym));
            Assert.Equal(2, g.TradesSeen);
        }

        // ═══ §5 테이프 ═══

        [Fact]
        public void Tape_Roundtrip_Is_Lossless_And_Header_Validation_Fails_Closed()
        {
            string path = TempTape();
            try
            {
                var b = Book(70_000, 123, 69_900, 456, symbol: 660);
                b.AskQty[7] = 9_999; b.BidPrice[9] = 0;
                using (var w = ArenaTapeWriter.Create(path, 10, createdUtcTicks: T0))
                {
                    Assert.True(w.AppendBook(in b, T0 + 1));
                    Assert.True(w.AppendTrade(Trade(69_900, 77, -1, symbol: 660), T0 + 2));
                    Assert.Equal(2, w.Seal());
                    Assert.False(w.AppendBook(in b, T0 + 3));                    // 봉인 후 기록 거부
                }
                Assert.Equal(ArenaTapeFormat.FileBytes(2), new FileInfo(path).Length);   // 실제 레코드 수로 절단
                using (var r = ArenaTapeReader.Open(path))
                {
                    Assert.Equal(2, r.Count);
                    Assert.Equal(T0, r.CreatedUtcTicks);
                    Assert.True(r.Read(0, out var rb));
                    Assert.False(r.TryReadTrade(0, out _));
                    Assert.Equal((660L, T0 + 1), (rb.SymbolCode, rb.TimestampTicks));
                    for (int l = 0; l < 10; l++)
                        Assert.Equal((b.AskPrice[l], b.AskQty[l], b.BidPrice[l], b.BidQty[l]), (rb.AskPrice[l], rb.AskQty[l], rb.BidPrice[l], rb.BidQty[l]));
                    Assert.False(r.Read(1, out _));
                    Assert.True(r.TryReadTrade(1, out var t));
                    Assert.Equal((660L, 69_900L, 77L, -1), (t.Symbol, t.PriceWon, t.Qty, t.Side));
                    Assert.Throws<ArgumentOutOfRangeException>(() => r.TimestampOf(2));
                }
                byte[] raw = File.ReadAllBytes(path);
                raw[0] ^= 0xFF;
                File.WriteAllBytes(path, raw);
                Assert.Throws<InvalidDataException>(() => ArenaTapeReader.Open(path));
                raw[0] ^= 0xFF; raw[16] = 3;                                     // count 3 > 파일 수용 2
                File.WriteAllBytes(path, raw);
                Assert.Throws<InvalidDataException>(() => ArenaTapeReader.Open(path));
                File.WriteAllBytes(path, raw.AsSpan(0, 10).ToArray());
                Assert.Throws<InvalidDataException>(() => ArenaTapeReader.Open(path));
                Assert.Throws<IOException>(() => ArenaTapeWriter.Create(path, 10)); // 기존 파일 덮어쓰기 금지
            }
            finally { Delete(path); }
        }

        [Fact]
        public void Unsealed_Tape_Is_Readable_Up_To_Last_Count_Flush_And_Seal_During_Writes_Loses_Nothing()
        {
            string path = TempTape();
            try
            {
                var b = Book(70_000, 1, 69_900, 1);
                var w = ArenaTapeWriter.Create(path, 1_000_000);
                for (int i = 0; i < 2_500; i++) w.AppendBook(in b, T0 + i);
                using (var r = ArenaTapeReader.Open(path)) Assert.Equal(2_048, r.Count);   // 비정상 종료 시점 = 1,024 단위 count
                long ok = 2_500;
                bool started = false;
                var writer = new Thread(() => { Volatile.Write(ref started, true); while (w.AppendBook(in b, T0)) Interlocked.Increment(ref ok); }) { IsBackground = true };
                writer.Start();
                while (!Volatile.Read(ref started)) Thread.Yield();
                Thread.Sleep(20);
                long sealedCount = w.Seal();                                     // 진행 중 기록 완료 대기 후 확정
                Assert.True(writer.Join(5_000));
                Assert.Equal(Interlocked.Read(ref ok), sealedCount);
                using var r2 = ArenaTapeReader.Open(path);
                Assert.Equal(sealedCount, r2.Count);
                for (int i = 0; i < r2.Count; i++) Assert.Equal(ArenaTapeFormat.KindBook, r2.KindOf(i));   // 부분 기록 슬롯 0
                _out.WriteLine($"[봉인 경합] 기록 {sealedCount:N0}건 · 유실 0");
            }
            finally { Delete(path); }
        }

        [Fact]
        public void Recorder_On_Off_Records_Live_Books_And_Trade_Deltas_And_Lists_Tapes()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ms2e_tapes_" + Guid.NewGuid().ToString("N"));
            long clock = T0;
            try
            {
                using var rec = new ArenaTapeRecorder(dir, 1_000, () => clock);
                var b = Book(70_000, 10, 69_900, 10);
                rec.OnBook(in b);                                                // OFF = 기록 0
                rec.OnTick(Sym, 69_900, 1_000);                                  // OFF 중에도 기준선
                Assert.False(rec.Recording);
                Assert.True(rec.StartRecording());
                Assert.False(rec.StartRecording());
                clock += Ms; rec.OnBook(in b);
                clock += Ms; rec.OnTick(Sym, 70_000, 1_250);                      // +250
                clock += Ms; rec.OnTick(Sym, 70_000, 1_250);                      // +0 = 기록 0
                Assert.Equal(2, rec.RecordedCount);
                Assert.Empty(rec.ListTapes());                                   // 녹화 중 파일 제외
                rec.StopRecording();
                Assert.False(rec.Recording);
                Assert.Equal(2, rec.RecordedCount);
                var list = rec.ListTapes();
                Assert.Single(list);
                Assert.Equal(2, list[0].Records);
                using var r = ArenaTapeReader.Open(list[0].Path);
                Assert.True(r.Read(0, out _));
                Assert.True(r.TryReadTrade(1, out var t));
                Assert.Equal((70_000L, 250L, T0 + 2 * Ms), (t.PriceWon, t.Qty, r.TimestampOf(1)));
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { } }
        }

        [Fact]
        public void Replay_Loads_Recorded_Tape_And_Limit_Order_Fills_From_Recorded_Trades()
        {
            string path = TempTape();
            try
            {
                using (var w = ArenaTapeWriter.Create(path, 100))
                {
                    for (int i = 0; i < 20; i++)
                    {
                        var b = Book(70_000, 500, 69_900, 300, ts: 0);
                        w.AppendBook(in b, T0 + i * 20 * Ms);
                        if (i >= 5) w.AppendTrade(Trade(69_900, 100, -1), T0 + i * 20 * Ms + Ms);   // 매도 주도 100주씩
                    }
                }
                long now = 0;
                using var e = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(1_000), () => now, 10_000_000, ownThread: false,
                                                        latencyMinTicks: 40 * Ms, latencyMaxTicks: 40 * Ms);
                Assert.False(e.LoadTape(path + ".missing"));
                Assert.True(e.LoadTape(path));
                e.Pump(now);
                Assert.Equal(Path.GetFileName(path), e.SourceLabel);
                Assert.Equal(35, e.Status.Total);
                e.SetSpeed(ArenaReplaySpeed.Max);
                e.Pump(now);
                e.Play(10_000_000);
                e.Pump(now);                                                     // 최대 배속 = 전 테이프(첫 회차에 접수가 없으므로 체결 0)
                Assert.Equal(ArenaReplayState.Finished, e.State);
                Assert.True(e.Arena.TryRead(out var s));
                Assert.Equal(0, s.Human.Fills);
                Assert.Equal(15, e.Governor.TradesSeen);

                e.Reset(); e.Pump(now);
                e.SetSpeed(1); e.Pump(now);
                e.Play(10_000_000); e.Pump(now);                                 // 레코드 0(호가)
                Assert.Equal(ArenaReject.None, e.Arena.SubmitHumanLimit(Sym, 1, 50, 69_900));
                for (int k = 0; k < 40; k++) { now += 10 * Ms; e.Pump(now); }    // 1x · 400ms
                e.OnHubPump(); e.Pump(now);
                Assert.True(e.Arena.TryRead(out s));
                // 접수 = 레코드 1(20ms) · 활성 = 60ms 이후 첫 레코드 → 앞 300 · 체결 틱 100주씩: 3건 소진 후 4번째에서 50 체결
                Assert.Equal(1, s.Human.Fills);
                Assert.Equal(50, e.Governor.Simulator.Book(ArenaSide.Human).Position(Sym));

                Assert.True(e.LoadTape(null));
                e.Pump(now);
                Assert.Equal(ArenaTickReplayEngine.SyntheticLabel, e.SourceLabel);
                Assert.Equal(1_000, e.Status.Total);
            }
            finally { Delete(path); }
        }

        // ═══ §6 δ=0 · GC 0 ═══

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

        private static (int, int, int) GcCounts() => (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        [Fact]
        public void Queue_And_Fill_HotPath_Is_Zero_Alloc_With_No_Gc()
        {
            var sim = Sim(10_000_000_000, latency: 30 * Ms);
            var h = sim.Book(ArenaSide.Human);
            var quote = Book(70_000, 1_000, 69_900, 1_000);
            long t = T0, fills0 = 0;
            void Loop()
            {
                for (int i = 0; i < 2_000; i++)
                {
                    t += 10 * Ms;
                    quote.BidQty[0] = 500 + (i & 255);
                    sim.OnQuote(in quote);
                    sim.Submit(ArenaSide.Human, Sym, 1, 3, ArenaOrderKind.Limit, 69_900, t);
                    sim.Submit(ArenaSide.Ai, Sym, 1, 2, ArenaOrderKind.Market, 0, t);
                    if (h.Sellable(Sym) > 0) sim.Submit(ArenaSide.Human, Sym, -1, 1, ArenaOrderKind.Market, 0, t);
                    sim.OnTrade(new ArenaTrade { Symbol = Sym, PriceWon = 69_900, Qty = 400, Side = -1 });
                    if ((i & 15) == 0) sim.Submit(ArenaSide.Human, 0, 0, 0, ArenaOrderKind.CancelAll, 0, t);
                    sim.Process(t);
                    sim.MarkToMarket(ArenaSide.Human, out _);
                }
            }
            Loop();
            fills0 = h.Fills;
            var gc0 = GcCounts();
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, GcCounts());
            Assert.True(h.Fills > fills0 && h.Cancels > 0 && sim.Book(ArenaSide.Ai).Fills > 0, $"fills={h.Fills} cancels={h.Cancels}");
            Assert.True(h.OrderCount() < ArenaBook.MaxOrders);
        }

        [Fact]
        public void OneMillion_Record_Tape_Write_Read_And_Replay_Are_Zero_Alloc_With_No_Gc()
        {
            const int N = 1_000_000, Warm = 1_000;
            string path = TempTape();
            try
            {
                var b = Book(70_000, 500, 69_900, 500);
                var t = Trade(69_900, 100, -1);
                var w = ArenaTapeWriter.Create(path, N, createdUtcTicks: T0);
                for (int i = 0; i < Warm; i++) { if ((i & 3) == 0) w.AppendTrade(in t, T0 + i * Ms); else w.AppendBook(in b, T0 + i * Ms); }
                GC.Collect(); GC.WaitForPendingFinalizers();
                var gc0 = GcCounts();
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                long sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = Warm; i < N; i++)
                {
                    if ((i & 3) == 0) { t.Qty = 1 + (i & 127); w.AppendTrade(in t, T0 + i * Ms); }
                    else { b.BidQty[0] = i & 4095; w.AppendBook(in b, T0 + i * Ms); }
                }
                long writeAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
                double writeSec = (System.Diagnostics.Stopwatch.GetTimestamp() - sw0) / (double)System.Diagnostics.Stopwatch.Frequency;
                Assert.Equal(gc0, GcCounts());
                Assert.Equal(0, writeAlloc);
                Assert.Equal(N, w.Seal());
                Assert.Equal(0, w.Dropped);

                long sum = 0;
                using (var r = ArenaTapeReader.Open(path))
                {
                    Assert.Equal(N, r.Count);
                    for (int i = 0; i < Warm; i++) { r.Read(i, out var ob); r.TryReadTrade(i, out var ot); sum += ob.BidQty[0] + ot.Qty; }
                    gc0 = GcCounts();
                    a0 = GC.GetAllocatedBytesForCurrentThread();
                    sw0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    long trades = 0, books = 0, bad = 0;
                    for (int i = Warm; i < N; i++)
                    {
                        if (r.Read(i, out var ob)) { books++; sum += ob.BidQty[0]; }
                        else if (r.TryReadTrade(i, out var ot)) { trades++; sum += ot.Qty; if (ot.Qty != 1 + (i & 127)) bad++; }
                        sum += r.TimestampOf(i) & 1;
                    }
                    long readAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
                    double readSec = (System.Diagnostics.Stopwatch.GetTimestamp() - sw0) / (double)System.Diagnostics.Stopwatch.Frequency;
                    Assert.Equal(gc0, GcCounts());
                    Assert.Equal(0, readAlloc);
                    Assert.Equal(N - Warm, books + trades);
                    Assert.Equal(0, bad);
                    Assert.Equal((N - Warm) / 4, trades);
                    _out.WriteLine($"[1M 테이프] 기록 {writeSec:F3}s({N / writeSec:N0}/s) · 판독 {readSec:F3}s({N / readSec:N0}/s) · 할당 0B · GC 0 · 파일 {new FileInfo(path).Length:N0}B");
                }

                long now = 0;
                using var e = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(10), () => now, 10_000_000, ownThread: false);
                Assert.True(e.LoadTape(path));
                e.SetSpeed(ArenaReplaySpeed.Max);
                e.Pump(now);
                e.Play(1_000_000_000);
                e.Pump(now);                                                     // 콜드(AI 인스턴스 · 원장 재설정) + 첫 배치
                e.Arena.SubmitHumanLimit(Sym, 1, 10, 69_900);
                gc0 = GcCounts();
                a0 = GC.GetAllocatedBytesForCurrentThread();
                while (e.State == ArenaReplayState.Playing) { now += 1_000; e.Pump(now); }
                long replayAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
                Assert.Equal(gc0, GcCounts());
                Assert.Equal(0, replayAlloc);
                Assert.Equal(N, e.Processed);
                Assert.True(e.Arena.TryRead(out var s));
                Assert.True(s.Human.Fills >= 1, "리플레이 지정가 체결 0");
                Assert.True(sum > 0);
            }
            finally { Delete(path); }
        }
    }

    internal static class ArenaBookTestExtensions
    {
        public static int OrderCount(this ArenaBook book) => book.PendingCount + book.WorkingCount;
    }
}
