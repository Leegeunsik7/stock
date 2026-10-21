// ============================================================================
// Stage4FullRolloutSimulationTests — v11.4.5 Stage 4 전액 전개 승격 & 롤백 검증
//
//   docs/CANARY_DEPLOYMENT_RUNBOOK.md Stage 4(Full Deployment Rollout) 규격을 결정론적으로 실증한다:
//     ★ 물리 이중 키 무장 후 0.1%→1%→5%→25%→100% 단계별 정상 승격.
//     ★ 임의 단계에서 이상 감지 시 0ms 원자 롤백 → 0.1% 안전지대 또는 0% Disarm + 승격 토큰 0x00 소거.
//     ★ (Think-Beyond) 동시 승격 race 에도 중복/오버슈트 0 · 롤백 후 잔여 권한 참조 전량 차단.
//     ★ 물리 이중 키 미충족 시 Stage 4 진입 100% Veto.
//
//   검증 대상: StepUpTierGovernor + InstantRollbackPurger.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Stage4FullRolloutSimulationTests
    {
        private const long Total = 10_000_000_000L;   // 100억
        private static (StepUpTierGovernor g, InstantRollbackPurger p) Wire()
        {
            var p = new InstantRollbackPurger();
            return (new StepUpTierGovernor(p), p);
        }

        // ── §1 물리 이중 키 미충족 → Stage 4 진입 100% Veto ─────────────────────
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void PhysicalDualKey_Unmet_Rollout_Entry_Vetoed(bool valve, bool live)
        {
            var (g, _) = Wire();
            Assert.False(g.Arm(Total, valve, live));   // ★한 키라도 결여 → 무장 실패
            Assert.False(g.IsArmed);
            Assert.Equal(-1, g.CurrentTier);
            Assert.Equal(0L, g.CapRaw);
            Assert.False(g.TryStepUp(0));               // 승격 시도 전량 거부
        }

        // ── §2 단계별 정상 승격 0.1%→1%→5%→25%→100% ────────────────────────────
        [Fact]
        public void Stepped_Escalation_Advances_Through_All_Tiers()
        {
            var (g, _) = Wire();
            Assert.True(g.Arm(Total, valveWired: true, liveArmingWired: true));
            Assert.Equal((int)RolloutTier.Micro01, g.CurrentTier);    // 0.1% 시작
            Assert.Equal(10_000_000L, g.CapRaw);                       // 100억×0.1%=1천만

            Assert.True(g.TryStepUp(0)); Assert.Equal(1, g.CurrentTier); Assert.Equal(100_000_000L, g.CapRaw);   // 1%
            Assert.True(g.TryStepUp(1)); Assert.Equal(2, g.CurrentTier); Assert.Equal(500_000_000L, g.CapRaw);   // 5%
            Assert.True(g.TryStepUp(2)); Assert.Equal(3, g.CurrentTier); Assert.Equal(2_500_000_000L, g.CapRaw); // 25%
            Assert.True(g.TryStepUp(3)); Assert.Equal(4, g.CurrentTier); Assert.Equal(10_000_000_000L, g.CapRaw);// 100%
            Assert.False(g.TryStepUp(4));                              // Full 초과 없음
        }

        [Fact]
        public void StepUp_Wrong_FromTier_Rejected()
        {
            var (g, _) = Wire();
            g.Arm(Total, true, true);        // tier 0
            Assert.False(g.TryStepUp(2));    // 현재 0 인데 2 에서 승격 시도 → CAS 실패
            Assert.Equal(0, g.CurrentTier);
        }

        // ── §3 임의 단계 롤백 → 안전지대/Disarm + 토큰 소거 ─────────────────────
        [Fact]
        public void Rollback_To_SafeZone_Returns_To_Micro01_And_Purges()
        {
            var (g, p) = Wire();
            g.Arm(Total, true, true);
            g.TryStepUp(0); g.TryStepUp(1); g.TryStepUp(2);   // 25% 도달
            Assert.Equal(3, g.CurrentTier);

            g.Rollback(toSafeZone: true);                     // ★0.1% 안전지대 복귀
            Assert.Equal(0, g.CurrentTier);
            Assert.Equal(10_000_000L, g.CapRaw);              // 0.1% 한도
            Assert.True(p.IsPurged && p.BufferIsZero);        // 승격 토큰 소거
        }

        [Fact]
        public void Rollback_Full_Disarm_Zeroes_Cap_And_Authority()
        {
            var (g, p) = Wire();
            g.Arm(Total, true, true);
            g.TryStepUp(0); g.TryStepUp(1);                   // 5%
            long epBefore = g.Epoch;

            g.Rollback();                                     // ★0% 완전 Disarm
            Assert.False(g.IsArmed);
            Assert.Equal(-1, g.CurrentTier);
            Assert.Equal(0L, g.CapRaw);
            Assert.True(g.Epoch > epBefore);                  // epoch bump
            Assert.True(p.IsPurged && p.BufferIsZero);        // ★권한 0x00 소거
            Assert.False(g.TryStepUp(0));                     // Disarm 후 승격 0
        }

        // ── §4 잔여 권한 재참조 차단 ────────────────────────────────────────────
        [Fact]
        public void Purge_Blocks_Residual_Authority_Reference()
        {
            var (g, p) = Wire();
            g.Arm(Total, true, true);
            long grantedEpoch = g.Epoch;                      // 부여된 권한 토큰(= epoch)
            Assert.True(p.ValidAuthority(0, grantedEpoch));   // 무장 중 유효

            g.Rollback();                                     // 롤백 → 소거

            Assert.False(p.ValidAuthority(0, grantedEpoch));  // ★잔여 토큰 재참조 전량 거부
            Assert.True(p.BufferIsZero);
        }
    }

    // ── §5 적대검증(동시 승격 race → 중복/오버슈트 0 · δ=0) ────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Stage4FullRolloutConcurrencyTests
    {
        private const long Total = 10_000_000_000L;

        [Fact]
        public void Concurrent_StepUp_No_Duplicate_No_Overshoot()
        {
            // ★다중 스레드가 같은 단계에서 동시에 승격을 시도해도 CAS 로 정확히 1단계만 전진(중복/오버슈트 불가).
            var p = new InstantRollbackPurger();
            var g = new StepUpTierGovernor(p);
            g.Arm(Total, true, true);   // tier 0

            const int threads = 16;
            int successes = 0; Exception? err = null;
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    try { if (g.TryStepUp(0)) Interlocked.Increment(ref successes); }
                    catch (Exception e) { err = e; }
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);

            Assert.Equal(1, successes);            // ★정확히 1개 스레드만 성공(중복 승격 0)
            Assert.Equal(1, g.CurrentTier);        // 단 1단계만 전진(오버슈트 0)
        }

        [Fact]
        public void Full_Escalation_Race_Never_Exceeds_Full()
        {
            // 여러 스레드가 각 단계 승격을 마구 시도해도 최종 단계는 Full(4)을 절대 넘지 않는다.
            var p = new InstantRollbackPurger();
            var g = new StepUpTierGovernor(p);
            g.Arm(Total, true, true);

            const int threads = 12, perThread = 100_000;
            Exception? err = null;
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    try { for (int i = 0; i < perThread; i++) g.TryStepUp(g.CurrentTier); }
                    catch (Exception e) { err = e; }
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);

            Assert.Equal((int)RolloutTier.Full, g.CurrentTier);   // 정확히 Full 도달
            Assert.Equal(Total, g.CapRaw);                         // 100% 한도(오버슈트 0)
        }

        [Fact]
        public void Cap_AlwaysMatchesTier_UnderStepUpRollbackDisarmRace()
        {
            // [QA · 실결함 회귀] 단계 CAS 와 한도 기록이 분리돼 있던 시절: 늦게 깬 승격 스레드가 이전 단계 한도로 덮어 'Full 인데 50%'(실측) ·
            //   롤백과 겹치면 '안전지대/Disarm 인데 높은 한도'(fail-open)가 남았다. 이제 한도 = 단계의 순수 함수 → 정지 후 항상 정합.
            long[] ppm = { 1_000, 10_000, 50_000, 250_000, 1_000_000 };
            for (int round = 0; round < 20; round++)
            {
                var g = new StepUpTierGovernor(new InstantRollbackPurger());
                g.Arm(Total, true, true);
                using var go = new ManualResetEventSlim(false);
                var ths = new Thread[8];
                for (int t = 0; t < ths.Length; t++)
                {
                    int id = t;
                    ths[t] = new Thread(() =>
                    {
                        go.Wait();
                        for (int i = 0; i < 20_000; i++)
                        {
                            if (id == 0 && (i & 255) == 0) g.Rollback(toSafeZone: true);
                            else g.TryStepUp(g.CurrentTier);
                            long tier = g.CurrentTier, cap = g.CapRaw;
                            if (tier >= 0 && cap > Total * ppm[4] / 1_000_000) throw new InvalidOperationException("한도 > 100%");
                        }
                        if (id == 1) g.Rollback(toSafeZone: false);
                    });
                    ths[t].Start();
                }
                go.Set();
                foreach (var th in ths) Assert.True(th.Join(30_000));
                int final = g.CurrentTier;
                Assert.Equal(final < 0 ? 0 : Total * ppm[final] / 1_000_000, g.CapRaw);   // ★정지 후 한도 = 단계 산출(완전 Disarm = 0)
            }
            var d = new StepUpTierGovernor(new InstantRollbackPurger());
            d.Arm(Total, true, true);
            d.TryStepUp(0); d.TryStepUp(1); d.TryStepUp(2);
            d.Rollback(toSafeZone: true);
            Assert.Equal((0, Total * 1_000 / 1_000_000), (d.CurrentTier, d.CapRaw));
            d.Rollback(toSafeZone: false);
            Assert.Equal((-1, 0L), (d.CurrentTier, d.CapRaw));
        }

        [Fact]
        public void StepUp_And_ValidAuthority_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var p = new InstantRollbackPurger();
            var g = new StepUpTierGovernor(p);
            g.Arm(Total, true, true);
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { g.TryStepUp(4); _ = p.ValidAuthority(0, 1); }   // Full-초과 경로(무동작)+검증
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) { g.TryStepUp(4); _ = p.ValidAuthority(0, 1); }
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
