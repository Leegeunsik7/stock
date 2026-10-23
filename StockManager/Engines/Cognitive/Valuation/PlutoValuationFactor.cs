// ============================================================================
// PlutoValuationFactor — v12.18.65-DEV: 플루토 브레인 2단계 — 종목 1개 밸류에이션 판정(불변 값 타입 · 정수 스케일 · 할당 0)
//
//   등급 매트릭스(안전마진 MoS = (적정가 − 현재가) / 현재가 · bp):
//     DeepValue     +70~+100 : MoS ≥ +30% ∧ Fwd PER < 업종 평균 ∧ ROE > 0(미충족 = FairDiscount 강등)
//     FairDiscount  +20~+69  : MoS +10% ~ +30% 미만
//     FairlyValued  −19~+19  : MoS −15% 초과 ~ +10% 미만 · 적자(Fwd EPS ≤ 0) 종목의 할인 판정 상한
//     Overvalued    −20~−59  : MoS −15% 이하 ~ −30% 초과(현재가 ≥ 적정가 × 1.18) → 아레나 신규 매수 보류
//     ExtremeBubble −60~−100 : MoS −30% 이하 ∨ (Fwd PER > 5년 밴드 상단 ∧ 선행 EPS 역성장) → 아레나 보유 조기 청산
//   점수 = 등급 구간 내 MoS 선형 보간(0 방향 절단) + 분기 영업이익 서프라이즈 가감(±10점 상한 · 등급 구간으로 포화) — 점수는 항상 등급 밴드 안.
//   PlutoValuationTag = 버퍼 판정 + 체결 시점 호가로 MoS · 배수 · 등급 · 점수 재판정(정수 산술만 · δ=0). 기준일 + 180일 경과 · 1일 넘는 미래 = 빈 태그(fail-closed).
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Valuation
{
    public enum PlutoValuationGrade : byte
    {
        Unknown = 0,
        DeepValue = 1,
        FairDiscount = 2,
        FairlyValued = 3,
        Overvalued = 4,
        ExtremeBubble = 5,
    }

    public static class ValuationText
    {
        public const int DeepValueMosBps = 3000, FairDiscountMosBps = 1000, OvervaluedMosBps = -1500, BubbleMosBps = -3000;
        public const int MaxSurpriseAdjust = 10;
        /// <summary>안전마진 bp 포화(±10000%).</summary>
        public const int MaxMosBps = 1_000_000;
        public static readonly long MaxAgeTicks = TimeSpan.FromDays(180).Ticks;
        public static readonly long MaxSkewTicks = TimeSpan.FromDays(1).Ticks;

        public static (int Low, int High) Band(PlutoValuationGrade g) => g switch
        {
            PlutoValuationGrade.DeepValue => (70, 100),
            PlutoValuationGrade.FairDiscount => (20, 69),
            PlutoValuationGrade.FairlyValued => (-19, 19),
            PlutoValuationGrade.Overvalued => (-59, -20),
            PlutoValuationGrade.ExtremeBubble => (-100, -60),
            _ => (0, 0),
        };

        public static bool InBand(PlutoValuationGrade g, int score)
        {
            var (lo, hi) = Band(g);
            return g != PlutoValuationGrade.Unknown && score >= lo && score <= hi;
        }

        public static string Label(PlutoValuationGrade g) => g switch
        {
            PlutoValuationGrade.DeepValue => "극심한 저평가",
            PlutoValuationGrade.FairDiscount => "합리적 저평가",
            PlutoValuationGrade.FairlyValued => "적정 주가",
            PlutoValuationGrade.Overvalued => "고평가 경계",
            PlutoValuationGrade.ExtremeBubble => "극심한 과열",
            _ => "미판정",
        };

        public static string SourceLabel(PlutoFundamentalSource s) => s switch
        {
            PlutoFundamentalSource.Seed => "시드 프로필(실 재무 피드 미결선)",
            PlutoFundamentalSource.Dart => "DART 재무",
            PlutoFundamentalSource.SecEdgar => "SEC EDGAR 재무",
            PlutoFundamentalSource.Manual => "수동 입력",
            _ => "출처 미상",
        };
    }

    /// <summary>현재가 기준 판독 결과(값 타입). Grade = Unknown ↔ 판정 불가.</summary>
    public readonly struct PlutoValuationReading
    {
        public readonly PlutoValuationGrade Grade;
        public readonly int Score;
        public readonly int MarginOfSafetyBps;
        /// <summary>Fwd PER ×10(Fwd EPS ≤ 0 = 0 · 산출 불가).</summary>
        public readonly int FwdPerX10;
        /// <summary>PBR ×100(BPS ≤ 0 = 0 · 산출 불가).</summary>
        public readonly int PbrX100;

        public PlutoValuationReading(PlutoValuationGrade grade, int score, int marginOfSafetyBps, int fwdPerX10, int pbrX100)
        {
            Grade = grade; Score = score; MarginOfSafetyBps = marginOfSafetyBps; FwdPerX10 = fwdPerX10; PbrX100 = pbrX100;
        }
    }

    public readonly struct PlutoValuationFactor
    {
        public readonly long Symbol;
        public readonly long Seq;
        /// <summary>재무 기준 시각(UTC) — 평가 시각이 아니다(만료 판정 기준).</summary>
        public readonly long TimestampUtcTicks;
        public readonly long EvaluatedPrice;
        public readonly long FairPrice;
        public readonly long FwdEps, Bps;
        public readonly int ValuationScore;
        public readonly int MarginOfSafetyBps;
        public readonly int FwdPerX10;
        public readonly int PbrX100;
        public readonly int RoeBps;
        public readonly int SectorFwdPerX10;
        public readonly int PerBandHighX10;
        public readonly int EpsGrowthBps;
        public readonly int SurpriseBps;
        public readonly PlutoValuationGrade Grade;
        public readonly PlutoMarket Market;
        public readonly PlutoFundamentalSource Source;

        public PlutoValuationFactor(in PlutoFundamentalProfile p, long fairPrice, long evaluatedPrice, in PlutoValuationReading r, long seq = 0)
        {
            Symbol = p.Symbol; Seq = seq; TimestampUtcTicks = p.AsOfUtcTicks;
            EvaluatedPrice = evaluatedPrice; FairPrice = fairPrice; FwdEps = p.FwdEps; Bps = p.Bps;
            ValuationScore = r.Score; MarginOfSafetyBps = r.MarginOfSafetyBps; FwdPerX10 = r.FwdPerX10; PbrX100 = r.PbrX100; Grade = r.Grade;
            RoeBps = p.RoeBps; SectorFwdPerX10 = p.SectorFwdPerX10; PerBandHighX10 = p.PerBandHighX10;
            EpsGrowthBps = p.EpsGrowthBps; SurpriseBps = p.SurpriseBps; Market = p.Market; Source = p.Source;
        }

        private PlutoValuationFactor(in PlutoValuationFactor src, long seq)
        {
            this = src;
            Seq = seq;
        }

        /// <summary>버퍼 게시 순번만 바꾼 사본.</summary>
        internal PlutoValuationFactor WithSeq(long seq) => new(in this, seq);

        /// <summary>현재가 기준 재판정(정수 산술 · 할당 0).</summary>
        public PlutoValuationReading ReadAt(long price) =>
            PlutoValuationMath.Assess(FairPrice, price, FwdEps, Bps, RoeBps, SectorFwdPerX10, PerBandHighX10, EpsGrowthBps, SurpriseBps);

        /// <summary>무결성: 식별 · 시각 · 가격 양수 · 등급 판정 · 점수가 등급 밴드 안 · 저장 판독 = 평가가 재판정(위조/찢김 검출).</summary>
        public bool IsWellFormed
        {
            get
            {
                if (Symbol <= 0 || TimestampUtcTicks <= 0 || EvaluatedPrice <= 0 || FairPrice <= 0 || Market == PlutoMarket.Unknown) return false;
                if (!ValuationText.InBand(Grade, ValuationScore)) return false;
                var r = ReadAt(EvaluatedPrice);
                return r.Grade == Grade && r.Score == ValuationScore && r.MarginOfSafetyBps == MarginOfSafetyBps
                    && r.FwdPerX10 == FwdPerX10 && r.PbrX100 == PbrX100;
            }
        }
    }

    /// <summary>
    /// 아레나 체결 시점에 판독된 밸류에이션(근거 카드 결속용 · 값 타입). default = 판정 없음.
    /// 판독 = 버퍼 판정의 적정가/EPS/BPS 를 체결 호가로 재판정(MoS · 배수 · 등급 · 점수). 만료 · 과도한 미래 · 무효 가격 = 빈 태그.
    /// </summary>
    public readonly struct PlutoValuationTag
    {
        /// <summary>점수 1점 = 시그널 100bp(±100 → ±10000bp).</summary>
        public const int BpPerPoint = 100;

        public readonly PlutoValuationFactor Factor;
        public readonly long Price;
        public readonly PlutoValuationReading Reading;

        public PlutoValuationTag(in PlutoValuationFactor factor, long price, long nowUtcTicks)
        {
            long age = nowUtcTicks - factor.TimestampUtcTicks;
            if (factor.Symbol <= 0 || factor.FairPrice <= 0 || price <= 0 || nowUtcTicks <= 0
                || age < -ValuationText.MaxSkewTicks || age > ValuationText.MaxAgeTicks)
            {
                this = default;
                return;
            }
            var r = factor.ReadAt(price);
            if (r.Grade == PlutoValuationGrade.Unknown)
            {
                this = default;
                return;
            }
            Factor = factor;
            Price = price;
            Reading = r;
        }

        public bool HasValuation => Factor.Symbol != 0;
        public PlutoValuationGrade Grade => Reading.Grade;
        public int Score => Reading.Score;
        public int MarginOfSafetyBps => Reading.MarginOfSafetyBps;
        public int ValuationBp => Reading.Score * BpPerPoint;
        /// <summary>고평가 경계 이상 = 신규 매수 보류.</summary>
        public bool BlocksEntry => Reading.Grade is PlutoValuationGrade.Overvalued or PlutoValuationGrade.ExtremeBubble;
        /// <summary>극심한 과열 = 보유 조기 청산.</summary>
        public bool IsBubble => Reading.Grade == PlutoValuationGrade.ExtremeBubble;

        /// <summary>무결성: 판정 없음(종목 0 · 판독 0) 또는 판정 무결 ∧ 가격 양수 ∧ 판독 = 재판정 ∧ 점수가 등급 밴드 안.</summary>
        public bool IsWellFormed
        {
            get
            {
                if (Factor.Symbol == 0)
                    return Price == 0 && Reading.Grade == PlutoValuationGrade.Unknown && Reading.Score == 0 && Reading.MarginOfSafetyBps == 0;
                if (Price <= 0 || !Factor.IsWellFormed || !ValuationText.InBand(Reading.Grade, Reading.Score)) return false;
                var r = Factor.ReadAt(Price);
                return r.Grade == Reading.Grade && r.Score == Reading.Score && r.MarginOfSafetyBps == Reading.MarginOfSafetyBps
                    && r.FwdPerX10 == Reading.FwdPerX10 && r.PbrX100 == Reading.PbrX100;
            }
        }
    }
}
