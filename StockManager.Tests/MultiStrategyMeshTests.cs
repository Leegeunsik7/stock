// ============================================================================
// MultiStrategyMeshTests — v5.4.0 다중 전략 인터리빙 공존 & 실시간 바스켓 리밸런서
//
//  §1 RealTimeBasketNetter — 대향 상쇄·부분 상쇄·절감 계량.
//  §2 WashTradeGuard — 대향 윈도우 거부·동일측 통과·윈도우 밖 통과·심볼 독립.
//  §3 MultiStrategyInterleavedMesh — 팬아웃·가중치 자본 분할·1raw 초과 차단·봉인 레인 스킵·잔여 사출.
//  §4 CrossStrategyContagionFirewall — 공진 봉인·손실속도 퓨즈·캐스케이드·자본 격리(재분배 금지).
//  §5 ★적대검증 — 팬아웃/네팅 핫패스 δ=0·미무장 잔여 실주문 0.
// ============================================================================
using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MultiStrategyMeshTests
    {
        private const long Scale = 100_000_000L;
        private static long M(decimal krw) => QMoney.Of(krw).Raw;

        private static StrategyIntent I(int lane, long signedQty, long price) => new StrategyIntent(lane, lane, 5930, signedQty, price);

        // ════════════ §1 RealTimeBasketNetter ════════════

        [Fact]
        public void Net_FullyOpposing_ResidualZero_CrossedFull()
        {
            var netter = new RealTimeBasketNetter();
            Span<StrategyIntent> its = stackalloc StrategyIntent[2];
            its[0] = I(0, +100 * Scale, 100 * Scale);
            its[1] = I(1, -100 * Scale, 100 * Scale);
            var r = netter.Net(its, 100 * Scale);
            Assert.Equal(0L, r.ResidualSignedQtyRaw);        // 완전 상쇄
            Assert.Equal(100 * Scale, r.CrossedQtyRaw);
            Assert.True(r.SavedCostRaw > 0);                 // 왕복 비용 절감 발생
        }

        [Fact]
        public void Net_PartialOpposing_ResidualNetOfLargerSide()
        {
            var netter = new RealTimeBasketNetter();
            Span<StrategyIntent> its = stackalloc StrategyIntent[3];
            its[0] = I(0, +100 * Scale, 100 * Scale);
            its[1] = I(1, -40 * Scale, 100 * Scale);
            its[2] = I(2, -10 * Scale, 100 * Scale);
            var r = netter.Net(its, 100 * Scale);
            Assert.Equal(50 * Scale, r.ResidualSignedQtyRaw);   // +100 -50 = +50 순매수
            Assert.Equal(50 * Scale, r.CrossedQtyRaw);          // min(100,50)=50 상쇄
            Assert.Equal(OrderSide.Buy, r.ResidualSide);
        }

        [Fact]
        public void Net_AllSameSide_NoCrossing()
        {
            var netter = new RealTimeBasketNetter();
            Span<StrategyIntent> its = stackalloc StrategyIntent[2];
            its[0] = I(0, +30 * Scale, 100 * Scale);
            its[1] = I(1, +70 * Scale, 100 * Scale);
            var r = netter.Net(its, 100 * Scale);
            Assert.Equal(100 * Scale, r.ResidualSignedQtyRaw);   // 상쇄 없음 — 전량 잔여
            Assert.Equal(0L, r.CrossedQtyRaw);
            Assert.Equal(0L, r.SavedCostRaw);
        }

        [Fact]
        public void Net_EfficiencyBps_AccumulatesAcrossTicks()
        {
            var netter = new RealTimeBasketNetter();
            Span<StrategyIntent> its = stackalloc StrategyIntent[2];
            its[0] = I(0, +100 * Scale, 100 * Scale);
            its[1] = I(1, -100 * Scale, 100 * Scale);
            netter.Net(its, 100 * Scale);
            Assert.Equal(10000L, netter.NettingEfficiencyBps);   // 전량 상쇄 → 100%
            Assert.True(netter.SavedCostRawCum > 0);
        }

        // ════════════ §2 WashTradeGuard ════════════

        [Fact]
        public void WashGuard_OpposingWithinWindow_Rejected()
        {
            var wg = new WashTradeGuard(windowTicks: 1000);
            Assert.True(wg.Admit(5930, OrderSide.Buy, 0));       // 최초 통과
            Assert.False(wg.Admit(5930, OrderSide.Sell, 500));   // 대향·윈도우 내(500≤1000) → 거부
            Assert.Equal(1L, wg.Rejections);
        }

        [Fact]
        public void WashGuard_SameSide_Admitted()
        {
            var wg = new WashTradeGuard(windowTicks: 1000);
            Assert.True(wg.Admit(5930, OrderSide.Buy, 0));
            Assert.True(wg.Admit(5930, OrderSide.Buy, 200));     // 동일측 — 자전거래 아님
            Assert.Equal(0L, wg.Rejections);
        }

        [Fact]
        public void WashGuard_OpposingOutsideWindow_Admitted()
        {
            var wg = new WashTradeGuard(windowTicks: 1000);
            Assert.True(wg.Admit(5930, OrderSide.Buy, 0));
            Assert.True(wg.Admit(5930, OrderSide.Sell, 2000));   // 대향이나 윈도우 밖(2000>1000) → 통과
            Assert.Equal(0L, wg.Rejections);
        }

        [Fact]
        public void WashGuard_DifferentSymbols_Independent()
        {
            var wg = new WashTradeGuard(windowTicks: 1000);
            Assert.True(wg.Admit(5930, OrderSide.Buy, 0));
            Assert.True(wg.Admit(660, OrderSide.Sell, 100));     // 다른 심볼 — 독립
            Assert.Equal(0L, wg.Rejections);
        }

        // ════════════ §3 MultiStrategyInterleavedMesh ════════════

        [Fact]
        public void Repartition_SplitsCapitalByWeight_LosslessSum()
        {
            var mesh = new MultiStrategyInterleavedMesh(capacity: 4);
            mesh.AddLane(1, weightBps: 6000, strategy: null);
            mesh.AddLane(2, weightBps: 3000, strategy: null);
            mesh.AddLane(3, weightBps: 1000, strategy: null);
            long global = M(10_000_000m);
            mesh.Repartition(global);
            long sum = mesh.LaneSubBudgetRaw(0) + mesh.LaneSubBudgetRaw(1) + mesh.LaneSubBudgetRaw(2);
            Assert.Equal(global, sum);   // ★Σ 서브버짓 == 전역(누수 0)
            Assert.True(mesh.LaneSubBudgetRaw(0) > mesh.LaneSubBudgetRaw(1));   // 6000 > 3000 비율
            Assert.True(mesh.LaneSubBudgetRaw(1) > mesh.LaneSubBudgetRaw(2));
        }

        [Fact]
        public void CapitalGuard_LaneCannotExceedItsAllocation()
        {
            // 레인 0 자본을 극소로 → 큰 매수 신호가 서브버짓 초과 → fail-closed 거부(1raw 초과 차단).
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 2, ring: ring, netter: new RealTimeBasketNetter());
            mesh.AddLane(1, weightBps: 1, strategy: new FakeStrategy("A", 0, signal: 1000 * Scale));   // 거대 매수 신호
            mesh.AddLane(2, weightBps: 9999, strategy: null);
            mesh.Repartition(M(1_000m));   // 전역 1천원 — 레인0 몫 극소(1/10000)
            long residual = mesh.ProcessTick(5930, 100 * Scale, 0, 0);
            Assert.Equal(0L, residual);                 // 자본 초과 → 거부 → 잔여 0
            Assert.True(mesh.CapitalRejects >= 1);      // ★1raw 초과 차단 계수
        }

        [Fact]
        public void ProcessTick_OpposingLanes_NetsToResidual_PublishesOnlyResidual()
        {
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 2, ring: ring, netter: new RealTimeBasketNetter());
            mesh.AddLane(1, weightBps: 5000, strategy: new FakeStrategy("A", 0, signal: +100 * Scale));
            mesh.AddLane(2, weightBps: 5000, strategy: new FakeStrategy("B", 0, signal: -60 * Scale));
            mesh.Repartition(M(100_000_000m));
            long residual = mesh.ProcessTick(5930, 100 * Scale, 0, 0);
            Assert.Equal(40 * Scale, residual);         // +100 -60 = +40 순매수 잔여
            Assert.Equal(1L, mesh.ResidualsPublished);
            Assert.Equal(0L, ring.Published);           // 단일 제안(id 0) 발행됨(Published=최고 시퀀스)
        }

        [Fact]
        public void ProcessTick_FullyNetted_NothingPublished()
        {
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 2, ring: ring, netter: new RealTimeBasketNetter());
            mesh.AddLane(1, weightBps: 5000, strategy: new FakeStrategy("A", 0, signal: +100 * Scale));
            mesh.AddLane(2, weightBps: 5000, strategy: new FakeStrategy("B", 0, signal: -100 * Scale));
            mesh.Repartition(M(100_000_000m));
            long residual = mesh.ProcessTick(5930, 100 * Scale, 0, 0);
            Assert.Equal(0L, residual);                 // 완전 상쇄 → 물리 사출 0
            Assert.Equal(0L, mesh.ResidualsPublished);
        }

        [Fact]
        public void SealedLane_SkippedInFanout()
        {
            var mesh = new MultiStrategyInterleavedMesh(capacity: 2, netter: new RealTimeBasketNetter());
            mesh.AddLane(1, weightBps: 5000, strategy: new FakeStrategy("A", 0, signal: +100 * Scale));
            mesh.AddLane(2, weightBps: 5000, strategy: new FakeStrategy("B", 0, signal: +50 * Scale));
            mesh.Repartition(M(100_000_000m));
            mesh.SetLaneState(1, LaneState.Muted);      // 레인1 진압
            long residual = mesh.ProcessTick(5930, 100 * Scale, 0, 0);
            Assert.Equal(100 * Scale, residual);        // 레인0(+100)만 — 레인1 억제
        }

        // ════════════ §4 CrossStrategyContagionFirewall ════════════

        [Fact]
        public void Firewall_VelocityFuse_SealsSelfDestructingLane()
        {
            var fw = new CrossStrategyContagionFirewall(capacity: 4, velocityFuse: 3);
            for (int e = 0; e < 3; e++) { fw.MarkResult(2, pnlDeltaRaw: -100, exposureNotionalRaw: 1000); fw.AdvanceEpoch(); }
            var v = fw.Evaluate();
            Assert.True((v.SealLaneMask & (1UL << 2)) != 0);   // 레인2 연속 낙폭 3 → 봉인
            Assert.Equal(1, v.Reason);                          // 손실속도 퓨즈
        }

        [Fact]
        public void Firewall_CoDrawdownResonance_SealsDominantLane()
        {
            var fw = new CrossStrategyContagionFirewall(capacity: 4, resonanceThreshold: 4, velocityFuse: 100);
            // 레인0·1 이 4에폭 동기 낙폭 → 공진. 레인0 익스포저 우세 → 레인0 봉인(주도 진압).
            for (int e = 0; e < 4; e++)
            {
                fw.MarkResult(0, -100, exposureNotionalRaw: 9000);
                fw.MarkResult(1, -100, exposureNotionalRaw: 1000);
                fw.AdvanceEpoch();
            }
            var v = fw.Evaluate();
            Assert.True(v.ResonanceMetric >= 4);
            Assert.True((v.SealLaneMask & (1UL << 0)) != 0);    // 주도(대형 익스포저) 봉인
            Assert.Equal(2, v.Reason);                           // 공진
        }

        [Fact]
        public void Firewall_Cascade_TriggersIgnitionBackstop()
        {
            var ign = new IgnitionLock();
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "sig"));
            var fw = new CrossStrategyContagionFirewall(capacity: 4, velocityFuse: 2, cascadeSeals: 3, ignition: ign);
            // 3개 레인 각각 연속 2낙폭 → 한 번의 Evaluate 로 3봉인 → 캐스케이드.
            for (int e = 0; e < 2; e++)
            {
                fw.MarkResult(0, -1, 100); fw.MarkResult(1, -1, 100); fw.MarkResult(2, -1, 100);
                fw.AdvanceEpoch();
            }
            var v = fw.Evaluate();
            Assert.True(v.SystemCascade);                              // 3구획 봉인 → 캐스케이드
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);        // ★전역 IgnitionLock 백스톱 강등
            Assert.True(fw.Cascades >= 1);
        }

        [Fact]
        public void Firewall_SealedLane_CapitalQuarantined_NotRedistributed()
        {
            // 방화벽 봉인 → mesh 가 해당 레인 자본 격리 예치 + 생존 구획 재분할(봉인분 재분배 금지).
            var ign = new IgnitionLock();
            var fw = new CrossStrategyContagionFirewall(capacity: 3, velocityFuse: 2, cascadeSeals: 99, ignition: ign);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 3, netter: new RealTimeBasketNetter(), firewall: fw);
            mesh.AddLane(1, weightBps: 3333, strategy: null);
            mesh.AddLane(2, weightBps: 3333, strategy: null);
            mesh.AddLane(3, weightBps: 3334, strategy: null);
            long global = M(9_000_000m);
            mesh.Repartition(global);
            long lane0Before = mesh.LaneSubBudgetRaw(0);          // 봉인 대상 몫(≈3M)
            long survivorsBefore = mesh.LaneSubBudgetRaw(1) + mesh.LaneSubBudgetRaw(2);

            // 레인0 자폭(연속 2낙폭) → 봉인.
            for (int e = 0; e < 2; e++) { mesh.ObserveFill(0, -100); mesh.AdvanceRiskEpochAndEvaluate(); }

            Assert.Equal(1, mesh.SealedLaneCount);
            Assert.Equal(0L, mesh.LaneSubBudgetRaw(0));          // ★봉인 구획 자본 0(격리 예치)
            long survivorsAfter = mesh.LaneSubBudgetRaw(1) + mesh.LaneSubBudgetRaw(2);
            // ★구획 격벽 실증: 생존 구획은 봉인분(레인0 ≈3M)을 흡수하지 않는다 — 재분할 후에도 ≈ 이전(누수 rounding 이내).
            Assert.True(Math.Abs(survivorsAfter - survivorsBefore) < 1000L, $"생존 합 {survivorsAfter} 가 봉인분을 흡수(이전 {survivorsBefore})");
            // 봉인분 ≈3M 이 배분 풀에서 제외되어 미할당(격리)로 남아야.
            Assert.True(global - survivorsAfter >= lane0Before - 1000L, "격리 자본이 생존 구획으로 재분배됨(격벽 붕괴)");
        }

        // ════════════ §5 적대검증 — liveArmed:false ════════════

        [Fact]
        public void LiveArmed_ResidualPublished_NoRealOrderWhenUnarmed()
        {
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 2, ring: ring, attestation: att, netter: new RealTimeBasketNetter());
            mesh.AddLane(1, weightBps: 5000, strategy: new FakeStrategy("A", 0, signal: +100 * Scale));
            mesh.AddLane(2, weightBps: 5000, strategy: new FakeStrategy("B", 0, signal: -30 * Scale));
            mesh.Repartition(M(100_000_000m));
            long residual = mesh.ProcessTick(5930, 100 * Scale, 0, 0);
            Assert.Equal(70 * Scale, residual);          // 잔여 발행됨

            var disp = new GatedOrderDispatcher(ring, isArmed: () => false, att);   // ★미무장
            bool sent = false;
            disp.Drain(_ => sent = true);
            Assert.False(sent);                          // ★실 송출 0(콜백 미호출)
            Assert.Equal(0L, disp.Forwarded);
            Assert.True(disp.GatedOut >= 1);             // 전량 게이팅 차단
        }

        [Fact]
        public void ProcessTick_Frozen_SuppressesAll()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("test");
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 2, ring: ring, attestation: att, netter: new RealTimeBasketNetter());
            mesh.AddLane(1, weightBps: 5000, strategy: new FakeStrategy("A", 0, signal: +100 * Scale));
            mesh.Repartition(M(100_000_000m));
            Assert.Equal(0L, mesh.ProcessTick(5930, 100 * Scale, 0, 0));   // 동결 — 전 신호 억제
            Assert.Equal(0L, mesh.ResidualsPublished);
        }

        // ── 적대검증 회귀: 자전거래 거부 시 상쇄분만 체결(예약 누수 클래스 제거·D2/D4) ──
        [Fact]
        public void WashRejectedResidual_BooksCrossedOnly_NoPhantomExposure()
        {
            var wg = new WashTradeGuard(windowTicks: 1_000_000L);
            wg.Admit(5930, OrderSide.Sell, 0);   // 사전 매도 마크 — 이후 매수 잔여를 대향으로 거부시킴
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 3, ring: ring, netter: new RealTimeBasketNetter(), washGuard: wg);
            mesh.AddLane(1, weightBps: 3333, strategy: new FakeStrategy("A", 0, +60 * Scale));   // idx0 매수
            mesh.AddLane(2, weightBps: 3333, strategy: new FakeStrategy("B", 0, +40 * Scale));   // idx1 매수
            mesh.AddLane(3, weightBps: 3334, strategy: new FakeStrategy("S", 0, -30 * Scale));   // idx2 매도
            mesh.Repartition(M(1_000_000_000m));
            long residual = mesh.ProcessTick(5930, 100 * Scale, 0, nowTicks: 100);   // 잔여 +70 매수 vs 직전 매도(0)·dt=100<window → 거부
            Assert.Equal(0L, residual);                       // 자전거래 거부 → 미사출
            // crossed=min(100,30)=30, 워터폴로 A(idx0) 30·B 0·S 30. residualFills=false → 상쇄분만 체결.
            long n30 = 30L * 100L * Scale;                    // Notional(30, 100) = 3000×Scale
            Assert.Equal(+n30, mesh.LaneExposureRaw(0));      // ★A 는 상쇄분 30만(잔여 미체결)
            Assert.Equal(0L, mesh.LaneExposureRaw(1));        // ★B 는 상쇄 0
            Assert.Equal(-n30, mesh.LaneExposureRaw(2));      // ★S 는 전량 상쇄 30
            Assert.Equal(0L, mesh.ResidualsPublished);
        }

        // ── 적대검증 회귀: WashTradeGuard 시각 역행(dt<0) fail-closed(D5) ──
        [Fact]
        public void WashGuard_ClockReversal_Opposing_FailsClosed()
        {
            var wg = new WashTradeGuard(windowTicks: 1000);
            Assert.True(wg.Admit(5930, OrderSide.Buy, 1000));
            Assert.False(wg.Admit(5930, OrderSide.Sell, 500));   // dt=-500<0(역행)+대향 → fail-closed 거부
            Assert.Equal(1L, wg.Rejections);
        }

        // ── 적대검증 회귀: 캐스케이드 백스톱 1회 래치(매 에폭 재발화 방지·D7) ──
        [Fact]
        public void Firewall_Cascade_BackstopLatched_NoRefire()
        {
            var ign = new IgnitionLock();
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "sig"));
            var fw = new CrossStrategyContagionFirewall(capacity: 4, velocityFuse: 2, cascadeSeals: 3, ignition: ign);
            for (int e = 0; e < 2; e++) { fw.MarkResult(0, -1, 100); fw.MarkResult(1, -1, 100); fw.MarkResult(2, -1, 100); fw.AdvanceEpoch(); }
            var v1 = fw.Evaluate();
            Assert.True(v1.SystemCascade);
            Assert.Equal(1L, fw.Cascades);
            // 재평가 — 여전히 캐스케이드 상태(UI)지만 Cascades 재증가·재강등 없음(래치).
            fw.AdvanceEpoch();
            var v2 = fw.Evaluate();
            Assert.True(v2.SystemCascade);        // 상태는 유지 보고
            Assert.Equal(1L, fw.Cascades);        // ★재발화 없음(래치)
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);   // 추가 강등 없음(재점화 가능 상태 유지)
        }

        // ── Cell Size 회귀 가드(정찰 지적 사각지대) — 신규 필드 seqlock 왕복 무결 ──
        [Fact]
        public void RingTelemetry_V540Fields_RoundTripThroughSeqlock()
        {
            var ch = new RingTelemetryChannel();
            var t = new RingTelemetry(1, 2, 3, 4, 5, 6.0,
                activeStrategyCount: 7, sealedLaneCount: 8, nettingSavedCostRaw: 9,
                washTradeRejections: 10, resonanceMetric: 11, capitalSaturationBps: 12);
            ch.Publish(in t);
            Assert.True(ch.TryRead(out var got));
            Assert.Equal(7L, got.ActiveStrategyCount);    // ★Size 미상향이면 여기서 오염/torn
            Assert.Equal(8L, got.SealedLaneCount);
            Assert.Equal(9L, got.NettingSavedCostRaw);
            Assert.Equal(10L, got.WashTradeRejections);
            Assert.Equal(11L, got.ResonanceMetric);
            Assert.Equal(12L, got.CapitalSaturationBps);
        }
    }

    // ★타이밍·δ=0 스트레스 — 직렬화.
    [Collection("ConcurrencyStress")]
    public sealed class MultiStrategyMeshConcurrencyTests
    {
        private const long Scale = 100_000_000L;
        private static long M(decimal krw) => QMoney.Of(krw).Raw;

        [Fact]
        public void HotPath_FanoutAndNet_ZeroAllocation()
        {
            // ★복수 전략이 단일 스트림을 찢어 팬아웃·네팅 매트릭스를 연산해도 힙 할당 0(δ=0).
            var ring = new OrderDispatchRing(64);
            var mesh = new MultiStrategyInterleavedMesh(capacity: 4, ring: ring, netter: new RealTimeBasketNetter(),
                washGuard: new WashTradeGuard(1_000_000L));
            // 완전 상쇄쌍(A+1 / B-1) — 잔여 0 → 자본 예약 전량 반납(축적 없음) → 순수 팬아웃+네팅 핫패스 반복.
            mesh.AddLane(1, weightBps: 5000, strategy: new FakeStrategy("A", 0, signal: +1 * Scale), carrier: new StrategyStateCarrier());
            mesh.AddLane(2, weightBps: 5000, strategy: new FakeStrategy("B", 0, signal: -1 * Scale), carrier: new StrategyStateCarrier());
            mesh.Repartition(M(1_000_000_000m));   // 넉넉한 예산 — 400k틱 익스포저 누적이 서브버짓 미포화(양 레인 상시 활성)

            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) mesh.ProcessTick(5930, 1 * Scale, 0, i);   // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++)
                        {
                            long res = mesh.ProcessTick(5930, 1 * Scale, 0, i);
                            if (res == long.MinValue) throw new Exception("sink");
                        }
                    });
                }
                catch (Exception e) when (e.Message != "sink") { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★팬아웃/네팅 핫패스 힙 0
        }

        [Fact]
        public void HotPath_NetterNet_ZeroAllocation()
        {
            var netter = new RealTimeBasketNetter();
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    Span<StrategyIntent> its = stackalloc StrategyIntent[4];
                    its[0] = new StrategyIntent(0, 0, 5930, +100 * Scale, 100 * Scale);
                    its[1] = new StrategyIntent(1, 1, 5930, -60 * Scale, 100 * Scale);
                    its[2] = new StrategyIntent(2, 2, 5930, -20 * Scale, 100 * Scale);
                    its[3] = new StrategyIntent(3, 3, 5930, +10 * Scale, 100 * Scale);
                    for (int i = 0; i < 300_000; i++) { var r = netter.Net(its, 100 * Scale); if (r.CrossedQtyRaw < 0) throw new Exception("sink"); }
                    long minAlloc = long.MaxValue;   // 람다 캡처 불가(Span 로컬) — ZeroAllocProbe 와 같은 다중 창 최솟값을 인라인으로
                    for (int zw = 0; zw < ZeroAllocProbe.DefaultWindows && minAlloc != 0; zw++)
                    {
                        long zb = GC.GetAllocatedBytesForCurrentThread();
                        for (int i = 0; i < 100_000; i++) { var r = netter.Net(its, 100 * Scale); if (r.CrossedQtyRaw < 0) throw new Exception("sink"); }
                        minAlloc = Math.Min(minAlloc, GC.GetAllocatedBytesForCurrentThread() - zb);
                    }
                    alloc = minAlloc;
                }
                catch (Exception e) when (e.Message != "sink") { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
