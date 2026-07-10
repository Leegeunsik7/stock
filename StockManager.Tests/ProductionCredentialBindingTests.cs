// ============================================================================
// ProductionCredentialBindingTests — v4.92.0: 프로덕션 자격증명 결속 안전 인프라
//
//  §1 엔드포인트 변이 방화벽 — 화이트리스트 교차검증·off-by-one·플레이스홀더·모드 불일치·KIS drift.
//  §2 프로덕션 스왑 컨트롤러 — 게이트 통과/차단(ForceFreeze)·실전 감지 마스크·drift 게이트.
//  §3 영지식 패킷 절연 — 주문 OpCode 스캔·미무장 주문 패킷 소켓 파괴+동결+throw·정당 무장 통과.
//  §4 자격증명 δ=0 — SecretLease WithSecret 사용·주문계열 미무장 로드 fail-closed·텔레메트리.
// ============================================================================
using System;
using System.IO;
using System.Text;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ProductionCredentialBindingTests : IDisposable
    {
        private const string KisSandbox = "https://openapivts.koreainvestment.com:29443";
        private const string KisProd    = "https://openapi.koreainvestment.com:9443";

        private readonly string _dbPath;
        private readonly LocalDatabase _db;
        public ProductionCredentialBindingTests()
        {
            _dbPath = Path.Combine(Path.GetTempPath(), $"prodswap_{Guid.NewGuid():N}.db");
            _db = new LocalDatabase(_dbPath);
        }
        public void Dispose() { _db.Dispose(); try { File.Delete(_dbPath); } catch { } }

        // ── §1 엔드포인트 변이 방화벽 ──────────────────────────────────────────

        [Fact]
        public void Firewall_KisSandbox_Match_Allowed_NotProduction()
        {
            var v = EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Sandbox, KisSandbox);
            Assert.True(v.Allowed, v.Reason);
            Assert.Equal(EndpointMode.Sandbox, v.ResolvedMode);
            Assert.False(v.IsProduction);
        }

        [Fact]
        public void Firewall_KisProd_Match_Allowed_Production()
        {
            var v = EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, KisProd);
            Assert.True(v.Allowed, v.Reason);
            Assert.True(v.IsProduction);
        }

        [Fact]
        public void Firewall_OffByOneHost_Blocked()
        {
            // 1글자 어긋난 호스트(openapi → 0penapi) → 화이트리스트 불일치 차단.
            var v = EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, "https://0penapi.koreainvestment.com:9443");
            Assert.False(v.Allowed);
        }

        [Fact]
        public void Firewall_CorrectHost_WrongPort_Blocked()
        {
            // 실전 호스트 + 샌드박스 포트(29443) → 포트 변이 차단('1글자라도 어긋나면'에 포트 포함).
            var v = EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, "https://openapi.koreainvestment.com:29443");
            Assert.False(v.Allowed);
            Assert.Contains("포트", v.Reason);
        }

        [Fact]
        public void Firewall_SubdomainSuffixAttack_Blocked()
        {
            // 화이트리스트 접미 위장(...koreainvestment.com.attacker.com) → Uri.Host 전체 불일치 차단.
            var v = EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, "https://openapi.koreainvestment.com.attacker.com:9443");
            Assert.False(v.Allowed);
        }

        [Fact]
        public void Firewall_PlaceholderExample_Blocked()
        {
            var v = EndpointMutationFirewall.Validate(BrokerKind.Toss, EndpointMode.Production, "https://api.toss-invest.example/v1");
            Assert.False(v.Allowed);
            Assert.Contains("플레이스홀더", v.Reason);
        }

        [Fact]
        public void Firewall_ModeDomainMismatch_Blocked()
        {
            // 선언은 Sandbox 인데 URL 은 실전 도메인 → 변이(drift) 차단.
            var v = EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Sandbox, KisProd);
            Assert.False(v.Allowed);
            Assert.Contains("불일치", v.Reason);
        }

        [Fact]
        public void Firewall_EmptyUrl_Blocked()
        {
            Assert.False(EndpointMutationFirewall.Validate(BrokerKind.Fx, EndpointMode.Production, "").Allowed);
            Assert.False(EndpointMutationFirewall.Validate(BrokerKind.Fx, EndpointMode.Production, null).Allowed);
        }

        [Fact]
        public void Firewall_KisModeDrift_Detected()
        {
            // 시세 모의(IsPaper=true) + 주문 실전(UseRealDomain=true) → 분열(drift).
            Assert.False(EndpointMutationFirewall.KisModesConsistent(quoteIsPaper: true, orderUseRealDomain: true, out var r1));
            Assert.Contains("drift", r1);
            // 둘 다 모의(정합).
            Assert.True(EndpointMutationFirewall.KisModesConsistent(quoteIsPaper: true, orderUseRealDomain: false, out _));
            // 둘 다 실전(정합).
            Assert.True(EndpointMutationFirewall.KisModesConsistent(quoteIsPaper: false, orderUseRealDomain: true, out _));
        }

        // ── §2 프로덕션 스왑 컨트롤러 ──────────────────────────────────────────

        [Fact]
        public void Controller_GuardSandbox_Passes_NoFreeze()
        {
            var att = new OrderAttestationService();
            var c = new ProductionSwapController(att);
            Assert.True(c.GuardEndpoint(BrokerKind.KisOrder, EndpointMode.Sandbox, KisSandbox));
            Assert.Equal(GatingMode.Normal, att.Mode);
            Assert.False(c.ProductionLiveDetected);
            Assert.Equal(0L, c.FirewallBlocks);
        }

        [Fact]
        public void Controller_GuardMismatch_Freezes()
        {
            var att = new OrderAttestationService();
            var c = new ProductionSwapController(att);
            Assert.False(c.GuardEndpoint(BrokerKind.KisOrder, EndpointMode.Sandbox, KisProd));   // 모드 불일치
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(c.FirewallBlocks >= 1);
        }

        [Fact]
        public void Controller_GuardProduction_DetectsLive()
        {
            var att = new OrderAttestationService();
            var c = new ProductionSwapController(att);
            Assert.True(c.GuardEndpoint(BrokerKind.KisOrder, EndpointMode.Production, KisProd));
            Assert.True(c.ProductionLiveDetected);
            Assert.True((c.ProductionLiveMask & (1L << (int)BrokerKind.KisOrder)) != 0);
        }

        [Fact]
        public void Controller_GuardKisModes_Drift_Freezes()
        {
            var att = new OrderAttestationService();
            var c = new ProductionSwapController(att);
            Assert.False(c.GuardKisModes(quoteIsPaper: true, orderUseRealDomain: true));   // drift
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ── §3 영지식 패킷 절연 ────────────────────────────────────────────────

        [Fact]
        public void Isolator_ContainsOrderOpcode_DetectsOrder_IgnoresMarketData()
        {
            Assert.True(ZeroLeakProtocolIsolator.ContainsOrderOpcode(Utf8("axKHOpenAPI.SendOrder(...)")));
            Assert.True(ZeroLeakProtocolIsolator.ContainsOrderOpcode(Utf8("tr_id=TTTC0802U buy")));
            Assert.True(ZeroLeakProtocolIsolator.ContainsOrderOpcode(Utf8("POST /v1/orders {side:buy}")));
            // 시세/구독/오더북 패킷 — 주문 아님(오탐 없음).
            Assert.False(ZeroLeakProtocolIsolator.ContainsOrderOpcode(Utf8("{\"type\":\"subscribe\",\"symbol\":\"005930\"}")));
            Assert.False(ZeroLeakProtocolIsolator.ContainsOrderOpcode(Utf8("GET /v1/orderbook?code=005930")));   // /orderbook ≠ /orders
        }

        [Fact]
        public void Isolator_KiwoomBinaryOrderFrame_Detected_And_Unarmed_Blocked()
        {
            // ★적대검증 #1 회귀: 실제 키움 IPC 주문 프레임은 ASCII 'SendOrder' 가 없는 순수 바이너리(선두 WireType=10).
            var frame = BridgeSerializer.PackOrderRequest(new OrderRequest());
            Assert.True(ZeroLeakProtocolIsolator.ContainsOrderOpcode(frame));                     // 바이너리 OpCode 탐지
            Assert.False(ZeroLeakProtocolIsolator.ContainsOrderOpcode(BridgeSerializer.PackHeartbeat(1)));  // 하트비트(WireType=0) 미탐
            var att = new OrderAttestationService();
            bool aborted = false;
            Assert.Throws<ZeroLeakViolationException>(() =>
                ZeroLeakProtocolIsolator.AssertSendAllowed(frame, dualKeyArmed: false, IgnitionStage.Locked, att, () => aborted = true));
            Assert.True(aborted);                                  // ★미무장 바이너리 주문 프레임 → 파이프 파괴
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);    // ★영구 동결
        }

        [Fact]
        public void Firewall_PlaintextSchemeDowngrade_Blocked()
        {
            // ★적대검증 #2 회귀: 화이트리스트 host:port 로 향해도 평문(ws/http) 스킴은 거부(자격증명 평문 노출 차단).
            Assert.False(EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, "ws://openapi.koreainvestment.com:9443/").Allowed);
            Assert.False(EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, "http://openapi.koreainvestment.com:9443/").Allowed);
            Assert.True(EndpointMutationFirewall.Validate(BrokerKind.KisOrder, EndpointMode.Production, "https://openapi.koreainvestment.com:9443/").Allowed);
        }

        [Fact]
        public void Isolator_OrderSendAllowed_OnlyWhenArmedAndMasterIgnited()
        {
            Assert.True(ZeroLeakProtocolIsolator.IsOrderSendAllowed(true, IgnitionStage.MasterIgnited));
            Assert.False(ZeroLeakProtocolIsolator.IsOrderSendAllowed(false, IgnitionStage.MasterIgnited));   // 이중키 미해제
            Assert.False(ZeroLeakProtocolIsolator.IsOrderSendAllowed(true, IgnitionStage.Locked));           // FSM Locked
            Assert.False(ZeroLeakProtocolIsolator.IsOrderSendAllowed(true, IgnitionStage.ProbeReady));
        }

        [Fact]
        public void Isolator_OrderPacket_Unarmed_AbortsAndFreezesAndThrows()
        {
            var att = new OrderAttestationService();
            bool aborted = false;
            var order = Utf8("POST /v1/orders {side:buy,qty:1}");
            var ex = Assert.Throws<ZeroLeakViolationException>(() =>
                ZeroLeakProtocolIsolator.AssertSendAllowed(order, dualKeyArmed: false, IgnitionStage.Locked, att, () => aborted = true));
            Assert.True(aborted);                                  // ★소켓/파이프 강제 파괴 호출됨
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);    // ★영구 동결 수렴
            Assert.Contains("절연 위반", ex.Message);
        }

        [Fact]
        public void Isolator_OrderPacket_Armed_Passes()
        {
            var att = new OrderAttestationService();
            bool aborted = false;
            var order = Utf8("POST /v1/orders {side:buy}");
            // 이중키 ∧ MasterIgnited → 정당 무장 → 통과(throw 없음·동결 없음·abort 없음).
            ZeroLeakProtocolIsolator.AssertSendAllowed(order, dualKeyArmed: true, IgnitionStage.MasterIgnited, att, () => aborted = true);
            Assert.False(aborted);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Isolator_NonOrderPacket_Unarmed_Passes()
        {
            var att = new OrderAttestationService();
            bool aborted = false;
            var md = Utf8("{\"type\":\"subscribe\",\"symbol\":\"005930\"}");
            ZeroLeakProtocolIsolator.AssertSendAllowed(md, dualKeyArmed: false, IgnitionStage.Locked, att, () => aborted = true);
            Assert.False(aborted);                       // 비-주문 패킷은 abort 안 됨
            Assert.Equal(GatingMode.Normal, att.Mode);   // 시세/구독은 미무장이어도 무관
        }

        // ── §4 자격증명 δ=0 · 텔레메트리 ───────────────────────────────────────

        [Fact]
        public void CredentialBinder_WithSecret_RevealsAndWipes()
        {
            var att = new OrderAttestationService();
            var vault = new SecurityAssetVault(_db, att, () => false);   // 미무장
            vault.StoreString("prodswap.test", "SECRET-VALUE-123");
            // requireArmed:false(시세류 비밀) — 미무장이어도 열림. delegate 가 span 을 받아 사용, 반환 후 lease 와이프.
            var got = ProductionCredentialBinder.WithSecret(vault, "prodswap.test", requireArmed: false,
                secret => new string(secret));
            Assert.Equal("SECRET-VALUE-123", got);
        }

        [Fact]
        public void CredentialBinder_OrderCredential_Unarmed_FailClosed()
        {
            var att = new OrderAttestationService();
            var vault = new SecurityAssetVault(_db, att, () => false);   // ★미무장(liveArmed:false)
            vault.StoreString("prodswap.orderkey", "ORDER-SECRET");
            // requireArmed:true(주문계열 자격증명) + 미무장 → Reveal 이 fail-closed throw(실주문 자격증명 로드 차단).
            Assert.ThrowsAny<Exception>(() =>
                ProductionCredentialBinder.WithSecret(vault, "prodswap.orderkey", requireArmed: true, s => new string(s)));
        }

        [Fact]
        public void RingTelemetry_ProdSwapFields_And_Helpers()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0,
                kiwoomLinkCode: (long)BrokerLinkState.Authenticated,
                tossLinkCode: (long)BrokerLinkState.SessionStale,
                tossTokenRemainSec: 42,
                endpointFirewallBlocks: 0,
                productionLiveMask: 1L << (int)BrokerKind.KisOrder);
            Assert.Equal(BrokerLinkState.Authenticated, t.KiwoomLink);
            Assert.Equal("SessionStale", RingTelemetry.LinkName(t.TossLink));
            Assert.Equal(42L, t.TossTokenRemainSec);
            Assert.True(t.EndpointFirewallClean);
            Assert.True(t.ProductionLiveDetected);
            Assert.False(t.SandboxSafe);
        }

        [Fact]
        public void Publisher_ReflectsProdSwapController()
        {
            var att = new OrderAttestationService();
            var c = new ProductionSwapController(att);
            c.SetKiwoomLink(BrokerLinkState.Authenticated);
            c.GuardEndpoint(BrokerKind.KisOrder, EndpointMode.Production, KisProd);   // 실전 감지
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L,
                null, null, null, null, null, null, null, null, c);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.Equal((long)BrokerLinkState.Authenticated, snap.KiwoomLinkCode);
            Assert.True(snap.ProductionLiveDetected);
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    }
}
