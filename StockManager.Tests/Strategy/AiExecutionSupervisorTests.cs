// ============================================================================
// AiExecutionSupervisorTests — [v12.18.47-DEV] L1 호가 · 기준가 컨텍스트 + AI 라우터 + 30대 인터락 → OrderProposal 후보 · δ=0
//
//   §1 종단 간: 호가 갱신 → 신호 → 매수 = BestAsk · 매도 = BestBid 지정가 후보(격자 · Kind 1 · QtyRaw = 주 × 10^8 · OrderId 단조) ·
//      슬리피지 bps · ±30% 클램프(호가가 상한가 위 → 상한가) · 종목 캡 · 티커 갱신 · 실 RouterFeedGate 가 후보를 수용(지정가 · 정수 주)
//   §2 fail-closed: 컨텍스트 미수신 · 무효 갱신(기준가 0 · 호가 ≤0 · 교차/동일 · 시각 역행 · 범위 이탈 · 슬롯 소진) = 기존 컨텍스트 무변경 ·
//      오래됨 · 미래 시각 = 평가 전 거부(라우터 슬롯 · 반전 이력 무변경)
//   §3 인터락: Bit 0 · 27 · 63(샌니티 미봉인) 등 30대 비트 각각 = 즉시 차단(라우터 무접촉) · 해제 다음 틱 복귀 · AI NaN → 비트 27 → 다음 신호부터 차단
//   §4 δ=0 · 적대: 100만 갱신 + 신호 종단 간 힙 0B(Release · 3창 최솟값) · 적대 혼합 스트림(무효 호가 · 미수신 · 인터락 토글 · 독 신호) =
//      승인 후보 전부 격자 · 상·하한가 내 · 캡 이하 · 인터락 서 있는 틱 승인 0
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Engines.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Strategy
{
    [Collection("ConcurrencyStress")]
    public sealed class AiExecutionSupervisorTests
    {
        private const long S = KrxTickAligner.Scale;
        private const long A = 5930, B = 660, C = 35420;
        private static readonly long Base = 70_000 * S;
        private readonly ITestOutputHelper _out;
        public AiExecutionSupervisorTests(ITestOutputHelper o) => _out = o;

        private sealed class Toggle : IArmingBlockSource { public bool On; public bool ArmingBlocked => On; }

        private sealed class Rig
        {
            public AiExecutionSupervisor Sup = null!;
            public MultiSymbolAiSignalRouter Router = null!;
            public ArmingInterlockBitmask Mask = null!;
            public StrategyAnomalyInterlock Anomaly = null!;
            public Toggle Boot = null!;
            public ArmingInterlockLink BootLink = null!;
        }

        private static Rig Make(int slippageBps = 0, long maxAge = 3_000_000, long maxQty = 1_000, long lot = 10, bool seal = true)
        {
            var r = new Rig { Boot = new Toggle(), Anomaly = new StrategyAnomalyInterlock() };
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly, ArmingInterlockSource.BootIntegrity);
            r.Anomaly.BindInterlock(r.Mask, ArmingInterlockSource.StrategyAnomaly);
            r.BootLink = r.Mask.Bind(ArmingInterlockSource.BootIntegrity, r.Boot);
            if (seal) Assert.True(r.Mask.Seal(out _));
            r.Router = new MultiSymbolAiSignalRouter(r.Anomaly, 16, maxQty, lot);
            r.Sup = new AiExecutionSupervisor(r.Router, r.Mask, maxAge, slippageBps, genomeId: 77);
            return r;
        }

        private static AiSignal Sig(long sym, double c, long t) => new(sym, c, 0.5, 0.5, t);

        private static void Quote(Rig r, long sym, long bidWon, long askWon, long t, long baseRaw = 0, long cap = long.MaxValue)
            => Assert.Equal(MarketContextVerdict.Ok, r.Sup.UpdateMarketContext(sym, baseRaw == 0 ? Base : baseRaw, bidWon * S, askWon * S, t, cap));

        // ════════════ §1 종단 간 ════════════

        [Fact]
        public void Buy_Uses_Best_Ask_Sell_Uses_Best_Bid_And_Packs_Limit_Proposal()
        {
            var r = Make();
            Quote(r, A, 69_900, 70_000, 1_000);
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 1_000), 1_000, out var p, out var rv));
            Assert.Equal(AiRouteVerdict.Accepted, rv);
            Assert.Equal((A, OrderSide.Buy, (byte)1), (p.SymbolCode, p.Side, p.KindByte));
            Assert.Equal(70_000 * S, p.PriceRaw);                                                 // BestAsk
            Assert.Equal(900 * S, p.QtyRaw);                                                     // 900주 × 10^8
            Assert.Equal((1L, 77L, 1_000L), (p.OrderId, p.GenomeId, p.TimestampTicks));

            Quote(r, B, 69_900, 70_000, 1_000);
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(B, -0.9, 1_000), 1_000, out p));
            Assert.Equal((OrderSide.Sell, 69_900 * S, 2L), (p.Side, p.PriceRaw, p.OrderId));     // BestBid · OrderId 단조
            Assert.Equal(2, r.Sup.AcceptedCount);
        }

        [Fact]
        public void Slippage_Bps_Moves_Reference_Then_Protective_Grid_Snap()
        {
            var r = Make(slippageBps: 30);
            Quote(r, A, 69_900, 70_000, 0);
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 0), 0, out var p));
            Assert.Equal(70_200 * S, p.PriceRaw);                                                 // 70,210 → 100원 내림
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, -0.9, 1), 1, out p));
            Assert.Equal(69_700 * S, p.PriceRaw);                                                 // 69,690.3 → 100원 올림
            Assert.True(KrxTickLadder.IsOnTick(p.PriceRaw / S));
        }

        [Fact]
        public void Quote_Beyond_Price_Band_Is_Clamped_To_Limit_Grid()
        {
            var r = Make(slippageBps: 1_000);
            Quote(r, A, 95_000, 95_100, 0);                                                      // 상한가 91,000 위 호가(전일 기준가 70,000)
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 0), 0, out var p));
            Assert.Equal(91_000 * S, p.PriceRaw);
            Quote(r, B, 45_000, 45_050, 0);                                                      // 하한가 49,000 아래 호가
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(B, -0.9, 0), 0, out p));
            Assert.Equal(49_000 * S, p.PriceRaw);
            Assert.Equal(2, r.Router.BandClamped);
            Quote(r, C, 102_000, 102_500, 0);                                                    // 반대 방향(매도 bid×0.9 = 91,800 > 상한가) = 거부
            Assert.Equal(AiSupervisorVerdict.RouterRejected, r.Sup.ProcessSignal(Sig(C, -0.9, 0), 0, out p, out var rv));
            Assert.Equal((AiRouteVerdict.PriceBandBreach, 0L), (rv, p.QtyRaw));
        }

        [Fact]
        public void Per_Symbol_Cap_From_Context_And_Ticker_Update()
        {
            var r = Make();
            Assert.Equal(MarketContextVerdict.Ok, r.Sup.UpdateMarketContext("A005930", Base, 69_900 * S, 70_000 * S, 0, maxSymbolQty: 255));
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 0), 0, out var p));
            Assert.Equal(250 * S, p.QtyRaw);                                                     // 900 → 캡 255 의 로트 내림
            Assert.True(r.Sup.TryGetMarketContext(A, out var ctx));
            Assert.Equal((A, Base, 69_900 * S, 70_000 * S, 255L), (ctx.SymbolCode, ctx.BasePriceRaw, ctx.BestBidRaw, ctx.BestAskRaw, ctx.MaxSymbolQty));
            Assert.Equal(MarketContextVerdict.InvalidSymbol, r.Sup.UpdateMarketContext("00593X", Base, 69_900 * S, 70_000 * S, 0));
        }

        [Fact]
        public void Candidate_Is_Admitted_By_Real_RouterFeedGate()
        {
            long now = 1_000_000;
            Func<long> clock = () => Volatile.Read(ref now);
            var price = new ReferencePriceFeedAdapter(capacityPow2: 64, maxAgeMicros: 3_000_000, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(2);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            var router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            var exec = new OrderExecutionFeedAdapter(router, 64, 10_000_000, clock, _ => { });
            var breaker = new FeedDisconnectCircuitBreaker(price, () => true, maxPriceSilenceMicros: 5_000_000);
            var gate = new RouterFeedGate(router, price, exec, breaker);
            price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(A), 70_000, 1);

            var r = Make();
            Quote(r, A, 69_900, 70_000, now);
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, now), now, out var p));
            Assert.True(gate.TryAdmit(in p, out var adm));
            Assert.Equal(((byte)1, 70_000 * S, 900 * S), (adm.KindByte, adm.PriceRaw, adm.QtyRaw));
            Assert.Equal((0L, 0L, 1L), (gate.TickSnapped, gate.MarketConverted, gate.Admitted));   // 이미 격자 지정가 — 게이트 재스냅 · 변환 0
        }

        // ════════════ §2 fail-closed ════════════

        [Fact]
        public void No_Context_Rejects_Before_Evaluation()
        {
            var r = Make();
            Assert.Equal(AiSupervisorVerdict.NoMarketContext, r.Sup.ProcessSignal(Sig(A, 0.9, 0), 0, out var p, out _));
            Assert.Equal(0, p.QtyRaw);
            Assert.Null(r.Router.GovernorFor(A));                                                 // 라우터 슬롯 생성 0
            Assert.Equal(0, r.Router.Routed);
            Assert.Equal(1, r.Sup.NoContextRejects);
        }

        [Theory]
        [InlineData(0L, 69_900L, 70_000L, MarketContextVerdict.InvalidPrice)]                  // 기준가 0 = 미수신 취급
        [InlineData(-1L, 69_900L, 70_000L, MarketContextVerdict.InvalidPrice)]
        [InlineData(70_000L, 0L, 70_000L, MarketContextVerdict.InvalidPrice)]
        [InlineData(70_000L, 69_900L, -1L, MarketContextVerdict.InvalidPrice)]
        [InlineData(70_000L, 70_000L, 70_000L, MarketContextVerdict.Crossed)]
        [InlineData(70_000L, 70_100L, 70_000L, MarketContextVerdict.Crossed)]
        public void Invalid_Update_Is_Rejected_And_Leaves_No_Context(long baseWon, long bidWon, long askWon, MarketContextVerdict expect)
        {
            var r = Make();
            Assert.Equal(expect, r.Sup.UpdateMarketContext(A, baseWon * S, bidWon * S, askWon * S, 0));
            Assert.Equal(0, r.Sup.SymbolsWithContext);
            Assert.Equal(AiSupervisorVerdict.NoMarketContext, r.Sup.ProcessSignal(Sig(A, 0.9, 0), 0, out var p));
            Assert.Equal(0, p.QtyRaw);
        }

        [Fact]
        public void Invalid_Update_Keeps_Previous_Context_And_Clock_Regression_Rejected()
        {
            var r = Make();
            Quote(r, A, 69_900, 70_000, 5_000);
            Assert.Equal(MarketContextVerdict.ClockRegressed, r.Sup.UpdateMarketContext(A, Base, 60_000 * S, 60_100 * S, 4_999));
            Assert.Equal(MarketContextVerdict.Crossed, r.Sup.UpdateMarketContext(A, Base, 60_100 * S, 60_000 * S, 6_000));
            Assert.Equal(MarketContextVerdict.InvalidPrice, r.Sup.UpdateMarketContext(A, 0, 60_000 * S, 60_100 * S, 6_000));
            Assert.True(r.Sup.TryGetMarketContext(A, out var ctx));
            Assert.Equal((69_900 * S, 70_000 * S, 5_000L), (ctx.BestBidRaw, ctx.BestAskRaw, ctx.UpdatedMicros));
            Assert.Equal(3, r.Sup.ContextUpdateRejects);
            Assert.Equal(MarketContextVerdict.InvalidSymbol, r.Sup.UpdateMarketContext(0, Base, 1 * S, 2 * S, 0));
            Assert.Equal(MarketContextVerdict.InvalidSymbol, r.Sup.UpdateMarketContext(1_000_000, Base, 1 * S, 2 * S, 0));
        }

        [Fact]
        public void Slots_Exhausted_After_64_Symbols()
        {
            var r = Make();
            for (int i = 0; i < AiExecutionSupervisor.MaxSlots; i++) Quote(r, 100_000 + i, 69_900, 70_000, 0);
            Assert.Equal(MarketContextVerdict.SlotsExhausted, r.Sup.UpdateMarketContext(999_999, Base, 69_900 * S, 70_000 * S, 0));
            Quote(r, 100_000, 69_800, 69_900, 1);                                                // 기존 종목 갱신은 계속
            Assert.Equal(64, r.Sup.SymbolsWithContext);
        }

        [Fact]
        public void Stale_Or_Future_Context_Rejects_Before_Evaluation()
        {
            var r = Make(maxAge: 1_000);
            Quote(r, A, 69_900, 70_000, 10_000);
            Assert.Equal(AiSupervisorVerdict.StaleMarketContext, r.Sup.ProcessSignal(Sig(A, 0.9, 11_001), 11_001, out var p));
            Assert.Equal(0, p.QtyRaw);
            Assert.Equal(AiSupervisorVerdict.FutureMarketContext, r.Sup.ProcessSignal(Sig(A, 0.9, 9_999), 9_999, out p));
            Assert.Null(r.Router.GovernorFor(A));
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 11_000), 11_000, out p));   // 경계 = 허용
        }

        // ════════════ §3 인터락 ════════════

        [Fact]
        public void Every_Interlock_Bit_Blocks_Before_Router()
        {
            foreach (ArmingInterlockSource bit in Enum.GetValues<ArmingInterlockSource>())
            {
                var r = Make(seal: false);
                var mask = new ArmingInterlockBitmask();
                var src = new Toggle { On = true };
                if (bit != ArmingInterlockSource.StartupSanity) mask.Bind(bit, src);              // 63 = 미봉인 샌니티 그 자체
                else Assert.True(mask.IsRaised(ArmingInterlockSource.StartupSanity));
                var sup = new AiExecutionSupervisor(r.Router, mask);
                Assert.Equal(MarketContextVerdict.Ok, sup.UpdateMarketContext(A, Base, 69_900 * S, 70_000 * S, 0));
                Assert.Equal(AiSupervisorVerdict.InterlockBlocked, sup.ProcessSignal(Sig(A, 0.9, 0), 0, out var p));
                Assert.Equal(0, p.QtyRaw);
                Assert.Equal(0, r.Router.Routed);                                                 // 라우터 무접촉
            }
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);
        }

        [Fact]
        public void Bit0_Raise_Blocks_Next_Tick_And_Clear_Restores()
        {
            var r = Make();
            Quote(r, A, 69_900, 70_000, 0);
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 0), 0, out _));
            r.Boot.On = true; r.BootLink.Resync();
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.BootIntegrity));
            for (int i = 1; i <= 100; i++)
            {
                Assert.Equal(AiSupervisorVerdict.InterlockBlocked, r.Sup.ProcessSignal(Sig(A, 0.9, i), i, out var p));
                Assert.Equal(0, p.QtyRaw);
            }
            r.Boot.On = false; r.BootLink.Resync();
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(A, 0.9, 101), 101, out _));
            Assert.Equal(100, r.Sup.InterlockBlockedRejects);
            Assert.Equal(2, r.Router.Routed);
        }

        [Fact]
        public void Ai_NaN_Trips_Bit27_And_Supervisor_Blocks_All_Symbols_From_Next_Signal()
        {
            var r = Make();
            Quote(r, A, 69_900, 70_000, 0);
            Quote(r, B, 69_900, 70_000, 0);
            Assert.Equal(AiSupervisorVerdict.RouterRejected, r.Sup.ProcessSignal(Sig(A, double.NaN, 0), 0, out var p, out var rv));
            Assert.Equal((AiRouteVerdict.NonFinite, 0L), (rv, p.QtyRaw));                         // 감독자는 NaN 을 삼키지 않고 관문까지 보냄
            Assert.True(r.Mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            long routed = r.Router.Routed;
            Assert.Equal(AiSupervisorVerdict.InterlockBlocked, r.Sup.ProcessSignal(Sig(B, 0.9, 1), 1, out p));
            Assert.Equal(0, p.QtyRaw);
            Assert.Equal(routed, r.Router.Routed);                                                // 비트 27 이후 라우터 도달 0
            Assert.True(r.Router.TryRearm(true, true));
            Assert.Equal(AiSupervisorVerdict.Accepted, r.Sup.ProcessSignal(Sig(B, 0.9, 2), 2, out _));
        }

        [Fact]
        public void Ctor_Rejects_Invalid_Config()
        {
            var r = Make();
            Assert.Throws<ArgumentNullException>(() => new AiExecutionSupervisor(null!, r.Mask));
            Assert.Throws<ArgumentNullException>(() => new AiExecutionSupervisor(r.Router, null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiExecutionSupervisor(r.Router, r.Mask, maxContextAgeMicros: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiExecutionSupervisor(r.Router, r.Mask, slippageBps: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiExecutionSupervisor(r.Router, r.Mask, slippageBps: 1_001));
        }

        // ════════════ §4 δ=0 · 적대 ════════════

        [Fact]
        public void Million_Context_Updates_Plus_Signals_End_To_End_ZeroAlloc()
        {
            var r = Make(maxQty: 10_000, lot: 1);
            var syms = new long[16];
            var bases = new long[16];
            for (int k = 0; k < 16; k++) { syms[k] = 300_000 + k * 19; bases[k] = (1_500 + k * 30_331) * S; }
            long sink = 0, t = 0;
            void Run()
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    int k = i & 15;
                    t += 40;
                    long b = bases[k], wob = (i & 63) * (b / 1_000);                            // 호가 = 기준가 ±(0 ~ 6.3%) 진동
                    r.Sup.UpdateMarketContext(syms[k], b, b - b / 200 - wob, b + b / 200 + wob, t, 500 + k);
                    double c = (k & 1) == 0 ? 0.2 + (i & 1023) / 1280.0 : -(0.2 + (i & 511) / 640.0);
                    if (r.Sup.ProcessSignal(new AiSignal(syms[k], c, 0.5, 0.5, t - 200), t, out var p) == AiSupervisorVerdict.Accepted)
                        sink += p.QtyRaw / S + (p.PriceRaw & 0xFF) + p.OrderId;
                }
            }
            Run();                                                                                // JIT 예열 + 슬롯 배정
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Run();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            _out.WriteLine($"[δ] 16종목 100만 호가 갱신 + AI 신호 → 후보 종단 간 × 3창 최소 = {min}B · 갱신 {r.Sup.ContextUpdates} · 승인 {r.Sup.AcceptedCount} · 캡 {r.Router.QuantityCapped} · sink {sink}");
            Assert.False(r.Mask.Blocked);
            Assert.Equal(4_000_000, r.Sup.ContextUpdates);
            Assert.Equal(4_000_000, r.Sup.AcceptedCount);
            Assert.Equal(0, min);
        }

        [Fact]
        public void Adversarial_Stream_Never_Emits_Invalid_Candidate()
        {
            var rng = new Random(20261021);
            double[] poison = { double.NaN, double.PositiveInfinity, -1.2 };
            long accepted = 0, blocked = 0, noCtx = 0, rejected = 0, latchedRuns = 0;
            for (int run = 0; run < 150; run++)
            {
                var r = Make();
                var basesWon = new long[24];
                for (int k = 0; k < 24; k++) basesWon[k] = rng.Next(1_000, 900_000);
                long t = 0;
                for (int i = 0; i < 400; i++)
                {
                    t += rng.Next(1, 30_000);
                    int k = rng.Next(24);
                    long sym = 1 + k * 1_009, bw = basesWon[k];
                    if (rng.Next(3) != 0)                                                        // 2/3 = 호가 갱신(일부 무효 · 상·하한가 밖)
                    {
                        long mid = (long)(bw * (0.5 + rng.NextDouble())) * S;
                        long spread = rng.Next(-2, 50) * S / 10;
                        long baseRaw = rng.Next(30) == 0 ? 0 : bw * S;
                        r.Sup.UpdateMarketContext(sym, baseRaw, mid - spread, mid + spread, t - rng.Next(0, 2_000), rng.Next(4) == 0 ? rng.Next(0, 900) : long.MaxValue);
                    }
                    if (rng.Next(50) == 0) { r.Boot.On = !r.Boot.On; r.BootLink.Resync(); }
                    bool maskUp = r.Mask.Blocked;
                    double c = rng.Next(60) == 0 ? poison[rng.Next(poison.Length)] : rng.NextDouble() * 2 - 1;
                    var v = r.Sup.ProcessSignal(new AiSignal(sym, c, 0.5, 0.5, t - rng.Next(0, 35_000)), t, out var p, out _);
                    if (v == AiSupervisorVerdict.Accepted)
                    {
                        Assert.False(maskUp);                                                     // 인터락 선 틱 승인 0
                        Assert.True(r.Sup.TryGetMarketContext(sym, out var ctx));
                        long px = p.PriceRaw;
                        Assert.True(KrxTickLadder.IsOnTick(px / S) && px % S == 0);
                        Assert.True(px / S <= ctx.BasePriceRaw / S * 1.3m && px / S >= ctx.BasePriceRaw / S * 0.7m);   // 상·하한가 내
                        Assert.True(p.Side == OrderSide.Buy ? px <= ctx.BestAskRaw : px >= ctx.BestBidRaw);           // 호가보다 불리한 쪽 0
                        Assert.True(p.QtyRaw > 0 && p.QtyRaw % S == 0 && p.QtyRaw / S <= ctx.MaxSymbolQty && p.QtyRaw / S % 10 == 0);
                        Assert.Equal((byte)1, p.KindByte);
                        accepted++;
                    }
                    else
                    {
                        Assert.Equal(0, p.QtyRaw);
                        if (v == AiSupervisorVerdict.InterlockBlocked) blocked++;
                        else if (v == AiSupervisorVerdict.NoMarketContext) noCtx++;
                        else rejected++;
                    }
                    Assert.NotEqual(AiSupervisorVerdict.InvariantBreach, v);
                }
                if (r.Anomaly.Tripped) latchedRuns++;
            }
            _out.WriteLine($"[적대] 150회 × 400틱 · 승인 {accepted}건(전부 격자 · 제한폭 내 · 캡 이하) · 인터락 차단 {blocked} · 미수신 {noCtx} · 기타 거부 {rejected} · 비트 27 래치 {latchedRuns}회");
            Assert.True(accepted > 200 && blocked > 0 && noCtx > 0 && latchedRuns > 0);
        }
    }
}
