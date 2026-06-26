using System;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // v4.63.0 — 다중 seqlock 텔레메트리 통합: ScenarioSeqlockMonitor 라운드트립 +
    //   RingTelemetryPublisher 가 시나리오 카운터를 RingTelemetry 에 합류시켜 채널로 발행하는지 박제.
    public sealed class S29eMultiSeqlockTelemetryTests
    {
        [Fact]
        public void ScenarioMonitor_Report_RoundTrips()
        {
            var mon = new ScenarioSeqlockMonitor();
            Assert.False(mon.HasReported);
            Assert.Equal(0L, mon.Version);

            mon.Report(writerCollisions: 7, frozen: 2, version: 13);
            Assert.Equal(7L, mon.WriterCollisions);
            Assert.Equal(2L, mon.Frozen);
            Assert.Equal(13L, mon.Version);
            Assert.True(mon.HasReported);

            mon.Report(9, 2, 20);   // 최신 보고가 덮어씀
            Assert.Equal(9L, mon.WriterCollisions);
            Assert.Equal(20L, mon.Version);
        }

        [Fact]
        public void Publisher_WithScenarioMonitor_MergesScenarioCounters_IntoFrame()
        {
            var ring       = new OrderDispatchRing(8);
            var dispatcher = new GatedOrderDispatcher(ring, () => false);
            var channel    = new RingTelemetryChannel();
            var mon         = new ScenarioSeqlockMonitor();
            long ticks = 0;
            var pub = new RingTelemetryPublisher(ring, dispatcher, channel, () => ticks += 1_000_000, mon);

            // 시나리오 페이지가 보고한 경합/동결/버전.
            mon.Report(writerCollisions: 5, frozen: 1, version: 42);

            ring.Publish(1, 1, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), 100);
            pub.Sample();

            Assert.True(channel.TryRead(out var f));
            Assert.Equal(5L, f.ScenarioWriterCollisions);   // ★시나리오 seqlock 카운터 합류
            Assert.Equal(1L, f.ScenarioFrozen);
            Assert.Equal(42L, f.ScenarioVersion);
            Assert.True(f.ScenarioActive);
            Assert.True(f.HasAlert);                        // 시나리오 동결>0 → 경보
        }

        [Fact]
        public void Publisher_WithoutScenarioMonitor_ScenarioFieldsZero_BackwardCompat()
        {
            // 모니터 미주입(기존 3-인자 ctor) → 시나리오 필드 0·ScenarioActive false(후방호환).
            var ring       = new OrderDispatchRing(8);
            var dispatcher = new GatedOrderDispatcher(ring, () => false);
            var channel    = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, dispatcher, channel);

            ring.Publish(1, 1, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), 100);
            pub.Sample();

            Assert.True(channel.TryRead(out var f));
            Assert.Equal(0L, f.ScenarioWriterCollisions);
            Assert.Equal(0L, f.ScenarioVersion);
            Assert.False(f.ScenarioActive);
        }
    }
}
