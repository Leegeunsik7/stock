// ============================================================================
// CompositeRuleCrudPort — Phase 94-H: 복합 알림 규칙 CRUD Port & 편집 드래프트(순수 UI 경계)
//
//   CompositeAlertsPage(가변 규칙 편집기)를 엔진 구상 도메인(CompositeAlertService 정적 싱글턴 · CompositeRule/
//   CompositeCondition · CondMetric/CondOp/RuleLogic · AiService)에서 물리 격리한다. 94-F 의 읽기 전용
//   ICompositeAlertPort(주입/조회) 와 달리, 이 Port 는 '가변 편집(CRUD)' 을 담당한다 — 읽기/명령 분리.
//
//   ★핵심 기법 — '가변 드래프트 뷰모델'(Admin/Mail 이관 교훈):
//     페이지는 도메인 CompositeRule 을 직접 @bind 하지 않는다. RCL 이 소유하는 가변 드래프트(CompositeRuleDraft/
//     CompositeConditionDraft)에 @bind 하고, 저장 시 어댑터가 드래프트→도메인으로 매핑한다(구상 도메인 무유입).
//     표 렌더는 읽기 전용 ICompositeRuleRow 로만 투사(도메인 enum/타입 무유입).
//
//   ★liveArmed:false: 알림 규칙 조립/저장/평가 — 발주·소켓·브로커 무접촉(fail-closed: 어댑터 예외 시 빈 목록/무동작).
//   ★열거형 정수값 정렬(엔진↔RCL): CondMetric↔RuleMetric · CondOp↔RuleCompareOp · RuleLogic↔RuleJoin.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Alerts
{
    /// <summary>조건 지표 — 엔진 CondMetric 의 순수 UI 대응(정수값 정렬).</summary>
    public enum RuleMetric { Price = 0, ChangePct = 1, MaGapPct = 2, Rsi = 3, VolumeSurgePct = 4 }

    /// <summary>비교 연산 — 엔진 CondOp 의 순수 UI 대응(정수값 정렬).</summary>
    public enum RuleCompareOp { Gte = 0, Lte = 1 }

    /// <summary>규칙 결합 논리 — 엔진 RuleLogic 의 순수 UI 대응(정수값 정렬). (엔진 RuleLogic 과 이름 분리.)</summary>
    public enum RuleJoin { And = 0, Or = 1 }

    /// <summary>편집 가능한 조건 드래프트(가변 뷰모델). 페이지가 직접 @bind. 어댑터가 도메인으로 매핑.</summary>
    public sealed class CompositeConditionDraft
    {
        public RuleMetric Metric { get; set; } = RuleMetric.Price;
        public int Param { get; set; } = 20;                 // MA/RSI 기간
        public RuleCompareOp Op { get; set; } = RuleCompareOp.Gte;
        public decimal Threshold { get; set; }

        private string OpKr => Op == RuleCompareOp.Gte ? "≥" : "≤";

        /// <summary>엔진 CompositeCondition.Describe() 와 동일 포맷(편집 중 실시간 서술).</summary>
        public string Describe() => Metric switch
        {
            RuleMetric.Price         => $"현재가 {OpKr} {Threshold:N0}",
            RuleMetric.ChangePct     => $"등락률 {OpKr} {Threshold:0.##}%",
            RuleMetric.MaGapPct      => $"MA{Param} 괴리 {OpKr} {Threshold:0.##}%",
            RuleMetric.Rsi           => $"RSI{Param} {OpKr} {Threshold:0.#}",
            RuleMetric.VolumeSurgePct => $"거래량 증가 {OpKr} {Threshold:0.#}%",
            _ => ""
        };
    }

    /// <summary>편집 가능한 규칙 드래프트(가변 뷰모델).</summary>
    public sealed class CompositeRuleDraft
    {
        public string Id { get; set; } = "";                 // 빈 문자열 = 신규(어댑터가 발번)
        public string Name { get; set; } = "새 규칙";
        public string AssetKind { get; set; } = "coin";      // "coin" | "stock"
        public string Target { get; set; } = "KRW-BTC";
        public RuleJoin Logic { get; set; } = RuleJoin.And;
        public List<CompositeConditionDraft> Conditions { get; set; } = new();
        public bool Enabled { get; set; } = true;
        public int CooldownMin { get; set; } = 30;
    }

    /// <summary>표 렌더용 읽기 전용 규칙 뷰(도메인 타입 무유입).</summary>
    public interface ICompositeRuleRow
    {
        string Id { get; }
        string Name { get; }
        string AssetKind { get; }
        string Target { get; }
        bool IsAndLogic { get; }
        IReadOnlyList<string> ConditionSummaries { get; }
        DateTime? LastFired { get; }
        int CooldownMin { get; }
        bool Enabled { get; }
    }

    /// <summary>복합 알림 규칙 CRUD Port — 가변 편집은 드래프트로, 조회는 읽기 뷰로.</summary>
    public interface ICompositeRuleCrudPort
    {
        int RuleCount { get; }
        int EnabledCount { get; }
        DateTime LastRun { get; }

        /// <summary>현재 규칙 목록(읽기 전용 뷰).</summary>
        IReadOnlyList<ICompositeRuleRow> ListRows();

        /// <summary>신규 규칙 드래프트(기본값).</summary>
        CompositeRuleDraft NewDraft();

        /// <summary>기존 규칙을 편집용 드래프트로 복제(없으면 null).</summary>
        CompositeRuleDraft? EditDraft(string ruleId);

        /// <summary>드래프트를 도메인으로 upsert + 영속(Id 빈 값=신규).</summary>
        void Save(CompositeRuleDraft draft);

        void Delete(string ruleId);
        void Toggle(string ruleId);
        Task EvaluateNowAsync();

        /// <summary>AI(LLM) 키 존재 여부 — 자연어→규칙 버튼 활성 판정.</summary>
        bool AiHasKey { get; }

        /// <summary>자연어를 규칙 드래프트로 컴파일(검토 후 저장). 실패 시 (null, error).</summary>
        Task<(CompositeRuleDraft? draft, string? error)> CompileNaturalLanguageAsync(string text);

        /// <summary>평가/저장 후 UI 갱신 이벤트.</summary>
        event Action? Evaluated;
    }
}
