// ============================================================================
// QueueLockedBookGuardTests — [v12.18.36-DEV] 아레나 대기열 FIFO 잠김/역전 호가 가드(가격 터치 단독 체결 0)
//
//  §1 잠김(매도 1호가 = 내 매수 지정가) · 역전(매도 1호가 < 내 지정가) 스냅샷 — 내 가격에 실 대기 잔량이 표시된 채 앞 잔량 > 0 이면
//     반대 호가 순회 체결 0(실 거래소는 앞 대기열부터 체결) · 앞 잔량 불변 · 보류 계수 증가 · 매도 대기 대칭
//  §2 앞 잔량 0(맨 앞) 또는 내 가격 실 대기 소멸(표시 0) = 종전대로 시장성 체결 · 접수 직후 활성화(테이커) 순회 = 무변경
//  §3 잠김 보류 중에도 체결 틱 FIFO 소진 → 남는 거래량만 체결 · 취소 감축(⑪) 정상 동작
//  §4 δ=0 — 100만 회 호가 갱신(잠김 보류 · 취소 감축 · 정상 경로 혼합) + 체결 틱 0B(3창 최솟값 · 타 스레드 GC 겹침 배제)
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class QueueLockedBookGuardTests
    {
        private const long Sym = 5930, T0 = 638_000_000_000_000_000;

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

        private static DualBookSimulator Sim()
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(100_000_000);
            return sim;
        }

        private static ArenaTrade Trade(long px, long qty, int side) => new() { Symbol = Sym, PriceWon = px, Qty = qty, Side = side };

        private static void Quote(DualBookSimulator sim, in OrderBookPayload b, ref long now) { sim.OnQuote(in b); now += 10_000; sim.Process(now); }

        // 매수 지정가 69,900 × qty 대기(앞 잔량 = 표시 1,000)
        private static (DualBookSimulator sim, ArenaBook h) WaitingBid(long qty = 10)
        {
            var sim = Sim();
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, qty, ArenaOrderKind.Limit, 69_900, T0));
            sim.Process(T0);
            var h = sim.Book(ArenaSide.Human);
            Assert.Equal(1_000, h.OldestQueueAhead);
            return (sim, h);
        }

        // 보유 100 → 매도 지정가 70,000 × qty 대기(앞 잔량 = 표시 1,000)
        private static (DualBookSimulator sim, ArenaBook h) WaitingAsk(long qty = 10)
        {
            var sim = Sim();
            long now = T0;
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 100, ArenaOrderKind.Market, 0, now));
            sim.Process(now);
            var h = sim.Book(ArenaSide.Human);
            Assert.Equal(100, h.Position(Sym));
            Quote(sim, Book(70_000, 1_000, 69_900, 1_000), ref now);
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, -1, qty, ArenaOrderKind.Limit, 70_000, now));
            sim.Process(now);
            Assert.Equal(1_000, h.OldestQueueAhead);
            return (sim, h);
        }

        // ════════════ §1 잠김 · 역전 = 체결 0 ════════════

        [Fact]
        public void LockedBook_AskTouchesMyBid_WithQueueAheadShown_DoesNotFill()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, Book(69_900, 500, 69_900, 1_000), ref now);                  // 매도 1호가 = 내 가격 · 내 가격 실 대기 1,000 그대로
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(1_000, h.OldestQueueAhead);
            Assert.Equal(1, h.QueueLockedHolds);
        }

        [Fact]
        public void CrossedBook_AskBelowMyBid_WithQueueAheadShown_DoesNotFill()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, Book(69_800, 5_000, 69_900, 1_000), ref now);                // 역전(매도 69,800 < 매수 69,900) · 대량 매도 잔량
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(1_000, h.OldestQueueAhead);
            Assert.Equal(1, h.QueueLockedHolds);
        }

        [Fact]
        public void SellSide_LockedBook_BidTouchesMyAsk_DoesNotFill()
        {
            var (sim, h) = WaitingAsk();
            long now = T0 + 100_000;
            Quote(sim, Book(70_000, 1_000, 70_000, 800), ref now);                  // 매수 1호가 = 내 매도 가격 · 내 가격 실 대기 1,000
            Assert.Equal(100, h.Position(Sym));
            Assert.Equal(1_000, h.OldestQueueAhead);
            Assert.Equal(1, h.QueueLockedHolds);
        }

        // ════════════ §2 정당한 시장성 체결 = 종전 ════════════

        [Fact]
        public void AskTouchesMyBid_AfterRealQueueVanished_FillsMarketable()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, Book(69_900, 500, 69_800, 1_000), ref now);                  // 내 가격 실 매수 대기 소멸(매수 1호가 69,800) → 내가 맨 앞
            Assert.Equal(10, h.Position(Sym));
            Assert.Equal(0, h.QueueLockedHolds);
        }

        [Fact]
        public void LockedBook_WhenQueueAheadExhausted_FillsMarketable()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            sim.OnTrade(Trade(69_900, 1_000, -1));                                  // 앞 1,000 전부 체결 틱 소진 · 남는 거래량 0
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(0, h.OldestQueueAhead);
            Quote(sim, Book(69_900, 500, 69_900, 300), ref now);                    // 잠김이지만 내가 맨 앞 = 반대 호가 체결
            Assert.Equal(10, h.Position(Sym));
            Assert.Equal(0, h.QueueLockedHolds);
        }

        [Fact]
        public void NewMarketableLimit_Activation_Walk_Unchanged()
        {
            var sim = Sim();
            sim.OnQuote(Book(69_900, 500, 69_900, 1_000));                          // 잠김 스냅샷에 새로 들어온 테이커 = 반대 호가 순회(무변경)
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 10, ArenaOrderKind.Limit, 69_900, T0));
            sim.Process(T0);
            var h = sim.Book(ArenaSide.Human);
            Assert.Equal(10, h.Position(Sym));
            Assert.Equal(0, h.QueueLockedHolds);
        }

        // ════════════ §3 보류 중 FIFO 소진 · 취소 감축 ════════════

        [Fact]
        public void LockedHold_ThenTradeTape_ConsumesQueueFirst_ThenFillsRemainder()
        {
            var (sim, h) = WaitingBid(qty: 10);
            long now = T0;
            Quote(sim, Book(69_900, 500, 69_900, 1_000), ref now);
            Assert.Equal(0, h.Position(Sym));
            sim.OnTrade(Trade(69_900, 999, -1));                                    // 앞 1 남음 · 체결 0
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(1, h.OldestQueueAhead);
            Quote(sim, Book(69_900, 500, 69_900, 1), ref now);                      // 여전히 잠김 · 앞 1 = 보류
            Assert.Equal(0, h.Position(Sym));
            sim.OnTrade(Trade(69_900, 5, -1));                                      // 1 소진 + 4 체결
            Assert.Equal(4, h.Position(Sym));
            Assert.Equal(0, h.OldestQueueAhead);
        }

        [Fact]
        public void LockedHold_CancelDepletion_StillShortensQueue()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, Book(70_000, 1_000, 69_900, 2_000), ref now);                // 뒤 1,000 합류
            Quote(sim, Book(69_900, 500, 69_900, 1_000), ref now);                  // 잠김 + 무체결 1,000 감소 = 취소 → 앞 500
            Assert.Equal(0, h.Position(Sym));
            Assert.Equal(500, h.OldestQueueAhead);
            Assert.Equal(500, h.QueueCancelDepletedQty);
            Assert.Equal(1, h.QueueLockedHolds);
        }

        // ════════════ §4 δ=0 ════════════

        [Fact]
        public void OneMillion_Quote_Updates_LockedHold_And_Depletion_ZeroAlloc()
        {
            var (sim, h) = WaitingBid(qty: 1);
            var locked = Book(69_900, 500, 69_900, 1_000);
            var hi = Book(70_000, 1_000, 69_900, 2_000);
            var lo = Book(70_000, 1_000, 69_900, 1_000);
            long now = T0;
            sim.OnQuote(in locked); sim.Process(++now); sim.OnQuote(in hi); sim.Process(++now);   // 예열
            long min = long.MaxValue;
            for (int w = 0; w < 3 && min != 0; w++)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 1_000_000; i++)
                {
                    int k = i % 3;
                    sim.OnQuote(k == 0 ? locked : k == 1 ? hi : lo);                   // 잠김 보류 · 증가 · 감소(취소 감축)
                    sim.Process(++now);
                    if ((i & 255) == 0) sim.OnTrade(Trade(69_900, 1, 1));               // 매수 주도 = 내 대기열 무관 · 경로 혼합
                }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
            }
            Assert.Equal(0, min);
            Assert.Equal(0, h.Position(Sym));                                       // 감축 절사로 앞 1 에 수렴(0 미도달) = 잠김 터치 100만 회에도 체결 0
            Assert.Equal(1, h.OldestQueueAhead);
            Assert.True(h.QueueLockedHolds > 0);
            Assert.True(h.QueueCancelDepletedQty > 0);
        }
    }
}
