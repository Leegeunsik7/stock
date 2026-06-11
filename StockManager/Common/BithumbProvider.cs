// ============================================================================
// BithumbProvider - 빗썸 공개 API (인증 불필요) (v3.3)
//  · 전체 원화 티커, 스파크라인용 일봉 캔들
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public record BithumbTick(
        string Symbol, decimal Price, decimal ChangeRate, decimal ChangeAmt,
        decimal TradeValueKrw, decimal Volume24h, double[] Spark);

    public class BithumbProvider
    {
        private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.bithumb.com") };
        private static readonly JsonDocumentOptions Opts = new() { AllowTrailingCommas = true };

        /// <summary>전체 원화 종목 티커 (인증 불필요)</summary>
        public async Task<List<BithumbTick>> GetAllTickersAsync()
        {
            var result = new List<BithumbTick>();
            try
            {
                var json = await Http.GetStringAsync("/public/ticker/ALL_KRW");
                using var doc = JsonDocument.Parse(json, Opts);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var data)) return result;
                foreach (var coin in data.EnumerateObject())
                {
                    if (coin.Name == "date") continue;
                    var v = coin.Value;
                    decimal price = GetDec(v, "closing_price");
                    decimal open = GetDec(v, "opening_price");
                    decimal changeAmt = price - open;
                    decimal changeRate = open > 0 ? changeAmt / open * 100m : 0m;
                    decimal tradeVal = GetDec(v, "acc_trade_value_24H");
                    decimal vol = GetDec(v, "units_traded_24H");
                    result.Add(new BithumbTick(coin.Name, price, changeRate, changeAmt, tradeVal, vol, Array.Empty<double>()));
                }
            }
            catch { }
            return result;
        }

        /// <summary>일봉 스파크라인 (24h 캔들, 최근 8개 종가)</summary>
        public async Task<double[]> GetSparkAsync(string symbol)
        {
            try
            {
                var json = await Http.GetStringAsync($"/public/candlestick/{symbol}_KRW/24h");
                using var doc = JsonDocument.Parse(json, Opts);
                if (!doc.RootElement.TryGetProperty("data", out var arr)) return Array.Empty<double>();
                var closes = arr.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3)
                    .Select(e => { double.TryParse(e[2].GetString(), out var c); return c; })
                    .Where(c => c > 0).ToList();
                return closes.Skip(Math.Max(0, closes.Count - 8)).ToArray();
            }
            catch { return Array.Empty<double>(); }
        }

        private static decimal GetDec(JsonElement e, string key) =>
            e.TryGetProperty(key, out var p) && decimal.TryParse(p.GetString(), out var v) ? v : 0m;
    }
}
