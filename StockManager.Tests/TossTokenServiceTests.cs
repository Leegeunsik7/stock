// ============================================================================
// TossTokenServiceTests — 1단계: 토스증권 OAuth 토큰 수명주기 적대적 검증 + 실배선 계약
//
//  §1 핫패스 fail-closed(6) — 미취득·만료(경계)·초과·무효수명(0/음수)은 CurrentAccessToken()="".
//  §2 갱신 정책(5)         — 성공 갱신·연속실패 임계 동결·성공 스트릭 리셋·취득 예외 흡수·선제 갱신.
//  §3 배선/데코레이터(4)   — bearer 공급자 반영·CreateToss 조립(래핑/비래핑)·INERT 위임 Place.
//
//  ★검증 핵심: '빈 토큰=미인증 주문 원천 차단' 및 '연속 실패 임계 → ForceFreeze(1회 래치)'가
//    실제로 동작하고, 만료 경계(==)에서 fail-open 하지 않음을 박제.
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public sealed class TossTokenServiceTests
    {
        // 주입 클럭(가변 ticks) — 결정론.
        private sealed class Clock { public long Now; }

        // 격리 페이크 인증 클라이언트 — 응답·throw 주입.
        private sealed class FakeAuth : ITossAuthClient
        {
            public Func<TossTokenResult> Responder = () => new TossTokenResult(true, "TOK", 3600);
            public int Calls;
            public TossTokenResult AcquireToken() { Calls++; return Responder(); }
        }

        // 격리 페이크 운반 — Place 위임 도달 계상.
        private sealed class FakeTransport : ITossTransport
        {
            public int PostCalls;
            public (bool ok, string body) PostOrder(string pathAndQuery, string jsonBody) { PostCalls++; return (true, "{}"); }
            public void DeleteOrder(string orderId) { }
            public decimal QueryHeldQty(PaperAsset asset, string symbol) => 0m;
            public bool TryReceive(Action<ReadOnlyMemory<byte>> onFrame, int timeoutMs) => false;
            public void ConnectAndSubscribe() { }
            public void Disconnect() { }
        }

        private const long OneHourTicks = 3600L * TimeSpan.TicksPerSecond;

        // ────────────────────────────────────────────────────────────────
        // §1 핫패스 fail-closed
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void BeforeRefresh_TokenEmpty_FailClosed()
        {
            var clock = new Clock();
            using var svc = new TossTokenService(new FakeAuth(), () => clock.Now);
            Assert.Equal("", svc.CurrentAccessToken());   // 초기 스냅샷 없음 → 미인증 원천 차단
            Assert.False(svc.HasValidToken);
        }

        [Fact]
        public void AfterRefresh_TokenValid()
        {
            var clock = new Clock { Now = 0 };
            using var svc = new TossTokenService(new FakeAuth(), () => clock.Now);
            svc.RefreshNow();
            Assert.Equal("TOK", svc.CurrentAccessToken());
            Assert.True(svc.HasValidToken);
        }

        [Fact]
        public void AtExpiryBoundary_ReturnsEmpty_NoFailOpen()
        {
            var clock = new Clock { Now = 0 };
            using var svc = new TossTokenService(new FakeAuth(), () => clock.Now);
            svc.RefreshNow();                       // 만료 = 0 + 1h
            clock.Now = OneHourTicks;               // 정확히 만료 tick
            Assert.Equal("", svc.CurrentAccessToken());   // 경계 포함(>=) → 무효
        }

        [Fact]
        public void PastExpiry_ReturnsEmpty()
        {
            var clock = new Clock { Now = 0 };
            using var svc = new TossTokenService(new FakeAuth(), () => clock.Now);
            svc.RefreshNow();
            clock.Now = OneHourTicks + 1;
            Assert.Equal("", svc.CurrentAccessToken());
        }

        [Fact]
        public void ZeroLifetime_Rejected_NoSnapshot_FailClosed()
        {
            var clock = new Clock { Now = 0 };
            var auth = new FakeAuth { Responder = () => new TossTokenResult(true, "TOK", 0) };
            using var svc = new TossTokenService(auth, () => clock.Now);
            svc.RefreshNow();                          // 수명 0 → 오염 스냅샷 금지
            Assert.Equal("", svc.CurrentAccessToken());
        }

        [Fact]
        public void NegativeLifetime_Rejected_FailClosed()
        {
            var clock = new Clock { Now = 0 };
            var auth = new FakeAuth { Responder = () => new TossTokenResult(true, "TOK", -5) };
            using var svc = new TossTokenService(auth, () => clock.Now);
            svc.RefreshNow();
            Assert.Equal("", svc.CurrentAccessToken());
        }

        // ────────────────────────────────────────────────────────────────
        // §2 갱신 정책
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void FailedAcquire_KeepsEmpty_NoFreezeBelowThreshold()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var auth = new FakeAuth { Responder = () => new TossTokenResult(false, "", 0) };
            using var svc = new TossTokenService(auth, () => clock.Now, att, maxConsecutiveFailures: 3);
            svc.RefreshNow();
            svc.RefreshNow();                         // 2회(<3) — 아직 미동결
            Assert.Equal("", svc.CurrentAccessToken());
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void ConsecutiveFailures_ReachThreshold_ForceFreeze()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var auth = new FakeAuth { Responder = () => new TossTokenResult(false, "", 0) };
            using var svc = new TossTokenService(auth, () => clock.Now, att, maxConsecutiveFailures: 3);
            svc.RefreshNow(); svc.RefreshNow(); svc.RefreshNow();   // 3회 도달
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(3L, svc.ConsecutiveFailures);
        }

        [Fact]
        public void Success_ResetsFailureStreak_PreventsFreeze()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            bool ok = false;
            var auth = new FakeAuth { Responder = () => ok ? new TossTokenResult(true, "TOK", 3600) : new TossTokenResult(false, "", 0) };
            using var svc = new TossTokenService(auth, () => clock.Now, att, maxConsecutiveFailures: 3);
            svc.RefreshNow(); svc.RefreshNow();       // 2회 실패(<3)
            ok = true; svc.RefreshNow();              // 성공 → 스트릭 리셋
            Assert.Equal(0L, svc.ConsecutiveFailures);
            ok = false; svc.RefreshNow(); svc.RefreshNow();   // 다시 2회(<3) — 리셋 덕에 미동결
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void AcquireThrows_CountedAsFailure_Isolated()
        {
            var clock = new Clock();
            var att = new OrderAttestationService();
            var auth = new FakeAuth { Responder = () => throw new InvalidOperationException("네트워크 단절 모사") };
            using var svc = new TossTokenService(auth, () => clock.Now, att, maxConsecutiveFailures: 2);
            svc.RefreshNow();                         // 예외 흡수 → 실패 계상(throw 전파 없음)
            Assert.Equal(1L, svc.ConsecutiveFailures);
            svc.RefreshNow();
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void MaybeRefresh_WithinSkew_TriggersProactiveRefresh()
        {
            var clock = new Clock { Now = 0 };
            var auth = new FakeAuth();
            using var svc = new TossTokenService(auth, () => clock.Now, refreshSkewSeconds: 60);
            svc.RefreshNow();                         // 만료 = 1h, calls=1
            Assert.Equal(1, auth.Calls);
            svc.MaybeRefresh();                       // 여유(만료-60s 전) → 재취득 안 함
            Assert.Equal(1, auth.Calls);
            clock.Now = OneHourTicks - 30 * TimeSpan.TicksPerSecond;   // skew(60s) 창 진입
            svc.MaybeRefresh();                       // 선제 갱신 발동
            Assert.Equal(2, auth.Calls);
        }

        // ────────────────────────────────────────────────────────────────
        // §3 배선 / 데코레이터 계약
        // ────────────────────────────────────────────────────────────────

        [Fact]
        public void NullArgs_Throw()
        {
            Assert.Throws<ArgumentNullException>(() => new TossTokenService(null!, () => 0L));
            Assert.Throws<ArgumentNullException>(() => new TossTokenService(new FakeAuth(), null!));
        }

        [Fact]
        public void CurrentAccessToken_AsBearerProvider_ReflectsToken()
        {
            var clock = new Clock { Now = 0 };
            using var svc = new TossTokenService(new FakeAuth(), () => clock.Now);
            Func<string> provider = svc.CurrentAccessToken;   // 메서드그룹 → Func<string>(TossHttpTransport bearer 계약)
            Assert.Equal("", provider());
            svc.RefreshNow();
            Assert.Equal("TOK", provider());
        }

        [Fact]
        public void CreateToss_NoGate_ReturnsRawTossBroker()
        {
            var ring = new OrderDispatchRing(8);
            var broker = BrokerHub.CreateToss(new FakeTransport(), ring, () => 0L);
            Assert.Equal("토스증권 실거래", broker.Name);
            Assert.True(broker.IsLive);
        }

        [Fact]
        public void CreateToss_WithInertGate_WrapsAndDelegatesPlace()
        {
            var ring = new OrderDispatchRing(8);
            var transport = new FakeTransport();
            var gate = new ShadowCutoverGate(enabled: false, new QTickSpec(QPrice.Of(1m), QQty.Of(1m)));
            var broker = BrokerHub.CreateToss(transport, ring, () => 0L, liveAttestation: null, shadowGate: gate);

            Assert.Equal("Shadow(토스증권 실거래)", broker.Name);   // ShadowClearingBroker 래핑 확인
            var (ok, _) = broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit, 1m, 70000m, 70000m);
            Assert.True(ok);                          // INERT/LegacyPrimary → inner(Toss).Place 위임
            Assert.Equal(1, transport.PostCalls);     // 실제 운반 도달
        }
    }
}
