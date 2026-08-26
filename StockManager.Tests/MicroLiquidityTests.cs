// ============================================================================
// MicroLiquidityTests — v5.2.0 미시 유동성 슬라이서 & 적응형 오더 스로틀
//
//  §1 MicroLiquiditySlicer — 비대칭·즉시체결 잔량·슬라이싱·덤프 슬리피지·독성 홀드.
//  §2 AdaptiveOrderThrottle — 90% 한도 Hold·창 만료 리셋.
//  §3 ToxicFlowSentinel — 하드 임계 Caution·유령 Toxic·북워크 베토 등급.
//  §4 ★적대검증 — 비대칭/슬라이싱 핫패스 δ=0·미무장 시 슬라이스 실주문 0.
// ============================================================================
using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MicroLiquidityTests
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

        // ════════════ §1 MicroLiquiditySlicer ════════════
        [Fact]
        public void ComputeImbalance_BidHeavy_Positive()
        {
            var d = Depth(new[]{P(100)}, new[]{Q(100)}, new[]{P(101)}, new[]{Q(50)});
            long imb = MicroLiquiditySlicer.ComputeImbalance(in d);   // (100-50)/150 × Scale = +0.333
            Assert.True(imb > 0);
            Assert.Equal((long)((Int128)(Q(100) - Q(50)) * Scale / (Q(100) + Q(50))), imb);
        }

        [Fact]
        public void ComputeFillableQty_Buy_WithinLimit()
        {
            var d = Depth(new[]{P(99)}, new[]{Q(10)}, new[]{P(100), P(101), P(102)}, new[]{Q(10), Q(20), Q(30)});
            long fill = MicroLiquiditySlicer.ComputeFillableQty(in d, OrderSide.Buy, P(101));   // ask price<=101 → 10+20
            Assert.Equal(Q(10) + Q(20), fill);
        }

        [Fact]
        public void SliceInto_CapsAtFillable_SplitsEqually()
        {
            Span<long> dst = stackalloc long[4];
            int n = MicroLiquiditySlicer.SliceInto(dst, totalQtyRaw: Q(100), fillableRaw: Q(30), maxSlices: 3);
            Assert.Equal(3, n);
            long sum = 0; for (int i = 0; i < n; i++) sum += dst[i];
            Assert.Equal(Q(30), sum);   // 즉시체결 잔량(30)까지만·총합 보존
        }

        [Fact]
        public void EstimateDumpSlippageBps_EscalatingBook_Positive()
        {
            var d = Depth(new[]{P(99)}, new[]{Q(10)}, new[]{P(100), P(110), P(120)}, new[]{Q(10), Q(10), Q(10)});
            long bps = MicroLiquiditySlicer.EstimateDumpSlippageBps(in d, OrderSide.Buy, Q(30));   // VWAP=110 vs best 100 → +1000bps
            Assert.True(bps > 0);
            Assert.Equal((long)((Int128)(P(110) - P(100)) * 10_000 / P(100)), bps);
        }

        [Fact]
        public void SliceAndDispatch_PublishesSlices_ToRing()
        {
            var ring = new OrderDispatchRing(64);
            var slicer = new MicroLiquiditySlicer(ring, toxic: null, throttle: null, maxSlices: 4);
            var d = Depth(new[]{P(99)}, new[]{Q(10)}, new[]{P(100), P(101)}, new[]{Q(20), Q(20)});
            int n = slicer.SliceAndDispatch(in d, 5930, 0, OrderSide.Buy, totalQtyRaw: Q(40), limitPriceRaw: P(101), nowTicks: 0);
            Assert.True(n > 0);
            Assert.Equal(n, slicer.SlicesEmitted);
        }

        // ════════════ §2 AdaptiveOrderThrottle ════════════
        [Fact]
        public void Throttle_UnderLimit_Acquires_Then90PercentHolds()
        {
            var th = new AdaptiveOrderThrottle(maxPerSec: 10, tickFrequency: 1_000_000L);   // 90% = 9
            int acquired = 0;
            for (int i = 0; i < 20; i++) if (th.TryAcquire(nowTicks: 0)) acquired++;   // 같은 초
            Assert.Equal(9, acquired);          // ★90%(9) 도달 후 Hold
            Assert.True(th.Throttled >= 11);
        }

        [Fact]
        public void Throttle_WindowExpires_ResetsRate()
        {
            var th = new AdaptiveOrderThrottle(maxPerSec: 10, tickFrequency: 1_000_000L);
            for (int i = 0; i < 20; i++) th.TryAcquire(0);        // 0초 창 소진
            Assert.False(th.TryAcquire(0));                       // 여전히 Hold
            Assert.True(th.TryAcquire(2_000_000L));               // 2초 후 → 새 창 → 허용
        }

        [Fact]
        public void Throttle_NewSecond_ResetsExactly_StaleAware()
        {
            // ★적대검증 회귀: 패킹 버킷 세대 교체가 정확히 리셋·Rate1s 는 stale-aware(다른 초→0, 배너 프리즈 봉쇄).
            var th = new AdaptiveOrderThrottle(maxPerSec: 10, tickFrequency: 1_000_000L);   // limit1s=9
            for (int i = 0; i < 20; i++) th.TryAcquire(0);
            Assert.Equal(9L, th.Rate1s(0));                 // 0초 창 정확히 9
            Assert.Equal(0L, th.Rate1s(5_000_000L));        // ★5초 시점 조회 → 0(흐름 정지 후 프리즈 안 함)
            Assert.True(th.TryAcquire(1_000_000L));         // 1초 → 새 세대 버킷 → 허용
            Assert.Equal(1L, th.Rate1s(1_000_000L));
        }

        [Fact]
        public void EstimateDumpSlippageBps_ThinBook_NotUnderstated()
        {
            // ★적대검증 회귀: best 만 채워진 얇은 북에서 remainder 를 best 로 과소평가하던 결함 → 유령 깊이 보수 페널티로 양수.
            var d = Depth(new[]{P(99)}, new[]{Q(10)}, new[]{P(100)}, new[]{Q(10)});   // ask best 만·잔량 10
            long bps = MicroLiquiditySlicer.EstimateDumpSlippageBps(in d, OrderSide.Buy, Q(30));   // 30 요청·가시 10 → 20 유령
            Assert.True(bps > 0);   // ★0 아님(유령 깊이 페널티 반영)
        }

        // ════════════ §3 ToxicFlowSentinel ════════════
        [Fact]
        public void Toxic_HardImbalancePlusRttPhantom_Toxic_Downgrades()
        {
            var sent = new ToxicFlowSentinel(new IgnitionLock(), rescaleSlippage: null, rttDeadlineTicks: 1000);
            var d = Depth(new[]{P(100)}, new[]{Q(95)}, new[]{P(101)}, new[]{Q(5)});
            var v = sent.Ingest(in d, imbalanceRaw: 95_000_000L, quoteRttTicks: 5000);   // 하드(95%)+RTT 유령
            Assert.Equal(ToxicityVerdict.Toxic, v);
            Assert.Equal(1L, sent.ToxicTrips);
        }

        [Fact]
        public void Toxic_HardImbalanceAlone_Caution()
        {
            var sent = new ToxicFlowSentinel();
            var d = Depth(new[]{P(100)}, new[]{Q(95)}, new[]{P(101)}, new[]{Q(5)});
            var v = sent.Ingest(in d, imbalanceRaw: 95_000_000L, quoteRttTicks: 0);   // 하드 단독·유령 없음
            Assert.Equal(ToxicityVerdict.Caution, v);
        }

        [Fact]
        public void Toxic_Balanced_Clear()
        {
            var sent = new ToxicFlowSentinel();
            var d = Depth(new[]{P(100)}, new[]{Q(50)}, new[]{P(101)}, new[]{Q(50)});
            Assert.Equal(ToxicityVerdict.Clear, sent.Ingest(in d, imbalanceRaw: 0, quoteRttTicks: 0));
        }

        [Fact]
        public void Toxic_AdjustedFillable_Grading()
        {
            var sent = new ToxicFlowSentinel();
            var balanced = Depth(new[]{P(100)}, new[]{Q(50)}, new[]{P(101)}, new[]{Q(50)});
            sent.Ingest(in balanced, 0, 0);   // Clear
            Assert.Equal(1000L, sent.ToxicityAdjustedFillable(1000L));   // 100%
            var caution = Depth(new[]{P(100)}, new[]{Q(95)}, new[]{P(101)}, new[]{Q(5)});
            sent.Ingest(in caution, 95_000_000L, 0);   // Caution
            Assert.Equal(500L, sent.ToxicityAdjustedFillable(1000L));    // 50%
        }

        [Fact]
        public void Slicer_ToxicVeto_HoldsAllSlices()
        {
            var ring = new OrderDispatchRing(64);
            var sent = new ToxicFlowSentinel(rttDeadlineTicks: 1000);   // RTT 유령 활성
            var slicer = new MicroLiquiditySlicer(ring, sent, throttle: null);
            var toxicDepth = Depth(new[]{P(100)}, new[]{Q(95)}, new[]{P(101)}, new[]{Q(5)});
            Assert.Equal(ToxicityVerdict.Toxic, sent.Ingest(in toxicDepth, 95_000_000L, 5000));   // 하드(95%)+RTT 유령 → Toxic
            int n = slicer.SliceAndDispatch(in toxicDepth, 5930, 0, OrderSide.Buy, Q(40), P(101), 0);
            Assert.Equal(0, n);                              // ★Toxic → 전량 홀드
            Assert.True(slicer.HeldByToxic > 0);
        }
    }

    // ── §4 ★적대검증(부하/타이밍 → 직렬화) ──────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class MicroLiquidityConcurrencyTests
    {
        private const long Scale = 100_000_000L;
        private static L2Depth MakeDepth()
        {
            var d = new L2Depth { SymbolCode = 5930 };
            for (int i = 0; i < 5; i++)
            {
                d.BidPriceRaw[i] = (100 - i) * Scale; d.BidQtyRaw[i] = (10 + i) * Scale;
                d.AskPriceRaw[i] = (101 + i) * Scale; d.AskQtyRaw[i] = (8 + i) * Scale;
            }
            return d;
        }

        [Fact]
        public void HotPath_ImbalanceAndSlicing_ZeroAllocation()
        {
            // ★비대칭 연산 + 5호가 슬라이싱 파티셔닝이 초당 수만 회전해도 힙 할당 0(δ=0).
            var d = MakeDepth();
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Span<long> slices = stackalloc long[16];
                    for (int i = 0; i < 300_000; i++)   // 워밍업
                    {
                        long imb = MicroLiquiditySlicer.ComputeImbalance(in d);
                        long fill = MicroLiquiditySlicer.ComputeFillableQty(in d, OrderSide.Buy, (101L + 4) * Scale);
                        MicroLiquiditySlicer.SliceInto(slices, 50 * Scale, fill, 8);
                        _ = imb;
                    }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++)
                    {
                        long imb = MicroLiquiditySlicer.ComputeImbalance(in d);
                        long fill = MicroLiquiditySlicer.ComputeFillableQty(in d, OrderSide.Buy, (101L + 4) * Scale);
                        int n = MicroLiquiditySlicer.SliceInto(slices, 50 * Scale, fill, 8);
                        if (imb == long.MinValue || n < 0) throw new Exception("sink");   // JIT 제거 방지
                    }
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) when (e.Message != "sink") { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★비대칭·슬라이싱 핫패스 힙 0
        }

        [Fact]
        public void Throttle_ConcurrentSameSecond_BoundedNoRunaway()
        {
            // ★적대검증 회귀: 원자 패킹 버킷 → 동시 TryAcquire 에도 카운트 유실 없이 상한+동시성 경계로 유계(lost-update fail-open 봉쇄).
            var th = new AdaptiveOrderThrottle(maxPerSec: 100, tickFrequency: 1_000_000L);   // limit1s=90
            const int threads = 8, iters = 5000;
            long acquired = 0;
            var barrier = new Barrier(threads);
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
                ths[t] = new Thread(() => { barrier.SignalAndWait(); for (int i = 0; i < iters; i++) if (th.TryAcquire(0L)) Interlocked.Increment(ref acquired); });
            foreach (var x in ths) x.Start();
            foreach (var x in ths) Assert.True(x.Join(30_000));
            long got = Interlocked.Read(ref acquired);
            Assert.True(got >= 90 && got <= 90 + threads, $"acquired={got} — 상한 90 + 경계 {threads} 유계여야(유실 시 폭주)");
            Assert.True(th.Throttled > 0);
        }

        [Fact]
        public void LiveArmed_Slices_NoRealOrderWhenUnarmed()
        {
            // ★슬라이서가 주문을 잘게 쪼개 링에 발행해도, 무장(이중키) 아니면 GatedOrderDispatcher 가 전량 GatedOut — 실 송출 0.
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var slicer = new MicroLiquiditySlicer(ring, toxic: null, throttle: null, maxSlices: 4);
            var d = new L2Depth { SymbolCode = 5930 };
            for (int i = 0; i < 5; i++) { d.AskPriceRaw[i] = (100L + i) * Scale; d.AskQtyRaw[i] = 20 * Scale; d.BidPriceRaw[i] = (99L - i) * Scale; d.BidQtyRaw[i] = 20 * Scale; }
            int emitted = slicer.SliceAndDispatch(in d, 5930, 0, OrderSide.Buy, 40 * Scale, 104 * Scale, 0);
            Assert.True(emitted > 0);   // 슬라이스 발행됨

            var disp = new GatedOrderDispatcher(ring, isArmed: () => false, att);   // ★미무장
            bool sent = false;
            int drained = disp.Drain(_ => sent = true);
            Assert.False(sent);                       // ★실 송출 0바이트(콜백 미호출)
            Assert.Equal(emitted, drained);
            Assert.True(disp.GatedOut >= emitted);    // 전량 게이팅 차단
            Assert.Equal(0L, disp.Forwarded);
        }
    }
}
