// ============================================================================
// UsTickAligner — [v12.18.53-DEV] 미국 주식 호가 단위 무할당 정수 정렬(QPrice raw 10^8 · δ=0)
//
//  규칙(SEC Reg NMS Rule 612 · 최소 호가 단위):
//   가격 ≥ $1.00 → $0.01(1센트 = raw 1,000,000) · 가격 < $1.00 → $0.0001(raw 10,000 · 서브페니 허용 구간)
//   ※ $1.00 경계는 상위 틱($0.01)의 배수 — 내림/올림 결과는 언제나 유효 격자점(KrxTickAligner 와 같은 성질).
//  보호 방향(KrxTickAligner 와 동일 계약): 매수 상한 = AlignDown(더 비싸게 사지 않음) · 매도 하한 = AlignUp(더 싸게 팔지 않음).
//  fail-closed: 가격 ≤0 → 0(무효) · 올림 오버플로(long.MaxValue 근방) → 0(무효 = 미송출). 분기 1 · 나머지 1 · 호출당 힙 0.
// ============================================================================
namespace StockManager.Engines.Pricing
{
    public static class UsTickAligner
    {
        public const long Scale = 100_000_000L;
        public const long PennyRaw = Scale / 100;           // $0.01
        public const long SubPennyRaw = Scale / 10_000;     // $0.0001
        public const long OneDollarRaw = Scale;             // 틱 경계 $1.00

        /// <summary>가격(raw)의 미국 주식 호가 단위(raw). 가격 ≤0 → 0(무효).</summary>
        public static long TickFor(long priceRaw)
        {
            if (priceRaw <= 0) return 0;
            return priceRaw < OneDollarRaw ? SubPennyRaw : PennyRaw;
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
