// ============================================================================
// ExecutionProfilerTests — v12.3.0 Micro-Slippage & Execution Fill Latency Profiler 적대검증
//   Section 2: ExecutionSlippageProfiler 가 슬리피지(bps)/Order-to-Fill 지연(µs) 을 산출해 기존
//     MultiTelemetryDisarmInterlock(TelemetrySource.Slippage/Latency) 에 Note() 로 전달 → 임계 초과 시
//     0ms 원자 Disarm(sticky) 격발을 실증한다(신규 병렬 인터록 없음 — 검증된 v11.4.4 경로 재사용).
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
    public sealed class ExecutionProfilerTests
    {
        private readonly ITestOutputHelper _out;
        public ExecutionProfilerTests(ITestOutputHelper o) => _out = o;

        // tickFrequency=1_000_000 → 1 tick = 1µs(테스트 편의를 위한 결정론적 스케일. 실사용은 Stopwatch.Frequency 주입).
        private static (ExecutionSlippageProfiler profiler, MultiTelemetryDisarmInterlock interlock) NewProfiler(
            long latencyThresholdMicros = 30_000, long slippageThresholdBps = 5)
        {
            var recorder = new AnomalyFlightRecorder();
            var interlock = new MultiTelemetryDisarmInterlock(recorder);
            interlock.Configure(latencyThresholdMicros, http429Count: 0, slippageThresholdBps);
            var profiler = new ExecutionSlippageProfiler(orderSlotCapacity: 64, interlock, heatmap: null, tickFrequency: 1_000_000);
            return (profiler, interlock);
        }

        // ── §1 임계 이내 — Disarm 없음 ────────────────────────────────────────
        [Fact]
        public void RecordFill_WithinThresholds_NoDisarm()
        {
            var (p, il) = NewProfiler();
            p.RecordOrderSent(slot: 0, intendedPriceRaw: 1_000_000, sentTicks: 0);
            var (slip, lat) = p.RecordFill(slot: 0, filledPriceRaw: 1_000_300, ackTicks: 5_000, brokerId: 0);   // 3bps, 5ms
            Assert.Equal(3, slip);
            Assert.True(slip < 5);
            Assert.True(lat < 30_000);
            Assert.False(il.IsDisarmed);
        }

        // ── §2 허용 슬리피지(0.05%=5bps) 초과 → 0ms Disarm ──────────────────────
        [Fact]
        public void RecordFill_SlippageExceedsThreshold_TripsDisarm()
        {
            var (p, il) = NewProfiler(slippageThresholdBps: 5);
            p.RecordOrderSent(slot: 1, intendedPriceRaw: 1_000_000, sentTicks: 0);
            var (slip, _) = p.RecordFill(slot: 1, filledPriceRaw: 1_000_600, ackTicks: 100, brokerId: 0);   // 6bps > 5bps
            Assert.Equal(6, slip);
            Assert.True(il.IsDisarmed);
            Assert.False(il.CanEmit);
            Assert.True(il.BreachedSource(TelemetrySource.Slippage));
        }

        // ── §3 Order-to-Fill 지연 > 30ms → 0ms Disarm ───────────────────────────
        [Fact]
        public void RecordFill_LatencyExceeds30ms_TripsDisarm()
        {
            var (p, il) = NewProfiler(latencyThresholdMicros: 30_000);
            p.RecordOrderSent(slot: 2, intendedPriceRaw: 1_000_000, sentTicks: 0);
            var (_, lat) = p.RecordFill(slot: 2, filledPriceRaw: 1_000_000, ackTicks: 35_000, brokerId: 0);   // 35ms
            Assert.Equal(35_000, lat);
            Assert.True(il.IsDisarmed);
            Assert.True(il.BreachedSource(TelemetrySource.Latency));
        }

        // ── §4 미스테이징 슬롯 — fail-closed 판정 생략 ──────────────────────────
        [Fact]
        public void RecordFill_UnstagedSlot_SkipsJudgement()
        {
            var (p, il) = NewProfiler();
            var (slip, lat) = p.RecordFill(slot: 9, filledPriceRaw: 999_999, ackTicks: 999_999, brokerId: 0);
            Assert.Equal(0, slip); Assert.Equal(0, lat);
            Assert.False(il.IsDisarmed);
        }

        // ── §5 범위 밖 슬롯 — 예외 없이 무시 ─────────────────────────────────────
        [Fact]
        public void RecordOrderSent_And_RecordFill_OutOfRangeSlot_NoThrow()
        {
            var (p, _) = NewProfiler();
            p.RecordOrderSent(slot: 999, intendedPriceRaw: 100, sentTicks: 0);   // no-op(범위 밖)
            var (slip, lat) = p.RecordFill(slot: 999, filledPriceRaw: 100, ackTicks: 1, brokerId: 0);
            Assert.Equal(0, slip); Assert.Equal(0, lat);
        }

        // ── §6 히트맵 라우터 결속 — 체결 슬리피지가 브로커별 히스토그램에 적재 ────
        [Fact]
        public void RecordFill_FeedsHeatmapRouter_PerBroker()
        {
            var recorder = new AnomalyFlightRecorder();
            var il = new MultiTelemetryDisarmInterlock(recorder);
            il.Configure(30_000, 0, 500);   // 슬리피지 축은 사실상 비활성(500bps) — 히트맵 적재만 검증
            var heatmap = new SlippageForensicHeatmapRouter(brokerCount: 2, bucketCount: 4, bucketWidthBps: 5);
            var profiler = new ExecutionSlippageProfiler(64, il, heatmap, tickFrequency: 1_000_000);

            profiler.RecordOrderSent(0, 1_000_000, 0);
            profiler.RecordFill(0, 1_000_030, 100, brokerId: 1);   // 0.3bps → bucket 0, broker 1

            Assert.Equal(1, heatmap.SampleCount(1));
            Assert.Equal(0, heatmap.SampleCount(0));
        }

        // ── §7 재사용 슬롯 — 신규 RecordOrderSent 가 이전 스테이징을 덮어씀 ──────
        [Fact]
        public void RecordOrderSent_SlotReuse_OverwritesPreviousStaging()
        {
            var (p, il) = NewProfiler(slippageThresholdBps: 5);
            p.RecordOrderSent(slot: 3, intendedPriceRaw: 1_000_000, sentTicks: 0);
            p.RecordOrderSent(slot: 3, intendedPriceRaw: 2_000_000, sentTicks: 0);   // 재사용 — 새 의도가
            var (slip, _) = p.RecordFill(slot: 3, filledPriceRaw: 2_000_010, ackTicks: 10, brokerId: 0);
            Assert.True(slip < 5);   // 새 의도가(2,000,000) 기준으로 산출되어야 함(이전 값 잔존 시 대량 슬리피지로 오판)
            Assert.False(il.IsDisarmed);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §8 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class ExecutionProfilerConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public ExecutionProfilerConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly AnomalyFlightRecorder _hotRecorder = new();
        private static readonly MultiTelemetryDisarmInterlock _hotInterlock = new(_hotRecorder);
        private static readonly ExecutionSlippageProfiler _hotProfiler = new(1024, _hotInterlock, null, 1_000_000);
        private static long _hotSlot;

        static ExecutionProfilerConcurrencyTests() => _hotInterlock.Configure(1_000_000_000, 0, 1_000_000_000);   // 사실상 비활성(핫패스 δ=0만 측정)

        private static void RoundtripHot()
        {
            int slot = (int)(Interlocked.Increment(ref _hotSlot) % 1024);
            _hotProfiler.RecordOrderSent(slot, 1_000_000, 0);
            _hotProfiler.RecordFill(slot, 1_000_010, 100, brokerId: 0);
        }

        [Fact]
        public void RecordOrderSent_RecordFill_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) RoundtripHot();
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 20_000; i++) RoundtripHot();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★슬리피지/지연 산출 + 기존 인터록 Note() 결선 힙 0
        }
    }
}
