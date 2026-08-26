// ============================================================================
// DecisionMirror — 실시간 결정 품질 미러 (Season 9 #5, v3.68.0)
//  현재 보유 포지션 컨텍스트를 과거 행동편향 이력과 교차분석,
//  '지금 이 순간'의 의사결정 위험을 정량화한다.
//  순수 결정론 · 외부의존 0 · 매매 지시 없음(판단 보조 정보만)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    public enum AlertSeverity { Info, Warning, High }

    public sealed record BiasAlert(string BiasKey, string Label, string Evidence, AlertSeverity Severity);

    public sealed record HoldingSignal(
        string Symbol, string Name,
        double PnlPct, int HoldDays,
        int RiskScore,
        IReadOnlyList<BiasAlert> Alerts);

    public sealed record MirrorResult(
        IReadOnlyList<HoldingSignal> Signals,
        IReadOnlyList<BiasAlert> PortfolioAlerts,
        bool HasHistory,
        string Note);

    public static class DecisionMirror
    {
        /// <summary>
        /// 현재 보유 포지션 × 과거 행동편향 이력 교차분석.
        /// CoachReport.IsValid == false 이면 편향 비교 없이 구조적 알림만 반환.
        /// </summary>
        public static MirrorResult Analyze(
            IReadOnlyList<Holding> holdings,
            CoachReport report,
            DateTime asOf)
        {
            if (holdings == null || holdings.Count == 0)
                return new MirrorResult(
                    Array.Empty<HoldingSignal>(), Array.Empty<BiasAlert>(), false,
                    "보유 종목이 없습니다.");

            var biasSet = new HashSet<string>(
                report.IsValid
                    ? report.Insights.Select(i => i.Key)
                    : Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            // ── 포트폴리오 레벨 경고 (per-holding 과 독립적으로 1회) ──
            var portAlerts = new List<BiasAlert>();
            if (biasSet.Contains("overtrading"))
                portAlerts.Add(new BiasAlert("overtrading", "포트폴리오 과매매 경향",
                    $"과거 이력 기준 월 {report.Stats.TradesPerMonth:0.0}회 — 새 매매 전 충동성 여부를 점검하세요.",
                    AlertSeverity.Warning));

            // ── 보유 종목별 분석 ──
            var signals = new List<HoldingSignal>();
            foreach (var h in holdings)
            {
                if (h.Quantity <= 0 || h.AvgPrice <= 0) continue;

                var pnlPct = h.CurrentPrice > 0
                    ? (double)((h.CurrentPrice - h.AvgPrice) / h.AvgPrice * 100m)
                    : 0.0;

                // FirstBuyDate가 기본값이면 0일 처리
                var holdDays = h.FirstBuyDate > DateTime.MinValue
                    ? Math.Max(0, (int)(asOf - h.FirstBuyDate).TotalDays)
                    : 0;

                var alerts = new List<BiasAlert>();

                // ① 처분효과: 이익 포지션 + 과거 승자 조기청산 이력
                if (biasSet.Contains("disposition") && pnlPct >= 5.0)
                {
                    var earlyDays = report.Stats.AvgHoldLossDays - report.Stats.AvgHoldWinDays;
                    alerts.Add(new BiasAlert("disposition", "처분효과 경계",
                        earlyDays > 0
                            ? $"+{pnlPct:0.0}% 이익 중. 과거 패자를 승자보다 {earlyDays:0}일 더 보유(승자 조기청산)."
                            : $"+{pnlPct:0.0}% 이익 중. 과거 처분효과 패턴과 유사.",
                        AlertSeverity.Warning));
                }

                // ② 패자 장기보유: 손실 + 처분효과 or 손익비 열위 이력
                if ((biasSet.Contains("disposition") || biasSet.Contains("payoff"))
                    && pnlPct < -5.0 && holdDays > 20)
                    alerts.Add(new BiasAlert("loss-hold", "패자 장기보유 위험",
                        $"{pnlPct:0.0}% 손실 · 보유 {holdDays}일. 손절 기준이 있다면 재점검하세요.",
                        pnlPct < -15.0 ? AlertSeverity.High : AlertSeverity.Warning));

                // ③ 추격매수: 최근 진입(≤10일) + 추격 이력 + 현재 손실
                if (biasSet.Contains("chasing") && holdDays is > 0 and <= 10 && pnlPct < 0)
                    alerts.Add(new BiasAlert("chasing", "추격매수 의심",
                        $"최근 {holdDays}일 전 진입 · {pnlPct:0.0}%. 과거 급등 후 진입 패턴과 유사합니다.",
                        AlertSeverity.Warning));

                // ④ 패닉매도 위험: 단기 보유 중 손실 + 패닉 이력
                if (biasSet.Contains("panic") && pnlPct < -3.0 && holdDays is > 0 and <= 14)
                    alerts.Add(new BiasAlert("panic", "패닉매도 충동 위험",
                        $"단기({holdDays}일) 보유 중 {pnlPct:0.0}% 하락. 과거 급락 후 저점 청산 패턴이 있습니다.",
                        AlertSeverity.Warning));

                // ⑤ 물타기 충동: 큰 손실 + 물타기 이력 다수
                if (biasSet.Contains("averaging-down")
                    && report.Stats.AveragingDownCount >= 3 && pnlPct < -8.0)
                    alerts.Add(new BiasAlert("averaging-down", "물타기 충동 위험",
                        $"과거 물타기 {report.Stats.AveragingDownCount}회. 현재 {pnlPct:0.0}% 손실 중 추가매수는 손실 확대 위험.",
                        AlertSeverity.Info));

                var score = Math.Min(100, alerts.Sum(a => a.Severity switch
                {
                    AlertSeverity.High    => 40,
                    AlertSeverity.Warning => 25,
                    _                    => 10
                }));

                signals.Add(new HoldingSignal(h.Symbol, h.Name ?? h.Symbol, pnlPct, holdDays, score, alerts));
            }

            signals.Sort((a, b) => b.RiskScore.CompareTo(a.RiskScore));

            var note = !report.IsValid
                ? "거래 이력 부족 — 편향 패턴 비교를 생략했습니다. 닫힌 라운드트립(매수→매도)이 있으면 정밀도가 높아집니다."
                : $"과거 닫힌 거래 {report.Stats.ClosedTrades}건 기반 패턴 매칭.";

            return new MirrorResult(signals, portAlerts, report.IsValid, note);
        }
    }
}
