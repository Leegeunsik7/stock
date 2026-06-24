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

        // ── v4.37.0 코인 시장 추론 + 픽커 권위 힌트 (US_NASDAQ 오분류 회귀) ──────────────
        private static (Market mkt, Currency ccy) ReadMarketCcy(LocalDatabase db, string sym)
        {
            var dt = db.Query("SELECT Market, Currency FROM Holdings WHERE Symbol=@s", new() { ["@s"] = sym });
            return dt.Rows.Count == 0 ? (Market.KRX_KOSPI, Currency.KRW)
                : ((Market)Safe.Int(dt.Rows[0]["Market"]), (Currency)Safe.Int(dt.Rows[0]["Currency"]));
        }

        [Fact]
        public void RecomputeAverage_CryptoSymbol_CorrectsStaleForeignMarket() => WithDb(db =>
        {
            // 과거 버그로 KRW-BTC가 US_NASDAQ/USD로 저장된 stale 보유 — 코인 거래 재계산 시 CRYPTO/KRW로 교정돼야.
            db.SaveHolding(new Holding { Symbol = "KRW-BTC", Name = "비트코인", Market = Market.US_NASDAQ, Currency = Currency.USD, Quantity = 0.13m, AvgPrice = 97_000_000m, CurrentPrice = 97_000_000m, FirstBuyDate = _baseAt, BrokerAccount = "" });
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy, Quantity = 0.13m, Price = 97_000_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "KRW-BTC");   // 힌트 없이도 'KRW-' 접두로 확정 교정
            var (mkt, ccy) = ReadMarketCcy(db, "KRW-BTC");
            Assert.Equal(Market.CRYPTO, mkt);   // US_NASDAQ → CRYPTO 교정(SaveHolding ON CONFLICT가 Market 갱신)
            Assert.Equal(Currency.KRW, ccy);
        });

        [Fact]
        public void Startup_CorrectsStaleCryptoHoldingMarket()
        {
            // 기존 DB에 코인이 US/USD로 잘못 저장돼 있으면 재오픈(InitSchema) 시 KRW- 일괄 교정으로 CRYPTO/KRW.
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smcryptofix_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var db = new LocalDatabase(path))
                    db.SaveHolding(new Holding { Symbol = "KRW-BTC", Name = "비트코인", Market = Market.US_NASDAQ, Currency = Currency.USD, Quantity = 0.13m, AvgPrice = 97_000_000m, CurrentPrice = 97_000_000m, FirstBuyDate = new DateTime(2025, 1, 1), BrokerAccount = "" });
                using (var db2 = new LocalDatabase(path))
                {
                    var dt = db2.Query("SELECT Market, Currency FROM Holdings WHERE Symbol='KRW-BTC'");
                    Assert.Equal((int)Market.CRYPTO, Safe.Int(dt.Rows[0]["Market"]));
                    Assert.Equal((int)Currency.KRW, Safe.Int(dt.Rows[0]["Currency"]));
                }
            }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { System.IO.File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void Crypto_Symbol_InfersCryptoMarket_NotUsStock() => WithDb(db =>
        {
            // KRW-BTC(7자)는 6자리 숫자가 아니라 과거엔 US_NASDAQ/USD로 오분류됐다 — 'KRW-' 접두 추론으로 CRYPTO/KRW.
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy, Quantity = 0.13m, Price = 99_000_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "KRW-BTC");
            var (mkt, ccy) = ReadMarketCcy(db, "KRW-BTC");
            Assert.Equal(Market.CRYPTO, mkt);
            Assert.Equal(Currency.KRW, ccy);
        });

        [Fact]
        public void MarketHint_OverridesInference_ForNewHolding() => WithDb(db =>
        {
            // 심볼 추론과 '다른' 힌트라야 두 축 모두 정직히 검증된다. '005930'은 6자리→추론상 KOSPI/KRW지만
            //   픽커 권위 힌트 US_NYSE/USD가 시장·통화 둘 다를 이겨야 한다(둘 다 추론값과 불일치 → 동어반복 아님).
            db.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 1, Price = 600_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "005930", null, Market.US_NYSE, Currency.USD);
            var (mkt, ccy) = ReadMarketCcy(db, "005930");
            Assert.Equal(Market.US_NYSE, mkt);   // 추론(KOSPI)을 이김
            Assert.Equal(Currency.USD, ccy);     // 추론(KRW)을 이김 — 통화축도 비공허
        });

        [Fact]
        public void Hint_Ignored_WhenHoldingExists_PreservesStored() => WithDb(db =>
        {
            // 힌트는 '신규 보유'에만 적용된다는 규약 검증 — 기존 보유엔 충돌 힌트를 넘겨도 저장값이 보존돼야 한다.
            db.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 10, Price = 70_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "005930");   // 신규 → KOSPI/KRW 확립(has=true)
            db.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 5, Price = 80_000m, ExecutedAt = _baseAt.AddMinutes(2) });
            PortfolioService.RecomputeAverage(db, "005930", null, Market.US_NYSE, Currency.USD);   // 충돌 힌트
            var (mkt, ccy) = ReadMarketCcy(db, "005930");
            Assert.Equal(Market.KRX_KOSPI, mkt);   // 힌트 무시 — 기존 보존
            Assert.Equal(Currency.KRW, ccy);
        });

        // ── v4.38.0 보유 수수료/세금은 '수동 필드'(거래로 안 덮음) + Id 기준 수정(중복행 방지) ──────────────
        [Fact]
        public void RecomputeAverage_PreservesManualHoldingFees_NotDerivedFromTransactions() => WithDb(db =>
        {
            // 직접 등록 보유에 수동 누적 수수료/세금 설정.
            db.SaveHolding(new Holding { Symbol = "005930", Name = "삼성전자", Market = Market.KRX_KOSPI, Currency = Currency.KRW, Quantity = 10, AvgPrice = 70_000m, CurrentPrice = 70_000m, FirstBuyDate = _baseAt, BrokerAccount = "키움증권", Commission = 5000m, Tax = 1234m });
            // 자체 수수료 150짜리 거래를 추가하고 재계산 → 수동값이 거래값으로 덮이면 안 된다(보존: fail-open 차단).
            db.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 5, Price = 80_000m, Commission = 150m, Tax = 9m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "005930");
            var dt = db.Query("SELECT Commission, Tax FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "005930" });
            Assert.Equal(5000m, Safe.Dec(dt.Rows[0]["Commission"]));   // 수동값 보존 (거래 150으로 덮지 않음)
            Assert.Equal(1234m, Safe.Dec(dt.Rows[0]["Tax"]));
        });

        [Fact]
        public void RecomputeAverage_NewHolding_FeesZero_FirstBuyDateFromEarliestTx() => WithDb(db =>
        {
            // 신규(거래기반) 보유: 수수료/세금은 0(거래 합계가 아님 — 거래별 비용은 Transactions/거래 이력에).
            db.AddTransaction(new Transaction { Symbol = "000660", Type = TransactionType.Buy, Quantity = 3, Price = 120_000m, Commission = 54m, Tax = 0m, ExecutedAt = _baseAt.AddMinutes(5) });
            PortfolioService.RecomputeAverage(db, "000660");
            var dt = db.Query("SELECT Commission, Tax, FirstBuyDate FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "000660" });
            Assert.Equal(0m, Safe.Dec(dt.Rows[0]["Commission"]));
            Assert.Equal(0m, Safe.Dec(dt.Rows[0]["Tax"]));
            Assert.NotEqual(default, Safe.DT(dt.Rows[0]["FirstBuyDate"]));               // 0001-01-01 default 아님
            Assert.Equal(_baseAt.AddMinutes(5), Safe.DT(dt.Rows[0]["FirstBuyDate"]));    // 최초 거래일 반영
        });

        [Fact]
        public void RecomputeAverage_PreservesMemoAndTags_OnTransactionRecord() => WithDb(db =>
        {
            // 거래 1건만 기록해도 RecomputeAverage→SaveHolding ON CONFLICT가 메모·태그를 빈값으로 파괴하던 회귀 차단.
            db.SaveHolding(new Holding { Symbol = "005930", Name = "삼성전자", Market = Market.KRX_KOSPI, Currency = Currency.KRW, Quantity = 10, AvgPrice = 70_000m, CurrentPrice = 70_000m, FirstBuyDate = _baseAt, BrokerAccount = "", Memo = "장기보유 핵심", Tags = new[] { "반도체", "코어" } });
            db.AddTransaction(new Transaction { Symbol = "005930", Type = TransactionType.Buy, Quantity = 5, Price = 80_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "005930");
            var dt = db.Query("SELECT Memo, Tags FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "005930" });
            Assert.Equal("장기보유 핵심", Safe.Str(dt.Rows[0]["Memo"]));   // 메모 보존
            Assert.Equal("반도체,코어", Safe.Str(dt.Rows[0]["Tags"]));      // 태그 보존
        });

        [Fact]
        public void UpdateHoldingById_EditsRow_NoDuplicate_PersistsBrokerNameMarketFees() => WithDb(db =>
        {
            // 직접 등록(증권사 빈값)으로 1행 생성.
            db.SaveHolding(new Holding { Symbol = "012860", Name = "모베이스전자", Market = Market.KRX_KOSPI, Currency = Currency.KRW, Quantity = 100, AvgPrice = 2600m, CurrentPrice = 2600m, FirstBuyDate = _baseAt, BrokerAccount = "" });
            int id = Safe.Int(db.Query("SELECT Id FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "012860" }).Rows[0]["Id"]);

            // 증권사·수수료·세금 + 종목명·시장 변경(ON CONFLICT 경로는 종목명/시장/증권사를 못 바꾸던 한계도 검증).
            db.UpdateHoldingById(new Holding { Id = id, Symbol = "012860", Name = "모베이스", Market = Market.KRX_KOSDAQ, Currency = Currency.KRW, Quantity = 100, AvgPrice = 2600m, CurrentPrice = 2600m, BrokerAccount = "키움증권", Commission = 39m, Tax = 0m });

            var dt = db.Query("SELECT Name, Market, BrokerAccount, Commission FROM Holdings WHERE Symbol=@s", new() { ["@s"] = "012860" });
            Assert.Equal(1, dt.Rows.Count);                                       // 중복행 없음(Id UPDATE)
            Assert.Equal("모베이스", Safe.Str(dt.Rows[0]["Name"]));                 // 종목명 변경 반영
            Assert.Equal((int)Market.KRX_KOSDAQ, Safe.Int(dt.Rows[0]["Market"]));  // 시장 변경 반영
            Assert.Equal("키움증권", Safe.Str(dt.Rows[0]["BrokerAccount"]));        // 증권사 반영
            Assert.Equal(39m, Safe.Dec(dt.Rows[0]["Commission"]));                // 수수료 반영
        });

        // ── v4.40.0 회귀: 매수 폼 BTC 저장 → 보유행 + 3분류 코인 카운트까지 끝단 재현 ──────────
        [Fact]
        public void TxFormBtcSave_CreatesCryptoHolding_ShowsInBreakdown() => WithDb(db =>
        {
            // SaveTx 가 하는 그대로: 거래 추가 + 픽커 권위 힌트(CRYPTO/KRW)로 재계산.
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy, Quantity = 0.13m, Price = 97_299_615m, ExecutedAt = _baseAt.AddMinutes(1) });
            PortfolioService.RecomputeAverage(db, "KRW-BTC", "비트코인", Market.CRYPTO, Currency.KRW);

            // GetAllHoldings(보유 목록과 동일 경로)에 KRW-BTC 가 떠야 한다(Quantity>0).
            var hs = db.GetAllHoldings();
            var btc = hs.Find(h => h.Symbol == "KRW-BTC");
            Assert.NotNull(btc);
            Assert.Equal(Market.CRYPTO, btc!.Market);
            Assert.Equal(0.13m, btc.Quantity);

            // 3분류 집계의 코인 버킷 카운트 1(스크린샷의 '0 종목' 회귀 차단).
            btc.CurrentPrice = 100_000_000m;   // 시세 갱신 후 가정
            var cats = PortfolioBreakdown.Compute(db.GetAllHoldings(), 1380m);
            Assert.Equal(1, cats[(int)AssetClass.Crypto].Count);
        });

        [Fact]
        public void AddTransaction_FractionalQty_StoredAsRealNotCoercedToZero() => WithDb(db =>
        {
            // 지휘관 환경 재현 의혹: 0.13 입력이 DB에 0으로 저장됨. 새 DB(현재 스키마)에서 실제 저장 storage class 확인.
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy, Quantity = 0.13m, Price = 97_299_615m, ExecutedAt = _baseAt.AddMinutes(1) });
            var dt = db.Query("SELECT Quantity AS q, typeof(Quantity) AS t FROM Transactions WHERE Symbol=@s", new() { ["@s"] = "KRW-BTC" });
            Assert.Equal(1, dt.Rows.Count);
            Assert.Equal(0.13m, Safe.Dec(dt.Rows[0]["q"]), 4);     // 0.13 보존(0으로 강제변환 아님)
            Assert.Equal("real", Safe.Str(dt.Rows[0]["t"]));        // REAL로 저장(integer 강제변환 아님)
        });

        [Fact]
        public void Schema_QuantityColumns_AreReal() => WithDb(db =>
        {
            // 신규 DB는 Quantity 컬럼이 REAL이어야 한다(소수 코인 수량 보존 — INTEGER 친화도 강제변환 회피).
            foreach (var t in new[] { "Transactions", "Holdings" })
            {
                var info = db.Query($"SELECT type AS t FROM pragma_table_info('{t}') WHERE lower(name)='quantity'");
                Assert.Equal(1, info.Rows.Count);
                Assert.Contains("REAL", Safe.Str(info.Rows[0]["t"]).ToUpperInvariant());
            }
        });

        [Fact]
        public void Migration_IntegerQuantityColumn_RebuiltToReal_PreservesData()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smmig_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                // 옛 스키마 모사: Quantity INTEGER 로 테이블 생성 + 데이터.
                using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE Transactions (Id INTEGER PRIMARY KEY AUTOINCREMENT, Symbol TEXT NOT NULL, Type INTEGER NOT NULL, Quantity INTEGER, Price REAL, Commission REAL DEFAULT 0, Tax REAL DEFAULT 0, ExecutedAt TEXT NOT NULL, BrokerAccount TEXT, Memo TEXT);
                        CREATE TABLE Holdings (Id INTEGER PRIMARY KEY AUTOINCREMENT, Symbol TEXT NOT NULL, Name TEXT, Market INTEGER, Currency INTEGER, Quantity INTEGER, AvgPrice REAL, CurrentPrice REAL, FirstBuyDate TEXT, BrokerAccount TEXT, Memo TEXT, Tags TEXT, UNIQUE(Symbol, BrokerAccount));
                        INSERT INTO Transactions(Symbol,Type,Quantity,Price,ExecutedAt) VALUES('005930',0,10,1000,'2025-01-01T09:00:00');";
                    cmd.ExecuteNonQuery();
                }
                // LocalDatabase 열기 → InitSchema가 Quantity INTEGER→REAL 재구성.
                using (var db = new LocalDatabase(path))
                {
                    foreach (var t in new[] { "Transactions", "Holdings" })
                    {
                        var info = db.Query($"SELECT type AS t FROM pragma_table_info('{t}') WHERE lower(name)='quantity'");
                        Assert.Equal(1, info.Rows.Count);
                        Assert.Contains("REAL", Safe.Str(info.Rows[0]["t"]).ToUpperInvariant());   // REAL로 전환
                    }
                    Assert.Equal(10m, Safe.Dec(db.Query("SELECT Quantity FROM Transactions WHERE Symbol='005930'").Rows[0]["Quantity"]));   // 기존 데이터 보존
                    db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy, Quantity = 0.13m, Price = 97_000_000m, ExecutedAt = new DateTime(2025, 1, 1, 9, 1, 0) });
                    Assert.Equal(0.13m, Safe.Dec(db.Query("SELECT Quantity FROM Transactions WHERE Symbol='KRW-BTC'").Rows[0]["Quantity"]), 4);   // 재구성 후 소수 저장
                }
            }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { System.IO.File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void Migration_VeryOldTransactions_MissingColumns_DoesNotSilentlyFail()
        {
            // 아주 오래된 스키마: Transactions에 Commission/Tax/BrokerAccount/Memo 가 없고 Quantity가 INTEGER.
            //   복사 컬럼을 '실제 존재하는 것만'으로 동적 구성하지 않으면 INSERT...SELECT가 'no such column'으로
            //   throw→롤백→마이그레이션 영영 미완료(0.13→0 버그 잔존)된다. 그 회귀를 차단함을 검증.
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "smmig2_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var c = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
                {
                    c.Open();
                    using var cmd = c.CreateCommand();
                    cmd.CommandText = @"
                        CREATE TABLE Transactions (Id INTEGER PRIMARY KEY AUTOINCREMENT, Symbol TEXT NOT NULL, Type INTEGER NOT NULL, Quantity INTEGER, Price REAL, ExecutedAt TEXT NOT NULL);
                        CREATE TABLE Holdings (Id INTEGER PRIMARY KEY AUTOINCREMENT, Symbol TEXT NOT NULL, Name TEXT, Market INTEGER, Currency INTEGER, Quantity INTEGER, AvgPrice REAL, CurrentPrice REAL, FirstBuyDate TEXT, BrokerAccount TEXT, Memo TEXT, Tags TEXT, UNIQUE(Symbol, BrokerAccount));
                        INSERT INTO Transactions(Symbol,Type,Quantity,Price,ExecutedAt) VALUES('005930',0,7,1000,'2025-01-01T09:00:00');";
                    cmd.ExecuteNonQuery();
                }
                using (var db = new LocalDatabase(path))
                {
                    var info = db.Query("SELECT type AS t FROM pragma_table_info('Transactions') WHERE lower(name)='quantity'");
                    Assert.Contains("REAL", Safe.Str(info.Rows[0]["t"]).ToUpperInvariant());   // 누락 컬럼이 있어도 REAL 전환 완료
                    Assert.Equal(7m, Safe.Dec(db.Query("SELECT Quantity FROM Transactions WHERE Symbol='005930'").Rows[0]["Quantity"]));   // 데이터 보존
                    db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy, Quantity = 0.13m, Price = 97_000_000m, ExecutedAt = new DateTime(2025, 1, 1, 9, 1, 0) });
                    Assert.Equal(0.13m, Safe.Dec(db.Query("SELECT Quantity FROM Transactions WHERE Symbol='KRW-BTC'").Rows[0]["Quantity"]), 4);   // 소수 저장
                }
            }
            finally { foreach (var s in new[] { "", "-wal", "-shm" }) { try { System.IO.File.Delete(path + s); } catch { } } }
        }

        [Fact]
        public void DeleteTransaction_Recompute_RecoversNetZeroHolding() => WithDb(db =>
        {
            // 지휘관 상황 재현: 매수 0.13 + (반복 시도 중 섞인) 매도 0.13 → 순수량 0 → 보유 목록에서 사라짐(qty 0).
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Buy,  Quantity = 0.13m, Price = 97_000_000m, ExecutedAt = _baseAt.AddMinutes(1) });
            db.AddTransaction(new Transaction { Symbol = "KRW-BTC", Type = TransactionType.Sell, Quantity = 0.13m, Price = 98_000_000m, ExecutedAt = _baseAt.AddMinutes(2) });
            PortfolioService.RecomputeAverage(db, "KRW-BTC", "비트코인", Market.CRYPTO, Currency.KRW);
            Assert.Equal(0m, ReadQty(db, "KRW-BTC"));   // 순수량 0(보유행 수량 0 → Quantity>0 필터에서 빠짐)

            // 거래 이력에서 매도를 삭제 → 재계산 → 0.13 복구(자가 정리 경로).
            long sellId = Safe.Long(db.Query("SELECT Id FROM Transactions WHERE Symbol=@s AND Type=@t",
                new() { ["@s"] = "KRW-BTC", ["@t"] = (int)TransactionType.Sell }).Rows[0]["Id"]);
            db.DeleteTransaction(sellId);
            PortfolioService.RecomputeAverage(db, "KRW-BTC", "비트코인", Market.CRYPTO, Currency.KRW);
            Assert.Equal(0.13m, ReadQty(db, "KRW-BTC"), 3);   // 매수만 남아 0.13 복구
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
