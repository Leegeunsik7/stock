// ============================================================================
// QueueDepletionTests — [v12.18.23-DEV] 아레나 대기열 취소 이탈(Cancel Depletion) 비례 감축
//
//  §1 순수 수식 — CancelDepletion(ahead, prevShown, shown, traded): C = max(0, ΔD − V) · 앞 몫 = C × min(ahead, prevShown) ÷ prevShown · 미상/증가/체결만 = 0 · ≤ ahead
//  §2 시뮬레이터 — 체결 없는 대량 취소 = 앞 잔량 비례 단축(종전 min 상한 모델 대비 순번 전진) · 체결 + 취소 혼합(이중 차감 0) · 잔량 증가 = 불변 ·
//     내 가격이 최우선보다 좋아짐 = 0 · 10호가 이탈 → 재진입 = 재기준(미상 구간 감축 0) · 단축 후 체결 틱이 남은 앞 잔량만 소진하고 체결 · 매도 대기 대칭
//  §3 δ=0 — 10만 회 호가 갱신(취소 감축 경로) + 체결 틱 0B
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class QueueDepletionTests
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

        private static OrderBookPayload WithBid0(long bidQty0)
        {
            var b = Book(70_000, 1_000, 69_900, 1_000);
            b.BidQty[0] = bidQty0;
            return b;
        }

        private static DualBookSimulator Sim()
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(100_000_000);
            return sim;
        }

        private static ArenaTrade Trade(long px, long qty, int side) => new() { Symbol = Sym, PriceWon = px, Qty = qty, Side = side };

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

        private static void Quote(DualBookSimulator sim, in OrderBookPayload b, ref long now) { sim.OnQuote(in b); now += 10_000; sim.Process(now); }

        // ════════════ §1 순수 수식 ════════════

        [Theory]
        [InlineData(1_000, 2_000, 1_000, 0, 500)]       // 1,000 취소 · 내 앞 = 절반 → 500
        [InlineData(600, 2_000, 1_000, 400, 180)]       // ΔD 1,000 − 체결 400 = 취소 600 × 600/2000
        [InlineData(1_000, 1_000, 600, 0, 400)]         // 앞 = 전부 → 취소 전량
        [InlineData(1_000, 1_000, 1_000, 0, 0)]         // 무변화
        [InlineData(1_000, 1_000, 1_500, 0, 0)]         // 증가 = 뒤에 붙은 신규
        [InlineData(1_000, 1_000, 700, 300, 0)]         // 감소 = 전부 체결(취소 0)
        [InlineData(1_000, 1_000, 700, 500, 0)]         // 체결 > 감소(새 주문 유입) = 취소 0
        [InlineData(1_000, 0, 500, 0, 0)]               // 직전 미상
        [InlineData(0, 1_000, 0, 0, 0)]                 // 앞 0
        [InlineData(300, 200, 0, 0, 200)]               // 앞(내 다른 가상 주문 포함) > 표시 — 시장 취소는 시장 잔량(200)만 걷어낸다
        public void CancelDepletion_Formula(long ahead, long prevShown, long shown, long traded, long expected)
        {
            long r = DualBookSimulator.CancelDepletion(ahead, prevShown, shown, traded);
            Assert.Equal(expected, r);
            Assert.InRange(r, 0, Math.Max(0, ahead));
        }

        // ════════════ §2 시뮬레이터 ════════════

        [Fact]
        public void MassCancel_NoTrades_AdvancesQueue_Proportionally_BeyondMinCapModel()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, WithBid0(2_000), ref now);                                  // 뒤에 1,000 합류 — 앞 불변
            Assert.Equal(1_000, h.OldestQueueAhead);
            Quote(sim, WithBid0(1_000), ref now);                                  // 1,000 취소(체결 0) — 종전 min 모델 = 1,000 그대로
            Assert.Equal(500, h.OldestQueueAhead);                                 // 균등 분포 기대값: 내 앞 몫 = 1,000 × 1,000/2,000
            Assert.Equal(500, h.QueueCancelDepletedQty);
            Quote(sim, WithBid0(400), ref now);                                    // 600 취소 × 500/1,000 = 300 → 200 · 표시 상한 400 미적용
            Assert.Equal(200, h.OldestQueueAhead);
            Assert.Equal(800, h.QueueCancelDepletedQty);
        }

        [Fact]
        public void Trades_And_Cancels_Mixed_NoDoubleCount()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, WithBid0(2_000), ref now);
            sim.OnTrade(Trade(69_900, 400, -1));                                    // 매도 주도 400 = 앞 소진 1,000 → 600
            Assert.Equal(600, h.OldestQueueAhead);
            Quote(sim, WithBid0(1_000), ref now);                                  // ΔD 1,000 − 체결 400 = 취소 600 × 600/2,000 = 180
            Assert.Equal(420, h.OldestQueueAhead);
            Assert.Equal(180, h.QueueCancelDepletedQty);
            Quote(sim, WithBid0(1_000), ref now);                                  // 무변화 · 체결 계수 리셋 확인
            Assert.Equal(420, h.OldestQueueAhead);
        }

        [Fact]
        public void BuyInitiatedTrades_AtMyBidPrice_DoNotCountAsMyQueueVolume()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            sim.OnTrade(Trade(69_900, 300, 1));                                     // 매수 주도 체결은 매수 대기열을 소진하지 않음
            Assert.Equal(1_000, h.OldestQueueAhead);
            Quote(sim, WithBid0(700), ref now);                                    // 감소 300 전부 취소로 판정 → 앞 = 1,000 × 300/1,000 감축
            Assert.Equal(700, h.OldestQueueAhead);
        }

        [Fact]
        public void Depletion_Then_TradeVolume_Fills_Earlier()
        {
            var (sim, h) = WaitingBid(qty: 10);
            long now = T0;
            Quote(sim, WithBid0(2_000), ref now);
            Quote(sim, WithBid0(1_000), ref now);                                  // 앞 500
            sim.OnTrade(Trade(69_900, 505, -1));                                    // 500 소진 + 5 체결(종전 min 모델이면 앞 1,000 → 체결 0)
            Assert.Equal(5, h.Position(Sym));
            sim.OnTrade(Trade(69_900, 10, -1));
            Assert.Equal(10, h.Position(Sym));
            Assert.Equal(-1, h.OldestQueueAhead);                                  // 전량 체결 = 대기 없음
        }

        [Fact]
        public void MyPriceBecomesBest_QueueZero()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, Book(70_000, 1_000, 69_800, 1_000), ref now);                // 매수 1호가 69,800 < 내 가격 = 내가 최우선 → 앞 0
            Assert.Equal(0, h.OldestQueueAhead);
        }

        [Fact]
        public void PriceLeavesLadder_ThenReturns_Rebaselines_NoPhantomDepletion()
        {
            var (sim, h) = WaitingBid();
            long now = T0;
            Quote(sim, Book(71_200, 1_000, 71_100, 1_000), ref now);                // 내 가격(69,900) 10호가(71,100 ~ 70,200) 밖 = 미상
            Assert.Equal(1_000, h.OldestQueueAhead);
            Quote(sim, WithBid0(500), ref now);                                     // 재진입 — 직전 미상 → 취소 감축 0 · 표시 상한만
            Assert.Equal(500, h.OldestQueueAhead);
            Assert.Equal(0, h.QueueCancelDepletedQty);
        }

        [Fact]
        public void OutOfLadder_Unknown_Then_Visible_NoDepletionFromUnknown()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            long now = T0;
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, 1, 10, ArenaOrderKind.Limit, 68_500, T0));   // 69,900 ~ 69,000 밖 → 10번째 잔량 추정
            sim.Process(T0);
            Assert.Equal(1_000, h.OldestQueueAhead);
            Quote(sim, Book(69_000, 1_000, 68_900, 300), ref now);                  // 68,500 가시화(표시 300) — 직전 미상 → 상한만(취소 감축 0)
            Assert.Equal(300, h.OldestQueueAhead);
            Assert.Equal(0, h.QueueCancelDepletedQty);
        }

        [Fact]
        public void SellSide_Symmetric()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            long now = T0;
            sim.OnQuote(Book(70_000, 1_000, 69_900, 1_000));
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, -1, 10, ArenaOrderKind.Limit, 70_000, T0));   // 무포지션 매도 = 숏 개시 지정가
            sim.Process(T0);
            Assert.Equal(1_000, h.OldestQueueAhead);
            var b = Book(70_000, 1_000, 69_900, 1_000); b.AskQty[0] = 2_000;
            Quote(sim, b, ref now);
            sim.OnTrade(Trade(70_000, 200, 1));                                     // 매수 주도 = 매도 대기열 소진
            Assert.Equal(800, h.OldestQueueAhead);
            b.AskQty[0] = 1_000;
            Quote(sim, b, ref now);                                                 // 취소 800 × 800/2,000 = 320
            Assert.Equal(480, h.OldestQueueAhead);
        }

        // ════════════ §3 δ=0 ════════════

        [Fact]
        public void HundredThousand_QueueDepletion_Updates_ZeroAlloc()
        {
            var (sim, h) = WaitingBid(qty: 1);
            var hi = WithBid0(2_000);
            var lo = WithBid0(1_000);
            long now = T0;
            sim.OnQuote(in hi); sim.Process(++now); sim.OnQuote(in lo); sim.Process(++now);   // 예열
            long min = long.MaxValue;
            for (int w = 0; w < 3 && min != 0; w++)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100_000; i++)
                {
                    sim.OnQuote((i & 1) == 0 ? hi : lo);                               // 홀짝 = 증가/감소(취소 감축 경로)
                    sim.Process(++now);
                    if ((i & 255) == 0) sim.OnTrade(Trade(69_900, 1, 1));               // 매수 주도 = 내 대기열 무관(체결 0) · 경로 혼합
                }
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
            }
            Assert.Equal(0, min);
            Assert.True(h.QueueCancelDepletedQty > 0);
            Assert.InRange(h.OldestQueueAhead, 0, 1);                               // 반복 감축(½ 씩 · 절사 = 보수적) 끝에 맨 앞 근접
        }
    }
}
