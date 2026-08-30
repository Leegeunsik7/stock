// ============================================================================
// Phase114CanarySentinelTests — v11.4.0 CanaryHealthSentinel 적대적 검증(100% 단언, Think-Beyond)
//   카나리 폭발 반경(Blast Radius) 격리 엔진 지상 검증:
//     ① ★liveArmed:false: 물리 이중 키(둘 다) 없이는 무장 불가·인가 0
//     ② 한도 초과 '1회'라도 즉시 Kill(fail-closed·누출 0)
//     ③ 이상(429/지연/슬리피지) 감지 즉시 Kill·sticky 락아웃
//     ④ 경합: 다중 발주 스레드에서 cap 누출 0(락-프리 CAS)
//     ⑤ δ=0: TryAuthorize 핫패스 힙 0
// ============================================================================
using System;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase114CanarySentinelTests
    {
        // ══ ① ★liveArmed:false — 물리 이중 키 게이트 ═══════════════════════════
        [Fact]
        public void Arm_Requires_Both_Physical_Keys()
        {
            var s = new CanaryHealthSentinel();
            Assert.False(s.Arm(1000, valveWired: false, liveArmingWired: false));
            Assert.False(s.Arm(1000, valveWired: true,  liveArmingWired: false));   // 밸브만
            Assert.False(s.Arm(1000, valveWired: false, liveArmingWired: true));    // 무장키만
            Assert.False(s.IsArmed);
            Assert.Equal(0L, s.TryAuthorize(1));   // ★물리 키 없이는 단 1비트도 인가 0

            Assert.True(s.Arm(1000, valveWired: true, liveArmingWired: true));      // 둘 다 → 무장
            Assert.True(s.IsArmed);
            Assert.True(s.TryAuthorize(100) > 0);
        }

        // ══ ② 한도 초과 '1회'라도 즉시 Kill ══════════════════════════════════
        [Fact]
        public void Budget_Breach_Kills_Immediately()
        {
            var s = new CanaryHealthSentinel();
            s.Arm(1000, true, true);
            Assert.True(s.TryAuthorize(400) > 0);
            Assert.True(s.TryAuthorize(400) > 0);      // 누적 800
            Assert.Equal(800L, s.SpentRaw);
            Assert.Equal(0L, s.TryAuthorize(300));     // 800+300>1000 → 초과 → Kill+0
            Assert.True(s.IsKilled);
            Assert.False(s.IsArmed);
            Assert.Equal(CanaryKillReason.BudgetBreach, s.LastKillReason);
            Assert.True(s.SpentRaw <= s.CapRaw);        // ★누출 0(초과분 미통과)
        }

        // ══ ③ 이상 감지 즉시 Kill · sticky ═══════════════════════════════════
        [Fact]
        public void Anomaly_Kills_And_Latches_Sticky()
        {
            var s = new CanaryHealthSentinel();
            s.Arm(10_000, true, true);
            Assert.True(s.TryAuthorize(100) > 0);

            s.NoteAnomaly(CanaryKillReason.NetworkAnomaly);   // 429/단선 시뮬
            Assert.True(s.IsKilled);
            Assert.False(s.IsArmed);
            Assert.Equal(CanaryKillReason.NetworkAnomaly, s.LastKillReason);
            Assert.Equal(0L, s.TryAuthorize(1));               // sticky — 재무장 전까지 발주 0

            // 재무장(물리 키)만이 락아웃 해제
            Assert.True(s.Arm(10_000, true, true));
            Assert.False(s.IsKilled);
            Assert.True(s.TryAuthorize(1) > 0);
        }

        // ══ ④ 경합: 다중 스레드 인가에도 cap 누출 0 ══════════════════════════
        [Fact]
        public void Concurrent_Authorize_No_Budget_Leak()
        {
            var s = new CanaryHealthSentinel();
            const long unit = 10, cap = 100 * unit;
            s.Arm(cap, true, true);
            Parallel.For(0, 8, _ => { for (int i = 0; i < 200; i++) s.TryAuthorize(unit); });

            Assert.True(s.SpentRaw <= cap);            // ★경합 하에서도 한도 누출 0
            Assert.True(s.Authorized <= 100);          // 최대 100회만 인가
            Assert.True(s.IsKilled);                   // 초과 시도 → Kill
            Assert.Equal(CanaryKillReason.BudgetBreach, s.LastKillReason);
        }

        // ══ ⑤ δ=0: TryAuthorize 핫패스 힙 0 ══════════════════════════════════
        [Fact]
        public void TryAuthorize_ZeroAllocation()
        {
            var s = new CanaryHealthSentinel();
            s.Arm(long.MaxValue / 2, true, true);      // 초대형 한도 → 측정 중 Kill 없음
            for (int i = 0; i < 50_000; i++) _ = s.TryAuthorize(1);   // 워밍업
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                long acc = 0;
                for (int i = 0; i < 500_000; i++) acc += s.TryAuthorize(1);
                GC.KeepAlive(acc);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★인가 게이트 핫패스 힙 0(Interlocked/CAS)
        }

        // ══ Kill 후 Disarm 재무장 필요 ═══════════════════════════════════════
        [Fact]
        public void Manual_Kill_Blocks_Until_Rearm()
        {
            var s = new CanaryHealthSentinel();
            s.Arm(1000, true, true);
            s.Kill(CanaryKillReason.Manual);
            Assert.True(s.IsKilled);
            Assert.Equal(0L, s.TryAuthorize(1));
            Assert.Equal(1L, s.Kills);
            s.Kill(CanaryKillReason.Manual);           // 이중 Kill → 계상 1회(sticky)
            Assert.Equal(1L, s.Kills);
        }
    }
}
