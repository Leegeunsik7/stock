// ============================================================================
// PlutoRecommendationPort — v12.18.78-DEV: 플루토 오늘의 AI 추천 종목 UI 경계(국내/미국 Top 5 카드 · 순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 스크리너 불변 스냅샷을 이 DTO 로 옮긴다. UI 는 엔진을 new 하지 않는다.
//   카드 = 종목명/코드 · 종합 점수(0~100) · 4대 팩터 점수(각 0~25) · 핵심 근거 3줄 · 악마의 변호인 판정 · 목표 안전마진 · 추천 수량.
//   [👤 1-Click 아레나 담기] = 페이지가 자기 휴먼 티켓 상태(종목 · 수량)만 채운다 — 접수는 기존 매수 버튼 → I*ArenaPort.SubmitHuman(휴먼 가상 원장 큐)뿐.
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0. 스크리너 미가동 호스트 = UnwiredPlutoRecommendationPort(IsWired false) → '추천 엔진 미결선' 정직 렌더.
//   [v12.18.85-DEV] 카드 Score = 학습 가중치 투영 적응 점수 · LearnMomentumPts/LearnDevilPts/LearnText = 투영 근거(학습 전 = 0 · 중립 문구).
//   [v12.18.90-DEV] Vetoes = 리스크 비토 차단 쇼케이스(리스크 통제관 · 악마의 변호인이 진입을 막은 시세 판독 종목 + 실측 수치 인용 차단 사유 · 날조 0).
//   [v12.18.80-DEV] Swarm = 병렬 학습 스웜 레인별 실데이터 수집 상태(업비트 공개 REST · KIS 일봉 · 미결선 = '실피드 연결 대기').
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Trading
{
    public static class PlutoRecoMarket
    {
        public const int Krx = 0, Us = 1, Crypto = 2;   // [v12.18.79-DEV] Crypto = 업비트 KRW(3대 자산)

        public static string Label(int m) => m switch { Us => "🇺🇸 미국", Crypto => "🪙 가상자산 (업비트)", _ => "🇰🇷 국내" };
    }

    /// <summary>
    /// 추천 카드 1건. Symbol = KRX 종목 코드 정수(US · 코인 = 엔진 내부 키 · 티켓 바인딩은 Code) · Code = KRX 6자리/US 티커/업비트 마켓("KRW-BTC") ·
    /// 4대 팩터 점수 = 주식(안전마진 · 기술 · 수급 · 거시) · 코인(유동성 · 볼밴 스퀴즈 · CVD · 15호가 뎁스벽) · RecommendedQty = 주(코인 = 코인 × 10^8 raw) ·
    /// MarginOfSafetyBps = 목표 안전마진(HasValuation false = 판독 없음) · CounterCount/CounterText = 악마의 변호인 판정(0~1건만 노출 · 2건 이상 = 목록 탈락).
    /// </summary>
    public sealed record PlutoRecommendationCard(
        int Market, int Rank, long Symbol, string Code, string Name, int Score,
        int ValuationPts, int TechnicalPts, int MicroPts, int MacroPts, IReadOnlyList<string> Reasons,
        bool HasValuation, int MarginOfSafetyBps, string PriceText, string FairPriceText, bool SeedProfile,
        long RecommendedQty, int CounterCount, string CounterText)
    {
        /// <summary>[v12.18.85-DEV] 학습 가중치 투영 — 모멘텀 조정(±) · 악마의 변호인 감점(+ = 감점) · 근거 문구(빈 값 = 미표기). Score = 적응 점수.</summary>
        public int LearnMomentumPts { get; init; }
        public int LearnDevilPts { get; init; }
        public string LearnText { get; init; } = "";

        /// <summary>[v12.18.88-DEV] 교차 자산 선행 파급 점수(KRX 전용 · 0 = 미적용) · 위원회 합의 점수(−100~+100 · CouncilWired false = 미표기) ·
        /// 프랙탈 켈리(KellyState = PlutoKellyCode · KellyBp = 권장 비중 · KellySamples = 엣지 표본 · KellyText = 근거 문구).</summary>
        public int LeadPts { get; init; }
        public bool CouncilWired { get; init; }
        public int CouncilConsensus { get; init; }
        public int KellyState { get; init; }
        public int KellyBp { get; init; }
        public int KellySamples { get; init; }
        public string KellyText { get; init; } = "";

        /// <summary>[v12.18.85-DEV] 학습 투영 순 조정(모멘텀 − 감점) · 기본 4대 팩터 합 = Score − 순 조정(포화 전).</summary>
        public int LearnNetPts => LearnMomentumPts - LearnDevilPts;

        /// <summary>점수 뱃지 등급(≥ 75 강력 · ≥ 60 우수 · 그 외 관심).</summary>
        public string ScoreCss => Score >= 75 ? "hot" : Score >= 60 ? "good" : "ok";

        public string ScoreLabel => Score >= 75 ? "강력 추천" : Score >= 60 ? "추천" : "관심";

        public string MosText => Market == PlutoRecoMarket.Crypto
            ? "🎯 가상자산 = 재무 안전마진 해당 없음(유동성 · 스퀴즈 · CVD · 뎁스 4대 팩터 평가)"
            : HasValuation
            ? "🎯 목표 안전마진 " + (MarginOfSafetyBps >= 0 ? "+" : "") + (MarginOfSafetyBps / 100m).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "% · 적정가 " + FairPriceText + (SeedProfile ? "(시드 프로필)" : "")
            : "🎯 안전마진 판독 없음(재무 프로필 미보유)";

        /// <summary>[v12.18.79-DEV] 코인 단위("KRW-BTC" → "BTC" · 주식 = "").</summary>
        public string Coin => Market == PlutoRecoMarket.Crypto && Code.StartsWith("KRW-", StringComparison.Ordinal) ? Code[4..] : "";

        /// <summary>[v12.18.79-DEV] 추천 수량 표기(주식 "271주" · 코인 raw 10^8 → "0.0123 BTC").</summary>
        public string QtyText => Market == PlutoRecoMarket.Crypto
            ? (RecommendedQty / 100_000_000m).ToString("0.########", System.Globalization.CultureInfo.InvariantCulture) + " " + Coin
            : RecommendedQty.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + "주";
    }

    /// <summary>시장 1개 추천 목록 + 스캔 집계(Scanned = 피드 판독 종목 · Vetoed = 악마의 변호인 탈락 · Insufficient = 판독 전무 · BelowThreshold = 50점 미만 ·
    /// [v12.18.79-DEV] Monitored = 감시 카탈로그 종목 수 · Passed = 추천 기준 통과).</summary>
    public sealed record PlutoRecoMarketView(IReadOnlyList<PlutoRecommendationCard> Cards, int Scanned, int Vetoed, int Insufficient, int BelowThreshold,
        int Monitored = 0, int Passed = 0)
    {
        public static readonly PlutoRecoMarketView None = new(Array.Empty<PlutoRecommendationCard>(), 0, 0, 0, 0);

        /// <summary>[v12.18.90-DEV] 리스크 통제관 비토 탈락 수(위원회 미결선 = 0) · 차단 합계 = 악마의 변호인 + 리스크 통제관.</summary>
        public int CouncilVetoed { get; init; }
        public int BlockedTotal => Vetoed + CouncilVetoed;

        /// <summary>감시 중이지만 피드 판독이 아직 없는 종목 수(점수 산출 0 · 정직 집계).</summary>
        public int AwaitingFeed => Math.Max(0, Monitored - Scanned);
    }

    /// <summary>추천 보드 전체. Generation 0 = 첫 스캔 전 · UpdatedUtc null = 스캔 이력 없음 · [v12.18.79-DEV] Crypto = 업비트 KRW 슬롯(미지정 = None).</summary>
    public sealed record PlutoRecoBoardView(bool Running, long Generation, DateTime? UpdatedUtc, PlutoRecoMarketView Krx, PlutoRecoMarketView Us)
    {
        public static readonly PlutoRecoBoardView None = new(false, 0, null, PlutoRecoMarketView.None, PlutoRecoMarketView.None);

        public PlutoRecoMarketView Crypto { get; init; } = PlutoRecoMarketView.None;

        /// <summary>[v12.18.80-DEV] 병렬 학습 스웜 실데이터 수집 상태(미지정 = None · 뱃지 미표시).</summary>
        public PlutoSwarmView Swarm { get; init; } = PlutoSwarmView.None;

        /// <summary>[v12.18.88-DEV] 4대 인지 에이전트 위원회 · 선행 파급 · 켈리(미지정 = None · 위젯 '위원회 미결선').</summary>
        public PlutoCouncilView Council { get; init; } = PlutoCouncilView.None;

        /// <summary>[v12.18.90-DEV] 비토 차단 목록(시장별 상위 ≤ 10 · 차단 전 기본 점수 내림차순 · 미지정 = 빈 목록).</summary>
        public IReadOnlyList<PlutoVetoCard> Vetoes { get; init; } = Array.Empty<PlutoVetoCard>();

        /// <summary>[v12.18.90-DEV] 3대 시장 비토 차단 합계(목록 상한과 무관한 전수 집계).</summary>
        public int TotalBlocked => Krx.BlockedTotal + Us.BlockedTotal + Crypto.BlockedTotal;

        public PlutoRecoMarketView Of(int market) => market switch { PlutoRecoMarket.Us => Us, PlutoRecoMarket.Crypto => Crypto, _ => Krx };

        /// <summary>[v12.18.79-DEV] 3대 시장 전수 감시 합계 · 피드 판독 합계 · 추천 기준 통과 합계.</summary>
        public int TotalMonitored => Krx.Monitored + Us.Monitored + Crypto.Monitored;
        public int TotalScanned => Krx.Scanned + Us.Scanned + Crypto.Scanned;
        public int TotalPassed => Krx.Passed + Us.Passed + Crypto.Passed;

        /// <summary>상태 뱃지 "📡 3대 시장 전수 감시 중: KRX 200개 | US 100개 | Upbit 120개".</summary>
        public string MonitorBadgeText => "📡 3대 시장 전수 감시 중: KRX " + Krx.Monitored.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "개 | US " + Us.Monitored.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "개 | Upbit " + Crypto.Monitored.ToString(System.Globalization.CultureInfo.InvariantCulture) + "개";
    }

    /// <summary>[v12.18.90-DEV] 비토 차단 주체 코드(1 리스크 통제관 · 2 악마의 변호인 · 미상 = 리스크 통제관 취급 아님 → 0).</summary>
    public static class PlutoVetoSourceCode
    {
        public const int Unknown = 0, Council = 1, Devil = 2;

        public static int Normalize(int s) => s is Council or Devil ? s : Unknown;

        public static string Label(int s) => Normalize(s) switch
        {
            Council => "🛡️ 리스크 통제관",
            Devil => "😈 악마의 변호인",
            _ => "차단 주체 미상",
        };

        public static string Css(int s) => Normalize(s) switch { Council => "cro", Devil => "devil", _ => "none" };
    }

    /// <summary>
    /// [v12.18.90-DEV] 비토 차단 종목 1건 — 시세 판독 종목만(피드 미판독 = 목록 없음) · BaseScore = 차단 전 기본 점수 · Source = PlutoVetoSourceCode ·
    /// Reason = 실측 수치 인용 차단 사유("VIX 38.2 (35 초과) — 패닉 투매 위험 차단").
    /// </summary>
    public sealed record PlutoVetoCard(int Market, string Code, string Name, int BaseScore, int Source, string Reason, string PriceText);

    /// <summary>
    /// [v12.18.80-DEV] 병렬 학습 스웜 레인 1개(시장별 실데이터 수집 상태). Connected false = 소스 미결선(KIS 미구성 등) → '감시 중 · 실피드 연결 대기'(합성 점수 0) ·
    /// Live = 실데이터 판독 슬롯 · NoData = 응답 봉 없음/키 인코딩 불가 · Faulted = 조회 실패 · CoolingDown = 레이트 리밋 쿨다운(재시도 0).
    /// </summary>
    public sealed record PlutoSwarmLaneView(int Market, bool Connected, string SourceLabel, int Monitored, int Live, int Awaiting, int NoData, int Faulted,
        bool Sweeping, bool CoolingDown, long Fetches, long RateLimited)
    {
        public static readonly PlutoSwarmLaneView None = new(PlutoRecoMarket.Krx, false, "실데이터 소스 미결선", 0, 0, 0, 0, 0, false, false, 0, 0);

        public string StatusText => !Connected
            ? "감시 중 · 실피드 연결 대기"
            : CoolingDown ? "⏸ 레이트 리밋 쿨다운(재시도 0) · 실데이터 " + Live.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + Monitored.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "실데이터 " + Live.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + Monitored.ToString(System.Globalization.CultureInfo.InvariantCulture) + (Sweeping ? " · 수집 중" : "");

        public string Css => !Connected ? "await" : CoolingDown || Faulted > 0 ? "warn" : "live";
    }

    /// <summary>[v12.18.80-DEV] 병렬 학습 스웜 전체(Wired false = 스웜 미가동 호스트 · 뱃지 미표시).</summary>
    public sealed record PlutoSwarmView(bool Wired, bool Running, int DutyPercent, PlutoSwarmLaneView Krx, PlutoSwarmLaneView Us, PlutoSwarmLaneView Crypto)
    {
        public static readonly PlutoSwarmView None = new(false, false, 0, PlutoSwarmLaneView.None, PlutoSwarmLaneView.None, PlutoSwarmLaneView.None);

        public PlutoSwarmLaneView Of(int market) => market switch { PlutoRecoMarket.Us => Us, PlutoRecoMarket.Crypto => Crypto, _ => Krx };

        public int TotalLive => Krx.Live + Us.Live + Crypto.Live;

        /// <summary>"🧠 병렬 학습 스웜(BelowNormal · CPU 듀티 25%): KRX 감시 중 · 실피드 연결 대기 | US … | Upbit 실데이터 117/120".</summary>
        public string BadgeText => "🧠 병렬 학습 스웜(BelowNormal · CPU 듀티 " + DutyPercent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"
            + (Running ? "" : " · 정지") + "): KRX " + Krx.StatusText + " | US " + Us.StatusText + " | Upbit " + Crypto.StatusText;
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
