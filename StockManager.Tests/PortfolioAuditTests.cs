// ============================================================================
// PortfolioAuditTests — v12.9.0 [Step 3/7] 포트폴리오 메뉴 전수 오디팅 적대검증
//   Think Beyond Idea A: PortfolioValuationMatrixCalculator(δ=0 평가액/손익/수익률/비중 매트릭스).
//   ★TotalAssetPage.razor/PortfolioPage.razor 의 RefreshSafeAsync/RefreshBoardSafeAsync 결속(폴링 루프 예외
//   무방비 결함 수정)은 bUnit 등 Blazor 렌더 하네스가 없어 xUnit 으로 직접 검증 불가 — 사중 빌드 0W/0E 컴파일
//   통과로 구조 정합만 확인(정직 명기, Step 1/2 와 동일 한계).
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class PortfolioAuditTests
    {
        private readonly ITestOutputHelper _out;
        public PortfolioAuditTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void Compute_ValuationPnlRetWeight_MatchesExpected()
        {
            decimal[] qty = { 10m, 5m };
            decimal[] avg = { 1000m, 2000m };
            decimal[] cur = { 1200m, 1800m };
            var val = new decimal[2]; var pnl = new decimal[2]; var ret = new decimal[2]; var wt = new decimal[2];

            PortfolioValuationMatrixCalculator.Compute(qty, avg, cur, val, pnl, ret, wt);

            Assert.Equal(12_000m, val[0]);   // 10 * 1200
            Assert.Equal(9_000m, val[1]);    // 5 * 1800
            Assert.Equal(2_000m, pnl[0]);    // 12000 - 10*1000
            Assert.Equal(-1_000m, pnl[1]);   // 9000 - 5*2000
            Assert.Equal(20m, ret[0]);       // (1200-1000)/1000*100
            Assert.Equal(-10m, ret[1]);      // (1800-2000)/2000*100
            // 비중 = 12000/21000*100, 9000/21000*100
            Assert.Equal(Math.Round(12_000m / 21_000m * 100m, 10), Math.Round(wt[0], 10));
            Assert.Equal(Math.Round(9_000m / 21_000m * 100m, 10), Math.Round(wt[1], 10));
        }

        [Fact]
        public void Compute_ZeroAvgPrice_YieldsZeroReturn_NotDivideByZero()
        {
            decimal[] qty = { 1m }; decimal[] avg = { 0m }; decimal[] cur = { 500m };
            var val = new decimal[1]; var pnl = new decimal[1]; var ret = new decimal[1]; var wt = new decimal[1];

            PortfolioValuationMatrixCalculator.Compute(qty, avg, cur, val, pnl, ret, wt);

            Assert.Equal(0m, ret[0]);    // fail-closed — 예외 없이 0%
            Assert.Equal(500m, val[0]);
            Assert.Equal(100m, wt[0]);   // 유일 종목 → 비중 100%
        }

        [Fact]
        public void Compute_EmptyPortfolio_AllWeightsZero_NoThrow()
        {
            decimal[] qty = { 0m }; decimal[] avg = { 0m }; decimal[] cur = { 0m };
            var val = new decimal[1]; var pnl = new decimal[1]; var ret = new decimal[1]; var wt = new decimal[1];
            PortfolioValuationMatrixCalculator.Compute(qty, avg, cur, val, pnl, ret, wt);
            Assert.Equal(0m, wt[0]);
        }

        [Fact]
        public void Compute_MismatchedSpanLength_Throws()
        {
            decimal[] qty = { 1m, 2m }; decimal[] avg = { 1m }; decimal[] cur = { 1m, 2m };
            var val = new decimal[2]; var pnl = new decimal[2]; var ret = new decimal[2]; var wt = new decimal[2];
            Assert.Throws<ArgumentException>(() => PortfolioValuationMatrixCalculator.Compute(qty, avg, cur, val, pnl, ret, wt));
        }

        [Fact]
        public void Summarize_AggregatesValuationAndPnl()
        {
            decimal[] val = { 12_000m, 9_000m };
            decimal[] pnl = { 2_000m, -1_000m };
            var (totalVal, totalPnl, totalRet) = PortfolioValuationMatrixCalculator.Summarize(val, pnl);
            Assert.Equal(21_000m, totalVal);
            Assert.Equal(1_000m, totalPnl);
            // cost = 21000-1000 = 20000, ret = 1000/20000*100 = 5%
            Assert.Equal(5m, totalRet);
        }

        [Fact]
        public void Summarize_MismatchedLength_Throws()
            => Assert.Throws<ArgumentException>(() => PortfolioValuationMatrixCalculator.Summarize(new decimal[2], new decimal[1]));
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §적대검증 — δ=0 핫패스
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class PortfolioAuditConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public PortfolioAuditConcurrencyTests(ITestOutputHelper o) => _out = o;

        private const int N = 64;
        private static readonly decimal[] _qty = new decimal[N], _avg = new decimal[N], _cur = new decimal[N];
        private static readonly decimal[] _val = new decimal[N], _pnl = new decimal[N], _ret = new decimal[N], _wt = new decimal[N];

        static PortfolioAuditConcurrencyTests()
        {
            for (int i = 0; i < N; i++) { _qty[i] = i + 1; _avg[i] = 1000m + i; _cur[i] = 1010m + i; }
        }

        [Fact]
        public void Compute_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) PortfolioValuationMatrixCalculator.Compute(_qty, _avg, _cur, _val, _pnl, _ret, _wt);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10_000; i++) PortfolioValuationMatrixCalculator.Compute(_qty, _avg, _cur, _val, _pnl, _ret, _wt);
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
