// ============================================================================
// GenesisIgnitionTests — v10.0.0 Genesis Live Ignition Gateway 적대적 검증(100% 단언)
//   🔧 LiveAccountingMesh 레이스: IsFrozen ⟹ att.Mode==EmergencyFreeze(고부하 herd 결정론)
//   🚀 GenesisLiveIgnitionGateway: 이중키∧방어선∧펄스·1회성 개통·veto 코드·δ=0
//   🔐 SealedEmissionInterlock(Think Beyond): exactly-once·페이로드 봉인·δ=0
//   📜 IgnitionAuditTrailRecorder: δ=0 링·µs+해시·무결성·랩
//   💓 HardwareKeyPulseHeartbeatSentinel: 펄스 생존·단선 래치·dormant 분별·주입 시계
//   🔒 liveArmed:false: 이중 물리 키 미결속 시 강한 사출도 실물 0비트
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class GenesisIgnitionTests
    {
        private const long Freq = 10_000_000L;
        private static bool SpinUntil(Func<bool> cond, int ms = 5000)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(2); }
            return cond();
        }

        // ══ 🔧 LiveAccountingMesh 레이스 회귀(Task 0) ═══════════════════════
        [Fact]
        public async Task Freeze_IsFrozen_Implies_AttEmergencyFreeze_HighLoadHerd()
        {
            // ★핀포인트 교정 실증: 고부하 16스레드 herd 하에서도 IsFrozen==true 관측 시 att.Mode==EmergencyFreeze 를
            //   반드시 동반(publish-after-effect·단발 청구). 반복으로 순서 역전 레이스 봉쇄를 스트레스.
            for (int iter = 0; iter < 12; iter++)
            {
                var att = new OrderAttestationService();
                var mesh = new LiveAccountingMesh(seedKrwRaw: QMoney.Of(1_000_000m).Raw, attestation: att);
                mesh.Start();
                using (var barrier = new Barrier(16))
                {
                    var tasks = new Task[16];
                    for (int i = 0; i < 16; i++)
                        tasks[i] = Task.Run(() =>
                        {
                            barrier.SignalAndWait();
                            // 보유 0 인데 매도 → 유령 예수금 봉쇄 Freeze(회계 스레드).
                            mesh.OnFill(5930, OrderSide.Sell, AssetKind.Domestic, QPrice.Of(100m).Raw, QQty.Of(1000m).Raw, 1000 + i);
                        });
                    await Task.WhenAll(tasks);
                }
                Assert.True(SpinUntil(() => mesh.IsFrozen));
                Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // ★IsFrozen ⟹ att.Mode(레이스 봉쇄)
                mesh.Dispose();
            }
        }

        // ══ 🔐 SealedEmissionInterlock (Think Beyond) ═══════════════════════
        [Fact]
        public void Sealed_ExactlyOnce_And_PayloadIntegrity()
        {
            var s = new SealedEmissionInterlock(capacity: 256);
            int sent = 0;
            long lastSeal = 0L;
            SealedSend cap = (in SealedOrderToken t) => { sent++; lastSeal = t.Seal; };

            Assert.True(s.TrySealAndEmit(1, 5930, 0, 1, 1000, 500, cap));   // 최초 사출
            Assert.Equal(1, sent);
            Assert.Equal(1L, s.SealedEmissions);
            // ★같은 orderId 재사출 → exactly-once 봉쇄(이중 사출 0%).
            Assert.False(s.TrySealAndEmit(1, 5930, 0, 1, 1000, 500, cap));
            Assert.Equal(1, sent);                                         // onSend 미재호출
            Assert.Equal(1L, s.DoubleDispatchBlocks);
            // 다른 orderId → 정상 사출.
            Assert.True(s.TrySealAndEmit(2, 5930, 1, 0, 2000, 300, cap));
            Assert.Equal(2, sent);

            // ★페이로드 봉인 무결성: 봉인된 토큰의 seal 은 필드와 일치, 변조 시 검증 실패.
            long seal = SealedEmissionInterlock.ComputeSeal(2, 5930, 1, 0, 2000, 300);
            Assert.Equal(seal, lastSeal);
            var good = new SealedOrderToken(2, 5930, 1, 0, 2000, 300, seal);
            Assert.True(SealedEmissionInterlock.VerifySeal(in good));
            var tampered = new SealedOrderToken(2, 5930, 1, 0, 9999, 300, seal);   // 가격 변조
            Assert.False(SealedEmissionInterlock.VerifySeal(in tampered));         // ★torn/변조 탐지
        }

        [Fact]
        public void Sealed_ZeroAllocation()
        {
            var s = new SealedEmissionInterlock(capacity: 1024);
            SealedSend noop = (in SealedOrderToken t) => { };
            for (int i = 0; i < 10_000; i++) s.TrySealAndEmit(i + 1, 5930, 0, 0, 1000, 100, noop);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) s.TrySealAndEmit(i + 100_000, 5930, 0, 0, 1000, 100, noop); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★봉인 사출 핫패스 힙 0
        }

        // ══ 📜 IgnitionAuditTrailRecorder ═══════════════════════════════════
        [Fact]
        public void Audit_Records_Micros_Hash_Integrity_And_Wrap()
        {
            long clock = 5_000_000L;   // 1e7 tick/s → 0.5s → 500_000µs
            var rec = new IgnitionAuditTrailRecorder(capacity: 16, nowTicks: () => clock, tickFrequency: Freq);
            rec.Record(IgnitionEventKind.ArmingChanged, 1L);
            rec.Record(IgnitionEventKind.IgnitionOpened, 1L);
            rec.Record(IgnitionEventKind.Emission, 42L, 5930L);
            Assert.Equal(3L, rec.TotalRecorded);
            Assert.Equal((long)IgnitionEventKind.Emission, rec.LastEventCode);
            Assert.True(rec.VerifyIntegrity());   // ★해시 무결성

            Span<IgnitionAuditRecord> buf = stackalloc IgnitionAuditRecord[4];
            int n = rec.SnapshotRecent(buf);
            Assert.True(n >= 3);
            Assert.Equal((long)IgnitionEventKind.Emission, buf[0].EventCode);   // 최신순
            Assert.Equal(500_000L, buf[0].TimestampMicros);                     // µs = ticks/10
            Assert.Equal(42L, buf[0].Detail0);

            // 랩(capacity 16 초과) → 최신만 유지·무결성 유지.
            for (int i = 0; i < 40; i++) rec.Record(IgnitionEventKind.Veto, i);
            Assert.Equal(43L, rec.TotalRecorded);
            Assert.Equal(16L, rec.RecordedCount);       // 링 용량 상한
            Assert.True(rec.VerifyIntegrity());
        }

        [Fact]
        public void Audit_Record_ZeroAllocation()
        {
            long clock = 1_000_000L;
            var rec = new IgnitionAuditTrailRecorder(capacity: 4096, nowTicks: () => clock, tickFrequency: Freq);
            for (int i = 0; i < 10_000; i++) rec.Record(IgnitionEventKind.IgnitionAttempt, i);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) rec.Record(IgnitionEventKind.IgnitionAttempt, i); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★감사 기록 핫패스 힙 0
        }

        // ══ 💓 HardwareKeyPulseHeartbeatSentinel ════════════════════════════
        [Fact]
        public void KeyPulse_Alive_Loss_Dormant_And_SustainedLatch()
        {
            long clock = 1_000_000_000L;
            int lost = 0;
            var kp = new HardwareKeyPulseHeartbeatSentinel(
                onKeyLost: () => lost++, pulseTimeoutMillis: 150, sustainThreshold: 3,
                nowTicks: () => clock, tickFrequency: Freq, startTimer: false);
            // dormant(미수신) → KeyAlive=false·SafeHold, 단선 카운트 없음.
            Assert.False(kp.KeyAlive);
            Assert.True(kp.SafeHoldActive);
            kp.EvaluateKeyPulseForTest();
            Assert.Equal(0L, kp.Losses);         // dormant 는 단선 아님

            // 펄스 수신 → 생존.
            kp.Pulse();
            Assert.True(kp.KeyAlive);
            Assert.False(kp.SafeHoldActive);

            // 타임아웃(150ms=1.5e6틱) 경과 → 단선(가역).
            clock += 2_000_000L;                 // 0.2s > 0.15s
            Assert.False(kp.KeyAlive);
            Assert.True(kp.SafeHoldActive);
            // 재펄스 → 복원(가역).
            kp.Pulse();
            Assert.True(kp.KeyAlive);
            Assert.True(kp.Restores >= 1);

            // 지속 단선 → 하드 래치 + onKeyLost.
            for (int i = 0; i < 3; i++) { clock += 2_000_000L; kp.EvaluateKeyPulseForTest(); }
            Assert.True(kp.Latched);
            Assert.Equal(1, lost);
            // 래치 후엔 펄스도 재무장 불가.
            kp.Pulse();
            Assert.False(kp.KeyAlive);
            Assert.True(kp.SafeHoldActive);
            kp.Dispose();
        }

        // ══ 🚀 GenesisLiveIgnitionGateway ═══════════════════════════════════
        [Fact]
        public void Gateway_DualKey_Defenses_Pulse_And_SingleShot()
        {
            var audit = new IgnitionAuditTrailRecorder(nowTicks: () => 1_000_000L, tickFrequency: Freq);
            var sealedEmit = new SealedEmissionInterlock();
            bool valve = false, arming = false, defenses = false, pulse = false;
            var gw = new GenesisLiveIgnitionGateway(
                valveWired: () => valve, liveArmingWired: () => arming, defensesPass: () => defenses,
                keyPulseAlive: () => pulse, sealedEmission: sealedEmit, audit: audit);

            Assert.False(gw.DualKeyWired);
            Assert.False(gw.IsIgnitable);
            Assert.False(gw.TryArmEmission());
            Assert.True((gw.LastVetoCode & GenesisLiveIgnitionGateway.V_VALVE) != 0L);
            Assert.True((gw.LastVetoCode & GenesisLiveIgnitionGateway.V_ARMING) != 0L);
            Assert.True((gw.LastVetoCode & GenesisLiveIgnitionGateway.V_DEFENSE) != 0L);
            Assert.True((gw.LastVetoCode & GenesisLiveIgnitionGateway.V_KEY_PULSE) != 0L);

            valve = true; arming = true;
            Assert.True(gw.DualKeyWired);
            Assert.False(gw.IsIgnitable);           // 방어선/펄스 아직
            defenses = true; pulse = true;
            Assert.True(gw.IsIgnitable);

            Assert.True(gw.TryArmEmission());       // ★1회성 개통
            Assert.True(gw.EmissionOpen);
            Assert.Equal(1L, gw.Ignitions);
            Assert.True(gw.TryArmEmission());       // 재호출 — 단발(Ignitions 불변)
            Assert.Equal(1L, gw.Ignitions);
            Assert.Equal(1L, gw.StateCode);         // armed-open
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_DualKeyUnwired_NoEmission_UntilBothKeys()
        {
            int sent = 0;
            SealedSend onSend = (in SealedOrderToken t) => { sent++; };
            var se = new SealedEmissionInterlock();

            // ★밸브 키 미결속 → veto·onSend 미호출(실물 0).
            var g1 = new GenesisLiveIgnitionGateway(() => false, () => true, () => true, () => true, se);
            Assert.False(g1.Emit(1, 5930, 0, 0, 1000, 100, onSend));
            Assert.Equal(0, sent);
            Assert.True((g1.LastVetoCode & GenesisLiveIgnitionGateway.V_VALVE) != 0L);

            // ★무장 키 미결속 → veto·실물 0.
            var g2 = new GenesisLiveIgnitionGateway(() => true, () => false, () => true, () => true, se);
            Assert.False(g2.Emit(2, 5930, 0, 0, 1000, 100, onSend));
            Assert.Equal(0, sent);

            // ★방어선 실패 → veto·실물 0.
            var g3 = new GenesisLiveIgnitionGateway(() => true, () => true, () => false, () => true, se);
            Assert.False(g3.Emit(3, 5930, 0, 0, 1000, 100, onSend));
            Assert.Equal(0, sent);

            // ★펄스 단선 → veto·실물 0.
            var g4 = new GenesisLiveIgnitionGateway(() => true, () => true, () => true, () => false, se);
            Assert.False(g4.Emit(4, 5930, 0, 0, 1000, 100, onSend));
            Assert.Equal(0, sent);

            // 이중키∧방어선∧펄스 전부 결속 → 봉인 사출(1비트).
            var g5 = new GenesisLiveIgnitionGateway(() => true, () => true, () => true, () => true, new SealedEmissionInterlock());
            Assert.True(g5.Emit(5, 5930, 0, 0, 1000, 100, onSend));
            Assert.Equal(1, sent);                  // ★전부 결속 시에만 실물 사출
            Assert.Equal(1L, g5.Emissions);
        }

        [Fact]
        public void Isolation_Gateway_ThroughDispatcher_NoForward_WhenUnwired()
        {
            // ★디스패처 드레인 경로에서도: 이중키 미결속 게이트웨이를 onSend 로 → forwarded 0.
            var att  = new OrderAttestationService();
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att);   // 디스패처는 무장(격리 검증)
            int realSent = 0;
            SealedSend realOnSend = (in SealedOrderToken t) => { realSent++; };
            var gw = new GenesisLiveIgnitionGateway(() => false, () => false, () => true,   // ★이중키 미결속
                sealedEmission: new SealedEmissionInterlock());
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)0, QPrice.Of(100m), QQty.Of(1m), 100);
            // ★Func<bool> 경로: 게이트웨이 veto → false → Dispatcher 는 Forwarded 아닌 GatedOut 계상(오계수 봉쇄).
            disp.Drain((OrderProposal p) => gw.Emit(p.OrderId, p.SymbolCode, p.SideByte, p.KindByte, p.PriceRaw, p.QtyRaw, realOnSend));
            Assert.Equal(0, realSent);              // ★실물 사출 0(이중키 미결속)
            Assert.Equal(0L, disp.Forwarded);       // ★veto 를 실 송출로 오계수하지 않음(텔레메트리 계약 사수)
            Assert.True(disp.GatedOut >= 1);        // ★하류 veto = 미송출 → GatedOut
            Assert.True(gw.Vetoes >= 1);
        }

        [Fact]
        public void Gateway_Emit_ZeroAllocation()
        {
            var audit = new IgnitionAuditTrailRecorder(capacity: 4096, nowTicks: () => 1_000_000L, tickFrequency: Freq);
            var se = new SealedEmissionInterlock(capacity: 1024);
            var gw = new GenesisLiveIgnitionGateway(() => true, () => true, () => true, () => true, se, audit);
            SealedSend noop = (in SealedOrderToken t) => { };
            for (int i = 0; i < 10_000; i++) gw.Emit(i + 1, 5930, 0, 0, 1000, 100, noop);
            long Measure() { long b = GC.GetAllocatedBytesForCurrentThread(); for (int i = 0; i < 200_000; i++) gw.Emit(i + 100_000, 5930, 0, 0, 1000, 100, noop); return GC.GetAllocatedBytesForCurrentThread() - b; }
            _ = Measure(); Assert.Equal(0L, Measure());   // ★점화 사출 핫패스 힙 0(감사·봉인 포함)
        }

        // ══ 🔗 실전 사출 경로 end-to-end(프로덕션 Dispatch→PlaceSealed→broker) ══════
        private sealed class RecordingBroker : IBroker
        {
            public int Placed;
            public PaperSide LastSide; public decimal LastQty; public decimal LastPrice;
            public string Name => "REC"; public bool IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
            { Placed++; LastSide = side; LastQty = q; LastPrice = lp; return (true, "ok"); }
            public void Cancel(string id) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;
        }

        [Fact]
        public void EndToEnd_RealDrainPath_EmitsOnlyWhenDualKeyWired()
        {
            // ★실전 프로덕션 경로 검증(복제 람다 아님): OrderDrainWorker.Dispatch → gateway.Emit → SealedEmissionInterlock → PlaceSealed → broker.Place.
            // 이중키 미결속 → 실 브로커 사출 0.
            var att  = new OrderAttestationService();
            var ring = new OrderDispatchRing(64);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att);
            var brokerOff = new RecordingBroker();
            var gwOff = new GenesisLiveIgnitionGateway(() => false, () => false, () => true, () => true, new SealedEmissionInterlock());
            using (var wOff = new OrderDrainWorker(disp, brokerOff, wired: false, gateway: gwOff))
            {
                ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, (byte)1, QPrice.Of(100m), QQty.Of(5m), 100);
                wOff.DrainOnce();
                Assert.Equal(0, brokerOff.Placed);      // ★이중키 미결속 → 실 브로커 사출 0비트(liveArmed:false 실전 경로)
                Assert.Equal(0L, disp.Forwarded);       // veto → Forwarded 오계수 없음
                Assert.True(disp.GatedOut >= 1);
            }

            // 이중키∧방어선∧펄스 전부 결속 → 실 브로커 사출 1.
            var att2  = new OrderAttestationService();
            var ring2 = new OrderDispatchRing(64);
            var disp2 = new GatedOrderDispatcher(ring2, isArmed: () => true, attestation: att2);
            var brokerOn = new RecordingBroker();
            var gwOn = new GenesisLiveIgnitionGateway(() => true, () => true, () => true, () => true, new SealedEmissionInterlock());
            using (var wOn = new OrderDrainWorker(disp2, brokerOn, wired: false, gateway: gwOn))
            {
                ring2.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Sell, (byte)1, QPrice.Of(200m), QQty.Of(3m), 200);
                wOn.DrainOnce();
                Assert.Equal(1, brokerOn.Placed);       // ★전부 결속 → 실 브로커 사출 1(봉인 경유)
                Assert.Equal(PaperSide.Sell, brokerOn.LastSide);
                Assert.Equal(1L, disp2.Forwarded);      // 실 송출 계상
                Assert.Equal(1L, gwOn.Emissions);
            }
        }
    }
}
