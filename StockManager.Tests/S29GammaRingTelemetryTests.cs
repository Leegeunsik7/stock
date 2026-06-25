using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S29 Path Gamma — SPMC 링 → UI 락-프리 단방향 텔레메트리.
    //   §1 seqlock(round-trip·torn 멸절·버전 단조) §2 무할당·비블록 §3 캐시라인 격리 §4 _writerCollisions 계상
    //   §5 퍼블리셔 스냅샷·처리량(주입 클럭)·널가드.
    [Collection("ConcurrencyStress")]   // v4.58.0 — 헤비 스트레스 직렬화(Barrier 100k·torn 200k 초과 구독 방지)
    public sealed class S29GammaRingTelemetryTests
    {
        // 검증 가능한 필드 간 불변식을 심은 프레임: Forwarded=P*2 ∧ GatedOut=P*3 ∧ Dropped=P*4 ∧ Collisions=P*5.
        private static RingTelemetry R(long p) => new(p, p * 2, p * 3, p * 4, p * 5, p);

        // ═══════════════════ §1 seqlock ═══════════════════

        [Fact]
        public void PublishTryRead_RoundTrip_AllFields()
        {
            var ch = new RingTelemetryChannel();
            ch.Publish(new RingTelemetry(7, 14, 21, 28, 35, 123.5));
            Assert.True(ch.TryRead(out var t));
            Assert.Equal(7,  t.Published);
            Assert.Equal(14, t.Forwarded);
            Assert.Equal(21, t.GatedOut);
            Assert.Equal(28, t.Dropped);
            Assert.Equal(35, t.WriterCollisions);
            Assert.Equal(123.5, t.ThroughputPerSec);
            Assert.Equal(2, ch.Version);   // 1회 발행 → 커서 2(짝)
        }

        [Fact]
        public void TryRead_BeforeAnyPublish_DefaultStable()
        {
            var ch = new RingTelemetryChannel();
            Assert.True(ch.TryRead(out var t));   // 버전 0(짝)·default — torn 아님
            Assert.False(t.HasActivity);
            Assert.Equal(0, ch.Version);
        }

        [Fact]
        public void Version_Monotonic_TwoPerPublish()
        {
            var ch = new RingTelemetryChannel();
            for (long s = 1; s <= 1000; s++) ch.Publish(R(s));
            Assert.Equal(2000, ch.Version);   // 발행마다 +2, 항상 짝
        }

        [Fact]
        public void TornRead_Melted_UnderConcurrentPublish()
        {
            // 단일 라이터 고빈도 발행 중 2 리더가 무할당 독출 — 필드 간 불변식이 깨지면 torn.
            //   비공허성: okReads(발행 중 성공 독출)·maxSeq(추격한 최신 발행) 집계로 '발행 폭풍 한가운데서 무수히 읽음' 단언.
            var ch = new RingTelemetryChannel();
            long torn = 0, okReads = 0, maxSeq = 0; bool stop = false;
            void Reader()
            {
                while (!Volatile.Read(ref stop))
                {
                    if (ch.TryRead(out var t) && t.Published >= 1)
                    {
                        Interlocked.Increment(ref okReads);
                        if (t.Published > Interlocked.Read(ref maxSeq)) Interlocked.Exchange(ref maxSeq, t.Published);
                        if (t.Forwarded != t.Published * 2 || t.GatedOut != t.Published * 3 ||
                            t.Dropped   != t.Published * 4 || t.WriterCollisions != t.Published * 5)
                            Interlocked.Increment(ref torn);
                    }
                }
            }
            var r1 = new Thread(Reader) { IsBackground = true };
            var r2 = new Thread(Reader) { IsBackground = true };
            r1.Start(); r2.Start();
            for (long s = 1; s <= 200000; s++) { ch.Publish(R(s)); if ((s & 0x3FF) == 0) Thread.Yield(); }
            Volatile.Write(ref stop, true); r1.Join(15000); r2.Join(15000);
            Assert.Equal(0, torn);                                  // torn read 0(불변식 무손상)
            Assert.True(okReads > 1000, $"동시 독출 비공허 — okReads={okReads}");
            Assert.True(maxSeq  > 1000, $"리더가 발행 폭풍 추격 — maxSeq={maxSeq}");
        }

        // ═══════════════════ §2 무할당 · 비블록 ═══════════════════

        [Fact]
        public void PublishTryRead_ZeroAllocation()
        {
            var ch = new RingTelemetryChannel();
            var t = R(7);
            for (int i = 0; i < 10_000; i++) { ch.Publish(t); ch.TryRead(out _); }   // 워밍업(tiered 안정화)
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++) { ch.Publish(t); ch.TryRead(out _); }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);   // 값타입 in/out — 박싱·힙 0
        }

        [Fact]
        public void Reader_NeverBlocks_StressReturnsPromptly()
        {
            var ch = new RingTelemetryChannel();
            bool stop = false; long reads = 0;
            var reader = new Thread(() => { while (!Volatile.Read(ref stop)) { ch.TryRead(out _); Interlocked.Increment(ref reads); } }) { IsBackground = true };
            reader.Start();
            for (long s = 1; s <= 200000; s++) ch.Publish(R(s));
            Volatile.Write(ref stop, true);
            Assert.True(reader.Join(15000));   // 완주 = 비블록
            Assert.True(Interlocked.Read(ref reads) > 0);
        }

        // ═══════════════════ §3 캐시라인 격리 ═══════════════════

        [Fact]
        public void CacheLine_FieldOffsets_Declared_PaddingRegressionGuard()
        {
            // 선언적 FieldOffset(의도) 잠금 — 패딩 축소 회귀 방지. 런타임 절대정렬·false-sharing 부재 증명 아님(정직).
            var cell = typeof(RingTelemetryChannel).GetNestedType("Cell", BindingFlags.NonPublic);
            Assert.NotNull(cell);
            int vOff = cell!.GetField("Version")!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            int dOff = cell.GetField("Data")!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;
            Assert.True(vOff >= 64, "Version 앞 선행 64B 전용 패딩 선언");
            Assert.True(dOff - vOff >= 64, "Version↔Data 별도 캐시라인 선언");
        }

        // ═══════════════════ §4 _writerCollisions 계상 ═══════════════════

        [Fact]
        public void WriterCollisions_ExactlyCountsConcurrentProducerViolations()
        {
            // 단일 생산자 계약 위반(동시 Publish)을 Barrier 로 동시 진입시켜 유발. CAS 낙방마다 카운터+throw 가 1:1.
            //   ★결정론 단언: WriterCollisions == 실제로 잡은 예외 수(충돌 횟수와 무관하게 정확 계상). Barrier 로 비공허(>0).
            var ring = new OrderDispatchRing(1024);
            long exceptions = 0;
            const int Rounds = 100_000;
            using var barrier = new Barrier(2);
            void Hammer()
            {
                for (int i = 0; i < Rounds; i++)
                {
                    barrier.SignalAndWait();   // 두 스레드 동시 진입 강제 → 충돌 확률 극대화
                    try { ring.Publish(1, 1, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), i); }
                    catch (InvalidOperationException) { Interlocked.Increment(ref exceptions); }
                }
            }
            var t1 = new Thread(Hammer) { IsBackground = true };
            var t2 = new Thread(Hammer) { IsBackground = true };
            t1.Start(); t2.Start(); t1.Join(30000); t2.Join(30000);
            Assert.Equal(exceptions, ring.WriterCollisions);   // 정확 계상(1:1)
            Assert.True(ring.WriterCollisions > 0, $"Barrier 동시 진입으로 충돌 비공허 입증 — collisions={ring.WriterCollisions}");
            Assert.True(ring.Published >= 0, "정당 발행도 다수 성공");
        }

        // ═══════════════════ §5 퍼블리셔 ═══════════════════

        [Fact]
        public void Publisher_SnapshotsRingAndDispatcherCounters()
        {
            var ring = new OrderDispatchRing(8);
            var dispatcher = new GatedOrderDispatcher(ring, () => false);   // 미무장 → 전부 GatedOut
            var channel = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, dispatcher, channel);

            ring.Publish(1, 1001, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), 100);
            ring.Publish(1, 1001, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), 101);
            dispatcher.Drain(_ => { });   // 미무장 → GatedOut 2, Forwarded 0

            pub.Sample();
            Assert.True(channel.TryRead(out var f));
            Assert.Equal(1, f.Published);    // 2 발행 → 0-based 시퀀스 1
            Assert.Equal(0, f.Forwarded);
            Assert.Equal(2, f.GatedOut);
            Assert.Equal(0, f.Dropped);
            Assert.Equal(0, f.WriterCollisions);
            Assert.True(f.HasActivity);
        }

        [Fact]
        public void Publisher_Throughput_FromInjectedClock()
        {
            long ticks = 0;
            var ring = new OrderDispatchRing(64);
            var dispatcher = new GatedOrderDispatcher(ring, () => false);
            var channel = new RingTelemetryChannel();
            // 주입 클럭으로 ctor 시점 lastTicks=0, lastPublished=-1(빈 링) 고정.
            var pub = new RingTelemetryPublisher(ring, dispatcher, channel, () => Volatile.Read(ref ticks));

            for (int i = 0; i < 10; i++) ring.Publish(1, 1, OrderSide.Buy, 1, QPrice.Of(1m), QQty.Of(1m), i);
            Volatile.Write(ref ticks, Stopwatch.Frequency);   // 정확히 1초 경과
            pub.Sample();

            Assert.True(channel.TryRead(out var f));
            // dPub = Published(9) - lastPublished(-1) = 10, 경과 1초 → 10 건/초.
            Assert.Equal(10.0, f.ThroughputPerSec, 3);
            Assert.Equal(9, f.Published);
        }

        [Fact]
        public void Publisher_NullArgs_Throw()
        {
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false);
            var ch = new RingTelemetryChannel();
            Assert.Throws<ArgumentNullException>(() => new RingTelemetryPublisher(null!, disp, ch));
            Assert.Throws<ArgumentNullException>(() => new RingTelemetryPublisher(ring, null!, ch));
            Assert.Throws<ArgumentNullException>(() => new RingTelemetryPublisher(ring, disp, null!));
        }
    }
}
