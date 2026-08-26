// ============================================================================
// LiveTotalIgnitionTests — v5.0.0 실전 총점화 3단계 + Think Beyond
//
//  §1 Phase1 Genesis — IsShadowMode 하드 검문·1raw 예수금 시딩·1회성·fail-closed.
//  §2 Phase2 Canary — 블래스트 반경 물리 강제(디스패처)·RTT 정규화·건별 net 정산 1raw 대조.
//  §3 Phase3 RedTeam — 원격 킬스위치(서명→봉인+Abort+동결)·데드맨 역-컷오버(생존신호 소실→자동 섀도우).
//  ★실 접속·자격증명·1주 사출은 운영자 영역 — 테스트는 전부 모의/페이퍼/주입 시계.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class LiveTotalIgnitionTests
    {
        private const string Sig = "SAFE_IGNITION_SEQ";

        private sealed class FakeBroker : IBroker
        {
            public string Name => "FAKE";
            public bool   IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp) => (true, "ok");
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        // 라이브 컷오버 조립체(양키 ON·해피패스 프로브).
        private static (AtomicCutoverGate gate, ShadowExecutionGateway gw, OrderAttestationService att) MakeLiveCutover()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == Sig, att);
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 3.0 };
            var seq = new SafeIgnitionSequencer(ign, att, sentinel, new FakeBroker(),
                valveWired: () => true, liveArmingWired: () => true,
                clock: () => new DateTime(2026, 7, 17, 9, 0, 0, DateTimeKind.Utc))
            { ProbeSymbol = "005930", ProbeReferencePrice = 1.0, MaxObservedSigma = 3.0 };
            var gw = new ShadowExecutionGateway(liveAttestation: att);
            var gate = new AtomicCutoverGate(gw, seq, ign, att, () => true, () => true);
            return (gate, gw, att);
        }

        // ════════════════════════ §1 Phase1 Genesis ══════════════════════════════

        [Fact]
        public void Genesis_ShadowMode_Seeds1RawDeposit()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);   // 기본 섀도우
            var mesh = new LiveAccountingMesh(0L, att);
            var genesis = new LiveAccountGenesis(mesh, gw, att);
            long krw = QMoney.Of(12_345_678m).Raw, usd = QMoney.Of(1_000m).Raw;
            var r = genesis.Establish((out AccountSnapshot s) => { s = new AccountSnapshot(krw, usd, QMoney.Of(5_000_000m).Raw); return true; });
            Assert.True(r.Success);
            Assert.Equal(GenesisState.Synced, genesis.State);
            Assert.Equal(krw, mesh.AvailableDepositRaw);   // ★1raw 정밀 시딩
            Assert.Equal(usd, mesh.AvailableUsdRaw);
        }

        [Fact]
        public void Genesis_LiveMode_Refused_FailClosed()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            gw.EnterLive();   // 실전 모드 — Genesis 금지
            var mesh = new LiveAccountingMesh(777L, att);
            var genesis = new LiveAccountGenesis(mesh, gw, att);
            var r = genesis.Establish((out AccountSnapshot s) => { s = new AccountSnapshot(QMoney.Of(100m).Raw, 0, 0); return true; });
            Assert.False(r.Success);
            Assert.Equal(GenesisState.Refused, genesis.State);
            Assert.Equal(777L, mesh.AvailableDepositRaw);   // 재시딩 안 됨(원 시드 유지)
        }

        [Fact]
        public void Genesis_QueryFails_Refused()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            var mesh = new LiveAccountingMesh(0L, att);
            var genesis = new LiveAccountGenesis(mesh, gw, att);
            var r = genesis.Establish((out AccountSnapshot s) => { s = default; return false; });   // 쿼리 실패
            Assert.False(r.Success);
            Assert.Equal(GenesisState.Refused, genesis.State);
        }

        [Fact]
        public void Genesis_NegativeDeposit_Refused()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            var mesh = new LiveAccountingMesh(0L, att);
            var genesis = new LiveAccountGenesis(mesh, gw, att);
            var r = genesis.Establish((out AccountSnapshot s) => { s = new AccountSnapshot(-1L, 0, 0); return true; });
            Assert.False(r.Success);
        }

        [Fact]
        public void Genesis_SecondCall_Refused_OneShot()
        {
            var att = new OrderAttestationService();
            using var gw = new ShadowExecutionGateway(liveAttestation: att);
            var mesh = new LiveAccountingMesh(0L, att);
            var genesis = new LiveAccountGenesis(mesh, gw, att);
            AccountSnapshotQuery q = (out AccountSnapshot s) => { s = new AccountSnapshot(QMoney.Of(100m).Raw, 0, 0); return true; };
            Assert.True(genesis.Establish(q).Success);
            Assert.False(genesis.Establish(q).Success);   // ★1회성 genesis 래치
        }

        [Fact]
        public void GenesisSeedDeposit_AfterFill_Refused()
        {
            var att = new OrderAttestationService();
            var mesh = new LiveAccountingMesh(long.MaxValue / 4, att);
            mesh.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(1m).Raw, 0);   // 체결 시작
            Assert.False(mesh.GenesisSeedDeposit(QMoney.Of(100m).Raw, 0));   // 체결 후 재시딩 금지
        }

        // ════════════════════════ §2 Phase2 Canary + 블래스트 반경 ════════════════

        [Fact]
        public void BlastRadius_CheckCeiling()
        {
            var br = new BlastRadiusLimiter();
            Assert.True(br.Check(long.MaxValue));           // 무제한 기본
            br.SetCeiling(1000);
            Assert.True(br.Check(1000));                    // 경계 통과
            Assert.False(br.Check(1001));                   // 초과 차단
            Assert.Equal(1L, br.Breaches);
            br.Clear();
            Assert.True(br.Check(long.MaxValue));           // 해제 후 무제한
        }

        [Fact]
        public void BlastRadius_InDispatcher_Exceeds_FreezesFailClosed()
        {
            // ★Think Beyond A: 완전 무장(armed=true)이어도 명목 상한 초과 주문은 송출 초크포인트에서 물리 차단+경성 동결.
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var br = new BlastRadiusLimiter();
            br.SetCeiling(QMoney.Of(500m).Raw);   // 상한 500원
            var disp = new GatedOrderDispatcher(ring, () => true, att, blackbox: null, blastRadius: br);
            // 명목 1000원(100×10) 주문 — 상한 초과.
            ring.PublishRaw(new OrderProposal(1, 0, 5930, OrderSide.Buy, 0, QPrice.Of(100m), QQty.Of(10m), 0));
            bool sent = false;
            disp.Drain(_ => sent = true);
            Assert.False(sent);                              // ★송출 안 됨(0바이트)
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★경성 fail-closed 동결
            Assert.True(br.Breaches > 0);
        }

        [Fact]
        public void BlastRadius_InDispatcher_WithinCeiling_Forwards()
        {
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var br = new BlastRadiusLimiter();
            br.SetCeiling(QMoney.Of(5000m).Raw);   // 상한 5000원 — 여유
            var disp = new GatedOrderDispatcher(ring, () => true, att, blackbox: null, blastRadius: br);
            ring.PublishRaw(new OrderProposal(1, 0, 5930, OrderSide.Buy, 0, QPrice.Of(100m), QQty.Of(10m), 0));
            bool sent = false;
            disp.Drain(_ => sent = true);
            Assert.True(sent);                               // 상한 이내 → 송출
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void BlastRadius_MarketOrder_ActiveCeiling_Blocked()
        {
            // ★적대검증 HIGH 회귀: 시장가(PriceRaw=0)는 명목 0으로 계산돼 상한을 우회했음. 상한 활성 중 시장가는 물리 차단+동결.
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var br = new BlastRadiusLimiter();
            br.SetCeiling(QMoney.Of(500m).Raw);
            var disp = new GatedOrderDispatcher(ring, () => true, att, blackbox: null, blastRadius: br);
            // 시장가 대형 매수 10만주(PriceRaw=0) — 이전엔 명목 0으로 통과했음.
            ring.PublishRaw(new OrderProposal(1, 0, 5930, OrderSide.Buy, 0, QPrice.Of(0m), QQty.Of(100_000m), 0));
            bool sent = false;
            disp.Drain(_ => sent = true);
            Assert.False(sent);                              // ★시장가 대형 주문 물리 차단(0바이트)
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(br.Breaches > 0);
        }

        [Fact]
        public void BlastRadius_MarketOrder_InactiveCeiling_Passes()
        {
            // 상한 비활성(무제한)이면 시장가는 무영향(회귀 방지 — 기본 동작 보존).
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var br = new BlastRadiusLimiter();   // 미설정 = 무제한
            var disp = new GatedOrderDispatcher(ring, () => true, att, blackbox: null, blastRadius: br);
            ring.PublishRaw(new OrderProposal(1, 0, 5930, OrderSide.Buy, 0, QPrice.Of(0m), QQty.Of(100_000m), 0));
            bool sent = false;
            disp.Drain(_ => sent = true);
            Assert.True(sent);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Deadman_DoubleCheck_IdempotentSingleRevert()
        {
            // ★적대검증 회귀: Timer+CheckOnce 동시성 → CAS 전이로 정확히 1회 역-컷오버(이중 롤백/계수 봉쇄).
            var (gate, gw, _) = MakeLiveCutover();
            gate.PreArm("운영자"); gate.Cutover("운영자");
            long clock = 0;
            var deadman = new DeadmanReverseCutover(gate, TimeSpan.FromSeconds(10), nowTicks: () => clock, tickFrequency: 1_000_000L);
            deadman.StartWatching();
            clock = 20_000_000L;
            deadman.CheckOnce();
            deadman.CheckOnce();   // 두 번째 — 이미 Reverted, 무동작
            deadman.CheckOnce();
            Assert.Equal(DeadmanState.Reverted, deadman.State);
            Assert.Equal(1L, deadman.Reversions);   // ★정확히 1회
        }

        [Fact]
        public void CanaryProbe_PathologicalRtt_TripsNotFailOpen()
        {
            // ★적대검증 회귀(ToMicros 오버플로): 초대형 RTT 가 음수로 wrap 해 fail-open(초고속 오인) 되면 안 됨 → clamp 로 trip(fail-closed).
            var (gate, gw, _) = MakeLiveCutover();
            gate.PreArm("운영자"); gate.Cutover("운영자");
            var mesh = new LiveAccountingMesh(0L);
            var probe = new CanaryLiveProbe(gate, mesh);
            for (int i = 0; i < 20; i++) probe.SeedBaselineRtt(10_000 + (i % 2) * 2_000, 10_000_000L);   // 정상 베이스라인
            bool tripped = probe.ObserveBrokerRtt(long.MaxValue, 10_000_000L);   // 병리적 초대형 RTT
            Assert.True(tripped);                 // ★fail-closed — 되감기 트립(음수 wrap 으로 통과하지 않음)
            Assert.True(gw.IsShadowMode);
        }

        [Fact]
        public void CanaryProbe_RttNormalized_FeedsCanary()
        {
            var (gate, gw, _) = MakeLiveCutover();
            gate.PreArm("운영자"); gate.Cutover("운영자");
            var mesh = new LiveAccountingMesh(0L);
            var probe = new CanaryLiveProbe(gate, mesh);
            // 100ns ticks(freq=10^7) → µs 정규화. 베이스라인 + 관측을 같은 단위로.
            for (int i = 0; i < 20; i++) probe.SeedBaselineRtt(10_000 + (i % 2) * 2_000, 10_000_000L);
            bool tripped = probe.ObserveBrokerRtt(10_000, 10_000_000L);   // 정상 범위 → 트립 아님
            Assert.False(tripped);
            Assert.True(probe.RttSamples > 0);
        }

        // ════════════════════════ §3 Phase3 RedTeam ══════════════════════════════

        [Fact]
        public void KillSwitch_ValidSignature_FreezesAbortsTrips()
        {
            var att = new OrderAttestationService();
            bool aborted = false;
            var ks = new RemoteKillSwitch(att, s => s == "KILL-OK", vault: null, abortSockets: () => aborted = true);
            Assert.True(ks.Trip("KILL-OK"));
            Assert.Equal(KillSwitchState.Tripped, ks.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★비상 정지 수렴
            Assert.True(aborted);                                 // ★소켓 Abort 호출
            Assert.Equal(1L, ks.Trips);
        }

        [Fact]
        public void KillSwitch_InvalidSignature_NoOp()
        {
            var att = new OrderAttestationService();
            bool aborted = false;
            var ks = new RemoteKillSwitch(att, s => s == "KILL-OK", vault: null, abortSockets: () => aborted = true);
            Assert.False(ks.Trip("위조"));       // 서명 불일치
            Assert.False(ks.Trip(""));           // 공백
            Assert.Equal(KillSwitchState.Ready, ks.State);
            Assert.Equal(GatingMode.Normal, att.Mode);            // ★동결 없음(위조로 흔들 수 없음)
            Assert.False(aborted);
            Assert.Equal(2L, ks.Rejected);
        }

        [Fact]
        public void KillSwitch_SealsVault()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"kill_{Guid.NewGuid():N}.db");
            using var db = new LocalDatabase(dbPath);
            try
            {
                var att = new OrderAttestationService();
                var vault = new SecurityAssetVault(db, att, () => false);
                var ks = new RemoteKillSwitch(att, s => !string.IsNullOrWhiteSpace(s), vault);
                Assert.False(vault.IsSealed);
                Assert.True(ks.Trip("KILL-20260717"));
                Assert.True(vault.IsSealed);                      // ★자격증명 금고 재봉인(불가역)
            }
            finally { db.Dispose(); try { File.Delete(dbPath); } catch { } }
        }

        [Fact]
        public void Deadman_SignalLapse_AutoReverseCutover()
        {
            var (gate, gw, att) = MakeLiveCutover();
            gate.PreArm("운영자"); gate.Cutover("운영자");
            Assert.True(gate.IsLive);
            long clock = 0;
            var deadman = new DeadmanReverseCutover(gate, TimeSpan.FromSeconds(10), nowTicks: () => clock, tickFrequency: 1_000_000L);
            deadman.StartWatching();          // _lastBeat = 0
            clock = 20_000_000L;              // 20s 경과(데드라인 10s 초과)
            deadman.CheckOnce();
            Assert.Equal(DeadmanState.Reverted, deadman.State);
            Assert.True(gw.IsShadowMode);     // ★자동 섀도우 역-컷오버
            Assert.False(gate.IsLive);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 되감기는 연성(동결 아님)
        }

        [Fact]
        public void Deadman_SignalAlive_NoRevert()
        {
            var (gate, gw, _) = MakeLiveCutover();
            gate.PreArm("운영자"); gate.Cutover("운영자");
            long clock = 0;
            var deadman = new DeadmanReverseCutover(gate, TimeSpan.FromSeconds(10), nowTicks: () => clock, tickFrequency: 1_000_000L);
            deadman.StartWatching();
            clock = 5_000_000L;   // 5s 경과
            deadman.Beat();       // 생존 신호(=5s)
            clock = 12_000_000L;  // 추가 7s(마지막 beat 이후 7s < 10s)
            deadman.CheckOnce();
            Assert.NotEqual(DeadmanState.Reverted, deadman.State);
            Assert.True(gate.IsLive);         // 실전 유지
        }
    }

    // ── §4 Canary 건별 net 정산 대조(회계 확정 대기 — 타이밍) ─────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class LiveTotalIgnitionCanaryTests
    {
        private const string Sig = "SAFE_IGNITION_SEQ";
        private sealed class FakeBroker : IBroker
        {
            public string Name => "FAKE"; public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp) => (true, "ok");
            public void Cancel(string id) { } public decimal HeldQty(PaperAsset a, string s) => 0m;
        }
        private static (AtomicCutoverGate gate, OrderAttestationService att) MakeLive(OrderAttestationService att)
        {
            var ign = new IgnitionLock(s => s == Sig, att);
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 3.0 };
            var seq = new SafeIgnitionSequencer(ign, att, sentinel, new FakeBroker(),
                valveWired: () => true, liveArmingWired: () => true,
                clock: () => new DateTime(2026, 7, 17, 9, 0, 0, DateTimeKind.Utc))
            { ProbeSymbol = "005930", ProbeReferencePrice = 1.0, MaxObservedSigma = 3.0 };
            var gw = new ShadowExecutionGateway(liveAttestation: att);
            var gate = new AtomicCutoverGate(gw, seq, ign, att, () => true, () => true);
            gate.PreArm("운영자"); gate.Cutover("운영자");
            return (gate, att);
        }

        [Fact]
        public void CanaryNetReconcile_Match_NoFreeze_Mismatch_Freezes()
        {
            // 일치 케이스.
            var attM = new OrderAttestationService();
            var (gateM, _) = MakeLive(attM);
            var meshM = new LiveAccountingMesh(long.MaxValue / 4, attM); meshM.Start();
            var probeM = new CanaryLiveProbe(gateM, meshM);
            meshM.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(1m).Raw, 0);
            WaitFinalized(meshM);
            long netM = meshM.LastFillNetRaw;
            Assert.True(probeM.TryReconcileLatestFill(netM));      // 정확 일치 대조 수행
            Assert.Equal(GatingMode.Normal, attM.Mode);           // ★일치 → 동결 없음
            meshM.Dispose();

            // 불일치 케이스(broker net 이 2raw 이탈).
            var attX = new OrderAttestationService();
            var (gateX, _) = MakeLive(attX);
            var meshX = new LiveAccountingMesh(long.MaxValue / 4, attX); meshX.Start();
            var probeX = new CanaryLiveProbe(gateX, meshX);
            meshX.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(1m).Raw, 0);
            WaitFinalized(meshX);
            long netX = meshX.LastFillNetRaw;
            Assert.True(probeX.TryReconcileLatestFill(netX + 2L)); // 2raw 이탈 대조 수행
            Assert.Equal(GatingMode.EmergencyFreeze, attX.Mode);  // ★불일치 → 경성 동결
            Assert.True(probeX.ReconcileFreezes > 0);
            meshX.Dispose();
        }

        [Fact]
        public void CanaryReconcile_SeqlockConsistent_SecondCallDeduped()
        {
            // ★적대검증 HIGH 회귀: (seq,net) 일관 페어 seqlock 독출 + seq 기반 dedup(같은 확정건 재대조 금지).
            var att = new OrderAttestationService();
            var (gate, _) = MakeLive(att);
            var mesh = new LiveAccountingMesh(long.MaxValue / 4, att); mesh.Start();
            var probe = new CanaryLiveProbe(gate, mesh);
            mesh.OnFill(5930, OrderSide.Buy, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(1m).Raw, 0);
            WaitFinalized(mesh);
            // seqlock 일관 독출 — seq 짝수·양수, net 페어.
            Assert.True(mesh.TryReadLatestNet(out long net, out _, out _, out long seq));
            Assert.True(seq > 0 && (seq % 2) == 0);
            Assert.True(net != 0);
            // 첫 대조 수행, 두 번째(같은 seq) 는 dedup 으로 보류(false).
            Assert.True(probe.TryReconcileLatestFill(net));
            Assert.False(probe.TryReconcileLatestFill(net));   // ★seq dedup
            Assert.Equal(GatingMode.Normal, att.Mode);         // 일치 → 동결 없음
            mesh.Dispose();
        }

        private static void WaitFinalized(LiveAccountingMesh m)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000 && m.FillsFinalized < 1 && !m.IsFrozen) Thread.Sleep(3);
            Assert.True(m.FillsFinalized >= 1, "체결 확정 대기 실패");
        }
    }
}
