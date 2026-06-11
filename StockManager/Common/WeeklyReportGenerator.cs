// ============================================================================
// WeeklyReportGenerator - 주간 PDF 리포트 (기능 I)
// QuestPDF 기반 (NuGet: QuestPDF, Community License MIT)
// 한글 글꼴 완벽 지원
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace StockManager.Common
{
    public static class WeeklyReportGenerator
    {
        static WeeklyReportGenerator()
        {
            // Community License (무료, 상업용 가능)
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public static WeeklyReport Build(LocalDatabase db)
        {
            var now = DateTime.Now;
            var weekStart = now.Date.AddDays(-7);
            var weekEnd = now.Date;

            var holdings = db.GetAllHoldings();
            var totalEndValue = holdings.Sum(h => h.CurrentValue);

            // 1주전 종가 기반 시작값 추정
            decimal totalStartValue = 0;
            foreach (var h in holdings)
            {
                var dt = db.Query(
                    "SELECT Close FROM Candles WHERE Symbol=@s AND Date >= @d ORDER BY Date LIMIT 1",
                    new() { ["@s"] = h.Symbol, ["@d"] = weekStart.ToString("yyyy-MM-dd") });
                var startPrice = dt.Rows.Count > 0 ? Safe.Dec(dt.Rows[0]["Close"]) : h.AvgPrice;
                totalStartValue += startPrice * h.Quantity;
            }
            if (totalStartValue == 0) totalStartValue = totalEndValue;

            var weeklyReturn = totalEndValue - totalStartValue;
            var weeklyPct = totalStartValue == 0 ? 0 : weeklyReturn / totalStartValue * 100m;

            var sorted = holdings.OrderByDescending(h => h.UnrealizedPnL).ToList();

            var txDt = db.Query(
                "SELECT * FROM Transactions WHERE ExecutedAt >= @d ORDER BY ExecutedAt DESC",
                new() { ["@d"] = weekStart.ToString("o") });
            var transactions = new List<Transaction>();
            decimal totalCommission = 0, totalTax = 0;
            foreach (DataRow r in txDt.Rows)
            {
                var t = new Transaction
                {
                    Symbol = Safe.Str(r["Symbol"]),
                    Type = (TransactionType)Safe.Int(r["Type"]),
                    Quantity = Safe.Int(r["Quantity"]),
                    Price = Safe.Dec(r["Price"]),
                    Commission = Safe.Dec(r["Commission"]),
                    Tax = Safe.Dec(r["Tax"]),
                    ExecutedAt = Safe.DT(r["ExecutedAt"]),
                    BrokerAccount = Safe.Str(r["BrokerAccount"])
                };
                transactions.Add(t);
                totalCommission += t.Commission;
                totalTax += t.Tax;
            }

            var sigDt = db.Query(
                "SELECT * FROM Signals WHERE GeneratedAt >= @d ORDER BY Confidence DESC LIMIT 10",
                new() { ["@d"] = weekStart.ToString("o") });
            var signals = new List<TradeSignal>();
            foreach (DataRow r in sigDt.Rows)
            {
                signals.Add(new TradeSignal
                {
                    Symbol = Safe.Str(r["Symbol"]), Name = Safe.Str(r["Name"]),
                    Signal = (SignalType)Safe.Int(r["Signal"]),
                    Strategy = Safe.Str(r["Strategy"]), Reason = Safe.Str(r["Reason"]),
                    Confidence = Safe.Dec(r["Confidence"]),
                    GeneratedAt = Safe.DT(r["GeneratedAt"])
                });
            }

            var newsDt = db.Query(
                "SELECT * FROM News WHERE PublishedAt >= @d ORDER BY ABS(COALESCE(SentimentScore,0)) DESC LIMIT 10",
                new() { ["@d"] = weekStart.ToString("o") });
            var news = new List<NewsArticle>();
            foreach (DataRow r in newsDt.Rows)
            {
                news.Add(new NewsArticle
                {
                    Title = Safe.Str(r["Title"]), Summary = Safe.Str(r["Summary"]),
                    Url = Safe.Str(r["Url"]), Source = Safe.Str(r["Source"]),
                    PublishedAt = Safe.DT(r["PublishedAt"]),
                    SentimentScore = Safe.Dec(r["SentimentScore"])
                });
            }

            return new WeeklyReport
            {
                WeekStart = weekStart, WeekEnd = weekEnd,
                StartingValue = totalStartValue, EndingValue = totalEndValue,
                WeeklyReturn = weeklyReturn, WeeklyReturnPct = weeklyPct,
                TopGainers = sorted.Take(5).ToList(),
                TopLosers = sorted.OrderBy(h => h.UnrealizedPnL).Take(5).ToList(),
                Transactions = transactions,
                StrongSignals = signals,
                KeyNews = news,
                TotalCommission = totalCommission,
                TotalTax = totalTax
            };
        }

        public static void SaveToPdf(WeeklyReport r, string path)
        {
            var doc = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Margin(30);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontFamily("Malgun Gothic").FontSize(10));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("MS2-E Stock Suite 주간 리포트")
                            .FontSize(20).Bold();
                        col.Item().Text($"기간: {r.WeekStart:yyyy-MM-dd} ~ {r.WeekEnd:yyyy-MM-dd}")
                            .FontSize(10).FontColor(Colors.Grey.Darken1);
                        col.Item().PaddingBottom(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
                    });

                    page.Content().Column(col =>
                    {
                        col.Spacing(15);

                        // 포트폴리오 요약
                        col.Item().Background(Colors.Blue.Lighten5).Padding(12).Column(inner =>
                        {
                            inner.Item().Text("■ 포트폴리오 요약").FontSize(13).Bold()
                                .FontColor(Colors.Blue.Darken2);
                            inner.Item().PaddingTop(6).Row(row =>
                            {
                                row.RelativeItem().Column(c =>
                                {
                                    c.Item().Text($"시작 자산: {r.StartingValue:N0}원");
                                    c.Item().Text($"종료 자산: {r.EndingValue:N0}원");
                                });
                                row.RelativeItem().Column(c =>
                                {
                                    var returnColor = r.WeeklyReturn >= 0
                                        ? Colors.Red.Darken1 : Colors.Blue.Darken2;
                                    c.Item().Text($"주간 수익: {(r.WeeklyReturn >= 0 ? "+" : "")}{r.WeeklyReturn:N0}원")
                                        .Bold().FontColor(returnColor);
                                    c.Item().Text($"수익률: {r.WeeklyReturnPct:+0.00;-0.00;0.00}%")
                                        .Bold().FontColor(returnColor);
                                });
                            });
                            inner.Item().PaddingTop(4)
                                .Text($"총 수수료: {r.TotalCommission:N0}원  ·  총 세금: {r.TotalTax:N0}원")
                                .FontSize(9).FontColor(Colors.Grey.Darken1);
                        });

                        // 상승 TOP 5
                        col.Item().Text("■ 상승 TOP 5").FontSize(13).Bold()
                            .FontColor(Colors.Red.Darken2);
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); });
                            table.Header(h =>
                            {
                                h.Cell().Element(CellHeader).Text("종목");
                                h.Cell().Element(CellHeader).Text("이름");
                                h.Cell().Element(CellHeader).Text("손익");
                                h.Cell().Element(CellHeader).Text("수익률");
                            });
                            foreach (var h in r.TopGainers.Where(x => x.UnrealizedPnL > 0))
                            {
                                table.Cell().Element(Cell).Text(h.Symbol);
                                table.Cell().Element(Cell).Text(h.Name);
                                table.Cell().Element(Cell).Text($"{h.UnrealizedPnL:+#,##0;-#,##0;0}원")
                                    .FontColor(Colors.Red.Darken1);
                                table.Cell().Element(Cell).Text($"{h.ChangeRate:+0.00;-0.00;0.00}%")
                                    .FontColor(Colors.Red.Darken1);
                            }
                        });

                        // 하락 TOP 5
                        col.Item().Text("■ 하락 TOP 5").FontSize(13).Bold()
                            .FontColor(Colors.Blue.Darken2);
                        col.Item().Table(table =>
                        {
                            table.ColumnsDefinition(c => { c.RelativeColumn(2); c.RelativeColumn(3); c.RelativeColumn(2); c.RelativeColumn(2); });
                            table.Header(h =>
                            {
                                h.Cell().Element(CellHeader).Text("종목");
                                h.Cell().Element(CellHeader).Text("이름");
                                h.Cell().Element(CellHeader).Text("손익");
                                h.Cell().Element(CellHeader).Text("수익률");
                            });
                            foreach (var h in r.TopLosers.Where(x => x.UnrealizedPnL < 0))
                            {
                                table.Cell().Element(Cell).Text(h.Symbol);
                                table.Cell().Element(Cell).Text(h.Name);
                                table.Cell().Element(Cell).Text($"{h.UnrealizedPnL:+#,##0;-#,##0;0}원")
                                    .FontColor(Colors.Blue.Darken2);
                                table.Cell().Element(Cell).Text($"{h.ChangeRate:+0.00;-0.00;0.00}%")
                                    .FontColor(Colors.Blue.Darken2);
                            }
                        });

                        // 거래 내역
                        col.Item().Text("■ 이번 주 거래").FontSize(13).Bold()
                            .FontColor(Colors.Green.Darken2);
                        if (r.Transactions.Count == 0)
                        {
                            col.Item().Text("  거래 없음").FontColor(Colors.Grey.Darken1);
                        }
                        else
                        {
                            col.Item().Table(table =>
                            {
                                table.ColumnsDefinition(c => {
                                    c.RelativeColumn(3); c.RelativeColumn(2);
                                    c.RelativeColumn(1); c.RelativeColumn(2); c.RelativeColumn(2);
                                });
                                table.Header(h =>
                                {
                                    h.Cell().Element(CellHeader).Text("일시");
                                    h.Cell().Element(CellHeader).Text("종목");
                                    h.Cell().Element(CellHeader).Text("구분");
                                    h.Cell().Element(CellHeader).Text("수량");
                                    h.Cell().Element(CellHeader).Text("단가");
                                });
                                foreach (var t in r.Transactions.Take(15))
                                {
                                    var tp = t.Type == TransactionType.Buy ? "매수" : "매도";
                                    var tpColor = t.Type == TransactionType.Buy
                                        ? Colors.Red.Darken1 : Colors.Blue.Darken2;
                                    table.Cell().Element(Cell).Text(t.ExecutedAt.ToString("MM-dd HH:mm"));
                                    table.Cell().Element(Cell).Text(t.Symbol);
                                    table.Cell().Element(Cell).Text(tp).FontColor(tpColor);
                                    table.Cell().Element(Cell).Text($"{t.Quantity:N0}");
                                    table.Cell().Element(Cell).Text($"{t.Price:N0}");
                                }
                            });
                        }

                        // 주요 시그널
                        if (r.StrongSignals.Count > 0)
                        {
                            col.Item().Text("■ 주요 시그널").FontSize(13).Bold()
                                .FontColor(Colors.Orange.Darken2);
                            foreach (var s in r.StrongSignals.Take(8))
                            {
                                var sigText = s.Signal switch
                                {
                                    SignalType.StrongBuy => "⭐ 강력매수", SignalType.Buy => "🟢 매수",
                                    SignalType.Sell => "🔴 매도", SignalType.StrongSell => "❌ 강력매도",
                                    _ => "➖ 관망"
                                };
                                col.Item().Text($"  • {s.Symbol} {s.Name} · {sigText} · {s.Strategy} · 신뢰도 {s.Confidence:F0}%")
                                    .FontSize(9);
                            }
                        }

                        // 주요 뉴스
                        if (r.KeyNews.Count > 0)
                        {
                            col.Item().PageBreak();
                            col.Item().Text("■ 주요 뉴스").FontSize(13).Bold()
                                .FontColor(Colors.Blue.Darken2);
                            foreach (var n in r.KeyNews.Take(20))
                            {
                                var sentStr = n.SentimentScore > 0.3m ? "[긍정]"
                                            : n.SentimentScore < -0.3m ? "[부정]" : "[중립]";
                                var sentColor = n.SentimentScore > 0.3m ? Colors.Red.Darken1
                                              : n.SentimentScore < -0.3m ? Colors.Blue.Darken2
                                              : Colors.Grey.Darken1;
                                col.Item().PaddingTop(4).Column(c =>
                                {
                                    c.Item().Row(row =>
                                    {
                                        row.AutoItem().Text(sentStr).FontSize(9).FontColor(sentColor).Bold();
                                        row.RelativeItem().PaddingLeft(5).Text(n.Title).FontSize(10);
                                    });
                                    c.Item().Text($"    {n.Source} · {n.PublishedAt:MM-dd HH:mm}")
                                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                                });
                            }
                        }
                    });

                    page.Footer().AlignCenter()
                        .Text($"MS2-E Stock Suite v2.2 · 생성일: {DateTime.Now:yyyy-MM-dd HH:mm}")
                        .FontSize(8).FontColor(Colors.Grey.Medium);
                });
            });

            doc.GeneratePdf(path);
        }

        private static IContainer CellHeader(IContainer c) =>
            c.DefaultTextStyle(x => x.SemiBold().FontColor(Colors.White))
             .Background(Colors.Grey.Darken2).Padding(5);

        private static IContainer Cell(IContainer c) =>
            c.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).Padding(5);
    }
}
