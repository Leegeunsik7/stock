// ============================================================================
// ArenaGovernanceMapper — v12.18.68-DEV: 엔진 플루토 거버넌스(PlutoGovernorStats · PlutoRegimeState · PlutoGovernanceTag) → 포트 DTO 투영(콜드 경로)
//
//   3대 아레나 어댑터 공용. UI 렌더 펌프 판독 경로 전용 — 정산 핫패스는 이 타입을 호출하지 않는다(문자열 · 리스트 할당 허용).
//   국면 코드 = 엔진 MarketRegime 바이트 값 그대로(ArenaRegimeCode 와 1:1 · 미상 값 = 판정 전).
//   쿨다운 판정 = 엔진 마지막 정산 KST 시각 기준(벽시계 미사용 — 합성 ×20 가상 세션에서도 엔진과 같은 시계).
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분

namespace StockManager.Adapters
{
    public static class ArenaGovernanceMapper
    {
        private static readonly IReadOnlyList<ArenaPenaltyTag> s_noPenalty = Array.Empty<ArenaPenaltyTag>();

        public static int Regime(MarketRegime r) => ArenaRegimeCode.Normalize((int)r);

        /// <summary>종목 국면 상태 판독 함수로 시장 대표 국면(패닉 우선 · 다수결) 산출.</summary>
        public static int Aggregate(IReadOnlyList<long> symbols, Func<long, MarketRegime> regimeOf)
        {
            Span<int> buf = stackalloc int[Math.Min(symbols.Count, 64)];
            int n = 0;
            for (int i = 0; i < symbols.Count && n < buf.Length; i++) buf[n++] = Regime(regimeOf(symbols[i]));
            return ArenaRegimeCode.Aggregate(buf[..n]);
        }

        /// <summary>
        /// 거버너 계수 + 실시간 가동 플래그 + 대표 국면 + 마지막 정산 시각 → 거버너 상태(판독 실패 = default).
        /// enabled = 엔진 현재 플래그(토글 직후 즉시 반영 — 계수 스냅샷의 Enabled 는 다음 정산 게시 때 갱신).
        /// </summary>
        public static ArenaGovernanceView View(bool read, in PlutoGovernorStats g, bool enabled, int regime, long lastKstTicks)
        {
            if (!read) return default;
            bool cooling = g.CooldownUntilTicks > 0 && lastKstTicks > 0 && lastKstTicks < g.CooldownUntilTicks;
            int remaining = cooling ? (int)Math.Min((g.CooldownUntilTicks - lastKstTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute, int.MaxValue) : 0;
            DateTime? until = g.CooldownUntilTicks > 0 ? new DateTime(g.CooldownUntilTicks, DateTimeKind.Unspecified) : null;
            return new ArenaGovernanceView(enabled, regime, g.LossStreak, cooling, remaining, until, g.Approved, g.PanicBlocks, g.CooldownBlocks, g.VetoBlocks);
        }

        /// <summary>근거 카드 거버넌스 태그 → 카드 DTO(미가동 = null).</summary>
        public static ArenaGovernanceCard? Card(in PlutoGovernanceTag g)
        {
            if (!g.Governed) return null;
            int regime = Regime(g.Regime);
            if (g.PostMortem.HasReview)
                return new ArenaGovernanceCard(regime, s_noPenalty, 0, 0, 0, g.LossStreak, false,
                    PlutoPostMortemText.Compose(in g.PostMortem), g.PostMortem.Verdict == PostMortemVerdict.Win);
            IReadOnlyList<ArenaPenaltyTag> tags = s_noPenalty;
            if (g.Theses != CounterThesis.None)
            {
                var list = new List<ArenaPenaltyTag>(3);
                if ((g.Theses & CounterThesis.Overheat) != 0) list.Add(new ArenaPenaltyTag("과열", PlutoDevilsAdvocateFilter.OverheatPenalty));
                if ((g.Theses & CounterThesis.OverheadSupplyWall) != 0) list.Add(new ArenaPenaltyTag("매도벽", PlutoDevilsAdvocateFilter.WallPenalty));
                if ((g.Theses & CounterThesis.MacroHeadwind) != 0) list.Add(new ArenaPenaltyTag("거시역풍", PlutoDevilsAdvocateFilter.MacroPenalty));
                if ((g.Theses & CounterThesis.PocResistance) != 0) list.Add(new ArenaPenaltyTag("매물벽(POC)", PlutoDevilsAdvocateFilter.PocPenalty));   // [v12.18.73-DEV]
                if ((g.Theses & CounterThesis.CvdBearishDivergence) != 0) list.Add(new ArenaPenaltyTag("약세 다이버전스(CVD)", PlutoDevilsAdvocateFilter.CvdBearishPenalty));   // [v12.18.75-DEV]
                if ((g.Theses & CounterThesis.DepthTrap) != 0) list.Add(new ArenaPenaltyTag("매도벽/허매수(10호가)", PlutoDevilsAdvocateFilter.DepthTrapPenalty));
                tags = list;
            }
            return new ArenaGovernanceCard(regime, tags, g.RawConfidence, g.Confidence, g.WeightBp, g.LossStreak, g.StreakCut, null, false);
        }
    }
}
