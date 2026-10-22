// ============================================================================
// BrokerSettingsStatusAdapter — v12.18.58-DEV: IBrokerSettingsStatusPort 구현(판독 전용)
//
//   ① KIS 모의 2종: Settings 의 KIS.Profile.{PaperDomestic|PaperOverseas}.{AppKey · AppSecret · Account} 원시값을 판독해
//     enc:v1(DPAPI) 봉인 여부를 판정하고, 봉인된 값만 복호 직후 CredentialMask.First2 로 마스킹한다(평문은 DTO 로 나가지 않음).
//     평문 저장(비봉인) 값은 복호 · 마스킹 없이 '****' 만 — 평문 저장을 UI 가 경고로 렌더하도록 Sealed=false.
//   ② 레거시 단일 키(KIS.AppKey/AppSecret/Account · Upbit.AccessKey/SecretKey — ApiKeyVault 저장분): 동일 규칙.
//   ③ 크립토 모의: CryptoPaperExchange 가 DI 에 등록된 경우에만 seqlock Snapshot() 판독. 미등록 = Wired=false(날조 0).
//
//   ★불변: liveArmed:false — 저장 · 발주 · 무장 경로 0(판독만) · DB 쓰기 0.
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Brokers;
using StockManager.Engines.Brokers.Crypto;

namespace StockManager.Adapters
{
    public sealed class BrokerSettingsStatusAdapter : IBrokerSettingsStatusPort
    {
        private const string PlainMask = "****";   // 비봉인(평문 저장) 값 — 앞자리도 노출하지 않음

        private readonly LocalDatabase _db;
        private readonly CryptoPaperExchange? _crypto;

        public BrokerSettingsStatusAdapter(IServiceProvider sp)
        {
            _db = (LocalDatabase)(sp.GetService(typeof(LocalDatabase)) ?? throw new InvalidOperationException("LocalDatabase 미등록"));
            _crypto = sp.GetService(typeof(CryptoPaperExchange)) as CryptoPaperExchange;   // 미등록 → null → Wired=false
        }

        public BrokerSettingsSnapshot GetSnapshot() => new(
            Profile("PaperDomestic", "KIS 국내 모의(VTS)"),
            Profile("PaperOverseas", "KIS 해외 모의(NASD · NYSE · AMEX)"),
            Field("App Key", "KIS.AppKey"),
            Field("App Secret", "KIS.AppSecret"),
            Field("계좌번호", "KIS.Account"),
            Field("Access Key", "Upbit.AccessKey"),
            Field("Secret Key", "Upbit.SecretKey"),
            Crypto());

        private KisProfileStatus Profile(string profile, string label)
        {
            string prefix = "KIS.Profile." + profile + ".";
            return new KisProfileStatus(profile, label,
                Field("App Key", prefix + "AppKey"),
                Field("App Secret", prefix + "AppSecret"),
                Field("계좌번호", prefix + "Account"));
        }

        private CredentialFieldStatus Field(string name, string key)
        {
            string? raw;
            try { raw = _db.GetSetting(key); } catch { raw = null; }
            if (string.IsNullOrEmpty(raw)) return new CredentialFieldStatus(name, false, false, "");
            if (!Secret.IsProtected(raw)) return new CredentialFieldStatus(name, true, false, PlainMask);
            string masked = CredentialMask.First2(Secret.Unprotect(raw));   // 복호 실패 = "" → 봉인 판정 취소
            return masked.Length == 0
                ? new CredentialFieldStatus(name, true, false, PlainMask)
                : new CredentialFieldStatus(name, true, true, masked);
        }

        private CryptoPaperView Crypto()
        {
            decimal initial = CryptoPaperExchange.InitialKrwRaw / (decimal)CryptoPaperExchange.Scale;
            if (_crypto is null) return new CryptoPaperView(false, initial, 0m, 0m, 0m, 0m, -1, 0);
            var s = _crypto.Snapshot();
            long ageMs = -1;
            if (s.QuoteUtcMicros > 0)
            {
                long nowMicros = (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;
                ageMs = Math.Max(0, (nowMicros - s.QuoteUtcMicros) / 1000);
            }
            decimal scale = CryptoPaperExchange.Scale;
            return new CryptoPaperView(true, initial, s.KrwRaw / scale, s.BtcRaw / scale, s.BidRaw / scale, s.AskRaw / scale, ageMs, s.FillCount);
        }
    }
}
