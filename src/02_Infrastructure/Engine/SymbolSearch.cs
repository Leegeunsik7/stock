// ============================================================================
// SymbolSearch — 라이브 종목 검색 (전 종목 이름→코드). UI 경계 어댑터.
//
//  큐레이트 내장 카탈로그(SymbolCatalog)는 대표주만 담아 롱테일이 누락된다. 네이버 증권 자동완성으로
//  코스피·코스닥·미국(NASDAQ/NYSE/AMEX) 전 종목을 한글명까지 실시간 검색한다(코인은 Upbit 라이브가 담당).
//
//  ⚠️ 안전 경계: 네트워크·파싱·취소 실패는 '빈 결과'로 흡수한다 — 호출자(SymbolPicker)는 로컬 카탈로그로
//  그레이스풀 폴백. 결정론 코어와 무관(UI 입력 보조). 실 provider 왕복은 CI 미검증(사용자 측), 파싱은 단위 검증.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public interface ISymbolSearch
    {
        Task<List<SymbolEntry>> SearchAsync(string query, CancellationToken ct = default);
    }

    /// <summary>네이버 증권 자동완성 기반 전 종목 라이브 검색. 실패는 빈 결과로 흡수(코어 무영향).</summary>
    public sealed class NaverSymbolSearch : ISymbolSearch
    {
        private const string Endpoint = "https://m.stock.naver.com/front-api/search/autoComplete?target=stock&query=";
        private readonly HttpClient _http = HttpPool.Shared;

        public async Task<List<SymbolEntry>> SearchAsync(string query, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(query)) return new List<SymbolEntry>();
            try
            {
                using var res = await _http.GetAsync(Endpoint + Uri.EscapeDataString(query.Trim()), ct);
                if (!res.IsSuccessStatusCode) return new List<SymbolEntry>();
                return ParseAutoComplete(await res.Content.ReadAsStringAsync(ct));
            }
            catch { return new List<SymbolEntry>(); }   // 네트워크·파싱·취소 → 빈(로컬 폴백)
        }

        /// <summary>자동완성 JSON → SymbolEntry 목록(순수·결정론·테스트 가능). category=="stock" + 알려진 시장만.</summary>
        public static List<SymbolEntry> ParseAutoComplete(string json)
        {
            var list = new List<SymbolEntry>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("result", out var r) ||
                    !r.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                    return list;
                foreach (var it in items.EnumerateArray())
                {
                    if (Str(it, "category") != "stock") continue;   // 지수·코인 등 제외(코인은 Upbit 담당)
                    string code = Str(it, "code");
                    if (code.Length == 0) continue;
                    if (!MapMarket(Str(it, "typeCode"), out var market, out var ccy)) continue;   // 미상 시장 제외
                    string name = Str(it, "name");
                    list.Add(new SymbolEntry(code, name.Length == 0 ? code : name, market, ccy));
                }
            }
            catch { /* 파싱 실패 → 빈 */ }
            return list;
        }

        private static bool MapMarket(string typeCode, out Market market, out Currency ccy)
        {
            switch (typeCode)
            {
                case "KOSPI":  market = Market.KRX_KOSPI;  ccy = Currency.KRW; return true;
                case "KOSDAQ": market = Market.KRX_KOSDAQ; ccy = Currency.KRW; return true;
                case "NASDAQ": market = Market.US_NASDAQ;  ccy = Currency.USD; return true;
                case "NYSE":   market = Market.US_NYSE;    ccy = Currency.USD; return true;
                case "AMEX":   market = Market.US_AMEX;    ccy = Currency.USD; return true;
                default:       market = default; ccy = default; return false;
            }
        }

        private static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    }
}
