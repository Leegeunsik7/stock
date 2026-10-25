// ============================================================================
// PlutoRecommendationPort — v12.18.78-DEV: 플루토 오늘의 AI 추천 종목 UI 경계(국내/미국 Top 5 카드 · 순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 스크리너 불변 스냅샷을 이 DTO 로 옮긴다. UI 는 엔진을 new 하지 않는다.
//   카드 = 종목명/코드 · 종합 점수(0~100) · 4대 팩터 점수(각 0~25) · 핵심 근거 3줄 · 악마의 변호인 판정 · 목표 안전마진 · 추천 수량.
//   [👤 1-Click 아레나 담기] = 페이지가 자기 휴먼 티켓 상태(종목 · 수량)만 채운다 — 접수는 기존 매수 버튼 → I*ArenaPort.SubmitHuman(휴먼 가상 원장 큐)뿐.
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0. 스크리너 미가동 호스트 = UnwiredPlutoRecommendationPort(IsWired false) → '추천 엔진 미결선' 정직 렌더.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    public static class PlutoRecoMarket
    {
        public const int Krx = 0, Us = 1;

        public static string Label(int m) => m == Us ? "🇺🇸 미국" : "🇰🇷 국내";
    }

    /// <summary>
    /// 추천 카드 1건. Symbol = KRX 종목 코드 정수(US = 엔진 내부 키 · 티켓 바인딩은 Code) · Code = KRX 6자리/US 티커 ·
    /// MarginOfSafetyBps = 목표 안전마진(HasValuation false = 판독 없음) · CounterCount/CounterText = 악마의 변호인 판정(0~1건만 노출 · 2건 이상 = 목록 탈락).
    /// </summary>
    public sealed record PlutoRecommendationCard(
        int Market, int Rank, long Symbol, string Code, string Name, int Score,
        int ValuationPts, int TechnicalPts, int MicroPts, int MacroPts, IReadOnlyList<string> Reasons,
        bool HasValuation, int MarginOfSafetyBps, string PriceText, string FairPriceText, bool SeedProfile,
        long RecommendedQty, int CounterCount, string CounterText)
    {
        /// <summary>점수 뱃지 등급(≥ 75 강력 · ≥ 60 우수 · 그 외 관심).</summary>
        public string ScoreCss => Score >= 75 ? "hot" : Score >= 60 ? "good" : "ok";

        public string ScoreLabel => Score >= 75 ? "강력 추천" : Score >= 60 ? "추천" : "관심";

        public string MosText => HasValuation
            ? "🎯 목표 안전마진 " + (MarginOfSafetyBps >= 0 ? "+" : "") + (MarginOfSafetyBps / 100m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "% · 적정가 " + FairPriceText + (SeedProfile ? "(시드 프로필)" : "")
            : "🎯 안전마진 판독 없음(재무 프로필 미보유)";
    }

    /// <summary>시장 1개 추천 목록 + 스캔 집계(Scanned = 유니버스 · Vetoed = 악마의 변호인 탈락 · Insufficient = 판독 전무 · BelowThreshold = 50점 미만).</summary>
    public sealed record PlutoRecoMarketView(IReadOnlyList<PlutoRecommendationCard> Cards, int Scanned, int Vetoed, int Insufficient, int BelowThreshold)
    {
        public static readonly PlutoRecoMarketView None = new(Array.Empty<PlutoRecommendationCard>(), 0, 0, 0, 0);
    }

    /// <summary>추천 보드 전체. Generation 0 = 첫 스캔 전 · UpdatedUtc null = 스캔 이력 없음.</summary>
    public sealed record PlutoRecoBoardView(bool Running, long Generation, DateTime? UpdatedUtc, PlutoRecoMarketView Krx, PlutoRecoMarketView Us)
    {
        public static readonly PlutoRecoBoardView None = new(false, 0, null, PlutoRecoMarketView.None, PlutoRecoMarketView.None);

        public PlutoRecoMarketView Of(int market) => market == PlutoRecoMarket.Us ? Us : Krx;
    }

    /// <summary>플루토 AI 추천 종목 판독 포트(호스트 필수 등록 · 스크리너 비활성 = UnwiredPlutoRecommendationPort).</summary>
    public interface IPlutoRecommendationPort
    {
        bool IsWired { get; }
        PlutoRecoBoardView Current { get; }
    }

    /// <summary>추천 엔진 미결선 null 객체(스크리너 비활성 호스트 · 테스트 하네스).</summary>
    public sealed class UnwiredPlutoRecommendationPort : IPlutoRecommendationPort
    {
        public static readonly UnwiredPlutoRecommendationPort Instance = new();
        private UnwiredPlutoRecommendationPort() { }
        public bool IsWired => false;
        public PlutoRecoBoardView Current => PlutoRecoBoardView.None;
    }
}
