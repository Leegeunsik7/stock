// ============================================================================
// MainDashboardAiFeedTests — v12.6.0 Main Dashboard AI Co-Pilot Feed & Real-Time Recommendation Board 적대검증
//   Section 1: DashboardAiFeedAdapter(보유종목→SIMD Top-N→Advisory 추천 카드 합성).
//   Section 2: OrderPrefillAdapter(Advisory-Only 프리필 단일 초크포인트) + 발주 파이프라인 무장 불가 리플렉션 검증.
//   Think Beyond: RationaleStreamProjector(δ=0 스트리밍 버퍼) · AiMicrostructureDivergenceGuard(AI↔호가 괴리).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Intel;
using MS2E.UI.Portable.Ports.Portfolio;
using MS2E.UI.Portable.Ports.Shell;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    internal sealed record FeedHolding(string Symbol, decimal AvgPrice, decimal CurrentPrice) : IHoldingView
    {
        public int Id => 0;
        public string Name => Symbol;
        public PfMarket Market => PfMarket.KRX_KOSPI;
        public PfCurrency Currency => PfCurrency.KRW;
        public decimal Quantity => 1m;
        public string BrokerAccount => "test";
        public decimal Commission => 0m;
        public decimal Tax => 0m;
    }

    internal sealed class StubPortfolioBoardPortWithHoldings : IPortfolioBoardPort
    {
        public IReadOnlyList<IHoldingView> Holdings { get; set; } = Array.Empty<IHoldingView>();
        public decimal TotalValue { get; set; }
        public decimal TotalPnl { get; set; }
        public decimal TotalRet { get; set; }
        public decimal TodayPnl { get; set; }
        public decimal TodayPct { get; set; }
        public decimal UsdKrw { get; set; } = 1300m;
        public string Timestamp { get; set; } = string.Empty;
        public int HoldingCount => Holdings.Count;

        public IReadOnlyList<IHoldingView> Deleted => Array.Empty<IHoldingView>();
        public IReadOnlyList<ICategoryView> Categories => Array.Empty<ICategoryView>();
        public IReadOnlyList<IHoldingView> ViewHoldings(string filter, string sortKey, bool sortDesc) => Holdings;
        public int FilterCount(string filter) => Holdings.Count;
        public bool TryGetDayPct(string symbol, out decimal pct) { pct = 0; return false; }
        public string SessionOf(string symbol) => string.Empty;
        public bool TryGetPrevPrice(string symbol, out decimal prev) { prev = 0; return false; }
        public double[] Spark(string symbol) => Array.Empty<double>();
        public Task LoadSparksAsync(Func<Task> onProgress) => Task.CompletedTask;
        public Task LoadAsync(bool persist = true) => Task.CompletedTask;
        public void LoadCustomOrder() { }
        public Task SaveHoldingAsync(HoldingDraft draft, bool edited) => Task.CompletedTask;
        public Task DeleteHoldingAsync(string symbol, string brokerAccount) => Task.CompletedTask;
        public Task RestoreHoldingAsync(string symbol, string brokerAccount) => Task.CompletedTask;
        public Task HardDeleteHoldingAsync(string symbol, string brokerAccount) => Task.CompletedTask;
        public void MoveCustom(string symbol, int dir, IReadOnlyList<string> viewSymbols) { }
        public IReadOnlyList<ITaxRowView> TaxRows() => Array.Empty<ITaxRowView>();
        public Task<PfCorrResult> CalcCorrelationAsync() => Task.FromResult(new PfCorrResult(Array.Empty<string>(), null, string.Empty));
        public Task LoadDividendsAsync() => Task.CompletedTask;
        public IReadOnlyList<IDividendView> Dividends => Array.Empty<IDividendView>();
        public (decimal commission, decimal tax) EstimateFees(string broker, PfMarket market, PfTxType type, decimal qty, decimal price) => (0m, 0m);
        public IReadOnlyList<string> Brokers => Array.Empty<string>();
        public PfAssetClass Classify(PfMarket market) => default;
        public HoldingDraft ToDraft(IHoldingView h) => new();
    }

    public sealed class MainDashboardAiFeedTests
    {
        private readonly ITestOutputHelper _out;
        public MainDashboardAiFeedTests(ITestOutputHelper o) => _out = o;

        private static DashboardAiFeedAdapter NewFeed(
            StubPortfolioBoardPortWithHoldings board, IOrderPrefillPort? prefill = null,
            AiMicrostructureDivergenceGuard? guard = null, Func<string, decimal?>? imbalanceLookup = null)
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var ai = new AiRecommendationAdapter(breaker);
            return new DashboardAiFeedAdapter(board, ai, prefill ?? new OrderPrefillAdapter(), guard, imbalanceLookup);
        }

        // ── §1 보유종목 → SIMD Top-N → 추천 카드 합성 ─────────────────────────
        [Fact]
        public async Task GetTopRecommendationsAsync_PicksHighestReturn_First()
        {
            var board = new StubPortfolioBoardPortWithHoldings
            {
                Holdings = new IHoldingView[]
                {
                    new FeedHolding("A", 100_000m, 100_500m),   // +0.5%
                    new FeedHolding("B", 100_000m, 120_000m),   // +20% → 최고 점수
                    new FeedHolding("C", 100_000m, 98_000m),    // -2%
                },
            };
            var feed = NewFeed(board);

            var cards = await feed.GetTopRecommendationsAsync(2, CancellationToken.None);

            Assert.Equal(2, cards.Count);
            Assert.Equal("B", cards[0].Symbol);   // 최고 수익률 종목이 1순위
            Assert.All(cards, c => Assert.True(c.IsAdvisoryOnly));
        }

        [Fact]
        public async Task GetTopRecommendationsAsync_NoHoldings_ReturnsEmpty()
        {
            var feed = NewFeed(new StubPortfolioBoardPortWithHoldings());
            var cards = await feed.GetTopRecommendationsAsync(5, CancellationToken.None);
            Assert.Empty(cards);
        }

        // ── §2 AI-Microstructure Divergence(Think Beyond B) ───────────────────
        [Theory]
        [InlineData(AiRecommendationGrade.StrongBuy, -0.9, true)]     // 매수추천·매도잔량폭주 → 괴리
        [InlineData(AiRecommendationGrade.StrongBuy, 0.5, false)]     // 매수추천·매수우위 → 정합
        [InlineData(AiRecommendationGrade.StrongSell, 0.9, true)]     // 매도추천·매수잔량폭주 → 괴리
        [InlineData(AiRecommendationGrade.Hold, -0.99, false)]        // 관망 등급은 괴리 판정 대상 아님
        public void DivergenceGuard_DetectsConflict(AiRecommendationGrade grade, double imbalance, bool expected)
        {
            var guard = new AiMicrostructureDivergenceGuard(imbalanceThreshold: 0.8m);
            Assert.Equal(expected, guard.IsDivergent(grade, (decimal)imbalance));
        }

        [Fact]
        public void DivergenceGuard_NullImbalance_FailsOpen_NotDivergent()
        {
            var guard = new AiMicrostructureDivergenceGuard();
            Assert.False(guard.IsDivergent(AiRecommendationGrade.StrongBuy, null));
        }

        // ── §3 Advisory-Only 프리필 단일 초크포인트 — 괴리 시 자동 차단 ────────
        [Fact]
        public void OrderPrefillAdapter_DivergenceBlocked_RefusesStage()
        {
            var prefill = new OrderPrefillAdapter();
            bool ok = prefill.TryStage("005930", 100_000m, 95_000m, divergenceBlocked: true);
            Assert.False(ok);
            Assert.False(prefill.Current.HasValue);
        }

        [Fact]
        public void OrderPrefillAdapter_ValidStage_Succeeds_And_RaisesOnChange()
        {
            var prefill = new OrderPrefillAdapter();
            int changes = 0;
            prefill.OnChange += () => changes++;

            bool ok = prefill.TryStage("005930", 100_000m, 95_000m, divergenceBlocked: false);

            Assert.True(ok);
            Assert.True(prefill.Current.HasValue);
            Assert.Equal("005930", prefill.Current.Symbol);
            Assert.Equal(1, changes);

            prefill.Clear();
            Assert.False(prefill.Current.HasValue);
            Assert.Equal(2, changes);
        }

        [Fact]
        public async Task Feed_TryStageOrderPrefill_BlockedWhenCardDivergent()
        {
            var board = new StubPortfolioBoardPortWithHoldings
            {
                Holdings = new IHoldingView[] { new FeedHolding("B", 100_000m, 120_000m) },   // → StrongBuy 유력
            };
            var prefill = new OrderPrefillAdapter();
            // 모든 심볼에 대해 매도 잔량 폭주(-0.95) 응답 → StrongBuy 와 상충
            var feed = NewFeed(board, prefill, imbalanceLookup: _ => -0.95m);

            var cards = await feed.GetTopRecommendationsAsync(1, CancellationToken.None);
            Assert.Single(cards);
            Assert.True(cards[0].Divergent);

            bool staged = feed.TryStageOrderPrefill(cards[0]);
            Assert.False(staged);
            Assert.False(prefill.Current.HasValue);
        }

        [Fact]
        public async Task Feed_TryStageOrderPrefill_SucceedsWhenNotDivergent()
        {
            var board = new StubPortfolioBoardPortWithHoldings
            {
                Holdings = new IHoldingView[] { new FeedHolding("B", 100_000m, 120_000m) },
            };
            var prefill = new OrderPrefillAdapter();
            var feed = NewFeed(board, prefill, imbalanceLookup: _ => 0.5m);   // 매수 우위 — 정합

            var cards = await feed.GetTopRecommendationsAsync(1, CancellationToken.None);
            Assert.False(cards[0].Divergent);

            bool staged = feed.TryStageOrderPrefill(cards[0]);
            Assert.True(staged);
            Assert.Equal("B", prefill.Current.Symbol);
        }

        // ── §4 RationaleStreamProjector(Think Beyond A) ────────────────────────
        [Fact]
        public void RationaleStreamProjector_AppendToken_AccumulatesAndVersions()
        {
            using var p = new RationaleStreamProjector(initialCapacity: 16);
            Assert.Equal(0, p.Version);
            p.AppendToken("AI ");
            p.AppendToken("분석 ");
            p.AppendToken("완료");
            Assert.Equal(3, p.Version);
            Assert.Equal("AI 분석 완료", p.View.ToString());
        }

        [Fact]
        public void RationaleStreamProjector_Reset_ClearsContentAndVersion()
        {
            using var p = new RationaleStreamProjector();
            p.AppendToken("abc");
            p.Reset();
            Assert.Equal(0, p.Version);
            Assert.Equal(0, p.View.Length);
        }

        // ── §5 liveArmed:false — AI 대시보드 파이프라인 발주 무장 불가(리플렉션) ──
        [Theory]
        [InlineData(typeof(DashboardAiFeedAdapter))]
        [InlineData(typeof(OrderPrefillAdapter))]
        [InlineData(typeof(AiMicrostructureDivergenceGuard))]
        public void AiDashboardTypes_NeverReference_LiveArmingOrDispatchTypes(Type target)
        {
            string[] forbidden =
            {
                "OrderDispatchRing", "GatedOrderDispatcher", "CanaryHealthSentinel", "HardwareKillSwitchBridge",
                "HardwareMasterKeyHandshake", "LockFreeBrokerSwitchboard", "HardwarePulseAntiReplayGuard",
                "IHardwareKillSwitchPort", "HardwareInterruptSpinLock", "HardwarePulseGuard",
            };
            var offenders = new List<string>();

            foreach (var ctor in target.GetConstructors())
                foreach (var p in ctor.GetParameters())
                    if (Array.IndexOf(forbidden, p.ParameterType.Name) >= 0)
                        offenders.Add($"ctor param {p.Name}:{p.ParameterType.Name}");

            foreach (var f in target.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                if (Array.IndexOf(forbidden, f.FieldType.Name) >= 0)
                    offenders.Add($"field {f.Name}:{f.FieldType.Name}");

            foreach (var m in target.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(forbidden, m.ReturnType.Name) >= 0) offenders.Add($"method {m.Name} returns {m.ReturnType.Name}");
                foreach (var p in m.GetParameters())
                    if (Array.IndexOf(forbidden, p.ParameterType.Name) >= 0)
                        offenders.Add($"method {m.Name} param {p.Name}:{p.ParameterType.Name}");
            }

            _out.WriteLine($"[MainDashboardAiFeed] scanned {target.FullName} — offenders: {offenders.Count}");
            Assert.Empty(offenders);
        }

        // ── §6 IAiRecommendationCardView.IsAdvisoryOnly 는 괴리/차단 상황에서도 항상 true ──
        [Fact]
        public async Task Card_IsAdvisoryOnly_AlwaysTrue_EvenWhenDivergentOrBlocked()
        {
            var board = new StubPortfolioBoardPortWithHoldings
            {
                Holdings = new IHoldingView[] { new FeedHolding("B", 100_000m, 130_000m) },
            };
            var feed = NewFeed(board, imbalanceLookup: _ => -0.99m);
            var cards = await feed.GetTopRecommendationsAsync(1, CancellationToken.None);
            Assert.True(cards[0].Divergent);
            Assert.True(cards[0].IsAdvisoryOnly);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §7 적대검증 — δ=0 핫패스 + 락프리 다중스레드
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class MainDashboardAiFeedConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public MainDashboardAiFeedConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly RationaleStreamProjector _hotProjector = new(8192);
        private static void AppendHot()
        {
            _hotProjector.AppendToken("tok ");
            if (_hotProjector.View.Length > 6000) _hotProjector.Reset();
        }

        [Fact]
        public void RationaleStreamProjector_AppendToken_ZeroAllocation_WithinCapacity()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) AppendHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) AppendHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly AiMicrostructureDivergenceGuard _hotGuard = new();
        private static long _hotSeq;
        private static void GuardHot()
        {
            long s = Interlocked.Increment(ref _hotSeq);
            _hotGuard.IsDivergent(AiRecommendationGrade.StrongBuy, (decimal)((s % 200) - 100) / 100m);
        }

        [Fact]
        public void DivergenceGuard_IsDivergent_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) GuardHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) GuardHot();
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
