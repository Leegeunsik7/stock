// ============================================================================
// LiveBrokers - 업비트/KIS 실주문·조회·잔고 어댑터 (v3.2.5)
//  ⚠️⚠️⚠️ 실제 자금이 집행되는 코드입니다. 이 환경에서 빌드·실거래 검증을 하지
//        못했으므로, 반드시 (1) KIS 모의(VTS) (2) 업비트 소액 으로 먼저 검증하세요.
//  안전장치(BrokerHub): Mode=live + LiveArmed=true 일 때만 실주문, MaxOrderKrw 상한,
//  OrderConfirmDialog 확인. 미무장 시 LiveBrokerStub이 거부.
//  v3.2.5: 업비트 서명(query_hash)을 전송 파라미터와 동일하게 URL 인코딩으로 일치,
//          실잔고 조회로 HeldQty 실제화, 주문 취소/조회 추가.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MS2E.Infrastructure
{
    /// <summary>업비트 실주문/조회/취소/잔고 — JWT(HS256) + 쿼리 SHA512 해시.</summary>
    public class UpbitLiveBroker : IBroker
    {
        private static readonly HttpClient Http = new() { BaseAddress = new Uri("https://api.upbit.com") };
        private readonly string _access, _secret;
        public UpbitLiveBroker(LocalDatabase db) { (_access, _secret) = ApiKeyVault.GetUpbit(db); }

        public string Name => "업비트 실거래";
        public bool IsLive => true;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            if (string.IsNullOrWhiteSpace(_access) || string.IsNullOrWhiteSpace(_secret))
                return (false, "업비트 API 키가 설정되지 않았습니다.");
            try
            {
                // 전송 순서 = 해시 순서 (일치 필수)
                var p = new List<KeyValuePair<string, string>> { new("market", symbol) };
                if (side == PaperSide.Buy)
                {
                    p.Add(new("side", "bid"));
                    if (kind == PaperKind.Market) { p.Add(new("ord_type", "price")); p.Add(new("price", (qty * currentPrice).ToString("0"))); }
                    else { p.Add(new("ord_type", "limit")); p.Add(new("volume", qty.ToString("0.########"))); p.Add(new("price", limitPrice.ToString("0.########"))); }
                }
                else
                {
                    p.Add(new("side", "ask"));
                    if (kind == PaperKind.Market) { p.Add(new("ord_type", "market")); p.Add(new("volume", qty.ToString("0.########"))); }
                    else { p.Add(new("ord_type", "limit")); p.Add(new("volume", qty.ToString("0.########"))); p.Add(new("price", limitPrice.ToString("0.########"))); }
                }

                var query = Encode(p);
                using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/orders");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt(query));
                req.Content = new FormUrlEncodedContent(p);
                var (ok, body) = SendRead(req);
                return ok ? (true, $"업비트 주문 접수: {symbol} {(side == PaperSide.Buy ? "매수" : "매도")}")
                          : (false, $"업비트 주문 실패: {Trunc(FinancialPiiMasker.Mask(body))}");   // [Zero Protocol Leakage] 마스킹 후 절단
            }
            catch (Exception ex) { return (false, "업비트 주문 오류: " + ex.Message); }
        }

        public void Cancel(string orderId)
        {
            try
            {
                var p = new List<KeyValuePair<string, string>> { new("uuid", orderId) };
                var query = Encode(p);
                using var req = new HttpRequestMessage(HttpMethod.Delete, "/v1/order?" + query);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt(query));
                SendRead(req);
            }
            catch { }
        }

        /// <summary>주문 상태 조회(uuid). 실패 시 빈 문자열.</summary>
        public string GetOrderStatus(string uuid)
        {
            try
            {
                var p = new List<KeyValuePair<string, string>> { new("uuid", uuid) };
                var query = Encode(p);
                using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/order?" + query);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt(query));
                var (ok, body) = SendRead(req);
                if (!ok) return "";
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("state", out var st) ? (st.GetString() ?? "") : "";
            }
            catch { return ""; }
        }

        /// <summary>실잔고: KRW-XXX → 코인 XXX 보유수량.</summary>
        public decimal HeldQty(PaperAsset asset, string symbol)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_access)) return 0m;
                var currency = symbol.Contains('-') ? symbol.Split('-')[1] : symbol;
                using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/accounts");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Jwt(""));
                var (ok, body) = SendRead(req);
                if (!ok) return 0m;
                using var doc = JsonDocument.Parse(body);
                foreach (var a in doc.RootElement.EnumerateArray())
                    if (a.TryGetProperty("currency", out var c) && string.Equals(c.GetString(), currency, StringComparison.OrdinalIgnoreCase))
                        return a.TryGetProperty("balance", out var b) && decimal.TryParse(b.GetString(), out var v) ? v : 0m;
                return 0m;
            }
            catch { return 0m; }
        }

        // ===== 내부 =====
        private static (bool ok, string body) SendRead(HttpRequestMessage req)
        {
            var res = Http.Send(req);
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return (res.IsSuccessStatusCode, body);
        }

        private static string Encode(List<KeyValuePair<string, string>> p) =>
            string.Join("&", p.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        private string Jwt(string query)
        {
            var header = B64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var payload = new Dictionary<string, string>
            { ["access_key"] = _access, ["nonce"] = Guid.NewGuid().ToString() };
            if (!string.IsNullOrEmpty(query))
            {
                using var sha = SHA512.Create();
                payload["query_hash"] = Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
                payload["query_hash_alg"] = "SHA512";
            }
            var payloadB64 = B64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            var signingInput = header + "." + payloadB64;
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_secret));
            var sig = B64Url(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingInput)));
            return signingInput + "." + sig;
        }

        private static string B64Url(byte[] b) =>
            Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        private static string Trunc(string s) => s.Length > 180 ? s[..180] : s;
    }

    /// <summary>KIS 실주문/잔고 — 토큰 + 해시키 + 주문. 기본 모의(VTS) 도메인.</summary>
    /// <remarks>[KIS · 실결함 교정 4건]
    /// ① 토큰 캐시가 '정적 단일 슬롯'이었다 — 모의(VTS)/실 도메인·서로 다른 AppKey 가 같은 토큰을 공유(도메인 전환 시 타 도메인 토큰 송신).
    ///    → (도메인, AppKey) 키 캐시. 만료도 '로컬 시각 + 23시간 추정' → 서버 expires_in/access_token_token_expired(5분 선행 갱신).
    /// ② 성공 판정이 본문 문자열 Contains(""rt_cd":"0"") — 공백/필드 순서에 취약 → Utf8JsonReader 판독(KisProtocol.TryParseOrderAck).
    /// ③ 성공 시 반환 msg 가 한글 안내문이었다 — IBroker 계약(키움 ExecutionBridgeBroker 와 동일)은 'msg = 브로커 주문번호'.
    ///    라우터 게이트(OnBrokerAccepted)가 번호를 못 읽어 추적 슬롯이 TTL 까지 미결 → OrderSlotAge sticky Disarm. → KIS 네임스페이스 번호 반환.
    /// ④ 소수 수량을 (int) 절단해 송출(요청 ≠ 송출) → 거부. Cancel 미구현 → 원 조직번호 보관 후 order-rvsecncl 취소 구현.
    /// 비밀값(AppKey/AppSecret/토큰)은 FinancialPiiMasker·RawCredentialLeakInterlock 에 등록 — 오류 본문도 마스킹 후 반환.</remarks>
    /// <summary>[v12.18.31] KIS 접속키 자격증명 거부(HTTP 401/403) — 일시 실패(null)와 구분해 재발급 sticky 중단(재시도 폭풍 차단).</summary>
    public sealed class KisApprovalRejectedException : Exception
    {
        public int StatusCode { get; }
        public KisApprovalRejectedException(int statusCode) : base("KIS 접속키 자격증명 거부(HTTP " + statusCode + ")") => StatusCode = statusCode;

        /// <summary>영구 거부 상태인가 — 401(인증 실패) · 403(권한/키 거부). 그 밖(429 · 5xx · 네트워크) = 일시 실패.</summary>
        public static bool IsCredentialRejection(int httpStatus) => httpStatus == 401 || httpStatus == 403;
    }

    public class KisLiveBroker : IBroker
    {
        private static readonly HttpClient Http = new();
        private readonly string _appKey, _appSecret, _account;
        private readonly bool _real;
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Token, DateTime ExpiresUtc)> s_tokens = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, long> _orgNoByOdno = new();   // ODNO → KRX_FWDG_ORD_ORGNO(취소용)
        // ★TR ID(공개 문서 기존 표기). KIS 는 2024 년 일부 주문 TR ID 신규 체계를 공지했다 — 실 연동 전 공지 대조 필요.
        public const string TrBuyReal = "TTTC0802U", TrSellReal = "TTTC0801U", TrCancelReal = "TTTC0803U";
        public const string TrBuyVts = "VTTC0802U", TrSellVts = "VTTC0801U", TrCancelVts = "VTTC0803U";

        public KisLiveBroker(LocalDatabase db)
        {
            (_appKey, _appSecret, _account) = ApiKeyVault.GetKis(db);
            _real = db.GetSetting("Kis.UseRealDomain") == "1";
            RegisterSecrets(_appKey, _appSecret);
        }

        private static void RegisterSecrets(params string?[] secrets)
        {
            foreach (var x in secrets)
            {
                try { FinancialPiiMasker.RegisterSecret(x); RawCredentialLeakInterlock.Shared.RegisterCredential(x); }
                catch (Exception) { /* 등록 실패 — 마스커 패턴 규칙이 2차 방어 */ }
            }
        }

        private string Domain => _real ? "https://openapi.koreainvestment.com:9443"
                                        : "https://openapivts.koreainvestment.com:29443";
        private (string cano, string prdt) Acct()
        {
            var parts = _account.Split('-');
            return (parts.Length > 0 ? parts[0] : _account, parts.Length > 1 ? parts[1] : "01");
        }
        public string Name => _real ? "KIS 실거래" : "KIS 모의(VTS)";
        public bool IsLive => _real;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            if (string.IsNullOrWhiteSpace(_appKey) || string.IsNullOrWhiteSpace(_account))
                return (false, "KIS API 키/계좌가 설정되지 않았습니다.");
            if (qty <= 0 || qty != decimal.Truncate(qty) || qty > int.MaxValue)
                return (false, $"KIS 주문 수량은 1 이상의 정수여야 합니다(요청 {qty}) — 절단 송출 금지");
            try
            {
                var token = EnsureToken();
                if (token == null) return (false, "KIS 토큰 발급 실패");
                var (cano, prdt) = Acct();
                var ordDvsn = kind == PaperKind.Market ? "01" : "00";
                var unpr = kind == PaperKind.Market ? "0" : limitPrice.ToString("0");
                var bodyObj = new Dictionary<string, string>
                {
                    ["CANO"] = cano, ["ACNT_PRDT_CD"] = prdt, ["PDNO"] = symbol,
                    ["ORD_DVSN"] = ordDvsn, ["ORD_QTY"] = ((int)qty).ToString(), ["ORD_UNPR"] = unpr
                };
                var bodyJson = JsonSerializer.Serialize(bodyObj);
                var trId = side == PaperSide.Buy ? (_real ? TrBuyReal : TrBuyVts) : (_real ? TrSellReal : TrSellVts);
                using var req = NewReq(HttpMethod.Post, "/uapi/domestic-stock/v1/trading/order-cash", token, trId, GetHashKey(bodyJson, token));
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                long divBefore = TrHeaderDivergences;
                var (ok, body) = SendRead(req);
                if (TrHeaderDivergences != divBefore)
                    return (false, $"{Name} 응답 TR 이탈({trId}) — 주문 상태 미확정(브로커 미체결 대사 대상) · 성공 판정 거부");
                if (ok && KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(body), out var ack) && ack.Ok)
                {
                    if (ack.OrgNo > 0) _orgNoByOdno[ack.OrderNo] = ack.OrgNo;
                    return (true, KisOrderIdSpace.Encode(ack.OrderNo).ToString(System.Globalization.CultureInfo.InvariantCulture));   // ★msg = 브로커 주문번호
                }
                // [KIS VTS · 실결함 교정] 수정 전엔 절단 뒤 마스킹 — 절단 경계에 걸친 등록 비밀값(토큰 · 키)은 접두만 남아 마스커가 못 알아봄 → 부분 유출.
                //   전체 본문을 먼저 마스킹한 뒤 절단한다(Zero Protocol Leakage).
                return (false, $"{Name} 주문 실패: {Trunc(FinancialPiiMasker.Mask(body))}");
            }
            catch (Exception ex) { return (false, "KIS 주문 오류: " + FinancialPiiMasker.Mask(ex.Message)); }
        }

        /// <summary>전량 취소(order-rvsecncl · 정정취소구분 02 · 잔량전부). orderId = Place 가 반환한 KIS 네임스페이스 번호.
        /// 원 조직번호(KRX_FWDG_ORD_ORGNO)를 모르는 주문(다른 세션 발주)은 송신하지 않는다(잘못된 원주문 취소 방지).</summary>
        public void Cancel(string orderId)
        {
            if (!long.TryParse(orderId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long id)) return;
            long odno = KisOrderIdSpace.Decode(id);
            if (odno <= 0 || !_orgNoByOdno.TryGetValue(odno, out long orgNo)) return;
            try
            {
                var token = EnsureToken();
                if (token == null) return;
                var (cano, prdt) = Acct();
                var bodyJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["CANO"] = cano, ["ACNT_PRDT_CD"] = prdt,
                    ["KRX_FWDG_ORD_ORGNO"] = orgNo.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                    ["ORGN_ODNO"] = odno.ToString("D10", System.Globalization.CultureInfo.InvariantCulture),
                    ["ORD_DVSN"] = "00", ["RVSE_CNCL_DVSN_CD"] = "02", ["ORD_QTY"] = "0", ["ORD_UNPR"] = "0", ["QTY_ALL_ORD_YN"] = "Y",
                });
                using var req = NewReq(HttpMethod.Post, "/uapi/domestic-stock/v1/trading/order-rvsecncl", token, _real ? TrCancelReal : TrCancelVts, GetHashKey(bodyJson, token));
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                var (ok, body) = SendRead(req);
                if (ok && KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(body), out var ack) && ack.Ok) _orgNoByOdno.TryRemove(odno, out _);
            }
            catch (Exception) { /* 취소 실패 — 체결 통보/장기 TTL 대사로 노출 확인(재시도 루프 없음) */ }
        }

        /// <summary>실잔고: 종목코드 보유수량(hldg_qty).</summary>
        public decimal HeldQty(PaperAsset asset, string symbol)
        {
            try
            {
                var token = EnsureToken();
                if (token == null) return 0m;
                var (cano, prdt) = Acct();
                var q = $"CANO={cano}&ACNT_PRDT_CD={prdt}&AFHR_FLPR_YN=N&OFL_YN=&INQR_DVSN=02&UNPR_DVSN=01"
                      + "&FUND_STTL_ICLD_YN=N&FNCG_AMT_AUTO_RDPT_YN=N&PRCS_DVSN=00&CTX_AREA_FK100=&CTX_AREA_NK100=";
                var trId = _real ? "TTTC8434R" : "VTTC8434R";
                using var req = NewReq(HttpMethod.Get, "/uapi/domestic-stock/v1/trading/inquire-balance?" + q, token, trId, null);
                var (ok, body) = SendRead(req);
                if (!ok) return 0m;
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("output1", out var arr)) return 0m;
                foreach (var it in arr.EnumerateArray())
                    if (it.TryGetProperty("pdno", out var pd) && pd.GetString() == symbol)
                        return it.TryGetProperty("hldg_qty", out var h) && decimal.TryParse(h.GetString(), out var v) ? v : 0m;
                return 0m;
            }
            catch { return 0m; }
        }

        // ── [KIS 실배선] 분할 한도 피드용 REST 조회(콜드 · 응답 원문 바이트 → KisProtocol δ=0 판독은 호출측) ──

        public bool IsRealDomain => _real;
        /// <summary>[KIS VTS] 종합계좌번호 앞 8자리(CANO) — 체결통보 계좌 핀(HTS ID 구독은 그 ID 의 모든 계좌 통보를 보낸다).</summary>
        public string AccountCano => Acct().cano;
        public bool Configured => !string.IsNullOrWhiteSpace(_appKey) && !string.IsNullOrWhiteSpace(_appSecret) && !string.IsNullOrWhiteSpace(_account);

        /// <summary>매수가능조회(TTTC8908R · inquire-psbl-order) 원문. 실패 = null. ★필드/TR 은 공개 문서 기준 — 실계좌 미검증.</summary>
        public byte[]? QueryBuyableJson()
        {
            if (!Configured) return null;
            try
            {
                var token = EnsureToken();
                if (token == null) return null;
                var (cano, prdt) = Acct();
                var q = $"CANO={cano}&ACNT_PRDT_CD={prdt}&PDNO=&ORD_UNPR=&ORD_DVSN=01&CMA_EVLU_AMT_ICLD_YN=N&OVRS_ICLD_YN=N";
                using var req = NewReq(HttpMethod.Get, "/uapi/domestic-stock/v1/trading/inquire-psbl-order?" + q, token, _real ? "TTTC8908R" : "VTTC8908R", null);
                var (ok, body) = SendRead(req);
                return ok ? Encoding.UTF8.GetBytes(body) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>주식잔고조회(TTTC8434R · inquire-balance) 원문(1페이지). 실패 = null.</summary>
        public byte[]? QueryBalanceJson()
        {
            if (!Configured) return null;
            try
            {
                var token = EnsureToken();
                if (token == null) return null;
                var (cano, prdt) = Acct();
                var q = $"CANO={cano}&ACNT_PRDT_CD={prdt}&AFHR_FLPR_YN=N&OFL_YN=&INQR_DVSN=02&UNPR_DVSN=01"
                      + "&FUND_STTL_ICLD_YN=N&FNCG_AMT_AUTO_RDPT_YN=N&PRCS_DVSN=00&CTX_AREA_FK100=&CTX_AREA_NK100=";
                using var req = NewReq(HttpMethod.Get, "/uapi/domestic-stock/v1/trading/inquire-balance?" + q, token, _real ? "TTTC8434R" : "VTTC8434R", null);
                var (ok, body) = SendRead(req);
                return ok ? Encoding.UTF8.GetBytes(body) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// [미체결 대사] 주식일별주문체결조회(TTTC8001R · 모의 VTTC8001R · inquire-daily-ccld · 당일 · CCLD_DVSN=02 미체결) 1페이지 원문.
        /// continuation = 다음 페이지(헤더 tr_cont "N" + 직전 응답의 ctx_area_fk100/nk100). more = 응답 헤더 tr_cont F/M(다음 페이지 있음).
        /// 실패(미설정 · 토큰 · HTTP · TR 이탈) = null. ★필드/TR 은 공개 문서 기준 — 실계좌 미검증. HTTP 할당은 전송 몫(콜드).
        /// </summary>
        public byte[]? QueryOpenOrdersJson(string ctxFk, string ctxNk, bool continuation, out bool more)
        {
            more = false;
            if (!Configured) return null;
            try
            {
                var token = EnsureToken();
                if (token == null) return null;
                var (cano, prdt) = Acct();
                string day = DateTime.UtcNow.AddHours(9).ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);   // KST 당일
                var q = $"CANO={cano}&ACNT_PRDT_CD={prdt}&INQR_STRT_DT={day}&INQR_END_DT={day}&SLL_BUY_DVSN_CD=00&INQR_DVSN=00&PDNO="
                      + "&CCLD_DVSN=02&ORD_GNO_BRNO=&ODNO=&INQR_DVSN_3=00&INQR_DVSN_1=&CTX_AREA_FK100=" + Uri.EscapeDataString(ctxFk ?? "")
                      + "&CTX_AREA_NK100=" + Uri.EscapeDataString(ctxNk ?? "");
                using var req = NewReq(HttpMethod.Get, "/uapi/domestic-stock/v1/trading/inquire-daily-ccld?" + q, token, _real ? "TTTC8001R" : "VTTC8001R", null);
                if (continuation) req.Headers.TryAddWithoutValidation("tr_cont", "N");
                var (ok, body, cont) = SendReadCont(req);
                if (!ok) return null;
                more = cont is "F" or "M";
                return Encoding.UTF8.GetBytes(body);
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// [미체결 대사 · 유령 정리] 브로커 원장 번호 직접 전량 취소(TTTC0803U · RVSE_CNCL_DVSN_CD=02 · QTY_ALL_ORD_YN=Y).
        /// orgNo = 미체결 조회 행의 ord_gno_brno(KRX_FWDG_ORD_ORGNO). 반환 = 브로커 접수 성공. 재시도 없음(다음 대사 패스가 상태 구동).
        /// </summary>
        public bool CancelOpenOrder(long odno, long orgNo)
        {
            if (!Configured || odno <= 0 || orgNo <= 0) return false;
            try
            {
                var token = EnsureToken();
                if (token == null) return false;
                var (cano, prdt) = Acct();
                var bodyJson = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    ["CANO"] = cano, ["ACNT_PRDT_CD"] = prdt,
                    ["KRX_FWDG_ORD_ORGNO"] = orgNo.ToString("D5", System.Globalization.CultureInfo.InvariantCulture),
                    ["ORGN_ODNO"] = odno.ToString("D10", System.Globalization.CultureInfo.InvariantCulture),
                    ["ORD_DVSN"] = "00", ["RVSE_CNCL_DVSN_CD"] = "02", ["ORD_QTY"] = "0", ["ORD_UNPR"] = "0", ["QTY_ALL_ORD_YN"] = "Y",
                });
                using var req = NewReq(HttpMethod.Post, "/uapi/domestic-stock/v1/trading/order-rvsecncl", token, _real ? TrCancelReal : TrCancelVts, GetHashKey(bodyJson, token));
                req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                var (ok, body) = SendRead(req);
                return ok && KisProtocol.TryParseOrderAck(Encoding.UTF8.GetBytes(body), out var ack) && ack.Ok;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 실시간(WebSocket) 접속키 발급(POST /oauth2/Approval · appkey/secretkey). 일시 실패 = null. 접속키는 마스커/유출 인터락에 등록.
        /// [v12.18.31] 자격증명 거부(HTTP 401/403) = KisApprovalRejectedException — 세션이 sticky 래치로 재발급을 멈춘다(재시도 폭풍 차단).
        /// </summary>
        public string? RequestApprovalKey()
        {
            if (!Configured) return null;
            try
            {
                var json = JsonSerializer.Serialize(new Dictionary<string, string>
                { ["grant_type"] = "client_credentials", ["appkey"] = _appKey, ["secretkey"] = _appSecret });
                using var req = new HttpRequestMessage(HttpMethod.Post, Domain + "/oauth2/Approval") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                using var res = Http.Send(req);
                int status = (int)res.StatusCode;
                if (KisApprovalRejectedException.IsCredentialRejection(status)) throw new KisApprovalRejectedException(status);
                if (!res.IsSuccessStatusCode) return null;
                var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(body);
                var key = doc.RootElement.TryGetProperty("approval_key", out var k) ? k.GetString() : null;
                if (string.IsNullOrWhiteSpace(key)) return null;
                RegisterSecrets(key);
                return key;
            }
            catch (KisApprovalRejectedException) { throw; }
            catch (Exception) { return null; }
        }

        private HttpRequestMessage NewReq(HttpMethod m, string path, string token, string trId, string? hash)
        {
            var req = new HttpRequestMessage(m, Domain + path);
            req.Headers.TryAddWithoutValidation("authorization", "Bearer " + token);
            req.Headers.TryAddWithoutValidation("appkey", _appKey);
            req.Headers.TryAddWithoutValidation("appsecret", _appSecret);
            req.Headers.TryAddWithoutValidation("tr_id", trId);
            if (hash != null) req.Headers.TryAddWithoutValidation("hashkey", hash);
            return req;
        }

        private string TokenKey => Domain + "\n" + _appKey;

        private string? EnsureToken()
        {
            string key = TokenKey;
            if (s_tokens.TryGetValue(key, out var cached) && DateTime.UtcNow < cached.ExpiresUtc) return cached.Token;
            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            { ["grant_type"] = "client_credentials", ["appkey"] = _appKey, ["appsecret"] = _appSecret });
            using var req = new HttpRequestMessage(HttpMethod.Post, Domain + "/oauth2/tokenP")
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            var (ok, body) = SendRead(req);
            if (!ok) return null;
            byte[] tok = new byte[4096];
            if (!KisProtocol.TryParseToken(Encoding.UTF8.GetBytes(body), tok, out int len, out long expiresIn, out long expiredAtKst)) return null;
            DateTime nowUtc = DateTime.UtcNow;
            DateTime exp = nowUtc.AddSeconds(expiresIn);
            if (expiredAtKst > 0)
            {
                var kstAsUtc = new DateTime(expiredAtKst, DateTimeKind.Utc).AddHours(-9);
                if (kstAsUtc < exp) exp = kstAsUtc;
            }
            exp = exp.AddMinutes(-5);                                               // 만료 5분 전 선행 갱신
            if (exp <= nowUtc) return null;
            string token = Encoding.ASCII.GetString(tok, 0, len);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(tok);
            RegisterSecrets(token);
            s_tokens[key] = (token, exp);
            return token;
        }

        private string? GetHashKey(string bodyJson, string token)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, Domain + "/uapi/hashkey")
                { Content = new StringContent(bodyJson, Encoding.UTF8, "application/json") };
                req.Headers.TryAddWithoutValidation("appkey", _appKey);
                req.Headers.TryAddWithoutValidation("appsecret", _appSecret);
                var (ok, body) = SendRead(req);
                if (!ok) return null;
                using var doc = JsonDocument.Parse(body);
                return doc.RootElement.TryGetProperty("HASH", out var h) ? h.GetString() : null;
            }
            catch { return null; }
        }

        private long _trHeaderDivergences;
        /// <summary>[KIS VTS] 응답 tr_id 헤더 ≠ 요청 tr_id 누적(도메인/라우팅 이탈).</summary>
        public long TrHeaderDivergences => System.Threading.Interlocked.Read(ref _trHeaderDivergences);
        /// <summary>[KIS VTS] 응답 TR 이탈 통지(인자 = 요청 TR ID · 비밀값 아님) — 호스트가 KIS 프로토콜 이탈 인터락(비트 26)으로 결선.</summary>
        public event Action<string>? ResponseDivergence;

        private (bool ok, string body) SendRead(HttpRequestMessage req)
        {
            var (ok, body, _) = SendReadCont(req);
            return (ok, body);
        }

        /// <summary>SendRead + 응답 헤더 tr_cont(연속 조회 F/M · 마지막 D/E · 없음 = "").</summary>
        private (bool ok, string body, string cont) SendReadCont(HttpRequestMessage req)
        {
            var res = Http.Send(req);
            var body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            // [KIS VTS] 요청에 tr_id 를 실었는데 응답 tr_id 헤더가 다르다 = 다른 TR/도메인의 응답 → 성공 판정 금지 + 통지.
            //   본문은 돌려주지 않는다(이탈 응답 원문을 오류 메시지/로그로 흘리지 않음).
            if (req.Headers.TryGetValues("tr_id", out var sent) && res.Headers.TryGetValues("tr_id", out var got))
            {
                string s = System.Linq.Enumerable.FirstOrDefault(sent) ?? "", g = System.Linq.Enumerable.FirstOrDefault(got) ?? "";
                if (!KisProtocol.ResponseTrIdConsistent(s, g))
                {
                    System.Threading.Interlocked.Increment(ref _trHeaderDivergences);
                    try { ResponseDivergence?.Invoke(s); } catch (Exception) { }
                    return (false, "", "");
                }
            }
            string cont = res.Headers.TryGetValues("tr_cont", out var c) ? (System.Linq.Enumerable.FirstOrDefault(c) ?? "") : "";
            return (res.IsSuccessStatusCode, body, cont);
        }
        private static string Trunc(string s) => s.Length > 180 ? s[..180] : s;
    }

    /// <summary>자산별 라우팅 + 금액 상한(armed 상태에서만 생성).</summary>
    public class LiveRouterBroker : IBroker
    {
        private readonly UpbitLiveBroker _upbit;
        private readonly KisLiveBroker _kis;
        private readonly decimal _maxKrw;

        public LiveRouterBroker(LocalDatabase db)
        {
            _upbit = new UpbitLiveBroker(db);
            _kis = new KisLiveBroker(db);
            _maxKrw = BrokerHub.MaxOrderKrw(db);
        }

        public string Name => "실거래(업비트/KIS)";
        public bool IsLive => true;
        public decimal HeldQty(PaperAsset asset, string symbol)
            => asset == PaperAsset.Coin ? _upbit.HeldQty(asset, symbol) : _kis.HeldQty(asset, symbol);

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name,
            PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            var notional = qty * (kind == PaperKind.Market ? currentPrice : limitPrice);
            if (_maxKrw > 0 && notional > _maxKrw)
                return (false, $"주문 금액 {notional:N0}원이 한도({_maxKrw:N0}원)를 초과했습니다. 거래 설정에서 한도를 조정하세요.");
            return asset == PaperAsset.Coin
                ? _upbit.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice)
                : _kis.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
        }

        public void Cancel(string orderId) => _upbit.Cancel(orderId);
    }
}
