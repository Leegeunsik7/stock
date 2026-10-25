// ============================================================================
// UpbitPublicCandleSource — v12.18.80-DEV: 업비트 공개 시세 REST(API 키 불필요) → 플루토 병렬 학습 스웜 가상자산 실데이터 소스
//
//   종목 1건 = GET 3회(전부 공개 시세 · 인증 헤더 0):
//     ① /v1/candles/minutes/1?market=KRW-XXX&count=200(1분 봉 · 아레나 브레인과 같은 봉 주기) ② /v1/trades/ticks?count=200(ask_bid 실측 테이커 방향 → CVD)
//     ③ /v1/orderbook?markets=KRW-XXX(최대 15호가 잔량 · 최우선 매수/매도 호가 → 스프레드 유동성/뎁스벽).
//   레이트 리밋: PlutoRequestPacer 초당 8회 이하(업비트 시세 그룹 한도 초당 10회 대비 여유) — 송신 직전 대기 · 초과 요청 큐잉 0.
//     HTTP 429/418 = RateLimited(스웜이 그 스윕 즉시 중단 + 쿨다운 · 재시도 0 · fail-closed) · 그 외 비 2xx/전송/파싱 실패 = Faulted.
//   파서(UpbitCandleProtocol): Utf8JsonReader 타입드 게터만(TryGetDecimal/TryGetInt64 · 문자열 = ValueTextEquals) · 예외 = JsonException 만 catch → 실패(−1).
//     가격/수량 raw = 원/코인 × 10^8(소수 8자리 초과 = 내림 · 음수/범위 초과 = 실패 · 거래량 범위 초과 = 상한 클램프).
//   동기 HttpClient.Send(워커 스레드 BelowNormal 에서 파싱/분석까지 수행) · 응답 버퍼 512KB 재사용(단일 워커 전용) · 요청당 시한 10초.
//   ★liveArmed:false — 시세 GET 만(주문/계좌/인증 경로 0) · 9자 이상 마켓(키 인코딩 불가)은 스웜이 조회 전 제외.
// ============================================================================
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Feeds.Crypto
{
    public static class UpbitCandleProtocol
    {
        private static readonly decimal RawLimit = (decimal)long.MaxValue / UpbitTickAligner.Scale;
        public const long MaxVolumeRaw = long.MaxValue / 4;

        /// <summary>캔들 배열(최신 → 과거) → 봉(오래된 것부터 · 반환 = 수 · 형식 오류 = −1 · 초과분 = 최신 우선 보존).</summary>
        public static int ParseCandles(ReadOnlySpan<byte> json, Span<PlutoOhlcvBar> dst)
        {
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1;
                int n = 0;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) return -1;
                    long o = 0, h = 0, l = 0, c = 0, v = 0, ts = 0;
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals("opening_price"u8)) { if (!ReadRaw(ref r, out o)) return -1; }
                        else if (r.ValueTextEquals("high_price"u8)) { if (!ReadRaw(ref r, out h)) return -1; }
                        else if (r.ValueTextEquals("low_price"u8)) { if (!ReadRaw(ref r, out l)) return -1; }
                        else if (r.ValueTextEquals("trade_price"u8)) { if (!ReadRaw(ref r, out c)) return -1; }
                        else if (r.ValueTextEquals("candle_acc_trade_volume"u8)) { if (!ReadVolume(ref r, out v)) return -1; }
                        else if (r.ValueTextEquals("timestamp"u8)) { if (!ReadMillisTicks(ref r, out ts)) return -1; }
                        else { r.Read(); r.Skip(); }
                    }
                    if (r.TokenType != JsonTokenType.EndObject) return -1;
                    if (n < dst.Length) dst[n++] = new PlutoOhlcvBar(ts, o, h, l, c, v);
                }
                dst.Slice(0, n).Reverse();
                return n;
            }
            catch (JsonException) { return -1; }
        }

        /// <summary>체결 배열(최신 → 과거) → 방향 체결(오래된 것부터 · ASK = 매도 테이커 −1 · BID = 매수 테이커 +1 · 미상 = 형식 오류).</summary>
        public static int ParseTrades(ReadOnlySpan<byte> json, Span<PlutoSideTrade> dst)
        {
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1;
                int n = 0;
                while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                {
                    if (r.TokenType != JsonTokenType.StartObject) return -1;
                    long p = 0, v = 0, ts = 0;
                    int side = 0;
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals("trade_price"u8)) { if (!ReadRaw(ref r, out p)) return -1; }
                        else if (r.ValueTextEquals("trade_volume"u8)) { if (!ReadVolume(ref r, out v)) return -1; }
                        else if (r.ValueTextEquals("timestamp"u8)) { if (!ReadMillisTicks(ref r, out ts)) return -1; }
                        else if (r.ValueTextEquals("ask_bid"u8))
                        {
                            if (!r.Read() || r.TokenType != JsonTokenType.String) return -1;
                            side = r.ValueTextEquals("BID"u8) ? 1 : r.ValueTextEquals("ASK"u8) ? -1 : 0;
                            if (side == 0) return -1;
                        }
                        else { r.Read(); r.Skip(); }
                    }
                    if (r.TokenType != JsonTokenType.EndObject) return -1;
                    if (n < dst.Length) dst[n++] = new PlutoSideTrade(ts, p, v, side);
                }
                dst.Slice(0, n).Reverse();
                return n;
            }
            catch (JsonException) { return -1; }
        }

        /// <summary>호가 배열 첫 마켓 → 호가별 잔량(최대 dst 길이) + 최우선 매수/매도 호가(반환 = 단 수 · 형식 오류 = −1 · 빈 배열 = 0).</summary>
        public static int ParseOrderbook(ReadOnlySpan<byte> json, Span<long> bidQty, Span<long> askQty, out long bestBid, out long bestAsk)
        {
            int tiers = ParseOrderbookCore(json, bidQty, askQty, out bestBid, out bestAsk);
            if (tiers <= 0) bestBid = bestAsk = 0;                                   // 실패/빈 호가 = 최우선 호가 판독 0(부분 판독 노출 0)
            return tiers;
        }

        private static int ParseOrderbookCore(ReadOnlySpan<byte> json, Span<long> bidQty, Span<long> askQty, out long bestBid, out long bestAsk)
        {
            bestBid = bestAsk = 0;
            try
            {
                var r = new Utf8JsonReader(json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1;
                if (!r.Read()) return -1;
                if (r.TokenType == JsonTokenType.EndArray) return 0;
                if (r.TokenType != JsonTokenType.StartObject) return -1;
                int tiers = 0, cap = Math.Min(bidQty.Length, askQty.Length);
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (!r.ValueTextEquals("orderbook_units"u8)) { r.Read(); r.Skip(); continue; }
                    if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return -1;
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject) return -1;
                        long ap = 0, bp = 0, az = 0, bz = 0;
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            if (r.ValueTextEquals("ask_price"u8)) { if (!ReadRaw(ref r, out ap)) return -1; }
                            else if (r.ValueTextEquals("bid_price"u8)) { if (!ReadRaw(ref r, out bp)) return -1; }
                            else if (r.ValueTextEquals("ask_size"u8)) { if (!ReadVolume(ref r, out az)) return -1; }
                            else if (r.ValueTextEquals("bid_size"u8)) { if (!ReadVolume(ref r, out bz)) return -1; }
                            else { r.Read(); r.Skip(); }
                        }
                        if (r.TokenType != JsonTokenType.EndObject) return -1;
                        if (tiers == 0) { bestAsk = ap; bestBid = bp; }
                        if (tiers < cap) { bidQty[tiers] = bz; askQty[tiers] = az; tiers++; }
                    }
                }
                if (bestBid <= 0 || bestAsk <= 0 || bestBid >= bestAsk) { bestBid = bestAsk = 0; return tiers == 0 ? 0 : -1; }   // 역전/무효 = fail-closed
                return tiers;
            }
            catch (JsonException) { return -1; }
        }

        private static bool ReadRaw(ref Utf8JsonReader r, out long raw)
        {
            raw = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetDecimal(out decimal d)) return false;
            if (d < 0m || d >= RawLimit) return false;
            raw = (long)decimal.Truncate(d * UpbitTickAligner.Scale);
            return true;
        }

        private static bool ReadVolume(ref Utf8JsonReader r, out long raw)
        {
            raw = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetDecimal(out decimal d) || d < 0m) return false;
            raw = d >= MaxVolumeRaw / (decimal)UpbitTickAligner.Scale ? MaxVolumeRaw : (long)decimal.Truncate(d * UpbitTickAligner.Scale);
            return true;
        }

        private static bool ReadMillisTicks(ref Utf8JsonReader r, out long utcTicks)
        {
            utcTicks = 0;
            if (!r.Read() || r.TokenType != JsonTokenType.Number || !r.TryGetInt64(out long ms) || ms <= 0 || ms > 253_402_300_799_000L) return false;
            utcTicks = DateTime.UnixEpoch.Ticks + ms * TimeSpan.TicksPerMillisecond;
            return true;
        }
    }

    public sealed class UpbitPublicCandleSource : IPlutoAssetDataSource
    {
        public const string BaseUrl = "https://api.upbit.com/v1";
        public const int DefaultPerSecond = 8, CandleCount = 200, TradeCount = 200, BufferBytes = 512 * 1024;
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly HttpClient _http;
        private readonly PlutoRequestPacer _pacer;
        private readonly byte[] _buffer = new byte[BufferBytes];

        public UpbitPublicCandleSource(HttpClient http, PlutoRequestPacer? pacer = null)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _pacer = pacer ?? new PlutoRequestPacer(DefaultPerSecond);
            if (_pacer.PerSecond > 10) throw new ArgumentOutOfRangeException(nameof(pacer), "업비트 시세 한도(초당 10회) 초과 게이트");
        }

        public PlutoScreenMarket Market => PlutoScreenMarket.Crypto;
        public bool IsConnected => true;
        public string Label => "업비트 공개 REST(키 불필요 · 초당 " + _pacer.PerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture) + "회 이하)";
        public PlutoRequestPacer Pacer => _pacer;

        public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(sample);
            string m = Uri.EscapeDataString(entry.Code);

            var r = Get(BaseUrl + "/candles/minutes/1?market=" + m + "&count=" + CandleCount.ToString(System.Globalization.CultureInfo.InvariantCulture), ct, out int len);
            if (r != PlutoFetchOutcome.Ok) return r;
            int bars = UpbitCandleProtocol.ParseCandles(_buffer.AsSpan(0, len), sample.Bars);
            if (bars < 0) return PlutoFetchOutcome.Faulted;
            if (bars == 0) return PlutoFetchOutcome.NoData;
            sample.BarCount = bars;

            r = Get(BaseUrl + "/trades/ticks?market=" + m + "&count=" + TradeCount.ToString(System.Globalization.CultureInfo.InvariantCulture), ct, out len);
            if (r != PlutoFetchOutcome.Ok) return r;
            int trades = UpbitCandleProtocol.ParseTrades(_buffer.AsSpan(0, len), sample.Trades);
            if (trades < 0) return PlutoFetchOutcome.Faulted;
            sample.TradeCount = trades;

            r = Get(BaseUrl + "/orderbook?markets=" + m, ct, out len);
            if (r != PlutoFetchOutcome.Ok) return r;
            int tiers = UpbitCandleProtocol.ParseOrderbook(_buffer.AsSpan(0, len), sample.BidQty, sample.AskQty, out long bid, out long ask);
            if (tiers < 0) return PlutoFetchOutcome.Faulted;
            sample.DepthTiers = tiers;
            sample.BestBid = bid;
            sample.BestAsk = ask;
            return PlutoFetchOutcome.Ok;
        }

        private PlutoFetchOutcome Get(string url, CancellationToken ct, out int length)
        {
            length = 0;
            if (!_pacer.Wait(ct)) { ct.ThrowIfCancellationRequested(); return PlutoFetchOutcome.Faulted; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                using var res = _http.Send(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (res.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)418) return PlutoFetchOutcome.RateLimited;
                if (!res.IsSuccessStatusCode) return PlutoFetchOutcome.Faulted;
                using var stream = res.Content.ReadAsStream(timeout.Token);
                while (true)
                {
                    if (length == _buffer.Length) return PlutoFetchOutcome.Faulted;           // 응답 과대 = 실패(절단 판독 0)
                    int k = stream.Read(_buffer, length, _buffer.Length - length);
                    if (k <= 0) break;
                    length += k;
                }
                return PlutoFetchOutcome.Ok;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return PlutoFetchOutcome.Faulted; }   // 요청 시한 초과
            catch (HttpRequestException) { return PlutoFetchOutcome.Faulted; }
            catch (IOException) { return PlutoFetchOutcome.Faulted; }
        }
    }
}
