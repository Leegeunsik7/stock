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

        // ── v4.30.0 코인 소수점 수량 (int→decimal 마이그레이션 회귀) ──────────────────
        private static decimal ReadQty(LocalDatabase db, string sym)
        {
            var dt = db.Query("SELECT Quantity FROM Holdings WHERE Symbol=@s", new() { ["@s"] = sym });
            return dt.Rows.Count == 0 ? 0m : Safe.Dec(dt.Rows[0]["Quantity"]);
        }

        [Fact]
        public void Crypto_FractionalQuantity_RoundTrips_NotTruncated() => WithDb(db =>
        {
            // 핵심 회귀 — 과거 int 라 2.555 가 2 로 절삭됐다. 이제 소수점 보존 + 가중평단 정확.
            db.AddTransaction(new Transaction { Symbol = "KRW-ETH", Type = TransactionType.Buy, Quantity = 1.5m,   Price = 2_000_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            db.AddTransaction(new Transaction { Symbol = "KRW-ETH", Type = TransactionType.Buy, Quantity = 1.055m, Price = 3_000_000m, ExecutedAt = _baseAt.AddMinutes(2) });
            PortfolioService.RecomputeAverage(db, "KRW-ETH");

            var (_, avg) = Read(db, "KRW-ETH");
            decimal qty = ReadQty(db, "KRW-ETH");
            Assert.Equal(2.555m, qty, 3);                 // 1.5+1.055 — 절삭 없이 보존(과거 버그면 2)
            Assert.True(qty > 2.5m);                       // 정수 절삭 아님 명시
            Assert.Equal(6_165_000m / 2.555m, avg, 2);     // 가중평단 = (1.5·2M + 1.055·3M)/2.555
        });

        [Fact]
        public void Crypto_FullSell_FractionalParts_FlattensNoDust() => WithDb(db =>
        {
            // 분할 소수 매수 후 합산 수량을 전량 매도 → double 라운드트립에도 dust 없이 0 (>= 견고화).
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy,  Quantity = 0.3m,  Price = 90_000_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy,  Quantity = 0.255m, Price = 95_000_000m, ExecutedAt = _baseAt.AddMinutes(2) });
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Sell, Quantity = 0.555m, Price = 98_000_000m, ExecutedAt = _baseAt.AddMinutes(3) });
            PortfolioService.RecomputeAverage(db, "KRW-BTC");

            decimal qty = ReadQty(db, "KRW-BTC");
            Assert.Equal(0m, qty);                         // 잔량 dust 0 (GetAllHoldings 의 Quantity>0 유령행 방지)
        });

        [Fact]
        public void AsOf_FractionalQuantity_Reconstructs() =>
            Assert.Equal(2.555m,
                AsOfPortfolio.Reconstruct(new[]
                {
                    new AsOfTx("KRW-ETH", TransactionType.Buy, 1.5m,   2_000_000m, _baseAt.AddMinutes(1)),
                    new AsOfTx("KRW-ETH", TransactionType.Buy, 1.055m, 3_000_000m, _baseAt.AddMinutes(2)),
                }, _baseAt.AddHours(1))[0].Quantity, 3);

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
