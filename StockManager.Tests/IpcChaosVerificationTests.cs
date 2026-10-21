// ============================================================================
// IpcChaosVerificationTests — v6.3.0 Task 2: IPC 적대적 카오스 검증
//   ① 비상 FREEZE 주입 → CausalWeaveClock 퓨즈 트립 + 비가역 fail-closed(모든 인과 전진 거부).
//   ② 파이프 강제 단선 → MMF Dual-Bus(pull) 무손실 폴백(최신 프레임 지속 수신) + 시퀀스 gap 감사.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class IpcChaosVerificationTests
    {
        private static void ReadExact(PipeStream s, byte[] buf)
        {
            int off = 0;
            while (off < buf.Length) { int n = s.Read(buf, off, buf.Length - off); if (n <= 0) throw new InvalidOperationException("EOF"); off += n; }
        }

        // ── ① 비상 FREEZE → 커널 비가역 봉인(모든 인과 전진 fail-closed 거부) ──
        [Fact]
        public void EmergencyFreeze_TripsFuse_FailClosed_Irreversible()
        {
            string pipe = "ms2e_chaos_" + Guid.NewGuid().ToString("N");
            string mmf  = "ms2e_chaosm_" + Guid.NewGuid().ToString("N");
            var att   = new OrderAttestationService();
            var clock = new CausalWeaveClock(() => 0L, att);
            using var server = new StockSuiteKernelTelemetryServer(
                () => new KernelFlags(true, true, true, 1),
                onFreeze: r => clock.TripFuse(r),   // FREEZE → 실 커널 비가역 봉인
                heartbeatMs: 30, pipeName: pipe, mmfName: mmf);
            server.Start();
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(3000);
                client.WriteByte(StockSuiteKernelTelemetryServer.CmdFreeze);   // ★IPC 로 FREEZE 주입
                client.Flush();

                var sw = Stopwatch.StartNew();
                while (!clock.FuseTripped && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);

                Assert.True(clock.FuseTripped);                        // 퓨즈 트립
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);    // fail-closed 봉인 착지
                Assert.True(server.FreezeCommands >= 1);

                // ★비가역 fail-closed: 트립 후 모든 인과 전진(링 발행 앵커·재생·시퀀스)이 영구 거부된다.
                clock.EnterPlayback(1000);
                Assert.False(clock.Advance(2000));       // 링/재생 전진 거부
                Assert.False(clock.AdvanceSeq(5000));    // 시퀀스 전진 거부
                clock.ExitPlayback();
                clock.EnterPlayback(9000);               // 재진입해도
                Assert.False(clock.Advance(10000));      // 여전히 봉인(자동복구 없음)
                Assert.True(clock.FuseTripped);
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            }
            finally { server.Dispose(); }
        }

        // ── ② 파이프 강제 단선 → MMF Dual-Bus 무손실 폴백(최신 프레임 지속 수신) ──
        [Fact]
        public void PipeKill_MmfDualBus_ZeroLossFallback()
        {
            string pipe = "ms2e_chaos_" + Guid.NewGuid().ToString("N");
            string mmf  = "ms2e_chaosm_" + Guid.NewGuid().ToString("N");
            using var server = new StockSuiteKernelTelemetryServer(
                () => new KernelFlags(true, true, true, 777), heartbeatMs: 25, pipeName: pipe, mmfName: mmf);
            server.Start();
            try
            {
                // 파이프 연결 → 프레임 수신(LIVE-PIPE)
                var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                client.Connect(3000);
                var buf = new byte[StockSuiteKernelTelemetryServer.FrameSize];
                ReadExact(client, buf);
                Assert.Equal(StockSuiteKernelTelemetryServer.FrameMagic, BinaryPrimitives.ReadUInt32LittleEndian(buf));

                // ★파이프 강제 Kill(송신 중 단선)
                client.Dispose();

                // ★MMF 이중버스 폴백: 서버는 파이프와 무관하게 MMF 를 계속 갱신 → 최신 프레임 무손실 지속 수신.
                var tracker = new MmAuditTracker();
                int fresh = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2500 && fresh < 4)
                {
                    if (StockSuiteKernelTelemetryServer.TryReadMmfSnapshot(out uint fl, out long fr, out long qp, out long seq, mmf))
                    {
                        Assert.Equal(7u, fl);      // 최신 상태(RIO|SIMD|MMF) 무손실
                        Assert.Equal(777L, fr);
                        Assert.True(qp > 0);
                        if (seq != tracker.LastSequence) { tracker.Observe(seq); fresh++; }
                    }
                    Thread.Sleep(40);
                }
                Assert.True(fresh >= 3, "파이프 단선 후에도 MMF 로 최신 프레임 지속 수신(zero-loss)");
                Assert.True(tracker.MaxSequence > 0, "MMF 시퀀스 전진 — 서버가 파이프 무관 상시 갱신");
                Assert.True(tracker.Observed >= 3);
            }
            finally { server.Dispose(); }
        }

        // ── ③ MmAuditTracker: 시퀀스 gap/리셋 정확 계수 ──
        [Fact]
        public void MmAuditTracker_CountsGaps_And_Resets()
        {
            var t = new MmAuditTracker();
            t.Observe(10); t.Observe(11); t.Observe(12);   // 연속 — gap 0
            Assert.Equal(0L, t.Gaps);
            Assert.Equal(3L, t.Observed);

            t.Observe(15);                                  // 12→15: 13,14 유실(2)
            Assert.Equal(2L, t.Gaps);
            Assert.Equal(1000L, new MmAuditTracker().IntegrityPermille);   // 빈 트래커=무손실

            t.Observe(3);                                   // 되감김(서버 재기동)
            Assert.Equal(1L, t.Resets);
            Assert.Equal(15L, t.MaxSequence);
            Assert.True(t.IntegrityPermille < 1000L);       // gap 발생 → 무결성 <100%
        }

        // ── ④ SubMicrosecondJitterAnalyzer: 지터 분포 + P99.9 꼬리 + δ=0 기록 ──
        [Fact]
        public void JitterAnalyzer_TracksTailAndSpike_And_ZeroAllocation()
        {
            var j = new SubMicrosecondJitterAnalyzer();
            long[] lat = { 100, 100, 100, 100, 100, 900, 100, 100 };   // 대부분 안정 + 800ns 스파이크 왕복
            foreach (var l in lat) j.Record(l);

            Assert.Equal(lat.Length - 1L, j.JitterSamples);   // 첫 표본 제외 = 7
            Assert.Equal(800L, j.MaxJitterNanos);             // 최대 지터(원시) 800
            Assert.InRange(j.PercentileNanos(0.50), 0, 8);    // 중앙 지터 ~0(대부분 안정)
            Assert.True(j.PercentileNanos(0.999) >= 512);     // ★P99.9 꼬리가 미세 스파이크 포착
            Assert.False(j.IsStable(100));                    // 100ns 임계 초과 → 불안정 조기 감지

            // δ=0 기록 핫패스(Double-Measure 하드닝)
            var j2 = new SubMicrosecondJitterAnalyzer();
            for (int i = 0; i < 20_000; i++) j2.Record(50 + (i & 31));   // 워밍업
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 300_000; i++) j2.Record(50 + (i & 31)); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));
        }
    }
}
