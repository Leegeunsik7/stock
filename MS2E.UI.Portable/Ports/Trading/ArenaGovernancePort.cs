// ============================================================================
// ArenaGovernancePort — v12.18.68-DEV: 플루토 프로 거버너 UI 경계(국면 뱃지 · 악마의 변호인 감점 태그 · 연패 쿨다운 · 자가 회고 · 순수 DTO · 엔진 타입 참조 0)
//
//   3대 아레나 포트(IKrxArenaPort · IUsArenaPort · ICryptoArenaPort)가 공용으로 쓰는 투영 타입. 호스트 어댑터가 엔진 거버넌스 계수 · 종목 국면 ·
//   근거 카드 Governance 태그를 이 DTO 로 옮긴다. UI 는 엔진을 new 하지 않는다.
//   국면 코드(ArenaRegimeCode) = 엔진 국면 열거 값과 1:1(0 판정 전 · 1 상승 · 2 하락 · 3 박스 · 4 패닉) — 미상 값 = 판정 전(fail-closed 표시).
//   ★liveArmed:false — 토글(SetGovernanceEnabled)은 AI 가상 원장의 진입 판정만 바꾼다(브로커 · 무장 · 실주문 경로 0).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>시장 국면 코드(엔진 국면 열거와 같은 값).</summary>
    public static class ArenaRegimeCode
    {
        public const int Unknown = 0, TrendingBull = 1, TrendingBear = 2, ChoppyRange = 3, PanicLiquidation = 4;

        public static int Normalize(int r) => r is >= TrendingBull and <= PanicLiquidation ? r : Unknown;

        /// <summary>뱃지 문구(이모지 포함).</summary>
        public static string Badge(int r) => Normalize(r) switch
        {
            TrendingBull => "📈 상승추세",
            TrendingBear => "📉 하락추세",
            ChoppyRange => "⏸️ 박스권",
            PanicLiquidation => "🚨 패닉차단",
            _ => "⏳ 국면 판정 전",
        };

        public static string Css(int r) => Normalize(r) switch
        {
            TrendingBull => "bull",
            TrendingBear => "bear",
            ChoppyRange => "range",
            PanicLiquidation => "panic",
            _ => "unknown",
        };

        /// <summary>
        /// 종목별 국면 → 시장 대표 국면(패닉 1종목이라도 = 패닉 · 그 외 다수결 · 동률 = 박스 · 판정 종목 0 = 판정 전).
        /// 패닉 우선 = 신규 매수 차단 국면을 UI 가 숨기지 않는다(fail-closed 표시).
        /// </summary>
        public static int Aggregate(ReadOnlySpan<int> regimes)
        {
            int bull = 0, bear = 0, range = 0;
            foreach (int raw in regimes)
            {
                switch (Normalize(raw))
                {
                    case PanicLiquidation: return PanicLiquidation;
                    case TrendingBull: bull++; break;
                    case TrendingBear: bear++; break;
                    case ChoppyRange: range++; break;
                }
            }
            if (bull + bear + range == 0) return Unknown;
            if (bull > bear && bull > range) return TrendingBull;
            if (bear > bull && bear > range) return TrendingBear;
            return ChoppyRange;
        }
    }

    /// <summary>
    /// 아레나 1개 거버너 상태. Enabled = 거버너 가동 · Regime = 시장 대표 국면(ArenaRegimeCode) · LossStreak = AI 연패 ·
    /// InCooldown = 마지막 정산 시각 기준 연패 쿨다운 중 · CooldownRemainingMinutes = 남은 분(올림 · 쿨다운 아님 = 0) · 계수 = 승인/차단 누계.
    /// </summary>
    public readonly record struct ArenaGovernanceView(
        bool Enabled, int Regime, int LossStreak, bool InCooldown, int CooldownRemainingMinutes, DateTime? CooldownUntilKst,
        long Approved, long PanicBlocks, long CooldownBlocks, long VetoBlocks);

    /// <summary>악마의 변호인 감점 1건(Label = "과열" · "매도벽" · "거시역풍" · Points = 음수 감점).</summary>
    public readonly record struct ArenaPenaltyTag(string Label, int Points)
    {
        public string Text => $"{Label} 감점 {Points}";
    }

    /// <summary>
    /// AI 근거 카드 1건의 거버넌스 투영. 진입 = 국면 · 감점 태그 · 원/최종 확신도 · 동적 비중 · 연패 축소 여부 /
    /// 청산 = 국면 · 연패 · 자가 회고 문장(PostMortem · 승리 = PostMortemWin true).
    /// </summary>
    public sealed record ArenaGovernanceCard(
        int Regime, IReadOnlyList<ArenaPenaltyTag> Penalties, int RawConfidence, int Confidence, int WeightBp, int LossStreak, bool StreakCut,
        string? PostMortem, bool PostMortemWin)
    {
        public bool IsExit => PostMortem is not null;
    }
}
