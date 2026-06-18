// ============================================================================
// CartridgeGenomeTests — 전략 유전자 진화소 순수 코어 검증 (결정론·오프라인·네트워크 0)
//  진화 결정론·족보(Lineage) 포맷·스펙 유효성·중복 제거·단위(손절pp) 회귀 가드.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class CartridgeGenomeTests
    {
        // 결정론 합성 캔들(난수 시드 없이 사인파+드리프트) — StrategyCompilerTests와 동일 방식
        private static List<Candle> Synth(int n = 260)
        {
            var list = new List<Candle>(n);
            var d = new DateTime(2022, 1, 1);
            for (int i = 0; i < n; i++)
            {
                double p = 100 + i * 0.15 + 12 * Math.Sin(i / 9.0) + 5 * Math.Sin(i / 3.0);
                decimal c = (decimal)Math.Round(p, 2);
                list.Add(new Candle { Date = d.AddDays(i), Open = c, High = c * 1.01m, Low = c * 0.99m, Close = c, Volume = 1000 + i });
            }
            return list;
        }

        // 다양성 있는 MA 크로스 씨앗 풀
        private static List<StrategySpec> Seeds() => new()
        {
            StrategyCompiler.FromMaCross(5, 20),
            StrategyCompiler.FromMaCross(10, 60),
            StrategyCompiler.FromMaCross(3, 30),
        };

        [Fact]
        public void Evolve_EmptySeeds_Invalid()
        {
            var r = CartridgeGenome.Evolve(new List<StrategySpec>(), Synth(), Market.KRX_KOSPI);
            Assert.False(r.IsValid);
            Assert.Empty(r.Ranked);
        }

        [Fact]
        public void Evolve_TooFewCandles_Invalid()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(40), Market.KRX_KOSPI);
            Assert.False(r.IsValid);
            Assert.Empty(r.Ranked);
        }

        [Fact]
        public void Evolve_Deterministic_SameRankingAndLineage()
        {
            var a = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 3, 12);
            var b = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 3, 12);
            Assert.True(a.IsValid);
            Assert.True(b.IsValid);
            Assert.Equal(a.Ranked.Count, b.Ranked.Count);
            for (int i = 0; i < a.Ranked.Count; i++)
            {
                Assert.Equal(a.Ranked[i].Spec.Id, b.Ranked[i].Spec.Id);
                Assert.Equal(a.Ranked[i].Lineage, b.Ranked[i].Lineage);     // 족보 결정론
                Assert.Equal(a.Ranked[i].Origin,  b.Ranked[i].Origin);
                Assert.Equal(a.Ranked[i].Sharpe,  b.Ranked[i].Sharpe);      // 적합도 결정론(비트 동일)
            }
        }

        [Fact]
        public void Evolve_AllRankedSpecs_AreValid()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 3, 12);
            Assert.NotEmpty(r.Ranked);
            Assert.All(r.Ranked, e => Assert.True(StrategyCompiler.Validate(e.Spec).ok, $"무효 스펙: {e.Lineage}"));
        }

        [Fact]
        public void Evolve_RankedBy_SharpeDescending()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 3, 12);
            for (int i = 1; i < r.Ranked.Count; i++)
                Assert.True(r.Ranked[i - 1].Sharpe >= r.Ranked[i].Sharpe);
        }

        [Fact]
        public void Evolve_NoDuplicateSpecIds_InLeaderboard()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 4, 12);
            var ids = r.Ranked.Select(e => e.Spec.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
        }

        [Fact]
        public void Evolve_Lineage_NonEmpty_AndMatchesOriginCategory()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 3, 12);
            Assert.True(r.IsValid);
            foreach (var e in r.Ranked)
            {
                Assert.False(string.IsNullOrWhiteSpace(e.Lineage));
                switch (e.Origin)
                {
                    case "씨앗":      Assert.StartsWith("씨앗", e.Lineage); break;
                    case "변이":      Assert.StartsWith("변이", e.Lineage); break;
                    case "교차":      Assert.StartsWith("교차", e.Lineage); break;
                    case "교차+변이":
                        Assert.StartsWith("교차", e.Lineage);
                        Assert.Contains("변이(", e.Lineage);
                        break;
                    default: Assert.Fail($"알 수 없는 출처 카테고리: {e.Origin}"); break;
                }
            }
        }

        [Fact]
        public void Evolve_CrossoverLineage_ReferencesParentIdFragments()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 4, 12);
            var crosses = r.Ranked.Where(e => e.Origin.StartsWith("교차")).ToList();
            Assert.NotEmpty(crosses);   // 교차 산물이 리더보드에 존재해야 함(집단>생존자)
            // "교차(진입:xxxxxx + 청산:yyyyyy)" — 6자리 16진수 부모 조각
            var rx = new Regex(@"교차\(진입:[0-9a-f]{1,6} \+ 청산:[0-9a-f]{1,6}\)");
            foreach (var e in crosses)
                Assert.Matches(rx, e.Lineage);
        }

        [Fact]
        public void Evolve_SeedsPresent_HaveSeedLineage()
        {
            // 동일 씨앗 1개만으로도 씨앗 족보가 형성되는지(부족분은 변이로 채움)
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 1, 12);
            Assert.True(r.IsValid);
            // 1세대에서는 초기 씨앗들이 대부분 생존 → 씨앗 족보가 최소 1개 존재
            Assert.Contains(r.Ranked, e => e.Origin == "씨앗" && e.Lineage.StartsWith("씨앗("));
        }

        [Fact]
        public void Evolve_SingleSeed_StillProducesPopulation()
        {
            var r = CartridgeGenome.Evolve(
                new List<StrategySpec> { StrategyCompiler.FromMaCross(5, 20) },
                Synth(), Market.KRX_KOSPI, 2, 8);
            Assert.True(r.IsValid);
            Assert.NotEmpty(r.Ranked);
        }

        // ===== 적대적 리뷰 회귀 가드 =====

        [Fact]
        public void Evolve_NoFractionalSubOnePctStops_UnitsRegression()
        {
            // 과거 결함: 손절 변이가 분수(±0.04)와 [0.01,0.30] 클램프를 써서 손절이 0.0x%로 뭉개짐.
            // 수정 후: 손절·익절은 퍼센트포인트(정수 격자). 0 < pct < 1 인 병리값이 절대 없어야 함.
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 4, 16);
            Assert.True(r.IsValid);
            Assert.DoesNotContain(r.Ranked, e => e.Spec.StopLossPct   > 0 && e.Spec.StopLossPct   < 1);
            Assert.DoesNotContain(r.Ranked, e => e.Spec.TakeProfitPct > 0 && e.Spec.TakeProfitPct < 1);
        }

        [Fact]
        public void Evolve_FractionalSeedStop_QuantizedInEvolution()
        {
            // 분수 손절·익절 씨앗(0<pct<1) 투입 시 진화 산출은 정수 격자여야 함
            // (InitPopulation SnapPct 입구 스냅 + Mutate Math.Round 이중 방어)
            var fracBase = StrategyCompiler.FromMaCross(5, 20);
            fracBase = fracBase with { Id = "", StopLossPct = 0.5, TakeProfitPct = 0.7 };
            fracBase = fracBase with { Id = StrategyCompiler.Id(fracBase) };
            var seeds = new List<StrategySpec> { fracBase, StrategyCompiler.FromMaCross(10, 60) };
            var r = CartridgeGenome.Evolve(seeds, Synth(), Market.KRX_KOSPI, 4, 12);
            Assert.True(r.IsValid);
            Assert.DoesNotContain(r.Ranked, e => e.Spec.StopLossPct   > 0 && e.Spec.StopLossPct   < 1);
            Assert.DoesNotContain(r.Ranked, e => e.Spec.TakeProfitPct > 0 && e.Spec.TakeProfitPct < 1);
        }

        [Fact]
        public void Evolve_GenerationsClamped_AndCounted()
        {
            var r = CartridgeGenome.Evolve(Seeds(), Synth(), Market.KRX_KOSPI, 99, 12);  // 상한 8로 클램프
            Assert.True(r.IsValid);
            Assert.Equal(8, r.Generations);
            Assert.True(r.TotalEvaluated > 0);
        }
    }
}
