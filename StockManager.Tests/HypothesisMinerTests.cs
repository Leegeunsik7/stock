// ============================================================================
// HypothesisMinerTests - 자동 가설 발굴 생성/게이트/과최적화방어 (순수·결정적)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class HypothesisMinerTests
    {
        // 평균회귀 진동(드리프트 0) — 저점(과매도/낙폭) 뒤 반등이 양 구간 모두 일관
        private static List<Candle> Wave(int n, double amp = 25, double period = 16)
        {
            var list = new List<Candle>(n);
            for (int i = 0; i < n; i++)
            {
                double p = 100 + amp * Math.Sin(2 * Math.PI * i / period);
                decimal close = (decimal)Math.Max(1.0, p);
                list.Add(new Candle
                {
                    Date = new DateTime(2018, 1, 1).AddDays(i),
                    Open = close, High = close * 1.01m, Low = close * 0.99m,
                    Close = close, Volume = 1000 + i % 5
                });
            }
            return list;
        }

        [Fact]
        public void GenerateCandidates_NonEmpty_AllHaveConditions()
        {
            var c = HypothesisMiner.GenerateCandidates();
            Assert.True(c.Count >= 10);
            Assert.All(c, r => Assert.NotEmpty(r.Conditions));
        }

        [Fact]
        public void Mine_TooFewCandles_Invalid()
        {
            var r = HypothesisMiner.Mine(Wave(80), 10);
            Assert.False(r.IsValid);
        }

        [Fact]
        public void Mine_StructuralInvariants()
        {
            var r = HypothesisMiner.Mine(Wave(600), 5);
            Assert.True(r.IsValid);
            Assert.True(r.Tested >= 10);
            Assert.True(r.Survivors <= r.Tested);
            Assert.True(r.SignificanceBar > 0);
            // 생존자는 모두 게이트 충족
            Assert.All(r.Pool, h =>
            {
                Assert.True(h.OosEdgePct >= r.SignificanceBar - 1e-9);
                Assert.True(h.OosWinRatePct >= 50);
                Assert.True(h.TrainEdgePct > 0);
                Assert.True(h.OosN >= 8);
            });
            // 풀은 OOS 엣지 내림차순
            for (int i = 1; i < r.Pool.Count; i++) Assert.True(r.Pool[i - 1].OosEdgePct >= r.Pool[i].OosEdgePct);
        }

        [Fact]
        public void Mine_MeanRevertingWave_FindsAtLeastOneSurvivor()
        {
            // 강한 평균회귀 → 과매도/낙폭 패턴이 양 구간 모두 양의 엣지 → 최소 1개 생존
            var r = HypothesisMiner.Mine(Wave(700, amp: 28, period: 18), 5, baseEdgeBar: 0.3);
            Assert.True(r.Survivors >= 1, $"생존 {r.Survivors} (기대 ≥1)");
            Assert.True(r.Pool.First().OosEdgePct > 0);
        }

        [Fact]
        public void Mine_ImpossibleBar_ZeroSurvivors_NoDataMining()
        {
            // 효과크기 바를 비현실적으로 높이면 아무도 생존 못 함 (과최적화 방어 동작)
            var r = HypothesisMiner.Mine(Wave(600), 5, baseEdgeBar: 999);
            Assert.True(r.IsValid);
            Assert.Equal(0, r.Survivors);
            Assert.Contains("생존 0", r.Note);
        }

        [Fact]
        public void Mine_Deterministic_SameInput_SamePool()
        {
            var c = Wave(600);
            var a = HypothesisMiner.Mine(c, 10);
            var b = HypothesisMiner.Mine(c, 10);
            Assert.Equal(a.Survivors, b.Survivors);
            Assert.Equal(a.Pool.Select(h => h.Label), b.Pool.Select(h => h.Label));
        }
    }
}
