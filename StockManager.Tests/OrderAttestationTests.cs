// ============================================================================
// OrderAttestationTests — S12 #2 거래 증적 감사 추적기 단위 테스트
//  · Reconcile: 통과 / 예수금 미스매치 / 수량 미스매치 / 부재 종목
//  · GatingMode 전환: Normal → EmergencyFreeze → Manual Release
//  · AssertNormal: 정상 시 무동작 / 동결 시 InvalidOperationException
//  · ManualRelease: 서명 필수 / 서명 없이 해제 시도 ArgumentException
//  · 허용 한도(Tolerance) 내외 경계 검증
//  · 감사 로그 순서·축적 검증
//  · ShadowBroker 통합: 동결 시 Place 차단 / 해제 후 재개
//  · CartridgeGenome 통합: 동결 시 Evolve 차단
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    public class OrderAttestationTests : IDisposable
    {
        private readonly string        _dbPath;
        private readonly LocalDatabase _db;
        private readonly PaperBroker   _paper;

        public OrderAttestationTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"attest_{Guid.NewGuid():N}.db");
            _db     = new LocalDatabase(_dbPath);
            _paper  = new PaperBroker(_db);
            _paper.ResetSeed(10_000_000m);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static OrderAttestationService DefaultSvc() => new();

        private static LocalState  LS(decimal cash, Dictionary<string, decimal>? h = null)
            => new(cash, h ?? new Dictionary<string, decimal>());
        private static BrokerState BS(decimal cash, Dictionary<string, decimal>? h = null)
            => new(cash, h ?? new Dictionary<string, decimal>());

        private static readonly DateTime T0 =
            new(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc);

        private static ShadowNoiseConfig ZeroNoise() => new()
        {
            Enabled = true, BaseDelayMs = 0, MaxDelayMs = 0,
            SlippageBasePct = 0.0, SlippageVolMultiplier = 0.0, Seed = 0,
        };

        // ════════════════════════════════════════════════════════════════════════
        // 1. Reconcile — 통과 조건
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Reconcile_IdenticalEmptyState_Passes()
        {
            var svc    = DefaultSvc();
            var result = svc.Reconcile(LS(0m), BS(0m));
            Assert.True(result.Passed);
            Assert.Empty(result.Mismatches);
            Assert.Equal(GatingMode.Normal, svc.Mode);
        }

        [Fact]
        public void Reconcile_IdenticalNonZeroState_Passes()
        {
            var svc = DefaultSvc();
            var h = new Dictionary<string, decimal> { ["005930"] = 10m, ["000660"] = 5m };
            var result = svc.Reconcile(LS(1_000_000m, h), BS(1_000_000m,
                new Dictionary<string, decimal> { ["005930"] = 10m, ["000660"] = 5m }));
            Assert.True(result.Passed);
            Assert.Equal(GatingMode.Normal, svc.Mode);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 2. Reconcile — 동결 조건
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Reconcile_CashMismatch_FreezesGating()
        {
            var svc    = DefaultSvc();
            var result = svc.Reconcile(LS(1_000_000m), BS(999_999m));
            Assert.False(result.Passed);
            Assert.Single(result.Mismatches);
            Assert.Equal("예수금(KRW)", result.Mismatches[0].Field);
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);
        }

        [Fact]
        public void Reconcile_HoldingMismatch_FreezesGating()
        {
            var svc    = DefaultSvc();
            var local  = new Dictionary<string, decimal> { ["005930"] = 10m };
            var broker = new Dictionary<string, decimal> { ["005930"] = 9m  };
            var result = svc.Reconcile(LS(0m, local), BS(0m, broker));
            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);
            Assert.Equal("005930", result.Mismatches[0].Field);
        }

        [Fact]
        public void Reconcile_BrokerHasExtraSymbol_FreezesGating()
        {
            var svc    = DefaultSvc();
            // broker reports 005930 but local has none
            var result = svc.Reconcile(
                LS(0m),
                BS(0m, new Dictionary<string, decimal> { ["005930"] = 1m }));
            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);
        }

        [Fact]
        public void Reconcile_LocalHasExtraSymbol_FreezesGating()
        {
            var svc    = DefaultSvc();
            var result = svc.Reconcile(
                LS(0m, new Dictionary<string, decimal> { ["000660"] = 3m }),
                BS(0m));
            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);
        }

        [Fact]
        public void Reconcile_MultipleMismatches_AllReported()
        {
            var svc    = DefaultSvc();
            var local  = new Dictionary<string, decimal> { ["005930"] = 10m, ["000660"] = 5m };
            var broker = new Dictionary<string, decimal> { ["005930"] = 9m,  ["000660"] = 3m };
            var result = svc.Reconcile(LS(500m, local), BS(400m, broker));
            // Cash + 005930 + 000660 = 3 mismatches
            Assert.Equal(3, result.Mismatches.Count);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 3. 허용 한도 (Tolerance)
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void Reconcile_CashWithinTolerance_Passes()
        {
            var svc = new OrderAttestationService { CashToleranceKrw = 100m };
            // delta = 50 <= tolerance = 100 → pass
            var result = svc.Reconcile(LS(1_000_050m), BS(1_000_000m));
            Assert.True(result.Passed);
        }

        [Fact]
        public void Reconcile_CashExceedsTolerance_Freezes()
        {
            var svc = new OrderAttestationService { CashToleranceKrw = 100m };
            // delta = 101 > tolerance = 100 → freeze
            var result = svc.Reconcile(LS(1_000_101m), BS(1_000_000m));
            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);
        }

        [Fact]
        public void Reconcile_QtyWithinTolerance_Passes()
        {
            var svc = new OrderAttestationService { QtyTolerance = 0.5m };
            var local  = new Dictionary<string, decimal> { ["005930"] = 10.3m };
            var broker = new Dictionary<string, decimal> { ["005930"] = 10.0m };
            // delta = 0.3 <= 0.5 → pass
            var result = svc.Reconcile(LS(0m, local), BS(0m, broker));
            Assert.True(result.Passed);
        }

        [Fact]
        public void Reconcile_QtyExceedsTolerance_Freezes()
        {
            var svc = new OrderAttestationService { QtyTolerance = 0.1m };
            var local  = new Dictionary<string, decimal> { ["005930"] = 10.5m };
            var broker = new Dictionary<string, decimal> { ["005930"] = 10.0m };
            // delta = 0.5 > 0.1 → freeze
            var result = svc.Reconcile(LS(0m, local), BS(0m, broker));
            Assert.False(result.Passed);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 4. AssertNormal
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void AssertNormal_WhenNormal_DoesNotThrow()
        {
            var svc = DefaultSvc();
            svc.AssertNormal("test");  // must not throw
        }

        [Fact]
        public void AssertNormal_WhenFrozen_ThrowsInvalidOperationException()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(1m), BS(0m));  // trigger freeze
            Assert.Throws<InvalidOperationException>(() => svc.AssertNormal("test"));
        }

        // ════════════════════════════════════════════════════════════════════════
        // 5. ManualRelease
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ManualRelease_WithSignature_ResetsNormal()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(1m), BS(0m));
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);

            svc.ManualRelease("gslee@angtec.com", "테스트 정합 완료");
            Assert.Equal(GatingMode.Normal, svc.Mode);
        }

        [Fact]
        public void ManualRelease_EmptySignature_ThrowsArgumentException()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(1m), BS(0m));
            Assert.Throws<ArgumentException>(() => svc.ManualRelease("   "));
        }

        [Fact]
        public void ManualRelease_LogHasSignature()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(1m), BS(0m));
            svc.ManualRelease("tester@example.com");
            var top = svc.AuditLog[0];  // most recent
            Assert.Equal("tester@example.com", top.Signature);
            Assert.True(top.Passed);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 6. 감사 로그 순서·축적
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void AuditLog_MostRecentFirst()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(0m), BS(0m));       // pass
            svc.Reconcile(LS(1m), BS(0m));       // freeze

            var log = svc.AuditLog;
            Assert.Equal(2, log.Count);
            Assert.False(log[0].Passed);   // freeze entry first
            Assert.True(log[1].Passed);    // pass entry second
        }

        [Fact]
        public void AuditLog_Accumulates_AcrossMultipleCycles()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(1m), BS(0m));              // freeze
            svc.ManualRelease("sig1");                   // release
            svc.Reconcile(LS(0m), BS(0m));              // pass
            Assert.Equal(3, svc.AuditLog.Count);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 7. ShadowBroker 통합 — 동결 차단 / 해제 후 재개
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void ShadowBroker_Place_WhenFrozen_ThrowsInvalidOperationException()
        {
            var svc    = DefaultSvc();
            var broker = new ShadowBroker(_paper, ZeroNoise(), null, svc);
            broker.Tick(T0, (_, _) => 1000m);

            svc.Reconcile(LS(1m), BS(0m));  // freeze
            Assert.Throws<InvalidOperationException>(() =>
                broker.Place(PaperAsset.Stock, "A", "A",
                    PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m));
        }

        [Fact]
        public void ShadowBroker_Place_AfterManualRelease_Succeeds()
        {
            var svc    = DefaultSvc();
            var broker = new ShadowBroker(_paper, ZeroNoise(), null, svc);
            broker.Tick(T0, (_, _) => 1000m);

            svc.Reconcile(LS(1m), BS(0m));   // freeze
            svc.ManualRelease("release-sig"); // unfreeze

            var (ok, _) = broker.Place(PaperAsset.Stock, "A", "A",
                PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            Assert.True(ok);
        }

        [Fact]
        public void ShadowBroker_Place_NoGating_WorksWithoutService()
        {
            // gating=null → no intercept
            var broker = new ShadowBroker(_paper, ZeroNoise());
            broker.Tick(T0, (_, _) => 1000m);
            var (ok, _) = broker.Place(PaperAsset.Stock, "A", "A",
                PaperSide.Buy, PaperKind.Market, 1m, 0m, 1000m);
            Assert.True(ok);
        }

        // ════════════════════════════════════════════════════════════════════════
        // 8. CartridgeGenome 통합 — 동결 차단
        // ════════════════════════════════════════════════════════════════════════

        [Fact]
        public void CartridgeGenome_Evolve_WhenFrozen_ThrowsInvalidOperationException()
        {
            var svc = DefaultSvc();
            svc.Reconcile(LS(1m), BS(0m));  // freeze

            var candles = BuildCandles(120);
            var seeds   = new List<StrategySpec> { StrategyCompiler.FromMaCross(5, 20) };
            Assert.Throws<InvalidOperationException>(() =>
                CartridgeGenome.Evolve(seeds, candles, Market.KRX_KOSPI,
                    generations: 1, populationSize: 4, gating: svc));
        }

        [Fact]
        public void CartridgeGenome_Evolve_WhenNormal_Succeeds()
        {
            var svc = DefaultSvc();
            // mode = Normal → no throw
            var candles = BuildCandles(120);
            var seeds   = new List<StrategySpec> { StrategyCompiler.FromMaCross(5, 20) };
            var result  = CartridgeGenome.Evolve(seeds, candles, Market.KRX_KOSPI,
                generations: 1, populationSize: 4, gating: svc);
            Assert.True(result.IsValid);
        }

        [Fact]
        public void CartridgeGenome_Evolve_NoGating_WorksWithoutService()
        {
            // gating=null → backward-compatible
            var candles = BuildCandles(120);
            var seeds   = new List<StrategySpec> { StrategyCompiler.FromMaCross(5, 20) };
            var result  = CartridgeGenome.Evolve(seeds, candles, Market.KRX_KOSPI,
                generations: 1, populationSize: 4);
            Assert.True(result.IsValid);
        }

        // ── 헬퍼: 가상 캔들 생성 ─────────────────────────────────────────────────

        private static List<Candle> BuildCandles(int count)
        {
            var list  = new List<Candle>(count);
            decimal p = 10_000m;
            for (int i = 0; i < count; i++)
            {
                p += (i % 3 == 0 ? 50m : -30m);
                list.Add(new Candle
                {
                    Date   = new DateTime(2025, 1, 1).AddDays(i),
                    Open   = p - 20m,
                    High   = p + 50m,
                    Low    = p - 50m,
                    Close  = p,
                    Volume = 100_000 + i * 1000,
                });
            }
            return list;
        }
    }
}
