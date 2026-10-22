// ============================================================================
// BrokerSettingsStatusPorts — v12.18.58-DEV: 거래 설정(BrokerSettingsPage) 자산군 상태 Port & 순수 DTO (조회 전용)
//
//   KIS 모의 2종(PaperDomestic · PaperOverseas) 자격증명 봉인 상태와 Upbit 공개 시세 · 24/7 크립토 모의 체결기 상태를
//   UI 가 LocalDatabase · Secret(DPAPI) · CryptoPaperExchange 구상 타입 0개 참조로 렌더하게 하는 경계.
//
//   ★불변: liveArmed:false(★이 Port 에는 저장/발주/무장 메서드가 없다 — 판독 전용) ·
//     평문 노출 0(어댑터가 복호 직후 CredentialMask 로 마스킹 — DTO 에는 마스킹 문자열만) ·
//     fail-closed(미배선 · 복호 실패 · 평문 저장 → Sealed=false / Wired=false · 날조 0).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Brokers
{
    /// <summary>자격증명 필드 1개의 봉인 상태. Masked = 앞 2자 외 '*'(미설정 = 빈 문자열).</summary>
    public readonly record struct CredentialFieldStatus(string Name, bool Present, bool Sealed, string Masked);

    /// <summary>KIS 프로필(PaperDomestic · PaperOverseas) 자격증명 상태. AllSealed = 3필드 전부 존재 + 전부 DPAPI(enc:v1).</summary>
    public sealed record KisProfileStatus(string Profile, string Label, CredentialFieldStatus AppKey, CredentialFieldStatus AppSecret, CredentialFieldStatus Account)
    {
        public bool AllSealed => AppKey.Sealed && AppSecret.Sealed && Account.Sealed;
        public bool AnyPlaintext => (AppKey.Present && !AppKey.Sealed) || (AppSecret.Present && !AppSecret.Sealed) || (Account.Present && !Account.Sealed);
    }

    /// <summary>
    /// 크립토(Upbit) 모의 상태. Wired=false 면 런타임에 공개 시세/모의 체결기가 배선되지 않음(나머지 런타임 필드 무의미 · 0).
    /// InitialKrw = 모의 체결기 초기 예수금(상수) · QuoteAgeMs &lt; 0 = 호가 미수신.
    /// </summary>
    public readonly record struct CryptoPaperView(bool Wired, decimal InitialKrw, decimal KrwBalance, decimal BtcBalance, decimal Bid, decimal Ask, long QuoteAgeMs, long FillCount);

    /// <summary>거래 설정 화면 상태 스냅샷.</summary>
    public sealed record BrokerSettingsSnapshot(
        KisProfileStatus PaperDomestic,
        KisProfileStatus PaperOverseas,
        CredentialFieldStatus LegacyKisAppKey,
        CredentialFieldStatus LegacyKisAppSecret,
        CredentialFieldStatus LegacyKisAccount,
        CredentialFieldStatus UpbitAccessKey,
        CredentialFieldStatus UpbitSecretKey,
        CryptoPaperView Crypto);

    /// <summary>거래 설정 상태 Port. ★판독 전용 — 저장 · 발주 · 무장 메서드 없음(liveArmed:false 물리 봉인).</summary>
    public interface IBrokerSettingsStatusPort
    {
        BrokerSettingsSnapshot GetSnapshot();
    }

    /// <summary>자격증명 마스킹(순수 · 어댑터 공용). 앞 2자만 노출 · 나머지는 길이만큼 '*'(최대 16). 2자 이하 = 전부 '*'.</summary>
    public static class CredentialMask
    {
        public const int Visible = 2;
        public const int MaxStars = 16;

        public static string First2(string? plain)
        {
            if (string.IsNullOrEmpty(plain)) return "";
            if (plain.Length <= Visible) return new string('*', plain.Length);
            int stars = Math.Min(plain.Length - Visible, MaxStars);
            return string.Create(Visible + stars, plain, static (dst, src) =>
            {
                dst[0] = src[0];
                dst[1] = src[1];
                dst[Visible..].Fill('*');
            });
        }
    }
}
