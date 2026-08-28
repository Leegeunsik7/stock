// ============================================================================
// Phase94FContractTests — v11.5.0(94-F) 시그널/옵티마이저 읽기 전용 계약 지상 검증(순수 타입·DI 불요)
//   이주된 RCL SignalPage/OptimizerPage 가 소비하는 인터페이스를 콘크리트가 공변으로 정확히 구현하는지 확인:
//     · BacktestSummary → IBacktestSummaryView   · WalkForwardResult → IWalkForwardReport
//     · CompositeRule → ICompositeRuleContract    · SignalRow → ICoinSignalView
// ============================================================================
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Strategy;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase94FContractTests
    {
        [Fact]
        public void BacktestSummary_Implements_IBacktestSummaryView()
        {
            var s = new MS2E.Infrastructure.BacktestSummary
            {
                TotalReturn = 5.5m, MaxDrawdown = 12m, WinRate = 60m, TradeCount = 9, Sharpe = 1.4m,
                EquityCurve = new List<decimal> { 1_000_000m, 1_050_000m, 1_055_000m }
            };
            IBacktestSummaryView v = s;
            Assert.Equal(3, v.EquityCurve.Count);
            Assert.Equal(1_055_000m, v.EquityCurve[2]);
            Assert.Equal(5.5m, v.TotalReturn);
            Assert.Equal(9, v.TradeCount);
        }

        [Fact]
        public void WalkForwardResult_Implements_IWalkForwardReport_Covariant()
        {
            var r = new MS2E.Infrastructure.WalkForwardResult
            {
                IsValid = true, Verdict = "강건", RecShortP = 5, RecLongP = 20,
                AvgInSample = 3m, AvgOutSample = 2m, OosWinRate = 66m, Efficiency = 66m, Degradation = 1m,
                Folds = new List<MS2E.Infrastructure.WalkForwardFold>
                {
                    new() { Index = 0, ShortP = 5, LongP = 20, InSampleReturn = 3m, OutSampleReturn = 2m, OosTradeCount = 4 }
                }
            };
            IWalkForwardReport w = r;
            Assert.True(w.IsValid);
            Assert.Equal("강건", w.Verdict);
            Assert.Equal("MA5/20", w.RecLabel);
            Assert.Equal(1, w.FoldCount);
            IWalkForwardFold f = w.Folds[0];
            Assert.Equal("MA5/20", f.Label);
            Assert.True(f.OosWin);           // OutSampleReturn 2 > 0
            Assert.Equal(4, f.OosTradeCount);
        }

        [Fact]
        public void CompositeRule_Implements_ICompositeRuleContract_Covariant()
        {
            var rule = new MS2E.Infrastructure.CompositeRule
            {
                Name = "🅔 삼성 MA20 추세", Target = "005930", AssetKind = "stock", Enabled = true,
                Conditions = new List<MS2E.Infrastructure.CompositeCondition>
                {
                    new() { Metric = MS2E.Infrastructure.CondMetric.MaGapPct, Param = 20, Op = MS2E.Infrastructure.CondOp.Gte, Threshold = 0m }
                }
            };
            ICompositeRuleContract c = rule;
            Assert.Equal("005930", c.Target);
            Assert.Equal("stock", c.AssetKind);
            Assert.True(c.Enabled);
            Assert.Single(c.Conditions);
            ICondMetricView cv = c.Conditions[0];
            Assert.Equal("MA20 괴리", cv.MetricLabel);
            Assert.Contains("MA20 괴리", cv.Summary);
            Assert.Equal(20, cv.Param);
            Assert.Equal(0m, cv.Threshold);
        }

        [Fact]
        public void SignalRow_Implements_ICoinSignalView()
        {
            var row = new StockManager.Blazor.SignalRow("BUY", "삼성전자", "골든크로스", 80);
            ICoinSignalView v = row;
            Assert.Equal("BUY", v.Side);
            Assert.Equal("삼성전자", v.Name);
            Assert.Equal("골든크로스", v.Reason);
            Assert.Equal(80, v.Pct);
        }
    }
}
