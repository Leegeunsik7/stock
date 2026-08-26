// ============================================================================
// HttpFxRateSource — 3단계: 프로덕션 환율 공급자(IFxRateSource) 실 HTTP 폴
//
//  ⚠️ liveArmed:false 불변 — '구성'만. 실 엔드포인트 URL·JSON 스키마·자격증명은 호출부 주입(openQuestions).
//     본 클래스는 자동 배선·실 키 로드를 하지 않는다.
//
//  ── 역할 ────────────────────────────────────────────────────────────────────
//   신뢰 공급자(예: 하나은행 Open API·두나무 환율 피드 등)의 REST 엔드포인트를 GET 폴하여
//   환율(decimal)을 뽑는다. 실 응답 스키마가 공급자마다 다르므로 'dot-path 키'(예 "USDKRW.deal_bas_r",
//   "0.basePrice")로 값 위치만 주입받아 JsonDocument 를 항해한다 — 특정 공급자 스키마를 코드에 박제하지 않음.
//
//  ── fail-soft → 브레이커 fail-closed ────────────────────────────────────────
//   네트워크 오류·비200·파싱 실패·키 부재는 예외를 삼키고 TryPoll=false 를 반환한다(콜드 패스). 이는
//   '이번 폴에 신규 데이터 없음'과 동일 취급 → QFxCircuitBreaker 의 스테일(>1s 무수신) 감지가 지연을
//   fail-closed(ForceFreeze)로 승격한다. 즉 소스는 fail-soft, 안전 판정은 브레이커가 단독 소유(권위 일원화).
//   ★소스가 자체적으로 att 를 동결하지 않는 이유: 일시적 5xx/타임아웃이 즉시 시스템 동결을 유발하면
//     과동결(가용성 파괴)이며, '지속' 지연만 브레이커가 스테일로 판정해 닫는 것이 올바른 계층 분리다.
// ============================================================================
using System;
using System.Net.Http;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    /// <summary>REST GET 폴 기반 환율 공급자. 응답 값 위치는 dot-path 키로 주입(공급자 스키마 비박제).</summary>
    public sealed class HttpFxRateSource : IFxRateSource, IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool       _ownsHttp;
        private readonly Uri        _endpoint;
        private readonly string[]   _path;      // dot-path 분해(키 또는 배열 인덱스)

        /// <param name="endpoint">환율 REST 엔드포인트(실 URL 주입 — openQuestions).</param>
        /// <param name="jsonRatePath">응답에서 환율 값 위치(dot-path, 예 "USDKRW.deal_bas_r" 또는 "0.basePrice").</param>
        /// <param name="http">공유 HttpClient(선택). 미주입 시 내부 생성(5s 타임아웃)·Dispose 소유.</param>
        public HttpFxRateSource(Uri endpoint, string jsonRatePath, HttpClient? http = null)
        {
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
            if (string.IsNullOrWhiteSpace(jsonRatePath))
                throw new ArgumentException("환율 JSON 경로(dot-path)가 비었습니다.", nameof(jsonRatePath));
            _path     = jsonRatePath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (_path.Length == 0)
                throw new ArgumentException("환율 JSON 경로가 유효하지 않습니다.", nameof(jsonRatePath));
            _ownsHttp = http is null;
            _http     = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        /// <summary>1회 폴. 성공 시 rate(decimal)>0 반환. 어떤 오류든 false(fail-soft → 브레이커 스테일이 지연 포착).</summary>
        public bool TryPoll(out decimal rate)
        {
            rate = 0m;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _endpoint);
                using var res = _http.Send(req);
                if (!res.IsSuccessStatusCode) return false;   // 비200 → 신규 데이터 없음(지연으로 표면화)
                string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (string.IsNullOrEmpty(body)) return false;
                using var doc = JsonDocument.Parse(body);
                return TryExtractRate(doc.RootElement, out rate);
            }
            catch { rate = 0m; return false; }   // 네트워크/파싱 예외 삼킴 → false(권위는 브레이커)
        }

        // dot-path 항해 — 객체 키 또는 배열 인덱스. 최종 노드를 decimal 로 해석(문자열/숫자 모두 허용).
        public bool TryExtractRate(JsonElement root, out decimal rate)
        {
            rate = 0m;
            JsonElement cur = root;
            foreach (string seg in _path)
            {
                switch (cur.ValueKind)
                {
                    case JsonValueKind.Object:
                        if (!cur.TryGetProperty(seg, out cur)) return false;
                        break;
                    case JsonValueKind.Array:
                        if (!int.TryParse(seg, out int idx) || idx < 0 || idx >= cur.GetArrayLength()) return false;
                        cur = cur[idx];
                        break;
                    default:
                        return false;   // 중간 노드가 스칼라인데 경로가 더 남음 → 불일치
                }
            }
            return TryReadDecimal(cur, out rate);
        }

        private static bool TryReadDecimal(JsonElement e, out decimal value)
        {
            value = 0m;
            switch (e.ValueKind)
            {
                case JsonValueKind.Number:
                    return e.TryGetDecimal(out value);
                case JsonValueKind.String:
                    // 일부 공급자는 환율을 문자열로 반환(콤마 제거 후 InvariantCulture 파싱).
                    string? s = e.GetString();
                    if (string.IsNullOrWhiteSpace(s)) return false;
                    return decimal.TryParse(s.Replace(",", ""), System.Globalization.NumberStyles.Any,
                                            System.Globalization.CultureInfo.InvariantCulture, out value);
                default:
                    return false;
            }
        }

        public void Dispose()
        {
            if (_ownsHttp) _http.Dispose();
        }
    }
}
