// ============================================================================
// ProductionCredentialBinding — v4.92.0: 프로덕션 자격증명 결속 안전 인프라
//
//  실 증권사 인프라(키움 COM·토스 WSS/REST·FX 피드)에 실 자격증명을 바인딩하기 직전, 설정 오염과 패킷 누수를
//  물리적으로 차단하는 두 방화벽 + 통합 관제 컨트롤러.
//
//  (A) EndpointMutationFirewall — 소스 하드코딩(바이너리 레벨) sandbox/real 도메인 화이트리스트와 주입 URL 을 교차
//      검증. 1글자라도 어긋나거나(off-by-one)·플레이스홀더(.example)·모드↔도메인 불일치·★KIS 시세/주문 모드 분열
//      (drift)이면 즉시 ForceFreeze 로 소켓 오픈 자체를 무력화. 설정 파일을 맹신하지 않는다.
//  (B) ZeroLeakProtocolIsolator — 최하위 송신 직전 아웃바운드 바이트(ReadOnlySpan<byte>)에서 주문 계열 OpCode/함수명을
//      스캔. 이중 키(ValveWired ∧ LiveArmingWired) 미해제 ∧ FSM≠MasterIgnited 인데 주문 패킷이 감지되면 예외를 삼키지
//      않고 소켓/파이프를 강제 Abort + 영구 ForceFreeze 로 수렴(상위 게이트웨이 버그로 인한 실주문 누수 최종 차단).
//  (C) ProductionSwapController — 방화벽·절연기·브로커 연결상태·화이트리스트 판정을 관제 텔레메트리로 통합(라이브 소스).
//
//  ── 불변식 ──────────────────────────────────────────────────────────────────
//   liveArmed:false : 실 소켓/파이프 오픈은 방화벽 통과 + 절연기 통과일 때만. 자격증명 로드도 주문계열은 무장 필요.
//   fail-closed     : 모호/불일치/누수 = OrderAttestationService.ForceFreeze 단일 소스(원자 래치). 소켓 오픈 무력화.
//   무할당(δ=0)     : 도메인 비교·OpCode 스캔은 ReadOnlySpan<char>/<byte> 위 ordinal 검색(힙 0). 비밀은 SecretLease ZeroMemory 와이프.
//   결정론          : 판정은 순수(벽시계/RNG 불사용). 한국색상은 UI 계층(실전 적색 점멸·모의 청/회).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum BrokerKind    : byte { KisQuote = 0, KisOrder = 1, Toss = 2, Fx = 3 }
    public enum EndpointMode  : byte { Sandbox = 0, Production = 1 }
    public enum BrokerLinkState : byte { Disconnected = 0, Connecting = 1, Authenticated = 2, SessionStale = 3 }

    /// <summary>엔드포인트 교차검증 판정(불변).</summary>
    public readonly record struct EndpointVerdict(bool Allowed, EndpointMode ResolvedMode, bool IsProduction, string Reason);

    /// <summary>패킷 절연 위반 — 미무장 상태 주문 패킷 누수 감지 시 던진다(삼키지 않음).</summary>
    public sealed class ZeroLeakViolationException : Exception
    {
        public ZeroLeakViolationException(string message) : base(message) { }
    }

    /// <summary>
    /// ★엔드포인트 변이 방화벽 — 소스 하드코딩 화이트리스트(바이너리 레벨) vs 주입 URL 교차검증. 순수·무할당·결정론.
    /// KIS 도메인은 실측 확정(단일 소스). Toss/FX 는 sandbox 확정 + production 은 운영자 확인 상수(플레이스홀더는 거부).
    /// </summary>
    public static class EndpointMutationFirewall
    {
        // ── 바이너리 레벨 하드코딩 화이트리스트(ReadOnlySpan<char> — 힙 0·데이터 섹션 참조) ──
        //   KIS: 실측 확정(BrokerProviders/LiveBrokers 세 곳 중복 리터럴을 단일 소스로 승격).
        private static ReadOnlySpan<char> KisSandboxHost => "openapivts.koreainvestment.com";
        private static ReadOnlySpan<char> KisProdHost    => "openapi.koreainvestment.com";
        //   Toss: sandbox 확정 · production 은 운영자 확인 대상 상수(실 도메인 확정 시 이 한 줄만 교체). '.example' 플레이스홀더는 항상 거부.
        private static ReadOnlySpan<char> TossSandboxHost => "sandbox-api.tossinvest.com";
        private static ReadOnlySpan<char> TossProdHost    => "api.tossinvest.com";
        //   FX: 별도 sandbox 없음 — production 데이터 피드 도메인만 화이트리스트(운영자 확인 상수). 플레이스홀더 거부.
        private static ReadOnlySpan<char> FxProdHost       => "fx-feed.ms2e.internal";

        /// <summary>
        /// 주입 URL 을 (broker, 선언 모드) 하드코딩 도메인과 교차검증. 호스트가 화이트리스트와 정확히(대소문자 무시 ordinal)
        /// 일치해야 통과. 플레이스홀더(.example)·빈 URL·파싱 실패·모드↔도메인 불일치는 모두 거부(Allowed=false).
        /// </summary>
        public static EndpointVerdict Validate(BrokerKind broker, EndpointMode declaredMode, string? injectedUrl)
        {
            if (string.IsNullOrWhiteSpace(injectedUrl))
                return new EndpointVerdict(false, declaredMode, false, $"{broker}: 주입 URL 없음 — 소켓 오픈 무력화");
            if (!Uri.TryCreate(injectedUrl, UriKind.Absolute, out var uri))
                return new EndpointVerdict(false, declaredMode, false, $"{broker}: URL 파싱 실패 '{injectedUrl}'");

            // ★평문/비TLS 스킴 다운그레이드 차단: 화이트리스트 host:port 로 향하더라도 http/ws 면 자격증명이 평문 노출 — 거부.
            //   허용 스킴은 TLS 계열(https REST · wss 소켓)만.
            if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals("wss",   StringComparison.OrdinalIgnoreCase))
                return new EndpointVerdict(false, declaredMode, false, $"{broker}: 비TLS 스킴 '{uri.Scheme}' — 평문 다운그레이드 변이 감지, 소켓 오픈 무력화");

            var host = uri.Host.AsSpan();
            // ★플레이스홀더 차단: '.example' 미주입 기본값이 실 소켓을 열지 못하게 한다.
            if (host.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
                host.Contains(".example.", StringComparison.OrdinalIgnoreCase))
                return new EndpointVerdict(false, declaredMode, false, $"{broker}: 플레이스홀더 도메인 '{uri.Host}' — 실 도메인 미주입, 소켓 오픈 무력화");

            ReadOnlySpan<char> sandbox, prod;
            int sandboxPort, prodPort;
            switch (broker)
            {
                // ★포트까지 정확 대조('1글자라도 어긋나면' = 포트 포함). KIS 는 sandbox 29443 / prod 9443 로 포트가 다르다.
                case BrokerKind.KisQuote:
                case BrokerKind.KisOrder: sandbox = KisSandboxHost;  sandboxPort = 29443; prod = KisProdHost;  prodPort = 9443; break;
                case BrokerKind.Toss:     sandbox = TossSandboxHost; sandboxPort = 443;   prod = TossProdHost; prodPort = 443;  break;
                case BrokerKind.Fx:       sandbox = default;         sandboxPort = -1;    prod = FxProdHost;   prodPort = 443;  break;   // FX sandbox 없음
                default: return new EndpointVerdict(false, declaredMode, false, "알 수 없는 브로커");
            }

            bool matchSandbox = !sandbox.IsEmpty && uri.Port == sandboxPort && host.Equals(sandbox, StringComparison.OrdinalIgnoreCase);
            bool matchProd    = uri.Port == prodPort && host.Equals(prod, StringComparison.OrdinalIgnoreCase);

            if (!matchSandbox && !matchProd)
            {
                // 호스트는 맞지만 포트만 어긋난 경우도 명시(변이 진단).
                bool hostOnly = (!sandbox.IsEmpty && host.Equals(sandbox, StringComparison.OrdinalIgnoreCase)) || host.Equals(prod, StringComparison.OrdinalIgnoreCase);
                return new EndpointVerdict(false, declaredMode, false,
                    $"{broker}: 권한부 '{uri.Host}:{uri.Port}' 가 화이트리스트 불일치({(hostOnly ? "포트" : "호스트")} 어긋남) — 소켓 오픈 무력화");
            }

            var resolved = matchProd ? EndpointMode.Production : EndpointMode.Sandbox;
            // ★모드↔도메인 불일치: 선언 모드와 URL 이 가리키는 실제 모드가 다르면 변이(drift) — 거부.
            if (resolved != declaredMode)
                return new EndpointVerdict(false, resolved, matchProd,
                    $"{broker}: 선언 모드({declaredMode})↔URL 모드({resolved}) 불일치 — 엔드포인트 변이 감지, 소켓 오픈 무력화");

            return new EndpointVerdict(true, resolved, matchProd, $"{broker}: {resolved} 엔드포인트 검증 통과");
        }

        /// <summary>
        /// ★KIS 시세/주문 모드 분열(drift) 교차검증. 'KIS.IsPaper'(시세)와 'Kis.UseRealDomain'(주문)은 의미가 반대·독립
        /// 저장이라 시세는 모의인데 주문은 실전인 불일치가 구조적으로 가능하다. 두 플래그가 같은 모드를 가리키지 않으면 drift.
        /// </summary>
        public static bool KisModesConsistent(bool quoteIsPaper, bool orderUseRealDomain, out string reason)
        {
            // 시세 실전 = !IsPaper. 주문 실전 = UseRealDomain. 두 모드가 일치해야 정합.
            bool quoteProd = !quoteIsPaper, orderProd = orderUseRealDomain;
            if (quoteProd != orderProd)
            {
                reason = $"KIS 모드 분열(drift): 시세={(quoteProd ? "실전" : "모의")} ↔ 주문={(orderProd ? "실전" : "모의")} 불일치";
                return false;
            }
            reason = "KIS 시세/주문 모드 정합";
            return true;
        }
    }

    /// <summary>
    /// ★영지식 패킷 절연기 — 최하위 송신 직전 아웃바운드 바이트에서 주문 계열 식별자를 스캔. 순수·무할당(span ordinal 검색).
    /// 상위 게이트웨이가 liveArmed:false 검문을 버그로 누락해도, 미무장 상태의 주문 패킷은 이 레이어에서 물리 차단된다.
    /// </summary>
    public static class ZeroLeakProtocolIsolator
    {
        // 주문 계열 시그니처(UTF8 리터럴 — 힙 0). 키움 SendOrder·KIS 주문 TR(TTTC08*/VTTC08*)·토스 주문 REST 경로.
        private static ReadOnlySpan<byte> SigKiwoomSendOrder => "SendOrder"u8;
        private static ReadOnlySpan<byte> SigKisRealOrderTr  => "TTTC08"u8;
        private static ReadOnlySpan<byte> SigKisPaperOrderTr => "VTTC08"u8;
        //   주문 REST 경로는 정확한 '/orders'(복수)만 — 바 '/order' 는 '/orderbook'(시세) 오탐 위험이라 제외(과차단 방지).
        private static ReadOnlySpan<byte> SigTossOrderPath   => "/orders"u8;

        /// <summary>
        /// 아웃바운드 패킷에 주문 계열 식별자가 있는지 스캔(δ=0). 두 프로토콜 계열 모두 커버:
        ///  · ★바이너리(키움 IPC): 프레임 선두 int32 WireType == BridgeMessageType.OrderRequest(10). 주문 OpCode 는 '숫자'라
        ///    ASCII 스캔으로는 못 잡는다(적대검증 교정) — 헤더를 직접 판정. PackOrderRequest 가 이 WireType 을 선두에 쓴다.
        ///  · 텍스트(KIS REST tr_id·Toss REST 경로·COM 메서드명): ASCII 시그니처 span 검색.
        /// </summary>
        public static bool ContainsOrderOpcode(ReadOnlySpan<byte> packet)
        {
            // 바이너리 IPC 주문 프레임 — 선두 4B WireType(LE) == OrderRequest(=10). (BridgeSerializer.Pack 규약과 동일 판정.)
            if (packet.Length >= 4 && BitConverter.ToInt32(packet) == (int)BridgeMessageType.OrderRequest)
                return true;
            return packet.IndexOf(SigKiwoomSendOrder) >= 0
                || packet.IndexOf(SigKisRealOrderTr)  >= 0
                || packet.IndexOf(SigKisPaperOrderTr) >= 0
                || packet.IndexOf(SigTossOrderPath)   >= 0;
        }

        /// <summary>실 무장 상태 = 이중 키(ValveWired ∧ LiveArmingWired) ∧ 점화 FSM MasterIgnited. 이때만 주문 패킷 송신 허용.</summary>
        public static bool IsOrderSendAllowed(bool dualKeyArmed, IgnitionStage stage)
            => dualKeyArmed && stage == IgnitionStage.MasterIgnited;

        /// <summary>
        /// 송신 직전 검문. 주문 패킷인데 미무장이면 abort(소켓/파이프 강제 파괴) + ForceFreeze(영구 동결) 후
        /// ZeroLeakViolationException 을 던진다(★예외 삼킴 금지). 주문 아님 또는 정당 무장이면 조용히 통과.
        /// </summary>
        public static void AssertSendAllowed(ReadOnlySpan<byte> packet, bool dualKeyArmed, IgnitionStage stage,
                                             OrderAttestationService? attestation, Action? abort = null)
        {
            if (!ContainsOrderOpcode(packet)) return;                 // 비-주문 패킷(시세/구독/핑) — 무관, 통과
            if (IsOrderSendAllowed(dualKeyArmed, stage)) return;      // 정당 무장(이중키 ∧ MasterIgnited) — 통과

            // ★미무장 상태 주문 패킷 누수 — 소켓/파이프 강제 파괴 + 영구 동결 + throw(삼킴 금지).
            try { abort?.Invoke(); } catch { /* 파괴 실패해도 동결·throw 는 진행 */ }
            string reason = $"영지식 패킷 절연 위반 — 미무장(이중키={dualKeyArmed}, FSM={stage}) 상태 주문 패킷 송신 시도 차단·소켓 파괴·영구 동결";
            try { attestation?.ForceFreeze(reason); } catch { }
            throw new ZeroLeakViolationException(reason);
        }
    }

    /// <summary>
    /// 프로덕션 스왑 통합 컨트롤러(DI 싱글톤·라이브 텔레메트리 소스). 방화벽 통과 시에만 소켓 오픈을 허용하고,
    /// 브로커 연결상태·토큰 잔여수명·실전 엔드포인트 감지를 관제 채널에 노출한다. 라이브 발주 경로 무접촉(순수 관측+게이트).
    /// </summary>
    public sealed class ProductionSwapController
    {
        private readonly OrderAttestationService _att;
        private int  _kiwoomLink, _tossLink;                 // BrokerLinkState (volatile int)
        private long _tossTokenRemainSec;
        private long _firewallPassMask, _productionLiveMask; // BrokerKind 비트마스크
        private long _firewallBlocks;

        public ProductionSwapController(OrderAttestationService attestation)
            => _att = attestation ?? throw new ArgumentNullException(nameof(attestation));

        // ── 관제 독출(락-프리) ─────────────────────────────────────────────────
        public BrokerLinkState KiwoomLink => (BrokerLinkState)Volatile.Read(ref _kiwoomLink);
        public BrokerLinkState TossLink   => (BrokerLinkState)Volatile.Read(ref _tossLink);
        public long   TossTokenRemainSec  => Interlocked.Read(ref _tossTokenRemainSec);
        public long   FirewallPassMask    => Interlocked.Read(ref _firewallPassMask);
        public long   ProductionLiveMask  => Interlocked.Read(ref _productionLiveMask);
        public long   FirewallBlocks      => Interlocked.Read(ref _firewallBlocks);
        public bool   ProductionLiveDetected => ProductionLiveMask != 0;
        public bool   AllGuardedPassed        => FirewallBlocks == 0;

        public void SetKiwoomLink(BrokerLinkState s) => Volatile.Write(ref _kiwoomLink, (int)s);
        public void SetTossLink(BrokerLinkState s)   => Volatile.Write(ref _tossLink, (int)s);
        public void SetTossTokenRemainSec(long sec)  => Interlocked.Exchange(ref _tossTokenRemainSec, sec < 0 ? 0 : sec);

        /// <summary>
        /// 소켓 오픈 직전 방화벽 게이트. 통과하면 true(오픈 허용) + 실전 감지 마스크 갱신. 실패하면 ForceFreeze + false(오픈 무력화).
        /// </summary>
        public bool GuardEndpoint(BrokerKind broker, EndpointMode declaredMode, string? injectedUrl)
        {
            var v = EndpointMutationFirewall.Validate(broker, declaredMode, injectedUrl);
            if (!v.Allowed)
            {
                Interlocked.Increment(ref _firewallBlocks);
                try { _att.ForceFreeze("엔드포인트 변이 방화벽 — " + v.Reason); } catch { }
                return false;
            }
            InterlockedOr(ref _firewallPassMask, 1L << (int)broker);
            if (v.IsProduction) InterlockedOr(ref _productionLiveMask, 1L << (int)broker);
            return true;
        }

        /// <summary>KIS 시세/주문 모드 분열(drift) 게이트 — 불일치면 ForceFreeze + false.</summary>
        public bool GuardKisModes(bool quoteIsPaper, bool orderUseRealDomain)
        {
            if (!EndpointMutationFirewall.KisModesConsistent(quoteIsPaper, orderUseRealDomain, out var reason))
            {
                Interlocked.Increment(ref _firewallBlocks);
                try { _att.ForceFreeze("엔드포인트 변이 방화벽 — " + reason); } catch { }
                return false;
            }
            return true;
        }

        private static void InterlockedOr(ref long target, long value)
        {
            long init, computed;
            do { init = Volatile.Read(ref target); computed = init | value; }
            while (Interlocked.CompareExchange(ref target, computed, init) != init);
        }
    }

    /// <summary>
    /// 자격증명 δ=0 결속 헬퍼 — SecurityAssetVault.Reveal 로 SecretLease 를 열어 delegate 에 ReadOnlySpan&lt;char&gt; 로 넘기고,
    /// 사용 직후 using 스코프에서 Dispose(ZeroMemory 와이프 + ArrayPool 반환)한다. 비밀 문자열을 힙에 영속시키지 않는다.
    /// </summary>
    public static class ProductionCredentialBinder
    {
        public delegate T SecretUse<out T>(ReadOnlySpan<char> secret);

        /// <summary>금고에서 비밀을 열어 use 에 넘기고 즉시 와이프. requireArmed:주문계열 자격증명은 true(미무장 시 fail-closed throw).</summary>
        public static T WithSecret<T>(SecurityAssetVault vault, string key, bool requireArmed, SecretUse<T> use)
        {
            if (vault is null) throw new ArgumentNullException(nameof(vault));
            if (use is null) throw new ArgumentNullException(nameof(use));
            using var lease = vault.Reveal(key, requireArmed);   // Dispose 시 char[] ZeroMemory 와이프
            return use(lease.Value);
        }
    }
}
