// ============================================================================
// Stage1CanaryIgnitionSimulationTests — v12.0.1 Stage-1 Canary Ignition 적대적 검증
//   ★docs/v12.0.0_GOLDEN_MASTER_IGNITION.md §2·§3 현장 점화 수순의 지상 실증:
//     콜드 부팅 안전자세 → 물리 이중 키(ValveWired∧LiveArmingWired) 0.1% 미세 무장 →
//     cap 이내 인가 → 0.1% 1원 초과 시 0ms 원자 킬(토큰 0x00) + PoisonedSecretBuffer 중독 →
//     하드웨어 킬스위치 주입 → 재무장 전까지 100% 차단(sticky).
//   ★liveArmed:false: 물리 키 미결속 시 단 1건의 발주 인가도 없음(100% Veto).
//   검증 대상: CanaryHealthSentinel(0.1% 인가 게이트) + PoisonedSecretBuffer(94-N·API Secret 중독)
//             + HardwareKillSwitchBridge(94-M·물리 인터럽트). 통합 점화 시퀀스.
//   ※타이밍/다중스레드 스트레스 → [Collection("ConcurrencyStress")](§6).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class Stage1CanaryIgnitionSimulationTests
    {
        private readonly ITestOutputHelper _out;
        public Stage1CanaryIgnitionSimulationTests(ITestOutputHelper o) => _out = o;

        // 총자산 100억 → 0.1% = 1천만(런북 산식 = 총자산 × 0.001).
        private const long TotalAssetsRaw = 10_000_000_000L;
        private static long Cap01Pct(long total) => (long)(total * 0.001m);

        // 모의 API Secret(토스/키움) — 점화 인터락이 킬 시 0x00 중독.
        private static PoisonedSecretBuffer NewSecret() =>
            new(new byte[] { 0xA1, 0xB2, 0xC3, 0xD4, 0xE5, 0xF6, 0x07, 0x18 });

        // ── §1 콜드 부팅 안전자세(골든마스터 부팅 무결성 5축과 정합) ──────────────
        [Fact]
        public void ColdBoot_Posture_Is_Disarmed_And_Unkilled()
        {
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            using var secret = NewSecret();

            Assert.False(canary.IsArmed);        // liveArmed:false
            Assert.False(canary.IsKilled);
            Assert.False(bridge.IsKilled);
            Assert.False(bridge.Token.IsCancellationRequested);
            Assert.False(secret.IsPoisoned);
            Assert.Equal(0L, canary.TryAuthorize(1L));   // 부팅 직후 발주 인가 0
        }

        // ── §2 물리 키 미결속 → 100% Veto(발주 인가 전량 0) ───────────────────────
        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]   // 밸브만
        [InlineData(false, true)]   // 라이브무장만
        public void PhysicalKeys_NotBothWired_Vetoes_All_Orders(bool valve, bool liveArm)
        {
            var canary = new CanaryHealthSentinel();
            long cap = Cap01Pct(TotalAssetsRaw);

            Assert.False(canary.Arm(cap, valveWired: valve, liveArmingWired: liveArm));   // 한 키라도 결여 → 무장 실패
            Assert.False(canary.IsArmed);

            long authorized = 0;
            for (int i = 0; i < 5_000; i++)
                if (canary.TryAuthorize(1L) > 0) authorized++;
            Assert.Equal(0L, authorized);   // ★5,000 발주 시도 100% Veto
        }

        // ── §3 물리 이중 키 → 0.1% 미세 무장 + cap 이내 인가 ──────────────────────
        [Fact]
        public void PhysicalDualKey_Arms_MicroCap_And_Authorizes_Within()
        {
            var canary = new CanaryHealthSentinel();
            long cap = Cap01Pct(TotalAssetsRaw);   // 1천만

            Assert.True(canary.Arm(cap, valveWired: true, liveArmingWired: true));
            Assert.True(canary.IsArmed);
            Assert.Equal(cap, canary.CapRaw);
            Assert.Equal(10_000_000L, cap);        // 100억 × 0.1%

            // 미세 발주(10만 × 100 = cap) — 누적 cap 이내 전량 인가.
            long q = 100_000L;
            for (int i = 0; i < 100; i++) Assert.True(canary.TryAuthorize(q) > 0);
            Assert.True(canary.SpentRaw <= cap);
            Assert.False(canary.IsKilled);
        }

        // ── §4 0.1% 1원 초과 → 0ms 원자 킬 + 토큰 0x00 + PoisonedSecretBuffer 중독 ──
        [Fact]
        public void CapBreach_Triggers_AtomicKill_TokenZeroize_And_SecretPoison()
        {
            var canary = new CanaryHealthSentinel();
            using var secret = NewSecret();
            long cap = Cap01Pct(TotalAssetsRaw);
            Assert.True(canary.Arm(cap, true, true));

            Assert.True(canary.TryAuthorize(cap) > 0);   // 정확히 cap 까지 인가

            // ★1원 초과 → 즉시 Kill. 점화 인터락: 킬 감지 시 API Secret 중독(0x00).
            var sw = Stopwatch.StartNew();
            long token = canary.TryAuthorize(1L);
            if (canary.IsKilled) secret.PurgeAndPoison();
            sw.Stop();

            Assert.Equal(0L, token);                     // 초과분 인가 0
            Assert.True(canary.IsKilled);
            Assert.Equal(CanaryKillReason.BudgetBreach, canary.LastKillReason);
            Assert.True(sw.Elapsed.TotalMilliseconds < 50, $"kill {sw.Elapsed.TotalMilliseconds}ms");   // ~0ms 원자

            // ★API Secret 0x00 소거 + 중독 → 뒤늦은 재참조 차단.
            Assert.True(secret.IsPoisoned && secret.AllZero());
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));

            // sticky: 재무장 전까지 100% 차단.
            for (int i = 0; i < 2_000; i++) Assert.Equal(0L, canary.TryAuthorize(1L));
        }

        // ── §5 점화 중 하드웨어 킬스위치 주입 → 카나리 Disarm + Secret 중독 + 토큰 취소 ──
        [Fact]
        public void HardwareKillSwitch_During_Ignition_Disarms_And_Poisons()
        {
            var canary = new CanaryHealthSentinel();
            var bridge = new HardwareKillSwitchBridge();
            using var secret = NewSecret();
            // 런북 §3 배선: 킬스위치 → 카나리 Kill + API Secret 중독.
            bridge.Killed += _ => { canary.Kill(CanaryKillReason.Manual); secret.PurgeAndPoison(); };

            long cap = Cap01Pct(TotalAssetsRaw);
            Assert.True(canary.Arm(cap, true, true));
            Assert.True(canary.TryAuthorize(cap / 100) > 0);   // 점화 진행 중(미세 발주 인가)

            var sw = Stopwatch.StartNew();
            bridge.Signal(KillSwitchReason.HardwareButton);
            sw.Stop();

            Assert.True(bridge.IsKilled);
            Assert.True(bridge.Token.IsCancellationRequested);   // 진행 발주 토큰 취소
            Assert.True(canary.IsKilled);
            Assert.True(secret.IsPoisoned && secret.AllZero());
            Assert.Throws<ObjectDisposedException>(() => secret.ReadByte(0));
            Assert.True(sw.Elapsed.TotalMilliseconds < 50);

            for (int i = 0; i < 1_000; i++) Assert.Equal(0L, canary.TryAuthorize(1L));   // 발주 0

            // ★재무장은 물리 이중 키만(소프트웨어 단독 복구 불가).
            Assert.False(bridge.TryRearm(valveWired: true, liveArmingWired: false));
            Assert.True(bridge.TryRearm(valveWired: true, liveArmingWired: true));
        }
    }

    // ── §6 적대검증(동시 발주 race — 0.1% cap 누출 0) ───────────────────────────────
    [Collection("ConcurrencyStress")]
    public sealed class Stage1CanaryIgnitionConcurrencyTests
    {
        private const long TotalAssetsRaw = 10_000_000_000L;
        private static long Cap01Pct(long total) => (long)(total * 0.001m);

        [Fact]
        public void Concurrent_MicroOrders_Never_Exceed_MicroCap()
        {
            var canary = new CanaryHealthSentinel();
            long cap = Cap01Pct(TotalAssetsRaw);   // 1천만
            Assert.True(canary.Arm(cap, true, true));

            const int threads = 12;
            const long q = 50_000L;                // 미세 발주 단위(50k) — cap/q = 200
            long authorized = 0;
            Exception? err = null;
            var start = new ManualResetEventSlim(false);
            var ths = new Thread[threads];
            for (int t = 0; t < threads; t++)
            {
                ths[t] = new Thread(() =>
                {
                    try
                    {
                        start.Wait();
                        for (int i = 0; i < 100; i++)
                            if (canary.TryAuthorize(q) > 0) Interlocked.Increment(ref authorized);
                    }
                    catch (Exception e) { err = e; }
                }) { IsBackground = true };
            }
            foreach (var th in ths) th.Start();
            start.Set();
            foreach (var th in ths) Assert.True(th.Join(30_000));
            Assert.Null(err);

            // ★락프리 CAS 누적 → 인가된 누적 명목액이 cap 을 절대 초과하지 않음(누출 0).
            Assert.True(canary.SpentRaw <= cap, $"spent {canary.SpentRaw} > cap {cap}");
            Assert.Equal(Interlocked.Read(ref authorized) * q, canary.SpentRaw);   // 인가 건수×단위 = 실지출(정합)
            // 총 시도(1200×50k=6천만)가 cap(1천만)을 초과하므로 초과 시점 Kill.
            Assert.True(canary.IsKilled);
            for (int i = 0; i < 500; i++) Assert.Equal(0L, canary.TryAuthorize(1L));   // 킬 후 발주 0
        }
    }
}
