// ============================================================================
// CrashTriageStressHarness — [크래시 트리아지 · 추천 A]
//
//  §1 NativeLifetimeGate — 진입/이탈/폐쇄 의미 · 진행 중 연산 배수 대기 · 시한 초과 = 해제 포기(누수) · 이중 폐쇄 차단 · δ=0.
//  §2 NativeCanaryBlock — 전/후방 1바이트 월경 탐지 · 파열 블록 해제 보류 · δ=0 검증.
//  §3 [실결함 Red→Green] 네이티브 소유자 4종(BlackSwan · CacheLineCoherence · SimdRingVectorizer · AsyncCompressedLogger) —
//     진행 중 연산이 있으면 Dispose 가 해제를 미룬다(수정 전: 즉시 해제 = use-after-free) · 해제 후 연산 거부.
//  §4 [실결함] 로거 IO 스레드 Join 타임아웃 후 이벤트 처분 → 미처리 ObjectDisposedException(프로세스 종료) — 이제 누수 + 스레드 자가 종료.
//  §5 연산 ↔ Dispose 경합 스트레스 50라운드(스레드 예외 0 · 카나리 파열 0 · 전 스레드 합류) · 핫패스 δ=0 · 종료 원인 기록기 설치.
//  전체 스위트 50회 연속 실행은 tools/crash-triage-loop.ps1(이벤트 로그 1000/1026/2004 · 종료 코드 · 기록기 로그 교차 판정).
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed unsafe class CrashTriageStressHarness
    {
        // ════════════ §1 수명 게이트 ════════════

        [Fact]
        public void LifetimeGate_EnterExit_CloseDrains_TimeoutLeaks_DoubleCloseRefused()
        {
            int g = 0;
            Assert.True(NativeLifetimeGate.TryEnter(ref g));
            Assert.Equal(1, NativeLifetimeGate.InFlight(ref g));
            bool drained = false;
            var closer = new Thread(() => drained = NativeLifetimeGate.CloseAndDrain(ref g, TimeSpan.FromSeconds(5))) { IsBackground = true };
            closer.Start();
            Assert.True(SpinWait.SpinUntil(() => NativeLifetimeGate.IsClosed(ref g), 2000));
            Assert.False(NativeLifetimeGate.TryEnter(ref g));                    // ★폐쇄 후 신규 진입 거부
            Thread.Sleep(100);
            Assert.True(closer.IsAlive);                                         // ★진행 중 연산 1 — 배수 대기
            NativeLifetimeGate.Exit(ref g);
            Assert.True(closer.Join(5000));
            Assert.True(drained);
            Assert.False(NativeLifetimeGate.CloseAndDrain(ref g, TimeSpan.Zero)); // ★이중 폐쇄 = false(이중 해제 차단)

            int h = 0;
            Assert.True(NativeLifetimeGate.TryEnter(ref h));
            Assert.False(NativeLifetimeGate.CloseAndDrain(ref h, TimeSpan.FromMilliseconds(50)));   // 시한 초과 = 해제 금지
            NativeLifetimeGate.Exit(ref h);
            int z = 0;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 1000; i++) { if (NativeLifetimeGate.TryEnter(ref z)) NativeLifetimeGate.Exit(ref z); } }));
        }

        // ════════════ §2 카나리 경계 가드 ════════════

        [Fact]
        public void CanaryBlock_DetectsOverrunAndUnderrun_HoldsCorruptFree_ZeroAlloc()
        {
            const nuint n = 200;
            byte* p = (byte*)NativeCanaryBlock.Alloc(n);
            Assert.Equal(0, (long)((nint)p % 64));                                 // 64B 정렬
            for (int i = 0; i < (int)n; i++) Assert.Equal(0, p[i]);               // 0 초기화
            Assert.True(NativeCanaryBlock.Verify(p, n));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 1000; i++) NativeCanaryBlock.Verify(p, n); }));
            p[n] ^= 0x01;                                                        // ★후방 1바이트 월경
            Assert.False(NativeCanaryBlock.Verify(p, n));
            Assert.False(NativeCanaryBlock.Free(p, n));                          // 파열 = 해제 보류
            p[n] ^= 0x01;
            p[-NativeCanaryBlock.GuardBytes] ^= 0x80;                            // ★전방 가드 오염(언더런)
            Assert.False(NativeCanaryBlock.Verify(p, n));
            p[-NativeCanaryBlock.GuardBytes] ^= 0x80;
            Assert.False(NativeCanaryBlock.Verify(p, n + 8));                    // 크기 불일치
            Assert.True(NativeCanaryBlock.Free(p, n));
            Assert.False(NativeCanaryBlock.Free(null, n));
        }

        // ════════════ §3 소유자 Dispose ↔ 진행 중 연산 ════════════

        private static void AssertDisposeWaitsForInFlight(Func<bool> hold, Action release, Action dispose, Func<bool> released)
        {
            Assert.True(hold());                                                 // 진행 중 연산 1건(포인터 사용 중)
            var t = new Thread(() => dispose()) { IsBackground = true };
            t.Start();
            Thread.Sleep(150);
            Assert.False(released());                                            // ★수정 전: 이 시점에 이미 해제(use-after-free 창)
            release();
            Assert.True(t.Join(5000));
            Assert.True(released());
            Assert.False(hold());                                                // 해제 후 진입 거부
        }

        [Fact]
        public void Owners_DisposeDefersFreeUntilInFlightDrained_ThenRefuse()
        {
            var eng = new ClaudeBlackSwanEngine();
            Assert.True(eng.VerifyBoundaries());
            AssertDisposeWaitsForInFlight(eng.HoldForTest, eng.ReleaseHoldForTest, eng.Dispose, () => eng.NativeReleased);
            Assert.False(eng.TryPrecognize(1, out _, out _));
            Assert.False(eng.VerifyBoundaries());
            Assert.Equal((0, false), (eng.CanaryFaults, eng.LeakedOnDispose));

            var coh = new CacheLineCoherenceSentinel(4);
            coh.Record(1);
            AssertDisposeWaitsForInFlight(coh.HoldForTest, coh.ReleaseHoldForTest, coh.Dispose, () => coh.NativeReleased);
            coh.Record(1);                                                       // 해제 후 = 무동작(기록 0)
            Assert.Equal(0, coh.CounterOf(1));

            var vec = new SimdAlignedRingVectorizer();
            vec.Push(1f);
            AssertDisposeWaitsForInFlight(vec.HoldForTest, vec.ReleaseHoldForTest, vec.Dispose, () => vec.NativeReleased);
            vec.Push(2f);
            Assert.Equal(0f, vec.WeightedMomentum(new float[SimdAlignedRingVectorizer.Window]));
        }

        [Fact]
        public void Owner_DrainTimeout_LeaksInsteadOfUseAfterFree()
        {
            var coh = new CacheLineCoherenceSentinel(2);
            Assert.True(coh.HoldForTest());
            coh.Dispose();                                                       // 2초 시한 내 미배수
            Assert.True(coh.LeakedOnDispose);
            Assert.False(coh.NativeReleased);                                    // ★해제하지 않음(누수) — 진행 중 연산이 쓰는 메모리 보존
            coh.ReleaseHoldForTest();
        }

        [Fact]
        public void Coherence_ArenaOverrun_TripsFuse_AndHoldsFree()
        {
            var coh = new CacheLineCoherenceSentinel(2);
            Assert.True(coh.AttestArena());
            coh.CorruptTrailingGuardForTest();
            Assert.False(coh.AttestArena());
            Assert.True(coh.CoherenceFuseTripped);
            coh.Dispose();
            Assert.Equal(1, coh.CanaryFaults);                                   // 파열 블록 해제 보류(손상 증폭 차단)
        }

        // ════════════ §4 로거 IO 스레드 ↔ 처분 ════════════

        [Fact]
        public void Logger_JoinTimeout_NoDisposedEventCrash_ThreadExitsOnItsOwn()
        {
            string path = Path.Combine(Path.GetTempPath(), "ms2e_triage_" + Guid.NewGuid().ToString("N") + ".ms2log");
            var gate = new ManualResetEventSlim(false);
            var blocked = new ManualResetEventSlim(false);
            var lg = new ZeroAllocAsyncCompressedLogger(path, 1024);
            try
            {
                lg.DrainHookForTest = () => { blocked.Set(); gate.Wait(10_000); };
                lg.Start();
                Assert.True(blocked.Wait(5000));                                // IO 스레드가 드레인 안에서 정지(디스크 stall 모사)
                lg.Log(1, 2);
                lg.Dispose();                                                    // Join(2초) 타임아웃
                Assert.True(lg.LeakedOnDispose);
                lg.Log(3, 4);                                                    // 처분 후 Log = 거부(해제/누수 버퍼 무접촉)
                lg.DrainHookForTest = null;
                gate.Set();                                                      // stall 해제 → 스레드가 _stop 보고 종료
                Assert.True(SpinWait.SpinUntil(() => !lg.IoThreadAlive, 10_000));   // ★수정 전: 처분된 _wake.Reset() → 미처리 ODE → 프로세스 종료
            }
            finally
            {
                gate.Set();
                SpinWait.SpinUntil(() => !lg.IoThreadAlive, 10_000);
                gate.Dispose(); blocked.Dispose();
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [Fact]
        public void KillSwitchSpin_ThrowingCallback_CountedNotFatal_KeepsWatching()
        {
            int calls = 0;
            using var spin = new HardwareInterruptSpinLock(_ => { if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("콜백 실패"); });
            spin.Start();
            spin.RaiseInterrupt(KillSwitchReason.Manual);
            Assert.True(SpinWait.SpinUntil(() => spin.CallbackFaults == 1, 5000));   // ★수정 전: 전용 스핀 스레드 미처리 예외 = 프로세스 종료
            spin.RaiseInterrupt(KillSwitchReason.Manual);
            Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref calls) == 2, 5000));   // 감시 지속 — 다음 격발도 처리
            Assert.Equal(2, spin.Fires);
        }

        // ════════════ §5 경합 스트레스 · δ=0 · 기록기 ════════════

        [Fact]
        public void Stress_50Rounds_OperationsVsDispose_NoThreadException_NoCanaryFault()
        {
            var errors = new ConcurrentQueue<Exception>();
            var lim = SimdRiskLimits.Default;
            long[] px = new long[SpeculativeOrderBook.Levels], qty = new long[SpeculativeOrderBook.Levels], apx = new long[SpeculativeOrderBook.Levels];
            for (int i = 0; i < px.Length; i++) { px[i] = 10_000 - i; apx[i] = 10_010 + i; qty[i] = 100; }
            for (int round = 0; round < 50; round++)
            {
                var eng = new ClaudeBlackSwanEngine();
                var coh = new CacheLineCoherenceSentinel(4);
                var vec = new SimdAlignedRingVectorizer();
                int stop = 0;
                var threads = new Thread[4];
                for (int t = 0; t < threads.Length; t++)
                {
                    int id = t;
                    threads[t] = new Thread(() =>
                    {
                        try
                        {
                            var w = new float[SimdAlignedRingVectorizer.Window];
                            while (Volatile.Read(ref stop) == 0)
                            {
                                eng.Prime(id & 7, id, px, qty, apx, qty, lim);
                                eng.TryPrecognize(id, out _, out _);
                                coh.Record(id & 3);
                                coh.AttestArena();
                                vec.Push(id);
                                vec.WeightedMomentum(w);
                            }
                            // Dispose 이후에도 수 회 더 호출 — 해제된 메모리 접근 0(게이트 거부)
                            for (int k = 0; k < 100; k++) { eng.Prime(0, 1, px, qty, apx, qty, lim); coh.Record(0); vec.Push(0); }
                        }
                        catch (Exception ex) { errors.Enqueue(ex); }
                    }) { IsBackground = true };
                    threads[t].Start();
                }
                Thread.Sleep(2);
                eng.Dispose(); coh.Dispose(); vec.Dispose();                     // ★연산 진행 중 처분
                Volatile.Write(ref stop, 1);
                foreach (var th in threads) Assert.True(th.Join(10_000));
                Assert.Equal((0, false, false), (eng.CanaryFaults, eng.LeakedOnDispose, coh.LeakedOnDispose));
                Assert.Equal(0, coh.CanaryFaults);
                Assert.True(eng.NativeReleased && coh.NativeReleased && vec.NativeReleased);
            }
            Assert.Empty(errors);
        }

        [Fact]
        public void HotPaths_ZeroAlloc_WithGate()
        {
            using var eng = new ClaudeBlackSwanEngine();
            using var coh = new CacheLineCoherenceSentinel(4);
            using var vec = new SimdAlignedRingVectorizer();
            var lim = SimdRiskLimits.Default;
            long[] px = new long[SpeculativeOrderBook.Levels], qty = new long[SpeculativeOrderBook.Levels], apx = new long[SpeculativeOrderBook.Levels];
            for (int i = 0; i < px.Length; i++) { px[i] = 10_000 - i; apx[i] = 10_010 + i; qty[i] = 100; }
            float[] w = new float[SimdAlignedRingVectorizer.Window];
            string path = Path.Combine(Path.GetTempPath(), "ms2e_triage_za_" + Guid.NewGuid().ToString("N") + ".ms2log");
            var lg = new ZeroAllocAsyncCompressedLogger(path, 4096);
            try
            {
                Action body = () =>
                {
                    for (int i = 0; i < 200; i++)
                    {
                        eng.Prime(i & 7, i, px, qty, apx, qty, lim); eng.TryPrecognize(i, out _, out _); eng.VerifyBoundaries();
                        coh.Record(i & 3); coh.AttestArena();
                        vec.Push(i); vec.WeightedMomentum(w);
                        lg.Log(i, i);
                    }
                };
                body();
                Assert.Equal(0, MinAllocated(body));
            }
            finally
            {
                lg.Dispose();
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        [Fact]
        public void TestHostExitSentinel_Installed_WritesToPerProcessLog()
        {
            TestHostExitSentinel.Install();                                      // 모듈 초기화로 이미 설치 — 재호출 무해(1회 가드)
            Assert.EndsWith(Environment.ProcessId + ".log", TestHostExitSentinel.LogPath);
            TestHostExitSentinel.Write("harness-probe");
            Assert.Contains("harness-probe", File.ReadAllText(TestHostExitSentinel.LogPath));
        }
    }
}
