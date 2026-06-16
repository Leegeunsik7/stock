// ============================================================================
// CartridgeCrypto - 전략 카트리지 서명/검증 (Season 7 #2) · BCL ECDsa(P-256), 외부 의존 0
//  카트리지의 제작자 신원(공개키)과 무결성을 ECDsa-P256 + SHA-256 서명으로 보장한다.
//  · 키쌍: PKCS#8(개인키) / SubjectPublicKeyInfo(공개키)를 base64로 직렬화.
//  · 개인키는 호출측에서 DPAPI로 at-rest 보관(여기선 순수 서명/검증만).
// ============================================================================
using System;
using System.Security.Cryptography;

namespace StockManager.Common
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
