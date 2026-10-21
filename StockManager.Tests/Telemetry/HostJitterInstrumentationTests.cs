// ============================================================================
// HostJitterInstrumentationTests — [v12.18.6-DEV · 펌프 지터] DashboardSnapshotHub 50ms 펌프 실행 소요 → 호스트 전용 센티널 → /aidash 배지
//
//  §1 δ=0 실측 — 결선 펌프(유휴 회차 · 게시 회차 · 스파이크 기록 회차) 0B · 계측 Begin/End 단가(ns) 출력 + 1µs 미만 상한.
//  §2 지연 주입 — 구독자 700µs 바쁜 대기 = 펌프 실행 구간 안 → 스파이크 +1 · 최대/마지막 ≥ 700µs · Kind = PumpJitterKind ·
//     다음 회차 배지 Spike · GC.Collect(0) 동반 = GC 태그(GcSpike).
//  §3 결선 전환 — 미결선 = '지터 미결선' → AttachPumpJitter 후 첫 회차 = 정상 지표(표본 1) · 실 타이머 50ms 첫 틱 직후 전환.
//  §4 폐기 봉인 — Dispose = 진행 중 회차 대기 후 봉인 → 이후 Pump false · 표본 불변 · 센티널 폐기와 경합 0(오류 0).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Telemetry
{
    [Collection("ConcurrencyStress")]
    public sealed class HostJitterInstrumentationTests
    {
        private const long Us = 1_000;                                           // ns
        private const long Sec = 10_000_000;                                     // 허브 시계(100ns 틱) 1초
        private readonly ITestOutputHelper _out;
        public HostJitterInstrumentationTests(ITestOutputHelper o) => _out = o;

        private static void BusyWait(long nanos)
        {
            long until = Stopwatch.GetTimestamp() + nanos * Stopwatch.Frequency / 1_000_000_000;
            while (Stopwatch.GetTimestamp() < until) { }
        }

        private static (int State, long Spikes, long Gc, long Max, long Last) Frame(DashboardSnapshotHub hub)
        {
            var f = hub.Acquire();
            Assert.NotNull(f);
            try { return (f!.JitterState, f.JitterSpikes, f.JitterGcSpikes, f.JitterMaxNanos, f.JitterLastSpikeNanos); }
            finally { hub.Release(f!); }
        }

        // ════════════ §1 δ=0 ════════════

        [Fact]
        public void ZeroAlloc_InstrumentedPump_IdlePublishSpike_OverheadBounded()
        {
            using var j = new MicrosecondJitterSentinel();
            using var hub = new DashboardSnapshotHub(nowTicks: () => 1);
            int delay = 0;
            Action<long> slow = _ => { if (Volatile.Read(ref delay) != 0) BusyWait(600 * Us); };
            hub.FrameReady += slow;
            hub.AttachPumpJitter(j);
            for (int i = 0; i < 64; i++) { hub.OnTick(5930, 70_000 + i); hub.Pump(); hub.Pump(); }   // 예열(JIT · 게시/유휴)

            long px = 80_000;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) hub.Pump(); }));                          // 유휴 회차(무변경)
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 500; i++) { hub.OnTick(5930, px++); hub.Pump(); } })); // 게시 회차
            Volatile.Write(ref delay, 1);
            long spikes0 = j.Spikes;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 20; i++) { hub.OnTick(5930, px++); hub.Pump(); } }));  // 스파이크 기록 + 사본 게시
            Volatile.Write(ref delay, 0);
            Assert.True(j.Spikes - spikes0 >= 20, $"스파이크 {j.Spikes - spikes0}");
            Assert.Equal(0, hub.PumpFaults);

            // 계측 단가(Begin + End + 정상 경로 Record) — 펌프 주기 50ms 대비 간섭 상한 1µs
            using var probe = new MicrosecondJitterSentinel();
            const int N = 1_000_000;
            for (int i = 0; i < 10_000; i++) probe.End(MicrosecondJitterSentinel.Begin(out int g), g, 1, 0);
            double perOpNs = 0;
            Assert.Equal(0, ZeroAllocProbe.MinAllocated(() =>
            {
                long t = Stopwatch.GetTimestamp();
                for (int i = 0; i < N; i++) probe.End(MicrosecondJitterSentinel.Begin(out int g), g, 1, 0);
                perOpNs = (Stopwatch.GetTimestamp() - t) * 1e9 / Stopwatch.Frequency / N;
            }));
            _out.WriteLine($"[§1] 계측 단가 {perOpNs:F1}ns/회차 (Begin+End · 주기 50ms 대비 {perOpNs / 50_000_000 * 100:E2}%)");
            Assert.True(perOpNs < 1_000, $"계측 단가 {perOpNs:F1}ns");
        }

        // ════════════ §2 지연 주입 ════════════

        [Fact]
        public void InjectedDelay_SpikeCounted_MaxReflected_BadgeSpike_GcTagged()
        {
            long now = 100 * Sec;
            using var j = new MicrosecondJitterSentinel();
            using var hub = new DashboardSnapshotHub(nowTicks: () => Volatile.Read(ref now));
            long inject = 0; int collect = 0;
            hub.FrameReady += _ =>
            {
                long d = Volatile.Read(ref inject);
                if (d > 0) BusyWait(d);
                if (Volatile.Read(ref collect) != 0) GC.Collect(0);
            };
            hub.AttachPumpJitter(j);
            long px = 70_000;
            Assert.True(hub.Pump());                                                              // 첫 회차 = 기준선
            Assert.Equal(1, j.Samples);
            Assert.NotEqual(DashJitter.Unbound, Frame(hub).State);

            long before = j.Spikes;
            Volatile.Write(ref inject, 700 * Us);
            hub.OnTick(5930, px++); now += Sec / 20;
            Assert.True(hub.Pump());                                                              // 지연 회차(측정 → 다음 회차 반영)
            Volatile.Write(ref inject, 0);
            Assert.Equal(before + 1, j.Spikes);
            Assert.True(j.MaxNanos >= 700 * Us, $"최대 {j.MaxNanos}");
            Span<JitterSpike> last = stackalloc JitterSpike[1];
            Assert.Equal(1, j.CopySpikes(last));
            Assert.Equal(DashboardSnapshotHub.PumpJitterKind, last[0].Kind);
            Assert.True(last[0].Nanos >= 700 * Us);

            now += Sec / 20;
            Assert.True(hub.Pump());                                                              // 새 스파이크 = 서명 변경 → 즉시 재게시
            var fr = Frame(hub);
            Assert.Equal(last[0].Cause == JitterCause.Gc ? DashJitter.GcSpike : DashJitter.Spike, fr.State);
            Assert.True(fr.Spikes >= 1 && fr.Last >= 700 * Us && fr.Max >= 700 * Us);
            _out.WriteLine($"[§2] 지연 주입 → {SignalLabelMapper.JitterStateLabel(fr.State)} 마지막 {SignalLabelMapper.JitterMicrosText(SignalLabelMapper.JitterSlotLast, fr.Last)}");

            long gcBefore = j.GcSpikes;
            Volatile.Write(ref inject, 700 * Us); Volatile.Write(ref collect, 1);
            hub.OnTick(5930, px++); now += Sec / 20;
            Assert.True(hub.Pump());                                                              // GC 동반 지연
            Volatile.Write(ref inject, 0); Volatile.Write(ref collect, 0);
            Assert.Equal(gcBefore + 1, j.GcSpikes);
            now += Sec / 20;
            Assert.True(hub.Pump());
            fr = Frame(hub);
            Assert.Equal(DashJitter.GcSpike, fr.State);
            Assert.Equal("gc", SignalLabelMapper.JitterStateClass(fr.State));

            now += DashJitter.HoldTicks + Sec;                                                    // 유지 만료 → 정상 복귀(누적 보존)
            hub.Pump();
            fr = Frame(hub);
            if (j.Spikes == fr.Spikes && j.Spikes == before + 2)                                  // 그 사이 자연 스파이크 없음 = 결정적 복귀
                Assert.Equal(DashJitter.Normal, fr.State);
            Assert.Equal(0, hub.PumpFaults);
        }

        // ════════════ §3 결선 전환 ════════════

        [Fact]
        public void Unbound_ToLiveIndicator_FirstPump_AndRealTimerFirstTick()
        {
            using (var bare = new DashboardSnapshotHub())
            {
                Assert.True(bare.Pump());
                Assert.Equal("지터 미결선", SignalLabelMapper.JitterStateLabel(Frame(bare).State));
            }

            using var j = new MicrosecondJitterSentinel();
            using (var hub = new DashboardSnapshotHub())
            {
                Assert.True(hub.Pump());
                Assert.Equal(DashJitter.Unbound, Frame(hub).State);
                hub.AttachPumpJitter(j);
                hub.OnTick(5930, 70_000);
                Assert.True(hub.Pump());                                                          // 결선 후 첫 회차
                var fr = Frame(hub);
                Assert.NotEqual(DashJitter.Unbound, fr.State);
                Assert.NotEqual("지터 미결선", SignalLabelMapper.JitterStateLabel(fr.State));
                Assert.Equal(1, j.Samples);
            }

            using var live = new MicrosecondJitterSentinel();
            using var host = new DashboardSnapshotHub();
            host.AttachPumpJitter(live);                                                          // 호스트 순서 그대로: 결선 → Start
            host.Start(DashboardSnapshotHub.DefaultPeriodMs);
            var sw = Stopwatch.StartNew();
            while (host.Published == 0 && sw.ElapsedMilliseconds < 5_000) Thread.Sleep(5);
            Assert.True(host.Published >= 1, "5초 내 첫 틱 없음");
            var first = Frame(host);
            Assert.NotEqual(DashJitter.Unbound, first.State);
            while (live.Samples < 2 && sw.ElapsedMilliseconds < 5_000) Thread.Sleep(5);
            host.Dispose();                                                                       // 봉인 후 판독 = 작성자 정지
            Assert.True(live.Samples >= 2);
            _out.WriteLine($"[§3] 실 타이머 첫 게시 {sw.ElapsedMilliseconds}ms 이내 · 표본 {live.Samples} · 최대 {live.MaxNanos / Us}µs · 평균 {live.MeanNanos / Us}µs");
            Assert.Equal(0, host.PumpFaults);
        }

        // ════════════ §4 폐기 봉인 ════════════

        [Fact]
        public void Dispose_WaitsInFlightPump_SealsPump_SentinelDisposeRaceFree()
        {
            var j = new MicrosecondJitterSentinel();
            var hub = new DashboardSnapshotHub();
            int entered = 0;
            hub.FrameReady += _ => { Volatile.Write(ref entered, 1); BusyWait(20_000 * Us); };   // 20ms 회차
            hub.AttachPumpJitter(j);
            using var done = new CountdownEvent(1);
            var pumper = new Thread(() => { try { hub.Pump(); } finally { done.Signal(); } }) { IsBackground = true };
            pumper.Start();
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref entered) == 0 && sw.ElapsedMilliseconds < 5_000) Thread.Yield();
            Assert.Equal(1, Volatile.Read(ref entered));
            hub.Dispose();                                                                        // 진행 중 회차 종료까지 대기
            Assert.Equal(1, j.Samples);                                                           // 회차 기록 완료 후 반환
            j.Dispose();
            Assert.False(hub.Pump());                                                             // 봉인 = 진입 0
            hub.Dispose();                                                                        // 중복 폐기 = 무해
            Assert.True(done.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(0, hub.PumpFaults);
        }
    }
}
