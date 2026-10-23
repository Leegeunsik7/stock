// ============================================================================
// StockDisplayNameResolver — v12.18.70-DEV: 아레나 종목 코드 → 한글 종목명(순수 · 정적 사전 · UI 콜드 경로)
//
//   국내 6자리 코드(000660) · 미국 티커(NVDA) · 업비트 마켓(KRW-BTC) 공용. 미등록 코드 = 이름 없음(null) — 날조 0, 코드 그대로 표시.
//   Display = "SK하이닉스 000660"(한글명 + 코드 병기) · 미등록 = 코드만.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;

namespace MS2E.UI.Portable.Helpers
{
    public static class StockDisplayNameResolver
    {
        private static readonly Dictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase)
        {
            // 국내 대형주(합성 데모 5종 + 브리지 실시간 주요 종목)
            ["005930"] = "삼성전자", ["000660"] = "SK하이닉스", ["373220"] = "LG에너지솔루션", ["207940"] = "삼성바이오로직스",
            ["005380"] = "현대차", ["068270"] = "셀트리온", ["000270"] = "기아", ["005490"] = "POSCO홀딩스", ["035420"] = "NAVER",
            ["035720"] = "카카오", ["051910"] = "LG화학", ["006400"] = "삼성SDI", ["105560"] = "KB금융", ["055550"] = "신한지주",
            ["012330"] = "현대모비스", ["028260"] = "삼성물산", ["005935"] = "삼성전자우", ["066570"] = "LG전자", ["003670"] = "포스코퓨처엠",
            ["086790"] = "하나금융지주",
            // 미국 빅테크
            ["AAPL"] = "애플", ["NVDA"] = "엔비디아", ["MSFT"] = "마이크로소프트", ["AMZN"] = "아마존", ["TSLA"] = "테슬라",
            ["GOOGL"] = "알파벳", ["META"] = "메타", ["AVGO"] = "브로드컴", ["AMD"] = "AMD",
            // 업비트 원화 마켓
            ["KRW-BTC"] = "비트코인", ["KRW-ETH"] = "이더리움", ["KRW-XRP"] = "리플", ["KRW-SOL"] = "솔라나",
        };

        /// <summary>국내 종목 정수 코드 → 6자리 문자열(660 → "000660").</summary>
        public static string KrxCode(long symbol) => symbol.ToString("D6", CultureInfo.InvariantCulture);

        /// <summary>한글 종목명(미등록 · 공백 = null).</summary>
        public static string? Name(string? code)
            => !string.IsNullOrWhiteSpace(code) && s_names.TryGetValue(code.Trim(), out var n) ? n : null;

        /// <summary>국내 정수 코드 한글명(미등록 = null).</summary>
        public static string? Name(long krxSymbol) => Name(KrxCode(krxSymbol));

        /// <summary>한글명(미등록 = 코드 그대로).</summary>
        public static string NameOrCode(string code) => Name(code) ?? code;

        /// <summary>"SK하이닉스 000660"(미등록 = 코드만).</summary>
        public static string Display(string code) => Name(code) is { } n ? n + " " + code : code;

        /// <summary>국내 정수 코드 표시("SK하이닉스 000660").</summary>
        public static string Display(long krxSymbol) => Display(KrxCode(krxSymbol));
    }
}
