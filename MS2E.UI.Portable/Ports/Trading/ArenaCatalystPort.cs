// ============================================================================
// ArenaCatalystPort — v12.18.64-DEV: 플루토 뉴스 촉매 배지 UI 경계(순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 플루토 버퍼 판정을 아레나 정산 시각(마지막 정산 KST − 9h) 기준으로 감쇄해 투영한다 — 합성/리플레이 세션에서도 엔진이 본 점수와 같다.
//   배지 규칙: 호재(실효 > 0) = 녹색 [호재 +점수] + 반감기 진행률 · 치명적 악재(구조적 악재 ∧ 실효 ≤ −60) = 점멸 적색 [긴급 악재 −점수](진입 금지 · 청산 경고) ·
//     그 밖 악재(실효 < 0) = 비점멸 적색 테두리 [악재 −점수] · 노이즈/중립(실효 0) = 투영하지 않음(null · 클린 UI).
//   툴팁 = 64바이트 헤드라인 요약 + 출처 + 경과/반감기.
// ============================================================================
namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>촉매 배지 종류.</summary>
    public static class ArenaCatalystKind
    {
        public const int None = 0, Positive = 1, Negative = 2, Shock = 3;
    }

    /// <summary>종목 1개 뉴스 촉매 투영. EffectiveScore = 감쇄 × 확신도(아레나 시그널 입력값) · HalfLifeProgressPct = 경과 / 반감기(0~100).</summary>
    public sealed record ArenaCatalystView(
        int Kind, int EffectiveScore, int RawScore, string Category, int HalfLifeMinutes, int AgeMinutes, int HalfLifeProgressPct, string Headline, string Source)
    {
        public bool IsShock => Kind == ArenaCatalystKind.Shock;
        public bool IsPositive => Kind == ArenaCatalystKind.Positive;

        /// <summary>배지 문구(대괄호 포함).</summary>
        public string BadgeText => Kind switch
        {
            ArenaCatalystKind.Positive => "[호재 +" + EffectiveScore + "]",
            ArenaCatalystKind.Shock => "[긴급 악재 " + EffectiveScore + "]",
            ArenaCatalystKind.Negative => "[악재 " + EffectiveScore + "]",
            _ => "",
        };

        /// <summary>툴팁(헤드라인 · 출처 · 유형 · 원점수 · 경과/반감기).</summary>
        public string Tooltip => Headline + "\n출처: " + Source + " · " + Category + " · 원점수 " + RawScore
            + "\n경과 " + AgeMinutes + "분 / 반감기 " + HalfLifeMinutes + "분(" + HalfLifeProgressPct + "%)"
            + (IsShock ? "\n⚠ 진입 금지 · 보유 즉시 청산 대상" : "");
    }
}
