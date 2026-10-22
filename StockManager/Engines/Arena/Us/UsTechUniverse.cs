// ============================================================================
// UsTechUniverse — v12.18.61-DEV: 미국 테크 아레나 종목 표(빅테크 5종 · 티커 ↔ long 키 · 거래소 · 팩터 민감도)
//
//   종목 키 = 티커 ASCII 1~8자를 상위 바이트부터 채운 long(Pack) — 핫패스는 정수 비교만(문자열 0). Unpack/Label = 콜드 경로.
//   거래소 = UsExchangeCode 매핑(거래 계열 NASD/NYSE · 시세 계열 NAS/NYS). 5종 모두 나스닥 상장 → NASD/NAS.
//   민감도(×100): QQQ 베타 · 반도체 ETF(SOXX) 베타 · 10년물 금리 감응(금리 +1bp 당 음의 기여 배율).
//   ★표 밖 티커 = 엔진/주입기 폐기(fail-closed).
// ============================================================================
using System;
using StockManager.Engines.Brokers.Kis;

namespace StockManager.Engines.Arena.Us
{
    public static class UsTechUniverse
    {
        public const int Count = 5;
        public const int Aapl = 0, Nvda = 1, Msft = 2, Amzn = 3, Tsla = 4;

        private static readonly string[] s_tickers = { "AAPL", "NVDA", "MSFT", "AMZN", "TSLA" };
        private static readonly byte[][] s_tickerUtf8 = { "AAPL"u8.ToArray(), "NVDA"u8.ToArray(), "MSFT"u8.ToArray(), "AMZN"u8.ToArray(), "TSLA"u8.ToArray() };
        private static readonly UsExchange[] s_exchange = { UsExchange.Nasdaq, UsExchange.Nasdaq, UsExchange.Nasdaq, UsExchange.Nasdaq, UsExchange.Nasdaq };
        private static readonly long[] s_symbols = { Pack("AAPL"), Pack("NVDA"), Pack("MSFT"), Pack("AMZN"), Pack("TSLA") };
        private static readonly int[] s_qqqBetaX100 = { 110, 130, 100, 120, 150 };
        private static readonly int[] s_semiBetaX100 = { 20, 150, 20, 10, 30 };
        private static readonly int[] s_rateSensX100 = { 80, 120, 90, 100, 150 };
        /// <summary>합성 데모 시작가(센트).</summary>
        private static readonly long[] s_syntheticStartCents = { 22_500, 13_500, 42_000, 19_000, 25_000 };

        public static long Symbol(int index) => s_symbols[index];
        public static string Ticker(int index) => s_tickers[index];
        public static ReadOnlySpan<byte> TickerUtf8(int index) => s_tickerUtf8[index];
        public static UsExchange Exchange(int index) => s_exchange[index];
        public static int QqqBetaX100(int index) => s_qqqBetaX100[index];
        public static int SemiBetaX100(int index) => s_semiBetaX100[index];
        public static int RateSensX100(int index) => s_rateSensX100[index];
        public static long SyntheticStartCents(int index) => s_syntheticStartCents[index];

        /// <summary>종목 키 → 표 인덱스(δ=0 · 표 밖 = −1).</summary>
        public static int IndexOf(long symbol)
        {
            for (int i = 0; i < Count; i++) if (s_symbols[i] == symbol) return i;
            return -1;
        }

        /// <summary>티커 → long 키(ASCII 대문자/숫자/'.'/'/' 1~8자 · 형식 이탈 = 0).</summary>
        public static long Pack(ReadOnlySpan<char> ticker)
        {
            if (ticker.Length > 8 || !UsExchangeCode.IsValidSymbol(ticker)) return 0;
            long v = 0;
            foreach (char ch in ticker) v = (v << 8) | ch;
            return v;
        }

        /// <summary>long 키 → 티커(콜드 경로). 0 이하 = "".</summary>
        public static string Unpack(long symbol)
        {
            if (symbol <= 0) return "";
            Span<char> buf = stackalloc char[8];
            int n = 0;
            for (int shift = 56; shift >= 0; shift -= 8)
            {
                char ch = (char)((symbol >> shift) & 0xFF);
                if (ch != 0 || n > 0) buf[n++] = ch;
            }
            return new string(buf[..n]);
        }

        /// <summary>표시명 "NVDA(NASD)"(콜드 경로 · 표 밖 = 티커만).</summary>
        public static string Label(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? Unpack(symbol) : s_tickers[i] + "(" + UsExchangeCode.TradeCode(s_exchange[i]) + ")";
        }
    }
}
