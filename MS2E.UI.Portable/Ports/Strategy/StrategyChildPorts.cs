// ============================================================================
// StrategyChildPorts — Phase 94-O: StrategyHub 잔여 자식(Sizer/EventStudy/Miner) compute Port
//   순수 UI 를 엔진 구상(PositionSizer·EventStudy·HypothesisMiner·SampleData·Candle·결과 DTO)에서 격리.
//   ★compute 포트: 어댑터가 시세 fetch + 엔진 계산 + 결과 뷰 반환(94-C Quant 포트 패턴). 심볼/설정/알림은
//     기존 IStrategyHubInfraPort 재사용, 복합규칙 CRUD 는 ICompositeRuleCrudPort 재사용. ★liveArmed:false: 정보 제공 전용.
// ============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket

namespace MS2E.UI.Portable.Ports.Strategy
{
    // ── ① 포지션 사이저 ──────────────────────────────────────────────────────
    public interface ISizeResultView
    {
        bool IsValid { get; }
        string Note { get; }
        string Verdict { get; }
        double WinProbPct { get; }
        double PayoffRatio { get; }
        double ExpectancyPct { get; }
        double KellyFull { get; }
        double KellyFraction { get; }
        double KellyWeight { get; }
        double VolWeight { get; }
        double RecommendedWeight { get; }
        double AssetVolAnnualPct { get; }
    }

    /// <summary>포지션 사이저 Port — 어댑터가 24개월 시세 fetch + 엣지/변동성 추정 + PositionSizer.Suggest.</summary>
    public interface IPositionSizerPort
    {
        Task<ISizeResultView> SuggestAsync(string symbol, PfMarket market, int horizon, int targetVolPct, double kellyFrac, int maxWPct);
    }

    // ── ② 이벤트 스터디 ──────────────────────────────────────────────────────
    /// <summary>규칙 선택지(프리셋/저장된 복합규칙) — 어댑터가 CompositeRule 을 격리.</summary>
    public interface IRuleChoiceView
    {
        string Id { get; }          // "preset:0" 또는 "saved:{id}"
        string Label { get; }
        string Description { get; } // 조건 서술(Describe)
        bool IsPreset { get; }
    }

    public interface IEventStatView
    {
        int Days { get; }
        int N { get; }
        decimal MeanPct { get; }
        decimal MedianPct { get; }
        decimal WinRatePct { get; }
        decimal EdgePct { get; }
        decimal P5 { get; }
        decimal P95 { get; }
    }

    public interface IEventStudyResultView
    {
        bool IsValid { get; }
        int Occurrences { get; }
        int Scanned { get; }
        string Note { get; }
        IReadOnlyList<string> RecentDates { get; }
        IReadOnlyList<IEventStatView> Stats { get; }
    }

    /// <summary>이벤트 스터디 Port — 규칙 선택지 제공 + 어댑터가 규칙 해석·시세 fetch·EventStudy.Analyze.</summary>
    public interface IEventStudyPort
    {
        IReadOnlyList<IRuleChoiceView> GetRuleChoices();
        Task<IEventStudyResultView> AnalyzeAsync(string ruleId, string symbol, PfMarket market, int months);
    }

    // ── ③ 가설 발굴(마이너) ──────────────────────────────────────────────────
    public interface IHypothesisView
    {
        string Id { get; }          // 결과 내 index
        string Label { get; }
        double TrainEdgePct { get; }
        double OosEdgePct { get; }
        double OosWinRatePct { get; }
        int OosN { get; }
    }

    public interface IMiningResultView
    {
        bool IsValid { get; }
        int Tested { get; }
        int Survivors { get; }
        double SignificanceBar { get; }
        string Note { get; }
        IReadOnlyList<IHypothesisView> Pool { get; }
    }

    /// <summary>가설 발굴 Port — 어댑터가 시세 fetch·HypothesisMiner.Mine·채택(복합규칙 저장) 격리. Adopt 는 최근 발굴 풀(index) 사용.</summary>
    public interface IHypothesisMinerPort
    {
        Task<IMiningResultView> MineAsync(string symbol, PfMarket market, int months, int horizon);
        /// <summary>발굴 가설(index)을 복합 알림 규칙으로 채택·저장. 반환=토스트 제목·메시지.</summary>
        (string title, string message) Adopt(int hypothesisIndex, string symbol);
    }
}
