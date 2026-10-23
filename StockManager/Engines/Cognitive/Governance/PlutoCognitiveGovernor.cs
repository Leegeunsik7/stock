// ============================================================================
// PlutoCognitiveGovernor — v12.18.67-DEV: 플루토 브레인 고도화 — 아레나 진입 거버넌스(국면 → 쿨다운 → 악마의 변호인 → 동적 사이징 · δ=0)
//
//   아레나 3대 엔진(KRX · US Tech · Upbit Crypto)이 EnableGovernance(true) 일 때 AI 신규 진입 직전에 호출한다(정산 스레드 · 단일 작성자).
//     ① 패닉 국면 = 재가중 이전 원점수로 판정해 PanicRegime 차단(신규 매수 100% fail-closed · 원점수 미달 = BelowEntryScore · 계수 = 실제 매수 신호만).
//     ② 국면 재가중: PlutoRegimeClassifier.Reweight(엔진 점수 · 모멘텀/불균형 기여) → 진입 점수 미달 = BelowEntryScore(무계수).
//     ③ 연패 쿨다운 중 = Cooldown 차단.
//     ④ 악마의 변호인: 원확신도(재가중 점수/100)에 반대 논리 3종 감점 → 확신도 < 30 ∨ 반대 논리 ≥ 2 = CounterThesisVeto 차단.
//     ⑤ 동적 사이징: 감점 후 확신도 · ATR · 연패 → 진입 비중 bp(5%~25% · 연패 ≥ 2 = 50% 축소).
//   승인 결과 PlutoGovernanceTag = AiTradeRationale.Governance(국면 뱃지 · 감점 내역 · 비중 · 회고) — 같은 근거 값에 내장되어 seqlock 쓰기 창 안에서 원자 게시.
//   ★δ=0: Approve/태그 생성 힙 할당 0. 한국어 문장(PlutoGovernanceText)은 콜드 경로. ★liveArmed:false — 판정일 뿐 주문 수단 0.
// ============================================================================
using System;
using System.Globalization;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분

namespace StockManager.Engines.Cognitive.Governance
{
    public enum PlutoBlockReason : byte
    {
        None = 0,
        BelowEntryScore = 1,
        PanicRegime = 2,
        Cooldown = 3,
        CounterThesisVeto = 4,
    }

    /// <summary>엔진 거버넌스 계수(값 타입 · 스냅샷 판독용).</summary>
    public readonly struct PlutoGovernorStats
    {
        public readonly bool Enabled;
        public readonly int LossStreak;
        public readonly long CooldownUntilTicks, Approved, PanicBlocks, CooldownBlocks, VetoBlocks, Closes, LossCloses;

        public PlutoGovernorStats(bool enabled, in PlutoSizerState sizer, long approved, long panicBlocks, long cooldownBlocks, long vetoBlocks)
        {
            Enabled = enabled; LossStreak = sizer.LossStreak; CooldownUntilTicks = sizer.CooldownUntilTicks; Closes = sizer.Closes; LossCloses = sizer.LossCloses;
            Approved = approved; PanicBlocks = panicBlocks; CooldownBlocks = cooldownBlocks; VetoBlocks = vetoBlocks;
        }
    }

    /// <summary>
    /// 체결 시점 거버넌스 판정(근거 카드 결속용 · 값 타입). default = 거버넌스 미가동(기존 동작).
    /// 진입 = 국면 · 반대 논리 · 감점 · 원/최종 확신도 · 재가중 점수 · 비중 · 연패 · 지표 / 청산 = 국면 · 연패(청산 반영 후) · 지표 · 자가 회고.
    /// </summary>
    public readonly struct PlutoGovernanceTag
    {
        public readonly bool Governed;
        public readonly MarketRegime Regime;
        public readonly CounterThesis Theses;
        public readonly int Penalty, RawConfidence, Confidence, AdjustedScore, WeightBp, LossStreak, Rsi, DisparityX100, AtrCbp;
        public readonly PlutoPostMortem PostMortem;

        private PlutoGovernanceTag(MarketRegime regime, CounterThesis theses, int penalty, int rawConfidence, int confidence, int adjustedScore, int weightBp,
            int lossStreak, int rsi, int disparityX100, int atrCbp, in PlutoPostMortem postMortem)
        {
            Governed = true; Regime = regime; Theses = theses; Penalty = penalty; RawConfidence = rawConfidence; Confidence = confidence;
            AdjustedScore = adjustedScore; WeightBp = weightBp; LossStreak = lossStreak; Rsi = rsi; DisparityX100 = disparityX100; AtrCbp = atrCbp;
            PostMortem = postMortem;
        }

        public static PlutoGovernanceTag ForEntry(in PlutoRegimeState regime, in PlutoCounterThesisVerdict v, int adjustedScore, int weightBp, int lossStreak) =>
            new(regime.Regime, v.Theses, v.Penalty, v.RawConfidence, v.Confidence, adjustedScore, weightBp, lossStreak, regime.Rsi, regime.DisparityX100, regime.AtrCbp, default);

        public static PlutoGovernanceTag ForExit(in PlutoRegimeState regime, int lossStreak, in PlutoPostMortem postMortem) =>
            new(regime.Regime, CounterThesis.None, 0, 0, 0, 0, 0, lossStreak, regime.Rsi, regime.DisparityX100, regime.AtrCbp, in postMortem);

        public bool IsEntry => Governed && !PostMortem.HasReview;
        public bool StreakCut => LossStreak >= PlutoDynamicSizer.StreakThreshold;

        /// <summary>
        /// 무결성: 미가동 = 전 필드 default · 가동 = 국면 정의 ∧ 감점 = 반대 논리 감점 합 ∧ 확신도 = max(0, 원 + 감점) ∧ 원확신도 0~100 ∧
        /// 진입 비중 0(청산) 또는 2.5%~25% ∧ RSI 0~100 ∧ 회고 무결 ∧ 진입 태그는 회고 없음 · 청산 태그는 반대 논리/비중 0.
        /// </summary>
        public bool IsWellFormed
        {
            get
            {
                if (!Governed)
                    return Regime == MarketRegime.Unknown && Theses == CounterThesis.None && Penalty == 0 && RawConfidence == 0 && Confidence == 0
                        && WeightBp == 0 && LossStreak == 0 && !PostMortem.HasReview && PostMortem.IsWellFormed;
                if (!MarketRegimeText.IsDefined(Regime) || Rsi is < 0 or > 100 || LossStreak < 0 || AtrCbp < 0 || !PostMortem.IsWellFormed) return false;
                int expected = PlutoDevilsAdvocateFilter.PenaltyOf(Theses & CounterThesis.Overheat) + PlutoDevilsAdvocateFilter.PenaltyOf(Theses & CounterThesis.OverheadSupplyWall)
                             + PlutoDevilsAdvocateFilter.PenaltyOf(Theses & CounterThesis.MacroHeadwind);
                if (PostMortem.HasReview) return Theses == CounterThesis.None && Penalty == 0 && WeightBp == 0;
                return Penalty == expected && RawConfidence is >= 0 and <= 100 && Confidence == Math.Max(0, RawConfidence + Penalty)
                    && WeightBp >= PlutoDynamicSizer.MinWeightBp * PlutoDynamicSizer.StreakCutPct / 100 && WeightBp <= PlutoDynamicSizer.MaxWeightBp;
            }
        }
    }

    public static class PlutoCognitiveGovernor
    {
        /// <summary>
        /// AI 신규 진입 승인(δ=0). 승인 = None + tag(국면 · 감점 · 비중). 차단 = 사유(tag = default).
        /// momentum/imbalance = 엔진 점수 산식의 모멘텀 · 호가 불균형 구성요소(bp)와 가중(%).
        /// </summary>
        public static PlutoBlockReason Approve(in PlutoRegimeState regime, int score, int momentumBp, int momentumWeight, int imbalanceBp, int imbalanceWeight,
            int entryScore, long bidQty, long askQty, int us10yChangeBp, int dollarIndexChangeBp, in PlutoSizerState sizer, long nowTicks, out PlutoGovernanceTag tag)
        {
            tag = default;
            if (MarketRegimeText.BlocksEntry(regime.Regime)) return score >= entryScore ? PlutoBlockReason.PanicRegime : PlutoBlockReason.BelowEntryScore;
            int adjusted = PlutoRegimeClassifier.Reweight(score, momentumBp, momentumWeight, imbalanceBp, imbalanceWeight, regime.Regime);
            if (adjusted < entryScore) return PlutoBlockReason.BelowEntryScore;
            if (sizer.InCooldown(nowTicks)) return PlutoBlockReason.Cooldown;
            var v = PlutoDevilsAdvocateFilter.Assess(adjusted / 100, regime.Rsi, regime.DisparityX100, bidQty, askQty, us10yChangeBp, dollarIndexChangeBp);
            if (v.Blocked) return PlutoBlockReason.CounterThesisVeto;
            int weight = PlutoDynamicSizer.WeightBp(v.Confidence, regime.AtrCbp, sizer.LossStreak);
            tag = PlutoGovernanceTag.ForEntry(in regime, in v, adjusted, weight, sizer.LossStreak);
            return PlutoBlockReason.None;
        }
    }

    /// <summary>거버넌스 한국어 문장(콜드 경로 — 호출마다 문자열 조립).</summary>
    public static class PlutoGovernanceText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Compose(in PlutoGovernanceTag g)
        {
            if (!g.Governed) return "";
            string badge = $"[국면 {MarketRegimeText.Badge(g.Regime)}] 플루토 국면: {MarketRegimeText.Label(g.Regime)}";
            string ind = string.Create(Inv, $"RSI {g.Rsi} · 이격도 {g.DisparityX100 / 100}.{g.DisparityX100 % 100:D2}% · ATR {g.AtrCbp / 100}.{g.AtrCbp % 100:D2}bp");
            if (g.PostMortem.HasReview)
                return $"{badge}({ind} · 연패 {g.LossStreak.ToString(Inv)}회). {PlutoPostMortemText.Compose(in g.PostMortem)}";
            string devil;
            if (g.Theses == CounterThesis.None) devil = "악마의 변호인: 반대 논리 없음";
            else
            {
                devil = "악마의 변호인:";
                string sep = " ";
                for (int bit = 0; bit < 3; bit++)
                {
                    var t = (CounterThesis)(1 << bit);
                    if ((g.Theses & t) == 0) continue;
                    devil += sep + PlutoDevilsAdvocateFilter.Label(t) + " " + PlutoDevilsAdvocateFilter.PenaltyOf(t).ToString(Inv) + "점";
                    sep = " · ";
                }
            }
            string w = string.Create(Inv, $"{g.WeightBp / 100}.{g.WeightBp % 100:D2}%");
            string streak = g.StreakCut ? string.Create(Inv, $" · 연패 {g.LossStreak}회 → 비중 50% 축소") : "";
            return string.Create(Inv, $"{badge}({ind}) · {devil} → 확신도 {g.RawConfidence}→{g.Confidence}/100 · 동적 비중 {w}{streak}.");
        }
    }
}
