// ============================================================================
// CryptoProviders - 암호화폐 시세 (업비트 REST/WebSocket + 바이낸스 REST)
//  v2.5: 업비트 WebSocket 스트리밍, 바이낸스 USDT, 김치 프리미엄 교차계산용
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public class CryptoMarket
    {
        public string Market { get; set; } = "";       // "KRW-BTC"
        public string KoreanName { get; set; } = "";
        public string EnglishName { get; set; } = "";
    }

    public class CryptoTick
    {
        public string Market { get; set; } = "";
        public string KoreanName { get; set; } = "";
        public decimal Price { get; set; }
        public decimal ChangeRate { get; set; }        // 부호 포함 (0.0123 = +1.23%)
        public decimal High24 { get; set; }
        public decimal Low24 { get; set; }
        public decimal AccTradePrice24h { get; set; }  // 24h 누적 거래대금(KRW)
    }

    public class BinanceTick
    {
        public string Symbol { get; set; } = "";        // "BTCUSDT"
        public decimal Price { get; set; }              // USDT
        public decimal ChangePct { get; set; }          // 24h %, 부호 포함
        public decimal High24 { get; set; }
        public decimal Low24 { get; set; }
    }

    /// <summary>업비트 공개 시세 (api.upbit.com) — API 키 불필요</summary>
    public class UpbitProvider
    {
        private const string Base = "https://api.upbit.com/v1";
        private readonly HttpClient _http = HttpPool.Shared;
        private Dictionary<string, string>? _nameCache;

        public async Task<List<CryptoMarket>> GetMarketsAsync()
        {
            var list = new List<CryptoMarket>();
            try
            {
                using var res = await _http.GetAsync($"{Base}/market/all?isDetails=false");
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var market = el.GetProperty("market").GetString() ?? "";
                    if (!market.StartsWith("KRW-", StringComparison.Ordinal)) continue;
                    list.Add(new CryptoMarket
                    {
                        Market = market,
                        KoreanName = el.TryGetProperty("korean_name", out var k) ? k.GetString() ?? "" : "",
                        EnglishName = el.TryGetProperty("english_name", out var e) ? e.GetString() ?? "" : ""
                    });
                }
                _nameCache = list.ToDictionary(m => m.Market, m => m.KoreanName, StringComparer.OrdinalIgnoreCase);
                Log.Info($"[Upbit] KRW 마켓 {list.Count}종목 로드");
            }
            catch (Exception ex) { Log.Warn($"[Upbit] 마켓 조회 실패: {ex.Message}"); }
            return list;
        }

        public async Task<List<CryptoTick>> GetTickersAsync(IEnumerable<string> markets)
        {
            var result = new List<CryptoTick>();
            var arr = markets?.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray() ?? Array.Empty<string>();
            if (arr.Length == 0) return result;
            try
            {
                var q = string.Join(",", arr);
                using var res = await _http.GetAsync($"{Base}/ticker?markets={Uri.EscapeDataString(q)}");
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var market = el.GetProperty("market").GetString() ?? "";
                    result.Add(new CryptoTick
                    {
                        Market = market,
                        KoreanName = _nameCache != null && _nameCache.TryGetValue(market, out var n) ? n : market,
                        Price = D(el, "trade_price"),
                        ChangeRate = D(el, "signed_change_rate"),
                        High24 = D(el, "high_price"),
                        Low24 = D(el, "low_price"),
                        AccTradePrice24h = D(el, "acc_trade_price_24h")
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"[Upbit] 시세 조회 실패: {ex.Message}"); }
            return result;
        }

        private static decimal D(JsonElement el, string prop)
        {
            try
            {
                return el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
                    ? v.GetDecimal() : 0m;
            }
            catch { return 0m; }
        }

        /// <summary>일봉 캔들 (ChartForm 공유용) — 오래된 순 정렬</summary>
        public async Task<List<Candle>> GetCandlesAsync(string market, int count = 200)
        {
            var list = new List<Candle>();
            if (string.IsNullOrWhiteSpace(market)) return list;
            try
            {
                using var res = await _http.GetAsync($"{Base}/candles/days?market={Uri.EscapeDataString(market)}&count={Math.Clamp(count, 1, 200)}");
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    DateTime.TryParse(el.TryGetProperty("candle_date_time_kst", out var dt) ? dt.GetString() : null, out var date);
                    list.Add(new Candle
                    {
                        Date = date,
                        Open = D(el, "opening_price"),
                        High = D(el, "high_price"),
                        Low = D(el, "low_price"),
                        Close = D(el, "trade_price"),
                        Volume = (long)D(el, "candle_acc_trade_volume")
                    });
                }
                list.Reverse(); // 업비트는 최신순 → 오래된 순으로
            }
            catch (Exception ex) { Log.Warn($"[Upbit] 캔들 조회 실패: {ex.Message}"); }
            return list;
        }
    }

    /// <summary>업비트 WebSocket 실시간 스트리밍 (wss) — 틱 단위 ticker 수신</summary>
    public class UpbitWebSocketClient : IDisposable
    {
        public event Action<CryptoTick>? TickReceived;
        public bool Connected => _ws?.State == WebSocketState.Open;

        private ClientWebSocket? _ws;
        private CancellationTokenSource? _cts;
        private string[] _codes = Array.Empty<string>();
        private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

        public void SetNames(Dictionary<string, string> names) => _names = names;

        public Task StartAsync(IEnumerable<string> codes)
        {
            Stop();
            _codes = codes?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct().ToArray() ?? Array.Empty<string>();
            if (_codes.Length == 0) return Task.CompletedTask;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => RunLoopAsync(ct), ct);
            return Task.CompletedTask;
        }

        private async Task RunLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    _ws = new ClientWebSocket();
                    await _ws.ConnectAsync(new Uri("wss://api.upbit.com/websocket/v1"), ct);
                    var codes = string.Join(",", _codes.Select(c => $"\"{c}\""));
                    var sub = "[{\"ticket\":\"ms2e-stock\"},{\"type\":\"ticker\",\"codes\":[" + codes + "]},{\"format\":\"SIMPLE\"}]";
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, ct);
                    Log.Info($"[UpbitWS] 연결 · 구독 {_codes.Length}종목");

                    var buf = new byte[16384];
                    using var ms = new MemoryStream();
                    while (_ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        ms.SetLength(0);
                        WebSocketReceiveResult r;
                        do
                        {
                            r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                            ms.Write(buf, 0, r.Count);
                        } while (!r.EndOfMessage);

                        if (r.MessageType == WebSocketMessageType.Close) break;
                        Parse(ms.ToArray());
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"[UpbitWS] {ex.Message}"); }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(2000, ct); } catch { break; }   // 재연결 대기
            }
        }

        private void Parse(byte[] bytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var el = doc.RootElement;
                var code = el.TryGetProperty("cd", out var c) ? c.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(code)) return;
                TickReceived?.Invoke(new CryptoTick
                {
                    Market = code,
                    KoreanName = _names.TryGetValue(code, out var n) ? n : code,
                    Price = D(el, "tp"),
                    ChangeRate = D(el, "scr"),
                    High24 = D(el, "hp"),
                    Low24 = D(el, "lp"),
                    AccTradePrice24h = D(el, "atp24h")
                });
            }
            catch { /* 단일 프레임 파싱 실패는 무시 */ }
        }

        private static decimal D(JsonElement el, string p)
        {
            try { return el.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m; }
            catch { return 0m; }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _ws?.Abort(); _ws?.Dispose(); } catch { }
            _ws = null;
        }

        public void Dispose() => Stop();
    }

    /// <summary>바이낸스 공개 시세 (api.binance.com) — API 키 불필요</summary>
    public class BinanceProvider
    {
        private const string Base = "https://api.binance.com/api/v3";
        private readonly HttpClient _http = HttpPool.Shared;

        public async Task<Dictionary<string, BinanceTick>> Get24hAsync(IEnumerable<string> symbols)
        {
            var dict = new Dictionary<string, BinanceTick>(StringComparer.OrdinalIgnoreCase);
            var arr = symbols?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray() ?? Array.Empty<string>();
            if (arr.Length == 0) return dict;
            try
            {
                var param = "[" + string.Join(",", arr.Select(s => $"\"{s}\"")) + "]";
                var url = $"{Base}/ticker/24hr?symbols={Uri.EscapeDataString(param)}";
                using var res = await _http.GetAsync(url);
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var sym = el.GetProperty("symbol").GetString() ?? "";
                    dict[sym] = new BinanceTick
                    {
                        Symbol = sym,
                        Price = Dec(el, "lastPrice"),
                        ChangePct = Dec(el, "priceChangePercent"),
                        High24 = Dec(el, "highPrice"),
                        Low24 = Dec(el, "lowPrice")
                    };
                }
            }
            catch (Exception ex) { Log.Warn($"[Binance] 24h 조회 실패: {ex.Message}"); }
            return dict;
        }

        /// <summary>USDT 마켓 전체 24h 시세 — 심볼 목록이 큰 화면(CoinPage 김프)용 전체 조회 후 필터.</summary>
        public async Task<Dictionary<string, BinanceTick>> GetAllUsdt24hAsync()
        {
            var dict = new Dictionary<string, BinanceTick>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var res = await _http.GetAsync($"{Base}/ticker/24hr");
                res.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var sym = el.GetProperty("symbol").GetString() ?? "";
                    if (!sym.EndsWith("USDT", StringComparison.Ordinal)) continue;
                    dict[sym] = new BinanceTick
                    {
                        Symbol = sym,
                        Price = Dec(el, "lastPrice"),
                        ChangePct = Dec(el, "priceChangePercent"),
                        High24 = Dec(el, "highPrice"),
                        Low24 = Dec(el, "lowPrice")
                    };
                }
            }
            catch (Exception ex) { Log.Warn($"[Binance] 전체 24h 조회 실패: {ex.Message}"); }
            return dict;
        }

        // 바이낸스는 숫자를 문자열로 반환 → 문자열/숫자 모두 처리
        private static decimal Dec(JsonElement el, string p)
        {
            try
            {
                if (!el.TryGetProperty(p, out var v)) return 0m;
                if (v.ValueKind == JsonValueKind.String)
                    return decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0m;
                if (v.ValueKind == JsonValueKind.Number) return v.GetDecimal();
                return 0m;
            }
            catch { return 0m; }
        }
    }
}
