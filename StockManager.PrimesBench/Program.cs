// ============================================================================
// PrimesBench - Primes 모듈 경량 벤치마크 (외부 패키지 없음 · Stopwatch 기반)
//  비교 대상: v1.0 시절 알고리즘(로컬 재현) vs v2.x 라이브러리 구현
//   A. 단일 판별 : 시산제 6k±1            vs 결정적 Miller–Rabin
//   B. 일괄 생성 : bool[] 체              vs 홀수 전용 비트 체
//   C. 소인수분해: 시산제 전체 순회        vs Pollard's rho(Brent)
//   D. π(n)     : 체 카운팅               vs Lucy_Hedgehog O(n^¾)
//  실행: dotnet run --project StockManager.PrimesBench -c Release
// ============================================================================
using System.Diagnostics;
using StockManager.Common;

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("== Primes 벤치마크 (Release 권장 · 외부 패키지 0) ==\n");

// ---------- A. 단일 판별: 10^15 부근의 소수 ----------
{
    const long p = 999_999_999_999_989;   // 10^15 미만 최대 소수
    IsPrimeTrial(97); Primes.IsPrime(97L); // JIT 워밍업

    var sw = Stopwatch.StartNew();
    bool t1 = IsPrimeTrial(p);
    sw.Stop();
    var trialMs = sw.Elapsed.TotalMilliseconds;

    const int reps = 10_000;
    sw.Restart();
    bool t2 = true;
    for (int i = 0; i < reps; i++) t2 &= Primes.IsPrime(p);
    sw.Stop();
    var mrUs = sw.Elapsed.TotalMilliseconds * 1000.0 / reps;

    Console.WriteLine($"[A] IsPrime({p:N0})  — 둘 다 true={t1 && t2}");
    Console.WriteLine($"    시산제 6k±1   : {trialMs,10:N2} ms   (√n ≈ 3.16×10^7 회)");
    Console.WriteLine($"    Miller–Rabin  : {mrUs,10:N2} µs   (12증인 결정적, {reps:N0}회 평균)");
    Console.WriteLine($"    → 약 {(trialMs * 1000.0 / mrUs):N0}배 빠름\n");
}

// ---------- B. 일괄 생성: 10^8 체 ----------
{
    const int limit = 100_000_000;        // π(10^8) = 5,761,455
    var sw = Stopwatch.StartNew();
    int c1 = SieveBoolCount(limit);
    sw.Stop();
    var boolMs = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    int c2 = Primes.CountPrimes(limit);
    sw.Stop();
    var bitMs = sw.Elapsed.TotalMilliseconds;

    Console.WriteLine($"[B] 체 10^8 — π={c1:N0} (일치={c1 == c2})");
    Console.WriteLine($"    bool[] 체     : {boolMs,10:N2} ms   (메모리 ~100.0 MB)");
    Console.WriteLine($"    홀수 비트 체  : {bitMs,10:N2} ms   (메모리 ~  6.3 MB · 1/16)\n");
}

// ---------- C. 소인수분해: 거대 세미프라임 ----------
{
    const long p1 = 2_147_483_629, p2 = 2_147_483_647;   // 둘 다 소수
    const long n = p1 * p2;                              // 4,611,685,975,477,714,963

    Console.WriteLine($"[C] Factorize({n:N0}) = {p1:N0} × {p2:N0}");
    Console.WriteLine("    시산제 전체 순회 측정 중… (√n ≈ 2.1×10^9 — 수 초 소요)");

    var sw = Stopwatch.StartNew();
    var f1 = FactorizeTrial(n);
    sw.Stop();
    var trialMs = sw.Elapsed.TotalMilliseconds;

    Primes.Factorize(1_000_003L * 1_000_033);            // 워밍업
    sw.Restart();
    var f2 = Primes.Factorize(n);
    sw.Stop();
    var rhoMs = sw.Elapsed.TotalMilliseconds;

    bool same = f1.Count == f2.Count && f1[0] == f2[0] && f1[1] == f2[1];
    Console.WriteLine($"    결과 일치={same}");
    Console.WriteLine($"    시산제 O(√n)  : {trialMs,10:N0} ms");
    Console.WriteLine($"    rho(Brent)    : {rhoMs,10:N2} ms   (O(n^¼))");
    Console.WriteLine($"    → 약 {(trialMs / Math.Max(rhoMs, 0.001)):N0}배 빠름\n");
}

// ---------- D. π(n): 체 vs Lucy ----------
{
    var sw = Stopwatch.StartNew();
    int c1 = Primes.CountPrimes(1_000_000_000);          // 체 한계점
    sw.Stop();
    var sieveMs = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    long c2 = Primes.CountPrimesLong(1_000_000_000);
    sw.Stop();
    var lucy9Ms = sw.Elapsed.TotalMilliseconds;

    sw.Restart();
    long c3 = Primes.CountPrimesLong(10_000_000_000);    // 체로는 불가능한 범위
    sw.Stop();
    var lucy10Ms = sw.Elapsed.TotalMilliseconds;

    Console.WriteLine($"[D] π(10^9) = {c1:N0} (일치={c1 == c2}) · π(10^10) = {c3:N0}");
    Console.WriteLine($"    비트 체 10^9  : {sieveMs,10:N2} ms   (메모리 ~62.5 MB)");
    Console.WriteLine($"    Lucy   10^9   : {lucy9Ms,10:N2} ms   (메모리 ~0.5 MB)");
    Console.WriteLine($"    Lucy   10^10  : {lucy10Ms,10:N2} ms   (체 한계 밖 — Lucy 전용)\n");
}

Console.WriteLine("== 완료 ==");

// ===== v1.0 시절 알고리즘 로컬 재현 (비교 기준선) =====
static bool IsPrimeTrial(long n)
{
    if (n < 2) return false;
    if (n < 4) return true;
    if (n % 2 == 0 || n % 3 == 0) return false;
    for (long i = 5; i <= n / i; i += 6)
        if (n % i == 0 || n % (i + 2) == 0) return false;
    return true;
}

static int SieveBoolCount(int limit)
{
    var composite = new bool[limit + 1];
    for (int i = 2; (long)i * i <= limit; i++)
    {
        if (composite[i]) continue;
        for (long j = (long)i * i; j <= limit; j += i)
            composite[j] = true;
    }
    int count = 0;
    for (int i = 2; i <= limit; i++)
        if (!composite[i]) count++;
    return count;
}

static List<long> FactorizeTrial(long n)
{
    var factors = new List<long>();
    while ((n & 1) == 0) { factors.Add(2); n >>= 1; }
    for (long i = 3; i <= n / i; i += 2)
        while (n % i == 0) { factors.Add(i); n /= i; }
    if (n > 1) factors.Add(n);
    return factors;
}
