// ============================================================================
// IgnitionLockTests — v5.0.0-a: 점화 제어반 FSM 적대적 검증 (20개)
//
//  §1 기본·Arm (5)     — 기본 Locked·서명 점화·미인가·비-Locked 거부·동결 중 거부
//  §2 프로브 단발 (4)  — 1주 소비 후 자동잠금·재소비 거부·동시소비 1승·소비 전 허용
//  §3 시간상자 (4)     — 창 내 허용·만료 자동잠금·만료 후 불허·창 내 다중 소비
//  §4 자동 후퇴 (4)    — 임의 단계 Lock·3σ 자동잠금·외부 동결 차단·Lock 시 프로브/창 청산
//  §5 fail-closed·감사 (3) — 마스터 소비 후 동결 차단·감사 기록·Detach 후 무잠금
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class IgnitionLockTests
    {
        private sealed class FakeClock { public DateTime Now = new DateTime(2026, 6, 21, 11, 0, 0, DateTimeKind.Utc); }

        private static FillPayload Fill(double price) => new FillPayload
        { OrderId = 1, SymbolCode = 5930, FillPrice = price, FillQty = 10, Side = 1, Status = 0, TimestampTicks = 1 };

        private static void TriggerAlert(SlippageSentinel s)
        {
            for (int i = 0; i < 5; i++) s.RecordFill(Fill(100000 + i), 100000);
            s.RecordFill(Fill(500000), 100000);   // 3σ 초과 이상치
        }

        // ════════════════════════ §1 기본·Arm ═════════════════════════════════════

        [Fact]
        public void Default_IsLocked_NoPhysical()
        {
            var ign = new IgnitionLock();
            Assert.Equal(IgnitionStage.Locked, ign.Stage);
            Assert.False(ign.AllowPhysical());
            Assert.False(ign.TryConsumePhysical());
        }

        [Fact]
        public void Arm_ProbeReady_ValidSignature()
        {
            var ign = new IgnitionLock(s => s == "CMDR");
            Assert.True(ign.Arm(IgnitionStage.ProbeReady, "CMDR"));
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);
            Assert.Equal(1, ign.ProbeRemaining);
            Assert.True(ign.AllowPhysical());
        }

        [Fact]
        public void Arm_InvalidSignature_Denied()
        {
            var ign = new IgnitionLock(s => s == "CMDR");
            Assert.False(ign.Arm(IgnitionStage.ProbeReady, "FORGED"));
            Assert.Equal(IgnitionStage.Locked, ign.Stage);
        }

        [Fact]
        public void Arm_NotFromLocked_Denied()
        {
            var ign = new IgnitionLock(s => s == "CMDR");
            ign.Arm(IgnitionStage.ProbeReady, "CMDR");
            // 이미 ProbeReady — 직접 TimeBox 로 못 감(반드시 Lock 경유)
            Assert.False(ign.Arm(IgnitionStage.TimeBoxOpen, "CMDR"));
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);
        }

        [Fact]
        public void Arm_DuringFreeze_Denied()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("동결");
            var ign = new IgnitionLock(s => s == "CMDR", att);
            Assert.False(ign.Arm(IgnitionStage.ProbeReady, "CMDR"));   // fail-closed
            Assert.Equal(IgnitionStage.Locked, ign.Stage);
        }

        // ════════════════════════ §2 프로브 단발 ══════════════════════════════════

        [Fact]
        public void Probe_ConsumeOnce_ThenAutoLock()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.ProbeReady, "OK");
            Assert.True(ign.TryConsumePhysical());
            Assert.Equal(IgnitionStage.Locked, ign.Stage);   // 1체결 즉시 자동 잠금
            Assert.Equal(0, ign.ProbeRemaining);
        }

        [Fact]
        public void Probe_SecondConsume_Denied()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.ProbeReady, "OK");
            Assert.True(ign.TryConsumePhysical());
            Assert.False(ign.TryConsumePhysical());   // 이미 소비·잠금
        }

        [Fact]
        public void Probe_ConcurrentConsume_ExactlyOneWins()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.ProbeReady, "OK");

            int wins = 0;
            var threads = new List<Thread>();
            for (int t = 0; t < 8; t++)
                threads.Add(new Thread(() => { if (ign.TryConsumePhysical()) Interlocked.Increment(ref wins); }));
            foreach (var th in threads) th.Start();
            foreach (var th in threads) th.Join(2000);

            Assert.Equal(1, wins);   // 단일주 프로브 원자성 — 정확히 1건만 통과
        }

        [Fact]
        public void Probe_AllowPhysical_TrueUntilConsumed()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.ProbeReady, "OK");
            Assert.True(ign.AllowPhysical());
            ign.TryConsumePhysical();
            Assert.False(ign.AllowPhysical());
        }

        // ════════════════════════ §3 시간상자 ═════════════════════════════════════

        [Fact]
        public void TimeBox_WithinWindow_Allows()
        {
            var clk = new FakeClock();
            var ign = new IgnitionLock(s => s == "OK", clock: () => clk.Now);
            Assert.True(ign.Arm(IgnitionStage.TimeBoxOpen, "OK", TimeSpan.FromMinutes(30)));
            Assert.True(ign.AllowPhysical());
            Assert.True(ign.TryConsumePhysical());
        }

        [Fact]
        public void TimeBox_Expired_AutoLocksOnConsume()
        {
            var clk = new FakeClock();
            var ign = new IgnitionLock(s => s == "OK", clock: () => clk.Now);
            ign.Arm(IgnitionStage.TimeBoxOpen, "OK", TimeSpan.FromMinutes(30));

            clk.Now = clk.Now.AddMinutes(31);   // 창 만료
            Assert.False(ign.TryConsumePhysical());
            Assert.Equal(IgnitionStage.Locked, ign.Stage);   // 자동 잠금
        }

        [Fact]
        public void TimeBox_AllowPhysical_FalseAfterExpiry()
        {
            var clk = new FakeClock();
            var ign = new IgnitionLock(s => s == "OK", clock: () => clk.Now);
            ign.Arm(IgnitionStage.TimeBoxOpen, "OK", TimeSpan.FromMinutes(30));
            clk.Now = clk.Now.AddMinutes(45);
            Assert.False(ign.AllowPhysical());
        }

        [Fact]
        public void TimeBox_MultipleConsume_WithinWindow()
        {
            var clk = new FakeClock();
            var ign = new IgnitionLock(s => s == "OK", clock: () => clk.Now);
            ign.Arm(IgnitionStage.TimeBoxOpen, "OK", TimeSpan.FromMinutes(30));
            Assert.True(ign.TryConsumePhysical());
            Assert.True(ign.TryConsumePhysical());   // 창 내 다회 허용
            Assert.Equal(IgnitionStage.TimeBoxOpen, ign.Stage);
        }

        // ════════════════════════ §4 자동 후퇴 ════════════════════════════════════

        [Fact]
        public void Lock_FromAnyStage_Downgrades()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.MasterIgnited, "OK");
            Assert.Equal(IgnitionStage.MasterIgnited, ign.Stage);
            ign.Lock("인간 취소");
            Assert.Equal(IgnitionStage.Locked, ign.Stage);
        }

        [Fact]
        public void SlippageAlert_AutoLocks()
        {
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 2.0, ConsecutiveAlertCount = 1 };
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.MasterIgnited, "OK");
            ign.Attach(sentinel);

            TriggerAlert(sentinel);   // 3σ 경보 → OnRiskAlert → Lock

            Assert.Equal(IgnitionStage.Locked, ign.Stage);
        }

        [Fact]
        public void ExternalFreeze_BlocksConsume()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == "OK", att);
            ign.Arm(IgnitionStage.MasterIgnited, "OK");
            Assert.True(ign.AllowPhysical());

            att.ForceFreeze("외부 디싱크 동결");
            Assert.False(ign.AllowPhysical());        // 동결 → 차단
            Assert.False(ign.TryConsumePhysical());
        }

        [Fact]
        public void Lock_ClearsProbeAndWindow()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.ProbeReady, "OK");
            Assert.Equal(1, ign.ProbeRemaining);
            ign.Lock("후퇴");
            Assert.Equal(0, ign.ProbeRemaining);
        }

        // ════════════════════════ §5 fail-closed·감사 ═════════════════════════════

        [Fact]
        public void MasterIgnited_Consume_TrueThenFreezeBlocks()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == "OK", att);
            ign.Arm(IgnitionStage.MasterIgnited, "OK");
            Assert.True(ign.TryConsumePhysical());    // 마스터 통과
            att.ForceFreeze("동결");
            Assert.False(ign.TryConsumePhysical());   // 동결 후 차단
        }

        [Fact]
        public void AuditLog_RecordsTransitions()
        {
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.MasterIgnited, "OK");
            ign.Lock("후퇴");

            var actions = new HashSet<string>();
            foreach (var e in ign.AuditLog) actions.Add(e.Action);
            Assert.Contains("Arm", actions);
            Assert.Contains("Lock", actions);
        }

        [Fact]
        public void Detach_StopsAutoLock()
        {
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 2.0, ConsecutiveAlertCount = 1 };
            var ign = new IgnitionLock(s => s == "OK");
            ign.Arm(IgnitionStage.MasterIgnited, "OK");
            ign.Attach(sentinel);
            ign.Detach(sentinel);

            TriggerAlert(sentinel);   // 구독 해제 후 → 자동 잠금 안 됨

            Assert.Equal(IgnitionStage.MasterIgnited, ign.Stage);
        }

        // ════════════════════════ §6 UI 매핑 (관제반 표시) ════════════════════════

        [Fact]
        public void StageLabel_MapsAllStages()
        {
            Assert.Equal("잠금(Locked)", IgnitionLock.StageLabel(IgnitionStage.Locked));
            Assert.Equal("1주 프로브",   IgnitionLock.StageLabel(IgnitionStage.ProbeReady));
            Assert.Equal("시간상자 개방", IgnitionLock.StageLabel(IgnitionStage.TimeBoxOpen));
            Assert.Equal("마스터 점화",   IgnitionLock.StageLabel(IgnitionStage.MasterIgnited));
        }

        [Fact]
        public void StageCss_MapsAllStages()
        {
            Assert.Equal("ign-locked",  IgnitionLock.StageCss(IgnitionStage.Locked));
            Assert.Equal("ign-probe",   IgnitionLock.StageCss(IgnitionStage.ProbeReady));
            Assert.Equal("ign-timebox", IgnitionLock.StageCss(IgnitionStage.TimeBoxOpen));
            Assert.Equal("ign-master",  IgnitionLock.StageCss(IgnitionStage.MasterIgnited));
        }
    }
}
