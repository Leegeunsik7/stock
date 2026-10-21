// ============================================================================
// Stage3SafetyInterlockSimulationTests — v11.4.4 Stage 3 실시간 안전 Disarm 인터락 검증
//
//   docs/CANARY_DEPLOYMENT_RUNBOOK.md Stage 3(Real-Time Safety Interlock, 50ms Disarm) 규격을 결정론적으로 실증한다:
//     ★ 틱 레이턴시(>50ms) / HTTP 429 연쇄 / 슬리피지 초과 → 즉시 원자적 Disarm + NoteAnomaly + sticky 락아웃.
//     ★ (Think-Beyond) 다중 이상 신호 경합에도 Disarm 지연 0(단일 원자 OR) · Disarm 직후 잔여 주문 비동기 사출 0.
//
//   검증 대상: CanaryHealthSentinel(50ms Disarm 게이트) + MultiTelemetryDisarmInterlock·AnomalyFlightRecorder(이중 엔진).
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Stage3SafetyInterlockSimulationTests
    {
        private static CanaryHealthSentinel Armed(long cap = 1_000_000L)
        {
            var c = new CanaryHealthSentinel();
            Assert.True(c.Arm(cap, valveWired: true, liveArmingWired: true));
            return c;
        }

        // ── §1 3대 이상 트리거 → 즉시 Disarm + 사유 기록 + sticky ────────────────
        [Theory]
        [InlineData(CanaryKillReason.LatencyBreach)]    // 틱 레이턴시 >50ms
        [InlineData(CanaryKillReason.NetworkAnomaly)]   // HTTP 429 연쇄
        [InlineData(CanaryKillReason.SlippageBreach)]   // 슬리피지 초과
        public void Anomaly_Triggers_Immediate_Disarm_And_Sticky_Lockout(CanaryKillReason reason)
        {
            var canary = Armed();
            Assert.True(canary.TryAuthorize(1000) > 0);     // Disarm 전에는 인가

            canary.NoteAnomaly(reason);                     // ★이상 감지 → 즉시 Disarm

            Assert.True(canary.IsKilled);
            Assert.False(canary.IsArmed);                   // 무장 해제(토큰 0x00 소거)
            Assert.Equal(reason, canary.LastKillReason);    // 사유 기록
            for (int i = 0; i < 500; i++)
                Assert.Equal(0L, canary.TryAuthorize(1L));  // ★sticky — 재무장 전 전량 0
        }

        [Fact]
        public void ReArm_Requires_Physical_DualKey_After_Disarm()
        {
            var canary = Armed();
            canary.NoteAnomaly(CanaryKillReason.LatencyBreach);
            Assert.True(canary.IsKilled);
            // 물리 이중 키 없이 재무장 시도 → 실패(발주 계속 0).
            Assert.False(canary.Arm(1_000_000L, valveWired: false, liveArmingWired: true));
            Assert.Equal(0L, canary.TryAuthorize(1L));
        }

        // ── §2 Think-Beyond ① 다중 텔레메트리 비트마스크 Disarm ──────────────────
        private static (MultiTelemetryDisarmInterlock il, AnomalyFlightRecorder rec) Wire()
        {
            var rec = new AnomalyFlightRecorder();
            var il = new MultiTelemetryDisarmInterlock(rec);
            il.Configure(latencyMicros: 50_000, http429Count: 5, slippageBps: 30);   // 50ms · 5회 · 30bp
            return (il, rec);
        }

        [Fact]
        public void MultiTelemetry_AnySource_Breach_Disarms_And_Blocks_Emit()
        {
            var (il, _) = Wire();
            Assert.True(il.CanEmit);                         // 초기 사출 가능
            Assert.True(il.Note(TelemetrySource.Latency, 60_000));   // 60ms > 50ms → breach
            Assert.True(il.IsDisarmed);
            Assert.False(il.CanEmit);                        // ★Disarm 후 발주 차단
            Assert.True(il.BreachedSource(TelemetrySource.Latency));
            Assert.False(il.BreachedSource(TelemetrySource.Http429));
        }

        [Fact]
        public void MultiTelemetry_Below_Threshold_No_Disarm()
        {
            var (il, _) = Wire();
            Assert.False(il.Note(TelemetrySource.Latency, 40_000));   // 40ms ≤ 50ms
            Assert.False(il.Note(TelemetrySource.Http429, 3));        // 3 ≤ 5
            Assert.True(il.CanEmit);                         // 미초과 → 무장 유지
        }

        [Fact]
        public void MultiTelemetry_Disarm_Is_Sticky_Until_ReArm()
        {
            var (il, _) = Wire();
            il.Note(TelemetrySource.Slippage, 100);          // breach
            Assert.False(il.CanEmit);
            il.Note(TelemetrySource.Latency, 10_000);        // 이후 정상 표본이 와도
            Assert.False(il.CanEmit);                        // ★sticky — 여전히 차단
            il.ReArm();                                       // 물리 수동 재무장
            Assert.True(il.CanEmit);
        }

        // ── §3 Think-Beyond ② Flight Recorder + 메모리 퍼저 ─────────────────────
        [Fact]
        public void Disarm_Freezes_Snapshot_And_Purges_Pending_Orders()
        {
            var (il, rec) = Wire();
            rec.StagePending(0, 5_000_000L);                 // 인가 대기 주문(무장 중)
            rec.StagePending(1, 3_000_000L);
            Assert.False(rec.PendingBufferIsZero);

            il.Note(TelemetrySource.Http429, 9);             // 429 연쇄 → Disarm

            Assert.True(rec.HasCaptured);                    // ★스냅샷 고정(포렌식)
            Assert.True(rec.Frozen.Captured);
            Assert.Equal(TelemetrySource.Http429, rec.Frozen.Source);
            Assert.Equal(9L, rec.Frozen.Value);
            Assert.True(rec.PendingBufferIsZero);            // ★대기 주문 0x00 소거(잔여 사출 차단)
        }

        [Fact]
        public void FlightRecorder_First_Capture_Wins_Idempotent()
        {
            var (il, rec) = Wire();
            il.Note(TelemetrySource.Latency, 60_000);        // 최초 격발
            var first = rec.Frozen;
            il.Note(TelemetrySource.Slippage, 999);          // 이후 격발
            Assert.Equal(first.Source, rec.Frozen.Source);   // 스냅샷은 최초만(멱등)
            Assert.Equal(TelemetrySource.Latency, rec.Frozen.Source);
        }
    }

    // ── §4 적대검증(다중 신호 동시 경합 → Disarm 지연 0 · δ=0) ─────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Stage3SafetyInterlockConcurrencyTests
    {
        [Fact]
        public void Concurrent_MultiSource_Anomaly_Race_Still_Disarms_No_Delay()
        {
            // ★레이턴시/429/슬리피지 다중 신호가 동시에 폭주해도 단일 원자 OR 로 Disarm 즉시 격발(경합 지연 불가).
            var rec = new AnomalyFlightRecorder();
            var il = new MultiTelemetryDisarmInterlock(rec);
            il.Configure(50_000, 5, 30);

            const int threads = 9, perThread = 50_000;
            var ths = new Thread[threads];
            Exception? err = null;
            for (int t = 0; t < threads; t++)
            {
                var src = (TelemetrySource)(t % 3);
                ths[t] = new Thread(() =>
                {
                    try { for (int i = 0; i < perThread; i++) il.Note(src, 1_000_000); }   // 전부 문턱 초과
                    catch (Exception e) { err = e; }
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);

            Assert.True(il.IsDisarmed);                       // ★반드시 Disarm(누락 불가)
            Assert.False(il.CanEmit);                          // 발주 차단
            Assert.True(rec.HasCaptured && rec.PendingBufferIsZero);
            // 3개 소스 전부 breach 비트 셋(다중 신호 동시 반영).
            Assert.True(il.BreachedSource(TelemetrySource.Latency));
            Assert.True(il.BreachedSource(TelemetrySource.Http429));
            Assert.True(il.BreachedSource(TelemetrySource.Slippage));
        }

        [Fact]
        public void Note_And_CanEmit_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var rec = new AnomalyFlightRecorder();
            var il = new MultiTelemetryDisarmInterlock(rec);
            il.Configure(50_000, 5, 30);
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { il.Note(TelemetrySource.Latency, 10_000); _ = il.CanEmit; }   // 미초과 경로(워밍업)
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) { il.Note(TelemetrySource.Latency, 10_000); if (!il.CanEmit) throw new Exception("x"); }
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
