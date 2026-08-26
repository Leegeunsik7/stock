// ============================================================================
// WarmRestartEngineTests — v4.90.0: 결정론적 웜 스타트 복구 엔진
//
//  §1 복구기 잠금 펜스   — 복구 중 라이브 라우팅 술어·CanRouteToLive 무조건 false(주문 폭주 차단).
//  §2 가격정합성 펜스     — 격자 미정렬(오염) → ForceFreeze·드레인 중단 / 정렬(정상) → 소진.
//  §3 시퀀스 전이         — 서명 거부·미동결 no-op·해피패스(Stable·MasterIgnited).
//  §4 단일 리더·텔레메트리 — RecoveryDrain Stop 강제·RingTelemetry 복구 필드/헬퍼·퍼블리셔 반영.
// ============================================================================
using System;
using System.IO;
using Xunit;

namespace StockManager.Tests
{
    public sealed class WarmRestartEngineTests : IDisposable
    {
        private const string Sig = "SAFE_IGNITION_SEQ";
        private static readonly DateTime FixedNow = new(2026, 6, 27, 9, 0, 0, DateTimeKind.Utc);
        private static readonly long TxTicks = new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc).Ticks;

        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        private readonly OrderAttestationService _att;
        private readonly SecurityAssetVault _vault;
        private readonly LedgerProtectionMesh _mesh;
        private readonly IgnitionLock _ign;
        private readonly SafeIgnitionSequencer _seq;

        private sealed class FakeBroker : IBroker
        {
            private readonly bool _ok;
            public FakeBroker(bool isLive, bool placeOk) { IsLive = isLive; _ok = placeOk; }
            public string Name => "FAKE";
            public bool   IsLive { get; }
            public (bool ok, string msg) Place(PaperAsset a, string s, string n,
                PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp) => _ok ? (true, "ok") : (false, "거부");
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        public WarmRestartEngineTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"warm_{Guid.NewGuid():N}.db");
            _db  = new LocalDatabase(_dbPath);
            _att = new OrderAttestationService();
            _vault = new SecurityAssetVault(_db, _att, () => false);
            _mesh  = new LedgerProtectionMesh(_db, _att);   // Start 안 함 — 테스트가 드레인 제어
            _ign   = new IgnitionLock(s => s == Sig, _att);
            var sentinel = new SlippageSentinel { AlertThresholdSigma = 3.0 };
            _seq = new SafeIgnitionSequencer(_ign, _att, sentinel, new FakeBroker(false, true),
                       valveWired: () => true, liveArmingWired: () => true, clock: () => FixedNow)
            { ProbeSymbol = "005930", ProbeReferencePrice = 70000.0, MaxObservedSigma = 3.0 };
        }
        public void Dispose() { try { _mesh.Dispose(); } catch { } _db.Dispose(); try { File.Delete(_dbPath); } catch { } }

        private DeterministicWarmRestartEngine Engine(Func<string, bool>? verify = null, Func<long, long>? live = null)
            => new(_att, _vault, _mesh, _seq, verify, live);

        private static DbTransactionPayload Buy(long code, decimal qty, decimal price)
            => new(code, (byte)TransactionType.Buy, QQty.Of(qty).Raw, QPrice.Of(price).Raw, 0, 0, TxTicks);

        // ── §1 복구기 잠금 펜스 (주문 폭주 방지) ──────────────────────────────────

        [Fact]
        public void RecoveryLockFence_BlocksArmedPredicate_AndCanRouteToLive()
        {
            // 무장 상태를 실제로 true 로 만든 게이트(Mode Normal ∧ 점화 MasterIgnited ∧ 브로커 무장).
            var gate = new LiveArmingGate(_ign, _att, () => true);
            Assert.True(_ign.Arm(IgnitionStage.MasterIgnited, Sig));   // AllowPhysical()=true
            Assert.True(gate.IsArmed());                               // 기준: 무장됨

            bool recovering = true;
            var pred = DispatchArming.ResolvePredicate(wired: true, gate, blockWhile: () => recovering);
            Assert.False(pred());   // ★복구 중 → 무조건 false(게이트 무장 무관)
            // CanRouteToLive 도 이 술어로 무조건 false.
            Assert.False(StrategyActivationGate.CanRouteToLive(PromotionState.Active, brokerLiveArmed: true, pred));

            recovering = false;
            Assert.True(pred());    // 복구 해제 → 게이트 평가로 복귀(무장됨)
            Assert.True(StrategyActivationGate.CanRouteToLive(PromotionState.Active, true, pred));
        }

        [Fact]
        public void IsRecovering_TrueForInFlightPhases_FalseForTerminal()
        {
            var e = Engine();
            Assert.Equal(RecoveryState.Idle, e.State);
            Assert.False(e.IsRecovering);   // Idle → 잠금 없음
        }

        // ── §2 동결창 가격정합성 펜스 ─────────────────────────────────────────────

        [Fact]
        public void Fence_OffGridPrice_Freezes_StopsDrain()
        {
            _mesh.Enqueue(Buy(5930, 1m, 70000.5m));   // 격자 미정렬(0.5원 sub-tick) — 동결창 오염
            _att.ForceFreeze("사전 동결");
            var r = Engine().BeginWarmRestart("운영자");

            Assert.False(r.Succeeded);
            Assert.Equal(RecoveryState.Frozen, r.FinalState);
            Assert.Equal(GatingMode.EmergencyFreeze, _att.Mode);   // 펜스가 재동결
            Assert.Equal(0L, r.Drained);                            // 오염 항목 미영속화
        }

        [Fact]
        public void Fence_OnGrid_Drains_Escalates_Stable()
        {
            _mesh.Enqueue(Buy(5930, 10m, 70000m));    // 격자 정렬 — 정상
            _mesh.Enqueue(Buy(5930, 5m, 71000m));
            _att.ForceFreeze("사전 동결");
            var e = Engine();
            var r = e.BeginWarmRestart("운영자");

            Assert.Equal(2L, r.Drained);                    // 둘 다 소진
            Assert.Equal(0L, e.FenceViolations);            // 오염 없음
            Assert.True(r.Succeeded, $"reason={r.Reason}");
            Assert.Equal(RecoveryState.Stable, e.State);
            Assert.Equal(IgnitionStage.MasterIgnited, _ign.Stage);   // 점진 승격 완료
            Assert.Equal(GatingMode.Normal, _att.Mode);
        }

        [Fact]
        public void ProbeRefused_DualKeyOff_ReFreezes_NoNormalLeak()
        {
            // ★적대검증 #1/#2 회귀: 이중 키 미충족(기본 안전 자세 liveArmed:false)에서 BeginIgnition 이 G0 freeze:false 로 거부돼도
            //   엔진은 반드시 재동결해야 한다. ManualRelease 로 해제된 Mode=Normal 이 잔존하면 비상 동결이 조용히 사라지는 치명 누수.
            var ign2 = new IgnitionLock(s => s == Sig, _att);
            var seqNoKey = new SafeIgnitionSequencer(ign2, _att, new SlippageSentinel { AlertThresholdSigma = 3.0 },
                new FakeBroker(false, true), valveWired: () => true, liveArmingWired: () => false, clock: () => FixedNow)
            { ProbeSymbol = "005930", ProbeReferencePrice = 70000.0, MaxObservedSigma = 3.0 };
            var e = new DeterministicWarmRestartEngine(_att, _vault, _mesh, seqNoKey);

            _mesh.Enqueue(Buy(5930, 10m, 70000m));
            _att.ForceFreeze("사전 동결");
            var r = e.BeginWarmRestart("운영자");

            Assert.False(r.Succeeded);
            Assert.Equal(RecoveryState.Frozen, e.State);
            Assert.Equal(GatingMode.EmergencyFreeze, _att.Mode);   // ★재동결 — Normal 누수 아님(핵심 불변식)
        }

        [Fact]
        public void Fence_LegitFill_LiveInCoarserBand_NoFalsePositive()
        {
            // ★적대검증 #3 회귀: 저장 체결가 4,995원(tick5 밴드·정렬). 실시간 시세 5,100원(tick10 밴드) 주입.
            //   격자 밴드를 저장가 자기 밴드로 검사하므로 오탐 동결이 없어야 한다(실시간 밴드로 잡으면 미정렬로 오탐).
            var live = new System.Collections.Generic.Dictionary<long, long> { [660] = QPrice.Of(5100m).Raw };
            var e = new DeterministicWarmRestartEngine(_att, _vault, _mesh, _seq, null,
                code => live.TryGetValue(code, out var v) ? v : 0L);
            _mesh.Enqueue(Buy(660, 100m, 4995m));   // 4995 % tick5 == 0 (자기 밴드 정렬)
            _att.ForceFreeze("사전 동결");
            var r = e.BeginWarmRestart("운영자");

            Assert.Equal(0L, e.FenceViolations);   // ★오탐 동결 없음
            Assert.Equal(1L, r.Drained);
        }

        [Fact]
        public void Fence_GrossDeviationFromLive_Freezes()
        {
            // ★적대검증 #4 회귀: 저장가가 실시간 시세의 배수 초과(스케일 글리치 100×) → 시세 괴리 오염으로 재동결.
            var live = new System.Collections.Generic.Dictionary<long, long> { [5930] = QPrice.Of(70000m).Raw };
            var e = new DeterministicWarmRestartEngine(_att, _vault, _mesh, _seq, null,
                code => live.TryGetValue(code, out var v) ? v : 0L);
            _mesh.Enqueue(Buy(5930, 1m, 7000000m));   // 700만원 = 실시간 7만원의 100배(스케일 글리치)
            _att.ForceFreeze("사전 동결");
            var r = e.BeginWarmRestart("운영자");

            Assert.True(e.FenceViolations >= 1L);
            Assert.Equal(RecoveryState.Frozen, e.State);
            Assert.Equal(GatingMode.EmergencyFreeze, _att.Mode);
        }

        // ── §3 시퀀스 전이 ────────────────────────────────────────────────────────

        [Fact]
        public void BadSignature_Refused_StaysFrozen_NoDrain()
        {
            _mesh.Enqueue(Buy(5930, 10m, 70000m));
            _att.ForceFreeze("사전 동결");
            var e = Engine(verify: _ => false);   // 서명 미인가
            var r = e.BeginWarmRestart("위조서명");

            Assert.False(r.Succeeded);
            Assert.Equal(RecoveryState.Frozen, e.State);
            Assert.Equal(GatingMode.EmergencyFreeze, _att.Mode);   // 해제 안 됨
            Assert.Equal(0L, r.Drained);
            Assert.True(_mesh.QueueDepth >= 1);                     // 백로그 보존(드레인 미착수)
        }

        [Fact]
        public void NotFrozen_NoOp_Idle()
        {
            var e = Engine();   // Mode Normal(미동결)
            var r = e.BeginWarmRestart("운영자");
            Assert.False(r.Succeeded);
            Assert.Equal(RecoveryState.Idle, r.FinalState);
            Assert.Contains("동결", r.Reason);
        }

        // ── §4 단일 리더 · 텔레메트리 ─────────────────────────────────────────────

        [Fact]
        public void RecoveryDrain_RequiresStop_ThrowsWhileRunning()
        {
            _mesh.Start();
            try
            {
                Assert.Throws<InvalidOperationException>(() =>
                    _mesh.RecoveryDrain((in DbTransactionPayload _) => true));
            }
            finally { _mesh.Stop(); }
        }

        [Fact]
        public void RingTelemetry_RecoveryFields_And_Helpers()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0,
                recoveryStateCode: (long)RecoveryState.Draining, recoveryBacklog: 7,
                probeRttMs: 42, probeSlippageSigmaRaw: QPrice.Of(1.5m).Raw);
            Assert.Equal("Draining", t.RecoveryStateName);
            Assert.Equal("rec-busy", t.RecoveryStateCss);
            Assert.True(t.RecoveryActive);
            Assert.Equal(7L, t.RecoveryBacklog);
            Assert.Equal(42L, t.ProbeRttMs);
            Assert.Equal(1.5m, t.ProbeSlippageSigmaDecimal);
        }

        [Fact]
        public void Publisher_ReflectsRecoveryEngine()
        {
            var e = Engine();
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, _att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L,
                null, null, null, null, null, null, e);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.Equal((long)RecoveryState.Idle, snap.RecoveryStateCode);
        }
    }
}
