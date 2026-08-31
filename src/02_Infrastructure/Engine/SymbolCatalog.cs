// ============================================================================
// SymbolCatalog — 종목·코인 검색 카탈로그 (이름→코드 자동 채움의 데이터 소스)
//
//  초보자는 종목코드(005930)를 모른다. "삼성전자"·"비트코인"으로 검색해 코드·시장·통화를
//  자동 채우도록, 앱에 대표 종목을 내장한다(외부 의존 0%·즉시 검색). 코인은 런타임에 업비트
//  라이브 마켓(전 종목)과 병합 검색하되, 네트워크 실패 시 내장 폴백으로 그레이스풀 강등한다.
//  카탈로그에 없는 종목은 폼에서 수동 입력으로 폴백(고급·미수록 대응).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace MS2E.Infrastructure
{
    /// <summary>검색 결과 1건 — 코드·이름·시장·통화. 폼에 그대로 자동 채워진다.</summary>
    public readonly struct SymbolEntry : MS2E.UI.Portable.Ports.Portfolio.ISymbolEntryView   // Phase 94-J: 공변 구현
    {
        public string   Code     { get; }
        public string   Name     { get; }
        public Market    Market   { get; }
        public Currency  Currency { get; }
        public bool      IsCrypto { get; }
        MS2E.UI.Portable.Ports.Portfolio.PfMarket MS2E.UI.Portable.Ports.Portfolio.ISymbolEntryView.Market
            => (MS2E.UI.Portable.Ports.Portfolio.PfMarket)(int)Market;
        MS2E.UI.Portable.Ports.Portfolio.PfCurrency MS2E.UI.Portable.Ports.Portfolio.ISymbolEntryView.Currency
            => (MS2E.UI.Portable.Ports.Portfolio.PfCurrency)(int)Currency;

        public SymbolEntry(string code, string name, Market market, Currency currency, bool isCrypto = false)
        {
            Code = code; Name = name; Market = market; Currency = currency; IsCrypto = isCrypto;
        }

        public bool HasValue => !string.IsNullOrEmpty(Code);

        /// <summary>UI 배지용 시장 라벨.</summary>
        public string MarketLabel => IsCrypto ? "코인" : Market switch
        {
            Market.KRX_KOSPI  => "코스피",
            Market.KRX_KOSDAQ => "코스닥",
            Market.US_NYSE    => "NYSE",
            Market.US_NASDAQ  => "NASDAQ",
            Market.US_AMEX    => "AMEX",
            Market.CRYPTO     => "코인",
            _                 => Market.ToString(),
        };
    }

    public static class SymbolCatalog
    {
        // ── 코스피·코스닥 대표주 ────────────────────────────────────────────────
        private static readonly SymbolEntry[] _stocks =
        {
            // 코스피
            S("005930", "삼성전자",         Market.KRX_KOSPI),
            S("000660", "SK하이닉스",       Market.KRX_KOSPI),
            S("373220", "LG에너지솔루션",   Market.KRX_KOSPI),
            S("207940", "삼성바이오로직스", Market.KRX_KOSPI),
            S("005935", "삼성전자우",       Market.KRX_KOSPI),
            S("005380", "현대차",           Market.KRX_KOSPI),
            S("000270", "기아",             Market.KRX_KOSPI),
            S("068270", "셀트리온",         Market.KRX_KOSPI),
            S("005490", "POSCO홀딩스",      Market.KRX_KOSPI),
            S("035420", "NAVER",            Market.KRX_KOSPI),
            S("035720", "카카오",           Market.KRX_KOSPI),
            S("051910", "LG화학",           Market.KRX_KOSPI),
            S("006400", "삼성SDI",          Market.KRX_KOSPI),
            S("105560", "KB금융",           Market.KRX_KOSPI),
            S("055550", "신한지주",         Market.KRX_KOSPI),
            S("086790", "하나금융지주",     Market.KRX_KOSPI),
            S("012330", "현대모비스",       Market.KRX_KOSPI),
            S("028260", "삼성물산",         Market.KRX_KOSPI),
            S("066570", "LG전자",           Market.KRX_KOSPI),
            S("003670", "포스코퓨처엠",     Market.KRX_KOSPI),
            S("015760", "한국전력",         Market.KRX_KOSPI),
            S("017670", "SK텔레콤",         Market.KRX_KOSPI),
            S("030200", "KT",               Market.KRX_KOSPI),
            S("033780", "KT&G",             Market.KRX_KOSPI),
            S("323410", "카카오뱅크",       Market.KRX_KOSPI),
            S("259960", "크래프톤",         Market.KRX_KOSPI),
            S("352820", "하이브",           Market.KRX_KOSPI),
            S("011200", "HMM",              Market.KRX_KOSPI),
            S("010130", "고려아연",         Market.KRX_KOSPI),
            S("009150", "삼성전기",         Market.KRX_KOSPI),
            S("034730", "SK",               Market.KRX_KOSPI),
            S("096770", "SK이노베이션",     Market.KRX_KOSPI),
            S("018260", "삼성에스디에스",   Market.KRX_KOSPI),
            S("032830", "삼성생명",         Market.KRX_KOSPI),
            S("316140", "우리금융지주",     Market.KRX_KOSPI),
            S("010950", "S-Oil",            Market.KRX_KOSPI),
            S("024110", "기업은행",         Market.KRX_KOSPI),
            S("090430", "아모레퍼시픽",     Market.KRX_KOSPI),
            // 코스닥
            S("247540", "에코프로비엠",     Market.KRX_KOSDAQ),
            S("086520", "에코프로",         Market.KRX_KOSDAQ),
            S("196170", "알테오젠",         Market.KRX_KOSDAQ),
            S("028300", "HLB",              Market.KRX_KOSDAQ),
            S("068760", "셀트리온제약",     Market.KRX_KOSDAQ),
            S("058470", "리노공업",         Market.KRX_KOSDAQ),
            S("263750", "펄어비스",         Market.KRX_KOSDAQ),
            S("293490", "카카오게임즈",     Market.KRX_KOSDAQ),
            S("035900", "JYP Ent.",         Market.KRX_KOSDAQ),
            S("041510", "에스엠",           Market.KRX_KOSDAQ),
            S("357780", "솔브레인",         Market.KRX_KOSDAQ),
            S("022100", "포스코DX",         Market.KRX_KOSDAQ),
            S("348370", "엔켐",             Market.KRX_KOSDAQ),
            S("277810", "레인보우로보틱스", Market.KRX_KOSDAQ),
            // 미국 대형주
            U("AAPL",  "Apple",             Market.US_NASDAQ),
            U("NVDA",  "NVIDIA",            Market.US_NASDAQ),
            U("MSFT",  "Microsoft",         Market.US_NASDAQ),
            U("TSLA",  "Tesla",             Market.US_NASDAQ),
            U("AMZN",  "Amazon",            Market.US_NASDAQ),
            U("GOOGL", "Alphabet",          Market.US_NASDAQ),
            U("META",  "Meta Platforms",    Market.US_NASDAQ),
            U("NFLX",  "Netflix",           Market.US_NASDAQ),
            U("AMD",   "AMD",               Market.US_NASDAQ),
            U("AVGO",  "Broadcom",          Market.US_NASDAQ),
            U("PLTR",  "Palantir",          Market.US_NASDAQ),
            U("COIN",  "Coinbase",          Market.US_NASDAQ),
        };

        // ── 코인 내장 폴백 (업비트 라이브 실패 시) ──────────────────────────────
        private static readonly SymbolEntry[] _cryptoFallback =
        {
            C("KRW-BTC",   "비트코인"),
            C("KRW-ETH",   "이더리움"),
            C("KRW-XRP",   "리플"),
            C("KRW-SOL",   "솔라나"),
            C("KRW-DOGE",  "도지코인"),
            C("KRW-ADA",   "에이다"),
            C("KRW-TRX",   "트론"),
            C("KRW-LINK",  "체인링크"),
            C("KRW-AVAX",  "아발란체"),
            C("KRW-DOT",   "폴카닷"),
        };

        private static SymbolEntry S(string code, string name, Market m) => new(code, name, m, Currency.KRW);
        private static SymbolEntry U(string code, string name, Market m) => new(code, name, m, Currency.USD);
        private static SymbolEntry C(string code, string name)           => new(code, name, Market.CRYPTO, Currency.KRW, true);

        public static IReadOnlyList<SymbolEntry> Stocks         => _stocks;
        public static IReadOnlyList<SymbolEntry> CryptoFallback => _cryptoFallback;

        /// <summary>
        /// 질의를 코드·이름(한/영)에 매칭해 관련도 순으로 반환. liveCrypto 가 있으면 코인 폴백 대신 사용
        /// (업비트 전 종목). query 가 비면 대표 종목 일부를 노출. 순수·무외부의존(검색은 인메모리).
        /// </summary>
        public static List<SymbolEntry> Search(string? query, int limit = 10, IReadOnlyList<SymbolEntry>? liveCrypto = null)
        {
            if (limit < 1) limit = 1;
            var crypto = liveCrypto is { Count: > 0 } ? liveCrypto : (IReadOnlyList<SymbolEntry>)_cryptoFallback;

            if (string.IsNullOrWhiteSpace(query))
                return _stocks.Take(limit - Math.Min(3, limit / 3))
                    .Concat(crypto.Take(Math.Min(3, limit / 3)))
                    .Take(limit).ToList();

            string q = query.Trim();
            return _stocks.Concat(crypto)
                .Select(e => (e, score: Score(e, q)))
                .Where(t => t.score > 0)
                .OrderByDescending(t => t.score)
                .ThenBy(t => t.e.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .Select(t => t.e)
                .ToList();
        }

        // 관련도: 코드 정확일치 > 코드 접두 > 이름 접두 > 이름/코드 부분일치. 0이면 비매칭.
        private static int Score(in SymbolEntry e, string q)
        {
            string code = e.Code;
            string bare = e.IsCrypto && code.StartsWith("KRW-", StringComparison.OrdinalIgnoreCase) ? code.Substring(4) : code;

            if (code.Equals(q, StringComparison.OrdinalIgnoreCase) ||
                bare.Equals(q, StringComparison.OrdinalIgnoreCase)) return 100;
            if (code.StartsWith(q, StringComparison.OrdinalIgnoreCase) ||
                bare.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 80;
            if (e.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return 60;
            if (e.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) return 40;
            if (code.Contains(q, StringComparison.OrdinalIgnoreCase)) return 20;
            return 0;
        }
    }
}
