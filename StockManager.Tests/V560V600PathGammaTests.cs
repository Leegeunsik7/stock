// ============================================================================
// V560V600PathGammaTests — v5.6.0 결정론 플레이백 + v6.0.0 RIO 커널 바이패스 + ★CausalWeave
//   적대적 검증(δ=0 무할당 · liveArmed:false 무-leak · 타임-와프 퓨즈 · 결정론 디코드 · 증명-드롭).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class V560V600PathGammaTests
    {
        private long _noleakCount;

        // ── 합성 .ms2log 빌더(테스트 셋업·힙 할당 무관) — 단일 프레임 flag=0(stored)+XOR+CRC32 ──
        private const long Ms2LogSeedBase = unchecked(0x4D53_3245_4C4F_4700L);

        private static byte[] BuildMs2Log(BlittableLogEvent[] evs)
        {
            int n = evs.Length, rawLen = n * 32;
            var payload = new byte[rawLen];
            MemoryMarshal.AsBytes<BlittableLogEvent>(evs).CopyTo(payload);
            long seed = unchecked(Ms2LogSeedBase ^ n);
            Xor(payload, seed);                                   // 온디스크 payload(=de-XOR 대상)
            uint crc = Crc32.Compute(payload);
            var file = new byte[13 + rawLen];
            file[0] = 0;                                          // flag=0(stored)
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(1), rawLen);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(5), rawLen);   // payloadLen==rawLen
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(9), crc);
            payload.CopyTo(file.AsSpan(13));
            return file;
        }

        private static void Xor(Span<byte> data, long seed)
        {
            ulong k = unchecked((ulong)seed);
            for (int i = 0; i < data.Length; i++)
            {
                k = unchecked(k * 6364136223846793005UL + 1442695040888963407UL);
                data[i] ^= (byte)(k >> 56);
            }
        }

        // ── 합성 .ms2log 빌더(flag=1·Brotli 압축) — 프로덕션 로거 기본 경로(압축 후 XOR) 모사 ──
        private static byte[] BuildMs2LogBrotli(BlittableLogEvent[] evs)
        {
            int n = evs.Length, rawLen = n * 32;
            var raw = new byte[rawLen];
            MemoryMarshal.AsBytes<BlittableLogEvent>(evs).CopyTo(raw);
            var comp = new byte[BrotliEncoder.GetMaxCompressedLength(rawLen)];
            if (!BrotliEncoder.TryCompress(raw, comp, out int compLen)) throw new InvalidOperationException("Brotli 압축 실패");
            var payload = new byte[compLen];
            comp.AsSpan(0, compLen).CopyTo(payload);
            long seed = unchecked(Ms2LogSeedBase ^ n);
            Xor(payload, seed);                                   // 로거 순서: 압축 → XOR
            uint crc = Crc32.Compute(payload);
            var file = new byte[13 + compLen];
            file[0] = 1;                                          // flag=1(Brotli)
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(1), rawLen);
            BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(5), compLen);   // payloadLen==compLen
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(9), crc);
            payload.CopyTo(file.AsSpan(13));
            return file;
        }

        private static BlittableLogEvent[] MonotonicLog(int n, long qpc0 = 1000)
        {
            var evs = new BlittableLogEvent[n];
            for (int i = 0; i < n; i++) evs[i] = new BlittableLogEvent(i + 1, qpc0 + i, i * 7L, i & 0xFF, 1);
            return evs;
        }

        // ── 1) CausalWeaveRing 발행/독출 δ=0(무할당 핫패스) ──────────────────
        [Fact]
        public void CausalWeaveRing_PublishReadNext_ZeroAllocation()
        {
            var ring = new CausalWeaveRing(1024, 256);
            var src = new byte[64];
            var dst = new byte[256];
            for (int i = 0; i < 10_000; i++) { ring.Publish(src, 1000 + i); ring.TryReadNext(dst, out _, out _, out _, out _); }  // 워밍업
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++) { ring.Publish(src, 1000 + i); ring.TryReadNext(dst, out _, out _, out _, out _); }
            Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);   // 발행+독출 힙 0
        }

        // ── 2) 오버런: newest-wins 회수를 '증명 가능'하게 계수(커널 크래시·조용한 드롭 0) ──
        [Fact]
        public void CausalWeaveRing_Overrun_EvictsProvably()
        {
            var ring = new CausalWeaveRing(8, 64);
            var b = new byte[16];
            for (int i = 0; i < 40; i++) { BinaryPrimitives.WriteInt32LittleEndian(b, i); ring.Publish(b, 1000 + i); }  // 소비 없이 40회(cap 8)
            Assert.Equal(40L, ring.Published);
            Assert.True(ring.Evictions > 0, "오버런 회수가 증명 계수되어야 함");
            var dst = new byte[64];
            bool got = ring.TryReadNext(dst, out int len, out _, out _, out long gap);   // 크래시 없이 독출
            Assert.True(got);
            Assert.Equal(16, len);
            Assert.True(gap > 0, "회수로 건너뛴 시퀀스가 갭으로 보고되어야 함(유실 증명)");
        }

        // ── 3) ★인과 퓨즈: 재생 시퀀스 역행(재정렬/변조) → 트립 + fail-closed 이중 봉인(ForceFreeze) ──
        [Fact]
        public void CausalWeaveClock_SequenceRegression_TripsFuseAndForceFreeze()
        {
            var att = new OrderAttestationService();
            var clock = new CausalWeaveClock(() => 0L, att);
            clock.EnterPlayback(100);
            Assert.True(clock.AdvanceSeq(1));      // 단조 시퀀스 — 정상
            Assert.True(clock.AdvanceSeq(2));
            Assert.True(clock.AdvanceSeq(5));      // 갭(오버런 회수 등)은 정상(전진)
            Assert.False(clock.AdvanceSeq(3));     // ★시퀀스 역행(재정렬/손상/변조) → 퓨즈 트립·정지
            Assert.True(clock.FuseTripped);
            Assert.True(clock.WarpEvents >= 1);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // fail-closed 봉인 착지
            Assert.False(clock.AdvanceSeq(6));     // 트립 후 어떤 전진도 거부(비가역)
            Assert.False(clock.Advance(200));      // QPC 전진도 트립 상태에선 거부
        }

        // ── 3b) ★D7 오검출 회귀 방지: 봉인 QPC 국소 역전(다중 생산자 지터)은 퓨즈를 트립시키지 않는다 ──
        [Fact]
        public void CausalWeaveClock_QpcJitterWithMonotonicSeq_DoesNotFalselyTrip()
        {
            // 다중 생산자 로거는 seq 를 Interlocked 로 먼저 클레임한 뒤 QPC 를 샘플하므로, 정상 데이터에서도
            // 디스크 seq 순서와 QPC 순서가 어긋날 수 있다(변조 아님). 이때 퓨즈가 트립되어선 안 된다.
            var att = new OrderAttestationService();
            var clock = new CausalWeaveClock(() => 0L, att);
            clock.EnterPlayback(1000);
            Assert.True(clock.AdvanceSeq(1)); Assert.True(clock.Advance(1001));
            Assert.True(clock.AdvanceSeq(2)); Assert.True(clock.Advance(1000));   // ★QPC 역전(정상 지터) — 트립 없음
            Assert.True(clock.AdvanceSeq(3)); Assert.True(clock.Advance(1002));
            Assert.False(clock.FuseTripped);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 봉인 없음
        }

        // ── 4) .ms2log 결정론 디코드: 순서·값·무결성 정확 재현 ──────────────
        [Fact]
        public void Playback_DecodesMs2Log_InOrderDeterministic()
        {
            var evs = MonotonicLog(5, qpc0: 500);
            byte[] file = BuildMs2Log(evs);
            var clock = new CausalWeaveClock(() => 0L);
            using var sim = new StatePlaybackSimulator(clock);
            Assert.True(sim.LoadLogBytes(file, out _));
            Assert.Equal(5L, sim.TotalEvents);
            Assert.Equal(1.0, sim.IntegrityScore, 6);   // 전 프레임 CRC 통과

            var seqs = new List<long>();
            var qpcs = new List<long>();
            sim.RunToEnd((in PlaybackFrame f) => { seqs.Add(f.Seq); qpcs.Add(f.Qpc); });
            Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, seqs.ToArray());
            Assert.Equal(new long[] { 500, 501, 502, 503, 504 }, qpcs.ToArray());
            Assert.Equal(PlaybackState.Completed, sim.State);
            Assert.False(clock.FuseTripped);
        }

        // ── 5) 재생 핫패스 δ=0(초당 수십만 프레임에도 GC 가비지 0) ──────────
        [Fact]
        public void Playback_Replay_ZeroAllocation_HotPath()
        {
            byte[] file = BuildMs2Log(MonotonicLog(2000));
            var clock = new CausalWeaveClock(() => 0L);
            using var sim = new StatePlaybackSimulator(clock);
            PlaybackSink noop = static (in PlaybackFrame f) => { };
            sim.LoadLogBytes(file, out _); sim.RunToEnd(noop);   // 워밍업(tiered 안정화)
            sim.LoadLogBytes(file, out _); sim.RunToEnd(noop);

            sim.LoadLogBytes(file, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long n = sim.RunToEnd(noop);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(2000L, n);
            Assert.Equal(0L, delta);   // 디코드+2000 프레임 사출 힙 0
        }

        // ── 5b) ★D9 회귀 방지: Brotli(flag=1) .ms2log 재생 핫패스 δ=0(해제는 콜드 Load 로 이관) ──
        [Fact]
        public void Playback_Replay_ZeroAllocation_BrotliPath()
        {
            byte[] file = BuildMs2LogBrotli(MonotonicLog(2000));   // 프로덕션 로거 기본 경로(Brotli 압축)
            var clock = new CausalWeaveClock(() => 0L);
            using var sim = new StatePlaybackSimulator(clock);
            PlaybackSink noop = static (in PlaybackFrame f) => { };
            sim.LoadLogBytes(file, out _); sim.RunToEnd(noop);     // 워밍업(tiered JIT 안정화)
            sim.LoadLogBytes(file, out _); sim.RunToEnd(noop);

            sim.LoadLogBytes(file, out _);
            long before = GC.GetAllocatedBytesForCurrentThread();
            long n = sim.RunToEnd(noop);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(2000L, n);
            Assert.Equal(0L, delta);   // ★Brotli 프레임도 핫패스 힙 0(SafeBrotliDecoderHandle 할당은 콜드 Load 로 이관)
        }

        // ── 6) 재정렬/변조 오염: 재생 도중 시퀀스 역행 → fail-closed 정지(Faulted) ──
        [Fact]
        public void Playback_SequenceRegression_HaltsFaulted()
        {
            var evs = MonotonicLog(6, qpc0: 1000);
            // idx=3 의 Seq 를 직전보다 뒤로(재정렬/손상/변조 주입 — 진짜 인과 위반).
            evs[3] = new BlittableLogEvent(evs[2].Seq - 1, evs[3].TimestampTicks, evs[3].Arg, evs[3].Code, evs[3].ThreadId);
            byte[] file = BuildMs2Log(evs);
            var clock = new CausalWeaveClock(() => 0L);
            using var sim = new StatePlaybackSimulator(clock);
            Assert.True(sim.LoadLogBytes(file, out _));
            long n = sim.RunToEnd((in PlaybackFrame f) => { });
            Assert.Equal(3L, n);                          // 0,1,2 재현 후 idx3(seq 역행)에서 정지
            Assert.Equal(PlaybackState.Faulted, sim.State);
            Assert.True(clock.FuseTripped);
        }

        // ── 6b) ★D7 오검출 회귀 방지: 정상 로그의 QPC 국소 역전(다중 생산자 지터)은 재생을 중단시키지 않는다 ──
        [Fact]
        public void Playback_QpcJitterMonotonicSeq_CompletesWithoutFalseTrip()
        {
            var evs = MonotonicLog(6, qpc0: 1000);
            // idx=3 의 QPC 만 직전보다 뒤로(Seq 는 단조 유지) — 다중 생산자 정상 지터 모사.
            evs[3] = new BlittableLogEvent(evs[3].Seq, evs[2].TimestampTicks - 50, evs[3].Arg, evs[3].Code, evs[3].ThreadId);
            byte[] file = BuildMs2Log(evs);
            var clock = new CausalWeaveClock(() => 0L);
            using var sim = new StatePlaybackSimulator(clock);
            Assert.True(sim.LoadLogBytes(file, out _));
            long n = sim.RunToEnd((in PlaybackFrame f) => { });
            Assert.Equal(6L, n);                          // 전 프레임 재현(오검출 없음)
            Assert.Equal(PlaybackState.Completed, sim.State);
            Assert.False(clock.FuseTripped);
        }

        // ── 7) liveArmed:false 무-leak: 나노초 재생이 회전해도 실 아웃바운드 발주 0 ──
        [Fact]
        public void PathGamma_LiveArmedFalse_NoOrderLeak()
        {
            // 미무장 발주 게이트(liveArmed:false 자세). 플레이백/RIO 는 이 경로에 참조 0(구조적 격리).
            var ring = new OrderDispatchRing(1024);
            var disp = new GatedOrderDispatcher(ring, () => false);

            byte[] file = BuildMs2Log(MonotonicLog(1000));
            var clock = new CausalWeaveClock(() => 0L);
            using var sim = new StatePlaybackSimulator(clock);
            _noleakCount = 0;
            sim.LoadLogBytes(file, out _);
            sim.RunToEnd((in PlaybackFrame f) => Interlocked.Increment(ref _noleakCount));

            Assert.True(_noleakCount > 0);                    // 나노초 재생이 실제로 회전
            disp.Drain(_ => { });                              // 발주 링 드레인(반환 계약은 본 모듈 관심사 아님)
            Assert.True(ring.Published < 0L);                  // ★주문 링 미발행(-1 sentinel) — 플레이백 무접촉
            Assert.Equal(0L, disp.Forwarded);                  // ★실 송출 0바이트(무-leak 불변식)
            // 이중키∧MasterIgnited 전엔 어떤 주문도 불허(미무장/미점화).
            Assert.False(ZeroLeakProtocolIsolator.IsOrderSendAllowed(dualKeyArmed: false, IgnitionStage.Locked));
            Assert.False(ZeroLeakProtocolIsolator.IsOrderSendAllowed(dualKeyArmed: true, IgnitionStage.Locked));
            Assert.True(ZeroLeakProtocolIsolator.IsOrderSendAllowed(dualKeyArmed: true, IgnitionStage.MasterIgnited));
        }

        // ── 8) RIO 안심 강등(관리형 UDP 폴백) 인바운드 수신 → CausalWeaveRing 발행 ──
        [Fact]
        public void RioMesh_Fallback_ReceivesInboundFrame()
        {
            int port = FreeUdpPort();
            var clock = new CausalWeaveClock();
            var ring = new CausalWeaveRing(64, 512);
            using var mesh = new RioKernelBypassMesh(clock, ring, slotCount: 64, slotSize: 512);
            mesh.ActivateFallback(new IPEndPoint(IPAddress.Loopback, port));
            Assert.True(mesh.FallbackActive);
            Assert.False(mesh.RioBypassActive);

            byte[] payload = { 0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02 };
            using (var send = new UdpClient())
                for (int i = 0; i < 3; i++) send.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));

            var sw = Stopwatch.StartNew();
            while (mesh.FramesReceived == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert.True(mesh.FramesReceived > 0, "폴백 소켓이 loopback 프레임을 수신해야 함");

            var dst = new byte[512];
            bool got = ring.TryReadNext(dst, out int len, out long qpc, out _, out _);
            Assert.True(got);
            Assert.Equal(payload.Length, len);
            Assert.True(qpc > 0);
        }

        // ── 9) ★Path A: 폴백 IGMP 멀티캐스트 조인 + NIC 선택이 수신 파이프를 깨지 않고, 제로카피 인라인 파서 발화 ──
        [Fact]
        public void RioMesh_FallbackMulticast_JoinsWithoutBreakingReceive_AndZeroCopyParses()
        {
            int port = FreeUdpPort();
            var clock = new CausalWeaveClock();
            var ring = new CausalWeaveRing(64, 512);
            using var mesh = new RioKernelBypassMesh(clock, ring, slotCount: 64, slotSize: 512);

            var group = IPAddress.Parse("239.192.77.7");   // 관리 범위 멀티캐스트 그룹
            long parsed = 0, lastField = 0;
            RioZeroCopyParser parser = (frame, qpc) =>
            {
                if (RioKernelBypassMesh.TryParseL3Header(frame, out _, out _, out uint field))
                { Interlocked.Increment(ref parsed); Volatile.Write(ref lastField, field); }
            };
            // 0.0.0.0:port 바인딩 + IGMP 조인(NIC=loopback) + 제로카피 파서. 조인 실패는 비-치명(안심 강등·수신 계속).
            mesh.ActivateFallback(new IPEndPoint(IPAddress.Any, port), null, group, IPAddress.Loopback, parser);
            Assert.True(mesh.FallbackActive);

            // L3 헤더 프레임: [u16 len=8 | u8 type=3 | u8 flags | u32 field=0x0BADF00D]
            byte[] payload = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(payload, 8);
            payload[2] = 3;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), 0x0BADF00Du);
            using (var send = new UdpClient())
                for (int i = 0; i < 3; i++) send.Send(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));

            var sw = Stopwatch.StartNew();
            while (mesh.FramesReceived == 0 && sw.ElapsedMilliseconds < 3000) Thread.Sleep(10);
            Assert.True(mesh.FramesReceived > 0, "멀티캐스트 조인 후에도 유니캐스트 수신 파이프가 살아있어야 함");
            Assert.True(Interlocked.Read(ref parsed) > 0, "제로카피 인라인 파서가 발행 직전 발화해야 함");
            Assert.Equal(0x0BADF00DL, Volatile.Read(ref lastField));   // 스팬 직파싱 필드 정확 복호
            // 조인은 성공(MulticastJoined) 또는 사유 기록 후 강등 — fail-closed 아님·안심 강등(둘 중 하나 참).
            Assert.True(mesh.MulticastJoined || mesh.LastFallbackReason.Length > 0);
        }

        private static int FreeUdpPort()
        {
            using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }
    }
}
