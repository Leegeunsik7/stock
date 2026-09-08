// ============================================================================
// TelemetryAnomalyCircuitBreakerTests — v12.1.0 Production Master Ignition & Hardware Field Activation 적대검증
//   Part 1 실시간 텔레메트리 이상 서킷 브레이커: 슬라이딩 지연/슬리피지/HTTP 연속오류 연산 → MultiTelemetryDisarmInterlock
//     결속 → 임계 초과 시 0ms Disarm(sticky) + 진행 발주 취소(CTS) + PoisonedSecretBuffer 0x00 소거.
//   Part 3 이중 엔진: ZeroAllocSlidingTelemetry(러닝섬 O(1) δ=0) · HardwarePulseAntiReplayGuard(HMAC 인증+임시키 파기).
//   Part 2 현장 마스터키: HardwareMasterKeyHandshake(순서 있는 Valve→LiveArming) + 5축 부팅 무결성·래칫≤40 프로덕션 포스처.
//   ★liveArmed:false: 핸드셰이크 검증·텔레메트리 측정은 무장 인가 아님 — 물리 키 미결속 시 발주 0.
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
    public sealed class TelemetryAnomalyCircuitBreakerTests
    {
        private readonly ITestOutputHelper _out;
        public TelemetryAnomalyCircuitBreakerTests(ITestOutputHelper o) => _out = o;

        private static PoisonedSecretBuffer NewSecret() =>
            new(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18 });
        private static readonly byte[] Key = { 0x9E, 0x3B, 0x77, 0x01, 0xC4, 0x52, 0xAA, 0x10, 0x66, 0x2F, 0xD8, 0x91, 0x3C, 0x40, 0x55, 0x7B };

        // ── §1 텔레메트리 이동평균·연속 카운터 산출 ──────────────────────────────────
        [Fact]
        public void Telemetry_Computes_MovingAverages_And_Consecutive()
        {
            var t = new ZeroAllocSlidingTelemetry(8);
            t.Push(10_000, 5, 200);
            t.Push(20_000, 15, 200);
            t.Push(30_000, 10, 200);
            Assert.Equal(3, t.Count);
            Assert.Equal(20_000, t.MeanDelayMicros);   // (10+20+30)/3 = 20ms
            Assert.Equal(10, t.MeanSlippageBps);        // (5+15+10)/3 = 10
            Assert.Equal(30_000, t.LastDelayMicros);
            Assert.Equal(0, t.Consecutive429);
        }

        // ── §2 HTTP 연속 오류 — 성공에 리셋 ─────────────────────────────────────────
        [Fact]
        public void Telemetry_ConsecutiveErrors_ResetOnSuccess()
        {
            var t = new ZeroAllocSlidingTelemetry(16);
            t.Push(1000, 0, 429); t.Push(1000, 0, 429); t.Push(1000, 0, 429);
            Assert.Equal(3, t.Consecutive429);
            t.Push(1000, 0, 503); t.Push(1000, 0, 503);   // 5xx 시작 → 429 리셋
            Assert.Equal(0, t.Consecutive429);
            Assert.Equal(2, t.Consecutive50x);
            t.Push(1000, 0, 200);                          // 성공 → 전부 리셋
            Assert.Equal(0, t.Consecutive429);
            Assert.Equal(0, t.Consecutive50x);
        }

        // ── §3 슬라이딩 윈도우 — 러닝섬 축출 정합(랩 후) ────────────────────────────
        [Fact]
        public void Telemetry_SlidingWindow_Evicts_Old_RunningSum()
        {
            var t = new ZeroAllocSlidingTelemetry(4);
            for (int i = 1; i <= 4; i++) t.Push(i * 1000, i, 200);   // [1k,2k,3k,4k] mean=2500
            Assert.Equal(2500, t.MeanDelayMicros);
            t.Push(9000, 9, 200);   // 최고참(1k) 축출 → [2k,3k,4k,9k] mean=4500
            Assert.Equal(4, t.Count);
            Assert.Equal(4500, t.MeanDelayMicros);   // ★러닝섬 축출 정합
        }

        // ── §4 ★지연 임계 초과 → 0ms Disarm + 진행 발주 취소 + Secret 소거 ────────────
        [Fact]
        public void Pipeline_DelayBreach_Disarms_CancelsInflight_Poisons()
        {
            var telem = new ZeroAllocSlidingTelemetry(64);
            var recorder = new AnomalyFlightRecorder(64);
            var interlock = new MultiTelemetryDisarmInterlock(recorder);
            interlock.Configure(latencyMicros: 50_000, http429Count: 5, slippageBps: 30);
            using var secret = NewSecret();
            using var inflight = new CancellationTokenSource();   // 진행 중 발주 토큰
            recorder.StagePending(0, 1_000_000);
            Assert.True(interlock.CanEmit);

            var sw = Stopwatch.StartNew();
            telem.Push(delayMicros: 60_000, slippageBps: 5, httpStatus: 200);   // 60ms > 50ms
            if (telem.DelayBreached(50_000)) interlock.Note(TelemetrySource.Latency, telem.LastDelayMicros);
            if (interlock.IsDisarmed) { inflight.Cancel(); secret.PurgeAndPoison(); }   // 진행 발주 취소 + Secret 소거
            sw.Stop();

            Assert.True(interlock.IsDisarmed && !interlock.CanEmit);   // ★fail-closed(주문 봉쇄)
            Assert.True(interlock.BreachedSource(TelemetrySource.Latency));
            Assert.True(inflight.IsCancellationRequested);             // ★진행 발주 취소
            Assert.True(recorder.HasCaptured && recorder.PendingBufferIsZero);   // 대기 주문 0x00
            Assert.True(secret.IsPoisoned && secret.AllZero());
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));
            Assert.True(sw.Elapsed.TotalMilliseconds < 10, $"disarm {sw.Elapsed.TotalMilliseconds}ms");
            _out.WriteLine($"[Telemetry] disarm {sw.Elapsed.TotalMilliseconds:F4}ms lastDelay={telem.LastDelayMicros}μs");
        }

        // ── §5 HTTP 연속 오류 폭풍 → Disarm ─────────────────────────────────────────
        [Fact]
        public void Pipeline_HttpErrorStorm_Disarms()
        {
            var telem = new ZeroAllocSlidingTelemetry(32);
            var interlock = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder(8));
            interlock.Configure(50_000, http429Count: 1, slippageBps: 30);   // 연속 임계 대비 Note 값 매핑

            for (int i = 0; i < 6; i++) telem.Push(1000, 0, 429);   // 429 연속 6
            if (telem.HttpErrorStorm(5)) interlock.Note(TelemetrySource.Http429, telem.Consecutive429);
            Assert.True(interlock.IsDisarmed);
            Assert.Equal(6, telem.Consecutive429);
        }

        // ── §6 슬리피지 이동평균 초과 → Disarm ──────────────────────────────────────
        [Fact]
        public void Pipeline_SlippageBreach_Disarms()
        {
            var telem = new ZeroAllocSlidingTelemetry(16);
            var interlock = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder(8));
            interlock.Configure(50_000, 5, slippageBps: 30);

            for (int i = 0; i < 8; i++) telem.Push(1000, 45, 200);   // 슬리피지 45bps 지속 → mean 45 > 30
            if (telem.SlippageBreached(30)) interlock.Note(TelemetrySource.Slippage, telem.MeanSlippageBps);
            Assert.True(interlock.IsDisarmed);
            Assert.True(interlock.BreachedSource(TelemetrySource.Slippage));
        }

        // ── §7 임계 이하 → 무Disarm(정상 가동) ──────────────────────────────────────
        [Fact]
        public void Pipeline_BelowThreshold_NoDisarm()
        {
            var telem = new ZeroAllocSlidingTelemetry(16);
            var interlock = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder(8));
            interlock.Configure(50_000, 5, 30);

            for (int i = 0; i < 10; i++) telem.Push(20_000, 10, 200);   // 20ms·10bps·성공 — 전부 이하
            if (telem.DelayBreached(50_000)) interlock.Note(TelemetrySource.Latency, telem.LastDelayMicros);
            if (telem.SlippageBreached(30)) interlock.Note(TelemetrySource.Slippage, telem.MeanSlippageBps);
            if (telem.HttpErrorStorm(5)) interlock.Note(TelemetrySource.Http429, telem.Consecutive429);
            Assert.True(interlock.CanEmit);   // ★정상 — 봉쇄 없음
        }

        // ── §8 하드웨어 펄스 — 정당 인증 펄스 Accept ────────────────────────────────
        [Fact]
        public void AntiReplay_GenuinePulse_Accepts()
        {
            using var guard = new HardwarePulseAntiReplayGuard(Key);
            Span<byte> tag = stackalloc byte[32];
            HardwarePulseAntiReplayGuard.ComputeTag(Key, 100, tag);
            Assert.Equal(AuthPulseVerdict.Accept, guard.Verify(100, tag, 1000));
            HardwarePulseAntiReplayGuard.ComputeTag(Key, 101, tag);
            Assert.Equal(AuthPulseVerdict.Accept, guard.Verify(101, tag, 1010));   // 단조 전진
            Assert.Equal(2, guard.AcceptCount);
            Assert.False(guard.IsKeyErased);
        }

        // ── §9 리플레이(시퀀스 역행) → 임시 키 파기 ─────────────────────────────────
        [Fact]
        public void AntiReplay_ReplaySeq_ErasesKey()
        {
            using var guard = new HardwarePulseAntiReplayGuard(Key);
            Span<byte> tag = stackalloc byte[32];
            HardwarePulseAntiReplayGuard.ComputeTag(Key, 100, tag);
            Assert.Equal(AuthPulseVerdict.Accept, guard.Verify(100, tag, 1000));

            HardwarePulseAntiReplayGuard.ComputeTag(Key, 100, tag);   // ★동일 seq 재전송(녹화 리플레이)
            Assert.Equal(AuthPulseVerdict.Replay, guard.Verify(100, tag, 1005));
            Assert.True(guard.IsKeyErased);                            // ★임시 키 파기
            Assert.Equal(1, guard.ReplayCount);
        }

        // ── §10 위조 태그 → 임시 키 파기 ────────────────────────────────────────────
        [Fact]
        public void AntiReplay_ForgedTag_ErasesKey()
        {
            using var guard = new HardwarePulseAntiReplayGuard(Key);
            Span<byte> forged = stackalloc byte[32];   // 전부 0 — 위조
            Assert.Equal(AuthPulseVerdict.Forged, guard.Verify(50, forged, 1000));
            Assert.True(guard.IsKeyErased);
            Assert.Equal(1, guard.ForgeryCount);
        }

        // ── §11 키 파기 후 sticky — 정당 펄스도 KeyErased ───────────────────────────
        [Fact]
        public void AntiReplay_KeyErased_Sticky()
        {
            using var guard = new HardwarePulseAntiReplayGuard(Key);
            Span<byte> forged = stackalloc byte[32];
            guard.Verify(50, forged, 1000);   // 위조 → 파기
            Assert.True(guard.IsKeyErased);

            Span<byte> tag = stackalloc byte[32];
            HardwarePulseAntiReplayGuard.ComputeTag(Key, 200, tag);   // 정당 태그여도
            Assert.Equal(AuthPulseVerdict.KeyErased, guard.Verify(200, tag, 2000));   // ★봉인 지속(fail-closed)
        }

        // ── §12 마스터키 핸드셰이크 — 순서 정합(Valve→LiveArming) Arms ──────────────
        [Fact]
        public void Handshake_InOrder_Arms()
        {
            var hs = new HardwareMasterKeyHandshake(windowTicks: 5 * TimeSpan.TicksPerSecond);
            Assert.True(hs.EngageValve(nonce: 1, nowTicks: 1000));
            Assert.Equal(HandshakeState.ValveEngaged, hs.State);
            Assert.True(hs.EngageLiveArming(nonce: 2, nowTicks: 1000 + TimeSpan.TicksPerSecond));
            Assert.True(hs.IsArmed);
        }

        // ── §13 순서 위반(라이브무장 선행) → 거부 ───────────────────────────────────
        [Fact]
        public void Handshake_OutOfOrder_Rejected()
        {
            var hs = new HardwareMasterKeyHandshake(5 * TimeSpan.TicksPerSecond);
            Assert.False(hs.EngageLiveArming(nonce: 1, nowTicks: 1000));   // ★밸브 없이 라이브무장
            Assert.Equal(HandshakeState.Rejected, hs.State);
            Assert.False(hs.IsArmed);
        }

        // ── §14 창 초과(stale) → 무장 실패 ──────────────────────────────────────────
        [Fact]
        public void Handshake_StaleWindow_NotArmed()
        {
            var hs = new HardwareMasterKeyHandshake(windowTicks: TimeSpan.TicksPerSecond);
            Assert.True(hs.EngageValve(1, 1000));
            Assert.False(hs.EngageLiveArming(2, 1000 + 2 * TimeSpan.TicksPerSecond));   // 2s > 1s 창
            Assert.False(hs.IsArmed);
            Assert.Equal(HandshakeState.Idle, hs.State);   // stale → Idle 복귀
        }

        // ── §15 nonce 리플레이 → 거부 ───────────────────────────────────────────────
        [Fact]
        public void Handshake_ReplayedNonce_Rejected()
        {
            var hs = new HardwareMasterKeyHandshake(5 * TimeSpan.TicksPerSecond);
            Assert.True(hs.EngageValve(5, 1000));
            Assert.False(hs.EngageLiveArming(5, 1000 + 1000));   // nonce 5 ≤ 5(밸브와 동일) → 리플레이 거부
            Assert.False(hs.IsArmed);
        }

        // ── §16 프로덕션 포스처 — 5축 부팅 무결성 + 래칫≤40 PASS ─────────────────────
        private sealed class SafeProbe : IGoldenMasterProbe
        {
            public bool LiveArmedFalse => true;
            public bool DualKeyAbsent => true;
            public bool VaultSealed => true;
        }

        [Fact]
        public void Production_FiveAxisBoot_And_Ratchet_Pass()
        {
            var host = typeof(CanaryHealthSentinel).Assembly;
            var rcl = typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly;

            Assert.True(GoldenMasterBootSentinel.RclReferencesNoEngine(rcl, host));   // RCL 0 엔진참조
            var rep = GoldenMasterBootSentinel.Scan(host, rcl, new SafeProbe(), out _);
            Assert.Equal(5, rep.Total);
            Assert.True(rep.AllPassed);   // ★5축 부팅 무결성(RCL/래칫/liveArmed:false/이중키부재/금고봉인) PASS

            var shell = PureShellFitnessSentinel.Scan(host);
            Assert.True(shell.WithinRatchet && shell.HostCoupledUi <= 40);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);   // 래칫 천장 봉인
            _out.WriteLine($"[Production] boot5={rep.AllPassed} coupled={shell.HostCoupledUi} ceiling={PureShellFitnessSentinel.RatchetCeiling}");
        }

        // ── §17 ★liveArmed:false — 핸드셰이크 검증돼도 물리 키 미결속 → 발주 100% Veto ─
        [Fact]
        public void LiveArmedFalse_HandshakeValidated_ButPhysicalKeyUnbridged_VetoesAll()
        {
            // 핸드셰이크 상태기계는 '순서 정당성'만 검증(논리) — 실제 물리 키 결속과 무관.
            var hs = new HardwareMasterKeyHandshake(5 * TimeSpan.TicksPerSecond);
            hs.EngageValve(1, 1000);
            hs.EngageLiveArming(2, 1000 + TimeSpan.TicksPerSecond);
            Assert.True(hs.IsArmed);   // 논리적 핸드셰이크 정당

            // 그러나 실물 발주 게이트는 물리 키 미결속 → 무장 실패 → 발주 0.
            var canary = new CanaryHealthSentinel();
            Assert.False(canary.Arm(10_000_000L, valveWired: false, liveArmingWired: false));
            long authorized = 0;
            for (int i = 0; i < 3_000; i++) if (canary.TryAuthorize(1L) > 0) authorized++;
            Assert.Equal(0L, authorized);   // ★핸드셰이크 검증 ≠ 발주 인가(liveArmed:false 불변)
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §18 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class TelemetryAnomalyCircuitBreakerConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public TelemetryAnomalyCircuitBreakerConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly byte[] Key = { 0x9E, 0x3B, 0x77, 0x01, 0xC4, 0x52, 0xAA, 0x10, 0x66, 0x2F, 0xD8, 0x91, 0x3C, 0x40, 0x55, 0x7B };

        private static readonly ZeroAllocSlidingTelemetry _hotTelem = new(1024);
        private static long _hotStamp;
        private static void PushHot()
        {
            long s = Interlocked.Increment(ref _hotStamp);
            _hotTelem.Push(s % 70_000, s % 50, (int)(s % 3 == 0 ? 429 : 200));
        }

        [Fact]
        public void Telemetry_Push_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) PushHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) PushHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★러닝섬 슬라이딩 연산 힙 0(고부하 틱 유입 δ=0)
        }

        // 리플레이-거부/봉인 fast-path δ=0(HMAC 이전 즉시 반환).
        private static readonly HardwarePulseAntiReplayGuard _erasedGuard = MakeErased();
        private static HardwarePulseAntiReplayGuard MakeErased()
        {
            var g = new HardwarePulseAntiReplayGuard(Key);
            Span<byte> forged = stackalloc byte[32];
            g.Verify(1, forged, 1);   // 위조 → 키 파기(이후 KeyErased fast-path)
            return g;
        }
        private static void ErasedFastPathHot()
        {
            Span<byte> tag = stackalloc byte[32];
            _erasedGuard.Verify(1, tag, 1);   // KeyErased 즉시 반환
        }

        [Fact]
        public void AntiReplay_KeyErasedFastPath_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) ErasedFastPathHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) ErasedFastPathHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★fail-closed 봉인 경로 힙 0
        }

        // HMAC 인증(Accept) 경로 δ=0 — HMACSHA256.HashData 정적 오버로드 무할당 실측.
        private static readonly HardwarePulseAntiReplayGuard _authGuard = new(Key);
        private static long _authSeq;
        private static void AcceptHot()
        {
            long s = Interlocked.Increment(ref _authSeq);
            Span<byte> tag = stackalloc byte[32];
            HardwarePulseAntiReplayGuard.ComputeTag(Key, s, tag);
            _authGuard.Verify(s, tag, s);
        }

        [Fact]
        public void AntiReplay_AcceptPath_HmacZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) AcceptHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) AcceptHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            _out.WriteLine($"[AntiReplay/HMAC] accept-path alloc={alloc}B over 100k");
            Assert.Equal(0L, alloc);   // ★HMAC-SHA256 정적 HashData + stackalloc → 인증 경로도 δ=0
        }

        // 단일 라이터 Push + 동시 판독 — torn/예외 없이 이동평균 범위 정합.
        [Fact]
        public void Telemetry_Concurrent_Read_During_Push_Consistent()
        {
            var telem = new ZeroAllocSlidingTelemetry(256);
            long badRead = 0; Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(5);
            long stop = 0;

            var writer = new Thread(() =>
            {
                try { start.Wait(); for (int i = 0; i < 500_000; i++) telem.Push(i % 60_000, i % 40, 200); }
                catch (Exception e) { err = e; }
                finally { Volatile.Write(ref stop, 1); done.Signal(); }
            }) { IsBackground = true };

            for (int r = 0; r < 4; r++)
            {
                new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        while (Volatile.Read(ref stop) == 0)
                        {
                            long mean = telem.MeanDelayMicros;   // 러닝섬/카운트 동시 판독
                            if (mean < 0 || mean > 60_000) Interlocked.Increment(ref badRead);   // 항상 [0,60000)
                        }
                    }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true }.Start();
            }
            writer.Start();
            start.Set();
            Assert.True(done.Wait(30_000));
            done.Dispose(); start.Dispose();
            Assert.Null(err);
            Assert.Equal(0L, Interlocked.Read(ref badRead));   // ★동시 판독 항상 유효 범위(torn 0)
            _out.WriteLine($"[Telemetry/5t] count={telem.Count} meanDelay={telem.MeanDelayMicros}μs");
        }
    }
}
