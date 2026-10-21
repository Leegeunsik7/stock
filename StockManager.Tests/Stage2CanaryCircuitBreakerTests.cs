// ============================================================================
// Stage2CanaryCircuitBreakerTests — v11.4.3 Stage 2 카나리 Micro-Arming 서킷 브레이커 검증
//
//   docs/CANARY_DEPLOYMENT_RUNBOOK.md Stage 2(Canary Micro-Arming 0.1%) 규격을 결정론적으로 실증한다:
//     ★ 물리 이중 키(ValveWired ∧ LiveArmingWired) 미충족 시 Stage 2 진입 100% Veto(무장 실패·인가 0).
//     ★ 정식 무장 상태에서 인가 발주액이 총자산의 0.1% Cap 이내일 때만 허용, 1원이라도 초과 감지 시 0ms 원자 킬스위치.
//     ★ 서킷 브레이커 발동 즉시 발주 토큰 CryptographicOperations.ZeroMemory 0x00 소거 + sticky 락아웃.
//     ★ (Think-Beyond) 동시 다발 발주 race 에도 cap 초과 0 · 소거 후 잔여 토큰 재참조 전량 차단.
//
//   검증 대상: CanaryHealthSentinel(프로덕션 인가 게이트) + AtomicMicroCapAccumulator·TamperProofTokenZeroizer(신규 이중 엔진).
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Stage2CanaryCircuitBreakerTests
    {
        // 총자산 100억 → 0.1% = 1천만 (raw = 원 단위 long)
        private const long TotalAssetsRaw = 10_000_000_000L;
        private static long Cap01Pct(long total) => (long)(total * 0.001m);   // 런북 산식과 동일

        // ── §1 물리 이중 키 미충족 → Stage 2 진입 100% Veto ─────────────────────
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void PhysicalDualKey_Unmet_Stage2_Entry_100pct_Veto(bool valve, bool liveArm)
        {
            var canary = new CanaryHealthSentinel();
            long cap = Cap01Pct(TotalAssetsRaw);

            bool armed = canary.Arm(cap, valveWired: valve, liveArmingWired: liveArm);
            Assert.False(armed);                          // ★한 키라도 결여 → 무장 실패
            Assert.False(canary.IsArmed);
            for (int i = 0; i < 1_000; i++)
                Assert.Equal(0L, canary.TryAuthorize(1L));   // ★진입 발주 전량 0(100% Veto)
        }

        [Fact]
        public void PhysicalDualKey_Both_Wired_Arms_Within_Cap()
        {
            var canary = new CanaryHealthSentinel();
            long cap = Cap01Pct(TotalAssetsRaw);           // 1천만
            Assert.True(canary.Arm(cap, valveWired: true, liveArmingWired: true));
            Assert.True(canary.IsArmed);
            Assert.Equal(cap, canary.CapRaw);
            Assert.True(canary.TryAuthorize(cap / 10) > 0);   // cap 이내 → 인가(토큰>0)
        }

        // ── §2 0.1% Cap 1원 초과 → 0ms 킬스위치 + 토큰 소거 + sticky ─────────────
        [Fact]
        public void Cap_Breach_By_One_Won_Triggers_Atomic_Kill()
        {
            var canary = new CanaryHealthSentinel();
            long cap = 1_000_000L;
            Assert.True(canary.Arm(cap, true, true));

            Assert.True(canary.TryAuthorize(cap) > 0);     // 정확히 cap 까지는 인가
            Assert.False(canary.IsKilled);

            long token = canary.TryAuthorize(1L);          // ★1원 초과 → 즉시 Kill
            Assert.Equal(0L, token);
            Assert.True(canary.IsKilled);                  // 0ms 원자 킬스위치 발동
            Assert.False(canary.IsArmed);                  // 무장 해제(토큰 0x00 소거됨)
        }

        [Fact]
        public void After_CircuitBreaker_Sticky_Lockout()
        {
            var canary = new CanaryHealthSentinel();
            Assert.True(canary.Arm(1_000_000L, true, true));
            canary.TryAuthorize(2_000_000L);               // cap 초과 → Kill
            Assert.True(canary.IsKilled);

            for (int i = 0; i < 1_000; i++)
                Assert.Equal(0L, canary.TryAuthorize(1L)); // ★재무장 전까지 전량 0(sticky 락아웃)
        }

        [Fact]
        public void MicroCap_0_1pct_Computed_From_TotalAssets()
        {
            long cap = Cap01Pct(TotalAssetsRaw);
            Assert.Equal(10_000_000L, cap);                // 100억 × 0.1% = 1천만
            var canary = new CanaryHealthSentinel();
            Assert.True(canary.Arm(cap, true, true));
            Assert.True(canary.TryAuthorize(cap) > 0);     // 한도 정확히 소진
            Assert.Equal(0L, canary.TryAuthorize(1L));     // 초과 1원 → Kill
        }

        // ── §3 Think-Beyond ① 원자적 마이크로캡 누산기(동시 race 봉쇄) ──────────
        [Fact]
        public void Accumulator_Exact_Cap_Fill_Then_One_Over_Breaches()
        {
            var acc = new AtomicMicroCapAccumulator();
            acc.Arm(1_000_000L);
            for (int i = 0; i < 10; i++) Assert.True(acc.TryReserve(100_000L));   // 10×10만 = 정확히 cap
            Assert.Equal(0L, acc.RemainingRaw);
            Assert.False(acc.TryReserve(1L));              // ★1원 초과 → 봉쇄
            Assert.True(acc.Breached);
            Assert.True(acc.SpentRaw <= acc.CapRaw);       // 누적은 절대 cap 초과 안 됨
        }

        [Fact]
        public void Accumulator_Disarmed_Reserves_Zero()
        {
            var acc = new AtomicMicroCapAccumulator();     // 미무장(cap=0)
            Assert.False(acc.TryReserve(1L));              // liveArmed:false → 예약 0
            acc.Arm(0L);                                    // cap≤0 → 미무장
            Assert.False(acc.TryReserve(1L));
        }

        // ── §4 Think-Beyond ② 변조불가 토큰 소거기(재참조 차단) ─────────────────
        [Fact]
        public void Token_Valid_Before_Revoke_Invalid_After_With_Residual_Reference()
        {
            var z = new TamperProofTokenZeroizer();
            var token = z.Issue(0xDEADBEEF);               // 발급
            Assert.True(z.Validate(token));                // 발급 직후 유효
            Assert.False(z.BufferIsZero);

            // ★잔여 포인터(복사본)를 보관한 채 소거.
            var residual = token;
            z.Revoke();

            Assert.True(z.IsRevoked);
            Assert.True(z.BufferIsZero);                   // ★버퍼 0x00 소거 실증
            Assert.False(z.Validate(residual));            // ★소거 후 잔여 토큰 재참조 전량 거부
        }

        [Fact]
        public void Token_Stale_Epoch_Rejected()
        {
            var z = new TamperProofTokenZeroizer();
            var token = z.Issue(12345);
            // 손댄(위조) epoch → 거부.
            Assert.False(z.Validate(new CanaryOrderToken(token.Value, token.Epoch + 999)));
            // 위조 값 → 거부.
            Assert.False(z.Validate(new CanaryOrderToken(token.Value + 1, token.Epoch)));
        }

        [Fact]
        public void Revoked_Zeroizer_Issues_Void()
        {
            var z = new TamperProofTokenZeroizer();
            z.Revoke();
            var t = z.Issue(1);
            Assert.True(t.IsVoid);                         // fail-closed — 소거 후 발급 무효
            Assert.False(z.Validate(t));
        }

        // ── §5 융합: Cap Breach → 토큰 소거(서킷 브레이커 완결) ──────────────────
        [Fact]
        public void Fusion_CapBreach_Revokes_Token()
        {
            var acc = new AtomicMicroCapAccumulator();
            var z = new TamperProofTokenZeroizer();
            acc.Arm(1_000_000L);
            var token = z.Issue(0xABCDEF);
            Assert.True(z.Validate(token));

            // cap 초과 발주 시도 → 예약 실패 → 서킷 브레이커: 토큰 소거.
            Assert.True(acc.TryReserve(999_999L));
            if (!acc.TryReserve(2L)) z.Revoke();           // 초과 → Revoke(Kill)

            Assert.True(acc.Breached);
            Assert.True(z.IsRevoked);
            Assert.True(z.BufferIsZero);
            Assert.False(z.Validate(token));               // 소거 후 무효
        }
    }

    // ── §6 적대검증(동시 폭주 race → cap 초과 0 · δ=0) ────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Stage2CanaryCircuitBreakerConcurrencyTests
    {
        [Fact]
        public void Accumulator_Concurrent_Barrage_Never_Exceeds_Cap()
        {
            // ★동시 다발 발주 race — 다중 스레드가 총합 > cap 을 시도해도 누적은 cap 을 '단 1비트도' 넘지 않는다.
            const long cap = 1_000_000L;
            var acc = new AtomicMicroCapAccumulator();
            acc.Arm(cap);

            const int threads = 8, perThread = 100_000;
            long totalReserved = 0; Exception? err = null;
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    long mine = 0;
                    try { for (int i = 0; i < perThread; i++) if (acc.TryReserve(7L)) mine += 7L; }
                    catch (Exception e) { err = e; }
                    Interlocked.Add(ref totalReserved, mine);
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);

            Assert.True(acc.SpentRaw <= cap);                 // ★cap 초과 0(lost-update 불가)
            Assert.Equal(acc.SpentRaw, totalReserved);        // 예약 합계 = 누적(회계 무손실)
            Assert.True(acc.SpentRaw >= cap - 6);             // 거의 정확히 소진(7원 단위 잔여 ≤ 6)
            Assert.True(acc.Breached);                         // 초과 시도 발생 → breach
        }

        [Fact]
        public void Accumulator_TryReserve_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var acc = new AtomicMicroCapAccumulator();
            acc.Arm(long.MaxValue);                            // 큰 cap(예약 성공 경로 δ=0)
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) acc.TryReserve(1L);       // 워밍업
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) acc.TryReserve(1L);
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        [Fact]
        public void Zeroizer_Validate_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var z = new TamperProofTokenZeroizer();
            var token = z.Issue(777);
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) { if (!z.Validate(token)) throw new Exception("warm"); }
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) { if (!z.Validate(token)) throw new Exception("hot"); }
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        [Fact]
        public void CanaryHealthSentinel_Concurrent_Authorize_No_Cap_Leak()
        {
            // ★프로덕션 게이트도 동시 인가 race 에서 cap 누출 0(기존 불변 재확인, Stage 2 맥락).
            const long cap = 500_000L;
            var canary = new CanaryHealthSentinel();
            Assert.True(canary.Arm(cap, true, true));

            const int threads = 8, perThread = 100_000;
            long authorizedSum = 0; Exception? err = null;
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    long mine = 0;
                    try { for (int i = 0; i < perThread; i++) if (canary.TryAuthorize(3L) > 0) mine += 3L; }
                    catch (Exception e) { err = e; }
                    Interlocked.Add(ref authorizedSum, mine);
                });
            }
            foreach (var th in ths) th.Start();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.True(authorizedSum <= cap);                // ★인가 총합 ≤ cap(누출 0)
        }
    }
}
