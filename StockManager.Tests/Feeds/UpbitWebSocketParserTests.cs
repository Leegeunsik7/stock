// ============================================================================
// UpbitWebSocketParserTests — [v12.18.56-DEV] 업비트 공개 WebSocket 피드 어댑터(오프라인 · 송출 0)
//
//   §1 파서: 업비트 DEFAULT 포맷 원시 orderbook/trade 프레임 정확값 · 필드 순서 무관 · status/error/unsupported 분류 ·
//      손상 프레임 표(잘림 · 뒤따르는 값 · 중복 필드 · 교차 호가 · 음수 · 문자열 숫자 · 시각 범위 · 깊이 폭탄 · 주석 · 비 UTF-8) = Invalid(예외 0) ·
//      무작위 변조 10만 회 예외 0 + 수용 프레임 불변식
//   §2 δ=0: 파서 10만 프레임 · OnFrame(시장 상태 + 거래소) 10만 프레임 · 수신 루프(조각 조립) 10만 프레임 · 서로 다른 체결 수용 경로 = 힙 0
//   §3 투영: 호가 → 시장 상태 + CryptoPaperExchange → 가상 매수/매도 정확값 · 시각 역행/재전송 폐기 · 손상 프레임 상태 무변경 ·
//      피드 정지 2초 초과 = StaleQuote(fail-closed) · seqlock 찢김 0(라이터 1 · 판독 4)
//   §4 연결: 버퍼 초과 메시지 폐기 · 지수 백오프(1 · 2 · 4 → 수신 실적 후 초기화) · 상한 · 전송 예외 무전파 · 침묵 단절 + PING ·
//      접속 대상 가드(정확 일치만) · 구독 페이로드 원문
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Feeds.Crypto;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Feeds
{
    [Collection("ConcurrencyStress")]
    public sealed class UpbitWebSocketParserTests
    {
        private const long S = UpbitTickAligner.Scale;
        private readonly ITestOutputHelper _out;
        public UpbitWebSocketParserTests(ITestOutputHelper o) => _out = o;

        // 업비트 개발자 문서 '호가(Orderbook)' · '체결(Trade)' WebSocket 응답 예시 형태(DEFAULT 포맷 · 필드 전부)
        private const string Ob =
            "{\"type\":\"orderbook\",\"code\":\"KRW-BTC\",\"timestamp\":1746601573804,\"total_ask_size\":4.79158413,\"total_bid_size\":2.65609625," +
            "\"orderbook_units\":[{\"ask_price\":145128000,\"bid_price\":145116000,\"ask_size\":0.02927645,\"bid_size\":0.01515405}," +
            "{\"ask_price\":145130000,\"bid_price\":145100000,\"ask_size\":0.1,\"bid_size\":0.2}],\"stream_type\":\"REALTIME\",\"level\":0}";

        private const string Tr =
            "{\"type\":\"trade\",\"code\":\"KRW-BTC\",\"timestamp\":1730336862082,\"trade_date\":\"2024-10-31\",\"trade_time\":\"01:07:42\"," +
            "\"trade_timestamp\":1730336862047,\"trade_price\":100473000.00000000,\"trade_volume\":0.00016185,\"ask_bid\":\"BID\"," +
            "\"prev_closing_price\":100571000.00000000,\"change\":\"FALL\",\"change_price\":98000.00000000,\"sequential_id\":17303368620470000," +
            "\"best_ask_price\":100473000,\"best_ask_size\":0.43139478,\"best_bid_price\":100465000,\"best_bid_size\":0.01990656,\"stream_type\":\"REALTIME\"}";

        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);
        private static UpbitStreamKind P(string s, out UpbitStreamFrame f) => UpbitStreamProtocol.TryParse(U(s), UpbitStreamProtocol.MarketKrwBtc, out f);

        private static string ObAt(long tsMs, long bidWon, long askWon) =>
            "{\"type\":\"orderbook\",\"code\":\"KRW-BTC\",\"timestamp\":" + tsMs + ",\"orderbook_units\":[{\"ask_price\":" + askWon + ",\"bid_price\":" + bidWon +
            ",\"ask_size\":0.5,\"bid_size\":0.25}],\"stream_type\":\"REALTIME\"}";

        private static string TrAt(long tsMs, long seq, long priceWon, string side = "ASK") =>
            "{\"type\":\"trade\",\"code\":\"KRW-BTC\",\"timestamp\":" + tsMs + ",\"trade_timestamp\":" + tsMs + ",\"trade_price\":" + priceWon +
            ",\"trade_volume\":0.001,\"ask_bid\":\"" + side + "\",\"sequential_id\":" + seq + ",\"stream_type\":\"REALTIME\"}";

        // ════════════ §1 파서 ════════════

        [Fact]
        public void Orderbook_Sample_Parses_L1_Exactly()
        {
            Assert.Equal(UpbitStreamKind.Orderbook, P(Ob, out var f));
            Assert.Equal(new UpbitStreamFrame(UpbitStreamKind.Orderbook, 1746601573804, 145_116_000 * S, 145_128_000 * S, 1_515_405, 2_927_645, 0, 0, UpbitTradeSide.None, 0), f);
        }

        [Fact]
        public void Trade_Sample_Parses_Exactly()
        {
            Assert.Equal(UpbitStreamKind.Trade, P(Tr, out var f));
            Assert.Equal(new UpbitStreamFrame(UpbitStreamKind.Trade, 1730336862047, 0, 0, 0, 0, 100_473_000 * S, 16_185, UpbitTradeSide.Bid, 17303368620470000), f);
            Assert.Equal(UpbitStreamKind.Trade, P(Tr.Replace("\"BID\"", "\"ASK\""), out var a));
            Assert.Equal(UpbitTradeSide.Ask, a.TradeSide);
        }

        [Fact]
        public void Field_Order_Independent_And_Unknown_Nested_Fields_Ignored()
        {
            string reordered = "{\"orderbook_units\":[{\"bid_size\":0.01515405,\"ask_size\":0.02927645,\"bid_price\":145116000,\"ask_price\":145128000}]," +
                               "\"extra\":{\"type\":\"trade\",\"code\":\"KRW-ETH\",\"arr\":[[1,2],{\"ask_price\":1}]},\"level\":0,\"timestamp\":1746601573804,\"code\":\"KRW-BTC\",\"type\":\"orderbook\"}";
            Assert.Equal(UpbitStreamKind.Orderbook, P(reordered, out var f));
            P(Ob, out var g);
            Assert.Equal(g, f);
            // 두 번째 단 이후는 판독 대상 아님(두 번째 단 가격이 교차여도 L1 = 첫 단)
            Assert.Equal(UpbitStreamKind.Orderbook, P(Ob.Replace("\"bid_price\":145100000", "\"bid_price\":999999999"), out _));
            // 수량 소수 8자리 초과 = 내림
            Assert.Equal(UpbitStreamKind.Orderbook, P(Ob.Replace("0.02927645", "0.123456789"), out var t));
            Assert.Equal(12_345_678, t.AskSizeRaw);
        }

        [Fact]
        public void Status_Error_Unsupported_Classified()
        {
            Assert.Equal(UpbitStreamKind.Status, P("{\"status\":\"UP\"}", out var a));
            Assert.Equal(default, a);
            Assert.Equal(UpbitStreamKind.Error, P("{\"error\":{\"name\":\"INVALID_PARAM\",\"message\":\"x\"}}", out var b));
            Assert.Equal(default, b);
            Assert.Equal(UpbitStreamKind.Unsupported, P("{\"type\":\"ticker\",\"code\":\"KRW-BTC\",\"trade_price\":1}", out var c));
            Assert.Equal(default, c);
        }

        public static IEnumerable<object[]> Malformed()
        {
            string[] cases =
            {
                "", " ", "{", "}", "[]", "[" + Ob + "]", "null", "123", "\"x\"", "true",
                Ob + " x", Ob + Ob, Ob + "{}", Ob.Substring(0, Ob.Length - 1), Ob.Substring(0, 40),
                Ob.Replace("\"KRW-BTC\"", "\"KRW-ETH\""), Ob.Replace("\"KRW-BTC\"", "\"krw-btc\""), Ob.Replace("\"code\":\"KRW-BTC\",", ""),
                Ob.Replace("\"type\":\"orderbook\",", ""), Ob.Replace("\"type\":\"orderbook\"", "\"type\":\"orderbook\",\"type\":\"orderbook\""),
                Ob.Replace("\"code\":\"KRW-BTC\"", "\"code\":\"KRW-BTC\",\"code\":\"KRW-BTC\""), Ob.Replace("\"timestamp\":1746601573804", "\"timestamp\":1746601573804,\"timestamp\":1"),
                Ob.Replace("\"type\":\"orderbook\"", "\"type\":1"), Ob.Replace("\"code\":\"KRW-BTC\"", "\"code\":null"),
                "{\"type\":\"orderbook\",\"code\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[]}",
                "{\"type\":\"orderbook\",\"code\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":{\"ask_price\":2,\"bid_price\":1,\"ask_size\":1,\"bid_size\":1}}",
                "{\"type\":\"orderbook\",\"code\":\"KRW-BTC\",\"timestamp\":1,\"orderbook_units\":[1,2]}",
                Ob.Replace("\"timestamp\":1746601573804,", ""), Ob.Replace(",\"bid_size\":0.01515405", ""), Ob.Replace("\"ask_price\":145128000,", ""),
                Ob.Replace("\"ask_price\":145128000", "\"ask_price\":145128000,\"ask_price\":145128000"),
                Ob.Replace("\"ask_price\":145128000", "\"ask_price\":145116000"), Ob.Replace("\"ask_price\":145128000", "\"ask_price\":145000000"),
                Ob.Replace("\"bid_price\":145116000", "\"bid_price\":0"), Ob.Replace("0.02927645", "-0.02927645"),
                Ob.Replace("\"ask_price\":145128000", "\"ask_price\":\"145128000\""), Ob.Replace("145128000", "1e30"),
                Ob.Replace("1746601573804", "0"), Ob.Replace("1746601573804", "-5"), Ob.Replace("1746601573804", "9223372036854775807"),
                Ob.Replace("1746601573804", "1746601573804.5"), Ob.Replace("1746601573804", "\"1746601573804\""),
                Ob.Replace("145128000", "NaN"), Ob.Replace("\"level\":0", "\"level\":0/*c*/"), Ob.Replace("\"code\"", "'code'"),
                Ob.Replace("\"level\":0", "\"level\":[[[[[[[[[[[[0]]]]]]]]]]]]"),
                Tr.Replace("\"trade_volume\":0.00016185", "\"trade_volume\":0"), Tr.Replace("\"trade_price\":100473000.00000000", "\"trade_price\":0"),
                Tr.Replace("\"BID\"", "\"XYZ\""), Tr.Replace("\"BID\"", "\"bid\""), Tr.Replace("\"ask_bid\":\"BID\",", ""), Tr.Replace("\"trade_timestamp\":1730336862047,", ""),
                Tr.Replace("17303368620470000", "-1"), Tr.Replace("17303368620470000", "1.5"), Tr.Replace("\"trade_price\":100473000.00000000,", ""),
                Tr.Replace("\"KRW-BTC\"", "\"KRW-XRP\""), Tr.Replace("\"ask_bid\":\"BID\"", "\"ask_bid\":\"BID\",\"ask_bid\":\"ASK\""),
                "{\"status\":\"UP\",\"type\":\"orderbook\"}", "{\"status\":\"UP\",\"status\":\"UP\"}", "{\"error\":{},\"type\":\"trade\"}", "{\"error\":{},\"status\":\"UP\"}",
                "{\"code\":\"KRW-BTC\"}", "{}",
            };
            foreach (var c in cases) yield return new object[] { c };
        }

        [Theory]
        [MemberData(nameof(Malformed))]
        public void Malformed_Frames_FailClosed_Without_Exception(string frame)
        {
            Assert.Equal(UpbitStreamKind.Invalid, P(frame, out var f));
            Assert.Equal(default, f);
        }

        [Fact]
        public void Binary_Garbage_And_Invalid_Utf8_FailClosed()
        {
            var bad = U(Ob);
            int at = Ob.IndexOf("KRW-BTC", StringComparison.Ordinal);
            bad[at] = 0xFF; bad[at + 1] = 0xFE;
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(bad, UpbitStreamProtocol.MarketKrwBtc, out _));
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(new byte[4096], UpbitStreamProtocol.MarketKrwBtc, out _));
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(new byte[] { 0x1F, 0x8B, 0x08 }, UpbitStreamProtocol.MarketKrwBtc, out _));   // gzip 머리
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(ReadOnlySpan<byte>.Empty, UpbitStreamProtocol.MarketKrwBtc, out _));
        }

        [Fact]
        public void Random_Mutations_100k_No_Exception_And_Accepted_Frames_Hold_Invariants()
        {
            var rng = new Random(20261022);
            byte[][] seeds = { U(Ob), U(Tr), U("{\"status\":\"UP\"}") };
            int accepted = 0, invalid = 0;
            var buf = new byte[8192];
            for (int i = 0; i < 100_000; i++)
            {
                var src = seeds[i % seeds.Length];
                int len = src.Length;
                src.CopyTo(buf, 0);
                int edits = 1 + rng.Next(4);
                for (int e = 0; e < edits; e++)
                {
                    int op = rng.Next(3), pos = rng.Next(Math.Max(1, len));
                    if (op == 0 && len > 0) buf[pos] = (byte)rng.Next(256);
                    else if (op == 1 && len < buf.Length - 1) { Buffer.BlockCopy(buf, pos, buf, pos + 1, len - pos); buf[pos] = (byte)"{}[]\",:0.-e"[rng.Next(11)]; len++; }
                    else if (len > 1) { Buffer.BlockCopy(buf, pos + 1, buf, pos, len - pos - 1); len--; }
                }
                var k = UpbitStreamProtocol.TryParse(buf.AsSpan(0, len), UpbitStreamProtocol.MarketKrwBtc, out var f);
                if (k == UpbitStreamKind.Orderbook) { accepted++; Assert.True(f.BidRaw > 0 && f.AskRaw > f.BidRaw && f.BidSizeRaw >= 0 && f.AskSizeRaw >= 0 && f.TimestampMs > 0); }
                else if (k == UpbitStreamKind.Trade) { accepted++; Assert.True(f.TradePriceRaw > 0 && f.TradeVolumeRaw > 0 && f.TradeSide != UpbitTradeSide.None && f.TimestampMs > 0); }
                else { if (k == UpbitStreamKind.Invalid) invalid++; Assert.Equal(default, f); }
            }
            _out.WriteLine($"변조 10만 회: 수용 {accepted} · Invalid {invalid} · 예외 0");
            Assert.True(invalid > 50_000);
        }

        // ════════════ §2 δ=0 ════════════

        [Fact]
        public void Parser_100k_Frames_Zero_Alloc()
        {
            byte[] ob = U(Ob), tr = U(Tr), st = U("{\"status\":\"UP\"}"), bad = U(Ob + " x");
            long ok = 0;
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++)
                {
                    var span = (i & 3) switch { 0 => ob, 1 => tr, 2 => st, _ => bad };
                    if (UpbitStreamProtocol.TryParse(span, UpbitStreamProtocol.MarketKrwBtc, out var f) != UpbitStreamKind.Invalid) ok += f.TimestampMs > 0 ? 1 : 0;
                }
            });
            Assert.Equal(0, bytes);
            Assert.True(ok >= 50_000);
        }

        [Fact]
        public void Adapter_OnFrame_100k_Frames_With_Exchange_Zero_Alloc()
        {
            var ex = new CryptoPaperExchange();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException());
            byte[] ob = U(Ob), tr = U(Tr), junk = U("{\"type\":\"orderbook\"");
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < 100_000; i++) a.OnFrame((i % 3) switch { 0 => ob, 1 => tr, _ => junk });
            });
            Assert.Equal(0, bytes);
            Assert.True(a.QuotesFed >= 33_333);
            Assert.True(a.Malformed >= 33_333);
            Assert.Equal(1, a.Trades);                                  // 같은 체결 재전송 = 첫 1회만 수용
            Assert.Equal(145_128_000 * S, ex.Snapshot().AskRaw);
        }

        [Fact]
        public void Distinct_Trades_Accepted_Path_Zero_Alloc()
        {
            const int N = 4_096;
            var frames = new byte[N][];
            for (int i = 0; i < N; i++) frames[i] = U(TrAt(1_760_000_000_000 + i, 1_000 + i, 150_000_000 + i * 1_000, (i & 1) == 0 ? "ASK" : "BID"));
            UpbitWebSocketFeedAdapter a = null!;
            long bytes = ZeroAllocProbe.MinAllocated(() =>
            {
                for (int i = 0; i < N; i++) a.OnFrame(frames[i]);
            }, reset: () => a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), factory: () => throw new InvalidOperationException()));
            Assert.Equal(0, bytes);
            Assert.Equal(N, a.Trades);
            Assert.Equal(0, a.TradesDropped);
            var s = a.State.Snapshot();
            Assert.Equal((150_000_000L + (N - 1) * 1_000L) * S, s.LastTradePriceRaw);
            Assert.Equal(UpbitTradeSide.Bid, s.LastTradeSide);
        }

        [Fact]
        public void ReceiveLoop_100k_Fragmented_Frames_Zero_Alloc()
        {
            var ex = new CryptoPaperExchange();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException());
            var t = new ScriptTransport(new[] { U(Ob), U(Tr), U("{\"status\":\"UP\"}") }, total: 100_000, chunk: 97);
            Task run = Task.CompletedTask;
            long bytes = ZeroAllocProbe.MinAllocated(() => { run = a.ReceiveLoopAsync(t, CancellationToken.None); }, windows: 1);
            Assert.True(run.IsCompletedSuccessfully);                       // 동기 완료(대역 전송) = 측정 창 안에서 10만 프레임 전량 처리
            Assert.Equal(0, bytes);
            Assert.Equal(100_000, a.FramesIn);
            Assert.Equal(0, a.Malformed);
            Assert.Equal(33_334, a.QuotesFed);
            Assert.Equal(33_333, a.StatusFrames);
        }

        // ════════════ §3 투영 ════════════

        [Fact]
        public void Orderbook_Projects_To_State_And_Exchange_Then_Paper_Fills_Exactly()
        {
            var ex = new CryptoPaperExchange();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException());
            Assert.Equal(UpbitStreamKind.Orderbook, a.OnFrame(U(Ob)));
            var s = a.State.Snapshot();
            Assert.Equal((145_116_000 * S, 145_128_000 * S, 1_515_405L, 2_927_645L, 1746601573804L, 1L), (s.BidRaw, s.AskRaw, s.BidSizeRaw, s.AskSizeRaw, s.QuoteTimestampMs, s.QuoteCount));
            var e = ex.Snapshot();
            Assert.Equal((145_116_000 * S, 145_128_000 * S, 1746601573804L * 1000), (e.BidRaw, e.AskRaw, e.QuoteUtcMicros));

            long now = 1746601573804L * 1000 + 500_000;
            var buy = ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, 100_000, 0, now);              // 0.001 BTC
            Assert.Equal(CryptoPaperStatus.Filled, buy.Status);
            Assert.Equal(145_158_000 * S, buy.PriceRaw);                // AlignUp(145,128,000 × 1.0002) = 145,158,000
            Assert.Equal(145_158 * S, buy.NotionalRaw);
            Assert.Equal(7_257_900_000L, buy.FeeRaw);                   // 72.579원
            var sell = ex.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, 100_000, 0, now);
            Assert.Equal(CryptoPaperStatus.Filled, sell.Status);
            Assert.Equal(145_086_000 * S, sell.PriceRaw);               // AlignDown(145,116,000 × 0.9998) = 145,086,000
            Assert.Equal(7_254_300_000L, sell.FeeRaw);
            Assert.Equal(0, ex.Snapshot().BtcRaw);
        }

        [Fact]
        public void Out_Of_Order_Quotes_And_Replayed_Trades_Dropped()
        {
            var ex = new CryptoPaperExchange();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException());
            a.OnFrame(U(ObAt(2_000, 150_000_000, 150_001_000)));
            a.OnFrame(U(ObAt(1_999, 140_000_000, 140_001_000)));                         // 역행 = 폐기
            a.OnFrame(U(ObAt(2_000, 150_002_000, 150_003_000)));                         // 같은 시각 = 수용(SNAPSHOT 재전송)
            Assert.Equal((2L, 1L), (a.QuotesFed, a.QuotesDropped));
            Assert.Equal(150_002_000 * S, ex.Snapshot().BidRaw);

            a.OnFrame(U(TrAt(5_000, 77, 150_000_000)));
            a.OnFrame(U(TrAt(5_000, 77, 150_000_000)));                                  // 재전송(같은 시각 · 같은 번호)
            a.OnFrame(U(TrAt(4_999, 78, 150_000_000)));                                  // 역행
            a.OnFrame(U(TrAt(5_000, 79, 150_001_000, "BID")));                           // 같은 시각 다른 번호 = 수용
            Assert.Equal((2L, 2L), (a.Trades, a.TradesDropped));
            var s = a.State.Snapshot();
            Assert.Equal((150_001_000 * S, 79L, UpbitTradeSide.Bid), (s.LastTradePriceRaw, s.LastTradeSequentialId, s.LastTradeSide));
        }

        [Fact]
        public void Malformed_And_Unaligned_Frames_Leave_Exchange_Unchanged()
        {
            var ex = new CryptoPaperExchange();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException());
            a.OnFrame(U(Ob));
            var before = ex.Snapshot();
            foreach (object[] m in Malformed()) a.OnFrame(U((string)m[0]));
            a.OnFrame(U("{\"error\":{\"name\":\"TOO_MANY_SUBSCRIBE\"}}"));
            a.OnFrame(U(ObAt(1746601573805, 145_116_500, 145_128_000)));                  // 호가 격자(1,000원) 비정렬 = 거래소 거부
            Assert.Equal(before, ex.Snapshot());
            Assert.Equal(1, a.QuotesRejected);
            Assert.Equal(1, a.ErrorFrames);
            Assert.Equal(Malformed().Count(), a.Malformed);
        }

        [Fact]
        public void Feed_Stops_Then_Exchange_Rejects_Stale_Quote()
        {
            var ex = new CryptoPaperExchange();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), ex, factory: () => throw new InvalidOperationException());
            a.OnFrame(U(Ob));
            long q = 1746601573804L * 1000;
            Assert.Equal(CryptoPaperStatus.StaleQuote, ex.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, 100_000, 0, q + CryptoPaperExchange.DefaultMaxQuoteAgeMicros + 1).Status);
            Assert.Equal(CryptoPaperExchange.InitialKrwRaw, ex.Snapshot().KrwRaw);
            Assert.False(a.IsConnected);
        }

        [Fact]
        public void Seqlock_One_Writer_Four_Readers_No_Torn_Snapshots()
        {
            var st = new UpbitMarketState();
            const int N = 200_000;
            int done = 0; long torn = 0, reads = 0;
            var readers = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
            {
                long local = 0, bad = 0;
                while (Volatile.Read(ref done) == 0 || local == 0)
                {
                    var s = st.Snapshot();
                    local++;
                    if (s.QuoteCount == 0) continue;
                    if (s.AskRaw - s.BidRaw != 1_000 * S || s.BidSizeRaw != s.QuoteTimestampMs || s.AskSizeRaw != s.QuoteCount) bad++;
                }
                Interlocked.Add(ref torn, bad); Interlocked.Add(ref reads, local);
            })).ToArray();
            foreach (var r in readers) r.Start();
            for (long k = 1; k <= N; k++)
            {
                var f = new UpbitStreamFrame(UpbitStreamKind.Orderbook, k, (100_000_000 + k * 1_000) * S, (100_001_000 + k * 1_000) * S, k, k, 0, 0, UpbitTradeSide.None, 0);
                Assert.Equal(UpbitPublishResult.Accepted, st.TryPublishQuote(in f));
            }
            Volatile.Write(ref done, 1);
            foreach (var r in readers) r.Join();
            _out.WriteLine($"판독 {reads:N0} · 찢김 {torn}");
            Assert.Equal(0, torn);
            Assert.Equal(N, st.Snapshot().QuoteCount);
        }

        // ════════════ §4 연결 ════════════

        [Fact]
        public async Task ReceiveLoop_Oversized_Message_Dropped_Next_Frame_Parsed()
        {
            var big = U("{\"type\":\"orderbook\",\"pad\":\"" + new string('a', UpbitWebSocketFeedAdapter.RxBufferBytes * 2) + "\"}");
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), factory: () => throw new InvalidOperationException());
            await a.ReceiveLoopAsync(new ScriptTransport(new[] { big, U(Ob) }, total: 2, chunk: 8192), CancellationToken.None);
            Assert.Equal((1L, 1L, 1L, 0L), (a.Oversized, a.FramesIn, a.QuotesFed, a.Malformed));
        }

        [Fact]
        public async Task RunAsync_Exponential_Backoff_Resets_After_Healthy_Connection_And_Never_Throws()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var delays = new List<TimeSpan>();
            var made = new List<ScriptTransport>();
            int n = 0;
            ScriptTransport Make()
            {
                n++;
                var t = n == 4 ? new ScriptTransport(new[] { U(Ob), U(Tr) }, total: 5, chunk: 1 << 20)
                      : n == 5 ? new ScriptTransport(new[] { U(Ob) }, total: 0, chunk: 1) { ReceiveThrows = new IOException("reset") }
                      : new ScriptTransport(Array.Empty<byte[]>(), 0, 1) { ConnectThrows = new WebSocketException("refused") };
                made.Add(t);
                return t;
            }
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), new CryptoPaperExchange(), Make, new UpbitFeedOptions { KeepAliveTick = TimeSpan.Zero },
                (d, _) => { delays.Add(d); if (delays.Count == 6) cts.Cancel(); return Task.CompletedTask; });
            await a.RunAsync(cts.Token);

            Assert.Equal(new[] { 1, 2, 4, 1, 2, 4 }, delays.Select(d => (int)d.TotalSeconds).ToArray());
            Assert.Equal((2L, 2L, 5L, 6L), (a.Connects, a.Disconnects, a.Faults, a.BackoffWaits));   // 결함 5 = 연결 실패 4(1·2·3·6번째) + 5번째 연결 후 수신 예외 1
            Assert.Equal(5, a.FramesIn);
            Assert.False(a.IsConnected);
            Assert.All(made.Take(6), t => Assert.Equal(UpbitStreamProtocol.Endpoint, t.ConnectedTo));
            Assert.Equal(UpbitStreamProtocol.SubscribePayload.ToArray(), Assert.Single(made[3].Sent));
            Assert.All(made, t => Assert.True(t.Disposed));
        }

        [Fact]
        public async Task RunAsync_Backoff_Capped_At_Max()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var delays = new List<TimeSpan>();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), null,
                () => new ScriptTransport(Array.Empty<byte[]>(), 0, 1) { ConnectThrows = new WebSocketException("x") },
                new UpbitFeedOptions { KeepAliveTick = TimeSpan.Zero, InitialBackoff = TimeSpan.FromSeconds(1), MaxBackoff = TimeSpan.FromSeconds(3) },
                (d, _) => { delays.Add(d); if (delays.Count == 5) cts.Cancel(); return Task.CompletedTask; });
            await a.RunAsync(cts.Token);
            Assert.Equal(new[] { 1, 2, 3, 3, 3 }, delays.Select(d => (int)d.TotalSeconds).ToArray());
            Assert.Equal(0, a.Connects);
        }

        [Fact]
        public async Task RunAsync_Silence_Watchdog_Aborts_HalfOpen_And_Sends_Ping()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var made = new List<ScriptTransport>();
            var a = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), null,
                () => { var t = new ScriptTransport(Array.Empty<byte[]>(), 0, 1) { Block = true }; lock (made) made.Add(t); return t; },
                new UpbitFeedOptions { KeepAliveTick = TimeSpan.FromMilliseconds(20), SilenceTimeout = TimeSpan.FromMilliseconds(400), PingInterval = TimeSpan.FromMilliseconds(60) },
                (_, _) => { cts.Cancel(); return Task.CompletedTask; });
            await a.RunAsync(cts.Token);
            Assert.False(a.IsConnected);
            Assert.Equal((1L, 1L, 1L, 1L, 0L), (a.Connects, a.Disconnects, a.SilenceAborts, a.Aborts, a.Faults));
            var sent = made[0].Sent;
            Assert.Equal(UpbitStreamProtocol.SubscribePayload.ToArray(), sent[0]);
            Assert.True(sent.Count >= 2 && a.Pings == sent.Count - 1, $"PING {a.Pings} · 송신 {sent.Count}");
            Assert.All(sent.Skip(1), p => Assert.Equal("PING"u8.ToArray(), p));
        }

        [Fact]
        public void Endpoint_Guard_Exact_Match_Only_And_Subscribe_Payload_Verbatim()
        {
            UpbitStreamProtocol.EnsureEndpoint(new Uri("wss://api.upbit.com/websocket/v1"));
            UpbitStreamProtocol.EnsureEndpoint(new Uri("wss://API.UPBIT.COM:443/websocket/v1"));       // 정규화 동일
            foreach (var bad in new[]
            {
                "ws://api.upbit.com/websocket/v1", "wss://api.upbit.com/websocket/v1/", "wss://api.upbit.com/websocket/v1?x=1", "wss://api.upbit.com/websocket/v1#f",
                "wss://api.upbit.com:8443/websocket/v1", "wss://u:p@api.upbit.com/websocket/v1", "wss://api.upbit.com.evil.io/websocket/v1",
                "wss://api.upbit.com/v1/orders", "wss://sg-api.upbit.com/websocket/v1", "https://api.upbit.com/websocket/v1",
            })
                Assert.Throws<ArgumentException>(() => UpbitStreamProtocol.EnsureEndpoint(new Uri(bad)));
            using (var real = new UpbitClientWebSocketTransport())                                       // 연결 시도 전 동기 거부(패킷 0)
                Assert.Throws<ArgumentException>(() => { _ = real.ConnectAsync(new Uri("wss://example.com/websocket/v1"), CancellationToken.None); });
            Assert.Equal("[{\"ticket\":\"SM-UPBIT-STREAM\"},{\"type\":\"orderbook\",\"codes\":[\"KRW-BTC\"]},{\"type\":\"trade\",\"codes\":[\"KRW-BTC\"]}]",
                         Encoding.UTF8.GetString(UpbitStreamProtocol.SubscribePayload.Span));
            Assert.Equal("PING", Encoding.UTF8.GetString(UpbitStreamProtocol.PingPayload.Span));
        }

        [Fact]
        public void Adapter_Source_Has_No_Order_Or_Auth_Path()
        {
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Engines", "Feeds", "Crypto", "UpbitWebSocketFeedAdapter.cs"));
            foreach (var forbidden in new[] { ".Submit(", "/v1/orders", "Authorization", "access_key", "HttpClient", "IBroker", "LiveArming", "ValveWired" })
                Assert.DoesNotContain(forbidden, src, StringComparison.Ordinal);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new InvalidOperationException("저장소 루트 없음");
        }

        /// <summary>대본형 전송 대역 — 프레임을 chunk 바이트 조각으로 동기 반환 · total 개 후 닫힘. Block = 취소까지 수신 대기(침묵).</summary>
        private sealed class ScriptTransport : IUpbitWsTransport
        {
            private readonly byte[][] _frames;
            private readonly long _total;
            private readonly int _chunk;
            private long _delivered;
            private int _idx, _off;
            public Exception? ConnectThrows, ReceiveThrows;
            public bool Block, Disposed;
            public Uri? ConnectedTo;
            public readonly List<byte[]> Sent = new();

            public ScriptTransport(byte[][] frames, long total, int chunk) { _frames = frames; _total = total; _chunk = chunk; }

            public Task ConnectAsync(Uri uri, CancellationToken ct)
            {
                ConnectedTo = uri;
                return ConnectThrows is null ? Task.CompletedTask : Task.FromException(ConnectThrows);
            }

            public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) { lock (Sent) Sent.Add(payload.ToArray()); return Task.CompletedTask; }

            public ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            {
                if (ReceiveThrows is not null) return ValueTask.FromException<(int, bool, bool)>(ReceiveThrows);
                if (Block) return new ValueTask<(int, bool, bool)>(BlockAsync(ct));
                if (_delivered >= _total) return new ValueTask<(int, bool, bool)>((0, true, true));
                var fr = _frames[_idx];
                int n = Math.Min(Math.Min(_chunk, fr.Length - _off), buffer.Length);
                fr.AsSpan(_off, n).CopyTo(buffer.Span);
                _off += n;
                bool end = _off == fr.Length;
                if (end) { _off = 0; _idx = (_idx + 1) % _frames.Length; _delivered++; }
                return new ValueTask<(int, bool, bool)>((n, end, false));
            }

            private static async Task<(int, bool, bool)> BlockAsync(CancellationToken ct)
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return (0, true, true);
            }

            public void Dispose() => Disposed = true;
        }
    }
}
