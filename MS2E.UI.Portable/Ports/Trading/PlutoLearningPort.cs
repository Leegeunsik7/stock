// ============================================================================
// PlutoLearningPort — v12.18.77-DEV: 플루토 지속 학습 UI 경계(글로벌 학습 상태 · 유사 국면 매칭 뱃지 · 순수 DTO · 엔진 타입 참조 0)
//
//   호스트 어댑터가 학습 스웜 상태 스냅샷을 이 DTO 로 옮긴다. UI 는 엔진을 new 하지 않는다.
//   수집 상태 코드(PlutoLearningHealth) = 0 미결선/관측 전 · 1 실시간 연동 중 · 2 직전 안정값 유지(지연) — 미상 값 = 미결선(fail-closed 표시).
//   [v12.18.82-DEV] 12대 역사적 국면 판정(RegimeName · RegimeSimilarityX10(0~1000) · RegimeNote 해설 · RegimeEffect 전략/편향 문구) — 호스트가 한국어 문자열로 옮긴다.
//   [v12.18.83-DEV] MacroFeedText = 호스트 거시 피드 결선 문구(예 '거시 피드: FRED API 키 미설정 · 관측 대기' · null = 미표기).
//   [v12.18.84-DEV] LifetimeEpisodes(재기동 합산 평생 누적 학습 에피소드 · −1 = 영속화 미결선) · PersistState(PlutoLearningPersist 코드) → 영속화 뱃지.
//   [v12.18.85-DEV] Shadow(PlutoShadowView) = 섀도우 검증 직전 판정 · 현행 가중치 섀도우 성과 → 인지 브리핑(PlutoBriefingComposer) 입력.
//   [v12.18.86-DEV] Tensor(PlutoTensorView) = 활성 국면 × 3대 자산군 해석 가중치(모멘텀 % · 악마의 변호인 엄격도) + 웜스타트 상태(WarmReady/NoData) → 인지 브리핑 텐서 패널.
//   [v12.18.92-DEV] EvolveAnchor(직전 진화 심사 시점 누적 표본 · −1 = 미상) → PlutoEvolutionEta 진행도 · ReadTensorCells = 39셀 텐서 콜드 스냅샷(인스펙터 열림 시만 판독).
//   [v12.18.93-DEV] TensorSource Prior(국면 사전 가중치 · 실측 아님) · CanFastReplay/LastReplay/RunFastReplayAsync = 원클릭 자율 학습 가속기(100회 리플레이 · 백그라운드).
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

    /// <summary>[v12.18.84-DEV] 학습 가중치 영속화 상태 코드 — 0 미결선 · 1 가동 중 · 2 손상 롤백 복원 · 3 손상 중립 폴백 · 4 저장소 장애(미상 값 = 미결선).</summary>
    public static class PlutoLearningPersist
    {
        public const int Off = 0, Active = 1, RolledBack = 2, NeutralFallback = 3, StoreFailed = 4;

        public static int Normalize(int p) => p is Active or RolledBack or NeutralFallback or StoreFailed ? p : Off;

        public static string Label(int p) => Normalize(p) switch
        {
            Active => "영속화 가동 중",
            RolledBack => "영속화 가동 중 · 손상 체크포인트 건너뜀 → 직전 골든 복원",
            NeutralFallback => "체크포인트 손상 감지 · 중립 가중치 폴백 · 영속화 가동 중",
            StoreFailed => "영속화 저장소 장애 · 이번 세션 기록 중지",
            _ => "영속화 미결선",
        };

        public static string Css(int p) => Normalize(p) switch
        {
            Active => "on",
            RolledBack or NeutralFallback => "warn",
            StoreFailed => "fail",
            _ => "none",
        };
    }

    /// <summary>
    /// [v12.18.85-DEV] 섀도우 검증 요약 — Verdict = 직전 판정 코드(PlutoShadowVerdictCode) · Baseline*/Candidate* = 직전 판정의 현행 대비 후보 승률/MDD(bp) ·
    /// Holdout = 홀드아웃 표본 · Live* = 현재 적용 중 가중치가 통과할 때 기록된 섀도우 승률/MDD/표본(영속 체크포인트 보존 · 0 = 학습 전).
    /// </summary>
    public readonly record struct PlutoShadowView(
        int Verdict, int BaselineWinRateBp, int CandidateWinRateBp, int BaselineMddBp, int CandidateMddBp, int Holdout,
        int LiveWinRateBp, int LiveMddBp, int LiveSamples)
    {
        /// <summary>직전 판정이 비교 수치를 가짐(홀드아웃 판정까지 도달).</summary>
        public bool HasComparison => Holdout > 0 && PlutoShadowVerdictCode.Normalize(Verdict) is PlutoShadowVerdictCode.Passed
            or PlutoShadowVerdictCode.WinRateDegraded or PlutoShadowVerdictCode.DrawdownWorse;

        /// <summary>승률 개선치 bp(후보 − 현행 · 비교 없음 = 0).</summary>
        public int WinRateGainBp => HasComparison ? CandidateWinRateBp - BaselineWinRateBp : 0;

        /// <summary>MDD 개선치 bp(현행 − 후보 · 양수 = 낙폭 축소 · 비교 없음 = 0).</summary>
        public int MddGainBp => HasComparison ? BaselineMddBp - CandidateMddBp : 0;
    }

    /// <summary>[v12.18.85-DEV] 섀도우 판정 코드 — 0 판정 전 · 1 통과 · 2 홀드아웃 부족 · 3 승률 악화 거부 · 4 낙폭 악화 거부 · 5 변화 없음(미상 = 판정 전).</summary>
    public static class PlutoShadowVerdictCode
    {
        public const int None = 0, Passed = 1, InsufficientHoldout = 2, WinRateDegraded = 3, DrawdownWorse = 4, NoChange = 5;

        public static int Normalize(int v) => v is >= Passed and <= NoChange ? v : None;

        public static string Label(int v) => Normalize(v) switch
        {
            Passed => "통과(핫스왑)",
            InsufficientHoldout => "홀드아웃 표본 부족(보류)",
            WinRateDegraded => "승률 악화 → 거부(현행 유지)",
            DrawdownWorse => "낙폭 악화 → 거부(현행 유지)",
            NoChange => "변화 없음",
            _ => "판정 전",
        };
    }

    /// <summary>[v12.18.86-DEV] 자산군 웜스타트 상태 코드 — 0 미결선 · 1 대기/진행 중 · 2 WarmReady(로컬 캔들 리플레이 결속) · 3 NoData(로컬 캔들 없음) · 4 예산 초과 · 5 판독 장애(미상 = 미결선).</summary>
    public static class PlutoWarmCode
    {
        public const int Off = 0, Pending = 1, Ready = 2, NoData = 3, TimedOut = 4, Failed = 5;

        public static int Normalize(int w) => w is >= Pending and <= Failed ? w : Off;

        public static string Label(int w) => Normalize(w) switch
        {
            Pending => "웜스타트 대기",
            Ready => "WarmReady",
            NoData => "NoData",
            TimedOut => "예산 초과(미결속)",
            Failed => "캔들 판독 장애",
            _ => "웜스타트 미결선",
        };

        public static string Css(int w) => Normalize(w) switch
        {
            Ready => "on",
            Pending => "stale",
            TimedOut or Failed => "fail",
            _ => "none",
        };
    }

    /// <summary>[v12.18.86-DEV] 텐서 가중치 출처 코드 — 0 전역(슬롯 · 기준선 미보정) · 1 자산 기준선 · 2 국면 × 자산군 슬롯(미상 = 전역).</summary>
    public static class PlutoTensorSourceCode
    {
        public const int Global = 0, Baseline = 1, Regime = 2, Prior = 3;

        public static string Label(int s) => s switch
        {
            Regime => "국면 슬롯",
            Prior => "국면 사전 가중치",
            Baseline => "자산 기준선",
            _ => "전역 폴백",
        };
    }

    /// <summary>
    /// [v12.18.86-DEV] 자산군 1레인(Asset = PlutoRecoMarket 값 · 0 KRX · 1 US · 2 Upbit) — Source = PlutoTensorSourceCode · Generation/MomentumTiltPct/DevilStrictnessPts = 현재 활성 슬롯 해석 가중치 ·
    /// Warm = PlutoWarmCode · WarmSeries/WarmSimulations = 리플레이 종목 수/가상 리플레이 수(WarmReady 만 양수).
    /// </summary>
    public readonly record struct PlutoTensorLaneView(int Asset, int Source, long Generation, int MomentumTiltPct, int DevilStrictnessPts, int Warm, int WarmSeries, int WarmSimulations)
    {
        public static string AssetLabel(int a) => a switch { PlutoRecoMarket.Us => "US", PlutoRecoMarket.Crypto => "Upbit", _ => "KRX" };
    }

    /// <summary>[v12.18.86-DEV] 36슬롯 인지 텐서 활성 상태(ActiveRegimeId 0 = 판정 전 · ActiveRegimeName null = 판정 전) · Lanes = 3대 자산군(KRX · US · Upbit 순).</summary>
    public sealed record PlutoTensorView(int ActiveRegimeId, string? ActiveRegimeName, int CalibratedSlots, IReadOnlyList<PlutoTensorLaneView> Lanes);

    /// <summary>
    /// 학습 상태 1건. Running = 백그라운드 스웜 가동 · Health = PlutoLearningHealth · EpisodeName/SimilarityPct = 최근접 과거 국면(null = 판정 불가) ·
    /// EpisodeIsSeed = 시드 근사 에피소드 · Generation = 가중치 핫스왑 세대(0 = 학습 전 중립) · MomentumTiltPct/DevilStrictnessPts = 현재 학습 가중치 ·
    /// Samples = 누적 청산 회고 표본 · Swaps/RejectedSwaps = 섀도우 게이트 통과/거부 누계 ·
    /// [v12.18.82-DEV] RegimeName/RegimeSimilarityX10 = 12대 역사적 국면 판정(null/−1 = 판정 전) · RegimeNote = 기간·해설 · RegimeEffect = 전략·가중치 편향 문구.
    /// [v12.18.83-DEV] MacroFeedText = 거시 피드 결선 문구(null = 미표기).
    /// [v12.18.84-DEV] LifetimeEpisodes = 평생 누적 학습 에피소드(−1 = 영속화 미결선) · PersistState = PlutoLearningPersist.
    /// [v12.18.85-DEV] Shadow = 섀도우 검증 요약(인지 브리핑 · default = 판정 전).
    /// [v12.18.86-DEV] Tensor = 36슬롯 인지 텐서 활성 슬롯 가중치 · 자산군별 웜스타트 상태(null = 텐서 미결선).
    /// [v12.18.92-DEV] EvolveAnchor = 직전 진화 심사 시점 누적 표본(−1 = 미상).
    /// </summary>
    public readonly record struct PlutoLearningView(
        bool Running, int Health, string? EpisodeName, int SimilarityPct, bool EpisodeIsSeed,
        long Generation, int MomentumTiltPct, int DevilStrictnessPts, long Samples, long Swaps, long RejectedSwaps,
        string? RegimeName = null, int RegimeSimilarityX10 = -1, string? RegimeNote = null, string? RegimeEffect = null, string? MacroFeedText = null,
        long LifetimeEpisodes = -1, int PersistState = PlutoLearningPersist.Off, PlutoShadowView Shadow = default, PlutoTensorView? Tensor = null,
        long EvolveAnchor = -1)
    {
        /// <summary>[v12.18.92-DEV] 다음 진화 심사까지 표본 진행도.</summary>
        public PlutoEvolutionProgress Evolution => PlutoEvolutionEta.Of(Samples, EvolveAnchor);

        public bool HasMatch => EpisodeName is not null && SimilarityPct >= 0;

        public bool HasRegime => RegimeName is not null && RegimeSimilarityX10 is >= 0 and <= 1000;

        /// <summary>국면 뱃지: [🧠 과거 국면 매칭: {이름} (유사도 91.4%)] · 판정 전 = "[🧠 과거 국면 매칭: 판정 전]".</summary>
        public string RegimeBadgeText => HasRegime
            ? $"[🧠 과거 국면 매칭: {RegimeName} (유사도 {RegimeSimilarityX10 / 10}.{RegimeSimilarityX10 % 10}%)]"
            : "[🧠 과거 국면 매칭: 판정 전]";

        /// <summary>영속화 뱃지: [🧠 누적 학습 세션: N회 누적 (영속화 가동 중)] · 미결선 = "[🧠 누적 학습 세션: 영속화 미결선]".</summary>
        public string PersistBadgeText => PlutoLearningPersist.Normalize(PersistState) == PlutoLearningPersist.Off || LifetimeEpisodes < 0
            ? "[🧠 누적 학습 세션: 영속화 미결선]"
            : $"[🧠 누적 학습 세션: {LifetimeEpisodes}회 누적 ({PlutoLearningPersist.Label(PersistState)})]";

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

        /// <summary>[v12.18.92-DEV] 39셀 인지 텐서 콜드 스냅샷(국면 행 1~12 → 기준선 행 순 · 자산 KRX · US · Upbit 순) · 텐서 미결선 = 빈 목록.</summary>
        IReadOnlyList<PlutoTensorCellView> ReadTensorCells() => Array.Empty<PlutoTensorCellView>();

        /// <summary>[v12.18.93-DEV] 자율 학습 가속기 결선 여부(미결선 = 버튼 비활성 · 정직 문구).</summary>
        bool CanFastReplay => false;

        /// <summary>[v12.18.93-DEV] 직전 가속 리플레이 결과(미실행 = Idle · 미결선 = Off).</summary>
        PlutoReplayRunView LastReplay => PlutoReplayRunView.Unwired;

        /// <summary>[v12.18.93-DEV] 가속 리플레이 1회(백그라운드 · 실행 중 재호출 = 진행 중 결과 공유 · 미결선 = Off 즉시 반환).</summary>
        Task<PlutoReplayRunView> RunFastReplayAsync() => Task.FromResult(PlutoReplayRunView.Unwired);
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
