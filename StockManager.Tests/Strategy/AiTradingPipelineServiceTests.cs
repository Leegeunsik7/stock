// ============================================================================
// AiTradingPipelineServiceTests — [v12.18.49-DEV] 호가/거래 상태 → AI 신호 → 감독자 → 싱크(RouterFeedGate · 디스패치 링) · VI/정지 차단 · δ=0
//
//   §1 상태 방어: 정적/동적 VI · 정지 · Unknown(상태 미제공) · 미정의 값 = 감독자 평가 전 차단(라우터 도달 0 · 게이트 도달 0) ·
//      상태 피드의 VI 는 호가의 Normal 로 해제 불가(고정) · 상태 피드 Normal 만 해제 · 늦게 온 옛 해제 거부 ·
//      정지 통지는 호가 무효/교차/시각 역행이어도 반영 · 라우터 평가 중 VI 진입 = 후보 폐기 · 코덱 우선순위
//   §2 활성 스위치: 기본 비활성 = 감독자 무접촉 · 이중키 하나라도 없으면 활성 거부 · 비활성화 즉시
//   §3 종단 간: 정상 호가 + 신호 → 실 RouterFeedGate 수용(지정가 · 격자 · 주) · 디스패치 링 싱크 = 링 게시(무장 게이트 하류 · OrderId 재발급) ·
//      링 단일 생산자 경합 = 미인계 · 싱크 예외 = SinkFault · 호스트 결속 소스 핀(기본 비활성 · 링 싱크 · 직결 싱크 미사용)
//   §4 δ=0 · 적대: 100만 호가+상태+신호 → 싱크 인계 ZeroAllocProbe 0B · 적대 상태 스트림 = 비정상 상태 틱 인계 0
// ============================================================================
using System;
using System.IO;
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
    public sealed class AiTradingPipelineServiceTests
    {
        private const long S = KrxTickAligner.Scale;
        private const long A = 5930, B = 660;
        private static readonly long Base = 70_000 * S;
        private readonly ITestOutputHelper _out;
        public AiTradingPipelineServiceTests(ITestOutputHelper o) => _out = o;

        private sealed class CountingSink : IAiProposalSink
        {
            public long Count, LastQtyRaw, LastPriceRaw; public bool Accept = true, Throw;
            public bool TrySubmit(in OrderProposal p)
            {
                if (Throw) throw new InvalidOperationException("sink");
                if (!Accept) return false;
                Count++; LastQtyRaw = p.QtyRaw; LastPriceRaw = p.PriceRaw;
                return true;
            }
        }

        private sealed class Rig
        {
            public AiTradingPipelineService P = null!;
            public MultiSymbolAiSignalRouter Router = null!;
            public ArmingInterlockBitmask Mask = null!;
            public CountingSink Sink = null!;
        }

        private static Rig Make(IAiProposalSink? sink = null, bool activate = true, long maxQty = 1_000, long lot = 10)
        {
            var r = new Rig { Sink = new CountingSink() };
            var anomaly = new StrategyAnomalyInterlock();
            r.Mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            anomaly.BindInterlock(r.Mask, ArmingInterlockSource.StrategyAnomaly);
            Assert.True(r.Mask.Seal(out _));
            r.Router = new MultiSymbolAiSignalRouter(anomaly, 16, maxQty, lot);
            r.P = new AiTradingPipelineService(new AiExecutionSupervisor(r.Router, r.Mask), sink ?? r.Sink);
            if (activate) Assert.True(r.P.TryActivate(true, true));
            return r;
        }

        private static AiSignal Sig(long sym, double c, long t) => new(sym, c, 0.5, 0.5, t);

        private static void Quote(Rig r, long sym, long t, MarketTradingStatus st = MarketTradingStatus.Normal, long bidWon = 69_900, long askWon = 70_000)
            => r.P.OnQuote(sym, Base, bidWon * S, askWon * S, st, t);

        // ════════════ §1 상태 방어 ════════════

        [Theory]
        [InlineData(MarketTradingStatus.StaticVi)]
        [InlineData(MarketTradingStatus.DynamicVi)]
        [InlineData(MarketTradingStatus.Halted)]
        [InlineData(MarketTradingStatus.Unknown)]
        [InlineData((MarketTradingStatus)77)]
        public void Non_Normal_Status_From_Quote_Blocks_Before_Router_And_Sink(MarketTradingStatus st)
        {
            var r = Make();
            Assert.Equal(MarketContextVerdict.Ok, r.P.OnQuote(A, Base, 69_900 * S, 70_000 * S, st, 0));
            for (int i = 0; i < 50; i++)
            {
                Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, i), i, out var p, out var sv));
                Assert.Equal((AiSupervisorVerdict.MarketNotTradable, 0L), (sv, p.QtyRaw));
            }
            Assert.Equal(0, r.Router.Routed);                                                   // 라우터 도달 0
            Assert.Equal(0, r.Sink.Count);                                                      // 게이트 도달 0
            Assert.Equal(50, r.P.Supervisor.NotTradableRejects);
        }

        [Fact]
        public void Status_Feed_Vi_Is_Pinned_Against_Quote_Normal_Until_Status_Feed_Lifts()
        {
            var r = Make();
            Quote(r, A, 1_000);
            Assert.Equal(AiPipelineVerdict.Submitted, r.P.OnSignal(Sig(A, 0.9, 1_000), 1_000));
            Assert.Equal(MarketContextVerdict.StatusOnly, r.P.OnTradingStatus(A, MarketTradingStatus.DynamicVi, 2_000));
            Quote(r, A, 3_000);                                                                 // 상태 미제공이 아니라 'Normal' 을 실어 와도
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, 3_000), 3_000, out _, out var sv));
            Assert.Equal(AiSupervisorVerdict.MarketNotTradable, sv);                            // 고정 VI 유지
            Assert.True(r.P.Supervisor.TryGetMarketContext(A, out var ctx));
            Assert.Equal((MarketTradingStatus.DynamicVi, true), (ctx.Status, ctx.StatusPinned));
            Assert.Equal(MarketContextVerdict.ClockRegressed, r.P.OnTradingStatus(A, MarketTradingStatus.Normal, 1_999));   // 옛 해제 = 거부
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, 3_001), 3_001));
            Assert.Equal(MarketContextVerdict.StatusOnly, r.P.OnTradingStatus(A, MarketTradingStatus.Normal, 3_500));
            Assert.Equal(AiPipelineVerdict.Submitted, r.P.OnSignal(Sig(A, 0.9, 3_600), 3_600));
            Assert.Equal(2, r.Sink.Count);
        }

        [Fact]
        public void Unknown_Quote_Status_Keeps_Previous_And_Halt_Lands_Even_With_Invalid_Or_Old_Quote()
        {
            var r = Make();
            Quote(r, A, 1_000);
            Quote(r, A, 1_100, MarketTradingStatus.Unknown);                                    // 상태 모름 = Normal 유지 · 호가 갱신
            Assert.Equal(AiPipelineVerdict.Submitted, r.P.OnSignal(Sig(A, 0.9, 1_100), 1_100));
            Assert.Equal(MarketContextVerdict.StatusOnly, r.P.OnQuote(A, Base, 0, 0, MarketTradingStatus.Halted, 1_200));   // 정지 중 빈 호가
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, 1_300), 1_300));
            Quote(r, B, 5_000);
            Assert.Equal(MarketContextVerdict.StatusOnly, r.P.OnQuote(B, Base, 70_000 * S, 69_900 * S, MarketTradingStatus.StaticVi, 4_000));   // 교차 + 역행
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(B, 0.9, 5_000), 5_000));
            Assert.Equal(MarketContextVerdict.InvalidPrice, r.P.OnQuote(A, Base, 0, 0, MarketTradingStatus.Normal, 1_400));   // 정상 + 무효 = 거부(정지 유지)
            Assert.True(r.P.Supervisor.TryGetMarketContext(A, out var ctx));
            Assert.Equal(MarketTradingStatus.Halted, ctx.Status);
            Assert.Equal(1, r.Sink.Count);
        }

        [Fact]
        public void Status_Only_For_Unseen_Symbol_Never_Trades_Alone()
        {
            var r = Make();
            Assert.Equal(MarketContextVerdict.StatusOnly, r.P.OnTradingStatus(A, MarketTradingStatus.Normal, 10_000));
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, 10_000), 10_000, out _, out var sv));
            Assert.Equal(AiSupervisorVerdict.NoMarketContext, sv);                              // 상태 통지만 · 호가 없음 = 미수신
            Assert.Equal(0, r.Router.Routed);
        }

        [Fact]
        public void Vi_Entering_During_Router_Evaluation_Discards_Candidate()
        {
            // 라우터 평가 중 상태 변화를 결정론적으로 만들기: 싱크가 아니라 감독자 사후 재판독 경로를 직접 검증 — 상태 전용 통지는 쓰기 CAS 를 쓰므로
            // 다른 스레드에서 VI 를 꽂으며 대량 신호를 흘리고, 인계된 후보는 전부 'VI 통지 이전' 이어야 한다.
            var r = Make(maxQty: 10, lot: 1);
            Quote(r, A, 0);
            long viAt = -1, submittedAfterVi = 0;
            using var go = new ManualResetEventSlim(false);
            var th = new Thread(() => { go.Wait(); Thread.SpinWait(20_000); Volatile.Write(ref viAt, 1); r.P.OnTradingStatus(A, MarketTradingStatus.StaticVi, 1); });
            th.Start();
            go.Set();
            for (int i = 0; i < 200_000; i++)
            {
                bool viKnownBefore = Volatile.Read(ref viAt) == 1 && r.P.Supervisor.TryGetMarketContext(A, out var c0) && c0.Status != MarketTradingStatus.Normal;
                var v = r.P.OnSignal(Sig(A, 0.9, 0), 0);
                if (v == AiPipelineVerdict.Submitted && viKnownBefore) submittedAfterVi++;
            }
            th.Join();
            Assert.Equal(0, submittedAfterVi);
            Assert.True(r.P.Supervisor.TryGetMarketContext(A, out var ctx) && ctx.Status == MarketTradingStatus.StaticVi);
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, 0), 0));
        }

        [Theory]
        [InlineData(false, false, false, MarketTradingStatus.Normal)]
        [InlineData(false, false, true, MarketTradingStatus.DynamicVi)]
        [InlineData(false, true, true, MarketTradingStatus.StaticVi)]
        [InlineData(true, true, true, MarketTradingStatus.Halted)]
        [InlineData(true, false, false, MarketTradingStatus.Halted)]
        public void Codec_Priority_Halt_Over_Static_Over_Dynamic(bool halted, bool sVi, bool dVi, MarketTradingStatus expect)
        {
            Assert.Equal(expect, MarketTradingStatusCodec.FromFlags(halted, sVi, dVi));
            Assert.Equal(expect == MarketTradingStatus.Normal, MarketTradingStatusCodec.IsTradable(expect));
        }

        // ════════════ §2 활성 스위치 ════════════

        [Fact]
        public void Default_Inactive_Blocks_Everything_Before_Supervisor()
        {
            var r = Make(activate: false);
            Assert.False(r.P.IsAutonomousTradingActive);
            Quote(r, A, 0);
            for (int i = 0; i < 100; i++) Assert.Equal(AiPipelineVerdict.Inactive, r.P.OnSignal(Sig(A, 0.9, 0), 0));
            Assert.Equal(0, r.P.Supervisor.Processed);                                          // 감독자 · 라우터 무접촉
            Assert.Equal(0, r.Router.Routed);
            Assert.Equal(0, r.Sink.Count);
            Assert.Equal(1, r.P.Quotes);                                                         // 비활성에도 컨텍스트는 반영
        }

        [Fact]
        public void Activation_Requires_Both_Physical_Keys_And_Deactivate_Is_Immediate()
        {
            var r = Make(activate: false);
            Assert.False(r.P.TryActivate(true, false));
            Assert.False(r.P.TryActivate(false, true));
            Assert.False(r.P.TryActivate(false, false));
            Assert.False(r.P.IsAutonomousTradingActive);
            Assert.True(r.P.TryActivate(true, true));
            Quote(r, A, 0);
            Assert.Equal(AiPipelineVerdict.Submitted, r.P.OnSignal(Sig(A, 0.9, 0), 0));
            r.P.Deactivate();
            Assert.Equal(AiPipelineVerdict.Inactive, r.P.OnSignal(Sig(A, 0.9, 1), 1));
            Assert.Equal(1, r.Sink.Count);
        }

        // ════════════ §3 종단 간 ════════════

        [Fact]
        public void Normal_Quote_And_Signal_Are_Admitted_By_Real_RouterFeedGate()
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
            var sink = new RouterFeedGateAiProposalSink(gate);

            var r = Make(sink);
            Quote(r, A, now);
            Assert.Equal(AiPipelineVerdict.Submitted, r.P.OnSignal(Sig(A, 0.9, now), now, out var p, out _));
            Assert.Equal((1L, 1L), (sink.Admitted, gate.Admitted));
            Assert.Equal(((byte)1, 70_000 * S, 900 * S), (sink.LastAdmission.KindByte, sink.LastAdmission.PriceRaw, sink.LastAdmission.QtyRaw));
            Assert.Equal(p.PriceRaw, sink.LastAdmission.PriceRaw);

            r.P.OnTradingStatus(A, MarketTradingStatus.Halted, now + 1);                        // 정지 → 게이트 도달 0
            Assert.Equal(AiPipelineVerdict.SupervisorRejected, r.P.OnSignal(Sig(A, 0.9, now + 2), now + 2));
            Assert.Equal(1L, gate.Admitted);
        }

        [Fact]
        public void Dispatch_Ring_Sink_Publishes_Downstream_Of_Arming_Gate_With_Fresh_OrderId()
        {
            var ring = new OrderDispatchRing(64);
            var consumer = ring.CreateConsumer();
            var sink = new DispatchRingAiProposalSink(ring);
            var r = Make(sink);
            ring.Publish(9, 1, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), 0);              // 다른 생산자가 먼저 id 1 사용
            Quote(r, A, 0);
            Assert.Equal(AiPipelineVerdict.Submitted, r.P.OnSignal(Sig(A, -0.9, 0), 0, out var p, out _));
            Assert.True(consumer.TryRead(out _));
            Assert.True(consumer.TryRead(out var onRing));
            Assert.Equal(2, onRing.OrderId);                                                    // 링이 재발급(호스트 단일 id 공간)
            Assert.Equal((A, OrderSide.Sell, (byte)1, p.PriceRaw, p.QtyRaw), (onRing.SymbolCode, onRing.Side, onRing.KindByte, onRing.PriceRaw, onRing.QtyRaw));
            Assert.Equal(1, sink.Published);
        }

        [Fact]
        public void Sink_Reject_And_Fault_Are_Fail_Closed()
        {
            var r = Make();
            Quote(r, A, 0);
            r.Sink.Accept = false;
            Assert.Equal(AiPipelineVerdict.SinkRejected, r.P.OnSignal(Sig(A, 0.9, 0), 0, out var p, out _));
            Assert.Equal(0, p.QtyRaw);
            r.Sink.Throw = true;
            Assert.Equal(AiPipelineVerdict.SinkFault, r.P.OnSignal(Sig(A, 0.9, 1), 1, out p, out _));
            Assert.Equal(0, p.QtyRaw);
            Assert.Equal((1L, 1L, 0L), (r.P.SinkRejects, r.P.SinkFaults, r.P.Submitted));
        }

        [Fact]
        public void Host_Wiring_Is_Inactive_Ring_Sink_And_Never_Direct_Gate()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("new StockManager.Engines.Strategy.AiTradingPipelineService(", src);
            Assert.Contains("new StockManager.Adapters.DispatchRingAiProposalSink(dispatchRing)", src);
            Assert.Contains("MarketTradingStatus.Unknown", src);                                // 상태 미제공 피드 = Unknown(정상 단정 0)
            Assert.DoesNotContain("RouterFeedGateAiProposalSink", src);                         // 무장 게이트 우회 직결 = 호스트 금지
            Assert.DoesNotContain("aiPipeline.TryActivate", src);                               // 기본 비활성 · 활성 경로 없음
            Assert.DoesNotContain("aiPipeline.OnSignal", src);                                  // 실 모델 피드 미결선(가짜 배선 0)
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }

        // ════════════ §4 δ=0 · 적대 ════════════

        [Fact]
        public void Million_Quote_Status_Signal_To_Sink_Is_Zero_Alloc()
        {
            var r = Make(maxQty: 10_000, lot: 1);
            var syms = new long[16];
            for (int k = 0; k < 16; k++) syms[k] = 400_000 + k * 23;
            long t = 0;
            void Run()
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    int k = i & 15;
                    t += 40;
                    if ((i & 1023) == 512) r.P.OnTradingStatus(syms[k], MarketTradingStatus.DynamicVi, t);     // 간헐 VI → 해제
                    else if ((i & 1023) == 528) r.P.OnTradingStatus(syms[k], MarketTradingStatus.Normal, t);
                    r.P.OnQuote(syms[k], Base, (69_900 - (i & 7) * 100) * S, (70_000 + (i & 7) * 100) * S, MarketTradingStatus.Normal, t);
                    double c = (k & 1) == 0 ? 0.3 + (i & 511) / 1024.0 : -(0.3 + (i & 255) / 512.0);
                    r.P.OnSignal(new AiSignal(syms[k], c, 0.5, 0.5, t - 100), t);
                }
            }
            Run();
            long min = ZeroAllocProbe.MinAllocated(Run);
            _out.WriteLine($"[δ] 16종목 100만 호가+상태+신호 → 싱크 = {min}B · 인계 {r.P.Submitted} · 상태 차단 {r.P.Supervisor.NotTradableRejects} · 상태 통지 {r.P.StatusEvents}");
            Assert.False(r.Mask.Blocked);
            Assert.True(r.P.Submitted > 1_000_000 && r.P.Supervisor.NotTradableRejects > 0);   // 인계 · 차단 경로 모두 실행
            Assert.Equal(0, min);
        }

        [Fact]
        public void Adversarial_Status_Stream_Never_Submits_On_Non_Normal_Tick()
        {
            var rng = new Random(20261021);
            var statuses = new[] { MarketTradingStatus.Normal, MarketTradingStatus.Normal, MarketTradingStatus.Normal, MarketTradingStatus.Unknown,
                                   MarketTradingStatus.StaticVi, MarketTradingStatus.DynamicVi, MarketTradingStatus.Halted, (MarketTradingStatus)9 };
            long submitted = 0, blocked = 0;
            for (int run = 0; run < 100; run++)
            {
                var r = Make();
                long t = 0;
                for (int i = 0; i < 500; i++)
                {
                    t += rng.Next(1, 20_000);
                    long sym = 1 + rng.Next(8) * 1_013;
                    var st = statuses[rng.Next(statuses.Length)];
                    if (rng.Next(3) == 0) r.P.OnTradingStatus(sym, st, t - rng.Next(0, 3_000));
                    else r.P.OnQuote(sym, Base, (69_000 + rng.Next(0, 2_000)) * S, (71_000 + rng.Next(0, 2_000)) * S, st, t - rng.Next(0, 3_000));
                    if (rng.Next(40) == 0) { if (r.P.IsAutonomousTradingActive) r.P.Deactivate(); else r.P.TryActivate(true, true); }
                    bool active = r.P.IsAutonomousTradingActive;
                    bool tradable = r.P.Supervisor.TryGetMarketContext(sym, out var ctx) && ctx.Status == MarketTradingStatus.Normal;
                    long before = r.Sink.Count;
                    var v = r.P.OnSignal(Sig(sym, rng.NextDouble() * 2 - 1, t - rng.Next(0, 35_000)), t, out var p, out _);
                    if (v == AiPipelineVerdict.Submitted)
                    {
                        Assert.True(active && tradable);                                           // ★비정상 상태 · 비활성 틱 인계 0
                        Assert.True(KrxTickLadder.IsOnTick(p.PriceRaw / S) && p.PriceRaw % S == 0 && p.QtyRaw > 0);
                        Assert.Equal(before + 1, r.Sink.Count);
                        submitted++;
                    }
                    else { Assert.Equal(before, r.Sink.Count); Assert.Equal(0, p.QtyRaw); if (!tradable) blocked++; }
                }
            }
            _out.WriteLine($"[적대] 100회 × 500틱 · 인계 {submitted}건(전부 Normal · 활성 틱) · 비정상 상태 차단 {blocked}건");
            Assert.True(submitted > 100 && blocked > 1_000);
        }
    }
}
