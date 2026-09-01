// ============================================================================
// StagingFieldAuditSimulationTests — v11.6.0 Phase 94-O Part 2
//   ★스테이징 실통신 24시간 연속 가동 감사(Field Audit) 시뮬레이션: 이중 브로커(키움 x86 COM / 토스 REST)의
//     지속 시세 스트림이 흐르는 동안 (a) liveArmed:false 가 절대 유지(미인가 발주 0), (b) 하드웨어 킬스위치 주입 시
//     즉각 인터럽트 + PoisonedSecretBuffer 오차 0 소거, (c) LOH 파편화/누수 없음(코얼레서 드레인 0-alloc + GC 센티널
//     오탐 0·스파이크 감속) 을 실증한다.
//   ※ '24h' 는 지속 스트림을 대표하는 대량 틱(압축 재현) — 타이밍/다중스레드 → [Collection("ConcurrencyStress")].
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class StagingFieldAuditSimulationTests
    {
        private readonly ITestOutputHelper _out;
        public StagingFieldAuditSimulationTests(ITestOutputHelper o) => _out = o;

        // ── §1 24h 이중 브로커 스트림 — liveArmed:false 절대 유지·미인가 발주 0 ─────────────
        [Fact]
        public void Field24h_DualBroker_Stream_Sustains_LiveArmed_False_Zero_Order_Leak()
        {
            var coalescer = new ZeroAllocDualBrokerCoalescer(1 << 14);
            var canary = new CanaryHealthSentinel();

            // ★물리 이중 키 미결속 → 무장 자체가 불가(liveArmed:false 봉인).
            Assert.False(canary.Arm(1_000_000L, valveWired: true, liveArmingWired: false));
            Assert.False(canary.Arm(1_000_000L, valveWired: false, liveArmingWired: true));
            Assert.False(canary.IsArmed);

            const int total = 1_000_000;   // 24h 지속 스트림 대표(키움+토스 합산)
            long unauthorized = 0, orderAttempts = 0, consumed = 0, run = 1;
            Exception? err = null;

            var consumer = new Thread(() =>
            {
                try
                {
                    var buf = new BrokerTick[512];   // 1회 할당(드레인은 0-alloc)
                    while (Volatile.Read(ref run) == 1 || coalescer.PendingCount > 0)
                    {
                        int n = coalescer.Drain(buf);
                        if (n > 0)
                        {
                            consumed += n;
                            // 시세 갱신마다 전략 발주 시도 — 미무장이면 전량 거부되어야 함.
                            Interlocked.Increment(ref orderAttempts);
                            if (canary.TryAuthorize(1L) > 0) Interlocked.Increment(ref unauthorized);
                        }
                    }
                }
                catch (Exception e) { err = e; }
            }) { IsBackground = true, Name = "ui-drain" };
            consumer.Start();

            void Producer(int broker)
            {
                for (int i = 0; i < total / 2; i++)
                {
                    var sw = new SpinWait();
                    while (!coalescer.Publish(broker, i, (long)i * 100, 1)) sw.SpinOnce();   // 무손실 생산
                }
            }
            var p0 = new Thread(() => Producer(0)) { IsBackground = true, Name = "kiwoom" };   // 키움 COM
            var p1 = new Thread(() => Producer(1)) { IsBackground = true, Name = "toss" };      // 토스 REST
            p0.Start(); p1.Start();
            Assert.True(p0.Join(120_000)); Assert.True(p1.Join(120_000));
            Volatile.Write(ref run, 0);
            Assert.True(consumer.Join(120_000));
            Assert.Null(err);

            _out.WriteLine($"[Field24h] published={coalescer.Published} consumed={consumed} dropped={coalescer.Dropped} orderAttempts={Interlocked.Read(ref orderAttempts)} unauthorized={Interlocked.Read(ref unauthorized)}");
            Assert.Equal(total, (int)coalescer.Published);
            Assert.Equal((long)total, consumed);                       // 무손실 스트림(24h 전량 전달)
            Assert.True(Interlocked.Read(ref orderAttempts) > 0);      // 발주 시도는 발생(부하 유효성)
            Assert.Equal(0L, Interlocked.Read(ref unauthorized));      // ★24h 내내 미인가 발주 0(liveArmed:false)
            Assert.False(canary.IsArmed);
        }

        // ── §2 스트리밍 도중 하드웨어 킬스위치 주입 — 즉각 인터럽트 + Secret 소거 ─────────────
        [Fact]
        public void Field24h_KillSwitch_Injection_Immediate_Disarm_And_Secret_Purge()
        {
            var coalescer = new ZeroAllocDualBrokerCoalescer(1 << 12);
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            var secret = new PoisonedSecretBuffer(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18 });   // 토스/키움 API Secret 모사
            bridge.Killed += _ => { canary.Kill(CanaryKillReason.Manual); secret.PurgeAndPoison(); };

            long run = 1, consumed = 0;
            var consumer = new Thread(() =>
            {
                var buf = new BrokerTick[256];
                while (Volatile.Read(ref run) == 1) consumed += coalescer.Drain(buf);
            }) { IsBackground = true };
            var producer = new Thread(() =>
            {
                for (int i = 0; i < 5_000_000 && Volatile.Read(ref run) == 1; i++) coalescer.Publish(i & 1, i, i, 1);
            }) { IsBackground = true };
            consumer.Start(); producer.Start();
            Thread.Sleep(120);   // 스트림 진행 중

            // ★스트리밍 도중 물리 킬스위치 주입 — 즉각 인터럽트.
            var sw = Stopwatch.StartNew();
            bridge.Signal(KillSwitchReason.HardwareButton);
            sw.Stop();

            Assert.True(bridge.IsKilled);
            Assert.True(bridge.Token.IsCancellationRequested);          // 모든 CancellationToken 취소
            Assert.True(canary.IsKilled);
            Assert.True(secret.IsPoisoned && secret.AllZero());         // ★API Secret 0x00 소거(오차 0)
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));   // 뒤늦은 재참조 차단
            Assert.True(sw.Elapsed.TotalMilliseconds < 50, $"disarm {sw.Elapsed.TotalMilliseconds}ms");

            Volatile.Write(ref run, 0);
            Assert.True(producer.Join(30_000)); Assert.True(consumer.Join(30_000));
            for (int i = 0; i < 1000; i++) Assert.Equal(0L, canary.TryAuthorize(1L));   // 킬 후 발주 0(sticky)
        }

        // ── §3 LOH/누수 래치 — 코얼레서 드레인 0-alloc + GC 센티널 오탐 0·스파이크 감속 ───────
        [Fact]
        public void Field24h_No_LOH_Leak_Coalescer_ZeroAlloc_And_GcSentinel_Latch()
        {
            // (a) 코얼레서 드레인 경로 0 Byte(장기 체공 LOH 파편화·누수 원천 차단) — 전용 스레드 측정.
            long alloc = 0; Exception? err = null;
            var coalescer = new ZeroAllocDualBrokerCoalescer(1 << 13);
            var th = new Thread(() =>
            {
                try
                {
                    Span<BrokerTick> buf = stackalloc BrokerTick[128];
                    for (int i = 0; i < 400_000; i++) { coalescer.Publish(i & 1, i, i, 1); if ((i & 127) == 0) coalescer.Drain(buf); }   // 워밍업
                    for (int i = 0; i < 4096; i++) coalescer.Publish(0, i, i, 1);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int k = 0; k < 200; k++) coalescer.Drain(buf);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★드레인 0 Byte

            // (b) GC 센티널: 24h 저압 스트림엔 오탐(false throttle) 0.
            var s = new LongRunningMemoryGcSentinel(gen2PerMinThreshold: 60, allocBytesPerSecThreshold: 500_000_000);
            long T = TimeSpan.TicksPerSecond;
            s.Sample(0, 0, 0);
            long cumAlloc = 0;
            for (int hour = 1; hour <= 24; hour++)
            {
                cumAlloc += 3600L * 2_000_000;   // 시간당 ~2MB/s(임계 훨씬 아래)
                s.Sample((long)hour * 3600 * T, 0, cumAlloc);
            }
            Assert.False(s.ShouldThrottle);
            Assert.Equal(0L, s.Breaches);   // ★24h 저압엔 감속/오탐 0

            // (c) 누수 스파이크(초당 800MB + Gen2 3회) 주입 → OOM '이전' 자율 감속 래치 작동.
            long baseT = 25L * 3600 * T;
            s.Sample(baseT, 0, cumAlloc);
            s.Sample(baseT + T, 3, cumAlloc + 800_000_000);
            Assert.True(s.ShouldThrottle);
            Assert.True(s.Breaches >= 1);
            _out.WriteLine($"[Field24h] gcSentinel breaches={s.Breaches} lastAllocBytesPerSec={s.LastAllocBytesPerSec:N0}");
        }
    }
}
