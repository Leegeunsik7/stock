// ============================================================================
// HotStrategyReloadTests — v5.3.0 무정지 전략 핫패치 게이트 & ALC 격리 그리드
//
//  §1 StrategyStateCarrier — In-Flight 등록/해제·8건 상한·통계 누적·δ=0 이월(참조 절단).
//  §2 ZombieAssemblyShield — 세대 펜스(고아 이벤트 거부)·WeakReference 수거·미수거 좀비 격리+Downgrade·슬롯 만원.
//  §3 HotStrategyReloadGate — 위임/동결 억제·fail-closed 검증·원자 스왑·세대 전진·ALC 언로드·상태 이월.
//  §4 ★적대검증 — OnQuote/세대펜스 핫패스 δ=0·언로드 중 핫패스 무결(torn-ref 부재)·미무장 시 실주문 0.
// ============================================================================
using System;
using System.Runtime.Loader;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    // 테스트용 δ=0 전략 — OnQuote 는 상수 신호 반환(할당 0).
    internal sealed class FakeStrategy : IHotStrategy
    {
        private readonly long _signal;
        public FakeStrategy(string ver, long buildTicks, long signal) { Version = ver; BuildTimestampTicks = buildTicks; _signal = signal; }
        public string Version { get; }
        public long BuildTimestampTicks { get; }
        public long OnQuote(long symbolCode, long priceRaw, long qtyRaw) => _signal;
    }

    public sealed class HotStrategyReloadTests
    {
        private const long Scale = 100_000_000L;

        // ════════════ §1 StrategyStateCarrier ════════════

        [Fact]
        public void TrackReleaseInFlight_MaintainsCount_AndSwapDeletes()
        {
            var c = new StrategyStateCarrier();
            c.TrackInFlight(11); c.TrackInFlight(22); c.TrackInFlight(33);
            Assert.Equal(3, c.InFlightCount);
            c.ReleaseInFlight(22);                       // 중간 원소 — 마지막(33)로 스왑 삭제
            Assert.Equal(2, c.InFlightCount);
            var snap = c.Snapshot();
            bool has11 = false, has33 = false, has22 = false;
            for (int i = 0; i < snap.InFlightCount; i++)
            {
                long id = snap.InFlightOrderIds[i];
                if (id == 11) has11 = true; if (id == 33) has33 = true; if (id == 22) has22 = true;
            }
            Assert.True(has11 && has33);
            Assert.False(has22);
        }

        [Fact]
        public void TrackInFlight_CapsAtEight_DropsOverflow()
        {
            var c = new StrategyStateCarrier();
            for (int i = 0; i < 8; i++) c.TrackInFlight(1000 + i);
            Assert.Equal(8, c.InFlightCount);
            Assert.Equal(0L, c.DroppedInFlight);
            c.TrackInFlight(9999);                        // 9번째 — 드롭
            Assert.Equal(8, c.InFlightCount);
            Assert.Equal(1L, c.DroppedInFlight);
        }

        [Fact]
        public void RecordFill_AccumulatesPnl_AndTracksMaxDrawdown()
        {
            var c = new StrategyStateCarrier();
            c.RecordFill(realizedPnlDeltaRaw: 500, equityRaw: 10_000);  // peak=10000
            c.RecordFill(realizedPnlDeltaRaw: 300, equityRaw: 12_000);  // peak=12000
            c.RecordFill(realizedPnlDeltaRaw: -400, equityRaw: 9_000);  // dd = 12000-9000 = 3000
            var snap = c.Snapshot();
            Assert.Equal(3L, snap.CumFills);
            Assert.Equal(400L, snap.RealizedPnlRaw);       // 500+300-400
            Assert.Equal(12_000L, snap.PeakEquityRaw);
            Assert.Equal(3_000L, snap.MaxDrawdownRaw);
        }

        [Fact]
        public void Handover_ReTagsGeneration_PreservesBlittableState()
        {
            // Handover 는 _nowTicks 를 2회 호출(시작·완료) — 순차 클록으로 0→250 tick(=250ns) 경과 모사.
            long[] seq = { 0, 250 };
            int idx = 0;
            var c = new StrategyStateCarrier(nowTicks: () => seq[idx < seq.Length ? idx++ : seq.Length - 1], tickFrequency: 1_000_000_000L);
            c.TrackInFlight(77);
            c.RecordFill(realizedPnlDeltaRaw: 1_234, equityRaw: 50_000);
            c.Handover(newGeneration: 7);
            Assert.Equal(1L, c.Handovers);
            Assert.Equal(250L, c.LastHandoverRttNs);
            var snap = c.Snapshot();
            Assert.Equal(7L, snap.Generation);             // 세대 재태그
            Assert.Equal(1, snap.InFlightCount);           // ★In-Flight 소유권 이월(값 보존)
            Assert.Equal(77L, snap.InFlightOrderIds[0]);
            Assert.Equal(1_234L, snap.RealizedPnlRaw);     // ★통계 이월
        }

        // ════════════ §2 ZombieAssemblyShield ════════════

        [Fact]
        public void GenerationFence_AcceptsCurrent_RejectsStaleOrphan()
        {
            var z = new ZombieAssemblyShield();
            z.Advance(5);
            Assert.True(z.AcceptEvent(5));                 // 현 세대 수용
            Assert.Equal(0L, z.OrphanEventsRejected);
            Assert.False(z.AcceptEvent(4));                // 구 세대(좀비 누출) 거부
            Assert.False(z.AcceptEvent(6));                // 불일치 거부
            Assert.Equal(2L, z.OrphanEventsRejected);
        }

        [Fact]
        public void RegisterUnload_Collected_CountsCollected_NoZombie()
        {
            var z = new ZombieAssemblyShield(graceSeconds: 5.0);
            z.RegisterUnload(generation: 1, isAlive: () => false);   // 즉시 수거됨
            z.SweepOnce();
            Assert.Equal(1L, z.CollectedCount);
            Assert.Equal(0L, z.ZombieCount);
            Assert.Equal(0, z.PendingUnloads);
        }

        [Fact]
        public void RegisterUnload_AlivePastGrace_EscalatesZombie_AndDowngrades()
        {
            long fakeNow = 0;
            var ign = new IgnitionLock();
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "valid-sig"));
            Assert.Equal(IgnitionStage.MasterIgnited, ign.Stage);

            var z = new ZombieAssemblyShield(ign, nowTicks: () => fakeNow, tickFrequency: 1_000_000_000L, graceSeconds: 5.0);
            z.RegisterUnload(generation: 3, isAlive: () => true);    // 영구 생존(참조 누출)
            z.SweepOnce();                                            // grace 내 — 아직 격리 안 함
            Assert.Equal(0L, z.ZombieCount);

            fakeNow = 6_000_000_000L;                                 // 6s 경과(grace 5s 초과)
            z.SweepOnce();
            Assert.Equal(1L, z.ZombieCount);                          // ★좀비 격리
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);        // ★MasterIgnited→ProbeReady 강등
        }

        [Fact]
        public void RegisterUnload_SlotOverflow_AllAlive_ImmediateZombieEscalation()
        {
            var z = new ZombieAssemblyShield(capacity: 4);
            for (int i = 0; i < 4; i++) z.RegisterUnload(i, () => true);   // 슬롯 만원(전부 생존)
            Assert.Equal(0L, z.ZombieCount);
            z.RegisterUnload(99, () => true);                              // 회수 불가(전부 생존) → 즉시 좀비 간주
            Assert.Equal(1L, z.ZombieCount);
        }

        [Fact]
        public void RegisterUnload_SlotOverflow_CollectedSlotsReclaimed_NoFalseZombie()
        {
            // ★적대검증(high) 회귀 — 만원이어도 이미 수거된(IsAlive==false) 슬롯이 있으면 인라인 회수 후 재사용 → 허위 좀비 격발 없음.
            long fakeNow = 0;
            var ign = new IgnitionLock();
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "sig"));
            var z = new ZombieAssemblyShield(ign, nowTicks: () => fakeNow, tickFrequency: 1_000_000_000L, capacity: 4);
            // 4슬롯 전부 '이미 수거됨'으로 등록(IsAlive=false).
            for (int i = 0; i < 4; i++) z.RegisterUnload(i, () => false);
            z.RegisterUnload(99, () => true);          // 만원이지만 수거분 회수 후 배치 → 좀비 0
            Assert.Equal(0L, z.ZombieCount);
            Assert.Equal(4L, z.CollectedCount);        // 회수된 4건
            Assert.Equal(IgnitionStage.MasterIgnited, ign.Stage);   // ★허위 Downgrade 없음
        }

        [Fact]
        public void SweepOnce_NoActivePending_IsNoOp_DoesNotForceGc()
        {
            // ★유휴 스킵 — 등록 0이면 driveGc:true 여도 무동작(수거 카운트 불변).
            var z = new ZombieAssemblyShield();
            z.SweepOnce(driveGc: true);
            Assert.Equal(0L, z.CollectedCount);
            Assert.Equal(0L, z.ZombieCount);
        }

        [Fact]
        public void SweepOnce_CollectedEntry_ReclaimsSlot_AdvancesCollected()
        {
            // ★스윕 펌프 회귀 — 수거된 등록은 스윕에서 슬롯 회수 + CollectedCount 증가(PendingUnloads→0).
            var z = new ZombieAssemblyShield();
            bool alive = true;
            z.RegisterUnload(1, () => alive);
            Assert.Equal(1, z.PendingUnloads);
            z.SweepOnce();
            Assert.Equal(1, z.PendingUnloads);      // 아직 생존 — 유지
            alive = false;                          // GC 수거 모사
            z.SweepOnce();
            Assert.Equal(0, z.PendingUnloads);      // ★슬롯 회수
            Assert.Equal(1L, z.CollectedCount);
            Assert.Equal(0L, z.ZombieCount);
        }

        // ════════════ §3 HotStrategyReloadGate ════════════

        [Fact]
        public void OnQuote_NullActive_ReturnsZero_ThenDelegatesAfterReload()
        {
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), requireProvenance: false);
            Assert.Equal(0L, gate.OnQuote(5930, 100, 10));            // 미배선 — 중립 0
            Assert.True(gate.Reload(new FakeStrategy("v1", 111, signal: 42)));
            Assert.Equal(42L, gate.OnQuote(5930, 100, 10));          // 위임
            Assert.Equal("v1", gate.ActiveVersion);
            Assert.Equal(111L, gate.ActiveBuildTicks);
            Assert.Equal(1L, gate.Generation);
        }

        [Fact]
        public void Reload_FailClosed_WhenRequireProvenanceAndNoVerifier()
        {
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), requireProvenance: true);   // 검증기 없음
            Assert.False(gate.Reload(new FakeStrategy("v1", 1, 7)));  // ★fail-closed 거부
            Assert.Equal(0L, gate.Reloads);
            Assert.Equal(1L, gate.Rejects);
            Assert.Equal(0L, gate.Generation);
            Assert.Equal("(none)", gate.ActiveVersion);
        }

        [Fact]
        public void Reload_VerifierRejects_NoSwap()
        {
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(),
                verifyProvenance: _ => false, requireProvenance: true);
            Assert.False(gate.Reload(new FakeStrategy("bad", 1, 7)));
            Assert.Equal(1L, gate.Rejects);
            Assert.Equal("(none)", gate.ActiveVersion);
        }

        [Fact]
        public void Reload_FrozenAttestation_Rejects()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("test");
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(),
                requireProvenance: false, attestation: att);
            Assert.False(gate.Reload(new FakeStrategy("v1", 1, 7)));   // 동결 시 로드 거부
            Assert.Equal(0L, gate.OnQuote(5930, 100, 10));            // 동결 시 신호 억제(중립)
        }

        [Fact]
        public void Reload_AdvancesShieldGeneration_OrphansOldEvents()
        {
            var shield = new ZombieAssemblyShield();
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), shield, requireProvenance: false);
            Assert.True(gate.Reload(new FakeStrategy("v1", 1, 7)));    // gen→1
            Assert.True(shield.AcceptEvent(1));                        // 현 세대 이벤트 수용
            Assert.True(gate.Reload(new FakeStrategy("v2", 2, 8)));    // gen→2
            Assert.False(shield.AcceptEvent(1));                       // ★구 세대(v1) 잔류 이벤트 고아 거부
            Assert.Equal(1L, shield.OrphanEventsRejected);
        }

        [Fact]
        public void Reload_UnloadsOldAlc_AndHandsOverState()
        {
            var carrier = new StrategyStateCarrier();
            var gate = new HotStrategyReloadGate(carrier, requireProvenance: false);
            var alc1 = new AssemblyLoadContext("t1", isCollectible: true);
            var alc2 = new AssemblyLoadContext("t2", isCollectible: true);
            Assert.True(gate.Reload(new FakeStrategy("v1", 1, 7), alc1));   // 구 ALC 없음 → 언로드 0
            Assert.Equal(0L, gate.Unloads);
            Assert.True(gate.Reload(new FakeStrategy("v2", 2, 8), alc2));   // 구 alc1 언로드
            Assert.Equal(1L, gate.Unloads);
            Assert.Equal(2L, carrier.Handovers);                            // ★리로드마다 상태 이월
        }

        [Fact]
        public void LiveArmed_HotSwappedStrategySignal_NoRealOrderWhenUnarmed()
        {
            // ★핫스왑된 전략이 강한 매수 신호(+대량)를 산출해도, 무장(이중키) 아니면 실 송출 0바이트.
            var att = new OrderAttestationService();
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), requireProvenance: false, attestation: att);
            gate.Reload(new FakeStrategy("aggressive", 1, signal: 100_000 * Scale));  // +대량 매수 신호
            long sig = gate.OnQuote(5930, 100 * Scale, 10 * Scale);
            Assert.True(sig > 0);   // 전략은 매수 신호를 냄

            // 그 신호를 주문 링에 실어도, 미무장 GatedOrderDispatcher 가 전량 차단.
            var ring = new OrderDispatchRing(64);
            ring.Publish(genomeId: 0, symbolCode: 5930, side: OrderSide.Buy, kind: 1,
                         price: new QPrice(100 * Scale), qty: new QQty(sig), timestampTicks: 0);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => false, att);   // ★미무장
            bool sent = false;
            disp.Drain(_ => sent = true);
            Assert.False(sent);                     // ★실 송출 0(콜백 미호출)
            Assert.Equal(0L, disp.Forwarded);
            Assert.True(disp.GatedOut >= 1);        // 전량 게이팅 차단
        }

        // ── 적대검증 회귀: LoadCollectible fail-closed 정리(누수 없음) ──
        [Fact]
        public void LoadCollectible_FileVerifyFails_RejectsBeforeLoad()
        {
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), requireProvenance: false);
            bool ok = gate.LoadCollectible("anything.dll", "T", _ => false);   // ★파일 서명 실패 → 로드 전 거부
            Assert.False(ok);
            Assert.Equal(1L, gate.Rejects);
            Assert.Equal("(none)", gate.ActiveVersion);
        }

        [Fact]
        public void LoadCollectible_TypeNotFound_UnloadsAndRejects_NoActiveChange()
        {
            // ★high 회귀: 실 어셈블리를 수거 ALC 로 로드했으나 타입 부재 → ALC 언로드 + 거부(누수·크래시 없음). 활성 전략 불변.
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), requireProvenance: false);
            string realAsm = typeof(HotStrategyReloadTests).Assembly.Location;
            bool ok = gate.LoadCollectible(realAsm, "No.Such.Strategy.Type", _ => true);
            Assert.False(ok);
            Assert.Equal(1L, gate.Rejects);
            Assert.Equal("(none)", gate.ActiveVersion);   // 미채택
            Assert.Equal(0L, gate.Generation);
        }

        // ── 적대검증 회귀: RecordFill 첫 체결 음수 순자산 시딩 ──
        [Fact]
        public void RecordFill_FirstEquityNegative_NoPhantomDrawdown()
        {
            var c = new StrategyStateCarrier();
            c.RecordFill(realizedPnlDeltaRaw: -50_000, equityRaw: -100_000);   // 첫 체결·음수 순자산(마진 소진)
            var snap = c.Snapshot();
            Assert.Equal(-100_000L, snap.PeakEquityRaw);   // ★고점이 실측치로 시딩(유령 0 아님)
            Assert.Equal(0L, snap.MaxDrawdownRaw);         // ★허위 낙폭 없음(교정 전엔 100_000 오발)
        }
    }

    // ★타이밍·δ=0 스트레스는 직렬화(다른 동시성 테스트와 코어 경합 격리).
    [Collection("ConcurrencyStress")]
    public sealed class HotStrategyReloadConcurrencyTests
    {
        [Fact]
        public void HotPath_OnQuoteAndFence_ZeroAllocation()
        {
            // ★활성 전략 위임(가상 호출)+세대 펜스 비교가 초당 수만 회전해도 힙 0(δ=0).
            var shield = new ZombieAssemblyShield();
            shield.Advance(1);
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), shield, requireProvenance: false);
            gate.Reload(new FakeStrategy("v1", 1, signal: 5));
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    long sink = 0;
                    for (int i = 0; i < 300_000; i++) { sink += gate.OnQuote(5930, 100, 10); if (shield.AcceptEvent(1)) sink++; }
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 100_000; i++)
                    {
                        sink += gate.OnQuote(5930, 100, 10);
                        if (shield.AcceptEvent(1)) sink++;
                        if (sink == long.MinValue) throw new Exception("sink");
                    }
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) when (e.Message != "sink") { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★핫패스 힙 0
        }

        [Fact]
        public void HotPathDuringUnload_NoTornReference_NoCrash()
        {
            // ★적대검증: 언로드(리로드)가 진행되는 동안 OnQuote 핫패스가 회전해도 torn-ref/크래시 없이 항상 유효 신호.
            var shield = new ZombieAssemblyShield(capacity: 128);
            var gate = new HotStrategyReloadGate(new StrategyStateCarrier(), shield, requireProvenance: false);
            gate.Reload(new FakeStrategy("v0", 0, signal: 0));

            const int reloadCount = 40;
            long observed = 0; Exception? readerErr = null; bool bad = false;
            int stop = 0;
            var reader = new Thread(() =>
            {
                try
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        long sig = gate.OnQuote(5930, 100, 10);
                        if (sig < 0 || sig > reloadCount) bad = true;   // torn-ref면 쓰레기값
                        observed++;
                    }
                }
                catch (Exception e) { readerErr = e; }
            });
            reader.Start();
            for (int g = 1; g <= reloadCount; g++)
                Assert.True(gate.Reload(new FakeStrategy("v" + g, g, signal: g),
                                        new AssemblyLoadContext("alc" + g, isCollectible: true)));
            Volatile.Write(ref stop, 1);
            Assert.True(reader.Join(30_000));
            Assert.Null(readerErr);
            Assert.False(bad, "torn reference — OnQuote 가 범위 밖 신호 반환");
            Assert.True(observed > 0);
            Assert.Equal("v" + reloadCount, gate.ActiveVersion);   // 최종 활성 = 마지막 스왑
            Assert.Equal((long)reloadCount + 1, gate.Reloads);     // 초기 v0 + 루프 40회
        }
    }
}
