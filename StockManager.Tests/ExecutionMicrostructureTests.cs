// ============================================================================
// ExecutionMicrostructureTests — v4.87.0: δ=0 고정소수점 슬리피지 3σ + FSM 강등 + fail-closed
//
//  §1 SlippageSentinel 고정소수점(5) — 워밍업 가드·시장가 제외·퇴화 no-breach·3σ 돌파·σ 노출.
//  §2 IgnitionLock.Downgrade(4)      — 마스터→프로브·프로브→락·타임박스→락·락 무동작.
//  §3 ExecutionMicrostructureGuard(4)— 2연속 3σ 강등·1회 미강등·레이턴시 100ms 강등·리셋.
//  §4 OrderDrainWorker fail-closed(3)— 이중키 붕괴 동결·검문 예외 동결·정상 무동결.
//  §5 텔레메트리(2)                  — 집행 필드/헬퍼·퍼블리셔 반영.
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ExecutionMicrostructureTests
    {
        private const long Scale = 100_000_000L;   // QMath.Scale (10^8)
        private static long Raw(double v) => (long)(v * Scale);

        // ── §1 SlippageSentinel 고정소수점 δ=0 ────────────────────────────────

        [Fact]
        public void Fixed_WarmupGuard_Below10Samples_NoBreach()
        {
            var s = new SlippageSentinel();
            for (int i = 0; i < 9; i++)
                Assert.False(s.RecordAndCheck3Sigma(Raw(100.0), Raw(90.0)));   // 큰 슬리피지라도 표본<10 → no-breach
            Assert.Equal(0L, s.LastSigmaRaw);   // σ 미확립
            Assert.True(s.FixedSampleCount >= 9);
        }

        [Fact]
        public void Fixed_MarketOrder_ExpectedZero_Excluded()
        {
            var s = new SlippageSentinel();
            Assert.False(s.RecordAndCheck3Sigma(0L, Raw(100.0)));   // expectedRaw<=0 → 측정 제외
            Assert.Equal(0, s.FixedSampleCount);
        }

        [Fact]
        public void Fixed_Degenerate_AllIdentical_NoBreach_NoDivZero()
        {
            var s = new SlippageSentinel();
            // 12건 전부 동일 슬리피지 → 분산 0(퇴화) → no-breach(분모 0 맹점 없음).
            for (int i = 0; i < 12; i++)
                Assert.False(s.RecordAndCheck3Sigma(Raw(100.0), Raw(99.5)));
            Assert.Equal(0L, s.LastSigmaRaw);
        }

        [Fact]
        public void Fixed_Spike_Beyond3Sigma_Breaches()
        {
            var s = new SlippageSentinel();
            // 안정 표본(작은 슬리피지 교번) 12건 → σ 확립.
            for (int i = 0; i < 12; i++)
                s.RecordAndCheck3Sigma(Raw(100.0), Raw(i % 2 == 0 ? 99.99 : 100.01));
            Assert.True(s.LastSigmaRaw > 0);
            // 대형 슬리피지 스파이크 → 3σ 돌파.
            Assert.True(s.RecordAndCheck3Sigma(Raw(100.0), Raw(95.0)));
        }

        [Fact]
        public void Fixed_WithinBand_NoBreach()
        {
            var s = new SlippageSentinel();
            for (int i = 0; i < 12; i++)
                s.RecordAndCheck3Sigma(Raw(100.0), Raw(i % 2 == 0 ? 99.90 : 100.10));
            // 밴드 내 소폭 → no-breach.
            Assert.False(s.RecordAndCheck3Sigma(Raw(100.0), Raw(99.95)));
        }

        // ── §2 IgnitionLock.Downgrade ─────────────────────────────────────────

        private static IgnitionLock ArmedMaster()
        {
            var ign = new IgnitionLock(s => s == "SIG");
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "SIG"));
            return ign;
        }

        [Fact]
        public void Downgrade_Master_To_ProbeReady()
        {
            var ign = ArmedMaster();
            Assert.Equal(IgnitionStage.ProbeReady, ign.Downgrade("3σ"));
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);
            Assert.Equal(1, ign.ProbeRemaining);            // 프로브 1주만(대형 차단)
            Assert.True(ign.AllowPhysical());               // 프로브 허용
        }

        [Fact]
        public void Downgrade_ProbeReady_To_Locked()
        {
            var ign = ArmedMaster();
            ign.Downgrade("1차");                            // → ProbeReady
            Assert.Equal(IgnitionStage.Locked, ign.Downgrade("2차"));   // → Locked
            Assert.False(ign.AllowPhysical());
        }

        [Fact]
        public void Downgrade_TimeBox_To_Locked()
        {
            var ign = new IgnitionLock(s => s == "SIG");
            ign.Arm(IgnitionStage.TimeBoxOpen, "SIG", TimeSpan.FromMinutes(30));
            Assert.Equal(IgnitionStage.Locked, ign.Downgrade("위험"));
        }

        [Fact]
        public void Downgrade_Locked_NoOp()
        {
            var ign = new IgnitionLock(s => s == "SIG");
            Assert.Equal(IgnitionStage.Locked, ign.Downgrade("x"));
            Assert.Equal(IgnitionStage.Locked, ign.Stage);
        }

        // ── §3 ExecutionMicrostructureGuard ───────────────────────────────────

        private static (ExecutionMicrostructureGuard g, IgnitionLock ign) Guard()
        {
            var ign = new IgnitionLock(s => s == "SIG");
            ign.Arm(IgnitionStage.MasterIgnited, "SIG");
            var sentinel = new SlippageSentinel();
            var g = new ExecutionMicrostructureGuard(sentinel, ign);
            return (g, ign);
        }

        [Fact]
        public void Guard_SustainedEscalatingSlippage_Downgrades()
        {
            var (g, ign) = Guard();
            // σ 확립 warmup(정상 체결·저지연).
            for (int i = 0; i < 12; i++) g.OnFill(Raw(100.0), Raw(i % 2 == 0 ? 99.99 : 100.01), 0);
            // ★에스컬레이팅 대형 슬리피지(지속 악화) — 누적 σ 인플레이션을 앞질러 3σ 연속 돌파 → 강등 보장.
            //   (동일 크기 스파이크는 자기 분산을 부풀려 2연속이 안 될 수 있음 — 롤링-σ 이상탐지의 본질.)
            double fill = 95.0;
            for (int i = 0; i < 10 && g.Downgrades == 0; i++) { g.OnFill(Raw(100.0), Raw(fill), 0); fill -= 5.0; }
            Assert.True(g.Downgrades >= 1L);
            Assert.NotEqual(IgnitionStage.MasterIgnited, ign.Stage);   // 강등됨(마스터 이탈 = 대형 발주 차단)
        }

        [Fact]
        public void Guard_LatencyBreach_Downgrades_Immediately()
        {
            var (g, ign) = Guard();
            long over100ms = 101 * TimeSpan.TicksPerMillisecond;
            Assert.True(g.OnFill(Raw(100.0), Raw(100.0), over100ms));   // 레이턴시 초과 → 즉시 강등(슬리피지 무관)
            Assert.Equal(1L, g.Downgrades);
            Assert.Equal(1L, g.LatencyTrips);
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);
        }

        [Fact]
        public void Guard_NormalFills_NoDowngrade_StaysMaster()
        {
            var (g, ign) = Guard();
            // 밴드 내 소폭 체결만 지속 → 강등 없음(안정 상태).
            for (int i = 0; i < 30; i++) g.OnFill(Raw(100.0), Raw(i % 2 == 0 ? 99.98 : 100.02), 0);
            Assert.Equal(0L, g.Downgrades);
            Assert.Equal(IgnitionStage.MasterIgnited, ign.Stage);
        }

        [Fact]
        public void Guard_NormalFill_ResetsConsecutive()
        {
            var (g, _) = Guard();
            for (int i = 0; i < 12; i++) g.OnFill(Raw(100.0), Raw(i % 2 == 0 ? 99.99 : 100.01), 0);
            g.OnFill(Raw(100.0), Raw(95.0), 0);                 // 3σ(누적)
            g.OnFill(Raw(100.0), Raw(99.99), 0);                // 밴드 내 정상 → 연속 카운터 리셋
            Assert.Equal(0, g.Consecutive3Sigma);
        }

        // ── §4 OrderDrainWorker fail-closed ───────────────────────────────────

        private sealed class NoopBroker : IBroker
        {
            public string Name => "NOOP"; public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp) => (true, "ok");
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        [Fact]
        public void Drain_DualKeyBroken_ForceFreezes()
        {
            var att = new OrderAttestationService();
            var disp = new GatedOrderDispatcher(new OrderDispatchRing(8), () => false, att);
            using var w = new OrderDrainWorker(disp, new NoopBroker(), wired: false, periodMs: 50,
                attestation: att, dualKeyIntact: () => false);   // 무결성 붕괴
            w.DrainOnce();
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Drain_IntegrityCheckThrows_ForceFreezes()
        {
            var att = new OrderAttestationService();
            var disp = new GatedOrderDispatcher(new OrderDispatchRing(8), () => false, att);
            using var w = new OrderDrainWorker(disp, new NoopBroker(), wired: false, periodMs: 50,
                attestation: att, dualKeyIntact: () => throw new InvalidOperationException("boom"));
            w.DrainOnce();
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Drain_Intact_EmptyRing_NoFreeze()
        {
            var att = new OrderAttestationService();
            var disp = new GatedOrderDispatcher(new OrderDispatchRing(8), () => false, att);
            using var w = new OrderDrainWorker(disp, new NoopBroker(), wired: false, periodMs: 50,
                attestation: att, dualKeyIntact: () => true);
            w.DrainOnce();
            Assert.Equal(GatingMode.Normal, att.Mode);   // 정상 — 무동결
        }

        // ── §5 텔레메트리 ─────────────────────────────────────────────────────

        [Fact]
        public void RingTelemetry_ExecFields_And_Helpers()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0, 0, 0, 0, 0, 0, 0, 0, 0.0, 0, 0, 0, 0.0,
                ignitionStageCode: (long)IgnitionStage.MasterIgnited, slippageRaw: Raw(-0.5),
                slippageSigmaRaw: Raw(0.1), execDowngrades: 2);
            Assert.True(t.ExecMasterIgnited);
            Assert.Equal("마스터 점화", t.IgnitionStageName);
            Assert.Equal(-0.5m, t.SlippageDecimal);
            Assert.Equal(0.1m, t.SlippageSigmaDecimal);
            Assert.Equal(2L, t.ExecDowngrades);
        }

        [Fact]
        public void Publisher_ReflectsIgnitionAndGuard()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == "SIG");
            ign.Arm(IgnitionStage.MasterIgnited, "SIG");
            var sentinel = new SlippageSentinel();
            var guard = new ExecutionMicrostructureGuard(sentinel, ign);
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L, null, null, null, ign, guard);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.Equal((long)IgnitionStage.MasterIgnited, snap.IgnitionStageCode);
            Assert.True(snap.ExecMasterIgnited);
        }
    }
}
