// ============================================================================
// ShadowExecutionGatewayTests — v4.97.0: 실거래 섀도우 인입 & 원자적 컷오버 게이트웨이
//
//  §1 송출 라우팅 판정 — 섀도우 바이패스 / 실전 무장 / fail-closed 차단.
//  §2 ★영지식 격리 샌드박스 펜스(적대검증 #1) — IsShadowMode==true → 실 송출 0바이트(스레드 경합·오염 주문 포함).
//  §3 ★이중 차단 인터락(적대검증 #2·liveArmed:false) — 컷오버해도 이중키 미충족이면 실주문 0 + fail-closed 동결.
//  §4 토스 REST 경계 절연(기존 갭 보강) — 섀도우 바이패스 / 라이브-미무장 abort+freeze+throw.
//  §5 2단 원자 컷오버 오케스트레이션 — PreArm(이중키∧프로브∧3σ)→Cutover(원자 flip)·가역 되감기.
//  §6 Think Beyond — 카나리 단측 3σ 되감기 / 듀얼트랙 1raw 발산 경성 동결.
//  §7 δ=0 핫패스 & 50ms 가상 청산 공급(부하/타이밍 → 직렬화 컬렉션).
// ============================================================================
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ShadowExecutionGatewayTests
    {
        private const string Sig = "SAFE_IGNITION_SEQ";
        private static byte[] OrderBytes() => Encoding.UTF8.GetBytes("POST /v1/orders {side:buy,qty:1}");
        private static byte[] SubscribeBytes() => Encoding.UTF8.GetBytes("{\"type\":\"subscribe\",\"symbol\":\"005930\"}");

        private static ShadowRouteIntent Buy(long qtyShares = 10) =>
            new ShadowRouteIntent(5930, OrderSide.Buy, AssetKind.Domestic,
                QPrice.Of(100m).Raw, QQty.Of(qtyShares).Raw, 0L);

        // ════════════════════════ §1 송출 라우팅 판정 ════════════════════════════

        [Fact]
        public void TryRoute_ShadowMode_ReturnsShadow_NeverLive()
        {
            using var gw = new ShadowExecutionGateway();      // 기본 섀도우
            Assert.True(gw.IsShadowMode);
            // 이중키·MasterIgnited 를 넘겨도 섀도우면 Live 아님(라우팅 격리).
            Assert.Equal(RouteVerdict.Shadow, gw.TryRoute(Buy(), dualKeyArmed: true, IgnitionStage.MasterIgnited));
            Assert.Equal(0L, gw.LiveRoutedCount);
            Assert.True(gw.BypassedCount > 0);
        }

        [Fact]
        public void TryRoute_PoisonOrder_Blocked()
        {
            using var gw = new ShadowExecutionGateway();
            var poison = new ShadowRouteIntent(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, 0L, 0L);
            Assert.Equal(RouteVerdict.Blocked, gw.TryRoute(poison, dualKeyArmed: true, IgnitionStage.MasterIgnited));
        }

        [Fact]
        public void TryRoute_Live_Armed_ReturnsLive()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            gw.EnterLive();
            Assert.Equal(RouteVerdict.Live, gw.TryRoute(Buy(), dualKeyArmed: true, IgnitionStage.MasterIgnited));
            Assert.Equal(1L, gw.LiveRoutedCount);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 정당 무장 — 동결 없음
        }

        // ════════════════════════ §2 ★영지식 격리 샌드박스 펜스 ══════════════════

        [Fact]
        public void AllowOutboundBytes_Shadow_OrderPacket_BlockedZeroBytes()
        {
            using var gw = new ShadowExecutionGateway();
            // 섀도우 → 주문 패킷 실 송출 금지(0바이트). abort/throw 없이 조용히 false.
            Assert.False(gw.AllowOutboundBytes(OrderBytes(), dualKeyArmed: true, IgnitionStage.MasterIgnited));
            Assert.True(gw.BypassedCount > 0);
        }

        [Fact]
        public void AllowOutboundBytes_Shadow_NonOrderPacket_Passes()
        {
            using var gw = new ShadowExecutionGateway();
            // 비-주문(구독)은 섀도우여도 통과(시세/구독은 실 소켓 유지 — 게이트웨이 스코프는 '주문' 송출만).
            Assert.True(gw.AllowOutboundBytes(SubscribeBytes(), dualKeyArmed: false, IgnitionStage.Locked));
        }

        [Fact]
        public void AllowOutboundBytes_Live_Unarmed_OrderPacket_AbortsFreezesThrows()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            gw.EnterLive();
            bool aborted = false;
            Assert.Throws<ZeroLeakViolationException>(() =>
                gw.AllowOutboundBytes(OrderBytes(), dualKeyArmed: false, IgnitionStage.MasterIgnited, abort: () => aborted = true));
            Assert.True(aborted);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void AllowOutboundBytes_Live_Frozen_ButArmed_Blocked_NoSend()
        {
            // ★fail-closed 회귀: 동결 상태면 이중키∧MasterIgnited 여도 실 송출 금지(AssertSendAllowed 는 동결 미검 → 게이트웨이가 직교 검문).
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            gw.EnterLive();
            att.ForceFreeze("선행 동결(타 서브시스템)");
            // throw 없이 false(차단) — 정당 무장이지만 동결이라 송출 안 함.
            Assert.False(gw.AllowOutboundBytes(OrderBytes(), dualKeyArmed: true, IgnitionStage.MasterIgnited));
        }

        // ════════════════════════ §3 ★이중 차단 인터락(liveArmed:false) ══════════

        [Fact]
        public void Cutover_ThenUnarmedRouting_Blocked_FailClosedFreeze()
        {
            // ★핵심 증명: 섀도우를 실전으로 flip(EnterLive)했더라도, 발주 시점에 이중키(dualKeyArmed=false)면
            //   실주문 0(Blocked) + fail-closed 동결. 컷오버 플래그 하나로는 실주문이 나가지 않는다(이중 차단 ②층).
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            gw.EnterLive();
            Assert.Equal(RouteVerdict.Blocked, gw.TryRoute(Buy(), dualKeyArmed: false, IgnitionStage.MasterIgnited));
            Assert.Equal(0L, gw.LiveRoutedCount);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // fail-closed 동결
        }

        [Fact]
        public void Cutover_MasterNotIgnited_Blocked()
        {
            // 이중키는 참이나 FSM 이 MasterIgnited 아니면(예: ProbeReady) 실주문 0(IsOrderSendAllowed=false) → 동결.
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            gw.EnterLive();
            Assert.Equal(RouteVerdict.Blocked, gw.TryRoute(Buy(), dualKeyArmed: true, IgnitionStage.ProbeReady));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ════════════════════════ §4 토스 REST 경계 절연(갭 보강) ════════════════

        [Fact]
        public void TossPostOrder_ShadowGateway_OrderPost_ZeroBytesBypass()
        {
            using var gw = new ShadowExecutionGateway();   // 섀도우
            var transport = new TossHttpTransport("https://api.toss-invest.example",
                orderArmed: () => true, ignitionStage: () => IgnitionStage.MasterIgnited, shadowGateway: gw);
            // 주문 POST → 섀도우 바이패스(실 HTTP 미발생) → (false, "[SHADOW]…"). 네트워크 미접촉.
            var (ok, body) = transport.PostOrder("/v1/orders", "{\"side\":\"buy\"}");
            Assert.False(ok);
            Assert.Contains("SHADOW", body);
            Assert.True(gw.BypassedCount > 0);
        }

        [Fact]
        public void TossDeleteOrder_ShadowGateway_ZeroBytesBypass()
        {
            // ★취소도 주문 계열('/v1/orders/{id}') — 섀도우면 실 DELETE 0바이트 바이패스(예외 없이 조용히 반환).
            using var gw = new ShadowExecutionGateway();
            var transport = new TossHttpTransport("https://api.toss-invest.example",
                orderArmed: () => true, ignitionStage: () => IgnitionStage.MasterIgnited, shadowGateway: gw);
            long before = gw.BypassedCount;
            transport.DeleteOrder("ORDER-123");        // 네트워크 미접촉 — 바이패스 계수 증가로 확인
            Assert.True(gw.BypassedCount > before);
        }

        [Fact]
        public void TossPostOrder_Live_Unarmed_FreezesThrows()
        {
            // 게이트웨이 미배선 + 절연기 직접 — 라이브-미무장 주문 POST → abort+ForceFreeze+throw(삼킴 금지).
            var att = new OrderAttestationService();
            var transport = new TossHttpTransport("https://api.toss-invest.example",
                attestation: att, orderArmed: () => false, ignitionStage: () => IgnitionStage.Locked);
            Assert.Throws<ZeroLeakViolationException>(() => transport.PostOrder("/v1/orders", "{\"side\":\"buy\"}"));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ════════════════════════ §5 2단 원자 컷오버 오케스트레이션 ═══════════════

        private sealed class FakeBroker : IBroker
        {
            public string Name => "FAKE";
            public bool   IsLive => false;   // ★격리 — 프로브는 IsLive=false 페이퍼 경로만
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp) => (true, "ok");
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        private static (AtomicCutoverGate gate, ShadowExecutionGateway gw, OrderAttestationService att) MakeCutover(
            Func<bool> valve, Func<bool> live)
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == Sig, att);
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 3.0 };
            var seq = new SafeIgnitionSequencer(ign, att, sentinel, new FakeBroker(),
                valveWired: valve, liveArmingWired: live,
                clock: () => new DateTime(2026, 7, 16, 9, 0, 0, DateTimeKind.Utc))
            { ProbeSymbol = "005930", ProbeReferencePrice = 1.0, MaxObservedSigma = 3.0 };
            var gw = new ShadowExecutionGateway(liveAttestation: att);
            var gate = new AtomicCutoverGate(gw, seq, ign, att, valve, live);
            return (gate, gw, att);
        }

        [Fact]
        public void PreArm_DualKeyOff_Refused_StaysShadow()
        {
            // ★인터락 ①층: 이중키 미충족이면 PreArm(=BeginIgnition G0) 거부 → 섀도우 유지·동결 없음.
            var (gate, gw, att) = MakeCutover(valve: () => false, live: () => false);
            var r = gate.PreArm("운영자");
            Assert.False(r.Success);
            Assert.True(gw.IsShadowMode);                    // 여전히 섀도우(자본 안전)
            Assert.False(gate.IsLive);
            Assert.Equal(GatingMode.Normal, att.Mode);       // 거부일 뿐 동결 아님
        }

        [Fact]
        public void PreArm_ThenCutover_BothKeysOn_GoesLive()
        {
            var (gate, gw, att) = MakeCutover(valve: () => true, live: () => true);
            Assert.True(gate.PreArm("운영자").Success);
            Assert.Equal(CutoverState.ArmedReady, gate.State);
            Assert.True(gw.IsShadowMode);                    // ★사전무장 후에도 섀도우 유지(자본 안전)
            var c = gate.Cutover("운영자");
            Assert.True(c.Success);
            Assert.True(gate.IsLive);                        // ★원자 flip 완료
            Assert.False(gw.IsShadowMode);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Cutover_DualKeyDroppedAfterPreArm_FailClosedFreeze()
        {
            // ★인터락 ②층: 사전무장(양키 ON)→ArmedReady 후 한 키를 내리면 Cutover 순간 재검 실패 → fail-closed 동결·실전 미전환.
            bool live = true;
            var (gate, gw, att) = MakeCutover(valve: () => true, live: () => live);
            Assert.True(gate.PreArm("운영자").Success);
            live = false;                                    // 컷오버 직전 이중키 붕괴
            var c = gate.Cutover("운영자");
            Assert.False(c.Success);
            Assert.False(gate.IsLive);                       // 실전 미전환
            Assert.True(gw.IsShadowMode);                    // 섀도우 유지
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // fail-closed 동결
        }

        [Fact]
        public void Cutover_BlankSignature_Refused()
        {
            var (gate, _, _) = MakeCutover(valve: () => true, live: () => true);
            Assert.True(gate.PreArm("운영자").Success);
            Assert.False(gate.Cutover("   ").Success);       // 공백 서명 fail-closed
        }

        // ════════════════════════ §6 Think Beyond — 카나리 / 듀얼트랙 ═════════════

        [Fact]
        public void Canary_ThreeSigmaSpike_ReversibleRollback()
        {
            var (gate, gw, att) = MakeCutover(valve: () => true, live: () => true);
            gate.PreArm("운영자"); gate.Cutover("운영자");
            Assert.True(gate.IsLive);
            // 안정 RTT 다수 주입(베이스라인 — 분산>0 위해 900/1100 교대) → 단측 3σ 상향 스파이크 → 가역 되감기(EnterShadow), 동결 아님.
            for (int i = 0; i < 40; i++) gate.ObserveCanaryRtt(900 + (i % 2) * 200);
            bool tripped = gate.ObserveCanaryRtt(1_000_000);   // 급격한 느려짐
            Assert.True(tripped);
            Assert.True(gw.IsShadowMode);                       // ★되감기 — 자본 즉시 안전
            Assert.False(gate.IsLive);
            Assert.Equal(GatingMode.Normal, att.Mode);          // ★가역(경성 동결 아님)
            Assert.True(gate.RollbackCount > 0 && gate.CanaryTripCount > 0);
        }

        [Fact]
        public void Canary_SeededBaseline_DoesNotCollapseQuarantineWindow()
        {
            // ★검역창 회귀: 시드 베이스라인 표본은 승격 카운트에 포함되면 안 됨. 컷오버 후 청정 라이브 표본 16개를 채워야 Live.
            var (gate, gw, att) = MakeCutover(valve: () => true, live: () => true);
            for (int i = 0; i < 30; i++) gate.SeedCanaryBaseline(900 + (i % 2) * 200);   // 풍부한 베이스라인(분산>0)
            gate.PreArm("운영자"); gate.Cutover("운영자");
            Assert.Equal(CutoverState.CanaryQuarantine, gate.State);
            for (int i = 0; i < 15; i++) Assert.False(gate.ObserveCanaryRtt(1000));       // 청정 15개 — 아직 검역 중
            Assert.Equal(CutoverState.CanaryQuarantine, gate.State);                       // ★단일 표본으로 붕괴하지 않음
            Assert.False(gate.ObserveCanaryRtt(1000));                                     // 16번째 청정 → 승격
            Assert.Equal(CutoverState.Live, gate.State);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void DualTrack_ExactlyOneRaw_Tolerated_NoFreeze()
        {
            // ★off-by-one 회귀: 정상 ±1raw 반올림 잔차는 관용(동결 아님). |drift|>tol 만 동결.
            var (gate, gw, att) = MakeCutover(valve: () => true, live: () => true);
            gate.PreArm("운영자"); gate.Cutover("운영자");
            Assert.False(gate.ReconcileDualTrack(1_000_000L, 1_000_000L));   // 0raw — 통과
            Assert.False(gate.ReconcileDualTrack(1_000_000L, 1_000_001L));   // +1raw — 관용(통과)
            Assert.False(gate.ReconcileDualTrack(1_000_000L,   999_999L));   // -1raw — 관용(통과)
            Assert.Equal(GatingMode.Normal, att.Mode);                       // 동결 없음
            Assert.True(gate.IsLive);                                        // 실전 유지
        }

        [Fact]
        public void DualTrack_DivergenceBeyondOneRaw_HardFreeze_ReShadows()
        {
            var (gate, gw, att) = MakeCutover(valve: () => true, live: () => true);
            gate.PreArm("운영자"); gate.Cutover("운영자");
            Assert.True(gate.ReconcileDualTrack(1_000_000L, 1_000_002L));    // 2raw 발산(>tol) → 동결
            Assert.Equal(CutoverState.Frozen, gate.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);              // ★경성 동결(비가역)
            Assert.True(gw.IsShadowMode);                                    // ★섀도우 복귀(ManualRelease 후 즉시 재라이브 방지)
            Assert.True(gate.DualTrackFreezeCount > 0);
        }

        [Fact]
        public void Rollback_ThenReCutover_Reversible()
        {
            // 되감기 후 재컷오버가 즉시 가능(점화 FSM 파괴 없는 플래그 되감기 = 진짜 가역).
            var (gate, gw, _) = MakeCutover(valve: () => true, live: () => true);
            gate.PreArm("운영자"); gate.Cutover("운영자");
            gate.RollbackToShadow("수동 되감기");
            Assert.True(gw.IsShadowMode);
            Assert.Equal(CutoverState.RolledBack, gate.State);
            var c = gate.Cutover("운영자");                  // 재컷오버(RolledBack 에서 허용)
            Assert.True(c.Success);
            Assert.True(gate.IsLive);
        }
    }

    // ── §7 δ=0 & 50ms 가상 청산(부하/타이밍 민감 → 직렬화) ──────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class ShadowExecutionGatewayConcurrencyTests
    {
        private static ShadowRouteIntent Buy() =>
            new ShadowRouteIntent(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(10m).Raw, 0L);

        [Fact]
        public void TryRoute_ShadowHotPath_ZeroAllocation()
        {
            using var gw = new ShadowExecutionGateway();   // 펌프 미기동 — 링 발행만(δ=0)
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    var o = Buy();
                    for (int i = 0; i < 300_000; i++) gw.TryRoute(o, true, IgnitionStage.MasterIgnited);   // 워밍업
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++) gw.TryRoute(o, true, IgnitionStage.MasterIgnited);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★섀도우 라우팅 핫패스 힙 할당 0
        }

        [Fact]
        public void ZeroKnowledgeSandboxFence_ThreadRace_ZeroLiveRoute()
        {
            // ★적대검증 #1(영지식 격리 샌드박스 펜스): IsShadowMode==true 인 동안 어떤 스레드 경합·오염 주문에도
            //   실 송출로 단 1건도 새지 않는다. Live 판정 0 · AllowOutboundBytes(주문)=true 0.
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);   // 섀도우 고정(cutover 미호출)
            byte[] order = Encoding.UTF8.GetBytes("POST /v1/orders {side:buy}");
            const int threads = 8, iters = 50_000;
            long liveVerdicts = 0, wouldSend = 0;
            var barrier = new Barrier(threads);
            var poison = new ShadowRouteIntent(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, 0L, 0L);
            var good   = Buy();

            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < iters; i++)
                    {
                        // 정상·오염 주문을 뒤섞어 경합 유발. 이중키∧MasterIgnited 를 넘겨도 섀도우면 절대 Live 아님.
                        var intent = (i & 1) == 0 ? good : poison;
                        if (gw.TryRoute(intent, dualKeyArmed: true, IgnitionStage.MasterIgnited) == RouteVerdict.Live)
                            Interlocked.Increment(ref liveVerdicts);
                        if (gw.AllowOutboundBytes(order, dualKeyArmed: true, IgnitionStage.MasterIgnited))
                            Interlocked.Increment(ref wouldSend);   // 주문 패킷이 실 송출 허용됐다면 누수(있어선 안 됨)
                    }
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));

            Assert.Equal(0L, Interlocked.Read(ref liveVerdicts));   // ★Live 판정 0
            Assert.Equal(0L, Interlocked.Read(ref wouldSend));      // ★실 송출 허용 0(1바이트도 안 나감)
            Assert.Equal(0L, gw.LiveRoutedCount);                   // ★게이트웨이 실 라우팅 카운터 0
            Assert.Equal(GatingMode.Normal, att.Mode);              // 섀도우 정상 — 동결 없음
        }

        [Fact]
        public void VirtualFill_SuppliedToShadowMesh_WithinPumpPeriod()
        {
            // 섀도우 주문 → 50ms 펌프가 슬리피지 반영 가상 체결을 전용 섀도우 청산 메쉬에 공급.
            var shadowAtt  = new OrderAttestationService();
            var shadowMesh = new LiveAccountingMesh(long.MaxValue / 4, shadowAtt);
            shadowMesh.Start();
            using var gw = new ShadowExecutionGateway(liveAttestation: new OrderAttestationService(),
                shadowMesh: shadowMesh, pumpPeriodMs: 20);
            gw.StartPump();
            // 섀도우 매수 3건 인입.
            for (int i = 0; i < 3; i++) gw.TryRoute(Buy(), dualKeyArmed: false, IgnitionStage.Locked);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3_000 && gw.VirtualFillCount < 3) Thread.Sleep(5);
            Assert.True(gw.VirtualFillCount >= 3, $"가상 체결 미공급: {gw.VirtualFillCount}");
            Assert.True(gw.LastFillRttMicros >= 0);
            shadowMesh.Dispose();
        }

        [Fact]
        public void VirtualFill_FractionalCrypto_NotTruncated()
        {
            // ★회귀: 소수 수량(코인 0.5)은 정수 FillQty 왕복이면 0으로 절사·유실됐다. raw 경로(OnFill) 직결로 실제 체결 공급 확인.
            var shadowMesh = new LiveAccountingMesh(long.MaxValue / 4, new OrderAttestationService());
            shadowMesh.Start();
            using var gw = new ShadowExecutionGateway(liveAttestation: new OrderAttestationService(),
                shadowMesh: shadowMesh, pumpPeriodMs: 20);
            gw.StartPump();
            var frac = new ShadowRouteIntent(1, OrderSide.Buy, AssetKind.Crypto, QPrice.Of(50000m).Raw, QQty.Of(0.5m).Raw, 0L);
            gw.TryRoute(frac, dualKeyArmed: false, IgnitionStage.Locked);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 3_000 && gw.VirtualFillCount < 1) Thread.Sleep(5);
            Assert.True(gw.VirtualFillCount >= 1, "소수 수량 가상 체결이 공급되지 않음(절사 유실 회귀)");
            shadowMesh.Dispose();
        }

        [Fact]
        public void FlushShadowBacklog_CancelsUnfilled()
        {
            // 펌프 미기동 상태로 백로그 적재 → 컷오버 원자 플러시가 미체결 섀도우 주문을 취소(가상 체결 미생성).
            var shadowAtt  = new OrderAttestationService();
            var shadowMesh = new LiveAccountingMesh(long.MaxValue / 4, shadowAtt);
            using var gw = new ShadowExecutionGateway(liveAttestation: new OrderAttestationService(), shadowMesh: shadowMesh);
            for (int i = 0; i < 5; i++) gw.TryRoute(Buy(), dualKeyArmed: false, IgnitionStage.Locked);
            long flushed = gw.FlushShadowBacklog();
            Assert.Equal(5L, flushed);
            Assert.Equal(0L, gw.VirtualFillCount);   // 플러시는 취소 — 가상 체결 생성 안 함
            shadowMesh.Dispose();
        }
    }
}
