// ============================================================================
// MultiBrokerFailoverTests — v12.0.4 Phase 95-A Real-Broker Sandbox Binding & Multi-Broker Failover 적대검증
//   Part 3 이중 엔진(LockFreeBrokerSwitchboard·NativeInteropFaultGuard) + Part 2 BrokerFailoverSentinel 조립 실증:
//     · 주 브로커 단절/429/50x/슬리피지/COM HRESULT/프록시사망 → <10ms 원자 페일오버 + 0ms Disarm + 자격증명 0x00 소거
//     · 락프리 CAS 라우팅(스레드 블로킹/GC 0)·COM HRESULT 심각도 분류(미상=fail-closed)·프록시 하트비트 침묵 감지
//     · ★liveArmed:false: 샌드박스 피드 연결 상태에서도 물리 이중 키 미결속 → 실물 발주 100% Veto
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
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
    public sealed class MultiBrokerFailoverTests
    {
        private readonly ITestOutputHelper _out;
        public MultiBrokerFailoverTests(ITestOutputHelper o) => _out = o;

        private static PoisonedSecretBuffer NewSecret() =>
            new(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18 });

        // ── §1 스위치보드 — 초기 활성 + 강제 원자 전환 ──────────────────────────────
        [Fact]
        public void Switchboard_InitialActive_And_AtomicSwitch()
        {
            var b = new LockFreeBrokerSwitchboard(2, initialActive: 0);
            Assert.Equal(0, b.Active);
            Assert.Equal(0, b.SwitchCount);

            int prev = b.SwitchTo(1, nowTicks: 100);
            Assert.Equal(0, prev);
            Assert.Equal(1, b.Active);
            Assert.Equal(1, b.SwitchCount);
            Assert.Equal(100, b.LastSwitchTicks);

            b.SwitchTo(1, 200);                 // 동일 대상 → 카운트 증가 없음(no-op 전환)
            Assert.Equal(1, b.SwitchCount);
            Assert.Equal(100, b.LastSwitchTicks);

            b.SwitchTo(9, 300);                 // 범위 밖 → 무시
            Assert.Equal(1, b.Active);
        }

        // ── §2 스위치보드 — CAS 조건부 전환 ─────────────────────────────────────────
        [Fact]
        public void Switchboard_TrySwitch_CAS_Semantics()
        {
            var b = new LockFreeBrokerSwitchboard(3, 0);
            Assert.False(b.TrySwitch(expected: 1, next: 2, nowTicks: 10));   // 현재 0≠기대 1 → 실패
            Assert.Equal(0, b.Active);
            Assert.True(b.TrySwitch(0, 2, 20));                              // 현재 0=기대 0 → 성공
            Assert.Equal(2, b.Active);
            Assert.Equal(1, b.SwitchCount);
            Assert.True(b.TrySwitch(2, 2, 30));                              // next==expected → 성공하나 전환 카운트 불변
            Assert.Equal(1, b.SwitchCount);
        }

        // ── §3 COM HRESULT 심각도 분류(미상=fail-closed) ────────────────────────────
        [Fact]
        public void FaultGuard_ClassifyHResult_FailClosed()
        {
            Assert.Equal(ComFaultSeverity.Ok, NativeInteropFaultGuard.ClassifyHResult(0));               // S_OK
            Assert.Equal(ComFaultSeverity.Ok, NativeInteropFaultGuard.ClassifyHResult(1));               // 성공(양수)
            Assert.Equal(ComFaultSeverity.Transient, NativeInteropFaultGuard.ClassifyHResult(NativeInteropFaultGuard.RPC_E_SERVERCALL_RETRYLATER));
            Assert.Equal(ComFaultSeverity.Fatal, NativeInteropFaultGuard.ClassifyHResult(NativeInteropFaultGuard.RPC_E_DISCONNECTED));
            Assert.Equal(ComFaultSeverity.Fatal, NativeInteropFaultGuard.ClassifyHResult(NativeInteropFaultGuard.RPC_S_SERVER_UNAVAILABLE));
            Assert.Equal(ComFaultSeverity.Fatal, NativeInteropFaultGuard.ClassifyHResult(NativeInteropFaultGuard.STATUS_ACCESS_VIOLATION));   // AV 대리
            Assert.Equal(ComFaultSeverity.Fatal, NativeInteropFaultGuard.ClassifyHResult(unchecked((int)0x80040111)));   // ★미상 실패 → 보수적 Fatal
        }

        // ── §4 프록시 생존성 — 하트비트 침묵 감지 ────────────────────────────────────
        [Fact]
        public void ProxyLiveness_Detects_Silence()
        {
            long timeout = 3 * TimeSpan.TicksPerSecond;
            var probe = new ProxyLivenessProbe(timeout);
            Assert.False(probe.IsDead(1_000));                 // 미시작 → 사망 판정 보류(오탐 방지)

            probe.Beat(nowTicks: 10_000_000_000L);
            Assert.False(probe.IsDead(10_000_000_000L + timeout));           // 임계 이내 생존
            Assert.True(probe.IsDead(10_000_000_000L + timeout + 1));        // ★침묵 초과 → 사망
        }

        // ── §5 ComReleaseGuard — null/비-COM no-op ──────────────────────────────────
        [Fact]
        public void ComReleaseGuard_Null_And_NonCom_NoOp()
        {
            Assert.Equal(0, ComReleaseGuard.SafeFinalRelease(null));         // null → 0(무동작)
            Assert.Equal(-1, ComReleaseGuard.SafeFinalRelease(new object())); // 비-COM RCW → -1(대상 아님)
            Assert.Equal(-1, ComReleaseGuard.SafeFinalRelease("credential")); // 문자열도 비-COM
        }

        // ── §6 주 브로커 단절 → <10ms 원자 페일오버 + 0ms Disarm + 자격증명 소거 ───────
        [Fact]
        public void Failover_PrimaryDisconnect_SwitchesSecondary_Disarms_Poisons()
        {
            var board = new LockFreeBrokerSwitchboard(2, 0);   // 0=키움(주), 1=토스(보조)
            using var secret = NewSecret();
            var sentinel = new BrokerFailoverSentinel(board, credentials: secret);
            Assert.True(sentinel.CanEmit);                     // 장애 전 — 봉쇄 없음

            var sw = Stopwatch.StartNew();
            bool faulted = sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, nowTicks: 5000);
            sw.Stop();

            Assert.True(faulted);
            Assert.Equal(1, sentinel.ActiveBroker);            // ★보조(토스)로 원자 페일오버
            Assert.Equal(1, sentinel.FailoverCount);
            Assert.True(sentinel.IsDisarmed && !sentinel.CanEmit);   // ★주문 경로 fail-closed
            Assert.Equal(BrokerFaultKind.Disconnect, sentinel.LastFault);
            Assert.True(sentinel.CredentialsWiped);
            Assert.True(secret.IsPoisoned && secret.AllZero());
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));
            Assert.True(sw.Elapsed.TotalMilliseconds < 10, $"failover {sw.Elapsed.TotalMilliseconds}ms");
            _out.WriteLine($"[Failover] active→{sentinel.ActiveBroker} {sw.Elapsed.TotalMilliseconds:F4}ms disarmed={sentinel.IsDisarmed}");
        }

        // ── §7 429 문턱 이하 → 무장애, 이상 → 장애 ───────────────────────────────────
        [Fact]
        public void Failover_Http429_Threshold()
        {
            var sentinel = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0), http429Threshold: 5);
            Assert.False(sentinel.ReportFault(0, BrokerFaultKind.Http429, 4, 100));   // 4<5 → 무장애
            Assert.True(sentinel.CanEmit);
            Assert.True(sentinel.ReportFault(0, BrokerFaultKind.Http429, 5, 200));    // 5≥5 → 장애
            Assert.False(sentinel.CanEmit);
            Assert.Equal(1, sentinel.ActiveBroker);
        }

        // ── §8 COM HRESULT — Fatal 만 장애(Transient 불발) ──────────────────────────
        [Fact]
        public void Failover_ComHResult_OnlyFatal()
        {
            var s1 = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0));
            Assert.False(s1.ReportFault(0, BrokerFaultKind.ComHResult, NativeInteropFaultGuard.RPC_E_SERVERCALL_RETRYLATER, 100));   // Transient → 무장애
            Assert.True(s1.CanEmit);

            var s2 = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0));
            Assert.True(s2.ReportFault(0, BrokerFaultKind.ComHResult, NativeInteropFaultGuard.RPC_S_SERVER_UNAVAILABLE, 200));       // Fatal → 페일오버
            Assert.Equal(1, s2.ActiveBroker);
            Assert.False(s2.CanEmit);
        }

        // ── §9 비활성 브로커 장애 → Disarm 하되 페일오버 없음 ────────────────────────
        [Fact]
        public void Failover_NonActiveBroker_Disarms_NoSwitch()
        {
            var sentinel = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0));
            Assert.True(sentinel.ReportFault(1, BrokerFaultKind.Disconnect, 0, 100));   // 보조(비활성) 장애
            Assert.True(sentinel.IsDisarmed);              // fail-closed(주문 봉쇄)
            Assert.Equal(0, sentinel.ActiveBroker);        // ★활성(주) 유지 — 페일오버 없음
            Assert.Equal(0, sentinel.FailoverCount);
        }

        // ── §10 재무장은 물리 이중 키만(liveArmed:false 불변) ────────────────────────
        [Fact]
        public void Failover_Rearm_Requires_Physical_DualKey()
        {
            var sentinel = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0));
            sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, 100);
            Assert.True(sentinel.IsDisarmed);

            Assert.False(sentinel.TryRearm(valveWired: true, liveArmingWired: false));   // 한 키 결여 → 거부
            Assert.False(sentinel.TryRearm(false, true));
            Assert.True(sentinel.IsDisarmed);                                            // 여전히 봉쇄
            Assert.True(sentinel.TryRearm(true, true));                                  // ★물리 이중 키 → 재무장
            Assert.True(sentinel.CanEmit);
        }

        // ── §11 프록시 사망(COM AV 대리) → 페일오버 ──────────────────────────────────
        [Fact]
        public void Failover_ProxyDead_Triggers()
        {
            var board = new LockFreeBrokerSwitchboard(2, 0);
            using var secret = NewSecret();
            var sentinel = new BrokerFailoverSentinel(board, credentials: secret);
            var probe = new ProxyLivenessProbe(TimeSpan.TicksPerSecond);
            probe.Beat(1000);

            long now = 1000 + 2 * TimeSpan.TicksPerSecond;
            if (probe.IsDead(now))   // 하트비트 침묵 → 프록시 사망 → 장애 보고
                sentinel.ReportFault(0, BrokerFaultKind.ProxyDead, 0, now);

            Assert.Equal(BrokerFaultKind.ProxyDead, sentinel.LastFault);
            Assert.Equal(1, sentinel.ActiveBroker);
            Assert.True(secret.IsPoisoned && secret.AllZero());
        }

        // ── §12 ★liveArmed:false — 샌드박스 연결 + 물리 키 미결속 → 실물 발주 100% Veto ──
        [Fact]
        public void LiveArmedFalse_SandboxConnected_PhysicalKeyUnbridged_VetoesAll()
        {
            // 페일오버 센티널은 정상(장애 0·CanEmit=true) — '피드 샌드박스 연결됨'을 모사.
            var sentinel = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0));
            Assert.True(sentinel.CanEmit);

            // 그러나 실물 발주 게이트(CanaryHealthSentinel)는 물리 이중 키 미결속 → 무장 실패.
            var canary = new CanaryHealthSentinel();
            long cap = 10_000_000L;
            Assert.False(canary.Arm(cap, valveWired: true, liveArmingWired: false));   // 한 키 결여
            Assert.False(canary.IsArmed);

            long authorized = 0;
            for (int i = 0; i < 3_000; i++)
                if (canary.TryAuthorize(1L) > 0) authorized++;
            Assert.Equal(0L, authorized);   // ★샌드박스 연결·페일오버 정상이어도 물리 키 없으면 발주 인가 0
        }

        // ── §13 자격증명 소거 멱등(다중 장애에도 1회·예외 없음) ──────────────────────
        [Fact]
        public void Failover_CredentialWipe_Idempotent()
        {
            using var secret = NewSecret();
            var sentinel = new BrokerFailoverSentinel(new LockFreeBrokerSwitchboard(2, 0), credentials: secret);
            for (int i = 0; i < 100; i++) sentinel.ReportFault(i & 1, BrokerFaultKind.Disconnect, 0, 100 + i);
            Assert.True(sentinel.CredentialsWiped);
            Assert.True(secret.IsPoisoned && secret.AllZero());   // 중복 소거에도 안정
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §14 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class MultiBrokerFailoverConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public MultiBrokerFailoverConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly LockFreeBrokerSwitchboard _hotBoard = new(2, 0);
        private static long _hotTick;
        private static void SwitchHot() { long t = Interlocked.Increment(ref _hotTick); _hotBoard.SwitchTo((int)(t & 1), t); }

        private static readonly BrokerFailoverSentinel _hotSentinel = new(new LockFreeBrokerSwitchboard(2, 0), http429Threshold: 1_000_000);
        private static long _hotTick2;
        private static void ReportNoFaultHot() { long t = Interlocked.Increment(ref _hotTick2); _hotSentinel.ReportFault(0, BrokerFaultKind.Http429, 0, t); }   // 0<문턱 → 무장애

        [Fact]
        public void Switchboard_SwitchTo_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) SwitchHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) SwitchHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★CAS 라우팅 전환 힙 0(스레드 블로킹/GC 0)
        }

        [Fact]
        public void Sentinel_ReportFault_NoFault_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) ReportNoFaultHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) ReportNoFaultHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★장애 분류 핫패스 힙 0(결정론)
        }

        // ── 락프리 동시 페일오버 — 활성 인덱스 항상 유효·회계 정합 ──────────────────
        [Fact]
        public void Switchboard_Concurrent_Switch_ActiveAlwaysValid()
        {
            var board = new LockFreeBrokerSwitchboard(4, 0);
            const int threads = 8; const int iters = 50_000;
            long badActive = 0; Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(threads);
            for (int t = 0; t < threads; t++)
            {
                int seed = t;
                new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        for (int i = 0; i < iters; i++)
                        {
                            board.SwitchTo((seed + i) & 3, i);
                            int a = board.Active;
                            if ((uint)a >= 4u) Interlocked.Increment(ref badActive);   // 활성 인덱스는 항상 [0,4)
                        }
                    }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true }.Start();
            }
            start.Set();
            Assert.True(done.Wait(30_000));
            done.Dispose(); start.Dispose();
            Assert.Null(err);
            Assert.Equal(0L, Interlocked.Read(ref badActive));   // ★torn/무효 인덱스 판독 0
            Assert.InRange(board.Active, 0, 3);
            _out.WriteLine($"[Switchboard/8t] switches={board.SwitchCount} finalActive={board.Active}");
        }

        // ── 동시 장애 폭주 — Disarm 정확 1회·자격증명 1회 소거·예외 0 ─────────────────
        [Fact]
        public void Sentinel_Concurrent_Fault_DisarmsAndWipesOnce()
        {
            var board = new LockFreeBrokerSwitchboard(2, 0);
            using var secret = new PoisonedSecretBuffer(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var sentinel = new BrokerFailoverSentinel(board, credentials: secret);
            const int threads = 12;
            Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(threads);
            for (int t = 0; t < threads; t++)
            {
                new Thread(() =>
                {
                    try { start.Wait(); for (int i = 0; i < 5_000; i++) sentinel.ReportFault(0, BrokerFaultKind.Disconnect, 0, i); }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true }.Start();
            }
            start.Set();
            Assert.True(done.Wait(30_000));
            done.Dispose(); start.Dispose();
            Assert.Null(err);   // ★멱등 소거 — 12스레드×5000 중복 장애에도 호스트 크래시/예외 0

            Assert.True(sentinel.IsDisarmed && !sentinel.CanEmit);
            Assert.True(sentinel.CredentialsWiped);
            Assert.True(secret.IsPoisoned && secret.AllZero());
            Assert.True(sentinel.FailoverCount >= 1);   // 최소 1회 페일오버(활성 장애)
        }
    }
}
