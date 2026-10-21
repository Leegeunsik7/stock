// ============================================================================
// Phase95HardwareKillSwitchTests — v11.5.0 하드웨어 킬스위치 & Think-Beyond 적대적 검증
//   ★킬스위치 격발 후 단 1건의 API 통신/발주 인가도 불가함을 100% 실증(liveArmed:false 물리적 봉인).
//   ★Think-Beyond: 0-레이턴시 스핀락 인터럽트 + 휘발성 메모리 퍼저(API Secret 0x00 소거).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase95HardwareKillSwitchTests
    {
        // ── §1 킬스위치 브릿지 — Disarm + 토큰 취소 + sticky ────────────────────
        [Fact]
        public void Signal_Kills_And_Cancels_Token()
        {
            var b = new HardwareKillSwitchBridge();
            var tok = b.Token;
            Assert.False(b.IsKilled);
            Assert.False(tok.IsCancellationRequested);

            b.Signal(KillSwitchReason.HardwareButton);

            Assert.True(b.IsKilled);
            Assert.True(tok.IsCancellationRequested);          // ★진행 중 퀀트/발주 토큰 취소
            Assert.Equal(KillSwitchReason.HardwareButton, b.LastReason);
        }

        [Fact]
        public void Killed_Event_Fires_Once_Idempotent()
        {
            var b = new HardwareKillSwitchBridge();
            int fires = 0; KillSwitchReason got = KillSwitchReason.None;
            b.Killed += r => { fires++; got = r; };
            b.Signal(KillSwitchReason.NamedPipe);
            b.Signal(KillSwitchReason.HardwareButton);         // 2차 신호 → 재격발 없음(sticky)
            Assert.Equal(1, fires);
            Assert.Equal(KillSwitchReason.NamedPipe, got);
        }

        [Fact]
        public void Rearm_Requires_Physical_DualKey()
        {
            var b = new HardwareKillSwitchBridge();
            b.Signal(KillSwitchReason.Watchdog);
            Assert.True(b.IsKilled);
            Assert.False(b.TryRearm(valveWired: false, liveArmingWired: true));   // ★한 키 결여 → 실패
            Assert.False(b.TryRearm(valveWired: true, liveArmingWired: false));
            Assert.True(b.IsKilled);                            // 여전히 sticky
            Assert.True(b.TryRearm(valveWired: true, liveArmingWired: true));     // 물리 이중 키 → 재무장
            Assert.False(b.IsKilled);
            Assert.False(b.Token.IsCancellationRequested);      // 새 토큰(미취소)
        }

        // ── §2 ★융합: 킬스위치 → CanaryHealthSentinel Disarm + Secret 소거 ──────
        [Fact]
        public void Fusion_KillSwitch_Disarms_Canary_And_Purges_Secrets()
        {
            var canary = new CanaryHealthSentinel();
            var purger = new VolatileMemoryPurger();
            var bridge = new HardwareKillSwitchBridge();
            bridge.Killed += _ => { canary.Kill(CanaryKillReason.Manual); purger.PurgeAll(); };

            Assert.True(canary.Arm(1_000_000L, valveWired: true, liveArmingWired: true));
            Assert.True(canary.TryAuthorize(1000) > 0);         // 무장 중 인가 가능
            var secret = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x13, 0x37 };   // API Secret 모사
            purger.Register(secret);

            bridge.Signal(KillSwitchReason.HardwareButton);      // ★킬스위치 격발

            Assert.True(canary.IsKilled);                        // 발주 게이트 Disarm
            Assert.True(purger.IsPurged && purger.AllZero);      // ★API Secret 0x00 소거
            foreach (var bb in secret) Assert.Equal(0, bb);      // 실 버퍼 소거 확인
        }

        [Fact]
        public void After_Kill_Zero_API_Authorization_And_Cancellation()
        {
            // ★핵심: 킬스위치 격발 후 단 1건의 발주 인가도 발생하지 않는다(liveArmed:false 100% 봉인).
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            bridge.Killed += _ => canary.Kill(CanaryKillReason.Manual);
            canary.Arm(1_000_000L, true, true);

            bridge.Signal(KillSwitchReason.HardwareButton);

            for (int i = 0; i < 2_000; i++)
                Assert.Equal(0L, canary.TryAuthorize(1L));       // ★발주 인가 전량 0(sticky)
            Assert.True(bridge.Token.IsCancellationRequested);   // API 통신 파이프라인 취소
        }

        // ── §3 Think-Beyond ① 스핀락 하드웨어 인터럽트 ──────────────────────────
        [Fact]
        public void SpinLock_RaiseInterrupt_PollOnce_Fires()
        {
            KillSwitchReason got = KillSwitchReason.None; int fires = 0;
            var spin = new HardwareInterruptSpinLock(r => { got = r; fires++; });
            Assert.False(spin.PollOnce());                       // 대기 신호 없음 → 무동작
            spin.RaiseInterrupt(KillSwitchReason.NamedPipe);
            Assert.True(spin.PollOnce());                        // ★인터럽트 감지 → 격발
            Assert.Equal(1, fires);
            Assert.Equal(KillSwitchReason.NamedPipe, got);
            Assert.False(spin.PollOnce());                       // 격발 후 플래그 클리어
            Assert.Equal(1L, spin.Fires);
        }

        // ── §4 Think-Beyond ② 휘발성 메모리 퍼저 ────────────────────────────────
        [Fact]
        public void Purger_Zeroes_Byte_And_Char_Secrets()
        {
            var p = new VolatileMemoryPurger();
            var kiwoom = new byte[] { 1, 2, 3, 4, 5 };
            var toss = "s3cr3t-key".ToCharArray();
            p.Register(kiwoom); p.Register(toss); p.Register((byte[]?)null!);   // null 안전
            Assert.Equal(2, p.RegisteredCount);
            Assert.False(p.AllZero);

            p.PurgeAll();

            Assert.True(p.IsPurged && p.AllZero);
            foreach (var b in kiwoom) Assert.Equal(0, b);
            foreach (var c in toss) Assert.Equal('\0', c);
        }
    }

    // ── §5 적대검증(전용 스레드·동시·δ=0) ──────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Phase95HardwareKillSwitchConcurrencyTests
    {
        [Fact]
        public void Dedicated_Thread_Fires_On_Interrupt_Bypassing_ThreadPool()
        {
            // ★전용 최고우선순위 스핀 스레드가 인터럽트를 스레드 풀 없이 즉시 감지·격발.
            long fired = 0;
            using var spin = new HardwareInterruptSpinLock(_ => Interlocked.Exchange(ref fired, 1));
            spin.Start();
            spin.RaiseInterrupt(KillSwitchReason.HardwareButton);

            var swWait = System.Diagnostics.Stopwatch.StartNew();
            while (Volatile.Read(ref fired) == 0 && swWait.ElapsedMilliseconds < 5000) Thread.SpinWait(64);
            Assert.Equal(1L, Volatile.Read(ref fired));          // 전용 스레드가 격발
            Assert.True(spin.Fires >= 1);
        }

        [Fact]
        public void Concurrent_Signal_KillSwitch_Single_Kill_No_Leak()
        {
            // ★다중 스레드 동시 킬 신호에도 정확히 1회 Kill(발주 인가 0·중복 격발 방어).
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            long killFires = 0;
            bridge.Killed += _ => { Interlocked.Increment(ref killFires); canary.Kill(CanaryKillReason.Manual); };
            canary.Arm(1_000_000L, true, true);

            const int threads = 12;
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
                ths[t] = new Thread(() => bridge.Signal(KillSwitchReason.HardwareButton));
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));

            Assert.Equal(1L, Volatile.Read(ref killFires));      // ★Killed 이벤트 정확히 1회
            Assert.True(canary.IsKilled);
            for (int i = 0; i < 500; i++) Assert.Equal(0L, canary.TryAuthorize(1L));   // 발주 0
        }

        [Fact]
        public void SpinLock_PollOnce_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var spin = new HardwareInterruptSpinLock(_ => { });
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) spin.PollOnce();   // 대기 없음 경로(δ=0)
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) spin.PollOnce();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
