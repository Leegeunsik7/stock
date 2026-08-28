// ============================================================================
// CompositeAlerts - 복합 조건 알림 엔진 (v3.2)
//  · 여러 조건(이동평균 괴리, 거래대금 급증, RSI, 현재가, 등락률)을 AND/OR로 결합.
//  · 조건 충족 시 AlertCenter로 알림(→ 텔레그램 자동전송). 쿨다운으로 중복 방지.
//  · 외부 의존성 없음. Settings 'CompositeAlerts.Rules' JSON 영속.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MS2E.UI.Portable.Ports.Strategy;   // Phase 94-F: 복합 알림 읽기 전용 계약(도메인 구상 은닉)

namespace MS2E.Infrastructure
{
    public enum CondMetric { Price, ChangePct, MaGapPct, Rsi, VolumeSurgePct }
    public enum CondOp { Gte, Lte }
    public enum RuleLogic { And, Or }

    public class CompositeCondition : ICondMetricView
    {
        public CondMetric Metric { get; set; } = CondMetric.Price;
        public int Param { get; set; } = 20;        // MA/RSI 기간
        public CondOp Op { get; set; } = CondOp.Gte;
        public decimal Threshold { get; set; }

        // Phase 94-F: 읽기 전용 계약(도메인 enum 미유입) — 라벨/서술 문자열로만 투사.
        public string MetricLabel => Metric switch
        {
            CondMetric.Price => "현재가",
            CondMetric.ChangePct => "등락률",
            CondMetric.MaGapPct => $"MA{Param} 괴리",
            CondMetric.Rsi => $"RSI{Param}",
            CondMetric.VolumeSurgePct => "거래량 증가",
            _ => ""
        };
        string ICondMetricView.Summary => Describe();

        public string Describe() => Metric switch
        {
            CondMetric.Price => $"현재가 {OpKr} {Threshold:N0}",
            CondMetric.ChangePct => $"등락률 {OpKr} {Threshold:0.##}%",
            CondMetric.MaGapPct => $"MA{Param} 괴리 {OpKr} {Threshold:0.##}%",
            CondMetric.Rsi => $"RSI{Param} {OpKr} {Threshold:0.#}",
            CondMetric.VolumeSurgePct => $"거래량 증가 {OpKr} {Threshold:0.#}%",
            _ => ""
        };
        private string OpKr => Op == CondOp.Gte ? "≥" : "≤";
    }

    public class CompositeRule : ICompositeRuleContract
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
        public string Name { get; set; } = "새 규칙";
        public string AssetKind { get; set; } = "coin";   // "coin" | "stock"
        public string Target { get; set; } = "KRW-BTC";    // 코인 마켓 또는 주식 심볼
        public RuleLogic Logic { get; set; } = RuleLogic.And;
        public List<CompositeCondition> Conditions { get; set; } = new();
        public bool Enabled { get; set; } = true;
        public int CooldownMin { get; set; } = 30;
        public DateTime? LastFired { get; set; }

        // Phase 94-F: 공변 업캐스트 — List<CompositeCondition> → IReadOnlyList<ICondMetricView>.
        IReadOnlyList<ICondMetricView> ICompositeRuleContract.Conditions => Conditions;
    }

    /// <summary>평가 입력: 시세 스냅샷 + 일봉 종가/거래량 시계열</summary>
    public class MarketCtx
    {
        public string Market = "";
        public decimal Price;
        public decimal ChangePct;          // 부호 포함 %
        public List<decimal> Closes = new();
        public List<decimal> Volumes = new();
    }

    public static class CompositeAlertEngine
    {
        public static List<CompositeRule> Load(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting("CompositeAlerts.Rules");
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<List<CompositeRule>>(j) ?? new();
            }
            catch { }
            return new List<CompositeRule>();
        }

        public static void Save(LocalDatabase db, List<CompositeRule> rules)
        {
            try { db.SetSetting("CompositeAlerts.Rules", JsonSerializer.Serialize(rules)); } catch { }
        }

        public static decimal Metric(CondMetric m, int param, MarketCtx c)
        {
            switch (m)
            {
                case CondMetric.Price: return c.Price;
                case CondMetric.ChangePct: return c.ChangePct;
                case CondMetric.MaGapPct:
                {
                    var ma = Ma(c.Closes, param);
                    return ma <= 0 ? 0 : (c.Price - ma) / ma * 100m;
                }
                case CondMetric.Rsi: return Rsi(c.Closes, param);
                case CondMetric.VolumeSurgePct:
                {
                    if (c.Volumes.Count < 2) return 0;
                    var today = c.Volumes[^1]; var prev = c.Volumes[^2];
                    return prev <= 0 ? 0 : (today - prev) / prev * 100m;
                }
                default: return 0;
            }
        }

        public static bool Evaluate(CompositeRule rule, MarketCtx c)
        {
            if (rule.Conditions.Count == 0) return false;
            bool Check(CompositeCondition cond)
            {
                var v = Metric(cond.Metric, cond.Param, c);
                return cond.Op == CondOp.Gte ? v >= cond.Threshold : v <= cond.Threshold;
            }
            return rule.Logic == RuleLogic.And ? rule.Conditions.All(Check) : rule.Conditions.Any(Check);
        }

        /// <summary>규칙 평가 후 충족+쿨다운 통과 시 알림 발생. 발생하면 true.</summary>
        public static bool TryFire(CompositeRule rule, MarketCtx c)
        {
            if (!rule.Enabled) return false;
            if (rule.LastFired is { } lf && (DateTime.Now - lf).TotalMinutes < rule.CooldownMin) return false;
            if (!Evaluate(rule, c)) return false;

            rule.LastFired = DateTime.Now;
            var detail = string.Join(rule.Logic == RuleLogic.And ? " 그리고 " : " 또는 ",
                rule.Conditions.Select(x => x.Describe()));
            AlertCenter.Instance.RaisePrice(rule.Target,
                $"복합 알림: {rule.Name}",
                $"{rule.Target} · {detail}  (현재가 {c.Price:N0})",
                AlertLevel.Critical);
            return true;
        }

        // ===== 지표 =====
        private static decimal Ma(List<decimal> closes, int n)
        {
            if (closes.Count < n || n <= 0) return 0;
            decimal s = 0; for (int i = closes.Count - n; i < closes.Count; i++) s += closes[i];
            return s / n;
        }

        private static decimal Rsi(List<decimal> closes, int n)
        {
            if (closes.Count <= n || n <= 0) return 0;
            decimal gain = 0, loss = 0;
            for (int i = closes.Count - n; i < closes.Count; i++)
            {
                var d = closes[i] - closes[i - 1];
                if (d >= 0) gain += d; else loss -= d;
            }
            if (loss == 0) return 100m;
            var rs = (gain / n) / (loss / n);
            return 100m - 100m / (1m + rs);
        }
    }
}
