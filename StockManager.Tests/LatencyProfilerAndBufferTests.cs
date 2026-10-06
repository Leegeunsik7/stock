// ============================================================================
// LatencyProfilerAndBufferTests — [저지연 · 추천 A] 네이티브 L2 버퍼 · Tick-to-Trade 프로파일러 · 로그 히스토그램(방향 A) · 정지 Disarm(방향 B)
//
//  §1 NativeSeqlockTable/UnmanagedOrderBookBuffer — 10호가 왕복 · 64B 정렬 · 포화 · 처분 후 안전 거부 · 동시 기록/판독 찢김 0.
//  §2 누수 0 — Dispose 즉시 · 미처분(종료자) 경로 모두 LiveAllocations/LiveBytes 기준선 복귀.
//  §3 DepthBook 네이티브 격상 — 판정 동작 동일 · 처분 후 Missing(fail-closed).
//  §4 프로파일러 — µs 환산 정확 · 참조가 틱 나이 합산 · 최근 1,024건 P50/P99 정확 · 히스토그램 P99.9 보수 상한(≤12.5%).
//  §5 [방향 B] 송출 직전 정지 → 미송출 + Disarm · 송출 후 정지 → Disarm · 이중키 재무장 · 집계 비트.
//  §6 E2E — OrderDrainWorker 프로브 결선 · 정지 시 브로커 호출 0 · 슬롯 롤백 · 평균 프로세스 내 지연 < 1,000µs.
//  §7 δ=0 · 호스트 결선.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Buffers;
using StockManager.Engines;
using StockManager.Telemetry;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class LatencyProfilerAndBufferTests
    {
        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static OrderBookPayload Book(long code, long baseVal)
        {
            var b = new OrderBookPayload { SymbolCode = code, TimestampTicks = baseVal };
            for (int i = 0; i < 10; i++) { b.AskPrice[i] = baseVal + i; b.AskQty[i] = baseVal; b.BidPrice[i] = baseVal - i - 1; b.BidQty[i] = baseVal; }
            return b;
        }

        private static void FlushFinalizers()
        {
            for (int i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        }

        // ════════════ §1 네이티브 표 ════════════

        [Fact]
        public void NativeBuffer_RoundTrip10Levels_Overwrite_Missing_Full()
        {
            long now = 1_000;
            using var buf = new UnmanagedOrderBookBuffer(capacityPow2: 4, nowMicros: () => now);
            Assert.Equal(0, buf.SlotBytes % 64);
            var b1 = Book(5930, 70_000);
            Assert.True(buf.Write(in b1));
            now = 1_250;
            Assert.True(buf.TryRead(5930, out var r1, out long age));
            Assert.Equal(250, age);
            Assert.Equal(70_009, r1.AskPrice[9]);
            Assert.Equal(69_990, r1.BidPrice[9]);
            var b2 = Book(5930, 71_000);
            Assert.True(buf.Write(in b2));
            Assert.True(buf.TryRead(5930, out var r2, out _));
            Assert.Equal(71_000, r2.AskPrice[0]);
            Assert.Equal(1, buf.Symbols);
            Assert.False(buf.TryRead(660, out _, out _));
            for (long c = 1; c <= 3; c++) { var b = Book(c, 100); Assert.True(buf.Write(in b)); }
            var over = Book(999, 1);
            Assert.False(buf.Write(in over));                                  // 4 슬롯 포화
        }

        [Fact]
        public void NativeHandle_Aligned64_And_AfterDispose_SafeRejection()
        {
            using (var h = new NativeMemoryHandle(1000))
                Assert.Equal(0, h.DangerousGetHandle().ToInt64() % 64);
            var buf = new UnmanagedOrderBookBuffer(capacityPow2: 8);
            var b = Book(5930, 70_000);
            Assert.True(buf.Write(in b));
            buf.Dispose();
            Assert.True(buf.IsDisposed);
            Assert.False(buf.Write(in b));                                     // ★처분 후 접근 = 거부(use-after-free 0 · 예외 0)
            Assert.False(buf.TryRead(5930, out _, out _));
            buf.Dispose();                                                     // 멱등
        }

        [Fact]
        public void NativeTable_ConcurrentWriteRead_NoTornSnapshot()
        {
            using var t = new NativeSeqlockTable<OrderBookPayload>(16);
            long stop = 0, torn = 0, reads = 0;
            var wr = new Thread(() =>
            {
                for (long v = 1; Volatile.Read(ref stop) == 0; v++) { var b = Book(5930, v); t.Write(5930, in b, v); }
            }) { IsBackground = true };
            var rd = new Thread(() =>
            {
                for (int i = 0; i < 300_000; i++)
                {
                    if (!t.TryRead(5930, out var b, out long at)) continue;
                    reads++;
                    long v = b.TimestampTicks;
                    if (at != v || b.AskQty[9] != v || b.AskPrice[9] != v + 9 || b.BidPrice[9] != v - 10) torn++;
                }
            }) { IsBackground = true };
            wr.Start(); rd.Start();
            Assert.True(rd.Join(60_000));
            Volatile.Write(ref stop, 1);
            Assert.True(wr.Join(10_000));
            Assert.Equal(0, torn);
            Assert.True(reads > 0);
        }

        // ════════════ §2 누수 ════════════

        [Fact]
        public void NativeMemory_NoLeak_DisposeAndFinalizerPaths()
        {
            FlushFinalizers();
            long baseAllocs = NativeMemoryHandle.LiveAllocations, baseBytes = NativeMemoryHandle.LiveBytes;
            for (int i = 0; i < 20; i++)
            {
                using var b = new UnmanagedOrderBookBuffer(64);
                using var d = new DepthBook(64);
                using var p = new TickToTradeLatencyProfiler();
                Assert.True(NativeMemoryHandle.LiveAllocations >= baseAllocs + 3);
            }
            Assert.Equal(baseAllocs, NativeMemoryHandle.LiveAllocations);        // ★Dispose 경로 누수 0
            Assert.Equal(baseBytes, NativeMemoryHandle.LiveBytes);

            CreateAbandoned();                                                 // 미처분 — SafeHandle 임계 종료자가 해제
            Assert.True(NativeMemoryHandle.LiveAllocations > baseAllocs);
            FlushFinalizers();
            Assert.Equal(baseAllocs, NativeMemoryHandle.LiveAllocations);        // ★종료자 경로 누수 0
            Assert.Equal(baseBytes, NativeMemoryHandle.LiveBytes);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void CreateAbandoned()
        {
            for (int i = 0; i < 5; i++) { var b = new UnmanagedOrderBookBuffer(64); var x = Book(1, 1); b.Write(in x); }
        }

        // ════════════ §3 DepthBook 네이티브 격상 ════════════

        [Fact]
        public void DepthBook_Native_SameVerdicts_DisposedIsMissing()
        {
            long now = 1_000;
            var book = new DepthBook(capacityPow2: 16, maxAgeMicros: 1_000, nowMicros: () => now);
            var d = new L2Depth { SymbolCode = 5930 };
            d.AskPriceRaw[0] = 70_000 * 100_000_000L; d.AskQtyRaw[0] = 10 * 100_000_000L;
            d.BidPriceRaw[0] = 69_900 * 100_000_000L; d.BidQtyRaw[0] = 10 * 100_000_000L;
            book.OnDepth(77, in d);
            Assert.Equal(DepthVerdict.Ok, book.Evaluate(77, OrderSide.Buy, 70_000 * 100_000_000L, 5, out long a));
            Assert.Equal(5, a);
            Assert.Equal(DepthVerdict.Reduced, book.Evaluate(77, OrderSide.Buy, 70_000 * 100_000_000L, 50, out a));
            now += 1_001;
            Assert.Equal(DepthVerdict.Stale, book.Evaluate(77, OrderSide.Buy, 70_000 * 100_000_000L, 5, out _));
            book.Dispose();
            Assert.Equal(DepthVerdict.Missing, book.Evaluate(77, OrderSide.Buy, 70_000 * 100_000_000L, 5, out _));   // fail-closed
            book.OnDepth(77, in d);                                            // 처분 후 기록 무해
        }

        // ════════════ §4 프로파일러 ════════════

        private sealed class Clock { public long T = 1_000_000; }

        [Fact]
        public void Profiler_MicrosExact_BasisAgeAdded_RingPercentiles()
        {
            var c = new Clock();
            using var p = new TickToTradeLatencyProfiler(stallThresholdMicros: 1_000_000, nowTimestamp: () => c.T, timestampFrequency: 1_000_000);
            for (int i = 1; i <= 1_000; i++)
            {
                long t0 = p.Timestamp();
                c.T += i;                                                      // 프로세스 내 i µs
                p.RecordSent(t0, basisAgeMicros: 100);
            }
            var inp = p.InProcess();
            Assert.Equal(1_000, inp.Count);
            Assert.Equal(500, inp.P50Micros);
            Assert.Equal(990, inp.P99Micros);
            Assert.Equal(1_000, inp.MaxMicros);
            Assert.Equal(500, inp.MeanMicros);                                 // 500.5 → 정수 평균
            var t2t = p.TickToTrade();
            Assert.Equal(600, t2t.P50Micros);                                  // +틱 나이 100µs
            Assert.Equal(1_100, t2t.MaxMicros);
            Assert.True(inp.P999Micros >= 999 && inp.P999Micros <= 999 * 1125 / 1000 + 1, $"P99.9 버킷 상한 {inp.P999Micros}");
            Assert.False(p.Tripped);
        }

        [Fact]
        public void Profiler_RingKeepsLatest1024()
        {
            var c = new Clock();
            using var p = new TickToTradeLatencyProfiler(1_000_000, () => c.T, 1_000_000);
            for (int i = 0; i < 3_000; i++) { long t0 = p.Timestamp(); c.T += i < 1_000 ? 5_000 : 10; p.RecordSent(t0, -1); }   // 최근 2,000건은 10µs
            var d = p.InProcess();
            Assert.Equal(3_000, d.Count);
            Assert.Equal(10, d.P99Micros);                                     // 최근 1,024건은 전부 10µs
            Assert.Equal(5_000, d.MaxMicros);                                  // 최대값은 전 기간
        }

        [Fact]
        public void Histogram_BucketMath_MonotonicAndBounded()
        {
            int prev = -1;
            for (long v = 0; v < 2_000_000; v = v < 64 ? v + 1 : v + v / 7)
            {
                int b = TickToTradeLatencyProfiler.BucketOf(v);
                Assert.True(b >= prev, $"버킷 역행 {v}");
                prev = b;
                long upper = TickToTradeLatencyProfiler.BucketUpper(b);
                Assert.True(upper >= v, $"상한 {upper} < 값 {v}");
                Assert.True(v < 16 ? upper == v : upper <= v + v / 8 + 1, $"상대 오차 초과 {v} → {upper}");
            }
            int top = TickToTradeLatencyProfiler.BucketOf(long.MaxValue);
            Assert.True(top < TickToTradeLatencyProfiler.HistogramBuckets);
            Assert.Equal(long.MaxValue, TickToTradeLatencyProfiler.BucketUpper(top));   // 최상위 버킷 상한 = long.MaxValue(오버플로 0)
        }

        // ════════════ §5 방향 B ════════════

        [Fact]
        public void Stall_PreSend_Vetoes_And_Disarms_PostSend_Disarms_Rearm()
        {
            var c = new Clock();
            using var p = new TickToTradeLatencyProfiler(stallThresholdMicros: 5_000, nowTimestamp: () => c.T, timestampFrequency: 1_000_000);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.LatencyStall);
            p.BindInterlock(mask, ArmingInterlockSource.LatencyStall);
            Assert.True(mask.Seal(out _));
            long t0 = p.Timestamp(); c.T += 4_999;
            Assert.True(p.AdmitBeforeSend(t0));
            Assert.False(mask.Blocked);
            t0 = p.Timestamp(); c.T += 12_000;                                 // GC 정지 모사
            Assert.False(p.AdmitBeforeSend(t0));                               // ★미송출
            Assert.True(mask.Blocked);
            Assert.Equal(1, p.PreSendStalls);
            Assert.False(p.TryRearm(true, false));
            Assert.True(p.TryRearm(true, true));
            Assert.False(mask.Blocked);
            t0 = p.Timestamp(); c.T += 7_000;
            p.RecordSent(t0, 0);                                               // 송출 구간 정지 — 이미 나간 주문은 취소하지 않고 회로만 차단
            Assert.True(mask.Blocked);
            Assert.Equal(1, p.PostSendStalls);
        }

        // ════════════ §6 E2E ════════════

        private sealed class AdvancingGate : IOrderRouteGate
        {
            private readonly Clock _c; private readonly long _advance;
            public int NotSent, Accepted;
            public AdvancingGate(Clock c, long advance) { _c = c; _advance = advance; }
            public bool TryAdmit(in OrderProposal p, out OrderAdmission a)
            {
                _c.T += _advance;                                              // 게이트 판정 중 지연(스레드 선점 모사)
                a = new OrderAdmission(1, p.PriceRaw, p.QtyRaw, 250);
                return true;
            }
            public void OnBrokerAccepted(long id, string bid) => Accepted++;
            public void OnNotSent(long id) => NotSent++;
        }

        private sealed class StubBroker : IBroker
        {
            public int Calls;
            public string Name => "Stub"; public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
            { Calls++; return (true, "1"); }
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        [Fact]
        public void E2E_DrainWorker_StallBeforeSend_ZeroBrokerCalls_RolledBack()
        {
            var c = new Clock();
            using var p = new TickToTradeLatencyProfiler(5_000, () => c.T, 1_000_000);
            var gate = new AdvancingGate(c, advance: 8_000);
            var ring = new OrderDispatchRing(16);
            ring.Publish(0, 5930, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(1m), 1);
            var broker = new StubBroker();
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
            using var w = new OrderDrainWorker(disp, broker, wired: false, routeGate: gate, latencyProbe: p);
            w.DrainOnce();
            Assert.Equal(0, broker.Calls);                                     // ★지연 폭주 주문 송출 0
            Assert.Equal(1, w.LatencyVetoes);
            Assert.Equal(1, gate.NotSent);                                     // 슬롯 롤백
            Assert.True(p.Tripped);
        }

        [Fact]
        public void E2E_DrainWorker_RecordsTickToTrade_SubMillisecondInProcess()
        {
            using var p = new TickToTradeLatencyProfiler(stallThresholdMicros: 1_000_000);   // 실 Stopwatch
            var gate = new AdvancingGate(new Clock(), advance: 0);
            var broker = new StubBroker();
            // 예열(JIT) 후 계측
            for (int round = 0; round < 2; round++)
            {
                var ring = new OrderDispatchRing(256);
                for (int i = 0; i < 200; i++) ring.Publish(0, 5930, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(1m), i);
                var disp = new GatedOrderDispatcher(ring, isArmed: () => true, new OrderAttestationService());
                using var w = new OrderDrainWorker(disp, broker, wired: false, routeGate: gate, latencyProbe: round == 0 ? null : p);
                w.DrainOnce();
            }
            var inp = p.InProcess();
            Assert.Equal(200, inp.Count);
            Assert.True(inp.MeanMicros < 1_000, $"평균 프로세스 내 지연 {inp.MeanMicros}µs ≥ 1ms");
            Assert.True(p.TickToTrade().P50Micros >= 250);                    // 틱 나이 250µs 합산
            Assert.Equal(400, broker.Calls);
        }

        // ════════════ §7 δ=0 · 호스트 결선 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            using var buf = new UnmanagedOrderBookBuffer(64);
            using var p = new TickToTradeLatencyProfiler(1_000_000);
            var b = Book(5930, 70_000);
            long sink = 0;
            buf.Write(in b); buf.TryRead(5930, out _, out _);
            long w0 = p.Timestamp(); p.AdmitBeforeSend(w0); p.RecordSent(w0, 10); _ = p.InProcess();
            Assert.Equal(0L, MinAllocated(() =>
            {
                for (int i = 0; i < 20_000; i++)
                {
                    buf.Write(in b);
                    if (buf.TryRead(5930, out var r, out long age)) sink += r.AskPrice[3] + age;
                    long t0 = p.Timestamp();
                    if (p.AdmitBeforeSend(t0)) p.RecordSent(t0, 100);
                }
                sink += p.InProcess().P99Micros + p.TickToTrade().P999Micros;
            }));
            Assert.True(sink > 0);
        }

        [Fact]
        public void Host_WiresProfilerAndNativeBuffer()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(TickToTradeLatencyProfiler).GetMethod(nameof(TickToTradeLatencyProfiler.BindInterlock))!,
                typeof(UnmanagedOrderBookBuffer).GetMethod(nameof(UnmanagedOrderBookBuffer.Write))!,
            };
            var found = new bool[targets.Length];
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var t in types)
            {
                var all = new List<MethodBase>(t.GetMethods(AllF));
                all.AddRange(t.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
            var ctor = typeof(OrderDrainWorker).GetConstructors()[0];
            Assert.Contains(ctor.GetParameters(), x => x.ParameterType == typeof(ITickToTradeProbe));
        }
    }
}
