// ============================================================================
// PlutoPublicMacroSourceTests — v12.18.83-DEV: 플루토 공식 거시 수집원(FRED 공식 REST API · 무료 API 키) → 12대 국면 판정기 실가동
//
//   ① 키 판정: 설정 우선 · 환경 변수 폴백 · 공백 = 미설정 · 영소문자/숫자 32자 아님 = 형식 위반 · 위반 키 생성자 거부 · 대기 문구.
//   ② JSON 판독: observations 내림차순 · 결측 '.' 건너뜀 · error_code/HTML/오름차순/0 이하/숫자 아님/최신 10일 초과/관측 0 = 판독 불가(fail-closed).
//   ③ 관측 조립: 최신값 vs 1개월 전 기준값 → bp(10년물 = 절대 · 그 외 = 상대) · VIX = 최신 종가 · 이상치/창 미달 = 그 필드만 누락 · 전량 누락 = null.
//   ④ 수집원: https api.stlouisfed.org 5 시리즈 GET 만(공식 쿼리 · 인증 헤더/쿠키/본문 0) · 15분 재사용 · 429 = 잔여 요청 취소 + 예외 · 부분 실패 = 남은 필드.
//   ⑤ 실가동: 수집 → 인제스터 → 스웜 Step → 판정기 → 포트 뱃지 · 거시 피드 문구 · 3초 시간 초과 = 직전 안정값 유지 · 키 미설정 = 수집원 미생성(호스트 결선 문자열).
//   ⑥ δ=0: 공식 수집 관측 게시 후 정산 스레드 판독(판정기 TryRead + 편향) 10만 회 = 0B(MinAllocated · Release).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Adapters;
using StockManager.Engines.Cognitive.Learning;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoPublicMacroSourceTests
    {
        private const int Hike = 10;
        private const string Key = "abcdefghijklmnopqrstuvwxyz012345";
        private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Latest = new(2026, 10, 22);

        /// <summary>FRED observations 응답(내림차순 · 2026-10-22 까지 평일 n 일) — 1개월 기준일(09-22) 이하 = baseValue · 이후 = last.</summary>
        private static byte[] Json(int n, double baseValue, double last, Func<DateTime, string?>? overrideValue = null)
        {
            var sb = new StringBuilder("{\"realtime_start\":\"2026-10-23\",\"realtime_end\":\"2026-10-23\",\"units\":\"lin\",\"output_type\":1,\"file_type\":\"json\",")
                .Append("\"order_by\":\"observation_date\",\"sort_order\":\"desc\",\"count\":9000,\"offset\":0,\"limit\":30,\"observations\":[");
            int k = 0;
            for (var d = Latest; k < n; d = d.AddDays(-1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
                string v = overrideValue?.Invoke(d) ?? (d <= Latest.AddMonths(-1) ? baseValue : last).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                if (k++ > 0) sb.Append(',');
                sb.Append("{\"realtime_start\":\"2026-10-23\",\"realtime_end\":\"2026-10-23\",\"date\":\"").Append(d.ToString("yyyy-MM-dd")).Append("\",\"value\":\"").Append(v).Append("\"}");
            }
            return Encoding.UTF8.GetBytes(sb.Append("]}").ToString());
        }

        /// <summary>2022 금리인상 충격급 장세(VIX 28 · 10년물 +60bp · 달러 +2.5% · 원/달러 +4.5% · S&amp;P −9%).</summary>
        private static byte[]?[] RateShockBodies() => new byte[]?[]
        {
            Json(30, 18, 28),
            Json(30, 4.00, 4.60),
            Json(30, 120, 123),
            Json(30, 1300, 1358.5),
            Json(30, 5000, 4550),
        };

        // ── ① 키 판정 ────────────────────────────────────────────────────────

        [Fact]
        public void ResolveApiKey_Config_First_Then_Env_And_Fails_Closed_On_Format()
        {
            Assert.Equal(FredKeyState.Ok, PlutoPublicMacroSource.ResolveApiKey(" " + Key + " ", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", out var k1));
            Assert.Equal(Key, k1);
            Assert.Equal(FredKeyState.Ok, PlutoPublicMacroSource.ResolveApiKey("  ", Key, out var k2));
            Assert.Equal(Key, k2);
            Assert.Equal(FredKeyState.Missing, PlutoPublicMacroSource.ResolveApiKey(null, null, out var k3));
            Assert.Null(k3);
            Assert.Equal(FredKeyState.Missing, PlutoPublicMacroSource.ResolveApiKey("", " ", out _));
            foreach (var bad in new[] { Key[..31], Key + "6", Key.ToUpperInvariant(), Key[..30] + "&x", Key[..31] + "=" })
            {
                Assert.Equal(FredKeyState.Malformed, PlutoPublicMacroSource.ResolveApiKey(bad, null, out var k));
                Assert.Null(k);                                                              // 형식 위반 키 = URL 주입 0
                Assert.Throws<ArgumentException>(() => new PlutoPublicMacroSource(bad));
            }
            Assert.Equal("거시 피드: FRED API 키 미설정 · 관측 대기", PlutoPublicMacroSource.FeedText(FredKeyState.Missing));
            Assert.Equal("거시 피드: FRED API 키 형식 오류 · 관측 대기", PlutoPublicMacroSource.FeedText(FredKeyState.Malformed));
            Assert.StartsWith("거시 피드: FRED 공식 API", PlutoPublicMacroSource.FeedText(FredKeyState.Ok));
            Assert.Equal("MS2E_FRED_API_KEY", PlutoPublicMacroSource.ApiKeyEnvVar);
            Assert.Equal("Pluto.FredApiKey", PlutoPublicMacroSource.ApiKeyConfigKey);
        }

        // ── ② JSON 판독 ──────────────────────────────────────────────────────

        [Fact]
        public void TryParseObservations_Reads_Descending_And_Skips_Missing_Dots()
        {
            byte[] j = Encoding.UTF8.GetBytes("{\"count\":3,\"observations\":[{\"date\":\"2026-10-22\",\"value\":\"19.03\"},{\"date\":\"2026-10-21\",\"value\":\".\"},{\"realtime_start\":\"x\",\"date\":\"2026-10-20\",\"value\":\"16.37\",\"extra\":{\"a\":[1]}}],\"tail\":1}");
            Assert.True(PlutoPublicMacroSource.TryParseObservations(j, Now, out var obs));
            Assert.Equal(new[] { (Latest, 19.03), (new DateTime(2026, 10, 20), 16.37) }, obs);
        }

        [Theory]
        [InlineData("{\"error_code\":400,\"error_message\":\"Bad Request.  The value for variable api_key is not registered.\"}")]
        [InlineData("<!DOCTYPE html><html><body>Error</body></html>")]
        [InlineData("{\"observations\":[{\"date\":\"2026-10-21\",\"value\":\"18\"},{\"date\":\"2026-10-22\",\"value\":\"19\"}]}")]   // 오름차순
        [InlineData("{\"observations\":[{\"date\":\"2026-10-22\",\"value\":\"19\"},{\"date\":\"2026-10-22\",\"value\":\"19\"}]}")]   // 중복 날짜
        [InlineData("{\"observations\":[{\"date\":\"2026-10-22\",\"value\":\"-1\"}]}")]
        [InlineData("{\"observations\":[{\"date\":\"2026-10-22\",\"value\":\"abc\"}]}")]
        [InlineData("{\"observations\":[{\"date\":\"2026-10-22\",\"value\":19}]}")]                                                  // 숫자 타입(문자열 아님)
        [InlineData("{\"observations\":[{\"date\":\"10/22/2026\",\"value\":\"19\"}]}")]
        [InlineData("{\"observations\":[{\"value\":\"19\"}]}")]                                                                      // 날짜 누락
        [InlineData("{\"observations\":[{\"date\":\"2026-10-01\",\"value\":\"19\"}]}")]                                               // 최신 관측 10일 초과
        [InlineData("{\"observations\":[{\"date\":\"2026-10-22\",\"value\":\".\"}]}")]                                                // 값 0개
        [InlineData("{\"observations\":[]}")]
        [InlineData("{\"count\":0}")]                                                                                                 // 배열 부재
        [InlineData("[]")]
        [InlineData("{\"observations\":[{\"date\":\"2026-10-22\",\"value\":\"19\"}")]                                                 // 잘림
        [InlineData("")]
        public void TryParseObservations_Fails_Closed(string json)
        {
            Assert.False(PlutoPublicMacroSource.TryParseObservations(Encoding.UTF8.GetBytes(json), Now, out var obs));
            Assert.Empty(obs);
        }

        // ── ③ 관측 조립 ──────────────────────────────────────────────────────

        [Fact]
        public void BuildObservation_Computes_One_Month_Changes()
        {
            byte[]? json = PlutoPublicMacroSource.BuildObservation(RateShockBodies(), Now, out var fields);
            Assert.NotNull(json);
            Assert.Equal(PlutoMacroField.All & ~PlutoMacroField.Sentiment, fields);
            Assert.True(PlutoGlobalDataIngestor.TryParse(json, 1, out var s));
            Assert.Equal(2800, s.VixX100);
            Assert.Equal(60, s.Us10yChangeBp);
            Assert.Equal(250, s.DollarIndexChangeBp);
            Assert.Equal(450, s.UsdKrwChangeBp);
            Assert.Equal(-900, s.EquityTrendBp);
            Assert.False(s.Has(PlutoMacroField.Sentiment));                                  // 감성 = 미제공(날조 0)
            Assert.Equal(Now.Ticks, s.AsOfUtcTicks);                                         // 기준 시각 = 수집 시각
        }

        [Fact]
        public void BuildObservation_Base_Is_Latest_Observation_At_Or_Before_One_Month()
        {
            // 기준일 09-22(화) = 4000 · 그 이전 = 1 · 09-23 이후 = 4200 · 최신 = 4400 → +10% · 기준일 결측 '.' = 직전 영업일(09-21) 값
            var b = RateShockBodies();
            b[4] = Json(30, 0, 0, d => d == Latest ? "4400" : d == new DateTime(2026, 9, 22) ? "4000" : d < new DateTime(2026, 9, 22) ? "1" : "4200");
            Assert.True(PlutoGlobalDataIngestor.TryParse(PlutoPublicMacroSource.BuildObservation(b, Now, out _), 1, out var s));
            Assert.Equal(1000, s.EquityTrendBp);
            b[4] = Json(30, 0, 0, d => d == Latest ? "4400" : d == new DateTime(2026, 9, 22) ? "." : d == new DateTime(2026, 9, 21) ? "3520" : d < new DateTime(2026, 9, 21) ? "1" : "4200");
            Assert.True(PlutoGlobalDataIngestor.TryParse(PlutoPublicMacroSource.BuildObservation(b, Now, out _), 1, out s));
            Assert.Equal(2500, s.EquityTrendBp);
        }

        [Fact]
        public void BuildObservation_Drops_Only_Anomalous_Or_Short_Fields()
        {
            var b = RateShockBodies();
            b[1] = Json(30, 1.0, 80.0);                                                      // +7,900bp = 이상치
            b[2] = Json(15, 120, 123);                                                       // 15 영업일 = 1개월 창 미달
            b[3] = null;                                                                     // 수신 실패
            b[0] = Json(1, 0, 250);                                                          // VIX 200 초과
            Assert.True(PlutoGlobalDataIngestor.TryParse(PlutoPublicMacroSource.BuildObservation(b, Now, out var fields), 1, out var s));
            Assert.Equal(PlutoMacroField.EquityTrend, fields);
            Assert.Equal(PlutoMacroField.EquityTrend, s.Fields);

            Assert.Null(PlutoPublicMacroSource.BuildObservation(new byte[]?[5], Now, out var none));
            Assert.Equal(PlutoMacroField.None, none);
            Assert.Throws<ArgumentException>(() => PlutoPublicMacroSource.BuildObservation(new byte[]?[4], Now, out _));
        }

        // ── ④ 수집원 ─────────────────────────────────────────────────────────

        private sealed class FredStub : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Requests = new();
            public int Canceled;
            public Func<string, CancellationToken, Task<HttpResponseMessage>> Respond =
                (id, _) => Task.FromResult(Ok(RateShockBodies()[Array.FindIndex(PlutoPublicMacroSource.Series, s => s.Id == id)]!));
            public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

            public static string IdOf(Uri u) => u.Query.Split('&')[0]["?series_id=".Length..];

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                lock (Requests) Requests.Add(request);
                try { return await Respond(IdOf(request.RequestUri!), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { Interlocked.Increment(ref Canceled); throw; }
            }
        }

        [Fact]
        public async Task FetchAsync_Sends_Five_Official_Gets_Only_And_Reuses_For_15_Minutes()
        {
            var stub = new FredStub();
            var now = Now;
            using var src = new PlutoPublicMacroSource(Key, stub, () => now);
            Assert.Equal("FRED 공식 API(일별)", src.Name);
            Assert.DoesNotContain(Key, src.Name);
            byte[] first = await src.FetchAsync(CancellationToken.None);
            Assert.Equal(5, stub.Requests.Count);
            foreach (var r in stub.Requests)
            {
                Assert.Equal(HttpMethod.Get, r.Method);
                Assert.Equal("https", r.RequestUri!.Scheme);
                Assert.Equal("api.stlouisfed.org", r.RequestUri.Host);
                Assert.Equal("/fred/series/observations", r.RequestUri.AbsolutePath);
                Assert.Equal($"?series_id={FredStub.IdOf(r.RequestUri)}&api_key={Key}&file_type=json&sort_order=desc&limit=30", r.RequestUri.Query);
                Assert.Null(r.Content);
                Assert.Null(r.Headers.Authorization);
                Assert.False(r.Headers.Contains("Cookie"));
            }
            Assert.Equal(PlutoPublicMacroSource.Series.Select(s => s.Id).OrderBy(x => x), stub.Requests.Select(r => FredStub.IdOf(r.RequestUri!)).OrderBy(x => x));
            Assert.Equal(PlutoMacroField.All & ~PlutoMacroField.Sentiment, src.LastFields);
            Assert.DoesNotContain(Key, Encoding.UTF8.GetString(first));                      // 관측 JSON 에 키 유출 0

            now = Now.AddMinutes(14);
            Assert.Same(first, await src.FetchAsync(CancellationToken.None));                // 재사용(송신 0)
            Assert.Equal(5, stub.Requests.Count);
            now = Now.AddMinutes(15);
            await src.FetchAsync(CancellationToken.None);
            Assert.Equal(10, stub.Requests.Count);
        }

        [Fact]
        public async Task FetchAsync_429_Cancels_Remaining_Requests_And_Throws_Without_Retry()
        {
            var stub = new FredStub();
            stub.Respond = async (id, ct) =>
            {
                if (id == "DGS10") return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return FredStub.Ok(RateShockBodies()[0]!);
            };
            using var src = new PlutoPublicMacroSource(Key, stub, () => Now);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => src.FetchAsync(CancellationToken.None));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2));                               // 즉시 중단(3초 시간 제한 대기 0)
            Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
            Assert.DoesNotContain(Key, ex.Message);
            Assert.Equal(5, stub.Requests.Count);                                            // 재시도 0
            Assert.Equal(4, stub.Canceled);                                                  // 잔여 4 요청 취소
            Assert.Equal(PlutoMacroField.None, src.LastFields);
        }

        [Fact]
        public async Task FetchAsync_Total_Failure_Throws_Without_Stale_Cache()
        {
            var stub = new FredStub();
            var now = Now;
            using var src = new PlutoPublicMacroSource(Key, stub, () => now);
            await src.FetchAsync(CancellationToken.None);
            now = Now.AddMinutes(20);
            stub.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
                { Content = new StringContent("{\"error_code\":400,\"error_message\":\"Bad Request.\"}") });
            await Assert.ThrowsAsync<InvalidOperationException>(() => src.FetchAsync(CancellationToken.None));   // 15분 지난 캐시 반환 0
            Assert.Equal(PlutoMacroField.None, src.LastFields);
            stub.Respond = (_, _) => Task.FromResult(FredStub.Ok(Encoding.UTF8.GetBytes("{\"error_code\":500}")));
            await Assert.ThrowsAsync<InvalidOperationException>(() => src.FetchAsync(CancellationToken.None));
        }

        [Fact]
        public async Task FetchAsync_Partial_Failure_Keeps_Remaining_Fields()
        {
            var stub = new FredStub();
            var ok = stub.Respond;
            stub.Respond = (id, ct) => id == "DEXKOUS" ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)) : ok(id, ct);
            using var src = new PlutoPublicMacroSource(Key, stub, () => Now);
            Assert.True(PlutoGlobalDataIngestor.TryParse(await src.FetchAsync(CancellationToken.None), 1, out var s));
            Assert.False(s.Has(PlutoMacroField.UsdKrw));
            Assert.Equal(PlutoMacroField.Vix | PlutoMacroField.Us10y | PlutoMacroField.Dollar | PlutoMacroField.EquityTrend, s.Fields);
        }

        // ── ⑤ 실가동 ─────────────────────────────────────────────────────────

        [Fact]
        public async Task Official_Feed_Activates_Regime_Matcher_And_Port_Badge()
        {
            var matcher = new PlutoRegimeMatcher();
            var mem = new PlutoEpisodicMemoryStore();
            mem.SeedDefaults();
            var ingestor = new PlutoGlobalDataIngestor(() => Now.Ticks);
            var swarm = new PlutoContinuousLearningSwarm(ingestor, mem, new PlutoWeightCalibrator(), new PlutoCognitiveWeightSeqlock(), () => Now.Ticks, matcher);
            string feed = PlutoPublicMacroSource.FeedText(FredKeyState.Ok);
            var idle = PlutoLearningPortAdapter.Map(swarm.Step(), running: true, feed);
            Assert.Equal("[🧠 과거 국면 매칭: 판정 전]", idle.RegimeBadgeText);
            Assert.Equal(feed, idle.MacroFeedText);

            using var src = new PlutoPublicMacroSource(Key, new FredStub(), () => Now);
            Assert.True(ingestor.Ingest(await src.FetchAsync(CancellationToken.None)));
            Assert.Equal(PlutoIngestHealth.Live, ingestor.Health);
            var st = swarm.Step();
            Assert.Equal(Hike, st.Regime.CycleId);
            Assert.True(st.Regime.SimilarityX10 >= 990);                                     // 5차원 자기 매칭급
            var view = PlutoLearningPortAdapter.Map(st, running: true, feed);
            Assert.True(view.HasRegime);
            Assert.Equal(feed, view.MacroFeedText);
            Assert.StartsWith("[🧠 과거 국면 매칭: 2022 연준 금리인상 충격 (유사도 ", view.RegimeBadgeText);
            Assert.True(matcher.TryRead(out var live));
            Assert.Equal(Hike, live.CycleId);
        }

        [Fact]
        public void Missing_Key_Port_Renders_Honest_Waiting_State()
        {
            var ingestor = new PlutoGlobalDataIngestor(() => Now.Ticks);
            var mem = new PlutoEpisodicMemoryStore();
            mem.SeedDefaults();
            var swarm = new PlutoContinuousLearningSwarm(ingestor, mem, new PlutoWeightCalibrator(), new PlutoCognitiveWeightSeqlock(), () => Now.Ticks, new PlutoRegimeMatcher());
            var port = new PlutoLearningPortAdapter(swarm, PlutoPublicMacroSource.FeedText(FredKeyState.Missing));
            Assert.Equal("거시 피드: FRED API 키 미설정 · 관측 대기", port.Current.MacroFeedText);
            swarm.Step();
            var v = port.Current;
            Assert.Equal("거시 피드: FRED API 키 미설정 · 관측 대기", v.MacroFeedText);
            Assert.Equal(MS2E.UI.Portable.Ports.Trading.PlutoLearningHealth.NoData, v.Health);   // 가짜 관측 0
            Assert.False(v.HasRegime);
            Assert.Equal(0, ingestor.Polls);
            Assert.Null(new PlutoLearningPortAdapter(swarm).Current.MacroFeedText);
        }

        private sealed class HangingSource : IPlutoMacroSource
        {
            public int Calls;
            public string Name => "hang";
            public async Task<byte[]> FetchAsync(CancellationToken ct)
            {
                Interlocked.Increment(ref Calls);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return Array.Empty<byte>();
            }
        }

        [Fact]
        public async Task Timeout_Counts_Failure_And_Keeps_Last_Stable_Value()
        {
            var ingestor = new PlutoGlobalDataIngestor(() => Now.Ticks);
            Assert.True(ingestor.Ingest(PlutoPublicMacroSource.BuildObservation(RateShockBodies(), Now, out _)));
            var src = new HangingSource();
            using var cts = new CancellationTokenSource();
            var run = ingestor.RunAsync(src, TimeSpan.FromMinutes(10), cts.Token, TimeSpan.FromMilliseconds(200));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (ingestor.SourceFailures == 0 && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            cts.Cancel();
            await run;
            Assert.Equal(1, ingestor.SourceFailures);                                        // 시간 초과 1회 = 장애 · 재시도 0(다음 주기 = 10분 뒤)
            Assert.Equal(1, src.Calls);
            Assert.True(ingestor.TryRead(out var s));
            Assert.Equal(60, s.Us10yChangeBp);                                               // 직전 안정값 유지
            Assert.Equal(TimeSpan.FromSeconds(3), PlutoPublicMacroSource.FetchTimeout);
            Assert.Equal(TimeSpan.FromSeconds(60), PlutoPublicMacroSource.PollInterval);
            Assert.Equal(TimeSpan.FromMinutes(30), PlutoGlobalDataIngestor.DefaultStaleAfter);
        }

        [Fact]
        public void Host_Wires_Official_Source_Only_With_Valid_Key()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string form = System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("else if (cfg.GetBool(\"Pluto.PublicMacroEnabled\", true))", form);
            Assert.Contains("PlutoPublicMacroSource.ResolveApiKey(cfg.GetString(StockManager.Adapters.PlutoPublicMacroSource.ApiKeyConfigKey, \"\"),", form);
            Assert.Contains("Environment.GetEnvironmentVariable(StockManager.Adapters.PlutoPublicMacroSource.ApiKeyEnvVar), out var fredKey);", form);
            Assert.Contains("if (keyState == StockManager.Adapters.FredKeyState.Ok)", form);
            Assert.Contains("new StockManager.Adapters.PlutoPublicMacroSource(fredKey!)", form);
            Assert.Contains("plutoIngestor.RunAsync(publicMacro, StockManager.Adapters.PlutoPublicMacroSource.PollInterval, macroCts.Token,", form);
            Assert.Contains("StockManager.Adapters.PlutoPublicMacroSource.FetchTimeout));", form);
            Assert.Contains("new StockManager.Adapters.PlutoLearningPortAdapter(plutoSwarm, macroFeedText)", form);
            Assert.Equal(1, form.Split("new StockManager.Adapters.PlutoPublicMacroSource(").Length - 1);   // 키 검증 분기 밖 생성 0
            string src = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "StockManager", "Adapters", "PlutoPublicMacroSource.cs"));
            Assert.DoesNotContain("QuantumVault", src.Split("// ====")[^1]);                // 코드 본문 피어 참조 0
            Assert.DoesNotContain("PostAsync", src);
            Assert.DoesNotContain("SendAsync", src);
            string razor = System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor"));
            Assert.Contains("@lv.MacroFeedText", razor);
        }

        /// <summary>옵트인 실측(MS2E_PUBLIC_MACRO_LIVE=1 + 유효 MS2E_FRED_API_KEY 일 때만 · 공식 GET 5회 · 3초 시간 제한) — 실 응답 → 국면 판정 '판정 전' 해소.</summary>
        [Fact]
        public async Task Live_Fred_Feed_Resolves_Regime_OptIn()
        {
            if (Environment.GetEnvironmentVariable("MS2E_PUBLIC_MACRO_LIVE") != "1") return;
            if (PlutoPublicMacroSource.ResolveApiKey(null, Environment.GetEnvironmentVariable(PlutoPublicMacroSource.ApiKeyEnvVar), out var key) != FredKeyState.Ok) return;
            using var src = new PlutoPublicMacroSource(key!);
            using var cts = new CancellationTokenSource(PlutoPublicMacroSource.FetchTimeout);
            byte[] json = await src.FetchAsync(cts.Token);
            Assert.True(PlutoGlobalDataIngestor.TryParse(json, DateTime.UtcNow.Ticks, out var snap));
            Assert.True(snap.Has(PlutoMacroField.Vix));
            var m = new PlutoRegimeMatcher().Publish(in snap);
            Assert.True(m.Found);
            Console.WriteLine($"[live-macro] {Encoding.UTF8.GetString(json)} → {PlutoRegimeText.Badge(in m)}");
        }

        // ── ⑥ δ=0 ────────────────────────────────────────────────────────────

        [Fact]
        public void Settlement_Read_After_Official_Ingestion_Is_Zero_Alloc()
        {
            var matcher = new PlutoRegimeMatcher();
            Assert.True(PlutoGlobalDataIngestor.TryParse(PlutoPublicMacroSource.BuildObservation(RateShockBodies(), Now, out _), 1, out var snap));
            Assert.Equal(Hike, matcher.Publish(in snap).CycleId);
            var weights = new PlutoCognitiveWeightSeqlock();
            long acc = 0;
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    weights.TryRead(out var w);
                    matcher.TryRead(out var h);
                    var learned = h.Bias(in w);
                    acc += h.CycleId + learned.DevilStrictnessPts;
                }
            }
            Reads(10_000);
            Assert.Equal(0, MinAllocated(() => Reads(100_000)));
            Assert.True(acc > 0);
        }
    }
}
