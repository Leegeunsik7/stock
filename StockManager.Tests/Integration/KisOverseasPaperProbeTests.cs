// ============================================================================
// KisOverseasPaperProbeTests — [v12.18.53-DEV → v12.18.57-DEV] KIS 해외주식 모의투자(VTS) 통신 점검(토큰 · VTTS3012R 3대 거래소 잔고 · HHDFS00000300 현재가) 하네스 검증
//
//   §1 송출 허용 목록(오프라인): 실도메인 · 위장 호스트 · http · 해외 주문 TR · 실 해외 조회 TR(TTTS3012R) · 국내 잔고 경로/TR ·
//      허용 밖 경로/메서드 · tr_id 중복 · 본문 동봉 · 경로↔TR 교차 · 3대 외 거래소/통화/심볼 이탈 = 예외 · 가드 위반 요청 핸들러 도달 0(패킷 0) ·
//      요청 생성기(3대 거래소 잔고 · 시세) = 허용 목록 통과
//   §2 자격증명(오프라인 · 임시 DB): PaperOverseas 키 3종만 판독(PaperDomestic · Real 미판독) · 허용 프로필 밖(Real · PaperFutures) = DB 미접속 거부 ·
//      국내 프로필 자격증명으로 해외 점검 실행 = 거부 · 옵트인 Skip 사유(국내 사전 점검 · 주문 프로브 옵트인과 분리)
//   §3 마스킹 · 판독(오프라인): 가짜 VTS 왕복(토큰 1 + 잔고 3 + 시세 3) 출력 줄에 키/시크릿/토큰/계좌 원문 0 · 토큰 실패 = 잔고/시세 미송출 ·
//      USD 금액 raw 10^8 · 거래소 불일치 행 집계 · 세션 페이즈 → 시세 유효성 · 형식 이탈 fail-closed
//   §4 리얼 와이어(옵트인 MS2E_KIS_VTS_OVERSEAS=1): 실제 VTS 토큰 발급 → VTTS3012R 3대 거래소 잔고 → HHDFS00000300 현재가 3종 → 마스킹 요약 출력
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MS2E.Infrastructure;
using StockManager.Engines.Brokers.Kis;
using StockManager.Engines.Feeds;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    public sealed class KisOverseasPaperProbeTests
    {
        private readonly ITestOutputHelper _out;
        public KisOverseasPaperProbeTests(ITestOutputHelper o) => _out = o;

        private const string FakeKey = "PSovsFakeAppKey0123456789abcdefghijk";
        private const string FakeSecret = "ovsFakeSecretValue/0123456789+abcdefghijklmnopqrstuvwx==";
        private const string FakeCano = "50098765";
        private const string FakeToken = "eyJovsFakeAccessToken.0123456789abcdefghijklmnopqrstuv";
        private static readonly VtsPreFlightCredentials Cred = new(FakeKey, FakeSecret, FakeCano, "01", VtsPreFlightCredentials.OverseasProfile);

        private const string Vts = "https://openapivts.koreainvestment.com:29443";
        private const string BalQ = KisOverseasPaperGuard.BalancePath + "?CANO=1";

        private sealed class FakeVts : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Seen = new();
            public string TokenJson = "{\"access_token\":\"" + FakeToken + "\",\"token_type\":\"Bearer\",\"expires_in\":86400,\"access_token_token_expired\":\"2026-10-22 22:00:00\"}";
            public string BalanceJson = "{\"ctx_area_fk200\":\"" + FakeCano + "^01^NASD^USD\",\"ctx_area_nk200\":\"\",\"output1\":[{\"ovrs_pdno\":\"AAPL\"}],"
                                      + "\"output2\":{\"frcr_pchs_amt1\":\"1234.56000\",\"ovrs_rlzt_pfls_amt\":\"0.00000\",\"ovrs_tot_pfls\":\"-12.34500\","
                                      + "\"tot_evlu_pfls_amt\":\"-7.89000000\",\"tot_pftrt\":\"-0.64\"},"
                                      + "\"rt_cd\":\"0\",\"msg_cd\":\"20310000\",\"msg1\":\"모의투자 조회가 완료되었습니다.                       \"}";
            public HttpStatusCode TokenStatus = HttpStatusCode.OK;
            public bool EchoExchange;                                                       // 잔고 행 ovrs_excg_cd 를 요청 거래소로 응답
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Seen.Add(request);
                string path = request.RequestUri!.AbsolutePath, query = request.RequestUri.Query;
                string Arg(string k) { foreach (var p in query.TrimStart('?').Split('&')) if (p.StartsWith(k + "=", StringComparison.Ordinal)) return p[(k.Length + 1)..]; return ""; }
                bool tok = path == VtsPreFlightGuard.TokenPath;
                string json = tok ? TokenJson
                    : path == KisOverseasPaperGuard.QuotePath ? QuoteJson(Arg("EXCD"), Arg("SYMB"))
                    : EchoExchange ? BalanceJson.Replace("\"ovrs_pdno\":\"AAPL\"", "\"ovrs_pdno\":\"AAPL\",\"ovrs_excg_cd\":\"" + Arg("OVRS_EXCG_CD") + "\"") : BalanceJson;
                return Task.FromResult(new HttpResponseMessage(tok ? TokenStatus : HttpStatusCode.OK)
                { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            }

            public static string QuoteJson(string excd, string symb)
                => "{\"output\":{\"rsym\":\"D" + excd + symb + "\",\"zdiv\":\"4\",\"base\":\"227.5200\",\"pvol\":\"41000000\",\"last\":\"229.0000\",\"sign\":\"2\","
                 + "\"diff\":\"1.4800\",\"rate\":\"+0.65\",\"tvol\":\"38123456\",\"tamt\":\"8730000000\",\"ordy\":\"매수불가\"},"
                 + "\"rt_cd\":\"0\",\"msg_cd\":\"MCA00000\",\"msg1\":\"정상처리 되었습니다.\"}";
        }

        private static HttpRequestMessage Get(string url, params string[] trs)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            foreach (var t in trs) r.Headers.TryAddWithoutValidation("tr_id", t);
            return r;
        }

        // ════════════ §1 송출 허용 목록 ════════════

        [Fact]
        public void Allowlist_Accepts_Only_Vts_Token_Post_And_Vtts3012R_Overseas_Balance_Get()
        {
            using (var t = VtsPreFlightRequests.Token(Cred)) KisOverseasPaperGuard.EnsureAllowed(t);
            using (var b = KisOverseasPaperRequests.Balance(Cred, FakeToken)) KisOverseasPaperGuard.EnsureAllowed(b);
            foreach (var ex in UsExchangeCode.All)
            {
                using (var b = KisOverseasPaperRequests.Balance(Cred, FakeToken, ex)) KisOverseasPaperGuard.EnsureAllowed(b);
                using (var q = KisOverseasPaperRequests.Quote(Cred, FakeToken, ex, "AAPL")) KisOverseasPaperGuard.EnsureAllowed(q);
            }

            void Rejects(HttpRequestMessage r) { using (r) Assert.Throws<InvalidOperationException>(() => KisOverseasPaperGuard.EnsureAllowed(r)); }
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:9443" + VtsPreFlightGuard.TokenPath));             // 실도메인
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:29443" + VtsPreFlightGuard.TokenPath));            // 실 호스트 + VTS 포트
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapivts.koreainvestment.com:9443" + VtsPreFlightGuard.TokenPath));          // 실 포트
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapivts.koreainvestment.com.evil.io:29443" + VtsPreFlightGuard.TokenPath));  // 위장 호스트
            Rejects(new HttpRequestMessage(HttpMethod.Post, "http://openapivts.koreainvestment.com:29443" + VtsPreFlightGuard.TokenPath));           // 평문
            Rejects(Get("https://openapi.koreainvestment.com:9443" + BalQ, "TTTS3012R"));                                                            // 실 해외 잔고
            Rejects(Get(Vts + BalQ, "TTTS3012R"));                                                                                                   // VTS 호스트 + 실 TR
            Rejects(Get(Vts + BalQ, "VTTT1002U"));                                                                                                   // 해외 매수 주문 TR
            Rejects(Get(Vts + BalQ, "VTTT1001U"));                                                                                                   // 해외 매도 주문 TR
            Rejects(Get(Vts + BalQ, "VTTC8434R"));                                                                                                   // 국내 잔고 TR
            Rejects(Get(Vts + BalQ));                                                                                                                // tr_id 없음
            Rejects(Get(Vts + BalQ, "VTTS3012R", "VTTS3012R"));                                                                                      // tr_id 중복
            Rejects(Get(Vts + BalQ, "VTTS3012R", "VTTT1002U"));
            Rejects(Get(Vts + VtsPreFlightGuard.BalancePath + "?CANO=1", "VTTS3012R"));                                                              // 국내 잔고 경로
            Rejects(Get(Vts + "/uapi/overseas-stock/v1/trading/order", "VTTS3012R"));                                                                // 해외 주문 경로
            Rejects(Get(Vts + "/uapi/overseas-stock/v1/trading/inquire-present-balance", "VTTS3012R"));                                              // 허용 밖 조회
            Rejects(Get(Vts + VtsPreFlightGuard.TokenPath));                                                                                         // 토큰 = POST 만
            var postBal = new HttpRequestMessage(HttpMethod.Post, Vts + BalQ); postBal.Headers.TryAddWithoutValidation("tr_id", "VTTS3012R");
            Rejects(postBal);                                                                                                                        // 잔고 = GET 만
            var getBody = Get(Vts + BalQ, "VTTS3012R"); getBody.Content = new StringContent("{\"ORD_QTY\":\"1\"}");
            Rejects(getBody);                                                                                                                        // 조회 + 본문
            var tokTr = new HttpRequestMessage(HttpMethod.Post, Vts + VtsPreFlightGuard.TokenPath); tokTr.Headers.TryAddWithoutValidation("tr_id", "VTTT1002U");
            Rejects(tokTr);                                                                                                                          // 토큰 + tr_id
            Rejects(new HttpRequestMessage(HttpMethod.Post, Vts + "/uapi/overseas-stock/v1/trading/order"));                                         // 해외 주문 POST
            // [v12.18.57] 3대 거래소 · 시세 경로
            const string Bal = KisOverseasPaperGuard.BalancePath + "?CANO=1&ACNT_PRDT_CD=01&TR_CRCY_CD=USD&OVRS_EXCG_CD=";
            const string Px = KisOverseasPaperGuard.QuotePath + "?AUTH=&EXCD=";
            KisOverseasPaperGuard.EnsureAllowed(Get(Vts + Bal + "AMEX", "VTTS3012R"));
            KisOverseasPaperGuard.EnsureAllowed(Get(Vts + Px + "NYS&SYMB=BRK.B", "HHDFS00000300"));
            Rejects(Get(Vts + Bal + "SEHK", "VTTS3012R"));                                                                                           // 3대 외 거래소(홍콩)
            Rejects(Get(Vts + Bal + "NAS", "VTTS3012R"));                                                                                            // 시세 코드를 잔고에(계열 혼용)
            Rejects(Get(Vts + Bal + "nasd", "VTTS3012R"));                                                                                           // 대소문자
            Rejects(Get(Vts + Bal + "NASD&OVRS_EXCG_CD=NYSE", "VTTS3012R"));                                                                         // 거래소 중복
            Rejects(Get(Vts + KisOverseasPaperGuard.BalancePath + "?CANO=1&OVRS_EXCG_CD=NASD&TR_CRCY_CD=HKD", "VTTS3012R"));                          // 통화 이탈
            Rejects(Get(Vts + KisOverseasPaperGuard.BalancePath + "?CANO=1&OVRS_EXCG_CD=NASD", "VTTS3012R"));                                         // 통화 없음
            Rejects(Get(Vts + Px + "NASD&SYMB=AAPL", "HHDFS00000300"));                                                                              // 잔고 코드를 시세에(계열 혼용)
            Rejects(Get(Vts + Px + "NAS&SYMB=aapl", "HHDFS00000300"));                                                                               // 심볼 소문자
            Rejects(Get(Vts + Px + "NAS&SYMB=AAPL%26X", "HHDFS00000300"));                                                                           // 심볼 주입
            Rejects(Get(Vts + Px + "NAS", "HHDFS00000300"));                                                                                         // 심볼 없음
            Rejects(Get(Vts + Px + "NAS&SYMB=AAPL", "VTTS3012R"));                                                                                   // 시세 경로 + 잔고 TR(교차)
            Rejects(Get(Vts + Bal + "NASD", "HHDFS00000300"));                                                                                       // 잔고 경로 + 시세 TR(교차)
            Rejects(Get(Vts + Px + "NAS&SYMB=AAPL", "HHDFS00000300", "HHDFS00000300"));                                                             // 시세 tr_id 중복
            Rejects(Get("https://openapi.koreainvestment.com:9443" + Px + "NAS&SYMB=AAPL", "HHDFS00000300"));                                       // 실도메인 시세
            var postPx = new HttpRequestMessage(HttpMethod.Post, Vts + Px + "NAS&SYMB=AAPL"); postPx.Headers.TryAddWithoutValidation("tr_id", "HHDFS00000300");
            Rejects(postPx);                                                                                                                         // 시세 = GET 만
            Assert.Throws<ArgumentOutOfRangeException>(() => KisOverseasPaperRequests.Balance(Cred, FakeToken, UsExchange.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => KisOverseasPaperRequests.Quote(Cred, FakeToken, (UsExchange)9, "AAPL"));
            Assert.Throws<ArgumentException>(() => KisOverseasPaperRequests.Quote(Cred, FakeToken, UsExchange.Nasdaq, "AAPL&EXCD=HKS"));
        }

        [Fact]
        public async Task Blocked_Requests_Never_Reach_The_Wire()
        {
            var h = new FakeVts();
            using var http = new HttpMessageInvoker(h);
            foreach (var r in new[]
            {
                new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:9443" + VtsPreFlightGuard.TokenPath),
                Get("https://openapi.koreainvestment.com:9443" + BalQ, "TTTS3012R"),
                Get(Vts + BalQ, "VTTT1002U"),
                new HttpRequestMessage(HttpMethod.Post, Vts + "/uapi/overseas-stock/v1/trading/order"),
            })
                using (r) await Assert.ThrowsAsync<InvalidOperationException>(() => KisOverseasPaperGuard.SendAsync(http, r, CancellationToken.None));
            Assert.Empty(h.Seen);                                                                                                                    // 패킷 0
        }

        [Fact]
        public void Balance_Request_Pins_Vts_Endpoint_Read_Tr_Nasdaq_Usd_No_Body()
        {
            using var b = KisOverseasPaperRequests.Balance(Cred, FakeToken);
            Assert.Equal(HttpMethod.Get, b.Method);
            Assert.Equal("openapivts.koreainvestment.com", b.RequestUri!.Host);
            Assert.Equal(29443, b.RequestUri.Port);
            Assert.Equal(KisOverseasPaperGuard.BalancePath, b.RequestUri.AbsolutePath);
            Assert.Equal(new[] { "VTTS3012R" }, b.Headers.GetValues("tr_id"));
            Assert.Contains("CANO=" + FakeCano + "&ACNT_PRDT_CD=01&OVRS_EXCG_CD=NASD&TR_CRCY_CD=USD&", b.RequestUri.Query);
            Assert.Null(b.Content);                                                                                                                  // 조회 = 본문 0
        }

        [Theory]
        [InlineData(UsExchange.Nasdaq, "NASD", "NAS")]
        [InlineData(UsExchange.Nyse, "NYSE", "NYS")]
        [InlineData(UsExchange.Amex, "AMEX", "AMS")]
        public void Per_Exchange_Requests_Use_Trade_Code_For_Balance_And_Quote_Code_For_Price(UsExchange ex, string trade, string quote)
        {
            using (var b = KisOverseasPaperRequests.Balance(Cred, FakeToken, ex))
            {
                Assert.Contains("&OVRS_EXCG_CD=" + trade + "&TR_CRCY_CD=USD&", b.RequestUri!.Query);
                Assert.Equal(new[] { "VTTS3012R" }, b.Headers.GetValues("tr_id"));
            }
            using var q = KisOverseasPaperRequests.Quote(Cred, FakeToken, ex, "SPY");
            Assert.Equal(HttpMethod.Get, q.Method);
            Assert.Equal("openapivts.koreainvestment.com", q.RequestUri!.Host);
            Assert.Equal(29443, q.RequestUri.Port);
            Assert.Equal(KisOverseasPaperGuard.QuotePath, q.RequestUri.AbsolutePath);
            Assert.Equal("?AUTH=&EXCD=" + quote + "&SYMB=SPY", q.RequestUri.Query);
            Assert.Equal(new[] { "HHDFS00000300" }, q.Headers.GetValues("tr_id"));
            Assert.Null(q.Content);
        }

        // ════════════ §2 자격증명(임시 DB) ════════════

        private static string TempDb(Dictionary<string, string> rows)
        {
            string p = Path.Combine(Path.GetTempPath(), "ms2e-overseas-" + Guid.NewGuid().ToString("N") + ".db");
            using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = p, Pooling = false }.ToString());
            c.Open();
            using (var cmd = c.CreateCommand()) { cmd.CommandText = "CREATE TABLE Settings([Key] TEXT PRIMARY KEY, Value TEXT)"; cmd.ExecuteNonQuery(); }
            foreach (var (k, v) in rows)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = "INSERT INTO Settings VALUES(@k,@v)";
                cmd.Parameters.AddWithValue("@k", k); cmd.Parameters.AddWithValue("@v", v);
                cmd.ExecuteNonQuery();
            }
            return p;
        }

        private static Dictionary<string, string> Sealed() => new()
        {
            ["KIS.Profile.PaperOverseas.AppKey"] = Secret.Protect(FakeKey),
            ["KIS.Profile.PaperOverseas.AppSecret"] = Secret.Protect(FakeSecret),
            ["KIS.Profile.PaperOverseas.Account"] = Secret.Protect(FakeCano),
            ["KIS.Profile.PaperDomestic.AppKey"] = Secret.Protect("DOMESTIC-KEY-MUST-NOT-BE-READ"),
            ["KIS.Profile.PaperDomestic.AppSecret"] = Secret.Protect("DOMESTIC-SECRET-MUST-NOT-BE-READ"),
            ["KIS.Profile.PaperDomestic.Account"] = Secret.Protect("11112222"),
            ["KIS.Profile.Real.AppKey"] = Secret.Protect("REAL-KEY-MUST-NOT-BE-READ"),
            ["KIS.Profile.Real.AppSecret"] = Secret.Protect("REAL-SECRET-MUST-NOT-BE-READ"),
            ["KIS.Profile.Real.Account"] = Secret.Protect("99998888"),
        };

        [Fact]
        public void Load_Reads_Only_PaperOverseas_Dpapi_Sealed_Keys_ReadOnly()
        {
            string db = TempDb(Sealed());
            try
            {
                byte[] before = File.ReadAllBytes(db);
                var c = VtsPreFlightCredentials.Load(db, VtsPreFlightCredentials.OverseasProfile, out var why);
                Assert.Null(why);
                Assert.NotNull(c);
                Assert.Equal(FakeKey, c!.AppKey);
                Assert.Equal(FakeSecret, c.Secret);
                Assert.Equal(FakeCano, c.Cano);
                Assert.Equal("01", c.Prdt);
                Assert.Equal(VtsPreFlightCredentials.OverseasProfile, c.ProfileName);
                Assert.Equal("PaperOverseas(appkey=PS" + new string('*', FakeKey.Length - 2) + " · cano=50******)", c.ToString());
                Assert.Equal(before, File.ReadAllBytes(db));                                                                                         // 읽기 전용(쓰기 0)
                Assert.False(File.Exists(db + "-wal"));
                // 국내 기본 오버로드 = PaperDomestic 그대로(회귀 0)
                Assert.Equal("DOMESTIC-KEY-MUST-NOT-BE-READ", VtsPreFlightCredentials.Load(db, out _)!.AppKey);
            }
            finally { File.Delete(db); }
        }

        [Theory]
        [InlineData("Real")]
        [InlineData("PaperFutures")]
        [InlineData("paperoverseas")]
        [InlineData("PaperOverseas.AppKey' OR 1=1 --")]
        [InlineData("")]
        public void Non_Paper_Profiles_Are_Refused_Before_Db_Access(string profile)
        {
            string absent = Path.Combine(Path.GetTempPath(), "ms2e-overseas-absent-" + Guid.NewGuid().ToString("N") + ".db");
            Assert.Null(VtsPreFlightCredentials.Load(absent, profile, out var why));                                                                 // 파일 없음 → 열었다면 SqliteException
            Assert.Contains("판독 거부", why);
            Assert.False(File.Exists(absent));                                                                                                       // DB 미접속(생성 0)
        }

        [Fact]
        public void Missing_Overseas_Keys_Fail_Closed_And_Do_Not_Fall_Back_To_Domestic()
        {
            var rows = Sealed();
            rows.Remove("KIS.Profile.PaperOverseas.AppSecret");
            string db = TempDb(rows);
            try
            {
                Assert.Null(VtsPreFlightCredentials.Load(db, VtsPreFlightCredentials.OverseasProfile, out var why));
                Assert.Contains("PaperOverseas.AppSecret 미등록", why);
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public async Task Domestic_Profile_Credentials_Are_Refused_By_Overseas_Probe()
        {
            var h = new FakeVts();
            using var http = new HttpMessageInvoker(h);
            var dom = new VtsPreFlightCredentials(FakeKey, FakeSecret, FakeCano, "01");
            await Assert.ThrowsAsync<InvalidOperationException>(() => KisOverseasPaperProbe.RunAsync(http, dom, CancellationToken.None));
            Assert.Empty(h.Seen);
        }

        [Fact]
        public void OptIn_Is_Required_And_Separate_From_Other_Probes()
        {
            string db = TempDb(Sealed());
            try
            {
                string? Env(string v) => v == KisOverseasPaperGuard.OptInVar ? "1" : null;
                Assert.NotNull(KisOverseasPaperGuard.SkipReason(_ => null, db));
                Assert.NotNull(KisOverseasPaperGuard.SkipReason(v => v == KisOverseasPaperGuard.OptInVar ? "true" : null, db));
                Assert.NotNull(KisOverseasPaperGuard.SkipReason(Env, db + ".absent"));
                Assert.Null(KisOverseasPaperGuard.SkipReason(Env, db));
                Assert.NotNull(KisOverseasPaperGuard.SkipReason(v => v == VtsPreFlightCredentials.OptInVar ? "1" : null, db));                      // 국내 사전 점검 옵트인 ≠ 해외
                Assert.NotEqual(VtsPreFlightCredentials.OptInVar, KisOverseasPaperGuard.OptInVar);
                Assert.NotEqual(VtsProbeConfig.OptInVar, KisOverseasPaperGuard.OptInVar);
            }
            finally { File.Delete(db); }
        }

        // ════════════ §3 마스킹 · 판독 ════════════

        // 2026-10-22 23:00 KST = 10:00 EDT(정규장) · 18:00 KST = 05:00 EDT(프리) · 2026-10-24 23:00 KST = 토요일
        private static long KstMicros(int y, int mo, int d, int h, int mi) => (new DateTime(y, mo, d, h, mi, 0).Ticks - TimeSpan.TicksPerHour * 9 - DateTime.UnixEpoch.Ticks) / 10;
        private static KisOverseasProbeOptions Fast(long utcMicros) => new() { Pace = TimeSpan.Zero, UtcMicrosNow = () => utcMicros };

        [Fact]
        public async Task Fake_Vts_Roundtrip_Reports_Token_Three_Exchange_Balances_And_Quotes_Without_Leaking_Secrets()
        {
            var h = new FakeVts { EchoExchange = true };
            using var http = new HttpMessageInvoker(h);
            var rep = await KisOverseasPaperProbe.RunAsync(http, Cred, Fast(KstMicros(2026, 10, 22, 23, 0)), CancellationToken.None);
            Assert.True(rep.TokenIssued);
            Assert.Equal(86400, rep.TokenExpiresInSeconds);
            Assert.True(rep.BalanceOk);
            Assert.Equal(new[] { UsExchange.Nasdaq, UsExchange.Nyse, UsExchange.Amex }, rep.Balances.ConvertAll(b => b.Exchange));
            foreach (var b in rep.Balances)
            {
                Assert.Equal(1, b.Summary.HoldingRows);
                Assert.Equal(0, b.Summary.MismatchedExchangeRows);                                                                                    // 행 거래소 = 요청 거래소
                Assert.Equal(123_456_000_000L, b.Summary.PurchaseUsdRaw);                                                                             // $1,234.56
                Assert.Equal(-789_000_000L, b.Summary.EvalPnlUsdRaw);                                                                                 // −$7.89
                Assert.Equal(-1_234_500_000L, b.Summary.TotalPnlUsdRaw);                                                                              // −$12.345
            }
            Assert.True(rep.QuotesOk);
            Assert.Equal(3, rep.Quotes.Count);
            foreach (var q in rep.Quotes)
            {
                Assert.True(q.Summary.SymbolVerified);
                Assert.Equal(22_900_000_000L, q.Summary.LastRaw);                                                                                     // $229.00
                Assert.Equal(22_752_000_000L, q.Summary.BaseRaw);
                Assert.Equal(38_123_456L, q.Summary.Volume);
                Assert.Equal(UsSessionPhase.ContinuousTrading, q.Summary.Phase);
                Assert.Equal(UsQuoteValidity.Tradable, q.Summary.Validity);
            }
            Assert.Equal(7, h.Seen.Count);                                                                                                           // 토큰 1 + 잔고 3 + 시세 3
            Assert.Equal("Bearer " + FakeToken, string.Join("", h.Seen[1].Headers.GetValues("authorization")));
            for (int i = 1; i <= 3; i++) Assert.Equal(new[] { "VTTS3012R" }, h.Seen[i].Headers.GetValues("tr_id"));
            for (int i = 4; i <= 6; i++) Assert.Equal(new[] { "HHDFS00000300" }, h.Seen[i].Headers.GetValues("tr_id"));
            Assert.Contains("OVRS_EXCG_CD=NASD&", h.Seen[1].RequestUri!.Query);
            Assert.Contains("OVRS_EXCG_CD=NYSE&", h.Seen[2].RequestUri!.Query);
            Assert.Contains("OVRS_EXCG_CD=AMEX&", h.Seen[3].RequestUri!.Query);
            Assert.Contains("EXCD=NAS&SYMB=AAPL", h.Seen[4].RequestUri!.Query);
            Assert.Contains("EXCD=NYS&SYMB=IBM", h.Seen[5].RequestUri!.Query);
            Assert.Contains("EXCD=AMS&SYMB=SPY", h.Seen[6].RequestUri!.Query);
            string all = string.Join("\n", rep.Lines);
            foreach (var s in new[] { FakeKey, FakeSecret, FakeCano, FakeToken, FakeKey[..10], FakeToken[..10], FakeSecret[..10] })
                Assert.DoesNotContain(s, all);
            Assert.Contains("[token] 발급 성공", all);
            Assert.Contains("[balance NASD] 해외 잔고 조회 성공", all);
            Assert.Contains("[balance NYSE] 해외 잔고 조회 성공", all);
            Assert.Contains("[balance AMEX] 해외 잔고 조회 성공", all);
            Assert.Contains("[quote AMS SPY] 현재가 조회 성공", all);
            Assert.Contains("현재가 $229.00", all);
            Assert.Contains("유효성 Tradable", all);
            Assert.Contains("외화매입 $1,234.56", all);
            Assert.Contains("모의투자 조회가 완료되었습니다. ·", all);                                                                                  // msg1 후행 공백 절단
            Assert.DoesNotContain("거래소 불일치", all);
            foreach (var l in rep.Lines) _out.WriteLine(l);
        }

        [Fact]
        public async Task Quote_Validity_Follows_Us_Session_Phase_And_Mismatched_Exchange_Rows_Are_Counted()
        {
            var h = new FakeVts();
            using var http = new HttpMessageInvoker(h);
            var pre = await KisOverseasPaperProbe.RunAsync(http, Cred, Fast(KstMicros(2026, 10, 22, 18, 0)), CancellationToken.None);
            Assert.All(pre.Quotes, q => Assert.Equal(UsSessionPhase.PreMarket, q.Summary.Phase));
            Assert.All(pre.Quotes, q => Assert.Equal(UsQuoteValidity.ExtendedIndicative, q.Summary.Validity));
            Assert.Contains("주문 근거 불가", string.Join("\n", pre.Lines));
            var weekend = await KisOverseasPaperProbe.RunAsync(http, Cred, Fast(KstMicros(2026, 10, 24, 23, 0)), CancellationToken.None);
            Assert.All(weekend.Quotes, q => Assert.Equal(UsQuoteValidity.ClosedReference, q.Summary.Validity));

            // 응답 행이 다른 거래소 코드를 실어 오면 집계 · 시세 rsym 불일치 = 사용 불가(Invalid)
            var bad = new FakeVts { BalanceJson = "{\"output1\":[{\"ovrs_excg_cd\":\"NYSE\"},{\"ovrs_excg_cd\":\"NASD\"}],\"output2\":{},\"rt_cd\":\"0\"}" };
            using var http2 = new HttpMessageInvoker(bad);
            var o = Fast(KstMicros(2026, 10, 22, 23, 0));
            o.BalanceExchanges = new[] { UsExchange.Nasdaq };
            o.Quotes = new[] { (UsExchange.Nasdaq, "MSFT") };
            var r2 = await KisOverseasPaperProbe.RunAsync(http2, Cred, o, CancellationToken.None);
            Assert.Equal(2, r2.Balances[0].Summary.HoldingRows);
            Assert.Equal(1, r2.Balances[0].Summary.MismatchedExchangeRows);
            Assert.Contains("★거래소 불일치 행 1", string.Join("\n", r2.Lines));
            var q2 = VtsOverseasQuoteSummary.Parse(Encoding.UTF8.GetBytes(FakeVts.QuoteJson("NYS", "MSFT")), UsExchange.Nasdaq, "MSFT", UsSessionPhase.ContinuousTrading);
            Assert.True(q2.Ok); Assert.False(q2.SymbolVerified); Assert.Equal(UsQuoteValidity.Invalid, q2.Validity);
        }

        [Fact]
        public async Task Token_Failure_Stops_Before_Balance_And_Echoed_Secrets_Are_Masked()
        {
            var h = new FakeVts
            {
                TokenStatus = HttpStatusCode.Forbidden,
                TokenJson = "{\"error_description\":\"유효하지 않은 AppKey 입니다: " + FakeKey + "\",\"error_code\":\"EGW00103\"}",
            };
            using var http = new HttpMessageInvoker(h);
            var rep = await KisOverseasPaperProbe.RunAsync(http, Cred, CancellationToken.None);
            Assert.False(rep.TokenIssued);
            Assert.False(rep.BalanceOk);
            Assert.Single(h.Seen);                                                                                                                   // 잔고 · 시세 미송출
            Assert.Empty(rep.Balances); Assert.Empty(rep.Quotes);
            string all = string.Join("\n", rep.Lines);
            Assert.Contains("EGW00103", all);
            Assert.DoesNotContain(FakeKey, all);
        }

        [Fact]
        public void Overseas_Balance_Summary_Fails_Closed_On_Error_And_Garbage()
        {
            var e = VtsOverseasBalanceSummary.Parse("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"msg1\":\"초당 거래건수를 초과하였습니다.\"}"u8);
            Assert.True(e.Parsed); Assert.False(e.Ok); Assert.Equal("EGW00201", e.MsgCd); Assert.Equal(-1, e.HoldingRows);
            Assert.Equal(VtsOverseasBalanceSummary.Missing, e.PurchaseUsdRaw);
            Assert.Equal("n/a", VtsOverseasBalanceSummary.Usd(e.PurchaseUsdRaw));
            Assert.False(VtsOverseasBalanceSummary.Parse("<html>502</html>"u8).Ok);
            Assert.False(VtsOverseasBalanceSummary.Parse(ReadOnlySpan<byte>.Empty).Ok);
            Assert.False(VtsOverseasBalanceSummary.Parse("[1,2]"u8).Ok);
            var arr = VtsOverseasBalanceSummary.Parse("{\"rt_cd\":\"0\",\"output1\":[],\"output2\":[{\"frcr_pchs_amt1\":\"0.00000\"}]}"u8);           // 배열 변형 수용
            Assert.True(arr.Ok); Assert.Equal(0, arr.PurchaseUsdRaw); Assert.Equal(VtsOverseasBalanceSummary.Missing, arr.EvalPnlUsdRaw);
            var huge = VtsOverseasBalanceSummary.Parse("{\"rt_cd\":\"0\",\"output2\":{\"frcr_pchs_amt1\":\"999999999999999999999.0\"}}"u8);         // raw 오버플로 = Missing
            Assert.Equal(VtsOverseasBalanceSummary.Missing, huge.PurchaseUsdRaw);
        }

        // ════════════ §4 리얼 와이어(옵트인) ════════════

        [KisOverseasPaperFact]
        public async Task LiveWire_Vts_Overseas_Token_Three_Exchange_Balances_And_Quotes_Roundtrip()
        {
            var c = VtsPreFlightCredentials.Load(VtsPreFlightCredentials.DefaultDbPath, VtsPreFlightCredentials.OverseasProfile, out var why);
            Assert.True(c is not null, "자격증명 판독 실패: " + why);
            using var http = VtsPreFlightGuard.CreateInvoker();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var rep = await KisOverseasPaperProbe.RunAsync(http, c!, cts.Token);
            string dir = Path.Combine(Path.GetTempPath(), "ms2e-vts-probe");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "overseas-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), rep.Lines);
            foreach (var l in rep.Lines) _out.WriteLine(l);
            Assert.True(rep.TokenIssued, "토큰 발급 실패");
            Assert.Equal(3, rep.Balances.Count);
            Assert.True(rep.BalanceOk, "해외 잔고 조회 실패(3대 거래소 중 1곳 이상)");
            Assert.Equal(3, rep.Quotes.Count);
            Assert.True(rep.QuotesOk, "해외 현재가 조회 실패(rt_cd ≠ 0 또는 HTTP ≠ 200)");
        }
    }
}
