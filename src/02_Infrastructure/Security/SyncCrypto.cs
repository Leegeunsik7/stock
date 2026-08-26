// ============================================================================
// SyncCrypto - 동기화 번들 종단간 암호화(E2EE) (v3.43.0)
//  암호 → PBKDF2-SHA256(200k) → AES-256-GCM. BCL 전용(추가 의존 0).
//  번들 포맷:  magic(4="MS2S") | ver(1) | salt(16) | nonce(12) | tag(16) | ciphertext
//  GCM 인증 태그로 변조 탐지 — 암호가 틀리거나 1바이트라도 손상되면 복호화가 거부된다.
//  키는 어디에도 저장하지 않는다(사용자가 기기 간 직접 공유). 비밀정보(API키 등)는 번들에 포함되지 않음.
// ============================================================================
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MS2E.Infrastructure
{
    public static class SyncCrypto
    {
        private static readonly byte[] Magic = { (byte)'M', (byte)'S', (byte)'2', (byte)'S' };
        private const byte Ver = 1;
        private const int SaltLen = 16, NonceLen = 12, TagLen = 16, KeyLen = 32, Iterations = 200_000;
        private static readonly int HeaderLen = Magic.Length + 1 + SaltLen + NonceLen + TagLen;

        private static byte[] DeriveKey(string passphrase, byte[] salt) =>
            Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase ?? ""), salt, Iterations, HashAlgorithmName.SHA256, KeyLen);

        public static byte[] Encrypt(byte[] plaintext, string passphrase)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltLen);
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);
            var key = DeriveKey(passphrase, salt);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagLen];
            using (var gcm = new AesGcm(key, TagLen))
                gcm.Encrypt(nonce, plaintext, cipher, tag);

            using var ms = new MemoryStream(HeaderLen + cipher.Length);
            ms.Write(Magic, 0, Magic.Length);
            ms.WriteByte(Ver);
            ms.Write(salt, 0, salt.Length);
            ms.Write(nonce, 0, nonce.Length);
            ms.Write(tag, 0, tag.Length);
            ms.Write(cipher, 0, cipher.Length);
            return ms.ToArray();
        }

        public static bool TryDecrypt(byte[] blob, string passphrase, out byte[] plaintext, out string error)
        {
            plaintext = Array.Empty<byte>();
            error = "";
            try
            {
                if (blob == null || blob.Length < HeaderLen) { error = "형식 오류 — 파일이 너무 짧음"; return false; }
                for (int i = 0; i < Magic.Length; i++)
                    if (blob[i] != Magic[i]) { error = "MS2-E 동기화 번들이 아님"; return false; }
                int off = Magic.Length;
                byte ver = blob[off++];
                if (ver != Ver) { error = $"지원하지 않는 번들 버전: {ver}"; return false; }

                var salt = blob[off..(off + SaltLen)]; off += SaltLen;
                var nonce = blob[off..(off + NonceLen)]; off += NonceLen;
                var tag = blob[off..(off + TagLen)]; off += TagLen;
                var cipher = blob[off..];

                var key = DeriveKey(passphrase, salt);
                var pt = new byte[cipher.Length];
                using (var gcm = new AesGcm(key, TagLen))
                    gcm.Decrypt(nonce, cipher, tag, pt);   // 변조/오암호 → CryptographicException
                plaintext = pt;
                return true;
            }
            catch (CryptographicException) { error = "복호화 실패 — 암호가 틀리거나 파일이 손상됨"; return false; }
            catch (Exception ex) { error = "복호화 오류: " + ex.Message; return false; }
        }

        public static byte[] Decrypt(byte[] blob, string passphrase)
        {
            if (TryDecrypt(blob, passphrase, out var pt, out var err)) return pt;
            throw new CryptographicException(err);
        }
    }
}
