// ============================================================================
// CartridgeCrypto - 전략 카트리지 서명/검증 (Season 7 #2) · BCL ECDsa(P-256), 외부 의존 0
//  카트리지의 제작자 신원(공개키)과 무결성을 ECDsa-P256 + SHA-256 서명으로 보장한다.
//  · 키쌍: PKCS#8(개인키) / SubjectPublicKeyInfo(공개키)를 base64로 직렬화.
//  · 개인키는 호출측에서 DPAPI로 at-rest 보관(여기선 순수 서명/검증만).
// ============================================================================
using System;
using System.Buffers;
using System.Security.Cryptography;

namespace MS2E.Infrastructure
{
    public static class CartridgeCrypto
    {
        public static (string privB64, string pubB64) GenerateKeyPair()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (Convert.ToBase64String(ec.ExportPkcs8PrivateKey()),
                    Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
        }

        public static string Sign(byte[] message, string privB64)
        {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privB64), out _);
            return Convert.ToBase64String(ec.SignData(message, HashAlgorithmName.SHA256));
        }

        /// <summary>
        /// v4.86.0 — 금고(SecretLease) 연동 서명. 개인키를 관리 string 으로 물질화하지 않고 base64 char 스팬에서
        /// 직접 디코드(풀 버퍼·사용 후 ZeroMemory 소거)하고, 메시지를 stackalloc[32] 로 SHA256 해시한 뒤
        /// ec.SignHash 로 서명한다. SignHash(SHA256(msg)) 는 기존 Verify(=VerifyData(msg,·,SHA256))와 완전 호환.
        /// ★키 유실/손상 시 throw(호출부가 fail-closed 로 폐기). 반환 sig base64.
        /// </summary>
        public static string SignZeroAlloc(ReadOnlySpan<byte> message, ReadOnlySpan<char> privB64)
        {
            if (privB64.IsEmpty) throw new FormatException("ECDsa 개인키 없음(빈 키) — fail-closed");
            int maxLen = (privB64.Length >> 2) * 3 + 3;                 // base64 → 최대 바이트 길이
            byte[] pk = ArrayPool<byte>.Shared.Rent(maxLen);
            try
            {
                if (!Convert.TryFromBase64Chars(privB64, pk, out int pkLen) || pkLen == 0)
                    throw new FormatException("ECDsa 개인키 base64 손상 — fail-closed");
                Span<byte> hash = stackalloc byte[32];
                SHA256.HashData(message, hash);                        // ★zero-alloc 해시(스택 버퍼)
                using var ec = ECDsa.Create();
                ec.ImportPkcs8PrivateKey(pk.AsSpan(0, pkLen), out _);
                return Convert.ToBase64String(ec.SignHash(hash));      // SignHash(SHA256) ↔ VerifyData(SHA256) 호환
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pk.AsSpan(0, maxLen));   // 개인키 pkcs8 바이트 즉시 소거
                ArrayPool<byte>.Shared.Return(pk);
            }
        }

        public static bool Verify(byte[] message, string sigB64, string pubB64)
        {
            try
            {
                if (string.IsNullOrEmpty(sigB64) || string.IsNullOrEmpty(pubB64)) return false;
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);
                return ec.VerifyData(message, Convert.FromBase64String(sigB64), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }
    }
}
