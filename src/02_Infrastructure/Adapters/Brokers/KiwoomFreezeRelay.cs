// ============================================================================
// KiwoomFreezeRelay — 과제3: 키움 파이프 생존성 이벤트 → 실거래 계층 원자적 EmergencyFreeze
//
//  설계 원칙(시스템 제약):
//   · #2 fail-closed : ProxyLost / (옵트인)Error / 핸들러 내부 예외 → 즉시 동결.
//   · 멱등          : Interlocked CAS 단일 게이트로 ForceFreeze 정확히 1회.
//   · 재진입 안전   : 동결 경로 자체가 예외나도 _frozen 유지 + 예외 삼킴(throw 금지).
//   · 락-프리       : volatile int + Interlocked.CompareExchange (단일락 불요).
//   · 동결경로 할당 허용(핫패스 아님) — 로그 문자열 OK.
//
//  ★ 체결 감사 경계(중요):
//    체결(Fill) 감사·보유수량 갱신은 ExecutionBridgeBroker 가 소유한 LiveAuditService
//    .ProcessFill(fill)(StockValidator 1차 + OrderAttestationService 2차)가 '진짜 기대주문'
//    으로 단독 책임진다. KiwoomBridgeClient.Dispatch 는 OrderFilled 뿐 아니라 OrderRejected
//    까지 FillReceived 로 발행하므로, Relay 가 FillReceived 를 다시 구독해 독립 감사를 하면
//    (1) 기대주문 날조로 과체결 게이트 무력화(fail-open), (2) 평범한 주문거부 한 건이
//    전체 실거래를 동결(freeze-storm), (3) 거짓 통과 증적 박제가 발생한다.
//    따라서 Relay 는 FillReceived 를 구독하지 않는다. 동결 트리거는 파이프 생존성과
//    옵트인 에러로 한정한다. (적대적 검증 critical 2건 교정 반영)
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>
    /// 32bit 키움 OCX(COM) 추상화. 실 DLL(KHOPENAPI.OCX) 의존부는 KiwoomProxyHost(x86)가
    /// 구현하며, 본 어셈블리는 인터페이스로만 참조한다(openQuestions 참고).
    /// </summary>
    public interface IKiwoomComProxy
    {
        bool IsComConnected { get; }
        void Disconnect();
    }

    public sealed class KiwoomFreezeRelay : IDisposable
    {
        private readonly OrderAttestationService _attestation;
        private readonly KiwoomBridgeClient      _bridge;
        private readonly IKiwoomComProxy?        _com;       // 선택: 동결 시 COM 강제 단선
        private readonly bool                    _freezeOnAnyError;

        // 멱등 게이트 — 0=정상, 1=동결 트리거 완료. Interlocked 로만 전이.
        private int          _frozen;
        private int          _attached;
        private volatile string? _lastFreezeError;

        /// <summary>동결 트리거가 이미 발동했는지(멱등 관측).</summary>
        public bool IsFrozen => Volatile.Read(ref _frozen) == 1;

        /// <summary>동결 시도 중 발생한 마지막 예외 메시지(없으면 null). 진단용.</summary>
        public string? LastFreezeError => _lastFreezeError;

        /// <param name="freezeOnAnyError">
        /// true면 ErrorReceived 모든 코드를 동결 트리거로 취급. 기본 false 라도 IsFatalError
        /// 가 '미상 코드는 보수적으로 동결(과동결 허용, 거짓 통과 없음)'을 적용하므로
        /// code!=0 은 동결한다. 코드 매핑표 도입 전까지의 보수 정책(openQuestions).
        /// </param>
        public KiwoomFreezeRelay(
            OrderAttestationService attestation,
            KiwoomBridgeClient      bridge,
            IKiwoomComProxy?        com              = null,
            bool                    freezeOnAnyError = false)
        {
            _attestation      = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _bridge           = bridge      ?? throw new ArgumentNullException(nameof(bridge));
            _com              = com;
            _freezeOnAnyError = freezeOnAnyError;
        }

        // ── 구독/해제 (idempotent) ──────────────────────────────────────────────

        /// <summary>이벤트 구독. 중복 호출해도 1회만 구독(중복 트리거 누적 방지).
        ///   FillReceived 는 의도적으로 구독하지 않는다(체결 감사 경계 참고).</summary>
        public void Attach()
        {
            if (Interlocked.CompareExchange(ref _attached, 1, 0) != 0) return;

            _bridge.ProxyLost     += OnProxyLost;
            _bridge.ErrorReceived += OnError;
        }

        public void Detach()
        {
            if (Interlocked.CompareExchange(ref _attached, 0, 1) != 1) return;

            _bridge.ProxyLost     -= OnProxyLost;
            _bridge.ErrorReceived -= OnError;
        }

        // ── 이벤트 핸들러: 모두 try-catch, catch 에서도 반드시 동결 ───────────────

        private void OnProxyLost()
        {
            try
            {
                FreezeOnce("KiwoomProxyHost 연결 끊김 — 하트비트 초과(파이프 단절), 고아 주문 방지");
            }
            catch (Exception ex)
            {
                // 핸들러 내부 예외조차 fail-open 으로 새지 않게: 같은 게이트로 다시 동결 시도.
                _lastFreezeError = ex.Message;
                FreezeOnce("ProxyLost 핸들러 예외 — 안전 동결");
            }
        }

        private void OnError(ErrorPayload err)
        {
            try
            {
                if (_freezeOnAnyError || IsFatalError(err.ErrorCode))
                    FreezeOnce($"키움 에러 리포트 수신 — code={err.ErrorCode}");
            }
            catch (Exception ex)
            {
                _lastFreezeError = ex.Message;
                FreezeOnce("ErrorReceived 핸들러 예외 — 안전 동결");
            }
        }

        // ── 멱등 동결 코어 ─────────────────────────────────────────────────────

        /// <summary>
        /// CAS 0→1 에 성공한 단 한 번만 ForceFreeze + COM 단선을 수행.
        /// 동결 시도 자체가 예외나도 _frozen 은 1 로 유지(다시 정상으로 풀리지 않음).
        /// 절대 throw 하지 않는다 — 백그라운드 디스패치 스레드 핫패스 보호.
        /// ※ OrderAttestationService.ForceFreeze 는 _modeInt 멱등 쓰기이므로, 브로커의
        ///   OnProxyLost 등 다른 경로와 중복 발동해도 상태는 한 번만 EmergencyFreeze 로 수렴.
        /// </summary>
        private void FreezeOnce(string reason)
        {
            if (Interlocked.CompareExchange(ref _frozen, 1, 0) != 0)
                return;   // 이미 누군가 동결 트리거를 점유 — 멱등 통과

            try
            {
                _attestation.ForceFreeze(reason);   // GatingMode → EmergencyFreeze
            }
            catch (Exception ex)
            {
                // ForceFreeze 가 실패해도 _frozen 은 1. 진단만 박제.
                _lastFreezeError = ex.Message;
            }

            // 보강: COM 측까지 강제 단선해 고아 주문 발생 표면을 더 줄임(있을 때만).
            try { _com?.Disconnect(); }
            catch (Exception ex) { _lastFreezeError = ex.Message; }
        }

        /// <summary>
        /// 미상 에러코드는 보수적으로 동결(과동결 허용, 거짓 통과 없음).
        /// 코드 매핑표 도입 시 정보성 경고 코드를 화이트리스트로 제외 예정(openQuestions).
        /// </summary>
        private static bool IsFatalError(int code) => code != 0;

        public void Dispose() => Detach();
    }
}
