// ============================================================================
// Phase99HardwarePulseGuardTests — v12.0.2 Think Beyond 적대적 검증 (Engine ①·②)
//   ① HardwarePulseGuard — 하드웨어 접점 바운스/위조 신호 안티탬퍼.
//        · 단조 토큰 역행/재생(seq ≤ 마지막수용) → Tamper(위조·fail-closed 격발).
//        · 디바운스 창 안 후속 펄스 → Debounced(바운스 억제·격발 차단).
//        · 창 밖 단조 전진 → Accept(정당 물리 동작·격발).
//        · ShouldFire: Accept∧Tamper 는 격발(fail-closed), Debounced 만 억제.
//        · 결정론(주입 tick)·δ=0 핫패스·락프리 CAS 다중스레드 정합.
//   ② ZeroAllocMemoryProfiler — O(1) δ=0 스냅샷·누수 측정(빈 hot=0·할당 hot>0).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class Phase99HardwarePulseGuardTests
    {
        private readonly ITestOutputHelper _out;
        public Phase99HardwarePulseGuardTests(ITestOutputHelper o) => _out = o;

        // ── §1 첫 펄스는 미초기화 → seq/tick 값과 무관하게 무조건 수용(콜드 격발) ──
        [Fact]
        public void FirstPulse_AcceptedEvenLowSeqAndTicks()
        {
            var g = new HardwarePulseGuard(100);
            Assert.Equal(PulseVerdict.Accept, g.Evaluate(0, 0));   // 미초기화 → Tamper/Debounce 분기 건너뜀
            Assert.Equal(1, g.AcceptedCount);
            Assert.Equal(0L, g.LastSeq);   // 수용된 seq=0 이 마지막
        }

        // ── §2 전체 시퀀스 — 3분류(Accept/Debounced/Tamper) 결정론 검증 ──────────────
        [Fact]
        public void FullSequence_ClassifiesBounceReplayGenuine()
        {
            var g = new HardwarePulseGuard(100);   // 디바운스 창 100 tick

            Assert.Equal(PulseVerdict.Accept,    g.Evaluate(10, 1000));  // 첫 정당(uninit) → lastAccept=1000, lastSeq=10
            Assert.Equal(PulseVerdict.Debounced, g.Evaluate(11, 1050));  // 11>10, gap 50<100 → 바운스 억제(lastSeq→11)
            Assert.Equal(PulseVerdict.Debounced, g.Evaluate(12, 1090));  // 12>11, gap 90<100 → 바운스
            Assert.Equal(PulseVerdict.Accept,    g.Evaluate(20, 1200));  // 20>12, gap 200≥100 → 정당(lastAccept=1200)
            Assert.Equal(PulseVerdict.Tamper,    g.Evaluate(20, 1210));  // 20≤20 → 재생(위조)
            Assert.Equal(PulseVerdict.Tamper,    g.Evaluate(15, 1220));  // 15≤20 → 역행(위조)
            Assert.Equal(PulseVerdict.Debounced, g.Evaluate(21, 1250));  // 21>20, gap 50<100 → 바운스(lastSeq→21)
            Assert.Equal(PulseVerdict.Accept,    g.Evaluate(30, 1400));  // 30>21, gap 200≥100 → 정당

            Assert.Equal(3, g.AcceptedCount);
            Assert.Equal(3, g.DebouncedCount);
            Assert.Equal(2, g.TamperCount);
            Assert.Equal(30L, g.LastSeq);
            _out.WriteLine($"[PulseGuard] accepted={g.AcceptedCount} debounced={g.DebouncedCount} tamper={g.TamperCount} lastSeq={g.LastSeq}");
        }

        // ── §3 ShouldFire fail-closed 의미 — Accept∧Tamper 격발, Debounced 억제 ──────
        [Fact]
        public void ShouldFire_FailClosed_Semantics()
        {
            var g = new HardwarePulseGuard(100);

            Assert.True(g.ShouldFire(5, 1000, out var v1));    // 첫 정당 → 격발
            Assert.Equal(PulseVerdict.Accept, v1);

            Assert.False(g.ShouldFire(6, 1010, out var v2));   // 창 안 바운스 → 억제(격발 차단)
            Assert.Equal(PulseVerdict.Debounced, v2);

            Assert.True(g.ShouldFire(3, 1020, out var v3));     // ★재생/위조 → 격발(fail-closed 안전측 Disarm)
            Assert.Equal(PulseVerdict.Tamper, v3);
        }

        // ── §4 Reset → 콜드 자세 복원(카운터 0·다음 첫 펄스 재수용) ────────────────────
        [Fact]
        public void Reset_RestoresColdPosture()
        {
            var g = new HardwarePulseGuard(50);
            Assert.Equal(PulseVerdict.Accept,    g.Evaluate(10, 1000));
            Assert.Equal(PulseVerdict.Debounced, g.Evaluate(11, 1010));   // gap 10<50
            Assert.Equal(PulseVerdict.Tamper,    g.Evaluate(5, 1020));    // 5≤11
            Assert.Equal(1, g.AcceptedCount); Assert.Equal(1, g.DebouncedCount); Assert.Equal(1, g.TamperCount);

            g.Reset();
            Assert.Equal(0, g.AcceptedCount); Assert.Equal(0, g.DebouncedCount); Assert.Equal(0, g.TamperCount);
            Assert.Equal(0L, g.LastSeq);
            Assert.Equal(PulseVerdict.Accept, g.Evaluate(2, 5));   // 리셋 후 미초기화 → 첫 펄스 재수용
        }

        // ── §5 프로파일러 — 할당 hot 은 누수>0·NoLeak false, null hot 은 0 ────────────
        private static volatile byte[]? _sink;
        private static void AllocHot() => _sink = new byte[128];   // 힙 할당(정적 sink 로 escape → 소거 방지)

        [Fact]
        public void Profiler_MeasureLeak_DetectsAllocation()
        {
            long leak = ZeroAllocMemoryProfiler.MeasureLeak(warmup: 10, iters: 200, AllocHot);
            Assert.True(leak > 0, $"할당 hot 누수 미검출: {leak}");   // 200×~144B ≈ 28KB
            _out.WriteLine($"[Profiler] allocHot leak={leak}B over 200 iters");
        }

        [Fact]
        public void Profiler_MeasureLeak_NullHot_Zero()
            => Assert.Equal(0L, ZeroAllocMemoryProfiler.MeasureLeak(10, 10, null!));

        // ── §6 프로파일러 스냅샷/델타 의미 ───────────────────────────────────────────
        [Fact]
        public void Profiler_Snapshot_Deltas_Semantics()
        {
            var before = ZeroAllocMemoryProfiler.Capture();
            _sink = new byte[4096];   // 동일 스레드 힙 할당
            var after = ZeroAllocMemoryProfiler.Capture();

            Assert.True(ZeroAllocMemoryProfiler.LeakBytes(before, after) >= 4096);   // 스레드 누적할당 단조 증가
            Assert.True(after.ThreadAllocBytes >= before.ThreadAllocBytes);
            Assert.True(ZeroAllocMemoryProfiler.Gen2Delta(before, after) >= 0);      // 세대 카운트 비역행
            Assert.True(ZeroAllocMemoryProfiler.NoLeak(before, before));             // 동일 스냅샷 → 누수 0
            Assert.False(ZeroAllocMemoryProfiler.NoLeak(before, after));             // 할당 후 → 누수 감지
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §7 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class Phase99HardwarePulseGuardConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public Phase99HardwarePulseGuardConcurrencyTests(ITestOutputHelper o) => _out = o;

        // 정적 상태 + 정적 델리게이트(캡처 없음 → 측정구간 힙 0).
        private static readonly HardwarePulseGuard _hot = new(1);
        private static long _seq;
        private static void PulseHot() { long s = Interlocked.Increment(ref _seq); _hot.Evaluate(s, s * 10L); }   // 단조 전진 → Accept 경로

        private static readonly Action NoOp = static () => { };

        // ── Evaluate 핫패스 δ=0(락프리 CAS 단일스레드 첫시도 성공·SpinWait 는 struct) ──
        [Fact]
        public void Evaluate_HotPath_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Volatile.Write(ref _seq, 0); _hot.Reset();
                    for (int i = 0; i < 300_000; i++) PulseHot();   // 워밍업(JIT+단조 전진)
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) PulseHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★Accept 경로(Volatile 쓰기+Interlocked CAS/Increment) 힙 0
        }

        // ── Capture 커널 δ=0(unmanaged 판독·record struct 반환) ──────────────────────
        [Fact]
        public void Capture_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { var s = ZeroAllocMemoryProfiler.Capture(); GC.KeepAlive(s.ThreadAllocBytes); }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long sink = 0;
                    for (int i = 0; i < 100_000; i++) sink += ZeroAllocMemoryProfiler.Capture().Gen0;
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (sink < 0) err = new Exception("unreachable");
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        // ── MeasureLeak 빈 hot 은 정확히 0(δ=0 측정기 자체의 무누수 자기검증) ──────────
        [Fact]
        public void MeasureLeak_EmptyHot_ExactlyZero()
        {
            long leak = -1; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    _ = ZeroAllocMemoryProfiler.MeasureLeak(1_000, 1_000, NoOp);   // 사전 JIT(MeasureLeak/Capture/LeakBytes)
                    leak = ZeroAllocMemoryProfiler.MeasureLeak(300_000, 100_000, NoOp);
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, leak);   // ★측정기 자체 힙 0(빈 워크로드 누수 0)
        }

        // ── 락프리 다중스레드 정합 — 정확 1회 분류(누락·이중계수 0)·LastSeq 단조 ────────
        [Fact]
        public void Concurrent_Evaluate_ExactlyOnceAccounting()
        {
            var g = new HardwarePulseGuard(1);
            const int threads = 12, iters = 5_000;
            long issued = 0;
            Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(threads);
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        for (int i = 0; i < iters; i++)
                        {
                            long s = Interlocked.Increment(ref issued);   // 전역 단조 유일 seq
                            g.Evaluate(s, s);
                        }
                    }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true };
            }
            foreach (var th in ths) th.Start();
            start.Set();
            Assert.True(done.Wait(30_000));
            done.Dispose();   // ★조인 완료 후 처분(처분 레이스 회피)
            start.Dispose();
            Assert.Null(err);

            long total = (long)threads * iters;
            long classified = g.AcceptedCount + g.DebouncedCount + g.TamperCount;
            Assert.Equal(total, classified);          // ★정확 1회 분류(누락·이중계수 0)
            Assert.True(g.AcceptedCount >= 1);         // 최소 1건은 정당 수용
            Assert.True(g.LastSeq >= 1 && g.LastSeq <= Interlocked.Read(ref issued));   // LastSeq 단조·발급 범위 내
            _out.WriteLine($"[PulseGuard/12t] total={total} accept={g.AcceptedCount} debounce={g.DebouncedCount} tamper={g.TamperCount} lastSeq={g.LastSeq}");
        }
    }
}
