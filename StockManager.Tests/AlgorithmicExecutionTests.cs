// ============================================================================
// AlgorithmicExecutionTests — [알고 집행 하드닝 · 추천 A] VWAP/TWAP 분할 · 동적 슬리피지 가드 · 집행 매트릭스 · 임팩트 인터락
//
//  §1 TwapVwapExecutor — VWAP 오버필 회귀(Σ=Total 정확) · 입력 거부 · 취소 시 자식 주문 일괄 취소 · 대기 직후 취소 누출 0 · 미상 예외 동결.
//  §2 ExecutionMatrixTracker(방향 A) — 분할 계획 Σ 정확 · 부분 체결 수렴 · 오버필 격리 · 취소 후 체결 · Int128 명목.
//  §3 ExecutionImpactInterlock(방향 B) — 임팩트 초과 → 잔여 조각 전량 취소 + sticky Disarm · 자격증명 소거 · 물리 이중키 재무장.
//  §4 MicroLiquiditySlicer — 호가 깊이 슬리피지 상한 · 잔량 포화 · 스로틀 중단 잔량 보고.
//  §5 δ=0 — 계획/체결 반영/임팩트 판정/슬리피지 상한 핫패스 힙 할당 0B(Release).
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    /// <summary>자식 주문 ID 발급 + 취소 기록 스파이. throwOnCall(1-based) 번째 Place 에서 미상 예외.</summary>
    file sealed class ChildSpyBroker : IBroker
    {
        private readonly int _throwOnCall;
        private int _calls;
        public ChildSpyBroker(int throwOnCall = 0) => _throwOnCall = throwOnCall;
        public readonly ConcurrentQueue<(string Id, decimal Qty)> Placed = new();
        public readonly ConcurrentQueue<string> Cancelled = new();
        public string Name => "ChildSpy";
        public bool IsLive => false;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            int k = Interlocked.Increment(ref _calls);
            if (k == _throwOnCall) throw new InvalidOperationException("broker transport fault");
            string id = $"CHILD-{k}";
            Placed.Enqueue((id, qty));
            return (true, id);
        }

        public void Cancel(string orderId) => Cancelled.Enqueue(orderId);
        public decimal HeldQty(PaperAsset asset, string symbol) => 0m;
    }

    [Collection("ConcurrencyStress")]
    public sealed class AlgorithmicExecutionTests
    {
        private const long Scale = 100_000_000L;

        private static L2Depth Depth(long[] bidP, long[] bidQ, long[] askP, long[] askQ)
        {
            var d = new L2Depth { SymbolCode = 5930, TimestampTicks = 0 };
            for (int i = 0; i < 5; i++)
            {
                d.BidPriceRaw[i] = i < bidP.Length ? bidP[i] : 0;
                d.BidQtyRaw[i]   = i < bidQ.Length ? bidQ[i] : 0;
                d.AskPriceRaw[i] = i < askP.Length ? askP[i] : 0;
                d.AskQtyRaw[i]   = i < askQ.Length ? askQ[i] : 0;
            }
            return d;
        }
        private static long P(decimal v) => (long)(v * Scale);
        private static long Q(decimal v) => (long)(v * Scale);

        private static L2Depth Ladder()   // 매도 100~104 · 매수 99~95 · 각 20
            => Depth(new[] { P(99), P(98), P(97), P(96), P(95) }, new[] { Q(20), Q(20), Q(20), Q(20), Q(20) },
                     new[] { P(100), P(101), P(102), P(103), P(104) }, new[] { Q(20), Q(20), Q(20), Q(20), Q(20) });

        // ════════════ §1 TwapVwapExecutor ════════════

        [Fact]
        public void Vwap_TotalBelowSliceCount_NoOverfill()
        {
            // [실발생 회귀] 수정 전: 슬라이스마다 최소 1주 강제 → 5주/10슬라이스 = 10주 발주(모 주문 2배).
            var order = new AlgoOrder { TotalQty = 5m, Slices = 10, Style = AlgoStyle.Vwap };
            Span<decimal> q = stackalloc decimal[10];
            TwapVwapExecutor.ComputeVwap(order, q);
            decimal sum = 0m;
            foreach (var x in q) { Assert.True(x >= 0m, $"음수 슬라이스 {x}"); sum += x; }
            Assert.Equal(5m, sum);
        }

        [Theory]
        [InlineData(1, 1)] [InlineData(1, 10)] [InlineData(7, 3)] [InlineData(100, 10)]
        [InlineData(12_345, 64)] [InlineData(12_345, 65)] [InlineData(999_999, 200)] [InlineData(3, 1000)]
        public void TwapAndVwap_SumEqualsTotal_Exactly(int total, int slices)
        {
            var arr = new decimal[slices];
            foreach (var style in new[] { AlgoStyle.Twap, AlgoStyle.Vwap })
            {
                var order = new AlgoOrder { TotalQty = total, Slices = slices, Style = style };
                Array.Clear(arr);
                if (style == AlgoStyle.Twap) TwapVwapExecutor.ComputeTwap(order, arr); else TwapVwapExecutor.ComputeVwap(order, arr);
                Assert.All(arr, x => Assert.True(x >= 0m && x == Math.Floor(x), $"{style}: 비정수/음수 슬라이스 {x}"));
                Assert.Equal((decimal)total, arr.Sum());
            }
        }

        [Fact]
        public void ComputeSlices_ZeroSlices_Throws()
        {
            var order = new AlgoOrder { TotalQty = 10m, Slices = 0 };
            Assert.Throws<ArgumentOutOfRangeException>(() => TwapVwapExecutor.ComputeTwap(order, Span<decimal>.Empty));
            Assert.Throws<ArgumentOutOfRangeException>(() => TwapVwapExecutor.ComputeVwap(order, Span<decimal>.Empty));
        }

        [Fact]
        public void Place_InvalidInputs_RejectedFailClosed_NoChildOrders()
        {
            var spy = new ChildSpyBroker();
            using var ex0 = new TwapVwapExecutor(spy, delayFactory: (_, _) => Task.CompletedTask)
            { DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 0 } };
            Assert.False(ex0.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 100m, 85_000m, 0m).ok);   // 슬라이스 0
            Assert.False(ex0.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 0m, 85_000m, 0m).ok);     // 수량 0
            Assert.False(ex0.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, -5m, 85_000m, 0m).ok);    // 음수

            using var ex1 = new TwapVwapExecutor(spy, delayFactory: (_, _) => Task.CompletedTask)
            { DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 5, Duration = TimeSpan.Zero } };
            Assert.False(ex1.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 100m, 85_000m, 0m).ok);   // 시간창 0

            using var ex2 = new TwapVwapExecutor(spy, delayFactory: (_, _) => Task.CompletedTask)
            { DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = TwapVwapExecutor.MaxSlices + 1 } };
            Assert.False(ex2.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 100m, 85_000m, 0m).ok);   // 과대 분할

            Assert.Empty(spy.Placed);
        }

        [Fact]
        public void Cancel_MidExecution_CancelsAllPlacedChildren()
        {
            // [실발생] 수정 전 Cancel 은 이후 조각만 멈추고 하위 브로커에 이미 나간 미체결 자식은 방치.
            var spy = new ChildSpyBroker();
            using var gate = new SemaphoreSlim(0);
            using var completed = new ManualResetEventSlim(false);
            int fired = 0;
            AlgoExecution? done = null;
            using var executor = new TwapVwapExecutor(spy, delayFactory: (_, ct) => gate.WaitAsync(ct))
            { DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 10, Style = AlgoStyle.Twap } };
            executor.SliceFired += _ => Interlocked.Increment(ref fired);
            executor.AlgoCompleted += e => { done = e; completed.Set(); };

            var (ok, msg) = executor.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 100m, 85_000m, 0m);
            Assert.True(ok);
            gate.Release(3);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref fired) >= 3, 10_000), "3 슬라이스 발사 대기 타임아웃");

            executor.Cancel(msg);
            Assert.True(completed.Wait(10_000), "취소 후 종료 대기 타임아웃");

            Assert.Equal(AlgoState.Cancelled, done!.State);
            var placed = spy.Placed.Select(p => p.Id).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var cancelled = spy.Cancelled.Distinct().OrderBy(s => s, StringComparer.Ordinal).ToArray();
            Assert.Equal(3, placed.Length);
            Assert.Equal(placed, cancelled);          // 나간 자식 전부 취소
            Assert.Empty(executor.ActiveExecutions);  // 활성 표 정리
        }

        [Fact]
        public void Cancel_ArrivingDuringDelay_NoSliceLeaks()
        {
            // [실발생] 대기 종료와 취소가 경합하면 수정 전에는 취소 뒤 조각 1건이 발주됐다(발주 직전 재확인 부재).
            //   동기 완료 지연으로 결정론 재현: 2번째 간격 대기 중 취소 도착 → 대기는 정상 반환.
            var spy = new ChildSpyBroker();
            TwapVwapExecutor? executor = null;
            int delays = 0;
            AlgoExecution? done = null;
            executor = new TwapVwapExecutor(spy, delayFactory: (_, _) =>
            {
                if (Interlocked.Increment(ref delays) == 2) executor!.Cancel("ALGO-1");
                return Task.CompletedTask;
            })
            { DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 10, Style = AlgoStyle.Twap } };
            using (executor)
            {
                executor.AlgoCompleted += e => done = e;
                var (ok, _) = executor.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 100m, 85_000m, 0m);
                Assert.True(ok);
                Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref done) != null, 10_000));

                Assert.Single(spy.Placed);                        // 취소 이후 발주 0
                Assert.Equal(AlgoState.Cancelled, done!.State);
                Assert.Contains("CHILD-1", spy.Cancelled);        // 먼저 나간 자식은 취소
            }
        }

        [Fact]
        public void UnknownBrokerFault_FreezesAndCancelsChildren()
        {
            // [실발생] 수정 전: GATING FREEZE 외 예외는 RunAlgoAsync 밖으로 새어 State 가 Running 으로 고착(활성 표 잔존).
            var spy = new ChildSpyBroker(throwOnCall: 3);
            AlgoExecution? done = null;
            using var executor = new TwapVwapExecutor(spy, delayFactory: (_, _) => Task.CompletedTask)
            { DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 10, Style = AlgoStyle.Twap } };
            executor.AlgoCompleted += e => done = e;
            Assert.True(executor.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 100m, 85_000m, 0m).ok);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref done) != null, 10_000));

            Assert.Equal(AlgoState.Frozen, done!.State);
            Assert.Equal(2, spy.Placed.Count);                                  // 3번째 이후 발주 0
            Assert.Equal(new[] { "CHILD-1", "CHILD-2" }, spy.Cancelled.OrderBy(s => s, StringComparer.Ordinal).ToArray());
            Assert.Empty(executor.ActiveExecutions);
        }

        [Fact]
        public void CompletedRun_SumOfChildQty_EqualsParent()
        {
            var spy = new ChildSpyBroker();
            AlgoExecution? done = null;
            using var executor = new TwapVwapExecutor(spy, delayFactory: (_, _) => Task.CompletedTask)
            { DefaultConfig = new AlgoConfig { MinQty = 1m, Slices = 10, Style = AlgoStyle.Vwap } };
            executor.AlgoCompleted += e => done = e;
            Assert.True(executor.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 7m, 85_000m, 0m).ok);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref done) != null, 10_000));
            Assert.Equal(AlgoState.Completed, done!.State);
            Assert.Equal(7m, spy.Placed.Sum(p => p.Qty));       // 오버필 0 · 잔량 유실 0
            Assert.Equal(7m, done.FilledQty);
            Assert.Empty(spy.Cancelled);
        }

        // ════════════ §2 ExecutionMatrixTracker (방향 A) ════════════

        [Theory]
        [InlineData(1L, 1)] [InlineData(5L, 10)] [InlineData(1_000L, 7)] [InlineData(4_611_686_018_427_387_903L, 64)]
        public void PlanTwap_SumExact_SpreadWithinOne(long total, int slices)
        {
            var dst = new long[slices];
            Assert.Equal(slices, ExecutionMatrixTracker.PlanTwap(total, slices, dst));
            Int128 sum = 0;
            foreach (var x in dst) sum += x;
            Assert.Equal((Int128)total, sum);
            Assert.True(dst.Max() - dst.Min() <= 1);
        }

        [Fact]
        public void PlanVwap_SumExact_HugeTotal_NoOverflow()
        {
            long total = long.MaxValue / 2;
            ReadOnlySpan<long> w = new long[] { long.MaxValue / 4, 3, long.MaxValue / 4, 0, 1 };
            var dst = new long[5];
            Assert.Equal(5, ExecutionMatrixTracker.PlanVwap(total, w, dst));
            Int128 sum = 0;
            foreach (var x in dst) { Assert.True(x >= 0); sum += x; }
            Assert.Equal((Int128)total, sum);
            Assert.Equal(0L, dst[3]);   // 가중치 0 구간 = 0
        }

        [Fact]
        public void PlanVwap_NegativeWeight_Rejected_AllZero_FallsBackToTwap()
        {
            var dst = new long[4];
            Assert.Equal(-1, ExecutionMatrixTracker.PlanVwap(100, new long[] { 1, -1, 1, 1 }, dst));
            Assert.Equal(4, ExecutionMatrixTracker.PlanVwap(10, new long[] { 0, 0, 0, 0 }, dst));
            Assert.Equal(new long[] { 3, 3, 2, 2 }, dst);
            Assert.Equal(-1, ExecutionMatrixTracker.PlanTwap(0, 4, dst));
            Assert.Equal(-1, ExecutionMatrixTracker.PlanTwap(10, 5, dst));   // dst 부족
        }

        [Fact]
        public void Begin_RejectsPlanNotSummingToParent()
        {
            var t = new ExecutionMatrixTracker(8);
            Assert.False(t.Begin(10, new long[] { 5, 6 }));          // 오버필 계획
            Assert.False(t.Begin(10, new long[] { 5, 4 }));          // 잔량 유실 계획
            Assert.False(t.Begin(10, new long[] { 11, -1 }));        // 음수 슬라이스
            Assert.False(t.Begin(10, new long[9]));                  // 용량 초과
            Assert.True(t.Begin(10, new long[] { 5, 5 }));
            Assert.Equal(10, t.ResidualQty);
        }

        [Fact]
        public void PartialFills_Converge_ToParent_WithOverfillClamped()
        {
            var t = new ExecutionMatrixTracker(4);
            Assert.True(t.Begin(100, new long[] { 40, 60 }));
            Assert.Equal(ChildFillVerdict.NotSent, t.ApplyFill(0, 10, 1_000));       // 발주 전 체결 = 이상 통보
            Assert.True(t.MarkSent(0, 9001));
            Assert.True(t.MarkSent(1, 9002));
            Assert.False(t.MarkSent(1, 9003));                                        // 이중 발주 거부

            Assert.Equal(ChildFillVerdict.Applied, t.ApplyFill(0, 15, 1_000));
            Assert.Equal(ChildFillVerdict.Applied, t.ApplyFill(0, 20, 1_010));
            Assert.Equal(ChildOrderState.PartiallyFilled, t.ReadChild(0).State);
            Assert.Equal(ChildFillVerdict.OverfillClamped, t.ApplyFill(0, 9, 1_020)); // 잔량 5 초과 4 격리
            Assert.Equal(ChildOrderState.Filled, t.ReadChild(0).State);
            Assert.Equal(ChildFillVerdict.OverfillClamped, t.ApplyFill(0, 3, 1_020)); // 완료 자식 추가 통보 전량 격리
            Assert.Equal(ChildFillVerdict.ChildCompleted, t.ApplyFill(1, 60, 1_000));
            Assert.Equal(ChildFillVerdict.InvalidFill, t.ApplyFill(1, 0, 1_000));
            Assert.Equal(ChildFillVerdict.InvalidFill, t.ApplyFill(1, 1, 0));
            Assert.Equal(ChildFillVerdict.UnknownChild, t.ApplyFill(7, 1, 1_000));

            Assert.Equal(100, t.FilledQty);                  // 모 주문 초과 0
            Assert.Equal(0, t.ResidualQty);
            Assert.Equal(7, t.OverfillRejectedQty);
            Assert.Equal(1_000_000, t.FillRatioPpm);
            // Σ = 15×1000 + 20×1010 + 5×1020 + 60×1000 = 100,300 → 1003
            Assert.Equal(1_003, t.AvgFillPriceRaw);
        }

        [Fact]
        public void CancelAllOpen_ThenLateFill_ReconciledAsLateAfterCancel()
        {
            var t = new ExecutionMatrixTracker(4);
            Assert.True(t.Begin(90, new long[] { 30, 30, 30 }));
            t.MarkSent(0, 11); t.MarkSent(1, 12);          // 자식 2 는 미발주
            t.ApplyFill(0, 30, 500);
            t.ApplyFill(1, 10, 500);

            Span<long> ids = stackalloc long[4];
            int n = t.RequestCancelAllOpen(ids);
            Assert.Equal(1, n);
            Assert.Equal(12, ids[0]);                                                // 미체결 남은 발주 자식만
            Assert.Equal(ChildOrderState.Cancelled, t.ReadChild(2).State);            // 미발주 → 즉시 취소
            Assert.False(t.MarkSent(2, 13));                                          // 취소 뒤 발주 불가
            Assert.Equal(-1, t.NextPlanned());
            Assert.Equal(0, t.OpenQty);

            Assert.Equal(ChildFillVerdict.LateAfterCancel, t.ApplyFill(1, 5, 500));  // 취소 경합 체결 = 반영 + 식별
            Assert.Equal(ChildOrderState.CancelRequested, t.ReadChild(1).State);
            Assert.True(t.ConfirmCancelled(1));
            Assert.False(t.ConfirmCancelled(1));
            Assert.Equal(45, t.FilledQty);
            Assert.Equal(45, t.ResidualQty);
        }

        [Fact]
        public void Notional_Int128_NoOverflow_AvgPriceExact()
        {
            const long price = 900_000_000_000_000_000L;   // qty×price = 9e20 > long.MaxValue
            var t = new ExecutionMatrixTracker(2);
            Assert.True(t.Begin(2_000, new long[] { 1_000, 1_000 }));
            t.MarkSent(0, 1); t.MarkSent(1, 2);
            t.ApplyFill(0, 1_000, price);
            t.ApplyFill(1, 1_000, price);
            Assert.Equal(price, t.AvgFillPriceRaw);
            Assert.Equal((Int128)price * 1_000, t.ReadChild(0).FilledNotional);
        }

        [Fact]
        public void ConcurrentFills_TotalNeverExceedsParent()
        {
            var t = new ExecutionMatrixTracker(8);
            var plan = new long[8];
            ExecutionMatrixTracker.PlanTwap(80_000, 8, plan);
            Assert.True(t.Begin(80_000, plan));
            for (int i = 0; i < 8; i++) t.MarkSent(i, i + 1);
            const int threads = 8;
            using var barrier = new Barrier(threads);
            var ths = new Thread[threads];
            for (int k = 0; k < threads; k++)
            {
                int seed = k;
                ths[k] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < 5_000; i++) t.ApplyFill((seed + i) & 7, 3, 1_000);   // 총 통보 120,000 > 80,000
                });
            }
            foreach (var x in ths) x.Start();
            foreach (var x in ths) Assert.True(x.Join(30_000));
            Assert.Equal(80_000, t.FilledQty);
            Assert.Equal(120_000 - 80_000, t.OverfillRejectedQty);
            Assert.Equal(1_000, t.AvgFillPriceRaw);
        }

        // ════════════ §3 ExecutionImpactInterlock (방향 B) ════════════

        private static ExecutionMatrixTracker SentTracker(int children, long each)
        {
            var t = new ExecutionMatrixTracker(children);
            var plan = new long[children];
            Array.Fill(plan, each);
            Assert.True(t.Begin(each * children, plan));
            for (int i = 0; i < children; i++) Assert.True(t.MarkSent(i, 101 + i));
            return t;
        }

        [Fact]
        public void ImpactOverLimit_CancelsRemainingChildren_StickyDisarm_PurgesSecrets()
        {
            var t = SentTracker(4, 25);
            var cancelled = new ConcurrentQueue<long>();
            var purger = new VolatileMemoryPurger();
            var secret = new byte[] { 1, 2, 3, 4 };
            purger.Register(secret);
            int trips = 0; long tripBps = 0;
            var il = new ExecutionImpactInterlock(t, isBuy: true, arrivalPriceRaw: 1_000_000, maxAdverseBps: 50,
                                                  cancelChild: cancelled.Enqueue, purger: purger,
                                                  onTrip: b => { Interlocked.Increment(ref trips); tripBps = b; });

            il.OnFill(0, 25, 1_002_000);                     // 평균 +20bps — 허용
            Assert.False(il.Tripped);
            Assert.True(il.CanSendNext);
            il.OnFill(1, 25, 1_020_000);                     // 평균 1,011,000 = +110bps — 초과
            Assert.True(il.Tripped);
            Assert.True(il.ArmingBlocked);
            Assert.False(il.CanSendNext);
            Assert.Equal(110, tripBps);
            Assert.Equal(new long[] { 103, 104 }, cancelled.ToArray());   // 잔여 미체결 자식 전량 취소
            Assert.Equal(2, il.CancelRequests);
            Assert.All(secret, b => Assert.Equal(0, b));                  // 자격증명 소거

            Assert.Equal(ChildFillVerdict.LateAfterCancel, il.OnFill(2, 5, 1_030_000));   // 경합 체결 반영
            Assert.Equal(1, trips);                                        // 재트립 0(원샷)
            Assert.Equal(2, il.CancelRequests);

            Assert.False(il.TryRearm(true, false));
            Assert.False(il.TryRearm(false, true));
            Assert.True(il.ArmingBlocked);
            Assert.True(il.TryRearm(true, true));
            Assert.False(il.ArmingBlocked);
        }

        [Theory]
        [InlineData(true, 995_000L)]    // 매수 · 도착가 아래 체결 = 유리
        [InlineData(false, 1_005_000L)] // 매도 · 도착가 위 체결 = 유리
        public void FavorableFill_NeverTrips(bool isBuy, long px)
        {
            var t = SentTracker(2, 10);
            var il = new ExecutionImpactInterlock(t, isBuy, 1_000_000, maxAdverseBps: 0);
            il.OnFill(0, 10, px);
            Assert.False(il.Tripped);
            Assert.True(il.LastAdverseBps < 0);
        }

        [Fact]
        public void SellSide_AdverseDrop_Trips()
        {
            var t = SentTracker(2, 10);
            int trips = 0;
            var il = new ExecutionImpactInterlock(t, isBuy: false, arrivalPriceRaw: 1_000_000, maxAdverseBps: 30, onTrip: _ => trips++);
            il.OnFill(0, 10, 995_000);    // −50bps 불리
            Assert.True(il.Tripped);
            Assert.Equal(1, trips);
            Assert.Equal(ChildOrderState.CancelRequested, t.ReadChild(1).State);
        }

        [Fact]
        public void CallbackFaults_DoNotBlockCancelOrDisarm()
        {
            var t = SentTracker(3, 10);
            var il = new ExecutionImpactInterlock(t, true, 1_000_000, 10,
                                                  cancelChild: _ => throw new InvalidOperationException("cancel fault"),
                                                  onTrip: _ => throw new InvalidOperationException("trip fault"));
            il.OnFill(0, 10, 1_100_000);
            Assert.True(il.ArmingBlocked);
            Assert.Equal(2, il.CancelRequests);
            Assert.Equal(3, il.CallbackFaults);
        }

        [Fact]
        public void AdverseBps_Int128_ExtremePrices_NoOverflow()
        {
            Assert.Equal(10_000, ExecutionImpactInterlock.AdverseBps(true, long.MaxValue / 2, long.MaxValue - 1));
            Assert.Equal(0, ExecutionImpactInterlock.AdverseBps(true, 0, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => new ExecutionImpactInterlock(new ExecutionMatrixTracker(1), true, 0, 10));
        }

        // ════════════ §4 MicroLiquiditySlicer ════════════

        [Fact]
        public void SlippageCap_Market_ExcludesLevelsBeyondBand()
        {
            var d = Ladder();
            Assert.Equal(Q(100), MicroLiquiditySlicer.ComputeFillableQty(in d, OrderSide.Buy, 0));                    // 캡 없음 = 5호가 전부
            Assert.Equal(Q(40), MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Buy, 0, 150));     // 100 × 1.015 = 101.5 → 100·101
            Assert.Equal(Q(20), MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Buy, P(100.5m), 150));   // 지정가가 더 보수적
            Assert.Equal(Q(40), MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Buy, P(104), 150));      // 캡이 더 보수적
            Assert.Equal(Q(40), MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Sell, 0, 150));    // 99 × 0.985 = 97.515 → 99·98
            Assert.Equal(Q(100), MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Buy, 0, 0));      // 0 = 비활성
        }

        [Fact]
        public void SlippageCap_MissingBest_FailClosedZero()
        {
            var d = Depth(new[] { 0L, P(98) }, new[] { Q(20), Q(20) }, new[] { 0L, P(101) }, new[] { Q(20), Q(20) });
            Assert.Equal(0, MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Buy, 0, 100));
            Assert.Equal(0, MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Sell, 0, 100));
        }

        [Fact]
        public void Fillable_HugeDepth_Saturates_NotNegative()
        {
            // [실발생 회귀] 수정 전 long 누적 → 오버플로 음수 → 조각 0 / 오판.
            long h = long.MaxValue / 2;
            var d = Depth(new[] { P(99) }, new[] { Q(1) }, new[] { P(100), P(101), P(102) }, new[] { h, h, h });
            Assert.Equal(long.MaxValue, MicroLiquiditySlicer.ComputeFillableQty(in d, OrderSide.Buy, 0));
        }

        [Fact]
        public void SliceAndDispatch_SlippageCapped_ResidualReported()
        {
            var ring = new OrderDispatchRing(64);
            var slicer = new MicroLiquiditySlicer(ring, toxic: null, throttle: null, maxSlices: 4, maxSliceSlippageBps: 150);
            var d = Ladder();
            int n = slicer.SliceAndDispatch(in d, 5930, 0, OrderSide.Buy, Q(100), 0, 0, out long residual);
            Assert.True(n > 0);
            Assert.Equal(Q(60), residual);                    // 캡 40 사출 · 60 호출측 반환(유실 0)
            Assert.Equal(150, slicer.MaxSliceSlippageBps);
        }

        [Fact]
        public void SliceAndDispatch_ThrottleStop_ResidualReturned()
        {
            var ring = new OrderDispatchRing(64);
            var th = new AdaptiveOrderThrottle(maxPerSec: 10, tickFrequency: 1_000_000L);   // limit1s = 9
            var slicer = new MicroLiquiditySlicer(ring, toxic: null, throttle: th, maxSlices: 16);
            var d = Ladder();
            int n = slicer.SliceAndDispatch(in d, 5930, 0, OrderSide.Buy, Q(80), 0, 0, out long residual);   // 16 × 5
            Assert.Equal(9, n);
            Assert.Equal(Q(80) - 9 * Q(5), residual);
            Assert.Equal(residual, slicer.ResidualReturnedRaw);
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var plan = new long[16];
            var w = new long[16];
            for (int i = 0; i < w.Length; i++) w[i] = i + 1;
            var t = new ExecutionMatrixTracker(16);
            var il = new ExecutionImpactInterlock(t, true, 1_000_000, 1_000_000);
            var d = Ladder();
            long sink = 0;

            void Setup()
            {
                ExecutionMatrixTracker.PlanTwap(1_600, 16, plan);
                t.Begin(1_600, plan);
                for (int i = 0; i < 16; i++) t.MarkSent(i, i + 1);
            }
            Setup();
            il.OnFill(0, 1, 1_000_000);
            MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, OrderSide.Buy, 0, 150);

            Assert.Equal(0, MinAllocated(() =>
            {
                for (int k = 0; k < 1_000; k++)
                {
                    sink += ExecutionMatrixTracker.PlanTwap(1_600, 16, plan);
                    sink += ExecutionMatrixTracker.PlanVwap(1_600, w, plan);
                }
            }));
            Assert.Equal(0, MinAllocated(() =>
            {
                Setup();
                for (int k = 0; k < 1_600; k++) sink += (long)t.ApplyFill(k & 15, 1, 1_000_000 + k);
                sink += t.AvgFillPriceRaw + t.FillRatioPpm + t.OpenQty;
            }));
            Assert.Equal(0, MinAllocated(() =>
            {
                Setup();
                for (int k = 0; k < 1_600; k++) sink += (long)il.OnFill(k & 15, 1, 1_000_000);
            }));
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int k = 0; k < 10_000; k++) sink += MicroLiquiditySlicer.ComputeFillableWithinSlippage(in d, (k & 1) == 0 ? OrderSide.Buy : OrderSide.Sell, 0, 150);
            }));
            Assert.True(sink != 0);
        }

        [Fact]
        public void ImpactTrip_ZeroAlloc()
        {
            long sink = 0;
            Action<long> cancel = id => sink += id;
            var warm = new ExecutionImpactInterlock(SentTracker(16, 10), true, 1_000_000, 10, cancelChild: cancel);
            warm.OnFill(0, 10, 1_100_000);   // Trip 경로 JIT 예열(별 인스턴스)
            var t = SentTracker(16, 10);
            var il = new ExecutionImpactInterlock(t, true, 1_000_000, 10, cancelChild: cancel);
            Assert.Equal(0, MinAllocated(() => il.OnFill(0, 10, 1_100_000), windows: 1));   // Trip + 15 취소 콜백 · 사전할당 버퍼
            Assert.True(il.Tripped);
            Assert.Equal(15, il.CancelRequests);
            Assert.True(sink > 0);
        }
    }
}
