// ============================================================================
// Phase96SignalDemuxerTests — v11.5.1 Phase 94-N Part 3 Think-Beyond 적대적 검증
//   ① LockFreeSignalDemuxer — 다중 전송로 경합 하 킬 신호 무손실 병합 + 승자 결정론 + μs 전역 Disarm + δ=0.
//   ② PoisonedSecretBuffer — 소거 후 재참조(뒤늦은 async API 키 재사용) O(1) 예외 차단.
//   ③ 융합 — 데뮤서 → 킬스위치 브릿지 → 카나리 Disarm + Secret 중독 소거.
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class Phase96SignalDemuxerTests
    {
        private readonly ITestOutputHelper _out;
        public Phase96SignalDemuxerTests(ITestOutputHelper o) => _out = o;

        // ── §1 데뮤서 — 단일 신호 → Disarm·마스크·승자 ────────────────────────────
        [Fact]
        public void Demuxer_Single_Signal_Disarms_And_Records()
        {
            int fires = 0; long winner = 0;
            var d = new LockFreeSignalDemuxer(b => { fires++; winner = b; });
            Assert.False(d.IsDisarmed);

            d.Signal(KillTransport.NamedPipe);

            Assert.True(d.IsDisarmed);
            Assert.Equal(1, fires);
            Assert.Equal(1L << (int)KillTransport.NamedPipe, winner);
            Assert.Equal(KillTransport.NamedPipe, d.FirstTransport);
            Assert.True(d.Fired(KillTransport.NamedPipe));
            Assert.False(d.Fired(KillTransport.UsbButton));
            Assert.Equal(1, d.FiredCount);
        }

        // ── §2 다중 전송로 무손실 누적 + 승자 1회 ─────────────────────────────────
        [Fact]
        public void Demuxer_Multi_Transport_Accumulates_Mask_Fires_Once()
        {
            int fires = 0;
            var d = new LockFreeSignalDemuxer(_ => fires++);

            d.Signal(KillTransport.UsbButton);      // 최초 → 승자
            d.Signal(KillTransport.ComButton);
            d.Signal(KillTransport.MemoryMappedFile);
            d.Signal(KillTransport.UsbButton);      // 중복(멱등)

            Assert.Equal(1, fires);                 // ★Disarm 격발은 정확히 1회
            Assert.Equal(KillTransport.UsbButton, d.FirstTransport);
            Assert.Equal(3, d.FiredCount);          // 서로 다른 전송로 3종 무손실 누적
            Assert.True(d.Fired(KillTransport.UsbButton));
            Assert.True(d.Fired(KillTransport.ComButton));
            Assert.True(d.Fired(KillTransport.MemoryMappedFile));
            Assert.False(d.Fired(KillTransport.Watchdog));
        }

        [Fact]
        public void Demuxer_Reset_Rearms()
        {
            var d = new LockFreeSignalDemuxer();
            d.Signal(KillTransport.Manual);
            Assert.True(d.IsDisarmed);
            d.Reset();
            Assert.False(d.IsDisarmed);
            Assert.Equal(0, d.FiredCount);
            Assert.Null(d.FirstTransport);
        }

        // ── §3 PoisonedSecretBuffer — 소거 후 재참조 예외 차단 ─────────────────────
        [Fact]
        public void Poisoned_Buffer_Read_Works_Then_Throws_After_Purge()
        {
            var buf = new PoisonedSecretBuffer(new byte[] { 0x11, 0x22, 0x33, 0x44 });
            Assert.False(buf.IsPoisoned);
            Assert.Equal(0x22, buf.ReadByte(1));                 // 소거 전 정상 read
            Span<byte> dst = stackalloc byte[4];
            buf.CopyTo(dst);
            Assert.Equal(0x33, dst[2]);

            buf.PurgeAndPoison();                                // ★소거 + 중독

            Assert.True(buf.IsPoisoned && buf.AllZero());
            Assert.Throws<ObjectDisposedException>(() => buf.ReadByte(1));   // ★재참조 예외
            byte[] tmp = new byte[4];
            Assert.Throws<ObjectDisposedException>(() => buf.CopyTo(tmp));
            Assert.Throws<ObjectDisposedException>(() =>
            {
                var _ = buf.AsSpan().Length;   // AsSpan 접근도 차단
            });
        }

        [Fact]
        public void Poisoned_Buffer_Dispose_Purges()
        {
            var buf = new PoisonedSecretBuffer(new byte[] { 9, 9, 9 });
            buf.Dispose();
            Assert.True(buf.IsPoisoned && buf.AllZero());
            Assert.Throws<ObjectDisposedException>(() => buf.ReadByte(0));
        }

        // ── §4 융합 — 데뮤서 → 킬스위치 브릿지 → 카나리 Disarm + Secret 중독 소거 ────
        [Fact]
        public void Fusion_Demuxer_Trips_Bridge_Disarms_Canary_Poisons_Secret()
        {
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            var secret = new PoisonedSecretBuffer(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
            bridge.Killed += _ => { canary.Kill(CanaryKillReason.Manual); secret.PurgeAndPoison(); };

            // 데뮤서의 최초 Disarm → 브릿지 팬인(전송로 승자를 사유로 매핑).
            var demux = new LockFreeSignalDemuxer(_ => bridge.Signal(KillSwitchReason.NamedPipe));
            Assert.True(canary.Arm(1_000_000L, true, true));
            Assert.True(canary.TryAuthorize(1000) > 0);          // 무장 중 인가 가능

            demux.Signal(KillTransport.NamedPipe);               // ★물리 전송로 격발

            Assert.True(demux.IsDisarmed);
            Assert.True(bridge.IsKilled && canary.IsKilled);     // 발주 게이트 Disarm
            Assert.True(bridge.Token.IsCancellationRequested);   // 토큰 취소
            Assert.True(secret.IsPoisoned && secret.AllZero());  // ★Secret 중독 소거
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));   // 재참조 차단
            for (int i = 0; i < 500; i++) Assert.Equal(0L, canary.TryAuthorize(1L));   // 발주 0
        }

        // ── §5 뒤늦은 async 태스크의 소거-후 재참조 → 예외(권한 탈취 창 봉쇄) ────────
        [Fact]
        public async Task Poisoned_Buffer_Late_Async_Reference_Throws()
        {
            var secret = new PoisonedSecretBuffer(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var gate = new ManualResetEventSlim(false);

            // 소거 '이전' 발사된 async 발주 태스크가 잔여 참조를 붙든 채 뒤늦게 재-read 시도.
            var late = Task.Run(() =>
            {
                gate.Wait(5000);                                 // 소거 이후까지 대기(뒤늦은 재참조 모사)
                secret.CopyTo(new byte[8]);                      // ★소거 후 재참조 → 예외 기대
            });

            secret.PurgeAndPoison();                             // 킬스위치 소거
            gate.Set();

            var ex = await Assert.ThrowsAsync<ObjectDisposedException>(() => late);
            Assert.Contains("재참조", ex.Message);
        }
    }

    // ── §6 적대검증(동시·전용 스레드·δ=0) ──────────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Phase96SignalDemuxerConcurrencyTests
    {
        // ★다중 라이터(전 전송로 동시 폭주)에도 승자는 정확히 1, 마스크는 무손실(전 비트 OR).
        [Fact]
        public void Concurrent_MultiWriter_No_Signal_Lost_Single_Winner()
        {
            long fires = 0;
            var demux = new LockFreeSignalDemuxer(_ => Interlocked.Increment(ref fires));
            var transports = new[]
            {
                KillTransport.UsbButton, KillTransport.ComButton, KillTransport.NamedPipe,
                KillTransport.MemoryMappedFile, KillTransport.Watchdog, KillTransport.Manual,
            };
            const int perTransport = 16;
            var gate = new ManualResetEventSlim(false);
            var ths = new Thread[transports.Length * perTransport];
            int idx = 0;
            foreach (var t in transports)
                for (int k = 0; k < perTransport; k++)
                {
                    var tt = t;
                    ths[idx++] = new Thread(() => { gate.Wait(); demux.Signal(tt); }) { IsBackground = true };
                }
            foreach (var th in ths) th.Start();
            gate.Set();                                          // 96개 신호 동시 방출
            foreach (var th in ths) Assert.True(th.Join(30_000));
            gate.Dispose();

            Assert.Equal(1L, Interlocked.Read(ref fires));       // ★Disarm 격발 정확히 1회(경합 승자 유일)
            Assert.True(demux.IsDisarmed);
            Assert.Equal(transports.Length, demux.FiredCount);   // ★전 전송로 무손실 누적(신호 유실 0)
            foreach (var t in transports) Assert.True(demux.Fired(t));
            Assert.NotNull(demux.FirstTransport);                // 승자 확정
        }

        // ★Signal 핫패스(최초 격발 이후 반복) 힙 할당 0(δ=0).
        [Fact]
        public void Demuxer_Signal_HotPath_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var demux = new LockFreeSignalDemuxer(_ => { });
            demux.Signal(KillTransport.UsbButton);               // 최초 격발(콜백 1회) — 이후는 멱등 핫패스
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) demux.Signal(KillTransport.UsbButton);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++)
                    {
                        demux.Signal(KillTransport.ComButton);   // 원자 OR + 분기(할당 0)
                        if (demux.Fired(KillTransport.ComButton) && demux.FiredCount < 0) err = new Exception("unreachable");
                    }
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);                             // ★δ=0
        }
    }
}
