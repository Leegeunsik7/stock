// ============================================================================
// ToolPorts — v12.0.0 Phase 94-P: 잔여 도구 페이지(TimeMachine/Research/Copilot/Cartridge) Ports
//   순수 UI 를 엔진 구상(TimeMachine·NightlyResearch·CopilotEngine/Store·CartridgeService/Engine·LocalDatabase·
//   Process.Start)에서 격리(래칫 ≤40 격파용 잔여 이주). ★liveArmed:false: 정보/도구 전용(실 발주 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket

namespace MS2E.UI.Portable.Ports.Tools
{
    // ── 공용: 폴더 열기(Process.Start 격리) ────────────────────────────────────
    public interface IFolderOpenPort
    {
        void OpenFolder(string path);
    }

    // ── ① 타임머신 샌드박스 ────────────────────────────────────────────────────
    public interface ITimeMachineResultView
    {
        bool Valid { get; } string Note { get; }
        DateTime AsOf { get; } decimal AsOfClose { get; } int AsOfBars { get; }
        double RsiAsOf { get; } double MaGapPct { get; } string Regime { get; }
        string OptimizerWinner { get; } double OptimizerReturnPct { get; }
        int Stance { get; } string StanceLabel { get; }
        bool Graded { get; } double RealizedPct { get; } int FwdBars { get; } bool Hit { get; }
    }
    public interface ITimeMachinePort
    {
        Task<ITimeMachineResultView> ReplayAsync(string symbol, PfMarket market, DateTime asOf, int horizon);
    }

    // ── ② 퀀트 코파일럿 ────────────────────────────────────────────────────────
    public interface ICopilotPolicyView { string Name { get; } int Kind { get; } bool Enabled { get; } }
    public interface ICopilotProposalView { string Title { get; } string Detail { get; } }
    public interface ICopilotCtxView { bool Valid { get; } double PortfolioVolPct { get; } double DrawdownPct { get; } string Regime { get; } }

    public interface ICopilotPort
    {
        bool GetEnabled();
        void SetEnabled(bool on);
        IReadOnlyList<ICopilotPolicyView> GetPolicies();
        /// <summary>자연어 목표 추가 — 반환=상태 메시지("" 아님; 인식 실패 시 안내).</summary>
        string AddFromNl(string nl);
        string AddManual(string kind, double threshold);
        void TogglePolicy(int index, bool enabled);
        void RemovePolicy(int index);
        (ICopilotCtxView ctx, IReadOnlyList<ICopilotProposalView> proposals) Evaluate();
    }

    // ── ③ 야간 연구 일지 ───────────────────────────────────────────────────────
    public interface IResearchFindingView
    {
        string Symbol { get; } string Name { get; } int Short { get; } int Long { get; }
        double OosMeanPct { get; } double OosStdPct { get; } double Score { get; } int Trades { get; }
        bool HasCone { get; } double ProjExpPct { get; } double ProbLossPct { get; } double GoalHitPct { get; }
    }
    public interface IResearchBriefView
    {
        string GeneratedIso { get; } int Scanned { get; } int Found { get; } double GoalPct { get; } string CartridgeFile { get; }
        string Headline { get; }
        IReadOnlyList<IResearchFindingView> Findings { get; }
        IReadOnlyList<string> Proposals { get; }
    }
    public interface IResearchPort
    {
        (bool enabled, bool telegram, int hour, double goal) LoadSettings();
        void SaveSettings(bool enabled, bool telegram, int hour, double goal);
        bool IsRunning { get; }
        IResearchBriefView? LoadLatest();
        IReadOnlyList<IResearchBriefView> LoadHistory();
        Task<IResearchBriefView?> RunAsync(double goal, System.Threading.CancellationToken ct);
    }

    // ── ④ 전략 카트리지(서명·증명서 교환) ──────────────────────────────────────
    public interface ICartStrategyView
    {
        string Name { get; } int Seed { get; }
        double AttestTotalReturnPct { get; } double AttestMaxDrawdownPct { get; } int AttestTrades { get; }
        bool Reproduced { get; }   // CartridgeEngine.VerifyStrategy 재현 결과(가져오기 표시용)
    }
    public interface ICartridgeVerdictView { bool SignatureOk { get; } bool AttestationOk { get; } bool FullyTrusted { get; } string Note { get; } }
    public interface ICartridgeInfoView
    {
        string Name { get; } string Author { get; } string CreatedIso { get; } string AuthorPubKey { get; }
        IReadOnlyList<ICartStrategyView> Strategies { get; }
        IReadOnlyList<string> MacroAliases { get; }
        int MacroCount { get; }
    }
    /// <summary>전략 카트리지 Port — 서명·증명서 무신뢰 교환(CartridgeService/Engine 격리). ★상태 보유(드래프트·가져온 카트리지) → scoped.</summary>
    public interface ICartridgePort
    {
        string EnsureIdentity();
        string Author();
        int MacroCount();
        IReadOnlyList<string> ListFiles();
        IReadOnlyList<ICartStrategyView> Drafts { get; }
        string AddDraft(int shortMa, int longMa, int seed);
        void RemoveDraft(int index);
        string Export(string name, string desc);
        (ICartridgeInfoView? cart, ICartridgeVerdictView? verdict, string err) Import(string file);
        string ApplyMacros();
        void OpenFolder();
    }
}
