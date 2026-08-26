// ============================================================================
// DbMaintenanceTests - VACUUM/ANALYZE/checkpoint 통합 테스트 (임시 DB)
//  실제 SQLite 명령을 임시 파일 DB에서 실행해 예외 없이 완료되고 DB가 멀쩡한지 검증.
// ============================================================================
using System;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    public class DbMaintenanceTests
    {
        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smmt_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void Run_CompletesAndDbStillUsable() => WithDb(db =>
        {
            // 데이터 적재 후 일부 삭제 → 파편화 유도
            for (int i = 0; i < 200; i++)
                db.AddTransaction(new Transaction { Symbol = "00" + (i % 50), Type = TransactionType.Buy, Quantity = 1, Price = 1000, ExecutedAt = new DateTime(2025, 1, 1).AddMinutes(i) });
            db.Execute("DELETE FROM Transactions WHERE Quantity=1");   // 전부 삭제 → 빈 페이지

            var (before, after) = DbMaintenance.Run(db);
            Assert.True(before >= 0 && after >= 0);

            // VACUUM 후에도 정상 동작(쓰기/읽기)
            db.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 10, Price = 70000, ExecutedAt = DateTime.Now });
            var dt = db.Query("SELECT COUNT(*) AS c FROM Transactions WHERE Symbol='005930'");
            Assert.Equal(1, Safe.Int(dt.Rows[0]["c"]));
        });

        [Fact]
        public void SizeKb_NonNegative() => WithDb(db => Assert.True(DbMaintenance.SizeKb(db) >= 0));
    }
}
