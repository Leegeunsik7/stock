// ============================================================================
// PlutoMicrostructureFactor — v12.18.75-DEV: 플루토 브레인 4단계 — 미시구조 팩터(CVD 다이버전스 + 10호가 뎁스 불균형 합성 · 불변 값 타입 · 정수 필드 · 할당 0)
//
//   상태(PlutoMicrostructureState · 엔진 종목별 값 타입 · 정산 스레드 단일 작성자):
//     CVD(PlutoCvdState · 체결 방향 누적 · 1분 봉 링) + 직전 뎁스 판독(PlutoDepthReading · 호가 수신마다 교체).
//   팩터(Assess · 현재가 재판정): CvdSlopeBps · DepthRatioBps · DivergenceType(byte) · MicrostructureScore(−100~+100) + CVD/뎁스 세부.
//     합성 점수 = clamp(CVD 점수 + 뎁스 점수, ±100) · 시그널 = 점수 × 100bp(아레나 MicrostructureWeight 가중).
//   게시: 아레나 정산 1회의 seqlock 쓰기 창 안에서 종목 슬롯에 기록 → TryReadMicrostructure(같은 버전 검증 · δ=0).
//   악마의 변호인: 약세 다이버전스 = ⑤ CvdBearishDivergence(−25) · 매도벽/허매수 = ⑥ DepthTrap(−20).
//   ★liveArmed:false — 팩터는 가상 대결 시그널 입력일 뿐 주문 수단 0. ★QuantumVault · 피어 · 외부 네트워크 접근 0.
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Microstructure
{
    /// <summary>종목 1개 미시구조 상태(CVD + 직전 뎁스 판독 · default = 데이터 없음).</summary>
    public struct PlutoMicrostructureState
    {
        public PlutoCvdState Cvd;
        public PlutoDepthReading Depth;
    }

    public static class PlutoMicrostructureAnalyzer
    {
        /// <summary>호가 잔량 수신 → 뎁스 판독 교체(δ=0 · 무효 = 판정 없음으로 교체 · fail-closed).</summary>
        public static void OnDepth(ref PlutoMicrostructureState s, ReadOnlySpan<long> bidQty, ReadOnlySpan<long> askQty) =>
            s.Depth = PlutoOrderbookDepthAnalyzer.Analyze(bidQty, askQty);

        /// <summary>L1(최우선 1호가)만 제공되는 피드 — 1호가 범위 비례 가중(δ=0 · 스택 단일 원소 span).</summary>
        public static void OnL1(ref PlutoMicrostructureState s, long bidQty, long askQty) =>
            s.Depth = PlutoOrderbookDepthAnalyzer.Analyze(new ReadOnlySpan<long>(in bidQty), new ReadOnlySpan<long>(in askQty));

        /// <summary>실측 방향 체결 1건(업비트 ask_bid).</summary>
        public static void OnTrade(ref PlutoMicrostructureState s, int side, long volume, long price, long timeTicks) =>
            PlutoCumulativeVolumeDelta.OnTrade(ref s.Cvd, side, volume, price, timeTicks);

        /// <summary>방향 미제공 피드(KRX/US) — 틱 룰 방향으로 누적(첫 가격 = 방향 미정 · 누적 생략).</summary>
        public static void OnTickRuleTrade(ref PlutoMicrostructureState s, long volume, long price, long timeTicks)
        {
            int side = PlutoCumulativeVolumeDelta.TickRuleSide(ref s.Cvd, price);
            if (side != 0) PlutoCumulativeVolumeDelta.OnTrade(ref s.Cvd, side, volume, price, timeTicks);
        }

        /// <summary>합성 점수 = clamp(CVD + 뎁스, ±100).</summary>
        public static int Combine(int cvdScore, int depthScore) => Math.Clamp(cvdScore + depthScore, -100, 100);

        /// <summary>현재가 기준 팩터(순수 · 할당 0). CVD 미성숙 ∧ 뎁스 판정 없음 = 판정 없음(HasMicrostructure = false · 기여 0).</summary>
        public static PlutoMicrostructureFactor Assess(in PlutoMicrostructureState s, long symbol, long price)
        {
            if (symbol <= 0 || price <= 0) return default;
            var cvd = PlutoCumulativeVolumeDelta.Read(in s.Cvd, price);
            if (!cvd.Ready && s.Depth.Tiers == 0) return default;
            return new PlutoMicrostructureFactor(symbol, price, in cvd, in s.Depth);
        }
    }

    /// <summary>종목 1개 미시구조 팩터(불변 값 타입 · 정수 필드). default = 판정 없음.</summary>
    public readonly struct PlutoMicrostructureFactor
    {
        /// <summary>점수 1점 시그널 100bp(±100 → ±10000bp).</summary>
        public const int BpPerPoint = 100;
        /// <summary>CVD 매수 우위 뱃지 문턱(창 순공격 매수 비중 +10%).</summary>
        public const int BuyDominanceBps = 1000;

        public readonly long Symbol, Price, Cvd;
        public readonly bool CvdReady;
        public readonly int CvdBars, CvdSlopeBps, PriceChangeBps, CvdScore;
        public readonly PlutoDivergenceType DivergenceType;
        public readonly int DepthTiers, DepthRatioBps, DepthScore;
        public readonly long WeightedBidDepth, WeightedAskDepth;
        public readonly PlutoDepthFlags DepthFlags;
        public readonly int MicrostructureScore;

        public PlutoMicrostructureFactor(long symbol, long price, in PlutoCvdReading cvd, in PlutoDepthReading depth)
        {
            Symbol = symbol; Price = price;
            Cvd = cvd.Cvd; CvdReady = cvd.Ready; CvdBars = cvd.Bars; CvdSlopeBps = cvd.CvdSlopeBps; PriceChangeBps = cvd.PriceChangeBps; CvdScore = cvd.Score;
            DivergenceType = cvd.Divergence;
            DepthTiers = depth.Tiers; DepthRatioBps = depth.DepthRatioBps; DepthScore = depth.Score;
            WeightedBidDepth = depth.WeightedBid; WeightedAskDepth = depth.WeightedAsk; DepthFlags = depth.Flags;
            MicrostructureScore = PlutoMicrostructureAnalyzer.Combine(cvd.Score, depth.Score);
        }

        public bool HasMicrostructure => Symbol > 0;
        public int MicrostructureBp => MicrostructureScore * BpPerPoint;
        public bool IsBullishDivergence => DivergenceType == PlutoDivergenceType.Bullish;
        /// <summary>가격 상승 ∧ CVD 급락 — 악마의 변호인 반대 논리 ⑤(CvdBearishDivergence).</summary>
        public bool IsBearishDivergence => DivergenceType == PlutoDivergenceType.Bearish;
        public bool IsCvdBuyDominant => CvdReady && CvdSlopeBps >= BuyDominanceBps;
        public bool HasBidSupportWall => (DepthFlags & PlutoDepthFlags.BidSupportWall) != 0;
        public bool HasAskWall => (DepthFlags & PlutoDepthFlags.AskWall) != 0;
        public bool IsBidSpoof => (DepthFlags & PlutoDepthFlags.BidSpoof) != 0;
        /// <summary>상단 매도벽 ∨ 하단 허매수 — 악마의 변호인 반대 논리 ⑥(DepthTrap).</summary>
        public bool IsDepthTrap => HasAskWall || IsBidSpoof;

        /// <summary>
        /// 무결성: default = 판정 없음(전 필드 0) · 판정 = 식별/가격 양수 · 점수 범위 · 합성 재계산 일치 ·
        /// CVD 미성숙 = 기울기/다이버전스/점수 0 · 성숙 = 봉 수 MinBars~Window · 다이버전스-점수-문턱 일치 · 뎁스 0호가 = 비율/플래그/점수 0 ·
        /// 벽 플래그 = 3호가 이상 · 허매수와 매수벽 배타 · 뎁스 점수 = 비율/플래그 재계산 일치.
        /// </summary>
        public bool IsWellFormed
        {
            get
            {
                if (Symbol == 0)
                    return Price == 0 && Cvd == 0 && !CvdReady && CvdBars == 0 && CvdSlopeBps == 0 && PriceChangeBps == 0 && CvdScore == 0
                        && DivergenceType == PlutoDivergenceType.None && DepthTiers == 0 && DepthRatioBps == 0 && DepthScore == 0
                        && WeightedBidDepth == 0 && WeightedAskDepth == 0 && DepthFlags == PlutoDepthFlags.None && MicrostructureScore == 0;
                if (Symbol < 0 || Price <= 0) return false;
                if (MicrostructureScore != PlutoMicrostructureAnalyzer.Combine(CvdScore, DepthScore)) return false;
                if (!CvdReady)
                {
                    if (CvdSlopeBps != 0 || PriceChangeBps != 0 || CvdScore != 0 || DivergenceType != PlutoDivergenceType.None) return false;
                }
                else
                {
                    if (CvdBars < PlutoCumulativeVolumeDelta.MinBars || CvdBars > PlutoCumulativeVolumeDelta.Window || CvdSlopeBps is < -10000 or > 10000) return false;
                    bool ok = DivergenceType switch
                    {
                        PlutoDivergenceType.Bullish => PriceChangeBps <= PlutoCumulativeVolumeDelta.FlatBps && CvdSlopeBps >= PlutoCumulativeVolumeDelta.DivergenceSlopeBps
                                                       && CvdScore == PlutoCumulativeVolumeDelta.BullishScore(CvdSlopeBps),
                        PlutoDivergenceType.Bearish => PriceChangeBps >= PlutoCumulativeVolumeDelta.RiseBps && CvdSlopeBps <= -PlutoCumulativeVolumeDelta.DivergenceSlopeBps
                                                       && CvdScore == PlutoCumulativeVolumeDelta.BearishScore,
                        PlutoDivergenceType.None => CvdScore == CvdSlopeBps / PlutoCumulativeVolumeDelta.NeutralSlopePerPoint,
                        _ => false,
                    };
                    if (!ok) return false;
                }
                if (DepthTiers == 0) return DepthRatioBps == 0 && DepthScore == 0 && DepthFlags == PlutoDepthFlags.None && WeightedBidDepth == 0 && WeightedAskDepth == 0;
                if (DepthTiers > PlutoOrderbookDepthAnalyzer.MaxTiers || DepthRatioBps is < -10000 or > 10000 || WeightedBidDepth < 0 || WeightedAskDepth < 0) return false;
                if (DepthFlags != PlutoDepthFlags.None && DepthTiers < PlutoOrderbookDepthAnalyzer.WallMinTiers) return false;
                if (HasBidSupportWall && IsBidSpoof) return false;
                return DepthScore == PlutoOrderbookDepthAnalyzer.ScoreOf(DepthRatioBps, DepthFlags);
            }
        }
    }

    /// <summary>미시구조 팩터 한국어 표기(콜드 경로 — 호출마다 문자열 조립).</summary>
    public static class PlutoMicrostructureText
    {
        public const string CvdBuyBadge = "[📊 CVD 매수우위]", BullishBadge = "[📈 강세 다이버전스]", BearishBadge = "[📉 약세 다이버전스]";
        public const string BidWallBadge = "[🛡️ 10호가 매수벽 지지]", AskWallBadge = "[🧱 10호가 매도벽]", SpoofBadge = "[🪤 허매수 의심]";

        /// <summary>뱃지 열(해당 없음 = 빈 문자열).</summary>
        public static string Badges(in PlutoMicrostructureFactor f)
        {
            if (!f.HasMicrostructure) return "";
            string b = "";
            if (f.IsCvdBuyDominant) b += CvdBuyBadge;
            if (f.IsBullishDivergence) b += (b.Length > 0 ? " " : "") + BullishBadge;
            if (f.IsBearishDivergence) b += (b.Length > 0 ? " " : "") + BearishBadge;
            if (f.HasBidSupportWall) b += (b.Length > 0 ? " " : "") + BidWallBadge;
            if (f.HasAskWall) b += (b.Length > 0 ? " " : "") + AskWallBadge;
            if (f.IsBidSpoof) b += (b.Length > 0 ? " " : "") + SpoofBadge;
            return b;
        }

        public static string DivergenceLabel(PlutoDivergenceType d) => d switch
        {
            PlutoDivergenceType.Bullish => "강세 다이버전스(가격 횡보/하락 · CVD 상승)",
            PlutoDivergenceType.Bearish => "약세 다이버전스(가격 상승 · CVD 급락)",
            _ => "다이버전스 없음",
        };
    }
}
