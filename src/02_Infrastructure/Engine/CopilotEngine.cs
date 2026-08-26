// ============================================================================
// CopilotEngine - 목표주도 퀀트 코파일럿 (Season 4 🅰) (v3.37.0)
//  임계값 알림을 넘어 "목표를 추구"한다. 사용자가 정책(목표)을 정하면 백그라운드가
//  포트폴리오 상태(변동성·낙폭·레짐)를 지속 평가해 행동을 '제안'한다.
//   ⚠️ 게이팅: 제안·알림만. 실주문 자동 집행은 절대 안 한다(우리 하드 라인).
//  BuildCtx(자산곡선→상태)·Evaluate(정책→제안)·ParseGoal(NL→정책)은 모두 순수·테스트 가능.
//  컨텍스트는 Dashboard.Snapshots(엔진이 적재한 일별 총자산)에서 산출 → 네트워크 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace StockManager.Common
{
    public enum PolicyKind { VolTarget, MaxDrawdown, RegimeGuard }

    public class CopilotPolicy
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..6];
        public string Name { get; set; } = "";
        public PolicyKind Kind { get; set; }
        public double Threshold { get; set; }   // VolTarget/MaxDrawdown = %, RegimeGuard 미사용
        public bool Enabled { get; set; } = true;
        public string LastFired { get; set; } = "";
    }

    public class CopilotProposal
    {
        public string PolicyId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string ActionLink { get; set; } = "";
    }

    public static class CopilotEngine
    {
        public class Ctx
        {
            public bool Valid;
            public double PortfolioVolPct;   // 연환산
            public double DrawdownPct;        // 현재 고점대비(음수)
            public RegimeKind Regime = RegimeKind.None;
        }

        /// <summary>일별 총자산 시계열 → 변동성·낙폭·레짐(순수, 네트워크 0).</summary>
        public static Ctx BuildCtx(IReadOnlyList<decimal> equity)
        {
            var ctx = new Ctx();
            if (equity == null || equity.Count < 3) return ctx;
            var e = equity.Select(x => (double)x).ToList();

            // 일수익률 → 연환산 변동성
            var rets = new List<double>();
            for (int i = 1; i < e.Count; i++) if (e[i - 1] != 0) rets.Add((e[i] - e[i - 1]) / e[i - 1] * 100.0);
            if (rets.Count >= 2)
            {
                double m = rets.Average(), ss = 0;
                foreach (var r in rets) ss += (r - m) * (r - m);
                ctx.PortfolioVolPct = Math.Sqrt(ss / (rets.Count - 1)) * Math.Sqrt(252.0);
            }

            // 고점 대비 최대/현재 낙폭(가장 최근 시점 기준 — 현재 낙폭)
            double peak = e[0], minDd = 0;
            foreach (var v in e) { if (v > peak) peak = v; if (peak > 0) { var dd = (v - peak) / peak * 100.0; if (dd < minDd) minDd = dd; } }
            double cur = e[^1]; ctx.DrawdownPct = peak > 0 ? (cur - peak) / peak * 100.0 : 0;

            ctx.Regime = RegimeDetector.Analyze(equity.ToList()).Kind;
            ctx.Valid = true;
            return ctx;
        }

        /// <summary>정책 + 상태 → 행동 제안(순수). 게이팅: 제안만, 주문 없음.</summary>
        public static List<CopilotProposal> Evaluate(IEnumerable<CopilotPolicy> policies, Ctx ctx)
        {
            var outp = new List<CopilotProposal>();
            if (ctx == null || !ctx.Valid) return outp;
            foreach (var p in (policies ?? Enumerable.Empty<CopilotPolicy>()).Where(x => x.Enabled))
            {
                switch (p.Kind)
                {
                    case PolicyKind.VolTarget when ctx.PortfolioVolPct > p.Threshold:
                        outp.Add(new CopilotProposal { PolicyId = p.Id, Title = $"변동성 목표 초과 · {p.Name}",
                            Detail = $"포트 변동성 {ctx.PortfolioVolPct:0.0}% > 목표 {p.Threshold:0.0}% — 리밸런싱으로 위험 축소 검토",
                            ActionLink = "/portfolio/rebalance" });
                        break;
                    case PolicyKind.MaxDrawdown when ctx.DrawdownPct <= -Math.Abs(p.Threshold):
                        outp.Add(new CopilotProposal { PolicyId = p.Id, Title = $"낙폭 한도 도달 · {p.Name}",
                            Detail = $"현재 낙폭 {ctx.DrawdownPct:0.0}% ≤ -{Math.Abs(p.Threshold):0.0}% — 헷지/현금 비중 확대 검토",
                            ActionLink = "/portfolio/risk" });
                        break;
                    case PolicyKind.RegimeGuard when ctx.Regime is RegimeKind.VolatilitySpike or RegimeKind.MeanShiftDown or RegimeKind.ReturnOutlier:
                        outp.Add(new CopilotProposal { PolicyId = p.Id, Title = $"레짐 경고 · {p.Name}",
                            Detail = $"포트 레짐 변화({ctx.Regime}) — 포지션·헷지 점검",
                            ActionLink = "/risklab/scenario" });
                        break;
                }
            }
            return outp;
        }

        /// <summary>자연어 목표 → 정책(순수 키워드 파싱, LLM 불필요·오프라인). 인식 실패 시 null.</summary>
        public static CopilotPolicy? ParseGoal(string nl)
        {
            if (string.IsNullOrWhiteSpace(nl)) return null;
            var s = nl.Trim();
            double num = ExtractNumber(s);
            if (s.Contains("변동성") || s.Contains("vol", StringComparison.OrdinalIgnoreCase))
                return new CopilotPolicy { Kind = PolicyKind.VolTarget, Threshold = num > 0 ? num : 15, Name = $"변동성 {(num > 0 ? num : 15):0}% 이하" };
            if (s.Contains("낙폭") || s.Contains("MDD", StringComparison.OrdinalIgnoreCase) || s.Contains("드로다운"))
                return new CopilotPolicy { Kind = PolicyKind.MaxDrawdown, Threshold = num > 0 ? num : 10, Name = $"낙폭 -{(num > 0 ? num : 10):0}% 방어" };
            if (s.Contains("레짐") || s.Contains("regime", StringComparison.OrdinalIgnoreCase) || s.Contains("급변"))
                return new CopilotPolicy { Kind = PolicyKind.RegimeGuard, Name = "레짐 변화 가드" };
            return null;
        }

        private static double ExtractNumber(string s)
        {
            var sb = new System.Text.StringBuilder();
            bool seen = false;
            foreach (var ch in s)
            {
                if (char.IsDigit(ch) || (ch == '.' && seen)) { sb.Append(ch); seen = true; }
                else if (seen) break;
            }
            return double.TryParse(sb.ToString(), out var v) ? v : 0;
        }
    }

    public static class CopilotStore
    {
        private const string Key = "Copilot.Policies";

        public static List<CopilotPolicy> Load(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting(Key);
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<List<CopilotPolicy>>(j) ?? new();
            }
            catch { }
            return new();
        }

        public static void Save(LocalDatabase db, List<CopilotPolicy> list)
        {
            try { db.SetSetting(Key, JsonSerializer.Serialize(list)); } catch { }
        }
    }
}
