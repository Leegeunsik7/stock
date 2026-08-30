// ============================================================================
// CompositeRuleCrudAdapter — Phase 94-H: 복합 알림 규칙 CRUD Port 호스트 어댑터 (DIP 구현)
//   순수 UI 의 ICompositeRuleCrudPort 를 엔진 구상(CompositeAlertService 정적 싱글턴 + AiService)에 결속한다.
//
//   ★가변 드래프트 ↔ 도메인 매핑: 페이지는 RCL CompositeRuleDraft/CompositeConditionDraft 만 편집하고, 저장 시
//     이 어댑터가 도메인 CompositeRule/CompositeCondition 으로 매핑한다(구상 도메인·enum 이 RCL 로 유입되지 않음).
//   ★읽기 뷰 투영: ListRows 는 CompositeRule 을 ICompositeRuleRow 로만 투사(도메인 타입 무유입).
//   ★수명: CompositeAlertService.Instance 는 프로세스 전역 싱글턴 → 어댑터도 싱글턴(Evaluated 구독 1회, 누수 없음).
//   ★liveArmed:false / fail-closed: 규칙 조립·저장·평가 전용(발주·소켓·브로커 무접촉).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Alerts;

namespace MS2E.Infrastructure
{
    public sealed class CompositeRuleCrudAdapter : ICompositeRuleCrudPort
    {
        private readonly CompositeAlertService _svc = CompositeAlertService.Instance;
        private readonly AiService _ai;

        public CompositeRuleCrudAdapter(AiService ai)
        {
            _ai = ai;
            _svc.Evaluated += () => Evaluated?.Invoke();   // 엔진 평가/저장 → 포트 이벤트 재발화(싱글턴 수명)
        }

        public int RuleCount => _svc.Rules.Count;
        public int EnabledCount => _svc.Rules.Count(r => r.Enabled);
        public DateTime LastRun => _svc.LastRun;

        public IReadOnlyList<ICompositeRuleRow> ListRows()
        {
            var rows = new List<ICompositeRuleRow>(_svc.Rules.Count);
            foreach (var r in _svc.Rules) rows.Add(new Row(r));
            return rows;
        }

        public CompositeRuleDraft NewDraft() => new()
        {
            Id = "", Name = "새 규칙", AssetKind = "coin", Target = "KRW-BTC",
            Logic = RuleJoin.And, Enabled = true, CooldownMin = 30,
            Conditions = new() { new CompositeConditionDraft { Metric = RuleMetric.Rsi, Param = 14, Op = RuleCompareOp.Lte, Threshold = 30 } }
        };

        public CompositeRuleDraft? EditDraft(string ruleId)
        {
            var r = _svc.Rules.FirstOrDefault(x => x.Id == ruleId);
            return r is null ? null : ToDraft(r);
        }

        public void Save(CompositeRuleDraft draft)
        {
            if (draft is null) return;   // fail-closed
            var rule = ToDomain(draft);
            if (string.IsNullOrEmpty(draft.Id))
            {
                _svc.Rules.Add(rule);
            }
            else
            {
                var i = _svc.Rules.FindIndex(x => x.Id == draft.Id);
                if (i >= 0) _svc.Rules[i] = rule; else _svc.Rules.Add(rule);
            }
            _svc.Persist();   // 저장 + Evaluated 이벤트
        }

        public void Delete(string ruleId)
        {
            if (_svc.Rules.RemoveAll(x => x.Id == ruleId) > 0) _svc.Persist();
        }

        public void Toggle(string ruleId)
        {
            var r = _svc.Rules.FirstOrDefault(x => x.Id == ruleId);
            if (r is null) return;
            r.Enabled = !r.Enabled;
            _svc.Persist();
        }

        public Task EvaluateNowAsync() => _svc.EvaluateNowAsync();

        public bool AiHasKey => _ai.HasKey;

        public async Task<(CompositeRuleDraft? draft, string? error)> CompileNaturalLanguageAsync(string text)
        {
            var (rule, err) = await _ai.CompileRuleAsync((text ?? "").Trim()).ConfigureAwait(false);
            return rule is null ? (null, err) : (ToDraft(rule), null);
        }

        public event Action? Evaluated;

        // ── 매핑 (RCL 드래프트 ↔ 엔진 도메인, 열거형 정수값 정렬 캐스팅) ──────────────
        private static CompositeRuleDraft ToDraft(CompositeRule r) => new()
        {
            Id = r.Id, Name = r.Name, AssetKind = r.AssetKind, Target = r.Target,
            Logic = (RuleJoin)(int)r.Logic, Enabled = r.Enabled, CooldownMin = r.CooldownMin,
            Conditions = r.Conditions.Select(c => new CompositeConditionDraft
            {
                Metric = (RuleMetric)(int)c.Metric, Param = c.Param,
                Op = (RuleCompareOp)(int)c.Op, Threshold = c.Threshold
            }).ToList()
        };

        private static CompositeRule ToDomain(CompositeRuleDraft d)
        {
            var rule = new CompositeRule
            {
                Name = string.IsNullOrWhiteSpace(d.Name) ? "새 규칙" : d.Name,
                AssetKind = d.AssetKind, Target = d.Target,
                Logic = (RuleLogic)(int)d.Logic, Enabled = d.Enabled, CooldownMin = d.CooldownMin,
                Conditions = d.Conditions.Select(c => new CompositeCondition
                {
                    Metric = (CondMetric)(int)c.Metric, Param = c.Param,
                    Op = (CondOp)(int)c.Op, Threshold = c.Threshold
                }).ToList()
            };
            if (!string.IsNullOrEmpty(d.Id)) rule.Id = d.Id;   // 편집: 기존 Id 보존(신규는 도메인 기본 Guid)
            return rule;
        }

        private sealed class Row : ICompositeRuleRow
        {
            private readonly CompositeRule _r;
            public Row(CompositeRule r) { _r = r; }
            public string Id => _r.Id;
            public string Name => _r.Name;
            public string AssetKind => _r.AssetKind;
            public string Target => _r.Target;
            public bool IsAndLogic => _r.Logic == RuleLogic.And;
            public IReadOnlyList<string> ConditionSummaries => _r.Conditions.Select(c => c.Describe()).ToList();
            public DateTime? LastFired => _r.LastFired;
            public int CooldownMin => _r.CooldownMin;
            public bool Enabled => _r.Enabled;
        }
    }
}
