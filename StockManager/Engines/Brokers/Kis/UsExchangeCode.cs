// ============================================================================
// UsExchangeCode — [v12.18.57-DEV] KIS 해외주식 미국 3대 거래소 식별자 ↔ 거래소 코드 매핑(무할당 · δ=0)
//
//  ★KIS 는 같은 거래소를 API 계열별로 다른 코드로 부른다(혼용 = 조회 실패 또는 엉뚱한 거래소 판독):
//   거래 계열(잔고 VTTS3012R · 주문 · 체결 — 파라미터 OVRS_EXCG_CD · 4자): NASD(나스닥) · NYSE(뉴욕) · AMEX(아멕스)
//   시세 계열(현재체결가 HHDFS00000300 — 파라미터 EXCD · 3자):            NAS        · NYS      · AMS
//   ※ 모의(VTS) 잔고는 거래소별 개별 조회(실전의 'NASD = 미국 전체' 와 다름) — 3대 거래소를 각각 질의해야 미국 보유가 전부 보인다.
//   ※ NYSE Arca 상장 ETF(SPY 등)는 KIS 분류상 AMEX/AMS 로 조회한다.
//  코드는 전부 컴파일 타임 상수(u8 리터럴 = 어셈블리 데이터 · 문자열 = 인턴 상수) — 판독/역매핑 호출당 힙 0.
//  fail-closed: 미지정(None)/범위 밖 값 → 빈 코드 · 미지 코드 역매핑 → None(호출 측 송출/판독 거부).
// ============================================================================
using System;

namespace StockManager.Engines.Brokers.Kis
{
    public enum UsExchange : byte
    {
        None = 0,                   // default ≠ 유효
        Nasdaq = 1,
        Nyse = 2,
        Amex = 3,
    }

    public static class UsExchangeCode
    {
        public const int Count = 3;

        private static readonly UsExchange[] s_all = { UsExchange.Nasdaq, UsExchange.Nyse, UsExchange.Amex };

        /// <summary>3대 거래소(조회 순서 고정: 나스닥 → 뉴욕 → 아멕스).</summary>
        public static ReadOnlySpan<UsExchange> All => s_all;

        public static bool IsValid(UsExchange ex) => ex is UsExchange.Nasdaq or UsExchange.Nyse or UsExchange.Amex;

        /// <summary>거래 계열 코드(OVRS_EXCG_CD · 잔고/주문). 무효 = "".</summary>
        public static string TradeCode(UsExchange ex) => ex switch
        {
            UsExchange.Nasdaq => "NASD",
            UsExchange.Nyse => "NYSE",
            UsExchange.Amex => "AMEX",
            _ => "",
        };

        /// <summary>시세 계열 코드(EXCD · 현재체결가). 무효 = "".</summary>
        public static string QuoteCode(UsExchange ex) => ex switch
        {
            UsExchange.Nasdaq => "NAS",
            UsExchange.Nyse => "NYS",
            UsExchange.Amex => "AMS",
            _ => "",
        };

        /// <summary>거래 계열 코드 UTF-8(무할당 판독 비교용). 무효 = 빈 스팬.</summary>
        public static ReadOnlySpan<byte> TradeCodeUtf8(UsExchange ex) => ex switch
        {
            UsExchange.Nasdaq => "NASD"u8,
            UsExchange.Nyse => "NYSE"u8,
            UsExchange.Amex => "AMEX"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };

        /// <summary>시세 계열 코드 UTF-8(무할당 판독 비교용). 무효 = 빈 스팬.</summary>
        public static ReadOnlySpan<byte> QuoteCodeUtf8(UsExchange ex) => ex switch
        {
            UsExchange.Nasdaq => "NAS"u8,
            UsExchange.Nyse => "NYS"u8,
            UsExchange.Amex => "AMS"u8,
            _ => ReadOnlySpan<byte>.Empty,
        };

        /// <summary>거래 계열 코드 역매핑(서수 · 대소문자 구분 · 공백 불허). 미지 = None.</summary>
        public static UsExchange FromTradeCode(ReadOnlySpan<byte> code)
            => code.SequenceEqual("NASD"u8) ? UsExchange.Nasdaq
             : code.SequenceEqual("NYSE"u8) ? UsExchange.Nyse
             : code.SequenceEqual("AMEX"u8) ? UsExchange.Amex
             : UsExchange.None;

        /// <summary>시세 계열 코드 역매핑(서수 · 대소문자 구분 · 공백 불허). 미지 = None.</summary>
        public static UsExchange FromQuoteCode(ReadOnlySpan<byte> code)
            => code.SequenceEqual("NAS"u8) ? UsExchange.Nasdaq
             : code.SequenceEqual("NYS"u8) ? UsExchange.Nyse
             : code.SequenceEqual("AMS"u8) ? UsExchange.Amex
             : UsExchange.None;

        /// <summary>미국 종목 심볼 형식(ASCII 대문자 · 숫자 · '.' · '/' · 1~12자 — 쿼리 주입/공백/소문자 = 거부).</summary>
        public static bool IsValidSymbol(ReadOnlySpan<char> symbol)
        {
            if (symbol.Length is < 1 or > 12) return false;
            foreach (char ch in symbol)
                if (!(ch is >= 'A' and <= 'Z' || ch is >= '0' and <= '9' || ch == '.' || ch == '/')) return false;
            return true;
        }
    }
}
