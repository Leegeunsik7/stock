// ============================================================================
// FutureConeTests - 확률 미래 엔진 (순수·결정론). 시드 재현·밴드 단조·경계 검증.
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class FutureConeTests
    {
        private static List<double> Rets(int n, double r)
        {
            var l = new List<double>();
            for (int i = 0; i < n; i++) l.Add(r);
            return l;
        }
        // 결정론적 의사 변동 시계열(시드 없는 단순 생성 — 테스트 입력용)
        private static List<double> Wobble(int n)
        {
            var l = new List<double>();
            for (int i = 0; i < n; i++) l.Add(((i * 37 % 11) - 5) / 1000.0);   // -0.5%~+0.5% 결정적
            return l;
        }

        [Fact]
        public void InsufficientSample_IsInvalid()
        {
            var r = FutureCone.Project(Rets(10, 0.001), 21, 1000, 1);
            Assert.False(r.IsValid);
        }

        [Fact]
        public void Deterministic_SameSeed_SameResult()
        {
            var data = Wobble(120);
            var a = FutureCone.Project(data, 30, 1000, 42, goalPct: 5);
            var b = FutureCone.Project(data, 30, 1000, 42, goalPct: 5);
            Assert.Equal(a.MedianReturnPct, b.MedianReturnPct, 10);
            Assert.Equal(a.P95ReturnPct, b.P95ReturnPct, 10);
            Assert.Equal(a.GoalHitPct, b.GoalHitPct, 10);
            Assert.Equal(a.Bands.Count, b.Bands.Count);
        }

        [Fact]
        public void Bands_AreMonotone_AndHorizonLength()
        {
            var r = FutureCone.Project(Wobble(200), 40, 1500, 7);
            Assert.True(r.IsValid);
            Assert.Equal(40, r.Bands.Count);
            foreach (var b in r.Bands)
            {
                Assert.True(b.P5 <= b.P25 + 1e-9);
                Assert.True(b.P25 <= b.P50 + 1e-9);
                Assert.True(b.P50 <= b.P75 + 1e-9);
                Assert.True(b.P75 <= b.P95 + 1e-9);
            }
        }

        [Fact]
        public void ConstantPositiveReturns_ConeCollapses_AndGrowsUp()
        {
            var r = FutureCone.Project(Rets(60, 0.002), 30, 800, 3);   // 매일 +0.2% 고정
            Assert.True(r.IsValid);
            // 분산 0 → 밴드 폭 거의 0 (P5≈P95)
            var last = r.Bands[^1];
            Assert.True(last.P95 - last.P5 < 1e-6);
            Assert.True(last.P50 > 0);                 // 상승 누적
            Assert.Equal(0, r.ProbLossPct, 6);         // 손실 경로 없음
        }

        [Fact]
        public void GoalHit_WithinBounds()
        {
            var r = FutureCone.Project(Wobble(150), 30, 1000, 9, goalPct: 3);
            Assert.InRange(r.GoalHitPct, 0, 100);
            Assert.InRange(r.ProbLossPct, 0, 100);
            Assert.True(r.MedianMaxDrawdownPct >= 0);
        }
    }
}
