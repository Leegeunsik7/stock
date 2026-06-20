// ============================================================================
// MultiAccountMeshTests — S20 #3(Path B): 가상 서브계좌 할당 검증 (15개)
//
//  §1 구성 (4)         — 빈/중복/음수·0가중 거부, 유효 구성
//  §2 Apportion (6)    — 합 불변식·균등·최대잉여·0수량·단일·가중분배+타이브레이크
//  §3 PartitionCash(3) — 합 == realCash·비례·잔여 흡수
//  §4 침범 가드 (2)    — WithinShare·MaxShareValue
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MultiAccountMeshTests
    {
        private static MultiAccountMesh Mesh(params (string id, decimal w)[] subs)
            => new MultiAccountMesh(subs.Select(s => new SubAccountSpec(s.id, s.w)).ToList());

        // ════════════════════════ §1 구성 ════════════════════════════════════════

        [Fact]
        public void Ctor_NullOrEmpty_Throws()
        {
            Assert.Throws<ArgumentException>(() => new MultiAccountMesh(null!));
            Assert.Throws<ArgumentException>(() => new MultiAccountMesh(new List<SubAccountSpec>()));
        }

        [Fact]
        public void Ctor_DuplicateId_Throws()
            => Assert.Throws<ArgumentException>(() => Mesh(("a", 1m), ("a", 1m)));

        [Fact]
        public void Ctor_NegativeWeight_OrZeroTotal_Throws()
        {
            Assert.Throws<ArgumentException>(() => Mesh(("a", -1m), ("b", 2m)));
            Assert.Throws<ArgumentException>(() => Mesh(("a", 0m), ("b", 0m)));   // 합 0
        }

        [Fact]
        public void Ctor_Valid_ExposesShares()
        {
            var m = Mesh(("alpha", 3m), ("beta", 1m));
            Assert.Equal(2, m.Count);
            Assert.Equal(4m, m.TotalWeight);
            Assert.Equal(0.75m, m.ShareOf("alpha"));
            Assert.Equal(0.25m, m.ShareOf("beta"));
            Assert.Equal(new[] { "alpha", "beta" }, m.Ids);
        }

        // ════════════════════════ §2 Apportion ════════════════════════════════════

        [Fact]
        public void Apportion_SumAlwaysEqualsTotalQty()
        {
            var m = Mesh(("a", 2m), ("b", 1m), ("c", 1m));
            foreach (long q in new long[] { 0, 1, 2, 3, 7, 10, 99, 1000, 999_983 })
            {
                var split = m.Apportion(q);
                Assert.Equal(q, split.Values.Sum());   // 무손실 — 주식 생성·소실 0
            }
        }

        [Fact]
        public void Apportion_EvenSplit()
        {
            var s = Mesh(("a", 1m), ("b", 1m)).Apportion(100);
            Assert.Equal(50, s["a"]);
            Assert.Equal(50, s["b"]);
        }

        [Fact]
        public void Apportion_LargestRemainder_DistributesLeftover()
        {
            // 10 / (1:1:1) = 3.33 each → floors 3,3,3 (=9), leftover 1 → 잔여 큰 첫 계좌
            var s = Mesh(("a", 1m), ("b", 1m), ("c", 1m)).Apportion(10);
            Assert.Equal(4, s["a"]);
            Assert.Equal(3, s["b"]);
            Assert.Equal(3, s["c"]);
        }

        [Fact]
        public void Apportion_ZeroQty_AllZero()
        {
            var s = Mesh(("a", 1m), ("b", 1m)).Apportion(0);
            Assert.Equal(0, s["a"]);
            Assert.Equal(0, s["b"]);
        }

        [Fact]
        public void Apportion_SingleSub_GetsAll()
        {
            var s = Mesh(("solo", 1m)).Apportion(777);
            Assert.Equal(777, s["solo"]);
        }

        [Fact]
        public void Apportion_WeightedSplit_AndDeterministicTieBreak()
        {
            // 5 / (2:1:1) = 2.5, 1.25, 1.25 → floors 2,1,1(=4), leftover 1 → 잔여 최대(a, 0.5)
            var s = Mesh(("a", 2m), ("b", 1m), ("c", 1m)).Apportion(5);
            Assert.Equal(3, s["a"]);
            Assert.Equal(1, s["b"]);
            Assert.Equal(1, s["c"]);

            // 동률 잔여(0.333) → 인덱스순 결정론: a 가 잉여 획득
            var t = Mesh(("a", 1m), ("b", 1m), ("c", 1m)).Apportion(7);
            Assert.Equal(3, t["a"]);
            Assert.Equal(2, t["b"]);
            Assert.Equal(2, t["c"]);
        }

        // ════════════════════════ §3 PartitionCash ════════════════════════════════

        [Fact]
        public void PartitionCash_SumEqualsRealCash_Exact()
        {
            var m = Mesh(("a", 1m), ("b", 1m), ("c", 1m));
            foreach (decimal cash in new[] { 100m, 1_000_000m, 333.33m, 1m })
                Assert.Equal(cash, m.PartitionCash(cash).Sum(x => x.Cash));
        }

        [Fact]
        public void PartitionCash_Proportional()
        {
            var parts = Mesh(("a", 6m), ("b", 4m)).PartitionCash(1_000_000m);
            Assert.Equal(600_000m, parts.First(p => p.Id == "a").Cash);
            Assert.Equal(400_000m, parts.First(p => p.Id == "b").Cash);
        }

        [Fact]
        public void PartitionCash_LastAbsorbsResidual()
        {
            // 100 / (1:1:1): 앞 둘은 100/3, 마지막이 잔여 흡수 → 합 정확히 100
            var parts = Mesh(("a", 1m), ("b", 1m), ("c", 1m)).PartitionCash(100m);
            Assert.Equal(100m, parts.Sum(p => p.Cash));
            Assert.Equal(100m / 3m, parts[0].Cash);
            Assert.Equal(100m / 3m, parts[1].Cash);
        }

        // ════════════════════════ §4 침범 가드 ════════════════════════════════════

        [Fact]
        public void MaxShareValue_IsRealTotalTimesShare()
        {
            var m = Mesh(("a", 3m), ("b", 1m));
            Assert.Equal(750_000m, m.MaxShareValue("a", 1_000_000m));   // 75%
            Assert.Equal(250_000m, m.MaxShareValue("b", 1_000_000m));   // 25%
        }

        [Fact]
        public void WithinShare_GuardsOverlap()
        {
            var m = Mesh(("a", 1m), ("b", 1m));   // 각 50%
            // 실총 1,000,000 → a 한도 500,000
            Assert.True(m.WithinShare("a", 500_000m, 1_000_000m));    // 한도 내
            Assert.True(m.WithinShare("a", 400_000m, 1_000_000m));
            Assert.False(m.WithinShare("a", 500_001m, 1_000_000m));   // 침범 — 자기 지분 초과
        }
    }
}
