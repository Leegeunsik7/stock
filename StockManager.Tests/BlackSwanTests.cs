// ============================================================================
// BlackSwanTests - 역사적 위기 주입 (순수·결정론). 낙폭·베타 스케일·경계 검증.
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class BlackSwanTests
    {
        private static CrisisProfile Covid => BlackSwan.ById("covid20")!;
        private static List<BlackSwanHolding> One(double beta) => new() { new("005930", "삼성전자", 1.0, beta) };

        [Fact]
        public void Empty_IsInvalid_Single_IsValid()
        {
            Assert.False(BlackSwan.Inject(Covid, new List<BlackSwanHolding>()).IsValid);
            Assert.True(BlackSwan.Inject(Covid, One(1)).IsValid);
        }

        [Fact]
        public void Crisis_ProducesLoss_AndDrawdown()
        {
            var r = BlackSwan.Inject(Covid, One(1.0));
            Assert.True(r.IsValid);
            Assert.True(r.TerminalPct < -10);                 // 명백한 폭락
            Assert.True(r.MaxDrawdownPct <= r.TerminalPct + 1e-9);  // 저점은 종점보다 깊거나 같음
            Assert.Equal(Covid.MarketDailyReturns.Count + 1, r.EquityCurve.Count);
            Assert.InRange(r.TroughDay, 1, Covid.MarketDailyReturns.Count);
        }

        [Fact]
        public void HigherBeta_WorseOutcome()
        {
            var lo = BlackSwan.Inject(Covid, One(1.0));
            var hi = BlackSwan.Inject(Covid, One(2.0));
            Assert.True(hi.TerminalPct < lo.TerminalPct);     // 고베타가 더 큰 손실
            Assert.Equal(2.0, hi.PortfolioBeta, 6);
            Assert.True(hi.Worst[0].ContribPct < lo.Worst[0].ContribPct);
        }

        [Fact]
        public void Deterministic_And_WeightedBeta()
        {
            var hs = new List<BlackSwanHolding> { new("A", "A", 3, 0.5), new("B", "B", 1, 2.5) };
            var a = BlackSwan.Inject(Covid, hs);
            var b = BlackSwan.Inject(Covid, hs);
            Assert.Equal(a.TerminalPct, b.TerminalPct, 9);
            // 정규화 비중(0.75/0.25) × beta → 0.75*0.5 + 0.25*2.5 = 1.0
            Assert.Equal(1.0, a.PortfolioBeta, 6);
        }

        [Fact]
        public void Worst_SortedMostNegativeFirst()
        {
            var hs = new List<BlackSwanHolding> { new("LOW", "저베타", 0.5, 0.3), new("HIGH", "고베타", 0.5, 2.0) };
            var r = BlackSwan.Inject(Covid, hs);
            Assert.Equal("HIGH", r.Worst[0].Symbol);          // 고베타가 가장 큰 손실 기여
        }
    }
}
