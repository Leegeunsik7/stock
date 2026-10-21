// ============================================================================
// BrokerRateLimiterTests — [v12.18.21-DEV] 락프리 슬라이딩 창 레이트 게이트 · 적응형 지수 백오프 · 키움/KIS 결선
//
//  §1 창 수학 — 버스트 5 즉시 · 6번째 거부 · 정확히 W 경과 시 재허가 · 2창(초당 5 · 분당 100) · 시계 역행 · 임의 도착 패턴 전수 창 대조
//  §2 16 스레드 경합 — (a) 가상 시계 계단(단계마다 16 스레드 × 40 시도 · 시각 고정 = 허가 시각 정확) → 임의 1초 ≤ 5 · 임의 60초 ≤ 100 전수 대조 +
//     활성(이론 최대 125 의 90% 이상) (b) 실 Stopwatch 2.2초 자유 경합 → 허가 ≤ 5 × ⌈경과/1초⌉ · 스레드별 할당 0B
//  §3 δ=0 — 10만 회 TryAcquire(허가/거부 혼합) · Remaining · 백오프 TryEnter/Note 0B
//  §4 백오프 — 단계 지연 100 · 200 · 400 · 800 · 1,600 · 3,000(상한) · 지터 [기준, 기준 × 1.25] · 창 동안 거부 · 연속 3회 sticky 래치 · 경보 1회 ·
//     취소(위험 감소) 경로는 래치 무시 · 재무장 물리 이중키 · 성공 = 연속 리셋 · 시드 결정론
//  §5 키움 코어 결선(FakeOcx) — 6건 동시 → OCX 5건 + 6번째 RcRateGated · 거부 통보 · −308 × 3 → 래치 → 신규 OCX 0 · 취소는 창 뒤 통과 · 조회 2 TR
//  §6 KIS REST 결선(스텁 HttpMessageHandler · 실 네트워크 0) — 동시 6건 → 핸들러 5건 · 429 × 3 → 래치 → 이후 핸들러 0
//  §7 Fail-Closed IL — 두 게이트 형식의 IL 참조 허용목록(브로커 · OCX · HTTP · 무장 형식 0) · 공개 표면 발주 멤버 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Adapters
{
    [Collection("ConcurrencyStress")]
    public sealed class BrokerRateLimiterTests
    {
        private const long Us = 1_000_000;                                   // 가상 시계 주파수(µs 틱)

        private sealed class Clock
        {
            public long T;
            public long Now() => Volatile.Read(ref T);
            public void Advance(long micros) => Interlocked.Add(ref T, micros);
        }

        // 정렬된 허가 시각에서 임의 반개구간 [a, a + w) 의 최대 허가 수(두 포인터).
        private static int MaxInAnyWindow(IReadOnlyList<long> sorted, long w)
        {
            int best = 0;
            for (int lo = 0, hi = 0; hi < sorted.Count; hi++)
            {
                while (sorted[hi] - sorted[lo] >= w) lo++;
                best = Math.Max(best, hi - lo + 1);
            }
            return best;
        }

        // ════════════ §1 창 수학 ════════════

        [Fact]
        public void Burst5_ThenDenied_UntilExactWindow_ThenRegranted()
        {
            var c = new Clock();
            var b = new LockFreeTokenBucket(5, LockFreeTokenBucket.SecondMicros, nowTicks: c.Now, tickFrequency: Us);
            Assert.Equal(5, b.Remaining());
            for (int i = 0; i < 5; i++) Assert.True(b.TryAcquire());                // 버스트 5 즉시
            Assert.False(b.TryAcquire());
            Assert.Equal(0, b.Remaining());
            c.Advance(999_999);
            Assert.False(b.TryAcquire());                                         // 1µs 모자람
            c.Advance(1);
            Assert.Equal(5, b.Remaining());
            Assert.True(b.TryAcquire());                                          // 정확히 W 경과
            Assert.Equal(4, b.Remaining());
            Assert.Equal((6L, 2L), (b.Granted, b.Denied));
        }

        [Fact]
        public void SpacedGrants_SlideOneByOne()
        {
            var c = new Clock();
            var b = new LockFreeTokenBucket(5, LockFreeTokenBucket.SecondMicros, nowTicks: c.Now, tickFrequency: Us);
            for (int i = 0; i < 5; i++) { Assert.True(b.TryAcquire()); c.Advance(100_000); }   // 0 · 0.1 · … · 0.4s
            c.Advance(500_000 - 1);                                               // 0.999999s
            Assert.False(b.TryAcquire());
            c.Advance(1);                                                         // 1.0s — 첫 허가만 만료
            Assert.True(b.TryAcquire());
            Assert.False(b.TryAcquire());
            c.Advance(100_000);                                                   // 1.1s — 두 번째 만료
            Assert.True(b.TryAcquire());
            Assert.False(b.TryAcquire());
        }

        [Fact]
        public void TwoWindows_FivePerSecond_HundredPerMinute()
        {
            var c = new Clock();
            var b = KiwoomComEventCore.CreateKiwoomLimiter(c.Now, Us);
            int granted = 0;
            for (int s = 0; s < 60; s++)
            {
                for (int k = 0; k < 10; k++) if (b.TryAcquire()) granted++;
                c.Advance(LockFreeTokenBucket.SecondMicros);
            }
            Assert.Equal(100, granted);                                           // 0~19초 매초 5 → 분당 100 소진 · 20~59초 0
            Assert.True(b.TryAcquire());                                          // 60초 = 첫 묶음 만료
            Assert.Equal(4, b.Remaining());
        }

        [Fact]
        public void ClockBackwards_NeverOpensWindowEarly()
        {
            var c = new Clock { T = 10 * Us };
            var b = new LockFreeTokenBucket(5, LockFreeTokenBucket.SecondMicros, nowTicks: c.Now, tickFrequency: Us);
            for (int i = 0; i < 5; i++) Assert.True(b.TryAcquire());
            c.T = 0;                                                              // 역행
            Assert.False(b.TryAcquire());
            c.T = 10 * Us + 999_999;
            Assert.False(b.TryAcquire());
            c.T = 11 * Us;
            Assert.True(b.TryAcquire());
        }

        [Fact]
        public void RandomArrivals_EveryWindow_WithinBound_AndGreedyOptimal()
        {
            var c = new Clock();
            var b = new LockFreeTokenBucket(5, LockFreeTokenBucket.SecondMicros, 40, 10 * LockFreeTokenBucket.SecondMicros, c.Now, Us);
            var rng = new Random(20261019);
            var times = new List<long>();
            for (int i = 0; i < 200_000; i++)
            {
                c.Advance(rng.Next(0, 40_000));                                  // 평균 20ms 간격(초당 50 시도)
                if (b.TryAcquire()) times.Add(c.T);
            }
            Assert.True(MaxInAnyWindow(times, Us) <= 5);
            Assert.True(MaxInAnyWindow(times, 10 * Us) <= 40);
            // 탐욕 최적성 근사: 10초 창 상한이 지배 → 총 경과 / 10초 × 40 의 95% 이상 허가(과잉 거부 없음)
            double expected = c.T / (10.0 * Us) * 40;
            Assert.True(times.Count >= expected * 0.95, $"허가 {times.Count} · 기대 {expected:F0}");
        }

        [Fact]
        public void Ctor_Rejects_Invalid()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeTokenBucket(0, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeTokenBucket(5, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeTokenBucket(LockFreeTokenBucket.MaxPerWindow + 1, 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeTokenBucket(5, 1, 3, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BrokerBackoffController(latchThreshold: 0));
        }

        // ════════════ §2 16 스레드 경합 ════════════

        [Fact]
        public void Sixteen_Threads_VirtualStepClock_NeverExceeds_5PerSecond_100PerMinute()
        {
            const int Threads = 16, Steps = 1_300, Attempts = 40;
            const long StepUs = 50_000;                                           // 50ms 계단 · 65초
            var c = new Clock();
            var b = KiwoomComEventCore.CreateKiwoomLimiter(c.Now, Us);
            var perStep = new long[Steps];
            var alloc = new long[Threads];
            using var barrier = new Barrier(Threads + 1);
            Exception? fault = null;
            var ts = new Thread[Threads];
            for (int t = 0; t < Threads; t++)
            {
                int id = t;
                ts[t] = new Thread(() =>
                {
                    try
                    {
                        long mine = 0;                                            // 0 이 아닌 단계 창 수(기록형 판정 — 단계는 재실행 불가)
                        for (int s = 0; s < Steps; s++)
                        {
                            barrier.SignalAndWait();                              // 단계 시작(시각 고정)
                            long before = GC.GetAllocatedBytesForCurrentThread();
                            int got = 0;
                            for (int k = 0; k < Attempts; k++) if (b.TryAcquire()) got++;
                            if (GC.GetAllocatedBytesForCurrentThread() - before != 0) mine++;
                            if (got > 0) Interlocked.Add(ref perStep[s], got);
                            barrier.SignalAndWait();                              // 단계 끝
                        }
                        alloc[id] = mine;
                    }
                    catch (Exception ex) { fault = ex; barrier.RemoveParticipant(); }
                }) { IsBackground = true };
                ts[t].Start();
            }
            for (int s = 0; s < Steps; s++)
            {
                barrier.SignalAndWait();
                barrier.SignalAndWait();
                c.Advance(StepUs);
            }
            foreach (var t in ts) Assert.True(t.Join(TimeSpan.FromSeconds(30)));
            Assert.Null(fault);

            var times = new List<long>();
            for (int s = 0; s < Steps; s++) for (long k = 0; k < perStep[s]; k++) times.Add(s * StepUs);
            Assert.True(MaxInAnyWindow(times, LockFreeTokenBucket.SecondMicros) <= 5, "임의 1초 창 초과");
            Assert.True(MaxInAnyWindow(times, LockFreeTokenBucket.MinuteMicros) <= 100, "임의 60초 창 초과");
            Assert.True(times.Count <= 125);                                      // 이론 최대: 0~19초 100 + 60~64초 25
            Assert.True(times.Count >= 112, $"활성 저하 — 허가 {times.Count}/125 · 경합 거부 {b.Contended} · 미발행 거부 {b.Unpublished}");
            Assert.Equal(times.Count, b.Granted);
            Assert.All(alloc, a => Assert.True(ZeroAllocProbe.PassesRecorded(Steps, a), $"0B 아닌 단계 창 {a}/{Steps}"));   // 경합 핫패스 스레드별 0B(창 1개 GC 잡음만 허용)
        }

        [Fact]
        public void Sixteen_Threads_RealClock_FreeRun_WithinBound_ZeroAlloc()
        {
            const int Threads = 16;
            var b = new LockFreeTokenBucket(5, LockFreeTokenBucket.SecondMicros);
            long granted = 0;
            var alloc = new long[Threads];                                        // 스레드별 0B 아닌 창 수(1,024회 연속 창 · 기록형 판정)
            var windows = new long[Threads];
            using var stop = new ManualResetEventSlim(false);
            using var done = new CountdownEvent(Threads);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int t = 0; t < Threads; t++)
            {
                int id = t;
                new Thread(() =>
                {
                    try
                    {
                        long mine = 0, n = 0, wins = 0, noisy = 0;
                        long a = GC.GetAllocatedBytesForCurrentThread();
                        while (!stop.IsSet)
                        {
                            if (b.TryAcquire()) mine++;
                            Thread.SpinWait(16);
                            if ((++n & 1_023) == 0) { long c = GC.GetAllocatedBytesForCurrentThread(); if (c != a) noisy++; wins++; a = c; }
                        }
                        if (GC.GetAllocatedBytesForCurrentThread() != a) noisy++;
                        alloc[id] = noisy;
                        windows[id] = wins + 1;
                        Interlocked.Add(ref granted, mine);
                    }
                    finally { done.Signal(); }
                }) { IsBackground = true }.Start();
            }
            Thread.Sleep(2_200);
            stop.Set();
            Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
            double elapsedSec = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            long bound = 5 * (long)Math.Ceiling(elapsedSec);                      // 허가 시각 ⊂ [t0, 끝] → ⌈경과/W⌉ 개 창으로 분할
            Assert.True(granted <= bound, $"허가 {granted} > 상한 {bound}(경과 {elapsedSec:F3}s)");
            Assert.True(granted >= 10, $"활성 저하 — 허가 {granted}");
            for (int t = 0; t < Threads; t++)
                Assert.True(ZeroAllocProbe.PassesRecorded(windows[t], alloc[t]), $"스레드 {t}: 0B 아닌 창 {alloc[t]}/{windows[t]}");
        }

        // ════════════ §3 δ=0 ════════════

        [Fact]
        public void HundredThousand_TryAcquire_And_Backoff_ZeroAlloc()
        {
            var c = new Clock();
            var b = KiwoomComEventCore.CreateKiwoomLimiter(c.Now, Us);
            var bo = new BrokerBackoffController(c.Now, Us, latchThreshold: int.MaxValue);
            b.TryAcquire(); b.Remaining(); bo.TryEnter(); bo.Note(429); bo.Note(0); bo.TryEnterRiskReducing();   // JIT 예열
            long granted = 0, denied = 0;
            long a = MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    c.Advance(7_000);                                             // 7ms 간격 시도 = 허가/거부 혼합
                    if (b.TryAcquire()) granted++; else denied++;
                    if ((i & 1023) == 0) { _ = b.Remaining(); bo.Note(i % 3 == 0 ? 429 : 0); _ = bo.TryEnter(); _ = bo.RemainingBackoffMicros(); }
                }
            });
            Assert.Equal(0, a);
            Assert.True(granted > 0 && denied > 0);
        }

        // ════════════ §4 백오프 ════════════

        [Fact]
        public void Backoff_Steps_Doubling_Capped_WithBoundedJitter()
        {
            long[] expected = { 100_000, 200_000, 400_000, 800_000, 1_600_000, 3_000_000, 3_000_000, 3_000_000 };
            for (int n = 1; n <= expected.Length; n++) Assert.Equal(expected[n - 1], BrokerBackoffController.BaseDelayMicros(n));
            var c = new Clock();
            var bo = new BrokerBackoffController(c.Now, Us, latchThreshold: int.MaxValue, jitterSeed: 42);
            for (int n = 1; n <= expected.Length; n++)
            {
                bo.NoteLimitExceeded();
                long d = bo.LastDelayMicros, lo = expected[n - 1], hi = Math.Min(lo + lo / 4, BrokerBackoffController.MaxDelayMicros);
                Assert.InRange(d, lo, hi);
                Assert.False(bo.TryEnter());                                      // 창 시작 직후 거부
                c.Advance(d - 1);
                Assert.False(bo.TryEnter());
                c.Advance(1);
                Assert.True(bo.TryEnter());                                       // 정확히 지연 경과 = 허가
            }
            Assert.Equal(expected.Length, bo.Consecutive);
        }

        [Fact]
        public void Backoff_Jitter_Deterministic_PerSeed()
        {
            static long[] Run(ulong seed)
            {
                var c = new Clock();
                var bo = new BrokerBackoffController(c.Now, Us, latchThreshold: int.MaxValue, jitterSeed: seed);
                var d = new long[5];
                for (int i = 0; i < 5; i++) { bo.NoteLimitExceeded(); d[i] = bo.LastDelayMicros; }
                return d;
            }
            Assert.Equal(Run(7), Run(7));
            Assert.NotEqual(Run(7), Run(8));
        }

        [Fact]
        public void Backoff_ThreeConsecutive_StickyLatch_AlarmOnce_RearmNeedsBothKeys()
        {
            var c = new Clock();
            var alarms = new List<long>();
            var bo = new BrokerBackoffController(c.Now, Us, onLatch: alarms.Add);
            Assert.True(bo.Note(429));
            Assert.True(bo.Note(BrokerBackoffController.KiwoomOrderOverload));
            Assert.False(bo.Latched);
            Assert.True(bo.Note(BrokerBackoffController.KiwoomQueryOverload));   // 3회째
            Assert.True(bo.Latched && bo.ArmingBlocked);
            Assert.Equal(new long[] { 3 }, alarms);
            bo.NoteLimitExceeded();
            Assert.Single(alarms);                                                // 경보 1회(전이)
            c.Advance(10 * Us);                                                   // 백오프 창 경과
            Assert.False(bo.TryEnter());                                          // 래치 = sticky
            Assert.True(bo.TryEnterRiskReducing());                               // 취소 경로는 창만 따름
            Assert.True(bo.LatchDenied >= 1);
            Assert.False(bo.Note(200));                                           // 성공도 래치를 풀지 않음
            Assert.False(bo.TryEnter());
            Assert.False(bo.TryRearm(true, false));
            Assert.False(bo.TryRearm(false, true));
            Assert.True(bo.TryRearm(true, true));
            Assert.True(bo.TryEnter());
            Assert.Equal(0, bo.Consecutive);
        }

        [Fact]
        public void Backoff_SuccessResetsConsecutive_NonRateErrorsIgnoredByCore()
        {
            var c = new Clock();
            var bo = new BrokerBackoffController(c.Now, Us);
            bo.NoteLimitExceeded(); bo.NoteLimitExceeded();
            bo.NoteSuccess();
            Assert.Equal(0, bo.Consecutive);
            bo.NoteLimitExceeded();
            Assert.False(bo.Latched);                                             // 연속 끊김 → 1회
            Assert.False(BrokerBackoffController.IsRateLimitCode(-302));
            Assert.False(BrokerBackoffController.IsRateLimitCode(500));
            Assert.True(BrokerBackoffController.IsRateLimitCode(-308) && BrokerBackoffController.IsRateLimitCode(-200) && BrokerBackoffController.IsRateLimitCode(429));
        }

        [Fact]
        public void Backoff_OverlappingHits_NeverShortenWindow()
        {
            var c = new Clock();
            var bo = new BrokerBackoffController(c.Now, Us, latchThreshold: int.MaxValue);
            for (int i = 0; i < 5; i++) bo.NoteLimitExceeded();                  // 1.6s 이상
            long longWin = bo.RemainingBackoffMicros();
            bo.NoteSuccess();
            bo.NoteLimitExceeded();                                               // 새 연속 1회 = 100ms — 기존 창을 줄이지 않음
            Assert.True(bo.RemainingBackoffMicros() >= longWin);
        }

        // ════════════ §5 키움 코어 결선 ════════════

        private sealed class KiwoomRig
        {
            public readonly Clock Clock = new();
            public readonly FakeOcx Ocx = new();
            public readonly List<FillPayload> Fills = new();
            public KiwoomComEventCore Core = null!;
            public BrokerBackoffController Backoff = null!;
        }

        private static KiwoomRig MakeKiwoom()
        {
            var r = new KiwoomRig();
            r.Core = new KiwoomComEventCore(r.Ocx, null, r.Fills.Add, null, null, null, () => r.Clock.T * 10, mapCapacityPow2: 64);
            r.Backoff = new BrokerBackoffController(r.Clock.Now, Us);
            r.Core.AttachRateGate(KiwoomComEventCore.CreateKiwoomLimiter(r.Clock.Now, Us), KiwoomComEventCore.CreateKiwoomLimiter(r.Clock.Now, Us), r.Backoff);
            return r;
        }

        [Fact]
        public void Kiwoom_SixOrdersSameInstant_FiveReachOcx_SixthRejectedImmediately()
        {
            var r = MakeKiwoom();
            for (int i = 1; i <= 5; i++) Assert.Equal(0, r.Core.SendOrder(i, "5001", "8012345611", 5930, 1, 70_000, 1));
            Assert.Equal(KiwoomComEventCore.RcRateGated, r.Core.SendOrder(6, "5001", "8012345611", 5930, 1, 70_000, 1));
            Assert.Equal(5, r.Ocx.SentRq.Count);                                  // ★OCX 도달 5건
            var rej = Assert.Single(r.Fills);
            Assert.Equal(6, rej.OrderId);
            Assert.Equal(BridgeFillStatus.Rejected, BridgeFillStatusCodec.Decode(in rej, out _));
            Assert.Equal(1, r.Core.RateGated);
            r.Clock.Advance(LockFreeTokenBucket.SecondMicros);
            Assert.Equal(0, r.Core.SendOrder(7, "5001", "8012345611", 5930, 1, 70_000, 1));   // 창 경과 = 재허가(재송신은 호출측 새 주문 · 자동 재시도 0)
            Assert.Equal(6, r.Ocx.SentRq.Count);
            Assert.DoesNotContain("RQ6", r.Ocx.SentRq);
        }

        [Fact]
        public void Kiwoom_Overload308_ThreeTimes_LatchesNewOrders_CancelStillPassesAfterWindow()
        {
            var r = MakeKiwoom();
            Assert.Equal(0, r.Core.SendOrder(1, "5001", "8012345611", 5930, 1, 70_000, 10));
            r.Ocx.OrderNoReply = "0000123";
            r.Core.OnTrData("RQ1", "KOA_NORMAL_BUY_KP_ORD");                       // 원주문 사상(취소 대상)
            r.Ocx.SendRc = BrokerBackoffController.KiwoomOrderOverload;
            for (int i = 0; i < 3; i++)
            {
                r.Clock.Advance(4 * Us);                                          // 백오프 창 밖에서 재시도(호출측 새 요청)
                Assert.Equal(-308, r.Core.SendOrder(10 + i, "5001", "8012345611", 5930, 1, 70_000, 1));
            }
            Assert.True(r.Backoff.Latched);
            r.Ocx.SendRc = 0;
            int before = r.Ocx.SentRq.Count;
            r.Clock.Advance(10 * Us);
            Assert.Equal(KiwoomComEventCore.RcRateGated, r.Core.SendOrder(20, "5001", "8012345611", 5930, 1, 70_000, 1));
            Assert.Equal(KiwoomAmendResult.RateLimited, r.Core.ModifyOrder(1, 71_000, 5));
            Assert.Equal(before, r.Ocx.SentRq.Count);                             // ★래치 후 신규/정정 OCX 0
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(1));          // 위험 감소 = 래치 무시
            Assert.Equal(before + 1, r.Ocx.SentRq.Count);
            Assert.Equal("RC1", r.Ocx.SentRq[^1]);
        }

        [Fact]
        public void Kiwoom_Cancel_RespectsBackoffWindow_AndRateWindow()
        {
            var r = MakeKiwoom();
            Assert.Equal(0, r.Core.SendOrder(1, "5001", "8012345611", 5930, 1, 70_000, 10));
            r.Ocx.OrderNoReply = "0000123";
            r.Core.OnTrData("RQ1", "KOA_NORMAL_BUY_KP_ORD");
            r.Backoff.NoteLimitExceeded();                                        // 100ms+ 창
            Assert.Equal(KiwoomAmendResult.RateLimited, r.Core.CancelOrder(1));   // 창 안 = 취소도 거부(서버 과부하 가중 금지)
            r.Clock.Advance(Us);
            Assert.Equal(KiwoomAmendResult.Sent, r.Core.CancelOrder(1));
        }

        [Fact]
        public void Kiwoom_QueryAccount_ConsumesTwoTokens_SixthTrDenied()
        {
            var r = MakeKiwoom();
            Assert.Equal(0, r.Core.QueryAccount("8012345611", "5001"));            // 2 TR
            Assert.Equal(0, r.Core.QueryAccount("8012345611", "5001"));            // 4 TR
            Assert.Equal(KiwoomComEventCore.RcRateGated, r.Core.QueryAccount("8012345611", "5001"));   // 5번째 허가 · 6번째 거부 → 송신 0
            Assert.Equal(4, r.Ocx.Requests.Count);
            Assert.Equal(KiwoomComEventCore.RcRateGated, r.Core.QueryOpenOrders("8012345611", "5001"));   // 창 내 6번째 = 거부
            r.Clock.Advance(Us);
            Assert.Equal(0, r.Core.QueryOpenOrders("8012345611", "5001"));
            Assert.Equal(5, r.Ocx.Requests.Count);
        }

        [Fact]
        public void Kiwoom_Unattached_LegacyBehavior_Unchanged()
        {
            var ocx = new FakeOcx();
            var core = new KiwoomComEventCore(ocx, null, null, null, null, null, () => 0, mapCapacityPow2: 64);
            for (int i = 1; i <= 20; i++) Assert.Equal(0, core.SendOrder(i, "5001", "8012345611", 5930, 1, 70_000, 1));
            Assert.Equal(20, ocx.SentRq.Count);
            Assert.Null(core.OrderLimiter);
        }

        // ════════════ §6 KIS REST 결선(스텁 핸들러 · 실 네트워크 0) ════════════

        private sealed class StubHandler : HttpMessageHandler
        {
            public int Quotes, Tokens;
            public HttpStatusCode QuoteStatus = HttpStatusCode.OK;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            {
                string path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/oauth2/tokenP", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref Tokens);
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"t\"}", Encoding.UTF8, "application/json") });
                }
                Interlocked.Increment(ref Quotes);
                return Task.FromResult(new HttpResponseMessage(QuoteStatus)
                { Content = new StringContent("{\"output\":{\"stck_prpr\":\"70000\",\"prdy_vrss\":\"100\",\"prdy_vrss_sign\":\"2\"}}", Encoding.UTF8, "application/json") });
            }
        }

        private static KisProvider MakeKis(StubHandler h, Clock c, out BrokerBackoffController bo)
        {
            bo = new BrokerBackoffController(c.Now, Us);
            return new KisProvider(new KisConfig { AppKey = "k", AppSecret = "s", IsPaper = true }, new HttpClient(h),
                                   new LockFreeTokenBucket(KisProvider.DefaultPerSecond, LockFreeTokenBucket.SecondMicros, nowTicks: c.Now, tickFrequency: Us), bo);
        }

        [Fact]
        public async Task Kis_SixQuotesSameInstant_FiveReachServer()
        {
            var h = new StubHandler();
            var c = new Clock();
            var kis = MakeKis(h, c, out _);
            int ok = 0;
            for (int i = 0; i < 6; i++) if (await kis.GetQuoteAsync("005930", Market.KRX_KOSPI) is not null) ok++;
            Assert.Equal((5, 5), (ok, h.Quotes));
            Assert.Equal(1, kis.RateGated);
            c.Advance(Us);
            Assert.NotNull(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
            Assert.Equal(6, h.Quotes);
        }

        [Fact]
        public async Task Kis_Http429_ThreeTimes_Latches_NoFurtherServerCalls()
        {
            var h = new StubHandler { QuoteStatus = (HttpStatusCode)429 };
            var c = new Clock();
            var kis = MakeKis(h, c, out var bo);
            for (int i = 0; i < 3; i++) { Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI)); c.Advance(4 * Us); }
            Assert.True(bo.Latched);
            Assert.Equal(3, h.Quotes);
            h.QuoteStatus = HttpStatusCode.OK;
            c.Advance(60 * Us);
            Assert.Null(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
            Assert.Empty(await kis.GetHistoryAsync("005930", Market.KRX_KOSPI, DateTime.Today.AddDays(-5), DateTime.Today));
            Assert.Equal(3, h.Quotes);                                            // ★래치 후 서버 도달 0
            Assert.True(bo.TryRearm(true, true));
            Assert.NotNull(await kis.GetQuoteAsync("005930", Market.KRX_KOSPI));
        }

        // ════════════ §7 Fail-Closed IL ════════════

        private static bool Allowed(Type t)
        {
            while (t.IsArray || t.IsByRef || t.IsPointer) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) { if (!t.GetGenericArguments().All(Allowed)) return false; t = t.GetGenericTypeDefinition(); }
            if (t.IsGenericParameter || t.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)) return true;
            if (t.DeclaringType is { } d) return Allowed(d.IsGenericType ? d.GetGenericTypeDefinition() : d);
            if (t == typeof(LockFreeTokenBucket) || t == typeof(BrokerBackoffController)) return true;
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal)
                || (ns == "System.Threading" && (t == typeof(Interlocked) || t == typeof(Volatile)))
                || t == typeof(System.Diagnostics.Stopwatch);
        }

        [Fact]
        public void Gates_Il_References_Only_Allowlisted_Types_NoOrderSurface()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            foreach (var type in new[] { typeof(LockFreeTokenBucket), typeof(BrokerBackoffController) }.SelectMany(t => t.GetNestedTypes(All).Prepend(t)))
            {
                foreach (var f in type.GetFields(All)) if (!Allowed(f.FieldType)) bad.Add(type.Name + "." + f.Name + " : " + f.FieldType.FullName);
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var rm in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = rm as Type ?? rm.DeclaringType;
                        if (owner is not null && !Allowed(owner)) bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + rm.Name);
                    }
                foreach (var n in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(x => x is MethodBase or PropertyInfo or EventInfo).Select(x => x.Name))   // 상수(코드 분류값) 제외
                    foreach (var banned in new[] { "Submit", "Place", "Order", "Send", "Dispatch", "Broker" })
                        if (n.Contains(banned, StringComparison.OrdinalIgnoreCase)) bad.Add("공개 멤버 " + type.Name + "." + n);
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
