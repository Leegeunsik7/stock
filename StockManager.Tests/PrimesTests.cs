// ============================================================================
// PrimesTests - Primes 모듈 회귀 테스트 (가이드 문서 §4.1 경계값 표 + 신규 API)
//  · P0: 경계값(0,1,음수,limit<2,…)을 케이스로 고정
//  · 대형 known-prime/known-composite 회귀 (2^31-1, 1e9+7, M89, M67 …)
//  · v2.0 신규: NextPrime(null 시그널)/NthPrime/CountPrimes/IsCoprime/Totient/청크 구간체
// ============================================================================
using System.Numerics;
using Xunit;

namespace StockManager.Tests
{
    public class IsPrimeTests
    {
        [Theory]
        // ----- 경계값: 2 미만은 전부 합성도 소수도 아님 → false -----
        [InlineData(-7, false)]
        [InlineData(-1, false)]
        [InlineData(0, false)]
        [InlineData(1, false)]
        // ----- 작은 수 -----
        [InlineData(2, true)]
        [InlineData(3, true)]
        [InlineData(4, false)]
        [InlineData(5, true)]
        [InlineData(6, false)]
        [InlineData(9, false)]
        [InlineData(25, false)]
        [InlineData(97, true)]
        // ----- 유사소수(pseudoprime) 함정 -----
        [InlineData(341, false)]    // 2-유사소수 (341 = 11×31)
        [InlineData(561, false)]    // 카마이클 수 (3×11×17)
        [InlineData(1_373_653, false)] // 강한 유사소수 경계 (829×1657)
        // ----- 대형 known 값 -----
        [InlineData(2_147_483_647, true)]              // 2^31-1 (메르센 소수 M31)
        [InlineData(1_000_000_007, true)]
        [InlineData(1_000_000_005, false)]             // 5의 배수
        [InlineData(1_000_000_016_000_000_063, false)] // (1e9+7)×(1e9+9)
        [InlineData(9_223_372_036_854_775_783, true)]  // long 범위 최대 소수
        [InlineData(long.MaxValue, false)]             // 2^63-1 = 7×73×127×337×…
        public void IsPrime_Long(long n, bool expected) => Assert.Equal(expected, Primes.IsPrime(n));

        [Fact]
        public void IsPrime_BigInteger_MersennePrime_M89()
        {
            var m89 = BigInteger.Pow(2, 89) - 1;   // 618,970,019,642,690,137,449,562,111 — 소수
            Assert.True(Primes.IsPrime(m89));
        }

        [Fact]
        public void IsPrime_BigInteger_MersenneComposite_M67()
        {
            var m67 = BigInteger.Pow(2, 67) - 1;   // 콜(Cole)의 유명한 합성수: 193707721 × 761838257287
            Assert.False(Primes.IsPrime(m67));
        }

        [Fact]
        public void IsPrime_BigInteger_DelegatesToLongRange()
        {
            Assert.True(Primes.IsPrime((BigInteger)1_000_000_007));
            Assert.False(Primes.IsPrime((BigInteger)(-7)));
            Assert.False(Primes.IsPrime(BigInteger.One));
        }
    }

    public class SieveTests
    {
        [Theory]
        [InlineData(-10)]
        [InlineData(0)]
        [InlineData(1)]
        public void Sieve_BelowTwo_ReturnsEmpty(int limit) => Assert.Empty(Primes.Sieve(limit));

        [Fact]
        public void Sieve_Two_ReturnsOnlyTwo() => Assert.Equal(new[] { 2 }, Primes.Sieve(2));

        [Fact]
        public void Sieve_Thirty_KnownPrimes() =>
            Assert.Equal(new[] { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29 }, Primes.Sieve(30));

        [Fact]
        public void Sieve_Hundred_Has25Primes() => Assert.Equal(25, Primes.Sieve(100).Count);

        [Fact]
        public void Sieve_OverLimit_Throws() =>
            Assert.Throws<System.ArgumentOutOfRangeException>(() => Primes.Sieve(int.MaxValue));

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(100, 25)]
        [InlineData(1_000_000, 78_498)]   // π(10^6) — 표준 검증값
        public void CountPrimes_KnownValues(int limit, int expected) =>
            Assert.Equal(expected, Primes.CountPrimes(limit));
    }

    public class SegmentedSieveTests
    {
        [Fact]
        public void Range_100_120() =>
            Assert.Equal(new long[] { 101, 103, 107, 109, 113 }, Primes.SegmentedSieve(100, 120));

        [Fact]
        public void HighBelowTwo_ReturnsEmpty() => Assert.Empty(Primes.SegmentedSieve(-5, 1));

        [Fact]
        public void LowGreaterThanHigh_ReturnsEmpty() => Assert.Empty(Primes.SegmentedSieve(50, 10));

        [Fact]
        public void LowBelowTwo_ClampsToTwo() =>
            Assert.Equal(new long[] { 2, 3, 5, 7 }, Primes.SegmentedSieve(-100, 10));

        [Fact]
        public void BillionRange_KnownTwinPrimes()
        {
            var primes = Primes.SegmentedSieve(1_000_000_000, 1_000_000_010);
            Assert.Equal(new long[] { 1_000_000_007, 1_000_000_009 }, primes);
        }

        [Fact]
        public void CrossValidate_AgainstSieve()
        {
            // 청크 경계(1<<20=1,048,576)를 가로지르는 구간을 일반 체와 교차 검증
            var expected = new System.Collections.Generic.List<long>();
            foreach (var p in Primes.Sieve(1_050_000))
                if (p >= 1_040_000) expected.Add(p);
            Assert.Equal(expected, Primes.SegmentedSieve(1_040_000, 1_050_000));
        }
    }

    public class NextPrimeTests
    {
        [Theory]
        [InlineData(-10, 2)]
        [InlineData(0, 2)]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(3, 5)]
        [InlineData(8, 11)]
        [InlineData(13, 17)]
        [InlineData(7919, 7927)]
        public void NextPrime_Known(long n, long expected) => Assert.Equal(expected, Primes.NextPrime(n));

        [Fact]
        public void NextPrime_BeforeMaxLongPrime() =>
            Assert.Equal(Primes.MaxLongPrime, Primes.NextPrime(Primes.MaxLongPrime - 1));

        [Fact]
        public void NextPrime_AtOrBeyondMaxLongPrime_ReturnsNull()
        {
            Assert.Null(Primes.NextPrime(Primes.MaxLongPrime));   // 오버플로우 시그널 (P1)
            Assert.Null(Primes.NextPrime(long.MaxValue));
        }
    }

    public class FactorizeTests
    {
        [Theory]
        [InlineData(-4)]
        [InlineData(0)]
        [InlineData(1)]
        public void BelowTwo_ReturnsEmpty(long n) => Assert.Empty(Primes.Factorize(n));

        [Fact]
        public void Factorize_360() =>
            Assert.Equal(new long[] { 2, 2, 2, 3, 3, 5 }, Primes.Factorize(360));

        [Fact]
        public void Factorize_Prime_ReturnsItself() =>
            Assert.Equal(new long[] { 13 }, Primes.Factorize(13));

        [Fact]
        public void Factorize_ProjectEuler3() =>
            Assert.Equal(new long[] { 71, 839, 1471, 6857 }, Primes.Factorize(600_851_475_143));

        [Fact]
        public void Factorize_TwoTimesMersenne61_EarlyExit()
        {
            // 잔여값 MR 조기 판정 확인 — 시산제만이면 √(4.6e18)≈2.1e9 회 순회로 수십 초 걸림
            const long m61 = 2_305_843_009_213_693_951;   // 2^61-1, 메르센 소수
            Assert.Equal(new long[] { 2, m61 }, Primes.Factorize(2 * m61));
        }

        // ===== v2.1: Pollard's rho — 두 거대 소수 곱(시산제 최악 케이스) =====
        [Fact]
        public void Factorize_Rho_MidSemiprime() =>
            Assert.Equal(new long[] { 1_000_003, 1_000_033 },
                Primes.Factorize(1_000_003L * 1_000_033));   // 1,000,036,000,099

        [Fact]
        public void Factorize_Rho_HugeSemiprime()
        {
            // (2^31-1) × 2147483629 ≈ 4.6×10^18 — 시산제면 √n ≈ 2.1×10^9 회 순회
            const long p1 = 2_147_483_629, p2 = 2_147_483_647;
            Assert.Equal(new long[] { p1, p2 }, Primes.Factorize(p1 * p2));
        }

        [Fact]
        public void Factorize_Rho_PrimeSquare() =>
            Assert.Equal(new long[] { 1_000_003, 1_000_003 },
                Primes.Factorize(1_000_003L * 1_000_003));

        [Fact]
        public void Factorize_Rho_MixedFactors_SortedAscending()
        {
            const long n = 8L * 1_000_003 * 2_147_483_647;   // ≈ 1.7×10^16
            Assert.Equal(new long[] { 2, 2, 2, 1_000_003, 2_147_483_647 }, Primes.Factorize(n));
        }

        [Fact]
        public void Factorize_Carmichael561() =>
            Assert.Equal(new long[] { 3, 11, 17 }, Primes.Factorize(561));
    }

    public class SegmentedSieveStreamTests
    {
        [Fact]
        public void Stream_EqualsListVersion()
        {
            var list = Primes.SegmentedSieve(100, 120);
            var streamed = new System.Collections.Generic.List<long>(Primes.SegmentedSieveStream(100, 120));
            Assert.Equal(list, streamed);
        }

        [Fact]
        public void Stream_CrossChunkBoundary_EqualsListVersion()
        {
            // 1<<20 청크 경계를 가로지르는 구간
            var list = Primes.SegmentedSieve(1_048_000, 1_049_000);
            var streamed = new System.Collections.Generic.List<long>(Primes.SegmentedSieveStream(1_048_000, 1_049_000));
            Assert.Equal(list, streamed);
        }

        [Fact]
        public void Stream_EmptyCases()
        {
            Assert.Empty(Primes.SegmentedSieveStream(-5, 1));
            Assert.Empty(Primes.SegmentedSieveStream(50, 10));
        }

        [Fact]
        public void Stream_Validation_IsEager()
        {
            // 이터레이터 지연이 아니라 '호출 즉시' 검증되는지 — 열거 없이 예외 발생해야 함
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => Primes.SegmentedSieveStream(2, long.MaxValue));
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => Primes.SegmentedSieve(2, long.MaxValue));
        }
    }

    public class CountPrimesLongTests
    {
        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 0)]
        [InlineData(2, 1)]
        [InlineData(3, 2)]
        [InlineData(10, 4)]
        [InlineData(100, 25)]
        [InlineData(1_000_000, 78_498)]              // π(10^6)
        [InlineData(1_000_000_000, 50_847_534)]      // π(10^9) — 체 한계 안 (교차 검증용)
        [InlineData(10_000_000_000, 455_052_511)]    // π(10^10) — 체 한계 '밖', Lucy 전용
        public void Lucy_KnownValues(long limit, long expected) =>
            Assert.Equal(expected, Primes.CountPrimesLong(limit));

        [Fact]
        public void Lucy_MatchesSieveCount() =>
            Assert.Equal(Primes.CountPrimes(1_000_000), Primes.CountPrimesLong(1_000_000));

        [Fact]
        public void Lucy_OverLimit_Throws() =>
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => Primes.CountPrimesLong(Primes.MaxPiLimit + 1));
    }

    public class GenerateTests
    {
        [Fact]
        public void First10()
        {
            var got = new System.Collections.Generic.List<long>(Primes.Generate(10));
            Assert.Equal(new long[] { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29 }, got);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void NonPositiveCount_Empty(int count) => Assert.Empty(Primes.Generate(count));

        [Fact]
        public void Thousandth_Is7919()
        {
            long last = 0;
            foreach (var p in Primes.Generate(1000)) last = p;
            Assert.Equal(7919, last);
        }
    }

    public class NumberTheoryTests
    {
        [Theory]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(5, 11)]
        [InlineData(6, 13)]
        [InlineData(25, 97)]
        [InlineData(1000, 7919)]
        public void NthPrime_Known(int n, long expected) => Assert.Equal(expected, Primes.NthPrime(n));

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void NthPrime_BelowOne_Throws(int n) =>
            Assert.Throws<System.ArgumentOutOfRangeException>(() => Primes.NthPrime(n));

        [Theory]
        [InlineData(8, 15, true)]
        [InlineData(12, 18, false)]
        [InlineData(1, 0, true)]      // gcd(1,0)=1
        [InlineData(0, 0, false)]     // gcd(0,0)=0
        [InlineData(-4, 9, true)]     // 부호 무시
        [InlineData(long.MinValue, 3, true)]   // |MinValue|=2^63, gcd(2^63,3)=1 — AbsU 오버플로우 안전
        [InlineData(long.MinValue, 2, false)]
        public void IsCoprime_Known(long a, long b, bool expected) =>
            Assert.Equal(expected, Primes.IsCoprime(a, b));

        [Theory]
        [InlineData(-5, 0)]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(9, 6)]
        [InlineData(10, 4)]
        [InlineData(13, 12)]    // 소수 → n-1
        [InlineData(36, 12)]
        [InlineData(1_000_000_007, 1_000_000_006)]   // 큰 소수 — MR 조기 경로
        public void Totient_Known(long n, long expected) => Assert.Equal(expected, Primes.Totient(n));
    }
}
