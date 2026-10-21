// ============================================================================
// KisVtsOrderBookParserTests — [KIS VTS 호가 · 추천 A]
//
//  ★정직: 이 저장소/호스트에는 VTS 실수신 H0STASP0 캡처가 없다 — §1 은 KIS 공식 공개 예제(koreainvestment/open-trading-api ·
//    examples_llm/domestic_stock/asking_price_krx)의 63개 컬럼 정의와 파서 인덱스를 1:1 대조한다(실패킷 대조는 운용 과제).
//  §1 공식 컬럼 정의 ↔ 파서 사상(필드마다 고유값 · 총잔량 43/44 · 시간구분 2).
//  §2 경계 방어 — 잘림 · 종목코드 · 알파벳 섞임 · 음수 · 자릿수 초과 · 최우선 결측 · 단조성 · 구멍 · 가격 없는 잔량 · 연속매매 교차 · 시간구분.
//  §3 어댑터 — 이탈 틱 무시 + 블랙박스(사유 코드) · 연속 5회 = KIS 호가 격리(전달 0) · 정상 20회 = 복귀 · 예상 호가(HOUR_CLS_CODE≠0) 제외 ·
//     ★오파싱 데이터의 시그널 엔진 유입 0.
//  §4 엔진 — 격리 = 키움 단일 출처 · [방향 B] 수신 시각 격차 &gt; 1초 = 교차 확신 0(CrossSkewVeto) · 0.5~1초 = 단일 출처 ½.
//  §5 δ=0 · 결선 · 오중 빌드.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Text;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using Xunit;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KisVtsOrderBookParserTests
    {
        private const long Px = 70_000, Tick = 100, Sym = 5930;

        /// <summary>KIS 공식 예제 컬럼 정의(H0STASP0 · 63필드 · 2026-10-08 조회).</summary>
        private static readonly string[] OfficialColumns =
        {
            "MKSC_SHRN_ISCD", "BSOP_HOUR", "HOUR_CLS_CODE",
            "ASKP1", "ASKP2", "ASKP3", "ASKP4", "ASKP5", "ASKP6", "ASKP7", "ASKP8", "ASKP9", "ASKP10",
            "BIDP1", "BIDP2", "BIDP3", "BIDP4", "BIDP5", "BIDP6", "BIDP7", "BIDP8", "BIDP9", "BIDP10",
            "ASKP_RSQN1", "ASKP_RSQN2", "ASKP_RSQN3", "ASKP_RSQN4", "ASKP_RSQN5", "ASKP_RSQN6", "ASKP_RSQN7", "ASKP_RSQN8", "ASKP_RSQN9", "ASKP_RSQN10",
            "BIDP_RSQN1", "BIDP_RSQN2", "BIDP_RSQN3", "BIDP_RSQN4", "BIDP_RSQN5", "BIDP_RSQN6", "BIDP_RSQN7", "BIDP_RSQN8", "BIDP_RSQN9", "BIDP_RSQN10",
            "TOTAL_ASKP_RSQN", "TOTAL_BIDP_RSQN", "OVTM_TOTAL_ASKP_RSQN", "OVTM_TOTAL_BIDP_RSQN",
            "ANTC_CNPR", "ANTC_CNQN", "ANTC_VOL", "ANTC_CNTG_VRSS", "ANTC_CNTG_VRSS_SIGN", "ANTC_CNTG_PRDY_CTRT", "ACML_VOL",
            "TOTAL_ASKP_RSQN_ICDC", "TOTAL_BIDP_RSQN_ICDC", "OVTM_TOTAL_ASKP_ICDC", "OVTM_TOTAL_BIDP_ICDC", "STCK_DEAL_CLS_CODE",
            "MID_PRC", "MIDP_TOTAL_RSQN", "MIDP_CLS_CODE", "MARKET_CLS_CODE",
        };

        /// <summary>공식 컬럼명 → 값 사전으로 63필드 프레임 본문 생성(필드마다 고유값).</summary>
        private static string[] Fields(string hourCls = "0", long mid = Px)
        {
            var f = new string[OfficialColumns.Length];
            for (int i = 0; i < f.Length; i++)
            {
                string c = OfficialColumns[i];
                f[i] = c switch
                {
                    "MKSC_SHRN_ISCD" => Sym.ToString("D6"),
                    "BSOP_HOUR" => "093015",
                    "HOUR_CLS_CODE" => hourCls,
                    _ when c.StartsWith("ASKP_RSQN") && !c.Contains("ICDC") => (1_000 + int.Parse(c[9..])).ToString(),
                    _ when c.StartsWith("BIDP_RSQN") && !c.Contains("ICDC") => (2_000 + int.Parse(c[9..])).ToString(),
                    _ when c.StartsWith("ASKP") && char.IsDigit(c[^1]) && !c.Contains('_') => (mid + Tick * int.Parse(c[4..])).ToString(),
                    _ when c.StartsWith("BIDP") && char.IsDigit(c[^1]) && !c.Contains('_') => (mid - Tick * (int.Parse(c[4..]) - 1)).ToString(),
                    "TOTAL_ASKP_RSQN" => "10055",
                    "TOTAL_BIDP_RSQN" => "20055",
                    "STCK_DEAL_CLS_CODE" => "0",
                    _ => "7",
                };
            }
            return f;
        }

        private static byte[] U8(string s) => Encoding.ASCII.GetBytes(s);
        private static byte[] Body(string[] f) => U8(string.Join('^', f));
        private static int Ix(string col) => Array.IndexOf(OfficialColumns, col);

        // ════════════ §1 공식 컬럼 ↔ 파서 ════════════

        [Fact]
        public void OfficialColumnLayout_MapsOneToOne()
        {
            Assert.Equal(63, OfficialColumns.Length);
            Assert.Equal((0, 2, 3, 12, 13, 22, 23, 32, 33, 42, 43, 44), (Ix("MKSC_SHRN_ISCD"), Ix("HOUR_CLS_CODE"), Ix("ASKP1"), Ix("ASKP10"), Ix("BIDP1"), Ix("BIDP10"),
                         Ix("ASKP_RSQN1"), Ix("ASKP_RSQN10"), Ix("BIDP_RSQN1"), Ix("BIDP_RSQN10"), Ix("TOTAL_ASKP_RSQN"), Ix("TOTAL_BIDP_RSQN")));
            Assert.True(KisOrderBookParser.TryParse(Body(Fields()), 77, out var b, out var rej, out byte hc, out long ta, out long tb));
            Assert.Equal((KisBookReject.None, (byte)'0', 10_055L, 20_055L, Sym, 77L), (rej, hc, ta, tb, b.SymbolCode, b.TimestampTicks));
            for (int i = 0; i < 10; i++)
            {
                Assert.Equal(Px + Tick * (i + 1), b.AskPrice[i]);
                Assert.Equal(Px - Tick * i, b.BidPrice[i]);
                Assert.Equal(1_000 + i + 1, b.AskQty[i]);
                Assert.Equal(2_000 + i + 1, b.BidQty[i]);
            }
        }

        // ════════════ §2 경계 방어 ════════════

        private static KisBookReject Rej(string[] f)
        {
            bool ok = KisOrderBookParser.TryParse(Body(f), 1, out _, out var r, out _, out _, out _);
            Assert.Equal(ok, r == KisBookReject.None);
            return r;
        }

        private static string[] With(params (string Col, string Val)[] set)
        {
            var f = Fields();
            foreach (var (c, v) in set) f[Ix(c)] = v;
            return f;
        }

        [Fact]
        public void BoundaryRejects_EachReasonCoded()
        {
            Assert.Equal(KisBookReject.Truncated, Rej(Fields()[..44]));
            Assert.Equal(KisBookReject.None, Rej(Fields()[..45]));                                       // 0~44 최소
            Assert.Equal(KisBookReject.BadCode, Rej(With(("MKSC_SHRN_ISCD", "00593A"))));
            Assert.Equal(KisBookReject.BadCode, Rej(With(("MKSC_SHRN_ISCD", "5930"))));
            Assert.Equal(KisBookReject.NonNumeric, Rej(With(("ASKP_RSQN3", "12A"))));                    // 알파벳 섞임
            Assert.Equal(KisBookReject.NonNumeric, Rej(With(("BIDP2", " 69900"))));
            Assert.Equal(KisBookReject.NonNumeric, Rej(With(("ASKP_RSQN1", "99999999999999999999"))));   // 자릿수 초과
            Assert.Equal(KisBookReject.Negative, Rej(With(("BIDP_RSQN4", "-5"))));                      // 음수 잔량
            Assert.Equal(KisBookReject.MissingTop, Rej(With(("ASKP1", "0"))));
            Assert.Equal(KisBookReject.NonMonotonic, Rej(With(("ASKP3", (Px + Tick).ToString()))));     // 매도 오름차순 위반
            Assert.Equal(KisBookReject.NonMonotonic, Rej(With(("BIDP5", Px.ToString()))));              // 매수 내림차순 위반
            Assert.Equal(KisBookReject.LevelGap, Rej(With(("ASKP4", "0"), ("ASKP_RSQN4", "0"))));        // 4단계 비고 5단계 값 = 구멍
            Assert.Equal(KisBookReject.LevelGap, Rej(With(("ASKP10", "0"))));                            // 가격 없는 단계에 잔량
            Assert.Equal(KisBookReject.None, Rej(With(("ASKP10", "0"), ("ASKP_RSQN10", "0"))));          // 뒤쪽 결측 = 정상
            Assert.Equal(KisBookReject.CrossedTop, Rej(With(("BIDP1", (Px + Tick).ToString()))));         // 연속매매 최우선 잠김/교차
            var auction = Fields("B"); auction[Ix("BIDP1")] = (Px + Tick).ToString();
            Assert.Equal(KisBookReject.None, Rej(auction));                                              // 장전 예상 = 교차 허용(피드 제외는 어댑터)
            Assert.Equal(KisBookReject.BadHourClass, Rej(With(("HOUR_CLS_CODE", "AB"))));
            Assert.Equal(KisBookReject.BadHourClass, Rej(With(("HOUR_CLS_CODE", ""))));
            Assert.Equal(KisBookReject.BadHourClass, Rej(With(("HOUR_CLS_CODE", "?"))));
        }

        // ════════════ §3 어댑터 ════════════

        private static byte[] Frame(string[] f, string enc = "0", string count = "001") => U8(enc + "|H0STASP0|" + count + "|" + string.Join('^', f));

        [Fact]
        public void Adapter_IgnoresBadTicks_LogsReason_QuarantinesAfterFive_ReleasesAfterTwenty_NoBadDataReachesEngine()
        {
            var box = new MmfFlightRecorder(1024);
            var quarantine = new System.Collections.Generic.List<bool>();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), crossConfirm: true);
            KisOpenApiAdapter? a = null;
            a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onBook: bk => q.OnBook(in bk, QuantSignalProvider.FeedKis),
                                      blackbox: box, onBookQuarantine: v => { quarantine.Add(v); q.SetFeedQuarantined(QuantSignalProvider.FeedKis, v); });
            byte[] good = Frame(Fields()), alpha = Frame(With(("ASKP_RSQN3", "12A"))), neg = Frame(With(("BIDP_RSQN4", "-5"))), cut = Frame(Fields()[..30]);
            Assert.True(a.OnFrame(good));
            Assert.Equal(1, q.KisBooks);
            foreach (var bad in new[] { alpha, neg, cut, alpha }) Assert.False(a.OnFrame(bad));
            Assert.True(a.OnFrame(good));                                                                   // 4회 뒤 정상 = 연속 끊김
            foreach (var bad in new[] { alpha, neg, cut, alpha }) Assert.False(a.OnFrame(bad));
            Assert.False(a.BookQuarantined);
            Assert.Equal(2, q.KisBooks);                                                                    // ★이탈 8틱 = 엔진 유입 0
            Assert.False(a.OnFrame(Frame(Fields(), enc: "1")));                                             // 5번째 연속(암호 플래그)
            Assert.True(a.BookQuarantined);                                                                 // ★연속 5 = 격리
            Assert.Equal(new[] { true }, quarantine.ToArray());
            Assert.True(q.IsFeedQuarantined(QuantSignalProvider.FeedKis));
            long emitted = a.BooksEmitted;
            for (int i = 0; i < KisOpenApiAdapter.BookReleaseAfter - 1; i++) Assert.True(a.OnFrame(good));
            Assert.Equal(emitted, a.BooksEmitted);                                                          // ★어댑터 단 격리 = 전달 0(엔진 격리와 별개 1차 방어)
            Assert.Equal(2, q.KisBooks);                                                                    // 격리 중 정상 프레임도 엔진 유입 0
            Assert.True(a.OnFrame(good));                                                                   // 20번째 정상 = 복귀(그 프레임부터 전달)
            Assert.False(a.BookQuarantined);
            Assert.Equal(new[] { true, false }, quarantine.ToArray());
            Assert.Equal(3, q.KisBooks);
            Assert.True(a.OnFrame(good));
            Assert.Equal(4, q.KisBooks);
            Assert.True(a.OnFrame(Frame(Fields("A"))));                                                     // 장후 예상 호가 = 정상 판독 · 피드 제외
            Assert.Equal((4L, 1L), (q.KisBooks, a.BookNonContinuous));
            Assert.Equal(9, a.BookRejects);
            var snap = new FlightRecordEvent[box.Capacity];
            int n = box.SnapshotRecent(snap);
            var rej = snap.Take(n).Where(e => e.Event == FlightEventKind.KisBookRejected).ToArray();
            Assert.Equal(9, rej.Length);
            Assert.Contains(rej, e => e.Arg0 == (long)KisBookReject.NonNumeric && e.SymbolCode == Sym);   // 사유 · 종목 기록
            Assert.Contains(rej, e => e.Arg0 == (long)KisBookReject.Negative);
            Assert.Contains(rej, e => e.Arg0 == (long)KisBookReject.Truncated);
            Assert.Contains(rej, e => e.Arg0 == (long)KisBookReject.FrameHeader);
            Assert.Equal(2, snap.Take(n).Count(e => e.Event == FlightEventKind.KisBookQuarantine));         // 격리 + 복귀
        }

        // ════════════ §4 엔진 ════════════

        private sealed class Clock { public long Now = 1_000_000; public long Read() => Now; }

        private static OrderBookPayload Book(long mid = Px)
        {
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int i = 0; i < 10; i++) { b.AskPrice[i] = mid + Tick * (i + 1); b.BidPrice[i] = mid - Tick * i; b.AskQty[i] = 100; b.BidQty[i] = 900; }
            return b;
        }

        [Fact]
        public void Engine_HardSkewOverOneSecond_ZeroConfidence_QuarantineFallsBackToSingleSource()
        {
            var c = new Clock();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: c.Read, crossConfirm: true);
            var b = Book();
            q.OnBook(in b, QuantSignalProvider.FeedKiwoom); q.OnBook(in b, QuantSignalProvider.FeedKis);
            Assert.True(q.TryGetSignal(Sym, out var s0) && s0.Sources == 2 && s0.Confidence > 0);
            c.Now += 700_000; q.OnBook(in b, QuantSignalProvider.FeedKiwoom);                               // 격차 0.7초 = 단일 출처 ½
            Assert.True(q.TryGetSignal(Sym, out var s1));
            Assert.Equal((int)QuantReason.SingleSource, s1.Reason);
            c.Now += 400_000; q.OnBook(in b, QuantSignalProvider.FeedKiwoom);                               // 격차 1.1초 = 동시성 붕괴
            Assert.True(q.TryGetSignal(Sym, out var s2));
            Assert.Equal((0.0, (int)QuantReason.CrossSkewVeto), (s2.Confidence, s2.Reason));                 // ★교차 확신 0
            Assert.True(s2.CrossSkewMicros > 1_000_000);
            Assert.Equal(1, q.CrossSkewVetoes);
            q.SetFeedQuarantined(QuantSignalProvider.FeedKis, true);                                         // KIS 격리 = 판정 제외
            c.Now += 1_000; q.OnBook(in b, QuantSignalProvider.FeedKiwoom);
            Assert.True(q.TryGetSignal(Sym, out var s3));
            Assert.Equal((1, (int)QuantReason.SingleSource), (s3.Sources, s3.Reason));                       // 키움 단일 출처(½ 감쇄 유지)
            q.OnBook(in b, QuantSignalProvider.FeedKis);
            Assert.Equal(1, q.QuarantinedInputs);                                                            // 격리 피드 입력 무시
            Assert.Throws<ArgumentOutOfRangeException>(() => new QuantSignalProvider(new StrategyAnomalyInterlock(), crossMaxSkewMicros: 500_000, crossHardSkewMicros: 400_000));
        }

        // ════════════ §5 δ=0 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void ParseRejectAndAdapterPaths_ZeroAlloc()
        {
            byte[] good = Body(Fields()), bad = Body(With(("ASKP_RSQN3", "12A"))), gap = Body(With(("ASKP4", "0"), ("ASKP_RSQN4", "0")));
            byte[] fGood = Frame(Fields()), fBad = Frame(With(("BIDP_RSQN4", "-5"))), fAuction = Frame(Fields("B"));
            var box = new MmfFlightRecorder(256);
            var c = new Clock();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: c.Read, crossConfirm: true);
            var a = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, () => 1), onBook: bk => q.OnBook(in bk, QuantSignalProvider.FeedKis), blackbox: box);
            var kb = Book();
            for (int i = 0; i < 50; i++) { KisOrderBookParser.TryParse(good, i, out _, out _, out _, out _, out _); a.OnFrame(fGood); a.OnFrame(fBad); a.OnFrame(fAuction); q.OnBook(in kb, 0); }
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    KisOrderBookParser.TryParse(good, i, out _, out _, out _, out _, out _);
                    KisOrderBookParser.TryParse(bad, i, out _, out _, out _, out _, out _);
                    KisOrderBookParser.TryParse(gap, i, out _, out _, out _, out _, out _);
                    a.OnFrame(fGood);
                    a.OnFrame(fBad);
                    a.OnFrame(fAuction);
                    c.Now += (i & 7) == 0 ? 1_200_000 : 1_000;                                              // 주기적 시각 붕괴 경로 포함
                    q.OnBook(in kb, QuantSignalProvider.FeedKiwoom);
                }
            }));
        }

        [Fact]
        public void Host_Wiring_QuarantineAndHardSkew_FiveBuilds()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string root = d!.FullName;
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("onBookQuarantine: q => Volatile.Read(ref quantRef)?.SetFeedQuarantined(StockManager.Engines.QuantSignalProvider.FeedKis, q));", form);
            Assert.Contains("crossHardSkewMicros: cfg.GetInt(\"Strategy.CrossHardSkewMs\", 1_000, 50, 60_000) * 1000L", form);
            Assert.Equal((5, 20), (KisOpenApiAdapter.BookQuarantineAfter, KisOpenApiAdapter.BookReleaseAfter));
            Assert.Equal((98, 99), ((int)FlightEventKind.KisBookRejected, (int)FlightEventKind.KisBookQuarantine));
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}
