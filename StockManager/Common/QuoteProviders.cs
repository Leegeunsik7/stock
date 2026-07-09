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
                return ParseRealtime(symbol, market, data);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Naver] {symbol} 조회 실패: {ex.Message}");
                return null;
            }
        }

        // 네이버 realtime/domestic datas[0] → Quote (순수·테스트 대상). 등락 방향·시간외 단일가 반영.
        public static Quote ParseRealtime(string symbol, Market market, JsonElement data)
        {
            var price = Safe.JsonDec(data, "closePrice");
            // ⚠️ compareToPreviousClosePrice는 "변동액"이지 "전일종가"가 아님.
            var delta = Safe.JsonDec(data, "compareToPreviousClosePrice");
            // 등락 방향: 실제 응답 필드는 compareToPreviousPrice(객체).name = RISING/FALLING/UPPER_LIMIT/LOWER_LIMIT.
            //   (기존 코드의 "risingOrFalling" 은 응답에 없는 필드라 항상 ""→상승 취급 → 하락일 전일종가·부호가 틀렸음. 실측 교정.)
            var dirName = "";
            if (data.TryGetProperty("compareToPreviousPrice", out var cmpEl) && cmpEl.ValueKind == JsonValueKind.Object
                && cmpEl.TryGetProperty("name", out var nmEl)) dirName = nmEl.GetString() ?? "";
            var isDown = dirName.Contains("FALL", StringComparison.OrdinalIgnoreCase)
                      || dirName.Contains("LOWER", StringComparison.OrdinalIgnoreCase);
            var change = isDown ? -delta : delta;
            var previousClose = price - change;

            var q = new Quote
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

            // v4.89.0 — 장 마감(15:30) 후 시간외 단일가 지속 갱신. 실측 응답:
            //   "overMarketPriceInfo":{"overMarketStatus":"OPEN","overPrice":"282,000","compareToPreviousClosePrice":"4,500",...}
            //   시간외 매매 중(OPEN)이고 overPrice>0 일 때만 현재가를 시간외가로 교체(전일종가는 정규장값 유지 →
            //   ChangeRate 가 '전일종가 대비 시간외 포함 등락'). 시간외 미개장/필드 부재 시 정규장 종가 유지(fail-safe).
            if (data.TryGetProperty("overMarketPriceInfo", out var om) && om.ValueKind == JsonValueKind.Object)
            {
                var overStatus = om.TryGetProperty("overMarketStatus", out var stEl) ? (stEl.GetString() ?? "") : "";
                var overPrice = Safe.JsonDec(om, "overPrice");
                if (overPrice > 0 && overStatus.Equals("OPEN", StringComparison.OrdinalIgnoreCase))
                {
                    q.Price = overPrice;
                    q.Session = "AFTER";
                }
            }
            return q;
        }

        // v4.68.0 — 순차→병렬 전환: SemaphoreSlim(4)로 최대 4개 동시 fetch.
        //   기존 50ms 인터-리퀘스트 딜레이 제거 → 13종목 기준 ~1.95s→~450ms.
        public async Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default)
        {
            var symArr = symbols.ToArray();
            using var sem = new SemaphoreSlim(4, 4);
            var tasks = symArr.Select(async s =>
            {
                await sem.WaitAsync(ct);
                try { return await GetQuoteAsync(s, market, ct); }
                finally { sem.Release(); }
            }).ToArray();
            var results = await Task.WhenAll(tasks);
            return results.Where(q => q != null).ToList()!;
        }

        public async Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var url = $"https://api.finance.naver.com/siseJson.naver?symbol={symbol}&requestType=1" +
                      $"&startTime={from:yyyyMMdd}&endTime={to:yyyyMMdd}&timeframe=day";
            try
            {
                var text = await _http.GetStringAsync(url, ct);
                return ParseSiseJson(text);
            }
            catch (Exception ex) { Log.Warn($"[Naver] {symbol} 히스토리 실패: {ex.Message}"); return new List<Candle>(); }
        }

        // 네이버 siseJson 텍스트 → 캔들 (순수·테스트 대상).
        //  실제 응답은 값 사이에 공백이 있음: ["20240102", 78200, 79800, ...] → \s* 허용 필수.
        //  (헤더 행 ['날짜',...]은 8자리 날짜가 아니라 매치되지 않음)
        public static List<Candle> ParseSiseJson(string text)
        {
            var candles = new List<Candle>();
            if (string.IsNullOrEmpty(text)) return candles;
            var matches = Regex.Matches(text,
                @"\[\s*""(\d{8})""\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*(\d+)");
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
            // v4.89.0 — includePrePost=true: 미국 프리/애프터마켓(정규장 마감 후 한국시간 새벽~오전) 시세를 함께 받는다.
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{ys}?interval=1m&range=1d&includePrePost=true";
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);
                var meta = doc.RootElement.GetProperty("chart").GetProperty("result")[0].GetProperty("meta");
                return ParseChartMeta(symbol, ys, market, meta);
            }
            catch (Exception ex)
            {
                Log.Warn($"[Yahoo] {symbol} 조회 실패: {ex.Message}");
                return null;
            }
        }

        // Yahoo chart meta → Quote (순수·테스트 대상). includePrePost=true 응답에서 프리/애프터마켓 시세를 세션에 맞춰 채택.
        public static Quote ParseChartMeta(string symbol, string ys, Market market, JsonElement meta)
        {
            // 정규장가 기준. 프리/애프터가 있으면 세션에 맞춰 그 값을 현재가로 채택(전일종가는 정규장값 유지).
            var regular = Safe.JsonDec(meta, "regularMarketPrice");
            var pre  = Safe.JsonDec(meta, "preMarketPrice");
            var post = Safe.JsonDec(meta, "postMarketPrice");
            var state = meta.TryGetProperty("marketState", out var msEl) ? (msEl.GetString() ?? "") : "";
            decimal chosen = regular; string session = "";
            // marketState: PRE / REGULAR / POST / POSTPOST / PREPRE / CLOSED. 애프터(POST·CLOSED)에 post 가 있으면 우선.
            if (post > 0 && (state.StartsWith("POST", StringComparison.OrdinalIgnoreCase) || state.Equals("CLOSED", StringComparison.OrdinalIgnoreCase)))
            { chosen = post; session = "POST"; }
            else if (pre > 0 && state.StartsWith("PRE", StringComparison.OrdinalIgnoreCase))
            { chosen = pre; session = "PRE"; }
            if (chosen <= 0) { chosen = regular; session = ""; }   // fail-safe: 시간외가 없으면 정규장가

            return new Quote
            {
                Symbol = symbol,
                Name = meta.TryGetProperty("longName", out var ln) ? (ln.GetString() ?? ys) : ys,
                Market = market,
                Currency = market >= Market.US_NYSE && market <= Market.US_AMEX ? Currency.USD : Currency.KRW,
                Price = chosen,
                PreviousClose = Safe.JsonDec(meta, "previousClose"),
                High = Safe.JsonDec(meta, "regularMarketDayHigh"),
                Low = Safe.JsonDec(meta, "regularMarketDayLow"),
                Volume = Safe.JsonLong(meta, "regularMarketVolume"),
                Session = session,
                Timestamp = meta.TryGetProperty("regularMarketTime", out var rt)
                    ? DateTimeOffset.FromUnixTimeSeconds(Safe.JsonLong(rt)).LocalDateTime
                    : DateTime.Now,
                Source = DataSource.YahooFinance
            };
        }

        // v4.68.0 — 순차→병렬 전환: SemaphoreSlim(3)로 최대 3개 동시 fetch.
        //   ⚠️ Yahoo v7/finance/quote 배치 API는 crumb 인증 필요 → v8/finance/chart 단건 병렬.
        public async Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default)
        {
            var symArr = symbols.ToArray();
            using var sem = new SemaphoreSlim(3, 3);
            var tasks = symArr.Select(async s =>
            {
                await sem.WaitAsync(ct);
                try { return await GetQuoteAsync(s, market, ct); }
                finally { sem.Release(); }
            }).ToArray();
            var results = await Task.WhenAll(tasks);
            return results.Where(q => q != null).ToList()!;
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

        // 히스토리는 폴백 체인 — 시장 지원 프로바이더를 순서대로 시도, 첫 성공(≥2캔들) 채택.
        //  예: KRX는 Naver(siseJson) 실패/차단 시 Yahoo(005930.KS)로 자동 폴백 → 차트 공백 방지.
        public async Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
        {
            List<Candle>? best = null;
            foreach (var p in _providers)
            {
                if (Array.IndexOf(p.SupportedMarkets, market) < 0) continue;
                try
                {
                    var cs = await p.GetHistoryAsync(symbol, market, from, to, ct);
                    if (cs != null && cs.Count >= 2) return cs;                      // 첫 유효 결과 채택
                    if (cs != null && (best == null || cs.Count > best.Count)) best = cs;
                }
                catch (Exception ex) { Log.Warn($"[Agg] {p.Source} {symbol} 히스토리 실패: {ex.Message}"); }
            }
            return best ?? new List<Candle>();
        }

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
