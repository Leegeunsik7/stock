// ============================================================================
// DashboardLatencyVisualizerTests — [v12.17.5-DEV · 추천 A] 주문 경로 지연 관제 결속(OrderPathStageProfiler → DashboardSnapshotHub → /aidash)
//
//  §1 [방향 A] 지연 축 — 버킷 경계 연속성 · 호스트 LatencyHistogram ↔ RCL SignalLabelMapper 전 버킷 대조(축 복제 동기화 강제) ·
//     라벨 단조 · 보수성(라벨 ≥ 상한) · 백분위 정확도(정확 순서통계 ≤ 히스토그램 ≤ 1.125배) · 하한/상한 넘침.
//  §2 최근 창 — 1,500 부모 → 최신 1,000 과거→최신 순 · 구간 값 정확(주입 시계) · 대상 길이 제한.
//  §3 허브 결속 — 미결선 = 미계측(숫자 0) · 결선 = p50/p99/최대 · 추이 64점 · 등급 3단(정상/미세 정체/심각 지연) · 새 부모만 재계산.
//  §4 [방향 B] 감속 가드 — 즉시 감속 · 200ms 게시 간격 · 보류 변경 유실 0 · 2초 무정체 복귀 · 정상 지연 = 감속 0 · 인자 검증.
//  §5 δ=0 실측 — 히스토그램 · 계측기 창 적재/복사 · 허브 펌프(결선 · 감속 포함) · 라벨/마이크로초 텍스트/지연 스파크라인 작성 0B.
//  §6 Zero UI Interference — 드레인 스레드(계측 작성)와 펌프 · 판독 스레드 동시 가동: 드레인 무할당 · 1ms 초과 비율 ≤ 1e-3 · 찢긴 표본 0.
//  §7 드라이런 통합 — 가상 2 브로커 실 사슬(MultiBrokerDryRunHarness.Stages) → 허브 프레임 실측 · 미승인 도달 0 · 키 off = 계측 대기.
//  §8 래칫(≤40) · RCL 순수성 · 뷰 결선 · 호스트 정직 미결선 · 오중 빌드.
// ============================================================================
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class DashboardLatencyVisualizerTests
    {
        private const long Ns = 1_000_000_000;                                   // 주입 시계 주파수 = 1ns 틱
        private const long Ms = 10_000;                                          // 허브 시계(100ns 틱) 1ms
        private readonly ITestOutputHelper _out;
        public DashboardLatencyVisualizerTests(ITestOutputHelper o) => _out = o;

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

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        /// <summary>주입 시계 계측기(1 틱 = 1ns).</summary>
        private sealed class Clocked
        {
            public long T = 1_000;
            public readonly OrderPathStageProfiler P;
            private long _id;
            public Clocked() => P = new OrderPathStageProfiler(1024, () => Volatile.Read(ref T), Ns);

            /// <summary>부모 1건: 시그널 → 게이트 a · 게이트 → 출구 진입 b · 출구 진입 → 마지막 판정 c(ns). S2 = a + b + c.</summary>
            public void Order(long a, long b, long c, bool forwarded = true)
            {
                long id = ++_id;
                P.BeginOrder(id, T);
                T += a; P.MarkGate(id);
                T += b; long entry = T;
                T += c; P.MarkEgress(entry, forwarded);
                P.EndOrder();
            }
        }

        // ════════════ §1 [방향 A] 지연 축 ════════════

        [Fact]
        public void Axis_ContiguousBuckets_HostAndRclParity_LabelsMonotoneAndConservative()
        {
            Assert.Equal(LatencyHistogram.Buckets, SignalLabelMapper.LatencyBuckets);
            Assert.Equal(LatencyHistogram.MaxTrackedNanos, SignalLabelMapper.LatencyMaxTrackedNanos);
            Assert.Equal(10_485_759, LatencyHistogram.MaxTrackedNanos);                          // 10ms 포함
            double prevUs = -1;
            for (int b = 0; b < LatencyHistogram.Buckets; b++)
            {
                long up = LatencyHistogram.UpperBoundNanos(b);
                Assert.Equal(up, SignalLabelMapper.LatencyBucketUpperNanos(b));
                if (b < LatencyHistogram.Overflow)
                {
                    Assert.Equal(b, LatencyHistogram.BucketOf(up));                              // 상한 = 자기 버킷
                    Assert.Equal(b + 1, LatencyHistogram.BucketOf(up + 1));                      // 상한 + 1 = 다음 버킷(빈틈 · 겹침 0)
                    Assert.Equal(b, SignalLabelMapper.LatencyBucket(up));
                    Assert.Equal(b + 1, SignalLabelMapper.LatencyBucket(up + 1));
                }
                long lower = b == 0 ? 1 : LatencyHistogram.UpperBoundNanos(b - 1) + 1;
                string label = SignalLabelMapper.LatencyText(up);
                Assert.Same(label, SignalLabelMapper.LatencyText(lower));                            // 버킷 하한 · 상한 = 같은 정적 인스턴스
                double us = ParseMicros(label);
                Assert.True(us >= prevUs, $"라벨 단조 {b}: {label}");
                if (b < LatencyHistogram.Overflow) Assert.True(us * 1000 >= up, $"보수성 {b}: {label} < {up}ns");
                prevUs = us;
            }
            var rng = new Random(20261013);
            for (int i = 0; i < 100_000; i++)
            {
                long v = (long)Math.Exp(rng.NextDouble() * Math.Log(50_000_000));
                Assert.Equal(LatencyHistogram.BucketOf(v), SignalLabelMapper.LatencyBucket(v));
            }
            Assert.Equal("-", SignalLabelMapper.LatencyText(0));
            Assert.Equal("≤21µs", SignalLabelMapper.LatencyText(20_000));
            Assert.Equal("≤5.3ms", SignalLabelMapper.LatencyText(5_000_000));
            Assert.Equal(">10.5ms", SignalLabelMapper.LatencyText(20_000_000));
        }

        private static double ParseMicros(string label)
        {
            string s = label.TrimStart('≤', '>');
            return s.EndsWith("ms", StringComparison.Ordinal) ? double.Parse(s[..^2], CultureInfo.InvariantCulture) * 1000
                                                              : double.Parse(s[..^2], CultureInfo.InvariantCulture);
        }

        [Fact]
        public void Histogram_Percentiles_WithinOneBucket_OfExactOrderStatistics_OverUnderflow()
        {
            var rng = new Random(7);
            var h = new LatencyHistogram();
            for (int trial = 0; trial < 20; trial++)
            {
                var v = new long[1000];
                for (int i = 0; i < v.Length; i++) v[i] = 1_024 + (long)Math.Exp(rng.NextDouble() * Math.Log(10_000_000 - 1_024));
                h.Load(v);
                var sorted = v.OrderBy(x => x).ToArray();
                foreach (int pm in new[] { 500, 990, 999 })
                {
                    long exact = sorted[(int)Math.Max(1, (v.Length * (long)pm + 999) / 1000) - 1];
                    long got = h.Percentile(pm);
                    Assert.True(got >= exact && got <= exact * 1.125 + 1, $"p{pm / 10.0} exact {exact} got {got}");
                }
                Assert.Equal(sorted[^1], h.MaxNanos);
                Assert.InRange(h.MeanNanos, (long)v.Average() - 1, (long)v.Average() + 1);
            }
            h.Load(new long[] { 10, 500, 50_000_000, 60_000_000, -5 });
            Assert.Equal(3, h.Underflow);                                                         // 음수 = 0 으로 적재
            Assert.Equal(2, h.OverflowCount);
            Assert.Equal(60_000_000, h.Percentile(1000));                                         // 넘침 = 관측 최대
            Assert.Equal(1_023, h.Percentile(500));                                               // 순위 3 = 버킷 0(1µs 미만) 상한
            h.Clear();
            Assert.Equal(0, h.Count);
            Assert.Equal(0, h.Percentile(990));
        }

        // ════════════ §2 최근 창 ════════════

        [Fact]
        public void RecentWindow_KeepsLatest1000_Chronological_ExactStageValues()
        {
            var c = new Clocked();
            for (int i = 1; i <= 1_500; i++) c.Order(i * 10, 1_000, i);                          // S0 = 10i · S1 = 1,000 · S2 = 11i + 1,000
            Assert.Equal(1_500, c.P.RecentTotal);
            long[] s0 = new long[1_200], s1 = new long[1_200], s2 = new long[1_200];
            int n = c.P.CopyRecent(s0, s1, s2);
            Assert.Equal(OrderPathStageProfiler.RecentWindow, n);
            for (int k = 0; k < n; k++)
            {
                long i = 501 + k;
                Assert.Equal(i * 10, s0[k]);
                Assert.Equal(1_000, s1[k]);
                Assert.Equal(i * 11 + 1_000, s2[k]);
            }
            Assert.Equal(64, c.P.CopyRecent(s0.AsSpan(0, 64), s1, s2));                          // 가장 짧은 대상 길이로 제한 · 최신 64
            Assert.Equal(1_500 * 11 + 1_000, s2[63]);
            // 출구 미도달 부모(게이트 승인 · 출구 0) = 창 미적재(S2 없음)
            var d = new Clocked();
            d.P.BeginOrder(1, d.T); d.T += 100; d.P.MarkGate(1); d.P.EndOrder();
            Assert.Equal(0, d.P.RecentTotal);
        }

        // ════════════ §3 허브 결속 ════════════

        [Fact]
        public void Hub_Unbound_RendersUnmeasured_NoFabricatedNumbers()
        {
            var hub = new DashboardSnapshotHub();
            Assert.False(hub.OrderPathBound);
            hub.OnTick(5930, 70_000);
            Assert.True(hub.Pump());
            var f = hub.Acquire()!;
            Assert.False(f.LatencyBound);
            Assert.Equal(DashLatencyGrade.Unmeasured, f.LatencyGrade);
            Assert.Equal("미계측", SignalLabelMapper.LatencyGradeLabel(f.LatencyGrade));
            Assert.Equal(0, f.LatencyTrendCount);
            Assert.All(f.LatencyStages, s => Assert.Equal(0, s.Count + s.P50Nanos + s.P99Nanos + s.MaxNanos));
            Assert.False(f.LatencyThrottled);
            hub.Release(f);
        }

        [Fact]
        public void Hub_Bound_StagePercentiles_Trend64_GradesThreeLevels_RecomputesOnlyOnNewParents()
        {
            var c = new Clocked();
            long now = 1;
            var hub = new DashboardSnapshotHub(nowTicks: () => now, orderPath: c.P);
            Assert.True(hub.OrderPathBound);
            Assert.True(hub.Pump());
            var f0 = hub.Acquire()!;
            Assert.True(f0.LatencyBound);
            Assert.Equal(DashLatencyGrade.Unmeasured, f0.LatencyGrade);                             // 결선 · 표본 0 = 미계측
            hub.Release(f0);

            for (int i = 0; i < 200; i++) c.Order(5_000, 3_000, 2_000 + (i % 10) * 1_000);     // S2 = 10µs .. 19µs
            now += 50 * Ms;
            Assert.True(hub.Pump());
            var f = hub.Acquire()!;
            var e2e = f.Latency(DashLatencyGrade.SignalToEgress);
            Assert.Equal(200, e2e.Count);
            Assert.Equal(19_000, e2e.MaxNanos);
            Assert.InRange(e2e.P50Nanos, 14_000, 14_000 * 1.125 + 1);
            Assert.InRange(e2e.P99Nanos, 19_000, 19_000);                                          // 상한 ≥ 최대 → 관측 최대로 클램프
            Assert.Equal(5_000, f.Latency(DashLatencyGrade.SignalToGate).MaxNanos);
            Assert.Equal(3_000, f.Latency(DashLatencyGrade.GateToEgress).MaxNanos);
            Assert.Equal(DashLatencyGrade.Normal, f.LatencyGrade);
            Assert.Equal(PaperDashboardFrame.MaxLatencyTrend, f.LatencyTrendCount);
            Assert.Equal(f.LatencyTrendCount, f.LatencyTrendSpan.Length);
            Assert.Equal(10_000 + (199 % 10) * 1_000, f.LatencyTrendSpan[^1]);                  // 최신 = 마지막
            Assert.Equal(200, f.LatencyOrders);
            Assert.Equal(200, f.LatencyForwarded);
            Assert.Equal("정상", SignalLabelMapper.LatencyGradeLabel(f.LatencyGrade));
            Assert.Equal("ok", SignalLabelMapper.LatencyGradeClass(f.LatencyGrade));
            hub.Release(f);

            now += 50 * Ms;
            Assert.False(hub.Pump());                                                               // 새 부모 0 · 그 외 불변 = 게시 0

            var mid = new Clocked();
            var hub2 = new DashboardSnapshotHub(nowTicks: () => now, orderPath: mid.P);
            for (int i = 0; i < 100; i++) mid.Order(1_000, 1_000, i < 95 ? 10_000 : 3_000_000); // 상위 5% = 3ms → p99 > 1ms
            hub2.Pump();
            var g = hub2.Acquire()!;
            Assert.Equal(DashLatencyGrade.MicroSpike, g.LatencyGrade);
            Assert.Equal("미세 정체", SignalLabelMapper.LatencyGradeLabel(g.LatencyGrade));
            hub2.Release(g);

            var lag = new Clocked();
            var hub3 = new DashboardSnapshotHub(nowTicks: () => now, orderPath: lag.P);
            for (int i = 0; i < 100; i++) lag.Order(1_000, 1_000, 15_000_000);
            hub3.Pump();
            var l = hub3.Acquire()!;
            Assert.Equal(DashLatencyGrade.SevereLag, l.LatencyGrade);
            Assert.Equal("lag", SignalLabelMapper.LatencyGradeClass(l.LatencyGrade));
            Assert.Equal(">10.5ms", SignalLabelMapper.LatencyText(l.Latency(DashLatencyGrade.SignalToEgress).P99Nanos));
            hub3.Release(l);
        }

        // ════════════ §4 [방향 B] 감속 가드 ════════════

        [Fact]
        public void SpikeGuard_Unit_ImmediateDegrade_200msGate_TimeHysteresisRecovery_ArgValidation()
        {
            var g = new LatencySpikeDegradationGuard();
            long t = 1_000 * Ms;
            Assert.False(g.Observe(true, 900_000, 100, t));                                       // 0.9ms = 정상
            Assert.False(g.Degraded);
            Assert.Equal(50, g.PeriodMs);
            Assert.False(g.Observe(false, 5_000_000, 100, t));                                    // 새 표본 없음 = 관측 아님
            Assert.False(g.Observe(true, 5_000_000, 0, t));                                       // 표본 0 = 관측 아님
            Assert.True(g.Observe(true, 1_000_001, 100, t));                                      // 1ms 초과 1회 = 즉시 감속
            Assert.True(g.Degraded);
            Assert.Equal(200, g.PeriodMs);
            Assert.True(g.AllowPublish(t)); g.MarkPublished(t);
            Assert.False(g.AllowPublish(t + 199 * Ms));
            Assert.True(g.AllowPublish(t + 200 * Ms));
            Assert.Equal(1, g.Suppressed);
            Assert.False(g.Observe(true, 2_000_000, 100, t + 1_000 * Ms));                        // 재관측 = 정체 시각 갱신
            Assert.False(g.Observe(true, 100_000, 100, t + 2_999 * Ms));                           // 정상 표본이어도 마지막 정체 + 2초 전 = 유지
            Assert.True(g.Degraded);
            Assert.True(g.Observe(false, 2_000_000, 100, t + 3_000 * Ms));                        // 2초 무정체(새 표본 없어도) = 복귀
            Assert.False(g.Degraded);
            Assert.Equal(1, g.Degradations);
            Assert.Equal(1, g.Recoveries);
            Assert.True(g.AllowPublish(t + 3_001 * Ms));

            Assert.Throws<ArgumentOutOfRangeException>(() => new LatencySpikeDegradationGuard(spikeNanos: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LatencySpikeDegradationGuard(normalPeriodMs: 200, degradedPeriodMs: 200));
            Assert.Throws<ArgumentOutOfRangeException>(() => new LatencySpikeDegradationGuard(recoverHoldMs: 100));
        }

        [Fact]
        public void Hub_Spike_Throttles50To200ms_NoLostChange_RecoversAfter2s_NormalNeverThrottles()
        {
            var c = new Clocked();
            long now = 10_000 * Ms;
            var hub = new DashboardSnapshotHub(nowTicks: () => now, orderPath: c.P);
            for (int i = 0; i < 100; i++) c.Order(1_000, 1_000, 5_000_000);                       // p99 = 5ms
            Assert.True(hub.Pump());                                                                // 첫 프레임은 항상 게시
            var f = hub.Acquire()!;
            Assert.True(f.LatencyThrottled);
            Assert.Equal(200, f.PublishPeriodMs);
            Assert.Equal(200, hub.CurrentPeriodMs);
            hub.Release(f);

            // 50ms 펌프 × 40 (2초) · 매 회차 새 부모(정체 지속) → 게시는 200ms 마다(4회차에 1번)
            long published0 = hub.Published;
            for (int k = 0; k < 40; k++) { c.Order(1_000, 1_000, 5_000_000); now += 50 * Ms; hub.Pump(); }
            Assert.Equal(10, hub.Published - published0);
            Assert.Equal(30, hub.LatencyGuard.Suppressed);

            // 보류 변경 유실 0: 변경 1건 후 보류 회차들 → 다음 허가 회차에 최신 누적 수로 게시
            c.Order(1_000, 1_000, 5_000_000);
            long expectOrders = c.P.RecentTotal;
            now += 50 * Ms; Assert.False(hub.Pump());
            now += 50 * Ms; Assert.False(hub.Pump());
            now += 50 * Ms; Assert.False(hub.Pump());
            now += 50 * Ms; Assert.True(hub.Pump());
            var g = hub.Acquire()!;
            Assert.Equal(expectOrders, g.LatencyOrders);
            hub.Release(g);

            // 주문 중단 → 마지막 정체 + 2초 = 복귀 → 정상 게시 주기
            for (int k = 0; k < 36; k++) { now += 50 * Ms; hub.Pump(); }                         // 마지막 정체 관측(+50ms 회차) + 1,950ms
            Assert.True(hub.LatencyGuard.Degraded);
            now += 50 * Ms; Assert.True(hub.Pump());                                               // 복귀 회차 = 표시 갱신 게시
            var r = hub.Acquire()!;
            Assert.False(r.LatencyThrottled);
            Assert.Equal(50, r.PublishPeriodMs);
            hub.Release(r);
            Assert.Equal(1, hub.LatencyGuard.Recoveries);

            // 정상 지연(50µs) = 감속 0 — 새 부모마다 50ms 펌프 전부 게시
            var n = new Clocked();
            var calm = new DashboardSnapshotHub(nowTicks: () => now, orderPath: n.P);
            long p0 = calm.Published;
            for (int k = 0; k < 40; k++) { n.Order(10_000, 10_000, 30_000); now += 50 * Ms; calm.Pump(); }
            Assert.Equal(40, calm.Published - p0);
            Assert.Equal(0, calm.LatencyGuard.Degradations);
            Assert.Equal(0, calm.LatencyGuard.Suppressed);
        }

        // ════════════ §5 δ=0 실측 ════════════

        [Fact]
        public void ZeroAlloc_Histogram_ProfilerWindow_HubPump_Labels_LatencySparkline()
        {
            var h = new LatencyHistogram();
            var vals = new long[1000];
            for (int i = 0; i < vals.Length; i++) vals[i] = 1_000 + i * 997L;
            h.Load(vals); h.Percentile(990);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) { h.Load(vals); h.Percentile(500); h.Percentile(990); } }));

            var c = new Clocked();
            long[] a = new long[1000], b = new long[1000], s = new long[1000];
            c.Order(1, 1, 1); c.P.CopyRecent(a, b, s);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 5_000; i++) c.Order(1_000, 2_000, 3_000); c.P.CopyRecent(a, b, s); }));

            // 허브 펌프(결선 · 정상 → 감속 → 복귀 포함) — 새 부모가 있는 회차마다 창 1,000 재계산 · 게시
            long now = 1;
            var hub = new DashboardSnapshotHub(nowTicks: () => now, orderPath: c.P);
            for (int k = 0; k < 5; k++) { c.Order(1_000, 1_000, 1_000); now += 50 * Ms; hub.Pump(); }
            long before = hub.Published;
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int k = 0; k < 100; k++)
                {
                    c.Order(1_000, 1_000, k < 20 ? 5_000_000 : 20_000);   // 창 1,000 중 20 = 2% > 1% → p99 > 1ms
                    now += 50 * Ms;
                    hub.Pump();
                    var fr = hub.Acquire();
                    if (fr is not null) hub.Release(fr);
                }
            }));
            Assert.True(hub.Published - before >= 20, "게시 " + (hub.Published - before));        // 비공허성(실제 조립 · 게시 경로 실행)
            Assert.True(hub.LatencyGuard.Degradations >= 1);                                        // 감속 경로도 측정 창 안에서 실행

            // 라벨 · 마이크로초 텍스트 · 지연 스파크라인(고정 버퍼)
            var trend = new long[64];
            for (int i = 0; i < trend.Length; i++) trend[i] = 2_000L << (i % 12);
            var scratch = new long[64];
            var buf = new char[64 * 12];
            Assert.True(SparklineWriter.WriteLatency(trend, scratch, buf, 300, 40) > 0);
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    SparklineWriter.WriteLatency(trend, scratch, buf, 300, 40);
                    _ = SignalLabelMapper.LatencyText(trend[i & 63]);
                    _ = SignalLabelMapper.LatencyGradeLabel(i & 3);
                    _ = SignalLabelMapper.LatencyGradeClass(i & 3);
                    _ = SignalLabelMapper.LatencyStage(i % 3);
                    _ = SparklineWriter.LatencyY(40);
                    _ = DashLatencyGrade.Classify(true, 10, trend[i & 63]);
                }
            }));
            Assert.Equal(0, SparklineWriter.WriteLatency(trend, scratch.AsSpan(0, 10), buf, 300, 40));   // 스크래치 부족 = 0
            Assert.Equal(0, SparklineWriter.WriteLatency(trend.AsSpan(0, 1), scratch, buf, 300, 40));    // 값 1개 = 0
            Assert.Equal(40, SparklineWriter.LatencyY(40, 0) );                                           // 1µs 미만 = 바닥
            Assert.True(SparklineWriter.LatencyY(40) is > 0 and < 40);                                    // 1ms 기준선 = 화면 안
        }

        // ════════════ §6 Zero UI Interference ════════════

        [Fact]
        public void ConcurrentPumpAndReader_DrainThreadNeverWaits_ZeroAlloc_NoTornSamples()
        {
            var p = new OrderPathStageProfiler(1024);
            var hub = new DashboardSnapshotHub(orderPath: p);
            const int Orders = 200_000;
            long drainAlloc = -1, over1ms = 0, maxTicks = 0, torn = 0, reads = 0;
            int stop = 0;
            var pump = new Thread(() => { while (Volatile.Read(ref stop) == 0) { hub.Pump(); var f = hub.Acquire(); if (f is not null) hub.Release(f); } }) { IsBackground = true };
            var reader = new Thread(() =>
            {
                long[] a = new long[1000], b = new long[1000], s = new long[1000];
                while (Volatile.Read(ref stop) == 0)
                {
                    int n = p.CopyRecent(a, b, s);
                    for (int k = 0; k < n; k++) if (s[k] < a[k] + b[k]) torn++;                    // S2 ≥ S0 + S1(단조 시계) — 부모 간 혼합 = 위반
                    reads++;
                }
            }) { IsBackground = true };
            var drain = new Thread(() =>
            {
                long oneMs = System.Diagnostics.Stopwatch.Frequency / 1000;
                for (int i = 0; i < 2_000; i++) Step(p, i + 1);                                       // 워밍업(JIT)
                long a0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Orders; i++)
                {
                    long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                    Step(p, 10_000 + i);
                    long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                    if (dt > oneMs) over1ms++;
                    if (dt > maxTicks) maxTicks = dt;
                }
                drainAlloc = GC.GetAllocatedBytesForCurrentThread() - a0;
            });
            pump.Start(); reader.Start(); drain.Start();
            drain.Join();
            Volatile.Write(ref stop, 1);
            pump.Join(); reader.Join();
            _out.WriteLine($"[무간섭] 드레인 {Orders:N0}건 · 1ms 초과 {over1ms} · 최대 {maxTicks * 1_000_000 / System.Diagnostics.Stopwatch.Frequency}µs · 펌프 {hub.Pumps:N0} · 게시 {hub.Published:N0} · 판독 {reads:N0} · 찢김 {torn}");
            Assert.Equal(0, drainAlloc);
            Assert.True(over1ms <= Orders / 1000, "1ms 초과 " + over1ms);
            Assert.Equal(0, torn);
            Assert.True(hub.Pumps >= 10 && hub.Published >= 2 && reads >= 10, "동시성 비공허성");
            Assert.Equal(0, hub.PumpFaults);
        }

        private static void Step(OrderPathStageProfiler p, long id)
        {
            p.BeginOrder(id, p.Timestamp());
            p.MarkGate(id);
            long e = p.Timestamp();
            p.MarkEgress(e, true);
            p.EndOrder();
        }

        // ════════════ §7 드라이런 통합 ════════════

        private const long Sym = 5930, Mid = 70_000, Tick = 100;

        private static void StrongBuy(MultiBrokerDryRunHarness h)
        {
            for (int k = 0; k < 6; k++)
            {
                double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
                long bid10 = (long)Math.Round(5_000 * (1 + imb10)), ask10 = 10_000 - bid10;
                h.OnBook(Sym, Mid, 950, 50, (bid10 - 4_750) / 5, (ask10 - 250) / 5);
            }
        }

        [Fact]
        public void DryRun_RealChain_FeedsHubFrame_StageBadges_ZeroUnsanctioned_KeysOffWaits()
        {
            using var h = new MultiBrokerDryRunHarness(confidenceThreshold: 0.5);
            h.SetPhysicalKeys(true, true);
            h.SetCaps(60, 40);
            var hub = new DashboardSnapshotHub(h.Interlocks, h.Matrix, orderPath: h.Stages);
            int sent = 0;
            for (int i = 0; i < 60; i++)
            {
                StrongBuy(h);
                if (h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + 3 * Tick) > 0) sent++;
                h.Pump();
                h.Advance(1_000);
            }
            Assert.True(hub.Pump());
            var f = hub.Acquire()!;
            var e2e = f.Latency(DashLatencyGrade.SignalToEgress);
            _out.WriteLine($"[드라이런] 발행 {sent} · 창 {e2e.Count} · 시그널→발주 p50 {SignalLabelMapper.LatencyText(e2e.P50Nanos)} p99 {SignalLabelMapper.LatencyText(e2e.P99Nanos)} 최대 {e2e.MaxNanos}ns · 등급 {SignalLabelMapper.LatencyGradeLabel(f.LatencyGrade)}");
            Assert.True(sent >= 20, "발행 " + sent);                                               // 비공허성
            Assert.True(f.LatencyBound);
            Assert.Equal(h.Stages.RecentTotal, e2e.Count);
            Assert.Equal(h.Stages.Stage(OrderPathStage.SignalToEgress).Count, e2e.Count);          // 창(≤1,000) = 누적(< 1,000)
            Assert.True(e2e.P50Nanos > 0 && e2e.P99Nanos >= e2e.P50Nanos && e2e.MaxNanos >= e2e.P99Nanos);
            Assert.NotEqual(DashLatencyGrade.Unmeasured, f.LatencyGrade);
            Assert.Equal(h.InnerPlaces, f.LatencyForwarded);
            Assert.True(f.LatencyTrendCount >= 2);
            Assert.Equal(0, h.UnsanctionedPlaces);
            hub.Release(f);

            using var off = new MultiBrokerDryRunHarness(confidenceThreshold: 0.5);               // 물리 키 off(기본) = 출구 도달 0
            var hubOff = new DashboardSnapshotHub(off.Interlocks, orderPath: off.Stages);
            StrongBuy(off);
            off.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + 3 * Tick);
            off.Pump();
            hubOff.Pump();
            var g = hubOff.Acquire()!;
            Assert.True(g.LatencyBound);
            Assert.Equal(0, g.Latency(DashLatencyGrade.SignalToEgress).Count);
            Assert.Equal(DashLatencyGrade.Unmeasured, g.LatencyGrade);                              // UI = '계측 대기'
            Assert.Equal(0, off.InnerPlaces);
            Assert.Equal(0, off.UnsanctionedPlaces);
            hubOff.Release(g);
        }

        // ════════════ §8 래칫 · 순수성 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void Ratchet_RclPurity_ViewWiring_HostHonestlyUnbound_FiveBuilds()
        {
            var r = PureShellFitnessSentinel.Scan(typeof(CanaryHealthSentinel).Assembly);
            Assert.True(r.WithinRatchet && r.HostCoupledUi <= 40, $"coupled {r.HostCoupledUi}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            string root = RepoRoot();
            foreach (var file in new[] { @"MS2E.UI.Portable\Helpers\SignalLabelMapper.cs", @"MS2E.UI.Portable\Helpers\DashboardRenderHelpers.cs",
                                         @"MS2E.UI.Portable\Ports\Trading\PaperDashboardPort.cs" })
            {
                string src = File.ReadAllText(Path.Combine(root, file));
                Assert.DoesNotContain("StockManager.", src);                                          // RCL 엔진 참조 0
                Assert.DoesNotContain("$\"", src);
            }
            foreach (var file in new[] { @"StockManager\Telemetry\LatencyHistogram.cs", @"StockManager\Telemetry\LatencySpikeDegradationGuard.cs",
                                         @"StockManager\Telemetry\OrderPathStageProfiler.cs" })
            {
                string src = File.ReadAllText(Path.Combine(root, file));
                Assert.DoesNotContain("System.Linq", src);                                            // 핫패스 LINQ · 보간 0
                Assert.DoesNotContain("$\"", src);
                Assert.DoesNotContain("lock (", src);                                                 // 작성자 대기 0
            }
            string view = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Equal(2, view.Split('\n').Count(l => l.TrimStart().StartsWith("@inject ")));
            Assert.Contains("SignalLabelMapper.LatencyText(st.P50Nanos)", view);
            Assert.Contains("SignalLabelMapper.LatencyText(st.P99Nanos)", view);
            Assert.Contains("SignalLabelMapper.LatencyGradeLabel(f.LatencyGrade)", view);
            Assert.Contains("SparklineWriter.WriteLatency(f.LatencyTrendSpan", view);
            Assert.Contains("f.LatencyThrottled", view);
            Assert.Contains("계측 미결선", view);
            Assert.DoesNotContain("$\"", view);
            // [v12.17.6] 호스트 실 경로 결선 — 발행 스탬프 생산자(링 관측자)와 함께 허브에 주입
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("orderPath: orderPathProfiler", form);
            Assert.Contains("dispatchRing.PathObserver = orderPathProfiler;", form);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.Contains("WebOnly", File.ReadAllText(Path.Combine(root, "StockManager", "StockManager.csproj")));
        }
    }
}
