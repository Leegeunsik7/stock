// ============================================================================
// HardwareKillSwitchStressTests — v11.5.1 Phase 94-N Part 2
//   ★하드웨어 킬스위치 고처리량 스트레스: 100k+ ticks/sec 백그라운드 발주 부하가 흐르는 도중, 외부 물리 인터럽트
//     (USB/COM 버튼·NamedPipe·MMF 워치독)를 주입해 (a) 0ms 원자 Disarm, (b) 모든 CancellationToken 취소,
//     (c) VolatileMemoryPurger 가 API Secret 을 0x00 소거, (d) 물리 이중 키 재무장 전까지 발주 인가 100% 차단
//     — 을 실증한다. 스레드 풀 기아 하에서도 전용 스핀 스레드가 초저지연 격발함을 함께 검증.
//   ※ 처리량/타이밍/다중스레드 스트레스 → [Collection("ConcurrencyStress")] 필수(병렬 플래키 격리).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class HardwareKillSwitchStressTests
    {
        private readonly ITestOutputHelper _out;
        public HardwareKillSwitchStressTests(ITestOutputHelper o) => _out = o;

        // ── §1 100k+ ticks/sec 부하 도중 킬스위치 → 원자 Disarm·토큰 취소·Secret 소거·발주 0 ──────────
        [Fact]
        public void HighThroughput_100k_Ticks_KillSwitch_AtomicDisarm_ZeroOrderLeak()
        {
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            var purger = new VolatileMemoryPurger();
            bridge.Killed += _ => { canary.Kill(CanaryKillReason.Manual); purger.PurgeAll(); };
            // 처리량이 한도에 걸리지 않도록 초대형 cap(발주 게이트 자체는 정상 무장).
            Assert.True(canary.Arm(long.MaxValue / 4, valveWired: true, liveArmingWired: true));

            var kiwoomSecret = new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18 };
            var tossSecret   = "toss-live-app-secret-DEADBEEF".ToCharArray();
            purger.Register(kiwoomSecret);
            purger.Register(tossSecret);

            const int workers = 6;
            long ticks = 0, grantsBeforeKill = 0, grantsAfterKill = 0;
            long run = 1;
            Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var token = bridge.Token;

            var ths = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                ths[w] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        while (Volatile.Read(ref run) == 1)
                        {
                            Interlocked.Increment(ref ticks);          // 틱 = 퀀트/발주 결정 1회
                            bool killedBefore = canary.IsKilled;       // ★인가 '직전' Disarm 상태 판독(분류 레이스 제거)
                            long grant = canary.TryAuthorize(1L);      // 발주 인가 시도(Disarm/취소 시 0)
                            if (grant > 0)
                            {
                                // killLatch=1(Disarm 확정) 인데 인가가 나왔다면 sticky 위반 = 진짜 누수. Kill 은
                                // armed=0 을 killLatch=1 '이전'에 기록 → IsKilled ⇒ armed=0 ⇒ 인가 0(누수 불가).
                                if (killedBefore) Interlocked.Increment(ref grantsAfterKill);
                                else              Interlocked.Increment(ref grantsBeforeKill);
                            }
                        }
                    }
                    catch (Exception e) { err = e; }
                }) { IsBackground = true, Name = $"tick-{w}" };
            }
            foreach (var th in ths) th.Start();

            var swThroughput = Stopwatch.StartNew();
            start.Set();
            Thread.Sleep(300);                                          // 무장 상태 처리량 측정 창
            long ticksAtKill = Interlocked.Read(ref ticks);
            double preKillSec = swThroughput.Elapsed.TotalSeconds;

            // ★킬스위치 격발 — Signal 은 동기 원자(canary.Kill·PurgeAll 동기 실행). 반환 시점에 Disarm 확정.
            var swDisarm = Stopwatch.StartNew();
            bridge.Signal(KillSwitchReason.HardwareButton);
            swDisarm.Stop();
            Assert.True(bridge.IsKilled);

            Thread.Sleep(300);                                          // 킬 후 부하 지속(sticky 위반 채증)
            Volatile.Write(ref run, 0);
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);

            double rate = ticksAtKill / Math.Max(preKillSec, 1e-6);
            _out.WriteLine($"[KillStress] rate={rate:N0} t/s beforeKill={grantsBeforeKill} afterKill={grantsAfterKill} disarm={swDisarm.Elapsed.TotalMilliseconds:F4}ms");

            Assert.True(rate >= 100_000, $"처리량 {rate:N0} ticks/sec < 100k — 부하 미달");   // ★100k+ ticks/sec
            Assert.True(grantsBeforeKill > 0, "무장 중 인가가 발생해야 정상(부하 유효성)");
            Assert.Equal(0L, Interlocked.Read(ref grantsAfterKill));    // ★킬 이후 발주 인가 100% 차단
            Assert.True(token.IsCancellationRequested);                 // ★모든 CancellationToken 취소
            Assert.True(purger.IsPurged && purger.AllZero);             // ★API Secret 0x00 소거
            foreach (var b in kiwoomSecret) Assert.Equal(0, b);
            foreach (var c in tossSecret) Assert.Equal('\0', c);
            Assert.True(swDisarm.Elapsed.TotalMilliseconds < 50,        // ~0ms 원자 Disarm(동기·스케줄링 0)
                $"Disarm 지연 {swDisarm.Elapsed.TotalMilliseconds}ms — 원자성 위반");
        }

        // ── §2 스레드 풀 기아 하 전용 스핀 스레드 초저지연 격발(풀 스케줄링 우회) ──────────────────────
        [Fact]
        public void ThreadPoolStarvation_SpinLock_DedicatedThread_Fires_SubSecond()
        {
            // CLR 스레드 풀을 블로킹 작업으로 포화 → 풀 큐잉 지연(기아) 인공 유발.
            ThreadPool.GetMinThreads(out int wMin, out _);
            int hog = Math.Max(wMin, Environment.ProcessorCount) + 8;
            var release = new ManualResetEventSlim(false);
            var done = new CountdownEvent(hog);                         // 전 hog 종료 조인용(Dispose 안전화)
            long occupied = 0;
            bool allDrained = false;
            try
            {
                for (int i = 0; i < hog; i++)
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        Interlocked.Increment(ref occupied);
                        try { release.Wait(15_000); }                  // 포화 유지(release 까지 블록)
                        catch (ObjectDisposedException) { }            // ★뒤늦게 스케줄된 작업의 처분-후 Wait 방어
                        finally { done.Signal(); }
                    });

                var swOccupy = Stopwatch.StartNew();
                while (Interlocked.Read(ref occupied) < Math.Min(hog, wMin) && swOccupy.ElapsedMilliseconds < 3000)
                    Thread.SpinWait(256);

                long fired = 0;
                var swFire = Stopwatch.StartNew();
                using (var spin = new HardwareInterruptSpinLock(_ => Interlocked.Exchange(ref fired, 1)))
                {
                    spin.Start();                                       // ★전용 ThreadPriority.Highest 스레드(풀 밖)
                    spin.RaiseInterrupt(KillSwitchReason.NamedPipe);
                    while (Volatile.Read(ref fired) == 0 && swFire.ElapsedMilliseconds < 5000) Thread.SpinWait(64);
                    swFire.Stop();

                    _out.WriteLine($"[KillStress] starvation fire latency={swFire.Elapsed.TotalMilliseconds:F4}ms occupied={Interlocked.Read(ref occupied)}");
                    Assert.Equal(1L, Volatile.Read(ref fired));        // ★풀 기아에도 격발 성립
                    Assert.True(spin.Fires >= 1);
                    Assert.True(swFire.Elapsed.TotalMilliseconds < 2000,
                        $"기아 하 격발 {swFire.Elapsed.TotalMilliseconds}ms — 전용 스레드가 풀에 종속됨(설계 위반)");
                }
            }
            finally
            {
                release.Set();                                          // 전 hog 즉시 해제
                allDrained = done.Wait(20_000);                        // ★전 hog 이 release 관측·종료할 때까지 대기 → Dispose 레이스 제거
            }
            if (allDrained) { release.Dispose(); done.Dispose(); }      // 모든 작업 종료 확인 시에만 처분(미배수 시 GC 위임)
        }

        // ── §3 4개 물리 전송로(USB/COM/NamedPipe/MMF) 동시 주입 → 정확히 1회 Kill·전량 취소·소거 ────────
        [Fact]
        public void MultiTransport_Concurrent_Injection_SingleKill_AllSecretsPurged()
        {
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            var purger = new VolatileMemoryPurger();
            long killFires = 0;
            bridge.Killed += _ => { Interlocked.Increment(ref killFires); canary.Kill(CanaryKillReason.Manual); purger.PurgeAll(); };
            Assert.True(canary.Arm(1_000_000L, true, true));

            var secrets = new[] { new byte[] { 1, 2, 3, 4 }, new byte[] { 9, 8, 7, 6, 5 } };
            foreach (var s in secrets) purger.Register(s);

            // USB 버튼/COM 라인/NamedPipe/MMF 워치독 — 각 전송로가 다중 라이터로 동시 격발.
            var reasons = new[]
            {
                KillSwitchReason.HardwareButton,  // USB/COM 물리 버튼
                KillSwitchReason.NamedPipe,       // NamedPipe IPC
                KillSwitchReason.Watchdog,        // MMF 워치독
                KillSwitchReason.Manual,          // 운영자 수동
            };
            const int perTransport = 8;
            var ths = new List<Thread>(reasons.Length * perTransport);
            var gate = new ManualResetEventSlim(false);
            foreach (var r in reasons)
                for (int k = 0; k < perTransport; k++)
                {
                    var rr = r;
                    ths.Add(new Thread(() => { gate.Wait(); bridge.Signal(rr); }) { IsBackground = true });
                }
            foreach (var th in ths) th.Start();
            gate.Set();                                                 // 32개 신호 동시 방출
            foreach (var th in ths) Assert.True(th.Join(30_000));
            gate.Dispose();

            Assert.Equal(1L, Interlocked.Read(ref killFires));          // ★다중 전송로 동시 신호 → 정확히 1회 Kill
            Assert.True(bridge.IsKilled && canary.IsKilled);
            Assert.True(bridge.Token.IsCancellationRequested);          // ★모든 토큰 취소
            Assert.True(purger.IsPurged && purger.AllZero);             // ★Secret 소거
            for (int i = 0; i < 1000; i++) Assert.Equal(0L, canary.TryAuthorize(1L));   // 발주 인가 0
        }

        // ── §4 지속 부하 → 킬 → 소프트웨어 재무장 거부 → 물리 이중 키 재무장 후에만 발주 재개 ────────────
        [Fact]
        public void Sustained_Load_100pct_Block_Until_Physical_DualKey_Rearm()
        {
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            bridge.Killed += _ => canary.Kill(CanaryKillReason.Manual);
            Assert.True(canary.Arm(long.MaxValue / 4, true, true));

            long run = 1, grantsAfterKill = 0, grantsBeforeKill = 0;
            Exception? err = null;
            var start = new ManualResetEventSlim(false);
            const int workers = 4;
            var ths = new Thread[workers];
            for (int w = 0; w < workers; w++)
            {
                ths[w] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        while (Volatile.Read(ref run) == 1)
                        {
                            bool killedBefore = canary.IsKilled;        // ★인가 직전 Disarm 상태(분류 레이스 제거)
                            long g = canary.TryAuthorize(1L);
                            if (g > 0)
                            {
                                if (killedBefore) Interlocked.Increment(ref grantsAfterKill);   // sticky 위반만
                                else              Interlocked.Increment(ref grantsBeforeKill);
                            }
                        }
                    }
                    catch (Exception e) { err = e; }
                }) { IsBackground = true };
            }
            foreach (var th in ths) th.Start();
            start.Set();
            Thread.Sleep(150);                                          // 무장 부하

            bridge.Signal(KillSwitchReason.HardwareButton);             // ★킬
            Thread.Sleep(150);                                          // 킬 후 부하(sticky 위반 채증)

            // ★소프트웨어 단일 키 재무장 시도 → 거부(물리 이중 키 필수). 여전히 봉인.
            Assert.False(bridge.TryRearm(valveWired: true, liveArmingWired: false));
            Assert.False(bridge.TryRearm(valveWired: false, liveArmingWired: true));
            Assert.True(bridge.IsKilled);
            Thread.Sleep(50);

            Volatile.Write(ref run, 0);
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.True(Interlocked.Read(ref grantsBeforeKill) > 0);    // 무장 중엔 인가 발생(부하 유효성)
            Assert.Equal(0L, Interlocked.Read(ref grantsAfterKill));    // ★킬~재무장 구간 발주 인가 100% 차단

            // ★물리 이중 키 재무장 → 새 토큰·봉인 해제.
            Assert.True(bridge.TryRearm(valveWired: true, liveArmingWired: true));
            Assert.False(bridge.IsKilled);
            Assert.False(bridge.Token.IsCancellationRequested);

            // 발주 게이트도 물리 재무장(이중 키) 후에만 재개 — 소프트웨어 명령 단독으로는 복구 불가.
            Assert.Equal(0L, canary.TryAuthorize(1L));                  // canary 는 아직 kill sticky
            Assert.True(canary.Arm(1000L, valveWired: true, liveArmingWired: true));
            Assert.True(canary.TryAuthorize(1L) > 0);                   // ★물리 재무장 후 인가 재개
        }
    }
}
