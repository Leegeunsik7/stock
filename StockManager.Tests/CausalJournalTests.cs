// ============================================================================
// CausalJournalTests - 인과 추적 저널 타임라인 통합/필터/그룹 회귀 테스트
//  임시 DB에 4개 소스(거래·시그널·알림·뉴스)를 넣고 BuildTimeline/GroupByDay 검증.
//  날짜 범위는 명시적 from/to로 고정 → 현재 시각에 의존하지 않는 결정적 테스트.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class CausalJournalTests
    {
        private static readonly DateTime From = new(2025, 1, 1);
        private static readonly DateTime To = new(2025, 12, 31, 23, 59, 59);

        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smcj_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        private static void Tx(LocalDatabase db, string sym, DateTime at, TransactionType type = TransactionType.Buy)
            => db.AddTransaction(new Transaction { Symbol = sym, Type = type, Quantity = 10, Price = 1000, ExecutedAt = at });

        private static void Sig(LocalDatabase db, string sym, DateTime at, SignalType t = SignalType.Buy)
            => db.Execute(@"INSERT INTO Signals(Symbol,Name,Signal,Strength,Strategy,Reason,TargetPrice,StopLoss,Confidence,GeneratedAt)
                            VALUES(@s,@n,@sig,1,'테스트','이유',1050,950,70,@g)",
                new() { ["@s"] = sym, ["@n"] = sym + "명", ["@sig"] = (int)t, ["@g"] = at.ToString("o") });

        private static void Alert(LocalDatabase db, string sym, DateTime at)
            => db.Execute(@"INSERT INTO Alerts(Time,Source,Level,Title,Message,Symbol) VALUES(@t,0,2,@ti,@m,@s)",
                new() { ["@t"] = at.ToString("o"), ["@ti"] = "알림제목", ["@m"] = "메시지", ["@s"] = sym });

        private static void News(LocalDatabase db, string url, DateTime at, string related, double sentiment = 0.5)
            => db.Execute(@"INSERT INTO News(Title,Summary,Url,Source,PublishedAt,RelatedSymbols,SentimentScore,Language)
                            VALUES('뉴스','요약',@u,'src',@p,@r,@sc,'ko')",
                new() { ["@u"] = url, ["@p"] = at.ToString("o"), ["@r"] = related, ["@sc"] = sentiment });

        [Fact]
        public void MergesAllFourSources_SortedDescending() => WithDb(db =>
        {
            Tx(db, "005930", new DateTime(2025, 3, 1, 9, 0, 0));
            Sig(db, "005930", new DateTime(2025, 3, 1, 10, 0, 0));
            Alert(db, "005930", new DateTime(2025, 3, 1, 11, 0, 0));
            News(db, "u1", new DateTime(2025, 3, 1, 12, 0, 0), "005930");

            var ev = CausalJournal.BuildTimeline(db, From, To);
            Assert.Equal(4, ev.Count);
            // 내림차순 — 뉴스(12시)가 처음, 거래(9시)가 마지막
            Assert.Equal(JournalKind.News, ev.First().Kind);
            Assert.Equal(JournalKind.Transaction, ev.Last().Kind);
            Assert.True(ev.Zip(ev.Skip(1), (a, b) => a.When >= b.When).All(x => x));
            Assert.Contains(ev, e => e.Kind == JournalKind.Signal);
            Assert.Contains(ev, e => e.Kind == JournalKind.Alert);
        });

        [Fact]
        public void DateRange_ExcludesOutside() => WithDb(db =>
        {
            Tx(db, "AAA", new DateTime(2025, 6, 15, 9, 0, 0));   // in
            Tx(db, "AAA", new DateTime(2024, 6, 15, 9, 0, 0));   // before From
            Tx(db, "AAA", new DateTime(2026, 6, 15, 9, 0, 0));   // after To

            var ev = CausalJournal.BuildTimeline(db, From, To);
            Assert.Single(ev);
            Assert.Equal(2025, ev[0].When.Year);
        });

        [Fact]
        public void SymbolFilter_ExactForRows_PartialForNews() => WithDb(db =>
        {
            Tx(db, "005930", new DateTime(2025, 2, 1, 9, 0, 0));
            Tx(db, "AAPL", new DateTime(2025, 2, 1, 9, 30, 0));
            Sig(db, "AAPL", new DateTime(2025, 2, 1, 10, 0, 0));
            News(db, "n1", new DateTime(2025, 2, 1, 11, 0, 0), "005930,000660");   // 005930 부분일치
            News(db, "n2", new DateTime(2025, 2, 1, 11, 30, 0), "AAPL,MSFT");      // 미일치

            var ev = CausalJournal.BuildTimeline(db, From, To, "005930");
            Assert.Equal(2, ev.Count);   // 005930 거래 1 + 005930 관련 뉴스 1
            Assert.Contains(ev, e => e.Kind == JournalKind.Transaction && e.Symbol == "005930");
            Assert.Contains(ev, e => e.Kind == JournalKind.News);
            Assert.DoesNotContain(ev, e => e.Kind == JournalKind.Signal);   // AAPL 시그널 제외
        });

        [Fact]
        public void GroupByDay_DaysDesc_EventsWithinDayDesc() => WithDb(db =>
        {
            Tx(db, "X", new DateTime(2025, 4, 1, 9, 0, 0));
            Alert(db, "X", new DateTime(2025, 4, 2, 9, 0, 0));
            Sig(db, "X", new DateTime(2025, 4, 2, 15, 0, 0));

            var days = CausalJournal.GroupByDay(db, CausalJournal.BuildTimeline(db, From, To));
            Assert.Equal(2, days.Count);
            Assert.Equal(new DateTime(2025, 4, 2), days[0].Date);     // 최신 날짜 먼저
            Assert.Equal(new DateTime(2025, 4, 1), days[1].Date);
            // 4/2 그룹 내: 15:00 시그널이 09:00 알림보다 먼저(내림차순)
            Assert.True(days[0].Events[0].When > days[0].Events[1].When);
        });

        [Fact]
        public void EquityChange_ComputedFromSnapshots() => WithDb(db =>
        {
            db.SetSetting("Dashboard.Snapshots", JsonSerializer.Serialize(
                new Dictionary<string, decimal> { ["2025-05-01"] = 1_000_000m, ["2025-05-02"] = 1_100_000m }));
            Tx(db, "X", new DateTime(2025, 5, 2, 9, 0, 0));   // 5/2에 이벤트 → 해당 일자 그룹 생성

            var days = CausalJournal.GroupByDay(db, CausalJournal.BuildTimeline(db, From, To));
            var d = days.Single(x => x.Date == new DateTime(2025, 5, 2));
            Assert.NotNull(d.EquityChangePct);
            Assert.Equal(10m, d.EquityChangePct!.Value, 3);   // (110만-100만)/100만 = +10%
        });

        [Fact]
        public void EmptyDb_NoThrow_Empty() => WithDb(db =>
        {
            var ev = CausalJournal.BuildTimeline(db, From, To);
            Assert.Empty(ev);
            Assert.Empty(CausalJournal.GroupByDay(db, ev));
        });
    }
}
