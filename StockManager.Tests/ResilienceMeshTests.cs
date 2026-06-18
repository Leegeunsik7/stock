// ============================================================================
// ResilienceMeshTests — S15 탄력 메시 단위 테스트 (18개)
//
//  §1 TwapVwapExecutor — TWAP 계산 (3), VWAP 계산 (2), 집행 루프 (3)
//  §2 MultiBrokerRouter — 정상 라우팅 (2), 폴백 전환 (3)
//  §3 SlippageSentinel  — 시장가 제외 (1), 연속 미달 (1), 연속 초과 (2), Welford 통계 (1)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // ── 테스트용 스파이 브로커 ────────────────────────────────────────────────

    file sealed class SpyBroker : IBroker
    {
        private readonly string _name;
        public SpyBroker(string name = "Spy") => _name = name;

        public string  Name   => _name;
        public bool    IsLive => false;

        public readonly List<(string Symbol, decimal Qty, PaperSide Side)> Calls = new();

        public (bool ok, string msg) Place(
            PaperAsset _, string symbol, string __,
            PaperSide side, PaperKind ___, decimal qty,
            decimal ____, decimal _____)
        {
            Calls.Add((symbol, qty, side));
            return (true, $"{_name}-{Calls.Count}");
        }

        public void Cancel(string _) { }

        public decimal HeldQty(PaperAsset _, string __) => 0m;
    }

    // ── 게이팅 동결 시 예외를 던지는 모의 브로커 ─────────────────────────────

    file sealed class FrozenBroker : IBroker
    {
        public string  Name   => "Frozen";
        public bool    IsLive => false;

        public (bool ok, string msg) Place(PaperAsset _, string __, string ___,
            PaperSide ____, PaperKind _____, decimal ______, decimal _______, decimal ________)
            => throw new InvalidOperationException("[GATING FREEZE] 테스트 강제 동결");

        public void    Cancel(string _) { }
        public decimal HeldQty(PaperAsset _, string __) => 5m;
    }

    // ── §1 TwapVwapExecutor ─────────────────────────────────────────────────

    public class TwapVwapExecutorTests
    {
        private static AlgoOrder MakeOrder(decimal qty, int slices, AlgoStyle style = AlgoStyle.Twap)
            => new AlgoOrder
            {
                Symbol     = "005930",
                Name       = "삼성전자",
                Asset      = PaperAsset.Stock,
                Side       = PaperSide.Buy,
                Kind       = PaperKind.Limit,
                TotalQty   = qty,
                LimitPrice = 85_000m,
                Style      = style,
                Slices     = slices,
                Duration   = TimeSpan.FromMinutes(10),
            };

        // ── TWAP 계산 ──────────────────────────────────────────────────────

        [Fact]
        public void ComputeTwap_EvenDivision_AllSlicesEqual()
        {
            var order  = MakeOrder(100m, 5);
            var result = new decimal[5];
            TwapVwapExecutor.ComputeTwap(order, result);

            Assert.All(result, q => Assert.Equal(20m, q));
            Assert.Equal(100m, result.Sum());
        }

        [Fact]
        public void ComputeTwap_UnevenQty_LastSliceAbsorbsRemainder()
        {
            var order  = MakeOrder(103m, 5);  // 103 / 5 = 20 × 4 + 23
            var result = new decimal[5];
            TwapVwapExecutor.ComputeTwap(order, result);

            Assert.Equal(20m, result[0]);
            Assert.Equal(20m, result[3]);
            Assert.Equal(23m, result[4]);
            Assert.Equal(103m, result.Sum());
        }

        [Fact]
        public void ComputeTwap_SingleSlice_EqualsTotal()
        {
            var order  = MakeOrder(77m, 1);
            var result = new decimal[1];
            TwapVwapExecutor.ComputeTwap(order, result);

            Assert.Equal(77m, result[0]);
        }

        // ── VWAP 계산 ──────────────────────────────────────────────────────

        [Fact]
        public void ComputeVwap_WeightedSlices_SumToTotalQty()
        {
            var order  = MakeOrder(1000m, 10, AlgoStyle.Vwap);
            var result = new decimal[10];
            TwapVwapExecutor.ComputeVwap(order, result);

            Assert.Equal(1000m, result.Sum());
            Assert.All(result, q => Assert.True(q >= 1m));
        }

        [Fact]
        public void ComputeVwap_OddQty_SumToTotalQty()
        {
            var order  = MakeOrder(501m, 7, AlgoStyle.Vwap);
            var result = new decimal[7];
            TwapVwapExecutor.ComputeVwap(order, result);

            Assert.Equal(501m, result.Sum());
        }

        // ── 집행 루프 ─────────────────────────────────────────────────────

        [Fact]
        public async Task Place_BelowMinQty_PassesThroughImmediately()
        {
            var spy = new SpyBroker();
            var executor = new TwapVwapExecutor(spy, delayFactory: FastDelay)
            {
                DefaultConfig = new AlgoConfig { MinQty = 100m }
            };

            var (ok, msg) = executor.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, qty: 50m, limitPrice: 85_000m, currentPrice: 0m);

            Assert.True(ok);
            Assert.DoesNotContain("ALGO", msg);
            Assert.Single(spy.Calls);
            await Task.CompletedTask;
        }

        [Fact]
        public async Task Place_AboveMinQty_TwapFiresAllSlices()
        {
            var spy = new SpyBroker();
            var executor = new TwapVwapExecutor(spy, delayFactory: FastDelay)
            {
                DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 5, Style = AlgoStyle.Twap }
            };

            var (ok, msg) = executor.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, qty: 100m, limitPrice: 85_000m, currentPrice: 0m);

            Assert.True(ok);
            Assert.StartsWith("ALGO-", msg);

            await Task.Delay(800);   // 여유 있는 대기: 5 × 1ms + 스케줄러 지연 허용

            Assert.Equal(5, spy.Calls.Count);
            Assert.Equal(100m, spy.Calls.Sum(c => c.Qty));
        }

        [Fact]
        public async Task Cancel_RunningAlgo_StopsBeforeAllSlices()
        {
            var spy = new SpyBroker();

            // 슬라이스 간격 20ms — 취소 전 3-4개만 발사되도록 설계
            var executor = new TwapVwapExecutor(
                spy,
                delayFactory: (ms, ct) => Task.Delay(20, ct))
            {
                DefaultConfig = new AlgoConfig { MinQty = 10m, Slices = 10, Style = AlgoStyle.Twap }
            };

            var (_, msg) = executor.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, qty: 100m, limitPrice: 85_000m, currentPrice: 0m);

            // 3-4개 발사 예상 시점(70ms)에 executor.Cancel 호출
            await Task.Delay(70);
            executor.Cancel(msg);   // 내부 per-algo CTS 취소

            // 추가 슬라이스가 발사되지 않도록 기다림
            await Task.Delay(200);

            Assert.InRange(spy.Calls.Count, 1, 9);  // 전체 10개 미만
        }

        private static Task FastDelay(int _, CancellationToken ct)
            => Task.Delay(1, ct);
    }

    // ── §2 MultiBrokerRouter ────────────────────────────────────────────────

    public class MultiBrokerRouterTests
    {
        [Fact]
        public void Place_PrimaryHealthy_RoutesToPrimary()
        {
            var primary  = new SpyBroker("Primary");
            var fallback = new SpyBroker("Fallback");
            var router   = new MultiBrokerRouter(primary, fallback);

            var (ok, msg) = router.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 10m, 85_000m, 0m);

            Assert.True(ok);
            Assert.Single(primary.Calls);
            Assert.Empty(fallback.Calls);
            Assert.False(router.IsUsingFallback);
        }

        [Fact]
        public void HandleProxyLost_SwitchesToFallback_EventFired()
        {
            var primary  = new SpyBroker("Primary");
            var fallback = new SpyBroker("Fallback");
            var router   = new MultiBrokerRouter(primary, fallback);

            FailoverEvent? fired = null;
            router.FailoverOccurred += e => fired = e;

            router.HandleProxyLost();

            Assert.True(router.IsUsingFallback);
            Assert.Equal(1, router.FailoverCount);
            Assert.NotNull(fired);
            Assert.Equal("Primary", fired!.PrimaryName);
            Assert.Equal("Fallback", fired.FallbackName);
            Assert.Contains("단절", fired.Reason);
        }

        [Fact]
        public void Place_AfterProxyLost_RoutesToFallback()
        {
            var primary  = new SpyBroker("Primary");
            var fallback = new SpyBroker("Fallback");
            var router   = new MultiBrokerRouter(primary, fallback);

            router.HandleProxyLost();

            router.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 10m, 85_000m, 0m);

            Assert.Empty(primary.Calls);
            Assert.Single(fallback.Calls);
        }

        [Fact]
        public void Place_PrimaryGatingFrozen_AutoFailsOverToFallback()
        {
            var frozen   = new FrozenBroker();
            var fallback = new SpyBroker("Fallback");
            var router   = new MultiBrokerRouter(frozen, fallback);

            FailoverEvent? fired = null;
            router.FailoverOccurred += e => fired = e;

            var (ok, _) = router.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 10m, 85_000m, 0m);

            Assert.True(ok);
            Assert.True(router.IsUsingFallback);
            Assert.NotNull(fired);
            Assert.Contains("동결", fired!.Reason);
            Assert.Single(fallback.Calls);
        }

        [Fact]
        public void HandleProxyLost_CalledTwice_EventFiredOnce()
        {
            var router  = new MultiBrokerRouter(new SpyBroker(), new SpyBroker());
            int eventCount = 0;
            router.FailoverOccurred += _ => eventCount++;

            router.HandleProxyLost();
            router.HandleProxyLost();

            Assert.Equal(1, eventCount);
            Assert.Equal(1, router.FailoverCount);
        }
    }

    // ── §3 SlippageSentinel ─────────────────────────────────────────────────

    public class SlippageSentinelTests
    {
        private static FillPayload MakeFill(double price, long symbolCode = 5930L)
            => new FillPayload
            {
                OrderId       = 1L,
                SymbolCode    = symbolCode,
                FillPrice     = price,
                FillQty       = 10L,
                Side          = 1,
                TimestampTicks = DateTime.UtcNow.Ticks,
            };

        [Fact]
        public void RecordFill_MarketOrder_ZeroExpectedPrice_Skipped()
        {
            var sentinel = new SlippageSentinel();
            sentinel.RecordFill(MakeFill(100_000.0), expectedPrice: 0.0);

            Assert.Equal(0, sentinel.SampleCount);
        }

        [Fact]
        public void RecordFill_SmallResiduals_NoAlert()
        {
            SlippageAlert? captured = null;
            var sentinel = new SlippageSentinel
            {
                AlertThresholdSigma  = 3.0,
                ConsecutiveAlertCount = 3,
            };
            sentinel.AlertTriggered += a => captured = a;

            // 작은 잔차 10건 — 절대 3σ 미달
            double[] residuals = { 0.001, -0.001, 0.002, -0.002, 0.0015,
                                   -0.0015, 0.001, -0.001, 0.0005, -0.0005 };
            foreach (double r in residuals)
            {
                sentinel.RecordFill(MakeFill(100_000.0 * (1 + r)), 100_000.0);
            }

            Assert.Null(captured);
            Assert.Equal(10, sentinel.SampleCount);
        }

        [Fact]
        public void RecordFill_HighResidual_BelowConsecutiveThreshold_NoAlert()
        {
            SlippageAlert? captured = null;
            var sentinel = new SlippageSentinel
            {
                AlertThresholdSigma  = 2.0,
                ConsecutiveAlertCount = 3,
            };
            sentinel.AlertTriggered += a => captured = a;

            // 충분한 기준 샘플(30건)로 mean≈0·sigma≈0.001 확립
            PrimeWithSmallResiduals(sentinel, 30);

            // 극단값 1건 → 연속 횟수 1 (< 3) → 경보 없음
            sentinel.RecordFill(MakeFill(150_000.0), 100_000.0);

            Assert.Null(captured);
        }

        [Fact]
        public void RecordFill_ConsecutiveHighZ_TriggersAlert()
        {
            SlippageAlert? captured = null;
            var sentinel = new SlippageSentinel
            {
                AlertThresholdSigma  = 2.0,
                ConsecutiveAlertCount = 3,
            };
            sentinel.AlertTriggered += a => captured = a;

            // 30건으로 기준선 확립: 극단값 오염 희석 (2번째 이탈치도 Z≈3.8 유지)
            PrimeWithSmallResiduals(sentinel, 30);

            // 50% 이탈 3회 연속: Z_1≈5.4 Z_2≈3.8 Z_3≈3.1 (모두 > 2σ)
            for (int i = 0; i < 3; i++)
                sentinel.RecordFill(MakeFill(150_000.0), 100_000.0);

            Assert.NotNull(captured);
            Assert.True(captured!.ZScore > 2.0, $"Z={captured.ZScore:F2} should be > 2.0");
            Assert.Equal(3, captured.ConsecutiveExceedances);
        }

        [Fact]
        public void RecordFill_BreakInConsecutive_ResetsCounter()
        {
            SlippageAlert? captured = null;
            var sentinel = new SlippageSentinel
            {
                AlertThresholdSigma  = 2.0,
                ConsecutiveAlertCount = 3,
            };
            sentinel.AlertTriggered += a => captured = a;

            // 30건으로 기준선 확립 (2번째 이탈치도 Z≈3.8 > 2σ 유지)
            PrimeWithSmallResiduals(sentinel, 30);

            // 극단값 2회 → 연속 2
            sentinel.RecordFill(MakeFill(150_000.0), 100_000.0);
            sentinel.RecordFill(MakeFill(150_000.0), 100_000.0);

            // 정상값 1건 → Z << 2σ → 연속 리셋
            sentinel.RecordFill(MakeFill(100_100.0), 100_000.0);

            // 다시 극단값 2회 → 연속 2 (임계치 3 미달)
            sentinel.RecordFill(MakeFill(150_000.0), 100_000.0);
            sentinel.RecordFill(MakeFill(150_000.0), 100_000.0);

            Assert.Null(captured);
        }

        [Fact]
        public void RunningStats_Welford_CorrectMeanAndStdDev()
        {
            var sentinel = new SlippageSentinel();

            // 잔차 0.01, 0.03, 0.05 → 평균=0.03, 표본표준편차=0.02
            double[] residuals = { 0.01, 0.03, 0.05 };
            foreach (double r in residuals)
                sentinel.RecordFill(MakeFill(100_000.0 * (1 + r)), 100_000.0);

            Assert.Equal(3, sentinel.SampleCount);
            Assert.InRange(sentinel.RunningMean,   0.0295, 0.0305);  // ≈ 0.03
            Assert.InRange(sentinel.RunningStdDev, 0.0195, 0.0205);  // ≈ 0.02
        }

        // ── 공통 헬퍼 ──────────────────────────────────────────────────────

        private static void PrimeWithSmallResiduals(SlippageSentinel s, int count)
        {
            double[] pattern = { 0.001, -0.001, 0.002, -0.002, 0.0005, -0.0005, 0.0015, -0.0015 };
            for (int i = 0; i < count; i++)
            {
                double r = pattern[i % pattern.Length];
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
    }
}
