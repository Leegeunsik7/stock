// ============================================================================
// UpbitStreamSoakTests — [v12.18.74-DEV] 업비트 공개 WebSocket 4종 결선 + 가상 24시간 무할당 소크(오프라인 · 송출 0)
//
//   §1 마켓 표: UpbitMarketSet 코드 형식/개수/중복 검증 · KRW-BTC 단독 페이로드 = 기존 SubscribePayload 바이트 동일 ·
//      아레나 표(StreamMarkets) 페이로드 원문 · 다중 마켓 판독(인덱스 · 표 밖 = Invalid) · 다중 마켓 파서 10만 프레임 δ=0
//   §2 투영: 마켓별 시장 상태 독립(다른 마켓의 이른 시각 = 수용) · 거래소 = 주 마켓만 · 기존 tap = 주 마켓만 ·
//      marketTap = 수용 프레임만(재접속 직전 체결 재전송 = 통지 0 → 아레나 누적 체결량 이중 가산 0) · 4종 → 아레나 4 슬롯
//   §3 가상 24시간 소크: 실 RunAsync 루프(연결 24회 · 가상 1시간당 1회 재접속 · 509B 조각 · 연결마다 SNAPSHOT 재전송 + PING 응답 +
//      잘린 프레임 + 표 밖 마켓 + 64KB 초과 메시지) → 4종 시장 상태 → 아레나 주입기 → 엔진 정산(8 프레임마다 펌프).
//      프레임 1,036,800(24h × 12/s) 전량 동기 처리 · 계수 정확값 · 체결량 무손실/무중복 · 마켓별 최종 상태 = 생성기 최종값 ·
//      δ=0 = 같은 연결 일정에서 프레임 수만 다른 두 실행(9,600 vs 1,036,800)의 할당 바이트 차 = 0(연결당 콜드 비용은 상쇄)
// ============================================================================
using System;
using System.Buffers.Text;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Feeds.Crypto;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Feeds
{
    [Collection("ConcurrencyStress")]
    public sealed class UpbitStreamSoakTests
    {
        private const long S = UpbitTickAligner.Scale;
        private readonly ITestOutputHelper _out;
        public UpbitStreamSoakTests(ITestOutputHelper o) => _out = o;

        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

        private static string ObAt(string code, long tsMs, long bidWon, long askWon) =>
            "{\"type\":\"orderbook\",\"code\":\"" + code + "\",\"timestamp\":" + tsMs + ",\"orderbook_units\":[{\"ask_price\":" + askWon + ",\"bid_price\":" + bidWon +
            ",\"ask_size\":0.5,\"bid_size\":0.25}],\"stream_type\":\"REALTIME\"}";

        private static string TrAt(string code, long tsMs, long seq, long priceWon, string side = "BID") =>
            "{\"type\":\"trade\",\"code\":\"" + code + "\",\"timestamp\":" + tsMs + ",\"trade_timestamp\":" + tsMs + ",\"trade_price\":" + priceWon +
            ",\"trade_volume\":0.001,\"ask_bid\":\"" + side + "\",\"sequential_id\":" + seq + ",\"stream_type\":\"REALTIME\"}";

        // ════════════ §1 마켓 표 ════════════

        [Fact]
        public void MarketSet_Validates_Codes_Count_And_Duplicates()
        {
            Assert.Throws<ArgumentNullException>(() => new UpbitMarketSet(null!));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UpbitMarketSet());
            Assert.Throws<ArgumentOutOfRangeException>(() => new UpbitMarketSet("KRW-A", "KRW-B", "KRW-C", "KRW-D", "KRW-E", "KRW-F", "KRW-G", "KRW-H", "KRW-I"));
            Assert.Throws<ArgumentException>(() => new UpbitMarketSet("KRW-BTC", "KRW-BTC"));
            foreach (var bad in new[] { "", "KRWBTC", "-BTC", "KRW-", "krw-btc", "KRW-BTC-X", "KRW-B\"TC", "KRW-B\\TC", "KRW BTC", "ABCDEFGHI-BTC", "KRW-ABCDEFGHIJK", "KRW-비트" })
            {
                Assert.False(UpbitMarketSet.IsValidCode(bad), bad);
                Assert.Throws<ArgumentException>(() => new UpbitMarketSet(bad));
            }
            Assert.Throws<ArgumentNullException>(() => new UpbitMarketSet("KRW-BTC", null!));
            foreach (var ok in new[] { "KRW-BTC", "BTC-ETH", "USDT-XRP", "KRW-SHIB", "KRW-1INCH" }) Assert.True(UpbitMarketSet.IsValidCode(ok), ok);
            Assert.Equal(8, new UpbitMarketSet("KRW-A", "KRW-B", "KRW-C", "KRW-D", "KRW-E", "KRW-F", "KRW-G", "KRW-H").Count);
        }

        [Fact]
        public void KrwBtc_Default_Payload_Is_Byte_Identical_And_Arena_Payload_Lists_Four_Markets()
        {
            Assert.Equal(UpbitStreamProtocol.SubscribePayload.ToArray(), UpbitMarketSet.KrwBtc.SubscribePayload.ToArray());
            Assert.Same(UpbitMarketSet.KrwBtc, new UpbitFeedOptions().Markets);
            var m = CryptoArenaUniverse.StreamMarkets;
            Assert.Equal(CryptoArenaUniverse.Count, m.Count);
            for (int i = 0; i < m.Count; i++) Assert.Equal(CryptoArenaUniverse.Market(i), m.Market(i));
            Assert.Equal("[{\"ticket\":\"SM-UPBIT-STREAM\"},{\"type\":\"orderbook\",\"codes\":[\"KRW-BTC\",\"KRW-ETH\",\"KRW-XRP\",\"KRW-SOL\"]}," +
                         "{\"type\":\"trade\",\"codes\":[\"KRW-BTC\",\"KRW-ETH\",\"KRW-XRP\",\"KRW-SOL\"]}]", Encoding.UTF8.GetString(m.SubscribePayload.Span));
        }

        [Fact]
        public void MultiMarket_Parse_Returns_Index_And_Rejects_Unlisted_Market()
        {
            var set = CryptoArenaUniverse.StreamMarkets;
            Assert.Equal(UpbitStreamKind.Orderbook, UpbitStreamProtocol.TryParse(U(ObAt("KRW-XRP", 1_761_000_000_000, 3_500, 3_501)), set, out int i, out var f));
            Assert.Equal((CryptoArenaUniverse.Xrp, 3_500 * S, 3_501 * S), (i, f.BidRaw, f.AskRaw));
            Assert.Equal(UpbitStreamKind.Trade, UpbitStreamProtocol.TryParse(U(TrAt("KRW-SOL", 1_761_000_000_001, 7, 280_000, "ASK")), set, out i, out f));
            Assert.Equal((CryptoArenaUniverse.Sol, 280_000 * S, UpbitTradeSide.Ask, 7L), (i, f.TradePriceRaw, f.TradeSide, f.SequentialId));
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(U(ObAt("KRW-DOGE", 1_761_000_000_000, 300, 301)), set, out i, out f));
            Assert.Equal((-1, default(UpbitStreamFrame)), (i, f));
            Assert.Equal(UpbitStreamKind.Status, UpbitStreamProtocol.TryParse(U("{\"status\":\"UP\"}"), set, out i, out _));
            Assert.Equal(-1, i);
            // 이스케이프된 코드도 같은 값(ValueTextEquals 는 해제 후 비교)
            Assert.Equal(UpbitStreamKind.Orderbook, UpbitStreamProtocol.TryParse(U(ObAt("KRW\\u002DETH", 1_761_000_000_000, 5_500_000, 5_501_000)), set, out i, out _));
            Assert.Equal(CryptoArenaUniverse.Eth, i);
            // 단일 마켓 판독(기존 서명) = 다른 마켓 거부 그대로
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(U(ObAt("KRW-ETH", 1_761_000_000_000, 5_500_000, 5_501_000)), UpbitStreamProtocol.MarketKrwBtc, out _));
            Assert.Throws<ArgumentNullException>(() => UpbitStreamProtocol.TryParse(default, (UpbitMarketSet)null!, out _, out _));
        }

        [Fact]
        public void MultiMarket_Parser_100k_Frames_Zero_Alloc()
        {
            var set = CryptoArenaUniverse.StreamMarkets;
            byte[][] frames =
            {
                U(ObAt("KRW-BTC", 1_761_000_000_000, 150_000_000, 150_001_000)), U(TrAt("KRW-ETH", 1_761_000_000_000, 1, 5_500_000)),
                U(ObAt("KRW-XRP", 1_761_000_000_000, 3_500, 3_501)), U(TrAt("KRW-SOL", 1_761_000_000_000, 2, 280_000)), U(ObAt("KRW-DOGE", 1_761_000_000_000, 300, 301)),
            };
            long sum = 0;
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int k = 0; k < 100_000; k++)
                    if (UpbitStreamProtocol.TryParse(frames[k % frames.Length], set, out int i, out _) != UpbitStreamKind.Invalid) sum += i;
            });
            Assert.Equal(0, bytes);
            Assert.True(sum > 0);
        }

        // ════════════ §2 투영 ════════════

        [Fact]
        public void Per_Market_States_Are_Independent_Exchange_And_Legacy_Tap_Are_Primary_Only()
        {
            var ex = new CryptoPaperExchange();
            int legacy = 0;
            var notified = new int[CryptoArenaUniverse.Count];
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException(),
                options: new UpbitFeedOptions { Markets = CryptoArenaUniverse.StreamMarkets },
                tap: (in UpbitStreamFrame _) => legacy++, marketTap: (int m, in UpbitStreamFrame _) => notified[m]++);
            long t = 1_761_000_000_000;
            Assert.Equal(UpbitStreamKind.Orderbook, a.OnFrame(U(ObAt("KRW-BTC", t, 150_000_000, 150_001_000))));
            Assert.Equal(UpbitStreamKind.Orderbook, a.OnFrame(U(ObAt("KRW-ETH", t - 5_000, 5_500_000, 5_501_000))));   // 다른 마켓의 이른 시각 = 수용
            Assert.Equal(UpbitStreamKind.Orderbook, a.OnFrame(U(ObAt("KRW-ETH", t - 6_000, 5_400_000, 5_401_000))));   // 같은 마켓 역행 = 폐기
            Assert.Equal(UpbitStreamKind.Trade, a.OnFrame(U(TrAt("KRW-SOL", t, 9, 280_000))));
            Assert.Equal(UpbitStreamKind.Trade, a.OnFrame(U(TrAt("KRW-SOL", t, 9, 280_000))));                          // 재전송 = 폐기
            Assert.Equal(UpbitStreamKind.Trade, a.OnFrame(U(TrAt("KRW-BTC", t, 9, 150_001_000))));                      // 다른 마켓 같은 seq = 수용

            Assert.Equal((150_000_000 * S, 150_001_000 * S), (a.State.Snapshot().BidRaw, a.State.Snapshot().AskRaw));
            Assert.Same(a.State, a.StateOf(0));
            Assert.Equal(5_500_000 * S, a.StateOf(CryptoArenaUniverse.Eth).Snapshot().BidRaw);
            Assert.Equal(280_000 * S, a.StateOf(CryptoArenaUniverse.Sol).Snapshot().LastTradePriceRaw);
            Assert.Equal(0, a.StateOf(CryptoArenaUniverse.Xrp).Snapshot().QuoteCount);
            Assert.Equal(150_001_000 * S, ex.Snapshot().AskRaw);                                                         // 거래소 = 주 마켓(BTC)만
            Assert.Equal((1L, 1L, 0L, 0L), (a.QuotesOf(0), a.QuotesOf(1), a.QuotesOf(2), a.QuotesOf(3)));
            Assert.Equal((1L, 0L, 0L, 1L), (a.TradesOf(0), a.TradesOf(1), a.TradesOf(2), a.TradesOf(3)));
            Assert.Equal((2L, 1L, 2L, 1L), (a.QuotesFed, a.QuotesDropped, a.Trades, a.TradesDropped));
            Assert.Equal(2, legacy);                                                                                      // BTC 호가 + BTC 체결
            Assert.Equal(new[] { 2, 1, 0, 1 }, notified);                                                                 // 수용분만
        }

        [Fact]
        public async Task Four_Market_Stream_Feeds_Four_Arena_Slots_And_Reconnect_Replay_Is_Not_Double_Counted()
        {
            var e = new UpbitCryptoArenaEngine();
            var feeder = new CryptoArenaQuoteFeeder(e);
            long t = 1_761_274_800_000;
            byte[][] first =
            {
                U(ObAt("KRW-BTC", t, 150_000_000, 150_001_000)), U(ObAt("KRW-ETH", t, 5_500_000, 5_501_000)),
                U(ObAt("KRW-XRP", t, 3_500, 3_501)), U(ObAt("KRW-SOL", t, 280_000, 280_100)),
                U(TrAt("KRW-ETH", t + 1, 11, 5_501_000, "BID")),
            };
            byte[][] second =                                                                    // 재접속 SNAPSHOT = 직전 호가 + 직전 체결 재전송
            {
                U(ObAt("KRW-ETH", t, 5_500_000, 5_501_000)), U(TrAt("KRW-ETH", t + 1, 11, 5_501_000, "BID")),
                U(TrAt("KRW-ETH", t + 2, 12, 5_500_000, "ASK")),
            };
            int conn = 0;
            using var cts = new CancellationTokenSource();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(),
                factory: () => new ListTransport(conn++ == 0 ? first : second, conn >= 2 ? cts : null),
                options: new UpbitFeedOptions { Markets = CryptoArenaUniverse.StreamMarkets, KeepAliveTick = TimeSpan.Zero },
                delay: (_, _) => Task.CompletedTask, marketTap: feeder.OnUpbitMarketFrame);
            feeder.AttachStream(a);
            await a.RunAsync(cts.Token);

            Assert.Equal((2L, 2L, 0L), (a.Connects, a.Disconnects, a.Faults));
            Assert.Equal((2L, 1L), (a.Trades, a.TradesDropped));                                // 재전송 체결 1건 폐기
            Assert.Equal(5, a.QuotesOf(0) + a.QuotesOf(1) + a.QuotesOf(2) + a.QuotesOf(3));
            Assert.Equal(7, feeder.StreamUpdates);                                               // 호가 5 + 체결 2(재전송 0)
            Assert.Equal(0, feeder.DroppedQuotes);

            feeder.Pump(DateTime.UnixEpoch.Ticks + (t + 3) * TimeSpan.TicksPerMillisecond);
            Assert.True(e.TryReadSnapshot(out var s));
            Assert.Equal(4, s.Ticks);                                                           // 4 슬롯 전부 호가
            Assert.Equal(2, s.Trades);                                                          // 매수 1회 + 매도 1회(누적 차분)
            var q = new DuelQuote[4];
            Assert.Equal(4, e.CopyQuotes(q));
            for (int k = 0; k < 4; k++) Assert.Equal(CryptoArenaUniverse.Symbol(k), q[k].Symbol);
            Assert.Equal((3_500 * S, 3_501 * S), (q[CryptoArenaUniverse.Xrp].BidWon, q[CryptoArenaUniverse.Xrp].AskWon));
        }

        // ════════════ §3 가상 24시간 소크 ════════════

        private const int Conns = 24;                                                           // 가상 1시간당 1회 재접속
        private const int DayFramesPerConn = 43_200;                                            // 24 × 43,200 = 1,036,800 = 24h × 12 프레임/초
        private const int SmallFramesPerConn = 400;

        [Fact]
        public void Virtual_24h_Soak_Through_RunAsync_Is_Exact_Lossless_And_Zero_Alloc_Per_Frame()
        {
            RunDay(SmallFramesPerConn, out _);                                                  // JIT · 정적 초기화 예열(측정 밖)
            long small = ZeroAllocProbe.MinOf(() => { RunDay(SmallFramesPerConn, out var r); return r.Bytes; });
            long day = ZeroAllocProbe.MinOf(() => { RunDay(DayFramesPerConn, out var r); return r.Bytes - small; });
            RunDay(DayFramesPerConn, out var d);
            var a = d.Adapter;
            var s = d.Engine;
            _out.WriteLine($"frames={a.FramesIn:N0} quotes={a.QuotesFed:N0} trades={a.Trades:N0} replayDropped={a.TradesDropped} malformed={a.Malformed} oversized={a.Oversized} " +
                           $"status={a.StatusFrames} connects={a.Connects} smallRunBytes={small:N0} perFrameDelta={day}B ticks={s.Ticks:N0} engineTrades={s.Trades:N0} aiFills={s.Ai.Fills} " +
                           $"elapsed={d.ElapsedMs}ms");

            Assert.Equal(0, day);                                                               // 프레임 103만 추가 = 할당 0
            const long Normal = (long)Conns * DayFramesPerConn;
            Assert.Equal(Conns * (DayFramesPerConn + 3L) + (Conns - 1) * 8L, a.FramesIn);       // 정상 + 상태/잘림/표 밖(초과 메시지 = 미판독) + 재전송 8
            Assert.Equal(((long)Conns, (long)Conns, 0L, 0L, 0L), (a.Connects, a.Disconnects, a.Faults, a.Aborts, a.ErrorFrames));
            Assert.Equal(Conns - 1L, a.BackoffWaits);
            Assert.Equal((2L * Conns, (long)Conns, (long)Conns), (a.Malformed, a.StatusFrames, a.Oversized));
            Assert.Equal((Normal / 2, (Conns - 1) * 4L), (a.Trades, a.TradesDropped));          // 재전송 체결 = 전량 폐기
            Assert.Equal(Normal / 2 + (Conns - 1) * 4L, a.QuotesFed);                          // 재전송 호가(같은 시각) = 수용(멱등)
            Assert.Equal(0, a.QuotesDropped);
            for (int m = 0; m < CryptoArenaUniverse.Count; m++)
            {
                Assert.Equal(Normal / 8 + Conns - 1, a.QuotesOf(m));
                Assert.Equal(Normal / 8, a.TradesOf(m));
                var st = a.StateOf(m).Snapshot();
                Assert.Equal((d.Script.LastBid[m], d.Script.LastAsk[m], d.Script.LastTradePx[m], d.Script.LastTradeSeq[m]),
                             (st.BidRaw, st.AskRaw, st.LastTradePriceRaw, st.LastTradeSequentialId));
                Assert.Equal(d.Script.VolumeSum[m], d.Tap.VolumeSum[m]);                       // 체결량 무손실 · 무중복
            }
            Assert.Equal(a.QuotesFed + a.Trades, d.Feeder.StreamUpdates);
            Assert.Equal(0, d.Feeder.DroppedQuotes);
            Assert.Equal(Normal / 8, d.Feeder.Pumps);
            Assert.True(s.Ticks > 0 && s.Trades > 0);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
            var buf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            int n = d.EngineRef.CopyRationales(buf);
            for (int k = 0; k < n; k++) Assert.True(buf[k].IsWellFormed, buf[k].HumanReadableSummary);
        }

        private sealed record DayResult(long Bytes, UpbitWebSocketFeedAdapter Adapter, CryptoArenaQuoteFeeder Feeder, UpbitCryptoArenaEngine EngineRef,
                                        CryptoArenaSnapshot Engine, DayScript Script, CountingTap Tap, long ElapsedMs);

        private static void RunDay(int framesPerConn, out DayResult result)
        {
            var e = new UpbitCryptoArenaEngine();
            var feeder = new CryptoArenaQuoteFeeder(e);
            var tap = new CountingTap(feeder);
            var cts = new CancellationTokenSource();
            var script = new DayScript(framesPerConn, Conns, cts, feeder);
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), factory: script.Next,
                options: new UpbitFeedOptions { Markets = CryptoArenaUniverse.StreamMarkets, KeepAliveTick = TimeSpan.Zero },
                delay: static (_, _) => Task.CompletedTask, marketTap: tap.Tap);
            long t0 = Environment.TickCount64;
            long before = GC.GetAllocatedBytesForCurrentThread();
            Task run = a.RunAsync(cts.Token);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            long elapsed = Environment.TickCount64 - t0;
            Assert.True(run.IsCompletedSuccessfully, "대역 전송 = 동기 완료(측정 창 안에서 하루치 전량 처리)");
            Assert.True(e.TryReadSnapshot(out var snap));
            cts.Dispose();
            result = new DayResult(bytes, a, feeder, e, snap, script, tap, elapsed);
        }

        /// <summary>수용 프레임 체결량 집계 후 아레나 주입기로 전달(δ=0).</summary>
        private sealed class CountingTap
        {
            private readonly CryptoArenaQuoteFeeder _f;
            public readonly long[] VolumeSum = new long[CryptoArenaUniverse.Count];
            public CountingTap(CryptoArenaQuoteFeeder f) => _f = f;
            public void Tap(int m, in UpbitStreamFrame frame)
            {
                if (frame.Kind == UpbitStreamKind.Trade) VolumeSum[m] += frame.TradeVolumeRaw;
                _f.OnUpbitMarketFrame(m, in frame);
            }
        }

        /// <summary>
        /// 가상 하루 대본(연결 간 연속 상태) — 프레임을 고정 버퍼에 무할당 직렬화(Utf8Formatter) · 509B 조각 반환.
        /// 연결 1개 = [재전송 8(첫 연결 제외)] + 상태 + 잘림 + 표 밖 마켓 + 64KB 초과 + 정상 framesPerConn → 닫힘(마지막 연결 = 취소 후 닫힘).
        /// 정상 프레임 k: 마켓 = k % 4 · 종류 = (k / 4) % 2(호가/체결) · 시각 = 하루를 균등 분할 · 8 프레임마다 정산 펌프(같은 스레드 = 측정 포함).
        /// </summary>
        private sealed class DayScript
        {
            private const int Chunk = 509, OversizeBytes = 70_000;
            private static readonly byte[][] s_codes = { U("KRW-BTC"), U("KRW-ETH"), U("KRW-XRP"), U("KRW-SOL") };
            private const long T0 = 1_761_350_400_000;                                          // 2025-10-25 00:00 UTC

            private readonly int _framesPerConn, _conns;
            private readonly CancellationTokenSource _cts;
            private readonly CryptoArenaQuoteFeeder _feeder;
            private readonly long _totalFrames;
            private readonly byte[] _msg = new byte[1024];
            private readonly long[] _px = new long[4];
            private ulong _rng = 0x9E37_79B9_7F4A_7C15UL;
            private int _conn, _phase, _phaseLeft, _len, _off;
            private long _k;
            private bool _oversize;
            public readonly long[] LastBid = new long[4], LastAsk = new long[4], LastTradePx = new long[4], LastTradeSeq = new long[4], VolumeSum = new long[4];
            private readonly long[] _lastBookTs = new long[4], _lastTradeTs = new long[4], _lastTradeVol = new long[4], _lastBidSz = new long[4];
            private readonly bool[] _lastTradeBid = new bool[4];

            public DayScript(int framesPerConn, int conns, CancellationTokenSource cts, CryptoArenaQuoteFeeder feeder)
            {
                _framesPerConn = framesPerConn; _conns = conns; _cts = cts; _feeder = feeder;
                _totalFrames = (long)framesPerConn * conns;
                for (int m = 0; m < 4; m++) _px[m] = CryptoArenaUniverse.SyntheticStartWon(m) * S;
            }

            public IUpbitWsTransport Next()
            {
                _conn++;
                _phase = _conn == 1 ? 1 : 0;
                _phaseLeft = _phase == 0 ? 8 : 0;
                return new Transport(this);
            }

            private ulong Rnd() { ulong x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; return _rng = x; }

            // 다음 메시지 준비. false = 이 연결 종료(닫힘).
            private bool Prepare()
            {
                _off = 0; _oversize = false;
                while (true)
                {
                    switch (_phase)
                    {
                        case 0:                                                                 // SNAPSHOT 재전송(마켓별 직전 호가 + 직전 체결)
                            if (_phaseLeft == 0) { _phase = 1; continue; }
                            int r = 8 - _phaseLeft--;
                            if (r < 4) WriteBook(r, _lastBookTs[r], LastBid[r], LastAsk[r], _lastBidSz[r]);
                            else WriteTrade(r - 4, _lastTradeTs[r - 4], LastTradePx[r - 4], _lastTradeVol[r - 4], _lastTradeBid[r - 4], LastTradeSeq[r - 4]);
                            return true;
                        case 1: _len = Put(0, "{\"status\":\"UP\"}"u8); _phase = 2; return true;
                        case 2: _len = Put(0, "{\"type\":\"trade\",\"code\":\"KRW-BTC\",\"trade_price\":1"u8); _phase = 3; return true;
                        case 3:
                            _len = Put(0, "{\"type\":\"orderbook\",\"code\":\"KRW-DOGE\",\"timestamp\":1761350400000,\"orderbook_units\":[{\"ask_price\":301,\"bid_price\":300,\"ask_size\":1,\"bid_size\":1}]}"u8);
                            _phase = 4; return true;
                        case 4: _oversize = true; _len = OversizeBytes; _phase = 5; _phaseLeft = _framesPerConn; return true;
                        case 5:
                            if (_phaseLeft == 0) return false;
                            _phaseLeft--;
                            WriteNormal();
                            return true;
                    }
                }
            }

            private void WriteNormal()
            {
                long k = _k++;
                int m = (int)(k % 4);
                long ts = T0 + k * 86_400_000L / _totalFrames;
                if ((k / 4) % 2 == 0)
                {
                    long px = _px[m];
                    long unit = Math.Max(UpbitTickAligner.TickFor(px), px / 4000);
                    long start = CryptoArenaUniverse.SyntheticStartWon(m) * S;
                    px = UpbitTickAligner.AlignNearest(Math.Clamp(px + ((long)(Rnd() % 7) - 3) * unit, start * 7 / 10, start * 13 / 10));
                    _px[m] = px;
                    long ask = UpbitTickAligner.AlignUp(px + 1);
                    long size = (long)(Rnd() % 1_000_000_000) + 1;
                    LastBid[m] = px; LastAsk[m] = ask; _lastBookTs[m] = ts; _lastBidSz[m] = size;
                    WriteBook(m, ts, px, ask, size);
                }
                else
                {
                    bool bid = (Rnd() & 1) == 0;
                    long tp = bid ? LastAsk[m] : LastBid[m];
                    long vol = (long)(Rnd() % 50_000_000) + 1;
                    LastTradePx[m] = tp; LastTradeSeq[m] = k + 1; _lastTradeTs[m] = ts; _lastTradeVol[m] = vol; _lastTradeBid[m] = bid;
                    VolumeSum[m] += vol;
                    WriteTrade(m, ts, tp, vol, bid, k + 1);
                }
                if ((k & 7) == 7) _feeder.Pump(DateTime.UnixEpoch.Ticks + ts * TimeSpan.TicksPerMillisecond);
            }

            private void WriteBook(int m, long ts, long bid, long ask, long bidSz)
            {
                int p = Put(0, "{\"type\":\"orderbook\",\"code\":\""u8);
                p = Put(p, s_codes[m]);
                p = Put(p, "\",\"timestamp\":"u8); p = Num(p, ts);
                p = Put(p, ",\"total_ask_size\":12.5,\"total_bid_size\":7.25,\"orderbook_units\":[{\"ask_price\":"u8); p = Raw(p, ask);
                p = Put(p, ",\"bid_price\":"u8); p = Raw(p, bid);
                p = Put(p, ",\"ask_size\":0.5,\"bid_size\":"u8); p = Raw(p, bidSz);
                p = Put(p, "},{\"ask_price\":"u8); p = Raw(p, UpbitTickAligner.AlignUp(ask + 1));
                p = Put(p, ",\"bid_price\":"u8); p = Raw(p, UpbitTickAligner.AlignDown(bid - 1));
                p = Put(p, ",\"ask_size\":0.1,\"bid_size\":0.2}],\"stream_type\":\"REALTIME\",\"level\":0}"u8);
                _len = p;
            }

            private void WriteTrade(int m, long ts, long px, long vol, bool bid, long seq)
            {
                int p = Put(0, "{\"type\":\"trade\",\"code\":\""u8);
                p = Put(p, s_codes[m]);
                p = Put(p, "\",\"timestamp\":"u8); p = Num(p, ts + 3);
                p = Put(p, ",\"trade_date\":\"2025-10-25\",\"trade_time\":\"00:00:00\",\"trade_timestamp\":"u8); p = Num(p, ts);
                p = Put(p, ",\"trade_price\":"u8); p = Raw(p, px);
                p = Put(p, ",\"trade_volume\":"u8); p = Raw(p, vol);
                p = Put(p, bid ? ",\"ask_bid\":\"BID\",\"sequential_id\":"u8 : ",\"ask_bid\":\"ASK\",\"sequential_id\":"u8); p = Num(p, seq);
                p = Put(p, ",\"stream_type\":\"REALTIME\"}"u8);
                _len = p;
            }

            private int Put(int p, ReadOnlySpan<byte> s) { s.CopyTo(_msg.AsSpan(p)); return p + s.Length; }
            private int Num(int p, long v) { Utf8Formatter.TryFormat(v, _msg.AsSpan(p), out int n); return p + n; }
            private int Raw(int p, long raw)
            {
                p = Num(p, raw / S);
                long frac = raw % S;
                if (frac == 0) return p;
                _msg[p++] = (byte)'.';
                Utf8Formatter.TryFormat(frac, _msg.AsSpan(p), out int n, new System.Buffers.StandardFormat('D', 8));
                return p + n;
            }

            private ValueTask<(int Count, bool EndOfMessage, bool Closed)> Receive(Memory<byte> buffer, ref bool open)
            {
                if (!open) return new ValueTask<(int, bool, bool)>((0, true, true));
                if (_off == 0 && !Prepare())
                {
                    open = false;
                    if (_conn == _conns) _cts.Cancel();                                         // 하루 끝 = 루프 종료
                    return new ValueTask<(int, bool, bool)>((0, true, true));
                }
                int n = Math.Min(Math.Min(Chunk, _len - _off), buffer.Length);
                if (_oversize) buffer.Span.Slice(0, n).Fill((byte)' ');
                else _msg.AsSpan(_off, n).CopyTo(buffer.Span);
                _off += n;
                bool end = _off == _len;
                if (end) _off = 0;
                return new ValueTask<(int, bool, bool)>((n, end, false));
            }

            private sealed class Transport : IUpbitWsTransport
            {
                private readonly DayScript _s;
                private bool _open = true;
                public Transport(DayScript s) => _s = s;
                public Task ConnectAsync(Uri uri, CancellationToken ct) { UpbitStreamProtocol.EnsureEndpoint(uri); return Task.CompletedTask; }
                public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
                public ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct) => _s.Receive(buffer, ref _open);
                public void Dispose() { }
            }
        }

        /// <summary>고정 프레임 목록 전송 대역(연결당 1회 순회 후 닫힘 · stop ≠ null = 닫기 전 취소).</summary>
        private sealed class ListTransport : IUpbitWsTransport
        {
            private readonly byte[][] _frames;
            private readonly CancellationTokenSource? _stop;
            private int _i;
            public ListTransport(byte[][] frames, CancellationTokenSource? stop) { _frames = frames; _stop = stop; }
            public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;
            public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;
            public ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            {
                if (_i == _frames.Length) { _stop?.Cancel(); return new ValueTask<(int, bool, bool)>((0, true, true)); }
                var f = _frames[_i++];
                f.CopyTo(buffer);
                return new ValueTask<(int, bool, bool)>((f.Length, true, false));
            }
            public void Dispose() { }
        }
    }
}
