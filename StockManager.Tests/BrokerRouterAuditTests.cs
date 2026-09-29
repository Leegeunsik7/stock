// ============================================================================
// BrokerRouterAuditTests — v12.10.0 [Step 4/7] 트레이딩 샌드박스 & 브로커 라우터 전수 오디팅 적대검증
//   Section 2: ShadowBroker.Place 노이즈 활성 경로(기본값)의 가격 검증 누락 결함 수정 실증(Fail-Closed 초크포인트).
//   Think Beyond A: OrderParameterValidator(δ=0 호가단위/발주금액 검증, Int128 오버플로 안전).
//   Think Beyond B: PendingOrderCascadeCanceller(브로커 페일오버 시 미체결 잔량 원자 취소).
//   ★SandboxBrokerPage.razor 의 UI 레벨 선제 검증은 bUnit 부재로 xUnit 직접 검증 불가 — 사중 빌드 0W/0E 로 구조
//   정합만 확인(Step 1~3 과 동일 한계, 정직 명기).
// ============================================================================
using System;
using System.IO;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class BrokerRouterAuditTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        private readonly PaperBroker _inner;

        public BrokerRouterAuditTests(ITestOutputHelper o)
        {
            _out = o;
            _dbPath = Path.Combine(Path.GetTempPath(), $"shadowaudit_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
            _inner = new PaperBroker(_db);
            _inner.ResetSeed(10_000_000m);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        private static ShadowNoiseConfig NoiseOn(int ms = 100) => new()
        {
            Enabled = true, BaseDelayMs = ms, MaxDelayMs = ms,
            SlippageBasePct = 0.0, SlippageVolMultiplier = 0.0, Seed = 42,
        };

        // ── §1 ShadowBroker 가격 검증 결함 수정 실증 ────────────────────────────
        [Fact]
        public void ShadowBroker_Place_MarketOrder_ZeroCurrentPrice_Rejected()
        {
            var broker = new ShadowBroker(_inner, NoiseOn());
            var (ok, msg) = broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Market, qty: 1m, limitPrice: 0m, currentPrice: 0m);
            Assert.False(ok);
            Assert.Contains("현재가", msg);
            Assert.Equal(0, broker.QueueDepth);   // ★거부됐으므로 큐에 삽입되지 않아야 함
        }

        [Fact]
        public void ShadowBroker_Place_LimitOrder_ZeroLimitPrice_Rejected()
        {
            var broker = new ShadowBroker(_inner, NoiseOn());
            var (ok, msg) = broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, qty: 1m, limitPrice: 0m, currentPrice: 70_000m);
            Assert.False(ok);
            Assert.Contains("지정가", msg);
            Assert.Equal(0, broker.QueueDepth);
        }

        [Fact]
        public void ShadowBroker_Place_LimitOrder_NegativeLimitPrice_Rejected()
        {
            var broker = new ShadowBroker(_inner, NoiseOn());
            var (ok, _) = broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, qty: 1m, limitPrice: -100m, currentPrice: 70_000m);
            Assert.False(ok);
        }

        [Fact]
        public void ShadowBroker_Place_ValidOrder_StillAccepted_NoRegression()
        {
            var broker = new ShadowBroker(_inner, NoiseOn());
            var (ok, _) = broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Market, qty: 1m, limitPrice: 0m, currentPrice: 70_000m);
            Assert.True(ok);
            Assert.Equal(1, broker.QueueDepth);
        }

        // ── §2 OrderParameterValidator(Think Beyond A) ─────────────────────────
        [Fact]
        public void Validator_ValidOrder_ReturnsOk()
            => Assert.Equal(OrderValidationResult.Ok, OrderParameterValidator.Validate(70_000, 10, 100, 100_000, 1_000_000_000));

        [Fact]
        public void Validator_InvalidQty_Rejected()
            => Assert.Equal(OrderValidationResult.InvalidQty, OrderParameterValidator.Validate(70_000, 0, 100, 0, 0));

        [Fact]
        public void Validator_InvalidPrice_Rejected()
            => Assert.Equal(OrderValidationResult.InvalidPrice, OrderParameterValidator.Validate(0, 10, 100, 0, 0));

        [Fact]
        public void Validator_TickSizeViolation_Detected()
            => Assert.Equal(OrderValidationResult.TickSizeViolation, OrderParameterValidator.Validate(70_050, 10, 100, 0, 0));

        [Fact]
        public void Validator_TickSizeDisabled_WhenTickSizeZero()
            => Assert.Equal(OrderValidationResult.Ok, OrderParameterValidator.Validate(70_050, 10, 0, 0, 0));

        [Fact]
        public void Validator_BelowMinNotional_Rejected()
            => Assert.Equal(OrderValidationResult.BelowMinNotional, OrderParameterValidator.Validate(100, 1, 0, 10_000, 0));

        [Fact]
        public void Validator_AboveMaxNotional_Rejected()
            => Assert.Equal(OrderValidationResult.AboveMaxNotional, OrderParameterValidator.Validate(1_000_000, 100, 0, 0, 1_000_000));

        [Fact]
        public void Validator_HugeNotional_NoOverflow_Int128Safe()
        {
            // price×qty = 2.5e19 — long.MaxValue(~9.22e18)를 초과해 순수 long 곱셈이면 오버플로/음수 래핑된다.
            // Int128 경유라면 정확히 계산되어 maxNotional(1e18) 초과로 정상 판정되어야 한다.
            long price = 5_000_000_000L, qty = 5_000_000_000L;
            var r = OrderParameterValidator.Validate(price, qty, 0, 0, maxNotionalRaw: 1_000_000_000_000_000_000L);
            Assert.Equal(OrderValidationResult.AboveMaxNotional, r);
        }

        [Fact]
        public void ValidateBatch_ComputesPerElement()
        {
            long[] prices = { 70_000, 0, 70_050 };
            long[] qtys = { 10, 10, 10 };
            var results = new OrderValidationResult[3];
            OrderParameterValidator.ValidateBatch(prices, qtys, 100, 0, 0, results);
            Assert.Equal(OrderValidationResult.Ok, results[0]);
            Assert.Equal(OrderValidationResult.InvalidPrice, results[1]);
            Assert.Equal(OrderValidationResult.TickSizeViolation, results[2]);
        }

        [Fact]
        public void ValidateBatch_MismatchedLength_Throws()
        {
            long[] prices = { 1, 2 }; long[] qtys = { 1 };
            var results = new OrderValidationResult[2];
            Assert.Throws<ArgumentException>(() => OrderParameterValidator.ValidateBatch(prices, qtys, 0, 0, 0, results));
        }

        // ── §3 PendingOrderCascadeCanceller(Think Beyond B) ────────────────────
        [Fact]
        public void CascadeCanceller_CancelAllForBroker_CancelsOnlyThatBroker()
        {
            var c = new PendingOrderCascadeCanceller(16);
            int s0 = c.Register(brokerId: 0);
            int s1 = c.Register(brokerId: 0);
            int s2 = c.Register(brokerId: 1);

            int cancelled = c.CancelAllForBroker(0);

            Assert.Equal(2, cancelled);
            Assert.True(c.IsCancelled(s0));
            Assert.True(c.IsCancelled(s1));
            Assert.False(c.IsCancelled(s2));   // 브로커1은 무관 — 취소 안 됨
        }

        [Fact]
        public void CascadeCanceller_MarkFilled_ExcludesFromCascade()
        {
            var c = new PendingOrderCascadeCanceller(16);
            int s0 = c.Register(0);
            c.MarkFilled(s0);   // 체결 완료 — 추적 해제

            int cancelled = c.CancelAllForBroker(0);
            Assert.Equal(0, cancelled);
            Assert.False(c.IsCancelled(s0));
        }

        [Fact]
        public void CascadeCanceller_DoubleCancel_DoesNotDoubleCount()
        {
            var c = new PendingOrderCascadeCanceller(16);
            c.Register(0);
            int first = c.CancelAllForBroker(0);
            int second = c.CancelAllForBroker(0);   // 이미 취소된 슬롯 — 재카운트 없음
            Assert.Equal(1, first);
            Assert.Equal(0, second);
        }

        [Fact]
        public void CascadeCanceller_PendingCount_ExcludesCancelledAndFilled()
        {
            var c = new PendingOrderCascadeCanceller(16);
            c.Register(0); c.Register(0);
            int s2 = c.Register(0);
            c.MarkFilled(s2);
            Assert.Equal(2, c.PendingCount(0));
            c.CancelAllForBroker(0);
            Assert.Equal(0, c.PendingCount(0));
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §적대검증 — δ=0 핫패스
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class BrokerRouterAuditConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public BrokerRouterAuditConcurrencyTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void Validator_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) OrderParameterValidator.Validate(70_000, 10, 100, 0, 0);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) OrderParameterValidator.Validate(70_000, 10, 100, 0, 0);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly PendingOrderCascadeCanceller _hotCanceller = new(4096);
        private static long _hotBroker;
        private static void RegisterCancelHot()
        {
            long b = Interlocked.Increment(ref _hotBroker) % 4;
            int slot = _hotCanceller.Register((int)b);
            if (slot % 2 == 0) _hotCanceller.MarkFilled(slot);
        }

        [Fact]
        public void CascadeCanceller_Register_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) RegisterCancelHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10_000; i++) RegisterCancelHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
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
