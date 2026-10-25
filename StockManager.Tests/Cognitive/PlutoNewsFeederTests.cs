// ============================================================================
// PlutoNewsFeederTests — v12.18.64-DEV: 플루토 브레인 2단계(DART/SEC 수집 피더 · IVirtualClock 동기화 · 촉매 UI 배지)
//
// §1 가상 시계 — 시간 가속 감쇄(10/30/60분 경과 · 유형별 반감기) · 틱 동기 단조 래치/역행 무시/Reset 복귀 · 분석기 미래/과거 필터의 시계 도메인 ·
//               합성 시세 주입기 → 시계 동기(출처 전환 = Reset) · 리플레이 결정론(같은 시나리오 · 같은 결과) · 시계 판독/태그 판독 δ=0
// §2 E2E — DART list.json 모의 수신(기준선 · 신규 전달 · 중복 제거 · 013/오류 상태) → 분석 → 버퍼 · SEC Atom(Item 4.02 → restatement · CIK 매핑) ·
//          골든 시나리오(공급계약 · 유상증자 · 시황 노이즈 · 감사의견 거절 · 사상 최대 매출 · 리콜) 시간 순 주입
// §3 Fail-Closed 격리 — 연결 실패 · 타임아웃 · HTTP 500 · 비정상/과대 페이로드 · DTD · 싱크 예외 → 무예외 · 계수 · 백오프 · 엔진/분석기 무중단
// §4 UI 배지 — 포트 Catalyst 투영(호재/긴급 악재/악재/노이즈 생략) · DualArenaPage 배지 렌더(녹색 + 반감기 막대 · 점멸 적색 · 툴팁)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.News;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoNewsFeederTests
    {
        private const long Samsung = 5930, Hynix = 660, Hyundai = 5380, Lges = 373220, SamsungBio = 207940;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);            // 목 10:00 KST
        private static long Utc(DateTime kst) => kst.Ticks - PlutoClock.KstOffsetTicks;
        private static readonly long Nvda = UsTechUniverse.Pack("NVDA"), Tsla = UsTechUniverse.Pack("TSLA"), Aapl = UsTechUniverse.Pack("AAPL");
        private readonly ITestOutputHelper _out;

        public PlutoNewsFeederTests(ITestOutputHelper o) => _out = o;

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _f;
            public int Calls;
            public string? LastUri;
            public string? LastUserAgent;

            public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> f) => _f = f;

            public static StubHandler Body(Func<int, string> body, string mediaType = "application/json")
                => new((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(0), Encoding.UTF8, mediaType) }));

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref Calls);
                LastUri = request.RequestUri?.ToString();
                LastUserAgent = request.Headers.TryGetValues("User-Agent", out var ua) ? string.Join(" ", ua) : null;
                return _f(request, cancellationToken);
            }
        }

        private static HttpResponseMessage Ok(string body, string mediaType = "application/json")
            => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

        private static string DartJson(params (string Corp, string Code, string Report, string Rcept)[] rows)
        {
            var sb = new StringBuilder("{\"status\":\"000\",\"message\":\"정상\",\"page_no\":1,\"page_count\":100,\"total_count\":" + rows.Length + ",\"list\":[");
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var r = rows[i];
                sb.Append("{\"corp_code\":\"00164779\",\"corp_name\":\"").Append(r.Corp).Append("\",\"stock_code\":\"").Append(r.Code)
                  .Append("\",\"corp_cls\":\"Y\",\"report_nm\":\"").Append(r.Report).Append("\",\"rcept_no\":\"").Append(r.Rcept)
                  .Append("\",\"flr_nm\":\"").Append(r.Corp).Append("\",\"rcept_dt\":\"20261022\",\"rm\":\"\"}");
            }
            return sb.Append("]}").ToString();
        }

        private static string SecAtom(params (string Title, string UpdatedIso, string SummaryHtmlEscaped, string Id)[] entries)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"ISO-8859-1\" ?>\n<feed xmlns=\"http://www.w3.org/2005/Atom\"><title>Latest Filings</title><updated>2026-10-22T10:00:00-04:00</updated>");
            foreach (var e in entries)
                sb.Append("<entry><title>").Append(e.Title).Append("</title><link rel=\"alternate\" type=\"text/html\" href=\"https://www.sec.gov/x\"/><summary type=\"html\">")
                  .Append(e.SummaryHtmlEscaped).Append("</summary><updated>").Append(e.UpdatedIso).Append("</updated><category scheme=\"https://www.sec.gov/\" label=\"form type\" term=\"8-K\"/><id>")
                  .Append(e.Id).Append("</id></entry>");
            return sb.Append("</feed>").ToString();
        }

        // Processed 는 Ingest 진입 시 증가 — 게시 완료까지 기다리려면 종결 계수(게시 · 노이즈 · 기각 · 예외) 합으로 판정한다.
        private static long Completed(PlutoCatalystAnalyzer a) => a.PublishedItems + a.NoiseItems + a.RejectedItems + a.Faulted;

        private static void WaitProcessed(PlutoCatalystAnalyzer a, long n)
            => Assert.True(SpinWait.SpinUntil(() => Completed(a) >= n, 5000), "분석기 워커 처리 대기 초과(completed=" + Completed(a) + ")");

        // ── §1 가상 시계 ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData("[삼성전자] 목표주가 상향", PlutoSource.Wire, Samsung, CatalystCategory.MomentumPositive, 35, 10)]
        [InlineData("[삼성전자] 목표주가 상향", PlutoSource.Wire, Samsung, CatalystCategory.MomentumPositive, 35, 30)]
        [InlineData("[삼성전자] 목표주가 상향", PlutoSource.Wire, Samsung, CatalystCategory.MomentumPositive, 35, 60)]
        [InlineData("[SK하이닉스] 단일판매ㆍ공급계약체결", PlutoSource.Dart, Hynix, CatalystCategory.StructuralPositive, 75, 10)]
        [InlineData("[SK하이닉스] 단일판매ㆍ공급계약체결", PlutoSource.Dart, Hynix, CatalystCategory.StructuralPositive, 75, 30)]
        [InlineData("[SK하이닉스] 단일판매ㆍ공급계약체결", PlutoSource.Dart, Hynix, CatalystCategory.StructuralPositive, 75, 60)]
        [InlineData("[현대차] 주요사항보고서(유상증자결정)", PlutoSource.Dart, Hyundai, CatalystCategory.StructuralNegative, -80, 10)]
        [InlineData("[현대차] 주요사항보고서(유상증자결정)", PlutoSource.Dart, Hyundai, CatalystCategory.StructuralNegative, -80, 30)]
        [InlineData("[현대차] 주요사항보고서(유상증자결정)", PlutoSource.Dart, Hyundai, CatalystCategory.StructuralNegative, -80, 60)]
        public void Accelerated_Virtual_Clock_Decay_Matches_Half_Life_Formula(string headline, PlutoSource src, long symbol, CatalystCategory cat, int score, int minutes)
        {
            long t0 = Utc(Kst10);
            var clock = new ManualVirtualClock(t0);
            var buf = new PlutoSentimentBuffer();
            var a = new PlutoCatalystAnalyzer(buf, clock);
            var r = a.Ingest(new PlutoNewsItem(src, headline, t0));
            Assert.Equal(PlutoIngestStatus.Published, r.Status);
            Assert.Equal(cat, r.Verdict.Category);
            Assert.Equal(score, r.Verdict.Score);

            clock.Advance(TimeSpan.FromMinutes(minutes));                 // 벽시계 대기 0 — 가상 시간만 가속
            Assert.True(buf.TryReadTag(symbol, clock, out var tag));
            int expected = (int)(score * Math.Pow(2.0, -(double)minutes / CatalystText.HalfLifeMinutes(cat)));
            Assert.Equal(expected, tag.DecayedScore);
            Assert.Equal(expected * r.Verdict.Confidence / 100, tag.EffectiveScore);
            Assert.Equal(minutes, tag.AgeMinutes);
            _out.WriteLine($"{cat} {score} @+{minutes}m → decayed {tag.DecayedScore} · effective {tag.EffectiveScore}");
        }

        [Fact]
        public void Accelerated_Decay_Exact_Values_At_10_30_60_Minutes()
        {
            long t0 = Utc(Kst10);
            var clock = new ManualVirtualClock(t0);
            var buf = new PlutoSentimentBuffer();
            buf.Publish(new PlutoSentimentFactor(Samsung, t0, 40, 100, CatalystCategory.MomentumPositive, PlutoSource.Wire, "m"u8), clock);
            buf.Publish(new PlutoSentimentFactor(Hynix, t0, 80, 100, CatalystCategory.StructuralPositive, PlutoSource.Dart, "s"u8), clock);
            buf.Publish(new PlutoSentimentFactor(Lges, t0, -100, 100, CatalystCategory.StructuralNegative, PlutoSource.Dart, "n"u8), clock);
            int[] mins = { 10, 30, 60 };
            int[,] want =
            {
                { 20, 5, 0 },          // 모멘텀(반감기 10분): 40 → 20 · 5 · 40·2^-6 = 0.625 → 0(0 방향 절단)
                { 63, 40, 20 },        // 구조적 호재(30분): 80·2^-1/3 = 63.49 → 63 · 40 · 20
                { -89, -70, -50 },     // 구조적 악재(60분): −100·2^-1/6 = −89.09 → −89 · −70.71 → −70 · −50
            };
            long prev = t0;
            for (int i = 0; i < mins.Length; i++)
            {
                clock.Advance(TimeSpan.FromTicks(t0 + mins[i] * TimeSpan.TicksPerMinute - prev));
                prev = clock.UtcNowTicks;
                Assert.True(buf.TryReadTag(Samsung, clock, out var m));
                Assert.True(buf.TryReadTag(Hynix, clock, out var s));
                Assert.True(buf.TryReadTag(Lges, clock, out var n));
                Assert.Equal(want[0, i], m.DecayedScore);
                Assert.Equal(want[1, i], s.DecayedScore);
                Assert.Equal(want[2, i], n.DecayedScore);
                Assert.Equal(n.DecayedScore <= PlutoNewsTag.ShockScore, n.IsShock);
            }
        }

        [Fact]
        public void Tick_Synced_Clock_Latches_Monotonic_Ignores_Regression_And_Resets_To_Fallback()
        {
            long wall = Utc(Kst10);
            var fallback = new ManualVirtualClock(wall);
            var c = new TickSyncedVirtualClock(fallback);
            Assert.False(c.IsSynthetic);
            Assert.Equal(wall, c.UtcNowTicks);

            long t = wall - TimeSpan.FromHours(3).Ticks;              // 합성 도메인이 벽시계보다 과거여도 동기 후엔 틱 시각
            Assert.True(c.Observe(t));
            Assert.True(c.IsSynthetic);
            Assert.Equal(t, c.UtcNowTicks);
            Assert.True(c.Observe(t + 10));
            Assert.False(c.Observe(t + 5));                           // 역행 무시
            Assert.False(c.Observe(0));
            Assert.False(c.Observe(-1));
            Assert.False(c.Observe(long.MaxValue));
            Assert.Equal(t + 10, c.UtcNowTicks);
            Assert.Equal(2, c.Observed);
            Assert.Equal(4, c.Ignored);

            fallback.Advance(TimeSpan.FromMinutes(1));
            c.Reset();
            Assert.False(c.IsSynthetic);
            Assert.Equal(wall + TimeSpan.TicksPerMinute, c.UtcNowTicks);
            Assert.True(c.Observe(t));                                // Reset 후 과거 도메인 재동기 허용
            Assert.Equal(t, c.UtcNowTicks);
            Assert.Throws<ArgumentOutOfRangeException>(() => fallback.Advance(TimeSpan.FromTicks(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => fallback.Set(wall));
            Assert.Throws<ArgumentNullException>(() => new PlutoCatalystAnalyzer(new PlutoSentimentBuffer(), (IVirtualClock)null!));
            Assert.False(SystemVirtualClock.Instance.IsSynthetic);
            Assert.InRange(SystemVirtualClock.Instance.UtcNowTicks - DateTime.UtcNow.Ticks, -TimeSpan.TicksPerSecond, TimeSpan.TicksPerSecond);
        }

        [Fact]
        public void Analyzer_Future_And_Stale_Filters_Follow_The_Injected_Clock_Domain()
        {
            long t0 = Utc(Kst10);
            var clock = new ManualVirtualClock(t0);
            var buf = new PlutoSentimentBuffer();
            var a = new PlutoCatalystAnalyzer(buf, clock);
            Assert.Same(clock, a.Clock);
            var future = new PlutoNewsItem(PlutoSource.Dart, "[삼성전자] 주요사항보고서(유상증자결정)", t0 + TimeSpan.FromMinutes(10).Ticks);
            Assert.Equal(PlutoIngestStatus.FutureTimestamp, a.Ingest(future).Status);   // 가상 '지금' 기준 10분 미래 = 기각
            clock.Advance(TimeSpan.FromMinutes(6));
            Assert.Equal(PlutoIngestStatus.Published, a.Ingest(future).Status);         // 미래 4분 = 허용 오차 안
            Assert.True(buf.TryReadTag(Samsung, clock, out var tag));
            Assert.Equal(-80, tag.DecayedScore);                                         // 발생 전(오차 안) = 감쇄 0
            clock.Advance(TimeSpan.FromHours(25));
            Assert.Equal(PlutoIngestStatus.Stale, a.Ingest(new PlutoNewsItem(PlutoSource.Dart, "[SK하이닉스] 유상증자결정", t0)).Status);
            Assert.True(buf.TryReadTag(Samsung, clock, out var late));
            Assert.Equal(0, late.DecayedScore);                                          // 24 반감기 경과 = 0
        }

        [Fact]
        public void Synthetic_Quote_Feeders_Drive_The_Clock_And_Source_Switch_Resets_It()
        {
            var clock = new TickSyncedVirtualClock();
            var krx = new KrxArenaEngine();
            var kf = new KrxArenaQuoteFeeder(krx);
            kf.AttachClock(clock);
            long wall = new DateTime(2026, 10, 22, 12, 0, 0).Ticks;
            Assert.True(kf.SelectSource(KrxArenaFeed.Synthetic));
            for (int i = 0; i < 40; i++) kf.Pump(wall + i * 50 * TimeSpan.TicksPerMillisecond);
            Assert.True(clock.IsSynthetic);
            Assert.Equal(kf.SyntheticKstTicks - KrxArenaQuoteFeeder.SyntheticStepTicks - PlutoClock.KstOffsetTicks, clock.UtcNowTicks);
            Assert.True(krx.TryReadSnapshot(out var s));
            Assert.Equal(s.LastKstTicks - PlutoClock.KstOffsetTicks, clock.UtcNowTicks);  // 시계 = 엔진이 본 마지막 정산 시각

            Assert.True(kf.SelectSource(KrxArenaFeed.Bridge));
            kf.Pump(wall + TimeSpan.TicksPerMinute);
            Assert.False(clock.IsSynthetic);                                              // 실시세 복귀 = 벽시계

            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            uf.AttachClock(clock);
            Assert.True(uf.SelectSource(UsArenaFeed.Synthetic));
            for (int i = 0; i < 40; i++) uf.Pump(wall + i * 50 * TimeSpan.TicksPerMillisecond);
            Assert.True(clock.IsSynthetic);
            Assert.Equal(uf.SyntheticUtcTicks - UsArenaQuoteFeeder.SyntheticStepTicks, clock.UtcNowTicks);
        }

        [Fact]
        public void Replay_Is_Deterministic_Same_Scenario_Same_Clock_Same_Buffer_Trace()
        {
            string Run()
            {
                long t0 = Utc(Kst10);
                var clock = new ManualVirtualClock(t0);
                var buf = new PlutoSentimentBuffer();
                var a = new PlutoCatalystAnalyzer(buf, clock);
                var feeder = new ScenarioNewsFeeder(it => a.Ingest(it).Status is PlutoIngestStatus.Published or PlutoIngestStatus.Noise, clock);
                feeder.Arm();
                var sb = new StringBuilder();
                long[] syms = { SamsungBio, Hynix, Hyundai, Lges, Nvda, Tsla };
                for (int step = 0; step <= 90; step++)
                {
                    feeder.Pump();
                    foreach (long sym in syms)
                        sb.Append(buf.TryReadTag(sym, clock, out var tag) ? tag.EffectiveScore : 999).Append(',');
                    sb.Append('|');
                    clock.Advance(TimeSpan.FromSeconds(10));
                }
                return sb.ToString();
            }
            string a1 = Run(), a2 = Run();
            Assert.Equal(a1, a2);
            Assert.Contains("-", a1);
        }

        [Fact]
        public void Clock_Read_And_Tag_Read_Allocate_Zero_Bytes()
        {
            long t0 = Utc(Kst10);
            var tick = new TickSyncedVirtualClock();
            tick.Observe(t0);
            IVirtualClock manual = new ManualVirtualClock(t0);
            IVirtualClock sys = SystemVirtualClock.Instance;
            var buf = new PlutoSentimentBuffer();
            buf.Publish(new PlutoSentimentFactor(Samsung, t0, 85, 90, CatalystCategory.StructuralPositive, PlutoSource.Dart, "수주"u8), tick);
            buf.Publish(new PlutoSentimentFactor(Hynix, t0, -80, 90, CatalystCategory.StructuralNegative, PlutoSource.Dart, "유상증자"u8), tick);
            long acc = 0;
            void Loop(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    tick.Observe(t0 + (i & 1023) * TimeSpan.TicksPerSecond);
                    acc += tick.UtcNowTicks & 1;
                    acc += manual.UtcNowTicks & 1;
                    acc += sys.UtcNowTicks & 1;
                    if (buf.TryReadTag((i & 1) == 0 ? Samsung : Hynix, tick, out var tag)) acc += tag.NewsBp;
                }
            }
            Loop(10_000);
            long bytes = MinAllocated(() => Loop(100_000));
            _out.WriteLine($"clock+tag δ={bytes}B acc={acc}");
            Assert.Equal(0, bytes);
        }

        // ── §2 E2E ───────────────────────────────────────────────────────────────

        [Fact]
        public async Task Dart_Mock_Feed_Baseline_Then_New_Disclosure_Flows_To_Buffer()
        {
            long t0 = Utc(Kst10);
            var clock = new ManualVirtualClock(t0);
            var buf = new PlutoSentimentBuffer();
            using var a = new PlutoCatalystAnalyzer(buf, clock);
            a.Start();
            int round = 0;
            var handler = new StubHandler((req, _) =>
            {
                int n = Interlocked.Increment(ref round);
                string body = n == 1
                    ? DartJson(("삼성전자", "005930", "주요사항보고서(유상증자결정)", "20261022000001"))
                    : DartJson(("삼성전자", "005930", "주요사항보고서(유상증자결정)", "20261022000001"),
                               ("SK하이닉스", "000660", "주요사항보고서(유상증자결정)", "20261022000002"),
                               ("삼성바이오로직스", "207940", "단일판매ㆍ공급계약체결", "20261022000003"));
                return Task.FromResult(Ok(body));
            });
            using var feeder = new DartDisclosureFeeder("TEST-KEY-0123456789", a.TryEnqueue, clock, handler);

            Assert.Equal(0, await feeder.PollOnceAsync(CancellationToken.None));          // 기준선 = 기존 공시 전달 0(신선도 위장 금지)
            Assert.Contains("crtfc_key=TEST-KEY-0123456789", handler.LastUri);
            Assert.Contains("bgn_de=20261022", handler.LastUri);
            Assert.Contains("api/list.json", handler.LastUri);
            Assert.False(buf.TryRead(Samsung, out _));

            clock.Advance(TimeSpan.FromMinutes(1));
            Assert.Equal(2, await feeder.PollOnceAsync(CancellationToken.None));          // 신규 2건만(기준선 접수번호 = 중복)
            WaitProcessed(a, 2);
            Assert.True(buf.TryReadTag(Hynix, clock, out var neg));
            Assert.Equal(CatalystCategory.StructuralNegative, neg.Factor.Category);
            Assert.Equal(-80, neg.DecayedScore);
            Assert.Equal(PlutoSource.Dart, neg.Factor.Source);
            Assert.Equal(clock.UtcNowTicks, neg.Factor.TimestampUtcTicks);                 // 발생 시각 = 피더가 처음 본 가상 시각
            Assert.StartsWith("[SK하이닉스]", neg.Factor.Headline);
            Assert.True(buf.TryReadTag(SamsungBio, clock, out var pos));
            Assert.Equal(CatalystCategory.StructuralPositive, pos.Factor.Category);
            Assert.False(buf.TryRead(Samsung, out _));

            Assert.Equal(0, await feeder.PollOnceAsync(CancellationToken.None));          // 같은 목록 재수신 = 전달 0
            var h = feeder.Health;
            Assert.Equal(3, h.Polls);
            Assert.Equal(2, h.Delivered);
            Assert.Equal(4, h.Duplicates);
            Assert.Equal(0, h.Failures);
            Assert.Equal(PlutoFeederFault.None, h.LastFault);
            Assert.Equal(PlutoSource.Dart, h.Source);
        }

        [Fact]
        public async Task Dart_No_Data_Status_Is_Clean_And_Api_Error_Is_Counted()
        {
            var clock = new ManualVirtualClock(Utc(Kst10));
            int calls = 0;
            var handler = new StubHandler((_, _) => Task.FromResult(Ok(Interlocked.Increment(ref calls) == 1
                ? "{\"status\":\"013\",\"message\":\"조회된 데이타가 없습니다.\"}"
                : "{\"status\":\"020\",\"message\":\"요청 제한을 초과하였습니다.\"}")));
            var got = new List<PlutoNewsItem>();
            using var f = new DartDisclosureFeeder("K", it => { got.Add(it); return true; }, clock, handler, skipInitialBacklog: false);
            Assert.Equal(0, await f.PollOnceAsync(CancellationToken.None));
            Assert.Equal(0, f.Health.Failures);
            Assert.Equal(0, await f.PollOnceAsync(CancellationToken.None));
            Assert.Equal(1, f.Health.Failures);
            Assert.Equal(PlutoFeederFault.ApiError, f.Health.LastFault);
            Assert.Empty(got);
        }

        [Fact]
        public async Task Sec_Atom_Mock_Feed_Maps_Cik_And_Item_402_To_Restatement()
        {
            var updated = new DateTimeOffset(2026, 10, 22, 10, 5, 0, TimeSpan.FromHours(-4));
            var clock = new ManualVirtualClock(updated.UtcTicks + TimeSpan.FromMinutes(1).Ticks);
            var buf = new PlutoSentimentBuffer();
            using var a = new PlutoCatalystAnalyzer(buf, clock);
            a.Start();
            string atom = SecAtom(
                ("8-K - NVIDIA CORP (0001045810) (Filer)", "2026-10-22T10:05:00-04:00",
                    "&lt;b&gt;Filed:&lt;/b&gt; 2026-10-22 &lt;b&gt;AccNo:&lt;/b&gt; 0001045810-26-000001&lt;br&gt;Item 4.02: Non-Reliance on Previously Issued Financial Statements&lt;br&gt;Item 9.01: Financial Statements and Exhibits",
                    "urn:tag:sec.gov,2008:accession-number=0001045810-26-000001"),
                ("8-K - Apple Inc. (0000320193) (Filer)", "2026-10-22T10:04:00-04:00",
                    "&lt;b&gt;Filed:&lt;/b&gt; 2026-10-22&lt;br&gt;Item 2.02: Results of Operations and Financial Condition",
                    "urn:tag:sec.gov,2008:accession-number=0000320193-26-000009"));
            var handler = new StubHandler((_, _) => Task.FromResult(Ok(atom, "application/atom+xml")));
            using var f = new SecEdgarFeeder("MS2E StockSuite test@example.invalid", a.TryEnqueue, clock, handler);

            Assert.Equal(2, await f.PollOnceAsync(CancellationToken.None));
            Assert.Equal("MS2E StockSuite test@example.invalid", handler.LastUserAgent);
            Assert.Contains("action=getcurrent", handler.LastUri);
            WaitProcessed(a, 2);
            Assert.True(buf.TryReadTag(Nvda, clock, out var tag));
            Assert.Equal(CatalystCategory.StructuralNegative, tag.Factor.Category);
            Assert.Equal(-90, tag.Factor.Score);
            Assert.Equal(PlutoSource.Sec, tag.Factor.Source);
            Assert.Equal(updated.UtcTicks, tag.Factor.TimestampUtcTicks);
            Assert.Contains("restatement", tag.Factor.Headline);
            Assert.False(buf.TryRead(Aapl, out _));                                        // Item 2.02 단독 = 촉매 키워드 없음 → 노이즈
            Assert.Equal(1, a.NoiseItems);
            Assert.Equal("NVDA", SecEdgarFeeder.TickerForCik(1045810));
            Assert.Null(SecEdgarFeeder.TickerForCik(1));
        }

        [Fact]
        public async Task Golden_Scenario_Injects_In_Time_Order_Through_Analyzer_Queue()
        {
            long t0 = Utc(Kst10);
            var clock = new ManualVirtualClock(t0);
            var buf = new PlutoSentimentBuffer();
            using var a = new PlutoCatalystAnalyzer(buf, clock);
            a.Start();
            using var f = new ScenarioNewsFeeder(a.TryEnqueue, clock);
            Assert.Equal(6, f.StepCount);
            f.Arm();

            Assert.Equal(0, await f.PollOnceAsync(CancellationToken.None));               // T+0 = 도래 단계 없음
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(1, f.Pump());                                                      // T+30s 공급계약
            WaitProcessed(a, 1);
            Assert.True(buf.TryReadTag(SamsungBio, clock, out var bio));
            Assert.Equal(85, bio.DecayedScore);                                             // 대규모 · 조원 강조 +10
            Assert.StartsWith(ScenarioNewsFeeder.SyntheticPrefix, bio.Factor.Headline);   // 합성 표기 = 64B 요약에 그대로
            Assert.False(buf.TryRead(Hynix, out _));                                        // 2분 단계는 아직

            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.Equal(5, f.Pump());
            Assert.True(f.Completed);
            WaitProcessed(a, 6);
            Assert.True(buf.TryReadTag(Hynix, clock, out var hy));
            Assert.Equal(CatalystCategory.StructuralNegative, hy.Factor.Category);
            Assert.False(buf.TryRead(Hyundai, out _));                                     // 단순 시황 = 노이즈 기각
            Assert.Equal(1, a.NoiseItems);
            Assert.True(buf.TryReadTag(Lges, clock, out var lg));
            Assert.Equal(-100, lg.Factor.Score);
            Assert.True(lg.IsShock);                                                        // 감사의견 거절 = 긴급 악재
            Assert.True(buf.TryReadTag(Nvda, clock, out var nv));
            Assert.Equal(CatalystCategory.StructuralPositive, nv.Factor.Category);
            Assert.True(buf.TryReadTag(Tsla, clock, out var ts));
            Assert.Equal(CatalystCategory.MomentumNegative, ts.Factor.Category);
            Assert.Equal(t0 + TimeSpan.FromMinutes(10).Ticks, ts.Factor.TimestampUtcTicks); // 발생 시각 = 시작 + 오프셋(가상 도메인)
            Assert.Equal(0, f.Pump());
            Assert.Equal(6, f.Health.Delivered);
        }

        [Fact]
        public void Scenario_Requires_Synthetic_Clock_When_Asked_And_Rearms_On_Clock_Regression()
        {
            var tick = new TickSyncedVirtualClock(new ManualVirtualClock(Utc(Kst10)));
            var got = new List<PlutoNewsItem>();
            var f = new ScenarioNewsFeeder(it => { got.Add(it); return true; }, tick,
                new[] { new PlutoScenarioStep(TimeSpan.Zero, PlutoSource.Dart, "A"), new PlutoScenarioStep(TimeSpan.FromMinutes(1), PlutoSource.Dart, "B") },
                requireSynthetic: true);
            Assert.Equal(0, f.Pump());                                                      // 실시세(벽시계) = 무장 0
            Assert.False(f.Armed);
            long s1 = Utc(Kst10) + TimeSpan.FromHours(1).Ticks;
            tick.Observe(s1);
            Assert.Equal(1, f.Pump());
            Assert.Equal("[합성] A", got[0].Headline);
            tick.Reset();
            tick.Observe(s1 - TimeSpan.FromHours(2).Ticks);                                 // 합성 재선택 = 과거 도메인 재동기
            Assert.Equal(1, f.Pump());                                                      // 역행 = 재무장(처음부터)
            Assert.Equal(1, f.Rearms);
            Assert.Equal(2, got.Count);
            Assert.Throws<ArgumentException>(() => new ScenarioNewsFeeder(_ => true, tick, new[] { new PlutoScenarioStep(TimeSpan.FromSeconds(-1), PlutoSource.Dart, "x") }));
        }

        // ── §3 Fail-Closed 격리 ──────────────────────────────────────────────────

        [Fact]
        public async Task Network_Failure_Timeout_And_Http_500_Never_Throw_And_Back_Off()
        {
            var clock = new ManualVirtualClock(Utc(Kst10));
            var got = new List<PlutoNewsItem>();
            bool Sink(PlutoNewsItem it) { got.Add(it); return true; }

            using var down = new DartDisclosureFeeder("K", Sink, clock, new StubHandler((_, _) => throw new HttpRequestException("연결 거부")), pollInterval: TimeSpan.FromSeconds(10));
            Assert.Equal(0, await down.PollOnceAsync(CancellationToken.None));
            Assert.Equal(PlutoFeederFault.Network, down.Health.LastFault);
            Assert.Equal(TimeSpan.FromSeconds(20), down.NextDelay());
            Assert.Equal(0, await down.PollOnceAsync(CancellationToken.None));
            Assert.Equal(TimeSpan.FromSeconds(40), down.NextDelay());
            for (int i = 0; i < 20; i++) await down.PollOnceAsync(CancellationToken.None);
            Assert.Equal(PollingNewsFeeder.MaxBackoff, down.NextDelay());                  // 상한 포화
            Assert.Equal(22, down.Health.ConsecutiveFailures);

            var hang = new StubHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Ok("{}"); });
            using var slow = new SecEdgarFeeder("UA", Sink, clock, hang, timeout: TimeSpan.FromMilliseconds(150));
            var sw = Stopwatch.StartNew();
            Assert.Equal(0, await slow.PollOnceAsync(CancellationToken.None));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000, "타임아웃 미적용 " + sw.ElapsedMilliseconds + "ms");
            Assert.Equal(PlutoFeederFault.Timeout, slow.Health.LastFault);
            Assert.Equal(1, slow.Health.Timeouts);

            using var err = new DartDisclosureFeeder("K", Sink, clock, new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))), skipInitialBacklog: false);
            Assert.Equal(0, await err.PollOnceAsync(CancellationToken.None));
            Assert.Equal(PlutoFeederFault.HttpStatus, err.Health.LastFault);

            // 회복: 다음 회차 성공 = 연속 실패 0 · 기본 주기 복귀
            int n = 0;
            using var flaky = new DartDisclosureFeeder("K", Sink, clock, new StubHandler((_, _) => Interlocked.Increment(ref n) == 1
                ? throw new HttpRequestException("x")
                : Task.FromResult(Ok(DartJson(("SK하이닉스", "000660", "주요사항보고서(유상증자결정)", "1"))))), skipInitialBacklog: false);
            Assert.Equal(0, await flaky.PollOnceAsync(CancellationToken.None));
            Assert.Equal(1, await flaky.PollOnceAsync(CancellationToken.None));
            Assert.Equal(0, flaky.Health.ConsecutiveFailures);
            Assert.Equal(DartDisclosureFeeder.DefaultInterval, flaky.NextDelay());
            Assert.Single(got);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Equal(0, await flaky.PollOnceAsync(cancelled.Token));                   // 호출 측 취소 = 0(예외 0)
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("not json at all")]
        [InlineData("[1,2,3]")]
        [InlineData("{\"status\":\"000\"}")]
        [InlineData("{\"status\":\"000\",\"list\":{}}")]
        [InlineData("{\"status\":\"000\",\"list\":[1,2]}")]
        [InlineData("{\"status\":000,\"list\":[]}")]
        [InlineData("{\"status\":\"000\",\"list\":[{\"report_nm\":\"유상증자\",\"rcept_no\":\"1\"}")]
        [InlineData("\u0000\u0001\u0002ÿ")]
        public async Task Malformed_Dart_Payload_Is_Contained_And_Delivers_Nothing(string payload)
        {
            var clock = new ManualVirtualClock(Utc(Kst10));
            var buf = new PlutoSentimentBuffer();
            using var a = new PlutoCatalystAnalyzer(buf, clock);
            using var f = new DartDisclosureFeeder("K", a.TryEnqueue, clock, new StubHandler((_, _) => Task.FromResult(Ok(payload))), skipInitialBacklog: false);
            Assert.Equal(0, await f.PollOnceAsync(CancellationToken.None));
            Assert.Equal(1, f.Health.Failures);
            Assert.Equal(1, f.Health.Malformed);
            Assert.Equal(PlutoFeederFault.Malformed, f.Health.LastFault);
            Assert.Equal(0, a.Enqueued);
            Assert.Equal(0, buf.Count);
        }

        [Theory]
        [InlineData("<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><title>8-K")]
        [InlineData("<?xml version=\"1.0\"?><!DOCTYPE feed [<!ENTITY x \"xxxxxxxx\"><!ENTITY y \"&x;&x;&x;&x;\">]><feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><title>&y;</title></entry></feed>")]
        [InlineData("<html><body>Request Rate Threshold Exceeded</body></html>")]
        [InlineData("{\"json\":true}")]
        public async Task Malformed_Sec_Payload_Or_Dtd_Is_Contained(string payload)
        {
            var clock = new ManualVirtualClock(Utc(Kst10));
            var got = new List<PlutoNewsItem>();
            using var f = new SecEdgarFeeder("UA", it => { got.Add(it); return true; }, clock, new StubHandler((_, _) => Task.FromResult(Ok(payload, "text/xml"))));
            Assert.Equal(0, await f.PollOnceAsync(CancellationToken.None));
            Assert.Equal(PlutoFeederFault.Malformed, f.Health.LastFault);
            Assert.Empty(got);
        }

        [Fact]
        public async Task Oversize_Payload_And_Throwing_Sink_Are_Contained()
        {
            var clock = new ManualVirtualClock(Utc(Kst10));
            string huge = DartJson(Enumerable.Range(0, 400).Select(i => ("삼성전자", "005930", "주요사항보고서(유상증자결정)", "R" + i)).ToArray());
            using var big = new DartDisclosureFeeder("K", _ => true, clock, new StubHandler((_, _) => Task.FromResult(Ok(huge))), skipInitialBacklog: false, maxPayloadBytes: 4096);
            Assert.Equal(0, await big.PollOnceAsync(CancellationToken.None));
            Assert.Equal(PlutoFeederFault.Oversize, big.Health.LastFault);

            using var boom = new DartDisclosureFeeder("K", _ => throw new InvalidOperationException("싱크 붕괴"), clock,
                new StubHandler((_, _) => Task.FromResult(Ok(DartJson(("SK하이닉스", "000660", "유상증자결정", "1"), ("삼성전자", "005930", "유상증자결정", "2"))))),
                skipInitialBacklog: false);
            Assert.Equal(0, await boom.PollOnceAsync(CancellationToken.None));
            Assert.Equal(2, boom.Health.SinkRejected);
            Assert.Equal(0, boom.Health.Failures);
        }

        [Fact]
        public async Task Failing_Feeder_Background_Loop_Does_Not_Stall_Engine_Or_Analyzer()
        {
            var clock = new ManualVirtualClock(Utc(Kst10));
            var buf = new PlutoSentimentBuffer();
            using var a = new PlutoCatalystAnalyzer(buf, clock);
            a.Start();
            var hang = new StubHandler(async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return Ok("{}"); });
            using var f = new SecEdgarFeeder("UA", a.TryEnqueue, clock, hang, pollInterval: TimeSpan.FromSeconds(1), timeout: TimeSpan.FromMilliseconds(100));
            f.Start();
            f.Start();                                                                        // 재호출 무시
            Assert.True(f.Health.Running);

            var e = new KrxArenaEngine();
            e.AttachPluto(buf);
            var sw = Stopwatch.StartNew();
            var t = Kst10;
            for (int i = 0; i < 20_000; i++)
            {
                t = t.AddMilliseconds(50);
                e.OnQuote(Samsung, 70_000, 70_100, 1_000, 1_200, 100, t);
            }
            sw.Stop();
            Assert.True(e.TryReadSnapshot(out var s));
            Assert.Equal(20_000, s.Ticks);
            Assert.True(a.TryEnqueue(new PlutoNewsItem(PlutoSource.Dart, "[삼성전자] 주요사항보고서(유상증자결정)", clock.UtcNowTicks)));
            WaitProcessed(a, 1);
            Assert.Equal(0, a.Faulted);
            Assert.True(buf.TryRead(Samsung, out _));
            Assert.True(SpinWait.SpinUntil(() => f.Health.Timeouts >= 1, 5000));
            var stopSw = Stopwatch.StartNew();
            f.Stop();
            Assert.True(stopSw.ElapsedMilliseconds < 3000);
            Assert.False(f.Health.Running);
            Assert.Equal(0, await f.PollOnceAsync(new CancellationToken(true)));
            _out.WriteLine($"engine 20k ticks {sw.ElapsedMilliseconds}ms while feeder failing · timeouts={f.Health.Timeouts}");
        }

        [Fact]
        public void Parsers_Are_Usable_Offline_For_Replay()
        {
            var items = new List<PlutoNewsItem>();
            long now = Utc(Kst10);
            Assert.Equal(PlutoFeederFault.None, DartDisclosureFeeder.ParseListForTest(Encoding.UTF8.GetBytes(DartJson(
                ("LG에너지솔루션", "373220", "감사보고서제출 — 감사의견 거절", "9"), ("비상장사", "", "주요사항보고서(유상증자결정)", "10"), ("X", "1", "", "11"))), now, items));
            Assert.Equal(2, items.Count);                                                    // 보고서명 결손 = 건너뜀
            Assert.Equal("373220", items[0].SymbolHint);
            Assert.Null(items[1].SymbolHint);                                                // 비상장 = 힌트 없음(별칭 매핑)
            Assert.All(items, it => Assert.Equal(now, it.PublishedUtcTicks));
            items.Clear();
            Assert.Equal(PlutoFeederFault.Malformed, SecEdgarFeeder.ParseAtomForTest("<x"u8, items));
            Assert.Equal(PlutoFeederFault.None, SecEdgarFeeder.ParseAtomForTest(Encoding.UTF8.GetBytes(SecAtom(
                ("8-K - TESLA, INC. (0001318605) (Filer)", "2026-10-22T14:00:00Z", "Item 1.03: Bankruptcy or Receivership", "id-1"))), items));
            Assert.Single(items);
            Assert.Equal("TSLA", items[0].SymbolHint);
            Assert.Contains("bankruptcy", items[0].Headline);
        }

        // ── §4 UI 배지 ───────────────────────────────────────────────────────────

        private static (KrxArenaEngine Engine, KrxArenaQuoteFeeder Feeder, TickSyncedVirtualClock Clock, PlutoSentimentBuffer Buffer) SyntheticKrx()
        {
            var clock = new TickSyncedVirtualClock();
            var buf = new PlutoSentimentBuffer();
            var e = new KrxArenaEngine();
            e.AttachPluto(buf);
            var f = new KrxArenaQuoteFeeder(e);
            f.AttachClock(clock);
            Assert.True(f.SelectSource(KrxArenaFeed.Synthetic));
            long wall = new DateTime(2026, 10, 22, 12, 0, 0).Ticks;
            for (int i = 0; i < 10; i++) f.Pump(wall + i * 50 * TimeSpan.TicksPerMillisecond);
            return (e, f, clock, buf);
        }

        [Fact]
        public void Port_Catalyst_Projects_Positive_Shock_Negative_And_Omits_Noise()
        {
            var (e, f, clock, buf) = SyntheticKrx();
            var port = new KrxArenaPortAdapter(e, f);
            long now = clock.UtcNowTicks;
            buf.Publish(new PlutoSentimentFactor(Samsung, now - 15 * TimeSpan.TicksPerMinute, 80, 90, CatalystCategory.StructuralPositive, PlutoSource.Dart, Encoding.UTF8.GetBytes("[삼성전자] 대규모 공급계약")), clock);
            buf.Publish(new PlutoSentimentFactor(Lges, now, -100, 90, CatalystCategory.StructuralNegative, PlutoSource.Dart, Encoding.UTF8.GetBytes("[LG에너지솔루션] 감사의견 거절")), clock);
            buf.Publish(new PlutoSentimentFactor(Hynix, now, -30, 70, CatalystCategory.MomentumNegative, PlutoSource.Wire, Encoding.UTF8.GetBytes("목표가 하향")), clock);
            buf.Publish(new PlutoSentimentFactor(Hyundai, now - TimeSpan.FromDays(1).Ticks, 30, 70, CatalystCategory.MomentumPositive, PlutoSource.Wire, Encoding.UTF8.GetBytes("만료")), clock);

            var pos = port.Catalyst(Samsung);
            Assert.NotNull(pos);
            Assert.Equal(ArenaCatalystKind.Positive, pos!.Kind);
            Assert.StartsWith("[호재 +", pos.BadgeText);
            Assert.Equal(50, pos.HalfLifeProgressPct);                                      // 15분 / 반감기 30분
            Assert.Equal(30, pos.HalfLifeMinutes);
            Assert.Contains("DART 공시", pos.Tooltip);
            Assert.Contains("대규모 공급계약", pos.Tooltip);

            var shock = port.Catalyst(Lges);
            Assert.NotNull(shock);
            Assert.True(shock!.IsShock);
            Assert.Equal("[긴급 악재 -90]", shock.BadgeText);
            Assert.Contains("진입 금지", shock.Tooltip);

            var neg = port.Catalyst(Hynix);
            Assert.Equal(ArenaCatalystKind.Negative, neg!.Kind);
            Assert.Equal("[악재 -21]", neg.BadgeText);

            Assert.Null(port.Catalyst(Hyundai));                                            // 감쇄 0 = 중립 → 생략
            Assert.Null(port.Catalyst(207940));                                             // 뉴스 없음
            Assert.Null(new KrxArenaPortAdapter(new KrxArenaEngine()).Catalyst(Samsung));   // 버퍼 미결속 · 정산 0회

            var us = new UsTechArenaEngine();
            us.AttachPluto(buf);
            Assert.Null(new UsArenaPortAdapter(us).Catalyst("NVDA"));                        // 정산 0회 = 기준 시각 없음
            Assert.Null(new UsArenaPortAdapter(us).Catalyst("ZZZZ"));
        }

        [Fact]
        public void DualArenaPage_Renders_Green_Positive_Blinking_Shock_And_Tooltips()
        {
            var (e, f, clock, buf) = SyntheticKrx();
            long now = clock.UtcNowTicks;
            buf.Publish(new PlutoSentimentFactor(SamsungBio, now, 85, 90, CatalystCategory.StructuralPositive, PlutoSource.Dart, Encoding.UTF8.GetBytes("[합성] [삼성바이오로직스] 공급계약")), clock);
            buf.Publish(new PlutoSentimentFactor(Lges, now, -100, 90, CatalystCategory.StructuralNegative, PlutoSource.Dart, Encoding.UTF8.GetBytes("[합성] [LG에너지솔루션] 감사의견 거절")), clock);
            f.Pump(new DateTime(2026, 10, 22, 12, 1, 0).Ticks);

            using var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(e, f));
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new StockManager.Engines.Arena.Crypto.UpbitCryptoArenaEngine()));   // [v12.18.66-DEV]
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);   // [v12.18.69-DEV] 토글 영속화 포트(저장소 미결선 null 객체)
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);   // [v12.18.77-DEV] 학습 엔진 미결선
            var cut = ctx.RenderComponent<DualArenaPage>();

            var strip = cut.Find(".da-cats");
            Assert.Contains("뉴스 촉매", strip.TextContent);
            var badges = strip.QuerySelectorAll(".da-cat");
            Assert.Equal(2, badges.Length);                                                  // 뉴스 없는 3종목 = 배지 0(클린 UI)
            var green = cut.Find(".da-cats .da-cat.pos");
            Assert.StartsWith("[호재 +", green.TextContent.Trim());
            Assert.NotNull(green.QuerySelector(".da-hl"));                                  // 반감기 진행 막대
            Assert.Contains("[합성] [삼성바이오로직스] 공급계약", green.GetAttribute("title"));
            Assert.Contains("DART 공시", green.GetAttribute("title"));
            var shock = cut.Find(".da-cats .da-cat.shock");
            Assert.StartsWith("[긴급 악재 -", shock.TextContent.Trim());
            Assert.Equal(ArenaCatalystKind.Shock.ToString(), shock.GetAttribute("data-kind"));
            Assert.Contains("진입 금지", shock.GetAttribute("title"));
            Assert.Contains("@keyframes da-blink", cut.Markup);
            Assert.Contains("prefers-reduced-motion", cut.Markup);
        }
    }
}
