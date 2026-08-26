// ============================================================================
// FrictionAdapterTests — S16 #1+#2: FrictionFeedbackBridge · AdaptiveExecutionTuner
//                         + TwapVwapExecutor 튜너 연결 (14개)
//
//  §1 FrictionFeedbackBridge (2)  — 스냅샷 필드 검증, 제로-할당 검증
//  §2 AdaptiveExecutionTuner (9)  — 증가·감소·유지·캡·미달샘플·제로-할당
//  §3 TwapVwapExecutor 연결 (3)   — 고마찰→슬라이스 증가, 저마찰→슬라이스 감소, 튜너 없음
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockManager.Tests
{
    // ── 공통 헬퍼 ─────────────────────────────────────────────────────────────

    file static class FrictionHelpers
    {
        public static FillPayload MakeFill(double price, long symCode = 5930L)
            => new FillPayload
            {
                OrderId        = 1L,
                SymbolCode     = symCode,
                FillPrice      = price,
                FillQty        = 10L,
                Side           = 1,
                TimestampTicks = 1L,
            };

        // 30건의 작은 잔차로 Welford 기준선 확립 (mean≈0, sigma≈0.00137)
        public static void PrimeSmall(SlippageSentinel s, int count)
        {
            double[] pat = { 0.001, -0.001, 0.002, -0.002, 0.0005, -0.0005, 0.0015, -0.0015 };
            for (int i = 0; i < count; i++)
            {
                double r = pat[i % pat.Length];
                s.RecordFill(new FillPayload
                {
                    OrderId        = i + 1L,
                    SymbolCode     = 5930L,
                    FillPrice      = 100_000.0 * (1 + r),
                    FillQty        = 10L,
                    Side           = 1,
                    TimestampTicks = 1L,
                }, 100_000.0);
            }
        }

        // Z >> HighZThreshold(1.5): 50% 이탈치 → Z ≈ 5.4
        public static (FrictionFeedbackBridge b, AdaptiveExecutionTuner t)
            HighZ(int initSlices = 10, int initDurSec = 60)
        {
            var s = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            PrimeSmall(s, 30);
            s.RecordFill(MakeFill(150_000.0), 100_000.0);
            var b = new FrictionFeedbackBridge(s);
            return (b, new AdaptiveExecutionTuner(b, initSlices, TimeSpan.FromSeconds(initDurSec)));
        }

        // Z ≈ 0.21 < LowZThreshold(0.5): 0.03% 이탈치
        public static (FrictionFeedbackBridge b, AdaptiveExecutionTuner t)
            LowZ(int initSlices = 10, int initDurSec = 60)
        {
            var s = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            PrimeSmall(s, 30);
            s.RecordFill(MakeFill(100_030.0), 100_000.0);
            var b = new FrictionFeedbackBridge(s);
            return (b, new AdaptiveExecutionTuner(b, initSlices, TimeSpan.FromSeconds(initDurSec)));
        }

        // Z ≈ 0.70 ∈ (0.5, 1.5): 0.10% 이탈치
        public static (FrictionFeedbackBridge b, AdaptiveExecutionTuner t)
            MidZ(int initSlices = 10, int initDurSec = 60)
        {
            var s = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            PrimeSmall(s, 30);
            s.RecordFill(MakeFill(100_100.0), 100_000.0);
            var b = new FrictionFeedbackBridge(s);
            return (b, new AdaptiveExecutionTuner(b, initSlices, TimeSpan.FromSeconds(initDurSec)));
        }
    }

    // ── 연결 테스트 전용 브로커 ───────────────────────────────────────────────

    file sealed class NopBroker : IBroker
    {
        public string  Name   => "Nop";
        public bool    IsLive => false;
        public (bool ok, string msg) Place(PaperAsset _, string __, string ___,
            PaperSide ____, PaperKind _____, decimal ______, decimal _______, decimal ________)
            => (true, "ok");
        public void    Cancel(string _) { }
        public decimal HeldQty(PaperAsset _, string __) => 0m;
    }

    // ── §1 FrictionFeedbackBridge ──────────────────────────────────────────────

    public class FrictionFeedbackBridgeTests
    {
        [Fact]
        public void GetSnapshot_FieldsMatchSentinel()
        {
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            FrictionHelpers.PrimeSmall(sentinel, 30);
            sentinel.RecordFill(FrictionHelpers.MakeFill(150_000.0), 100_000.0);

            var snap = new FrictionFeedbackBridge(sentinel).GetSnapshot();

            Assert.Equal(sentinel.SampleCount,   snap.SampleCount);
            Assert.Equal(sentinel.RunningMean,   snap.Mean);
            Assert.Equal(sentinel.RunningStdDev, snap.Sigma);
            Assert.Equal(sentinel.LastZ,          snap.LastZ);
        }

        [Fact]
        public void GetSnapshot_ZeroGcAllocation()
        {
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            FrictionHelpers.PrimeSmall(sentinel, 30);
            sentinel.RecordFill(FrictionHelpers.MakeFill(150_000.0), 100_000.0);
            var bridge = new FrictionFeedbackBridge(sentinel);

            // ★work-bounded 결정론(부하 무관): 측정을 '전용 스레드'에서 수행 — GC.GetAllocatedBytesForCurrentThread()
            //   는 per-thread 카운터라, 전용 스레드면 xUnit 병렬 실행/풀 스레드 재사용의 할당이 절대 섞이지 않는다.
            //   또한 측정 전 '동일 루프'를 충분히 워밍업해 tiered-JIT / OSR(긴 루프 최적화 재컴파일) 전환을 끝낸다.
            //   (과거 플래키: 단일 호출 워밍업만으론 측정 루프의 OSR 전환이 측정 구간에 섞여 간헐 delta>0.)
            long delta = -1;
            int  sink  = 0;   // 결과 누적 → JIT 가 호출/루프를 제거(elide)하지 못하게
            var th = new Thread(() =>
            {
                for (int w = 0; w < 300_000; w++) sink += bridge.GetSnapshot().SampleCount;   // OSR/tier-1 settle
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100_000; i++) sink += bridge.GetSnapshot().SampleCount;     // 측정(이미 최적화됨)
                long after  = GC.GetAllocatedBytesForCurrentThread();
                delta = after - before;
            }) { IsBackground = true };
            th.Start();
            Assert.True(th.Join(TimeSpan.FromSeconds(30)), "제로-할당 측정 스레드 타임아웃");
            GC.KeepAlive(sink);

            Assert.Equal(0L, delta);
        }
    }

    // ── §2 AdaptiveExecutionTuner ──────────────────────────────────────────────

    public class AdaptiveExecutionTunerTests
    {
        [Fact]
        public void Calibrate_HighZ_IncreasesSlicesAndDuration()
        {
            var (_, t) = FrictionHelpers.HighZ(initSlices: 10, initDurSec: 60);
            t.Calibrate();
            Assert.Equal(12, t.CurrentSlices);                      // 10 + 2
            Assert.Equal(70, (int)t.CurrentDuration.TotalSeconds);  // 60 + 10
        }

        [Fact]
        public void Calibrate_LowZ_DecreasesSlicesAndDuration()
        {
            var (_, t) = FrictionHelpers.LowZ(initSlices: 10, initDurSec: 60);
            t.Calibrate();
            Assert.Equal(9,  t.CurrentSlices);                      // 10 - 1
            Assert.Equal(50, (int)t.CurrentDuration.TotalSeconds);  // 60 - 10
        }

        [Fact]
        public void Calibrate_MidZ_HoldsValues()
        {
            var (_, t) = FrictionHelpers.MidZ(initSlices: 10, initDurSec: 60);
            t.Calibrate();
            Assert.Equal(10, t.CurrentSlices);
            Assert.Equal(60, (int)t.CurrentDuration.TotalSeconds);
        }

        [Fact]
        public void Calibrate_AtMaxSlices_ClampsToMaxSlices()
        {
            var (_, t) = FrictionHelpers.HighZ(initSlices: 63, initDurSec: 60);
            t.Calibrate();
            Assert.Equal(64, t.CurrentSlices);  // 63 + 2 = 65 → clamped to 64
        }

        [Fact]
        public void Calibrate_AtMinSlices_ClampsToMinSlices()
        {
            var (_, t) = FrictionHelpers.LowZ(initSlices: 2, initDurSec: 60);
            t.Calibrate();
            Assert.Equal(2, t.CurrentSlices);   // 2 - 1 = 1 → clamped to 2
        }

        [Fact]
        public void Calibrate_AtMaxDuration_ClampsToMaxDuration()
        {
            var (_, t) = FrictionHelpers.HighZ(initSlices: 10, initDurSec: 295);
            t.Calibrate();
            Assert.Equal(300, (int)t.CurrentDuration.TotalSeconds);  // 295 + 10 = 305 → 300
        }

        [Fact]
        public void Calibrate_AtMinDuration_ClampsToMinDuration()
        {
            var (_, t) = FrictionHelpers.LowZ(initSlices: 10, initDurSec: 15);
            t.Calibrate();
            Assert.Equal(10, (int)t.CurrentDuration.TotalSeconds);   // 15 - 10 = 5 → 10
        }

        [Fact]
        public void Calibrate_InsufficientSamples_NoChange()
        {
            var s = new SlippageSentinel();
            for (int i = 0; i < 3; i++)  // MinSamples=4 미달
                s.RecordFill(FrictionHelpers.MakeFill(150_000.0), 100_000.0);
            var t = new AdaptiveExecutionTuner(new FrictionFeedbackBridge(s), 10, TimeSpan.FromSeconds(60));

            t.Calibrate();

            Assert.Equal(10, t.CurrentSlices);
            Assert.Equal(60, (int)t.CurrentDuration.TotalSeconds);
        }

        [Fact]
        public void Calibrate_HotPath_ZeroGcAllocation()
        {
            var (_, t) = FrictionHelpers.HighZ(initSlices: 10, initDurSec: 60);
            t.Calibrate();  // JIT 워밍업

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000_000; i++) t.Calibrate();
            long after  = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }
    }

    // ── §3 TwapVwapExecutor + Tuner 연결 ──────────────────────────────────────

    public class TwapVwapTunerWiringTests
    {
        [Fact]
        public async Task Place_WithTuner_HighFriction_UsesIncreasedSlices()
        {
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            FrictionHelpers.PrimeSmall(sentinel, 30);
            sentinel.RecordFill(FrictionHelpers.MakeFill(150_000.0), 100_000.0);  // Z ≈ 5.4

            var bridge = new FrictionFeedbackBridge(sentinel);
            var tuner  = new AdaptiveExecutionTuner(bridge, 10, TimeSpan.FromSeconds(60));

            var tcs = new TaskCompletionSource<AlgoExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var executor = new TwapVwapExecutor(new NopBroker(),
                delayFactory: (_, ct) => Task.Delay(1, ct))
            {
                DefaultConfig = new AlgoConfig { MinQty = 1m },
                Tuner         = tuner,
            };
            executor.AlgoCompleted += e => tcs.TrySetResult(e);

            executor.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 10_000m, 85_000m, 85_000m);

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));   // 부하 하 알고 완료 지연 대비 데드락 안전망(완료 즉시 반환)
            Assert.Equal(12, result.Order.Slices);                       // 10 + SliceStepUp(2)
            Assert.Equal(70, (int)result.Order.Duration.TotalSeconds);   // 60 + DurationStep(10)
        }

        [Fact]
        public async Task Place_WithTuner_LowFriction_UsesDecreasedSlices()
        {
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 2.0 };
            FrictionHelpers.PrimeSmall(sentinel, 30);
            sentinel.RecordFill(FrictionHelpers.MakeFill(100_030.0), 100_000.0);  // Z ≈ 0.21

            var bridge = new FrictionFeedbackBridge(sentinel);
            var tuner  = new AdaptiveExecutionTuner(bridge, 10, TimeSpan.FromSeconds(60));

            var tcs = new TaskCompletionSource<AlgoExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var executor = new TwapVwapExecutor(new NopBroker(),
                delayFactory: (_, ct) => Task.Delay(1, ct))
            {
                DefaultConfig = new AlgoConfig { MinQty = 1m },
                Tuner         = tuner,
            };
            executor.AlgoCompleted += e => tcs.TrySetResult(e);

            executor.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 10_000m, 85_000m, 85_000m);

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));   // 부하 하 알고 완료 지연 대비 데드락 안전망(완료 즉시 반환)
            Assert.Equal(9,  result.Order.Slices);                       // 10 - SliceStepDown(1)
            Assert.Equal(50, (int)result.Order.Duration.TotalSeconds);   // 60 - DurationStep(10)
        }

        [Fact]
        public async Task Place_WithoutTuner_UsesDefaultConfig()
        {
            var tcs = new TaskCompletionSource<AlgoExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var executor = new TwapVwapExecutor(new NopBroker(),
                delayFactory: (_, ct) => Task.Delay(1, ct))
            {
                DefaultConfig = new AlgoConfig { MinQty = 1m, Slices = 7, Duration = TimeSpan.FromSeconds(45) },
            };
            executor.AlgoCompleted += e => tcs.TrySetResult(e);

            executor.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 10_000m, 85_000m, 85_000m);

            var result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));   // 부하 하 알고 완료 지연 대비 데드락 안전망(완료 즉시 반환)
            Assert.Equal(7,  result.Order.Slices);
            Assert.Equal(45, (int)result.Order.Duration.TotalSeconds);
        }
    }
}
