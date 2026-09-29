// ============================================================================
// OrderBookMicrostructureTests — v12.3.0 High-Throughput Order Book Depth Matrix & Micro-Slippage Profiler 적대검증
//   Section 1: OrderBookDepthMatrixCoalescer(δ=0 seqlock 링) → 최신 호가 매트릭스 프로젝션 + 잔량 불균형비 산출
//     → IOrderBookPort/OrderBookAdapter 결속.
//   Think Beyond: SlippageForensicHeatmapRouter(POH 히스토그램·원자 페널티·라우팅 우선순위).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class OrderBookMicrostructureTests
    {
        private readonly ITestOutputHelper _out;
        public OrderBookMicrostructureTests(ITestOutputHelper o) => _out = o;

        // ── §1 미발행 상태 — 프로젝션 실패(false) ───────────────────────────────
        [Fact]
        public void TryProjectLatest_NoPublishYet_ReturnsFalse()
        {
            var book = new OrderBookDepthMatrixCoalescer(depth: 4, capacityPow2: 8);
            Span<long> a = stackalloc long[4], b = stackalloc long[4], c = stackalloc long[4], d = stackalloc long[4];
            Assert.False(book.TryProjectLatest(a, b, c, d, out long seq, out long imb));
            Assert.Equal(0, seq); Assert.Equal(0, imb);
        }

        // ── §2 Publish → TryProjectLatest 왕복 — 필드 정합 ──────────────────────
        [Fact]
        public void Publish_Then_Project_RoundTrips_Fields()
        {
            var book = new OrderBookDepthMatrixCoalescer(depth: 3, capacityPow2: 8);
            long[] bidPx = { 9990, 9980, 9970 }, bidQty = { 10, 20, 30 };
            long[] askPx = { 10000, 10010, 10020 }, askQty = { 5, 15, 25 };
            long seq0 = book.Publish(bidPx, bidQty, askPx, askQty);
            Assert.Equal(0, seq0);

            Span<long> obp = stackalloc long[3], obq = stackalloc long[3], oap = stackalloc long[3], oaq = stackalloc long[3];
            Assert.True(book.TryProjectLatest(obp, obq, oap, oaq, out long seq, out long imbalancePpm));
            Assert.Equal(0, seq);
            Assert.Equal(9990, obp[0]); Assert.Equal(10, obq[0]);
            Assert.Equal(10020, oap[2]); Assert.Equal(25, oaq[2]);

            // sumBid=60, sumAsk=45 → imbalance=(60-45)*1e6/105 ≈ 142,857(매수 우위 양수)
            Assert.True(imbalancePpm > 0);
            Assert.Equal((60 - 45) * 1_000_000L / 105L, imbalancePpm);
        }

        // ── §3 깊이 불일치 — fail-closed 즉시 예외(부분 갱신 없음) ───────────────
        [Fact]
        public void Publish_DepthMismatch_Throws()
        {
            var book = new OrderBookDepthMatrixCoalescer(depth: 5, capacityPow2: 4);
            long[] wrong = { 1, 2, 3 };
            Assert.Throws<ArgumentException>(() => book.Publish(wrong, wrong, wrong, wrong));
        }

        // ── §4 매도 우위 — 불균형비 음수 ─────────────────────────────────────────
        [Fact]
        public void Imbalance_AskHeavy_YieldsNegativeRatio()
        {
            var book = new OrderBookDepthMatrixCoalescer(depth: 2, capacityPow2: 4);
            book.Publish(new long[] { 100, 99 }, new long[] { 1, 1 }, new long[] { 101, 102 }, new long[] { 100, 100 });
            Span<long> a = stackalloc long[2], b = stackalloc long[2], c = stackalloc long[2], d = stackalloc long[2];
            book.TryProjectLatest(a, b, c, d, out _, out long imb);
            Assert.True(imb < 0);
        }

        // ── §5 최신 상태만 유효(드레인 아님) — 다중 Publish 후 항상 마지막 스냅샷 ──
        [Fact]
        public void MultiplePublish_ProjectsAlwaysLatest_NotQueue()
        {
            var book = new OrderBookDepthMatrixCoalescer(depth: 1, capacityPow2: 4);
            for (long i = 1; i <= 10; i++)
                book.Publish(new[] { i }, new[] { i * 10 }, new[] { i + 1000 }, new[] { i * 5 });

            Span<long> a = stackalloc long[1], b = stackalloc long[1], c = stackalloc long[1], d = stackalloc long[1];
            Assert.True(book.TryProjectLatest(a, b, c, d, out long seq, out _));
            Assert.Equal(9, seq);         // 0-based — 10번째 게시(i=10)의 시퀀스
            Assert.Equal(10, a[0]);        // 가장 최근 값만 반영(중간 이력 무의미)
        }

        // ── §6 SlippageForensicHeatmapRouter — 버킷 누적 + 이상 버킷 페널티 가산 ───
        [Fact]
        public void Heatmap_RecordsBuckets_And_PenalizesTopBucket()
        {
            var router = new SlippageForensicHeatmapRouter(brokerCount: 2, bucketCount: 4, bucketWidthBps: 5);
            router.RecordSlippage(0, 2);    // bucket 0(0-4bps)
            router.RecordSlippage(0, 100);  // 최상위 버킷(이상) → 페널티 가산
            Assert.Equal(1, router.BucketHits(0, 0));
            Assert.Equal(1, router.BucketHits(0, 3));
            Assert.Equal(2, router.SampleCount(0));
            Assert.True(router.PenaltyPpm(0) > 0);
            Assert.True(router.RoutingPriorityPpm(0) < router.RoutingPriorityPpm(1));   // 브로커1은 무결 → 우선순위 상대적 우위
        }

        // ── §7 페널티 캡 — 대량 이상치에도 1,000,000 을 초과하지 않음 ───────────────
        [Fact]
        public void Heatmap_PenaltyCap_NeverExceedsMax()
        {
            var router = new SlippageForensicHeatmapRouter(brokerCount: 1, bucketCount: 2, bucketWidthBps: 1);
            for (int i = 0; i < 500; i++) router.RecordSlippage(0, 1_000);   // 매번 최상위 버킷
            Assert.Equal(1_000_000, router.PenaltyPpm(0));
            Assert.Equal(0, router.RoutingPriorityPpm(0));
        }

        // ── §8 범위 밖 brokerId — fail-closed 무시 ────────────────────────────
        [Fact]
        public void Heatmap_OutOfRangeBrokerId_Ignored()
        {
            var router = new SlippageForensicHeatmapRouter(brokerCount: 1, bucketCount: 2, bucketWidthBps: 1);
            router.RecordSlippage(5, 100);   // 범위 밖
            Assert.Equal(0, router.SampleCount(5));
            Assert.Equal(0, router.PenaltyPpm(5));
        }

        // ── §9 OrderBookAdapter — IOrderBookPort 결속 통합 실증 ──────────────────
        [Fact]
        public void Adapter_Snapshot_Bridges_Engine_To_Port()
        {
            var engine = new OrderBookDepthMatrixCoalescer(depth: 2, capacityPow2: 4);
            engine.Publish(new long[] { 10000, 9990 }, new long[] { 300, 200 }, new long[] { 10010, 10020 }, new long[] { 100, 100 });
            var adapter = new OrderBookAdapter("005930", engine, priceScale: 100m);

            var snap = adapter.Snapshot();
            Assert.True(snap.HasData);
            Assert.Equal("005930", snap.Symbol);
            Assert.Equal(2, snap.Bids.Count);
            Assert.Equal(100.00m, snap.Bids[0].Price);   // 10000/100
            Assert.Equal(3.00m, snap.Bids[0].Qty);        // 300/100
            Assert.True(snap.ImbalanceRatio > 0);          // 매수(500) > 매도(200)
        }

        [Fact]
        public void Adapter_Snapshot_NoData_ReturnsHasDataFalse()
        {
            var engine = new OrderBookDepthMatrixCoalescer(depth: 2, capacityPow2: 4);
            var adapter = new OrderBookAdapter("005930", engine);
            Assert.False(adapter.Snapshot().HasData);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §10 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class OrderBookMicrostructureConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public OrderBookMicrostructureConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly OrderBookDepthMatrixCoalescer _hotBook = new(depth: 10, capacityPow2: 64);
        private static readonly long[] _hotBidPx = new long[10], _hotBidQty = new long[10], _hotAskPx = new long[10], _hotAskQty = new long[10];
        private static void PublishHot() => _hotBook.Publish(_hotBidPx, _hotBidQty, _hotAskPx, _hotAskQty);

        [Fact]
        public void Publish_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) PublishHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) PublishHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★호가 매트릭스 게시 힙 0(Pinned 배열 직접 기입)
        }

        [Fact]
        public void TryProjectLatest_ZeroAllocation()
        {
            _hotBook.Publish(_hotBidPx, _hotBidQty, _hotAskPx, _hotAskQty);
            var a = new long[10]; var b = new long[10]; var c = new long[10]; var d = new long[10];
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) _hotBook.TryProjectLatest(a, b, c, d, out _, out _);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) _hotBook.TryProjectLatest(a, b, c, d, out _, out _);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★프로젝션+불균형비 동시 산출 힙 0
        }

        // 50,000+ 건/초 다중 생산자 게시 동시 지속 판독 — torn 없이 항상 자기정합(같은 슬롯 내 필드 쌍 일치).
        [Fact]
        public void Concurrent_MultiProducer_Publish_And_Read_NoTornSnapshots()
        {
            var book = new OrderBookDepthMatrixCoalescer(depth: 4, capacityPow2: 256);
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(4);
            Exception? err = null;

            for (int p = 0; p < 3; p++)
            {
                var th = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        for (long i = 1; i <= 20_000; i++)
                        {
                            long[] px = { i, i, i, i }; long[] qty = { i * 2, i * 2, i * 2, i * 2 };
                            book.Publish(px, qty, px, qty);   // bid==ask 값 동일 패턴 → 판독 시 자기정합 검증 가능
                        }
                    }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true };
                th.Start();
            }

            var reader = new Thread(() =>
            {
                try
                {
                    start.Wait();
                    var a = new long[4]; var b = new long[4]; var c = new long[4]; var d = new long[4];
                    for (int i = 0; i < 20_000; i++)
                    {
                        if (book.TryProjectLatest(a, b, c, d, out _, out _))
                        {
                            for (int k = 0; k < 4; k++)
                            {
                                Assert.Equal(a[k], c[k]);          // bid price == ask price(동일 패턴 게시)
                                Assert.Equal(b[k], a[k] * 2);       // qty == price*2(같은 슬롯 내 필드 쌍 일치 = non-torn)
                            }
                        }
                    }
                }
                catch (Exception e) { err = e; }
                finally { done.Signal(); }
            }) { IsBackground = true };
            reader.Start();

            start.Set();
            Assert.True(done.Wait(30_000));
            Assert.Null(err);
        }
    }
}
