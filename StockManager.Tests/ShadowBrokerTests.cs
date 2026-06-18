// ============================================================================
// ShadowBrokerTests — S12 #1 섀도우 샌드박스 브로커 단위 테스트
//  · 노이즈 비활성화 / 즉시 체결
//  · 지연 큐 등록 · 만기 체결 · 조기 취소
//  · 슬리피지 (매수 상승 / 매도 하락 / 0)
//  · 고변동성 레짐 → 슬리피지·지연 증폭
//  · 결정론 (동일 시드 → 동일 결과)
//  · 멀티 주문 정렬 (단축 지연 우선 체결)
//  · 지정가: 지연 후 inner 전달, 슬리피지 없음
//  · 위임 확인 (HeldQty, Name, IsLive)
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class ShadowBrokerTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        private readonly PaperBroker _inner;

        public ShadowBrokerTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"shadow_{Guid.NewGuid():N}.db");
            _db     = new LocalDatabase(_dbPath);
            _inner  = new PaperBroker(_db);
            _inner.ResetSeed(10_000_000m);   // 1,000만원 시드
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static ShadowNoiseConfig FixedDelay(int ms = 100) => new()
        {
            Enabled               = true,
            BaseDelayMs           = ms,
            MaxDelayMs            = ms,       // delayRange=0 → 고정 지연
            SlippageBasePct       = 0.0,
            SlippageVolMultiplier = 0.0,
            Seed                  = 0,
        };

        private static readonly DateTime T0 = new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

        // ════════════════════════════════════════════════════════════════════════
        // 1. 노이즈 비활성화 → 즉시 체결
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Place_NoiseDisabled_FillsImmediately()
        {
            var cfg    = new ShadowNoiseConfig { Enabled = false };
            var broker = new ShadowBroker(_inner, cfg);

            var (ok, _) = broker.Place(
                PaperAsset.Stock, "A", "테스트A",
                PaperSide.Buy, PaperKind.Market,
                1m, 0m, 1000m);

            Assert.True(ok);
            Assert.Equal(0, broker.QueueDepth);          // 큐에 쌓이지 않음
            Assert.Equal(1m, _inner.HeldQty(PaperAsset.Stock, "A"));
        }

        // ════════════════════════════════════════════════════════════════════════
        // 2. 지연 큐 등록 및 만기 체결
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Place_NoiseEnabled_EnqueuesOrder()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            broker.Tick(T0, (_, _) => 1000m);

            var (ok, _) = broker.Place(
                PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            Assert.True(ok);
            Assert.Equal(1, broker.QueueDepth);
            Assert.Equal(0m, _inner.HeldQty(PaperAsset.Stock, "A"));   // 아직 미체결
        }

        [Fact]
        public void Tick_BeforeDelay_OrderNotFilled()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            // 99ms 전진 — 만기 미도달
            var fills = broker.Tick(T0.AddMilliseconds(99), (_, _) => 1000m);

            Assert.Empty(fills);
            Assert.Equal(1, broker.QueueDepth);
        }

        [Fact]
        public void Tick_AtDelay_OrderFilled()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            // 정확히 100ms — 체결
            var fills = broker.Tick(T0.AddMilliseconds(100), (_, _) => 1000m);

            Assert.Single(fills);
            Assert.Equal(0, broker.QueueDepth);
            Assert.Equal(1m, _inner.HeldQty(PaperAsset.Stock, "A"));
        }

        // ════════════════════════════════════════════════════════════════════════
        // 3. 슬리피지
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Slippage_BuyOrder_FillPriceAboveSubmitted()
        {
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 0, MaxDelayMs = 0,
                SlippageBasePct = 0.01, SlippageVolMultiplier = 0.0, Seed = 0,
            };
            var broker = new ShadowBroker(_inner, cfg);
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            var fills = broker.Tick(T0.AddMilliseconds(1), (_, _) => 1000m);

            Assert.Single(fills);
            // 매수 슬리피지: 1000 × 1.01 = 1010
            Assert.Equal(1010m, fills[0].ActualFillPrice);
            Assert.True(fills[0].ActualFillPrice > fills[0].SubmittedPrice);
        }

        [Fact]
        public void Slippage_SellOrder_FillPriceBelowSubmitted()
        {
            // 먼저 매수 포지션 확보 (슬리피지=0으로)
            var noCfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 0, MaxDelayMs = 0,
                SlippageBasePct = 0.0, SlippageVolMultiplier = 0.0, Seed = 0,
            };
            var setup = new ShadowBroker(_inner, noCfg);
            setup.Tick(T0, (_, _) => 1000m);
            setup.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 2m, 0m, 1000m);
            setup.Tick(T0.AddMilliseconds(1), (_, _) => 1000m);

            // 슬리피지 있는 브로커로 매도
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 0, MaxDelayMs = 0,
                SlippageBasePct = 0.01, SlippageVolMultiplier = 0.0, Seed = 0,
            };
            var broker = new ShadowBroker(_inner, cfg);
            broker.Tick(T0.AddMilliseconds(1), (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Sell, PaperKind.Market, 1m, 0m, 1000m);

            var fills = broker.Tick(T0.AddMilliseconds(2), (_, _) => 1000m);

            Assert.Single(fills);
            // 매도 슬리피지: 1000 × (1 - 0.01) = 990
            Assert.Equal(990m, fills[0].ActualFillPrice);
            Assert.True(fills[0].ActualFillPrice < fills[0].SubmittedPrice);
        }

        [Fact]
        public void Slippage_Zero_FillsAtExactPrice()
        {
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 0, MaxDelayMs = 0,
                SlippageBasePct = 0.0, SlippageVolMultiplier = 0.0, Seed = 0,
            };
            var broker = new ShadowBroker(_inner, cfg);
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            var fills = broker.Tick(T0.AddMilliseconds(1), (_, _) => 1000m);

            Assert.Single(fills);
            Assert.Equal(1000m, fills[0].ActualFillPrice);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 4. 고변동성 레짐 → 슬리피지 증폭
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void HighVolRatio_AmplifiedSlippage()
        {
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 0, MaxDelayMs = 0,
                SlippageBasePct = 0.001, SlippageVolMultiplier = 1.0, Seed = 0,
            };

            // volR=1 브로커: slippage = 0.001 × (1 + 1×1) = 0.002
            var lowRegime = new RegimeSignal { VolRatio = 1.0 };
            var brokerLow = new ShadowBroker(_inner, cfg, () => lowRegime);
            brokerLow.Tick(T0, (_, _) => 1000m);
            brokerLow.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            var lowSlippage = brokerLow.PendingOrders[0].SlippagePct;

            // volR=5 브로커: slippage = 0.001 × (1 + 5×1) = 0.006
            var highRegime = new RegimeSignal { VolRatio = 5.0 };
            var brokerHigh = new ShadowBroker(_inner, cfg, () => highRegime);
            brokerHigh.Tick(T0, (_, _) => 1000m);
            brokerHigh.Place(PaperAsset.Stock, "B", "B", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            var highSlippage = brokerHigh.PendingOrders[0].SlippagePct;

            Assert.True(highSlippage > lowSlippage, "고변동성 시 슬리피지가 더 커야 함");
            Assert.Equal(0.002, lowSlippage,  10);
            Assert.Equal(0.006, highSlippage, 10);
        }

        [Fact]
        public void HighVolRatio_AmplifiedDelay()
        {
            // BaseDelay=100, MaxDelay=200, delayRange=100
            // volR=1: delayMs = 100 + rand×100×1 ∈ [100,200]
            // volR=3: delayMs = 100 + rand×100×3 ∈ [100,400]
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 100, MaxDelayMs = 200,
                SlippageBasePct = 0.0, Seed = 999,
            };

            var lowR  = new RegimeSignal { VolRatio = 1.0 };
            var highR = new RegimeSignal { VolRatio = 3.0 };

            var bLow  = new ShadowBroker(_inner, cfg, () => lowR);
            var bHigh = new ShadowBroker(_inner, cfg, () => highR);
            bLow .Tick(T0, (_, _) => 1000m);
            bHigh.Tick(T0, (_, _) => 1000m);
            bLow .Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            bHigh.Place(PaperAsset.Stock, "B", "B", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            // 고변동성에서의 최대 가능 지연이 저변동성 최대 지연보다 큼
            Assert.True(bHigh.PendingOrders[0].DelayMs >= bLow.PendingOrders[0].DelayMs);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 5. 결정론: 동일 시드 → 동일 지연·슬리피지 시퀀스
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Deterministic_SameSeed_SameDelayAndSlippage()
        {
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 50, MaxDelayMs = 300,
                SlippageBasePct = 0.005, SlippageVolMultiplier = 1.5, Seed = 777,
            };

            var dbPath2 = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"shadow2_{Guid.NewGuid():N}.db");
            using var db2    = new LocalDatabase(dbPath2);
            var       inner2 = new PaperBroker(db2);
            inner2.ResetSeed(10_000_000m);

            try
            {
                var b1 = new ShadowBroker(_inner, cfg);
                var b2 = new ShadowBroker(inner2,  cfg);
                b1.Tick(T0, (_, _) => 1000m);
                b2.Tick(T0, (_, _) => 1000m);

                for (int i = 0; i < 3; i++)
                {
                    b1.Place(PaperAsset.Stock, "X", "X", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
                    b2.Place(PaperAsset.Stock, "X", "X", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
                }

                for (int i = 0; i < 3; i++)
                {
                    Assert.Equal(b1.PendingOrders[i].DelayMs,    b2.PendingOrders[i].DelayMs);
                    Assert.Equal(b1.PendingOrders[i].SlippagePct, b2.PendingOrders[i].SlippagePct);
                }
            }
            finally
            {
                db2.Dispose();
                try { System.IO.File.Delete(dbPath2); } catch { }
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        // 6. 취소
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Cancel_BeforeFill_RemovesFromQueue()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(200));
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            var orderId = broker.PendingOrders[0].Id;
            broker.Cancel(orderId);
            Assert.Equal(0, broker.QueueDepth);

            // 만기 이후에도 체결 안됨
            var fills = broker.Tick(T0.AddMilliseconds(300), (_, _) => 1000m);
            Assert.Empty(fills);
            Assert.Equal(0m, _inner.HeldQty(PaperAsset.Stock, "A"));
        }

        [Fact]
        public void Cancel_NonExistentId_DelegatesToInner()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            // 존재하지 않는 ID를 취소해도 예외 없이 완료
            broker.Cancel("00000000");
        }

        // ════════════════════════════════════════════════════════════════════════
        // 7. 멀티 주문 정렬 — 짧은 지연 먼저 체결
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void MultipleOrders_ExecuteInDelayOrder()
        {
            // A: t0+0ms 접수, delay=100ms → ExecuteAt=t0+100ms
            // B: t0+50ms 접수, delay=100ms → ExecuteAt=t0+150ms
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            // 50ms 후 B 접수
            broker.Tick(T0.AddMilliseconds(50), (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "B", "B", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);

            // t0+100ms: A만 체결
            var fills1 = broker.Tick(T0.AddMilliseconds(100), (_, _) => 1000m);
            Assert.Single(fills1);
            Assert.Equal("A", fills1[0].Symbol);
            Assert.Equal(1, broker.QueueDepth);   // B 여전히 대기

            // t0+150ms: B 체결
            var fills2 = broker.Tick(T0.AddMilliseconds(150), (_, _) => 1000m);
            Assert.Single(fills2);
            Assert.Equal("B", fills2[0].Symbol);
            Assert.Equal(0, broker.QueueDepth);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 8. 지정가 주문: 지연 후 inner 전달, 슬리피지 없음
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void LimitOrder_NoSlippage_ForwardedToInner()
        {
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 100, MaxDelayMs = 100,
                SlippageBasePct = 0.01, SlippageVolMultiplier = 0.0, Seed = 0,
            };
            var broker = new ShadowBroker(_inner, cfg);
            // 매수 지정가 1000m, 현재가 1100m (> 지정가) → ProcessLimits 즉시 체결 방지
            broker.Tick(T0, (_, _) => 1100m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Limit, 1m, 1000m, 1100m);

            // 지정가: 슬리피지 없음
            Assert.Equal(0.0, broker.PendingOrders[0].SlippagePct);

            // 지연 후: inner에 지정가로 전달됨
            var fills = broker.Tick(T0.AddMilliseconds(100), (_, _) => 1100m);
            Assert.Single(fills);
            Assert.Equal(1000m, fills[0].ActualFillPrice);   // limit price 그대로

            // 현재가 > 지정가이므로 inner의 Pending 상태 — 아직 미체결
            Assert.Equal(0m, _inner.HeldQty(PaperAsset.Stock, "A"));

            // 현재가 = 지정가로 Tick → ProcessLimits가 체결
            broker.Tick(T0.AddMilliseconds(200), (_, _) => 1000m);
            Assert.Equal(1m, _inner.HeldQty(PaperAsset.Stock, "A"));
        }

        // ════════════════════════════════════════════════════════════════════════
        // 9. 위임 확인
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void HeldQty_DelegatesToInner()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(0));
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 3m, 0m, 1000m);
            broker.Tick(T0.AddMilliseconds(1), (_, _) => 1000m);

            Assert.Equal(3m, broker.HeldQty(PaperAsset.Stock, "A"));
            Assert.Equal(3m, _inner.HeldQty(PaperAsset.Stock, "A"));
        }

        [Fact]
        public void Name_ContainsInnerBrokerName()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            Assert.StartsWith("Shadow(", broker.Name);
            Assert.Contains(_inner.Name, broker.Name);
        }

        [Fact]
        public void IsLive_AlwaysFalse()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            Assert.False(broker.IsLive);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 10. 수량 0 거부 (내부 가드)
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Place_ZeroQty_Rejected()
        {
            var broker = new ShadowBroker(_inner, FixedDelay(100));
            broker.Tick(T0, (_, _) => 1000m);
            var (ok, _) = broker.Place(PaperAsset.Stock, "A", "A",
                PaperSide.Buy, PaperKind.Market, 0m, 0m, 1000m);
            Assert.False(ok);
            Assert.Equal(0, broker.QueueDepth);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 11. 노이즈 없을 때 QueueDepth=0 확인
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void NoiseDisabled_QueueAlwaysEmpty()
        {
            var broker = new ShadowBroker(_inner, new ShadowNoiseConfig { Enabled = false });
            broker.Tick(T0, (_, _) => 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            broker.Place(PaperAsset.Stock, "A", "A", PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            Assert.Equal(0, broker.QueueDepth);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 12. FillEvent 내용 검증
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void FillEvent_ContainsCorrectFields()
        {
            var cfg = new ShadowNoiseConfig
            {
                Enabled = true, BaseDelayMs = 100, MaxDelayMs = 100,
                SlippageBasePct = 0.005, SlippageVolMultiplier = 0.0, Seed = 0,
            };
            var broker = new ShadowBroker(_inner, cfg);
            broker.Tick(T0, (_, _) => 2000m);
            broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Market, 1m, 0m, 2000m);

            var fills = broker.Tick(T0.AddMilliseconds(100), (_, _) => 2000m);

            Assert.Single(fills);
            var f = fills[0];
            Assert.Equal("005930",      f.Symbol);
            Assert.Equal(PaperSide.Buy, f.Side);
            Assert.Equal(2000m,         f.SubmittedPrice);
            Assert.Equal(2010m,         f.ActualFillPrice);   // 2000 × 1.005
            Assert.Equal(100.0,         f.DelayMs,  0);
            Assert.True(f.WasAccepted);
        }
    }
}
