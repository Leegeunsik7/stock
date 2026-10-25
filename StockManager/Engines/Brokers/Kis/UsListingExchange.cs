// ============================================================================
// UsListingExchange — [v12.18.81-DEV] 플루토 US 감시 카탈로그(S&P 100 · 나스닥 100 = 100종) 티커 → 상장 거래소(KIS 시세 계열 EXCD 결정)
//
//   KIS 해외 일봉(HHDFS76240000)은 EXCD(NAS/NYS/AMS)가 상장 거래소와 맞아야 봉을 준다 → 티커별 주 상장 거래소 정적 표.
//   ★정적 스냅샷 — 상장 이전(예: LIN 2023 · PLTR 2024 · WMT 2025 나스닥 이전) 반영. 이후 이전 = 표 갱신 필요(런타임 조회 0).
//    표가 틀리면 KIS 가 빈 봉을 돌려준다 → 스웜 슬롯 NoData(날조 0 · 다른 거래소 재시도 0).
//   fail-closed: 표에 없는 티커 = None(조회 0 · 레이트 예산 보존).
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Engines.Brokers.Kis
{
    public static class UsListingExchange
    {
        private static readonly string[] s_nasdaq =
        {
            "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "GOOG", "META", "AVGO", "TSLA", "COST",
            "NFLX", "AMD", "PEP", "ADBE", "LIN", "CSCO", "INTU", "QCOM", "TXN", "AMGN",
            "ISRG", "CMCSA", "HON", "AMAT", "BKNG", "SBUX", "GILD", "MU", "INTC", "PYPL",
            "ADP", "PLTR", "WMT",
            "ADI", "LRCX", "MDLZ", "KLAC", "PANW", "SNPS", "CDNS", "MRVL", "CRWD", "ABNB",
            "MELI", "ASML", "FTNT", "ORLY", "CSX", "MAR", "CTAS", "REGN",
        };

        private static readonly string[] s_nyse =
        {
            "BRK.B", "JPM", "LLY", "V", "UNH", "XOM", "MA", "JNJ", "PG", "HD",
            "ABBV", "BAC", "CRM", "ORCL", "CVX", "KO", "MRK", "TMO", "ACN", "MCD",
            "ABT", "WFC", "DIS", "DHR", "CAT", "VZ", "IBM", "PM", "GE", "NOW",
            "UNP", "SPGI", "GS", "NEE", "RTX", "LOW", "T", "UBER", "AXP", "BLK",
            "MS", "LMT", "DE", "MDT", "BMY", "C", "SCHW", "MO", "PFE",
        };

        private static readonly Dictionary<string, UsExchange> s_map = Build();

        private static Dictionary<string, UsExchange> Build()
        {
            var d = new Dictionary<string, UsExchange>(s_nasdaq.Length + s_nyse.Length, StringComparer.Ordinal);
            foreach (var t in s_nasdaq) d.Add(t, UsExchange.Nasdaq);
            foreach (var t in s_nyse) d.Add(t, UsExchange.Nyse);
            return d;
        }

        public static int Count => s_map.Count;

        /// <summary>티커(서수 · 대소문자 구분) → 상장 거래소. 미등재 = None.</summary>
        public static UsExchange Of(string ticker) => ticker is not null && s_map.TryGetValue(ticker, out var x) ? x : UsExchange.None;
    }
}
