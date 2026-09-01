// ============================================================================
// CausalJournal - 인과 추적 저널 🅕 (외부 의존 0, 로컬 DB만) (v3.16.0)
//  타임스탬프가 있는 4개 소스(거래·시그널·알림·뉴스)를 하나의 타임라인으로 통합하고,
//  일자별로 묶어 "그날의 기록"을 만든다. LLM은 이 결정적 기록만 근거로 인과를 주석한다
//  (환각 방지 — 수치/사건은 C#이 DB에서 정확히 뽑고, 모델은 해석만).
//
//  설계 노트 / 숨은 룰:
//   · 날짜 필터는 C#에서 수행(Safe.DT 파싱 후 비교) — 저장 포맷 차이에 견고
//   · 종목 필터: 거래/시그널/알림은 Symbol 일치, 뉴스는 RelatedSymbols 부분일치
//   · 정렬: 전체 내림차순(When desc), 일자 그룹도 내림차순, 그룹 내 이벤트도 내림차순
//   · 가격 맥락: 포트폴리오 총자산은 Dashboard.Snapshots(엔진이 적재)에서 전일 대비 산출,
//     종목별 등락은 Candles(있으면) best-effort — 둘 다 없으면 생략(거짓 수치 금지)
//   · 이 모듈은 사실만 직렬화한다. 인과 '설명'은 AiService.ExplainDayAsync가 담당.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    public enum JournalKind { Transaction, Signal, Alert, News, Dividend }

    public class JournalEvent : MS2E.UI.Portable.Ports.Intel.IJournalEventView   // Phase 94-O: 공변(When/Icon/KindLabel/Title/Detail 암시)
    {
        public DateTime When { get; set; }
        public JournalKind Kind { get; set; }
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public int Level { get; set; }   // 알림 레벨(0~2) 또는 시그널 강도

        public string Icon => Kind switch
        {
            JournalKind.Transaction => "💱",
            JournalKind.Signal => "⚡",
            JournalKind.Alert => "🔔",
            JournalKind.News => "📰",
            JournalKind.Dividend => "💵",
            _ => "•"
        };
        public string KindLabel => Kind switch
        {
            JournalKind.Transaction => "거래",
            JournalKind.Signal => "시그널",
            JournalKind.Alert => "알림",
            JournalKind.News => "뉴스",
            JournalKind.Dividend => "배당",
            _ => ""
        };
    }

    public class JournalDay : MS2E.UI.Portable.Ports.Intel.IJournalDayView   // Phase 94-O: 공변(Date/EquityChangePct/Annotation 암시)
    {
        public DateTime Date { get; set; }
        public List<JournalEvent> Events { get; set; } = new();
        public decimal? EquityChangePct { get; set; }   // 그날 총자산 전일 대비(%) — 없으면 null
        public string Annotation { get; set; } = "";     // LLM 인과 주석(페이지에서 채움)

        // List<JournalEvent> → IReadOnlyList<IJournalEventView> : 명시적 공변(동명 반환형 충돌 회피).
        IReadOnlyList<MS2E.UI.Portable.Ports.Intel.IJournalEventView>
            MS2E.UI.Portable.Ports.Intel.IJournalDayView.Events => Events;
    }

    public static class CausalJournal
    {
        /// <summary>4개 소스를 통합한 이벤트 타임라인(내림차순). symbol 지정 시 해당 종목만.</summary>
        public static List<JournalEvent> BuildTimeline(LocalDatabase db, DateTime from, DateTime to, string? symbol = null)
        {
            var list = new List<JournalEvent>();
            bool filt = !string.IsNullOrWhiteSpace(symbol);
            string sym = symbol?.Trim() ?? "";
            bool InRange(DateTime t) => t >= from && t <= to;
            bool SymOk(string s) => !filt || s.Equals(sym, StringComparison.OrdinalIgnoreCase);

            // ── 거래 ──
            try
            {
                foreach (DataRow r in db.Query("SELECT Symbol,Type,Quantity,Price,Memo,ExecutedAt FROM Transactions").Rows)
                {
                    var when = Safe.DT(r["ExecutedAt"]); var s = Safe.Str(r["Symbol"]);
                    if (!InRange(when) || !SymOk(s)) continue;
                    var type = (TransactionType)Safe.Int(r["Type"]);
                    list.Add(new JournalEvent
                    {
                        When = when, Kind = JournalKind.Transaction, Symbol = s,
                        Title = $"{KrType(type)} {s} {Safe.Int(r["Quantity"])}주 @{Safe.Dec(r["Price"]):N0}",
                        Detail = Safe.Str(r["Memo"])
                    });
                }
            }
            catch { }

            // ── 시그널 ──
            try
            {
                foreach (DataRow r in db.Query("SELECT Symbol,Name,Signal,Strength,Strategy,Reason,Confidence,GeneratedAt FROM Signals").Rows)
                {
                    var when = Safe.DT(r["GeneratedAt"]); var s = Safe.Str(r["Symbol"]);
                    if (!InRange(when) || !SymOk(s)) continue;
                    var t = (SignalType)Safe.Int(r["Signal"]);
                    list.Add(new JournalEvent
                    {
                        When = when, Kind = JournalKind.Signal, Symbol = s, Name = Safe.Str(r["Name"]),
                        Level = Safe.Int(r["Strength"]),
                        Title = $"{KrSignal(t)} · {Safe.Str(r["Name"])}({s})",
                        Detail = $"{Safe.Str(r["Strategy"])} · 신뢰 {Safe.Dec(r["Confidence"]):0}" +
                                 (string.IsNullOrWhiteSpace(Safe.Str(r["Reason"])) ? "" : $" · {Safe.Str(r["Reason"])}")
                    });
                }
            }
            catch { }

            // ── 알림 ──
            try
            {
                foreach (DataRow r in db.Query("SELECT Time,Source,Level,Title,Message,Symbol FROM Alerts").Rows)
                {
                    var when = Safe.DT(r["Time"]); var s = Safe.Str(r["Symbol"]);
                    if (!InRange(when) || !SymOk(s)) continue;
                    list.Add(new JournalEvent
                    {
                        When = when, Kind = JournalKind.Alert, Symbol = s, Level = Safe.Int(r["Level"]),
                        Title = Safe.Str(r["Title"]), Detail = Safe.Str(r["Message"])
                    });
                }
            }
            catch { }

            // ── 뉴스 (종목 필터는 RelatedSymbols 부분일치) ──
            try
            {
                foreach (DataRow r in db.Query("SELECT Title,Summary,RelatedSymbols,SentimentScore,PublishedAt FROM News").Rows)
                {
                    var when = Safe.DT(r["PublishedAt"]);
                    if (!InRange(when)) continue;
                    var rel = Safe.Str(r["RelatedSymbols"]);
                    if (filt && !(rel?.Contains(sym, StringComparison.OrdinalIgnoreCase) ?? false)) continue;
                    var sent = Safe.Dec(r["SentimentScore"]);
                    list.Add(new JournalEvent
                    {
                        When = when, Kind = JournalKind.News, Symbol = filt ? sym : "",
                        Title = Safe.Str(r["Title"]),
                        Detail = $"{Sentiment(sent)}({sent:+0.0;-0.0})" +
                                 (string.IsNullOrWhiteSpace(Safe.Str(r["Summary"])) ? "" : $" · {Safe.Str(r["Summary"])}")
                    });
                }
            }
            catch { }

            return list.OrderByDescending(e => e.When).ToList();
        }

        /// <summary>이벤트를 일자별로 묶고(내림차순) 총자산 전일대비 변동을 부착.</summary>
        public static List<JournalDay> GroupByDay(LocalDatabase db, IEnumerable<JournalEvent> events)
        {
            var snaps = LoadSnapshots(db);
            var days = events.GroupBy(e => e.When.Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new JournalDay
                {
                    Date = g.Key,
                    Events = g.OrderByDescending(e => e.When).ToList(),
                    EquityChangePct = EquityMovePct(snaps, g.Key)
                })
                .ToList();
            return days;
        }

        /// <summary>한 날짜의 "그날의 기록"을 LLM 근거용 한국어 텍스트로 직렬화.</summary>
        public static string BuildDayContext(LocalDatabase db, JournalDay day)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[날짜] {day.Date:yyyy-MM-dd}");
            if (day.EquityChangePct is { } chg)
                sb.AppendLine($"[그날 포트폴리오 총자산 전일대비] {chg:+0.0;-0.0}%");

            // 종목별 당일 등락 (Candles 있으면 best-effort)
            try
            {
                var moves = new List<string>();
                foreach (var h in db.GetAllHoldings())
                {
                    var pc = DayMovePct(db, h.Symbol, day.Date);
                    if (pc is { } v) moves.Add($"{h.Name}({h.Symbol}) {v:+0.0;-0.0}%");
                }
                if (moves.Count > 0) sb.AppendLine("[당일 보유종목 등락] " + string.Join(", ", moves));
            }
            catch { }

            sb.AppendLine("[그날의 기록]");
            foreach (var e in day.Events)
                sb.AppendLine($"- {e.When:HH:mm} [{e.KindLabel}] {e.Title}" +
                              (string.IsNullOrWhiteSpace(e.Detail) ? "" : $" — {e.Detail}"));
            return sb.ToString();
        }

        // ── 내부 헬퍼 ──
        private static Dictionary<string, decimal> LoadSnapshots(LocalDatabase db)
        {
            try
            {
                var j = db.GetSetting("Dashboard.Snapshots");
                if (!string.IsNullOrWhiteSpace(j))
                    return JsonSerializer.Deserialize<Dictionary<string, decimal>>(j) ?? new();
            }
            catch { }
            return new();
        }

        // 스냅샷 dict(키=날짜문자열)에서 date의 값과 그 직전 날짜 값으로 변동% (없으면 null)
        private static decimal? EquityMovePct(Dictionary<string, decimal> snaps, DateTime date)
        {
            if (snaps.Count < 2) return null;
            var key = date.ToString("yyyy-MM-dd");
            var ordered = snaps.Where(k => string.Compare(k.Key, key, StringComparison.Ordinal) <= 0)
                               .OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
            if (ordered.Count < 2 || !ordered[^1].Key.StartsWith(key, StringComparison.Ordinal)) return null;
            var cur = ordered[^1].Value; var prev = ordered[^2].Value;
            return prev > 0 ? (cur - prev) / prev * 100m : (decimal?)null;
        }

        // Candles에서 date 이하 최근 2개 종가로 일간 등락% (테이블 비었으면 null)
        private static decimal? DayMovePct(LocalDatabase db, string symbol, DateTime date)
        {
            try
            {
                var dt = db.Query("SELECT Close FROM Candles WHERE Symbol=@s AND Date<=@d ORDER BY Date DESC LIMIT 2",
                    new() { ["@s"] = symbol, ["@d"] = date.ToString("yyyy-MM-dd") + "T23:59:59" });
                if (dt.Rows.Count < 2) return null;
                var c0 = Safe.Dec(dt.Rows[0]["Close"]); var c1 = Safe.Dec(dt.Rows[1]["Close"]);
                return c1 > 0 ? (c0 - c1) / c1 * 100m : (decimal?)null;
            }
            catch { return null; }
        }

        private static string Sentiment(decimal s) => s > 0 ? "긍정" : s < 0 ? "부정" : "중립";

        private static string KrType(TransactionType t) => t switch
        {
            TransactionType.Buy => "매수", TransactionType.Sell => "매도", TransactionType.Dividend => "배당",
            TransactionType.Split => "분할", TransactionType.Deposit => "입금", TransactionType.Withdraw => "출금", _ => t.ToString()
        };
        private static string KrSignal(SignalType t) => t switch
        {
            SignalType.StrongBuy => "강력매수", SignalType.Buy => "매수", SignalType.Hold => "중립",
            SignalType.Sell => "매도", SignalType.StrongSell => "강력매도", _ => t.ToString()
        };
    }
}
