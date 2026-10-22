// ============================================================================
// VtsOrderRejectionProbeTests — [v12.18.41-DEV] VTS 고의 거부 프로브 하네스 검증
//
//   §1 도메인 가드(오프라인): 실도메인 플래그 · 실 REST 호스트/포트 · 위장 호스트(부분 문자열 'openapivts' 포함) · http · 실 WebSocket(21000) · 실 TR(TTTC…) = 예외 ·
//      송출 직전 재검 = 가드 위반 요청은 HttpMessageHandler 도달 0(패킷 0) · VTS 요청만 1회 도달
//   §2 주문 명세(오프라인): 005930 매도 1주 지정가 999,999 · ORD_DVSN 00 · VTTC0801U · 취소 VTTC0803U(원주문 · 조직번호) · 같은 가드
//   §3 실행 조건(오프라인): PROBE=1 옵트인 · HTS ID(env) · portfolio.db · 정규장 — 미충족 = Skip 사유(실패 아님) · [v12.18.55] DPAPI 로더 결합 · ToString 마스킹 ·
//      마스킹 · VTS H0STCNI9 거부 통보 → 비트 23
//   §4 리얼 와이어(옵트인 · 장중): 체결통보 구독 확인 → 고의 거부 주문 1회 →
//      [단계 1] REST 동기 응답 원시 캡처(rt_cd · msg_cd · msg1 · 공백 패딩) + 제품 판독기 TryParseOrderAck 대조 ·
//      [단계 2] 접수 후 사후 거부 = H0STCNI9 + 비트 23 래치 단언 / REST 즉시 기각 = 통보 미발생 사유 기록(실패 아님)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    public sealed class VtsOrderRejectionProbeTests
    {
        private readonly ITestOutputHelper _out;
        public VtsOrderRejectionProbeTests(ITestOutputHelper o) => _out = o;

        private static readonly VtsProbeConfig Cfg = new("app-key-x", "secret-x", "htsuser", "50012345", "01");

        private sealed class RecordingHandler : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Seen = new();
            public readonly List<string> Bodies = new();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Seen.Add(request);
                Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"rt_cd\":\"1\",\"msg_cd\":\"TEST0000\",\"msg1\":\"offline\"}") };
            }
        }

        // ════════════ §1 도메인 가드 ════════════

        [Fact]
        public void Guard_Accepts_Only_Exact_Vts_Rest_Ws_And_Tr()
        {
            VtsDomainGuard.EnsureVts(VtsDomainGuard.VtsRestBase, false, KisRealtimeSession.VtsUri, "VTTC0801U");   // 정상 = 무예외
            Assert.Equal("https://openapivts.koreainvestment.com:29443/", VtsDomainGuard.VtsRestBase.ToString());

            void Rejects(Action a) => Assert.Throws<InvalidOperationException>(a);
            Rejects(() => VtsDomainGuard.EnsureVts(VtsDomainGuard.VtsRestBase, true, KisRealtimeSession.VtsUri, "VTTC0801U"));          // IsRealDomain
            Rejects(() => VtsDomainGuard.EnsureVts(new Uri("https://openapi.koreainvestment.com:9443"), false, KisRealtimeSession.VtsUri, "VTTC0801U"));   // 실 REST
            Rejects(() => VtsDomainGuard.EnsureVts(new Uri("https://openapivts.koreainvestment.com:9443"), false, KisRealtimeSession.VtsUri, "VTTC0801U")); // 실 포트
            Rejects(() => VtsDomainGuard.EnsureVts(new Uri("https://openapivts.koreainvestment.com.evil.io:29443"), false, KisRealtimeSession.VtsUri, "VTTC0801U"));   // 위장(부분 문자열)
            Rejects(() => VtsDomainGuard.EnsureVts(new Uri("https://evil.io:29443/openapivts"), false, KisRealtimeSession.VtsUri, "VTTC0801U"));
            Rejects(() => VtsDomainGuard.EnsureVts(new Uri("http://openapivts.koreainvestment.com:29443"), false, KisRealtimeSession.VtsUri, "VTTC0801U"));    // 평문
            Rejects(() => VtsDomainGuard.EnsureVts(VtsDomainGuard.VtsRestBase, false, KisRealtimeSession.RealUri, "VTTC0801U"));       // 실 WebSocket 21000
            Rejects(() => VtsDomainGuard.EnsureVts(VtsDomainGuard.VtsRestBase, false, KisRealtimeSession.VtsUri, "TTTC0801U"));        // 실 TR
            Rejects(() => VtsDomainGuard.EnsureVts(VtsDomainGuard.VtsRestBase, false, KisRealtimeSession.VtsUri, KisLiveBroker.TrSellReal));
            Assert.StartsWith("VTTC", KisLiveBroker.TrSellVts);
        }

        [Fact]
        public async Task Guarded_Send_Never_Reaches_The_Wire_For_Real_Domain_Requests()
        {
            var h = new RecordingHandler();
            using var http = new HttpMessageInvoker(h);
            async Task Blocked(HttpRequestMessage r)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => VtsDomainGuard.SendGuardedAsync(http, r, CancellationToken.None));
                r.Dispose();
            }
            await Blocked(new HttpRequestMessage(HttpMethod.Post, "https://openapi.koreainvestment.com:9443/uapi/domestic-stock/v1/trading/order-cash"));
            var realTr = new HttpRequestMessage(HttpMethod.Post, new Uri(VtsDomainGuard.VtsRestBase, "/uapi/domestic-stock/v1/trading/order-cash"));
            realTr.Headers.TryAddWithoutValidation("tr_id", "TTTC0801U");                       // VTS 호스트 + 실 TR 헤더
            await Blocked(realTr);
            Assert.Empty(h.Seen);                                                               // ★가드 위반 = 핸들러 도달 0(패킷 0)

            var (status, _) = await VtsRestClient.PostAsync(http, VtsRejectionOrder.Build(VtsDomainGuard.VtsRestBase, Cfg, "tok"), CancellationToken.None);
            Assert.Equal(200, status);
            var sent = Assert.Single(h.Seen);
            Assert.Equal(VtsDomainGuard.VtsHost, sent.RequestUri!.Host);
            Assert.Equal(29443, sent.RequestUri.Port);
        }

        // ════════════ §2 주문 명세 ════════════

        [Fact]
        public async Task Rejection_Order_Is_A_One_Share_Sell_Far_Above_The_Price_Limit()
        {
            var h = new RecordingHandler();
            using var http = new HttpMessageInvoker(h);
            await VtsRestClient.PostAsync(http, VtsRejectionOrder.Build(VtsDomainGuard.VtsRestBase, Cfg, "tok"), CancellationToken.None);
            var req = h.Seen[0];
            Assert.Equal("/uapi/domestic-stock/v1/trading/order-cash", req.RequestUri!.AbsolutePath);
            Assert.Equal("VTTC0801U", req.Headers.GetValues("tr_id").Single());               // 모의 매도
            Assert.Equal("Bearer tok", req.Headers.GetValues("authorization").Single());
            using var body = JsonDocument.Parse(h.Bodies[0]);
            var b = body.RootElement;
            Assert.Equal(("005930", "1", "999999", "00"), (b.GetProperty("PDNO").GetString(), b.GetProperty("ORD_QTY").GetString(), b.GetProperty("ORD_UNPR").GetString(), b.GetProperty("ORD_DVSN").GetString()));
            Assert.Equal(("50012345", "01"), (b.GetProperty("CANO").GetString(), b.GetProperty("ACNT_PRDT_CD").GetString()));

            await VtsRestClient.PostAsync(http, VtsRejectionOrder.BuildCancel(VtsDomainGuard.VtsRestBase, Cfg, "tok", orgNo: 91252, odno: 117057), CancellationToken.None);
            Assert.Equal("VTTC0803U", h.Seen[1].Headers.GetValues("tr_id").Single());
            using var cb = JsonDocument.Parse(h.Bodies[1]);
            Assert.Equal(("91252", "0000117057", "02", "Y"), (cb.RootElement.GetProperty("KRX_FWDG_ORD_ORGNO").GetString(), cb.RootElement.GetProperty("ORGN_ODNO").GetString(),
                                                              cb.RootElement.GetProperty("RVSE_CNCL_DVSN_CD").GetString(), cb.RootElement.GetProperty("QTY_ALL_ORD_YN").GetString()));
            Assert.Throws<InvalidOperationException>(() => VtsRejectionOrder.Build(new Uri("https://openapi.koreainvestment.com:9443"), Cfg, "tok"));
        }

        // ════════════ §3 실행 조건 · 마스킹 · VTS 통보 판독 ════════════

        [Fact]
        public void Skip_Reasons_Are_Graceful_And_Require_Explicit_Opt_In()
        {
            var open = new DateTime(2026, 10, 21, 1, 0, 0, DateTimeKind.Utc);                  // 수 10:00 KST
            var closed = new DateTime(2026, 10, 20, 22, 0, 0, DateTimeKind.Utc);               // 수 07:00 KST
            string db = Path.GetTempFileName();
            try
            {
                var all = new Dictionary<string, string?> { [VtsProbeConfig.HtsIdVar] = "h", [VtsProbeConfig.OptInVar] = "1" };
                string? Reason(Action<Dictionary<string, string?>> edit, DateTime t, string? dbPath = null)
                {
                    var e = new Dictionary<string, string?>(all);
                    edit(e);
                    return VtsProbeConfig.SkipReason(v => e.GetValueOrDefault(v), t, dbPath ?? db);
                }
                Assert.Null(Reason(_ => { }, open));                                            // 전부 충족 = 실행
                Assert.Contains("PROBE", Reason(e => e[VtsProbeConfig.OptInVar] = null, open)); // 사전 점검 옵트인만 = 주문 0
                Assert.Contains("PROBE", Reason(e => e[VtsProbeConfig.OptInVar] = "true", open));
                Assert.Contains("MS2E_KIS_HTS_ID", Reason(e => e.Remove(VtsProbeConfig.HtsIdVar), open));
                Assert.Contains("MS2E_KIS_HTS_ID", Reason(e => e[VtsProbeConfig.HtsIdVar] = "  ", open));
                Assert.Contains("portfolio.db", Reason(_ => { }, open, Path.Combine(Path.GetTempPath(), "ms2e-none-" + Guid.NewGuid().ToString("N") + ".db")));
                Assert.Contains("정규장", Reason(_ => { }, closed));
                Assert.Contains("정규장", Reason(_ => { }, new DateTime(2026, 12, 31, 1, 0, 0, DateTimeKind.Utc)));    // [v12.18.52] 연말 폐장일 10:00 = 미송출
                Assert.Contains("정규장", Reason(_ => { }, new DateTime(2029, 1, 4, 1, 0, 0, DateTimeKind.Utc)));     // 달력 범위 밖 = 미송출(감시 측과 반대 방향)
                // [v12.18.55] 평문 자격증명 환경변수 경로 폐지 — 옛 변수(MS2E_KIS_VTS_APPKEY 등)만 있어도 HTS ID/옵트인 없으면 미송출
                Assert.NotNull(VtsProbeConfig.SkipReason(v => v.StartsWith("MS2E_KIS_VTS_", StringComparison.Ordinal) && v != VtsProbeConfig.OptInVar ? "x" : null, open, db));
            }
            finally { File.Delete(db); }
        }

        [Fact]
        public void Config_Loads_PaperDomestic_From_Dpapi_Db_And_Masks_ToString()
        {
            string db = Path.Combine(Path.GetTempPath(), "ms2e-vtsprobe-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                using (var c = new SqliteConnection("Data Source=" + db + ";Pooling=False"))
                {
                    c.Open();
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "CREATE TABLE Settings([Key] TEXT PRIMARY KEY, Value TEXT)";
                        cmd.ExecuteNonQuery();
                    }
                    void Put(string k, string v)
                    {
                        using var i = c.CreateCommand();
                        i.CommandText = "INSERT INTO Settings VALUES(@k,@v)";
                        i.Parameters.AddWithValue("@k", k);
                        i.Parameters.AddWithValue("@v", Secret.Protect(v));
                        i.ExecuteNonQuery();
                    }
                    Put(VtsPreFlightCredentials.AppKeyKey, "PSdomFakeAppKey0123456789");
                    Put(VtsPreFlightCredentials.SecretKey, "domFakeSecret/0123456789+abc==");
                    Put(VtsPreFlightCredentials.AccountKey, "50098765-01");
                    Put("KIS.Profile.Real.AppKey", "REALKEY-MUST-NOT-LOAD");
                }
                Assert.Null(VtsProbeConfig.Load(db, null, out var f0));
                Assert.Contains("MS2E_KIS_HTS_ID", f0);
                var cfg = VtsProbeConfig.Load(db, " htsuser01 ", out var f1);
                Assert.True(cfg is not null, f1);
                Assert.Equal(("PSdomFakeAppKey0123456789", "domFakeSecret/0123456789+abc==", "htsuser01", "50098765", "01"), (cfg!.AppKey, cfg.Secret, cfg.HtsId, cfg.Cano, cfg.Prdt));
                string shown = cfg.ToString();
                foreach (var secret in new[] { cfg.AppKey, cfg.Secret, cfg.HtsId, cfg.Cano }) Assert.DoesNotContain(secret, shown);
                Assert.Null(VtsProbeConfig.Load(db + ".missing", "h", out var f2));               // DB 없음 = 거부
                Assert.False(string.IsNullOrEmpty(f2));
            }
            finally { SqliteConnection.ClearAllPools(); File.Delete(db); }
        }

        [Fact]
        public void Masking_Hides_Customer_And_Account()
        {
            Assert.Equal("cu****^12******^0000117057^x", VtsPayloadMask.MaskNotice("cust01^12345678^0000117057^x"));
            Assert.Equal("{\"CANO\":\"50******\"}", VtsPayloadMask.MaskAccount("{\"CANO\":\"50012345\"}", "50012345"));
        }

        private static string VtsNotice(long odno, int refuse, int cntgYn)
            => string.Join('^', new[] { "cust01", "50012345", odno.ToString("D10"), "", "01", "0", "00", "0", "005930", "0", "0", "093001", refuse.ToString(), cntgYn.ToString(), "1", "91252", "1", "홍길동", "삼성전자" });

        [Fact]
        public void Vts_H0STCNI9_Reject_Notice_Latches_Bit23_Through_The_Product_Path()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            var m = new ChildSlotTrackingMatrix(16, () => 1);
            m.BindInterlock(mask, ArmingInterlockSource.ChildSlotRejection);
            Assert.True(mask.Seal(out _));
            byte[] key = Encoding.ASCII.GetBytes("abcdefghijklmnopqrstuvwxyz012345"), iv = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP");
            var validator = new KisVtsProtocolValidator(vts: true);
            using var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onFill: m.OnFill, validator: validator, accountCano: "50012345");
            a.SetNoticeKeys(key, iv);
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] frame = Encoding.UTF8.GetBytes("1|H0STCNI9|001|" + Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(VtsNotice(117_058, 1, 1)), iv, PaddingMode.PKCS7)));
            Assert.True(VtsNoticeDecoder.IsNotice(frame));
            Assert.Equal(VtsNotice(117_058, 1, 1), VtsNoticeDecoder.Plain(frame, key, iv));    // 덤프용 복호 = 제품 판독 입력과 동일 평문
            Assert.True(a.OnFrame(frame));                                                      // 모의 체결통보 TR(H0STCNI9) 수용 · 계좌 핀 일치
            Assert.True(m.Tripped);
            Assert.Equal(0x00800000L, mask.Mask & 0x00800000L);
            Assert.Equal(1, m.UntrackedRejects);
            Assert.False(validator.Tripped);                                                    // 모의 세션의 모의 TR = 도메인 이탈 아님
        }

        // ════════════ §4 리얼 와이어(옵트인) ════════════

        [KisVtsProbeFact]
        public async Task LiveWire_Vts_Deliberate_Rejection_Captures_Raw_Payloads()
        {
            var cfg = VtsProbeConfig.Load(VtsPreFlightCredentials.DefaultDbPath, Environment.GetEnvironmentVariable(VtsProbeConfig.HtsIdVar), out var loadFailure);
            Assert.True(cfg is not null, "PaperDomestic 자격증명 판독 실패 — 주문 미송출: " + loadFailure);
            var red = new PreFlightRedactor();
            foreach (var v in new[] { cfg!.AppKey, cfg.Secret, cfg.HtsId, cfg.Cano, cfg.Cano + cfg.Prdt, cfg.Cano + "-" + cfg.Prdt }) red.Add(v);
            var log = new StringBuilder();
            void Log(string s) { s = red.Redact(s); _out.WriteLine(s); log.AppendLine(s); }
            Log("[자격증명] " + cfg + " · 출처 portfolio.db(DPAPI · PaperDomestic) · HTS ID = env " + VtsProbeConfig.HtsIdVar);
            using var http = new HttpMessageInvoker(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = false });

            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.ChildSlotRejection);
            var matrix = new ChildSlotTrackingMatrix(16);
            matrix.BindInterlock(mask, ArmingInterlockSource.ChildSlotRejection);
            Assert.True(mask.Seal(out _));
            var fills = new List<FillPayload>();
            var interlock = new KisSessionInterlock(null, 1, marketHours: () => KisSessionInterlock.IsKrxRegularSession(DateTime.UtcNow));
            KisRealtimeSession? rt = null;
            VtsCapturingTransport? cap = null;
            using var adapter = new KisOpenApiAdapter(interlock, send: f => rt!.TrySendFrame(f), onFill: f => { lock (fills) fills.Add(f); matrix.OnFill(f); },
                                                      validator: new KisVtsProtocolValidator(vts: true), accountCano: cfg.Cano);
            rt = new KisRealtimeSession(adapter, interlock, () => cap = new VtsCapturingTransport(), () => VtsRestClient.ApprovalKey(http, cfg),
                                        cfg.HtsId, vts: true, reconnectDelay: TimeSpan.FromSeconds(5));
            Assert.Equal("H0STCNI9", rt.NoticeTrId);
            using var cts = new CancellationTokenSource();
            var run = Task.Run(() => rt.RunAsync(cts.Token));
            bool restAccepted = false;
            string restRt = "", restMsgCd = "";
            try
            {
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < 300 && !adapter.HasNoticeKeys; i++) await Task.Delay(100);   // 체결통보 구독(복호 키) 확인 후 송출
                Log($"[구독] 연결 {rt.Connects} · 구독 응답 {adapter.SubscribeAcks}/거부 {adapter.SubscribeRejects} · 체결통보 키 {(adapter.HasNoticeKeys ? "장착" : "없음")} · {sw.Elapsed.TotalMilliseconds:F0}ms(접속키 발급 포함)");
                Assert.True(adapter.HasNoticeKeys, "체결통보(H0STCNI9) 구독 키 미수신 — 주문 미송출(통보 캡처 불가 상태에서 송출 안 함)");

                sw.Restart();
                string? token = await VtsRestClient.TokenAsync(http, cfg, cts.Token);
                double tokenMs = sw.Elapsed.TotalMilliseconds;
                Assert.False(string.IsNullOrEmpty(token), "VTS 접근 토큰 발급 실패 — 주문 미송출");
                red.Add(token);
                Log($"[토큰] 발급 {token!.Length}자 · {tokenMs:F1}ms(원문 미출력)");

                sw.Restart();
                var (status, body) = await VtsRestClient.PostAsync(http, VtsRejectionOrder.Build(VtsDomainGuard.VtsRestBase, cfg, token), cts.Token);
                double orderMs = sw.Elapsed.TotalMilliseconds;
                string rest = Encoding.UTF8.GetString(body);
                Log($"[단계 1 · REST 주문 응답] HTTP {status} · {orderMs:F1}ms · 원문 {rest}");
                string restMsg1 = "";
                using (var doc = JsonDocument.Parse(body))
                {
                    string Str(string n) => doc.RootElement.TryGetProperty(n, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString()! : "(없음)";
                    (restRt, restMsgCd, restMsg1) = (Str("rt_cd"), Str("msg_cd"), Str("msg1"));
                }
                Log($"[단계 1 · 필드] rt_cd '{restRt}' · msg_cd '{restMsgCd}' · msg1 '{restMsg1}'(길이 {restMsg1.Length} · 후행 공백 {restMsg1.Length - restMsg1.TrimEnd().Length}자) · '^' 구분자 {(rest.Contains('^') ? "있음" : "없음(JSON)")}");
                bool parsed = KisProtocol.TryParseOrderAck(body, out var ack);
                Log($"[단계 1 · 제품 판독기 TryParseOrderAck] 판독 {parsed} · Ok {ack.Ok} · ODNO {ack.OrderNo} · ORGNO {ack.OrgNo}");
                Assert.True(parsed, "실 VTS REST 주문 응답을 제품 판독기가 판독 못함(형식 이탈)");
                if (restRt.Trim() != "0") Assert.False(ack.Ok, "rt_cd ≠ 0 인데 판독기가 접수(Ok)로 판정");
                restAccepted = ack.Ok;
                if (restAccepted)
                {
                    var (cs, cbody) = await VtsRestClient.PostAsync(http, VtsRejectionOrder.BuildCancel(VtsDomainGuard.VtsRestBase, cfg, token, ack.OrgNo, ack.OrderNo), cts.Token);
                    Log($"[예상 밖 접수 → 즉시 취소] HTTP {cs} · {Encoding.UTF8.GetString(cbody)}");
                }
                for (int i = 0; i < 150; i++) await Task.Delay(100);                            // 비동기 체결통보 대기 15초
            }
            finally
            {
                cts.Cancel();
                try { await run; } catch (OperationCanceledException) { }
            }

            var msgs = cap?.Snapshot() ?? Array.Empty<byte[]>();
            VtsNoticeDecoder.TryKeys(msgs, out var key, out var iv);
            int notices = 0;
            foreach (var f in msgs.Where(VtsNoticeDecoder.IsNotice))
            {
                notices++;
                string raw = Encoding.ASCII.GetString(f);
                Log($"[H0STCNI9 원시 #{notices}] {(raw.Length > 120 ? raw[..120] + "…" : raw)}");
                string? plain = VtsNoticeDecoder.Plain(f, key, iv);
                Log($"[H0STCNI9 평문 #{notices}] {(plain is null ? "복호 실패" : VtsPayloadMask.MaskNotice(plain))}");
                if (plain is not null)
                {
                    var p = plain.Split('^');
                    Log($"   필드수 {p.Length} · [2]주문 {Get(p, 2)} · [4]매도매수 {Get(p, 4)} · [5]정정구분 {Get(p, 5)} · [8]종목 {Get(p, 8)} · [12]거부 {Get(p, 12)} · [13]체결 {Get(p, 13)} · [16]주문수량 {Get(p, 16)}");
                    Log($"   제품 판독 TryParseNotice {KisProtocol.TryParseNotice(Encoding.UTF8.GetBytes(plain), out var n, out _)} · Kind {n.Kind}");
                }
            }
            FillPayload[] got;
            lock (fills) got = fills.ToArray();
            int rejectedNotices = got.Count(x => BridgeFillStatusCodec.Decode(x.Status, out _) == BridgeFillStatus.Rejected);
            Log($"[단계 2 · 대조] 수신 메시지 {msgs.Length} · 체결통보 {notices} · 어댑터 통보 {got.Length}(거부 {rejectedNotices}) · " +
                $"미추적 거부 {matrix.UntrackedRejects} · 비트23 {(matrix.Tripped ? "래치" : "미래치")} · 마스크 0x{mask.Mask:X} · 형식이탈 {adapter.Malformed} · 키없음 {adapter.NoticeWithoutKey}");
            if (!restAccepted && notices == 0)
                Log($"[단계 2 · 판정] REST 동기 기각(rt_cd '{restRt}' · msg_cd '{restMsgCd}') — 주문번호 미발급 → 거래소 미전달 → H0STCNI9 미발생(정상). " +
                    "비트 23 실측 불가 · 오프라인 재생(Vts_H0STCNI9_Reject_Notice_Latches_Bit23_Through_The_Product_Path)으로만 보증.");

            string dir = Path.Combine(Path.GetTempPath(), "ms2e-vts-probe");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "probe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            File.WriteAllText(file, log.ToString(), Encoding.UTF8);
            _out.WriteLine("[덤프] " + file);

            string dump = log.ToString();
            foreach (var secret in new[] { cfg.AppKey, cfg.Secret, cfg.HtsId, cfg.Cano }) Assert.DoesNotContain(secret, dump);   // 비밀값 원문 0
            Assert.True(adapter.Malformed == 0 || notices > 0, "형식 이탈 발생 — 덤프 대조 필요");
            if (rejectedNotices > 0)
            {
                Assert.True(matrix.Tripped, "거부 통보 수신했으나 비트 23 미래치");
                Assert.Equal(0x00800000L, mask.Mask & 0x00800000L);
            }
        }

        private static string Get(string[] p, int i) => i < p.Length ? p[i] : "(없음)";
    }
}
