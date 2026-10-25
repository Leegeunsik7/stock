// ============================================================================
// PlutoPublicMacroSource — v12.18.83-DEV: 플루토 공식 거시 수집원(FRED 공식 REST API · 무료 API 키 · 설정 `Pluto.FredApiKey` / 환경 변수 MS2E_FRED_API_KEY)
//   GET https://api.stlouisfed.org/fred/series/observations?series_id={ID}&api_key={KEY}&file_type=json&sort_order=desc&limit=30
//   시리즈 5종 = VIXCLS(VIX 종가) · DGS10(미 10년물 %) · DTWEXBGS(광의 달러 인덱스 · ICE DXY 아님) · DEXKOUS(원/달러) · SP500(지수 추세).
//   최근 30 관측(내림차순) → 최신 유효값 vs 1개월 전(최신 관측일 − 1개월 이전 첫 유효값) 변화 → PlutoGlobalDataIngestor.TryParse 스키마 JSON 1건(asOfUtc = 수집 시각).
//   키 판정(ResolveApiKey): 미설정 = 수집원 미생성(HTTP 0건) · 형식 위반(영소문자/숫자 32자 아님) = 미생성(URL 주입 0) → UI '거시 피드: FRED API 키 미설정 · 관측 대기'.
//   Fail-Closed: error_code 응답 · 비 JSON · 내림차순 위반 · 관측 부족 · 최신 관측 10일 초과 · 0 이하/비유한 값 · 범위 밖 = 그 필드만 누락(직전 안정값 유지) ·
//     전 필드 누락 = 예외 · HTTP 429 = 이번 주기 잔여 요청 즉시 취소 + 예외(재시도 = 다음 주기뿐 · 큐잉 0). 정상 응답 = RefreshInterval(15분) 재사용(FRED = 일 1회 갱신).
//   ★API 키 = 쿼리 문자열에만(예외 메시지 · Name · 로그 미포함). 감성 = 미제공. 조회 전용 — 쿠키 · 송출 본문 0. ★QuantumVault · 피어 · 브로커 경로 0.
// ============================================================================
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Adapters
{
    public enum FredKeyState : byte
    {
        Missing = 0,
        Malformed = 1,
        Ok = 2,
    }

    public sealed class PlutoPublicMacroSource : IPlutoMacroSource, IDisposable
    {
        public const int MaxBodyBytes = 64 * 1024;
        public const int ObservationLimit = 30, ApiKeyLength = 32, MaxObservationAgeDays = 10;
        public const string BaseUrl = "https://api.stlouisfed.org/fred/series/observations";
        public const string ApiKeyEnvVar = "MS2E_FRED_API_KEY";
        public const string ApiKeyConfigKey = "Pluto.FredApiKey";
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);
        public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

        public static readonly (string Id, PlutoMacroField Field)[] Series =
        {
            ("VIXCLS", PlutoMacroField.Vix),
            ("DGS10", PlutoMacroField.Us10y),
            ("DTWEXBGS", PlutoMacroField.Dollar),
            ("DEXKOUS", PlutoMacroField.UsdKrw),
            ("SP500", PlutoMacroField.EquityTrend),
        };

        private readonly HttpClient _http;
        private readonly string _apiKey;
        private readonly Func<DateTime> _utcNow;
        private byte[]? _cached;
        private DateTime _cachedAtUtc;

        public PlutoPublicMacroSource(string apiKey, HttpMessageHandler? handler = null, Func<DateTime>? utcNow = null)
        {
            if (ResolveApiKey(apiKey, null, out var key) != FredKeyState.Ok) throw new ArgumentException("FRED API 키 형식 위반", nameof(apiKey));
            _apiKey = key!;
            _http = handler is null ? new HttpClient() : new HttpClient(handler);
            _http.MaxResponseContentBufferSize = MaxBodyBytes;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public string Name => "FRED 공식 API(일별)";

        /// <summary>마지막 네트워크 수집에서 채운 필드(0 = 수집 전).</summary>
        public PlutoMacroField LastFields { get; private set; }

        /// <summary>키 판정: 설정값 우선 · 비면 환경 변수. 공백 = 미설정 · 영소문자/숫자 32자 아님 = 형식 위반(키 null).</summary>
        public static FredKeyState ResolveApiKey(string? configured, string? environment, out string? key)
        {
            key = null;
            string candidate = (string.IsNullOrWhiteSpace(configured) ? environment : configured)?.Trim() ?? "";
            if (candidate.Length == 0) return FredKeyState.Missing;
            if (candidate.Length != ApiKeyLength) return FredKeyState.Malformed;
            foreach (char c in candidate)
                if (c is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')) return FredKeyState.Malformed;
            key = candidate;
            return FredKeyState.Ok;
        }

        /// <summary>UI 거시 피드 문구(호스트 → 학습 포트).</summary>
        public static string FeedText(FredKeyState state) => state switch
        {
            FredKeyState.Ok => "거시 피드: FRED 공식 API(VIX · 10년물 · 달러 · 원/달러 · S&P500)",
            FredKeyState.Malformed => "거시 피드: FRED API 키 형식 오류 · 관측 대기",
            _ => "거시 피드: FRED API 키 미설정 · 관측 대기",
        };

        public static Uri SeriesUri(string id, string apiKey) =>
            new($"{BaseUrl}?series_id={id}&api_key={apiKey}&file_type=json&sort_order=desc&limit={ObservationLimit}");

        public async Task<byte[]> FetchAsync(CancellationToken ct)
        {
            DateTime now = _utcNow();
            if (_cached is not null && now - _cachedAtUtc < RefreshInterval) return _cached;

            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var tasks = new Task<byte[]?>[Series.Length];
            for (int i = 0; i < Series.Length; i++) tasks[i] = FetchSeriesAsync(SeriesUri(Series[i].Id, _apiKey), cycle);
            await Task.WhenAll(tasks).ConfigureAwait(false);                                // 429 = 결함 태스크 예외 전파(취소된 잔여 요청은 예외 미기여)

            var bodies = new byte[]?[Series.Length];
            for (int i = 0; i < tasks.Length; i++) bodies[i] = tasks[i].Result;
            byte[]? json = BuildObservation(bodies, now, out var fields);
            LastFields = fields;
            if (json is null) throw new InvalidOperationException("FRED 시리즈 전량 판독 불가");
            _cached = json;
            _cachedAtUtc = now;
            return json;
        }

        /// <summary>시리즈 1종 GET. 429 = 이번 주기 잔여 요청 취소 + 예외 · 그 외 실패 = null(그 필드만 누락).</summary>
        private async Task<byte[]?> FetchSeriesAsync(Uri uri, CancellationTokenSource cycle)
        {
            HttpResponseMessage resp;
            try { resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, cycle.Token).ConfigureAwait(false); }
            catch (HttpRequestException) { return null; }
            using (resp)
            {
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    cycle.Cancel();
                    throw new HttpRequestException("FRED 429 — 이번 주기 중단 · 다음 주기까지 쿨다운", null, resp.StatusCode);
                }
                if (!resp.IsSuccessStatusCode) return null;
                return await resp.Content.ReadAsByteArrayAsync(cycle.Token).ConfigureAwait(false);
            }
        }

        /// <summary>시리즈 JSON 5종(Series 순서 · null = 수신 실패) → 관측 JSON. 유효 필드 0 = null.</summary>
        public static byte[]? BuildObservation(byte[]?[] bodies, DateTime nowUtc, out PlutoMacroField fields)
        {
            ArgumentNullException.ThrowIfNull(bodies);
            if (bodies.Length != Series.Length) throw new ArgumentException("시리즈 수 불일치", nameof(bodies));
            fields = PlutoMacroField.None;
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                for (int i = 0; i < Series.Length; i++)
                {
                    var (_, field) = Series[i];
                    if (bodies[i] is not byte[] body || !TryParseObservations(body, nowUtc, out var obs)) continue;
                    double last = obs[0].Value;
                    if (field == PlutoMacroField.Vix)
                    {
                        if (last > PlutoMacroSnapshot.MaxVixX100 / 100.0) continue;
                        w.WriteNumber("vix", Math.Round(last, 2));
                        fields |= field;
                        continue;
                    }
                    if (!TryMonthBase(obs, out double baseValue)) continue;                 // 1개월 창 미달 = 그 필드 누락
                    double bp = field == PlutoMacroField.Us10y
                        ? (last - baseValue) * 100                                          // % → bp(절대 변화)
                        : (last / baseValue - 1) * 10000;                                   // 상대 변화 bp
                    if (!double.IsFinite(bp) || Math.Abs(bp) > PlutoMacroSnapshot.MaxChangeBp) continue;   // 이상치 = 그 필드 누락(직전 안정값 유지)
                    int v = (int)Math.Round(bp, MidpointRounding.AwayFromZero);
                    switch (field)
                    {
                        case PlutoMacroField.Us10y: w.WriteNumber("us10yChangeBp", v); break;
                        case PlutoMacroField.Dollar: w.WriteNumber("dxyChangeBp", v); break;
                        case PlutoMacroField.UsdKrw: w.WriteNumber("usdKrwChangeBp", v); break;
                        default: w.WriteNumber("equityTrendBp", v); break;
                    }
                    fields |= field;
                }
                w.WriteString("asOfUtc", new DateTimeOffset(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc)));
                w.WriteEndObject();
            }
            return fields == PlutoMacroField.None ? null : buffer.WrittenSpan.ToArray();
        }

        /// <summary>1개월 기준값 = 최신 관측일 − 1개월 이하인 첫(가장 최근) 유효 관측. 없음 = false.</summary>
        public static bool TryMonthBase(IReadOnlyList<(DateTime Date, double Value)> desc, out double baseValue)
        {
            baseValue = 0;
            DateTime cutoff = desc[0].Date.AddMonths(-1);
            for (int i = 1; i < desc.Count; i++)
                if (desc[i].Date <= cutoff) { baseValue = desc[i].Value; return true; }
            return false;
        }

        /// <summary>
        /// FRED observations JSON(Utf8JsonReader 타입드 게터 · 예외 = JsonException 만 catch) → 내림차순 유효 관측(결측 '.' = 건너뜀).
        /// 최상위 객체 아님 · error_code 존재 · observations 배열 부재 · 날짜 형식/내림차순 위반 · 0 이하/비유한 값 · 유효 관측 0 · 최신 관측 MaxObservationAgeDays 초과 = false.
        /// </summary>
        public static bool TryParseObservations(ReadOnlySpan<byte> utf8Json, DateTime nowUtc, out (DateTime Date, double Value)[] observations)
        {
            observations = Array.Empty<(DateTime, double)>();
            var list = new List<(DateTime, double)>(ObservationLimit);
            bool sawArray = false;
            try
            {
                var r = new Utf8JsonReader(utf8Json);
                if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("error_code"u8)) return false;
                    if (!r.ValueTextEquals("observations"u8)) { if (!r.Read()) return false; r.Skip(); continue; }
                    if (!r.Read() || r.TokenType != JsonTokenType.StartArray) return false;
                    sawArray = true;
                    DateTime prev = DateTime.MaxValue;
                    while (r.Read() && r.TokenType == JsonTokenType.StartObject)
                    {
                        DateTime? date = null;
                        string? value = null;
                        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                        {
                            if (r.ValueTextEquals("date"u8))
                            {
                                if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
                                if (!DateTime.TryParseExact(r.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
                                date = d;
                            }
                            else if (r.ValueTextEquals("value"u8))
                            {
                                if (!r.Read() || r.TokenType != JsonTokenType.String) return false;
                                value = r.GetString();
                            }
                            else { if (!r.Read()) return false; r.Skip(); }
                        }
                        if (r.TokenType != JsonTokenType.EndObject || date is not DateTime dt || value is null || dt >= prev) return false;
                        prev = dt;
                        if (value is "." or "") continue;                                      // 휴장일 결측
                        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || !double.IsFinite(v) || v <= 0) return false;
                        list.Add((dt, v));
                    }
                    if (r.TokenType != JsonTokenType.EndArray) return false;
                }
            }
            catch (JsonException) { return false; }
            if (!sawArray || list.Count == 0 || (nowUtc.Date - list[0].Item1).TotalDays > MaxObservationAgeDays) return false;
            observations = list.ToArray();
            return true;
        }

        public void Dispose() => _http.Dispose();
    }
}
