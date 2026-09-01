// ============================================================================
// IntelPorts — Phase 94-O: IntelHub(AI/Advisor/Debate/Journal) Ports
//   순수 UI 를 엔진 구상(AiService·ComplianceGate·DebateService/DebateLog·CausalJournal·결과 DTO)에서 격리.
//   ★liveArmed:false: 정보 제공/분석 전용(실 발주 무접촉). ★컴플라이언스 게이트 = 세션 sticky 락(싱글턴 필수).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket

namespace MS2E.UI.Portable.Ports.Intel
{
    // ── ① AI 어시스턴트(AiService 격리) ──────────────────────────────────────
    public interface IMemoryAnswerView
    {
        string Answer { get; }
        IReadOnlyList<string> Snippets { get; }
    }

    public interface IAiAssistantPort
    {
        bool HasKey { get; }
        bool HasLlm { get; }
        string ActiveLlm { get; }
        Task<string> AnalyzeAsync(string symbol, string market, string model);
        Task<string> AskAsync(string question, decimal usdKrw);
        Task<IMemoryAnswerView> AskWithMemoryAsync(string question, decimal usdKrw);
        Task<string> MorningBriefAsync(decimal usdKrw);
        Task<string> ExplainDayAsync(string dayContext, string model);
        // 아침 브리핑 스케줄 설정(LocalDatabase 격리).
        bool GetBriefEnabled();
        void SetBriefEnabled(bool on);
        int GetBriefHour();
        void SetBriefHour(int hour);
    }

    // ── ② 컴플라이언스 게이트(세션 sticky 락 — 싱글턴) ─────────────────────────
    public interface IComplianceGatePort
    {
        bool IsLocked { get; }
        /// <summary>질문 의도 분류 + Admit. 비정상 의도(확정수익/유사자문/탈옥)면 sticky 락 + false(fail-closed).</summary>
        bool AdmitQuestion(string question);
        string RefusalNotice { get; }
    }

    // ── ③ 인과 저널(CausalJournal 격리) ──────────────────────────────────────
    public interface IJournalEventView
    {
        DateTime When { get; }
        string Icon { get; }
        string KindLabel { get; }
        string Title { get; }
        string Detail { get; }
    }

    public interface IJournalDayView
    {
        DateTime Date { get; }
        decimal? EquityChangePct { get; }
        string Annotation { get; set; }   // LLM 인과 주석(페이지가 채움 — 가변)
        IReadOnlyList<IJournalEventView> Events { get; }
    }

    public interface IJournalPort
    {
        IReadOnlyList<IJournalDayView> BuildTimeline(int rangeDays, string? symbol);
        /// <summary>그날 문맥 조립 + LLM 인과 복기. 반환=주석(페이지가 day.Annotation 에 세팅).</summary>
        Task<string> ExplainDayAsync(IJournalDayView day, string model);
    }

    // ── ④ 멀티에이전트 토론(DebateService/DebateLog 격리) ──────────────────────
    public interface IBullBearRecordView
    {
        int BullishN { get; }
        int BearishN { get; }
        double BullishHitPct { get; }
        double BearishHitPct { get; }
    }

    public interface IDebateResultView
    {
        bool Ok { get; }
        string Error { get; }
        string Context { get; }
        string BullText { get; }
        string BearText { get; }
        string JudgeText { get; }
        int Stance { get; }
        string StanceLabel { get; }
    }

    public interface IDebatePort
    {
        int PendingCount();
        IBullBearRecordView Stats();
        /// <summary>시세 fetch + 3에이전트 토론 + 판정 기록(어댑터 내재화).</summary>
        Task<IDebateResultView> RunAsync(string symbol, PfMarket market, string model);
        /// <summary>만기 판정 정산(어댑터: Load + 시세 fetch + ResolveDue).</summary>
        Task ResolveAsync();
    }
}
