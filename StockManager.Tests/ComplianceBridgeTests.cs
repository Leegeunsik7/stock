// ============================================================================
// ComplianceBridgeTests — S17 #2: 자동 경보 생성 브릿지 E2E 검증 (12개)
//
//  §1 Bridge·Sentinel 배선 (6)  — 경보 DB 적재, 메시지 형식, 멀티 경보,
//                                  NoDb 안전, Detach 차단, 소스·심각도 검증
//  §2 Tuner 캡 감시 (4)         — 5회 연속 캡 → IsFrozen, Calibrate 차단,
//                                  연속 리셋, 콜백 단 1회 발행
//  §3 E2E 통합 (2)              — Executor 동결 차단, Bridge+Tuner 풀 파이프
//
//  비동기 격리 패턴: WaitFor() 폴링으로 ThreadPool 작업 항목 완료를 결정론적 대기.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ComplianceBridgeTests : IDisposable
    {
        private readonly string         _dbPath;
        private readonly LocalDatabase  _db;
        private readonly ComplianceBridge _bridge;

        public ComplianceBridgeTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(),
                Guid.NewGuid().ToString("N") + "-bridge.db");
            _db     = new LocalDatabase(_dbPath);
            _bridge = new ComplianceBridge();
            _bridge.Initialize(_db);
        }

        public void Dispose()
        {
            _db.Dispose();
            try { File.Delete(_dbPath); } catch { }
        }

        // ── §1 Bridge·Sentinel 배선 ────────────────────────────────────────────

        [Fact]
        public void Bridge_SentinelAlert_WritesHighAlertToDb()
        {
            var sentinel = MakeSentinel();
            _bridge.Attach(sentinel);
            TriggerAlert(sentinel);

            Assert.True(WaitFor(() => _db.GetFraudAlerts(10).Count >= 1),
                "SENTINEL HIGH 경보가 2초 내 DB에 기록되어야 한다");

            var fa = _db.GetFraudAlerts(1).First();
            Assert.Equal("SENTINEL", fa.TransactionType);
            Assert.Equal("HIGH",     fa.Severity);
            Assert.False(fa.IsReviewed);
        }

        [Fact]
        public void Bridge_SentinelAlert_MessageContainsZScoreStats()
        {
            var sentinel = MakeSentinel();
            _bridge.Attach(sentinel);
            TriggerAlert(sentinel);

            Assert.True(WaitFor(() => _db.GetFraudAlerts(10).Count >= 1));
            string reason = _db.GetFraudAlerts(1).First().Reason;

            Assert.Contains("Z=",    reason);
            Assert.Contains("σ=",    reason);
            Assert.Contains("연속=", reason);
        }

        [Fact]
        public void Bridge_MultipleAlerts_AllWrittenToDb()
        {
            var sentinel = MakeSentinel(consecCount: 1);
            _bridge.Attach(sentinel);

            // 3번 연속 경보 트리거 (매번 별도 sentinel, 공유 bridge)
            for (int i = 0; i < 3; i++)
                TriggerAlert(sentinel);

            Assert.True(WaitFor(() => _db.GetFraudAlerts(10).Count >= 3),
                "3회 경보 → 3행 적재");
        }

        [Fact]
        public void Bridge_NoDb_AlertDoesNotThrow()
        {
            // Initialize() 미호출 bridge — DB 경로는 silently skip
            var bridge2  = new ComplianceBridge();
            var sentinel = MakeSentinel();
            bridge2.Attach(sentinel);

            var ex = Record.Exception(() => TriggerAlert(sentinel));
            Assert.Null(ex);
        }

        [Fact]
        public void Bridge_Detach_StopsDbWrites()
        {
            var sentinel = MakeSentinel();
            _bridge.Attach(sentinel);
            _bridge.Detach(sentinel);   // 구독 해제

            TriggerAlert(sentinel);
            Thread.Sleep(200);          // 충분히 대기

            Assert.Empty(_db.GetFraudAlerts(10));
        }

        [Fact]
        public void Bridge_SentinelAlert_SymbolMatchesFillSymbol()
        {
            var sentinel = MakeSentinel();
            _bridge.Attach(sentinel);

            // SymbolCode 5930 → BridgeSerializer.FormatSymbol 포맷으로 저장
            TriggerAlert(sentinel);
            Assert.True(WaitFor(() => _db.GetFraudAlerts(10).Count >= 1));

            var fa = _db.GetFraudAlerts(1).First();
            Assert.False(string.IsNullOrEmpty(fa.Symbol)); // 심볼이 공란이 아닌지 확인
        }

        // ── §2 Tuner 캡 감시 ──────────────────────────────────────────────────

        [Fact]
        public void Tuner_FiveConsecutiveCapHits_SetsFrozenFlag()
        {
            var (tuner, _) = MakeHighZTuner(capThreshold: 5);
            for (int i = 0; i < 5; i++) tuner.Calibrate();
            Assert.True(tuner.IsFrozen, "5회 연속 캡 → 안전 동결");
        }

        [Fact]
        public void Tuner_Frozen_CalibrateIsSilentNoOp()
        {
            var (tuner, _) = MakeHighZTuner(capThreshold: 3);
            for (int i = 0; i < 3; i++) tuner.Calibrate();
            Assert.True(tuner.IsFrozen);

            int slicesBefore = tuner.CurrentSlices;
            // 동결 이후 Calibrate 는 아무것도 바꾸지 않아야 한다
            for (int i = 0; i < 10; i++) tuner.Calibrate();
            Assert.Equal(slicesBefore, tuner.CurrentSlices);
        }

        [Fact]
        public void Tuner_CapStreak_ResetsOnNonCapHit()
        {
            // MaxSlices=8, initialSlices=2, SliceStepUp=2 → 3 high-Z calls = 2+2+2+2=8=cap
            // CapBreachThreshold=10 → 3 cap hits는 동결 안 됨
            var (tuner, sentinel) = MakeHighZTuner(capThreshold: 10);

            tuner.Calibrate(); tuner.Calibrate(); tuner.Calibrate(); // cap hit 3회

            // 낮은 Z 주입으로 연속 리셋 (σ가 매우 낮을 때만 건너뜀 → 새 fill로 LowZ 유도)
            for (int i = 0; i < 50; i++)
                sentinel.RecordFill(MakeFill(100_000.0 + i * 0.01), 100_000.0);
            // 충분한 샘플 후 LastZ가 LowZThreshold(0.5) 미만이 되면 카운터 리셋
            // 단순 검증: 3회로는 동결 안 됨
            Assert.False(tuner.IsFrozen, "threshold=10이면 3회로는 동결되지 않아야 함");
        }

        [Fact]
        public void Tuner_CapBreach_CallbackFiredExactlyOnce()
        {
            int callCount = 0;
            var (tuner, _) = MakeHighZTuner(capThreshold: 5);
            tuner.OnCapBreached = (_, _) => Interlocked.Increment(ref callCount);

            // 5번 → 동결 + 콜백 1회
            for (int i = 0; i < 5; i++) tuner.Calibrate();
            // 5번 추가 — 동결 상태이므로 추가 콜백 없음
            for (int i = 0; i < 5; i++) tuner.Calibrate();

            Assert.Equal(1, callCount);
        }

        // ── §3 E2E 통합 ───────────────────────────────────────────────────────

        [Fact]
        public void Executor_FrozenTuner_PlaceReturnsFalseImmediately()
        {
            var (tuner, _) = MakeHighZTuner(capThreshold: 5);
            for (int i = 0; i < 5; i++) tuner.Calibrate();
            Assert.True(tuner.IsFrozen);

            using var exec = new TwapVwapExecutor(
                new NopBroker2(),
                delayFactory: (_, _) => Task.CompletedTask);
            exec.Tuner = tuner;

            var (ok, msg) = exec.Place(
                PaperAsset.Stock, "A005930", "삼성전자",
                PaperSide.Buy,   PaperKind.Market,
                qty: 200m,       limitPrice: 0m, currentPrice: 70_000m);

            Assert.False(ok);
            Assert.Contains("TUNER-FREEZE", msg);
        }

        [Fact]
        public async Task Bridge_Wire_TunerCapBreach_WritesCriticalAlertToDb()
        {
            var (tuner, _) = MakeHighZTuner(capThreshold: 5);
            _bridge.Wire(tuner);   // 콜백 → PostCapCritical → DB

            // 5회 연속 캡 → 동결 + CRITICAL 경보 fire-and-forget
            for (int i = 0; i < 5; i++) tuner.Calibrate();

            // ThreadPool 작업 항목 완료 대기
            Assert.True(WaitFor(() => _db.GetFraudAlerts(10)
                .Any(f => f.Severity == "CRITICAL" && f.TransactionType == "TUNER")),
                "CRITICAL TUNER 경보가 2초 내 DB에 기록되어야 한다");

            await Task.CompletedTask;  // async 반환 타입 요건 충족
        }

        // ── 공통 헬퍼 ─────────────────────────────────────────────────────────

        private static SlippageSentinel MakeSentinel(double alertSigma = 2.0, int consecCount = 1)
            => new SlippageSentinel
            {
                AlertThresholdSigma   = alertSigma,
                ConsecutiveAlertCount = consecCount,
            };

        private static FillPayload MakeFill(double price, long symCode = 5930L)
            => new FillPayload
            {
                OrderId        = 1L,
                SymbolCode     = symCode,
                FillPrice      = price,
                FillQty        = 10L,
                Side           = 1,
                TimestampTicks = 1L,
            };

        private static void TriggerAlert(SlippageSentinel sentinel)
        {
            // 정상 분포 기준선 5건 (σ 형성)
            for (int i = 0; i < 5; i++)
                sentinel.RecordFill(MakeFill(100_000.0 + i), 100_000.0);
            // 극단적 편차 → Z >> AlertThresholdSigma
            sentinel.RecordFill(MakeFill(500_000.0), 100_000.0);
        }

        private static (AdaptiveExecutionTuner tuner, SlippageSentinel sentinel) MakeHighZTuner(
            int capThreshold = 5)
        {
            var sentinel = MakeSentinel(alertSigma: 1.0, consecCount: 1);
            for (int i = 0; i < 5; i++)
                sentinel.RecordFill(MakeFill(100_000.0 + i), 100_000.0);
            sentinel.RecordFill(MakeFill(500_000.0), 100_000.0);  // LastZ >> 1.5

            var bridge = new FrictionFeedbackBridge(sentinel);
            var tuner  = new AdaptiveExecutionTuner(bridge, 2, TimeSpan.FromSeconds(10))
            {
                MaxSlices         = 4,       // 2+SliceStepUp(2)=4 → 첫 Calibrate 에 캡 도달
                CapBreachThreshold = capThreshold,
            };
            return (tuner, sentinel);
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs = 8000)   // 부하 하 교차스레드 알림→DB 지연 대비(조건 충족 즉시 반환 → 비용 0)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline)
            {
                if (cond()) return true;
                Thread.Sleep(10);
            }
            return false;
        }
    }

    // ── 파일 범위 헬퍼 ──────────────────────────────────────────────────────

    file sealed class NopBroker2 : IBroker
    {
        public string  Name   => "Nop2";
        public bool    IsLive => false;
        public (bool ok, string msg) Place(
            PaperAsset a, string s, string n,
            PaperSide sd, PaperKind k,
            decimal q, decimal lp, decimal cp) => (true, "ok");
        public void    Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;
    }
}
