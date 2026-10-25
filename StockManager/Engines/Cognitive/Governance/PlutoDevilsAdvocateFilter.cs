// ============================================================================
// PlutoDevilsAdvocateFilter — v12.18.67-DEV: 플루토 브레인 고도화 — 악마의 변호인(매수 시그널 반대 논리 3종 + [v12.18.73-DEV] POC 매물벽 = 4종 + [v12.18.75-DEV] CVD 약세 다이버전스 · 뎁스 함정 = 6종 · 확증 편향 차단 · δ=0)
//
//   매수 시그널마다 '사지 말아야 할 이유'를 강제로 검증하고 확신도(0~100)를 감점한다.
//     ① 재료 선반영/고점 과열(Overheat −35): RSI > 70 ∧ 이격도 > 108%.
//        ※ 이격도 = 중간가 / 느린 EWMA(α=1/32 틱) — 일봉 피드 미결선이라 '5일 이격도'의 틱 기반 근사(정직 표기).
//     ② 상단 매도 호가벽(OverheadSupplyWall −20): 매도1 잔량 ≥ 매수1 잔량 × 3(매수1 잔량 0 이면 매도1 잔량 > 0).
//     ③ 거시 역풍(MacroHeadwind −15): 미 10년물 금리 변화 ≥ +10bp ∨ 달러 인덱스 변화 ≥ +50bp(+0.5%).
//     ④ [v12.18.73-DEV] POC 매물벽 저항(PocResistance −20): 현재가 바로 위(구간 폭 2.5배 이내)에 최대 매물대(POC) — PlutoVolumeProfileScanner 판정.
//     ⑤ [v12.18.75-DEV] CVD 약세 다이버전스(CvdBearishDivergence −25): 가격 상승 ∧ 체결강도 누적 델타 급락 — PlutoCumulativeVolumeDelta 판정.
//     ⑥ [v12.18.75-DEV] 10호가 뎁스 함정(DepthTrap −20): 상단 매도벽 ∨ 하단 허매수(원거리 미끼 물량) — PlutoOrderbookDepthAnalyzer 판정(3호가 이상 제공 시).
//   판정: 확신도 = 원확신도 + 감점 합(0 하한). 차단(Blocked) = 감점 후 확신도 < 30(진입 최소 확신도) ∨ 반대 논리 2종 이상(독립 반증 다수 = 거부권).
//   ★δ=0: Assess 힙 할당 0(값 타입 · 정수 비교만).
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Governance
{
    /// <summary>반대 논리(비트 플래그 · 복수 충족 가능).</summary>
    [Flags]
    public enum CounterThesis : byte
    {
        None = 0,
        Overheat = 1,
        OverheadSupplyWall = 2,
        MacroHeadwind = 4,
        PocResistance = 8,         // [v12.18.73-DEV] 상단 최대 매물대(POC) 저항
        CvdBearishDivergence = 16, // [v12.18.75-DEV] 가격 상승 ∧ CVD 급락
        DepthTrap = 32,            // [v12.18.75-DEV] 15호가(v12.18.76) 매도벽 ∨ 허매수
    }

    /// <summary>악마의 변호인 판정(값 타입).</summary>
    public readonly struct PlutoCounterThesisVerdict
    {
        public readonly CounterThesis Theses;
        /// <summary>감점 합(≤ 0).</summary>
        public readonly int Penalty;
        public readonly int RawConfidence, Confidence;
        public readonly bool Blocked;

        public PlutoCounterThesisVerdict(CounterThesis theses, int penalty, int rawConfidence, int confidence, bool blocked)
        {
            Theses = theses; Penalty = penalty; RawConfidence = rawConfidence; Confidence = confidence; Blocked = blocked;
        }

        public int Count => PlutoDevilsAdvocateFilter.CountOf(Theses);
    }

    public static class PlutoDevilsAdvocateFilter
    {
        public const int OverheatRsi = 70, OverheatDisparityX100 = 10800;
        public const int WallRatio = 3;
        public const int RateShockBp = 10, DollarStrengthBp = 50;
        public const int OverheatPenalty = -35, WallPenalty = -20, MacroPenalty = -15, PocPenalty = -20, CvdBearishPenalty = -25, DepthTrapPenalty = -20;
        public const int MinEntryConfidence = 30, VetoThesisCount = 2;

        public static bool IsOverheated(int rsi, int disparityX100) => rsi > OverheatRsi && disparityX100 > OverheatDisparityX100;

        public static bool IsSupplyWall(long bidQty, long askQty) =>
            askQty > 0 && bidQty >= 0 && (Int128)askQty >= (Int128)bidQty * WallRatio;

        public static bool IsMacroHeadwind(int us10yChangeBp, int dollarIndexChangeBp) => us10yChangeBp >= RateShockBp || dollarIndexChangeBp >= DollarStrengthBp;

        public static int PenaltyOf(CounterThesis t) => t switch
        {
            CounterThesis.Overheat => OverheatPenalty,
            CounterThesis.OverheadSupplyWall => WallPenalty,
            CounterThesis.MacroHeadwind => MacroPenalty,
            CounterThesis.PocResistance => PocPenalty,
            CounterThesis.CvdBearishDivergence => CvdBearishPenalty,
            CounterThesis.DepthTrap => DepthTrapPenalty,
            _ => 0,
        };

        public static int CountOf(CounterThesis t) =>
            ((t & CounterThesis.Overheat) != 0 ? 1 : 0) + ((t & CounterThesis.OverheadSupplyWall) != 0 ? 1 : 0) + ((t & CounterThesis.MacroHeadwind) != 0 ? 1 : 0)
            + ((t & CounterThesis.PocResistance) != 0 ? 1 : 0) + ((t & CounterThesis.CvdBearishDivergence) != 0 ? 1 : 0) + ((t & CounterThesis.DepthTrap) != 0 ? 1 : 0);

        /// <summary>반대 논리 6종 검증 + 확신도 감점(δ=0). rawConfidence 는 0~100 으로 포화. pocResistance = [v12.18.73-DEV] 기술적 팩터 POC 매물벽 판정(미가동 = false) ·
        /// cvdBearish/depthTrap = [v12.18.75-DEV] 미시구조 팩터 약세 다이버전스 · 매도벽/허매수 판정(미가동 = false).</summary>
        public static PlutoCounterThesisVerdict Assess(int rawConfidence, int rsi, int disparityX100, long bidQty, long askQty, int us10yChangeBp, int dollarIndexChangeBp,
            bool pocResistance = false, bool cvdBearish = false, bool depthTrap = false)
        {
            int raw = Math.Clamp(rawConfidence, 0, 100);
            var t = CounterThesis.None;
            int penalty = 0;
            if (IsOverheated(rsi, disparityX100)) { t |= CounterThesis.Overheat; penalty += OverheatPenalty; }
            if (IsSupplyWall(bidQty, askQty)) { t |= CounterThesis.OverheadSupplyWall; penalty += WallPenalty; }
            if (IsMacroHeadwind(us10yChangeBp, dollarIndexChangeBp)) { t |= CounterThesis.MacroHeadwind; penalty += MacroPenalty; }
            if (pocResistance) { t |= CounterThesis.PocResistance; penalty += PocPenalty; }
            if (cvdBearish) { t |= CounterThesis.CvdBearishDivergence; penalty += CvdBearishPenalty; }
            if (depthTrap) { t |= CounterThesis.DepthTrap; penalty += DepthTrapPenalty; }
            int conf = Math.Max(0, raw + penalty);
            bool blocked = conf < MinEntryConfidence || CountOf(t) >= VetoThesisCount;
            return new PlutoCounterThesisVerdict(t, penalty, raw, conf, blocked);
        }

        public static string Label(CounterThesis single) => single switch
        {
            CounterThesis.Overheat => "재료 선반영·고점 과열(RSI>70 · 이격도>108%)",
            CounterThesis.OverheadSupplyWall => "상단 매도 호가벽",
            CounterThesis.MacroHeadwind => "거시 역풍(금리 급등/달러 강세)",
            CounterThesis.PocResistance => "POC 매물벽 저항(상단 최대 매물대)",
            CounterThesis.CvdBearishDivergence => "CVD 약세 다이버전스(가격 상승 · 매도 공격 우위)",
            CounterThesis.DepthTrap => "15호가 매도벽/허매수 함정",
            _ => "반대 논리 없음",
        };
    }
}
