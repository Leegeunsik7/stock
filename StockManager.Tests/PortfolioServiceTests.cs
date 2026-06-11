// ============================================================================
// PortfolioServiceTests - 거래기록 → 평단 재계산(RecomputeAverage) 회귀 테스트
//  숨은 룰 고정: 이동평균법(매도 시 평단 불변·원가 비례 차감), 전량매도→평단 0,
//  보유 0 상태 매도 무시, 종목 간 독립성.
//  임시 DB로 Transactions를 넣고 RecomputeAverage 후 Holdings를 직접 검증.
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class PortfolioServiceTests
    {
        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smpf_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        private static DateTime _baseAt = new(2025, 1, 1, 9, 0, 0);
        private static void Tx(LocalDatabase db, string sym, TransactionType type, int qty, decimal price, int minute)
            => db.AddTransaction(new Transaction
            {
                Symbol = sym, Type = type, Quantity = qty, Price = price,
                ExecutedAt = _baseAt.AddMinutes(minute)
            });

        private static (int qty, decimal avg) Read(LocalDatabase db, string sym)
        {
            var dt = db.Query("SELECT Quantity, AvgPrice FROM Holdings WHERE Symbol=@s", new() { ["@s"] = sym });
            return dt.Rows.Count == 0 ? (0, 0m) : (Safe.Int(dt.Rows[0]["Quantity"]), Safe.Dec(dt.Rows[0]["AvgPrice"]));
        }

        [Fact]
        public void Buys_Accumulate_WeightedAverage() => WithDb(db =>
        {
            Tx(db, "005930", TransactionType.Buy, 10, 1000, 1);
            Tx(db, "005930", TransactionType.Buy, 10, 2000, 2);   // 가중평단 = (10·1000+10·2000)/20 = 1500
            PortfolioService.RecomputeAverage(db, "005930");
            var (qty, avg) = Read(db, "005930");
            Assert.Equal(20, qty);
            Assert.Equal(1500m, avg);
        });

        [Fact]
        public void PartialSell_KeepsAverage_ReducesQty() => WithDb(db =>
        {
            Tx(db, "005930", TransactionType.Buy, 10, 1000, 1);
            Tx(db, "005930", TransactionType.Sell, 4, 5000, 2);   // 매도가 무관 — 이동평균법은 평단 유지
            PortfolioService.RecomputeAverage(db, "005930");
            var (qty, avg) = Read(db, "005930");
            Assert.Equal(6, qty);
            Assert.Equal(1000m, avg);     // 평단 불변
        });

        [Fact]
        public void FullSell_ResetsToZero() => WithDb(db =>
        {
            Tx(db, "005930", TransactionType.Buy, 10, 1000, 1);
            Tx(db, "005930", TransactionType.Sell, 10, 1234, 2);  // 전량 매도 → 0/0 방지: qty 0, avg 0
            PortfolioService.RecomputeAverage(db, "005930");
            var (qty, avg) = Read(db, "005930");
            Assert.Equal(0, qty);
            Assert.Equal(0m, avg);
        });

        [Fact]
        public void SellWithNoHolding_IsIgnored() => WithDb(db =>
        {
            Tx(db, "005930", TransactionType.Sell, 5, 1000, 1);   // 보유 0 상태 매도 → 무시
            Tx(db, "005930", TransactionType.Buy, 3, 2000, 2);    // 이후 매수만 반영
            PortfolioService.RecomputeAverage(db, "005930");
            var (qty, avg) = Read(db, "005930");
            Assert.Equal(3, qty);
            Assert.Equal(2000m, avg);
        });

        [Fact]
        public void RebuyAfterFullSell_StartsFresh() => WithDb(db =>
        {
            Tx(db, "005930", TransactionType.Buy, 10, 1000, 1);
            Tx(db, "005930", TransactionType.Sell, 10, 3000, 2);  // 전량 매도
            Tx(db, "005930", TransactionType.Buy, 5, 4000, 3);    // 재매수 → 새 평단 4000
            PortfolioService.RecomputeAverage(db, "005930");
            var (qty, avg) = Read(db, "005930");
            Assert.Equal(5, qty);
            Assert.Equal(4000m, avg);
        });

        [Fact]
        public void Symbols_AreIndependent() => WithDb(db =>
        {
            Tx(db, "005930", TransactionType.Buy, 10, 1000, 1);
            Tx(db, "AAPL", TransactionType.Buy, 2, 500, 2);
            PortfolioService.RecomputeAverage(db, "005930");
            PortfolioService.RecomputeAverage(db, "AAPL");
            Assert.Equal((10, 1000m), Read(db, "005930"));
            Assert.Equal((2, 500m), Read(db, "AAPL"));
        });
    }
}
