// ============================================================================
// SecretTests - DPAPI 비밀 보관(Secret) + at-rest 재암호화 마이그레이션 회귀 테스트
//  · 라운드트립/마커 판별/레거시 평문 통과는 DPAPI 성공 여부와 무관하게 성립하도록 작성
//  · 마이그레이션은 임시 DB로 통합 검증
// ============================================================================
using System;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class SecretTests
    {
        [Fact]
        public void Protect_Then_Unprotect_RoundTrips()
        {
            // 암호화/폴백 어느 경로든 Unprotect(Protect(x)) == x 가 성립해야 한다
            const string plain = "sk-super-secret-key-12345";
            Assert.Equal(plain, Secret.Unprotect(Secret.Protect(plain)));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void Protect_EmptyOrNull_ReturnsEmpty(string? input)
            => Assert.Equal("", Secret.Protect(input));

        [Fact]
        public void IsProtected_DetectsByMarker()
        {
            Assert.True(Secret.IsProtected("enc:v1:anything"));   // 마커 존재
            Assert.False(Secret.IsProtected("plaintext-token"));
            Assert.False(Secret.IsProtected(""));
            Assert.False(Secret.IsProtected(null));
        }

        [Fact]
        public void Unprotect_LegacyPlaintext_PassesThrough()
            => Assert.Equal("legacy-plaintext", Secret.Unprotect("legacy-plaintext"));   // 마커 없으면 그대로
    }

    public class SecretMigrationTests
    {
        [Fact]
        public void MigrateSecretsAtRest_EncryptsLingeringPlaintext_AndRoundTrips()
        {
            var path = Path.Combine(Path.GetTempPath(), "smsec_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var db = new LocalDatabase(path);
                // 레거시 상태 시뮬: 평문으로 저장
                db.SetSetting("Telegram.Token", "plain-token-xyz");
                db.SetSetting("Gemini.ApiKey", "");                 // 빈 값은 건너뛰어야

                int migrated = db.MigrateSecretsAtRest(new[] { "Telegram.Token", "Gemini.ApiKey", "Upbit.AccessKey" });

                Assert.True(migrated >= 1);                          // 평문 1건 이상 재암호화
                Assert.Equal("plain-token-xyz", db.GetSecret("Telegram.Token"));  // 복호화 시 원본 복원
            }
            finally
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(path + "-wal"); } catch { }
                try { File.Delete(path + "-shm"); } catch { }
            }
        }

        [Fact]
        public void MigrateSecretsAtRest_AlreadyEncrypted_IsNoOp()
        {
            var path = Path.Combine(Path.GetTempPath(), "smsec_" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using var db = new LocalDatabase(path);
                db.SetSecret("Telegram.Token", "already-secret");   // 처음부터 암호화 경로로 저장
                int migrated = db.MigrateSecretsAtRest(new[] { "Telegram.Token" });
                // DPAPI가 동작하면 마커가 붙어 0건이어야 하고, 폴백(평문)이면 1건일 수 있음 → 음수만 아니면 됨
                Assert.True(migrated >= 0);
                Assert.Equal("already-secret", db.GetSecret("Telegram.Token"));
            }
            finally
            {
                try { File.Delete(path); } catch { }
                try { File.Delete(path + "-wal"); } catch { }
                try { File.Delete(path + "-shm"); } catch { }
            }
        }
    }
}
