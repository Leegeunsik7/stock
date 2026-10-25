// ============================================================================
// PlutoOrderbookDepthAnalyzer — v12.18.75-DEV: 플루토 브레인 4단계 — 15호가(v12.18.76 확장) 지수 가중 뎁스 불균형 + 매도벽/매수벽/허매수 판정(순수 함수 · 정수 산술 · δ=0)
//
//   입력 = 매수/매도 호가별 잔량(ReadOnlySpan · [0] = 최우선 · 최대 MaxTiers(15) · 수량 단위 정수).
//   가중: 최유리 호가 근접도 지수 감쇄 — 3호가마다 절반(w_i = round(1000 × 2^(−i/3)) ‰ = 1000, 794, 630, 500, 397, 315, 250, 198, 157, 125, 99, 79, 63, 50, 39).
//     양측 공통 제공 범위 n = min(매수 호가 수, 매도 호가 수, 15)만 비교(US/KRX 현 피드 = L1 → n = 1 · 비율식이 제공 범위 내 가중을 자동 정규화 = 비례 가중).
//     [v12.18.76-DEV] 업비트 = orderbook_units 최대 15호가 실측(UpbitOrderbookDepth) → n = 15 · 벽 판정 실가동. 벽 위치 = BidWallTier/AskWallTier(1부터 · 없음 0).
//   DepthRatioBps = (가중 매수 − 가중 매도) × 10000 / (가중 매수 + 가중 매도)(−10000~+10000 · 양측 0 = 0).
//   벽 판정(n ≥ WallMinTiers(3) · 한 호가 잔량 ≥ 나머지 호가 평균 × WallMultiple(4)):
//     매수벽 지지(BidSupportWall) = 매수 측 벽이 근접 호가(< NearTiers(3)) · 상단 매도벽(AskWall) = 매도 측 벽(위치 무관) ·
//     허매수(BidSpoof · Trap) = 매수 측 벽이 원거리 호가(≥ NearTiers) ∧ 그 잔량이 매수 원잔량 합의 50% 이상(체결 의사 낮은 미끼 물량).
//   점수(−50~+50) = DepthRatioBps / 250(±40) · 허매수 = 양수 기여 박탈 후 −10 · 매수벽 지지 +10 · 매도벽 −15.
//   악마의 변호인 ⑥(DepthTrap): 매도벽 ∨ 허매수.
//   ★δ=0: Analyze 힙 할당 0(정적 가중표 · 값 타입 · Int128 · LINQ/박싱/클로저/문자열 0). 음수 잔량 = 판정 없음(fail-closed).
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Microstructure
{
    [Flags]
    public enum PlutoDepthFlags : byte
    {
        None = 0,
        BidSupportWall = 1,
        AskWall = 2,
        BidSpoof = 4,
    }

    /// <summary>뎁스 판독(값 타입 · Tiers = 0 ↔ 판정 없음).</summary>
    public readonly struct PlutoDepthReading
    {
        public readonly int Tiers, DepthRatioBps, Score;
        public readonly long WeightedBid, WeightedAsk;
        public readonly PlutoDepthFlags Flags;
        /// <summary>[v12.18.76-DEV] 벽 위치(1 = 최우선 · 0 = 해당 플래그 없음). BidWallTier = 매수벽 지지 ∨ 허매수 호가 · AskWallTier = 매도벽 호가.</summary>
        public readonly int BidWallTier, AskWallTier;

        public PlutoDepthReading(int tiers, long weightedBid, long weightedAsk, int depthRatioBps, PlutoDepthFlags flags, int score, int bidWallTier = 0, int askWallTier = 0)
        {
            Tiers = tiers; WeightedBid = weightedBid; WeightedAsk = weightedAsk; DepthRatioBps = depthRatioBps; Flags = flags; Score = score;
            BidWallTier = bidWallTier; AskWallTier = askWallTier;
        }
    }

    public static class PlutoOrderbookDepthAnalyzer
    {
        public const int MaxTiers = 15, WallMinTiers = 3, NearTiers = 3, WallMultiple = 4, SpoofSharePct = 50;
        public const int RatioPerPoint = 250, SupportWallBonus = 10, AskWallPenalty = -15, SpoofPenalty = -10, MaxScore = 50;
        public const int WeightScale = 1000;

        /// <summary>호가 i(0 = 최우선) 가중(‰ · 3호가마다 절반).</summary>
        private static readonly int[] Weights = { 1000, 794, 630, 500, 397, 315, 250, 198, 157, 125, 99, 79, 63, 50, 39 };

        public static int WeightOf(int tier) => (uint)tier < MaxTiers ? Weights[tier] : 0;

        /// <summary>뎁스 판정(순수 · 할당 0). 공통 제공 호가 0 ∨ 음수 잔량 = 판정 없음(default).</summary>
        public static PlutoDepthReading Analyze(ReadOnlySpan<long> bidQty, ReadOnlySpan<long> askQty)
        {
            int n = Math.Min(Math.Min(bidQty.Length, askQty.Length), MaxTiers);
            if (n <= 0) return default;
            Int128 wb = 0, wa = 0, rb = 0, ra = 0;
            long maxB = 0, maxA = 0;
            int maxBi = 0, maxAi = 0;
            for (int i = 0; i < n; i++)
            {
                long b = bidQty[i], a = askQty[i];
                if (b < 0 || a < 0) return default;
                int w = Weights[i];
                wb += (Int128)b * w; wa += (Int128)a * w;
                rb += b; ra += a;
                if (b > maxB) { maxB = b; maxBi = i; }
                if (a > maxA) { maxA = a; maxAi = i; }
            }
            Int128 sum = wb + wa;
            int ratio = sum <= 0 ? 0 : (int)((wb - wa) * 10000 / sum);
            var flags = PlutoDepthFlags.None;
            int bidTier = 0, askTier = 0;
            if (n >= WallMinTiers)
            {
                if (IsWall(maxB, rb, n))
                    flags |= maxBi < NearTiers ? PlutoDepthFlags.BidSupportWall
                           : (Int128)maxB * 100 >= rb * SpoofSharePct ? PlutoDepthFlags.BidSpoof : PlutoDepthFlags.None;
                if ((flags & (PlutoDepthFlags.BidSupportWall | PlutoDepthFlags.BidSpoof)) != 0) bidTier = maxBi + 1;
                if (IsWall(maxA, ra, n)) { flags |= PlutoDepthFlags.AskWall; askTier = maxAi + 1; }
            }
            return new PlutoDepthReading(n, Sat(wb / WeightScale), Sat(wa / WeightScale), ratio, flags, ScoreOf(ratio, flags), bidTier, askTier);
        }

        /// <summary>벽 = 최대 호가 잔량 ≥ 나머지 (n − 1) 호가 평균 × WallMultiple(최대 &gt; 0).</summary>
        private static bool IsWall(long max, Int128 total, int n) => max > 0 && (Int128)max * (n - 1) >= (total - max) * WallMultiple;

        private static long Sat(Int128 v) => (long)Int128.Clamp(v, 0, long.MaxValue);

        /// <summary>점수 = 비율 / 250 · 허매수 = 양수 박탈 −10 · 매수벽 +10 · 매도벽 −15(±50 포화).</summary>
        public static int ScoreOf(int ratioBps, PlutoDepthFlags flags)
        {
            int score = Math.Clamp(ratioBps, -10000, 10000) / RatioPerPoint;
            if ((flags & PlutoDepthFlags.BidSpoof) != 0) score = Math.Min(score, 0) + SpoofPenalty;
            if ((flags & PlutoDepthFlags.BidSupportWall) != 0) score += SupportWallBonus;
            if ((flags & PlutoDepthFlags.AskWall) != 0) score += AskWallPenalty;
            return Math.Clamp(score, -MaxScore, MaxScore);
        }
    }
}
