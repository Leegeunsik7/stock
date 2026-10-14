// ============================================================================
// JitterTelemetryBindingTests — [v12.18.6-DEV] 마이크로초 지터 센티널 → DashboardSnapshotHub → /aidash 배지 무할당 결속
//
//  §1 seqlock 사본 — 게시 = 드문 경로(스파이크 · 꼬리 진입)와 Reset 에서만 · 정상 표본 100만 회 = 게시 0 · 값 정확(누적 · GC · 최대 · 마지막 · 최악 K 요약).
//  §2 δ=0 실측 — 정상/스파이크 기록 · 사본 판독 · 허브 펌프(결선) · 라벨/µs 텍스트 반복 조회 0B.
//  §3 허브 결속 — 미결선 = Unbound · 첫 판독 = 기준선(결선 전 스파이크 ≠ 최근) · 스파이크 주입 → 다음 펌프에 즉시 Spike/GcSpike ·
//     5초 유지 후 Normal 복귀(누적 보존) · 사본 판독은 작성자 스레드 상태 미접근.
//  §4 동시성 — 작성자 스레드 연속 스파이크 · 판독 스레드 사본: 찢긴 사본 0(불변식 4종 대조).
//  §5 Soak 하네스 통합 — LongSoakProfileHarness 실 측정 센티널을 허브에 결선 → 프레임 최악 K = 실표본.
//  §6 래칫(≤40) · RCL 순수성 · 뷰 결선 · 호스트 정직 미결선(실 측정 생산자 없음 = 배지 '지터 미결선').
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Telemetry
{
    [Collection("ConcurrencyStress")]
    public sealed class JitterTelemetryBindingTests
    {
        private const long Us = 1_000;                                           // ns
        private const long Sec = 10_000_000;                                     // 허브 시계(100ns 틱) 1초
        private readonly ITestOutputHelper _out;
        public JitterTelemetryBindingTests(ITestOutputHelper o) => _out = o;

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

        private static JitterSnapshot Snap(MicrosecondJitterSentinel j)
        {
            Assert.True(j.TryReadSnapshot(out var s));
            return s;
        }

        /// <summary>펌프 1회 후 게시본 사본 판독(리스 → 값 복사 → 반납).</summary>
        private static (int State, long Spikes, long Gc, long Max, long Last, int WorstN, long Floor, int WorstGc) Frame(DashboardSnapshotHub hub)
        {
            var f = hub.Acquire();
            Assert.NotNull(f);
            try { return (f!.JitterState, f.JitterSpikes, f.JitterGcSpikes, f.JitterMaxNanos, f.JitterLastSpikeNanos, f.JitterWorstCount, f.JitterWorstFloorNanos, f.JitterWorstGcCount); }
            finally { hub.Release(f!); }
        }

        // ════════════ §1 seqlock 사본 ════════════

        [Fact]
        public void Snapshot_PublishedOnRarePathOnly_ExactValues_ResetPublishes()
        {
            using var j = new MicrosecondJitterSentinel(thresholdNanos: 500 * Us, ringCapacity: 8, tailCapacity: 4);
            Assert.Equal(default, Snap(j));                                                       // 게시 전 = 0 · Version 0
            for (int i = 0; i < 4; i++) j.Record(10 * Us, 0, 1, 0);                               // 꼬리 4칸 채움 = 게시 4
            long v = Snap(j).Version;
            Assert.Equal(4, v);
            for (int i = 0; i < 1_000_000; i++) j.Record(10 * Us, 0, 1, 0);                       // 정상 경로(임계 미만 · 꼬리 최소와 같음) = 게시 0
            Assert.Equal(v, Snap(j).Version);

            j.Record(800 * Us, 0, 2, 0);                                                          // 스파이크(비 GC)
            var s = Snap(j);
            Assert.Equal(1, s.SpikeCountOver500Us); Assert.Equal(0, s.GcSpikeCount);
            Assert.Equal(800 * Us, s.MaxJitterNanos); Assert.Equal(800, s.MaxJitterUs);
            Assert.Equal(800 * Us, s.LastSpikeNanos); Assert.False(s.LastSpikeGcTagged);

            j.Record(1_200 * Us, 1, 2, 0);                                                        // GC 유발 스파이크
            j.Record(600 * Us, 0, 2, 0);                                                          // 마지막 = 비 GC
            s = Snap(j);
            Assert.Equal(3, s.SpikeCountOver500Us); Assert.Equal(1, s.GcSpikeCount);
            Assert.Equal(1_200 * Us, s.MaxJitterNanos);
            Assert.Equal(600 * Us, s.LastSpikeNanos); Assert.False(s.LastSpikeGcTagged);
            Assert.Equal(4, s.WorstSampleCount);
            Assert.Equal(10 * Us, s.WorstSampleFloorNanos);                                       // 1,200 · 800 · 600 · 10 → 4번째 = 10µs
            Assert.Equal(1, s.WorstSampleGcCount);
            Assert.Equal(j.Spikes, s.SpikeCountOver500Us); Assert.Equal(j.MaxNanos, s.MaxJitterNanos);

            j.Record(700 * Us, 2, 2, 0);
            Assert.True(Snap(j).LastSpikeGcTagged);

            long before = Snap(j).Version;
            j.Reset();
            s = Snap(j);
            Assert.Equal(before + 1, s.Version);
            Assert.Equal(0, s.SpikeCountOver500Us); Assert.Equal(0, s.MaxJitterNanos); Assert.Equal(0, s.WorstSampleCount);
            Assert.False(s.LastSpikeGcTagged);

            j.Dispose();
            Assert.True(j.TryReadSnapshot(out _));                                                // 사본 = 관리 필드 → 폐기 후 판독 안전
        }

        // ════════════ §2 δ=0 ════════════

        [Fact]
        public void ZeroAlloc_RecordSpikeSnapshot_HubPump_Labels()
        {
            using var j = new MicrosecondJitterSentinel();
            using var hub = new DashboardSnapshotHub(nowTicks: () => 1);
            hub.AttachJitter(j);
            for (int i = 0; i < 64; i++) { j.Record(10 * Us, 0, 1, 0); j.Record(900 * Us, i & 1, 1, 0); hub.Pump(); }   // 예열(JIT)
            for (int s = 0; s < SignalLabelMapper.JitterStateCount; s++) { SignalLabelMapper.JitterStateLabel(s); SignalLabelMapper.JitterStateClass(s); }
            SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotMax, 1_234_567);

            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 100_000; i++) j.Record(5 * Us, 0, 1, 0); }));           // 정상 경로
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) j.Record(900 * Us + i, i & 1, 1, 0); }));  // 스파이크 + 게시
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 10_000; i++) j.TryReadSnapshot(out _); }));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 200; i++) { j.Record(950 * Us + i, 0, 1, 0); hub.Pump(); } }));   // 매 회차 재게시
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 10_000; i++)
                {
                    SignalLabelMapper.JitterStateLabel(i & 3); SignalLabelMapper.JitterStateClass(i & 3);
                    SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotMax, 1_234_567);
                }
            }));
        }

        // ════════════ §3 허브 결속 ════════════

        [Fact]
        public void Hub_Unbound_Baseline_SpikeImmediate_GcRed_HoldThenNormal()
        {
            long now = 100 * Sec;
            using (var bare = new DashboardSnapshotHub(nowTicks: () => Volatile.Read(ref now)))
            {
                Assert.True(bare.Pump());
                Assert.Equal(DashJitter.Unbound, Frame(bare).State);
            }

            using var j = new MicrosecondJitterSentinel();
            j.Record(700 * Us, 0, 1, 0);                                                          // 결선 전 스파이크 = 최근 아님
            using var hub = new DashboardSnapshotHub(nowTicks: () => Volatile.Read(ref now));
            hub.AttachJitter(j);
            Assert.True(hub.Pump());
            var fr = Frame(hub);
            Assert.Equal(DashJitter.Normal, fr.State);
            Assert.Equal(1, fr.Spikes);
            Assert.Equal(700 * Us, fr.Max);

            for (int i = 0; i < 1_000; i++) j.Record(20 * Us, 0, 1, 0);
            now += Sec / 20;
            hub.Pump();
            Assert.Equal(DashJitter.Normal, Frame(hub).State);

            j.Record(1_500 * Us, 0, 3, 0);                                                        // 스파이크 주입 → 다음 펌프 즉시
            now += Sec / 20;
            Assert.True(hub.Pump());
            fr = Frame(hub);
            Assert.Equal(DashJitter.Spike, fr.State);
            Assert.Equal(2, fr.Spikes); Assert.Equal(1_500 * Us, fr.Last); Assert.Equal(1_500 * Us, fr.Max);
            _out.WriteLine($"[§3] 스파이크 → {SignalLabelMapper.JitterStateLabel(fr.State)} {SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, fr.Last)}");

            j.Record(2_000 * Us, 1, 3, 0);                                                        // GC 유발 = 적색
            now += Sec / 20;
            Assert.True(hub.Pump());
            fr = Frame(hub);
            Assert.Equal(DashJitter.GcSpike, fr.State);
            Assert.Equal(1, fr.Gc); Assert.Equal(1, fr.WorstGc);
            Assert.Equal("gc", SignalLabelMapper.JitterStateClass(fr.State));

            now += DashJitter.HoldTicks - Sec / 20;                                               // 유지 직전 = 아직 GC
            hub.Pump();
            Assert.Equal(DashJitter.GcSpike, Frame(hub).State);
            now += Sec / 10;                                                                      // 만료 → 정상 복귀 재게시(누적 보존)
            Assert.True(hub.Pump());
            fr = Frame(hub);
            Assert.Equal(DashJitter.Normal, fr.State);
            Assert.Equal(3, fr.Spikes); Assert.Equal(2_000 * Us, fr.Max);
            Assert.Equal(0, hub.PumpFaults);
            Assert.Equal(0, hub.JitterContended);

            j.Reset();                                                                            // 예열 제외 리셋 = 감소 → 스파이크 아님
            now += Sec / 20;
            hub.Pump();
            fr = Frame(hub);
            Assert.Equal(DashJitter.Normal, fr.State);
            Assert.Equal(0, fr.Spikes);
        }

        // ════════════ §4 동시성 ════════════

        [Fact]
        public void Concurrent_WriterSpikes_ReaderSnapshots_NoTornSnapshot()
        {
            using var j = new MicrosecondJitterSentinel(thresholdNanos: 500 * Us, ringCapacity: 64, tailCapacity: 16);
            const long Base = 600 * Us;
            const int Writes = 400_000;
            long torn = 0, reads = 0, contended = 0;
            int stop = 0;
            using var done = new CountdownEvent(2);
            var writer = new Thread(() =>
            {
                try { for (int k = 0; k < Writes; k++) j.Record(Base + k, k & 1, 1, 0); }       // 값 = Base + 순번 · 홀수 = GC
                finally { Volatile.Write(ref stop, 1); done.Signal(); }
            }) { IsBackground = true };
            var reader = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref stop) == 0 || reads < 1_000)
                    {
                        if (!j.TryReadSnapshot(out var s)) { contended++; continue; }
                        reads++;
                        long n = s.SpikeCountOver500Us;
                        if (n == 0) continue;
                        bool ok = s.LastSpikeNanos == Base + n - 1                               // 마지막 = n 번째 값
                               && s.MaxJitterNanos == s.LastSpikeNanos                              // 단조 증가 → 최대 = 마지막
                               && s.LastSpikeGcTagged == (((n - 1) & 1) == 1)
                               && s.GcSpikeCount == n / 2
                               && s.WorstSampleCount == (int)Math.Min(n, 16);
                        if (!ok) torn++;
                    }
                }
                finally { done.Signal(); }
            }) { IsBackground = true };
            writer.Start(); reader.Start();
            Assert.True(done.Wait(TimeSpan.FromSeconds(60)), "스레드 60초 무응답");
            _out.WriteLine($"[§4] 판독 {reads:N0} · 경합 {contended:N0} · 찢김 {torn}");
            Assert.Equal(0, torn);
            Assert.True(reads >= 1_000);
            var fin = Snap(j);
            Assert.Equal(Writes, fin.SpikeCountOver500Us);
            Assert.Equal(Writes / 2, fin.GcSpikeCount);
        }

        // ════════════ §5 Soak 하네스 통합 ════════════

        [Fact]
        public void SoakHarness_RealSentinel_BoundToHub_FrameCarriesWorstSamples()
        {
            using var soak = new LongSoakProfileHarness(fullChain: true);
            Assert.NotNull(soak.Jitter);
            using var hub = new DashboardSnapshotHub();
            hub.AttachJitter(soak.Jitter!);
            soak.Warmup();
            soak.RunTicks(20_000);
            Assert.True(hub.Pump());
            var fr = Frame(hub);
            var s = Snap(soak.Jitter!);
            _out.WriteLine($"[§5] 표본 {soak.Jitter!.Samples:N0} · 스파이크 {s.SpikeCountOver500Us} (GC {s.GcSpikeCount}) · 최대 {s.MaxJitterUs}µs · 최악 {s.WorstSampleCount}개 ≥ {s.WorstSampleFloorNanos / Us}µs");
            Assert.NotEqual(DashJitter.Unbound, fr.State);
            Assert.Equal(MicrosecondJitterSentinel.DefaultTailCapacity, fr.WorstN);
            Assert.Equal(soak.Jitter!.MaxNanos, fr.Max);
            Assert.Equal(soak.Jitter!.Spikes, fr.Spikes);
            Assert.True(fr.Floor > 0 && fr.Floor <= fr.Max);
        }

        // ════════════ §6 래칫 · 순수성 · 결선 ════════════

        [Fact]
        public void Labels_Ratchet_RclPurity_ViewWiring_HostHonestlyUnbound()
        {
            Assert.Equal(4, SignalLabelMapper.JitterStateCount);
            Assert.Equal("지터 미결선", SignalLabelMapper.JitterStateLabel(DashJitter.Unbound));
            Assert.Equal("ok", SignalLabelMapper.JitterStateClass(DashJitter.Normal));
            Assert.Equal("spike", SignalLabelMapper.JitterStateClass(DashJitter.Spike));
            Assert.Equal("gc", SignalLabelMapper.JitterStateClass(DashJitter.GcSpike));
            Assert.Equal(SignalLabelMapper.Unknown, SignalLabelMapper.JitterStateLabel(9));
            Assert.Equal("-", SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, 0));
            Assert.Equal("1,234µs", SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, 1_234_999));
            Assert.Equal(DashJitter.Unbound, DashJitter.Classify(false, true, true));
            Assert.Equal(DashJitter.Normal, DashJitter.Classify(true, false, true));

            var r = PureShellFitnessSentinel.Scan(typeof(CanaryHealthSentinel).Assembly);
            Assert.True(r.WithinRatchet && r.HostCoupledUi <= 40, $"coupled {r.HostCoupledUi}");
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);

            string root = RepoRoot();
            foreach (var file in new[] { @"MS2E.UI.Portable\Helpers\SignalLabelMapper.cs", @"MS2E.UI.Portable\Ports\Trading\PaperDashboardPort.cs" })
            {
                string src = File.ReadAllText(Path.Combine(root, file));
                Assert.DoesNotContain("StockManager.", src);
                Assert.DoesNotContain("$\"", src);
            }
            foreach (var file in new[] { @"StockManager\Telemetry\MicrosecondJitterSentinel.cs", @"StockManager\Telemetry\JitterSnapshot.cs" })
            {
                string src = File.ReadAllText(Path.Combine(root, file));
                Assert.DoesNotContain("System.Linq", src);
                Assert.DoesNotContain("$\"", src);
                Assert.DoesNotContain("lock (", src);
            }
            string view = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Equal(2, view.Split('\n').Count(l => l.TrimStart().StartsWith("@inject ")));
            Assert.Contains("SignalLabelMapper.JitterStateLabel(f.JitterState)", view);
            Assert.Contains("SignalLabelMapper.JitterStateClass(f.JitterState)", view);
            Assert.Contains("SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, f.JitterLastSpikeNanos)", view);
            Assert.DoesNotContain("$\"", view);
            // 호스트 정직 미결선: 실 측정 생산자(센티널 작성자)는 Soak 하네스(시뮬레이션)뿐 — 호스트는 결선하지 않는다(배지 = '지터 미결선').
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.DoesNotContain("AttachJitter", form);
            Assert.DoesNotContain("LongSoakProfileHarness", form);
            string shell = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "Pages", "PaperTradingDashboard.razor"));
            Assert.DoesNotContain("@inject", shell);
        }
    }
}
