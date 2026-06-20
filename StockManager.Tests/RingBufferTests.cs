// ============================================================================
// RingBufferTests — S20 #1: 락-프리 SPMC 링버퍼 검증 (15개)
//
//  §1 구성 (2)        — 2의 거듭제곱 강제, 초기 상태
//  §2 단일스레드 (6)  — 빈버퍼·1건·용량내 전건·다중소비자·래핑드롭·Published
//  §3 동시성 (4)      — SPMC 무torn·비추월 전건수신·추월 무torn·독립커서
//  §4 패딩·래핑 (3)   — 캐시라인 패딩 크기, 래핑 추적, 드롭 계측
//
//  Stamp 는 3필드(N·A·B)가 N 에서 파생 — torn read 시 IsConsistent 가 깨져 검출.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class RingBufferTests
    {
        // ── torn read 검출용 3필드 구조체 ──────────────────────────────────────────
        private readonly struct Stamp
        {
            public readonly long N, A, B;
            public Stamp(long n) { N = n; A = n * 2654435761L; B = (n << 20) ^ (n + 0x5DEECE66DL); }
            public bool IsConsistent => A == N * 2654435761L && B == ((N << 20) ^ (N + 0x5DEECE66DL));
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs = 5000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; Thread.Sleep(2); }
            return cond();
        }

        // 스트레스 스레드는 BelowNormal — 병렬 실행되는 타이밍 민감 테스트(이웃)를 굶기지 않음
        private static Thread LowPrioThread(Action body)
            => new Thread(() => body()) { IsBackground = true, Priority = ThreadPriority.BelowNormal };

        // ════════════════════════ §1 구성 ════════════════════════════════════════

        [Fact]
        public void Ctor_NonPowerOfTwo_Throws()
        {
            Assert.Throws<ArgumentException>(() => new RingBufferCore<Stamp>(3));
            Assert.Throws<ArgumentException>(() => new RingBufferCore<Stamp>(0));
            Assert.Throws<ArgumentException>(() => new RingBufferCore<Stamp>(1));
            Assert.Throws<ArgumentException>(() => new RingBufferCore<Stamp>(-8));
        }

        [Fact]
        public void Ctor_PowerOfTwo_InitialState()
        {
            var rb = new RingBufferCore<Stamp>(8);
            Assert.Equal(8, rb.Capacity);
            Assert.Equal(-1, rb.Published);
        }

        // ════════════════════════ §2 단일스레드 ═══════════════════════════════════

        [Fact]
        public void EmptyBuffer_TryRead_False()
        {
            var rb = new RingBufferCore<Stamp>(8);
            var c  = rb.CreateConsumer();
            Assert.False(c.TryRead(out _));
        }

        [Fact]
        public void PublishOne_Consumer_Reads()
        {
            var rb = new RingBufferCore<Stamp>(8);
            var c  = rb.CreateConsumer();
            rb.Publish(new Stamp(0));

            Assert.True(c.TryRead(out var item));
            Assert.Equal(0, item.N);
            Assert.True(item.IsConsistent);
            Assert.False(c.TryRead(out _));   // 두 번째는 없음
        }

        [Fact]
        public void PublishWithinCapacity_ReadsAllInOrder_ZeroDropped()
        {
            var rb = new RingBufferCore<Stamp>(8);
            var c  = rb.CreateConsumer();
            for (int i = 0; i < 6; i++) rb.Publish(new Stamp(i));

            for (int i = 0; i < 6; i++)
            {
                Assert.True(c.TryRead(out var item));
                Assert.Equal(i, item.N);
                Assert.True(item.IsConsistent);
            }
            Assert.False(c.TryRead(out _));
            Assert.Equal(0, c.Dropped);
        }

        [Fact]
        public void MultipleConsumers_EachReadsFullStream()
        {
            var rb = new RingBufferCore<Stamp>(8);
            var c1 = rb.CreateConsumer();
            var c2 = rb.CreateConsumer();
            for (int i = 0; i < 6; i++) rb.Publish(new Stamp(i));

            for (int i = 0; i < 6; i++)
            {
                Assert.True(c1.TryRead(out var a)); Assert.Equal(i, a.N);
                Assert.True(c2.TryRead(out var b)); Assert.Equal(i, b.N);
            }
            Assert.False(c1.TryRead(out _));
            Assert.False(c2.TryRead(out _));
        }

        [Fact]
        public void Lapping_SmallCapacity_DropsOldButReadsValid()
        {
            var rb = new RingBufferCore<Stamp>(4);
            var c  = rb.CreateConsumer();
            for (int i = 0; i < 20; i++) rb.Publish(new Stamp(i));   // 추월 — 소비자 미독출

            long last = -1; int reads = 0;
            while (c.TryRead(out var item))
            {
                Assert.True(item.IsConsistent);         // torn 아님
                Assert.True(item.N > last);             // 단조 증가
                last = item.N; reads++;
            }
            Assert.Equal(19, last);                     // 최신은 반드시 읽힘
            Assert.True(c.Dropped > 0);                 // 오래된 것은 유실
            Assert.True(reads <= 4);                    // 용량 이내만 잔존
        }

        [Fact]
        public void Published_ReflectsLatestSequence()
        {
            var rb = new RingBufferCore<Stamp>(8);
            for (int i = 0; i < 5; i++) rb.Publish(new Stamp(i));
            Assert.Equal(4, rb.Published);
        }

        // ════════════════════════ §3 동시성 ═══════════════════════════════════════

        [Fact]
        public void Spmc_Stress_NoTornReads_MonotonicPerConsumer()
        {
            const int N = 20_000;
            var rb = new RingBufferCore<Stamp>(1024);   // 작은 용량 → 추월 빈발
            var errors = new ConcurrentQueue<string>();
            var done = false;

            var consumers = new List<Thread>();
            var readCounts = new int[4];
            for (int t = 0; t < 4; t++)
            {
                int ti = t;
                var c = rb.CreateConsumer();
                consumers.Add(LowPrioThread(() =>
                {
                    long last = -1;
                    while (!Volatile.Read(ref done) || c.Cursor < rb.Published)
                    {
                        if (c.TryRead(out var item))
                        {
                            if (!item.IsConsistent) { errors.Enqueue($"torn N={item.N}"); return; }
                            if (item.N <= last)     { errors.Enqueue($"non-monotonic {item.N}<={last}"); return; }
                            last = item.N; readCounts[ti]++;
                        }
                    }
                }));
            }
            foreach (var th in consumers) th.Start();

            var producer = LowPrioThread(() =>
            {
                for (int i = 0; i < N; i++) rb.Publish(new Stamp(i));
                Volatile.Write(ref done, true);
            });
            producer.Start();

            producer.Join(10000);
            foreach (var th in consumers) th.Join(10000);

            Assert.Empty(errors);
            for (int t = 0; t < 4; t++) Assert.True(readCounts[t] > 0, $"consumer {t} read nothing");
        }

        [Fact]
        public void Spmc_NonLapping_AllConsumersReceiveEveryItem()
        {
            const int N = 10_000;
            var rb = new RingBufferCore<Stamp>(1 << 16);   // 용량 > 항목 → 추월 없음
            var errors = new ConcurrentQueue<string>();

            var consumers = new List<Thread>();
            var counts = new int[3];
            for (int t = 0; t < 3; t++)
            {
                int ti = t;
                var c = rb.CreateConsumer();
                consumers.Add(LowPrioThread(() =>
                {
                    long expect = 0;
                    while (expect < N)
                    {
                        if (c.TryRead(out var item))
                        {
                            if (item.N != expect || !item.IsConsistent)
                            { errors.Enqueue($"expect {expect} got {item.N} ok={item.IsConsistent}"); return; }
                            expect++; counts[ti]++;
                        }
                    }
                }));
            }
            foreach (var th in consumers) th.Start();

            var producer = LowPrioThread(() => { for (int i = 0; i < N; i++) rb.Publish(new Stamp(i)); });
            producer.Start(); producer.Join(10000);

            bool finished = WaitFor(() => counts[0] == N && counts[1] == N && counts[2] == N, 10000);
            foreach (var th in consumers) th.Join(10000);

            Assert.Empty(errors);
            Assert.True(finished, $"counts={counts[0]},{counts[1]},{counts[2]}");
            for (int t = 0; t < 3; t++) Assert.Equal(N, counts[t]);   // 비추월 → 전건 수신(드롭 0 함의)
        }

        [Fact]
        public void LappedConsumer_UnderStress_NeverTorn()
        {
            const int N = 20_000;
            var rb = new RingBufferCore<Stamp>(16);   // 매우 작은 용량 → 격심한 추월
            var errors = new ConcurrentQueue<string>();
            var done = false;
            int reads = 0;

            var slow = rb.CreateConsumer();
            var reader = LowPrioThread(() =>
            {
                long last = -1;
                while (!Volatile.Read(ref done) || slow.Cursor < rb.Published)
                {
                    if (slow.TryRead(out var item))
                    {
                        if (!item.IsConsistent) { errors.Enqueue($"torn N={item.N}"); return; }
                        if (item.N <= last)     { errors.Enqueue($"non-monotonic {item.N}<={last}"); return; }
                        last = item.N; reads++;
                        Thread.SpinWait(20);   // 느린 소비자 — 추월 유발
                    }
                }
            });
            reader.Start();

            var producer = LowPrioThread(() =>
            {
                for (int i = 0; i < N; i++) rb.Publish(new Stamp(i));
                Volatile.Write(ref done, true);
            });
            producer.Start();
            producer.Join(15000); reader.Join(15000);

            Assert.Empty(errors);            // 격심한 추월에도 단 1건의 torn read 없음
            Assert.True(reads > 0);
            Assert.True(slow.Dropped > 0);   // 다수 유실(추월)
        }

        [Fact]
        public void Consumers_HaveIndependentCursors()
        {
            var rb   = new RingBufferCore<Stamp>(8);
            var fast = rb.CreateConsumer();
            var slow = rb.CreateConsumer();
            for (int i = 0; i < 4; i++) rb.Publish(new Stamp(i));

            Assert.True(fast.TryRead(out _));
            Assert.True(fast.TryRead(out _));   // fast 는 2건 소비
            Assert.Equal(1, fast.Cursor);
            Assert.Equal(-1, slow.Cursor);      // slow 는 독립적으로 미소비
        }

        // ════════════════════════ §4 패딩·래핑·드롭 ═══════════════════════════════

        [Fact]
        public void PaddedSequence_OccupiesOwnCacheLine()
        {
            // 거짓 공유 차단 의도 — 시퀀스가 단독 캐시 라인(≥64B)을 점유
            Assert.Equal(128, Marshal.SizeOf<PaddedSequence>());
        }

        [Fact]
        public void WrapAround_ConsumerKeepingPace_ReadsAllAcrossWraps()
        {
            var rb = new RingBufferCore<Stamp>(4);
            var c  = rb.CreateConsumer();

            // 소비자가 보조를 맞추면 용량(4)을 여러 바퀴 넘겨도 전건 수신·0 드롭
            long expect = 0;
            for (int i = 0; i < 12; i++)   // 3바퀴
            {
                rb.Publish(new Stamp(i));
                Assert.True(c.TryRead(out var item));
                Assert.Equal(expect, item.N);
                Assert.True(item.IsConsistent);
                expect++;
            }
            Assert.Equal(0, c.Dropped);
        }

        [Fact]
        public void Dropped_AccountsSkippedSequences()
        {
            var rb = new RingBufferCore<Stamp>(4);
            var c  = rb.CreateConsumer();
            for (int i = 0; i < 10; i++) rb.Publish(new Stamp(i));   // 0..9, 용량 4

            Assert.True(c.TryRead(out var first));
            // 가장 오래된 유효 = published(9) - cap(4) + 1 = 6
            Assert.Equal(6, first.N);
            Assert.Equal(6, c.Dropped);   // 0~5 유실
        }
    }
}
