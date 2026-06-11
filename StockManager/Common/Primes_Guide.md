# 🔢 Primes 모듈 — 개발자 가이드 & 분석 문서

> **대상 파일:** `Common/Primes.cs` (v2.1) · **테스트:** `..\StockManager.Tests\PrimesTests.cs` · **벤치:** `..\StockManager.PrimesBench\`
> **의존성:** 없음 (순수 .NET 8) · **상태:** ✅ 빌드 통과 · ✅ **단위 테스트 105/105 통과 (80ms)** · ✅ 벤치마크 실측 완료

---

## 0. 변경 이력 요약

### v1.2 (P4 백로그 완료)

| 영역 | 이전 (v2.0) | 현재 (v2.1) | 실측 효과 |
|---|---|---|---|
| `Factorize` 최악 케이스 | 두 거대 소수 곱 p·q는 O(√n) | **Pollard's rho (Brent 변형)** O(n^¼) | (2³¹−1)×2147483629: 3,748ms → **7.74ms (≈485배)** |
| 구간 결과 메모리 | List 일괄 적재 | **`SegmentedSieveStream`** — 지연 산출, 일정 메모리 | 폭 10¹⁰ 구간도 청크당 1MB로 순회 |
| π(n) 범위 | 체 한계 10⁹ | **`CountPrimesLong`** — Lucy_Hedgehog O(n^¾), **10¹³까지** | π(10⁹): 3,583ms → **12.25ms (≈292배)** · π(10¹⁰)=455,052,511 **55ms** |
| 벤치마크 | 없음 | **무의존 Stopwatch 하니스** (BenchmarkDotNet 불요) | 아래 §6 실측표 |

### v1.1 (P0~P3 로드맵 완료)

| 영역 | v1.0 | v2.0 |
|---|---|---|
| 판별 엔진 | 시산제 + 6k±1, O(√n) | **결정적 Miller–Rabin** (증인 2..37) — long 전 범위 **확정** |
| 큰 수 | `long` 한정 | **`BigInteger` 오버로드** — 3.317×10²⁴ 미만 결정적 |
| 체 메모리 | `bool[]` | **홀수 전용 비트 체** (1/16) · `MaxSieveLimit = 10⁹` |
| 자원 폭발 입력 | OOM 위험 | **상한 가드** → `ArgumentOutOfRangeException` |
| 구간 체 | 폭 int 한계 | **1M 청크 분할** + `FloorSqrt` |
| `NextPrime` 끝단 | 무한 루프 위험 | **`long?`** — 없으면 `null` |
| 정수론 유틸 | 없음 | `NthPrime`·`CountPrimes`·`IsCoprime`·`Totient` |
| 테스트 | 없음 | xUnit 케이스 고정 |

---

## 1. 프로젝트 개요 (Project Overview)

**"소수(prime number)는 정수론의 원자다. 이 모듈은 그 원자를 다루는 작지만 단단한 연장통이다."**

`Primes`는 **판별(test) · 생성(generate) · 분해(factorize) · 계량(count)** 을 한 곳에서 책임지는 **순수 정적 유틸리티**입니다. 외부 패키지 0, 인스턴스 상태 0 — 어디서든, 여러 스레드에서, 그냥 호출하면 됩니다.

이 모듈의 매력은 **알고리즘의 진화 단계가 코드·테스트·벤치마크로 함께 보존**되어 있다는 점입니다. 시산제의 직관에서 출발해 에라토스테네스의 체와 구간 체를 지나, 지금은 **결정적 Miller–Rabin**이 판별을, **Pollard's rho(Brent)** 가 분해를, **Lucy_Hedgehog** 가 계량을 맡습니다. `9,223,372,036,854,775,783`(long 최대 소수)의 확정 판별에 **44µs**, 4.6×10¹⁸ 세미프라임 분해에 **7.7ms**, π(10¹⁰) 계산에 **55ms** — 전부 이 저장소에서 직접 잰 숫자입니다.

## 2. 현재 구현 상태 및 핵심 로직 분석

### 2.1 공개 API 한눈에 보기

| 메서드 | 역할 | 반환 | 시간 복잡도 | 비고 |
|---|---|---|---|---|
| `IsPrime(long)` | 단일 판별 (확정) | `bool` | **O(k·log³n)**, k=12 | n<2 → `false` |
| `IsPrime(BigInteger)` | 임의 정밀도 판별 | `bool` | O(k·log³n) | <3.3×10²⁴ 결정적 |
| `Sieve(int limit)` | limit 이하 전체 소수 | `IReadOnlyList<int>` | O(n log log n) | limit ≤ 10⁹ |
| `CountPrimes(int)` | π(limit), 체 기반 | `int` | O(n log log n) | 목록 미생성 |
| `CountPrimesLong(long)` | π(limit), **Lucy** | `long` | **O(n^¾)** · 메모리 O(√n) | limit ≤ `MaxPiLimit`(10¹³) |
| `SegmentedSieve(low,high)` | 구간 소수 목록 | `IReadOnlyList<long>` | O((h−l)·llog h + √h) | high ≤ 10¹⁸ |
| `SegmentedSieveStream(low,high)` | 구간 소수 **지연 산출** | `IEnumerable<long>` | 〃 | 청크당 1MB · **검증은 즉시** |
| `NextPrime(long)` | 다음 소수 | **`long?`** | ~O(gap·log³n) | 없으면 `null` |
| `Factorize(long)` | 소인수분해 | `IReadOnlyList<long>` | **O(n^¼)** (rho) | 오름차순 보장 |
| `Generate(int count)` | 지연 스트림 | `IEnumerable<long>` | O(count·log³p) | 2·3·5 휠 |
| `NthPrime(int n)` | n번째 소수 | `long` | 체 1회 | Rosser 상계 |
| `IsCoprime(a,b)` | 서로소 | `bool` | O(log min) | `long.MinValue` 안전 |
| `Totient(long)` | 오일러 φ | `long` | O(√n), 소수면 즉답 | n<1 → 0 |

공개 상수: `MaxSieveLimit = 10⁹` · `MaxLongPrime = 9_223_372_036_854_775_783` · `MaxPiLimit = 10¹³`.

### 2.2 핵심 알고리즘

#### ① `IsPrime` — 결정적 Miller–Rabin
`n−1 = d·2^r` 분해 후 증인 {2,3,…,37}로 `a^d`부터 제곱 반복하며 합성수 증거를 찾습니다. 이 12개 증인은 **n < 3.317×10²⁴에서 결정적임이 증명** — long 범위는 전부 확정 판별. 64비트 모듈러 곱셈은 .NET 8 **`UInt128`** 로 오버플로우를 구조적으로 차단합니다.

#### ② `Sieve` — 홀수 전용 비트 체
홀수 `n ↔ 비트 (n−3)/2` 매핑의 `ulong[]` 마스크(메모리 1/16). 마킹은 `i²`부터 `2i` 간격. 개수 선계산으로 배열 1회 할당.

#### ③ `SegmentedSieve(Stream)` — 청크 분할 구간 체
`FloorSqrt(high)` 이하 기반 소수로 `[low,high]`를 **1M 청크**씩 거릅니다. 스트림 버전이 본체이고 목록 버전은 래퍼.
> ⚠️ **이터레이터 함정 회피:** C# 이터레이터는 예외도 지연됩니다. 인자 검증(high ≤ 10¹⁸)은 **래퍼에서 즉시** 수행하고 내부 로컬 함수만 `yield` — "호출 시점 예외" 계약을 지킵니다(테스트로 고정).

#### ④ `Factorize` — 시산제 ⨯ Pollard's rho(Brent) 하이브리드
1. 2와 1000 미만 홀수 소인수를 시산제로 제거 (작은 인수는 이 경로가 가장 빠름).
2. 잔여 합성수는 **분할 정복**: `IsPrime`(MR)으로 소수면 수확, 아니면 **rho**로 비자명 약수를 찾아 재귀.
3. rho는 Brent 변형 — `f(y)=y²+c mod n` 궤도의 순환을 **128개 단위 배치 gcd**로 탐지, 실패 시 `c`를 무작위로 바꿔 재시도(Las Vegas: 결과는 항상 정확, 시간만 확률적). `Random.Shared`로 스레드 안전.
4. rho 산출은 비순차이므로 마지막에 `Sort()` — **오름차순 계약 유지**.

#### ⑤ `CountPrimesLong` — Lucy_Hedgehog
`S(v)` = "2..v 중 소수이거나 최소 소인수 > p인 수의 개수"를 ⌊limit/i⌋의 **서로 다른 값 ≈2√n개**에 대해서만 유지. 소수 p를 만날 때마다 `S(v) -= S(v/p) − π(p−1)` 로 'p가 최소 소인수인 합성수'를 일괄 차감 → 마지막 `S(limit)`이 π(limit). 시간 O(n^¾)·메모리 O(√n) — 체가 닿지 못하는 10¹³까지.

## 3. 향후 개선 방안 (남은 것)

- **Meissel–Lehmer / primecount 계열:** π(n)을 10¹³ 너머로 (Lucy의 O(n^¾) 시간 한계 돌파).
- **`Factorize(BigInteger)`:** rho + ECPP/BPSW 조합으로 임의 정밀도 분해.
- **`Generate`의 체 백엔드:** 수백만 개 연속 생성 시 MR 개별 판별 대신 세그먼트 체 스트리밍이 유리.
- **BenchmarkDotNet 정밀 측정:** 현재 Stopwatch 하니스는 무의존 원칙 때문에 의도적 선택 — 통계적 정밀도(분산·이상치 제거)가 필요해지면 별도 프로젝트로 도입.

## 4. 잠재적 버그 및 예외 처리 분석

### 4.1 경계값 — 전부 테스트로 고정됨 ✅ (105케이스)

| 입력 | 메서드 | 결과 | 테스트 |
|---|---|---|---|
| 음수 / 0 / 1 | `IsPrime` | `false` | ✅ |
| 341 · 561(카마이클) · 1,373,653 | `IsPrime` | `false` (유사소수 함정) | ✅ |
| 2³¹−1 · 10⁹+7 · long 최대 소수 | `IsPrime` | `true` | ✅ |
| 2⁸⁹−1 / 2⁶⁷−1(콜) | `IsPrime(BigInteger)` | `true` / `false` | ✅ |
| `limit<2` / `int.MaxValue` | `Sieve` | 빈 목록 / **예외** | ✅ |
| `high<2`·`low>high`·`low<2` | `SegmentedSieve(Stream)` | 빈 / 빈 / 2 보정 | ✅ |
| `high=long.MaxValue` | `SegmentedSieveStream` | **열거 없이 즉시 예외** | ✅ |
| 청크 경계 횡단 | Stream vs List vs `Sieve` | 3중 교차검증 일치 | ✅ |
| `n ≥ MaxLongPrime` | `NextPrime` | **`null`** | ✅ |
| 세미프라임 (2³¹−1)×2147483629 · 1000003² · 561 | `Factorize` | rho 정확 분해·정렬 | ✅ |
| π(10⁶)=78,498 · π(10⁹)=50,847,534 · π(10¹⁰)=455,052,511 | `CountPrimesLong` | 표준 검증값 일치 | ✅ |
| `MaxPiLimit+1` | `CountPrimesLong` | **예외** | ✅ |
| `(0,0)` / `(long.MinValue,3)` | `IsCoprime` | `false` / `true` | ✅ |
| `φ(1)=1` · `φ(36)=12` · `φ(10⁹+7)` | `Totient` | ✅ | ✅ |

### 4.2 남아 있는 의도적 한계 (문서화된 계약)

1. **`SegmentedSieve`의 `high ≤ 10¹⁸`** — 기반 소수 체가 `MaxSieveLimit`에 묶임.
2. **`CountPrimesLong`의 `limit ≤ 10¹³`** — O(n^¾) 시간 한계 (10¹² ≈ 수십 초).
3. **rho의 실행 시간은 확률적** — 결과는 항상 정확(Las Vegas). 최악 재시도가 드물게 수 회 발생 가능.
4. **`IsPrime(BigInteger)` 초대형 입력** — 3.317×10²⁴ 이상은 확률적(오판 < 6×10⁻⁸).

## 5. 다음 작업 가이드 (Next Steps)

- [x] ~~P0: xUnit 테스트~~ → **완료 (105케이스, 80ms)**
- [x] ~~P1: 가드·청크화·`long?`·`IReadOnlyList`~~ → **완료**
- [x] ~~P2: Miller–Rabin · BigInteger · 비트 체~~ → **완료**
- [x] ~~P3: NthPrime·π·IsCoprime·φ·휠~~ → **완료**
- [x] ~~P4: Pollard's rho(Brent)~~ → **완료 (485배 가속 실측)**
- [x] ~~P4: `SegmentedSieveStream`~~ → **완료 (즉시 검증 계약 포함)**
- [x] ~~P4: 대범위 π(n) — Lucy_Hedgehog~~ → **완료 (10¹³까지)**
- [x] ~~P4: 벤치마크~~ → **완료 (무의존 Stopwatch 하니스 + 실측표)**
- [ ] **P5-신규:** Meissel–Lehmer π(n) — 10¹³ 너머 *(추천 다음 작업)*
- [ ] **P5-신규:** `Factorize(BigInteger)` — rho 일반화
- [ ] **P5-신규:** `Generate` 세그먼트 체 백엔드

## 6. 벤치마크 실측 (Release · 본 저장소에서 직접 측정)

```
[A] IsPrime(999,999,999,999,989)            — 10^15 미만 최대 소수
    시산제 6k±1   :    28.01 ms      Miller–Rabin :    43.93 µs   → 약 638배

[B] 체 10^8 (π = 5,761,455)
    bool[] 체     :   867.70 ms (~100MB)   홀수 비트 체 : 172.94 ms (~6.3MB · 1/16)

[C] Factorize(4,611,685,975,477,714,963) = 2,147,483,629 × 2,147,483,647
    시산제 O(√n)  :    3,748 ms      rho(Brent)   :     7.74 ms   → 약 485배

[D] π(n)
    비트 체 10^9  : 3,583.35 ms (~62.5MB)   Lucy 10^9 :  12.25 ms (~0.5MB) → 약 292배
    Lucy   10^10  :    55.12 ms   (체로는 불가능한 범위)
```
재현: `dotnet run --project StockManager.PrimesBench -c Release`

### 🏁 시작하기
```csharp
using StockManager.Common;

bool a = Primes.IsPrime(9_223_372_036_854_775_783);            // true — 44µs급
var f  = Primes.Factorize(2_147_483_629L * 2_147_483_647);     // [2147483629, 2147483647] — 7.7ms
long pi = Primes.CountPrimesLong(10_000_000_000);              // 455,052,511 — 55ms
foreach (var p in Primes.SegmentedSieveStream(1_000_000_000_000, 1_000_000_001_000))
    Console.WriteLine(p);                                      // 10^12 부근 구간을 일정 메모리로
```

### 테스트 / 벤치 실행
```bash
dotnet test StockManager.Tests                       # 통과! 105/105
dotnet run --project StockManager.PrimesBench -c Release
```

**환영합니다.** 모든 주장이 테스트와 실측으로 봉인되어 있습니다. 다음 한 걸음(Meissel–Lehmer)도 같은 방식으로 — 즐겁게 이어가 주세요! 🚀
