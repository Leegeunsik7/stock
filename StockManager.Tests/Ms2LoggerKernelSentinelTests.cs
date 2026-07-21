// ============================================================================
// Ms2LoggerKernelSentinelTests — v5.5.0 영지식 로깅 패브릭 & 커널 지터 센티널 & I/O 역류 방화벽
//
//  §1 ZeroAllocAsyncCompressedLogger — 푸시 계수·영속·드롭-오래된-것.
//  §2 KernelJitterSpikeSentinel — 3σ/하드 임계 스파이크 → 가역 안전 홀드·자기 만료·지속 시 Downgrade.
//  §3 IoBackpressureFirewall — 디스크 stall → ForceFreeze·무stall 시 무동작.
//  §4 ★적대검증 — 로그 푸시 핫패스 δ=0·미무장 시 실주문 0(로거/홀드 동작 중에도).
//  §5 Cell Size 회귀 — v5.5.0 신규 8필드 seqlock 왕복.
// ============================================================================
using System;
using System.IO;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Ms2LoggerKernelSentinelTests
    {
        private static string TempLog() => Path.Combine(Path.GetTempPath(), $"ms2log_{Guid.NewGuid():N}.ms2log");

        // ════════════ §1 ZeroAllocAsyncCompressedLogger ════════════

        [Fact]
        public void Logger_PushesAndPersists_ToMs2LogFile()
        {
            var path = TempLog();
            try
            {
                using (var lg = new ZeroAllocAsyncCompressedLogger(path, capacity: 8192))
                {
                    lg.Start();
                    for (int i = 0; i < 5000; i++) lg.Log(code: 1, arg: i);
                    Assert.Equal(5000L, lg.PushedTotal);
                    Thread.Sleep(150);   // 백그라운드 드레인 여유
                }   // Dispose → 최종 드레인 + flush + native free
                Assert.True(File.Exists(path), "ms2log 파일 미생성");
                Assert.True(new FileInfo(path).Length > 0, "ms2log 빈 파일");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Logger_DropOldest_CountsWhenRingLaps()
        {
            // ★드롭-오래된-것: 소비 없이 capacity 초과 푸시 → 드레인 시 초과분(oldest) 드롭 계수(결정론·핫패스 무대기 실증).
            var path = TempLog();
            try
            {
                using var lg = new ZeroAllocAsyncCompressedLogger(path, capacity: 1024);   // Start 안 함(백그라운드 미간섭)
                for (int i = 0; i < 3000; i++) lg.Log(1, i);
                Assert.Equal(3000L, lg.PushedTotal);
                lg.DrainOnceForTest();
                Assert.Equal(3000L - 1024L, lg.DroppedTotal);   // 초과분 1976 드롭(oldest 덮어써짐)
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void Logger_WithinCapacity_ConsumesAllCommitted_NoFalseDrops()
        {
            // ★적대검증 회귀(D3): capacity 이내 커밋 이벤트는 전량 소비·오탐 드롭 0(3분기 루프가 커밋 슬롯을 건너뛰거나 유실하지 않음).
            var path = TempLog();
            try
            {
                using var lg = new ZeroAllocAsyncCompressedLogger(path, capacity: 4096);   // Start 안 함(결정론)
                for (int i = 0; i < 3000; i++) lg.Log(1, i);   // < capacity — 전량 커밋
                lg.DrainOnceForTest();
                Assert.Equal(3000L, lg.PushedTotal);
                Assert.Equal(0L, lg.DroppedTotal);   // ★유실/오탐 드롭 0
                Assert.Equal(0L, lg.QueueDepth);     // 전량 소비(tail==head)
                Assert.True(lg.FramesWritten > 0);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        // ════════════ §2 KernelJitterSpikeSentinel ════════════

        private static KernelJitterSpikeSentinel MakeSentinel(Func<long> clock, IgnitionLock? ign = null, int sustain = 5)
            => new KernelJitterSpikeSentinel(ign, blackbox: null, hardDeadlineMs: 2.0, holdMs: 200.0,
                   sustainThreshold: sustain, pollMs: 3_600_000, nowTicks: clock, tickFrequency: 1_000_000_000L);   // 1tick=1ns·타이머 1h(무간섭)

        [Fact]
        public void Sentinel_HardJitterSpike_ActivatesReversibleHold()
        {
            long clock = 1_000_000_000L;
            using var s = MakeSentinel(() => Volatile.Read(ref clock));
            for (int i = 0; i < 20; i++) s.EvaluateJitterForTest(1_000L);   // 베이스라인 1µs
            Assert.False(s.SafeHoldActive);
            s.EvaluateJitterForTest(5_000_000L);                            // 5ms > 2ms 하드 임계 → 스파이크
            Assert.True(s.SafeHoldActive);                                   // ★안전 홀드 활성
            Assert.True(s.JitterSpikes >= 1);
            clock += 250_000_000L;                                           // 250ms 경과(holdMs 200 초과)
            Assert.False(s.SafeHoldActive);                                  // ★가역·자기 만료
        }

        [Fact]
        public void Sentinel_SustainedJitter_EscalatesDowngrade()
        {
            long clock = 1_000_000_000L;
            var ign = new IgnitionLock();
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "sig"));
            using var s = MakeSentinel(() => Volatile.Read(ref clock), ign, sustain: 5);
            for (int i = 0; i < 5; i++) { s.EvaluateJitterForTest(5_000_000L); clock += 1_000_000L; }   // 연속 5회 스파이크
            Assert.True(s.Escalations >= 1);
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);   // ★지속 지터 → 강등
        }

        // ════════════ §3 IoBackpressureFirewall ════════════

        [Fact]
        public void Firewall_DiskStall_ForceFreezes()
        {
            var att = new OrderAttestationService();
            using var fw = new IoBackpressureFirewall(
                queueDepth: () => 100_000, millisSinceDrain: () => 2_000,   // 큐 고수위 + 진행 정체 2s
                attestation: att, queueHighWater: 32768, stallDeadlineMs: 1000, pollMs: 3_600_000);
            fw.CheckDiskStallForTest();
            Assert.True(fw.DiskStallFrozen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★디스크 목졸림 → 전역 동결(fail-closed)
            Assert.Equal(2L, fw.StatusCode);
        }

        [Fact]
        public void Firewall_NoStall_WhenQueueLow_StaysHealthy()
        {
            var att = new OrderAttestationService();
            using var fw = new IoBackpressureFirewall(
                queueDepth: () => 10, millisSinceDrain: () => 5000,        // 큐 낮음 → 정체여도 stall 아님
                attestation: att, queueHighWater: 32768, stallDeadlineMs: 1000, pollMs: 3_600_000);
            fw.CheckDiskStallForTest();
            Assert.False(fw.DiskStallFrozen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        // ════════════ §4 적대검증 — liveArmed:false ════════════

        [Fact]
        public void LiveArmed_LoggerActiveAndHeld_NoRealOrderWhenUnarmed()
        {
            // ★로거가 로그를 쏟아내고 지터 센티널이 홀드 중이어도, 미무장 GatedOrderDispatcher 는 실 송출 0.
            var path = TempLog();
            try
            {
                var att = new OrderAttestationService();
                using var lg = new ZeroAllocAsyncCompressedLogger(path, capacity: 4096);
                long clock = 1_000_000_000L;
                using var s = MakeSentinel(() => Volatile.Read(ref clock));
                s.EvaluateJitterForTest(5_000_000L);   // 안전 홀드 활성화
                Assert.True(s.SafeHoldActive);

                var ring = new OrderDispatchRing(64);
                ring.Publish(genomeId: 0, symbolCode: 5930, side: OrderSide.Buy, kind: 1,
                             price: new QPrice(100 * 100_000_000L), qty: new QQty(10 * 100_000_000L), timestampTicks: 0);
                for (int i = 0; i < 1000; i++) lg.Log(9, i);   // 로거 활동 중

                // 무장 술어가 안전 홀드를 존중(홀드 시 false) — 미무장이므로 애초에 false.
                var disp = new GatedOrderDispatcher(ring, isArmed: () => !s.SafeHoldActive && false, att);
                bool sent = false;
                disp.Drain(_ => sent = true);
                Assert.False(sent);                    // ★실 송출 0(콜백 미호출)
                Assert.Equal(0L, disp.Forwarded);
                Assert.True(disp.GatedOut >= 1);
            }
            finally { try { File.Delete(path); } catch { } }
        }

        // ════════════ §5 Cell Size 회귀 — v5.5.0 신규 8필드 seqlock 왕복 ════════════

        [Fact]
        public void RingTelemetry_V550Fields_RoundTripThroughSeqlock()
        {
            var ch = new RingTelemetryChannel();
            var t = new RingTelemetry(1, 2, 3, 4, 5, 6.0,
                logPersistBytesPerSec: 111, logQueueDepth: 222, logDroppedTotal: 333, logCompressionBps: 444,
                kernelJitterMicros: 555, kernelSafeHold: 1, nicDropsEstimate: 777, ioDefenseStatus: 2);
            ch.Publish(in t);
            Assert.True(ch.TryRead(out var got));
            Assert.Equal(111L, got.LogPersistBytesPerSec);   // ★Size 960 미상향이면 여기서 torn
            Assert.Equal(222L, got.LogQueueDepth);
            Assert.Equal(333L, got.LogDroppedTotal);
            Assert.Equal(444L, got.LogCompressionBps);
            Assert.Equal(555L, got.KernelJitterMicros);
            Assert.Equal(1L, got.KernelSafeHold);
            Assert.Equal(777L, got.NicDropsEstimate);
            Assert.Equal(2L, got.IoDefenseStatus);
        }
    }

    // ★타이밍·δ=0 스트레스 — 직렬화.
    [Collection("ConcurrencyStress")]
    public sealed class Ms2LoggerConcurrencyTests
    {
        [Fact]
        public void HotPath_LogPush_ZeroAllocation()
        {
            // ★적대검증(핵심): 초당 수십만 이진 로그 푸시에도 힙 할당 0(δ=0·언매니지드 링·Interlocked/포인터만).
            var path = Path.Combine(Path.GetTempPath(), $"ms2log_{Guid.NewGuid():N}.ms2log");
            long alloc = 0; Exception? err = null;
            try
            {
                using var lg = new ZeroAllocAsyncCompressedLogger(path, capacity: 65536);   // Start 안 함 — 푸시만 측정
                var th = new Thread(() =>
                {
                    try
                    {
                        for (int i = 0; i < 300_000; i++) lg.Log(7, i);   // 워밍업
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 100_000; i++) lg.Log(7, i);
                        alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                    }
                    catch (Exception e) { err = e; }
                });
                th.Start();
                Assert.True(th.Join(30_000));
                Assert.Null(err);
                Assert.Equal(0L, alloc);   // ★로그 푸시 핫패스 힙 0
            }
            finally { try { File.Delete(path); } catch { } }
        }
    }
}
