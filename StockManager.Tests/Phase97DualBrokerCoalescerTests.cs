// ============================================================================
// Phase97DualBrokerCoalescerTests — v11.6.0 Phase 94-O Part 3 Think-Beyond 적대적 검증
//   ① ZeroAllocDualBrokerCoalescer — 이중 브로커 병합 무손실·순번 정합·포화 드롭·δ=0·다중생산자 경합 무결.
//   ② LongRunningMemoryGcSentinel — 압력 임계 감속·히스테리시스 해제·결정론·δ=0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using MS2E.Core;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class Phase97DualBrokerCoalescerTests
    {
        private readonly ITestOutputHelper _out;
        public Phase97DualBrokerCoalescerTests(ITestOutputHelper o) => _out = o;

        // ── §1 코얼레서 기본 — 발행/드레인·순번·통계 ──────────────────────────────
        [Fact]
        public void Coalescer_Publish_Drain_Preserves_Order_And_Payload()
        {
            var c = new ZeroAllocDualBrokerCoalescer(8);
            Assert.True(c.Publish(0, 5930, 71000_0000, 10));   // 키움 삼성전자
            Assert.True(c.Publish(1, 660, 195000_0000, 3));    // 토스 SK하이닉스
            Assert.Equal(2, c.PendingCount);

            Span<BrokerTick> buf = stackalloc BrokerTick[8];
            int n = c.Drain(buf);
            Assert.Equal(2, n);
            Assert.Equal(0, buf[0].BrokerId);
            Assert.Equal(5930, buf[0].SymbolCode);
            Assert.Equal(71000_0000, buf[0].PriceRaw);
            Assert.Equal(0L, buf[0].Seq);
            Assert.Equal(1, buf[1].BrokerId);
            Assert.Equal(1L, buf[1].Seq);
            Assert.Equal(2, c.Published);
            Assert.Equal(0, c.PendingCount);
            Assert.Equal(0, c.Drain(buf));   // 소진 후 0
        }

        // ── §2 포화 드롭(백프레셔) — 무한 할당 금지 ───────────────────────────────
        [Fact]
        public void Coalescer_Full_Drops_Backpressure()
        {
            var c = new ZeroAllocDualBrokerCoalescer(4);
            for (int i = 0; i < 4; i++) Assert.True(c.Publish(0, i, i, 1));
            Assert.False(c.Publish(0, 99, 99, 1));   // 포화 → 드롭
            Assert.Equal(1, c.Dropped);
            Assert.Equal(4, c.Published);

            Span<BrokerTick> buf = stackalloc BrokerTick[4];
            Assert.Equal(4, c.Drain(buf));
            Assert.True(c.Publish(0, 100, 100, 1));   // 드레인 후 재수용
        }

        // ── §3 GC 센티널 — 정상/임계/히스테리시스(결정론·주입) ─────────────────────
        [Fact]
        public void GcSentinel_Throttles_On_Pressure_Releases_On_Calm()
        {
            var s = new LongRunningMemoryGcSentinel(gen2PerMinThreshold: 60, allocBytesPerSecThreshold: 500_000_000);
            long T = TimeSpan.TicksPerSecond;

            s.Sample(0, 0, 0);                 // 기준선(첫 샘플)
            Assert.False(s.ShouldThrottle);

            // 정상: 1초에 Gen2 0회·할당 1MB → 감속 없음
            s.Sample(1 * T, 0, 1_000_000);
            Assert.False(s.ShouldThrottle);

            // 압력: 1초에 Gen2 2회(=120/min ≥ 60) → 감속 진입
            s.Sample(2 * T, 2, 2_000_000);
            Assert.True(s.ShouldThrottle);
            Assert.Equal(1L, s.Breaches);

            // 진정: 1초에 Gen2 0회·할당 저조(<절반) → 감속 해제(히스테리시스)
            s.Sample(3 * T, 2, 2_100_000);
            Assert.False(s.ShouldThrottle);

            // 할당률 폭주(≥500MB/s)로 재진입
            s.Sample(4 * T, 2, 2_100_000 + 600_000_000);
            Assert.True(s.ShouldThrottle);
            Assert.Equal(2L, s.Breaches);
        }

        [Fact]
        public void GcSentinel_Deterministic_And_FailSafe_NonMonotonic()
        {
            var a = new LongRunningMemoryGcSentinel();
            var b = new LongRunningMemoryGcSentinel();
            long T = TimeSpan.TicksPerSecond;
            (long t, int g, long m)[] seq = { (0, 0, 0), (T, 1, 1_000_000), (2 * T, 5, 900_000_000), (3 * T, 5, 900_100_000) };
            foreach (var x in seq) { a.Sample(x.t, x.g, x.m); b.Sample(x.t, x.g, x.m); }
            Assert.Equal(a.ShouldThrottle, b.ShouldThrottle);   // 동일 주입 → 동일 결과(결정론)
            Assert.Equal(a.Breaches, b.Breaches);

            // 비단조 tick → 무시(fail-safe·상태 불변)
            long br = a.Breaches;
            a.Sample(T, 99, 9_000_000_000);   // 과거 tick
            Assert.Equal(br, a.Breaches);
        }
    }

    // ── §4 적대검증(동시·전용 스레드·δ=0) ──────────────────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Phase97DualBrokerCoalescerConcurrencyTests
    {
        // ★이중 브로커(키움+토스) 동시 폭주 + 단일 소비자 → 무손실(delivered+dropped=published)·순번 유일·페이로드 무결.
        [Fact]
        public void Concurrent_DualBroker_No_Loss_No_Corruption()
        {
            var c = new ZeroAllocDualBrokerCoalescer(1 << 14);   // 16384 슬롯
            const int perBroker = 100_000;
            var delivered = new HashSet<long>();
            long consumed = 0;
            long run = 1;
            Exception? err = null;

            var consumer = new Thread(() =>
            {
                try
                {
                    var buf = new BrokerTick[512];
                    while (Volatile.Read(ref run) == 1 || c.PendingCount > 0)
                    {
                        int n = c.Drain(buf);
                        for (int i = 0; i < n; i++)
                        {
                            var t = buf[i];
                            Assert.True(t.BrokerId == 0 || t.BrokerId == 1);   // 페이로드 무결
                            Assert.True(delivered.Add(t.Seq));                 // 순번 유일(중복 배달 0)
                            consumed++;
                        }
                    }
                }
                catch (Exception e) { err = e; }
            }) { IsBackground = true };
            consumer.Start();

            void Producer(int brokerId)
            {
                for (int i = 0; i < perBroker; i++)
                {
                    var sw = new SpinWait();
                    while (!c.Publish(brokerId, i, (long)i * 100, 1)) sw.SpinOnce();   // 포화 시 재시도(무손실 생산)
                }
            }
            var p0 = new Thread(() => Producer(0)) { IsBackground = true };
            var p1 = new Thread(() => Producer(1)) { IsBackground = true };
            p0.Start(); p1.Start();
            Assert.True(p0.Join(60_000)); Assert.True(p1.Join(60_000));
            Volatile.Write(ref run, 0);
            Assert.True(consumer.Join(60_000));
            Assert.Null(err);

            // ★무손실 불변식: Dropped 는 '데이터 손실'이 아니라 포화 시 재시도(백프레셔) 횟수 — 재시도로 전량 발행되므로
            //   Published/consumed/delivered 가 정확히 일치함이 무손실의 증거(Dropped 는 경합 강도 텔레메트리일 뿐).
            Assert.Equal(2 * perBroker, c.Published);          // 재시도 생산 → 전량 발행(성공분만 계상)
            Assert.Equal(2L * perBroker, consumed);            // ★전량 배달(무손실)
            Assert.Equal(2 * perBroker, delivered.Count);      // 순번 유일(중복/유실 0)
        }

        // ★Publish+Drain 핫패스 힙 할당 0(δ=0) — 사전 할당 링 + stackalloc 드레인.
        [Fact]
        public void Coalescer_PublishDrain_HotPath_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var c = new ZeroAllocDualBrokerCoalescer(1 << 12);
            var th = new Thread(() =>
            {
                try
                {
                    Span<BrokerTick> one = stackalloc BrokerTick[1];
                    for (int i = 0; i < 300_000; i++) { c.Publish(i & 1, i, i, 1); c.Drain(one); }   // 워밍업
                    long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
                    for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
                    {
                        long zb = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 100_000; i++) { c.Publish(i & 1, i, i, 1); c.Drain(one); }
                        minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
                    }
                    alloc = minAlloc;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★δ=0
        }

        // ★ShouldThrottle 핫패스 δ=0.
        [Fact]
        public void GcSentinel_ShouldThrottle_HotPath_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var s = new LongRunningMemoryGcSentinel();
            s.Sample(0, 0, 0); s.Sample(TimeSpan.TicksPerSecond, 0, 1000);
            var th = new Thread(() =>
            {
                try
                {
                    bool sink = false;
                    for (int i = 0; i < 300_000; i++) sink ^= s.ShouldThrottle;
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) sink ^= s.ShouldThrottle;
                    });
                    if (sink && alloc < 0) err = new Exception("unreachable");
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
