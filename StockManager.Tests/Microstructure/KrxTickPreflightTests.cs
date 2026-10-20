// ============================================================================
// KrxTickPreflightTests — [v12.18.42-DEV] KRX 호가단위 송출 전 정렬(지정가 보호 방향 스냅 · 분할 자식 재검 · 브로커 최종 빗장)
//
//   §1 규칙: 2023 개정 7구간(1 · 5 · 10 · 50 · 100 · 500 · 1,000원) — 경계 12값(1,999 ~ 500,000) 정밀 · raw(10^8) 정렬기 ↔ decimal 사다리 ↔ IsOnTick 3중 일치 ·
//      구간 내부 무효 호가 판별 · 0/음수/소수 원 = 무효(fail-closed)
//   §2 게이트: 비격자 지정가 매수 = 내림 · 매도 = 올림(가격 한도 넘는 쪽 이동 0) · 격자 지정가 무변경 · 분할 자식 비격자 = 라우팅 전 거부(슬롯 0)
//   §3 최종 빗장: 실 RouterFeedGate 송출가 = 항상 IsOnTick · KisLiveBroker/ExecutionBridgeBroker 소스 핀(토큰/프록시 이전 거부)
//   §4 δ=0: 100만 무작위 가격 정렬/판별 힙 0B(3창 최솟값)
// ============================================================================
using System;
using System.Linq;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Microstructure
{
    public sealed class KrxTickPreflightTests
    {
        private const long S = KrxTickAligner.Scale;
        private readonly ITestOutputHelper _out;
        public KrxTickPreflightTests(ITestOutputHelper o) => _out = o;

        // ════════════ §1 규칙 ════════════

        [Theory]
        [InlineData(1_999, 1, 1_999, 1_999)]
        [InlineData(2_000, 5, 2_000, 2_000)]
        [InlineData(4_995, 5, 4_995, 4_995)]
        [InlineData(5_000, 10, 5_000, 5_000)]
        [InlineData(19_990, 10, 19_990, 19_990)]
        [InlineData(20_000, 50, 20_000, 20_000)]
        [InlineData(49_950, 50, 49_950, 49_950)]
        [InlineData(50_000, 100, 50_000, 50_000)]
        [InlineData(199_900, 100, 199_900, 199_900)]
        [InlineData(200_000, 500, 200_000, 200_000)]
        [InlineData(499_500, 500, 499_500, 499_500)]
        [InlineData(500_000, 1_000, 500_000, 500_000)]
        [InlineData(2_003, 5, 2_000, 2_005)]
        [InlineData(4_999, 5, 4_995, 5_000)]                                                    // 올림이 다음 구간 경계로 = 그 구간에서도 유효
        [InlineData(19_995, 10, 19_990, 20_000)]
        [InlineData(20_020, 50, 20_000, 20_050)]
        [InlineData(49_990, 50, 49_950, 50_000)]
        [InlineData(70_050, 100, 70_000, 70_100)]
        [InlineData(199_950, 100, 199_900, 200_000)]
        [InlineData(200_100, 500, 200_000, 200_500)]
        [InlineData(499_999, 500, 499_500, 500_000)]
        [InlineData(999_999, 1_000, 999_000, 1_000_000)]
        public void Boundaries_And_Snaps_Match_Krx_2023_Table(long won, long tick, long down, long up)
        {
            Assert.Equal(tick * S, KrxTickAligner.TickFor(won * S));
            Assert.Equal((decimal)tick, KrxTickLadder.TickWonFor(won));
            Assert.Equal(down * S, KrxTickAligner.AlignDown(won * S));
            Assert.Equal(up * S, KrxTickAligner.AlignUp(won * S));
            bool valid = won % tick == 0;
            Assert.Equal(valid, KrxTickAligner.IsAligned(won * S));
            Assert.Equal(valid, KrxTickLadder.IsOnTick(won));
            Assert.True(KrxTickLadder.IsOnTick(down) && KrxTickLadder.IsOnTick(up));          // 정렬 결과 = 언제나 유효 호가
        }

        [Fact]
        public void Interior_Invalid_Prices_Are_Detected_In_Every_Band()
        {
            foreach (var (won, ok) in new[] { (1_234L, true), (2_001L, false), (3_335L, true), (5_005L, false), (12_340L, true), (20_010L, false),
                                              (33_350L, true), (50_050L, false), (123_400L, true), (200_100L, false), (333_500L, true), (500_500L, false), (1_234_000L, true) })
            {
                Assert.Equal(ok, KrxTickLadder.IsOnTick(won));
                Assert.Equal(ok, KrxTickAligner.IsAligned(won * S));
            }
        }

        [Fact]
        public void Zero_Negative_And_Fractional_Prices_Fail_Closed()
        {
            foreach (long raw in new[] { 0L, -1L, -70_000 * S, long.MinValue })
            {
                Assert.Equal(0, KrxTickAligner.TickFor(raw));
                Assert.Equal(0, KrxTickAligner.AlignDown(raw));
                Assert.Equal(0, KrxTickAligner.AlignUp(raw));
                Assert.False(KrxTickAligner.IsAligned(raw));
            }
            Assert.False(KrxTickLadder.IsOnTick(0m));
            Assert.False(KrxTickLadder.IsOnTick(-5m));
            Assert.False(KrxTickLadder.IsOnTick(70_000.5m));                                    // 소수 원
            Assert.Throws<ArgumentOutOfRangeException>(() => KrxTickLadder.TickWonFor(-1m));
            Assert.Equal(0, KrxTickAligner.AlignUp(long.MaxValue - 1));                         // 포화 = 무효(0)
        }

        // ════════════ §2 · §3 게이트 ════════════

        private sealed class Rig
        {
            public long Now = 1_000_000;
            public ReferencePriceFeedAdapter Price = null!;
            public ResilientOrderRouter Router = null!;
            public RouterFeedGate Gate = null!;
        }

        private const long Code = 5930;

        private static Rig Make()
        {
            var r = new Rig();
            Func<long> clock = () => System.Threading.Volatile.Read(ref r.Now);
            r.Price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var sentinel = new BrokerFailoverSentinel(board, pendingOrders: pending);
            r.Router = new ResilientOrderRouter(board, sentinel, pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            var exec = new OrderExecutionFeedAdapter(r.Router, 64, 60_000_000, clock, _ => { }, new ExtremeSlippageDisarmInterlock(200));
            var breaker = new FeedDisconnectCircuitBreaker(r.Price, () => true);
            r.Gate = new RouterFeedGate(r.Router, r.Price, exec, breaker, new ProtectiveLimitOrderConverter(30), null, true, null);
            r.Price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(Code), 70_000, 1);
            breaker.Evaluate();
            return r;
        }

        private static OrderProposal Limit(long id, decimal price, OrderSide side) => new(id, 0, Code, side, 1, QPrice.Of(price), QQty.Of(1m), 0);

        [Fact]
        public void Misaligned_Limit_Is_Snapped_In_The_Protective_Direction_Before_Routing()
        {
            var r = Make();
            var buy = Limit(1, 70_050m, OrderSide.Buy);                                        // 100원 단위 구간 · 비격자
            Assert.True(r.Gate.TryAdmit(in buy, out var a));
            Assert.Equal(70_000 * S, a.PriceRaw);                                               // ★매수 = 내림(더 비싸게 사지 않음)
            var sell = Limit(2, 70_050m, OrderSide.Sell);
            Assert.True(r.Gate.TryAdmit(in sell, out var b));
            Assert.Equal(70_100 * S, b.PriceRaw);                                               // ★매도 = 올림(더 싸게 팔지 않음)
            var ok = Limit(3, 70_100m, OrderSide.Buy);
            Assert.True(r.Gate.TryAdmit(in ok, out var c));
            Assert.Equal(70_100 * S, c.PriceRaw);                                               // 격자 지정가 = 무변경
            Assert.Equal(2, r.Gate.TickSnapped);
            foreach (var adm in new[] { a, b, c }) Assert.True(KrxTickLadder.IsOnTick(new QPrice(adm.PriceRaw).ToDecimal()));   // 송출가 = 항상 유효 호가
            Assert.Equal(1, a.KindByte);
        }

        [Fact]
        public void Unalignable_Limit_Is_Rejected_Without_Router_Side_Effect()
        {
            var r = Make();
            var bad = Limit(9, 0.5m, OrderSide.Buy);                                            // 1원 미만 매수 → 내림 0 = 정렬 불가
            Assert.False(r.Gate.TryAdmit(in bad, out var adm));
            Assert.Equal(default, adm);
            Assert.Equal(OrderRouteVerdict.InvalidPrice, r.Gate.LastVerdict);
            Assert.Equal(1, r.Gate.TickRejects);
            Assert.Equal(0, r.Router.TrackedOrders);
        }

        [Fact]
        public void Gate_And_Broker_Sources_Pin_The_Preflight()
        {
            var d = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !System.IO.File.Exists(System.IO.Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string Read(params string[] p) => System.IO.File.ReadAllText(System.IO.Path.Combine(new[] { d!.FullName }.Concat(p).ToArray()));
            string gate = Read("StockManager", "Adapters", "RouterFeedGate.cs");
            Assert.Contains("KrxTickAligner.IsAligned(children[i].LimitRaw)", gate);           // 분할 자식 재검(라우팅 전)
            string kis = Read("src", "02_Infrastructure", "Adapters", "Brokers", "LiveBrokers.cs");
            int guard = kis.IndexOf("KrxTickLadder.IsOnTick(limitPrice)", StringComparison.Ordinal);
            Assert.True(guard > 0 && guard < kis.IndexOf("var token = EnsureToken();", guard, StringComparison.Ordinal));   // 토큰/HTTP 이전
            string bridge = Read("src", "02_Infrastructure", "Adapters", "Brokers", "ExecutionBridgeBroker.cs");
            int bg = bridge.IndexOf("KrxTickLadder.IsOnTick(limitPrice)", StringComparison.Ordinal);
            Assert.True(bg > 0 && bg < bridge.IndexOf("long orderId = Interlocked.Increment(ref _nextOrderId);", StringComparison.Ordinal));   // 프록시 송신 이전
        }

        // ════════════ §4 δ=0 ════════════

        [Fact]
        public void OneMillion_Random_Prices_Align_And_Validate_With_Zero_Allocation()
        {
            var prices = new long[4096];
            var rng = new Random(20261021);
            for (int i = 0; i < prices.Length; i++) prices[i] = (long)(rng.NextDouble() * 1_200_000) * S + rng.Next(0, 2) * (S / 2);   // 0 · 정수 · 반원 섞음
            long sink = 0;
            void Run()
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    long p = prices[i & 4095];
                    long dn = KrxTickAligner.AlignDown(p), up = KrxTickAligner.AlignUp(p);
                    if (dn > 0 && !KrxTickAligner.IsAligned(dn)) throw new InvalidOperationException("내림 비격자");
                    if (up > 0 && !KrxTickAligner.IsAligned(up)) throw new InvalidOperationException("올림 비격자");
                    sink += dn + up + KrxTickAligner.TickFor(p);
                    if ((i & 15) == 0 && KrxTickLadder.IsOnTick(p / S)) sink++;
                }
            }
            Run();                                                                              // 예열(JIT)
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Run();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            _out.WriteLine($"[δ] 100만 가격 정렬/판별 × 3창 최소 = {min}B · sink {sink}");
            Assert.Equal(0, min);
        }
    }
}
