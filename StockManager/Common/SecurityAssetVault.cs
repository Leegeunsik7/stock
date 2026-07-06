// ============================================================================
// SecurityAssetVault — 과제 1: 민감 자산(토스/키움/FX 자격증명) DPAPI 금고 + 메모리 하드닝
//
//  ⚠️ liveArmed:false 불변 — Reveal(실 자격증명 복호화)은 무장(isLiveArmed()==true) 상태에서만 허용.
//     미무장·봉인·EmergencyFreeze 이면 fail-closed(throw). Store 는 무장과 무관(보관은 항상 허용).
//
//  ── 저장/열람 ────────────────────────────────────────────────────────────────
//   Store: 평문(ReadOnlySpan<char>) → UTF8 → ProtectedData.Protect(CurrentUser) → "enc:v1:"+base64 → DB.
//          기존 Secret 헬퍼와 동일 포맷(enc:v1:) → LocalDatabase.GetSecret 과 상호 호환.
//   Reveal: DB → base64 해제 → ProtectedData.Unprotect → UTF8 → 풀(ArrayPool) char[] 로 디코드 → SecretLease.
//           ★평문 문자열(string)을 절대 만들지 않는다 — 불변 string 은 GC 전까지 잔존해 덤프 탈취 표면이 됨.
//
//  ── δ=0 / 메모리 와이프(고도화) ───────────────────────────────────────────────
//   · 전이 버퍼는 ArrayPool 재사용(GC 할당 극소화). 평문 바이트/문자 버퍼는 사용 직후
//     CryptographicOperations.ZeroMemory 로 즉시 소거(휘발성 와이프). SecretLease.Dispose 가 char 버퍼를 와이프.
//
//  ── 동결 연동 래치(고도화) ────────────────────────────────────────────────────
//   · OrderAttestationService.ForceFreeze(EmergencyFreeze) 감지 시 금고를 영구 봉인(_sealed 래치) →
//     런타임 중 재복호화 원천 차단. 명시 Seal() 도 지원. 봉인은 프로세스 재시작 전까지 불가역.
// ============================================================================
using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>복호화된 자격증명의 수명 스코프. using 종료 시 char 버퍼를 ZeroMemory 로 소거 후 풀 반환.</summary>
    public sealed class SecretLease : IDisposable
    {
        private char[]? _buf;
        private readonly int _len;

        internal SecretLease(char[] buf, int len) { _buf = buf; _len = len; }

        /// <summary>복호화된 비밀(읽기전용 스팬). Dispose 후에는 빈 스팬.</summary>
        public ReadOnlySpan<char> Value => _buf is null ? ReadOnlySpan<char>.Empty : _buf.AsSpan(0, _len);
        public int  Length     => _buf is null ? 0 : _len;
        public bool IsRevealed => _buf is not null;

        public void Dispose()
        {
            var b = Interlocked.Exchange(ref _buf, null);
            if (b is null) return;
            // 평문 char 버퍼 즉시 소거(덤프 방어) 후 풀 반환(소거 완료분 재사용 안전).
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(b.AsSpan(0, _len)));
            ArrayPool<char>.Shared.Return(b);
        }
    }

    /// <summary>DPAPI(CurrentUser) 기반 민감 자산 금고. 메모리 와이프 + 동결 연동 봉인 래치 + 무장 펜스.</summary>
    public sealed class SecurityAssetVault
    {
        private const string Marker = "enc:v1:";   // Secret 헬퍼와 동일 포맷(상호 호환)

        private readonly LocalDatabase           _db;
        private readonly OrderAttestationService _att;
        private readonly Func<bool>              _isLiveArmed;   // ★liveArmed:false 옵트인 펜스(Reveal 게이트)
        private int _sealed;   // 0=열림, 1=봉인(동결/명시). Interlocked 래치.

        // 잘 알려진 자산 키(선택 — 호출부가 임의 키도 사용 가능).
        public const string TossClientSecretKey = "Toss.ClientSecret";
        public const string KisAppSecretKey      = "KIS.AppSecret";
        public const string UpbitSecretKey       = "Upbit.SecretKey";

        public SecurityAssetVault(LocalDatabase db, OrderAttestationService attestation, Func<bool> isLiveArmed)
        {
            _db          = db          ?? throw new ArgumentNullException(nameof(db));
            _att         = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _isLiveArmed = isLiveArmed ?? throw new ArgumentNullException(nameof(isLiveArmed));
        }

        /// <summary>금고 봉인 여부(관측). 봉인되면 Store/Reveal 모두 fail-closed.</summary>
        public bool IsSealed => Volatile.Read(ref _sealed) == 1;

        /// <summary>명시 봉인(불가역·프로세스 수명). 이후 재복호화 원천 차단.</summary>
        public void Seal() => Volatile.Write(ref _sealed, 1);

        /// <summary>평문 자격증명을 DPAPI 암호화해 보관. 평문 span 은 호출자 소유(가능하면 사용 후 호출자가 와이프).</summary>
        public void Store(string key, ReadOnlySpan<char> plaintext)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("빈 키", nameof(key));
            EnsureOpenOrThrow("Store");

            int byteCount = Encoding.UTF8.GetByteCount(plaintext);
            byte[] tmp = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                int n = Encoding.UTF8.GetBytes(plaintext, tmp.AsSpan(0, byteCount));
                byte[] exact = tmp.AsSpan(0, n).ToArray();   // DPAPI 는 정확 길이 byte[] 필요(전이·즉시 소거)
                byte[] enc;
                try { enc = ProtectedData.Protect(exact, null, DataProtectionScope.CurrentUser); }
                finally { CryptographicOperations.ZeroMemory(exact); }
                _db.SetSetting(key, Marker + Convert.ToBase64String(enc));   // enc = 암호문(비밀 아님)
            }
            finally
            {
                CryptographicOperations.ZeroMemory(tmp.AsSpan(0, byteCount));   // 전이 평문 바이트 와이프
                ArrayPool<byte>.Shared.Return(tmp);
            }
        }

        /// <summary>string 편의 오버로드(설정 UI 등). 내부에서 span 경로로 위임.</summary>
        public void StoreString(string key, string plaintext) => Store(key, (plaintext ?? "").AsSpan());

        /// <summary>
        /// 자격증명 복호화 → SecretLease(using 스코프). ★무장·비봉인·비동결에서만 허용(fail-closed).
        /// 반환 리스는 반드시 using 으로 소비 — Dispose 시 평문 char 버퍼가 ZeroMemory 로 소거된다.
        /// </summary>
        /// <summary>기본 Reveal — 실 브로커 자격증명용. 무장 상태에서만 허용(liveArmed:false 펜스).</summary>
        public SecretLease Reveal(string key) => Reveal(key, requireArmed: true);

        /// <summary>
        /// v4.86.0 — requireArmed 선택 Reveal. 실 브로커 자격증명(주문 송출 트리거)은 requireArmed:true(무장 펜스),
        /// 서명 키 등 '주문 송출이 아닌' 비밀은 requireArmed:false 로 무장과 무관하게 복호화(단, 봉인·EmergencyFreeze
        /// fail-closed 는 항상 적용). 메모리 와이프(SecretLease) 계약 동일.
        /// </summary>
        public SecretLease Reveal(string key, bool requireArmed)
        {
            if (string.IsNullOrEmpty(key)) throw new ArgumentException("빈 키", nameof(key));
            EnsureOpenOrThrow($"Reveal({key})");

            // ★liveArmed:false 옵트인 펜스 — 실 자격증명(주문 송출 트리거) 복호화는 무장 상태에서만.
            if (requireArmed)
            {
                bool armed;
                try { armed = _isLiveArmed(); } catch { armed = false; }   // 술어 예외 → 미무장 취급(fail-closed)
                if (!armed)
                    throw new InvalidOperationException($"SecurityAssetVault: 미무장 — Reveal({key}) 차단(liveArmed:false fail-closed)");
            }

            string stored = _db.GetSetting(key) ?? "";
            if (!stored.StartsWith(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException($"SecurityAssetVault: '{key}' 미보관 또는 비암호화 — fail-closed");

            byte[] enc = Convert.FromBase64String(stored.Substring(Marker.Length));   // 암호문(비밀 아님)
            byte[] dec = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);   // ★평문 바이트
            try
            {
                int n = Encoding.UTF8.GetCharCount(dec);
                char[] chars = ArrayPool<char>.Shared.Rent(Math.Max(1, n));   // 풀 재사용 버퍼
                try
                {
                    Encoding.UTF8.GetChars(dec, chars.AsSpan(0, n));
                    return new SecretLease(chars, n);   // Dispose 시 소거+반환
                }
                catch
                {
                    // ★디코드 예외 시에도 부분 평문이 담긴 풀 버퍼를 소거·반환(누수·재사용 유출 차단).
                    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(chars.AsSpan(0, n)));
                    ArrayPool<char>.Shared.Return(chars);
                    throw;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dec);   // 평문 바이트 즉시 소거(덤프 방어)
                CryptographicOperations.ZeroMemory(enc);   // 암호문도 소거(보수)
            }
        }

        /// <summary>키 존재 여부(복호화 없이). 봉인/동결과 무관한 순수 조회.</summary>
        public bool Has(string key)
            => !string.IsNullOrEmpty(key)
               && (_db.GetSetting(key) ?? "").StartsWith(Marker, StringComparison.Ordinal);

        // ── fail-closed 게이트 ────────────────────────────────────────────────
        private void EnsureOpenOrThrow(string op)
        {
            if (Volatile.Read(ref _sealed) == 1)
                throw new InvalidOperationException($"SecurityAssetVault 봉인 — {op} 차단(fail-closed)");
            if (_att.Mode == GatingMode.EmergencyFreeze)
            {
                // ★동결 연동 래치 — EmergencyFreeze 감지 시 영구 봉인(이후 att 해제돼도 재복호화 불가).
                Volatile.Write(ref _sealed, 1);
                throw new InvalidOperationException($"SecurityAssetVault: EmergencyFreeze 감지 — 금고 봉인·{op} 차단(fail-closed)");
            }
        }
    }
}
