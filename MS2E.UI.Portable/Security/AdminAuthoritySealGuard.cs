// ============================================================================
// AdminAuthoritySealGuard — v10.1.0 Phase 93-B: ★Think Beyond Gemini — Admin 권한 봉인 가드
//   "최고 권한 명령 표면의 두 파멸 리스크(비인가 호출·비상 중 상태 노출)를 '권한 봉인' 단일 불변식으로 통합 봉쇄."
//
//  ── 왜 제미나이의 A(핸드셰이크)/B(freeze-purge)를 초월하는가 ──────────────────────────────
//  Admin UI 가 분리 어셈블리로 이주하면 두 리스크가 생긴다: ①DI 가로채기 등으로 비인가 컴포넌트가 IAdminCommandPort 를
//  무단 호출(Rogue Port Hijacking) ②라이브 가동 중 ForceFreeze/금고 브리치 격발 시 Admin UI 렌더/폼 상태 노출·파손
//  (Corrupted Admin State under Freeze). 제미나이는 각각 별도 가드를 제안했다. 그러나 이 둘은 하나의 불변식의 두 얼굴이다 —
//  ★'유효 권한 봉인 = (정당한 UI 세션이 발급) ∧ (시스템 미동결)'. 세션 개시 시 비밀 세션 키를 세우고, dispatcher 가 intent 별
//  keyed-FNV 봉인을 발급, 어댑터가 Port 진입에서 검증한다 — 세션 키를 모르는 rogue 호출자는 봉인을 위조할 수 없다(=A). 그리고
//  ForceFreeze/브리치 시 Revoke() 가 세션 키를 O(1) 영점(0x00)하면 '이후 모든 봉인이 즉시 무효'가 되어 명령이 fail-closed 로
//  거부되고 onRevoke 로 UI 퍼지가 격발된다(=B). 즉 인가 토큰과 비상 킬스위치를 '봉인' 하나로 통합·초월.
//
//  ★불변: δ=0(IssueSeal/VerifySeal/Revoke 핫패스 힙 0·keyed-FNV·Interlocked) · 결정론(순수 산술·세션 키 주입) ·
//    liveArmed:false(순수 인가 판정 — IBroker·소켓·onSend·실 발주 유발 0) · fail-closed(revoke/미개시/봉인 불일치 → 거부).
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Dto;

namespace MS2E.UI.Portable.Security
{
    /// <summary>Admin 권한 봉인 가드(δ=0). 세션 키로 intent 봉인 발급/검증(rogue 차단) + ForceFreeze 시 O(1) revoke(freeze-safe).</summary>
    public sealed class AdminAuthoritySealGuard
    {
        private const ulong FnvPrime = 1099511628211UL;

        private readonly Action? _onRevoke;   // revoke 시 UI 퍼지 신호(SecureAdminViewPresenter.PurgeNow 등)
        private long _sessionKeyBits;          // 세션 권한 키(long 비트·0=미개시/revoke). 비밀 — rogue 미지.
        private long _revoked;                 // 0/1 — ForceFreeze/브리치 시 1(재개시 전 무효)
        private long _issued, _verifyOk, _verifyFail, _revocations;

        public AdminAuthoritySealGuard(Action? onRevoke = null) => _onRevoke = onRevoke;

        // ── 읽기(δ=0·락프리) ──
        public bool Authorized     => Volatile.Read(ref _revoked) == 0L && Interlocked.Read(ref _sessionKeyBits) != 0L;
        public bool Revoked        => Volatile.Read(ref _revoked) == 1L;
        public long Issued         => Interlocked.Read(ref _issued);
        public long VerifyOk       => Interlocked.Read(ref _verifyOk);
        public long VerifyFailures => Interlocked.Read(ref _verifyFail);
        public long Revocations    => Interlocked.Read(ref _revocations);

        /// <summary>세션 개시(UI init). 비밀 권한 키 설정(런타임 보안 소스 주입). ★fail-closed: 0 키(파생 실패/미초기화)는
        /// 세션 미개시(키 0 유지 → Authorized false → 전 명령 차단) — 공개 상수 대체 금지(known-key 강등 봉쇄).</summary>
        public void BeginSession(ulong sessionKey)
        {
            Interlocked.Exchange(ref _sessionKeyBits, unchecked((long)sessionKey));   // 0 이면 0 유지(미개시·fail-closed)
            Volatile.Write(ref _revoked, 0L);
        }

        /// <summary>★권한 봉인 발급(δ=0·keyed-FNV). ★internal — 오직 같은 어셈블리의 dispatcher 만 발급 가능(외부/rogue 어셈블리는
        /// 발급 불가 → Port 직접 호출용 정당 봉인 획득 봉쇄). 세션 키 시드로 위조 불가 봉인 생성.</summary>
        internal long IssueSeal(in AdminCommandIntent intent, long nonce)
        {
            Interlocked.Increment(ref _issued);
            return ComputeSeal(in intent, nonce, (ulong)Interlocked.Read(ref _sessionKeyBits));
        }

        /// <summary>
        /// ★권한 검증(δ=0). 어댑터가 Port.Execute 진입 시 호출. revoke·미개시(키 0)·봉인 불일치 → false(비인가/rogue/동결 차단·fail-closed).
        /// </summary>
        public bool VerifySeal(in AdminCommandIntent intent, long nonce, long seal)
        {
            if (Volatile.Read(ref _revoked) == 1L) { Interlocked.Increment(ref _verifyFail); return false; }   // 동결
            long key = Interlocked.Read(ref _sessionKeyBits);
            if (key == 0L) { Interlocked.Increment(ref _verifyFail); return false; }                            // 미개시/revoke
            if (ComputeSeal(in intent, nonce, (ulong)key) != seal) { Interlocked.Increment(ref _verifyFail); return false; }   // 위조/불일치(rogue)
            Interlocked.Increment(ref _verifyOk);
            return true;
        }

        /// <summary>
        /// ★비상 revoke(O(1)·ForceFreeze/금고 브리치). 세션 키를 즉시 0x00 영점 → 이후 모든 봉인 무효(fail-closed) + onRevoke 로
        /// UI 퍼지 격발(freeze-safe). 재개시(BeginSession) 전까지 전 Admin 명령 차단.
        /// </summary>
        public void Revoke(long reasonCode = 0L)
        {
            Volatile.Write(ref _revoked, 1L);
            Interlocked.Exchange(ref _sessionKeyBits, 0L);   // ★키 자가 소멸(비밀 잔류 0)
            Interlocked.Increment(ref _revocations);
            try { _onRevoke?.Invoke(); } catch { /* 퍼지 콜백 실패 무해 — revoke·키 영점은 이미 확정(fail-closed) */ }
        }

        // keyed-FNV-64 봉인(δ=0·순수). 세션 키 시드 → 위조 방지. 동일 (intent,nonce,key)면 동일 봉인.
        private static long ComputeSeal(in AdminCommandIntent intent, long nonce, ulong key)
        {
            unchecked
            {
                ulong h = key;
                h = (h ^ (byte)intent.Kind)      * FnvPrime;
                h = (h ^ (ulong)intent.Arg0)     * FnvPrime;
                h = (h ^ (ulong)intent.Arg1)     * FnvPrime;
                h = (h ^ (ulong)intent.IssuedMicros) * FnvPrime;
                h = (h ^ (ulong)nonce)           * FnvPrime;
                h = (h ^ key)                    * FnvPrime;
                return (long)h;
            }
        }
    }
}
