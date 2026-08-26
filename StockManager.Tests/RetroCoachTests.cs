// ============================================================================
// RetroCoachTests - 회고 코치 순수 엔진 검증 (이동평균 페어링·편향 탐지·결정론·네트워크 0)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class RetroCoachTests
    {
        private static CoachTx Buy(string sym, int q, decimal p, DateTime d, double? prior = null) => new(sym, TransactionType.Buy, q, p, 0m, d, prior);
        private static CoachTx Sell(string sym, int q, decimal p, DateTime d, decimal fees = 0m, double? prior = null) => new(sym, TransactionType.Sell, q, p, fees, d, prior);
        private static readonly DateTime D0 = new(2024, 1, 1);

        [Fact]
        public void Empty_IsInvalid()
        {
            Assert.False(RetroCoach.Compute(Array.Empty<CoachTx>()).IsValid);
        }

        [Fact]
        public void Pair_MovingAverage_RealizesAgainstRunningAvg()
        {
            // 매수 100@10, 매수 100@20 → 평단 15. 매도 100@30 → 손익 = (30-15)*100 = 1500, +100%
            var txs = new List<CoachTx>
            {
                Buy("A", 100, 10, D0),
                Buy("A", 100, 20, D0.AddDays(10)),
                Sell("A", 100, 30, D0.AddDays(20)),
            };
            var (trades, _) = RetroCoach.Pair(txs);
            Assert.Single(trades);
            Assert.Equal(15m, trades[0].AvgCost);
            Assert.Equal(1500m, trades[0].PnlNet);
            Assert.Equal(100.0, trades[0].PnlPct, 3);
            Assert.True(trades[0].IsWin);
        }

        [Fact]
        public void Pair_FeesNetted_AndPartialSell()
        {
            // 매수 100@10. 부분 매도 50@12, 수수료 10 → 손익=(12-10)*50 - 10 = 90
            var txs = new List<CoachTx> { Buy("A", 100, 10, D0), Sell("A", 50, 12, D0.AddDays(5), fees: 10m) };
            var (trades, _) = RetroCoach.Pair(txs);
            Assert.Single(trades);
            Assert.Equal(50, trades[0].Qty);
            Assert.Equal(90m, trades[0].PnlNet);
        }

        [Fact]
        public void Pair_SellWithNoPosition_Ignored()
        {
            var txs = new List<CoachTx> { Sell("A", 50, 12, D0) };
            var (trades, _) = RetroCoach.Pair(txs);
            Assert.Empty(trades);
        }

        [Fact]
        public void AveragingDown_CountedWhenBuyingBelowAvg()
        {
            var txs = new List<CoachTx>
            {
                Buy("A", 100, 20, D0),
                Buy("A", 100, 10, D0.AddDays(5)),   // 평단 20 미만 추가매수 → 물타기 1
                Buy("A", 100, 5, D0.AddDays(8)),    // 평단(15) 미만 추가매수 → 물타기 2
            };
            var (_, avgDown) = RetroCoach.Pair(txs);
            Assert.Equal(2, avgDown);
        }

        [Fact]
        public void Disposition_DetectedWhenLosersHeldLonger()
        {
            // 승자는 5일 보유 후 익절, 패자는 60일 보유 후 손절 — 처분효과
            var txs = new List<CoachTx>();
            for (int i = 0; i < 4; i++)
            {
                var b = D0.AddDays(i * 100);
                txs.Add(Buy($"W{i}", 10, 100, b));
                txs.Add(Sell($"W{i}", 10, 110, b.AddDays(5)));    // +10%, 5일
            }
            for (int i = 0; i < 4; i++)
            {
                var b = D0.AddDays(1000 + i * 100);
                txs.Add(Buy($"L{i}", 10, 100, b));
                txs.Add(Sell($"L{i}", 10, 92, b.AddDays(60)));     // -8%, 60일
            }
            var rep = RetroCoach.Compute(txs);
            Assert.True(rep.IsValid);
            Assert.Contains(rep.Insights, i => i.Key == "disposition");
            Assert.True(rep.Stats.AvgHoldLossDays > rep.Stats.AvgHoldWinDays);
        }

        [Fact]
        public void Stats_WinRate_ProfitFactor_Sentinel()
        {
            // 전부 이긴 거래 → 손익비 ∞(999 센티넬)
            var txs = new List<CoachTx>();
            for (int i = 0; i < 3; i++)
            {
                var b = D0.AddDays(i * 30);
                txs.Add(Buy($"S{i}", 10, 100, b));
                txs.Add(Sell($"S{i}", 10, 120, b.AddDays(10)));
            }
            var rep = RetroCoach.Compute(txs);
            Assert.Equal(100.0, rep.Stats.WinRatePct, 3);
            Assert.True(rep.Stats.ProfitFactor >= 999);
            Assert.Equal("∞", RetroCoach.Pf(rep.Stats.ProfitFactor));
        }

        [Fact]
        public void Chasing_And_Panic_FromPriorReturns()
        {
            var txs = new List<CoachTx>();
            // 매수 6건 모두 직전 5일 +15% 후 진입(추격), 매도 6건 모두 직전 -15% 후 청산(패닉) — 표본 5건 이상
            for (int i = 0; i < 6; i++)
            {
                var b = D0.AddDays(i * 50);
                txs.Add(Buy($"C{i}", 10, 100, b, prior: 15.0));
                txs.Add(Sell($"C{i}", 10, 95, b.AddDays(10), prior: -15.0));
            }
            var rep = RetroCoach.Compute(txs);
            Assert.Contains(rep.Insights, i => i.Key == "chasing");
            Assert.Contains(rep.Insights, i => i.Key == "panic");
            Assert.Equal(6, rep.Stats.ChasingBuys);
            Assert.Equal(6, rep.Stats.PanicSells);
        }

        [Fact]
        public void Overtrading_NotFiredOnTinySpan()
        {
            // 5거래일 안에 5라운드트립 — 월 환산하면 거대하지만 관측구간이 짧아 과매매로 단정하지 않음
            var txs = new List<CoachTx>();
            for (int i = 0; i < 5; i++)
            {
                var b = D0.AddDays(i);
                txs.Add(Buy($"T{i}", 10, 100, b));
                txs.Add(Sell($"T{i}", 10, 110, b));
            }
            var rep = RetroCoach.Compute(txs);
            Assert.DoesNotContain(rep.Insights, i => i.Key == "overtrading");
        }

        [Fact]
        public void BuyFees_NettedSymmetrically()
        {
            // 매수 100@10 수수료 50, 매도 100@12 수수료 30 → 순손익 = 1200-30 - 1000 - 50 = 120
            var txs = new List<CoachTx>
            {
                new("A", TransactionType.Buy, 100, 10, 50m, D0, null),
                Sell("A", 100, 12, D0.AddDays(5), fees: 30m),
            };
            var (trades, _) = RetroCoach.Pair(txs);
            Assert.Single(trades);
            Assert.Equal(120m, trades[0].PnlNet);
        }

        [Fact]
        public void Breakeven_NotCountedAsLoss_NoFalseInfinity()
        {
            var txs = new List<CoachTx>
            {
                Buy("A", 100, 10, D0), Sell("A", 100, 11, D0.AddDays(5)),   // 승
                Buy("B", 100, 10, D0), Sell("B", 100, 10, D0.AddDays(5)),   // 본전(pnl=0)
            };
            var rep = RetroCoach.Compute(txs);
            Assert.Equal(2, rep.Stats.ClosedTrades);
            Assert.Equal(1, rep.Stats.Wins);
            Assert.Equal(0, rep.Stats.Losses);   // 본전은 패배가 아님
        }

        [Fact]
        public void Compute_Deterministic()
        {
            var txs = new List<CoachTx>
            {
                Buy("A", 100, 10, D0), Sell("A", 100, 12, D0.AddDays(7)),
                Buy("B", 50, 20, D0.AddDays(2)), Sell("B", 50, 18, D0.AddDays(40)),
            };
            var a = RetroCoach.Compute(txs);
            var b = RetroCoach.Compute(txs);
            Assert.Equal(a.Stats.WinRatePct, b.Stats.WinRatePct, 9);
            Assert.Equal(a.Stats.ProfitFactor, b.Stats.ProfitFactor, 9);
            Assert.Equal(a.Trades.Count, b.Trades.Count);
        }

        [Fact]
        public void FactsText_ContainsStatsAndDisclaimer()
        {
            var txs = new List<CoachTx> { Buy("A", 100, 10, D0), Sell("A", 100, 12, D0.AddDays(7)) };
            var rep = RetroCoach.Compute(txs);
            var txt = RetroCoach.FactsText(rep);
            Assert.Contains("거래 행동 통계", txt);
            Assert.Contains("승률", txt);
        }
    }
}
