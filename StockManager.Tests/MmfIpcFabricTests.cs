// ============================================================================
// MmfIpcFabricTests — v4.94.0 과제3: MMF 제로레이턴시 IPC 패브릭 (unsafe·δ=0)
//
//  §1 라운드트립·빔·백프레셔    — 발행/소비 일치·빈 링·가득참(부패 아님).
//  §2 오염 방어 → ForceFreeze   — 라이터/상대 하트비트 20ms 단절·매직 파손 → 격리+동결.
//  §3 레이턴시(µs)·SPSC torn=0·δ=0 무할당·Dispose·liveArmed:false.
//
//  ★공유메모리/IPC/스핀 테스트는 부하·타이밍 민감 → ConcurrencyStress 컬렉션(직렬화)에 편입.
// ============================================================================
using System;
using System.IO.MemoryMappedFiles;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MmfIpcFabricTests
    {
        private sealed class MutClock { public long T; }
        private static long TotalBytes(int capacity, int maxPayload) => 384L + (long)capacity * (24 + maxPayload);

        // ── §1 라운드트립 · 빔 · 백프레셔 ──────────────────────────────────────

        [Fact]
        public void PublishConsume_RoundTrip_ExactBytes()
        {
            const string name = "MS2E_TEST_MMF_RT";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64);
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            Assert.True(w.TryPublish(payload));
            var dst = new byte[64];
            Assert.True(r.TryConsume(dst, out int len));
            Assert.Equal(5, len);
            Assert.Equal(payload, dst[..len]);
            Assert.Equal(1L, w.Published);
            Assert.Equal(1L, r.Consumed);
            Assert.False(w.IsCorrupted);
        }

        [Fact]
        public void Consume_Empty_ReturnsFalse_NoCorruption()
        {
            const string name = "MS2E_TEST_MMF_EMPTY";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 32);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 32);
            var dst = new byte[32];
            Assert.False(r.TryConsume(dst, out int len));
            Assert.Equal(0, len);
            Assert.False(r.IsCorrupted);
        }

        [Fact]
        public void Full_Backpressure_NotCorruption()
        {
            const string name = "MS2E_TEST_MMF_FULL";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 4, 8);
            var f = new byte[] { 9 };
            for (int i = 0; i < 4; i++) Assert.True(w.TryPublish(f));   // 용량 4 채움
            Assert.False(w.TryPublish(f));                              // 가득참
            Assert.False(w.IsCorrupted);                               // ★백프레셔는 부패 아님
        }

        // ── §2 오염 방어 → ForceFreeze ────────────────────────────────────────

        [Fact]
        public void WriterHeartbeatStale_Consume_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_HBSTALE";
            var att = new OrderAttestationService();
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, att, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, att, () => rc.T, TimeSpan.TicksPerSecond, 20);
            Assert.True(w.TryPublish(new byte[] { 1 }));       // 라이터 하트비트 = 1_000_000
            rc.T = 1_000_000 + 250_000;                        // 리더 시계 +25ms(> 20ms 임계=200_000 tick)
            var dst = new byte[64];
            Assert.False(r.TryConsume(dst, out _));
            Assert.True(r.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Contains("Corrupted", r.LastReason);
        }

        [Fact]
        public void PeerHeartbeatStale_CheckPeerAlive_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_PEER";
            var att = new OrderAttestationService();
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 32, att, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 32, att, () => rc.T, TimeSpan.TicksPerSecond, 20);
            r.Beat();                             // 리더 하트비트 = 1_000_000
            Assert.True(w.CheckPeerAlive());      // 아직 신선
            wc.T = 1_000_000 + 300_000;           // 라이터 시계 +30ms → 리더 하트비트 단절로 관측
            Assert.False(w.CheckPeerAlive());
            Assert.True(w.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void MagicCorruption_Consume_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_MAGIC";
            var att = new OrderAttestationService();
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, att);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, att);
            Assert.True(w.TryPublish(new byte[] { 7 }));
            // 공유메모리 매직(offset 0) 을 외부에서 파손.
            using (var raw = MemoryMappedFile.CreateOrOpen(name, TotalBytes(16, 64)))
            using (var acc = raw.CreateViewAccessor(0, 16))
                acc.Write(0, unchecked((long)0xDEADBEEFDEADBEEF));
            var dst = new byte[64];
            Assert.False(r.TryConsume(dst, out _));
            Assert.True(r.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Contains("Corrupted", r.LastReason);
        }

        [Fact]
        public void HeadCorruption_BoundsViolation_ForceFreeze()
        {
            const string name = "MS2E_TEST_MMF_BOUNDS";
            var att = new OrderAttestationService();
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, att);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, att);
            Assert.True(w.TryPublish(new byte[] { 3 }));
            // Head(offset 64) 를 불변식 위배 값(음수)으로 파손 → head>=tail 위반.
            using (var raw = MemoryMappedFile.CreateOrOpen(name, TotalBytes(16, 64)))
            using (var acc = raw.CreateViewAccessor(64, 8))
                acc.Write(0, -5L);
            var dst = new byte[64];
            Assert.False(r.TryConsume(dst, out _));
            Assert.True(r.IsCorrupted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ── §3 레이턴시 · SPSC torn=0 · δ=0 · Dispose · liveArmed ──────────────

        [Fact]
        public void Latency_ComputedInMicros()
        {
            const string name = "MS2E_TEST_MMF_LAT";
            var wc = new MutClock { T = 1_000_000 };
            var rc = new MutClock { T = 1_000_000 };
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64, null, () => wc.T, TimeSpan.TicksPerSecond, 20);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64, null, () => rc.T, TimeSpan.TicksPerSecond, 20);
            Assert.True(w.TryPublish(new byte[] { 1, 2 }));                  // pubTs = 1_000_000
            rc.T = 1_000_000 + 10 * TimeSpan.TicksPerMillisecond;           // +10ms(< 20ms 하트비트 임계)
            var dst = new byte[64];
            Assert.True(r.TryConsume(dst, out _));
            Assert.Equal(10_000L, r.LastLatencyMicros);                     // 10ms = 10,000µs
        }

        [Fact]
        public void Spsc_Concurrent_InOrder_NoTornNoLoss()
        {
            const string name = "MS2E_TEST_MMF_SPSC";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 1024, 32);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 1024, 32);
            const long N = 100_000;
            int torn = 0; long got = 0;

            var wt = new Thread(() =>
            {
                var buf = new byte[8];
                for (long i = 0; i < N; i++)
                {
                    BitConverter.TryWriteBytes(buf, i);
                    while (!w.TryPublish(buf)) { if (w.IsCorrupted) return; Thread.SpinWait(4); }   // 가득참 → 스핀
                }
            });
            var rt = new Thread(() =>
            {
                var dst = new byte[32];
                while (got < N)
                {
                    if (r.TryConsume(dst, out int len))
                    {
                        if (len != 8 || BitConverter.ToInt64(dst, 0) != got) Interlocked.Increment(ref torn);
                        got++;
                    }
                    else if (r.IsCorrupted) return;
                }
            });
            wt.Start(); rt.Start();
            Assert.True(wt.Join(60_000));
            Assert.True(rt.Join(60_000));
            Assert.False(w.IsCorrupted);
            Assert.Equal(0, torn);           // ★순서·무결성 보존(torn/loss/dup 0)
            Assert.Equal(N, got);
        }

        [Fact]
        public void HotLoop_PublishConsume_ZeroAllocation()
        {
            const string name = "MS2E_TEST_MMF_ALLOC";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 64, 32);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 64, 32);
            var frame = new byte[16];
            var dst   = new byte[32];
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 200_000; i++) { w.TryPublish(frame); r.TryConsume(dst, out _); }   // 워밍업
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long sink = 0;
                    for (int i = 0; i < 100_000; i++) { w.TryPublish(frame); if (r.TryConsume(dst, out int len)) sink += len; }
                    long after = GC.GetAllocatedBytesForCurrentThread();
                    GC.KeepAlive(sink);
                    alloc = after - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★unsafe 포인터 발행/소비 핫루프 힙 할당 0(δ=0)
        }

        [Fact]
        public void Dispose_ConcurrentWithHotOps_NoUseAfterFree()
        {
            // ★적대검증 회귀(결함C): Dispose 가 Beat/TryPublish 와 겹쳐도 인플라이트 드레인으로 언매핑 후 역참조(AccessViolation) 차단.
            const string name = "MS2E_TEST_MMF_DISPRACE";
            var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 64, 32);
            var frame = new byte[8];
            var stop = new bool[1];
            var hammer = new Thread(() =>
            {
                while (!Volatile.Read(ref stop[0])) { w.Beat(); w.TryPublish(frame); }
            });
            hammer.Start();
            Thread.Sleep(25);   // 잠시 해머링(hot 연산 인플라이트 확보)
            w.Dispose();        // ★해머링 중 Dispose — 드레인 후 언매핑, 크래시 없어야 함
            Volatile.Write(ref stop[0], true);
            Assert.True(hammer.Join(10_000));
            Assert.False(w.TryPublish(frame));   // Dispose 후 no-op
        }

        [Fact]
        public void Dispose_Idempotent_NoOpAfter()
        {
            const string name = "MS2E_TEST_MMF_DISPOSE";
            var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 32);
            w.Dispose();
            w.Dispose();   // 재호출 무해
            Assert.False(w.TryPublish(new byte[] { 1 }));   // Dispose 후 전 연산 false
        }

        [Fact]
        public void LiveArmed_FabricOpen_TransportsBytes_ButNeverEmitsOrder()
        {
            const string name = "MS2E_TEST_MMF_ARMED";
            using var w = new MmfIpcFabric(name, MmfFabricRole.Writer, 16, 64);
            using var r = new MmfIpcFabric(name, MmfFabricRole.Reader, 16, 64);
            // 주문처럼 보이는 프레임을 전송해도 패브릭은 '전송'만 — 외부 브로커 소켓 미오픈(intra-machine IPC).
            var orderLike = new byte[] { 10, 0, 0, 0, 1, 2, 3 };
            Assert.True(w.TryPublish(orderLike));
            var dst = new byte[64];
            Assert.True(r.TryConsume(dst, out _));
            Assert.False(w.IsCorrupted);
            // 실주문 송출 게이트는 여전히 수학적 0 — 패브릭 개통이 무장이 아니다.
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.False(DispatchArming.ResolvePredicate(wired: false, gate)());
        }
    }
}
