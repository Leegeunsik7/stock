// ============================================================================
// AdminCommandPortDispatcher — v10.1.0 Phase 93-B: δ=0 Admin 커맨드 버스 (Gemini 보강 과제)
//
//  Admin UI 에서 실행되는 권한 명령(퀀텀 금고 수동 제어·AI 캐시 무효화 등)을 무할당(δ=0) 불변 Intent DTO 로 포맷팅하고,
//  ★AdminAuthoritySealGuard 로 권한 봉인을 발급해 IAdminCommandPort 로 전송한다. UI 는 엔진 명령 API 를 직접 알지 못하며
//  오직 Intent DTO + 봉인 + Port 만 안다(Ports & Adapters 디커플링). 봉인 미인가/동결 시 Port 미전송(fail-closed).
//
//  ★불변: δ=0(Dispatch 핫패스 힙 0·record struct 값타입·seal 정수·Interlocked) · 결정론(주입 µs 시계·FNV 순수) ·
//    liveArmed:false(순수 명령 포맷/전송 — IBroker·소켓·실 발주 유발 0) · fail-closed(미인가 → 미전송).
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Dto;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Security;

namespace MS2E.UI.Portable.Bus
{
    /// <summary>δ=0 Admin 커맨드 버스. Dispatch(kind,args)=불변 Intent 포맷+권한 봉인 발급+Port 전송. 미인가/동결 시 미전송.</summary>
    public sealed class AdminCommandPortDispatcher
    {
        private readonly IAdminCommandPort _port;
        private readonly AdminAuthoritySealGuard _seal;
        private readonly Func<long> _nowMicros;
        private long _dispatched, _rejected;

        public AdminCommandPortDispatcher(IAdminCommandPort port, AdminAuthoritySealGuard seal, Func<long>? nowMicros = null)
        {
            _port = port ?? throw new ArgumentNullException(nameof(port));
            _seal = seal ?? throw new ArgumentNullException(nameof(seal));
            _nowMicros = nowMicros ?? (static () => 0L);   // 정적 람다(캐시·δ=0). 프로덕션은 µs 시계 주입.
        }

        // ── 읽기(δ=0·락프리) ──
        public long Dispatched => Interlocked.Read(ref _dispatched);
        public long Rejected   => Interlocked.Read(ref _rejected);

        /// <summary>
        /// ★권한 명령 전송(δ=0). 불변 Intent 포맷 + keyed 봉인 발급 + Port.Execute. 봉인 미인가(미개시/revoke/동결) 시
        /// Port 미전송·거부 반환(fail-closed). 힙 0(Intent 는 record struct 값타입·봉인/nonce 는 정수).
        /// </summary>
        public AdminCommandResult Dispatch(AdminCommandKind kind, long arg0 = 0L, long arg1 = 0L)
        {
            if (!_seal.Authorized) { Interlocked.Increment(ref _rejected); return new AdminCommandResult(false, -1L, 0L); }
            long nonce = Interlocked.Increment(ref _dispatched);
            var intent = new AdminCommandIntent(kind, arg0, arg1, _nowMicros());
            long seal = _seal.IssueSeal(in intent, nonce);
            return _port.Execute(in intent, nonce, seal);
        }
    }
}
