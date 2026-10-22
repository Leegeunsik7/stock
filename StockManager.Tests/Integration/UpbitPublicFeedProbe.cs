// ============================================================================
// UpbitPublicFeedProbe — [v12.18.54-DEV] 업비트 공개 시세 REST 점검(KRW-BTC 현재가 · L1 호가 · 지연 실측 · 인증 0 · 주문 0 · 테스트 도구)
//
//  목적: API 키 없는 업비트 공개 시세(Quotation) API 에서 KRW-BTC 현재가와 최우선 호가를 받아 무할당 파서로 판독하고 왕복 지연을 실측한 뒤,
//   그 L1 호가를 CryptoPaperExchange(순수 인메모리)에 먹여 가상 매수 → 가상 매도 1회를 수행한다(체결 = 메모리 잔고만 · 송출 0).
//  ★송출 허용 목록(그 외 = 예외 · HttpMessageInvoker.SendAsync 이전이라 패킷 0):
//   ① GET https://api.upbit.com/v1/ticker?markets=KRW-BTC      (현재가 · 체결가)
//   ② GET https://api.upbit.com/v1/orderbook?markets=KRW-BTC   (호가 — 현재가 API 에는 매수/매도 호가가 없다)
//   http · 다른 호스트/포트 · 다른 경로(★/v1/orders 등 거래 API) · 다른 쿼리 · 본문 · Authorization 헤더 = 거부. 자동 리다이렉트 꺼짐.
//  ★파서(UpbitPublicProtocol): KisProtocol 과 같은 형태 — ReadOnlySpan<byte> · Utf8JsonReader 타입드 게터(TryGetDecimal/TryGetInt64) ·
//   예외는 JsonException 만 catch · 최상위 배열 원소 정확히 1개 · market 정확 일치 · 필수 필드 중복/부재/≤0 = false(fail-closed) · 호출당 힙 0.
//   숫자 → raw 10^8: 음수 · long 범위 초과 = false · 소수 8자리 초과 = 내림(업비트 수량 소수 8자리).
//  ★실행 조건(옵트인): MS2E_UPBIT_PUBLIC=1. 미충족 = Skip(실패 아님). 24/7 시장이라 시각 무관. 공개 API 요청 한도(초당 10회) 이하로 간격 유지.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Pricing;
using Xunit;

namespace StockManager.Tests.Integration
{
    /// <summary>현재가 판독 결과(raw 10^8 · 시각 = 유닉스 밀리초).</summary>
    public readonly record struct UpbitTicker(long TradePriceRaw, long TradeTimestampMs, long TimestampMs);

    /// <summary>최우선 호가(orderbook_units[0]) 판독 결과.</summary>
    public readonly record struct UpbitL1Quote(long BidRaw, long AskRaw, long BidSizeRaw, long AskSizeRaw, long TimestampMs);

    /// <summary>업비트 공개 시세 응답 무할당 파서(δ=0 · fail-closed).</summary>
    public static class UpbitPublicProtocol
    {
        public static ReadOnlySpan<byte> MarketKrwBtc => "KRW-BTC"u8;

        /// <summary>GET /v1/ticker 응답 → 현재가. 최상위 배열 원소 1개 · market 일치 · trade_price/timestamp > 0 필수.</summary>
        public static bool TryParseTicker(ReadOnlySpan<byte> json, ReadOnlySpan<byte> market, out UpbitTicker t)
        {
            t = default;
            long price = 0, tradeTs = 0, ts = 0;
            bool marketOk = false, marketSeen = false, priceSeen = false, tsSeen = false;
            int elements = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return false;     // 오류 응답 {"error":…} = 객체 → 거부
                int depth = 1;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            depth++;
                            if (depth == 2 && ++elements > 1) return false;                  // 원소 2개 이상 = 대상 모호
                            break;
                        case JsonTokenType.StartArray: depth++; break;
                        case JsonTokenType.EndObject:
                        case JsonTokenType.EndArray: depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth != 2) break;
                            if (r.ValueTextEquals("market"u8))
                            {
                                if (marketSeen || !r.Read() || r.TokenType != JsonTokenType.String) return false;
                                marketSeen = true; marketOk = r.ValueTextEquals(market);
                            }
                            else if (r.ValueTextEquals("trade_price"u8)) { if (priceSeen || !ReadRaw(ref r, out price)) return false; priceSeen = true; }
                            else if (r.ValueTextEquals("trade_timestamp"u8)) { if (!ReadMillis(ref r, out tradeTs)) return false; }
                            else if (r.ValueTextEquals("timestamp"u8)) { if (tsSeen || !ReadMillis(ref r, out ts)) return false; tsSeen = true; }
                            break;
                    }
                }
                if (depth != 0) return false;
            }
            catch (JsonException) { return false; }
            if (elements != 1 || !marketOk || price <= 0 || ts <= 0) return false;
            t = new UpbitTicker(price, tradeTs, ts);
            return true;
        }

        /// <summary>GET /v1/orderbook 응답 → 최우선 호가. 원소 1개 · market 일치 · 0 < 매수1 < 매도1 · 잔량 ≥0 · timestamp > 0 필수.</summary>
        public static bool TryParseOrderbookL1(ReadOnlySpan<byte> json, ReadOnlySpan<byte> market, out UpbitL1Quote q)
        {
            q = default;
            long bid = 0, ask = 0, bidSz = 0, askSz = 0, ts = 0;
            bool marketOk = false, marketSeen = false, tsSeen = false, unitsSeen = false, bidSeen = false, askSeen = false;
            int elements = 0, units = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return false;
                int depth = 1; bool inUnits = false;
                while (r.Read())
                {
                    switch (r.TokenType)
                    {
                        case JsonTokenType.StartObject:
                            depth++;
                            if (depth == 2 && ++elements > 1) return false;
                            if (depth == 4 && inUnits) units++;
                            break;
                        case JsonTokenType.StartArray: depth++; break;
                        case JsonTokenType.EndObject: depth--; break;
                        case JsonTokenType.EndArray:
                            if (depth == 3) inUnits = false;
                            depth--; break;
                        case JsonTokenType.PropertyName:
                            if (depth == 2)
                            {
                                if (r.ValueTextEquals("market"u8))
                                {
                                    if (marketSeen || !r.Read() || r.TokenType != JsonTokenType.String) return false;
                                    marketSeen = true; marketOk = r.ValueTextEquals(market);
                                }
                                else if (r.ValueTextEquals("timestamp"u8)) { if (tsSeen || !ReadMillis(ref r, out ts)) return false; tsSeen = true; }
                                else if (r.ValueTextEquals("orderbook_units"u8))
                                {
                                    if (unitsSeen) return false;
                                    unitsSeen = true; inUnits = true;
                                }
                            }
                            else if (depth == 4 && inUnits && units == 1)                    // 최우선 단(첫 원소)만
                            {
                                if (r.ValueTextEquals("ask_price"u8)) { if (askSeen || !ReadRaw(ref r, out ask)) return false; askSeen = true; }
                                else if (r.ValueTextEquals("bid_price"u8)) { if (bidSeen || !ReadRaw(ref r, out bid)) return false; bidSeen = true; }
                                else if (r.ValueTextEquals("ask_size"u8)) { if (!ReadRaw(ref r, out askSz)) return false; }
                                else if (r.ValueTextEquals("bid_size"u8)) { if (!ReadRaw(ref r, out bidSz)) return false; }
                            }
                            break;
                    }
                }
                if (depth != 0) return false;
            }
            catch (JsonException) { return false; }
            if (elements != 1 || !marketOk || ts <= 0 || bid <= 0 || ask <= 0 || bid >= ask) return false;
            q = new UpbitL1Quote(bid, ask, bidSz, askSz, ts);
            return true;
        }

        private static readonly decimal RawLimit = (decimal)long.MaxValue / UpbitTickAligner.Scale;

        /// <summary>다음 토큰(숫자) → raw 10^8(음수 · 범위 초과 = false · 소수 8자리 초과 = 내림).</summary>
        private static bool ReadRaw(ref Utf8JsonReader r, out long raw)
        {
            raw = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetDecimal(out decimal d)) return false;
            if (d < 0m || d >= RawLimit) return false;                                          // 곱셈 전 차단(decimal 예외 0)
            raw = (long)decimal.Truncate(d * UpbitTickAligner.Scale);
            return true;
        }

        private static bool ReadMillis(ref Utf8JsonReader r, out long ms)
        {
            ms = 0;
            return r.Read() && r.TokenType == JsonTokenType.Number && r.TryGetInt64(out ms) && ms > 0;
        }
    }

    /// <summary>업비트 공개 시세 송출 허용 목록 가드 — GET ticker/orderbook(KRW-BTC) 두 요청만.</summary>
    public static class UpbitPublicFeedGuard
    {
        public const string Host = "api.upbit.com";
        public const string TickerPath = "/v1/ticker", OrderbookPath = "/v1/orderbook";
        public const string Query = "?markets=KRW-BTC";
        public const string OptInVar = "MS2E_UPBIT_PUBLIC";
        public static readonly Uri TickerUri = new("https://" + Host + TickerPath + Query);
        public static readonly Uri OrderbookUri = new("https://" + Host + OrderbookPath + Query);

        public static void EnsureAllowed(HttpRequestMessage req)
        {
            var u = req.RequestUri ?? throw new InvalidOperationException("업비트 공개 점검 거부: URI 없음");
            if (!u.IsAbsoluteUri || u.Scheme != Uri.UriSchemeHttps || !string.Equals(u.Host, Host, StringComparison.Ordinal) || u.Port != 443 || u.UserInfo.Length != 0)
                throw new InvalidOperationException("업비트 공개 점검 거부: 대상 ≠ https://" + Host);
            if (req.Method != HttpMethod.Get) throw new InvalidOperationException("업비트 공개 점검 거부: 메서드 " + req.Method + " ≠ GET");
            if (req.Content is not null) throw new InvalidOperationException("업비트 공개 점검 거부: 본문 있음");
            if (req.Headers.Authorization is not null || req.Headers.Contains("Authorization"))
                throw new InvalidOperationException("업비트 공개 점검 거부: Authorization 헤더(인증 API = 범위 밖)");
            if ((u.AbsolutePath != TickerPath && u.AbsolutePath != OrderbookPath) || u.Query != Query || u.Fragment.Length != 0)
                throw new InvalidOperationException("업비트 공개 점검 거부: 허용 목록 밖 요청(" + u.AbsolutePath + u.Query + ")");
        }

        public static Task<HttpResponseMessage> SendAsync(HttpMessageInvoker http, HttpRequestMessage req, CancellationToken ct)
        {
            EnsureAllowed(req);
            return http.SendAsync(req, ct);
        }

        public static HttpMessageInvoker CreateInvoker()
            => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, PooledConnectionLifetime = TimeSpan.FromMinutes(1) }, disposeHandler: true);

        public static HttpRequestMessage Get(Uri uri)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, uri);
            req.Headers.TryAddWithoutValidation("accept", "application/json");
            return req;
        }

        /// <summary>Skip 사유(null = 실행). env 는 주입식(테스트).</summary>
        public static string? SkipReason(Func<string, string?> env)
            => env(OptInVar) == "1" ? null : OptInVar + " ≠ 1(업비트 공개 시세 점검 옵트인 없음) — 미송출";
    }

    /// <summary>점검 결과(출력 줄 · 지연 · 마지막 판독값 · 가상 체결 결과).</summary>
    public sealed class UpbitPublicFeedReport
    {
        public readonly List<double> TickerLatencyMs = new(), OrderbookLatencyMs = new();
        public int TickerOk, OrderbookOk, Rounds;
        public UpbitTicker LastTicker;
        public UpbitL1Quote LastQuote;
        public CryptoPaperFill PaperBuy, PaperSell;
        public CryptoPaperBalance PaperAfter;
        public readonly List<string> Lines = new();
    }

    public static class UpbitPublicFeedProbe
    {
        public const int DefaultRounds = 5;
        public static readonly TimeSpan RoundGap = TimeSpan.FromMilliseconds(300);     // 라운드당 2요청 → 초당 ≤ 7회(공개 API 한도 10회 미만)
        public const long PaperOrderKrw = 10_000;                                        // 가상 주문 목표 금액(최소 5,000원 위)

        public static async Task<UpbitPublicFeedReport> RunAsync(HttpMessageInvoker http, int rounds, CancellationToken ct)
        {
            var rep = new UpbitPublicFeedReport();
            void Line(string s) => rep.Lines.Add(s);
            Line("[upbit] 대상 https://" + UpbitPublicFeedGuard.Host + " · KRW-BTC · 공개 API(인증 0) · 라운드 " + rounds);
            for (int i = 0; i < rounds; i++)
            {
                if (i > 0) await Task.Delay(RoundGap, ct).ConfigureAwait(false);
                rep.Rounds++;
                var (tStatus, tBody, tMs) = await FetchAsync(http, UpbitPublicFeedGuard.TickerUri, ct).ConfigureAwait(false);
                rep.TickerLatencyMs.Add(tMs);
                UpbitTicker tk = default;                                                    // && 단락 → out 미대입 경로(CS0165) 차단
                bool tOk = tStatus == 200 && UpbitPublicProtocol.TryParseTicker(tBody, UpbitPublicProtocol.MarketKrwBtc, out tk);
                if (tOk) { rep.TickerOk++; rep.LastTicker = tk; }

                var (oStatus, oBody, oMs) = await FetchAsync(http, UpbitPublicFeedGuard.OrderbookUri, ct).ConfigureAwait(false);
                rep.OrderbookLatencyMs.Add(oMs);
                UpbitL1Quote q = default;
                bool oOk = oStatus == 200 && UpbitPublicProtocol.TryParseOrderbookL1(oBody, UpbitPublicProtocol.MarketKrwBtc, out q);
                if (oOk) { rep.OrderbookOk++; rep.LastQuote = q; }

                Line("[round " + (i + 1) + "] ticker HTTP " + tStatus + (tOk ? " · 현재가 " + Won(rep.LastTicker.TradePriceRaw) : " · 판독 실패") + " · " + tMs.ToString("F1", CultureInfo.InvariantCulture) + "ms / "
                     + "orderbook HTTP " + oStatus + (oOk ? " · 매수1 " + Won(rep.LastQuote.BidRaw) + " · 매도1 " + Won(rep.LastQuote.AskRaw) : " · 판독 실패") + " · " + oMs.ToString("F1", CultureInfo.InvariantCulture) + "ms");
            }
            Line("[latency] ticker " + Stats(rep.TickerLatencyMs) + " · orderbook " + Stats(rep.OrderbookLatencyMs));
            if (rep.OrderbookOk == 0) { Line("[paper] 호가 판독 0회 — 가상 체결 생략"); return rep; }

            // 수신 L1 → 순수 인메모리 가상 체결(시각 = 로컬 수신 시각 · 서버 시각과의 시계 차이 배제)
            var q1 = rep.LastQuote;
            var ex = new CryptoPaperExchange();
            long now = NowUtcMicros();
            bool quoteOk = ex.UpdateQuote(q1.BidRaw, q1.AskRaw, now);
            long qty = (long)((Int128)PaperOrderKrw * UpbitTickAligner.Scale * UpbitTickAligner.Scale / q1.AskRaw) + 1;
            rep.PaperBuy = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, qty, 0, NowUtcMicros());
            rep.PaperSell = ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, qty, 0, NowUtcMicros());
            rep.PaperAfter = ex.Snapshot();
            Line("[paper] 호가 적재 " + (quoteOk ? "OK" : "거부") + " · 수량 " + Btc(qty) + " BTC");
            Line("[paper] 매수 " + rep.PaperBuy.Status + " · 체결가 " + Won(rep.PaperBuy.PriceRaw) + " · 대금 " + Won(rep.PaperBuy.NotionalRaw) + " · 수수료 " + Won(rep.PaperBuy.FeeRaw));
            Line("[paper] 매도 " + rep.PaperSell.Status + " · 체결가 " + Won(rep.PaperSell.PriceRaw) + " · 대금 " + Won(rep.PaperSell.NotionalRaw) + " · 수수료 " + Won(rep.PaperSell.FeeRaw));
            Line("[paper] 왕복 후 예수금 " + Won(rep.PaperAfter.KrwRaw) + " · BTC " + Btc(rep.PaperAfter.BtcRaw) + " · 누적 수수료 " + Won(rep.PaperAfter.FeesPaidRaw)
                 + " · 왕복 비용 " + Won(CryptoPaperExchange.InitialKrwRaw - rep.PaperAfter.KrwRaw) + " · 송출 0(인메모리)");
            return rep;
        }

        private static async Task<(int Status, byte[] Body, double Ms)> FetchAsync(HttpMessageInvoker http, Uri uri, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            using var req = UpbitPublicFeedGuard.Get(uri);
            using var resp = await UpbitPublicFeedGuard.SendAsync(http, req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            return ((int)resp.StatusCode, body, sw.Elapsed.TotalMilliseconds);
        }

        public static long NowUtcMicros() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10;

        public static string Won(long raw) => ((decimal)raw / UpbitTickAligner.Scale).ToString("#,0.########", CultureInfo.InvariantCulture) + "원";

        public static string Btc(long raw) => ((decimal)raw / UpbitTickAligner.Scale).ToString("0.00000000", CultureInfo.InvariantCulture);

        private static string Stats(List<double> ms)
        {
            if (ms.Count == 0) return "n/a";
            var a = ms.ToArray();
            Array.Sort(a);
            return "최소 " + a[0].ToString("F1", CultureInfo.InvariantCulture) + " · 중앙 " + a[a.Length / 2].ToString("F1", CultureInfo.InvariantCulture)
                   + " · 최대 " + a[^1].ToString("F1", CultureInfo.InvariantCulture) + "ms(첫 요청 = TLS 핸드셰이크 포함)";
        }
    }

    public sealed class UpbitPublicFeedFactAttribute : FactAttribute
    {
        public UpbitPublicFeedFactAttribute() => Skip = UpbitPublicFeedGuard.SkipReason(Environment.GetEnvironmentVariable);
    }
}
