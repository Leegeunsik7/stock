// ============================================================================
// RebalanceDriftTests - 리밸런싱 드리프트 계산/판정 (순수·결정적)
// ============================================================================
using System.Collections.Generic;
using Xunit;

namespace StockManager.Tests
{
    public class RebalanceDriftTests
    {
        private static Dictionary<string, double> D(params (string, double)[] kv)
        {
            var d = new Dictionary<string, double>();
            foreach (var (k, v) in kv) d[k] = v;
            return d;
        }

        [Fact]
        public void NoTarget_NotBreached()
        {
            var r = RebalanceDrift.Compute(D(("A", 1.0)), D());
            Assert.False(r.HasTarget);
            Assert.False(RebalanceDrift.Breached(r, 5));
        }

        [Fact]
        public void MaxDrift_AndTurnover()
        {
            // 현재 60/40, 타겟 50/50 → 각 10%p 드리프트, 회전율 10%
            var r = RebalanceDrift.Compute(D(("A", 0.6), ("B", 0.4)), D(("A", 0.5), ("B", 0.5)));
            Assert.Equal(10.0, r.MaxDriftPct, 6);
            Assert.Equal(10.0, r.TurnoverPct, 6);   // (|0.1|+|0.1|)/2 = 0.1
            Assert.Equal("A", r.MaxSymbol);
        }

        [Fact]
        public void MissingSymbol_CountsAsZero()
        {
            // 타겟엔 C 30%인데 현재 미보유 → C 드리프트 30%p
            var r = RebalanceDrift.Compute(D(("A", 0.7)), D(("A", 0.7), ("C", 0.3)));
            Assert.Equal(30.0, r.MaxDriftPct, 6);
            Assert.Equal("C", r.MaxSymbol);
        }

        [Fact]
        public void Breached_RespectsBand()
        {
            var r = RebalanceDrift.Compute(D(("A", 0.56), ("B", 0.44)), D(("A", 0.5), ("B", 0.5)));  // 6%p
            Assert.True(RebalanceDrift.Breached(r, 5));
            Assert.False(RebalanceDrift.Breached(r, 7));
        }

        [Fact]
        public void OnTarget_ZeroDrift()
        {
            var r = RebalanceDrift.Compute(D(("A", 0.5), ("B", 0.5)), D(("A", 0.5), ("B", 0.5)));
            Assert.Equal(0.0, r.MaxDriftPct, 6);
            Assert.Equal(0.0, r.TurnoverPct, 6);
            Assert.False(RebalanceDrift.Breached(r, 1));
        }

        [Fact]
        public void Deterministic_RowsSortedByAbsDiff()
        {
            var r = RebalanceDrift.Compute(D(("A", 0.5), ("B", 0.3), ("C", 0.2)), D(("A", 0.4), ("B", 0.4), ("C", 0.2)));
            // A +10%p, B -10%p, C 0 → 정렬 시 |diff| 큰 것 먼저
            Assert.True(System.Math.Abs(r.Rows[0].DiffPct) >= System.Math.Abs(r.Rows[^1].DiffPct));
            Assert.Equal("C", r.Rows[^1].Symbol);
        }
    }
}
