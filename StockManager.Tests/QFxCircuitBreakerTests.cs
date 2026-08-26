// ============================================================================
// QFxCircuitBreakerTests — 3단계: 실시간 환율 서킷 브레이커 적대적 검증 + FX 청산 통합
//
//  §1 경계 fail-closed(3) — 비양수 환율 trip·무표본 조회 비trip·신선 표본 가드 반환.
//  §2 급변동 3σ(3)        — 3σ 스파이크 trip·밴드 내 수용·평탄이력 σ=0 보류(보수 미발화).
//  §3 스테일(2)           — 수용 간격 초과 trip·가드 조회 시 스테일 trip.
//  §4 래치/펌프/통합(4)   — open 유지·Reset 재개·펌프 투입·guarded→KrxFxClearing 정상/차단.
//
//  ★검증 핵심: 급변동/스테일이 '모호하게 통과'하지 않고 ForceFreeze 로 닫히며, 신선·정상
//    환율만 청산 경로(KrxFxClearing)로 흐른다(fail-open 원천 차단).
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public sealed class QFxCircuitBreakerTests
    {
        private sealed class Clock { public long Now; }

        private sealed class FakeSource : IFxRateSource
        {
            public decimal? Next;
            public bool TryPoll(out decimal rate) { if (Next is { } v) { rate = v; return true; } rate = 0m; return false; }
        }

        private static readonly long TenthSec = TimeSpan.TicksPerSecond / 10;   // 0.1s (< stale 1s)

        // 창에 σ>0 이력 축적(1330±0.05 교번) — 스테일 미유발(0.1s 간격).
        private static void SeedBand(QFxCircuitBreaker b, Clock clock, int n = 12)
        {
            for (int i = 0; i < n; i++)
            {
                clock.Now += TenthSec;
                Assert.True(b.Submit(i % 2 == 0 ? 1330.05m : 1329.95m));
            }
        }

        // ────────────────────────────────────────────────────────────────
        // §1 경계 fail-closed
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void NonPositiveRate_Trips_FailClosed()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            Assert.False(b.Submit(0m));
            Assert.True(b.IsOpen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void NoSample_GuardedRate_ReturnsFalse_NoTrip()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            Assert.False(b.TryGetGuardedRate(clock.Now, out _));
            Assert.False(b.IsOpen);                          // 표본 없음은 오염 아님 — 동결 아님
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void FreshSample_GuardedRate_Returned()
        {
            var clock = new Clock { Now = 1000 };
            var b = new QFxCircuitBreaker(() => clock.Now);
            Assert.True(b.Submit(1330.50m));
            Assert.True(b.TryGetGuardedRate(clock.Now, out var r));
            Assert.Equal(1330.50m, r.ToDecimal());
        }

        // ────────────────────────────────────────────────────────────────
        // §2 급변동 3σ
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void ThreeSigmaSpike_Trips_And_GuardBlocks()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            SeedBand(b, clock);                              // 평균 1330·σ≈0.05
            clock.Now += TenthSec;
            Assert.False(b.Submit(1335.00m));               // dev 5 ≫ 3σ(0.15) → trip
            Assert.True(b.IsOpen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.False(b.TryGetGuardedRate(clock.Now, out _));   // 개방 → 가드 차단
        }

        [Fact]
        public void WithinBand_Accepted_NotOpen()
        {
            var clock = new Clock();
            var b = new QFxCircuitBreaker(() => clock.Now);
            SeedBand(b, clock);
            clock.Now += TenthSec;
            Assert.True(b.Submit(1330.06m));                // dev 0.06 < 3σ(0.15)
            Assert.False(b.IsOpen);
        }

        [Fact]
        public void FlatHistory_ThenMove_NoTrip_SigmaZeroGuard()
        {
            var clock = new Clock();
            var b = new QFxCircuitBreaker(() => clock.Now);
            for (int i = 0; i < 12; i++) { clock.Now += TenthSec; Assert.True(b.Submit(1330.00m)); }
            clock.Now += TenthSec;
            Assert.True(b.Submit(1340.00m));                // σ=0 → 판정 보류(보수 미발화)
            Assert.False(b.IsOpen);
        }

        // ────────────────────────────────────────────────────────────────
        // §3 스테일
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void StaleOnSubmit_Trips()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            clock.Now += TenthSec; Assert.True(b.Submit(1330m));
            clock.Now += TimeSpan.TicksPerSecond + 1;       // 1s 초과 gap
            Assert.False(b.Submit(1331m));
            Assert.True(b.IsOpen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void StaleOnQuery_Trips()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            clock.Now += TenthSec; Assert.True(b.Submit(1330m));
            clock.Now += TimeSpan.TicksPerSecond + 1;
            Assert.False(b.TryGetGuardedRate(clock.Now, out _));
            Assert.True(b.IsOpen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ────────────────────────────────────────────────────────────────
        // §4 래치 / 펌프 / 통합
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void OnceOpen_StaysClosed_UntilReset()
        {
            var clock = new Clock();
            var b = new QFxCircuitBreaker(() => clock.Now);   // att=null — 래치 격리 관찰
            Assert.False(b.Submit(-1m));
            Assert.True(b.IsOpen);
            clock.Now += TenthSec;
            Assert.False(b.Submit(1330m));                    // 개방 유지 — 수용 없음
            b.Reset();
            Assert.False(b.IsOpen);
            clock.Now += TenthSec;
            Assert.True(b.Submit(1330m));                     // 재개
        }

        [Fact]
        public void NullClock_Throws()
            => Assert.Throws<ArgumentNullException>(() => new QFxCircuitBreaker(null!));

        [Fact]
        public void FeedPump_PumpOnce_FeedsBreaker()
        {
            var clock = new Clock();
            var b = new QFxCircuitBreaker(() => clock.Now);
            var src = new FakeSource { Next = 1330m };
            using var pump = new QFxFeedPump(src, b);
            pump.PumpOnce();
            Assert.Equal(1L, b.Accepted);
            src.Next = null;                                  // 폴 없음(정상)
            pump.PumpOnce();
            Assert.Equal(1L, b.Accepted);                     // 미증가
        }

        [Fact]
        public void GuardedRate_FlowsToKrxFxClearing_Conservative()
        {
            var clock = new Clock { Now = 1000 };
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            Assert.True(b.Submit(1330m));
            Assert.True(b.TryGetGuardedRate(clock.Now, out var r));

            var quote  = new QFxQuote(r, r);                  // 신선 가드 환율로 호가 구성
            var bounds = QFxBounds.Of(1000m, 2000m, 10m);
            var gate   = new ShadowCutoverGate(enabled: false, BuiltInTickSpecs.BuildDefaultRegistry());

            // AAPL(USD 0.01틱·1주 스텝) 195.00 × 10 × 1330 = 2,593,500 KRW.
            Assert.True(KrxFxClearing.TryClearUsdToKrwBase(
                gate, att, "AAPL", 195.00m, 10m, OrderSide.Buy, quote, bounds, out var baseKrw));
            Assert.Equal(2_593_500m, baseKrw);
            Assert.Equal(GatingMode.Normal, att.Mode);        // 해피패스 — 동결 없음
        }

        [Fact]
        public void TrippedBreaker_BlocksClearing_FailClosed()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var b = new QFxCircuitBreaker(() => clock.Now, att);
            SeedBand(b, clock);
            clock.Now += TenthSec;
            Assert.False(b.Submit(1400m));                    // 급변동 trip
            // 청산 경로는 가드 환율을 얻지 못함 → 진입 불가(fail-closed).
            Assert.False(b.TryGetGuardedRate(clock.Now, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }
    }
}
