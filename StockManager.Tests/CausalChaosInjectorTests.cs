// ============================================================================
// CausalChaosInjectorTests — v6.1.0 Path B 적대적 검증
//   L3 마이크로-버스트 증명-드롭 · δ=0 무할당 · Time-Warp 퓨즈 트립(비가역·EmergencyFreeze) · 락프리 SPSC 무-데드락.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class CausalChaosInjectorTests
    {
        // ── 1) 마이크로-버스트 오버런: 소비 없는 폭주 → 정확한 증명 회수(Evictions == N - capacity)·데드락 없음 ──
        [Fact]
        public void MicroBurst_Overrun_EvictsExactlyAndProvably()
        {
            const int cap = 1024;
            var ring = new CausalWeaveRing(cap, 64);
            var inj  = new CausalChaosInjector(ring);
            long n   = 5_000_000;                        // 5백만발(무소비) — 10M MPS 폭주 설계의 검증 스케일

            var sw = Stopwatch.StartNew();
            var r  = inj.MicroBurst(n);
            sw.Stop();

            Assert.Equal(n, ring.Published);             // 전량 발행
            Assert.Equal(n, r.Published);
            Assert.Equal(n - cap, ring.Evictions);       // ★정확 계수(조용한 드롭 0·newest-wins)
            Assert.Equal(n - cap, r.Evictions);
            Assert.True(sw.ElapsedMilliseconds < 20_000, "폭주가 합리적 시간 내 완료(데드락/무한스핀 없음)");
        }

        // ── 2) 마이크로-버스트 핫패스 δ=0(초당 수백만 발에도 GC 가비지 0바이트) ──
        [Fact]
        public void MicroBurst_ZeroAllocation_HotPath()
        {
            var ring = new CausalWeaveRing(256, 64);
            var inj  = new CausalChaosInjector(ring);
            inj.MicroBurst(100_000);                     // 워밍업(tiered JIT 안정화)

            long delta = ZeroAllocProbe.MinAllocated(() =>
            {
                inj.MicroBurst(2_000_000);
            });

            Assert.Equal(0L, delta);                     // 주입 루프 힙 0(사전할당 스크래치 재사용)
        }

        // ── 3) ★Time-Warp 적대적 오염: 퓨즈 즉시 트립 + EmergencyFreeze + 비가역(자동복구 불가) ──
        [Fact]
        public void TimeWarp_Poison_TripsFuse_EmergencyFreeze_Irreversible()
        {
            var att   = new OrderAttestationService();
            var ign   = new IgnitionLock(attestation: att);
            var clock = new CausalWeaveClock(() => 0L, att, ign);   // 이중 봉인(ForceFreeze ∧ Downgrade) 배선
            var inj   = new CausalChaosInjector(new CausalWeaveRing(64, 64));

            var r = inj.InjectTimeWarp(clock, firstSeq: 10_000, forwardSteps: 50, stride: 10, regressionSeq: 9_000);

            Assert.True(r.FuseTripped);                             // 시퀀스 역행 즉시 트립
            Assert.True(clock.FuseTripped);
            Assert.Equal(50L, r.ForwardAccepted);                  // 단조 전진은 전부 수용
            Assert.True(r.WarpEvents >= 1);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);    // ★fail-closed 봉인 착지

            // ★비가역: 트립 후 어떤 전진도 영구 거부(재시작 전 자동복구 없음) — 재진입해도 봉인 유지.
            Assert.False(clock.Advance(1_000_000));
            clock.ExitPlayback();
            clock.EnterPlayback(2_000_000);
            Assert.False(clock.Advance(3_000_000));
            Assert.True(clock.FuseTripped);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);    // 여전히 동결
        }

        // ── 4) 락프리 SPSC: 생산자(주입) + 동시 소비자 → 데드락/torn/오염 없이 완주, 시퀀스 단조 소비 ──
        [Fact]
        public void MicroBurst_ConcurrentConsumer_NoDeadlock_NoTear()
        {
            var ring = new CausalWeaveRing(1024, 64);
            var inj  = new CausalChaosInjector(ring);
            long n   = 2_000_000;

            long consumed = 0, maxSeq = -1;
            bool tornOrCorrupt = false;
            var done = new ManualResetEventSlim(false);

            var consumer = new Thread(() =>
            {
                var dst = new byte[64];
                while (true)
                {
                    if (ring.TryReadNext(dst, out int len, out _, out long seq, out _))
                    {
                        if (len < 0 || len > 64) tornOrCorrupt = true;   // 오염 길이
                        if (seq < maxSeq) tornOrCorrupt = true;          // SPSC 단조 소비 위반
                        maxSeq = seq; consumed++;
                    }
                    else if (done.IsSet) break;                          // 생산 종료 + 드레인 완료 → 종료
                    else Thread.SpinWait(16);
                }
            }) { IsBackground = true, Name = "ChaosConsumer" };
            consumer.Start();

            var r = inj.MicroBurst(n);
            done.Set();

            Assert.True(consumer.Join(20_000), "소비자 완주(데드락 없음)");
            Assert.Equal(n, ring.Published);
            Assert.True(r.Evictions >= 0);
            Assert.False(tornOrCorrupt, "torn read/시퀀스 역행/오염 길이 없음(락프리 무결)");
            Assert.True(consumed > 0, "소비자가 실제로 프레임을 읽음");
        }

        // ── 5) 제로카피 L3 헤더 파싱(무할당 스팬 직파싱) — 마이크로-버스트 프레임 헤더 정확 복호 ──
        [Fact]
        public void ZeroCopy_L3HeaderParse_DecodesFrameSpan()
        {
            // 합성 프레임: [u16 len=48 | u8 type=7 | u8 flags | u32 field=0xCAFEBABE]
            Span<byte> frame = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(frame, 48);
            frame[2] = 7;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(frame.Slice(4), 0xCAFEBABEu);

            bool ok = RioKernelBypassMesh.TryParseL3Header(frame, out ushort len, out byte type, out uint field);
            Assert.True(ok);
            Assert.Equal((ushort)48, len);
            Assert.Equal((byte)7, type);
            Assert.Equal(0xCAFEBABEu, field);

            Assert.False(RioKernelBypassMesh.TryParseL3Header(stackalloc byte[4], out _, out _, out _));  // 미달 헤더 거부
        }
    }
}
