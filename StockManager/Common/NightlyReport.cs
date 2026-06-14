// ============================================================================
// NightlyReport - 야간 자동 퍼블리싱 (Season 4 ③) (v3.36.0)
//  헤드리스 새벽에 모든 분석 결과(손익·옵티마이저·예측보정·토론·드리프트)를 종합해
//  텍스트 요약 + PDF를 만들고 텔레그램/메일로 발송한다. "앱을 안 켜도 일하는" 서비스화.
//  Gather=DB 집계, ComposeText=순수(테스트 가능), BuildPdf=QuestPDF(런타임 IO).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace StockManager.Common
{
    public class NightlyData
    {
        public string Date { get; set; } = "";
        public decimal Total { get; set; }
        public decimal Pnl { get; set; }
        public double RetPct { get; set; }
        public int Holdings { get; set; }
        public List<(string name, double ret)> Gainers { get; set; } = new();
        public List<(string name, double ret)> Losers { get; set; } = new();
        public string OptimizerNote { get; set; } = "";
        public string ForecastNote { get; set; } = "";
        public string DebateNote { get; set; } = "";
        public string DriftNote { get; set; } = "";
    }

    public static class NightlyReport
    {
        static NightlyReport() { QuestPDF.Settings.License = LicenseType.Community; }

        /// <summary>요약 텍스트(텔레그램·메일 본문). 순수.</summary>
        public static string ComposeText(NightlyData d)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🌙 야간 리포트 · {d.Date}");
            sb.AppendLine($"총자산 {d.Total:N0}원 · 평가손익 {d.Pnl:+#,##0;-#,##0;0}원 ({d.RetPct:+0.0;-0.0}%) · {d.Holdings}종목");
            if (d.Gainers.Count > 0)
                sb.AppendLine("📈 상승: " + string.Join(", ", d.Gainers.Select(g => $"{g.name} {g.ret:+0.0;-0.0}%")));
            if (d.Losers.Count > 0)
                sb.AppendLine("📉 하락: " + string.Join(", ", d.Losers.Select(g => $"{g.name} {g.ret:+0.0;-0.0}%")));
            if (d.OptimizerNote.Length > 0) sb.AppendLine("🌙 옵티마이저: " + d.OptimizerNote);
            if (d.DriftNote.Length > 0) sb.AppendLine("⚖️ 리밸런싱: " + d.DriftNote);
            if (d.ForecastNote.Length > 0) sb.AppendLine("📐 예측보정: " + d.ForecastNote);
            if (d.DebateNote.Length > 0) sb.AppendLine("🗣 토론승률: " + d.DebateNote);
            sb.AppendLine("— 정보 제공이며 투자 권유가 아닙니다.");
            return sb.ToString();
        }

        /// <summary>보유·옵티마이저·예측·토론을 DB에서 집계(네트워크 없음).</summary>
        public static NightlyData Gather(LocalDatabase db, decimal usdKrw)
        {
            var d = new NightlyData { Date = DateTime.Now.ToString("yyyy-MM-dd (ddd)") };
            try
            {
                var holdings = db.GetAllHoldings();
                d.Holdings = holdings.Count;
                decimal total = 0, cost = 0;
                foreach (var h in holdings)
                {
                    var fx = h.Currency == Currency.USD ? usdKrw : 1m;
                    total += h.CurrentPrice * h.Quantity * fx;
                    cost += h.AvgPrice * h.Quantity * fx;
                }
                d.Total = total; d.Pnl = total - cost; d.RetPct = cost > 0 ? (double)(d.Pnl / cost * 100m) : 0;
                d.Gainers = holdings.Where(h => h.ReturnRate > 0).OrderByDescending(h => h.ReturnRate).Take(3)
                    .Select(h => (h.Name.Length > 0 ? h.Name : h.Symbol, (double)h.ReturnRate)).ToList();
                d.Losers = holdings.Where(h => h.ReturnRate < 0).OrderBy(h => h.ReturnRate).Take(3)
                    .Select(h => (h.Name.Length > 0 ? h.Name : h.Symbol, (double)h.ReturnRate)).ToList();
            }
            catch { }

            try
            {
                var j = db.GetSetting("Optimizer.Results");
                if (!string.IsNullOrWhiteSpace(j))
                {
                    var snap = System.Text.Json.JsonSerializer.Deserialize<OptSnapshot>(j);
                    var best = snap?.Rows.Where(r => r.IsValid).OrderByDescending(r => r.TotalReturn).FirstOrDefault();
                    if (best != null) d.OptimizerNote = $"최고 {best.Name} {best.Label} ({best.TotalReturn:+0.0;-0.0}%)";
                }
            }
            catch { }

            try
            {
                var has = !string.IsNullOrWhiteSpace(db.GetSetting("Rebalance.Target"));
                d.DriftNote = has ? "타겟 저장됨 — 드리프트 감시 가능" : "타겟 미저장";
            }
            catch { }

            try
            {
                var fc = ForecastScore.Score(ForecastLog.ResolvedToScore(db));
                if (fc.IsValid && fc.Scores.Count > 0)
                {
                    var s = fc.Scores[0];
                    d.ForecastNote = $"커버리지 {s.CoveragePct:0}%/{s.NominalPct:0}% · 편향 {s.BiasPct:+0.0;-0.0}% (n{s.N})";
                }
            }
            catch { }

            try
            {
                var r = DebateLog.Stats(db);
                if (r.ResolvedN > 0) d.DebateNote = $"강세 {r.BullishHitPct:0}%({r.BullishN}) · 약세 {r.BearishHitPct:0}%({r.BearishN})";
            }
            catch { }

            return d;
        }

        public static byte[] BuildPdf(NightlyData d)
        {
            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(32); page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontFamily("Malgun Gothic").FontSize(10));
                    page.Header().Column(col =>
                    {
                        col.Item().Text("MS2-E 야간 리포트").FontSize(20).Bold();
                        col.Item().Text(d.Date).FontSize(10).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingBottom(8).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    });
                    page.Content().Column(col =>
                    {
                        col.Spacing(12);
                        col.Item().Background(Colors.Blue.Lighten5).Padding(12).Column(c =>
                        {
                            c.Item().Text("■ 포트폴리오").FontSize(13).Bold().FontColor(Colors.Blue.Darken2);
                            c.Item().PaddingTop(4).Text($"총자산 {d.Total:N0}원");
                            var rc = d.Pnl >= 0 ? Colors.Red.Darken1 : Colors.Blue.Darken2;
                            c.Item().Text($"평가손익 {d.Pnl:+#,##0;-#,##0;0}원 ({d.RetPct:+0.0;-0.0}%) · {d.Holdings}종목").Bold().FontColor(rc);
                        });
                        void Section(string title, string body)
                        {
                            if (string.IsNullOrWhiteSpace(body)) return;
                            col.Item().Text("■ " + title).FontSize(12).Bold().FontColor(Colors.Grey.Darken3);
                            col.Item().Text(body).FontSize(10);
                        }
                        Section("상승", string.Join("\n", d.Gainers.Select(g => $"  {g.name} {g.ret:+0.0;-0.0}%")));
                        Section("하락", string.Join("\n", d.Losers.Select(g => $"  {g.name} {g.ret:+0.0;-0.0}%")));
                        Section("옵티마이저", d.OptimizerNote);
                        Section("리밸런싱", d.DriftNote);
                        Section("예측 보정도", d.ForecastNote);
                        Section("토론 승률", d.DebateNote);
                        col.Item().PaddingTop(8).Text("정보 제공이며 투자 권유가 아닙니다.").FontSize(8).FontColor(Colors.Grey.Medium);
                    });
                    page.Footer().AlignCenter().Text($"MS2-E Stock Suite · {DateTime.Now:yyyy-MM-dd HH:mm}")
                        .FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });
            return doc.GeneratePdf();
        }
    }
}
