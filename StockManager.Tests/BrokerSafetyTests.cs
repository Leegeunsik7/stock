// ============================================================================
// BrokerSafetyTests - 실거래 안전장치 회귀 테스트 (실제 체결 아님, 가드 로직만)
//  실주문 실행은 외부 API/자금이 필요해 검증 불가 → '오발주/한도초과를 막는' 보호 로직을 검증.
//  · BrokerHub.Create 라우팅(모의/미무장 거부/무장 라우터)
//  · LiveBrokerStub: 항상 주문 거부
//  · LiveRouterBroker: 주문 금액 상한 초과 시 거부 (네트워크 전에 차단)
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class BrokerSafetyTests
    {
        private static void WithDb(Action<LocalDatabase> test)
        {
            var path = Path.Combine(Path.GetTempPath(), "smbk_" + Guid.NewGuid().ToString("N") + ".db");
            try { using var db = new LocalDatabase(path); test(db); }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void Create_Default_IsPaper() => WithDb(db =>
        {
            var b = BrokerHub.Create(db);
            Assert.False(b.IsLive);
            Assert.IsType<PaperBroker>(b);
        });

        [Fact]
        public void Create_LiveButNotArmed_ReturnsStub_RejectsOrders() => WithDb(db =>
        {
            BrokerHub.SetLive(db, true);          // 실전 모드지만 무장 안 함
            var b = BrokerHub.Create(db);
            Assert.IsType<LiveBrokerStub>(b);
            var (ok, msg) = b.Place(PaperAsset.Coin, "KRW-BTC", "비트코인",
                PaperSide.Buy, PaperKind.Market, 1, 0, 50_000_000);
            Assert.False(ok);                     // 미무장 → 주문 거부
            Assert.Contains("무장", msg);
        });

        [Fact]
        public void Create_LiveArmed_ReturnsRouter() => WithDb(db =>
        {
            BrokerHub.SetLive(db, true);
            BrokerHub.SetArmed(db, true);
            var b = BrokerHub.Create(db);
            Assert.True(b.IsLive);
            Assert.IsType<LiveRouterBroker>(b);
        });

        [Fact]
        public void Router_RejectsOrderOverMaxKrw() => WithDb(db =>
        {
            BrokerHub.SetLive(db, true);
            BrokerHub.SetArmed(db, true);
            BrokerHub.SetMaxOrderKrw(db, 100_000m);     // 상한 10만원
            var router = new LiveRouterBroker(db);
            // 1개 × 20만원(시장가) = 20만원 > 10만원 → 네트워크 전에 한도 차단
            var (ok, msg) = router.Place(PaperAsset.Coin, "KRW-BTC", "비트코인",
                PaperSide.Buy, PaperKind.Market, 1, 0, 200_000m);
            Assert.False(ok);
            Assert.Contains("한도", msg);
        });

        [Fact]
        public void Stub_AlwaysRejects()
        {
            var (ok, _) = new LiveBrokerStub().Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Market, 10, 0, 70_000);
            Assert.False(ok);
        }

        [Fact]
        public void MaxOrderKrw_DefaultIs100k() => WithDb(db =>
            Assert.Equal(100_000m, BrokerHub.MaxOrderKrw(db)));   // 기본 상한 가드
    }
}
