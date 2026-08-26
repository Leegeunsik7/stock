// ============================================================================
// RebalanceDrift - MPT 리밸런싱 드리프트 트리거 🄸 (외부 의존 0) (v3.33.0)
//  /portfolio 리밸런싱 탭에서 저장한 '타겟 비중'과 현재 비중의 괴리(드리프트)를 계산한다.
//  백그라운드 엔진이 주기적으로 점검해 밴드(예 ±5%p) 이탈 시 "리밸런싱 시점" 알림.
//  순수·결정적. 키 합집합 기준(타겟에만 있거나 현재에만 있는 종목 = 반대쪽 0).
//
//  지표:
//   · MaxDriftPct = max|현재−타겟| (%p) — 단일 종목 최대 이탈
//   · TurnoverPct = Σ|현재−타겟|/2 (%) — 타겟 복귀에 필요한 총 매매 회전율
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace StockManager.Common
{
    public class DriftRow
    {
        public string Symbol { get; set; } = "";
        public double CurrentPct { get; set; }   // %
        public double TargetPct { get; set; }    // %
        public double DiffPct => CurrentPct - TargetPct;  // +면 과다보유, −면 부족
    }

    public class DriftReport
    {
        public bool HasTarget { get; set; }
        public double MaxDriftPct { get; set; }     // %p
        public string MaxSymbol { get; set; } = "";
        public double TurnoverPct { get; set; }     // %
        public List<DriftRow> Rows { get; set; } = new();
    }

    public static class RebalanceDrift
    {
        /// <summary>현재 vs 타겟 비중(0~1 분수) 괴리 계산. 키 합집합, 없는 쪽은 0.</summary>
        public static DriftReport Compute(IReadOnlyDictionary<string, double> current,
            IReadOnlyDictionary<string, double> target)
        {
            var rep = new DriftReport();
            if (target == null || target.Count == 0) return rep;
            rep.HasTarget = true;

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in target.Keys) keys.Add(k);
            if (current != null) foreach (var k in current.Keys) keys.Add(k);

            double sumAbs = 0;
            foreach (var k in keys)
            {
                double cur = current != null && current.TryGetValue(k, out var c) ? c : 0;
                double tgt = target.TryGetValue(k, out var t) ? t : 0;
                rep.Rows.Add(new DriftRow { Symbol = k, CurrentPct = cur * 100.0, TargetPct = tgt * 100.0 });
                sumAbs += Math.Abs(cur - tgt);
            }
            rep.Rows = rep.Rows.OrderByDescending(r => Math.Abs(r.DiffPct)).ToList();
            var top = rep.Rows.First();
            rep.MaxDriftPct = Math.Abs(top.DiffPct);
            rep.MaxSymbol = top.Symbol;
            rep.TurnoverPct = sumAbs / 2.0 * 100.0;
            return rep;
        }

        public static bool Breached(DriftReport d, double bandPct) => d.HasTarget && d.MaxDriftPct >= bandPct;
    }
}
