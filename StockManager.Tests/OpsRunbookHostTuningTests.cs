// ============================================================================
// OpsRunbookHostTuningTests — v10.1.0 실전 운용 & 호스트 튜닝 & 카나리 적대적 검증(100% 단언)
//   🖊️ IgnitionSignoffValidator: 사전 점화 8항목 채점·전 PASS 서명·미달 하드 차단·신선도 리스·δ=0
//   🐤 MicroCapitalCanaryGovernor: 자본 ≤0.1% 캡·CAS admit·슬리피지 강등·δ=0
//   🌀 HostEnvironmentDriftSentinel(Think Beyond): sealed-baseline·히스테리시스·연속 확인 강등·δ=0·결정론
//   🟣 OperatorDuressPanicSwitch: O(1) ForceFreeze·멱등·δ=0 상태 조회
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // δ=0/타이밍 — 방어적 격리
    public sealed class OpsRunbookHostTuningTests
    {
        private const long Freq = 10_000_000L;
        private const long Scale = 100_000_000L;

        private sealed class RecordingBroker : IBroker
        {
            public int Placed;
            public string Name => "REC"; public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
            { Placed++; return (true, "ok"); }
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        // 서명 8항목 전 PASS 건강 프레임(핵심 필드만·나머지 0=건강). ④는 금고 '무장'(양성) 요구 → quantumVaultArmed:1.
        private static RingTelemetry HealthyFrame()
            => new RingTelemetry(1L, 0L, 0L, 0L, 0L, 0.0,
                quantumVaultArmed: 1L, cpuPCoreCount: 8L, cpuPinnedThreads: 4L, preFlightScore: 100L, infraCouplingCount: 117L, hostTimerResNanos: 500_000L);
        private static RingTelemetry BadTimerFrame()
            => new RingTelemetry(1L, 0L, 0L, 0L, 0L, 0.0,
                quantumVaultArmed: 1L, cpuPCoreCount: 8L, cpuPinnedThreads: 4L, preFlightScore: 100L, infraCouplingCount: 117L, hostTimerResNanos: 20_000_000L);   // 20ms ≫ 1ms 예산

        // ══ 🖊️ IgnitionSignoffValidator ═════════════════════════════════════
        [Fact]
        public void Signoff_Healthy_Passes_Unhealthy_Blocks_And_FreshnessLease()
        {
            long clock = 1_000_000_000L;
            var sv = new IgnitionSignoffValidator(nowTicks: () => clock, tickFrequency: Freq, leaseMillis: 100,
                audit: new IgnitionAuditTrailRecorder(nowTicks: () => 1L, tickFrequency: Freq));
            var healthy = HealthyFrame();
            Assert.Equal(100L, sv.Certify(in healthy));
            Assert.True(sv.SignoffPassed);
            Assert.False(sv.SignoffBlocking);
            Assert.Equal(0L, sv.FailMask);
            Assert.NotEqual(0L, sv.Seal);
            Assert.True(sv.TotalItems >= 8L);

            var bad = BadTimerFrame();
            sv.Certify(in bad);
            Assert.True(sv.Score < 100L);            // 타이머 미튜닝 → 미달
            Assert.True(sv.SignoffBlocking);         // ★서명 없으면 무장 하드 차단
            Assert.NotEqual(0L, sv.FailMask);

            // ★신선도 리스: 재서명 후 시계만 전진 → stale → 차단(샘플러 정지 sticky-open 방지).
            Assert.Equal(100L, sv.Certify(in healthy));
            Assert.False(sv.SignoffBlocking);
            clock += 2_000_000L;   // 0.2s > 0.1s 리스
            Assert.True(sv.SignoffBlocking);
        }

        [Fact]
        public void Signoff_ZeroAllocation()
        {
            var sv = new IgnitionSignoffValidator();
            var f = HealthyFrame();
            for (int i = 0; i < 10_000; i++) sv.Certify(in f);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) sv.Certify(in f); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★서명 채점 핫패스 힙 0
        }

        // ══ 🐤 MicroCapitalCanaryGovernor ═══════════════════════════════════
        [Fact]
        public void Canary_Cap_Admits_Rejects_Downgrades_Reset()
        {
            bool downgraded = false;
            var g = new MicroCapitalCanaryGovernor(capitalKrwRaw: 1_000_000L, capBps: 1000L, slippageThresholdBps: 50L,
                onDowngrade: () => downgraded = true);
            Assert.Equal(100_000L, g.CapNotionalRaw);   // 1e6 × 1000/10000 = 1e5
            Assert.True(g.TryAdmitOrder(60_000L));
            Assert.True(g.TryAdmitOrder(40_000L));      // cum = cap
            Assert.False(g.TryAdmitOrder(1L));          // ★캡 초과 → 거부
            Assert.Equal(1000L, g.CapUtilizationPermille);
            Assert.Equal(2L, g.Admitted);
            Assert.True(g.Rejected >= 1L);

            g.RecordSlippage(80L);                      // 80 > 50 임계 → 강등
            Assert.True(g.Downgraded);
            Assert.True(downgraded);                    // ★onDowngrade 콜백(→ 드라이런)
            Assert.True(g.CanaryBlocking);
            Assert.False(g.TryAdmitOrder(1L));          // 강등 후 전부 거부

            g.Reset();                                  // 재개
            Assert.False(g.Downgraded);
            Assert.Equal(0L, g.CumNotionalRaw);
            Assert.True(g.TryAdmitOrder(10_000L));
        }

        [Fact]
        public void Canary_ZeroAllocation()
        {
            var g = new MicroCapitalCanaryGovernor(capitalKrwRaw: long.MaxValue / 2L, capBps: 10_000L, slippageThresholdBps: 50L);
            for (int i = 0; i < 50_000; i++) { g.TryAdmitOrder(1L); g.RecordSlippage(10L); }
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 500_000; i++) { g.TryAdmitOrder(1L); g.RecordSlippage(10L); } return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★TryAdmit/RecordSlippage 핫패스 힙 0
        }

        [Fact]
        public void Canary_Dispatch_MarketOrder_And_OverCap_FailClosed_NoBrokerLeak()
        {
            // ★실전(LiveBroker) 스코프 드레인 경로: 카나리 캡이 시장가(PriceRaw=0)·오버캡·Int128 절단을 fail-closed 거부(캡 우회 봉쇄).
            var att    = new OrderAttestationService();
            var ring   = new OrderDispatchRing(64);
            var disp   = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att);
            var broker = new RecordingBroker();
            var barrier = new ScopeSealedTransitionBarrier(nowTicks: () => 1_000_000_000L, tickFrequency: Freq);
            barrier.SetInitialMode(ScopeSealedTransitionBarrier.MODE_LIVE);   // 실전 스코프 → LiveBroker 라우팅
            var canary = new MicroCapitalCanaryGovernor(capitalKrwRaw: 100_000L * Scale, capBps: 10L);   // 캡 = 1e13×10/10000 = 1e10 raw = 100 KRW
            using var worker = new OrderDrainWorker(disp, broker, wired: false, scopeBarrier: barrier, canaryGov: canary);

            // ① 시장가(PriceRaw=0) → 명목 미상 → fail-closed 거부(캡 무한 우회 봉쇄).
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)0, QPrice.Of(0m), QQty.Of(1000m), 100);
            worker.DrainOnce();
            Assert.Equal(0, broker.Placed);
            Assert.True(canary.Rejected >= 1L);

            // ② 캡 초과 지정가(명목 1,000,000 KRW ≫ 100 캡) → 거부.
            ring.Publish(0, 5930, OrderSide.Buy, (byte)1, QPrice.Of(1000m), QQty.Of(1000m), 101);
            worker.DrainOnce();
            Assert.Equal(0, broker.Placed);

            // ③ 캡 이내 소액 지정가(명목 50 KRW ≤ 100 캡) → admit → 실 사출.
            ring.Publish(0, 5930, OrderSide.Buy, (byte)1, QPrice.Of(10m), QQty.Of(5m), 102);
            worker.DrainOnce();
            Assert.Equal(1, broker.Placed);
            Assert.True(canary.Admitted >= 1L);
        }

        // ══ 🌀 HostEnvironmentDriftSentinel (Think Beyond) ══════════════════
        [Fact]
        public void Drift_SealBaseline_Hysteresis_Trips_On_Sustained_Not_Blip()
        {
            var d = new HostEnvironmentDriftSentinel(timerSlackMult: 4L, timerAbsSlackNanos: 200_000L, jitterSlackPermille: 200L, confirmCount: 3L);
            Assert.False(d.Sealed);
            d.SealBaseline(timerResNanos: 500_000L, jitterPermille: 10L);   // 0.5ms 서명 봉인
            Assert.True(d.Sealed);
            Assert.Equal(500_000L, d.BaselineTimerResNanos);

            for (int i = 0; i < 10; i++) d.Probe(520_000L, 12L);   // 미세 변동 → 무드리프트
            Assert.False(d.DriftTripped);

            // ★단발 blip(1회) → 트립 안 함(히스테리시스).
            d.Probe(20_000_000L, 12L);   // 20ms revert 1회
            Assert.False(d.DriftTripped);
            d.Probe(520_000L, 12L);       // 회복 → 연속 카운터 리셋
            Assert.False(d.DriftTripped);

            // ★지속 드리프트(연속 3회 확인) → 원자 강등.
            d.Probe(20_000_000L, 12L);
            d.Probe(20_000_000L, 12L);
            d.Probe(20_000_000L, 12L);
            Assert.True(d.DriftTripped);
            Assert.True(d.DriftBlocking);
            Assert.True(d.Divergences >= 3L);
        }

        [Fact]
        public void Drift_Deterministic()
        {
            var a = new HostEnvironmentDriftSentinel();
            var b = new HostEnvironmentDriftSentinel();
            a.SealBaseline(500_000L, 10L);
            b.SealBaseline(500_000L, 10L);
            for (int i = 0; i < 5_000; i++)
            {
                long t = 500_000L + (i % 3 == 0 ? 3_000_000L : 1_000L);
                a.Probe(t, 12L);
                b.Probe(t, 12L);
            }
            Assert.Equal(a.DriftTripped, b.DriftTripped);
            Assert.Equal(a.Divergences, b.Divergences);
        }

        [Fact]
        public void Drift_Probe_ZeroAllocation()
        {
            var d = new HostEnvironmentDriftSentinel();
            d.SealBaseline(500_000L, 10L);
            for (int i = 0; i < 50_000; i++) d.Probe(520_000L, 12L);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 500_000; i++) d.Probe(520_000L + (i & 1023), 12L); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★Probe 판정 핫패스 힙 0
        }

        // ══ 🟣 OperatorDuressPanicSwitch ════════════════════════════════════
        [Fact]
        public void Panic_Trip_Freezes_Idempotent()
        {
            var att = new OrderAttestationService();
            bool panicHook = false;
            var p = new OperatorDuressPanicSwitch(attestation: att, onPanic: () => panicHook = true);
            Assert.False(p.Tripped);
            p.Trip(reasonCode: 42L);
            Assert.True(p.Tripped);
            Assert.True(p.PanicBlocking);
            Assert.True(panicHook);                                   // Cancel-All/zeroize 훅 격발
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);      // ★ForceFreeze(전 발주 비가역 차단)
            Assert.Equal(1L, p.Trips);
            p.Trip(reasonCode: 99L);                                 // ★멱등(재격발 무시)
            Assert.Equal(1L, p.Trips);
        }

        [Fact]
        public void Panic_StatusReads_ZeroAllocation()
        {
            var p = new OperatorDuressPanicSwitch();
            long acc = 0;
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 500_000; i++) { if (p.Tripped) acc++; acc += p.Trips; if (p.PanicBlocking) acc++; } return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★상태 조회 핫패스 힙 0
            Assert.True(acc >= 0);
        }
    }
}
