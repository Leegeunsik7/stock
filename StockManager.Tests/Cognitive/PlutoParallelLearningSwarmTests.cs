// ============================================================================
// PlutoParallelLearningSwarmTests — v12.18.80-DEV: 병렬 학습 스웜 · 420슬롯 매트릭스 · 데이터 레이크 · 업비트 공개 REST 실데이터 소스
//   ① 업비트 프로토콜(실 응답 형태): 캔들/체결/호가 파싱 · 시간 순서 반전 · raw 10^8 · 형식 오류/미상 방향/역전 호가 = 실패(fail-closed).
//   ② 소스: GET 3회(인증 헤더 0 · api.upbit.com) · 429 = RateLimited(후속 조회 0) · 페이서 초당 8회 이하 · 한도 초과 게이트 거부.
//   ③ 빌더: 봉 → 스퀴즈/프로파일 · 체결 → CVD · 15호가 → 뎁스 · 가격 = 매도 1호가 · 국면 = 판정 전 · 봉/호가 없음 = 판독 불가.
//   ④ 매트릭스: 배치 KRX 0 · US 200 · 코인 300 · 키 → 슬롯 · 상태 계수 · Live 아님 = 입력 폐기 · 스캐너 = Live 만 · 병합/조건부 유니버스.
//   ⑤ 레이크: 봉 교체/비움 · 최신 우선 복사 · 수집 누계.
//   ⑥ 스웜: 미결선 = 실피드 연결 대기 · 키 인코딩 불가 = 조회 0 · 429 = 스윕 중단 + 쿨다운 + 남은 인덱스 폐기 · 전용 스레드 BelowNormal 전수 스윕 · 듀티 스로틀.
//   ⑦ 스크리너 결선(매트릭스 Live → 코인 Top · KRX 미결선 = 판독 대기 200) · 포트 어댑터/뱃지 · DualArenaPage 스웜 뱃지 · 호스트 결선 문자열.
//   ⑧ δ=0: 동시 기록 중 매트릭스 TryRead/TryFindSlot/상태 계수 10만 회 = 0B · 찢어진 판독 0 · 레이크 TryCopyBars 10만 회 = 0B.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Screening;
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Cognitive.Valuation;
using StockManager.Engines.Feeds.Crypto;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoParallelLearningSwarmTests
    {
        private const long S = PlutoDiscoveryScorer.CryptoScale;
        private static readonly long T0 = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc).Ticks;
        private static readonly long Btc = CryptoArenaUniverse.Pack("KRW-BTC"), Eth = CryptoArenaUniverse.Pack("KRW-ETH");

        // ── ① 업비트 프로토콜 ─────────────────────────────────────────────────

        private const string CandlesJson =
            "[{\"market\":\"KRW-BTC\",\"candle_date_time_utc\":\"2026-10-25T07:24:00\",\"candle_date_time_kst\":\"2026-10-25T16:24:00\",\"opening_price\":108134000.00000000," +
            "\"high_price\":108134000.00000000,\"low_price\":108085000.00000000,\"trade_price\":108085000.00000000,\"timestamp\":1792913041025," +
            "\"candle_acc_trade_price\":1493199.43279000,\"candle_acc_trade_volume\":0.01381298,\"unit\":1}," +
            "{\"market\":\"KRW-BTC\",\"candle_date_time_utc\":\"2026-10-25T07:23:00\",\"candle_date_time_kst\":\"2026-10-25T16:23:00\",\"opening_price\":108000000.0," +
            "\"high_price\":108200000.0,\"low_price\":107900000.0,\"trade_price\":108134000.0,\"timestamp\":1792912980000," +
            "\"candle_acc_trade_price\":1.0,\"candle_acc_trade_volume\":1.5,\"unit\":1}]";

        private const string TradesJson =
            "[{\"market\":\"KRW-BTC\",\"trade_date_utc\":\"2026-10-25\",\"trade_time_utc\":\"07:24:00\",\"timestamp\":1792913040983,\"trade_price\":108085000.00000000," +
            "\"trade_volume\":0.00925197,\"prev_closing_price\":108517000.00000000,\"change_price\":-432000.00000000,\"ask_bid\":\"ASK\",\"sequential_id\":17929130409830000}," +
            "{\"market\":\"KRW-BTC\",\"trade_date_utc\":\"2026-10-25\",\"trade_time_utc\":\"07:23:59\",\"timestamp\":1792913039000,\"trade_price\":108134000.0," +
            "\"trade_volume\":0.5,\"prev_closing_price\":1.0,\"change_price\":1.0,\"ask_bid\":\"BID\",\"sequential_id\":1}]";

        private static string OrderbookJson(int tiers, long bestBidWon = 108085000, long bestAskWon = 108134000)
        {
            var sb = new StringBuilder("[{\"market\":\"KRW-BTC\",\"timestamp\":1792913046770,\"total_ask_size\":1.75,\"total_bid_size\":1.93,\"orderbook_units\":[");
            for (int i = 0; i < tiers; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"bid_price\":").Append(bestBidWon - i * 1000).Append(",\"bid_size\":0.").Append(i + 1)
                  .Append(",\"ask_price\":").Append(bestAskWon + i * 1000).Append(",\"ask_size\":0.0").Append(i + 1).Append('}');
            }
            return sb.Append("],\"level\":0}]").ToString();
        }

        [Fact]
        public void Protocol_Parses_Real_Upbit_Shapes_Oldest_First_In_Raw()
        {
            var bars = new PlutoOhlcvBar[8];
            Assert.Equal(2, UpbitCandleProtocol.ParseCandles(Encoding.UTF8.GetBytes(CandlesJson), bars));
            Assert.Equal(108_134_000 * S, bars[0].Close);                          // 07:23 봉이 먼저(오래된 것부터)
            Assert.Equal(150_000_000, bars[0].Volume);                             // 1.5 코인 = raw 10^8
            Assert.Equal(108_085_000 * S, bars[1].Close);
            Assert.Equal(1_381_298, bars[1].Volume);
            Assert.True(bars[0].TimeUtcTicks < bars[1].TimeUtcTicks);
            Assert.True(bars[0].IsValid && bars[1].IsValid);

            var trades = new PlutoSideTrade[8];
            Assert.Equal(2, UpbitCandleProtocol.ParseTrades(Encoding.UTF8.GetBytes(TradesJson), trades));
            Assert.Equal(1, trades[0].Side);                                       // BID = 매수 테이커(오래된 체결)
            Assert.Equal(-1, trades[1].Side);                                      // ASK = 매도 테이커
            Assert.Equal(925_197, trades[1].Volume);

            var bid = new long[15];
            var ask = new long[15];
            Assert.Equal(15, UpbitCandleProtocol.ParseOrderbook(Encoding.UTF8.GetBytes(OrderbookJson(20)), bid, ask, out long bb, out long ba));
            Assert.Equal(108_085_000 * S, bb);
            Assert.Equal(108_134_000 * S, ba);
            Assert.Equal(10_000_000, bid[0]);                                      // 0.1
            Assert.Equal(1_000_000, ask[0]);                                       // 0.01
        }

        [Theory]
        [InlineData("{}")]
        [InlineData("[{\"trade_price\":-1}]")]
        [InlineData("[{\"trade_price\":\"abc\"}]")]
        [InlineData("[{\"trade_price\":1")]
        [InlineData("[1,2]")]
        public void Protocol_Malformed_Candles_Fail_Closed(string json)
            => Assert.Equal(-1, UpbitCandleProtocol.ParseCandles(Encoding.UTF8.GetBytes(json), new PlutoOhlcvBar[4]));

        [Fact]
        public void Protocol_Unknown_Side_Inverted_Book_Empty_Book()
        {
            Assert.Equal(-1, UpbitCandleProtocol.ParseTrades(Encoding.UTF8.GetBytes("[{\"trade_price\":1.0,\"trade_volume\":1.0,\"timestamp\":1792913040983,\"ask_bid\":\"X\"}]"), new PlutoSideTrade[2]));
            var b = new long[15];
            var a = new long[15];
            Assert.Equal(-1, UpbitCandleProtocol.ParseOrderbook(Encoding.UTF8.GetBytes(OrderbookJson(3, 200, 100)), b, a, out long bb, out long ba));
            Assert.Equal(0, bb + ba);                                              // 역전 호가 = 판독 0
            Assert.Equal(0, UpbitCandleProtocol.ParseOrderbook(Encoding.UTF8.GetBytes("[]"), b, a, out _, out _));
        }

        // ── ② 소스 ────────────────────────────────────────────────────────────

        private sealed class FakeUpbit : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Requests = new();
            public HttpStatusCode CandleStatus = HttpStatusCode.OK;

            protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (Requests) Requests.Add(request);
                string path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("/candles/minutes/1", StringComparison.Ordinal) && CandleStatus != HttpStatusCode.OK) return new HttpResponseMessage(CandleStatus);
                string body = path.EndsWith("/candles/minutes/1", StringComparison.Ordinal) ? CandlesJson
                    : path.EndsWith("/trades/ticks", StringComparison.Ordinal) ? TradesJson
                    : path.EndsWith("/orderbook", StringComparison.Ordinal) ? OrderbookJson(15) : "[]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(Send(request, cancellationToken));
        }

        private static PlutoUniverseEntry BtcEntry() => PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Crypto)[IndexOf(PlutoScreenMarket.Crypto, "KRW-BTC")];

        private static int IndexOf(PlutoScreenMarket m, string code)
        {
            var list = PlutoMultiAssetUniverseCatalog.Of(m);
            for (int i = 0; i < list.Count; i++) if (list[i].Code == code) return i;
            return -1;
        }

        [Fact]
        public void Source_Issues_Three_Public_Gets_And_Fills_Sample()
        {
            var fake = new FakeUpbit();
            var src = new UpbitPublicCandleSource(new HttpClient(fake), new PlutoRequestPacer(10));
            var sample = new PlutoAssetSample();
            Assert.Equal(PlutoFetchOutcome.Ok, src.Fetch(BtcEntry(), sample, CancellationToken.None));
            Assert.Equal(3, fake.Requests.Count);
            foreach (var r in fake.Requests)
            {
                Assert.Equal(HttpMethod.Get, r.Method);
                Assert.Equal("api.upbit.com", r.RequestUri!.Host);
                Assert.Null(r.Headers.Authorization);                              // 인증 0(공개 시세)
                Assert.Contains("market", r.RequestUri.Query, StringComparison.Ordinal);
            }
            Assert.Contains("count=200", fake.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
            Assert.Equal(2, sample.BarCount);
            Assert.Equal(2, sample.TradeCount);
            Assert.Equal(15, sample.DepthTiers);
            Assert.Equal(108_134_000 * S, sample.BestAsk);
            Assert.True(src.IsConnected);
            Assert.Contains("키 불필요", src.Label, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(HttpStatusCode.TooManyRequests, PlutoFetchOutcome.RateLimited)]
        [InlineData((HttpStatusCode)418, PlutoFetchOutcome.RateLimited)]
        [InlineData(HttpStatusCode.InternalServerError, PlutoFetchOutcome.Faulted)]
        public void Source_429_Stops_Without_Further_Requests(HttpStatusCode status, PlutoFetchOutcome expected)
        {
            var fake = new FakeUpbit { CandleStatus = status };
            var src = new UpbitPublicCandleSource(new HttpClient(fake), new PlutoRequestPacer(10));
            Assert.Equal(expected, src.Fetch(BtcEntry(), new PlutoAssetSample(), CancellationToken.None));
            Assert.Single(fake.Requests);                                          // 후속 조회 · 재시도 0
        }

        [Fact]
        public void Pacer_Caps_At_Eight_Per_Second_And_Gate_Above_Upbit_Limit_Is_Rejected()
        {
            var p = new PlutoRequestPacer(UpbitPublicCandleSource.DefaultPerSecond);
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 9; i++) Assert.True(p.Wait(CancellationToken.None));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds >= 950, "9회 = 간격 8개 × 125ms ≥ 1초 · 실측 " + sw.ElapsedMilliseconds + "ms");
            Assert.Equal(9, p.Granted);
            Assert.Throws<ArgumentOutOfRangeException>(() => new UpbitPublicCandleSource(new HttpClient(new FakeUpbit()), new PlutoRequestPacer(11)));
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var slow = new PlutoRequestPacer(1);
            Assert.True(slow.Wait(CancellationToken.None));
            Assert.False(slow.Wait(cts.Token));                                    // 취소 = 송신 0
        }

        // ── ③ 빌더 ────────────────────────────────────────────────────────────

        private static void FillSample(PlutoAssetSample s, int bars, long baseRaw, bool depth)
        {
            s.Clear();
            long step = baseRaw / 1000;
            for (int i = 0; i < bars; i++)
            {
                long c = baseRaw + (i % 5) * step;
                s.Bars[i] = new PlutoOhlcvBar(T0 + i * TimeSpan.TicksPerMinute, c, c + step, c - step, c, 100_000_000);
            }
            s.BarCount = bars;
            for (int i = 0; i < 40; i++) s.Trades[i] = new PlutoSideTrade(T0 + i * 20 * TimeSpan.TicksPerSecond, baseRaw, 10_000_000, i % 3 == 0 ? -1 : 1);
            s.TradeCount = 40;
            if (depth)
            {
                for (int i = 0; i < 15; i++) { s.BidQty[i] = 50_000_000; s.AskQty[i] = 40_000_000; }
                s.DepthTiers = 15;
                s.BestBid = baseRaw - step / 10;
                s.BestAsk = baseRaw;
            }
        }

        [Fact]
        public void Builder_Replays_Brains_From_Real_Sample()
        {
            var s = new PlutoAssetSample();
            FillSample(s, 60, 100_000_000 * S, depth: true);
            var x = PlutoBarFactorBuilder.Build(PlutoScreenMarket.Crypto, Btc, s, default);
            Assert.Equal(PlutoScreenMarket.Crypto, x.Market);
            Assert.Equal(Btc, x.Symbol);
            Assert.Equal(100_000_000 * S, x.Price);                               // 매도 1호가
            Assert.Equal(s.BestBid, x.Bid);
            Assert.True(x.SpreadBps >= 0);
            Assert.True(x.HasTechnical);
            Assert.True(x.HasMicro);
            Assert.Equal(15, x.DepthTiers);
            Assert.False(x.HasValuation);
            Assert.Equal(MarketRegime.Unknown, x.Regime);                          // 국면 = 판정 전(날조 0)

            s.Clear();
            Assert.Equal(0, PlutoBarFactorBuilder.Build(PlutoScreenMarket.Crypto, Btc, s, default).Symbol);   // 표본 0 = 판독 불가
            FillSample(s, 60, 100_000_000 * S, depth: false);
            s.TradeCount = 0;
            var noBook = PlutoBarFactorBuilder.Build(PlutoScreenMarket.Krx, 5930, s, default);
            Assert.Equal(s.Bars[59].Close, noBook.Price);                          // 호가 없음 = 마지막 종가
            Assert.Equal(0, noBook.Bid);
            Assert.False(noBook.HasMicro);                                         // 체결/호가 없음 = 미시구조 판독 없음
            Assert.Equal(0, PlutoBarFactorBuilder.Build(PlutoScreenMarket.Crypto, 0, s, default).Symbol);
        }

        // ── ④ 매트릭스 ──────────────────────────────────────────────────────────

        private static PlutoScreenInputs CoinInputs(long sym, long price, int factor = 80) =>
            new(PlutoScreenMarket.Crypto, sym, price, false, 0, 0, PlutoValuationGrade.Unknown, 0, false,
                true, factor, PlutoSqueezePhase.Unknown, PlutoProfileZone.None, true, factor, PlutoDivergenceType.None, true, true, false,
                MarketRegime.Unknown, factor, factor, factor, 3000, 15, price - price / 10_000);

        [Fact]
        public void Matrix_Layout_Lookup_Status_Counts_And_Scanner()
        {
            var b = new PlutoUniverseMatrixBoard();
            Assert.Equal(420, b.SlotCount);
            Assert.Equal(0, b.OffsetOf(PlutoScreenMarket.Krx));
            Assert.Equal(200, b.OffsetOf(PlutoScreenMarket.Us));
            Assert.Equal(300, b.OffsetOf(PlutoScreenMarket.Crypto));
            Assert.Equal(120, b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.Awaiting));   // 초기 = 전부 실피드 연결 대기
            Assert.True(b.TryFindSlot(PlutoScreenMarket.Crypto, Btc, out int slot));
            Assert.Equal(300 + IndexOf(PlutoScreenMarket.Crypto, "KRW-BTC"), slot);
            Assert.True(b.TryFindSlot(PlutoScreenMarket.Krx, 5930, out int ss));
            Assert.Equal(IndexOf(PlutoScreenMarket.Krx, "005930"), ss);
            Assert.False(b.TryFindSlot(PlutoScreenMarket.Krx, 999_999, out _));
            Assert.Equal(-1, b.SlotOf(PlutoScreenMarket.Us, 100));

            b.Write(slot, PlutoMatrixStatus.Live, CoinInputs(Btc, 100 * S), T0, 200);
            Assert.True(b.TryRead(slot, out var c));
            Assert.True(c.IsLive);
            Assert.Equal(200, c.Bars);
            Assert.Equal(1, b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.Live));
            Assert.Equal(119, b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.Awaiting));
            Assert.True(b.TryFindSlot(PlutoScreenMarket.Crypto, Eth, out int es));
            b.Write(es, PlutoMatrixStatus.Faulted, CoinInputs(Eth, 5 * S), T0, 0);
            Assert.True(b.TryRead(es, out var f));
            Assert.Equal(0, f.Inputs.Symbol);                                       // Live 아님 = 입력 폐기(오래된 판독 재사용 0)

            var dest = new PlutoScreenInputs[PlutoMarketScreener.MaxUniverse];
            Assert.Equal(1, b.Scanner(PlutoScreenMarket.Crypto)(dest));
            Assert.Equal(Btc, dest[0].Symbol);
            Assert.Equal(0, b.Scanner(PlutoScreenMarket.Krx)(dest));
        }

        [Fact]
        public void Merge_Dedupes_And_When_Gates_Synthetic_Arena()
        {
            PlutoUniverseScanner a = d => { d[0] = CoinInputs(Btc, 100 * S); return 1; };
            PlutoUniverseScanner bb = d => { d[0] = CoinInputs(Btc, 1 * S); d[1] = CoinInputs(Eth, 5 * S); return 2; };
            var dest = new PlutoScreenInputs[8];
            Assert.Equal(2, PlutoUniverseMerge.Merge(a, bb)(dest));
            Assert.Equal(100 * S, dest[0].Price);                                   // 중복 = primary 유지
            Assert.Equal(Eth, dest[1].Symbol);
            bool real = false;
            var gated = PlutoUniverseMerge.When(() => real, bb);
            Assert.Equal(0, gated(dest));                                           // 합성 데모 = 0건
            real = true;
            Assert.Equal(2, gated(dest));
        }

        // ── ⑤ 레이크 ──────────────────────────────────────────────────────────

        [Fact]
        public void Lake_Replaces_Clears_And_Counts()
        {
            var board = new PlutoUniverseMatrixBoard();
            var lake = new PlutoMultiAssetDataLake(board);
            var s = new PlutoAssetSample();
            FillSample(s, 60, 100 * S, depth: false);
            lake.Record(300, PlutoFetchOutcome.Ok, s.Bars.AsSpan(0, 60), T0);
            var dst = new PlutoOhlcvBar[10];
            Assert.Equal(10, lake.TryCopyBars(300, dst, out long asOf));
            Assert.Equal(T0, asOf);
            Assert.Equal(s.Bars[59].TimeUtcTicks, dst[9].TimeUtcTicks);           // 최신 우선 보존
            lake.Record(300, PlutoFetchOutcome.Faulted, ReadOnlySpan<PlutoOhlcvBar>.Empty, T0 + 1);
            Assert.Equal(0, lake.TryCopyBars(300, dst, out _));                     // 실패 = 비움
            lake.Record(301, PlutoFetchOutcome.RateLimited, ReadOnlySpan<PlutoOhlcvBar>.Empty, T0);
            lake.Record(302, PlutoFetchOutcome.NotConnected, ReadOnlySpan<PlutoOhlcvBar>.Empty, T0);
            var st = lake.StatsOf(PlutoScreenMarket.Crypto);
            Assert.Equal(3, st.Fetches);                                            // 미결선 = 조회 아님
            Assert.Equal(1, st.Ok);
            Assert.Equal(1, st.Faulted);
            Assert.Equal(1, st.RateLimited);
            Assert.Equal(PlutoFetchOutcome.RateLimited, lake.LastOutcome(301));
            Assert.Throws<ArgumentException>(() => new PlutoParallelLearningSwarm(new PlutoUniverseMatrixBoard(), lake));
        }

        // ── ⑥ 스웜 ────────────────────────────────────────────────────────────

        private sealed class FakeSource : IPlutoAssetDataSource
        {
            public PlutoScreenMarket Market { get; init; } = PlutoScreenMarket.Crypto;
            public bool IsConnected { get; init; } = true;
            public string Label => "fake";
            public int Calls;
            public int RateLimitAt = -1;
            public int Thread0Priority = -1;

            public PlutoFetchOutcome Fetch(PlutoUniverseEntry entry, PlutoAssetSample sample, CancellationToken ct)
            {
                int n = Interlocked.Increment(ref Calls);
                Interlocked.CompareExchange(ref Thread0Priority, (int)Thread.CurrentThread.Priority, -1);
                if (n - 1 == RateLimitAt) return PlutoFetchOutcome.RateLimited;
                FillSample(sample, 40, (100 + n) * S, depth: true);
                return PlutoFetchOutcome.Ok;
            }
        }

        private static (PlutoUniverseMatrixBoard, PlutoParallelLearningSwarm) NewSwarm(int duty = 100)
        {
            var b = new PlutoUniverseMatrixBoard();
            return (b, new PlutoParallelLearningSwarm(b, new PlutoMultiAssetDataLake(b), duty, TimeSpan.FromMinutes(1), () => T0));
        }

        [Fact]
        public void Swarm_Unwired_And_Unconnected_Lanes_Await_Without_Fetch()
        {
            var (b, sw) = NewSwarm();
            var us = new FakeSource { Market = PlutoScreenMarket.Us, IsConnected = false };
            sw.Attach(us);
            Assert.Equal(PlutoFetchOutcome.NotConnected, sw.ProcessOne(PlutoScreenMarket.Krx, 0, CancellationToken.None));
            Assert.Equal(PlutoFetchOutcome.NotConnected, sw.ProcessOne(PlutoScreenMarket.Us, 0, CancellationToken.None));
            Assert.Equal(0, us.Calls);
            Assert.Equal(200, b.CountOf(PlutoScreenMarket.Krx, PlutoMatrixStatus.Awaiting));
            var st = sw.StatusOf(PlutoScreenMarket.Krx);
            Assert.False(st.Attached);
            Assert.False(st.Connected);
            Assert.Equal("실데이터 소스 미결선", st.Label);
            Assert.False(sw.StatusOf(PlutoScreenMarket.Us).Connected);
        }

        [Fact]
        public void Swarm_Unkeyable_Market_Skips_Fetch_And_Live_Writes_Board_And_Lake()
        {
            var (b, sw) = NewSwarm();
            var src = new FakeSource();
            sw.Attach(src);
            int unkeyed = -1;
            var list = PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Crypto);
            for (int i = 0; i < list.Count; i++) if (list[i].Symbol == 0) { unkeyed = i; break; }
            Assert.True(unkeyed >= 0);
            Assert.Equal(PlutoFetchOutcome.NoData, sw.ProcessOne(PlutoScreenMarket.Crypto, unkeyed, CancellationToken.None));
            Assert.Equal(0, src.Calls);                                             // 레이트 예산 보존

            int bi = IndexOf(PlutoScreenMarket.Crypto, "KRW-BTC");
            Assert.Equal(PlutoFetchOutcome.Ok, sw.ProcessOne(PlutoScreenMarket.Crypto, bi, CancellationToken.None));
            Assert.True(b.TryRead(300 + bi, out var c));
            Assert.True(c.IsLive);
            Assert.Equal(Btc, c.Inputs.Symbol);
            Assert.Equal(40, c.Bars);
            Assert.Equal(40, sw.Lake.TryCopyBars(300 + bi, new PlutoOhlcvBar[200], out _));
        }

        [Fact]
        public void Swarm_RateLimit_Aborts_Sweep_Drops_Remaining_And_Cools_Down()
        {
            var (b, sw) = NewSwarm();
            var src = new FakeSource { RateLimitAt = 2 };
            sw.Attach(src);
            Assert.True(sw.RequestSweep(PlutoScreenMarket.Crypto));
            Assert.False(sw.RequestSweep(PlutoScreenMarket.Crypto));                 // 진행 중 = 중복 투입 0
            int bi = IndexOf(PlutoScreenMarket.Crypto, "KRW-BTC"), ei = IndexOf(PlutoScreenMarket.Crypto, "KRW-ETH");
            Assert.Equal(PlutoFetchOutcome.Ok, sw.ProcessOne(PlutoScreenMarket.Crypto, bi, CancellationToken.None));
            Assert.Equal(PlutoFetchOutcome.Ok, sw.ProcessOne(PlutoScreenMarket.Crypto, ei, CancellationToken.None));
            int xi = IndexOf(PlutoScreenMarket.Crypto, "KRW-XRP");
            Assert.Equal(PlutoFetchOutcome.RateLimited, sw.ProcessOne(PlutoScreenMarket.Crypto, xi, CancellationToken.None));
            var st = sw.StatusOf(PlutoScreenMarket.Crypto);
            Assert.True(st.CoolingDown);
            Assert.Equal(120, st.DroppedOnRateLimit);                               // 남은 인덱스 폐기(재시도 · 큐잉 0)
            Assert.Equal(1, st.Faulted);
            Assert.Equal(1, st.Stats.RateLimited);
            Assert.Equal(3, src.Calls);
            Assert.False(sw.RequestSweep(PlutoScreenMarket.Crypto));                 // 쿨다운 = 스윕 거부
        }

        [Fact]
        public void Swarm_Threads_Run_BelowNormal_And_Sweep_All_120_Coins()
        {
            var b = new PlutoUniverseMatrixBoard();
            using var sw = new PlutoParallelLearningSwarm(b, new PlutoMultiAssetDataLake(b), 100);
            var src = new FakeSource();
            sw.Attach(src);
            sw.Start(TimeSpan.FromHours(1));
            Assert.Throws<InvalidOperationException>(() => sw.Attach(new FakeSource()));
            var deadline = Stopwatch.StartNew();
            while (b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.Live) + b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.NoData) < 120
                   || b.CountOf(PlutoScreenMarket.Krx, PlutoMatrixStatus.Awaiting) < 200)
            {
                Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), "스윕 미완료");
                Thread.Sleep(20);
            }
            int unkeyed = 0;
            foreach (var e in PlutoMultiAssetUniverseCatalog.Of(PlutoScreenMarket.Crypto)) if (e.Symbol == 0) unkeyed++;
            Assert.Equal(120 - unkeyed, b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.Live));
            Assert.Equal(120 - unkeyed, Volatile.Read(ref src.Calls));
            Assert.Equal((int)ThreadPriority.BelowNormal, src.Thread0Priority);     // 워커 스레드 격리
            Assert.Equal(ThreadPriority.BelowNormal, sw.StatusOf(PlutoScreenMarket.Crypto).Priority);
            Assert.True(sw.Running);
            Assert.Equal(0, sw.WorkerFaults);
            sw.Dispose();
            Assert.False(sw.Running);
            sw.Dispose();                                                           // 멱등
        }

        [Fact]
        public void Duty_Throttle_Math()
        {
            long ms10 = Stopwatch.Frequency / 100;
            Assert.Equal(30, PlutoParallelLearningSwarm.ThrottleMillis(ms10, 25));   // 10ms × 75/25
            Assert.Equal(0, PlutoParallelLearningSwarm.ThrottleMillis(ms10, 100));
            Assert.Equal(2000, PlutoParallelLearningSwarm.ThrottleMillis(Stopwatch.Frequency * 10, 25));
            Assert.Throws<ArgumentOutOfRangeException>(() => NewSwarm(0));
        }

        // ── ⑦ 스크리너 · 포트 · UI · 호스트 ─────────────────────────────────────

        [Fact]
        public void Screener_Scores_Matrix_Live_Cells_And_Counts_Unwired_As_Awaiting()
        {
            var (b, sw) = NewSwarm();
            b.TryFindSlot(PlutoScreenMarket.Crypto, Btc, out int bs);
            b.TryFindSlot(PlutoScreenMarket.Crypto, Eth, out int es);
            b.Write(bs, PlutoMatrixStatus.Live, CoinInputs(Btc, 100_000_000 * S), T0, 200);
            b.Write(es, PlutoMatrixStatus.Live, CoinInputs(Eth, 5_000_000 * S, 10), T0, 200);
            var screener = new PlutoMarketScreener(new PlutoRecommendationBoard(), utcNow: () => T0);
            screener.AddUniverse(PlutoScreenMarket.Krx, b.Scanner(PlutoScreenMarket.Krx), 100_000_000);
            screener.AddUniverse(PlutoScreenMarket.Crypto, b.Scanner(PlutoScreenMarket.Crypto), 100_000_000);
            var snap = screener.Step();
            Assert.Equal(Btc, snap.Of(PlutoScreenMarket.Crypto)[0].Symbol);
            Assert.Equal(2, snap.TallyOf(PlutoScreenMarket.Crypto).Scanned);
            Assert.Equal(0, snap.TallyOf(PlutoScreenMarket.Krx).Scanned);
            Assert.Equal(200, snap.TallyOf(PlutoScreenMarket.Krx).Monitored);      // 미결선 = 판독 대기 200(합성 점수 0)

            var view = new PlutoRecommendationPortAdapter(screener, sw).Current;
            Assert.True(view.Swarm.Wired);
            Assert.False(view.Swarm.Running);
            Assert.Equal("감시 중 · 실피드 연결 대기", view.Swarm.Krx.StatusText);
            Assert.Equal(2, view.Swarm.Crypto.Live);
            Assert.Contains("KRX 감시 중 · 실피드 연결 대기", view.Swarm.BadgeText, StringComparison.Ordinal);
            Assert.Contains("BelowNormal", view.Swarm.BadgeText, StringComparison.Ordinal);
            Assert.False(new PlutoRecommendationPortAdapter(screener).Current.Swarm.Wired);
        }

        [Fact]
        public void Lane_View_Text_And_Css()
        {
            var live = new PlutoSwarmLaneView(PlutoRecoMarket.Crypto, true, "업비트", 120, 117, 0, 3, 0, true, false, 351, 0);
            Assert.Equal("실데이터 117/120 · 수집 중", live.StatusText);
            Assert.Equal("live", live.Css);
            Assert.Equal("warn", (live with { CoolingDown = true }).Css);
            Assert.StartsWith("⏸ 레이트 리밋 쿨다운(재시도 0)", (live with { CoolingDown = true }).StatusText, StringComparison.Ordinal);
            Assert.Equal("await", (live with { Connected = false }).Css);
            Assert.False(PlutoRecoBoardView.None.Swarm.Wired);
        }

        private sealed class FixedRecoPort : IPlutoRecommendationPort
        {
            public FixedRecoPort(PlutoRecoBoardView v) => Current = v;
            public bool IsWired => true;
            public PlutoRecoBoardView Current { get; }
        }

        [Fact]
        public void DualArenaPage_Renders_Swarm_Badges()
        {
            var empty = Array.AsReadOnly(Array.Empty<PlutoRecommendationCard>());
            var await0 = new PlutoSwarmLaneView(PlutoRecoMarket.Krx, false, "실데이터 소스 미결선", 200, 0, 200, 0, 0, false, false, 0, 0);
            var view = new PlutoRecoBoardView(true, 2, new DateTime(T0, DateTimeKind.Utc),
                new PlutoRecoMarketView(empty, 0, 0, 0, 0, 200, 0), new PlutoRecoMarketView(empty, 0, 0, 0, 0, 100, 0))
            {
                Crypto = new PlutoRecoMarketView(empty, 117, 3, 10, 104, 120, 0),
                Swarm = new PlutoSwarmView(true, true, 25, await0, await0 with { Market = PlutoRecoMarket.Us, Monitored = 100, Awaiting = 100 },
                    new PlutoSwarmLaneView(PlutoRecoMarket.Crypto, true, "업비트 공개 REST", 120, 117, 0, 3, 0, false, false, 351, 0)),
            };
            using var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx, new KrxArenaQuoteFeeder(krx)));
            var us = new UsTechArenaEngine();
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(us, new UsArenaQuoteFeeder(us)));
            var ce = new UpbitCryptoArenaEngine();
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(ce, new CryptoArenaQuoteFeeder(ce)));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(new FixedRecoPort(view));
            var cut = ctx.RenderComponent<DualArenaPage>();

            Assert.Equal("117", cut.Find(".da-reco-swarm").GetAttribute("data-swarm-live"));
            Assert.Equal("🧠 병렬 학습 스웜(BelowNormal · CPU 듀티 25%): KRX 감시 중 · 실피드 연결 대기 | US 감시 중 · 실피드 연결 대기 | Upbit 실데이터 117/120",
                cut.Find(".da-reco-swarm-badge").TextContent.Trim());
            Assert.Equal(3, cut.FindAll(".da-swarm-lane").Count);
            Assert.Contains("await", cut.Find(".da-swarm-lane[data-swarm-lane='0']").ClassList);
            Assert.Contains("live", cut.Find(".da-swarm-lane[data-swarm-lane='2']").ClassList);
            Assert.Contains("감시 200종목 · 피드 판독 대기 200 · 통과 0 · 감시 중 · 실피드 연결 대기", cut.Find(".da-reco-cover").TextContent);
            cut.Find(".da-reco-tab[data-reco-tab='crypto']").Click();
            Assert.Contains("감시 120종목 · 피드 판독 대기 3 · 통과 0 · 실데이터 117/120", cut.Find(".da-reco-cover").TextContent);
        }

        [Fact]
        public void Host_Wires_Swarm_Sources_And_Real_Feed_Gates()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string Read(params string[] p) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, System.IO.Path.Combine(p)));
            string form = Read("StockManager", "Blazor", "BlazorHomeForm.cs");
            Assert.Contains("cfg.GetBool(\"Pluto.SwarmEnabled\", true)", form);
            Assert.Contains("new StockManager.Engines.Feeds.Crypto.UpbitPublicCandleSource(HttpPool.Shared)", form);
            Assert.Contains("new StockManager.Adapters.KisDailyBarSource(kisForSwarm, kisSwarmPacer), plutoValuationBuffer)", form);
            Assert.Contains("cryptoArenaFeeder.AppliedSource == MS2E.UI.Portable.Ports.Trading.CryptoArenaFeed.UpbitStream", form);
            Assert.Contains("krxArenaFeeder.AppliedSource == MS2E.UI.Portable.Ports.Trading.KrxArenaFeed.Bridge", form);
            Assert.Contains("usArenaFeeder.AppliedSource == MS2E.UI.Portable.Ports.Trading.UsArenaFeed.KisOverseas", form);
            Assert.Contains("PlutoRecommendationPortAdapter(plutoScreener, plutoSwarm)", form);
            string upbit = Read("StockManager", "Engines", "Feeds", "Crypto", "UpbitPublicCandleSource.cs");
            Assert.DoesNotContain("Authorization", upbit);                          // 공개 시세 · 인증 0
            Assert.DoesNotContain("HttpMethod.Post", upbit);
            Assert.DoesNotContain("/orders", upbit);
            Assert.DoesNotContain("Newtonsoft", upbit);
            foreach (var f in new[] { "PlutoParallelLearningSwarm.cs", "PlutoUniverseMatrixBoard.cs", "PlutoMultiAssetDataLake.cs", "PlutoAssetDataSource.cs" })
            {
                string src = Read("StockManager", "Engines", "Cognitive", "Screening", f);
                Assert.DoesNotContain("QuantumVault.", src);                        // 헤더 불변식 문구 외 참조 0
                Assert.DoesNotContain("HttpClient", src);                            // 엔진 = 네트워크 0(소스 경계 밖)
            }
        }

        // ── ⑧ δ=0 ────────────────────────────────────────────────────────────

        [Fact]
        public void Matrix_Reads_During_Concurrent_Writes_Are_ZeroAlloc_And_Untorn()
        {
            var b = new PlutoUniverseMatrixBoard();
            Assert.True(b.TryFindSlot(PlutoScreenMarket.Crypto, Btc, out int slot));
            static PlutoScreenInputs Gen(long g) => new(PlutoScreenMarket.Crypto, Btc, g * S, false, 0, 0, PlutoValuationGrade.Unknown, 0, false,
                true, 1, PlutoSqueezePhase.Unknown, PlutoProfileZone.None, false, 0, PlutoDivergenceType.None, false, false, false,
                MarketRegime.Unknown, 0, 0, 0, 0, (int)g, g * S - 1);
            b.Write(slot, PlutoMatrixStatus.Live, Gen(2), T0 + 2, 2);                // 시드 = 불변식(가격 − 매수 = 1 · 가격/S = 봉 = 시각 오프셋 = 단 수) 충족
            int stop = 0, torn = 0;
            var writer = new Thread(() =>
            {
                long g = 2;
                while (Volatile.Read(ref stop) == 0)
                {
                    g = g >= 1_000_000 ? 2 : g + 1;
                    var x = Gen(g);
                    b.Write(slot, PlutoMatrixStatus.Live, in x, T0 + g, (int)g);
                }
            }) { IsBackground = true };
            writer.Start();
            void Reads(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (!b.TryFindSlot(PlutoScreenMarket.Crypto, Btc, out int s) || s != slot) { torn++; continue; }
                    if (b.TryRead(s, out var c))
                    {
                        var x = c.Inputs;
                        if (!c.IsLive || x.Symbol != Btc || x.Price - x.Bid != 1 || x.Price / S != c.Bars || c.AsOfUtcTicks - T0 != c.Bars || x.DepthTiers != c.Bars) torn++;
                    }
                    if (b.CountOf(PlutoScreenMarket.Crypto, PlutoMatrixStatus.Live) != 1) torn++;
                }
            }
            try
            {
                Reads(10_000);
                Assert.Equal(0, MinAllocated(() => Reads(100_000)));
            }
            finally
            {
                Volatile.Write(ref stop, 1);
                writer.Join();
            }
            Assert.Equal(0, torn);
            Assert.True(b.Writes > 1);
        }

        [Fact]
        public void Lake_CopyBars_Is_ZeroAlloc()
        {
            var b = new PlutoUniverseMatrixBoard();
            var lake = new PlutoMultiAssetDataLake(b);
            var s = new PlutoAssetSample();
            FillSample(s, 200, 100 * S, depth: false);
            lake.Record(5, PlutoFetchOutcome.Ok, s.Bars.AsSpan(0, 200), T0);
            var dst = new PlutoOhlcvBar[200];
            long sum = 0;
            void Copy(int n) { for (int i = 0; i < n; i++) sum += lake.TryCopyBars(5, dst, out _); }
            Copy(1_000);
            Assert.Equal(0, MinAllocated(() => Copy(100_000)));
            Assert.True(sum > 0);
        }
    }
}
