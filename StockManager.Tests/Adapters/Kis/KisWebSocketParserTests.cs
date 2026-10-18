// ============================================================================
// KisWebSocketParserTests — [v12.18.16-DEV] KIS WebSocket 10호가/체결 판독 · 전문 시각 편차 감시(비트 28) 적대적 검증
//   §1 판독: H0STASP0(63필드 · 공개 예제 컬럼 순서) 10호가 정확값 · BSOP_HOUR · H0STCNT0 다건(46필드 보폭) 체결가/체결량/누적/CCLD_DVSN(1 매수 · 5 매도 · 3 장전) ·
//      형식 이탈(시각 · 암호 호가 · 다건 호가 · 잘림 · 미상 TR) · HHmmss 경계
//   §2 δ=0 · GC 0: 10만 회 연속 판독(호가 + 체결 2건) + 시각 편차 판정
//   §3 비트 28: 인위 51ms 지연 = 즉시 격발(50ms = 무격발) · 집계어 비트 28 Raise · 역전 · 시계 앞섬 · 형식 이탈 · 자정 경계 · 스트림 독립 ·
//      래치(회복 100 미만/이중키 결여 = 해제 불가 · 회복 중 위반 = 재계수) · 자체 재무장이 2차 래치를 풀지 않음 · 2차 입력 재결선 거부
//   §4 어댑터 결속: 지연 전문 = 비트 28 격발 + 호가/체결(주도 방향) 직결 유지 · 아레나 건별 체결 링(누적 교차 유령 거래량 0) · 테이프 주도 방향 보존
//   §5 Fail-Closed: 판독기 · 감시기 IL 참조 허용목록(송신/주문/브로커 멤버 0)
//   §6 폭주: 1M 전문(무작위 지연 · 역전) + 동시 재무장 · 판독 스레드 — 위반 전수 계수(독립 기대값 일치) · 예외 0 · 최종 비트 = 래치 상태
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Adapters.Kis;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Adapters.Kis
{
    [Collection("ConcurrencyStress")]
    public sealed class KisWebSocketParserTests
    {
        private readonly ITestOutputHelper _out;
        public KisWebSocketParserTests(ITestOutputHelper o) => _out = o;

        private static byte[] U8(string s) => Encoding.ASCII.GetBytes(s);

        // H0STASP0 본문(공개 예제 컬럼: 0 코드 · 1 BSOP_HOUR · 2 HOUR_CLS_CODE · 3~12 매도호가 · 13~22 매수호가 · 23~32 매도잔량 · 33~42 매수잔량 · 43/44 총잔량 · 45~62 기타)
        private static string BookBody(string code = "005930", string hour = "093015", long ask = 70_100, long bid = 70_000, long tick = 100, long askQ = 1_000, long bidQ = 2_000)
        {
            var f = new List<string> { code, hour, "0" };
            for (int l = 0; l < 10; l++) f.Add((ask + l * tick).ToString());
            for (int l = 0; l < 10; l++) f.Add((bid - l * tick).ToString());
            for (int l = 0; l < 10; l++) f.Add((askQ + l).ToString());
            for (int l = 0; l < 10; l++) f.Add((bidQ + l).ToString());
            f.Add("10045"); f.Add("20045");
            while (f.Count < 63) f.Add("0");
            return string.Join("^", f);
        }

        // H0STCNT0 레코드(46필드: 0 코드 · 1 체결시각 · 2 현재가 · 12 체결량 · 13 누적 · 21 CCLD_DVSN)
        private static string TradeRec(string code, string hour, long price, long vol, long acc, string side)
        {
            var f = Enumerable.Repeat("0", KisProtocol.H0STCNT0FieldCount).ToArray();
            f[0] = code; f[1] = hour; f[2] = price.ToString(); f[12] = vol.ToString(); f[13] = acc.ToString(); f[21] = side;
            return string.Join("^", f);
        }

        private static byte[] BookFrame(string hour = "093015", string enc = "0", string count = "001") => U8(enc + "|H0STASP0|" + count + "|" + BookBody(hour: hour));

        private static byte[] TradeFrame(params string[] recs) => U8("0|H0STCNT0|" + recs.Length.ToString("D3") + "|" + string.Join("^", recs));

        // KST 2026-10-16 hh:mm:ss.fff → UTC ticks
        private static long Kst(int hh, int mm, int ss, int ms = 0, int day = 16)
            => new DateTime(2026, 10, day, hh, mm, ss, ms, DateTimeKind.Utc).AddHours(-9).Ticks;

        // ═══ §1 판독 ═══

        [Fact]
        public void Book_Frame_Decodes_Ten_Levels_And_Exchange_Time()
        {
            var trades = new KisTick[KisProtocol.MaxRecordsPerFrame];
            var kind = KisWebSocketOrderbookParser.Decode(BookFrame(), 123, out var book, out long hhmmss, trades, out int n);
            Assert.Equal(KisWsFrameKind.Book, kind);
            Assert.Equal((5930L, 93_015L, 123L, 0), (book.SymbolCode, hhmmss, book.TimestampTicks, n));
            for (int l = 0; l < 10; l++)
            {
                Assert.Equal(70_100 + l * 100, book.AskPrice[l]); Assert.Equal(70_000 - l * 100, book.BidPrice[l]);
                Assert.Equal(1_000 + l, book.AskQty[l]); Assert.Equal(2_000 + l, book.BidQty[l]);
            }
        }

        [Fact]
        public void Trade_Frame_Decodes_Multi_Records_With_Aggressor_Side()
        {
            var trades = new KisTick[KisProtocol.MaxRecordsPerFrame];
            var frame = TradeFrame(TradeRec("005930", "093016", 70_100, 37, 1_000_037, "1"), TradeRec("005930", "093017", 70_000, 12, 1_000_049, "5"),
                                   TradeRec("000660", "085959", 180_000, 5, 5, "3"));
            Assert.Equal(KisWsFrameKind.Trades, KisWebSocketOrderbookParser.Decode(frame, 0, out _, out _, trades, out int n));
            Assert.Equal(3, n);
            Assert.Equal((5930L, 93_016L, 70_100L, 37L, 1_000_037L, 1), (trades[0].SymbolCode, trades[0].TimeHhmmss, trades[0].Price, trades[0].TradeVolume, trades[0].AccVolume, trades[0].Side));
            Assert.Equal((70_000L, 12L, -1), (trades[1].Price, trades[1].TradeVolume, trades[1].Side));
            Assert.Equal((660L, 0), (trades[2].SymbolCode, trades[2].Side));                      // 3 장전 = 미상
            var shortRec = string.Join("^", TradeRec("005930", "093018", 70_000, 1, 1, "1").Split('^').Take(14));   // 21 결측(필수 14만) = 레코드 유지 · 방향 0
            Assert.Equal(KisWsFrameKind.Trades, KisWebSocketOrderbookParser.Decode(U8("0|H0STCNT0|001|" + shortRec), 0, out _, out _, trades, out n));
            Assert.Equal((1, 0), (n, trades[0].Side));
        }

        [Fact]
        public void Malformed_Frames_Are_Rejected_Without_Throwing()
        {
            var trades = new KisTick[KisProtocol.MaxRecordsPerFrame];
            KisWsFrameKind D(byte[] f) => KisWebSocketOrderbookParser.Decode(f, 0, out _, out _, trades, out _);
            Assert.Equal(KisWsFrameKind.Malformed, D(BookFrame(hour: "096000")));                 // 60분
            Assert.Equal(KisWsFrameKind.Malformed, D(BookFrame(hour: "93015")));                  // 5자리
            Assert.Equal(KisWsFrameKind.Malformed, D(BookFrame(hour: "24000A")));
            Assert.Equal(KisWsFrameKind.Malformed, D(BookFrame(enc: "1")));                       // 호가 = 평문만
            Assert.Equal(KisWsFrameKind.Malformed, D(BookFrame(count: "002")));                   // 호가 = 1레코드만
            Assert.Equal(KisWsFrameKind.Malformed, D(U8("0|H0STASP0|001|005930^093015^0^70100")));   // 잘림
            Assert.Equal(KisWsFrameKind.Malformed, D(U8("garbage")));
            Assert.Equal(KisWsFrameKind.Encrypted, D(U8("1|H0STCNT0|001|" + TradeRec("005930", "093016", 1, 1, 1, "1"))));
            Assert.Equal(KisWsFrameKind.Other, D(U8("0|H0STCNI0|001|" + TradeRec("005930", "093016", 1, 1, 1, "1"))));
            Assert.Equal(KisWsFrameKind.Malformed, D(U8("0|H0STCNT0|001|ABCDEF^093016^x")));
            var rnd = new Random(7);
            var buf = new byte[600];
            for (int i = 0; i < 20_000; i++)                                                   // 무작위 변조 = 예외 0
            {
                var src = (i & 1) == 0 ? BookFrame() : TradeFrame(TradeRec("005930", "093016", 70_100, 3, 3, "1"));
                int len = rnd.Next(1, src.Length + 1);
                Array.Copy(src, buf, len);
                for (int k = 0; k < 3; k++) buf[rnd.Next(len)] = (byte)rnd.Next(256);
                KisWebSocketOrderbookParser.Decode(buf.AsSpan(0, len), 0, out _, out _, trades, out _);
            }
        }

        [Theory]
        [InlineData(0L, true, 0)]
        [InlineData(235_959L, true, 86_399)]
        [InlineData(90_000L, true, 32_400)]
        [InlineData(240_000L, false, 0)]
        [InlineData(126_000L, false, 0)]
        [InlineData(120_060L, false, 0)]
        [InlineData(-1L, false, 0)]
        public void Hhmmss_Boundaries(long hhmmss, bool ok, int seconds)
        {
            Assert.Equal(ok, KisWebSocketOrderbookParser.TryHhmmssToSeconds(hhmmss, out int s));
            Assert.Equal(seconds, s);
        }

        // ═══ §2 δ=0 ═══

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        [Fact]
        public void Hundred_Thousand_Decodes_And_Skew_Judgements_Are_Zero_Alloc_With_No_Gc()
        {
            var bookFrame = BookFrame("093015");
            var tradeFrame = TradeFrame(TradeRec("005930", "093015", 70_100, 37, 1_000_037, "1"), TradeRec("005930", "093015", 70_000, 12, 1_000_049, "5"));
            var trades = new KisTick[KisProtocol.MaxRecordsPerFrame];
            long now = Kst(9, 30, 15, 500);
            var guard = new KisTimeSkewGuard(() => now);
            long sink = 0;
            void Loop()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    if (KisWebSocketOrderbookParser.Decode(bookFrame, i, out var book, out long t, trades, out _) == KisWsFrameKind.Book)
                    {
                        guard.Observe(KisTimeSkewGuard.StreamBook, t);
                        sink += book.AskPrice[9] + book.BidQty[9];
                    }
                    if (KisWebSocketOrderbookParser.Decode(tradeFrame, i, out _, out _, trades, out int n) == KisWsFrameKind.Trades)
                        for (int k = 0; k < n; k++) { guard.Observe(KisTimeSkewGuard.StreamTrade, trades[k].TimeHhmmss); sink += trades[k].Side; }
                    sink += guard.Status.LagUpperMs;
                }
            }
            Loop();
            var gc0 = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
            Assert.Equal(0, MinAllocated(Loop));
            Assert.Equal(gc0, (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
            Assert.False(guard.Tripped);
            Assert.True(sink != 0 && guard.Frames >= 600_000);                         // 예열 1회 + 측정 창(첫 창 0B = 종료) × 30만
        }

        // ═══ §3 비트 28 ═══

        private static (ArmingInterlockBitmask mask, OrderPathLatencyInterlock il) Bit28()
        {
            var il = new OrderPathLatencyInterlock(new OrderPathStageProfiler());
            var m = new ArmingInterlockBitmask(ArmingInterlockSource.OrderPathLatency);
            il.BindInterlock(m, ArmingInterlockSource.OrderPathLatency);
            Assert.True(m.Seal(out _));
            return (m, il);
        }

        [Fact]
        public void Injected_51ms_Frame_Trips_Bit28_Immediately_But_50ms_Does_Not()
        {
            var (mask, il) = Bit28();
            long now = Kst(9, 0, 1, 49);
            var g = new KisTimeSkewGuard(() => now);
            g.BindLatencyInterlock(il);
            Assert.False(mask.Blocked);
            Assert.False(g.Observe(KisTimeSkewGuard.StreamBook, 90_000));                     // 09:00:00 전문 · 호스트 :01.049 → 하한 50ms = 경계(미초과)
            Assert.Equal((50L, 1_049L), (g.LagLowerMs, g.LagUpperMs));
            Assert.False(mask.IsRaised(ArmingInterlockSource.OrderPathLatency));
            now = Kst(9, 0, 1, 50);
            Assert.True(g.Observe(KisTimeSkewGuard.StreamBook, 90_000));                      // 하한 51ms → 즉시 격발
            Assert.True(mask.IsRaised(ArmingInterlockSource.OrderPathLatency));
            Assert.True(mask.Blocked && il.ArmingBlocked && il.FeedBlocked && !il.Tripped);
            Assert.Equal((KisSkewReason.Latency, 1L, 1L), (g.LastTripReason, g.Trips, g.Violations));
            Assert.Equal(DashKisFeed.Latched, g.Status.State);
            Assert.Equal(1L << 28, unchecked((long)0x800000001FFFFFFFUL) & (1L << 28));                        // 비트 28 = 30종 정의 마스크 내(정의 불변)
            Assert.Throws<InvalidOperationException>(() => new KisTimeSkewGuard().BindLatencyInterlock(il));   // 2차 입력 교체 금지
        }

        [Fact]
        public void Inversion_ClockAhead_BadTimestamp_Trip_And_Streams_Are_Independent()
        {
            long now = Kst(9, 0, 5, 20);                                                     // 090004 하한 21ms · 090005 상한 20ms = 둘 다 정상
            var a = new KisTimeSkewGuard(() => now);
            Assert.False(a.Observe(KisTimeSkewGuard.StreamBook, 90_005));
            Assert.False(a.Observe(KisTimeSkewGuard.StreamTrade, 90_004));                     // 다른 스트림 = 역전 아님(호가/체결 생성 경로 상이)
            Assert.False(a.Observe(KisTimeSkewGuard.StreamBook, 90_005));                      // 같은 초 = 허용
            Assert.True(a.Observe(KisTimeSkewGuard.StreamBook, 90_004));
            Assert.Equal(KisSkewReason.Inversion, a.LastTripReason);

            now = Kst(9, 0, 0, 900);
            var b = new KisTimeSkewGuard(() => now);
            Assert.True(b.Observe(KisTimeSkewGuard.StreamBook, 90_001));                       // 전문이 호스트보다 100ms 미래 = 시계 편차
            Assert.Equal((KisSkewReason.ClockAhead, -100L), (b.LastTripReason, b.LagUpperMs));
            now = Kst(9, 0, 0, 960);
            var b2 = new KisTimeSkewGuard(() => now);
            Assert.False(b2.Observe(KisTimeSkewGuard.StreamBook, 90_001));                     // −40ms = 허용

            var c = new KisTimeSkewGuard(() => now);
            Assert.True(c.Observe(KisTimeSkewGuard.StreamTrade, 96_000));
            Assert.Equal(KisSkewReason.BadTimestamp, c.LastTripReason);

            now = Kst(0, 0, 0, 500, day: 17);                                                // 자정 경계: 23:59:59 전문 · 호스트 00:00:00.500 = 하한 501ms
            var d = new KisTimeSkewGuard(() => now);
            Assert.True(d.Observe(KisTimeSkewGuard.StreamBook, 235_959));
            Assert.Equal((501L, 1_500L, KisSkewReason.Latency), (d.LagLowerMs, d.LagUpperMs, d.LastTripReason));
            var e = new KisTimeSkewGuard(() => now, recoveryFrames: 1);
            now = Kst(23, 59, 59, 30);
            Assert.False(e.Observe(KisTimeSkewGuard.StreamBook, 235_959));
            now = Kst(0, 0, 0, 30, day: 17);
            Assert.False(e.Observe(KisTimeSkewGuard.StreamBook, 0));                           // 23:59:59 → 00:00:00 = 역전 아님
        }

        [Fact]
        public void Latch_Holds_Until_Recovery_Frames_And_Double_Key_And_Own_Rearm_Does_Not_Clear_It()
        {
            var (mask, il) = Bit28();
            long now = Kst(10, 0, 0, 300);
            var g = new KisTimeSkewGuard(() => now, recoveryFrames: 100);
            g.BindLatencyInterlock(il);
            Assert.True(g.TryRearm(true, true));                                             // 미격발 = 무동작 성공
            now = Kst(10, 0, 2, 0);
            Assert.True(g.Observe(KisTimeSkewGuard.StreamBook, 100_000));                     // 하한 1,001ms
            now = Kst(10, 0, 2, 300);
            for (int i = 0; i < 99; i++) Assert.False(g.Observe(KisTimeSkewGuard.StreamBook, 100_002));
            Assert.Equal(99, g.HealthyStreak);
            Assert.False(g.TryRearm(true, true));                                            // 회복 99 < 100
            Assert.True(il.TryRearm(true, true));                                            // 자체 재무장 성공 —
            Assert.True(mask.IsRaised(ArmingInterlockSource.OrderPathLatency));               //   그래도 2차 래치가 비트 28 유지
            Assert.False(g.Observe(KisTimeSkewGuard.StreamBook, 100_002));
            Assert.False(g.TryRearm(true, false));                                           // 이중키 결여
            Assert.False(g.TryRearm(false, true));
            now = Kst(10, 0, 4, 0);
            g.Observe(KisTimeSkewGuard.StreamBook, 100_002);                                 // 회복 중 위반 = 연속 0 · 재격발 아님(이미 래치)
            Assert.Equal((0L, 1L, 2L), (g.HealthyStreak, g.Trips, g.Violations));
            now = Kst(10, 0, 4, 300);
            for (int i = 0; i < 100; i++) g.Observe(KisTimeSkewGuard.StreamBook, 100_004);
            Assert.True(g.TryRearm(true, true));
            Assert.False(g.Tripped);
            Assert.False(mask.IsRaised(ArmingInterlockSource.OrderPathLatency));
            Assert.False(mask.Blocked);
            Assert.Equal(DashKisFeed.Ok, g.Status.State);
        }

        // ═══ §4 어댑터 결속 ═══

        [Fact]
        public void Adapter_Observes_Frame_Time_Trips_Bit28_And_Still_Forwards_Book_And_Trades_With_Side()
        {
            var (mask, il) = Bit28();
            long now = Kst(9, 30, 15, 400);
            var g = new KisTimeSkewGuard(() => now);
            g.BindLatencyInterlock(il);
            var books = new List<OrderBookPayload>();
            var trades = new List<KisTick>();
            var session = new KisSessionInterlock(null, 1, () => 1);
            using var a = new KisOpenApiAdapter(session, onBook: b => books.Add(b), skewGuard: g, onTrade: t => trades.Add(t));
            Assert.True(a.OnFrame(BookFrame("093015")));
            Assert.True(a.OnFrame(TradeFrame(TradeRec("005930", "093015", 70_100, 37, 1_000_037, "1"), TradeRec("005930", "093015", 70_000, 4, 1_000_041, "5"))));
            Assert.False(g.Tripped);
            Assert.Equal(3, g.Frames);
            Assert.Single(books);
            Assert.Equal(new[] { 1, -1 }, trades.Select(t => t.Side).ToArray());
            Assert.Equal(new[] { 37L, 4L }, trades.Select(t => t.TradeVolume).ToArray());
            now = Kst(9, 30, 17, 0);                                                         // 1.05s 지연된 호가 전문
            Assert.True(a.OnFrame(BookFrame("093015")));
            Assert.True(g.Tripped);
            Assert.True(mask.IsRaised(ArmingInterlockSource.OrderPathLatency));
            Assert.Equal(2, books.Count);                                                     // 시세 피드는 유지(가상 원장 · 관측) — 차단은 무장 술어에서
        }

        [Fact]
        public void Kis_Trades_Feed_Arena_Without_Phantom_Volume_And_Tape_Keeps_Side()
        {
            long now = 638_000_000_000_000_000;
            var g = new StockManager.Engines.Arena.ArenaMatchGovernor(() => now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.True(g.Start(10_000_000));
            g.Step(now += 10_000);
            var q = new OrderBookPayload { SymbolCode = 5930 };
            for (int l = 0; l < 10; l++) { q.AskPrice[l] = 70_100 + l * 100; q.AskQty[l] = 1_000; q.BidPrice[l] = 70_000 - l * 100; q.BidQty[l] = 1_000; }
            g.OnQuote(in q);
            g.SetFocus(5930);
            // 브리지(누적) 1,000,000 → 1,000,040 과 KIS(건별) 37 · 12 가 교차 — 누적 표에 KIS 값이 섞이면 유령 거래량
            g.OnTrade(5930, 70_100, 1_000_000);                                             // 기준선
            g.OnTradeQty(5930, 70_100, 37, 1);
            g.OnTrade(5930, 70_100, 1_000_040);                                             // +40
            g.OnTradeQty(5930, 70_000, 12, -1);
            g.OnTradeQty(5930, 70_000, 0, -1);                                              // 0주 = 무시
            g.Step(now += 10_000);
            Assert.Equal(40 + 37 + 12, g.Vwap.SumVolume);
            Assert.Equal(3, g.TradesSeen);

            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_kistape_" + Guid.NewGuid().ToString("N"));
            using (var rec = new StockManager.Engines.Arena.ArenaTapeRecorder(dir, 16))
            {
                rec.OnTrade(5930, 70_100, 37, 1);                                            // 녹화 OFF = 기록 0
                Assert.True(rec.StartRecording());
                rec.OnBook(in q);
                rec.OnTrade(5930, 70_100, 37, 1);
                rec.OnTrade(5930, 70_000, 12, -1);
                rec.StopRecording();
                Assert.Equal(3, rec.RecordedCount);
                using var r = StockManager.Engines.Arena.ArenaTapeReader.Open(rec.RecordingFile!);
                Assert.True(r.TryReadTrade(1, out var t1));
                Assert.True(r.TryReadTrade(2, out var t2));
                Assert.Equal((37L, 1, 12L, -1), (t1.Qty, t1.Side, t2.Qty, t2.Side));
            }
            try { System.IO.Directory.Delete(dir, recursive: true); } catch (System.IO.IOException) { }
        }

        // ═══ §5 Fail-Closed ═══

        [Fact]
        public void Parser_And_Guard_Il_Reference_No_Send_Order_Or_Broker_Member()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            string[] badMember = { "Send", "Submit", "Cancel", "Place", "Dispatch", "Arm(" };
            string[] badOwner = { "Broker", "Dispatcher", "Egress", "Router", "Kiwoom", "OrderDrain" };
            var bad = new List<string>();
            var calledOnInterlock = new HashSet<string>();
            foreach (var type in new[] { typeof(KisWebSocketOrderbookParser), typeof(KisTimeSkewGuard) })
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = r as Type ?? r.DeclaringType;
                        if (owner is null) continue;
                        string ns = owner.Namespace ?? "";
                        if (owner == typeof(OrderPathLatencyInterlock)) calledOnInterlock.Add(r.Name);
                        if (owner.FullName == "<PrivateImplementationDetails>") continue;
                        bool nsOk = ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal) || ns == "MS2E.Infrastructure" || ns == "MS2E.Core"
                                 || ns == "StockManager.Adapters" || ns == "StockManager.Adapters.Kis" || ns == "MS2E.UI.Portable.Ports.Trading"
                                 || owner == typeof(OrderPathLatencyInterlock);
                        if (!nsOk || badOwner.Any(b => (owner.FullName ?? "").Contains(b, StringComparison.Ordinal)) || badMember.Any(b => r.Name.StartsWith(b, StringComparison.Ordinal)))
                            bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + r.Name);
                    }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
            Assert.Equal(new[] { "AttachFeedGuard", "Resync" }, calledOnInterlock.OrderBy(x => x, StringComparer.Ordinal).ToArray());
            Assert.DoesNotContain(typeof(IKisFeedLatencyPort).GetMembers().Select(x => x.Name), n => n.Contains("Rearm", StringComparison.Ordinal) || n.Contains("Arm", StringComparison.Ordinal));
        }

        // ═══ §6 폭주 ═══

        [Fact]
        public void Storm_Of_1M_Frames_With_Random_Lag_And_Inversions_Counts_Every_Violation_Under_Concurrent_Rearm()
        {
            var (mask, il) = Bit28();
            long now = 0;
            var g = new KisTimeSkewGuard(() => Volatile.Read(ref now), recoveryFrames: 64);
            g.BindLatencyInterlock(il);
            var rnd = new Random(20261018);
            long expected = 0;
            int lastSec = -1;
            using var stop = new CancellationTokenSource();
            Exception? fault = null;
            long rearms = 0, reads = 0;
            var side = new Thread(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var st = g.Status;
                        if (st.State == DashKisFeed.Latched && g.TryRearm(true, true)) Interlocked.Increment(ref rearms);
                        reads++;
                    }
                }
                catch (Exception ex) { fault = ex; }
            }) { IsBackground = true };
            side.Start();
            int sec = 9 * 3_600;
            for (int i = 0; i < 1_000_000; i++)
            {
                if (rnd.Next(50) == 0) sec++;
                int frameSec = rnd.Next(2_000) == 0 ? sec - 1 : sec;                              // 0.05% 역전
                int lagMs = rnd.Next(1_000) == 0 ? 1_051 + rnd.Next(500) : rnd.Next(1_000, 1_050);   // 0.1% 확실 지연 · 나머지 하한 1 ~ 50ms
                long host = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc).AddHours(-9).Ticks + (frameSec * 1_000L + lagMs) * TimeSpan.TicksPerMillisecond;
                Volatile.Write(ref now, host);
                bool inv = lastSec >= 0 && frameSec < lastSec;
                if (inv || lagMs - 999 > 50) expected++;
                lastSec = frameSec;
                long hhmmss = frameSec / 3_600 * 10_000 + frameSec / 60 % 60 * 100 + frameSec % 60;
                g.Observe(KisTimeSkewGuard.StreamBook, hhmmss);
            }
            stop.Cancel();
            Assert.True(side.Join(5_000), "판독/재무장 스레드 교착");
            Assert.Null(fault);
            _out.WriteLine($"[폭주] 전문 {g.Frames:N0} · 위반 {g.Violations:N0}(기대 {expected:N0}) · 격발 {g.Trips:N0} · 재무장 {rearms:N0} · 판독 {reads:N0}");
            Assert.Equal(1_000_000, g.Frames);
            Assert.Equal(expected, g.Violations);                                             // 미포착 위반 0
            Assert.True(g.Trips >= 1 && rearms >= 1);
            Assert.Equal(g.Tripped, mask.IsRaised(ArmingInterlockSource.OrderPathLatency));  // 최종 비트 = 래치 상태(경합 후 일치)
            g.TryRearm(true, true);
            Assert.Equal(g.Tripped, mask.IsRaised(ArmingInterlockSource.OrderPathLatency));
        }
    }
}
