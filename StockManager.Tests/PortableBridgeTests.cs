// ============================================================================
// PortableBridgeTests — S12 #3 단독 파일 포터블 브릿지 단위 테스트
//  · Export: 파일 포맷 헤더, 빈 패키지
//  · Import: 라운드트립, 잘못된 비밀번호, 변조 매직/암호문, 파일 길이 부족
//  · 전략 증명서 검증 실패 → 예외 + DB 롤백 (예측 미삽입)
//  · 예측 원자 머지: 중복 ForecastId → INSERT OR IGNORE
//  · 전략 저장: .ms2cart 파일 생성 확인
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace StockManager.Tests
{
    public class PortableBridgeTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;
        private readonly string        _cartDir;

        public PortableBridgeTests()
        {
            _dbPath  = Path.Combine(Path.GetTempPath(), $"bridge_{Guid.NewGuid():N}.db");
            _db      = new LocalDatabase(_dbPath);
            _ = new ForecastVaultService(_db);  // ForecastVault 스키마 초기화
            _cartDir = Path.Combine(Path.GetTempPath(), $"bridge_cart_{Guid.NewGuid():N}");
            Directory.CreateDirectory(_cartDir);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
            try { Directory.Delete(_cartDir, recursive: true); } catch { }
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static CartStrategy ValidStrategy(string name = "s1")
            => CartridgeEngine.Attest(name, 5, 20, 42);

        private static ForecastRecord SampleForecast(string id = "f001") => new(
            ForecastId:      id,
            CreatedAt:       new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EngineType:      ForecastEngineType.FutureCone,
            Symbol:          "005930",
            TargetDate:      new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            HorizonDays:     30,
            PredictedValue:  75_000.0,
            ConfidenceLow:   70_000.0,
            ConfidenceHigh:  80_000.0,
            ScenarioTag:     "base",
            CaptureMetaJson: "{}",
            ActualValue:     null,
            EvaluatedAt:     null,
            AbsoluteError:   null,
            Note:            "테스트");

        private long ForecastCount() =>
            _db.QueryScalar<long>("SELECT COUNT(*) FROM ForecastVault");

        // ════════════════════════════════════════════════════════════════════
        // 1. Export 기본 검증
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Export_ProducesValidMagicHeader()
        {
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord>(), "pw123");
            Assert.True(bytes.Length >= 8 + 16 + 16 + 16);
            Assert.Equal((byte)'Q', bytes[0]);
            Assert.Equal((byte)'B', bytes[1]);
            Assert.Equal((byte)'R', bytes[2]);
            Assert.Equal((byte)'I', bytes[3]);
            Assert.Equal((byte)'D', bytes[4]);
            Assert.Equal((byte)'G', bytes[5]);
            Assert.Equal((byte)'E', bytes[6]);
            Assert.Equal((byte)'1', bytes[7]);
        }

        [Fact]
        public void Export_EmptyPackage_Succeeds()
        {
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord>(), "pw");
            Assert.NotNull(bytes);
            Assert.True(bytes.Length > 40);
        }

        [Fact]
        public void Export_EmptyPassword_ThrowsArgumentException()
        {
            Assert.Throws<ArgumentException>(() =>
                PortableBridgeEngine.Export(
                    new List<CartStrategy>(), new List<ForecastRecord>(), ""));
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. Import — 오류 경로
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Import_WrongPassword_ThrowsInvalidData()
        {
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord>(), "correct");
            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(bytes, "wrong", _db, _cartDir));
        }

        [Fact]
        public void Import_TamperedCiphertext_ThrowsInvalidData()
        {
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord>(), "pw");
            // 암호문 첫 바이트 반전
            bytes[8 + 16 + 16] ^= 0xFF;
            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir));
        }

        [Fact]
        public void Import_TamperedMagic_ThrowsInvalidData()
        {
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord>(), "pw");
            bytes[0] = (byte)'X';
            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir));
        }

        [Fact]
        public void Import_TruncatedFile_ThrowsInvalidData()
        {
            var short_bytes = new byte[20];
            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(short_bytes, "pw", _db, _cartDir));
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. Import — 라운드트립 (전략 + 예측)
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Import_EmptyPackage_Succeeds_WithZeroCounts()
        {
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord>(), "pw");
            var result = PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir);
            Assert.True(result.Success);
            Assert.Equal(0, result.StrategiesImported);
            Assert.Equal(0, result.ForecastsImported);
        }

        [Fact]
        public void Import_ValidStrategy_CreatesCartridgeFile()
        {
            var strat = ValidStrategy("my-strat");
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy> { strat }, new List<ForecastRecord>(), "pw");
            var result = PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir);

            Assert.True(result.Success);
            Assert.Equal(1, result.StrategiesImported);
            var files = Directory.GetFiles(_cartDir, "*.ms2cart");
            Assert.Single(files);
        }

        [Fact]
        public void Import_ValidForecasts_InsertsToDb()
        {
            var fc = SampleForecast("fc-001");
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord> { fc }, "pw");
            var result = PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir);

            Assert.True(result.Success);
            Assert.Equal(1, result.ForecastsImported);
            Assert.Equal(1L, ForecastCount());
        }

        [Fact]
        public void Import_MultipleStrategiesAndForecasts_RoundTrip()
        {
            var strats = new List<CartStrategy>
            {
                CartridgeEngine.Attest("s1", 5, 20, 42),
                CartridgeEngine.Attest("s2", 10, 30, 99),
            };
            var fcs = new List<ForecastRecord>
            {
                SampleForecast("fc-A"),
                SampleForecast("fc-B"),
                SampleForecast("fc-C"),
            };
            var bytes = PortableBridgeEngine.Export(strats, fcs, "secret123");
            var result = PortableBridgeEngine.Import(bytes, "secret123", _db, _cartDir);

            Assert.True(result.Success);
            Assert.Equal(2, result.StrategiesImported);
            Assert.Equal(3, result.ForecastsImported);
            Assert.Equal(3L, ForecastCount());
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. 전략 증명서 변조 → 예외 + DB 롤백
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Import_TamperedStrategyAttest_ThrowsInvalidData()
        {
            // AttestTotalReturnPct를 의도적으로 다르게 설정
            var real = CartridgeEngine.Attest("s1", 5, 20, 42);
            var tampered = real with { AttestTotalReturnPct = real.AttestTotalReturnPct + 99.0 };

            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy> { tampered }, new List<ForecastRecord>(), "pw");
            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir));
        }

        [Fact]
        public void Import_TamperedStrategy_RollbacksForecasts()
        {
            // 전략 변조 + 유효한 예측 포함 → 전략 검증 실패 시 예측도 DB에 미삽입
            var real     = CartridgeEngine.Attest("s1", 5, 20, 42);
            var tampered = real with { AttestTrades = real.AttestTrades + 9999 };
            var fc       = SampleForecast("fc-rollback");

            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy> { tampered },
                new List<ForecastRecord> { fc },
                "pw");

            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir));

            // DB 에 예측이 삽입되지 않았어야 함
            Assert.Equal(0L, ForecastCount());
        }

        [Fact]
        public void Import_SecondStrategyTampered_FullRollback_NoForecastsInserted()
        {
            // 첫 번째 전략 유효, 두 번째 변조 → 전체 롤백
            var valid    = CartridgeEngine.Attest("s1", 5, 20, 42);
            var bad      = valid with { AttestMaxDrawdownPct = -999.0 };
            var fc       = SampleForecast("fc-partial");

            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy> { valid, bad },
                new List<ForecastRecord> { fc },
                "pw");

            Assert.Throws<InvalidDataException>(() =>
                PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir));
            Assert.Equal(0L, ForecastCount());
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. 예측 중복 처리 (INSERT OR IGNORE)
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Import_DuplicateForecastId_IgnoredGracefully()
        {
            var fc    = SampleForecast("fc-dup");
            var bytes = PortableBridgeEngine.Export(
                new List<CartStrategy>(), new List<ForecastRecord> { fc }, "pw");

            // 첫 번째 임포트 → 1건 삽입
            var r1 = PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir);
            Assert.Equal(1, r1.ForecastsImported);
            Assert.Equal(1L, ForecastCount());

            // 두 번째 임포트 (같은 ForecastId) → INSERT OR IGNORE, 여전히 1건
            var r2 = PortableBridgeEngine.Import(bytes, "pw", _db, _cartDir);
            Assert.True(r2.Success);
            Assert.Equal(1L, ForecastCount());
        }
    }
}
