// ============================================================================
// PlutoLearningPort — v12.18.77-DEV: 플루토 지속 학습 UI 경계(글로벌 학습 상태 · 유사 국면 매칭 뱃지 · 순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 학습 스웜 상태 스냅샷을 이 DTO 로 옮긴다. UI 는 엔진을 new 하지 않는다.
//   수집 상태 코드(PlutoLearningHealth) = 0 미결선/관측 전 · 1 실시간 연동 중 · 2 직전 안정값 유지(지연) — 미상 값 = 미결선(fail-closed 표시).
//   [v12.18.82-DEV] 12대 역사적 국면 판정(RegimeName · RegimeSimilarityX10(0~1000) · RegimeNote 해설 · RegimeEffect 전략/편향 문구) — 호스트가 한국어 문자열로 옮긴다.
//   [v12.18.83-DEV] MacroFeedText = 호스트 거시 피드 결선 문구(예 '거시 피드: FRED API 키 미설정 · 관측 대기' · null = 미표기).
//   ★판독 전용 — 주문 · 무장 · 브로커 경로 0. 학습 미가동 호스트 = UnwiredPlutoLearningPort(IsWired false) → 페이지가 '학습 엔진 미결선' 으로 정직 렌더.
// ============================================================================
namespace MS2E.UI.Portable.Ports.Trading
{
    public static class PlutoLearningHealth
    {
        public const int NoData = 0, Live = 1, Stale = 2;

        public static int Normalize(int h) => h is Live or Stale ? h : NoData;

        public static string Label(int h) => Normalize(h) switch
        {
            Live => "실시간 연동 중",
            Stale => "직전 안정값 유지(수집 지연)",
            _ => "수집원 미결선",
        };

        public static string Css(int h) => Normalize(h) switch
        {
            Live => "live",
            Stale => "stale",
            _ => "none",
        };
    }

    /// <summary>
    /// 학습 상태 1건. Running = 백그라운드 스웜 가동 · Health = PlutoLearningHealth · EpisodeName/SimilarityPct = 최근접 과거 국면(null = 판정 불가) ·
    /// EpisodeIsSeed = 시드 근사 에피소드 · Generation = 가중치 핫스왑 세대(0 = 학습 전 중립) · MomentumTiltPct/DevilStrictnessPts = 현재 학습 가중치 ·
    /// Samples = 누적 청산 회고 표본 · Swaps/RejectedSwaps = 섀도우 게이트 통과/거부 누계 ·
    /// [v12.18.82-DEV] RegimeName/RegimeSimilarityX10 = 12대 역사적 국면 판정(null/−1 = 판정 전) · RegimeNote = 기간·해설 · RegimeEffect = 전략·가중치 편향 문구.
    /// [v12.18.83-DEV] MacroFeedText = 거시 피드 결선 문구(null = 미표기).
    /// </summary>
    public readonly record struct PlutoLearningView(
        bool Running, int Health, string? EpisodeName, int SimilarityPct, bool EpisodeIsSeed,
        long Generation, int MomentumTiltPct, int DevilStrictnessPts, long Samples, long Swaps, long RejectedSwaps,
        string? RegimeName = null, int RegimeSimilarityX10 = -1, string? RegimeNote = null, string? RegimeEffect = null, string? MacroFeedText = null)
    {
        public bool HasMatch => EpisodeName is not null && SimilarityPct >= 0;

        public bool HasRegime => RegimeName is not null && RegimeSimilarityX10 is >= 0 and <= 1000;

        /// <summary>국면 뱃지: [🧠 과거 국면 매칭: {이름} (유사도 91.4%)] · 판정 전 = "[🧠 과거 국면 매칭: 판정 전]".</summary>
        public string RegimeBadgeText => HasRegime
            ? $"[🧠 과거 국면 매칭: {RegimeName} (유사도 {RegimeSimilarityX10 / 10}.{RegimeSimilarityX10 % 10}%)]"
            : "[🧠 과거 국면 매칭: 판정 전]";

        /// <summary>뱃지 문구: 🧠 글로벌 학습 상태: {상태} (과거 국면 유사도: {이름} {n}%).</summary>
        public string BadgeText
        {
            get
            {
                string state = Running ? PlutoLearningHealth.Label(Health) : "학습 엔진 정지";
                string match = HasMatch ? $"{EpisodeName}{(EpisodeIsSeed ? "(시드 근사)" : "")} {SimilarityPct}%" : "판정 전";
                return $"🧠 글로벌 학습 상태: {state} (과거 국면 유사도: {match})";
            }
        }
    }

    /// <summary>플루토 지속 학습 상태 판독 포트(호스트 필수 등록 · 학습 비활성 = UnwiredPlutoLearningPort).</summary>
    public interface IPlutoLearningPort
    {
        bool IsWired { get; }
        PlutoLearningView Current { get; }
    }

    /// <summary>학습 엔진 미결선 null 객체(학습 비활성 호스트 · 테스트 하네스). 상태 = 정지 · 판정 전.</summary>
    public sealed class UnwiredPlutoLearningPort : IPlutoLearningPort
    {
        public static readonly UnwiredPlutoLearningPort Instance = new();
        private UnwiredPlutoLearningPort() { }
        public bool IsWired => false;
        public PlutoLearningView Current => new(false, PlutoLearningHealth.NoData, null, -1, false, 0, 0, 0, 0, 0, 0);
    }
}
