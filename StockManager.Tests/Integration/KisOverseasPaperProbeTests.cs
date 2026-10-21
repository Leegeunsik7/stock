// ============================================================================
// KisOverseasPaperProbeTests — [v12.18.53-DEV] KIS 해외주식 모의투자(VTS) 통신 점검(토큰 · VTTS3012R 해외 잔고) 하네스 검증
//
//   §1 송출 허용 목록(오프라인): 실도메인 · 위장 호스트 · http · 해외 주문 TR · 실 해외 조회 TR(TTTS3012R) · 국내 잔고 경로/TR ·
//      허용 밖 경로/메서드 · tr_id 중복 · 본문 동봉 = 예외 · 가드 위반 요청 핸들러 도달 0(패킷 0) · 요청 생성기 = 허용 목록 통과
//   §2 자격증명(오프라인 · 임시 DB): PaperOverseas 키 3종만 판독(PaperDomestic · Real 미판독) · 허용 프로필 밖(Real · PaperFutures) = DB 미접속 거부 ·
//      국내 프로필 자격증명으로 해외 점검 실행 = 거부 · 옵트인 Skip 사유(국내 사전 점검 · 주문 프로브 옵트인과 분리)
//   §3 마스킹 · 판독(오프라인): 가짜 VTS 왕복 출력 줄에 키/시크릿/토큰/계좌 원문 0 · 토큰 실패 = 잔고 미송출 · USD 금액 raw 10^8 · 형식 이탈 fail-closed
//   §4 리얼 와이어(옵트인 MS2E_KIS_VTS_OVERSEAS=1): 실제 VTS 토큰 발급 → VTTS3012R 해외 잔고 → 마스킹 요약 출력(결과 코드 · USD 금액 · 지연)
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
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Seen.Add(request);
                bool tok = request.RequestUri!.AbsolutePath == VtsPreFlightGuard.TokenPath;
                return Task.FromResult(new HttpResponseMessage(tok ? TokenStatus : HttpStatusCode.OK)
                { Content = new StringContent(tok ? TokenJson : BalanceJson, Encoding.UTF8, "application/json") });
            }
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

        [Fact]
        public async Task Fake_Vts_Roundtrip_Reports_Token_And_Overseas_Balance_Without_Leaking_Secrets()
        {
            var h = new FakeVts();
            using var http = new HttpMessageInvoker(h);
            var rep = await KisOverseasPaperProbe.RunAsync(http, Cred, CancellationToken.None);
            Assert.True(rep.TokenIssued);
            Assert.Equal(86400, rep.TokenExpiresInSeconds);
            Assert.True(rep.BalanceOk);
            Assert.Equal(1, rep.Balance.HoldingRows);
            Assert.Equal(123_456_000_000L, rep.Balance.PurchaseUsdRaw);                                                                               // $1,234.56
            Assert.Equal(-789_000_000L, rep.Balance.EvalPnlUsdRaw);                                                                                   // −$7.89
            Assert.Equal(-1_234_500_000L, rep.Balance.TotalPnlUsdRaw);                                                                                // −$12.345
            Assert.Equal(2, h.Seen.Count);
            Assert.Equal("Bearer " + FakeToken, string.Join("", h.Seen[1].Headers.GetValues("authorization")));
            Assert.Equal(new[] { "VTTS3012R" }, h.Seen[1].Headers.GetValues("tr_id"));
            string all = string.Join("\n", rep.Lines);
            foreach (var s in new[] { FakeKey, FakeSecret, FakeCano, FakeToken, FakeKey[..10], FakeToken[..10], FakeSecret[..10] })
                Assert.DoesNotContain(s, all);
            Assert.Contains("[token] 발급 성공", all);
            Assert.Contains("[balance] 해외 잔고 조회 성공", all);
            Assert.Contains("외화매입 $1,234.56", all);
            Assert.Contains("모의투자 조회가 완료되었습니다. ·", all);                                                                                  // msg1 후행 공백 절단
            foreach (var l in rep.Lines) _out.WriteLine(l);
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
            Assert.Single(h.Seen);                                                                                                                   // 잔고 미송출
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
        public async Task LiveWire_Vts_Overseas_Token_And_Vtts3012R_Balance_Roundtrip()
        {
            var c = VtsPreFlightCredentials.Load(VtsPreFlightCredentials.DefaultDbPath, VtsPreFlightCredentials.OverseasProfile, out var why);
            Assert.True(c is not null, "자격증명 판독 실패: " + why);
            using var http = VtsPreFlightGuard.CreateInvoker();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var rep = await KisOverseasPaperProbe.RunAsync(http, c!, cts.Token);
            string dir = Path.Combine(Path.GetTempPath(), "ms2e-vts-probe");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "overseas-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), rep.Lines);
            foreach (var l in rep.Lines) _out.WriteLine(l);
            Assert.True(rep.TokenIssued, "토큰 발급 실패");
            Assert.True(rep.BalanceOk, "해외 잔고 조회 실패");
        }
    }
}
