// ============================================================================
// PlutoDiscoveryScorer — v12.18.78-DEV: 플루토 종목 발굴 4대 팩터 종합 스코어러(0~100점) + 악마의 변호인 리스크 게이트
//
//   입력 = PlutoScreenInputs(종목 1건 · 기존 4대 브레인 판독의 평탄화 값 타입) + PlutoScreenMacro(학습 스웜 최근접 과거 국면).
//   팩터별 0~25점:
//     a) 펀더멘털 안전마진 = PlutoValuationTag 점수(−100~+100) 선형 투영 · b) 기술 = 볼밴 스퀴즈 돌파 + POC 매물대 지지(PlutoTechnicalFactor 종합) ·
//     c) 수급 = CVD 다이버전스 + 15호가 뎁스(PlutoMicrostructureFactor 종합) · d) 거시 적합도 = 종목 국면(0~15) + 에피소딕 과거 국면 유사도(0~10).
//   ★판독 없는 팩터 = 0점(중립 12점 아님 — 검증 안 된 종목이 상위로 오르지 않게 · fail-closed). 판독 3종(a~c) 전무 = '근거 부족' 제외.
//   반대 논리 6종(고평가 · 기술 붕괴 · CVD 약세 · 뎁스 함정 · 약세/패닉 국면 · 거시 위험 회피 국면) — 2건 이상 중첩 = 추천 목록 자동 탈락.
//   추천 문턱 = 종합 50점(미만 = '기준 미달' 계수만 · 목록 미표시).
//   ★콜드 경로(Track B 스크리너 스레드) 전용 — 근거 문구 = 문자열 생성. 체결 핫패스는 PlutoRecommendationBoard.TryFind 만(δ=0).
//   ★liveArmed:false — 결과는 UI 추천 카드뿐(주문 · 무장 · 브로커 경로 0).
//   [v12.18.79-DEV] 가상자산(업비트) 전용 4대 팩터(각 0~25점 · 펀더멘털 없음): 유동성(최우선 스프레드 + 뎁스 호가 수) · 볼밴 스퀴즈 ·
//     CVD 다이버전스 · 15호가 뎁스벽(DepthRatioBps). 판독 2종(기술 · 미시구조) 전무 = '근거 부족'. 반대 논리 6종 3대 자산 공통 —
//     1번 게이트 = 주식 '고평가' · 코인 '호가 스프레드 과대(유동성 고갈 · ≥ 50bp)'(코인은 밸류에이션 판독이 없으므로 진입 가격 부담을 스프레드로 판정).
// ============================================================================
using System;
using System.Globalization;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Regime;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분

namespace StockManager.Engines.Cognitive.Screening
{
    public enum PlutoScreenMarket : byte
    {
        Krx = 0,
        Us = 1,
        Crypto = 2,   // [v12.18.79-DEV] 업비트 KRW(가격 = 원 × 10^8 raw · 수량 = 코인 × 10^8 raw)
    }

    /// <summary>스크리너 반대 논리(비트 플래그 · 복수 충족 가능).</summary>
    [Flags]
    public enum PlutoScreenRisk : byte
    {
        None = 0,
        Overvalued = 1,          // 고평가 경계 ∨ 극심한 과열(코인 = 호가 스프레드 과대)
        TechnicalBreakdown = 2,  // 볼밴 하단 이탈 ∨ POC 매물벽 직하 저항
        CvdBearish = 4,          // CVD 약세 다이버전스
        DepthTrap = 8,           // 15호가 매도벽 ∨ 허매수
        BearRegime = 16,         // 종목 국면 하락 추세 ∨ 패닉 투매
        MacroRiskOff = 32,       // 과거 약세/패닉 국면과 유사도 ≥ 60%
    }

    /// <summary>종목 1건 스크리닝 입력(평탄화 값 타입 · 판독 없는 팩터 = Has* false).</summary>
    public readonly struct PlutoScreenInputs
    {
        public readonly PlutoScreenMarket Market;
        public readonly long Symbol, Price;
        public readonly bool HasValuation;
        public readonly int ValuationScore, MarginOfSafetyBps;
        public readonly PlutoValuationGrade Grade;
        public readonly long FairPrice;
        public readonly bool SeedProfile;
        public readonly bool HasTechnical;
        public readonly int TechnicalScore;
        public readonly PlutoSqueezePhase Phase;
        public readonly PlutoProfileZone Zone;
        public readonly bool HasMicro;
        public readonly int MicroScore;
        public readonly PlutoDivergenceType Divergence;
        public readonly bool CvdBuyDominant, BidWall, DepthTrap;
        public readonly MarketRegime Regime;
        /// <summary>[v12.18.79-DEV] 코인 팩터 세부(스퀴즈 · CVD · 뎁스 점수 −100~+100 · 뎁스 비율 bp · 판독 호가 수) · Bid = 매수 1호가(0 = 미판독 → 유동성 0점).</summary>
        public readonly int SqueezeScore, CvdScore, DepthScore, DepthRatioBps, DepthTiers;
        public readonly long Bid;

        public PlutoScreenInputs(PlutoScreenMarket market, long symbol, long price,
            bool hasValuation, int valuationScore, int marginOfSafetyBps, PlutoValuationGrade grade, long fairPrice, bool seedProfile,
            bool hasTechnical, int technicalScore, PlutoSqueezePhase phase, PlutoProfileZone zone,
            bool hasMicro, int microScore, PlutoDivergenceType divergence, bool cvdBuyDominant, bool bidWall, bool depthTrap,
            MarketRegime regime,
            int squeezeScore = 0, int cvdScore = 0, int depthScore = 0, int depthRatioBps = 0, int depthTiers = 0, long bid = 0)
        {
            Market = market; Symbol = symbol; Price = price;
            HasValuation = hasValuation; ValuationScore = valuationScore; MarginOfSafetyBps = marginOfSafetyBps; Grade = grade; FairPrice = fairPrice; SeedProfile = seedProfile;
            HasTechnical = hasTechnical; TechnicalScore = technicalScore; Phase = phase; Zone = zone;
            HasMicro = hasMicro; MicroScore = microScore; Divergence = divergence; CvdBuyDominant = cvdBuyDominant; BidWall = bidWall; DepthTrap = depthTrap;
            Regime = regime;
            SqueezeScore = squeezeScore; CvdScore = cvdScore; DepthScore = depthScore; DepthRatioBps = depthRatioBps; DepthTiers = Math.Max(0, depthTiers);
            Bid = Math.Max(0, bid);
        }

        /// <summary>[v12.18.79-DEV] 최우선 호가 스프레드 bp(매도 1호가 기준 · 미판독/역전 = −1).</summary>
        public int SpreadBps => Bid > 0 && Price >= Bid ? (int)Int128.Min((Int128)(Price - Bid) * 10000 / Price, int.MaxValue) : -1;

        /// <summary>기존 브레인 판독 → 스크리닝 입력(판독 무효 = 해당 팩터 Has* false · bid = 매수 1호가 · 0 = 미판독).</summary>
        public static PlutoScreenInputs From(PlutoScreenMarket market, long symbol, long price,
            in PlutoValuationTag val, in PlutoTechnicalFactor tech, in PlutoMicrostructureFactor micro, MarketRegime regime, long bid = 0)
        {
            bool hv = val.HasValuation, ht = tech.HasTechnical, hm = micro.HasMicrostructure;
            return new PlutoScreenInputs(market, symbol, price,
                hv, hv ? val.Score : 0, hv ? val.MarginOfSafetyBps : 0, hv ? val.Grade : PlutoValuationGrade.Unknown, hv ? val.Factor.FairPrice : 0,
                hv && val.Factor.Source == PlutoFundamentalSource.Seed,
                ht, ht ? tech.TechnicalScore : 0, ht ? tech.SqueezePhase : default, ht ? tech.ProfileZone : default,
                hm, hm ? micro.MicrostructureScore : 0, hm ? micro.DivergenceType : PlutoDivergenceType.None,
                hm && micro.IsCvdBuyDominant, hm && micro.HasBidSupportWall, hm && micro.IsDepthTrap,
                MarketRegimeText.IsDefined(regime) ? regime : MarketRegime.Unknown,
                ht ? tech.SqueezeScore : 0, hm ? micro.CvdScore : 0, hm ? micro.DepthScore : 0, hm ? micro.DepthRatioBps : 0, hm ? micro.DepthTiers : 0, bid);
        }
    }

    /// <summary>학습 스웜 최근접 과거 국면(없음 = default · Kind Unknown).</summary>
    public readonly struct PlutoScreenMacro
    {
        public readonly PlutoEpisodeKind Kind;
        public readonly int SimilarityPct;
        public readonly string? EpisodeName;
        public readonly bool EpisodeIsSeed;

        public PlutoScreenMacro(PlutoEpisodeKind kind, int similarityPct, string? episodeName, bool episodeIsSeed)
        {
            Kind = kind; SimilarityPct = Math.Clamp(similarityPct, 0, 100); EpisodeName = episodeName; EpisodeIsSeed = episodeIsSeed;
        }

        public bool HasMatch => Kind != PlutoEpisodeKind.Unknown && EpisodeName is not null;

        /// <summary>학습 스웜 상태 → 거시 입력(상태 없음 · 매칭 없음 = default).</summary>
        public static PlutoScreenMacro FromLearning(PlutoLearningStatus? s)
            => s is { Match.Found: true, Episode: { } ep } ? new PlutoScreenMacro(ep.Kind, s.Match.SimilarityPct, ep.Name, ep.IsSeed) : default;
    }

    /// <summary>종목 1건 종합 점수(값 타입).</summary>
    public readonly struct PlutoDiscoveryScore
    {
        public readonly int ValuationPts, TechnicalPts, MicroPts, MacroPts;
        public readonly PlutoScreenRisk Risks;
        public readonly int Coverage;

        public PlutoDiscoveryScore(int valuationPts, int technicalPts, int microPts, int macroPts, PlutoScreenRisk risks, int coverage)
        {
            ValuationPts = valuationPts; TechnicalPts = technicalPts; MicroPts = microPts; MacroPts = macroPts; Risks = risks; Coverage = coverage;
        }

        public int Total => ValuationPts + TechnicalPts + MicroPts + MacroPts;
        public int RiskCount => PlutoDiscoveryScorer.CountOf(Risks);
        /// <summary>악마의 변호인 탈락(반대 논리 ≥ 2).</summary>
        public bool Vetoed => RiskCount >= PlutoDiscoveryScorer.VetoRiskCount;
        /// <summary>판독 3종(안전마진 · 기술 · 수급) 전무.</summary>
        public bool Insufficient => Coverage == 0;
        public bool Recommendable => !Vetoed && !Insufficient && Total >= PlutoDiscoveryScorer.MinRecommendScore;
    }

    public static class PlutoDiscoveryScorer
    {
        public const int FactorMax = 25, VetoRiskCount = 2, MinRecommendScore = 50, MacroRiskOffSimilarityPct = 60;
        /// <summary>[v12.18.79-DEV] 코인 1번 게이트 — 최우선 스프레드 ≥ 50bp = 유동성 고갈(진입 가격 부담).</summary>
        public const int IlliquidSpreadBps = 50;
        /// <summary>[v12.18.79-DEV] 코인 raw 스케일(가격 원 × 10^8 · 수량 코인 × 10^8 — 업비트 아레나와 동일).</summary>
        public const long CryptoScale = 100_000_000;

        /// <summary>팩터 점수(−100~+100) → 0~25점(0 = 12점 · 범위 밖 = 클램프).</summary>
        public static int Points(int factorScore) => (Math.Clamp(factorScore, -100, 100) + 100) * FactorMax / 200;

        /// <summary>종목 국면 0~15점.</summary>
        public static int RegimePoints(MarketRegime r) => r switch
        {
            MarketRegime.TrendingBull => 15,
            MarketRegime.ChoppyRange => 8,
            MarketRegime.TrendingBear => 2,
            MarketRegime.PanicLiquidation => 0,
            _ => 5,
        };

        /// <summary>에피소딕 과거 국면 0~10점(강세/유동성 = 5 + 유사도 비례 · 약세/패닉 = 5 − 유사도 비례 · 박스권/판정 전 = 5).</summary>
        public static int EpisodePoints(in PlutoScreenMacro m) => !m.HasMatch ? 5 : m.Kind switch
        {
            PlutoEpisodeKind.Bull or PlutoEpisodeKind.Liquidity => 5 + 5 * m.SimilarityPct / 100,
            PlutoEpisodeKind.Bear or PlutoEpisodeKind.Panic => 5 - 5 * m.SimilarityPct / 100,
            _ => 5,
        };

        /// <summary>[v12.18.79-DEV] 코인 유동성 0~25점 = 스프레드(≤5bp 20 · ≤10 16 · ≤25 10 · &lt;50 4 · 그 외 0) + 뎁스 호가 수(≥15 5 · ≥10 3 · ≥5 1). 스프레드 미판독 = 0점.</summary>
        public static int LiquidityPoints(int spreadBps, int depthTiers)
        {
            if (spreadBps < 0) return 0;
            int sp = spreadBps <= 5 ? 20 : spreadBps <= 10 ? 16 : spreadBps <= 25 ? 10 : spreadBps < IlliquidSpreadBps ? 4 : 0;
            int dp = depthTiers >= 15 ? 5 : depthTiers >= 10 ? 3 : depthTiers >= 5 ? 1 : 0;
            return Math.Min(FactorMax, sp + dp);
        }

        public static int CountOf(PlutoScreenRisk r) => System.Numerics.BitOperations.PopCount((uint)(byte)r);

        public static PlutoScreenRisk RisksOf(in PlutoScreenInputs x, in PlutoScreenMacro m)
        {
            var r = PlutoScreenRisk.None;
            if (x.Market == PlutoScreenMarket.Crypto) { if (x.SpreadBps >= IlliquidSpreadBps) r |= PlutoScreenRisk.Overvalued; }
            else if (x.HasValuation && x.Grade is PlutoValuationGrade.Overvalued or PlutoValuationGrade.ExtremeBubble) r |= PlutoScreenRisk.Overvalued;
            if (x.HasTechnical && (x.Phase == PlutoSqueezePhase.BreakoutDown || x.Zone == PlutoProfileZone.PocResistance)) r |= PlutoScreenRisk.TechnicalBreakdown;
            if (x.HasMicro && x.Divergence == PlutoDivergenceType.Bearish) r |= PlutoScreenRisk.CvdBearish;
            if (x.HasMicro && x.DepthTrap) r |= PlutoScreenRisk.DepthTrap;
            if (x.Regime is MarketRegime.TrendingBear or MarketRegime.PanicLiquidation) r |= PlutoScreenRisk.BearRegime;
            if (m.HasMatch && m.Kind is PlutoEpisodeKind.Bear or PlutoEpisodeKind.Panic && m.SimilarityPct >= MacroRiskOffSimilarityPct) r |= PlutoScreenRisk.MacroRiskOff;
            return r;
        }

        public static PlutoDiscoveryScore Score(in PlutoScreenInputs x, in PlutoScreenMacro m)
        {
            if (x.Market == PlutoScreenMarket.Crypto) return ScoreCrypto(in x, in m);
            int coverage = (x.HasValuation ? 1 : 0) + (x.HasTechnical ? 1 : 0) + (x.HasMicro ? 1 : 0);
            return new PlutoDiscoveryScore(
                x.HasValuation ? Points(x.ValuationScore) : 0,
                x.HasTechnical ? Points(x.TechnicalScore) : 0,
                x.HasMicro ? Points(x.MicroScore) : 0,
                RegimePoints(x.Regime) + EpisodePoints(in m),
                RisksOf(in x, in m), coverage);
        }

        /// <summary>[v12.18.79-DEV] 코인 4대 팩터: 슬롯 = (유동성 · 볼밴 스퀴즈 · CVD 다이버전스 · 15호가 뎁스벽) — PlutoDiscoveryScore 필드 순서 재사용.</summary>
        private static PlutoDiscoveryScore ScoreCrypto(in PlutoScreenInputs x, in PlutoScreenMacro m)
        {
            int coverage = (x.HasTechnical ? 1 : 0) + (x.HasMicro ? 1 : 0);
            return new PlutoDiscoveryScore(
                LiquidityPoints(x.SpreadBps, x.HasMicro ? x.DepthTiers : 0),
                x.HasTechnical ? Points(x.SqueezeScore) : 0,
                x.HasMicro ? Points(x.CvdScore) : 0,
                x.HasMicro ? Points(x.DepthScore) : 0,
                RisksOf(in x, in m), coverage);
        }

        /// <summary>[v12.18.79-DEV] 시장별 반대 논리 문구(코인 1번 게이트 = 스프레드 과대).</summary>
        public static string RiskLabel(PlutoScreenRisk single, PlutoScreenMarket market)
            => market == PlutoScreenMarket.Crypto && single == PlutoScreenRisk.Overvalued ? "호가 스프레드 과대(유동성 고갈)" : RiskLabel(single);

        public static string RiskLabel(PlutoScreenRisk single) => single switch
        {
            PlutoScreenRisk.Overvalued => "고평가 경계(안전마진 음수)",
            PlutoScreenRisk.TechnicalBreakdown => "기술적 붕괴(볼밴 하단 이탈/POC 매물벽 저항)",
            PlutoScreenRisk.CvdBearish => "CVD 약세 다이버전스",
            PlutoScreenRisk.DepthTrap => "15호가 매도벽/허매수 함정",
            PlutoScreenRisk.BearRegime => "하락 추세/패닉 국면",
            PlutoScreenRisk.MacroRiskOff => "과거 약세 국면과 유사(거시 위험 회피)",
            _ => "반대 논리 없음",
        };

        /// <summary>악마의 변호인 판정 문구(콜드).</summary>
        public static string RiskText(PlutoScreenRisk r) => RiskText(r, PlutoScreenMarket.Krx);

        public static string RiskText(PlutoScreenRisk r, PlutoScreenMarket market)
        {
            int n = CountOf(r);
            if (n == 0) return "😈 악마의 변호인: 반대 논리 0건 — 통과";
            string list = "";
            for (int bit = 1; bit <= (int)PlutoScreenRisk.MacroRiskOff; bit <<= 1)
                if (((int)r & bit) != 0) list += (list.Length > 0 ? " · " : "") + RiskLabel((PlutoScreenRisk)bit, market);
            return n >= VetoRiskCount
                ? "😈 악마의 변호인: 반대 논리 " + n.ToString(CultureInfo.InvariantCulture) + "건 중첩 — 탈락(" + list + ")"
                : "😈 악마의 변호인: 반대 논리 1건 — 감시 통과(" + list + ")";
        }

        /// <summary>시장별 가격 표기(KRX 원 · US 센트 → 달러 · 코인 raw → 원 · 1,000원 미만 = 소수 4자리).</summary>
        public static string PriceText(PlutoScreenMarket market, long price) => market switch
        {
            PlutoScreenMarket.Us => "$" + (price / 100m).ToString("N2", CultureInfo.InvariantCulture),
            PlutoScreenMarket.Crypto => (price / (decimal)CryptoScale).ToString(price < 1000 * CryptoScale ? "#,##0.####" : "N0", CultureInfo.InvariantCulture) + "원",
            _ => price.ToString("N0", CultureInfo.InvariantCulture) + "원",
        };

        /// <summary>핵심 추천 근거 3줄(점수 높은 팩터 순 · 판독 있는 팩터만 · 동점 = 안전마진 → 기술 → 수급 → 거시).</summary>
        public static string[] Reasons(in PlutoScreenInputs x, in PlutoScreenMacro m, in PlutoDiscoveryScore s)
        {
            bool crypto = x.Market == PlutoScreenMarket.Crypto;
            Span<int> pts = stackalloc int[4] { crypto ? (x.SpreadBps >= 0 ? s.ValuationPts : -1) : x.HasValuation ? s.ValuationPts : -1,
                x.HasTechnical ? s.TechnicalPts : -1, x.HasMicro ? s.MicroPts : -1, crypto ? (x.HasMicro ? s.MacroPts : -1) : s.MacroPts };
            var outp = new string[3];
            int n = 0;
            Span<bool> used = stackalloc bool[4];
            while (n < 3)
            {
                int best = -1;
                for (int i = 0; i < 4; i++) if (!used[i] && pts[i] >= 0 && (best < 0 || pts[i] > pts[best])) best = i;
                if (best < 0) break;
                used[best] = true;
                outp[n++] = crypto ? CryptoReasonOf(best, in x, in s) : ReasonOf(best, in x, in m, in s);
            }
            for (; n < 3; n++) outp[n] = "판독 대기 팩터(워밍업 · 0점 반영)";
            return outp;
        }

        private static string CryptoReasonOf(int factor, in PlutoScreenInputs x, in PlutoDiscoveryScore s)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (factor)
            {
                case 0:
                    return "💧 유동성 스프레드 " + (x.SpreadBps / 100m).ToString("0.00", ci) + "% · 뎁스 " + x.DepthTiers.ToString(ci) + "호가 [" + s.ValuationPts.ToString(ci) + "/25]";
                case 1:
                    return "🌀 " + PlutoTechnicalText.PhaseLabel(x.Phase) + " · 스퀴즈 점수 " + (x.SqueezeScore >= 0 ? "+" : "") + x.SqueezeScore.ToString(ci) + " [" + s.TechnicalPts.ToString(ci) + "/25]";
                case 2:
                    return "📊 " + PlutoMicrostructureText.DivergenceLabel(x.Divergence) + (x.CvdBuyDominant ? " · CVD 매수우위" : "") + " [" + s.MicroPts.ToString(ci) + "/25]";
                default:
                    return "🧱 15호가 뎁스 비율 " + (x.DepthRatioBps >= 0 ? "+" : "") + (x.DepthRatioBps / 100m).ToString("0.0", ci) + "%"
                        + (x.BidWall ? " · 매수벽 지지" : "") + (x.DepthTrap ? " · 매도벽/허매수 주의" : "") + " [" + s.MacroPts.ToString(ci) + "/25]";
            }
        }

        private static string ReasonOf(int factor, in PlutoScreenInputs x, in PlutoScreenMacro m, in PlutoDiscoveryScore s)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (factor)
            {
                case 0:
                    return "💎 안전마진 " + (x.MarginOfSafetyBps >= 0 ? "+" : "") + (x.MarginOfSafetyBps / 100m).ToString("0.0", ci) + "% · " + ValuationText.Label(x.Grade)
                        + " · 적정가 " + PriceText(x.Market, x.FairPrice) + (x.SeedProfile ? "(시드 프로필)" : "") + " [" + s.ValuationPts.ToString(ci) + "/25]";
                case 1:
                    return "📐 " + PlutoTechnicalText.PhaseLabel(x.Phase) + " · " + PlutoTechnicalText.ZoneLabel(x.Zone) + " [" + s.TechnicalPts.ToString(ci) + "/25]";
                case 2:
                    return "📊 " + PlutoMicrostructureText.DivergenceLabel(x.Divergence) + (x.CvdBuyDominant ? " · CVD 매수우위" : "")
                        + (x.BidWall ? " · 매수벽 지지" : "") + (x.DepthTrap ? " · 매도벽/허매수 주의" : "") + " [" + s.MicroPts.ToString(ci) + "/25]";
                default:
                    return "🌐 종목 국면 " + MarketRegimeText.Label(x.Regime) + " · 과거 유사 국면 "
                        + (m.HasMatch ? m.EpisodeName + (m.EpisodeIsSeed ? "(시드 근사)" : "") + " " + m.SimilarityPct.ToString(ci) + "%" : "판정 전")
                        + " [" + s.MacroPts.ToString(ci) + "/25]";
            }
        }
    }
}
