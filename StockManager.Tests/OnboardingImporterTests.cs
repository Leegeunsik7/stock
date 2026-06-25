// ============================================================================
// OnboardingImporterTests — v4.62.0: 온보딩 초기 보유 import 의 '거래 저장 연쇄 불변식' 박제.
//  ★Pitfall #3 — AddTransaction 단독은 Holdings 평단 디싱크. ImportHoldings 가 심볼별 RecomputeAverage 를
//   강제 연쇄하므로 Holdings 가 정확히 생성/누적된다(소수 수량 0.13 보존, fail-soft skip, 멱등 재계산).
// ============================================================================
using System;
using System.IO;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class OnboardingImporterTests
    {
        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smob_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        // Holdings 직접 조회(평단 디싱크 검증) — 수량은 소수 보존 위해 Safe.Dec.
        private static (decimal qty, decimal avg) Read(LocalDatabase db, string sym)
        {
            var dt = db.Query("SELECT Quantity, AvgPrice FROM Holdings WHERE Symbol=@s", new() { ["@s"] = sym });
            return dt.Rows.Count == 0 ? (0m, 0m) : (Safe.Dec(dt.Rows[0]["Quantity"]), Safe.Dec(dt.Rows[0]["AvgPrice"]));
        }

        [Fact]
        public void Import_BuildsHoldings_WithRecomputedAverage() => WithDb(db =>
        {
            var rows = new[]
            {
                new SeedHolding("005930", 10m, 70_000m, "삼성전자"),
                new SeedHolding("AAPL",   5m,  200m,    "Apple"),
            };
            var res = OnboardingImporter.ImportHoldings(db, rows, new DateTime(2025, 1, 1));

            Assert.Equal(2, res.Imported);
            Assert.Equal(0, res.Skipped);
            Assert.True(res.AllOk);

            // ★연쇄 강제 입증 — AddTransaction 만으로는 Holdings 가 비어야 정상이나, RecomputeAverage 강제로 평단 생성됨.
            var (q1, a1) = Read(db, "005930");
            Assert.Equal(10m, q1);
            Assert.Equal(70_000m, a1);
            var (q2, a2) = Read(db, "AAPL");
            Assert.Equal(5m, q2);
            Assert.Equal(200m, a2);
        });

        [Fact]
        public void Import_FractionalCoinQty_PreservedNotTruncated() => WithDb(db =>
        {
            // 0.13 BTC — type=number 절사 함정(0.13→0)을 ParseMoney/REAL 저장이 회피했음을 끝단 검증.
            var res = OnboardingImporter.ImportHoldings(db,
                new[] { new SeedHolding("KRW-BTC", 0.13m, 90_000_000m, "비트코인") }, new DateTime(2025, 1, 1));
            Assert.Equal(1, res.Imported);
            var (qty, avg) = Read(db, "KRW-BTC");
            Assert.Equal(0.13m, qty);          // 소수 보존(0 으로 절사되지 않음)
            Assert.Equal(90_000_000m, avg);
        });

        [Fact]
        public void Import_SameSymbolTwice_AccumulatesWeightedAverage_RecomputeOncePerSymbol() => WithDb(db =>
        {
            // 동일 심볼 2행 → 가중평단 = (10·1000 + 10·2000)/20 = 1500. 심볼별 1회 재계산이 전 이력 리플레이로 정확.
            var res = OnboardingImporter.ImportHoldings(db, new[]
            {
                new SeedHolding("005930", 10m, 1000m),
                new SeedHolding("005930", 10m, 2000m),
            }, new DateTime(2025, 1, 1));
            Assert.Equal(2, res.Imported);
            var (qty, avg) = Read(db, "005930");
            Assert.Equal(20m, qty);
            Assert.Equal(1500m, avg);
        });

        [Fact]
        public void Import_InvalidRows_Skipped_FailSoft_GoodRowsStillImport() => WithDb(db =>
        {
            var res = OnboardingImporter.ImportHoldings(db, new[]
            {
                new SeedHolding("",       10m, 100m),    // 빈 심볼 — skip
                new SeedHolding("005930", 0m,  100m),    // 수량 0 — skip
                new SeedHolding("AAPL",   5m,  0m),      // 단가 0 — skip
                new SeedHolding("000660", 7m,  120_000m),// 정상
            }, new DateTime(2025, 1, 1));

            Assert.Equal(1, res.Imported);
            Assert.Equal(3, res.Skipped);
            Assert.False(res.AllOk);
            Assert.Equal(3, res.Errors.Count);

            var (qty, _) = Read(db, "000660");
            Assert.Equal(7m, qty);                       // 정상 행은 한 행 오류와 무관하게 저장(fail-soft)
            Assert.Equal((0m, 0m), Read(db, "005930"));  // 무효 행은 Holdings 미생성
        });

        [Fact]
        public void Import_NullArgs_Throw() => WithDb(db =>
        {
            Assert.Throws<ArgumentNullException>(() => OnboardingImporter.ImportHoldings(null!, Array.Empty<SeedHolding>(), DateTime.UtcNow));
            Assert.Throws<ArgumentNullException>(() => OnboardingImporter.ImportHoldings(db, null!, DateTime.UtcNow));
        });
    }
}
