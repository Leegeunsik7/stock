// ============================================================================
// CrossBrokerSignalTests — [교차 피드 · 추천 A]
//
//  ★전제(정직): 키움 · KIS 는 같은 KRX 호가창을 중계 — 피드 간 차이 = 지연/노후화(차익 아님). 따라서 '잔량 많은 브로커로 자식 가중 배치'
//    (지시서 방향 A)는 근거가 없어 미구현 · 교차 피드는 데이터 품질 검증(방향 B)으로 쓴다.
//  §1 KisOrderBookParser(H0STASP0) — 공개 문서 필드 순서 · 잘림/비숫자/음수/최우선 결측 거부 · 어댑터 경로(평문 1레코드만 · VTS 도메인 중립) · δ=0.
//  §2 교차 확신도 — 한 피드만 = ½ 감쇄(임계 미달) · 두 피드 일치 = min · 방향 불일치 = 0 · 시각 이탈(>500ms) = 단일 출처 · 비교차 모드 = 피드 1 무시.
//  §3 [방향 B] 피드 간 꼬임(키움 매도 < KIS 매수) = 확신 0 · 연속 3회 = 비트 27 Disarm(CrossFeedCrossed) · 1~2회 회복 = 무트립.
//  §4 E2E — 하네스 라우터 게이트: 단일 피드 = 송출 0 · 양 피드 일치 = 매수 송출 · 꼬임 지속 = 집계어 차단 후 송출 0.
//  §5 δ=0 · 호스트 결선 · 오중 빌드.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Text;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Simulation;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class CrossBrokerSignalTests
    {
        private const long Px = 70_000, Tick = 100, Sym = 5930;
        private const int K = QuantSignalProvider.FeedKiwoom, S = QuantSignalProvider.FeedKis;

        private sealed class Clock { public long Now = 1_000_000; public long Read() => Now; }

        private static OrderBookPayload Book(long mid, long bidQty, long askQty, long deepBid, long deepAsk)
        {
            var b = new OrderBookPayload { SymbolCode = Sym, TimestampTicks = 1 };
            for (int i = 0; i < 10; i++)
            {
                b.AskPrice[i] = mid + Tick * (i + 1); b.BidPrice[i] = mid - Tick * i;
                b.AskQty[i] = i < 5 ? askQty : deepAsk; b.BidQty[i] = i < 5 ? bidQty : deepBid;
            }
            return b;
        }

        /// <summary>매수 쪽으로 기우는 호가(10호가 불균형 −0.05 → 0.95 · 스냅샷마다 +0.2). sell = 거울상(매도 쪽).</summary>
        private static OrderBookPayload Tilt(int k, long mid = Px, bool sell = false)
        {
            double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
            long bid10 = (long)Math.Round(5_000 * (1 + imb10)), ask10 = 10_000 - bid10;
            long b5 = 950, a5 = 50, db = (bid10 - 4_750) / 5, da = (ask10 - 250) / 5;
            return sell ? Book(mid, a5, b5, da, db) : Book(mid, b5, a5, db, da);
        }

        private static OrderProposal Prop(OrderSide side) => new(1, 0, Sym, side, 1, new QPrice(Px * 100_000_000), new QQty(100_000_000), 1);

        // ════════════ §1 H0STASP0 파서 ════════════

        private static string AspBody(long sym = Sym, long mid = Px, string? overrideField = null, int overrideAt = -1, int fields = 59)
        {
            var f = new string[fields];
            for (int i = 0; i < fields; i++) f[i] = "0";
            f[0] = sym.ToString("D6"); f[1] = "093001"; f[2] = "0";
            for (int i = 0; i < 10 && 3 + i < fields; i++) f[3 + i] = (mid + Tick * (i + 1)).ToString();       // 매도호가 1~10
            for (int i = 0; i < 10 && 13 + i < fields; i++) f[13 + i] = (mid - Tick * i).ToString();           // 매수호가 1~10
            for (int i = 0; i < 10 && 23 + i < fields; i++) f[23 + i] = (100 + i).ToString();                  // 매도잔량
            for (int i = 0; i < 10 && 33 + i < fields; i++) f[33 + i] = (200 + i).ToString();                  // 매수잔량
            if (overrideAt >= 0) f[overrideAt] = overrideField!;
            return string.Join('^', f);
        }

        private static byte[] U8(string s) => Encoding.ASCII.GetBytes(s);

        [Fact]
        public void Parser_H0STASP0_FieldOrder_RejectsMalformed()
        {
            Assert.True(KisOrderBookParser.TryParse(U8(AspBody()), 42, out var b));
            Assert.Equal((Sym, 42L), (b.SymbolCode, b.TimestampTicks));
            Assert.Equal((Px + Tick, Px + 10 * Tick, Px, Px - 9 * Tick), (b.AskPrice[0], b.AskPrice[9], b.BidPrice[0], b.BidPrice[9]));
            Assert.Equal((100L, 109L, 200L, 209L), (b.AskQty[0], b.AskQty[9], b.BidQty[0], b.BidQty[9]));
            Assert.True(KisOrderBookParser.TryParse(U8(AspBody(fields: 45)), 1, out _));                       // 최소 필드(0~44 · 총잔량 포함)
            Assert.False(KisOrderBookParser.TryParse(U8(AspBody(fields: 44)), 1, out _));                      // 잘림
            Assert.False(KisOrderBookParser.TryParse(U8(AspBody(overrideField: "7x", overrideAt: 25)), 1, out _));   // 비숫자
            Assert.False(KisOrderBookParser.TryParse(U8(AspBody(overrideField: "-5", overrideAt: 34)), 1, out _));   // 음수
            Assert.False(KisOrderBookParser.TryParse(U8(AspBody(overrideField: "0", overrideAt: 3)), 1, out _));     // 최우선 매도 결측
            Assert.False(KisOrderBookParser.TryParse(U8(AspBody(overrideField: "5930", overrideAt: 0)), 1, out _));  // 코드 6자리 아님
            var blank = AspBody().Split('^'); blank[12] = ""; blank[32] = "";                                    // 10단계 가격 · 잔량 모두 비움
            Assert.True(KisOrderBookParser.TryParse(U8(string.Join('^', blank)), 1, out var e) && e.AskPrice[9] == 0);   // 빈 단계 = 0
            Assert.Equal(KisTrDomain.Neutral, KisProtocol.ClassifyTr(U8("H0STASP0")));                         // 실/모의 공통 TR
        }

        [Fact]
        public void Adapter_RoutesPlainSingleRecordBooks_ToFeed_UnderVtsPin()
        {
            OrderBookPayload got = default; int n = 0;
            var v = new KisVtsProtocolValidator(vts: true);
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), validator: v, onBook: bk => { got = bk; n++; });
            Assert.True(a.OnFrame(U8("0|H0STASP0|001|" + AspBody())));
            Assert.Equal((1, Sym, Px + Tick), (n, got.SymbolCode, got.AskPrice[0]));
            Assert.False(a.OnFrame(U8("1|H0STASP0|001|" + AspBody())));                                        // 암호 플래그 = 형식 이탈
            Assert.False(a.OnFrame(U8("0|H0STASP0|002|" + AspBody() + "^" + AspBody())));                       // 다건 = 거부
            Assert.False(a.OnFrame(U8("0|H0STASP0|001|" + AspBody(fields: 30))));
            Assert.Equal((1, 1L, false), (n, a.BooksEmitted, v.Tripped));                                       // 도메인 중립(VTS 핀 무트립)
        }

        // ════════════ §2 교차 확신도 ════════════

        private static QuantSignalProvider Cross(Clock c, StrategyAnomalyInterlock? an = null)
            => new(an ?? new StrategyAnomalyInterlock(), nowMicros: c.Read, crossConfirm: true);

        private static void Feed(QuantSignalProvider q, Clock c, int feed, Func<int, OrderBookPayload> book, int steps = 6)
        {
            for (int k = 0; k < steps; k++) { var b = book(k); q.OnBook(in b, feed); c.Now += 10_000; }
        }

        [Fact]
        public void Cross_SingleSourceDecays_AgreementPasses_DisagreementZero_SkewFallsBackToSingle()
        {
            var c = new Clock();
            var q = Cross(c);
            Feed(q, c, K, k => Tilt(k));                                                                         // 키움만
            Assert.True(q.TryGetSignal(Sym, out var s1));
            Assert.Equal((1, (int)QuantReason.SingleSource), (s1.Sources, s1.Reason));
            Assert.True(s1.Confidence < 0.85 && s1.Confidence > 0.3, "conf " + s1.Confidence);                 // ★단일 출처 ½ — 임계 미달
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy)));

            var c2 = new Clock();
            var q2 = Cross(c2);
            for (int k = 0; k < 6; k++) { var b = Tilt(k); q2.OnBook(in b, K); q2.OnBook(in b, S); c2.Now += 10_000; }   // 같은 호가창 = 같은 스냅샷
            Assert.True(q2.TryGetSignal(Sym, out var s2));
            Assert.Equal(2, s2.Sources);
            Assert.True(s2.Confidence >= 0.85, "conf " + s2.Confidence);
            Assert.True(q2.AdmitSignal(Prop(OrderSide.Buy)));                                                   // ★양 피드 일치 = 통과
            Assert.False(q2.AdmitSignal(Prop(OrderSide.Sell)));
            Assert.InRange(s2.CrossSkewMicros, 0, 0);
            Assert.Equal(6, q2.KisBooks);

            var c3 = new Clock();
            var q3 = Cross(c3);
            for (int k = 0; k < 6; k++) { var b = Tilt(k); var m = Tilt(k, sell: true); q3.OnBook(in b, K); q3.OnBook(in m, S); c3.Now += 10_000; }
            Assert.True(q3.TryGetSignal(Sym, out var s3));
            Assert.Equal((0.0, (int)QuantReason.CrossDisagree), (s3.Confidence, s3.Reason));                    // ★방향 불일치 = 0
            Assert.True(q3.CrossDisagreements > 0);

            c2.Now += 600_000;                                                                                  // KIS 600ms 침묵(시각 이탈)
            var kb = Tilt(5); q2.OnBook(in kb, K);
            Assert.True(q2.TryGetSignal(Sym, out var s4));
            Assert.Equal((1, (int)QuantReason.SingleSource), (s4.Sources, s4.Reason));
            Assert.False(q2.AdmitSignal(Prop(OrderSide.Buy)));                                                  // Zero Single-Source Bias
            Assert.True(s4.CrossSkewMicros > 500_000);
        }

        [Fact]
        public void NonCrossMode_KisFeedDoesNotDriveSignal()
        {
            var c = new Clock();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: c.Read);                  // 교차 꺼짐(기본)
            Feed(q, c, S, k => Tilt(k));
            Assert.False(q.TryGetSignal(Sym, out _) && q.Books > 0);                                             // KIS 만으로는 신호 0
            Assert.Equal((6L, 0L), (q.KisBooks, q.Books));
            Feed(q, c, K, k => Tilt(k));
            Assert.True(q.AdmitSignal(Prop(OrderSide.Buy)));                                                    // 키움 단일(기존 동작 보존)
        }

        // ════════════ §3 [방향 B] 피드 간 꼬임 ════════════

        [Fact]
        public void CrossedFeeds_ZeroConfidence_PersistentThreeTimes_Disarms()
        {
            var c = new Clock();
            var an = new StrategyAnomalyInterlock();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            an.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            var q = Cross(c, an);
            for (int k = 0; k < 6; k++) { var b = Tilt(k); q.OnBook(in b, K); q.OnBook(in b, S); c.Now += 10_000; }
            Assert.True(q.AdmitSignal(Prop(OrderSide.Buy)));
            var hi = Tilt(5, Px + 6 * Tick);                                                                     // KIS 매수 70,600 > 키움 매도 70,100 = 꼬임
            var lo = Tilt(5);
            q.OnBook(in hi, S);
            Assert.True(q.TryGetSignal(Sym, out var s));
            Assert.Equal((0.0, (int)QuantReason.CrossedFeed), (s.Confidence, s.Reason));
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy)));                                                   // ★즉시 발주 차단(확신 0)
            q.OnBook(in lo, S);                                                                                 // 회복(1회 = 지터 허용)
            Assert.False(an.Tripped);
            for (int i = 0; i < 2; i++) { q.OnBook(in hi, S); c.Now += 1_000; }
            Assert.False(an.Tripped);                                                                           // 연속 2회 = 무트립
            q.OnBook(in hi, S);
            Assert.True(an.Tripped);                                                                            // ★연속 3회 = Disarm
            Assert.Equal(StrategyAnomalyKind.CrossFeedCrossed, an.FirstCause);
            Assert.True(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(4, q.CrossedBooks);
        }

        // ════════════ §4 E2E ════════════

        [Fact]
        public void E2E_Harness_SingleFeedBlocks_BothAgreeSends_CrossedDisarms()
        {
            using var h = new PaperTradingSimulationHarness();
            var an = new StrategyAnomalyInterlock();
            an.BindInterlock(h.Interlocks, ArmingInterlockSource.StrategyAnomaly);
            var q = new QuantSignalProvider(an, crossConfirm: true);
            h.Gate.EnableSignalGate(q);
            void Kw(OrderBookPayload b) { h.OnTossTick(Sym, Px); h.OnKiwoomBook(in b); q.OnBook(in b, K); }
            Kw(Tilt(0)); h.Pump();
            for (int k = 0; k < 6; k++) Kw(Tilt(k));
            h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 1); h.Pump();
            Assert.Equal(0, h.Broker.Placed);                                                                    // ★키움 단일 = 송출 0
            for (int k = 0; k < 6; k++) { var b = Tilt(k); Kw(b); q.OnBook(in b, S); }
            h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 1); h.Pump();
            Assert.Equal(1, h.Broker.Placed);                                                                    // ★양 피드 일치 = 송출
            var hi = Tilt(5, Px + 6 * Tick);
            for (int i = 0; i < 3; i++) q.OnBook(in hi, S);
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            for (int k = 0; k < 6; k++) { var b = Tilt(k); Kw(b); q.OnBook(in b, S); }                           // 복구돼 보여도
            h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 1); h.Pump();
            Assert.Equal(1, h.Broker.Placed);                                                                    // sticky — 송출 0
        }

        // ════════════ §5 δ=0 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void ParseAndCrossPaths_ZeroAlloc()
        {
            byte[] body = U8(AspBody()), frame = U8("0|H0STASP0|001|" + AspBody());
            var c = new Clock();
            var q = Cross(c);
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onBook: bk => q.OnBook(in bk, S));
            var books = Enumerable.Range(0, 6).Select(k => Tilt(k)).ToArray();
            var buy = Prop(OrderSide.Buy);
            for (int i = 0; i < 100; i++) { KisOrderBookParser.TryParse(body, 1, out _); a.OnFrame(frame); q.OnBook(in books[i % 6], K); q.OnBook(in books[i % 6], S); q.AdmitSignal(in buy); }
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    KisOrderBookParser.TryParse(body, i, out _);
                    a.OnFrame(frame);
                    q.OnBook(in books[i % 6], K);
                    q.OnBook(in books[(i + 1) % 6], S);
                    q.AdmitSignal(in buy);
                    q.TryGetSignal(Sym, out _);
                    c.Now += 1_000;
                }
            }));
        }

        [Fact]
        public void Host_Wiring_KisBooksFeedCrossMode_FiveBuilds()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string root = d!.FullName;
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            // [v12.18.16-DEV] onBook = 블록 람다(정량 엔진 교차 피드 + 아레나 듀얼 북 + 정규장 녹화) — 정량 피드 결선은 그대로
            Assert.Contains("Volatile.Read(ref quantRef)?.OnBook(in b, StockManager.Engines.QuantSignalProvider.FeedKis);   // [교차 피드]", form);
            Assert.Contains("arena.OnQuote(in b);", form);
            Assert.Contains("arenaTape.OnBook(in b);", form);
            Assert.Contains("bookSymbols: kisBookSymbols, recovery: kisRecovery, statusSymbols: kisStatusSymbols);", form);   // [세션 복구] 재접속마다 같은 호가 구독 재송신(+ v12.18.50 장운영 옵트인)
            Assert.Contains("crossConfirm: kisBookSymbols.Length > 0,", form);
            Assert.Contains("Volatile.Write(ref quantRef, quantSignals);", form);
            Assert.Contains("cfg.GetString(\"Kis.BookSymbols\", \"\")", form);
            string session = File.ReadAllText(Path.Combine(root, "StockManager", "Adapters", "KisRealtimeSession.cs"));
            Assert.Contains("BuildSubscribe(key!, \"H0STASP0\", sym)", session);
            Assert.Throws<ArgumentOutOfRangeException>(() => new KisRealtimeSession(new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1)),
                new KisSessionInterlock(null, 1, () => 1), () => null!, () => "k", "hts", true, bookSymbols: Enumerable.Repeat("005930", 39).ToArray()));
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}
