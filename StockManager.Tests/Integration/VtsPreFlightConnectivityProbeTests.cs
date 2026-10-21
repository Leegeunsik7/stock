// ============================================================================
// VtsPreFlightConnectivityProbeTests — [v12.18.53-DEV] VTS REST 사전 통신 점검(토큰 · 잔고) 하네스 검증
//
//   §1 송출 허용 목록(오프라인): 실 REST 호스트/포트 · 위장 호스트 · http · 주문 TR · 실 조회 TR(TTTC8434R) · 허용 밖 경로/메서드 · tr_id 중복 = 예외 ·
//      가드 위반 요청은 HttpMessageHandler 도달 0(패킷 0) · 토큰/잔고 요청 생성기 = 허용 목록 통과 · 자동 리다이렉트 꺼짐
//   §2 자격증명(오프라인 · 임시 DB): PaperDomestic 키 3종만 판독 · 평문 저장 거부 · 미등록 거부 · 계좌 형식(8 / 8-2) · 읽기 전용(파일 바이트 불변) · 옵트인 Skip 사유
//   §3 마스킹(오프라인): 앞 2자리 외 '*' · 긴 값 우선 · 짧은 값 비치환 · ToString 마스킹 · 가짜 VTS 왕복 출력 줄에 키/시크릿/토큰/계좌 원문 0
//   §4 리얼 와이어(옵트인 MS2E_KIS_VTS_PREFLIGHT=1): 실제 VTS 토큰 발급 → VTTC8434R 잔고 → 마스킹 요약 출력(결과 코드 · 금액 · 지연)
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
    public sealed class VtsPreFlightConnectivityProbeTests
    {
        private readonly ITestOutputHelper _out;
        public VtsPreFlightConnectivityProbeTests(ITestOutputHelper o) => _out = o;

        private const string FakeKey = "PSfakeAppKey0123456789abcdefghijklmn";
        private const string FakeSecret = "fakeSecretValue/0123456789+abcdefghijklmnopqrstuvwxyz==";
        private const string FakeCano = "50012345";
        private const string FakeToken = "eyJfakeAccessToken.0123456789abcdefghijklmnopqrstuvwxyz";
        private static readonly VtsPreFlightCredentials Cred = new(FakeKey, FakeSecret, FakeCano, "01");

        private sealed class FakeVts : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Seen = new();
            public string TokenJson = "{\"access_token\":\"" + FakeToken + "\",\"token_type\":\"Bearer\",\"expires_in\":86400,\"access_token_token_expired\":\"2026-10-22 21:00:00\"}";
            public string BalanceJson = "{\"rt_cd\":\"0\",\"msg_cd\":\"20310000\",\"msg1\":\"모의투자 조회가 완료되었습니다.\",\"output1\":[],"
                                      + "\"output2\":[{\"dnca_tot_amt\":\"10000000\",\"tot_evlu_amt\":\"10000000\",\"nass_amt\":\"10000000\"}]}";
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

        private const string Vts = "https://openapivts.koreainvestment.com:29443";
        private const string BalQ = VtsPreFlightGuard.BalancePath + "?CANO=1";

        // ════════════ §1 송출 허용 목록 ════════════

        [Fact]
        public void Allowlist_Accepts_Only_Vts_Token_Post_And_Vttc8434R_Balance_Get()
        {
            using (var t = VtsPreFlightRequests.Token(Cred)) VtsPreFlightGuard.EnsureAllowed(t);
            using (var b = VtsPreFlightRequests.Balance(Cred, FakeToken)) VtsPreFlightGuard.EnsureAllowed(b);

            void Rejects(HttpRequestMessage r) { using (r) Assert.Throws<InvalidOperationException>(() => VtsPreFlightGuard.EnsureAllowed(r)); }
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:9443" + VtsPreFlightGuard.TokenPath));            // 실도메인
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:29443" + VtsPreFlightGuard.TokenPath));           // 실 호스트 + VTS 포트
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapivts.koreainvestment.com:9443" + VtsPreFlightGuard.TokenPath));         // 실 포트
            Rejects(new HttpRequestMessage(HttpMethod.Post, "https://openapivts.koreainvestment.com.evil.io:29443" + VtsPreFlightGuard.TokenPath)); // 위장 호스트
            Rejects(new HttpRequestMessage(HttpMethod.Post, "http://openapivts.koreainvestment.com:29443" + VtsPreFlightGuard.TokenPath));          // 평문
            Rejects(Get("https://openapi.koreainvestment.com:9443" + BalQ, "TTTC8434R"));                                                           // 실 잔고
            Rejects(Get(Vts + BalQ, "TTTC8434R"));                                                                                                  // VTS 호스트 + 실 TR
            Rejects(Get(Vts + BalQ, "VTTC0801U"));                                                                                                  // 주문 TR
            Rejects(Get(Vts + BalQ));                                                                                                               // tr_id 없음
            Rejects(Get(Vts + BalQ, "VTTC8434R", "VTTC8434R"));                                                                                     // tr_id 중복
            Rejects(Get(Vts + BalQ, "VTTC8434R", "VTTC0801U"));
            Rejects(Get(Vts + "/uapi/domestic-stock/v1/trading/order-cash", "VTTC8434R"));                                                          // 주문 경로
            Rejects(Get(Vts + "/uapi/domestic-stock/v1/trading/inquire-psbl-order", "VTTC8434R"));                                                  // 허용 밖 조회
            Rejects(Get(Vts + VtsPreFlightGuard.TokenPath));                                                                                        // 토큰 = POST 만
            Rejects(Get(Vts + "/oauth2/Approval"));
            var postBal = new HttpRequestMessage(HttpMethod.Post, Vts + BalQ); postBal.Headers.TryAddWithoutValidation("tr_id", "VTTC8434R");
            Rejects(postBal);                                                                                                                       // 잔고 = GET 만
            var tokTr = new HttpRequestMessage(HttpMethod.Post, Vts + VtsPreFlightGuard.TokenPath); tokTr.Headers.TryAddWithoutValidation("tr_id", "VTTC0801U");
            Rejects(tokTr);                                                                                                                         // 토큰 + tr_id
            Rejects(new HttpRequestMessage(HttpMethod.Post, Vts + "/uapi/domestic-stock/v1/trading/order-cash"));                                    // 주문 POST
        }

        [Fact]
        public async Task Blocked_Requests_Never_Reach_The_Wire()
        {
            var h = new FakeVts();
            using var http = new HttpMessageInvoker(h);
            foreach (var r in new[]
            {
                new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:9443" + VtsPreFlightGuard.TokenPath),
                Get("https://openapi.koreainvestment.com:9443" + BalQ, "TTTC8434R"),
                Get(Vts + BalQ, "VTTC0801U"),
                new HttpRequestMessage(HttpMethod.Post, Vts + "/uapi/domestic-stock/v1/trading/order-cash"),
            })
                using (r) await Assert.ThrowsAsync<InvalidOperationException>(() => VtsPreFlightGuard.SendAsync(http, r, CancellationToken.None));
            Assert.Empty(h.Seen);                                                                                                                   // 패킷 0
        }

        [Fact]
        public void Request_Builders_Pin_Vts_Endpoint_Read_Tr_And_Masked_Fields()
        {
            using var t = VtsPreFlightRequests.Token(Cred);
            Assert.Equal(Vts + VtsPreFlightGuard.TokenPath, t.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, t.Method);
            Assert.False(t.Headers.Contains("tr_id"));
            using var b = VtsPreFlightRequests.Balance(Cred, FakeToken);
            Assert.Equal(HttpMethod.Get, b.Method);
            Assert.Equal("openapivts.koreainvestment.com", b.RequestUri!.Host);
            Assert.Equal(29443, b.RequestUri.Port);
            Assert.Equal(new[] { "VTTC8434R" }, b.Headers.GetValues("tr_id"));
            Assert.Contains("CANO=" + FakeCano + "&ACNT_PRDT_CD=01&", b.RequestUri.Query);
            Assert.Null(b.Content);                                                                                                                 // 조회 = 본문 0
        }

        [Fact]
        public void Live_Invoker_Does_Not_Follow_Redirects()
        {
            using var handler = VtsPreFlightGuard.CreateHandler();
            Assert.False(handler.AllowAutoRedirect);
        }

        // ════════════ §2 자격증명(임시 DB) ════════════

        private static string TempDb(Dictionary<string, string> rows)
        {
            string p = Path.Combine(Path.GetTempPath(), "ms2e-preflight-" + Guid.NewGuid().ToString("N") + ".db");
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

        private static Dictionary<string, string> Sealed(string account) => new()
        {
            [VtsPreFlightCredentials.AppKeyKey] = Secret.Protect(FakeKey),
            [VtsPreFlightCredentials.SecretKey] = Secret.Protect(FakeSecret),
            [VtsPreFlightCredentials.AccountKey] = Secret.Protect(account),
            ["KIS.Profile.Real.AppKey"] = Secret.Protect("REAL-KEY-MUST-NOT-BE-READ"),
            ["KIS.AppKey"] = Secret.Protect("ACTIVE-KEY-MUST-NOT-BE-READ"),
        };

        [Fact]
        public void Load_Reads_Only_PaperDomestic_Dpapi_Sealed_Keys_ReadOnly()
        {
            string db = TempDb(Sealed(FakeCano));
            try
            {
                byte[] before = File.ReadAllBytes(db);
                var c = VtsPreFlightCredentials.Load(db, out var why);
                Assert.Null(why);
                Assert.NotNull(c);
                Assert.Equal(FakeKey, c!.AppKey);
                Assert.Equal(FakeSecret, c.Secret);
                Assert.Equal(FakeCano, c.Cano);
                Assert.Equal("01", c.Prdt);
                Assert.Equal(before, File.ReadAllBytes(db));                                                                                        // 읽기 전용(쓰기 0)
                Assert.False(File.Exists(db + "-wal"));

                File.Delete(db);
                db = TempDb(Sealed("50012345-22"));
                Assert.Equal("22", VtsPreFlightCredentials.Load(db, out _)!.Prdt);
            }
            finally { File.Delete(db); }
        }

        [Theory]
        [InlineData("plain-appkey", null)]
        [InlineData("missing-secret", null)]
        [InlineData("bad-account", "5001234")]
        [InlineData("bad-account", "5001234X")]
        [InlineData("bad-account", "50012345-1")]
        public void Load_Fails_Closed_On_Plaintext_Missing_Or_Malformed(string kind, string? account)
        {
            var rows = Sealed(account ?? FakeCano);
            if (kind == "plain-appkey") rows[VtsPreFlightCredentials.AppKeyKey] = FakeKey;                                                         // 평문 저장
            if (kind == "missing-secret") rows.Remove(VtsPreFlightCredentials.SecretKey);
            string db = TempDb(rows);
            try
            {
                Assert.Null(VtsPreFlightCredentials.Load(db, out var why));
                Assert.False(string.IsNullOrEmpty(why));
                Assert.DoesNotContain(FakeKey, why!);
                Assert.DoesNotContain(FakeSecret, why!);
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void OptIn_Is_Required_And_Missing_Db_Skips()
        {
            string db = TempDb(Sealed(FakeCano));
            try
            {
                Assert.NotNull(VtsPreFlightCredentials.SkipReason(_ => null, db));
                Assert.NotNull(VtsPreFlightCredentials.SkipReason(v => v == VtsPreFlightCredentials.OptInVar ? "true" : null, db));
                Assert.NotNull(VtsPreFlightCredentials.SkipReason(v => v == VtsPreFlightCredentials.OptInVar ? "1" : null, db + ".absent"));
                Assert.Null(VtsPreFlightCredentials.SkipReason(v => v == VtsPreFlightCredentials.OptInVar ? "1" : null, db));
                Assert.NotEqual(VtsProbeConfig.OptInVar, VtsPreFlightCredentials.OptInVar);                                                         // 주문 프로브 옵트인과 분리
            }
            finally { File.Delete(db); }
        }

        // ════════════ §3 마스킹 ════════════

        [Fact]
        public void Redactor_Masks_All_But_First_Two_Chars_Longest_First()
        {
            var r = new PreFlightRedactor();
            r.Add(FakeCano); r.Add(FakeCano + "-01"); r.Add("ab"); r.Add(null); r.Add(FakeKey);
            string s = r.Redact("acct " + FakeCano + "-01 key=" + FakeKey + " ab ok");
            Assert.Equal("acct 50********* key=PS" + new string('*', FakeKey.Length - 2) + " ab ok", s);
            Assert.Equal("", r.Redact(null));
            Assert.Equal("50******", VtsPayloadMask.Mask(FakeCano));
            Assert.Equal("PaperDomestic(appkey=PS" + new string('*', FakeKey.Length - 2) + " · cano=50******)", Cred.ToString());
        }

        [Fact]
        public async Task Fake_Vts_Roundtrip_Reports_Token_And_Balance_Without_Leaking_Secrets()
        {
            var h = new FakeVts();
            using var http = new HttpMessageInvoker(h);
            var rep = await VtsPreFlightConnectivityProbe.RunAsync(http, Cred, CancellationToken.None);
            Assert.True(rep.TokenIssued);
            Assert.Equal(86400, rep.TokenExpiresInSeconds);
            Assert.Equal("2026-10-22 21:00:00", rep.TokenExpiredAtKst);
            Assert.True(rep.BalanceOk);
            Assert.Equal(10_000_000, rep.Balance.DepositWon);
            Assert.Equal(0, rep.Balance.HoldingRows);
            Assert.Equal(2, h.Seen.Count);
            Assert.Equal("Bearer " + FakeToken, string.Join("", h.Seen[1].Headers.GetValues("authorization")));
            string all = string.Join("\n", rep.Lines);
            foreach (var s in new[] { FakeKey, FakeSecret, FakeCano, FakeToken, FakeKey[..10], FakeToken[..10], FakeSecret[..10] })
                Assert.DoesNotContain(s, all);
            Assert.Contains("[token] 발급 성공", all);
            Assert.Contains("[balance] 조회 성공", all);
            Assert.Contains("예수금 10,000,000원", all);
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
            var rep = await VtsPreFlightConnectivityProbe.RunAsync(http, Cred, CancellationToken.None);
            Assert.False(rep.TokenIssued);
            Assert.False(rep.BalanceOk);
            Assert.Single(h.Seen);                                                                                                                  // 잔고 미송출
            string all = string.Join("\n", rep.Lines);
            Assert.Contains("EGW00103", all);
            Assert.DoesNotContain(FakeKey, all);
        }

        [Fact]
        public void Balance_Summary_Fails_Closed_On_Error_And_Garbage()
        {
            var e = VtsBalanceSummary.Parse("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00201\",\"msg1\":\"초당 거래건수를 초과하였습니다.\"}"u8);
            Assert.True(e.Parsed); Assert.False(e.Ok); Assert.Equal("EGW00201", e.MsgCd); Assert.Equal(-1, e.HoldingRows); Assert.Equal(-1, e.DepositWon);
            Assert.False(VtsBalanceSummary.Parse("<html>502</html>"u8).Ok);
            Assert.False(VtsBalanceSummary.Parse(ReadOnlySpan<byte>.Empty).Ok);
            Assert.False(VtsBalanceSummary.Parse("[1,2]"u8).Ok);
        }

        // ════════════ §4 리얼 와이어(옵트인) ════════════

        [KisVtsPreFlightFact]
        public async Task LiveWire_Vts_PreFlight_Token_And_Balance_Roundtrip()
        {
            var c = VtsPreFlightCredentials.Load(VtsPreFlightCredentials.DefaultDbPath, out var why);
            Assert.True(c is not null, "자격증명 판독 실패: " + why);
            using var http = VtsPreFlightGuard.CreateInvoker();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var rep = await VtsPreFlightConnectivityProbe.RunAsync(http, c!, cts.Token);
            string dir = Path.Combine(Path.GetTempPath(), "ms2e-vts-probe");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "preflight-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), rep.Lines);
            foreach (var l in rep.Lines) _out.WriteLine(l);
            Assert.True(rep.TokenIssued, "토큰 발급 실패");
            Assert.True(rep.BalanceOk, "잔고 조회 실패");
        }
    }
}
