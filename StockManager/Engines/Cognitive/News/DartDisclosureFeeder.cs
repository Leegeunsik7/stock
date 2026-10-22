// ============================================================================
// DartDisclosureFeeder — v12.18.64-DEV: 플루토 브레인 2단계 — DART Open API 공시검색(list.json) 폴링 피더(국내 · 콜드)
//
//   요청: GET {BaseUri}/api/list.json?crtfc_key=…&bgn_de=오늘(KST)&end_de=오늘&page_no=1&page_count=100 (인증키 = 생성자 주입 · 로그 0).
//   응답: {"status":"000","message":"정상","list":[{"corp_name","stock_code","report_nm","rcept_no","rcept_dt",…}]} —
//     Utf8JsonReader 타입드 게터만(모르는 속성 = Skip). status "000" = 정상 · "013" = 조회 결과 없음(정상 · 0건) · 그 밖 = ApiError(인증키 오류 · 한도 초과 등).
//   항목: 헤드라인 = "[회사명] 보고서명"(예: 주요사항보고서(유상증자결정) · 단일판매ㆍ공급계약체결) · SymbolHint = stock_code(6자리 · 비상장 = 별칭 매핑) ·
//     중복 키 = rcept_no(접수번호 14자리).
//   ★시각: list.json 은 접수 '일자'만 준다 → 발생 시각 = 피더가 처음 본 시각(피더 시계). 기동 직후 첫 성공 회차 = 기준선(접수번호 기록만 · 전달 0) —
//     장 시작 전에 이미 나온 공시를 '방금 나온 공시'로 위장하지 않는다(감쇄 과대평가 차단 · 정직).
//   ★인증키 미설정 = 호스트가 피더를 만들지 않는다(네트워크 호출 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace StockManager.Engines.Cognitive.News
{
    public sealed class DartDisclosureFeeder : PollingNewsFeeder
    {
        public static readonly Uri DefaultBaseUri = new("https://opendart.fss.or.kr/");
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
        public const int PageCount = 100;

        private readonly string _apiKey;
        private readonly Uri _baseUri;
        private readonly bool _skipInitialBacklog;

        public DartDisclosureFeeder(string apiKey, Func<PlutoNewsItem, bool> sink, IVirtualClock clock, HttpMessageHandler? handler = null,
            TimeSpan? pollInterval = null, TimeSpan? timeout = null, Uri? baseUri = null, bool skipInitialBacklog = true,
            int maxPayloadBytes = DefaultMaxPayloadBytes)
            : base("DART 공시", PlutoSource.Dart, sink, clock, handler, pollInterval ?? DefaultInterval, timeout, maxPayloadBytes)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("DART 인증키 필요", nameof(apiKey));
            _apiKey = apiKey.Trim();
            _baseUri = baseUri ?? DefaultBaseUri;
            _skipInitialBacklog = skipInitialBacklog;
        }

        protected override HttpRequestMessage BuildRequest(long nowUtcTicks)
        {
            string day = new DateTime(nowUtcTicks + PlutoClock.KstOffsetTicks).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            var uri = new Uri(_baseUri, "api/list.json?crtfc_key=" + Uri.EscapeDataString(_apiKey) + "&bgn_de=" + day + "&end_de=" + day
                + "&page_no=1&page_count=" + PageCount.ToString(CultureInfo.InvariantCulture));
            return new HttpRequestMessage(HttpMethod.Get, uri);
        }

        protected override bool ShouldDeliver(in ParsedItem item, bool firstSuccessfulPoll) => !(firstSuccessfulPoll && _skipInitialBacklog);

        protected override PlutoFeederFault Parse(ReadOnlySpan<byte> payload, long nowUtcTicks, List<ParsedItem> output)
        {
            try { return ParseList(payload, nowUtcTicks, output); }
            catch (JsonException) { output.Clear(); return PlutoFeederFault.Malformed; }
        }

        /// <summary>list.json 원문 → 항목(발생 시각 = nowUtcTicks). 시험 · 리플레이 공용.</summary>
        public static PlutoFeederFault ParseListForTest(ReadOnlySpan<byte> payload, long nowUtcTicks, List<PlutoNewsItem> output)
        {
            var tmp = new List<ParsedItem>();
            PlutoFeederFault f;
            try { f = ParseList(payload, nowUtcTicks, tmp); }
            catch (JsonException) { return PlutoFeederFault.Malformed; }
            foreach (var p in tmp) output.Add(p.Item);
            return f;
        }

        private static PlutoFeederFault ParseList(ReadOnlySpan<byte> payload, long nowUtcTicks, List<ParsedItem> output)
        {
            var r = new Utf8JsonReader(payload, new JsonReaderOptions { MaxDepth = 8 });
            if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return PlutoFeederFault.Malformed;
            string? status = null;
            bool sawList = false;
            while (r.Read())
            {
                if (r.TokenType == JsonTokenType.EndObject) break;
                if (r.TokenType != JsonTokenType.PropertyName) return PlutoFeederFault.Malformed;
                if (r.ValueTextEquals("status"u8))
                {
                    r.Read();
                    status = r.TokenType == JsonTokenType.String ? r.GetString() : null;
                }
                else if (r.ValueTextEquals("list"u8))
                {
                    r.Read();
                    if (r.TokenType != JsonTokenType.StartArray) return PlutoFeederFault.Malformed;
                    sawList = true;
                    while (r.Read() && r.TokenType != JsonTokenType.EndArray)
                    {
                        if (r.TokenType != JsonTokenType.StartObject) return PlutoFeederFault.Malformed;
                        string? corp = null, code = null, report = null, rcept = null;
                        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
                        {
                            if (r.TokenType != JsonTokenType.PropertyName) return PlutoFeederFault.Malformed;
                            if (r.ValueTextEquals("corp_name"u8)) { r.Read(); corp = StringOrNull(ref r); }
                            else if (r.ValueTextEquals("stock_code"u8)) { r.Read(); code = StringOrNull(ref r); }
                            else if (r.ValueTextEquals("report_nm"u8)) { r.Read(); report = StringOrNull(ref r); }
                            else if (r.ValueTextEquals("rcept_no"u8)) { r.Read(); rcept = StringOrNull(ref r); }
                            else { r.Read(); r.Skip(); }
                        }
                        if (string.IsNullOrWhiteSpace(report) || string.IsNullOrWhiteSpace(rcept)) continue;   // 필수 필드 결손 항목 = 건너뜀(나머지 계속)
                        string headline = string.IsNullOrWhiteSpace(corp) ? report.Trim() : "[" + corp.Trim() + "] " + report.Trim();
                        string? hint = code is { Length: 6 } ? code : null;
                        output.Add(new ParsedItem(rcept.Trim(), new PlutoNewsItem(PlutoSource.Dart, headline, nowUtcTicks, null, hint)));
                    }
                }
                else { r.Read(); r.Skip(); }
            }
            if (status is null) return PlutoFeederFault.Malformed;
            if (status == "013") { output.Clear(); return PlutoFeederFault.None; }
            if (status != "000") { output.Clear(); return PlutoFeederFault.ApiError; }
            return sawList ? PlutoFeederFault.None : PlutoFeederFault.Malformed;
        }

        private static string? StringOrNull(ref Utf8JsonReader r)
        {
            if (r.TokenType == JsonTokenType.String) return r.GetString();
            r.Skip();
            return null;
        }
    }
}
