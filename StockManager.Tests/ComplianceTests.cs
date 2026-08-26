// ============================================================================
// ComplianceTests — S17 #1: DB 감사 레이어 + AuditClient 파사드 검증 (12개)
//
//  §1 스키마 (3)     — FraudAlerts 테이블, Severity 컬럼, ReviewedAt nullable
//  §2 CRUD (6)       — AddFraudAlert, GetFraudAlerts limit, GetUnreviewed,
//                      MarkFraudReviewed(id+timestamp), 멱등성, MarkAllFraudReviewed
//  §3 통합·가드레일 (3) — 무효 거래→FraudAlert 자동 기록, PostFraudAlert null-safety,
//                        PostAlert DB 미주입 no-throw
//
//  격리: 각 테스트 클래스 인스턴스마다 고유 임시 SQLite 파일 사용 (IDisposable).
// ============================================================================
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ComplianceTests : IDisposable
    {
        private readonly string       _dbPath;
        private readonly LocalDatabase _db;

        public ComplianceTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + "-compliance.db");
            _db = new LocalDatabase(_dbPath);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // ── §1 스키마 ──────────────────────────────────────────────────────────

        [Fact]
        public void Schema_FraudAlertsTable_Exists()
        {
            // 테이블이 존재하면 SELECT가 예외 없이 실행되고 0 이상을 반환한다
            var count = _db.GetUnreviewedFraudCount();
            Assert.True(count >= 0);
        }

        [Fact]
        public void Schema_SeverityColumn_Exists()
        {
            _db.AddFraudAlert("A005930", "test-source", "severity-col-test", "MEDIUM");
            var alerts = _db.GetFraudAlerts(10);
            var fa0    = Assert.Single(alerts);
            Assert.Equal("MEDIUM", fa0.Severity);
        }

        [Fact]
        public void Schema_ReviewedAt_IsNullableByDefault()
        {
            _db.AddFraudAlert("A035420", "test-source", "reviewed-null-test");
            var alert = _db.GetFraudAlerts(1).First();
            Assert.Null(alert.ReviewedAt);
        }

        // ── §2 CRUD ───────────────────────────────────────────────────────────

        [Fact]
        public void AddFraudAlert_StoresAllFields()
        {
            _db.AddFraudAlert("A000660", "SlippageSentinel", "Z-score 3.2 초과", "HIGH");
            var list = _db.GetFraudAlerts(10);
            Assert.Single(list);
            var fa = list[0];
            Assert.Equal("A000660",          fa.Symbol);
            Assert.Equal("SlippageSentinel", fa.TransactionType);
            Assert.Equal("Z-score 3.2 초과", fa.Reason);
            Assert.Equal("HIGH",             fa.Severity);
            Assert.False(fa.IsReviewed);
        }

        [Fact]
        public void GetFraudAlerts_RespectsLimit()
        {
            for (int i = 0; i < 5; i++)
                _db.AddFraudAlert("X", "src", $"msg-{i}");
            var limited = _db.GetFraudAlerts(3);
            Assert.Equal(3, limited.Count);
        }

        [Fact]
        public void GetUnreviewedFraudCount_CountsOnlyUnreviewed()
        {
            _db.AddFraudAlert("A", "s1", "m1");
            _db.AddFraudAlert("B", "s2", "m2");
            _db.AddFraudAlert("C", "s3", "m3");
            var allIds = _db.GetFraudAlerts(10).Select(f => f.Id).ToList();
            _db.MarkFraudReviewed(allIds[0]);    // 1개만 검토 완료
            Assert.Equal(2, _db.GetUnreviewedFraudCount());
        }

        [Fact]
        public void MarkFraudReviewed_SetsTimestampCorrectly()
        {
            _db.AddFraudAlert("A005380", "src", "mark-test");
            var id  = _db.GetFraudAlerts(1).First().Id;
            var now = new DateTime(2026, 6, 19, 12, 0, 0, DateTimeKind.Utc);

            _db.MarkFraudReviewed(id, now);

            var fa = _db.GetFraudAlerts(1).First();
            Assert.True(fa.IsReviewed);
            Assert.NotNull(fa.ReviewedAt);
            // DateTime.TryParse는 로컬 KST로 변환하므로 UTC 정규화 후 비교
            Assert.Equal(now.ToUniversalTime(),
                         fa.ReviewedAt!.Value.ToUniversalTime(), TimeSpan.FromSeconds(1));
        }

        [Fact]
        public void MarkFraudReviewed_IsIdempotent()
        {
            _db.AddFraudAlert("A", "s", "idempotent-test");
            var id = _db.GetFraudAlerts(1).First().Id;
            _db.MarkFraudReviewed(id);
            _db.MarkFraudReviewed(id);              // 두 번 호출해도 예외 없음
            Assert.Equal(0, _db.GetUnreviewedFraudCount());
        }

        [Fact]
        public void MarkAllFraudReviewed_ClearsAllUnreviewed()
        {
            _db.AddFraudAlert("X1", "s", "m1");
            _db.AddFraudAlert("X2", "s", "m2");
            _db.AddFraudAlert("X3", "s", "m3");
            Assert.Equal(3, _db.GetUnreviewedFraudCount());

            var ts = new DateTime(2026, 6, 19, 9, 0, 0, DateTimeKind.Utc);
            _db.MarkAllFraudReviewed(ts);

            Assert.Equal(0, _db.GetUnreviewedFraudCount());
            foreach (var fa in _db.GetFraudAlerts(10))
            {
                Assert.True(fa.IsReviewed);
                Assert.NotNull(fa.ReviewedAt);
            }
        }

        // ── §3 통합·가드레일 ───────────────────────────────────────────────────

        [Fact]
        public void AddTransaction_InvalidData_AutoWritesFraudAlert()
        {
            // Price=0 은 StockValidator가 거부하므로 _WriteFraudAlert → FraudAlerts 1행 추가
            var bad = new Transaction
            {
                Symbol      = "A005930",
                Type        = TransactionType.Buy,
                Quantity    = 10,
                Price       = 0m,       // 유효하지 않음 — Price <= 0
                ExecutedAt  = DateTime.UtcNow,
            };

            Assert.Throws<ArgumentException>(() => _db.AddTransaction(bad));
            var written = Assert.Single(_db.GetFraudAlerts(10));
            Assert.Equal("A005930", written.Symbol);
        }

        [Fact]
        public void AuditClient_PostFraudAlert_NullTransaction_DoesNotThrow()
        {
            // null Transaction — FireAndForget 내부에서 null-safe 처리됨
            var ex = Record.Exception(() => AuditClient.PostFraudAlert(null!, null!));
            Assert.Null(ex);
        }

        [Fact]
        public void AuditClient_PostAlert_WithoutInitialize_DoesNotThrow()
        {
            // Initialize() 미호출 → _db == null → DB 경로 skip, HTTP만 fire-and-forget
            // 어떤 예외도 트레이딩 루프로 전파되어서는 안 된다
            var ex = Record.Exception(() =>
                AuditClient.PostAlert("A015760", "ExternalFeed", "비정상 주문 탐지", "HIGH"));
            Assert.Null(ex);
        }
    }
}
