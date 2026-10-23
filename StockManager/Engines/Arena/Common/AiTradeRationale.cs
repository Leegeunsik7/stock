// ============================================================================
// AiTradeRationale — v12.18.59-DEV: 설명 가능한 AI 매매 근거(불변 · 아레나 모드 공용)
//
//   AI 진영의 모든 가상 체결 1건 = 근거 1건. 단순 시그널이 아니라 진입/청산 사유(TriggerType) · 근거 구성요소
//   (야간 글로벌 · 호가 불균형 · 거래량 급증 · 기술적 모멘텀, 각 bp) · 감성 점수 · 확신도(0~100) · 목표가/손절가 · 손익비를 함께 싣는다.
//   ★δ=0: 필드는 전부 값 타입(핫패스 체결 루프에서 구조체 복사만). 한국어 요약(HumanReadableSummary)은 판독 시점에 조립하는
//     콜드 경로(UI/어댑터 전용 — 체결 루프는 호출하지 않는다).
//   ★liveArmed:false — 이 타입은 기록일 뿐 주문 수단이 아니다.
//   [v12.18.61-DEV] 통화(Currency): Krw = 가격 원 · Usd = 가격 센트(미국 테크 아레나). Usd 모드 구성요소 슬롯 =
//     SurgeBp ← QQQ/반도체 ETF 베타 · MacroBp ← 10년물 국채금리 감응 · ImbalanceBp ← 프리마켓 갭/수급 · MomentumBp ← 장중 모멘텀.
//   [v12.18.63-DEV] 플루토 뉴스 촉매(News · PlutoNewsTag 값 타입): 체결 시점에 판독한 종목 뉴스 판정(유형 · 원점수 · 감쇄 · 실효 · 64B 헤드라인).
//     진입 사유 NewsCatalyst(12 · 뉴스 기여 최대) · 청산 사유 NewsShock(13 · 치명적 구조적 악재 긴급 청산). 요약 끝에 플루토 진단 문장 결속.
//   [v12.18.65-DEV] 플루토 밸류에이션(Valuation · PlutoValuationTag 값 타입): 체결 호가로 재판정한 등급 · 점수 · 안전마진 · Fwd PER · PBR · 적정가.
//     진입 사유 ValuationDiscount(14 · 밸류에이션 기여 최대) · 청산 사유 ValuationBubble(15 · 극심한 과열 조기 청산). 요약 끝에 재무 근거 문장 결속.
//   [v12.18.66-DEV] 통화 Crypto(업비트 가상자산 아레나): 가격 = 원 raw 10^8 · 수량 = 코인 raw 10^8(1 = 0.00000001) · 손익 = 원 raw 10^8.
//     구성요소 슬롯 = ImbalanceBp ← L2 오더북 불균형 · MomentumBp ← 24시간 체결 모멘텀 · SurgeBp ← 테이커 체결 강도 · MacroBp ← 김치프리미엄/변동성 감응.
//     진입 사유 TradeFlowMomentum(16) · KimchiVolatility(17)(오더북 불균형 주도 = OrderbookImbalance 공용). 오더북 두께(BidDepthWon · AskDepthWon = 최우선 잔량 원화 환산) 결속.
//   [v12.18.67-DEV] 플루토 거버넌스(Governance · PlutoGovernanceTag 값 타입): 진입 = 국면 뱃지 · 악마의 변호인 감점 내역 · 원/최종 확신도 · 동적 비중 ·
//     청산 = 청산 국면 · 연패 · 자가 회고(PlutoPostMortem · 진입 근거 순번 1:1). 진입 ConfidenceScore = 감점 후 확신도. 요약 끝에 국면/감점/회고 문장 결속.
//   [v12.18.70-DEV] 청산 진입가(EntryPrice): 매도 근거 = 청산 직전 포지션 평균단가(가격 단위 동일 · 매수 = 0) — UI 실현손익 체결 내역(진입가 → 청산가 · 수익률).
// ============================================================================
using System;
using System.Globalization;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.News;
using StockManager.Engines.Cognitive.Valuation;

namespace StockManager.Engines.Arena.Common
{
    /// <summary>AI 매매 사유. 1~4 = 진입 주도 근거(시그널 구성요소 중 최대 기여) · 5~8 = 청산 사유.</summary>
    public enum ArenaTrigger : byte
    {
        None = 0,
        OrderbookImbalance = 1,
        VolumeSurge = 2,
        MacroTailwind = 3,
        MomentumBreakout = 4,
        TakeProfit = 5,
        StopLoss = 6,
        SignalReversal = 7,
        SessionClose = 8,
        EtfBeta = 9,               // [v12.18.61-DEV] 미국 테크 진입 주도 근거(9~11 · 장중 모멘텀 = MomentumBreakout 공용)
        RateSensitivity = 10,
        PremarketGapFlow = 11,
        NewsCatalyst = 12,         // [v12.18.63-DEV] 플루토 뉴스/공시 촉매 주도 진입
        NewsShock = 13,            // [v12.18.63-DEV] 플루토 치명적 구조적 악재 긴급 청산
        ValuationDiscount = 14,    // [v12.18.65-DEV] 플루토 밸류에이션 저평가 주도 진입
        ValuationBubble = 15,      // [v12.18.65-DEV] 플루토 극심한 과열 조기 청산
        TradeFlowMomentum = 16,    // [v12.18.66-DEV] 업비트 24시간 체결 모멘텀 주도 진입
        KimchiVolatility = 17,     // [v12.18.66-DEV] 김치프리미엄/변동성 감응 주도 진입
        OpeningGapFill = 18,       // [v12.18.71-DEV] 시초가 갭 하락 메우기(개장 창 · 매수 수급 우위) 진입
    }

    /// <summary>[v12.18.61-DEV] 근거 가격 통화(Krw = 원 · Usd = 센트).</summary>
    public enum ArenaCurrency : byte
    {
        Krw = 0,
        Usd = 1,
        Crypto = 2,                // [v12.18.66-DEV] 원 raw 10^8 · 코인 수량 raw 10^8
    }

    public static class ArenaTriggerText
    {
        public static bool IsEntry(ArenaTrigger t) => t is >= ArenaTrigger.OrderbookImbalance and <= ArenaTrigger.MomentumBreakout
                                                          or >= ArenaTrigger.EtfBeta and <= ArenaTrigger.PremarketGapFlow
                                                          or ArenaTrigger.NewsCatalyst or ArenaTrigger.ValuationDiscount
                                                          or ArenaTrigger.TradeFlowMomentum or ArenaTrigger.KimchiVolatility
                                                          or ArenaTrigger.OpeningGapFill;
        public static bool IsExit(ArenaTrigger t) => t is >= ArenaTrigger.TakeProfit and <= ArenaTrigger.SessionClose or ArenaTrigger.NewsShock
                                                         or ArenaTrigger.ValuationBubble;

        public static string Label(ArenaTrigger t) => t switch
        {
            ArenaTrigger.OrderbookImbalance => "호가 수급 불균형",
            ArenaTrigger.VolumeSurge => "거래량 급증",
            ArenaTrigger.MacroTailwind => "야간 글로벌 지표",
            ArenaTrigger.MomentumBreakout => "기술적 모멘텀 돌파",
            ArenaTrigger.TakeProfit => "목표가 도달(익절)",
            ArenaTrigger.StopLoss => "손절가 이탈(손절)",
            ArenaTrigger.SignalReversal => "시그널 반전",
            ArenaTrigger.SessionClose => "정규장 마감 전 청산",
            ArenaTrigger.EtfBeta => "QQQ/반도체 ETF 베타",
            ArenaTrigger.RateSensitivity => "10년물 국채금리 감응",
            ArenaTrigger.PremarketGapFlow => "프리마켓 갭/수급",
            ArenaTrigger.NewsCatalyst => "플루토 뉴스/공시 촉매",
            ArenaTrigger.NewsShock => "플루토 구조적 악재 긴급 청산",
            ArenaTrigger.ValuationDiscount => "플루토 밸류에이션 저평가",
            ArenaTrigger.ValuationBubble => "플루토 극심한 과열 조기 청산",
            ArenaTrigger.TradeFlowMomentum => "24시간 체결 모멘텀",
            ArenaTrigger.KimchiVolatility => "김치프리미엄/변동성 감응",
            ArenaTrigger.OpeningGapFill => "시초가 갭 메우기",
            _ => "미분류",
        };
    }

    /// <summary>
    /// AI 가상 체결 1건의 설명 가능한 근거. Side +1 매수(진입) · −1 매도(청산). 가격 = 원 · 구성요소/감성 = bp(−10000~10000).
    /// 청산 근거의 Target/StopLoss/RiskReward 는 해당 포지션 진입 시 값(사후 변경 0).
    /// </summary>
    public readonly struct AiTradeRationale
    {
        public readonly long Seq;
        public readonly long Symbol;
        public readonly int Side;
        public readonly long Qty;
        public readonly long PriceWon;
        public readonly long KstTicks;
        public readonly ArenaTrigger TriggerType;
        public readonly int SentimentScore;
        public readonly int ConfidenceScore;
        public readonly long TargetPrice;
        public readonly long StopLossPrice;
        public readonly int RiskRewardX100;
        public readonly int MacroBp, ImbalanceBp, SurgeBp, MomentumBp;
        public readonly long RealizedPnlWon;
        public readonly ArenaCurrency Currency;
        /// <summary>[v12.18.63-DEV] 체결 시점 플루토 뉴스 촉매(default = 뉴스 없음).</summary>
        public readonly PlutoNewsTag News;
        /// <summary>[v12.18.65-DEV] 체결 시점 플루토 밸류에이션(default = 판정 없음).</summary>
        public readonly PlutoValuationTag Valuation;
        /// <summary>[v12.18.66-DEV] 체결 시점 최우선 매수/매도 잔량의 원화 환산(원 · Crypto 전용 · 그 밖 = 0).</summary>
        public readonly long BidDepthWon, AskDepthWon;
        /// <summary>[v12.18.67-DEV] 체결 시점 플루토 거버넌스(국면 · 감점 · 비중 · 회고 · default = 미가동).</summary>
        public readonly PlutoGovernanceTag Governance;
        /// <summary>[v12.18.70-DEV] 청산 근거 = 청산 직전 포지션 평균단가(가격 단위 동일) · 매수 근거 = 0.</summary>
        public readonly long EntryPrice;

        public AiTradeRationale(long seq, long symbol, int side, long qty, long priceWon, long kstTicks, ArenaTrigger triggerType,
            int sentimentScore, int confidenceScore, long targetPrice, long stopLossPrice, int riskRewardX100,
            int macroBp, int imbalanceBp, int surgeBp, int momentumBp, long realizedPnlWon, ArenaCurrency currency = ArenaCurrency.Krw,
            in PlutoNewsTag news = default, in PlutoValuationTag valuation = default, long bidDepthWon = 0, long askDepthWon = 0,
            in PlutoGovernanceTag governance = default, long entryPrice = 0)
        {
            EntryPrice = entryPrice;
            Governance = governance;
            BidDepthWon = bidDepthWon; AskDepthWon = askDepthWon;
            Currency = currency;
            News = news;
            Valuation = valuation;
            Seq = seq; Symbol = symbol; Side = side; Qty = qty; PriceWon = priceWon; KstTicks = kstTicks; TriggerType = triggerType;
            SentimentScore = sentimentScore; ConfidenceScore = confidenceScore; TargetPrice = targetPrice; StopLossPrice = stopLossPrice;
            RiskRewardX100 = riskRewardX100; MacroBp = macroBp; ImbalanceBp = imbalanceBp; SurgeBp = surgeBp; MomentumBp = momentumBp;
            RealizedPnlWon = realizedPnlWon;
        }

        public double RiskRewardRatio => RiskRewardX100 / 100.0;
        public DateTime AtKst => new(KstTicks, DateTimeKind.Unspecified);

        /// <summary>
        /// 근거 무결성: 식별/수량/가격 양수 · 사유가 방향과 일치(매수=진입 사유 · 매도=청산 사유) · 확신도 0~100 · 감성 ±10000 ·
        /// 손절가 &lt; 목표가 · 손익비 &gt; 0 · 매수는 손절가 &lt; 체결가 &lt; 목표가.
        /// [v12.18.63-DEV] 뉴스 태그 무결 · 뉴스 종목 = 체결 종목(뉴스 있을 때) · NewsCatalyst = 실효 양수 · NewsShock = 충격 수준 악재.
        /// [v12.18.65-DEV] 밸류에이션 태그 무결 · 판정 종목 = 체결 종목 · ValuationDiscount = 점수 양수 · ValuationBubble = 극심한 과열.
        /// [v12.18.67-DEV] 거버넌스 태그 무결 · 가동 시 매수 = 회고 없음 ∧ 확신도 = 감점 후 확신도 · 매도 = 회고 있음 ∧ 회고 청산 사유 = 체결 사유.
        /// </summary>
        public bool IsWellFormed =>
            Seq > 0 && Symbol > 0 && Qty > 0 && PriceWon > 0 && KstTicks > 0
            && (Side == 1 ? ArenaTriggerText.IsEntry(TriggerType) : Side == -1 && ArenaTriggerText.IsExit(TriggerType))
            && ConfidenceScore is >= 0 and <= 100
            && SentimentScore is >= -10000 and <= 10000
            && StopLossPrice > 0 && StopLossPrice < TargetPrice && RiskRewardX100 > 0
            && (Side != 1 || (StopLossPrice < PriceWon && PriceWon < TargetPrice))
            && News.IsWellFormed && (News.Factor.Symbol == 0 || News.Factor.Symbol == Symbol)
            && (TriggerType != ArenaTrigger.NewsCatalyst || News.EffectiveScore > 0)
            && (TriggerType != ArenaTrigger.NewsShock || News.IsShock)
            && Valuation.IsWellFormed && (Valuation.Factor.Symbol == 0 || Valuation.Factor.Symbol == Symbol)
            && (TriggerType != ArenaTrigger.ValuationDiscount || Valuation.Score > 0)
            && (TriggerType != ArenaTrigger.ValuationBubble || Valuation.IsBubble)
            && BidDepthWon >= 0 && AskDepthWon >= 0
            && Governance.IsWellFormed
            && (!Governance.Governed || (Side == 1
                ? !Governance.PostMortem.HasReview && Governance.Confidence == ConfidenceScore
                : Governance.PostMortem.HasReview && Governance.PostMortem.ExitTrigger == TriggerType));

        /// <summary>한국어 설명 요약(콜드 경로 — 호출마다 문자열 조립).</summary>
        public string HumanReadableSummary => AiRationaleText.Compose(in this);
    }

    public static class AiRationaleText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private static string Signed(long v) => (v > 0 ? "+" : "") + v.ToString("N0", Inv);

        private static string Usd(long cents) => string.Create(Inv, $"${cents / 100:N0}.{Math.Abs(cents % 100):D2}");
        private static string SignedUsd(long cents) => (cents > 0 ? "+" : cents < 0 ? "-" : "") + Usd(Math.Abs(cents));

        public static string Compose(in AiTradeRationale r)
        {
            string core = r.Currency switch
            {
                ArenaCurrency.Usd => ComposeUsd(in r),
                ArenaCurrency.Crypto => ComposeCrypto(in r),
                _ => ComposeKrw(in r),
            };
            if (r.News.HasCatalyst) core += " " + ComposeNews(in r.News);
            if (r.Valuation.HasValuation) core += " " + ComposeValuation(in r.Valuation);
            return r.Governance.Governed ? core + " " + PlutoGovernanceText.Compose(in r.Governance) : core;
        }

        /// <summary>[v12.18.65-DEV] 플루토 밸류에이션 재무 근거 문장(가격 = 판정 시장 통화).</summary>
        public static string ComposeValuation(in PlutoValuationTag v)
        {
            var f = v.Factor;
            var r = v.Reading;
            string fair = f.Market == PlutoMarket.Us ? Usd(f.FairPrice) : string.Create(Inv, $"{f.FairPrice:N0}원");
            string per;
            if (r.FwdPerX10 <= 0) per = "Fwd PER 산출 불가(선행 적자)";
            else
            {
                long gapPct = (long)(f.SectorFwdPerX10 - r.FwdPerX10) * 100 / f.SectorFwdPerX10;
                string rel = gapPct > 0 ? $"{gapPct.ToString(Inv)}% 할인" : gapPct < 0 ? $"{(-gapPct).ToString(Inv)}% 프리미엄" : "동일";
                per = $"Fwd PER {X10(r.FwdPerX10)}배(업종 평균 {X10(f.SectorFwdPerX10)}배 대비 {rel})";
            }
            string pbr = r.PbrX100 > 0 ? $"PBR {(r.PbrX100 / 100).ToString(Inv)}.{(r.PbrX100 % 100).ToString("D2", Inv)}배" : "PBR 산출 불가";
            string surprise = f.SurpriseBps > 0 ? $"분기 영업이익 컨센서스 {Pct(f.SurpriseBps)} 상회"
                            : f.SurpriseBps < 0 ? $"분기 영업이익 컨센서스 {Pct(f.SurpriseBps)} 하회" : "분기 영업이익 컨센서스 부합";
            return $"플루토 밸류에이션: {ValuationText.Label(r.Grade)} {Signed(r.Score)}점 · {per} · {pbr} · ROE {Pct(f.RoeBps)} · " +
                   $"안전마진 {Pct(r.MarginOfSafetyBps)}(적정가 {fair}) · {surprise} · 선행 EPS {Pct(f.EpsGrowthBps)} · {ValuationText.SourceLabel(f.Source)}.";
        }

        private static string X10(int v) => $"{(v / 10).ToString(Inv)}.{(v % 10).ToString(Inv)}";

        /// <summary>bp → 부호 있는 % 소수 1자리(0 방향 절단).</summary>
        private static string Pct(int bps)
        {
            long a = Math.Abs((long)bps);
            return (bps > 0 ? "+" : bps < 0 ? "-" : "") + $"{(a / 100).ToString(Inv)}.{(a % 100 / 10).ToString(Inv)}%";
        }

        /// <summary>[v12.18.63-DEV] 플루토 뉴스 촉매 진단 문장.</summary>
        public static string ComposeNews(in PlutoNewsTag n)
        {
            var f = n.Factor;
            return $"플루토 뉴스 촉매: {CatalystText.Label(f.Category)} 실효 {Signed(n.EffectiveScore)}점(원점수 {Signed(f.Score)} · 감쇄 후 {Signed(n.DecayedScore)} · " +
                   $"확신도 {f.Confidence}/100 · {n.AgeMinutes.ToString(Inv)}분 경과 · 반감기 {CatalystText.HalfLifeMinutes(f.Category).ToString(Inv)}분 · {CatalystText.SourceLabel(f.Source)}) \"{f.Headline}\".";
        }

        private static string ComposeKrw(in AiTradeRationale r)
        {
            string head = string.Create(Inv, $"[AI {(r.Side > 0 ? "매수" : "매도")}] {r.Symbol:D6} {r.Qty:N0}주 @ {r.PriceWon:N0}원");
            string factors = $"야간 글로벌 {Signed(r.MacroBp)}bp · 호가 불균형 {Signed(r.ImbalanceBp)}bp · 거래량 급증 {Signed(r.SurgeBp)}bp · 기술적 모멘텀 {Signed(r.MomentumBp)}bp";
            string plan = string.Create(Inv, $"목표가 {r.TargetPrice:N0}원 · 손절가 {r.StopLossPrice:N0}원 · 손익비 {r.RiskRewardRatio:F2}");
            string label = ArenaTriggerText.Label(r.TriggerType);
            return r.Side > 0
                ? $"{head} — 주도 근거: {label}. {factors} → 감성 {Signed(r.SentimentScore)} · 확신도 {r.ConfidenceScore}/100. {plan}."
                : $"{head} — 청산 사유: {label}. 실현손익 {Signed(r.RealizedPnlWon)}원 · {factors} → 감성 {Signed(r.SentimentScore)} · 확신도 {r.ConfidenceScore}/100. 진입 계획: {plan}.";
        }

        /// <summary>[v12.18.61-DEV] 미국 테크 근거 요약(가격 = 센트 → $ 표기 · 종목 = 티커(거래 계열 거래소 코드)).</summary>
        private static string ComposeUsd(in AiTradeRationale r)
        {
            string head = string.Create(Inv, $"[AI {(r.Side > 0 ? "매수" : "매도")}] {StockManager.Engines.Arena.Us.UsTechUniverse.Label(r.Symbol)} {r.Qty:N0}주 @ {Usd(r.PriceWon)}");
            string factors = $"QQQ/반도체 ETF 베타 {Signed(r.SurgeBp)}bp · 10년물 금리 감응 {Signed(r.MacroBp)}bp · 프리마켓 갭/수급 {Signed(r.ImbalanceBp)}bp · 장중 모멘텀 {Signed(r.MomentumBp)}bp";
            string plan = string.Create(Inv, $"목표가 {Usd(r.TargetPrice)} · 손절가 {Usd(r.StopLossPrice)} · 손익비 {r.RiskRewardRatio:F2}");
            string label = ArenaTriggerText.Label(r.TriggerType);
            return r.Side > 0
                ? $"{head} — 주도 근거: {label}. {factors} → 감성 {Signed(r.SentimentScore)} · 확신도 {r.ConfidenceScore}/100. {plan}."
                : $"{head} — 청산 사유: {label}. 실현손익 {SignedUsd(r.RealizedPnlWon)} · {factors} → 감성 {Signed(r.SentimentScore)} · 확신도 {r.ConfidenceScore}/100. 진입 계획: {plan}.";
        }

        /// <summary>[v12.18.66-DEV] 원 raw 10^8 → "152,345,000원" · 원 미만 = 소수(최대 8자리 · 끝 0 제거).</summary>
        public static string WonRaw(long raw)
        {
            const long Scale = 100_000_000L;
            long a = Math.Abs(raw);
            string won = (a / Scale).ToString("N0", Inv);
            long frac = a % Scale;
            string f = frac == 0 ? "" : "." + frac.ToString("D8", Inv).TrimEnd('0');
            return (raw < 0 ? "-" : "") + won + f + "원";
        }

        private static string SignedWonRaw(long raw) => (raw > 0 ? "+" : "") + WonRaw(raw);

        /// <summary>[v12.18.66-DEV] 코인 수량 raw 10^8 → "0.01234567"(끝 0 제거).</summary>
        public static string CoinQty(long raw)
        {
            const long Scale = 100_000_000L;
            long a = Math.Abs(raw);
            long frac = a % Scale;
            string f = frac == 0 ? "" : "." + frac.ToString("D8", Inv).TrimEnd('0');
            return (raw < 0 ? "-" : "") + (a / Scale).ToString("N0", Inv) + f;
        }

        /// <summary>[v12.18.66-DEV] 업비트 가상자산 근거 요약(가격 = 원 raw 10^8 · 수량 = 코인 raw 10^8 · 오더북 두께 문장 결속).</summary>
        private static string ComposeCrypto(in AiTradeRationale r)
        {
            string market = StockManager.Engines.Arena.Crypto.CryptoArenaUniverse.Label(r.Symbol);
            string coin = StockManager.Engines.Arena.Crypto.CryptoArenaUniverse.CoinOf(r.Symbol);
            string head = $"[AI {(r.Side > 0 ? "매수" : "매도")}] {market} {CoinQty(r.Qty)} {coin} @ {WonRaw(r.PriceWon)}";
            string factors = $"L2 오더북 불균형 {Signed(r.ImbalanceBp)}bp · 24시간 체결 모멘텀 {Signed(r.MomentumBp)}bp(테이커 체결 강도 {Signed(r.SurgeBp)}bp) · 김치프리미엄/변동성 감응 {Signed(r.MacroBp)}bp";
            string plan = string.Create(Inv, $"목표가 {WonRaw(r.TargetPrice)} · 손절가 {WonRaw(r.StopLossPrice)} · 손익비 {r.RiskRewardRatio:F2}");
            long depth = r.BidDepthWon + r.AskDepthWon;
            string thick = depth <= 0 ? "오더북 두께 정보 없음"
                : string.Create(Inv, $"오더북 두께: 매수1 잔량 {r.BidDepthWon:N0}원 vs 매도1 잔량 {r.AskDepthWon:N0}원(매수 비중 {r.BidDepthWon * 100 / depth}%)");
            string label = ArenaTriggerText.Label(r.TriggerType);
            return r.Side > 0
                ? $"{head} — 주도 근거: {label}. {factors} → 감성 {Signed(r.SentimentScore)} · 확신도 {r.ConfidenceScore}/100. {plan}. {thick}."
                : $"{head} — 청산 사유: {label}. 실현손익 {SignedWonRaw(r.RealizedPnlWon)} · {factors} → 감성 {Signed(r.SentimentScore)} · 확신도 {r.ConfidenceScore}/100. 진입 계획: {plan}. {thick}.";
        }
    }
}
