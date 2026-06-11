// ============================================================================
// StockManager.Common - 멀티 소스 데이터 프로바이더 (v2.2)
// [개선점]
//  · 네이버 previousClose 필드 수정 (기존: 변동액을 전일종가로 오인)
//  · Yahoo GetDecimal() → Safe.JsonDec (int/double/string 혼재 대응)
//  · CultureInfo.InvariantCulture 명시
//  · HttpPool.Shared 싱글톤 사용 (소켓 고갈 방지)
//  · 에러 로깅 (Log.Warn)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public interface IQuoteProvider
    {
        DataSource Source { get; }
        Market[] SupportedMarkets { get; }
        bool IsRealtime { get; }

        Task<Quote?> GetQuoteAsync(string symbol, Market market, CancellationToken ct = default);
        Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default);
        Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default);

        event EventHandler<Quote>? QuoteUpdated;
        Task SubscribeAsync(string symbol, Market market);
        Task UnsubscribeAsync(string symbol, Market market);
    }

    // =========================================================================
    // 네이버 금융 프로바이더 (수정판)
    // =========================================================================
    public class NaverFinanceProvider : IQuoteProvider
    {
        private readonly HttpClient _http;
        public DataSource Source => DataSource.Naver;
        public Market[] SupportedMarkets => new[] { Market.KRX_KOSPI, Market.KRX_KOSDAQ };
        public bool IsRealtime => false;

        // 네이버는 풀링 방식이라 실시간 이벤트 미사용 (인터페이스 구현만)
#pragma warning disable CS0067
        public event EventHandler<Quote>? QuoteUpdated;
#pragma warning restore CS0067

        public NaverFinanceProvider(HttpClient? http = null)
        {
            _http = http ?? HttpPool.Shared;
        }

        public async Task<Quote?> GetQuoteAsync(string symbol, Market market, CancellationToken ct = default)
        {
            var url = $"https://polling.finance.naver.com/api/realtime/domestic/stock/{symbol}";
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var data = doc.RootElement.GetProperty("datas")[0];

                var price = Safe.JsonDec(data, "closePrice");
                // ⚠️ 수정: compareToPreviousClosePrice는 "변동액"이지 "전일종가"가 아님
                var delta = Safe.JsonDec(data, "compareToPreviousClosePrice");
                var direction = Safe.JsonStr(data, "risingOrFalling");
                var isDown = direction.StartsWith("FALL", StringComparison.OrdinalIgnoreCase)
                          || direction.StartsWith("LOWER", StringComparison.OrdinalIgnoreCase);
                var change = isDown ? -delta : delta;
                var previousClose = price - change;

                return new Quote
                {
                    Symbol = symbol,
                    Name = Safe.JsonStr(data, "stockName"),
                    Market = market,
                    Currency = Currency.KRW,
                    Price = price,
                    PreviousClose = previousClose,
                    Open = Safe.JsonDec(data, "openPrice"),
                    High = Safe.JsonDec(data, "highPrice"),
                    Low = Safe.JsonDec(data, "lowPrice"),
                    Volume = Safe.JsonLong(data, "accumulatedTradingVolume"),
                    TradingValue = Safe.JsonDec(data, "accumulatedTradingValue"),
                    MarketCap = Safe.JsonDec(data, "marketCap"),
                    Timestamp = DateTime.Now,
                    Source = DataSource.Naver
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"[Naver] {symbol} 조회 실패: {ex.Message}");
                return null;
            }
        }

        public async Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default)
        {
            var list = new List<Quote>();
            foreach (var s in symbols)
            {
                var q = await GetQuoteAsync(s, market, ct);
                if (q != null) list.Add(q);
                try { await Task.Delay(50, ct); } catch (TaskCanceledException) { break; }
            }
            return list;
        }

        public async Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var url = $"https://api.finance.naver.com/siseJson.naver?symbol={symbol}&requestType=1" +
                      $"&startTime={from:yyyyMMdd}&endTime={to:yyyyMMdd}&timeframe=day";
            var candles = new List<Candle>();
            try
            {
                var text = await _http.GetStringAsync(url, ct);
                var matches = Regex.Matches(text, @"\[""(\d{8})"",([\d.]+),([\d.]+),([\d.]+),([\d.]+),(\d+)");
                foreach (Match m in matches)
                {
                    candles.Add(new Candle
                    {
                        Date = DateTime.ParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture),
                        Open = decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                        High = decimal.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                        Low = decimal.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture),
                        Close = decimal.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                        Volume = long.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture)
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"[Naver] {symbol} 히스토리 실패: {ex.Message}"); }
            return candles;
        }

        public Task SubscribeAsync(string symbol, Market market) => Task.CompletedTask;
        public Task UnsubscribeAsync(string symbol, Market market) => Task.CompletedTask;
    }

    // =========================================================================
    // Yahoo Finance 프로바이더 (수정판)
    // =========================================================================
    public class YahooFinanceProvider : IQuoteProvider
    {
        private readonly HttpClient _http;
        public DataSource Source => DataSource.YahooFinance;
        public Market[] SupportedMarkets => new[] {
            Market.US_NYSE, Market.US_NASDAQ, Market.US_AMEX,
            Market.KRX_KOSPI, Market.KRX_KOSDAQ
        };
        public bool IsRealtime => false;

        // Yahoo는 풀링 방식이라 실시간 이벤트 미사용
#pragma warning disable CS0067
        public event EventHandler<Quote>? QuoteUpdated;
#pragma warning restore CS0067

        public YahooFinanceProvider(HttpClient? http = null)
        {
            _http = http ?? HttpPool.Shared;
        }

        private static string YahooSymbol(string symbol, Market market) => market switch
        {
            Market.KRX_KOSPI => $"{symbol}.KS",
            Market.KRX_KOSDAQ => $"{symbol}.KQ",
            _ => symbol
        };

        public async Task<Quote?> GetQuoteAsync(string symbol, Market market, CancellationToken ct = default)
        {
            var ys = YahooSymbol(symbol, market);
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{ys}?interval=1m&range=1d";
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");

                return new Quote
                {
                    Symbol = symbol,
                    Name = meta.TryGetProperty("longName", out var ln) ? (ln.GetString() ?? ys) : ys,
                    Market = market,
                    Currency = market >= Market.US_NYSE && market <= Market.US_AMEX ? Currency.USD : Currency.KRW,
                    Price = Safe.JsonDec(meta, "regularMarketPrice"),
                    PreviousClose = Safe.JsonDec(meta, "previousClose"),
                    High = Safe.JsonDec(meta, "regularMarketDayHigh"),
                    Low = Safe.JsonDec(meta, "regularMarketDayLow"),
                    Volume = Safe.JsonLong(meta, "regularMarketVolume"),
                    Timestamp = meta.TryGetProperty("regularMarketTime", out var rt)
                        ? DateTimeOffset.FromUnixTimeSeconds(Safe.JsonLong(rt)).LocalDateTime
                        : DateTime.Now,
                    Source = DataSource.YahooFinance
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"[Yahoo] {symbol} 조회 실패: {ex.Message}");
                return null;
            }
        }

        public async Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default)
        {
            // ⚠️ Yahoo v7/finance/quote 배치 API는 이제 crumb 인증을 요구해 빈 응답/401이 잦음.
            //    인증 불필요한 v8/finance/chart(단건 GetQuoteAsync)로 순회 — 안정적(국내 네이버와 동일 패턴).
            var symArr = symbols.ToArray();
            var list = new List<Quote>();
            foreach (var s in symArr)
            {
                var q = await GetQuoteAsync(s, market, ct);
                if (q != null) list.Add(q);
                try { await Task.Delay(40, ct); } catch (TaskCanceledException) { break; }
            }
            return list;
        }

        public async Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var ys = YahooSymbol(symbol, market);
            var p1 = new DateTimeOffset(from).ToUnixTimeSeconds();
            var p2 = new DateTimeOffset(to).ToUnixTimeSeconds();
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{ys}?period1={p1}&period2={p2}&interval=1d";
            var candles = new List<Candle>();
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
                var ts = r.GetProperty("timestamp");
                var q = r.GetProperty("indicators").GetProperty("quote")[0];
                var opens = q.GetProperty("open");
                var highs = q.GetProperty("high");
                var lows = q.GetProperty("low");
                var closes = q.GetProperty("close");
                var vols = q.GetProperty("volume");

                for (int i = 0; i < ts.GetArrayLength(); i++)
                {
                    if (closes[i].ValueKind == JsonValueKind.Null) continue;
                    candles.Add(new Candle
                    {
                        Date = DateTimeOffset.FromUnixTimeSeconds(ts[i].GetInt64()).LocalDateTime.Date,
                        Open = Safe.JsonDec(opens[i]),
                        High = Safe.JsonDec(highs[i]),
                        Low = Safe.JsonDec(lows[i]),
                        Close = Safe.JsonDec(closes[i]),
                        Volume = Safe.JsonLong(vols[i])
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"[Yahoo] {symbol} 히스토리 실패: {ex.Message}"); }
            return candles;
        }

        public Task SubscribeAsync(string symbol, Market market) => Task.CompletedTask;
        public Task UnsubscribeAsync(string symbol, Market market) => Task.CompletedTask;
    }

    // =========================================================================
    // QuoteAggregator - 멀티소스 라우팅
    // =========================================================================
    public class QuoteAggregator : IQuoteProvider
    {
        private readonly List<IQuoteProvider> _providers;
        public DataSource Source => DataSource.Manual;
        public Market[] SupportedMarkets { get; }
        public bool IsRealtime => false;

        public event EventHandler<Quote>? QuoteUpdated;

        public QuoteAggregator(params IQuoteProvider[] providers)
        {
            _providers = new List<IQuoteProvider>(providers);
            var markets = new HashSet<Market>();
            foreach (var p in providers)
                foreach (var m in p.SupportedMarkets) markets.Add(m);
            SupportedMarkets = markets.ToArray();

            foreach (var p in _providers)
                p.QuoteUpdated += (s, q) => QuoteUpdated?.Invoke(this, q);
        }

        private IQuoteProvider? ResolveProvider(Market market, bool preferRealtime = true)
        {
            IQuoteProvider? fallback = null;
            foreach (var p in _providers)
            {
                if (Array.IndexOf(p.SupportedMarkets, market) < 0) continue;
                if (preferRealtime && p.IsRealtime) return p;
                fallback ??= p;
            }
            return fallback;
        }

        public Task<Quote?> GetQuoteAsync(string symbol, Market market, CancellationToken ct = default)
            => ResolveProvider(market)?.GetQuoteAsync(symbol, market, ct) ?? Task.FromResult<Quote?>(null);

        public Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default)
            => ResolveProvider(market)?.GetQuotesAsync(symbols, market, ct) ?? Task.FromResult(new List<Quote>());

        public Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
            => ResolveProvider(market)?.GetHistoryAsync(symbol, market, from, to, ct) ?? Task.FromResult(new List<Candle>());

        public async Task SubscribeAsync(string symbol, Market market)
        {
            var p = ResolveProvider(market, preferRealtime: true);
            if (p != null) await p.SubscribeAsync(symbol, market);
        }

        public async Task UnsubscribeAsync(string symbol, Market market)
        {
            var p = ResolveProvider(market, preferRealtime: true);
            if (p != null) await p.UnsubscribeAsync(symbol, market);
        }
    }
}
