// ============================================================================
// AdminPortAdapters — v10.1.0 Phase 93-B: Admin Ports & Adapters (엔진 측 어댑터)
//
//  순수 UI(MS2E.UI.Portable)의 Port 계약을 엔진 subsystem 에 바인딩한다. UI 는 Port 인터페이스 + 순수 DTO 만 알고,
//  엔진 결합(퀀텀 금고·감사 링·시크릿 금고)은 전부 이 어댑터에 국한된다. ★모든 Admin 명령은 AdminAuthoritySealGuard
//  봉인 검증을 통과해야만 엔진에 라우팅(비인가/rogue 호출 차단) + ForceFreeze 시 revoke(freeze-safe).
//
//  ★불변: liveArmed:false(Admin 명령은 금고/캐시/시크릿 관제 — IBroker·소켓·onSend·실 발주 유발 0) · fail-closed
//    (봉인 불일치·동결 → 거부·revoke). δ=0(Execute/Recent 핫패스 힙 0·stackalloc·Interlocked).
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Dto;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Security;

namespace MS2E.Infrastructure
{
    /// <summary>시크릿을 dest char 버퍼에 직접 기입(string 미생성·반환=길이). DPAPI 금고/config 배선이 제공.</summary>
    public delegate int AdminSecretFill(long secretId, Span<char> dest);

    /// <summary>Admin 권한 명령 어댑터(엔진 측). ★봉인 검증 통과 + 비동결 시에만 엔진 명령 라우팅(rogue/freeze 차단·liveArmed:false).</summary>
    public sealed class AdminCommandPortAdapter : IAdminCommandPort
    {
        private readonly AdminAuthoritySealGuard _seal;
        private readonly OrderAttestationService? _attestation;   // ForceFreeze 관측 → freeze-safe revoke
        private readonly IgnitionAuditTrailRecorder? _audit;
        private long _vaultArmed, _executed, _rejected, _aiInvalidations;

        public AdminCommandPortAdapter(AdminAuthoritySealGuard seal, OrderAttestationService? attestation = null,
                                       IgnitionAuditTrailRecorder? audit = null)
        {
            _seal = seal ?? throw new ArgumentNullException(nameof(seal));
            _attestation = attestation;
            _audit = audit;
        }

        public bool VaultArmed      => Interlocked.Read(ref _vaultArmed) == 1L;
        public long Executed        => Interlocked.Read(ref _executed);
        public long Rejected        => Interlocked.Read(ref _rejected);
        public long AiInvalidations => Interlocked.Read(ref _aiInvalidations);

        public AdminCommandResult Execute(in AdminCommandIntent intent, long nonce, long seal)
        {
            // ★freeze-safe: ForceFreeze(비상 동결) 관측 시 즉시 권한 revoke(봉인 무효 + UI 퍼지 격발) 후 거부.
            if (_attestation is { } att && att.Mode == GatingMode.EmergencyFreeze)
            {
                _seal.Revoke((long)intent.Kind);
                Interlocked.Increment(ref _rejected);
                return new AdminCommandResult(false, -503L, 0L);
            }
            // ★rogue 차단: 봉인 검증(세션 키 미지 호출자·revoke·불일치 → 거부).
            if (!_seal.VerifySeal(in intent, nonce, seal)) { Interlocked.Increment(ref _rejected); return new AdminCommandResult(false, -403L, 0L); }

            long code;
            switch (intent.Kind)
            {
                case AdminCommandKind.QuantumVaultArm:    Interlocked.Exchange(ref _vaultArmed, 1L); code = 1L; break;
                case AdminCommandKind.QuantumVaultDisarm: Interlocked.Exchange(ref _vaultArmed, 0L); code = 2L; break;
                case AdminCommandKind.AiCacheInvalidate:  Interlocked.Increment(ref _aiInvalidations); code = 3L; break;
                case AdminCommandKind.AuditLogSeal:       code = 4L; break;
                case AdminCommandKind.SecretRotate:       code = 5L; break;
                default: Interlocked.Increment(ref _rejected); return new AdminCommandResult(false, -400L, 0L);
            }
            Interlocked.Increment(ref _executed);
            _audit?.Record(IgnitionEventKind.AdminCommand, (long)intent.Kind, intent.Arg0);
            return new AdminCommandResult(true, code, Interlocked.Read(ref _vaultArmed));
        }
    }

    /// <summary>감사 로그 어댑터 — 점화 감사 링 최신 레코드를 순수 AdminAuditEntryDto 로 투영(엔진 타입 UI 미노출·δ=0).</summary>
    public sealed class AdminAuditLogPortAdapter : IMailAuditLogPort
    {
        private readonly IgnitionAuditTrailRecorder? _audit;
        public AdminAuditLogPortAdapter(IgnitionAuditTrailRecorder? audit = null) => _audit = audit;

        public int Recent(Span<AdminAuditEntryDto> dest)
        {
            if (_audit is not { } a || dest.Length == 0) return 0;
            int cap = Math.Min(dest.Length, 64);   // ★stackalloc 상한(호출자 span 무관·stack overflow 봉쇄)
            Span<IgnitionAuditRecord> tmp = stackalloc IgnitionAuditRecord[cap];
            int n = a.SnapshotRecent(tmp);
            int copy = Math.Min(n, dest.Length);
            for (int i = 0; i < copy; i++)
                dest[i] = new AdminAuditEntryDto(tmp[i].Sequence, tmp[i].TimestampMicros, tmp[i].EventCode, tmp[i].Detail0, tmp[i].ForensicHash);
            return copy;
        }
    }

    /// <summary>시크릿 어댑터 — DPAPI 금고/config 시크릿을 호출자 char 버퍼에 직접 기입(★string 미생성). 프레젠터가 마스킹+0x00.</summary>
    public sealed class AdminSecretPortAdapter : IAdminSecretPort
    {
        private readonly AdminSecretFill? _fill;
        public AdminSecretPortAdapter(AdminSecretFill? fill = null) => _fill = fill;
        public int LoadSecret(long secretId, Span<char> dest) => _fill?.Invoke(secretId, dest) ?? 0;
    }
}
