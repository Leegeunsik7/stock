// ============================================================================
// UpbitOrderbookDepthTests — v12.18.76-DEV: 업비트 15호가(orderbook_units) 무할당 파이프라인 · 매수벽/매도벽/허매수 실가동 검증(오프라인 · 송출 0)
//
//   §1 파서: 15단 정확값(가격 · 잔량 raw 10^8) · L1 프레임 = 기존 판독과 동일 · 16단 이상 = 15단 기록 · 연속 유효 접두 절단
//      (필드 누락 · 교차 · 매도 비오름 · 매수 비내림) · 깊은 단 형식 위반(중복 · 음수 · 문자열) = 프레임 거부 · Trade = 0단.
//   §2 결속: 어댑터 depthTap → 피더 슬롯(L1 + 잔량 열 원자 기록) → Pump → 엔진 OnDepthQuote → 뎁스 판독 15호가 ·
//      매수벽(3호가) · 매도벽(5호가) · 허매수(8호가) 판정 · 근거 뱃지 · L1 경로 = 1호가(기존 동작) · 잔량 열 검증(ValidDepthTiers).
//   §3 악마의 변호인 ⑥: 거버넌스 가동 · 15호가 매도벽 → AI 진입 근거에 DepthTrap(−20) 동반 · 같은 시나리오 벽 없음 = DepthTrap 0.
//   §4 δ=0: 15호가 프레임 10만 회 디코딩 · 어댑터 → 피더 → Pump → 엔진 10만 회 = 0B(MinAllocated · Release).
// ============================================================================
using System;
using System.Globalization;
using System.Text;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Feeds.Crypto;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Feeds
{
    [Collection("ConcurrencyStress")]
    public sealed class UpbitOrderbookDepthTests
    {
        private const long S = UpbitTickAligner.Scale;
        private const long BidWon = 150_000_000, AskWon = 150_001_000, TickWon = 1_000;
        private static readonly long Sat = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        private static readonly long BtcSym = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
        private readonly ITestOutputHelper _out;

        public UpbitOrderbookDepthTests(ITestOutputHelper o) => _out = o;

        private static byte[] U(string s) => Encoding.UTF8.GetBytes(s);

        private static string Qty(long raw) => (raw / (decimal)S).ToString("0.########", CultureInfo.InvariantCulture);

        /// <summary>n 단 호가 프레임(매도 = AskWon + i 호가 · 매수 = BidWon − i 호가 · 잔량 raw).</summary>
        private static string Book(long tsMs, long[] bidQty, long[] askQty, string code = "KRW-BTC")
        {
            var sb = new StringBuilder("{\"type\":\"orderbook\",\"code\":\"" + code + "\",\"timestamp\":" + tsMs + ",\"total_ask_size\":12.5,\"total_bid_size\":7.25,\"orderbook_units\":[");
            for (int i = 0; i < bidQty.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"ask_price\":").Append(AskWon + i * TickWon).Append(",\"bid_price\":").Append(BidWon - i * TickWon)
                  .Append(",\"ask_size\":").Append(Qty(askQty[i])).Append(",\"bid_size\":").Append(Qty(bidQty[i])).Append('}');
            }
            return sb.Append("],\"stream_type\":\"REALTIME\",\"level\":0}").ToString();
        }

        private static long[] Flat(int n, long q) { var a = new long[n]; Array.Fill(a, q); return a; }

        private static long[] With(long[] a, int index, long q) { var c = (long[])a.Clone(); c[index] = q; return c; }

        private static long[] Arr(in UpbitDepthLevels levels, int n) { ReadOnlySpan<long> s = levels; return s.Slice(0, n).ToArray(); }

        // ── §1 파서 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Parser_Decodes_All_15_Tiers_Exactly_And_L1_Matches_Legacy()
        {
            var bid = new long[15];
            var ask = new long[15];
            for (int i = 0; i < 15; i++) { bid[i] = (i + 1) * 1_234_567; ask[i] = (15 - i) * 7_654_321 + 1; }
            byte[] frame = U(Book(1761274800000, bid, ask));
            var kind = UpbitStreamProtocol.TryParse(frame, UpbitMarketSet.KrwBtc, out int m, out var f, out var d);
            Assert.Equal((UpbitStreamKind.Orderbook, 0, 15), (kind, m, d.Tiers));
            Assert.Equal(bid, Arr(in d.BidSize, 15));
            Assert.Equal(ask, Arr(in d.AskSize, 15));
            for (int i = 0; i < 15; i++)
            {
                Assert.Equal((AskWon + i * TickWon) * S, d.AskPrice[i]);
                Assert.Equal((BidWon - i * TickWon) * S, d.BidPrice[i]);
            }
            Assert.Equal(UpbitStreamKind.Orderbook, UpbitStreamProtocol.TryParse(frame, UpbitStreamProtocol.MarketKrwBtc, out var legacy));
            Assert.Equal(legacy, f);                                                       // L1 프레임 = 기존 판독과 동일
            Assert.Equal(new UpbitStreamFrame(UpbitStreamKind.Orderbook, 1761274800000, BidWon * S, AskWon * S, bid[0], ask[0], 0, 0, UpbitTradeSide.None, 0), f);
        }

        [Fact]
        public void Parser_Records_First_15_Of_Longer_Book()
        {
            var q = Flat(17, S);
            var kind = UpbitStreamProtocol.TryParse(U(Book(1, q, With(q, 16, 3 * S))), UpbitMarketSet.KrwBtc, out _, out _, out var d);
            Assert.Equal((UpbitStreamKind.Orderbook, UpbitOrderbookDepth.MaxTiers), (kind, d.Tiers));
            Assert.Equal(Flat(15, S), Arr(in d.AskSize, 15));
        }

        [Theory]
        [InlineData(",\"bid_size\":1}", ",\"bid_sizeX\":1}", 3)]                                         // 4단 bid_size 누락 → 3단
        [InlineData("\"ask_price\":150004000,", "\"ask_price\":150002000,", 3)]                          // 4단 매도 비오름 → 3단
        [InlineData("\"bid_price\":149997000,", "\"bid_price\":149998000,", 3)]                           // 4단 매수 비내림 → 3단
        [InlineData("\"bid_price\":149997000,", "\"bid_price\":150004000,", 3)]                           // 4단 교차(매수 ≥ 매도) → 3단
        [InlineData("\"bid_price\":149997000,", "\"bid_price\":0,", 3)]                                   // 4단 매수 0 → 3단
        public void Parser_Truncates_Depth_At_First_Semantically_Invalid_Tier(string from, string to, int expectedTiers)
        {
            string s = Book(1, Flat(6, S), Flat(6, S));
            int at = s.IndexOf("\"ask_price\":150004000", StringComparison.Ordinal);       // 4단(인덱스 3) 시작
            Assert.True(at > 0);
            int fromAt = s.IndexOf(from, at, StringComparison.Ordinal);
            Assert.True(fromAt > 0, from);
            string bad = s[..fromAt] + to + s[(fromAt + from.Length)..];
            var kind = UpbitStreamProtocol.TryParse(U(bad), UpbitMarketSet.KrwBtc, out _, out var f, out var d);
            Assert.Equal(UpbitStreamKind.Orderbook, kind);                                // L1 유효 = 프레임 수용
            Assert.Equal(expectedTiers, d.Tiers);
            Assert.Equal(BidWon * S, f.BidRaw);
        }

        [Theory]
        [InlineData("\"ask_size\":1,\"ask_size\":1")]          // 깊은 단 중복 필드
        [InlineData("\"ask_size\":-1")]                         // 음수
        [InlineData("\"ask_size\":\"1\"")]                      // 문자열 숫자
        [InlineData("\"ask_size\":{\"x\":1}")]                  // 객체
        public void Parser_Rejects_Frame_With_Malformed_Deep_Tier_Field(string deepField)
        {
            string s = Book(1, Flat(6, S), Flat(6, S));
            int at = s.IndexOf("\"ask_price\":150005000", StringComparison.Ordinal);       // 5단
            int sz = s.IndexOf("\"ask_size\":1", at, StringComparison.Ordinal);
            string bad = s[..sz] + deepField + s[(sz + "\"ask_size\":1".Length)..];
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(U(bad), UpbitMarketSet.KrwBtc, out int m, out var f, out var d));
            Assert.Equal((-1, 0), (m, d.Tiers));
            Assert.Equal(default, f);
        }

        [Fact]
        public void Trade_And_Invalid_Frames_Carry_No_Depth()
        {
            string tr = "{\"type\":\"trade\",\"code\":\"KRW-BTC\",\"timestamp\":5,\"trade_timestamp\":5,\"trade_price\":150001000,\"trade_volume\":0.001,\"ask_bid\":\"BID\",\"sequential_id\":9}";
            Assert.Equal(UpbitStreamKind.Trade, UpbitStreamProtocol.TryParse(U(tr), UpbitMarketSet.KrwBtc, out _, out _, out var d));
            Assert.Equal(0, d.Tiers);
            string crossed = Book(1, Flat(5, S), Flat(5, S)).Replace("\"bid_price\":150000000", "\"bid_price\":150001000", StringComparison.Ordinal);
            Assert.Equal(UpbitStreamKind.Invalid, UpbitStreamProtocol.TryParse(U(crossed), UpbitMarketSet.KrwBtc, out _, out _, out var d2));
            Assert.Equal(0, d2.Tiers);                                                     // 부분 판독 노출 0
        }

        // ── §2 결속 ──────────────────────────────────────────────────────────────

        private sealed class Rig
        {
            public readonly UpbitCryptoArenaEngine Engine = new();
            public readonly CryptoArenaQuoteFeeder Feeder;
            public readonly UpbitWebSocketFeedAdapter Ws;
            public long Utc = new DateTime(2026, 10, 24, 3, 0, 0, DateTimeKind.Utc).Ticks;

            public Rig()
            {
                Feeder = new CryptoArenaQuoteFeeder(Engine);
                Ws = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), options: new UpbitFeedOptions { Markets = CryptoArenaUniverse.StreamMarkets },
                                                   depthTap: Feeder.OnUpbitMarketDepthFrame);
            }

            public void Push(byte[] frame)
            {
                Assert.Equal(UpbitStreamKind.Orderbook, Ws.OnFrame(frame));
                Utc += TimeSpan.TicksPerSecond / 10;
                Feeder.Pump(Utc);
            }

            public PlutoMicrostructureFactor Micro() { Assert.True(Engine.TryReadMicrostructure(BtcSym, out var f)); return f; }
        }

        [Fact]
        public void Depth_Tap_Feeds_15_Tiers_And_Detects_Ask_Wall_At_Tier_5()
        {
            var r = new Rig();
            var bid = Flat(15, S);
            var ask = With(Flat(15, S), 4, 20 * S);                                       // 5호가 매도벽
            r.Push(U(Book(1761274800000, bid, ask)));
            var f = r.Micro();
            _out.WriteLine($"tiers={f.DepthTiers} ratio={f.DepthRatioBps} flags={f.DepthFlags} ask@{f.AskWallTier} score={f.DepthScore} badges={PlutoMicrostructureText.Badges(in f)}");
            Assert.Equal(15, f.DepthTiers);
            Assert.Equal(PlutoDepthFlags.AskWall, f.DepthFlags);
            Assert.Equal((5, 0), (f.AskWallTier, f.BidWallTier));
            Assert.True(f.IsDepthTrap && f.IsWellFormed);
            var expected = PlutoOrderbookDepthAnalyzer.Analyze(bid, ask);
            Assert.Equal((expected.DepthRatioBps, expected.Score), (f.DepthRatioBps, f.DepthScore));
            Assert.Equal(expected.DepthRatioBps / 250 + PlutoOrderbookDepthAnalyzer.AskWallPenalty, f.DepthScore);   // 매도벽 −15
            Assert.Contains("[🧱 5호가 매도벽 -15]", PlutoMicrostructureText.Badges(in f));
        }

        [Fact]
        public void Depth_Tap_Detects_Bid_Support_Wall_At_Tier_3_And_Spoof_At_Tier_8()
        {
            var r = new Rig();
            r.Push(U(Book(1, With(Flat(15, S), 2, 30 * S), Flat(15, S))));                // 3호가 매수벽
            var w = r.Micro();
            Assert.Equal((PlutoDepthFlags.BidSupportWall, 3), (w.DepthFlags, w.BidWallTier));
            Assert.Equal(PlutoOrderbookDepthAnalyzer.ScoreOf(w.DepthRatioBps, PlutoDepthFlags.None) + PlutoOrderbookDepthAnalyzer.SupportWallBonus, w.DepthScore);   // +10
            Assert.False(w.IsDepthTrap);
            Assert.Contains("[🛡️ 3호가 매수벽]", PlutoMicrostructureText.Badges(in w));

            r.Push(U(Book(2, With(Flat(15, S), 7, 40 * S), Flat(15, S))));                // 8호가 원거리 미끼(원잔량 40/54 ≥ 50%)
            var s = r.Micro();
            _out.WriteLine($"spoof flags={s.DepthFlags} bid@{s.BidWallTier} ratio={s.DepthRatioBps} score={s.DepthScore} badges={PlutoMicrostructureText.Badges(in s)}");
            Assert.Equal((PlutoDepthFlags.BidSpoof, 8), (s.DepthFlags, s.BidWallTier));
            Assert.True(s.DepthRatioBps > 0);                                             // 가중 비율은 매수 우위로 보이지만
            Assert.Equal(PlutoOrderbookDepthAnalyzer.SpoofPenalty, s.DepthScore);         // 양수 박탈 −10
            Assert.True(s.IsDepthTrap && s.IsWellFormed);
            Assert.Contains("[⚠️ 8호가 허매수 감지]", PlutoMicrostructureText.Badges(in s));
        }

        [Fact]
        public void L1_Only_Paths_Keep_One_Tier_Reading()
        {
            var r = new Rig();
            var legacy = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), options: new UpbitFeedOptions { Markets = CryptoArenaUniverse.StreamMarkets },
                                                       marketTap: r.Feeder.OnUpbitMarketFrame);
            Assert.Equal(UpbitStreamKind.Orderbook, legacy.OnFrame(U(Book(1, Flat(15, S), With(Flat(15, S), 4, 20 * S)))));
            r.Feeder.Pump(r.Utc += TimeSpan.TicksPerSecond);
            var f = r.Micro();
            Assert.Equal((1, PlutoDepthFlags.None, 0), (f.DepthTiers, f.DepthFlags, f.AskWallTier));   // 기존 marketTap = L1 판독(벽 판정 없음 · 정직)
        }

        [Fact]
        public void ValidDepthTiers_Requires_L1_Match_And_Bounded_Prefix()
        {
            long[] b = { 5, 6, 7, 8 }, a = { 9, 9, 9, 9 };
            Assert.Equal(4, UpbitCryptoArenaEngine.ValidDepthTiers(5, 9, b, a));
            Assert.Equal(0, UpbitCryptoArenaEngine.ValidDepthTiers(4, 9, b, a));            // [0] ≠ L1 잔량 = 미사용
            Assert.Equal(0, UpbitCryptoArenaEngine.ValidDepthTiers(5, 9, ReadOnlySpan<long>.Empty, a));
            Assert.Equal(2, UpbitCryptoArenaEngine.ValidDepthTiers(5, 9, new long[] { 5, 6, -1, 8 }, a));   // 음수 단에서 절단
            Assert.Equal(1, UpbitCryptoArenaEngine.ValidDepthTiers(5, 9, new long[] { 5, UpbitCryptoArenaEngine.MaxBookQtyRaw + 1 }, a));
            Assert.Equal(3, UpbitCryptoArenaEngine.ValidDepthTiers(5, 9, new long[] { 5, 6, 7 }, a));    // 양측 공통 범위
            Assert.Equal(15, UpbitCryptoArenaEngine.ValidDepthTiers(1, 1, Flat(20, 1), Flat(20, 1)));    // 15단 상한

            var e = new UpbitCryptoArenaEngine();                                          // 불일치 열 = L1 판독(fail-closed)
            e.OnDepthQuote(BtcSym, BidWon * S, AskWon * S, S, S, Sat, With(Flat(15, S), 0, 2 * S), With(Flat(15, S), 4, 20 * S));
            Assert.True(e.TryReadMicrostructure(BtcSym, out var f));
            Assert.Equal((1, PlutoDepthFlags.None), (f.DepthTiers, f.DepthFlags));
        }

        // ── §3 악마의 변호인 ⑥ ──────────────────────────────────────────────────

        /// <summary>워밍업 후 매수 테이커 우세 · L1 매수 9 vs 매도 1 · 가격 상승(진입 문턱 이상) — 깊은 단 잔량 열(askWall = 5호가 매도벽 유무).</summary>
        private static AiTradeRationale[] GovernedEntry(bool askWall, out PlutoMicrostructureFactor last)
        {
            var e = new UpbitCryptoArenaEngine();
            e.EnableGovernance(true);
            e.EnableMicrostructure(true);
            long t = Sat;
            var flat = Flat(15, S);
            for (int i = 0; i < 40; i++) { t += 100_000; e.OnDepthQuote(BtcSym, BidWon * S, AskWon * S, S, S, t, flat, flat); }
            e.SetMarketContext(0, 300);
            for (int i = 0; i < 4; i++) { t += 1_000; e.OnTrade(BtcSym, (AskWon + 99_000) * S, S / 2, 1, t); }
            var bid = With(Flat(15, 3 * S), 0, 9 * S);
            var ask = askWall ? With(flat, 4, 12 * S) : flat;
            t += 100_000;
            e.OnDepthQuote(BtcSym, (BidWon + 100_000) * S, (AskWon + 100_000) * S, 9 * S, S, t, bid, ask);
            Assert.True(e.TryReadMicrostructure(BtcSym, out last));
            var buf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            return buf[..e.CopyRationales(buf)];
        }

        [Fact]
        public void Governed_Entry_Under_15_Tier_Ask_Wall_Carries_Devils_Advocate_DepthTrap()
        {
            var trapped = GovernedEntry(askWall: true, out var f);
            var clean = GovernedEntry(askWall: false, out var g);
            _out.WriteLine($"wall: flags={f.DepthFlags} ask@{f.AskWallTier} score={f.MicrostructureScore} entries={trapped.Length} · clean: flags={g.DepthFlags} entries={clean.Length}");
            Assert.True(f.IsDepthTrap && f.AskWallTier == 5 && f.DepthTiers == 15);
            Assert.False(g.IsDepthTrap);

            var a = Array.Find(trapped, r => r.Side == 1);
            Assert.True(a.Seq > 0, "15호가 매도벽 시나리오 진입 근거 없음");
            Assert.True(a.IsWellFormed, a.HumanReadableSummary);
            Assert.True((a.Governance.Theses & CounterThesis.DepthTrap) != 0);
            Assert.Equal(PlutoDevilsAdvocateFilter.DepthTrapPenalty, PlutoDevilsAdvocateFilter.PenaltyOf(a.Governance.Theses & CounterThesis.DepthTrap));
            string text = a.HumanReadableSummary;
            _out.WriteLine(text);
            Assert.Contains("[🧱 5호가 매도벽 -15]", text);
            Assert.Contains("15호가 매도벽/허매수 함정", text);
            Assert.Contains("15호가 지수 가중 뎁스", text);

            var b = Array.Find(clean, r => r.Side == 1);
            Assert.True(b.Seq > 0, "벽 없음 시나리오 진입 근거 없음");
            Assert.Equal(CounterThesis.None, b.Governance.Theses & CounterThesis.DepthTrap);
            Assert.True(a.Governance.Penalty <= PlutoDevilsAdvocateFilter.DepthTrapPenalty && b.Governance.Penalty > a.Governance.Penalty);   // ⑥ 감점이 확신도에 반영
        }

        // ── §4 δ=0 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Decode_15_Tier_Frames_100k_Zero_Alloc()
        {
            byte[] f1 = U(Book(1761274800000, With(Flat(15, S / 3), 2, 40 * S), Flat(15, S / 7)));
            byte[] f2 = U(Book(1761274800001, Flat(15, S / 5), With(Flat(15, S / 2), 9, 30 * S)));
            var markets = CryptoArenaUniverse.StreamMarkets;
            long acc = 0;
            void Run(int n)
            {
                for (int i = 0; i < n; i++)
                    if (UpbitStreamProtocol.TryParse((i & 1) == 0 ? f1 : f2, markets, out int m, out var f, out var d) == UpbitStreamKind.Orderbook)
                        acc += d.Tiers + d.BidSize[d.Tiers - 1] + m + (f.BidRaw & 1);
            }
            Run(10_000);
            long bytes = ZeroAllocProbe.MinAllocated(() => Run(100_000));
            _out.WriteLine($"decode δ={bytes}B acc={acc}");
            Assert.Equal(0, bytes);
            Assert.True(acc >= 110_000L * 15);
        }

        [Fact]
        public void Adapter_Feeder_Pump_Engine_15_Tier_Pipeline_100k_Zero_Alloc()
        {
            var r = new Rig();
            r.Engine.EnableMicrostructure(true);
            r.Engine.EnableGovernance(true);
            var frames = new byte[4][];
            for (int k = 0; k < 4; k++)
                frames[k] = U(Book(1761274800000, With(Flat(15, S / (k + 2)), 2 + k, 25 * S), With(Flat(15, S / 3), 4 + k, 18 * S)));
            long traps = 0;
            int j = 0;
            void Run(int n)
            {
                for (int i = 0; i < n; i++, j++)
                {
                    r.Ws.OnFrame(frames[j & 3]);
                    r.Utc += TimeSpan.TicksPerSecond / 20;
                    r.Feeder.Pump(r.Utc);
                    if (r.Engine.TryReadMicrostructure(BtcSym, out var f) && f.IsDepthTrap && f.DepthTiers == 15) traps++;
                }
            }
            Run(10_000);
            long bytes = ZeroAllocProbe.MinAllocated(() => Run(100_000));
            _out.WriteLine($"pipeline δ={bytes}B traps={traps} quotes={r.Ws.QuotesOf(0)}");
            Assert.Equal(0, bytes);
            Assert.True(traps >= 110_000);
        }
    }
}
