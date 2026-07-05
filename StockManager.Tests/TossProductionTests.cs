// ============================================================================
// TossProductionTests — 과제 1: 토스 실 WSS/REST 결속의 인증 fail-closed + 조립 격리
//
//  §1 인증 fail-closed(4) — REST 401/403 판정·공백토큰 WSS 연결 거부 동결·미연결 수신 throw.
//  §2 조립 격리(4)        — 조립기 산출(래핑/비래핑)·자동 시작 없음(인그레스/토큰 정지)·bearer 반영.
//
//  ★검증 핵심: 인증 실패가 '모호한 재시도'로 새지 않고 ForceFreeze 되며, 프로덕션 조립기가
//    어떤 것도 자동 시작·무장하지 않아 실 송출 확률이 기본 0 임(liveArmed:false)을 박제.
// ============================================================================
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // v4.85.0 — ConnectThenCloseLoop 는 실 백그라운드 인그레스 스레드+백오프 sleep 을 돌려 전체 스위트 병렬 시
    //   초과구독에 취약 → ConcurrencyStress(DisableParallelization) 편입(플래키 0% 사수).
    [Collection("ConcurrencyStress")]
    public sealed class TossProductionTests
    {
        private sealed class FakeAuth : ITossAuthClient
        {
            public TossTokenResult Result = new(true, "TOK", 3600);
            public TossTokenResult AcquireToken() => Result;
        }

        // ConnectAndSubscribe 는 항상 성공하지만 TryReceive 는 즉시 throw(권한취소·구독거부로 인한 즉시 close 모사).
        private sealed class ConnectThenCloseTransport : ITossTransport
        {
            public int Connects;
            public (bool ok, string body) PostOrder(string pathAndQuery, string jsonBody) => (true, "{}");
            public void DeleteOrder(string orderId) { }
            public decimal QueryHeldQty(PaperAsset asset, string symbol) => 0m;
            public bool TryReceive(Action<ReadOnlyMemory<byte>> onFrame, int timeoutMs)
                => throw new InvalidOperationException("connect-then-close 모사(권한취소)");
            public void ConnectAndSubscribe() { Interlocked.Increment(ref Connects); }
            public void Disconnect() { }
        }

        // ── §1 인증 fail-closed ───────────────────────────────────────────────

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, true)]
        [InlineData(HttpStatusCode.Forbidden,    true)]
        [InlineData(HttpStatusCode.OK,           false)]
        [InlineData(HttpStatusCode.InternalServerError, false)]
        public void RestAuthStatus_Classification(HttpStatusCode code, bool isAuth)
            => Assert.Equal(isAuth, TossHttpTransport.IsAuthFailureStatus(code));

        [Fact]
        public void Socket_EmptyToken_RefusesConnect_Freezes()
        {
            var att = new OrderAttestationService();
            var sock = new TossProductionSocket(new Uri("wss://feed.example/ws"), () => "", att);
            // 공백 토큰 → 네트워크 접촉 전에 거부(fail-closed). 실 소켓 미생성.
            Assert.Throws<InvalidOperationException>(() => sock.ConnectAndSubscribe());
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Socket_ReceiveBeforeConnect_Throws()
        {
            var sock = new TossProductionSocket(new Uri("wss://feed.example/ws"), () => "TOK");
            Assert.Throws<InvalidOperationException>(() => sock.TryReceive(_ => { }, timeoutMs: 10));
        }

        // ── §2 조립 격리 ──────────────────────────────────────────────────────

        [Fact]
        public void Assembler_WithGate_WrapsShadow_NoAutoStart()
        {
            var ring = new OrderDispatchRing(8);
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: false, new QTickSpec(QPrice.Of(1m), QQty.Of(1m)));
            var stack = TossProductionAssembler.Build(
                "https://api.toss.example", new Uri("wss://feed.example/ws"),
                new FakeAuth(), ring, () => 0L, att, gate);

            Assert.Equal("Shadow(토스증권 실거래)", stack.Broker.Name);   // 데코레이터 래핑
            Assert.True(stack.Raw.IsLive);
            Assert.False(stack.Raw.Running);      // ★인그레스 자동 시작 안 함
            Assert.False(stack.Tokens.Running);   // ★토큰 갱신 루프 자동 시작 안 함
            Assert.Equal(0L, stack.Raw.Published); // 자동 발행 0
        }

        [Fact]
        public void Assembler_NoGate_ReturnsRawBroker()
        {
            var ring = new OrderDispatchRing(8);
            var stack = TossProductionAssembler.Build(
                "https://api.toss.example", new Uri("wss://feed.example/ws"),
                new FakeAuth(), ring, () => 0L);
            Assert.Equal("토스증권 실거래", stack.Broker.Name);
            Assert.Same(stack.Raw, stack.Broker);   // gate 없으면 raw 그대로
        }

        [Fact]
        public void Assembler_BearerReflectsTokenService()
        {
            var ring = new OrderDispatchRing(8);
            var auth = new FakeAuth();
            var stack = TossProductionAssembler.Build(
                "https://api.toss.example", new Uri("wss://feed.example/ws"),
                auth, ring, () => 0L);

            Assert.Equal("", stack.Tokens.CurrentAccessToken());   // 아직 미취득 → 공백(미인증 원천 차단)
            stack.Tokens.RefreshNow();
            Assert.Equal("TOK", stack.Tokens.CurrentAccessToken());
        }

        [Fact]
        public void Assembler_NullArgs_Throw()
        {
            var ring = new OrderDispatchRing(8);
            Assert.Throws<ArgumentNullException>(() => TossProductionAssembler.Build(
                "u", new Uri("wss://x/ws"), null!, ring, () => 0L));
            Assert.Throws<ArgumentNullException>(() => TossProductionAssembler.Build(
                "u", new Uri("wss://x/ws"), new FakeAuth(), null!, () => 0L));
        }

        // ★적대검증 교정(fail-open livelock): connect→즉시 close 반복이 재연결 무한루프가 아니라
        //   스트릭 누적 → maxReconnects 초과 → fail-closed 동결로 수렴해야 한다.
        [Fact]
        public void ConnectThenCloseLoop_ConvergesToFreeze_NotLivelock()
        {
            var ring = new OrderDispatchRing(8);
            var att = new OrderAttestationService();
            using var broker = new TossLiveBroker(new ConnectThenCloseTransport(), ring, () => 0L, att,
                                                  genomeId: 0, maxReconnects: 2);
            broker.StartIngress();
            var sw = Stopwatch.StartNew();
            while (att.Mode == GatingMode.Normal && sw.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(25);
            broker.StopIngress();

            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 무한 livelock 이 아니라 동결로 수렴
            Assert.True(broker.FrozenTrips >= 1);
        }
    }
}
