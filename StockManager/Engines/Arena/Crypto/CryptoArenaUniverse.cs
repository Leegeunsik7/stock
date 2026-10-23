// ============================================================================
// CryptoArenaUniverse — v12.18.66-DEV: 업비트 가상자산 아레나 종목 표(KRW 마켓 4종 · 마켓 코드 ↔ long 키 · 팩터 민감도)
//
//   종목 키 = 업비트 마켓 코드 ASCII 1~8자("KRW-BTC")를 상위 바이트부터 채운 long(Pack) — 핫패스는 정수 비교만(문자열 0). Unpack/Label = 콜드 경로.
//   민감도(×100): 글로벌 BTC 24시간 등락 베타 · 김치프리미엄 감응(프리미엄 +1bp 당 음의 기여 배율 · 과열 회귀).
//   ★표 밖 마켓 = 엔진/주입기 폐기(fail-closed). ★실시간 업비트 스트림(UpbitWebSocketFeedAdapter)은 KRW-BTC 만 구독 — 나머지는 합성 데모 전용.
// ============================================================================
using System;

namespace StockManager.Engines.Arena.Crypto
{
    public static class CryptoArenaUniverse
    {
        public const int Count = 4;
        public const int Btc = 0, Eth = 1, Xrp = 2, Sol = 3;

        private static readonly string[] s_markets = { "KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL" };
        private static readonly string[] s_coins = { "BTC", "ETH", "XRP", "SOL" };
        private static readonly long[] s_symbols = { Pack("KRW-BTC"), Pack("KRW-ETH"), Pack("KRW-XRP"), Pack("KRW-SOL") };
        private static readonly int[] s_globalBetaX100 = { 100, 120, 140, 150 };
        private static readonly int[] s_kimchiSensX100 = { 100, 110, 130, 120 };
        /// <summary>합성 데모 시작가(원 · 업비트 호가 격자 정렬).</summary>
        private static readonly long[] s_syntheticStartWon = { 150_000_000, 5_500_000, 3_500, 280_000 };

        public static long Symbol(int index) => s_symbols[index];
        public static string Market(int index) => s_markets[index];
        public static string Coin(int index) => s_coins[index];
        public static int GlobalBetaX100(int index) => s_globalBetaX100[index];
        public static int KimchiSensX100(int index) => s_kimchiSensX100[index];
        public static long SyntheticStartWon(int index) => s_syntheticStartWon[index];

        /// <summary>종목 키 → 표 인덱스(δ=0 · 표 밖 = −1).</summary>
        public static int IndexOf(long symbol)
        {
            for (int i = 0; i < Count; i++) if (s_symbols[i] == symbol) return i;
            return -1;
        }

        /// <summary>마켓 코드 → long 키(ASCII 대문자/숫자/'-' 1~8자 · 형식 이탈 = 0).</summary>
        public static long Pack(ReadOnlySpan<char> market)
        {
            if (market.Length == 0 || market.Length > 8) return 0;
            long v = 0;
            foreach (char ch in market)
            {
                if (ch is not ((>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-')) return 0;
                v = (v << 8) | ch;
            }
            return v;
        }

        /// <summary>long 키 → 마켓 코드(콜드 경로). 0 이하 = "".</summary>
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

        /// <summary>표시명 "KRW-BTC"(콜드 경로 · 표 밖 = 키 복원값).</summary>
        public static string Label(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? Unpack(symbol) : s_markets[i];
        }

        /// <summary>코인 단위 "BTC"(콜드 경로 · 표 밖 = "").</summary>
        public static string CoinOf(long symbol)
        {
            int i = IndexOf(symbol);
            return i < 0 ? "" : s_coins[i];
        }
    }
}
