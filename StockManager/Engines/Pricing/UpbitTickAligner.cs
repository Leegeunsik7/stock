// ============================================================================
// UpbitTickAligner — [v12.18.54-DEV] 업비트 원화(KRW) 마켓 호가 단위 무할당 정수 정렬(raw 10^8 · δ=0)
//
//  규칙 = 업비트 개발자 문서 '원화 마켓 호가 단위'(2025-07-31 개정 · docs.upbit.com/kr/docs/krw-market-info · 2026-10-22 대조):
//   2,000,000 이상 1,000 · 1,000,000~2,000,000 1,000 · 500,000~1,000,000 500 · 100,000~500,000 100 · 50,000~100,000 50 ·
//   10,000~50,000 10 · 5,000~10,000 5 · 1,000~5,000 1 · 100~1,000 1 · 10~100 0.1 · 1~10 0.01 · 0.1~1 0.001 · 0.01~0.1 0.0001 ·
//   0.001~0.01 0.00001 · 0.0001~0.001 0.000001 · 0.00001~0.0001 0.0000001 · 0.00001 미만 0.00000001(raw 1)
//   ※ 개정 전 표(1,000,000~2,000,000 500 · 500,000~1,000,000 100 · 100,000~500,000 50)는 현행 거래소에서 거부되는 가격을 만든다 — 사용 금지.
//   ※ 종목 한정 예외(2025-03-21 USDT/USDC 1,000~10,000 구간 · 2024-10-14 16종목 100~1,000 구간)는 미반영 — KRW-BTC 대상(2,000,000 이상 = 1,000).
//  경계 성질: 각 구간 하한은 상위 틱의 배수 — 내림/올림 결과는 언제나 유효 격자점(UsTickAligner · KrxTickAligner 와 같은 성질).
//  보호 방향(동일 계약): 매수 상한 = AlignDown(더 비싸게 사지 않음) · 매도 하한 = AlignUp(더 싸게 팔지 않음).
//  fail-closed: 가격 ≤0 → 0(무효) · 올림 오버플로(long.MaxValue 근방) → 0(무효 = 미송출). 분기 사슬 · 나머지 1 · 호출당 힙 0.
// ============================================================================
namespace StockManager.Engines.Pricing
{
    public static class UpbitTickAligner
    {
        public const long Scale = 100_000_000L;     // 1원 = raw 10^8
        public const long Won = Scale;

        /// <summary>가격(raw)의 업비트 KRW 마켓 호가 단위(raw). 가격 ≤0 → 0(무효).</summary>
        public static long TickFor(long priceRaw)
        {
            if (priceRaw <= 0) return 0;
            if (priceRaw >= 1_000_000 * Won) return 1_000 * Won;
            if (priceRaw >= 500_000 * Won) return 500 * Won;
            if (priceRaw >= 100_000 * Won) return 100 * Won;
            if (priceRaw >= 50_000 * Won) return 50 * Won;
            if (priceRaw >= 10_000 * Won) return 10 * Won;
            if (priceRaw >= 5_000 * Won) return 5 * Won;
            if (priceRaw >= 100 * Won) return Won;                  // 100~5,000 = 1원(두 구간 동일)
            if (priceRaw >= 10 * Won) return Won / 10;
            if (priceRaw >= Won) return Won / 100;
            if (priceRaw >= Won / 10) return Won / 1_000;
            if (priceRaw >= Won / 100) return Won / 10_000;
            if (priceRaw >= Won / 1_000) return Won / 100_000;
            if (priceRaw >= Won / 10_000) return Won / 1_000_000;
            if (priceRaw >= Won / 100_000) return Won / 10_000_000;
            return 1;                                               // 0.00000001원
        }

        /// <summary>호가 격자로 내림(매수 상한 보호 정렬). ≤0 → 0.</summary>
        public static long AlignDown(long priceRaw)
        {
            long t = TickFor(priceRaw);
            return t == 0 ? 0 : priceRaw - priceRaw % t;
        }

        /// <summary>호가 격자로 올림(매도 하한 보호 정렬). ≤0 → 0 · 오버플로 = 0(무효).</summary>
        public static long AlignUp(long priceRaw)
        {
            long t = TickFor(priceRaw);
            if (t == 0) return 0;
            long r = priceRaw % t;
            if (r == 0) return priceRaw;
            long add = t - r;
            return priceRaw > long.MaxValue - add ? 0 : priceRaw + add;
        }

        /// <summary>가장 가까운 격자점(반올림 · 중간값 = 올림). ≤0 → 0 · 오버플로 = 0.</summary>
        public static long AlignNearest(long priceRaw)
        {
            long t = TickFor(priceRaw);
            if (t == 0) return 0;
            long r = priceRaw % t;
            return r * 2 < t ? priceRaw - r : AlignUp(priceRaw);
        }

        public static bool IsAligned(long priceRaw) => priceRaw > 0 && priceRaw % TickFor(priceRaw) == 0;
    }
}
