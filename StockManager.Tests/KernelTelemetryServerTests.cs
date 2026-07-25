// ============================================================================
// KernelTelemetryServerTests — v6.3.0 IPC 텔레메트리 서버 검증
//   24B QPC 프레임 브로드캐스트 · MMF 이중버스 스냅샷 · 비상 FREEZE → CausalWeaveClock.TripFuse 라운드트립.
//   (고유 파이프/MMF 명으로 실 서버·병렬 충돌 격리)
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class KernelTelemetryServerTests
    {
        private static void ReadExact(PipeStream s, byte[] buf)
        {
            int off = 0;
            while (off < buf.Length) { int n = s.Read(buf, off, buf.Length - off); if (n <= 0) throw new InvalidOperationException("EOF"); off += n; }
        }

        // ── 24B QPC 프레임 브로드캐스트 + MMF 이중버스 스냅샷 ──
        [Fact]
        public void Server_Broadcasts24ByteFrame_WithQpc_And_MmfMirror()
        {
            string pipe = "ms2e_test_" + Guid.NewGuid().ToString("N");
            string mmf  = "ms2e_mmf_"  + Guid.NewGuid().ToString("N");
            using var server = new StockSuiteKernelTelemetryServer(
                () => new KernelFlags(true, true, true, 12345), heartbeatMs: 30, pipeName: pipe, mmfName: mmf);
            server.Start();
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
                client.Connect(3000);
                var buf = new byte[StockSuiteKernelTelemetryServer.FrameSize];
                ReadExact(client, buf);

                Assert.Equal(StockSuiteKernelTelemetryServer.FrameMagic, BinaryPrimitives.ReadUInt32LittleEndian(buf));
                Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4)));   // rio|simd|mmf
                Assert.Equal(12345L, BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8)));
                Assert.True(BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(16)) > 0);     // ★QPC 타임스탬프

                // ★MMF 이중버스 스냅샷 pull(파이프와 별개 채널).
                bool ok = false; uint flags = 0; long fr = 0, qp = 0;
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 3000 &&
                       !(ok = StockSuiteKernelTelemetryServer.TryReadMmfSnapshot(out flags, out fr, out qp, out _, mmf)))
                    Thread.Sleep(20);
                Assert.True(ok, "MMF 이중버스 스냅샷 독출 가능해야 함");
                Assert.Equal(7u, flags);
                Assert.Equal(12345L, fr);
                Assert.True(qp > 0);
            }
            finally { server.Dispose(); }
        }

        // ── 비상 FREEZE 커맨드 → 커널 비가역 봉인(CausalWeaveClock.TripFuse) ──
        [Fact]
        public void Server_FreezeCommand_TripsKernelFuse_Irreversible()
        {
            string pipe = "ms2e_test_" + Guid.NewGuid().ToString("N");
            string mmf  = "ms2e_mmf_"  + Guid.NewGuid().ToString("N");
            var att   = new OrderAttestationService();
            var clock = new CausalWeaveClock(() => 0L, att);
            using var server = new StockSuiteKernelTelemetryServer(
                () => new KernelFlags(true, false, true, 1),
                onFreeze: reason => clock.TripFuse(reason),   // ★실 커널 배선: FREEZE → TripFuse(비가역 봉인)
                heartbeatMs: 30, pipeName: pipe, mmfName: mmf);
            server.Start();
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.None);
                client.Connect(3000);
                client.WriteByte(StockSuiteKernelTelemetryServer.CmdFreeze);   // [0xFF] 비상 FREEZE 송신
                client.Flush();

                var sw = Stopwatch.StartNew();
                while (!clock.FuseTripped && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
                Assert.True(clock.FuseTripped);                        // ★비가역 하드웨어 봉인 집행
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // fail-closed 착지
                Assert.True(server.FreezeCommands >= 1);
            }
            finally { server.Dispose(); }
        }
    }
}
