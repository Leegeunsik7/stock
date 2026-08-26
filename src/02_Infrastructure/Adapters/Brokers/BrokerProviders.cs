// ============================================================================
// StockManager.Common - 증권사 공식 API 프로바이더 (v2.2)
// [개선점]
//  · KIS 전일대비/등락률 필드 정확하게 사용 (prdy_vrss, prdy_ctrt)
//  · CultureInfo.InvariantCulture 명시
//  · HttpPool.Shared 싱글톤 사용
//  · 에러 로깅 (Log.Warn)
//  · SynchronizationContext로 스레드 안전 이벤트
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    // =========================================================================
    // 키움 OpenAPI+ 프로바이더 (32-bit COM 전용)
    // =========================================================================
    public class KiwoomProvider : IQuoteProvider, IDisposable
    {
        public DataSource Source => DataSource.Kiwoom;
        public Market[] SupportedMarkets => new[] { Market.KRX_KOSPI, Market.KRX_KOSDAQ };
        public bool IsRealtime => true;

        public event EventHandler<Quote>? QuoteUpdated;

        private readonly ConcurrentDictionary<string, Quote> _cache = new();
        private readonly SynchronizationContext? _syncCtx;
        private bool _connected;
#pragma warning disable CS0169, CS0414  // 향후 실제 키움 COM 연결 시 사용
        private dynamic? _ocx;
#pragma warning restore CS0169, CS0414

        public KiwoomProvider()
        {
            // 이벤트 발생 시 UI 스레드로 마샬링
            _syncCtx = SynchronizationContext.Current;
        }

        public Task ConnectAsync()
        {
            // 실제 구현: _ocx = new AxKHOpenAPILib.AxKHOpenAPI(); Form.Controls.Add(_ocx);
            // _ocx.CommConnect();
            _connected = true;
            Log.Info("[Kiwoom] 연결 완료 (더미)");
            return Task.CompletedTask;
        }

        public async Task<Quote?> GetQuoteAsync(string symbol, Market market, CancellationToken ct = default)
        {
            if (!_connected) await ConnectAsync();
            if (_cache.TryGetValue(symbol, out var cached)) return cached;
            return null;
        }

        public async Task<List<Quote>> GetQuotesAsync(IEnumerable<string> symbols, Market market, CancellationToken ct = default)
        {
            var list = new List<Quote>();
            foreach (var s in symbols)
            {
                var q = await GetQuoteAsync(s, market, ct);
                if (q != null) list.Add(q);
                try { await Task.Delay(200, ct); } catch (TaskCanceledException) { break; }
            }
            return list;
        }

        public Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
        {
            return Task.FromResult(new List<Candle>());
        }

        public Task SubscribeAsync(string symbol, Market market) => Task.CompletedTask;
        public Task UnsubscribeAsync(string symbol, Market market) => Task.CompletedTask;

        // 실시간 이벤트 핸들러 (UI 스레드로 마샬링)
        private void OnRealData(string symbol, string realType, string realData)
        {
            var q = _cache.GetOrAdd(symbol, _ => new Quote { Symbol = symbol, Market = Market.KRX_KOSPI });
            // q.Price = 파싱된 값;

            if (_syncCtx != null)
                _syncCtx.Post(_ => QuoteUpdated?.Invoke(this, q), null);
            else
                QuoteUpdated?.Invoke(this, q);
        }

        public void Dispose()
        {
            // _ocx?.CommTerminate();
            GC.SuppressFinalize(this);
        }
    }

    // =========================================================================
    // 한국투자증권 KIS Developers (REST) - 전일대비 필드 정확히 사용
    // =========================================================================
    public class KisProvider : IQuoteProvider
    {
        public DataSource Source => DataSource.KIS;
        public Market[] SupportedMarkets => new[] {
            Market.KRX_KOSPI, Market.KRX_KOSDAQ,
            Market.US_NYSE, Market.US_NASDAQ, Market.US_AMEX
        };
        public bool IsRealtime => true;

        // KIS REST 기반이라 실시간 이벤트 미사용. 차기에 WebSocket 추가 예정
#pragma warning disable CS0067
        public event EventHandler<Quote>? QuoteUpdated;
#pragma warning restore CS0067

        private readonly HttpClient _http;
        private readonly KisConfig _config;
        private string? _accessToken;
        private DateTime _tokenExpiry;
        private readonly SemaphoreSlim _tokenLock = new(1, 1);

        public KisProvider(KisConfig config, HttpClient? http = null)
        {
            _config = config;
            _http = http ?? HttpPool.Shared;
        }

        private string BaseUrl => _config.IsPaper
            ? "https://openapivts.koreainvestment.com:29443"
            : "https://openapi.koreainvestment.com:9443";

        private async Task<string> GetTokenAsync(CancellationToken ct = default)
        {
            if (_accessToken != null && DateTime.Now < _tokenExpiry) return _accessToken;

            await _tokenLock.WaitAsync(ct);
            try
            {
                if (_accessToken != null && DateTime.Now < _tokenExpiry) return _accessToken;

                var body = new
                {
                    grant_type = "client_credentials",
                    appkey = _config.AppKey,
                    appsecret = _config.AppSecret
                };
                var res = await _http.PostAsJsonAsync($"{BaseUrl}/oauth2/tokenP", body, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                _accessToken = json.GetProperty("access_token").GetString();
                _tokenExpiry = DateTime.Now.AddHours(23);
                Log.Info("[KIS] 토큰 발급 성공");
                return _accessToken!;
            }
            finally { _tokenLock.Release(); }
        }

        public async Task<Quote?> GetQuoteAsync(string symbol, Market market, CancellationToken ct = default)
        {
            try
            {
                var token = await GetTokenAsync(ct);
                HttpRequestMessage req;
                bool isKorean = market == Market.KRX_KOSPI || market == Market.KRX_KOSDAQ;

                if (isKorean)
                {
                    req = new HttpRequestMessage(HttpMethod.Get,
                        $"{BaseUrl}/uapi/domestic-stock/v1/quotations/inquire-price?FID_COND_MRKT_DIV_CODE=J&FID_INPUT_ISCD={symbol}");
                    req.Headers.Add("tr_id", "FHKST01010100");
                }
                else
                {
                    var excd = market == Market.US_NYSE ? "NYS" :
                               market == Market.US_NASDAQ ? "NAS" : "AMS";
                    req = new HttpRequestMessage(HttpMethod.Get,
                        $"{BaseUrl}/uapi/overseas-price/v1/quotations/price?AUTH=&EXCD={excd}&SYMB={symbol}");
                    req.Headers.Add("tr_id", "HHDFS00000300");
                }

                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("appkey", _config.AppKey);
                req.Headers.Add("appsecret", _config.AppSecret);

                var res = await _http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                var output = json.GetProperty("output");

                if (isKorean)
                {
                    // ⚠️ 수정: prdy_vrss (전일대비) 필드를 활용해 정확한 전일종가 계산
                    var price = ParseDec(Safe.JsonStr(output, "stck_prpr"));
                    var prdyVrss = ParseDec(Safe.JsonStr(output, "prdy_vrss"));   // 전일대비
                    var prdyVrssSign = Safe.JsonStr(output, "prdy_vrss_sign");     // 1 상한, 2 상승, 3 보합, 4 하한, 5 하락
                    // sign 4,5는 하락 → 음수 적용
                    var sign = (prdyVrssSign == "4" || prdyVrssSign == "5") ? -1 : 1;
                    var change = prdyVrss * sign;
                    var previousClose = price - change;

                    return new Quote
                    {
                        Symbol = symbol,
                        Name = Safe.JsonStr(output, "rprs_mrkt_kor_name"),
                        Market = market,
                        Currency = Currency.KRW,
                        Price = price,
                        PreviousClose = previousClose,
                        Open = ParseDec(Safe.JsonStr(output, "stck_oprc")),
                        High = ParseDec(Safe.JsonStr(output, "stck_hgpr")),
                        Low = ParseDec(Safe.JsonStr(output, "stck_lwpr")),
                        Volume = ParseLong(Safe.JsonStr(output, "acml_vol")),
                        TradingValue = ParseDec(Safe.JsonStr(output, "acml_tr_pbmn")),
                        MarketCap = ParseDec(Safe.JsonStr(output, "hts_avls")) * 100_000_000m, // 억 단위
                        Timestamp = DateTime.Now,
                        Source = DataSource.KIS
                    };
                }
                else
                {
                    return new Quote
                    {
                        Symbol = symbol,
                        Name = Safe.JsonStr(output, "rsym"), // 심볼만 반환
                        Market = market,
                        Currency = Currency.USD,
                        Price = ParseDec(Safe.JsonStr(output, "last")),
                        PreviousClose = ParseDec(Safe.JsonStr(output, "base")),
                        Volume = ParseLong(Safe.JsonStr(output, "tvol")),
                        Timestamp = DateTime.Now,
                        Source = DataSource.KIS
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[KIS] {symbol} 조회 실패: {ex.Message}");
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
                try { await Task.Delay(100, ct); } catch (TaskCanceledException) { break; }
            }
            return list;
        }

        public async Task<List<Candle>> GetHistoryAsync(string symbol, Market market, DateTime from, DateTime to, CancellationToken ct = default)
        {
            var candles = new List<Candle>();
            bool isKorean = market == Market.KRX_KOSPI || market == Market.KRX_KOSDAQ;
            if (!isKorean) return candles; // 해외 히스토리는 별도 엔드포인트 필요

            try
            {
                var token = await GetTokenAsync(ct);
                var req = new HttpRequestMessage(HttpMethod.Get,
                    $"{BaseUrl}/uapi/domestic-stock/v1/quotations/inquire-daily-itemchartprice" +
                    $"?FID_COND_MRKT_DIV_CODE=J&FID_INPUT_ISCD={symbol}" +
                    $"&FID_INPUT_DATE_1={from:yyyyMMdd}&FID_INPUT_DATE_2={to:yyyyMMdd}" +
                    $"&FID_PERIOD_DIV_CODE=D&FID_ORG_ADJ_PRC=0");
                req.Headers.Add("tr_id", "FHKST03010100");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Add("appkey", _config.AppKey);
                req.Headers.Add("appsecret", _config.AppSecret);

                var res = await _http.SendAsync(req, ct);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                foreach (var e in json.GetProperty("output2").EnumerateArray())
                {
                    var ds = Safe.JsonStr(e, "stck_bsop_date");
                    if (string.IsNullOrEmpty(ds)) continue;
                    candles.Add(new Candle
                    {
                        Date = DateTime.ParseExact(ds, "yyyyMMdd", CultureInfo.InvariantCulture),
                        Open = ParseDec(Safe.JsonStr(e, "stck_oprc")),
                        High = ParseDec(Safe.JsonStr(e, "stck_hgpr")),
                        Low = ParseDec(Safe.JsonStr(e, "stck_lwpr")),
                        Close = ParseDec(Safe.JsonStr(e, "stck_clpr")),
                        Volume = ParseLong(Safe.JsonStr(e, "acml_vol"))
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"[KIS] {symbol} 히스토리 실패: {ex.Message}"); }
            return candles;
        }

        public Task SubscribeAsync(string symbol, Market market) => Task.CompletedTask;
        public Task UnsubscribeAsync(string symbol, Market market) => Task.CompletedTask;

        // KIS는 모든 가격을 문자열로 반환 → Culture 명시 필수
        private static decimal ParseDec(string s) =>
            decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
        private static long ParseLong(string s) =>
            long.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var l) ? l : 0L;
    }

    public class KisConfig
    {
        public string AppKey { get; set; } = "";
        public string AppSecret { get; set; } = "";
        public string AccountNo { get; set; } = "";
        public string AccountProductCode { get; set; } = "01";
        public bool IsPaper { get; set; } = true;
    }
}
