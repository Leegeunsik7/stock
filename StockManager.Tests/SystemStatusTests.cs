// ============================================================================
// SystemStatusTests — S18 #2: 전체 관제 탑 검증 (14개)
//
//  §1 심각도 로직 (6)   — 정상·동결·튜너동결·누수·승격(max)·전 미배선
//  §2 어그리게이터 (2)  — 실 센서 취합 정확성 · 동결 후 Critical 승격
//  §3 Null 안전 (2)     — 전 미주입 폴백 · 부분 주입 NRE 방어
//  §4 CSS·라벨 (2)      — 심각도 → CSS 클래스 / 한글 라벨
//  §5 동시성·누수 (2)   — 멀티스레드 요동 중 스냅샷 정합성 · 타이머 리스 0 수렴
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class SystemStatusTests
    {
        // ── §1 심각도 로직 (직접 구성한 불변 스냅샷) ─────────────────────────────

        [Fact]
        public void Severity_AllNominal_BridgeAlive_IsOk()
        {
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.Normal,
                MemoryPresent = true, LeakSuspected = false, LiveLeases = 3,
                TunerPresent = true, TunerFrozen = false, TunerSlices = 4, TunerDurationSec = 30,
                BridgePresent = true, BridgeAlive = true,
            };
            Assert.Equal(SystemSeverity.Ok, s.AttestationSeverity);
            Assert.Equal(SystemSeverity.Ok, s.MemorySeverity);
            Assert.Equal(SystemSeverity.Ok, s.TunerSeverity);
            Assert.Equal(SystemSeverity.Ok, s.BridgeSeverity);
            Assert.Equal(SystemSeverity.Ok, s.Overall);
        }

        [Fact]
        public void Severity_AttestationFrozen_IsCritical()
        {
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.EmergencyFreeze,
                MemoryPresent = true, TunerPresent = true, BridgePresent = true, BridgeAlive = true,
            };
            Assert.Equal(SystemSeverity.Critical, s.AttestationSeverity);
            Assert.Equal(SystemSeverity.Critical, s.Overall);
        }

        [Fact]
        public void Severity_TunerFrozen_IsCritical()
        {
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.Normal,
                MemoryPresent = true, TunerPresent = true, TunerFrozen = true,
                BridgePresent = true, BridgeAlive = true,
            };
            Assert.Equal(SystemSeverity.Critical, s.TunerSeverity);
            Assert.Equal(SystemSeverity.Critical, s.Overall);
        }

        [Fact]
        public void Severity_MemoryLeak_NoCritical_IsWarning()
        {
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.Normal,
                MemoryPresent = true, LeakSuspected = true,
                TunerPresent = true, BridgePresent = true, BridgeAlive = true,
            };
            Assert.Equal(SystemSeverity.Warning, s.MemorySeverity);
            Assert.Equal(SystemSeverity.Warning, s.Overall);   // Critical 없음 → 최고는 Warning
        }

        [Fact]
        public void Severity_Escalation_FreezePlusLeak_PicksCritical()
        {
            // 동결(Critical) + 누수(Warning) 동시 → Overall = max = Critical
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.EmergencyFreeze,
                MemoryPresent = true, LeakSuspected = true,
                TunerPresent = true, BridgePresent = true, BridgeAlive = true,
            };
            Assert.Equal(SystemSeverity.Warning,  s.MemorySeverity);
            Assert.Equal(SystemSeverity.Critical, s.AttestationSeverity);
            Assert.Equal(SystemSeverity.Critical, s.Overall);
        }

        [Fact]
        public void Severity_AllAbsent_DefaultsToNotice()
        {
            var s = new SystemHealthSnapshot();   // 전 필드 기본값 — 모두 미배선
            Assert.Equal(SystemSeverity.Notice, s.AttestationSeverity);
            Assert.Equal(SystemSeverity.Notice, s.MemorySeverity);
            Assert.Equal(SystemSeverity.Notice, s.TunerSeverity);
            Assert.Equal(SystemSeverity.Notice, s.BridgeSeverity);
            Assert.Equal(SystemSeverity.Notice, s.Overall);
        }

        // ── §2 어그리게이터 통합 ──────────────────────────────────────────────────

        [Fact]
        public void Capture_RealSensors_ReflectsLiveState()
        {
            var att   = new OrderAttestationService();
            var guard = new MemoryLeakGuard();
            guard.Acquire("a"); guard.Acquire("b");          // LiveLeases = 2
            var tuner  = new AdaptiveExecutionTuner(
                new FrictionFeedbackBridge(new SlippageSentinel()), 4, TimeSpan.FromSeconds(30));
            var bridge = new KiwoomBridgeClient();           // 연결 안 함 → IsProxyAlive=false

            var agg = new SystemStatusAggregator(att, guard, tuner, bridge);
            var s   = agg.Capture();

            Assert.True(s.AttestationPresent);
            Assert.Equal(GatingMode.Normal, s.GatingMode);
            Assert.True(s.MemoryPresent);
            Assert.Equal(2, s.LiveLeases);
            Assert.True(s.TunerPresent);
            Assert.Equal(4, s.TunerSlices);
            Assert.True(s.BridgePresent);
            Assert.False(s.BridgeAlive);                     // 프록시 미연결
            // 모두 정상이나 브리지 대기 → Overall = Notice
            Assert.Equal(SystemSeverity.Notice, s.Overall);
        }

        [Fact]
        public void Capture_AfterForceFreeze_ReportsCritical()
        {
            var att = new OrderAttestationService();
            var agg = new SystemStatusAggregator(att, MemoryLeakGuard.Shared);
            att.ForceFreeze("관제탑 통합 테스트 — 강제 동결");

            var s = agg.Capture();

            Assert.Equal(GatingMode.EmergencyFreeze, s.GatingMode);
            Assert.Equal(SystemSeverity.Critical, s.AttestationSeverity);
            Assert.Equal(SystemSeverity.Critical, s.Overall);
        }

        // ── §3 Null 안전 (안전 기본값 폴백) ───────────────────────────────────────

        [Fact]
        public void Capture_AllNull_NoThrow_AllNotice()
        {
            var agg = new SystemStatusAggregator();   // 전 센서 미주입
            var ex  = Record.Exception(() =>
            {
                var s = agg.Capture();
                Assert.False(s.AttestationPresent);
                Assert.False(s.MemoryPresent);
                Assert.False(s.TunerPresent);
                Assert.False(s.BridgePresent);
                Assert.Equal(SystemSeverity.Notice, s.Overall);
            });
            Assert.Null(ex);   // NullReferenceException 없음
        }

        [Fact]
        public void Capture_PartialInjection_NoThrow()
        {
            // 메모리 가드만 주입, 나머지 크래시/미주입 시뮬레이션
            var guard = new MemoryLeakGuard();
            guard.Acquire("solo");
            var agg = new SystemStatusAggregator(memGuard: guard);

            var s = agg.Capture();

            Assert.True(s.MemoryPresent);
            Assert.Equal(1, s.LiveLeases);
            Assert.False(s.AttestationPresent);   // 미주입 → 안전 폴백
            Assert.False(s.TunerPresent);
            Assert.False(s.BridgePresent);
        }

        // ── §4 CSS·라벨 매핑 ──────────────────────────────────────────────────────

        [Fact]
        public void SeverityCssClass_MapsAllLevels()
        {
            Assert.Equal("sev-critical", SystemStatusAggregator.SeverityCssClass(SystemSeverity.Critical));
            Assert.Equal("sev-warning",  SystemStatusAggregator.SeverityCssClass(SystemSeverity.Warning));
            Assert.Equal("sev-notice",   SystemStatusAggregator.SeverityCssClass(SystemSeverity.Notice));
            Assert.Equal("sev-ok",       SystemStatusAggregator.SeverityCssClass(SystemSeverity.Ok));
        }

        [Fact]
        public void SeverityLabel_MapsAllLevels()
        {
            Assert.Equal("위험", SystemStatusAggregator.SeverityLabel(SystemSeverity.Critical));
            Assert.Equal("경고", SystemStatusAggregator.SeverityLabel(SystemSeverity.Warning));
            Assert.Equal("주의", SystemStatusAggregator.SeverityLabel(SystemSeverity.Notice));
            Assert.Equal("정상", SystemStatusAggregator.SeverityLabel(SystemSeverity.Ok));
        }

        // ── §5 동시성 정합성 · 타이머 리스 누수 ───────────────────────────────────

        [Fact]
        public void Capture_UnderConcurrentChurn_ConsistentAndExceptionFree()
        {
            var att   = new OrderAttestationService();
            var guard = new MemoryLeakGuard();
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 1.0, ConsecutiveAlertCount = 1 };
            for (int i = 0; i < 6; i++) sentinel.RecordFill(Fill(100_000.0 + i), 100_000.0);
            sentinel.RecordFill(Fill(400_000.0), 100_000.0);   // 고-Z 이상치
            var tuner  = new AdaptiveExecutionTuner(new FrictionFeedbackBridge(sentinel),
                                                    4, TimeSpan.FromSeconds(30));
            var bridge = new KiwoomBridgeClient();
            var agg    = new SystemStatusAggregator(att, guard, tuner, bridge);

            var errors = new ConcurrentQueue<Exception>();
            var cts    = new CancellationTokenSource();
            var threads = new List<Thread>
            {
                // 원장 동결/해제 토글
                new Thread(() => { try { while (!cts.IsCancellationRequested)
                    { att.ForceFreeze("churn"); att.ManualRelease("sig"); } }
                    catch (Exception e) { errors.Enqueue(e); } }),
                // 리스 발급/반납
                new Thread(() => { try { while (!cts.IsCancellationRequested)
                    { var l = guard.Acquire("churn"); l.Dispose(); } }
                    catch (Exception e) { errors.Enqueue(e); } }),
                // 튜너 보정 (Interlocked CAS 경로)
                new Thread(() => { try { while (!cts.IsCancellationRequested)
                    { tuner.Calibrate(); } }
                    catch (Exception e) { errors.Enqueue(e); } }),
            };

            foreach (var t in threads) t.Start();

            // 캡처 스레드: 매 스냅샷의 불변식 검증 (토른 리드·NRE 없음)
            for (int i = 0; i < 5000; i++)
            {
                var s = agg.Capture();
                Assert.True(s.GatingMode is GatingMode.Normal or GatingMode.EmergencyFreeze);
                Assert.True(s.LiveLeases >= 0);
                Assert.InRange(s.TunerSlices, 2, 64);   // _packed 원자 읽기 — 토른 리드 없음
                Assert.True(Enum.IsDefined(typeof(SystemSeverity), s.Overall));
            }

            cts.Cancel();
            foreach (var t in threads) t.Join(2000);

            Assert.Empty(errors);
        }

        [Fact]
        public void TimerHost_Dispose_ConvergesLeaseCountToZero()
        {
            // 결정론: 격리된 가드 → 정확히 0 수렴
            var guard = new MemoryLeakGuard();
            var host  = new TimerHost(guard);
            Assert.Equal(1, guard.LiveCount);
            host.Dispose();
            Assert.Equal(0, guard.LiveCount);            // 타이머 해제 후 잔여 핸들러 0

            // 정적 Shared 경로도 동일 패턴으로 누수 없음 (before==after)
            int before = MemoryLeakGuard.Shared.LiveCount;
            using (new TimerHost(MemoryLeakGuard.Shared)) { }
            Assert.Equal(before, MemoryLeakGuard.Shared.LiveCount);
        }

        // ── 헬퍼 ─────────────────────────────────────────────────────────────────

        private static FillPayload Fill(double price) => new FillPayload
        {
            OrderId = 1L, SymbolCode = 5930L, FillPrice = price,
            FillQty = 10L, Side = 1, TimestampTicks = 1L,
        };

        // CommandCentrePage 의 타이머+리스 수명 패턴을 그대로 모사한 테스트용 호스트
        private sealed class TimerHost : IDisposable
        {
            private readonly Timer        _timer;
            private readonly ResourceLease _lease;
            private volatile bool         _disposed;

            public TimerHost(MemoryLeakGuard guard)
            {
                _lease = guard.Acquire("command-centre-timer");
                _timer = new Timer(_ => { if (_disposed) return; }, null, 1_000, 1_000);
            }

            public void Dispose()
            {
                _disposed = true;
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                _timer.Dispose();
                _lease.Dispose();   // 리스 반납 — LiveCount 0 수렴
            }
        }
    }
}
