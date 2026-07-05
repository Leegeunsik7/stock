// ============================================================================
// AlphaRejectionValveTests — 과제 3: 알파 공장 자기 제어 밸브 + 텔레메트리 앰버 경보
//
//  §1 밸브(6)      — 최소표본 미만 미트립·임계 초과 트립·임계 미만 미트립·롤링 축출·수동 트립·Reset.
//  §2 공장(2)      — Suspend 시 RunCycle 스킵·Resume 재개(부작용 없는 경로).
//  §3 텔레메트리(3) — Amber 판정·퍼블리셔 반영(suspended/rate)·미배선 0.
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class AlphaRejectionValveTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;

        public AlphaRejectionValveTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"alpha_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
        }
        public void Dispose() { _db.Dispose(); try { File.Delete(_dbPath); } catch { } }

        private static void Feed(AlphaRejectionValve v, int rejected, int accepted)
        {
            for (int i = 0; i < rejected; i++) v.Record(rejected: true);
            for (int i = 0; i < accepted; i++) v.Record(rejected: false);
        }

        // ── §1 밸브 ───────────────────────────────────────────────────────────

        [Fact]
        public void BelowMinSamples_NoTrip_EvenIfAllRejected()
        {
            var v = new AlphaRejectionValve(window: 1000, threshold: 0.95, minSamples: 100);
            Feed(v, rejected: 50, accepted: 0);   // 전부 거부지만 표본 50 < 100
            Assert.False(v.IsTripped);
            Assert.Equal(1.0, v.RecentRate, precision: 6);
            Assert.Equal(50, v.WindowCount);
        }

        [Fact]
        public void AboveThreshold_Trips()
        {
            var v = new AlphaRejectionValve(window: 1000, threshold: 0.95, minSamples: 100);
            Feed(v, rejected: 96, accepted: 4);   // 100 표본, 거부율 0.96 > 0.95
            Assert.True(v.IsTripped);
            Assert.Equal(0.96, v.RecentRate, precision: 6);
        }

        [Fact]
        public void AtOrBelowThreshold_NoTrip()
        {
            var v = new AlphaRejectionValve(window: 1000, threshold: 0.95, minSamples: 100);
            Feed(v, rejected: 95, accepted: 5);   // 0.95 — '초과' 아님(> 엄격)
            Assert.False(v.IsTripped);
        }

        [Fact]
        public void RollingWindow_EvictsOldest_LatchStays()
        {
            var v = new AlphaRejectionValve(window: 10, threshold: 0.5, minSamples: 4);
            Feed(v, rejected: 10, accepted: 0);   // 창 가득 거부 → 트립
            Assert.True(v.IsTripped);
            Assert.Equal(1.0, v.RecentRate, precision: 6);
            Feed(v, rejected: 0, accepted: 6);    // 최고참 거부 6건 축출 → 창=[4거부,6수용]
            Assert.Equal(0.4, v.RecentRate, precision: 6);   // 롤링 반영
            Assert.True(v.IsTripped);                         // ★래치 — Reset 전까지 유지
        }

        [Fact]
        public void ManualTrip_Sets()
        {
            var v = new AlphaRejectionValve();
            Assert.False(v.IsTripped);
            v.Trip();
            Assert.True(v.IsTripped);
        }

        [Fact]
        public void Reset_ClearsLatchAndWindow()
        {
            var v = new AlphaRejectionValve(window: 1000, threshold: 0.95, minSamples: 100);
            Feed(v, rejected: 100, accepted: 0);
            Assert.True(v.IsTripped);
            v.Reset();
            Assert.False(v.IsTripped);
            Assert.Equal(0, v.WindowCount);
            Assert.Equal(0.0, v.RecentRate, precision: 6);
        }

        // ── §2 공장 ───────────────────────────────────────────────────────────

        [Fact]
        public void Factory_Suspended_RunCycleSkips()
        {
            var att = new OrderAttestationService();
            using var f = new AutonomousAlphaFactory(_db, att);
            f.Suspend();
            Assert.True(f.IsSuspended);
            var r = f.RunCycle();          // 밸브 트립 → 스킵(부작용·파일 export 없음)
            Assert.Empty(r);
            Assert.Equal(0, f.CyclesCompleted);   // 사이클 미증가(조기 반환)
        }

        [Fact]
        public void Factory_Resume_ClearsSuspend()
        {
            var att = new OrderAttestationService();
            using var f = new AutonomousAlphaFactory(_db, att);
            f.Suspend();
            Assert.True(f.IsSuspended);
            f.Resume();
            Assert.False(f.IsSuspended);
            Assert.Equal(0, f.RecentWindowCount);
        }

        // ── §3 텔레메트리 앰버 ────────────────────────────────────────────────

        [Theory]
        [InlineData(1L, 0.0, 0L, true)]    // 일시정지 → 앰버(활성 무관)
        [InlineData(0L, 0.96, 5L, true)]   // 활성 + 거부율 0.96 → 앰버
        [InlineData(0L, 0.50, 5L, false)]  // 활성 + 거부율 0.50 → 정상
        [InlineData(0L, 0.99, 0L, false)]  // 미활성(cycles 0) → 앰버 아님(rate 무시)
        public void RingTelemetry_AmberJudgment(long suspended, double rate, long cycles, bool amber)
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0, 0, 0, 0, 0,
                alphaFactoryCycles: cycles, alphaFactorySigned: 0, alphaFactoryRejected: 0,
                alphaFactoryRecentRejRate: rate, alphaFactorySuspended: suspended);
            Assert.Equal(amber, t.AlphaFactoryAmber);
        }

        [Fact]
        public void Publisher_ReflectsSuspendState()
        {
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch   = new RingTelemetryChannel();
            using var f = new AutonomousAlphaFactory(_db, att);
            f.Suspend();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L, null, f);
            pub.Sample();
            Assert.True(ch.TryRead(out var t));
            Assert.Equal(1L, t.AlphaFactorySuspended);
            Assert.True(t.AlphaFactoryAmber);
        }
    }
}
