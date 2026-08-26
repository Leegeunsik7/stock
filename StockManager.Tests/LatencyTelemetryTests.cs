// ============================================================================
// LatencyTelemetryTests — v4.84.0 Path Gamma: 레이턴시/변동성 관제 텔레메트리 검증
//
//  §1 무할당 스냅샷(5) — blittable 크기·헬퍼(IpcWired/PongDeficit/µs·FxWired/decimal/상태명).
//  §2 seqlock 채널(4)  — 미발행 default 안정·발행 라운드트립·버전 단조·최신 우선.
//  §3 퍼블리셔(3)      — FX 급전 반영·IPC 미연결 0·null 채널 가드.
//  §4 FX 상태코드(5)   — 무표본 0·정상 0·스테일 1·3σ 2·비양수 2(ReadFxStats).
//
//  ★검증 핵심: UI 전달 경로가 무할당 seqlock 이며, 미배선은 전부 0(정직), 브레이커 개방 사유가
//    상태코드(0/1/2)로 정확히 노출됨을 박제.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace StockManager.Tests
{
    public sealed class LatencyTelemetryTests
    {
        // ── §1 무할당 스냅샷 ──────────────────────────────────────────────────

        [Fact]
        public void Snapshot_IsBlittable_57Bytes()
            => Assert.Equal(57, Unsafe.SizeOf<LatencySnapshot>());   // 7×long(56) + byte(1), Pack=1

        [Fact]
        public void Snapshot_Default_UnwiredAndNormal()
        {
            LatencySnapshot s = default;
            Assert.False(s.IpcWired);
            Assert.False(s.FxWired);
            Assert.False(s.FxBreakerOpen);
            Assert.False(s.HasAlert);
            Assert.Equal("Normal", s.FxBreakerStateName);
        }

        [Fact]
        public void Snapshot_Ipc_HelpersMap()
        {
            // RTT 12,345 ticks(100ns) → 1234.5µs, ping 100 / pong 93 → deficit 7.
            var s = new LatencySnapshot(12_345L, 20_000L, 100L, 93L, 0L, 0L, 0L, 0);
            Assert.True(s.IpcWired);
            Assert.Equal(1234.5, s.CurrentRttMicros, precision: 6);
            Assert.Equal(2000.0, s.PeakRttMicros, precision: 6);
            Assert.Equal(7L, s.PongDeficit);
            Assert.False(s.FxWired);
        }

        [Fact]
        public void Snapshot_Fx_DecimalConversion()
        {
            // 1330.50 raw, MA 1330.00 raw, σ 0.50 raw.
            var s = new LatencySnapshot(0L, 0L, 0L, 0L,
                133_050_000_000L, 133_000_000_000L, 50_000_000L, 0);
            Assert.True(s.FxWired);
            Assert.Equal(1330.50m, s.LastFxRate);
            Assert.Equal(1330.00m, s.FxMovingAverage);
            Assert.Equal(0.50m, s.FxSigma);
        }

        [Theory]
        [InlineData(0, "Normal", false)]
        [InlineData(1, "Stale", true)]
        [InlineData(2, "Violated", true)]
        public void Snapshot_BreakerStateNames(byte code, string name, bool open)
        {
            var s = new LatencySnapshot(0, 0, 0, 0, 0, 0, 0, code);
            Assert.Equal(name, s.FxBreakerStateName);
            Assert.Equal(open, s.FxBreakerOpen);
            Assert.Equal(open, s.HasAlert);
        }

        // ── §2 seqlock 채널 ───────────────────────────────────────────────────

        [Fact]
        public void Channel_NeverPublished_TryReadReturnsStableDefault()
        {
            var ch = new LatencyTelemetryChannel();
            Assert.Equal(0L, ch.Version);
            Assert.True(ch.TryRead(out var s));   // 버전 0(짝) = 안정 → true
            Assert.Equal(default, s);
        }

        [Fact]
        public void Channel_Publish_RoundTrips()
        {
            var ch = new LatencyTelemetryChannel();
            var snap = new LatencySnapshot(11L, 22L, 33L, 30L, 133_000_000_000L, 132_900_000_000L, 40_000_000L, 1);
            ch.Publish(snap);
            Assert.Equal(2L, ch.Version);
            Assert.True(ch.TryRead(out var got));
            Assert.Equal(snap.CurrentIpcRttTicks, got.CurrentIpcRttTicks);
            Assert.Equal(snap.LastFxRateRaw, got.LastFxRateRaw);
            Assert.Equal((byte)1, got.FxBreakerState);
        }

        [Fact]
        public void Channel_Version_MonotonicByTwo()
        {
            var ch = new LatencyTelemetryChannel();
            ch.Publish(default);
            ch.Publish(default);
            ch.Publish(default);
            Assert.Equal(6L, ch.Version);   // 3회 × +2
        }

        [Fact]
        public void Channel_LatestWins()
        {
            var ch = new LatencyTelemetryChannel();
            ch.Publish(new LatencySnapshot(1, 0, 0, 0, 0, 0, 0, 0));
            ch.Publish(new LatencySnapshot(999, 0, 0, 0, 0, 0, 0, 0));
            Assert.True(ch.TryRead(out var s));
            Assert.Equal(999L, s.CurrentIpcRttTicks);
        }

        // ── §3 퍼블리셔 ───────────────────────────────────────────────────────

        [Fact]
        public void Publisher_Samples_FxFed_IpcUnconnected()
        {
            long now = 0;
            var fx = new QFxCircuitBreaker(() => now);
            Assert.True(fx.Submit(1330m));
            Assert.True(fx.Submit(1331m));   // clock 고정 → gap 0 < stale, 수용

            using var bridge = new KiwoomBridgeClient();   // 미연결 → RTT/ping 0
            var ch = new LatencyTelemetryChannel();
            var pub = new LatencyTelemetryPublisher(ch, bridge, fx);
            pub.Sample();

            Assert.True(ch.TryRead(out var s));
            Assert.Equal(0L, s.TotalPingSent);              // IPC 미연결
            Assert.False(s.IpcWired);
            Assert.Equal(133_100_000_000L, s.LastFxRateRaw); // 1331 × 1e8
            Assert.True(s.FxWired);
            Assert.Equal(0.5m, s.FxSigma);                  // σ(1330,1331) = 0.5
            Assert.Equal((byte)0, s.FxBreakerState);
        }

        [Fact]
        public void Publisher_NoSources_PublishesZeroSnapshot()
        {
            var ch = new LatencyTelemetryChannel();
            var pub = new LatencyTelemetryPublisher(ch);   // bridge·fx 모두 null
            pub.Sample();
            Assert.True(ch.TryRead(out var s));
            Assert.Equal(default, s);                       // 전부 0 — UI '미배선' 표기
        }

        [Fact]
        public void Publisher_NullChannel_Throws()
            => Assert.Throws<ArgumentNullException>(() => new LatencyTelemetryPublisher(null!));

        // ── §4 FX 상태코드(ReadFxStats) ──────────────────────────────────────

        [Fact]
        public void ReadFxStats_NoSamples_AllZeroNormal()
        {
            var fx = new QFxCircuitBreaker(() => 0L);
            fx.ReadFxStats(out long last, out long ma, out long sigma, out byte state);
            Assert.Equal(0L, last);
            Assert.Equal(0L, ma);
            Assert.Equal(0L, sigma);
            Assert.Equal((byte)0, state);
        }

        [Fact]
        public void ReadFxStats_Fed_NormalState_WithStats()
        {
            long now = 0;
            var fx = new QFxCircuitBreaker(() => now);
            fx.Submit(1330m); fx.Submit(1331m);
            fx.ReadFxStats(out long last, out long ma, out long sigma, out byte state);
            Assert.Equal(133_100_000_000L, last);
            Assert.Equal(133_050_000_000L, ma);   // 평균 1330.5
            Assert.True(sigma > 0);
            Assert.Equal((byte)0, state);
        }

        [Fact]
        public void ReadFxStats_StaleTrip_State1()
        {
            long now = 0;
            var att = new OrderAttestationService();
            var fx = new QFxCircuitBreaker(() => now, att);
            Assert.True(fx.Submit(1330m));
            now = TimeSpan.TicksPerSecond + 1;         // >1s gap
            Assert.False(fx.Submit(1331m));            // 스테일 trip
            fx.ReadFxStats(out _, out _, out _, out byte state);
            Assert.Equal((byte)1, state);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ReadFxStats_SigmaTrip_State2()
        {
            long now = 0;
            var fx = new QFxCircuitBreaker(() => now);
            for (int i = 0; i < 12; i++) { now += TimeSpan.TicksPerSecond / 10; Assert.True(fx.Submit(i % 2 == 0 ? 1330.05m : 1329.95m)); }
            now += TimeSpan.TicksPerSecond / 10;
            Assert.False(fx.Submit(1335m));            // 3σ 급변동 trip
            fx.ReadFxStats(out _, out _, out _, out byte state);
            Assert.Equal((byte)2, state);
        }

        [Fact]
        public void ReadFxStats_NonPositiveTrip_State2()
        {
            var fx = new QFxCircuitBreaker(() => 0L);
            Assert.False(fx.Submit(-1m));
            fx.ReadFxStats(out _, out _, out _, out byte state);
            Assert.Equal((byte)2, state);
        }
    }
}
