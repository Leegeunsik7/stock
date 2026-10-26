// ============================================================================
// PlutoCandleWarmStartWiringTests — v12.18.86-DEV: 웜스타트 실효성(Candles 적재 결선) + DualArenaPage 인지 브리핑 텐서 패널
//
//   ① PlutoCandleRecorder: Ok 일봉만 (Symbol, Date) Upsert(중복 = 갱신 · 동일 값 = 기록 0) · 단위 환원(KRX 원 · US 센트 → 달러 · 코인 raw → 원) ·
//      미수신(NoData/RateLimited/Faulted) = 기록 0 · 분 봉 = 일봉 둔갑 0 · 종목당 간격 스로틀 · DB 장애 = 롤백 + 내부 결과 불변.
//   ② UpbitPublicCandleSource 일봉 수확: harvestDaily 시 ④ /candles/days 1회 · DailyRefresh 내 재요청 0 · 429 = RateLimited · 기본 = GET 3회 불변.
//   ③ 종단: 레코더 적재 → PlutoLocalCandleSource → PlutoReplayWarmStarter 리플레이 → 어댑터 자산군별 WarmReady/NoData.
//   ④ 어댑터 텐서 투영(활성 슬롯 해석 · 전역 폴백 · 웜스타트 코드 매핑).
//   ⑤ DualArenaPage [📊 인지 브리핑] 텐서 패널 렌더(결선/미결선 정직) · RCL 순수성(신규 주입 0 · 엔진 참조 0) · 호스트 결선 문자열.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Feeds.Crypto;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoCandleWarmStartWiringTests : IDisposable
    {
        private const long Coin = 100_000_000L;
        private static readonly DateTime Day0 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-plutocandle-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "portfolio.db");

        public PlutoCandleWarmStartWiringTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        /// <summary>일봉 소스 대역(시장 · 결과 · 봉 수 · 종가 기울기 지정).</summary>
        private sealed class FakeDailySource : IPlutoAssetDataSource
        {
            public PlutoScreenMarket Market { get; init; } = PlutoScreenMarket.Krx;
            public bool IsConnected => true;
            public string Label => "fake-daily";
            public PlutoFetchOutcome Outcome = PlutoFetchOutcome.Ok;
            public int Days = 40;
            public long Unit = 1;
            public long Bump;
            public bool Intraday;
            public int Calls;

            public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
            {
                Calls++;
                for (int d = 0; d < Days; d++)
                {
                    long c = (1000 + d * 10 + Bump) * Unit;
                    long t = Intraday ? Day0.AddMinutes(d).Ticks : Day0.AddDays(d).Ticks;
                    sample.Bars[d] = new PlutoOhlcvBar(t, c - Unit, c + 5 * Unit, c - 5 * Unit, c, 100 + d);
                }
                sample.BarCount = Days;
                return Outcome;
            }
        }

        private static PlutoUniverseEntry Entry(PlutoScreenMarket m, string code) => new(m, PlutoUniverseSegment.Kospi, 1, code, code);

        private static long Count(LocalDatabase db, string symbol) =>
            Convert.ToInt64(db.Query("SELECT COUNT(*) FROM Candles WHERE Symbol = @s", new() { ["@s"] = symbol }).Rows[0][0]);

        private static double CloseOf(LocalDatabase db, string symbol, string date) =>
            Convert.ToDouble(db.Query("SELECT Close FROM Candles WHERE Symbol = @s AND Date = @d", new() { ["@s"] = symbol, ["@d"] = date }).Rows[0][0]);

        // ── ① 레코더 ─────────────────────────────────────────────────────────

        [Fact]
        public void Recorder_Upserts_Daily_Bars_Idempotently_And_Updates_After_Interval()
        {
            using var db = new LocalDatabase(DbPath);
            long now = 0;
            var inner = new FakeDailySource();
            var rec = new PlutoCandleRecorder(inner, db, PlutoCandleCadence.Daily, () => now);
            var sample = new PlutoAssetSample();
            Assert.Equal(PlutoFetchOutcome.Ok, rec.Fetch(Entry(PlutoScreenMarket.Krx, "005930"), sample, CancellationToken.None));
            Assert.Equal(40, Count(db, "005930"));
            Assert.Equal(1000.0, CloseOf(db, "005930", "2026-08-01"));
            Assert.Equal(1390.0, CloseOf(db, "005930", "2026-09-09"));
            Assert.Equal(1, rec.RecordedBatches);

            inner.Bump = 7;                                                        // 간격 내 재조회 = 기록 0(스로틀)
            rec.Fetch(Entry(PlutoScreenMarket.Krx, "005930"), sample, CancellationToken.None);
            Assert.Equal(1, rec.RecordedBatches);
            Assert.Equal(1390.0, CloseOf(db, "005930", "2026-09-09"));

            now += (long)PlutoCandleRecorder.MinRecordInterval.TotalMilliseconds;  // 간격 경과 = 같은 키 갱신(중복 행 0)
            rec.Fetch(Entry(PlutoScreenMarket.Krx, "005930"), sample, CancellationToken.None);
            Assert.Equal(2, rec.RecordedBatches);
            Assert.Equal(40, Count(db, "005930"));
            Assert.Equal(1397.0, CloseOf(db, "005930", "2026-09-09"));
            Assert.Equal(0, rec.Faults);
            Assert.Equal(inner.Market, rec.Market);
            Assert.Equal(inner.Label, rec.Label);
        }

        [Theory]
        [InlineData(PlutoFetchOutcome.NoData)]
        [InlineData(PlutoFetchOutcome.RateLimited)]
        [InlineData(PlutoFetchOutcome.Faulted)]
        [InlineData(PlutoFetchOutcome.NotConnected)]
        public void Recorder_Records_Nothing_When_Data_Not_Received(PlutoFetchOutcome outcome)
        {
            using var db = new LocalDatabase(DbPath);
            var rec = new PlutoCandleRecorder(new FakeDailySource { Outcome = outcome }, db, PlutoCandleCadence.Daily);
            Assert.Equal(outcome, rec.Fetch(Entry(PlutoScreenMarket.Krx, "005930"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(0, Count(db, "005930"));                                  // 미수신 = 가짜 캔들 0
            Assert.Equal(0, rec.RecordedRows);
        }

        [Fact]
        public void Recorder_Never_Records_Intraday_Bars_As_Daily_But_Records_Harvested_Daily()
        {
            using var db = new LocalDatabase(DbPath);
            var inner = new FakeDailySource { Market = PlutoScreenMarket.Crypto, Intraday = true, Unit = Coin };
            var rec = new PlutoCandleRecorder(inner, db, PlutoCandleCadence.Intraday);
            var sample = new PlutoAssetSample();
            rec.Fetch(Entry(PlutoScreenMarket.Crypto, "KRW-BTC"), sample, CancellationToken.None);
            Assert.Equal(0, Count(db, "KRW-BTC"));                                 // 분 봉만 = 적재 0

            var rec2 = new PlutoCandleRecorder(new HarvestingSource(inner), db, PlutoCandleCadence.Intraday);
            rec2.Fetch(Entry(PlutoScreenMarket.Crypto, "KRW-BTC"), sample, CancellationToken.None);
            Assert.Equal(3, Count(db, "KRW-BTC"));
            Assert.Equal(105_000_000.5, CloseOf(db, "KRW-BTC", "2026-08-03"));   // raw ÷ 10^8 = 원
            var vol = Convert.ToDouble(db.Query("SELECT Volume FROM Candles WHERE Symbol = 'KRW-BTC' AND Date = '2026-08-03'").Rows[0][0]);
            Assert.Equal(1.5, vol);
        }

        /// <summary>분 봉 + 일봉 3개(업비트 ④ 수확 모사).</summary>
        private sealed class HarvestingSource : IPlutoAssetDataSource
        {
            private readonly IPlutoAssetDataSource _inner;
            public HarvestingSource(IPlutoAssetDataSource inner) => _inner = inner;
            public PlutoScreenMarket Market => _inner.Market;
            public bool IsConnected => true;
            public string Label => "harvest";

            public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
            {
                var r = _inner.Fetch(entry, sample, ct);
                for (int d = 0; d < 3; d++)
                {
                    long c = 105_000_000 * Coin + Coin / 2;
                    sample.DailyBars[d] = new PlutoOhlcvBar(Day0.AddDays(d).AddHours(23).Ticks, c - Coin, c + Coin, c - Coin, c, 3 * Coin / 2);
                }
                sample.DailyBarCount = 3;
                return r;
            }
        }

        [Fact]
        public void Recorder_Converts_Us_Cents_And_Drops_Invalid_Bars()
        {
            var bars = new[]
            {
                new PlutoOhlcvBar(Day0.Ticks, 18_000, 18_100, 17_900, 18_050, 10),
                new PlutoOhlcvBar(Day0.AddDays(1).Ticks, 18_000, 17_000, 18_100, 18_050, 10),   // 고 < 저 = 무효
                new PlutoOhlcvBar(0, 18_000, 18_100, 17_900, 18_050, 10),                         // 시각 0 = 무효
                new PlutoOhlcvBar(Day0.AddHours(5).Ticks, 18_000, 18_200, 17_900, 18_150, 12),  // 같은 날짜 = 마지막 우선
            };
            var rows = PlutoCandleRecorder.Rows("AAPL", PlutoScreenMarket.Us, bars);
            var row = Assert.Single(rows);
            Assert.Equal("2026-08-01", row["@d"]);
            Assert.Equal(181.5, (double)row["@c"]);                               // 센트 ÷ 100 = 달러
            Assert.Equal(12L, row["@v"]);
        }

        [Fact]
        public void Recorder_Db_Fault_Rolls_Back_And_Leaves_Fetch_Result_Untouched()
        {
            var db = new LocalDatabase(DbPath);
            db.Dispose();
            var rec = new PlutoCandleRecorder(new FakeDailySource(), db, PlutoCandleCadence.Daily);
            Assert.Equal(PlutoFetchOutcome.Ok, rec.Fetch(Entry(PlutoScreenMarket.Krx, "005930"), new PlutoAssetSample(), CancellationToken.None));
            Assert.Equal(1, rec.Faults);
            Assert.Equal(0, rec.RecordedRows);
        }

        // ── ② 업비트 일봉 수확 ───────────────────────────────────────────────

        private sealed class FakeUpbit : HttpMessageHandler
        {
            public readonly List<string> Paths = new();
            public HttpStatusCode DaysStatus = HttpStatusCode.OK;

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string path = request.RequestUri!.AbsolutePath;
                lock (Paths) Paths.Add(path + request.RequestUri.Query);
                if (path.EndsWith("/candles/days", StringComparison.Ordinal) && DaysStatus != HttpStatusCode.OK) return new HttpResponseMessage(DaysStatus);
                string body = path.EndsWith("/candles/minutes/1", StringComparison.Ordinal) ? Candles(1_792_913_040_000L, 60_000L)
                    : path.EndsWith("/candles/days", StringComparison.Ordinal) ? Candles(1_792_800_000_000L, 86_400_000L)
                    : path.EndsWith("/trades/ticks", StringComparison.Ordinal) ? "[{\"trade_price\":100.0,\"trade_volume\":1.0,\"timestamp\":1792913040983,\"ask_bid\":\"BID\"}]"
                    : "[{\"market\":\"KRW-BTC\",\"orderbook_units\":[{\"bid_price\":99,\"bid_size\":1.0,\"ask_price\":101,\"ask_size\":1.0}]}]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(Send(request, cancellationToken));

            private static string Candles(long latestMs, long stepMs) =>
                "[" + string.Join(",", Enumerable.Range(0, 2).Select(i =>
                    $"{{\"opening_price\":100.0,\"high_price\":110.0,\"low_price\":90.0,\"trade_price\":{105 - i}.0,\"timestamp\":{latestMs - i * stepMs},\"candle_acc_trade_volume\":1.5}}")) + "]";
        }

        private static PlutoUniverseEntry Btc() => Entry(PlutoScreenMarket.Crypto, "KRW-BTC");

        [Fact]
        public void Upbit_Default_Issues_Three_Gets_Without_Daily_Harvest()
        {
            var fake = new FakeUpbit();
            var src = new UpbitPublicCandleSource(new HttpClient(fake), new PlutoRequestPacer(10));
            var sample = new PlutoAssetSample();
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(Btc(), sample, CancellationToken.None));
            Assert.Equal(3, fake.Paths.Count);
            Assert.Equal(0, sample.DailyBarCount);
            Assert.False(src.HarvestsDaily);
        }

        [Fact]
        public void Upbit_Harvests_Daily_Once_Per_Refresh_Window_On_Same_Pacer()
        {
            var fake = new FakeUpbit();
            long now = 1_000;
            var pacer = new PlutoRequestPacer(10);
            var src = new UpbitPublicCandleSource(new HttpClient(fake), pacer, harvestDaily: true, nowMs: () => now);
            var sample = new PlutoAssetSample();
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(Btc(), sample, CancellationToken.None));
            Assert.Equal(4, fake.Paths.Count);
            Assert.StartsWith("/v1/candles/days?market=KRW-BTC&count=120", fake.Paths[3], StringComparison.Ordinal);
            Assert.Equal(4, pacer.Granted);                                        // 일봉 GET 도 같은 페이서(합산 한도)
            Assert.Equal(2, sample.DailyBarCount);
            Assert.Equal(105 * Coin, sample.DailyBars[1].Close);                   // 오래된 순 · 최신 마지막
            Assert.Equal(2, sample.BarCount);                                      // 분 봉 표본 불변

            sample.Clear();
            Assert.Equal(0, sample.DailyBarCount);
            src.Fetch(Btc(), sample, CancellationToken.None);
            Assert.Equal(7, fake.Paths.Count);                                     // 창 내 = 일봉 재요청 0
            Assert.Equal(0, sample.DailyBarCount);

            now += (long)UpbitPublicCandleSource.DailyRefresh.TotalMilliseconds;
            src.Fetch(Btc(), sample, CancellationToken.None);
            Assert.Equal(11, fake.Paths.Count);
            Assert.Equal(2, sample.DailyBarCount);
        }

        [Fact]
        public void Upbit_Daily_429_Is_RateLimited_And_Other_Failures_Keep_Minute_Sample()
        {
            var fake = new FakeUpbit { DaysStatus = HttpStatusCode.TooManyRequests };
            var src = new UpbitPublicCandleSource(new HttpClient(fake), new PlutoRequestPacer(10), harvestDaily: true);
            Assert.Equal(PlutoFetchOutcome.RateLimited, src.Fetch(Btc(), new PlutoAssetSample(), CancellationToken.None));

            var fake2 = new FakeUpbit { DaysStatus = HttpStatusCode.InternalServerError };
            var src2 = new UpbitPublicCandleSource(new HttpClient(fake2), new PlutoRequestPacer(10), harvestDaily: true);
            var sample = new PlutoAssetSample();
            Assert.Equal(PlutoFetchOutcome.Ok, src2.Fetch(Btc(), sample, CancellationToken.None));
            Assert.Equal(0, sample.DailyBarCount);
            Assert.Equal(2, sample.BarCount);
            src2.Fetch(Btc(), sample, CancellationToken.None);
            Assert.Equal(7, fake2.Paths.Count);                                    // 실패도 창 내 재요청 0(즉시 재시도 0)
        }

        // ── ③ 종단: 적재 → 판독 → 웜스타트 → 어댑터 ──────────────────────────

        [Fact]
        public void End_To_End_Recorded_Candles_Make_Warm_Start_Ready_Only_For_Fed_Assets()
        {
            using var db = new LocalDatabase(DbPath);
            var tensor = new PlutoCognitiveTensorLake();
            var cold = new PlutoReplayWarmStarter(tensor, new PlutoLocalCandleSource(db));
            var coldReport = cold.Run();
            Assert.Equal(PlutoWarmStartState.NoData, coldReport.State);           // 적재 전 = NoData(가짜 시계열 0)
            for (int a = 0; a < 3; a++) Assert.Equal(PlutoWarmCode.NoData, PlutoLearningPortAdapter.WarmCode(coldReport, (PlutoAssetClass)a, out _));

            var rec = new PlutoCandleRecorder(new FakeDailySource(), db, PlutoCandleCadence.Daily);
            foreach (var code in new[] { "005930", "000660" })
                rec.Fetch(Entry(PlutoScreenMarket.Krx, code), new PlutoAssetSample(), CancellationToken.None);

            var warm = new PlutoReplayWarmStarter(new PlutoCognitiveTensorLake(), new PlutoLocalCandleSource(db));
            var report = warm.Run();
            Assert.Equal(PlutoWarmStartState.Completed, report.State);
            Assert.Equal(PlutoWarmCode.Ready, PlutoLearningPortAdapter.WarmCode(report, PlutoAssetClass.Krx, out var lane));
            Assert.Equal(2, lane.Series);
            Assert.Equal(PlutoReplayWarmStarter.Simulations, lane.Simulations);
            Assert.Equal(PlutoWarmCode.NoData, PlutoLearningPortAdapter.WarmCode(report, PlutoAssetClass.Us, out _));
            Assert.Equal(PlutoWarmCode.NoData, PlutoLearningPortAdapter.WarmCode(report, PlutoAssetClass.Upbit, out _));
        }

        // ── ④ 어댑터 텐서 투영 ───────────────────────────────────────────────

        [Fact]
        public void Adapter_Maps_Warm_States_And_Tensor_Global_Fallback()
        {
            Assert.Equal(PlutoWarmCode.Off, PlutoLearningPortAdapter.WarmCode(null, PlutoAssetClass.Krx, out _));
            Assert.Equal(PlutoWarmCode.Pending, PlutoLearningPortAdapter.WarmCode(PlutoWarmStartReport.NotStarted, PlutoAssetClass.Krx, out _));
            var failed = new PlutoWarmStartReport(PlutoWarmStartState.Failed, 1, 0, 0, 0, Array.Empty<PlutoWarmStartLane>(), "IOException");
            Assert.Equal(PlutoWarmCode.Failed, PlutoLearningPortAdapter.WarmCode(failed, PlutoAssetClass.Us, out _));
            var timedOut = new PlutoWarmStartReport(PlutoWarmStartState.TimedOut, 1200, 100, 1, 0,
                new[] { new PlutoWarmStartLane(PlutoAssetClass.Krx, 3, 100, 60, 1, default) });
            Assert.Equal(PlutoWarmCode.Ready, PlutoLearningPortAdapter.WarmCode(timedOut, PlutoAssetClass.Krx, out _));
            Assert.Equal(PlutoWarmCode.TimedOut, PlutoLearningPortAdapter.WarmCode(timedOut, PlutoAssetClass.Upbit, out _));

            var tensor = new PlutoCognitiveTensorLake();
            var global = new PlutoCognitiveWeights(4, -10, 6);
            var view = PlutoLearningPortAdapter.MapTensor(tensor, timedOut, in global);
            Assert.Equal(0, view.ActiveRegimeId);
            Assert.Null(view.ActiveRegimeName);                                    // 판정 전 = 정직 null
            Assert.Equal(3, view.Lanes.Count);
            Assert.All(view.Lanes, l => Assert.Equal((PlutoTensorSourceCode.Global, -10, 6), (l.Source, l.MomentumTiltPct, l.DevilStrictnessPts)));
            Assert.Equal((3, 100), (view.Lanes[0].WarmSeries, view.Lanes[0].WarmSimulations));
            Assert.Equal((0, 0), (view.Lanes[2].WarmSeries, view.Lanes[2].WarmSimulations));

            tensor.Activate(3);
            var active = PlutoLearningPortAdapter.MapTensor(tensor, null, in global);
            Assert.Equal(3, active.ActiveRegimeId);
            Assert.Equal(PlutoRegimeVectorLake.Get(3)!.Name, active.ActiveRegimeName);
            Assert.All(active.Lanes, l => Assert.Equal(PlutoWarmCode.Off, l.Warm));
        }

        // ── ⑤ 페이지 · 순수성 · 호스트 ───────────────────────────────────────

        private sealed class FixedLearningPort : IPlutoLearningPort
        {
            public FixedLearningPort(PlutoLearningView v) => Current = v;
            public bool IsWired => true;
            public PlutoLearningView Current { get; }
        }

        private static (Bunit.TestContext, IRenderedComponent<DualArenaPage>) Render(IPlutoLearningPort learning)
        {
            var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            var f = new KrxArenaQuoteFeeder(krx);
            Assert.True(f.SelectSource(KrxArenaFeed.Synthetic));
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, f));
            var us = new UsTechArenaEngine();
            var uf = new UsArenaQuoteFeeder(us);
            Assert.True(uf.SelectSource(UsArenaFeed.Synthetic));
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(us, uf));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton(learning);
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        private static PlutoLearningView WithTensor() => new(true, PlutoLearningHealth.Live, null, -1, false, 2, 4, 1, 30, 2, 0,
            Tensor: new PlutoTensorView(3, "테스트 국면", 2, new[]
            {
                new PlutoTensorLaneView(PlutoRecoMarket.Krx, PlutoTensorSourceCode.Regime, 3, 12, 2, PlutoWarmCode.Ready, 4, 100),
                new PlutoTensorLaneView(PlutoRecoMarket.Us, PlutoTensorSourceCode.Baseline, 1, -5, 0, PlutoWarmCode.NoData, 0, 0),
                new PlutoTensorLaneView(PlutoRecoMarket.Crypto, PlutoTensorSourceCode.Global, 2, 0, -1, PlutoWarmCode.Pending, 0, 0),
            }));

        [Fact]
        public void DualArenaPage_Briefing_Shows_Tensor_Lanes_Weights_And_Warm_State()
        {
            var (ctx, cut) = Render(new FixedLearningPort(WithTensor()));
            using var _ = ctx;
            Assert.Empty(cut.FindAll(".da-tensor"));                              // 닫힘 = 렌더 0
            cut.Find(".da-brief-toggle").Click();
            var panel = cut.Find(".da-brief .da-tensor");
            Assert.Equal("wired", panel.GetAttribute("data-tensor"));
            Assert.Equal("3", panel.GetAttribute("data-tensor-regime"));
            Assert.Contains("03 테스트 국면", panel.TextContent);
            var rows = cut.FindAll(".da-tensor-grid tbody tr");
            Assert.Equal(3, rows.Count);
            Assert.Contains("KRX", rows[0].TextContent);
            Assert.Contains("국면 슬롯", rows[0].TextContent);
            Assert.Contains("+12%", rows[0].TextContent);
            Assert.Contains("+2점", rows[0].TextContent);
            Assert.Contains("WarmReady", rows[0].TextContent);
            Assert.Contains("리플레이 100회", rows[0].TextContent);
            Assert.Contains("up", rows[0].QuerySelector("[data-tensor-mom]")!.ClassList);     // 상승 적색
            Assert.Contains("down", rows[1].QuerySelector("[data-tensor-mom]")!.ClassList);   // 하강 청색
            Assert.Equal(PlutoWarmCode.NoData.ToString(), rows[1].GetAttribute("data-tensor-warm"));
            Assert.Contains("NoData", rows[1].TextContent);
            Assert.Contains("Upbit", rows[2].TextContent);
            Assert.Contains("웜스타트 대기", rows[2].TextContent);
        }

        [Fact]
        public void DualArenaPage_Briefing_Tensor_Unwired_Renders_Honestly()
        {
            var (ctx, cut) = Render(UnwiredPlutoLearningPort.Instance);
            using var _ = ctx;
            cut.Find(".da-brief-toggle").Click();
            var panel = cut.Find(".da-tensor");
            Assert.Equal("unwired", panel.GetAttribute("data-tensor"));
            Assert.Contains("인지 텐서 미결선", panel.TextContent);
            Assert.Empty(cut.FindAll(".da-tensor-grid"));                         // 가짜 가중치 0
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        [Fact]
        public void Rcl_Stays_Pure_No_New_Inject_And_Host_Wires_Recorder_And_Tensor()
        {
            string root = Root();
            string panel = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Components", "PlutoTensorPanel.razor"));
            Assert.DoesNotContain("@inject", panel);
            Assert.DoesNotContain("[Inject]", panel);
            Assert.DoesNotContain("StockManager.", panel);
            string page = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor"));
            Assert.Equal(6, page.Split('\n').Count(l => l.TrimStart().StartsWith("@inject ", StringComparison.Ordinal)));   // 주입 6종 불변
            Assert.Contains("<PlutoTensorPanel Tensor=\"@lvb.Tensor\" />", page);
            Assert.DoesNotContain("StockManager.Engines", File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Ports", "Trading", "PlutoLearningPort.cs")));

            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("cfg.GetBool(\"Pluto.CandleRecorderEnabled\", true)", form);
            Assert.Contains("new StockManager.Adapters.PlutoCandleRecorder(src, db, cadence)", form);
            Assert.Contains("tensor: plutoTensor, warmStart: plutoWarmStart", form);
            string rec = File.ReadAllText(Path.Combine(root, "StockManager", "Adapters", "PlutoCandleRecorder.cs"));
            string code = string.Join("\n", rec.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            Assert.DoesNotContain("QuantumVault", code);
            Assert.DoesNotContain("HttpClient", code);                             // 레코더 = 네트워크 0
        }
    }
}
