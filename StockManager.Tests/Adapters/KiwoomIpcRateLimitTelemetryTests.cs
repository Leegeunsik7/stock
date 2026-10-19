// ============================================================================
// KiwoomIpcRateLimitTelemetryTests — [v12.18.22-DEV] x86 키움 프록시 → 호스트 레이트 게이트 관제(MMF · seqlock)
//
//  §1 정합 — 키움 코어 게이트(가상 시계) 판정 → 작성자 게시 → 판독자 Live 사본: 주문 1초/60초 잔여 · 조회 잔여 · 거부 누계 · 연속 초과 · 래치 · 백오프
//  §2 수명 — 맵 부재 = Missing(재시도 간격) · 첫 게시 전 = Missing · 노후(> 2초) = Stale(누계 보존) · 기록 중 종료(seq 홀수 고착) = Torn(유한 · 무블로킹) ·
//     작성자 해제 후 판독 지속 · 재기동 시퀀스 단조 · 판독자 Dispose 후/경합 = Missing(예외 0)
//  §3 δ=0 — 10만 회 TryRead · 10만 회 Publish 0B
//  §4 seqlock 스트레스 — 작성 스레드 4 + 판독 스레드 1(1.5초): Live 사본 필드 관계식 위반(찢김) 0 · 누계 단조
//  §6 Fail-Closed IL — 관제 · 분류 형식은 브로커 · OCX · HTTP · 무장 형식을 참조하지 않는다(판독/게시 전용 · 주문 경로 0)
//  §5 허브 투영 — 판독자 결선 시 키움 레인 Bound · Live 값 · 노후 시 잔여 0 + Stale · 미결선 = Unbound · 뷰 마크업 결선
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Reflection;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using Xunit;

namespace StockManager.Tests.Adapters
{
    [Collection("ConcurrencyStress")]
    public sealed class KiwoomIpcRateLimitTelemetryTests
    {
        private const long Us = 1_000_000;

        private static string MapName() => "Local\\MS2E_TEST_KRATE_" + Guid.NewGuid().ToString("N");

        private sealed class Clock
        {
            public long T;
            public long Now() => Volatile.Read(ref T);
            public void Advance(long micros) => Interlocked.Add(ref T, micros);
        }

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

        private sealed class Rig : IDisposable
        {
            public readonly Clock Clock = new();
            public readonly FakeOcx Ocx = new();
            public readonly KiwoomComEventCore Core;
            public readonly BrokerBackoffController Backoff;
            public readonly KiwoomRateTelemetryWriter Writer;
            public readonly KiwoomRateTelemetryReader Reader;
            public Rig()
            {
                string map = MapName();
                Core = new KiwoomComEventCore(Ocx, null, null, null, null, null, () => Clock.T * 10, mapCapacityPow2: 64);
                Backoff = new BrokerBackoffController(Clock.Now, Us);
                Core.AttachRateGate(KiwoomComEventCore.CreateKiwoomLimiter(Clock.Now, Us), KiwoomComEventCore.CreateKiwoomLimiter(Clock.Now, Us), Backoff);
                Writer = new KiwoomRateTelemetryWriter(map);
                Reader = new KiwoomRateTelemetryReader(map, reopenIntervalMicros: 1);
                Core.AttachRateTelemetry(Writer);
            }
            public KiwoomRateTelemetrySnapshot ReadLive()
            {
                Assert.Equal(KiwoomRateTelemetryRead.Live, Reader.TryRead(out var s));
                return s;
            }
            public void Dispose() { Reader.Dispose(); Writer.Dispose(); }
        }

        // ════════════ §1 정합 ════════════

        [Fact]
        public void Core_GateDecisions_Published_HostReadsExactQuota()
        {
            using var r = new Rig();
            var s = r.ReadLive();                                                // 결선 즉시 게시
            Assert.True(s.GateAttached);
            Assert.Equal((5, 100, 5, 100, 0L), (s.OrderRemaining1s, s.OrderRemaining60s, s.QueryRemaining1s, s.QueryRemaining60s, s.Rejected));
            for (int i = 1; i <= 6; i++) r.Core.SendOrder(i, "5001", "8012345611", 5930, 1, 70_000, 1);
            s = r.ReadLive();
            Assert.Equal((0, 95, 1L), (s.OrderRemaining1s, s.OrderRemaining60s, s.Rejected));
            Assert.Equal(Environment.ProcessId, s.Pid);
            Assert.Equal(0, r.Core.QueryAccount("8012345611", "5001"));            // 조회 2 TR
            s = r.ReadLive();
            Assert.Equal((3, 98), (s.QueryRemaining1s, s.QueryRemaining60s));
            r.Clock.Advance(Us);
            r.Core.PublishRateTelemetry();                                       // 펌프 박동 = 시간 경과 회복 반영
            s = r.ReadLive();
            Assert.Equal((5, 95, 5), (s.OrderRemaining1s, s.OrderRemaining60s, s.QueryRemaining1s));
        }

        [Fact]
        public void Core_Overload308_Backoff_ThenLatch_Visible_To_Host()
        {
            using var r = new Rig();
            r.Ocx.SendRc = BrokerBackoffController.KiwoomOrderOverload;
            r.Core.SendOrder(1, "5001", "8012345611", 5930, 1, 70_000, 1);
            var s = r.ReadLive();
            Assert.Equal(1, s.Consecutive);
            Assert.InRange(s.BackoffMicros, 1, 125_000);
            Assert.False(s.Latched);
            for (int i = 2; i <= 3; i++) { r.Clock.Advance(4 * Us); r.Core.SendOrder(i, "5001", "8012345611", 5930, 1, 70_000, 1); }
            s = r.ReadLive();
            Assert.True(s.Latched);
            Assert.Equal(3, s.Consecutive);
        }

        // ════════════ §2 수명 · 장애 ════════════

        [Fact]
        public void Reader_Missing_BeforeMap_AndBeforeFirstPublish_ThenLive()
        {
            string map = MapName();
            using var reader = new KiwoomRateTelemetryReader(map, reopenIntervalMicros: 1);
            Assert.Equal(KiwoomRateTelemetryRead.Missing, reader.TryRead(out _));
            using var w = new KiwoomRateTelemetryWriter(map);
            Thread.Sleep(1);
            Assert.Equal(KiwoomRateTelemetryRead.Missing, reader.TryRead(out _));   // 맵만 · 게시 전
            var src = new CountingSource();
            w.Publish(src);
            Assert.Equal(KiwoomRateTelemetryRead.Live, reader.TryRead(out var s));
            Assert.Equal(1, s.Rejected);
        }

        [Fact]
        public void Reader_ReopenAttempts_Throttled()
        {
            using var reader = new KiwoomRateTelemetryReader(MapName(), reopenIntervalMicros: 10_000_000);
            for (int i = 0; i < 1_000; i++) Assert.Equal(KiwoomRateTelemetryRead.Missing, reader.TryRead(out _));
            Assert.Equal(1, reader.OpenAttempts);                                // 10초 간격 — 펌프 회차마다 예외 생성 0
        }

        [Fact]
        public void Reader_Stale_WhenProxyStopsBeating_KeepsTotals()
        {
            string map = MapName();
            long skew = 0;
            using var w = new KiwoomRateTelemetryWriter(map);
            using var reader = new KiwoomRateTelemetryReader(map, nowQpc: () => Stopwatch.GetTimestamp() + Volatile.Read(ref skew), reopenIntervalMicros: 1);
            var src = new CountingSource();
            w.Publish(src);
            Assert.Equal(KiwoomRateTelemetryRead.Live, reader.TryRead(out _));
            Volatile.Write(ref skew, 3 * Stopwatch.Frequency);                     // 박동 정지 3초
            Assert.Equal(KiwoomRateTelemetryRead.Stale, reader.TryRead(out var s));
            Assert.Equal(1, s.Rejected);
            Assert.Equal(1, reader.StaleReads);
        }

        [Fact]
        public void WriterDiesMidWrite_OddSequenceStuck_ReaderReturnsTorn_Bounded_NoThrow()
        {
            string map = MapName();
            using var w = new KiwoomRateTelemetryWriter(map);
            w.Publish(new CountingSource());
            using (var m = MemoryMappedFile.OpenExisting(map, MemoryMappedFileRights.ReadWrite))
            using (var v = m.CreateViewAccessor(0, KiwoomRateTelemetry.Size))
                v.Write(8, v.ReadInt64(8) + 1);                                   // 기록 도중 종료 모사(홀수 고착)
            using var reader = new KiwoomRateTelemetryReader(map, reopenIntervalMicros: 1);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) Assert.Equal(KiwoomRateTelemetryRead.Torn, reader.TryRead(out _));
            Assert.True(sw.ElapsedMilliseconds < 2_000, "판독 블로킹 " + sw.ElapsedMilliseconds + "ms");
            Assert.Equal(100, reader.TornReads);
        }

        [Fact]
        public void ProxyExit_HostKeepsView_ThenRestart_SequenceMonotonic()
        {
            string map = MapName();
            using var reader = new KiwoomRateTelemetryReader(map, reopenIntervalMicros: 1);
            long seq1;
            using (var w1 = new KiwoomRateTelemetryWriter(map))
            {
                for (int i = 0; i < 5; i++) w1.Publish(new CountingSource());
                Assert.Equal(KiwoomRateTelemetryRead.Live, reader.TryRead(out var s1));
                seq1 = s1.Sequence;
            }                                                                      // 프록시 종료(작성자 해제)
            Assert.NotEqual(KiwoomRateTelemetryRead.Missing, reader.TryRead(out _));   // 호스트 뷰 유지 = 마지막 값(노후 판정 대상)
            using var w2 = new KiwoomRateTelemetryWriter(map);                      // 재기동 = 같은 맵 재사용
            w2.Publish(new CountingSource());
            Assert.Equal(KiwoomRateTelemetryRead.Live, reader.TryRead(out var s2));
            Assert.True(s2.Sequence > seq1, $"시퀀스 역행 {seq1} → {s2.Sequence}");
        }

        [Fact]
        public void Reader_AfterDispose_And_DisposeRace_NoThrow()
        {
            string map = MapName();
            using var w = new KiwoomRateTelemetryWriter(map);
            w.Publish(new CountingSource());
            for (int round = 0; round < 50; round++)
            {
                var reader = new KiwoomRateTelemetryReader(map, reopenIntervalMicros: 1);
                Assert.Equal(KiwoomRateTelemetryRead.Live, reader.TryRead(out _));
                Exception? fault = null;
                using var go = new ManualResetEventSlim();
                var t = new Thread(() =>
                {
                    try { go.Wait(); for (int i = 0; i < 2_000; i++) reader.TryRead(out _); }
                    catch (Exception ex) { fault = ex; }
                });
                t.Start();
                go.Set();
                reader.Dispose();
                Assert.True(t.Join(TimeSpan.FromSeconds(10)));
                Assert.Null(fault);
                Assert.Equal(KiwoomRateTelemetryRead.Missing, reader.TryRead(out _));
            }
        }

        [Fact]
        public void Writer_AfterDispose_PublishIsNoOp()
        {
            var w = new KiwoomRateTelemetryWriter(MapName());
            w.Dispose();
            w.Publish(new CountingSource());
            Assert.Equal(0, w.Publishes);
            w.Dispose();
        }

        // ════════════ §3 δ=0 ════════════

        [Fact]
        public void HundredThousand_Read_And_Publish_ZeroAlloc()
        {
            using var r = new Rig();
            r.Core.PublishRateTelemetry(); r.Reader.TryRead(out _);              // 예열
            long a = MinAllocated(() => { for (int i = 0; i < 100_000; i++) r.Reader.TryRead(out _); });
            Assert.Equal(0, a);
            long b = MinAllocated(() => { for (int i = 0; i < 100_000; i++) r.Core.PublishRateTelemetry(); });
            Assert.Equal(0, b);
            Assert.True(r.Writer.Publishes >= 100_000);
        }

        // ════════════ §4 seqlock 스트레스 ════════════

        /// <summary>회차마다 n 을 올려 관계식이 성립하는 값만 쓴다 — 판독 사본이 섞이면 관계식이 깨진다.</summary>
        private sealed class CountingSource : IKiwoomRateTelemetrySource
        {
            private long _n;
            public void FillRateTelemetry(ref KiwoomRateTelemetrySnapshot s)
            {
                long n = Interlocked.Increment(ref _n);
                s.Rejected = n; s.BackoffMicros = n * 3;
                s.OrderRemaining1s = (int)(n % 5); s.OrderRemaining60s = (int)(n % 100);
                s.QueryRemaining1s = (int)(n * 7 % 5); s.QueryRemaining60s = (int)(n * 3 % 100);
                s.Consecutive = (int)(n % 13);
                s.Flags = KiwoomRateTelemetrySnapshot.FlagGateAttached | ((n & 1) == 0 ? KiwoomRateTelemetrySnapshot.FlagLatched : 0);
            }
            public static bool Consistent(in KiwoomRateTelemetrySnapshot s)
            {
                long n = s.Rejected;
                return s.BackoffMicros == n * 3 && s.OrderRemaining1s == n % 5 && s.OrderRemaining60s == n % 100
                    && s.QueryRemaining1s == n * 7 % 5 && s.QueryRemaining60s == n * 3 % 100 && s.Consecutive == n % 13
                    && s.Flags == (KiwoomRateTelemetrySnapshot.FlagGateAttached | ((n & 1) == 0 ? KiwoomRateTelemetrySnapshot.FlagLatched : 0));
            }
        }

        [Fact]
        public void Seqlock_FourWriters_OneReader_ZeroTorn_MonotonicTotals()
        {
            string map = MapName();
            using var w = new KiwoomRateTelemetryWriter(map);
            using var reader = new KiwoomRateTelemetryReader(map, reopenIntervalMicros: 1);
            var src = new CountingSource();
            w.Publish(src);
            using var stop = new ManualResetEventSlim();
            using var done = new CountdownEvent(5);
            long torn = 0, live = 0, regress = 0;
            Exception? fault = null;
            for (int t = 0; t < 4; t++)
                new Thread(() =>
                {
                    try { while (!stop.IsSet) w.Publish(src); }
                    catch (Exception ex) { fault = ex; }
                    finally { done.Signal(); }
                }) { IsBackground = true }.Start();
            new Thread(() =>
            {
                try
                {
                    long last = 0, lastSeq = 0;
                    while (!stop.IsSet)
                    {
                        if (reader.TryRead(out var s) != KiwoomRateTelemetryRead.Live) continue;
                        live++;
                        if (!CountingSource.Consistent(in s)) torn++;
                        if (s.Rejected < last || s.Sequence < lastSeq) regress++;
                        last = s.Rejected; lastSeq = s.Sequence;
                    }
                }
                catch (Exception ex) { fault = ex; }
                finally { done.Signal(); }
            }) { IsBackground = true }.Start();
            Thread.Sleep(1_500);
            stop.Set();
            Assert.True(done.Wait(TimeSpan.FromSeconds(30)));
            Assert.Null(fault);
            Assert.True(live > 1_000, "Live 판독 " + live);
            Assert.Equal(0, torn);
            Assert.Equal(0, regress);
            Assert.True(w.Publishes > 1_000);
        }

        // ════════════ §5 허브 투영 ════════════

        [Fact]
        public void Hub_ProjectsKiwoomLane_FromMmf_Live_Then_Stale()
        {
            string map = MapName();
            long skew = 0;
            using var w = new KiwoomRateTelemetryWriter(map);
            using var reader = new KiwoomRateTelemetryReader(map, nowQpc: () => Stopwatch.GetTimestamp() + Volatile.Read(ref skew), reopenIntervalMicros: 1);
            var hub = new DashboardSnapshotHub();
            hub.Pump();
            var f0 = hub.Acquire()!;
            Assert.Equal((DashRateGate.Unbound, false), (f0.RateKiwoomState, f0.RateKiwoomBound));   // 판독자 미결선 = 미결선
            hub.Release(f0);

            hub.AttachKiwoomRateTelemetry(reader);
            hub.Pump();
            var f1 = hub.Acquire()!;
            Assert.True(f1.RateKiwoomBound);
            Assert.Equal(DashRateGate.Waiting, f1.RateKiwoomState);              // 게시 전 = 관제 미수신
            hub.Release(f1);

            var src = new CountingSource();
            for (int i = 0; i < 3; i++) w.Publish(src);                            // n = 3 → 잔여 3 · 60초 3 · 홀수 = 래치 아님
            hub.Pump();
            var f2 = hub.Acquire()!;
            Assert.Equal(DashRateGate.Live, f2.RateKiwoomState);                 // 백오프 9µs(관계식 n × 3) = ms 단위 0 → 정상 · 홀수 n = 래치 아님
            Assert.Equal((3, 3, 3L, 3L), (f2.RateKiwoomRemaining, f2.RateKiwoomRemaining60, f2.RateKiwoomRejected, f2.RateKiwoomConsecutive));
            Assert.True(DashRateGate.ShowsQuota(f2.RateKiwoomState));
            hub.Release(f2);

            Volatile.Write(ref skew, 3 * Stopwatch.Frequency);
            hub.Pump();
            var f3 = hub.Acquire()!;
            Assert.Equal(DashRateGate.Stale, f3.RateKiwoomState);
            Assert.Equal((0, 0, 3L), (f3.RateKiwoomRemaining, f3.RateKiwoomRemaining60, f3.RateKiwoomRejected));   // 낡은 '여유' 비표시 · 누계 보존
            Assert.False(DashRateGate.ShowsQuota(f3.RateKiwoomState));
            hub.Release(f3);
        }

        [Fact]
        public void View_Renders_Both_Rate_Lanes_From_Frame_Only()
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "StockManager.sln"))) root = Path.GetDirectoryName(root)!;
            string view = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Contains("DashRateGate.Label(f.RateKiwoomState)", view);
            Assert.Contains("DashRateGate.Label(f.RateKisState)", view);
            Assert.Contains("f.RateKiwoomRemaining60", view);
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("dashHub.AttachKiwoomRateTelemetry(", form);
            Assert.Equal("미결선", DashRateGate.Label(DashRateGate.Unbound));
            Assert.Equal("관제 노후(프록시 정지?)", DashRateGate.Label(DashRateGate.Stale));
        }
        // ════════════ §6 Fail-Closed IL ════════════

        private static bool Allowed(Type t)
        {
            while (t.IsArray || t.IsByRef || t.IsPointer) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) { if (!t.GetGenericArguments().All(Allowed)) return false; t = t.GetGenericTypeDefinition(); }
            if (t.IsGenericParameter || t.Name.StartsWith("<PrivateImplementationDetails>", StringComparison.Ordinal)) return true;
            if (t.DeclaringType is { } d) return Allowed(d.IsGenericType ? d.GetGenericTypeDefinition() : d);
            if (t == typeof(KiwoomRateTelemetry) || t == typeof(KiwoomRateTelemetryWriter) || t == typeof(KiwoomRateTelemetryReader) || t == typeof(KiwoomRateTelemetrySnapshot)
                || t == typeof(KiwoomRateTelemetryRead) || t == typeof(IKiwoomRateTelemetrySource) || t == typeof(KisRateLimitClassifier)
                || t == typeof(LockFreeTokenBucket) || t == typeof(BrokerBackoffController)) return true;
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal) || ns == "System.IO" || ns == "System.IO.MemoryMappedFiles"
                || ns == "System.Text.Json" || ns == "System.Diagnostics" && t == typeof(Stopwatch)
                || (ns == "System.Threading" && (t == typeof(Interlocked) || t == typeof(Volatile) || t == typeof(Thread) || t == typeof(SpinWait)));
        }

        [Fact]
        public void Telemetry_And_Classifier_Il_NoBrokerOrOrderReferences()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new System.Collections.Generic.List<string>();
            foreach (var type in new[] { typeof(KiwoomRateTelemetry), typeof(KiwoomRateTelemetryWriter), typeof(KiwoomRateTelemetryReader), typeof(KiwoomRateTelemetrySnapshot), typeof(KisRateLimitClassifier) })
            {
                foreach (var f in type.GetFields(All)) if (!Allowed(f.FieldType)) bad.Add(type.Name + "." + f.Name + " : " + f.FieldType.FullName);
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var rm in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = rm as Type ?? rm.DeclaringType;
                        if (owner is not null && !Allowed(owner)) bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + rm.Name);
                    }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
