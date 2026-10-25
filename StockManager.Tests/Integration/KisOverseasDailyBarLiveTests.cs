// ============================================================================
// KisOverseasDailyBarLiveTests — v12.18.81-DEV: KIS 해외 일봉(HHDFS76240000) 모의투자(VTS) 실측 — 플루토 스웜 US 레인 실 응답 규격 검증
//
//   실행 조건(옵트인): MS2E_KIS_OVERSEAS_LIVE=1 + portfolio.db(KIS.Profile.PaperOverseas.* 키). 미충족 = Skip(실패 아님 · 송신 0).
//   대상 2종(AAPL = NAS · JPM = NYS) · 송신 = 토큰 1(POST /oauth2/tokenP) + 해외 일봉 2(GET) · 간격 = KisDailyBarSource 기본 페이서 1,150ms.
//   ★송출 허용 목록 핸들러: https://openapivts.koreainvestment.com:29443 만 · 토큰 POST(tr_id 없음) · dailyprice GET(tr_id HHDFS76240000 1개) 외 = 송신 전 예외.
//    실도메인 · 주문/잔고 경로 · 그 밖 TR = 패킷 0. ★liveArmed:false — 시세 GET 만.
//   검증: 원문 rt_cd "0" · output2 배열 · 행 필드(xymd · open · high · low · clos · tvol = 문자열) · 소스 Ok · 봉 ≥ 60 · 오래된 것부터 · 센트 가격 범위 · 게이트 거부/EGW00201 0.
//   출력 = 마스킹 요약(키/시크릿/토큰 원문 0). ★토큰 재발급 1분 1회(EGW00133) — 다른 해외 실측 직후면 70초 이상 대기 후 실행.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines.Cognitive.Screening;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    public static class KisOverseasDailyLiveGuard
    {
        public const string OptInVar = "MS2E_KIS_OVERSEAS_LIVE";
        public const string DailyPath = "/uapi/overseas-price/v1/quotations/dailyprice";
        public const string TrDaily = "HHDFS76240000";

        public static string? SkipReason(Func<string, string?> env, string dbPath)
        {
            if (env(OptInVar) != "1") return OptInVar + " ≠ 1(KIS 해외 일봉 VTS 실측 옵트인 없음) — 미송출";
            if (!System.IO.File.Exists(dbPath)) return "portfolio.db 없음 — 미송출";
            return null;
        }

        /// <summary>허용 목록 밖 요청 = 예외(송신 전).</summary>
        public static void EnsureAllowed(HttpRequestMessage req)
        {
            VtsDomainGuard.EnsureVtsRest(req.RequestUri!);                                   // https · 호스트 정확 일치 · 29443
            string path = req.RequestUri!.AbsolutePath;
            bool hasTr = req.Headers.TryGetValues("tr_id", out var trs);
            if (req.Method == HttpMethod.Post && path == VtsPreFlightGuard.TokenPath)
            {
                if (hasTr) throw new InvalidOperationException("해외 일봉 실측 거부: 토큰 발급에 tr_id 헤더");
                return;
            }
            if (req.Method == HttpMethod.Get && path == DailyPath && hasTr)
            {
                int n = 0;
                foreach (var t in trs!) { if (t != TrDaily) throw new InvalidOperationException("해외 일봉 실측 거부: TR ≠ " + TrDaily); n++; }
                if (n == 1) return;
            }
            throw new InvalidOperationException("해외 일봉 실측 거부: 허용 목록 밖 요청(" + req.Method + " " + path + ")");
        }

        /// <summary>가드 + 일봉 원문 포착(응답 버퍼링 → KisProvider 재판독 가능).</summary>
        public sealed class Handler : DelegatingHandler
        {
            public readonly List<string> DailyBodies = new();
            public int Sent;

            public Handler(HttpMessageHandler inner) : base(inner) { }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                EnsureAllowed(request);
                Interlocked.Increment(ref Sent);
                var res = await base.SendAsync(request, ct).ConfigureAwait(false);
                if (request.RequestUri!.AbsolutePath == DailyPath)
                {
                    await res.Content.LoadIntoBufferAsync().ConfigureAwait(false);
                    string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    lock (DailyBodies) DailyBodies.Add(body);
                }
                return res;
            }
        }
    }

    public sealed class KisOverseasDailyLiveFactAttribute : FactAttribute
    {
        public KisOverseasDailyLiveFactAttribute() => Skip = KisOverseasDailyLiveGuard.SkipReason(Environment.GetEnvironmentVariable, VtsPreFlightCredentials.DefaultDbPath);
    }

    public sealed class KisOverseasDailyBarLiveTests
    {
        private readonly ITestOutputHelper _out;
        public KisOverseasDailyBarLiveTests(ITestOutputHelper o) => _out = o;

        private const string Vts = "https://openapivts.koreainvestment.com:29443";

        private static HttpRequestMessage Req(HttpMethod m, string url, params string[] trs)
        {
            var r = new HttpRequestMessage(m, url);
            foreach (var t in trs) r.Headers.TryAddWithoutValidation("tr_id", t);
            return r;
        }

        // ── 오프라인(항상 실행 · 송신 0) ─────────────────────────────────────

        [Fact]
        public void OptIn_SkipReason_Requires_Var_And_Db()
        {
            string db = typeof(KisOverseasDailyBarLiveTests).Assembly.Location;           // 존재 파일
            Assert.NotNull(KisOverseasDailyLiveGuard.SkipReason(_ => null, db));
            Assert.NotNull(KisOverseasDailyLiveGuard.SkipReason(_ => "0", db));
            Assert.NotNull(KisOverseasDailyLiveGuard.SkipReason(k => k == KisOverseasDailyLiveGuard.OptInVar ? "1" : null, db + ".absent"));
            Assert.Null(KisOverseasDailyLiveGuard.SkipReason(k => k == KisOverseasDailyLiveGuard.OptInVar ? "1" : null, db));
            Assert.NotEqual(KisOverseasPaperGuard.OptInVar, KisOverseasDailyLiveGuard.OptInVar);   // 기존 해외 점검 옵트인과 분리
        }

        [Fact]
        public void Guard_Allows_Only_Token_And_Overseas_Daily_Get()
        {
            KisOverseasDailyLiveGuard.EnsureAllowed(Req(HttpMethod.Post, Vts + "/oauth2/tokenP"));
            KisOverseasDailyLiveGuard.EnsureAllowed(Req(HttpMethod.Get, Vts + KisOverseasDailyLiveGuard.DailyPath + "?EXCD=NAS&SYMB=AAPL", "HHDFS76240000"));
            foreach (var bad in new[]
            {
                Req(HttpMethod.Get, "https://openapi.koreainvestment.com:9443" + KisOverseasDailyLiveGuard.DailyPath, "HHDFS76240000"),   // 실도메인
                Req(HttpMethod.Get, "http://openapivts.koreainvestment.com:29443" + KisOverseasDailyLiveGuard.DailyPath, "HHDFS76240000"),
                Req(HttpMethod.Get, Vts + KisOverseasDailyLiveGuard.DailyPath),                                                               // tr_id 없음
                Req(HttpMethod.Get, Vts + KisOverseasDailyLiveGuard.DailyPath, "HHDFS76240000", "HHDFS76240000"),                             // 중복
                Req(HttpMethod.Get, Vts + KisOverseasDailyLiveGuard.DailyPath, "HHDFS00000300"),
                Req(HttpMethod.Get, Vts + "/uapi/overseas-price/v1/quotations/price", "HHDFS00000300"),
                Req(HttpMethod.Post, Vts + "/uapi/overseas-stock/v1/trading/order", "VTTT1002U"),                                            // 해외 주문
                Req(HttpMethod.Get, Vts + "/uapi/overseas-stock/v1/trading/inquire-balance", "VTTS3012R"),
                Req(HttpMethod.Post, Vts + "/oauth2/tokenP", "HHDFS76240000"),
                Req(HttpMethod.Post, Vts + KisOverseasDailyLiveGuard.DailyPath, "HHDFS76240000"),
            })
                Assert.ThrowsAny<InvalidOperationException>(() => KisOverseasDailyLiveGuard.EnsureAllowed(bad));
        }

        private sealed class NeverSend : HttpMessageHandler
        {
            public int Reached;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Interlocked.Increment(ref Reached);
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{}") });
            }
        }

        [Fact]
        public async Task Guard_Handler_Blocks_Before_Wire()
        {
            var inner = new NeverSend();
            var h = new KisOverseasDailyLiveGuard.Handler(inner);
            using var http = new HttpClient(h);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => http.SendAsync(Req(HttpMethod.Post, Vts + "/uapi/overseas-stock/v1/trading/order", "VTTT1002U")));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => http.SendAsync(Req(HttpMethod.Get, "https://openapi.koreainvestment.com:9443" + KisOverseasDailyLiveGuard.DailyPath, "HHDFS76240000")));
            Assert.Equal((0, 0), (inner.Reached, h.Sent));                         // 패킷 0
        }

        // ── 리얼 와이어(옵트인) ──────────────────────────────────────────────

        [KisOverseasDailyLiveFact]
        public void LiveWire_Vts_Overseas_Daily_Bars_Aapl_Jpm_Spec_And_Swarm_Sample()
        {
            var c = VtsPreFlightCredentials.Load(VtsPreFlightCredentials.DefaultDbPath, VtsPreFlightCredentials.OverseasProfile, out var why);
            Assert.True(c is not null, "자격증명 판독 실패: " + why);
            var guard = new KisOverseasDailyLiveGuard.Handler(VtsPreFlightGuard.CreateHandler());
            using var http = new HttpClient(guard) { Timeout = TimeSpan.FromSeconds(20) };
            var kis = new KisProvider(new KisConfig { AppKey = c!.AppKey, AppSecret = c.Secret, IsPaper = true }, http);
            var src = new KisDailyBarSource(kis, null, PlutoScreenMarket.Us);           // 기본 페이서 1,150ms
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            foreach (var (code, excd) in new[] { ("AAPL", "NAS"), ("JPM", "NYS") })
            {
                PlutoUniverseEntry? entry = null;
                foreach (var e in PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Us)) if (e.Code == code) entry = e;
                Assert.NotNull(entry);
                var sample = new PlutoAssetSample();
                int before = guard.DailyBodies.Count;
                var r = src.Fetch(entry!, sample, cts.Token);

                Assert.True(guard.DailyBodies.Count == before + 1, code + " 일봉 응답 없음(토큰 발급 실패 또는 송신 0 — EGW00133 이면 70초 후 재실행)");
                using var doc = JsonDocument.Parse(guard.DailyBodies[before]);
                var root = doc.RootElement;
                string rt = root.TryGetProperty("rt_cd", out var rtv) ? rtv.GetString() ?? "" : "";
                string msg = (root.TryGetProperty("msg_cd", out var mc) ? mc.GetString() : "") + " " + (root.TryGetProperty("msg1", out var m1) ? m1.GetString()?.Trim() : "");
                _out.WriteLine($"[{code}/{excd}] rt_cd={rt} msg={msg} outcome={r} bars={sample.BarCount}");
                Assert.True(rt == "0", code + " rt_cd ≠ 0: " + msg);
                Assert.True(root.TryGetProperty("output2", out var rows) && rows.ValueKind == JsonValueKind.Array, code + " output2 배열 아님");
                Assert.True(rows.GetArrayLength() > 0, code + " output2 빈 배열");
                foreach (var row in rows.EnumerateArray())
                    foreach (var f in new[] { "xymd", "open", "high", "low", "clos", "tvol" })
                        Assert.True(row.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String, code + " 행 필드 " + f + " 부재/비문자열");

                Assert.Equal(PlutoFetchOutcome.Ok, r);
                Assert.InRange(sample.BarCount, 60, PlutoAssetSample.MaxBars);
                for (int i = 0; i < sample.BarCount; i++)
                {
                    ref readonly var b = ref sample.Bars[i];
                    Assert.True(b.IsValid, code + " 봉 " + i + " 무효");
                    Assert.InRange(b.Close, 1_000L, 1_000_000L);                     // $10 ~ $10,000(센트)
                    if (i > 0) Assert.True(sample.Bars[i - 1].TimeUtcTicks < b.TimeUtcTicks, code + " 시간 순서 역전");
                }
                var first = new DateTime(sample.Bars[0].TimeUtcTicks);
                var last = new DateTime(sample.Bars[sample.BarCount - 1].TimeUtcTicks);
                _out.WriteLine($"[{code}/{excd}] {first:yyyy-MM-dd} ~ {last:yyyy-MM-dd} · 최근 종가 ${sample.Bars[sample.BarCount - 1].Close / 100m:0.00}");
            }
            Assert.Equal((0L, 0L), (kis.RateGated, kis.EgwRateLimits));             // 게이트 거부 · EGW00201 0
            Assert.Equal(3, guard.Sent);                                             // 토큰 1 + 일봉 2
        }
    }
}
