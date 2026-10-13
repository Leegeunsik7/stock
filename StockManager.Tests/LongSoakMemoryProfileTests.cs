// ============================================================================
// LongSoakMemoryProfileTests — [Long-Soak · 추천 A]
//
//  §1 24시간 가속 Soak(86,400 모의 초 · 8종목 · 실 게이팅 사슬 + 가상 브로커) — 메모리 평탄도: 구동 스레드 누적 할당 0B ·
//     Gen0/1/2 수집 0 · 관리 힙 변화 0 근방 · 오작동 0: 워밍업 후 집계어 raised 전이 0 · 블랙박스 InterlockRaised 0 · 스톰 미격발 ·
//     최종 집계어 0 · 가상 체결 = 송출(미체결 잔존 0) · 거래일 경계(12시간째 누적 거래량 리셋) 통과.
//  §2 상호 입증 — 같은 Soak 에 Working Set > 1GB 주입 = 다음 헬스 스캔에서 정확히 비트 21(WorkingSet) 격발 · 이후 송출 0.
//  §3 [방향 B] 미세 누수 예측 — 1시간+ 꾸준한 우상향 · ETA ≤ 1시간 = MemoryTrend 트립 / 평탄+잡음 · GC 톱니 · 기동 성장(여유 큼) ·
//     창 1시간 미만 = 무트립.
//  §4 [실결함] 세션 VWAP — 누적 거래량 감소(새 거래일)에서 리셋(수정 전 여러 날 평균).
//  §5 비관리 호가 표 누수 0(Dispose 후 LiveAllocations 복귀) · 오중 빌드.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Buffers;
using StockManager.Engines;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class LongSoakMemoryProfileTests
    {
        private readonly ITestOutputHelper _out;
        public LongSoakMemoryProfileTests(ITestOutputHelper o) => _out = o;

        // ════════════ §1 24시간 가속 Soak ════════════

        [Fact]
        public void Soak24h_FlatMemory_ZeroGc_ZeroFalsePositives()
        {
            using var soak = new LongSoakProfileHarness();
            soak.Warmup();
            Assert.Equal(0L, soak.Sim.Interlocks.Mask);
            soak.RunSeconds(3_600);                                                               // 1시간 예열(JIT · 종목 캐시 · 링 회전)
            // GC 수집 횟수는 프로세스 전역 — 앞선 테스트(스레드 대량 생성 등)의 잔여 가비지가 측정 창에서 수집되면 오탐(실측 1/1/0).
            //   측정 직전 정리 + 기준선 재설정 → 창 안 GC 는 창 안 할당에서만 생긴다(구동 스레드 0B 는 별도 단언).
            //   [v12.18.4-DEV] 정리는 하네스 Settle — 그 강제 GC 정지는 헬스 GC 정지 판정(스캔 간 > 500ms = 비트 21)에서 제외(스위트 후반 오탐 교정).
            soak.Settle();
            soak.ResetBaseline();
            long t0 = Stopwatch.GetTimestamp();                                                   // (Stopwatch 객체 = 40B — 측정 구간 오염 방지)
            soak.RunSeconds(86_400);                                                              // ★24시간 측정 구간
            long realMs = (Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency;
            var r = soak.Report();
            _out.WriteLine($"[Soak] 모의 {r.SimSeconds:N0}s(실 {realMs:N0}ms) · 틱 {r.Ticks:N0} · 호가 {r.Books:N0} · 주문 {r.Orders:N0} · 체결 {r.Filled:N0}");
            _out.WriteLine($"[Soak] 표본 {r.Samples} · 스레드 할당 Δ {r.ThreadAllocatedDelta:N0}B · GC Δ {r.Gen0Delta}/{r.Gen1Delta}/{r.Gen2Delta} · 관리 힙 Δ {r.ManagedHeapDelta:N0}B · WS Δ {r.WorkingSetDelta:N0}B");
            _out.WriteLine($"[Soak] raised(워밍업 후) {r.RaisedAfterWarmup} · 블랙박스 {r.BlackboxRaisedAfterWarmup} · 최종 마스크 0x{r.FinalMask:X} · 스톰 {r.StormTripped} · 헬스 {r.HealthBreach} · 정량 체결 {soak.Quant.Trades:N0}");
            Assert.True(r.Samples >= 25);                                                         // 기준선 1 + 시간당 24
            Assert.Equal(0, r.ThreadAllocatedDelta);                                               // ★24시간 구동 스레드 관리 힙 할당 0B
            Assert.Equal((0L, 0L, 0L), (r.Gen0Delta, r.Gen1Delta, r.Gen2Delta));                  // ★GC 수집 0(Flatline)
            Assert.True((r.RaisedAfterWarmup, r.BlackboxRaisedAfterWarmup, r.FinalMask, r.StormTripped, r.HealthBreach) == (0L, 0L, 0L, false, 0),
                        $"오작동 — 헬스 {(HealthBreach)r.HealthBreach} · 스레드풀 대기 {soak.Health.LastStallMicros:N0}µs · 제외 GC 정지 {soak.ExcludedPauseMicros:N0}µs");   // ★오작동 0
            Assert.Equal(r.Placed, r.Filled);                                                      // 미체결 잔존 0
            Assert.True(r.Placed >= 2_800, "송출 " + r.Placed);                                    // 24h × 60 × 2 = 2,880(게이트 통과분)
            Assert.False(soak.Anomaly.Tripped);
        }

        // ════════════ §2 상호 입증 ════════════

        [Fact]
        public void Soak_InjectedWorkingSetOver1GB_HealthAgentDisarmsExactly()
        {
            using var soak = new LongSoakProfileHarness();
            soak.Warmup();
            soak.RunSeconds(7_200);
            Assert.Equal(0L, soak.Sim.Interlocks.Mask);
            long placed = soak.Sim.Broker.Placed;
            soak.InjectWorkingSet((1L << 30) + (64L << 20));                                       // 1.06GB
            soak.RunSeconds(3_600);                                                                // 다음 스캔에서 판정
            Assert.Equal(HealthBreach.WorkingSet, soak.Health.Breach);                             // ★정확히 WorkingSet 사유만
            Assert.Equal(1L << (int)ArmingInterlockSource.HealthAgent, soak.Sim.Interlocks.Mask);  // ★비트 21 단독
            long atTrip = soak.Sim.Broker.Placed;
            soak.InjectWorkingSet(0);
            soak.RunSeconds(3_600);
            Assert.Equal(atTrip, soak.Sim.Broker.Placed);                                          // 격발 후 송출 0(sticky — 값 복귀로 풀리지 않음)
            Assert.True(soak.Sim.Worker.InterlockVetoes > 0);
            Assert.True(atTrip > placed);
        }

        // ════════════ §3 [방향 B] 미세 누수 예측 ════════════

        private sealed class Fake { public long Ts, Ws; }

        private static HealthMonitoringAgent Agent(Fake f, long max = 1L << 30)
            => new(max, workingSet: () => f.Ws, gcPauseTicks: () => 0, nowTimestamp: () => f.Ts, useThreadPoolProbe: false);

        private static void Minute(Fake f) => f.Ts += 60L * Stopwatch.Frequency;

        [Fact]
        public void MicroLeak_SteadyRiseWithShortEta_TripsBeforeLimit()
        {
            var f = new Fake { Ts = 1, Ws = 700L << 20 };
            var a = Agent(f);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.HealthAgent);
            a.BindInterlock(mask, ArmingInterlockSource.HealthAgent);
            HealthBreach got = HealthBreach.None;
            int minutes = 0;
            for (; minutes < 240 && got == HealthBreach.None; minutes++) { got = a.Scan(); Minute(f); f.Ws += 3L << 20; }   // 분당 +3MB(시간당 180MB)
            Assert.Equal(HealthBreach.MemoryTrend, got);                                           // ★상한(1GB) 전 예측 트립
            Assert.True(f.Ws < (1L << 30), "상한 도달 전이어야 함: " + (f.Ws >> 20) + "MB");
            Assert.True(minutes >= 60, "1시간 창 이전 트립 금지: " + minutes);
            Assert.True(a.LastTrendR2 > 0.99 && a.LastTrendEtaSeconds <= 3_600);
            Assert.True(mask.IsRaised(ArmingInterlockSource.HealthAgent));
            _out.WriteLine($"[누수 예측] {minutes}분 · WS {f.Ws >> 20}MB · 기울기 {a.LastTrendSlopeBytesPerHour / (1 << 20):0}MB/h · ETA {a.LastTrendEtaSeconds / 60:0}분 · R² {a.LastTrendR2:0.000}");
        }

        [Fact]
        public void MicroLeak_NoFalsePositive_FlatNoise_GcSawtooth_StartupGrowth_ShortWindow()
        {
            var rng = new Random(7);
            var flat = new Fake { Ts = 1, Ws = 600L << 20 };
            var a1 = Agent(flat);
            for (int m = 0; m < 300; m++) { Assert.Equal(HealthBreach.None, a1.Scan()); Minute(flat); flat.Ws = (600L << 20) + rng.Next(-20 << 20, 20 << 20); }

            var saw = new Fake { Ts = 1, Ws = 500L << 20 };                                        // GC 톱니(10분 상승 후 회수)
            var a2 = Agent(saw);
            for (int m = 0; m < 300; m++) { Assert.Equal(HealthBreach.None, a2.Scan()); Minute(saw); saw.Ws = (500L << 20) + (m % 10) * (30L << 20); }

            var boot = new Fake { Ts = 1, Ws = 200L << 20 };                                       // 기동 성장 — 시간당 100MB · 여유 큼(ETA ≫ 1h)
            var a3 = Agent(boot);
            for (int m = 0; m < 180; m++) { Assert.Equal(HealthBreach.None, a3.Scan()); Minute(boot); boot.Ws += (100L << 20) / 60; }
            Assert.True(a3.LastTrendR2 > 0.99 && a3.LastTrendEtaSeconds > 3_600);                 // 추세는 보지만 판정은 여유로 무트립

            var fast = new Fake { Ts = 1, Ws = 900L << 20 };                                       // 급증이지만 창 1시간 미만
            var a4 = Agent(fast);
            for (int m = 0; m < 50; m++) { Assert.Equal(HealthBreach.None, a4.Scan()); Minute(fast); fast.Ws += 1L << 20; }
            Assert.Equal(0, a4.LastTrendR2);                                                       // 창 미달 = 추세 미산출
        }

        [Fact]
        public void TrendScan_ZeroAlloc()
        {
            var f = new Fake { Ts = 1, Ws = 600L << 20 };
            var a = Agent(f);
            for (int m = 0; m < 200; m++) { a.Scan(); Minute(f); }
            long b = GC.GetAllocatedBytesForCurrentThread();
            for (int m = 0; m < 2_000; m++) { a.Scan(); Minute(f); f.Ws ^= 1 << 12; }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - b);
        }

        // ════════════ §4 세션 VWAP ════════════

        [Fact]
        public void SessionVwap_ResetsOnCumulativeVolumeRollover()
        {
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: () => 1);
            const long Sym = 5930;
            q.OnTrade(Sym, 60_000, 1_000);
            q.OnTrade(Sym, 60_000, 11_000);                                                       // 전일 VWAP 60,000(1만 주)
            q.OnTrade(Sym, 70_000, 100);                                                          // ★누적 감소 = 새 거래일 — 100주 @70,000
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int i = 0; i < 10; i++) { b.AskPrice[i] = 70_050 + 100 * i; b.BidPrice[i] = 69_950 - 100 * i; b.AskQty[i] = 100; b.BidQty[i] = 100; }
            q.OnBook(in b);                                                                       // mid 70,000
            Assert.True(q.TryGetSignal(Sym, out var s));
            Assert.InRange(s.VwapDeviationBp, -0.01, 0.01);                                       // 세션 VWAP = 70,000(수정 전 ≈ 60,099 → +1,647bp)
        }

        // ════════════ §5 비관리 표 · 오중 빌드 ════════════

        [Fact]
        public void NativeBookTable_NoLeakAfterSoak_FiveBuilds()
        {
            long before = NativeMemoryHandle.LiveAllocations;
            using (var soak = new LongSoakProfileHarness())
            {
                soak.Warmup();
                soak.RunSeconds(600);
                Assert.True(soak.NativeBooks.Writes > 0);
                Assert.True(NativeMemoryHandle.LiveAllocations > before);                         // 비관리 표(호가 · 하네스 깊이 등) 생존 중
            }
            Assert.Equal(before, NativeMemoryHandle.LiveAllocations);                             // ★비관리 할당 복귀(누수 0)
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string sln = File.ReadAllText(Path.Combine(d!.FullName, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}
