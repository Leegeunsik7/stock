// ============================================================================
// Primes - 소수(素數) 판별 · 생성 · 처리 유틸리티 (v2.1)
//
//  v2.1 (가이드 문서 P4 백로그 반영)
//   · Factorize        : Pollard's rho(Brent 변형) — 두 거대 소수 곱도 O(n^¼)에 분해
//   · SegmentedSieveStream : IEnumerable<long> 스트리밍 — 결과 List 메모리 부담 제거
//   · CountPrimesLong  : Lucy_Hedgehog O(n^¾) — 체 한계(10⁹) 밖 π(n), 10¹³까지
//
//  v2.0 (가이드 문서 P0~P3 로드맵 반영)
//   · IsPrime(long)     : 결정적 Miller–Rabin (증인 2..37 → long 전 범위 확정 판별)
//   · IsPrime(BigInteger): 임의 정밀도 — 3.317×10^24 미만 결정적, 이상은 확률적(오판 < 4^-12)
//   · Sieve             : 홀수 전용 비트 체(메모리 1/16) + 상한 가드(MaxSieveLimit)
//   · SegmentedSieve    : 청크 분할로 구간 폭 int 한계 제거 · FloorSqrt로 √high 안전 계산
//   · NextPrime         : long? 반환 — long 범위에 다음 소수가 없으면 null (오버플로우 시그널)
//   · Factorize         : 잔여값 소수 조기 판정(MR)으로 큰 소인수에서 조기 종료
//   · Generate          : 2·3·5 휠(mod 30)로 후보 절감한 지연 스트림
//   · NthPrime / CountPrimes / IsCoprime / Totient : 정수론 처리 유틸
//
//  설계 원칙 (v1.0과 동일)
//   1) 외부 의존성 0 · 인스턴스 상태 0(전부 static · 스레드 안전).
//   2) 오버플로우 안전 — 곱셈은 UInt128, 제곱 비교는 나눗셈(i <= n/i)으로 수행.
//   3) 경계값(0,1,음수,limit<2 등)은 예외 대신 '의미 있는 빈 결과'.
//      단, 자원 폭발성 입력(초대형 limit)은 ArgumentOutOfRangeException으로 명시 거부.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Numerics;

namespace MS2E.Infrastructure
{
    /// <summary>소수 관련 순수 함수 모음. 상태가 없어 어디서든 안전하게 호출 가능.</summary>
    public static class Primes
    {
        /// <summary>Sieve/CountPrimes/NthPrime의 limit 상한. 홀수 전용 비트 체 기준 약 62.5MB.
        /// 이보다 큰 범위는 SegmentedSieve로 구간 처리할 것.</summary>
        public const int MaxSieveLimit = 1_000_000_000;

        /// <summary>long 범위에서 가장 큰 소수. NextPrime이 null을 반환하는 기준.</summary>
        public const long MaxLongPrime = 9_223_372_036_854_775_783L;

        /// <summary>CountPrimesLong(Lucy)의 limit 상한. O(n^¾) 시간 · O(√n) 메모리 —
        /// 10¹⁰ 수백 ms, 10¹² 수십 초 수준. 그 이상은 Meissel–Lehmer 영역(백로그).</summary>
        public const long MaxPiLimit = 10_000_000_000_000L;   // 10^13

        // Miller–Rabin 결정적 증인 집합 — n < 3.317×10^24 에서 확정 판별이 증명됨
        private static readonly ulong[] MrWitnesses = { 2, 3, 5, 7, 11, 13, 17, 19, 23, 29, 31, 37 };

        // ====================================================================
        // 1. 단일 판별 — 결정적 Miller–Rabin
        // ====================================================================
        /// <summary>
        /// n이 소수면 true. 2 미만(0,1,음수)은 모두 false.
        /// 증인 {2..37} Miller–Rabin — long 전 범위에서 '확률적'이 아닌 '확정' 판별.
        /// 시간 O(k·log³n) (k=12) — 시산제 O(√n) 대비 거대 수에서 압도적으로 빠름.
        /// </summary>
        public static bool IsPrime(long n) => n >= 2 && IsPrimeU((ulong)n);

        /// <summary>
        /// 임의 정밀도 판별. n &lt; 3.317×10^24 에서는 결정적,
        /// 그 이상에서는 확률적(합성수 오판 확률 &lt; 4^-12 ≈ 6×10^-8).
        /// </summary>
        public static bool IsPrime(BigInteger n)
        {
            if (n < 2) return false;
            if (n <= long.MaxValue) return IsPrime((long)n);

            foreach (var sp in MrWitnesses)                 // n > long.MaxValue ≫ 37 이므로 등호 불필요
                if ((n % sp).IsZero) return false;

            BigInteger d = n - 1, nm1 = n - 1;
            int r = 0;
            while (d.IsEven) { d >>= 1; r++; }

            foreach (var a in MrWitnesses)
            {
                var x = BigInteger.ModPow(a, d, n);
                if (x.IsOne || x == nm1) continue;
                bool passed = false;
                for (int i = 1; i < r; i++)
                {
                    x = x * x % n;
                    if (x == nm1) { passed = true; break; }
                }
                if (!passed) return false;
            }
            return true;
        }

        private static bool IsPrimeU(ulong n)
        {
            foreach (var p in MrWitnesses)
            {
                if (n == p) return true;
                if (n % p == 0) return false;
            }
            // 여기 도달 시 n > 37, 홀수
            ulong d = n - 1;
            int r = 0;
            while ((d & 1) == 0) { d >>= 1; r++; }

            foreach (var a in MrWitnesses)
            {
                ulong x = PowMod(a, d, n);
                if (x == 1 || x == n - 1) continue;
                bool passed = false;
                for (int i = 1; i < r; i++)
                {
                    x = MulMod(x, x, n);
                    if (x == n - 1) { passed = true; break; }
                }
                if (!passed) return false;
            }
            return true;
        }

        // UInt128(.NET 8)로 64비트 모듈러 곱셈 오버플로우 원천 차단
        private static ulong MulMod(ulong a, ulong b, ulong m) => (ulong)((UInt128)a * b % m);

        private static ulong PowMod(ulong b, ulong e, ulong m)
        {
            ulong result = 1;
            b %= m;
            while (e > 0)
            {
                if ((e & 1) == 1) result = MulMod(result, b, m);
                b = MulMod(b, b, m);
                e >>= 1;
            }
            return result;
        }

        // ====================================================================
        // 2. 일괄 생성 — 홀수 전용 비트 체 (에라토스테네스)
        // ====================================================================
        /// <summary>
        /// limit 이하의 모든 소수를 오름차순으로 반환. limit &lt; 2 이면 빈 목록.
        /// 짝수를 제외한 비트마스크 체 — bool[] 대비 메모리 1/16 (limit=10^9 → 약 62.5MB).
        /// limit &gt; <see cref="MaxSieveLimit"/> 이면 ArgumentOutOfRangeException.
        /// </summary>
        public static IReadOnlyList<int> Sieve(int limit)
        {
            if (limit < 2) return Array.Empty<int>();
            GuardSieveLimit(limit, nameof(limit));

            var bits = CreateOddSieve(limit);

            // 1차 패스로 개수 확정 → 정확한 크기의 배열 1회 할당 (List 재할당 제거)
            int count = 1;                                   // 2
            for (long n = 3; n <= limit; n += 2)
                if (!GetBit(bits, n)) count++;

            var primes = new int[count];
            primes[0] = 2;
            int k = 1;
            for (long n = 3; n <= limit; n += 2)
                if (!GetBit(bits, n)) primes[k++] = (int)n;
            return primes;
        }

        /// <summary>limit 이하 소수의 개수 π(limit). 목록을 만들지 않아 Sieve보다 가볍다.</summary>
        public static int CountPrimes(int limit)
        {
            if (limit < 2) return 0;
            GuardSieveLimit(limit, nameof(limit));

            var bits = CreateOddSieve(limit);
            int count = 1;                                   // 2
            for (long n = 3; n <= limit; n += 2)
                if (!GetBit(bits, n)) count++;
            return count;
        }

        /// <summary>
        /// 체 한계(<see cref="MaxSieveLimit"/>) 밖까지 π(limit) — Lucy_Hedgehog 알고리즘.
        /// 핵심: S(v) = "2..v 중 소수이거나 최소 소인수 &gt; p 인 수의 개수"를
        /// ⌊limit/i⌋의 서로 다른 값들(≈2√n개)에 대해서만 유지하며, 소수 p를 만날 때마다
        /// S(v) -= S(v/p) − π(p−1) 로 'p를 최소 소인수로 갖는 합성수'를 일괄 차감한다.
        /// 시간 O(n^¾) · 메모리 O(√n). limit &lt; 2 → 0, limit &gt; <see cref="MaxPiLimit"/> → 예외.
        /// </summary>
        public static long CountPrimesLong(long limit)
        {
            if (limit < 2) return 0;
            if (limit > MaxPiLimit)
                throw new ArgumentOutOfRangeException(nameof(limit), limit,
                    $"limit은 {MaxPiLimit:N0} 이하여야 합니다 (O(n^¾) 시간 한계).");

            long r = FloorSqrt(limit);
            // small[i] = S(i) (i ≤ √n) · large[i] = S(limit/i) — ⌊limit/i⌋ 값 전체를 양분해 커버
            var small = new long[r + 1];
            var large = new long[r + 1];
            for (long i = 1; i <= r; i++)
            {
                small[i] = i - 1;
                large[i] = limit / i - 1;
            }

            for (long p = 2; p <= r; p++)
            {
                if (small[p] == small[p - 1]) continue;      // p가 합성수면 S 변화 없음
                long sp = small[p - 1];                      // π(p−1)
                long p2 = p * p;

                long maxI = Math.Min(r, limit / p2);         // v = limit/i ≥ p² 인 i만 갱신
                for (long i = 1; i <= maxI; i++)
                {
                    long d = i * p;
                    large[i] -= (d <= r ? large[d] : small[limit / d]) - sp;
                }
                for (long v = r; v >= p2; v--)
                    small[v] -= small[v / p] - sp;
            }
            return large[1];                                 // S(limit) = π(limit)
        }

        private static void GuardSieveLimit(long limit, string paramName)
        {
            if (limit > MaxSieveLimit)
                throw new ArgumentOutOfRangeException(paramName, limit,
                    $"limit은 {MaxSieveLimit:N0} 이하여야 합니다. 더 큰 범위는 SegmentedSieve(low, high)를 사용하세요.");
        }

        // 홀수 n(≥3) ↔ 비트 idx=(n-3)/2 매핑. set = 합성수.
        private static ulong[] CreateOddSieve(long limit)
        {
            long oddCount = limit < 3 ? 0 : (limit - 3) / 2 + 1;
            var bits = new ulong[(oddCount + 63) / 64];
            for (long i = 3; i * i <= limit; i += 2)
            {
                if (GetBit(bits, i)) continue;               // 이미 합성수면 건너뜀
                long step = 2 * i;                           // 홀수 배수만 (i², i²+2i, …)
                for (long j = i * i; j <= limit; j += step)
                    SetBit(bits, j);
            }
            return bits;
        }

        private static bool GetBit(ulong[] bits, long oddN)
        {
            long idx = (oddN - 3) >> 1;
            return ((bits[idx >> 6] >> (int)(idx & 63)) & 1UL) != 0;
        }

        private static void SetBit(ulong[] bits, long oddN)
        {
            long idx = (oddN - 3) >> 1;
            bits[idx >> 6] |= 1UL << (int)(idx & 63);
        }

        // ====================================================================
        // 3. 구간 생성 — 청크 분할 구간 체(Segmented Sieve)
        // ====================================================================
        private const int SegmentChunk = 1 << 20;            // 1,048,576 — 청크당 1MB bool

        /// <summary>
        /// [low, high] 구간 안의 소수를 오름차순 목록으로 반환. (스트리밍이 필요하면
        /// <see cref="SegmentedSieveStream"/> 사용 — 결과 List 메모리는 호출자 책임.)
        /// high &lt; 2 또는 low &gt; high 이면 빈 목록. low &lt; 2 는 2로 보정.
        /// 제약: √high ≤ <see cref="MaxSieveLimit"/> — 즉 high ≤ 10^18. 초과 시 ArgumentOutOfRangeException.
        /// </summary>
        public static IReadOnlyList<long> SegmentedSieve(long low, long high)
            => new List<long>(SegmentedSieveStream(low, high));

        /// <summary>
        /// [low, high] 구간의 소수를 청크 단위로 거르며 지연(lazy) 산출.
        /// 청크당 1MB만 사용하므로 폭 10¹⁰짜리 구간도 일정 메모리로 순회 가능.
        /// 인자 검증(high ≤ 10^18)은 호출 즉시 수행된다(이터레이터 지연 아님).
        /// </summary>
        public static IEnumerable<long> SegmentedSieveStream(long low, long high)
        {
            // ⚠️ 이터레이터는 예외도 지연되므로, 검증은 래퍼에서 '즉시' 수행
            long root = FloorSqrt(Math.Max(high, 0));
            if (root > MaxSieveLimit)
                throw new ArgumentOutOfRangeException(nameof(high), high,
                    $"high는 {(long)MaxSieveLimit * MaxSieveLimit:N0} (= MaxSieveLimit²) 이하여야 합니다.");
            return Stream();

            IEnumerable<long> Stream()
            {
                if (high < 2 || low > high) yield break;
                long from = Math.Max(low, 2);

                var basePrimes = Sieve((int)root);           // √high 이하의 '기반 소수'

                for (long segLow = from; segLow <= high; segLow += SegmentChunk)
                {
                    long segHigh = Math.Min(high, segLow + SegmentChunk - 1);
                    int width = (int)(segHigh - segLow + 1);
                    var composite = new bool[width];

                    foreach (int bp in basePrimes)
                    {
                        long p = bp;
                        // 청크 내 첫 마킹 지점 = max(p², segLow 이상의 첫 p의 배수)
                        long start = Math.Max(p * p, (segLow + p - 1) / p * p);
                        for (long j = start; j <= segHigh; j += p)
                            composite[j - segLow] = true;
                    }

                    for (int i = 0; i < width; i++)
                        if (!composite[i]) yield return segLow + i;
                }
            }
        }

        /// <summary>⌊√n⌋ — Math.Sqrt의 부동소수 오차를 정수 나눗셈으로 보정(오버플로우 없음).</summary>
        private static long FloorSqrt(long n)
        {
            if (n < 0) return 0;
            long r = (long)Math.Sqrt(n);
            while (r > 0 && r > n / r) r--;                  // r² > n 이면 내림
            while (r + 1 <= n / (r + 1)) r++;                // (r+1)² ≤ n 이면 올림
            return r;
        }

        // ====================================================================
        // 4. 처리 유틸
        // ====================================================================
        /// <summary>
        /// n 보다 큰 가장 작은 소수. n &lt; 2 이면 2.
        /// long 범위에 다음 소수가 없으면(n ≥ <see cref="MaxLongPrime"/>) null — 오버플로우 시그널.
        /// 짝수 후보는 건너뛰어 검사 횟수를 절반으로 줄인다.
        /// </summary>
        public static long? NextPrime(long n)
        {
            if (n < 2) return 2;
            if (n >= MaxLongPrime) return null;              // long 안에 더 큰 소수 없음

            long c = n + 1;
            if (c == 2) return 2;
            if ((c & 1) == 0) c++;                           // 짝수 스킵 (2는 위에서 처리됨)
            for (; ; c += 2)
                if (IsPrime(c)) return c;                    // n < MaxLongPrime 보장 → 반드시 도달
        }

        /// <summary>
        /// n의 소인수분해(중복 포함, 오름차순). 예: 360 → [2,2,2,3,3,5].
        /// n &lt; 2 이면 빈 목록.
        /// 전략: ① 1000 미만 소인수는 시산제로 제거 → ② 잔여 합성수는
        /// Pollard's rho(Brent 변형, O(n^¼))로 분할 정복 + Miller–Rabin으로 소수 확정.
        /// 두 거대 소수의 곱(예: (2³¹−1)×2147483629 ≈ 4.6×10¹⁸)도 밀리초에 분해된다.
        /// </summary>
        public static IReadOnlyList<long> Factorize(long n)
        {
            var factors = new List<long>();
            if (n < 2) return factors;

            while ((n & 1) == 0) { factors.Add(2); n >>= 1; }
            for (long i = 3; i <= 997 && i <= n / i; i += 2)
                while (n % i == 0) { factors.Add(i); n /= i; }

            if (n > 1) FactorRec(n, factors);                // 잔여값(소인수 전부 > 997)
            factors.Sort();                                  // rho는 비순차 — 오름차순 계약 유지
            return factors;
        }

        // 분할 정복: 소수면 수확, 합성수면 rho로 쪼개 재귀 (깊이 ≤ log₂ n ≈ 63)
        private static void FactorRec(long n, List<long> acc)
        {
            if (n == 1) return;
            if (IsPrime(n)) { acc.Add(n); return; }
            long d = PollardRho(n);                          // 1 < d < n 보장
            FactorRec(d, acc);
            FactorRec(n / d, acc);
        }

        /// <summary>홀수 합성수 n의 비자명 약수 하나를 반환 (Brent 변형 rho).
        /// Las Vegas 알고리즘 — 결과는 항상 정확하고, 실패 시 c를 바꿔 재시도.</summary>
        private static long PollardRho(long n)
        {
            if ((n & 1) == 0) return 2;
            ulong un = (ulong)n;
            while (true)
            {
                // Random.Shared: .NET 6+ 스레드 안전 — 모듈의 '상태 없음' 원칙 유지
                ulong c = (ulong)Random.Shared.NextInt64(1, n);
                ulong g = Brent(un, c);
                if (g != 1 && g != un) return (long)g;
            }
        }

        private static ulong Brent(ulong n, ulong c)
        {
            const int batch = 128;                           // gcd 호출을 묶어 비용 절감
            ulong y = 2, r = 1, q = 1, g = 1, x = 0, ys = 0;

            while (g == 1)
            {
                x = y;
                for (ulong i = 0; i < r; i++) y = Step(y, c, n);
                ulong k = 0;
                while (k < r && g == 1)
                {
                    ys = y;
                    ulong lim = Math.Min((ulong)batch, r - k);
                    for (ulong i = 0; i < lim; i++)
                    {
                        y = Step(y, c, n);
                        q = MulMod(q, x > y ? x - y : y - x, n);
                    }
                    g = Gcd(q, n);
                    k += (ulong)batch;
                }
                r <<= 1;
            }
            if (g == n)                                      // 배치 누적이 0이 된 경우 한 걸음씩 역추적
            {
                do
                {
                    ys = Step(ys, c, n);
                    g = Gcd(x > ys ? x - ys : ys - x, n);
                } while (g == 1);
            }
            return g;                                        // g==n이면 호출자가 c를 바꿔 재시도
        }

        // f(y) = (y² + c) mod n — 합 최대 2n-2 < 2⁶⁴ 이므로 조건부 뺄셈으로 충분
        private static ulong Step(ulong y, ulong c, ulong n)
        {
            ulong t = MulMod(y, y, n) + c;
            return t >= n ? t - n : t;
        }

        // 2·3·5 휠: 7부터 mod 30 잉여류 {7,11,13,17,19,23,29,31}만 후보로 순회
        private static readonly int[] Wheel235 = { 4, 2, 4, 2, 4, 6, 2, 6 };

        /// <summary>
        /// 2부터 시작하는 소수를 지연(lazy)으로 최대 count개 생성. count ≤ 0 이면 빈 시퀀스.
        /// 2·3·5 휠로 후보를 8/30(≈27%)로 줄인 뒤 Miller–Rabin으로 확정.
        /// </summary>
        public static IEnumerable<long> Generate(int count)
        {
            if (count <= 0) yield break;

            int found = 0;
            foreach (long p in new long[] { 2, 3, 5, 7 })
            {
                yield return p;
                if (++found == count) yield break;
            }

            long c = 7;
            int w = 0;
            while (true)
            {
                c += Wheel235[w];
                w = (w + 1) & 7;
                if (IsPrime(c))
                {
                    yield return c;
                    if (++found == count) yield break;
                }
                if (c > MaxLongPrime - 6) yield break;       // long 끝 도달 가드 (사실상 도달 불가)
            }
        }

        /// <summary>
        /// n번째 소수 (1-기준: NthPrime(1)=2, NthPrime(1000)=7919).
        /// 상계 p_n &lt; n(ln n + ln ln n) (Rosser, n ≥ 6)까지 체를 돌려 추출.
        /// n &lt; 1 또는 상계가 <see cref="MaxSieveLimit"/> 초과면 ArgumentOutOfRangeException.
        /// </summary>
        public static long NthPrime(int n)
        {
            if (n < 1)
                throw new ArgumentOutOfRangeException(nameof(n), n, "n은 1 이상이어야 합니다 (NthPrime(1)=2).");
            if (n < 6)
                return n switch { 1 => 2, 2 => 3, 3 => 5, 4 => 7, _ => 11 };

            double ln = Math.Log(n);
            long bound = (long)(n * (ln + Math.Log(ln))) + 10;
            GuardSieveLimit(bound, nameof(n));

            var bits = CreateOddSieve(bound);
            int count = 1;                                   // 2가 1번째
            for (long c = 3; c <= bound; c += 2)
                if (!GetBit(bits, c) && ++count == n) return c;

            throw new InvalidOperationException($"NthPrime({n}) 상계 추정 실패 — 발생하면 버그이니 보고해 주세요.");
        }

        /// <summary>a와 b가 서로소(gcd=1)면 true. 부호 무시. gcd(0,0)=0 → false.</summary>
        public static bool IsCoprime(long a, long b) => Gcd(AbsU(a), AbsU(b)) == 1;

        /// <summary>
        /// 오일러 피 함수 φ(n) — n 이하의 n과 서로소인 양의 정수 개수. n &lt; 1 이면 0 (φ(1)=1).
        /// 소수면 즉시 n-1, 아니면 소인수 기반 곱셈 공식 φ(n) = n·∏(1 − 1/p).
        /// </summary>
        public static long Totient(long n)
        {
            if (n < 1) return 0;
            if (n == 1) return 1;
            if (IsPrime(n)) return n - 1;                    // MR 조기 판정 — 큰 소수에서 √n 순회 회피

            long result = n, m = n;
            if ((m & 1) == 0)
            {
                result -= result / 2;
                while ((m & 1) == 0) m >>= 1;
            }
            for (long i = 3; i <= m / i; i += 2)
            {
                if (m % i != 0) continue;
                result -= result / i;
                while (m % i == 0) m /= i;
                if (m > 1 && IsPrime(m)) break;              // 잔여값이 소수면 종료
            }
            if (m > 1) result -= result / m;
            return result;
        }

        // long.MinValue 안전 절댓값 (Math.Abs는 MinValue에서 예외)
        private static ulong AbsU(long v) => v < 0 ? (ulong)(-(v + 1)) + 1UL : (ulong)v;

        private static ulong Gcd(ulong a, ulong b)
        {
            while (b != 0) (a, b) = (b, a % b);
            return a;
        }
    }
}
