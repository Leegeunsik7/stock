// ============================================================================
// GenesisPreFlightTests — v7.4.0 실전 점화 전 정교성 점검 패브릭 적대적 검증(100% 단언)
//   🛡️ GenesisPreFlightCertifier: 4항목 채점·fail-closed veto·포렌식·δ=0
//   ⏳ AtomicArmingLease: 임대 만료 자동 하향·가역·δ=0(주입 시계)
//   💀 DeadMansSwitchWatchdog: 행 감지 트립·fail-closed·δ=0
//   🔥 FatFingerVelocityInterlock(Think Beyond): 가격 괴리 + 폭주 명목속도·δ=0
//   🔒 liveArmed:false: fat-finger veto → onSend 미호출 물리 격리 + blockWhile tighten-only
// ============================================================================
using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class GenesisPreFlightTests
    {
        private const long Freq = 10_000_000L;   // 주입 QPC 주파수(1s = 10M ticks·100ns)

        // 전 항목 적격 입력.
        private static PreFlightInputs AllPass() => new(
            ipcWired: true, rttMicros: 800, rateHeadroomPermille: 600,
            cpuSupported: true, pCorePinned: true, thermalSafeHold: false, effFreqPermille: 980,
            marginHeadroomRaw: 5_000_000_000L, varLimitBreached: false, covGuardState: 0,
            diskFreeMB: 10_000, ioStatusCode: 0, pageFaultHold: false);

        // ══ 🛡️ GenesisPreFlightCertifier ═══════════════════════════════════
        [Fact]
        public void Certifier_AllItemsPass_Score100_Certified_NoVeto()
        {
            var c = new GenesisPreFlightCertifier();
            Assert.True(c.VetoActive);          // ★미인증 기본 = fail-closed
            long s = c.Certify(AllPass());
            Assert.Equal(100L, s);
            Assert.True(c.IsCertified);
            Assert.False(c.VetoActive);
            Assert.Equal(0L, c.ForensicCode);
        }

        [Fact]
        public void Certifier_AnySingleItemFail_Veto_ForensicBitSet()
        {
            var c = new GenesisPreFlightCertifier();
            // IPC 다운 → 항목① 부적격(F_IPC_DOWN + RTT 도 IPC 의존이라 동반).
            var x = new PreFlightInputs(false, 800, 600, true, true, false, 980, 5_000_000_000L, false, 0, 10_000, 0, false);
            long s = c.Certify(x);
            Assert.True(s < 100L);
            Assert.True(c.VetoActive);
            Assert.False(c.IsCertified);
            Assert.True((c.ForensicCode & GenesisPreFlightCertifier.F_IPC_DOWN) != 0);

            // VaR 밸브 격발 → 항목③ 부적격.
            var y = AllPassBut(varLimitBreached: true);
            c.Certify(y);
            Assert.True(c.VetoActive);
            Assert.True((c.ForensicCode & GenesisPreFlightCertifier.F_VAR_BREACH) != 0);
            Assert.Equal(90L, c.Score);   // W_VAR=10 만 감점

            // 디스크 부족 → 항목④.
            c.Certify(AllPassBut(diskFreeMB: 10));
            Assert.True((c.ForensicCode & GenesisPreFlightCertifier.F_DISK_LOW) != 0);
            Assert.True(c.VetoActive);

            // 서멀 홀드 → 항목②.
            c.Certify(AllPassBut(thermalSafeHold: true));
            Assert.True((c.ForensicCode & GenesisPreFlightCertifier.F_THERMAL_HOLD) != 0);

            // 공분산 퓨즈 → 항목③.
            c.Certify(AllPassBut(covGuardState: 2));
            Assert.True((c.ForensicCode & GenesisPreFlightCertifier.F_COV_FUSE) != 0);

            // 전 항목 복원 → 재인증(가역).
            c.Certify(AllPass());
            Assert.True(c.IsCertified);
            Assert.False(c.VetoActive);
        }

        [Fact]
        public void Certifier_Certify_ZeroAllocation()
        {
            var c = new GenesisPreFlightCertifier();
            var x = AllPass();
            for (int i = 0; i < 10_000; i++) c.Certify(x);   // 워밍업
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) c.Certify(x);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★순수 함수 채점 핫패스 힙 0
        }

        // ══ ⏳ AtomicArmingLease ════════════════════════════════════════════
        [Fact]
        public void Lease_Dormant_Renew_Expire_Recover()
        {
            long now = 0;
            var lease = new AtomicArmingLease(leaseSeconds: 300, nowTicks: () => now, tickFrequency: Freq);
            // dormant — 개시 전엔 물지 않음(fail-safe).
            Assert.False(lease.Expired);
            Assert.False(lease.IsActive);
            Assert.Equal(0L, lease.RemainingSeconds);

            lease.Renew();
            Assert.True(lease.IsActive);
            Assert.False(lease.Expired);
            Assert.InRange(lease.RemainingSeconds, 299L, 300L);
            Assert.Equal(1000L, lease.RemainingPermille);

            now += 299 * Freq;                 // 임대 내
            Assert.False(lease.Expired);
            now += 2 * Freq;                   // 301s — 임대 초과
            Assert.True(lease.Expired);        // ★자동 만료
            Assert.False(lease.IsActive);
            Assert.Equal(0L, lease.RemainingSeconds);

            lease.Renew();                     // 재인증 → 복원(가역)
            Assert.False(lease.Expired);
            Assert.True(lease.IsActive);

            lease.Disarm();                    // 해제 → dormant
            Assert.False(lease.Expired);
            Assert.False(lease.IsActive);
        }

        [Fact]
        public void Lease_ZeroAllocation()
        {
            long now = 0;
            var lease = new AtomicArmingLease(300, () => now, Freq);
            lease.Renew();
            for (int i = 0; i < 10_000; i++) { _ = lease.Expired; _ = lease.RemainingSeconds; }
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { lease.Renew(); _ = lease.Expired; _ = lease.IsActive; _ = lease.RemainingSeconds; _ = lease.RemainingPermille; }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());
        }

        // ══ 💀 DeadMansSwitchWatchdog ══════════════════════════════════════
        [Fact]
        public void Watchdog_StaleBeat_Armed_Trips_And_Invokes()
        {
            long now = 0; bool tripped = false;
            var wd = new DeadMansSwitchWatchdog(isArmed: () => true, onDeadManTrip: () => tripped = true,
                                                hangThresholdMs: 500, nowTicks: () => now, tickFrequency: Freq);
            wd.Beat();                          // 심박(now=0)
            now = 400 * Freq / 1000;            // 400ms — 임계 이내
            wd.CheckOnce();
            Assert.False(wd.Tripped);
            Assert.False(tripped);

            now = 600 * Freq / 1000;            // 600ms > 500ms 임계
            wd.CheckOnce();
            Assert.True(wd.Tripped);            // ★행 감지 트립
            Assert.True(tripped);               // cancel-all + ForceFreeze 액션 격발
            Assert.Equal(1L, wd.TripCount);

            now = 700 * Freq / 1000; wd.CheckOnce();   // 1회 래치 — 재격발 없음
            Assert.Equal(1L, wd.TripCount);
        }

        [Fact]
        public void Watchdog_NotArmed_NoTrip_And_IsArmedThrows_FailClosed()
        {
            long now = 0;
            // 미무장 → 심박 끊겨도 트립 안 함.
            var wd1 = new DeadMansSwitchWatchdog(() => false, () => { }, 500, nowTicks: () => now, tickFrequency: Freq);
            wd1.Beat(); now = 5_000 * Freq / 1000; wd1.CheckOnce();
            Assert.False(wd1.Tripped);

            // isArmed throw → 무장 간주(fail-closed) → 심박 끊기면 트립.
            long now2 = 0; bool tripped = false;
            var wd2 = new DeadMansSwitchWatchdog(() => throw new InvalidOperationException(), () => tripped = true, 500, nowTicks: () => now2, tickFrequency: Freq);
            wd2.Beat(); now2 = 800 * Freq / 1000; wd2.CheckOnce();
            Assert.True(wd2.Tripped);
            Assert.True(tripped);
        }

        [Fact]
        public void Watchdog_Beat_CheckOnce_ZeroAllocation()
        {
            long now = 0;
            var wd = new DeadMansSwitchWatchdog(() => true, () => { }, 500, nowTicks: () => now, tickFrequency: Freq);
            for (int i = 0; i < 10_000; i++) { wd.Beat(); wd.CheckOnce(); }   // 워밍업(심박 신선 → 무트립)
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { wd.Beat(); wd.CheckOnce(); _ = wd.HeartbeatAgeMs; }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★심박/판정 핫패스 힙 0
        }

        [Fact]
        public void Watchdog_BackgroundThread_Trips_NoCrash()
        {
            bool tripped = false;
            using var wd = new DeadMansSwitchWatchdog(() => true, () => tripped = true, hangThresholdMs: 100, pollMs: 20);
            wd.Beat();
            wd.Start();
            // 심박을 넣지 않으면 100ms 후 행 판정 → 트립(실 시계).
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!wd.Tripped && sw.ElapsedMilliseconds < 3000) Thread.Sleep(20);
            Assert.True(wd.Tripped);
            Assert.True(tripped);
        }

        // ══ 🔥 FatFingerVelocityInterlock (Think Beyond) ════════════════════
        private static OrderProposal Order(long sym, OrderSide side, decimal price, decimal qty, long ts, byte kind = 1)
            => new(ts, 0, sym, side, kind, QPrice.Of(price), QQty.Of(qty), ts);

        [Fact]
        public void FatFinger_PriceDeviation_WithinAdmits_ExceedsVetoes()
        {
            Func<long, long> refFn = _ => QPrice.Of(100m).Raw;
            var il = new FatFingerVelocityInterlock(maxDeviationPermille: 100, refPriceRaw: refFn, nowTicks: () => 100L * Freq, tickFrequency: Freq);
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 105m, 1m, 0)));   // 5% = 50‰ < 100‰
            Assert.False(il.Frozen);
            Assert.False(il.Admit(Order(5930, OrderSide.Buy, 120m, 1m, 0)));  // 20% = 200‰ ≥ 100‰ → veto
            Assert.True(il.Frozen);
            Assert.Equal(1L, il.PriceVetoes);
            Assert.True((il.LastForensic & FatFingerVelocityInterlock.V_PRICE_DEVIATION) != 0);
        }

        [Fact]
        public void FatFinger_NotionalBudget_IndividuallyValidStorm_CollectivelyTrips()
        {
            // ★Knight Capital 데모: 개별 주문은 전부 가격 괴리 0(적격)이나, 집합 누적 명목이 예산을 넘어 폭주로 차단.
            Func<long, long> refFn = _ => QPrice.Of(100m).Raw;
            long one = (QPrice.Of(100m) * QQty.Of(10m)).Raw;   // 단일 주문 명목(공개 연산자 — QMath internal 회피)
            var il = new FatFingerVelocityInterlock(maxDeviationPermille: 100, windowSeconds: 1,
                windowNotionalBudgetRaw: one * 3L + one / 2L,   // 3.5 주문분 예산
                maxSameSideBurst: 0, refPriceRaw: refFn, nowTicks: () => 100L * Freq, tickFrequency: Freq);
            // 동일 시각·동일 가격(괴리 0) 4연발 — 개별 유효.
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 10m, 0)));   // 누적 1
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 10m, 0)));   // 누적 2
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 10m, 0)));   // 누적 3 (≤3.5)
            Assert.False(il.Admit(Order(5930, OrderSide.Buy, 100m, 10m, 0)));  // 누적 4 > 3.5 → ★폭주 차단
            Assert.True(il.Frozen);
            Assert.Equal(1L, il.VelocityVetoes);
            Assert.Equal(0L, il.PriceVetoes);   // 가격은 전부 적격 — 집합 폭주만으로 차단
            Assert.True((il.LastForensic & FatFingerVelocityInterlock.V_NOTIONAL_BUDGET) != 0);
        }

        [Fact]
        public void FatFinger_SameSideBurst_And_WindowExpiryRecovers()
        {
            long now = 100L * Freq;   // 양의 단조 기준시계(미기록 슬롯 Ticks=0 과 구분)
            var il = new FatFingerVelocityInterlock(maxDeviationPermille: 1000, windowSeconds: 1,
                windowNotionalBudgetRaw: 0, maxSameSideBurst: 3, refPriceRaw: _ => 0L,
                nowTicks: () => now, tickFrequency: Freq);
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 1m, now)));   // 1
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 1m, now)));   // 2
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 1m, now)));   // 3
            Assert.False(il.Admit(Order(5930, OrderSide.Buy, 100m, 1m, now)));  // 4 > 3 → 버스트 차단
            Assert.True((il.LastForensic & FatFingerVelocityInterlock.V_SAMESIDE_BURST) != 0);

            il.Reset();
            Assert.False(il.Frozen);
            // 윈도 밖으로 전진(단조) → 이전 이력 소멸 → 다시 3연발 허용.
            now += 5 * Freq;
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 1m, now)));
            now += Freq / 10;
            Assert.True(il.Admit(Order(5930, OrderSide.Buy, 100m, 1m, now)));
        }

        [Fact]
        public void FatFinger_FrozenLatch_And_Admit_ZeroAllocation()
        {
            Func<long, long> refFn = _ => QPrice.Of(100m).Raw;
            // 항상 허용 구성(예산/버스트 비활성·가격 적격) → 전체 링/스캔 경로 δ=0 측정.
            var il = new FatFingerVelocityInterlock(maxDeviationPermille: 1000, windowSeconds: 1,
                windowNotionalBudgetRaw: 0, maxSameSideBurst: 0, refPriceRaw: refFn, nowTicks: () => 100L * Freq, tickFrequency: Freq);
            var ord = Order(5930, OrderSide.Buy, 100m, 1m, 0);
            for (int i = 0; i < 10_000; i++) il.Admit(ord);   // 워밍업
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) il.Admit(ord);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★Admit(링 기록+윈도 스캔) 핫패스 힙 0
            Assert.False(il.Frozen);

            // Frozen 래치 — 1회 veto 후 전량 차단.
            var il2 = new FatFingerVelocityInterlock(maxDeviationPermille: 100, refPriceRaw: refFn, nowTicks: () => 100L * Freq, tickFrequency: Freq);
            Assert.False(il2.Admit(Order(5930, OrderSide.Buy, 500m, 1m, 0)));   // 400% → veto·Frozen
            Assert.False(il2.Admit(Order(5930, OrderSide.Buy, 100m, 1m, 0)));   // 적격이어도 Frozen → 차단
        }

        [Fact]
        public void FatFinger_MarketOrderNoRef_BudgetAxis_FailClosed()
        {
            // ★적대검증 확정결함(HIGH) 회귀: 시장가(PriceRaw=0)+기준가 미상 주문이 명목속도 예산 축을 우회하지 못함(fail-closed).
            //   교정 전엔 명목=0 취급으로 무한 시장가 스톰 허용 → 교정 후 예산 축 활성 시 '무한 명목'으로 즉시 차단.
            long one = (QPrice.Of(100m) * QQty.Of(10m)).Raw;
            var il = new FatFingerVelocityInterlock(maxDeviationPermille: 1000, windowSeconds: 1,
                windowNotionalBudgetRaw: one * 10L, maxSameSideBurst: 0, refPriceRaw: null,
                nowTicks: () => 100L * Freq, tickFrequency: Freq);
            var mkt = Order(5930, OrderSide.Buy, 0m, 10m, 100L * Freq, kind: 0);   // 시장가·기준가 미상
            Assert.False(il.Admit(mkt));   // ★우회 불가 — 미상 명목 시장가 즉시 차단(fail-closed)
            Assert.True(il.Frozen);
            Assert.True((il.LastForensic & FatFingerVelocityInterlock.V_NOTIONAL_BUDGET) != 0);

            // 예산 축 비활성(budget=0)이면 시장가 명목=0 취급 → 무해 통과(opt-out 존중).
            var il2 = new FatFingerVelocityInterlock(maxDeviationPermille: 1000, windowSeconds: 1,
                windowNotionalBudgetRaw: 0, maxSameSideBurst: 0, refPriceRaw: null,
                nowTicks: () => 100L * Freq, tickFrequency: Freq);
            Assert.True(il2.Admit(Order(5930, OrderSide.Buy, 0m, 10m, 100L * Freq, kind: 0)));
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_FatFingerVeto_OnSendNeverCalled_AtChokePoint()
        {
            // ★실 무장(isArmed=true)이어도 fat-finger veto 면 onSend(실 발주 경계)가 단 1회도 호출되지 않음을 물리 입증.
            var att  = new OrderAttestationService();
            var ring = new OrderDispatchRing(8);
            var il   = new FatFingerVelocityInterlock(maxDeviationPermille: 100, refPriceRaw: _ => QPrice.Of(100m).Raw,
                                                      nowTicks: () => 100L * Freq, tickFrequency: Freq);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => true, attestation: att, fatFinger: il);
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, kind: 1, QPrice.Of(300m), QQty.Of(1m), 100);  // 200% 괴리

            int forwarded = 0;
            disp.Drain(_ => forwarded++);
            Assert.Equal(0, forwarded);                              // ★onSend 미호출 — 실물 발주 0비트
            Assert.Equal(0L, disp.Forwarded);
            Assert.True(il.Frozen);
            Assert.True(disp.SendErrors >= 1);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);      // fail-closed 동결 착지
        }

        [Fact]
        public void Isolation_BlockWhileTighteners_ForceNotArmed_TightenOnly()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(s => s == "SIG");
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "SIG"));
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.True(gate.IsArmed());                             // 기준: 무장

            // Pre-Flight veto / lease 만료 / deadman / fat-finger 를 대표하는 blockWhile.
            bool veto = false;
            var pred = DispatchArming.ResolvePredicate(wired: true, gate, blockWhile: () => veto);
            Assert.True(pred());                                     // veto 없음 → 무장
            veto = true;
            Assert.False(pred());                                    // ★tightener → 무장 거둠(부여 불가)
            veto = false;
            Assert.True(pred());                                     // 해소 → 복원(가역)

            // Key B 미배선 = 수학적 격리(무장 무관 항상 false).
            var unwired = DispatchArming.ResolvePredicate(wired: false, gate);
            Assert.False(unwired());
        }

        // ── helper: 한 항목만 바꾼 입력 ──
        private static PreFlightInputs AllPassBut(bool? varLimitBreached = null, long? diskFreeMB = null,
            bool? thermalSafeHold = null, int? covGuardState = null)
        {
            var b = AllPass();
            return new PreFlightInputs(
                b.IpcWired, b.RttMicros, b.RateHeadroomPermille,
                b.CpuSupported, b.PCorePinned, thermalSafeHold ?? b.ThermalSafeHold, b.EffFreqPermille,
                b.MarginHeadroomRaw, varLimitBreached ?? b.VarLimitBreached, covGuardState ?? b.CovGuardState,
                diskFreeMB ?? b.DiskFreeMB, b.IoStatusCode, b.PageFaultHold);
        }
    }
}
