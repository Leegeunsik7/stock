// ============================================================================
// LogSecretCatalog — [PII 하드닝] 로그 차폐 대상 비밀 키 단일 원천
//
//   교정 전(실측): Program.RegisterLogSecrets 가 6개 키만 인라인 나열 — 금고(SetSecret)에 저장되는 Toss.ClientId/ClientSecret/
//   Account(실 브로커 OAuth), KIS.Account, Email.Pass, Cartridge.PrivKey 가 누락돼 이 값들이 로그에 평문으로 남을 수 있었다.
//   이제 키 목록은 여기 한 곳이며, LogSecretCatalogTests 가 소스 전체의 SetSecret("…") 키를 스캔해 누락을 자동 적발한다.
//   분류: Credentials = 유출 시 인터락 Trip(발주 토큰 소거·무장 차단) + 마스킹 / Identifiers = 마스킹만(정상 로그에도 등장 가능).
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    public static class LogSecretCatalog
    {
        /// <summary>자격증명(금고 GetSecret) — 원문 로그 유출 시도 = 침해로 간주(RawCredentialLeakInterlock).</summary>
        public static readonly IReadOnlyList<string> CredentialSecretKeys = new[]
        {
            "Gemini.ApiKey", "KIS.AppKey", "KIS.AppSecret", "Upbit.AccessKey", "Upbit.SecretKey", "Telegram.Token",
            "Toss.ClientSecret", "Email.Pass", "Cartridge.PrivKey",
        };

        /// <summary>식별자(금고 GetSecret) — 마스킹만.</summary>
        public static readonly IReadOnlyList<string> IdentifierSecretKeys = new[] { "KIS.Account", "Toss.Account", "Toss.ClientId" };

        /// <summary>식별자(평문 설정 GetSetting) — 마스킹만.</summary>
        public static readonly IReadOnlyList<string> IdentifierSettingKeys = new[] { "KIS.AccountNo", "Paper.Account" };

        /// <summary>금고/설정에서 값을 읽어 LogRedactor(전부)·인터락(자격증명만)에 등록. 반환 = (자격증명 수, 식별자 수).</summary>
        public static (int Credentials, int Identifiers) RegisterAll(Func<string, string?> getSecret, Func<string, string?> getSetting,
                                                                     LogRedactor redactor, RawCredentialLeakInterlock? interlock)
        {
            if (getSecret is null) throw new ArgumentNullException(nameof(getSecret));
            if (getSetting is null) throw new ArgumentNullException(nameof(getSetting));
            if (redactor is null) throw new ArgumentNullException(nameof(redactor));
            int c = 0, i = 0;
            foreach (var key in CredentialSecretKeys)
            {
                string? v = Safe(getSecret, key);
                if (string.IsNullOrWhiteSpace(v)) continue;
                redactor.Register(v);
                interlock?.RegisterCredential(v);
                c++;
            }
            foreach (var key in IdentifierSecretKeys) { string? v = Safe(getSecret, key); if (!string.IsNullOrWhiteSpace(v)) { redactor.Register(v); i++; } }
            foreach (var key in IdentifierSettingKeys) { string? v = Safe(getSetting, key); if (!string.IsNullOrWhiteSpace(v)) { redactor.Register(v); i++; } }
            return (c, i);
        }

        private static string? Safe(Func<string, string?> get, string key)
        {
            try { return get(key); } catch { return null; }   // 개별 키 판독 실패가 나머지 등록을 막지 않음
        }
    }
}
