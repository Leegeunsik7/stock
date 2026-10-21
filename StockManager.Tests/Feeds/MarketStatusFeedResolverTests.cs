// ============================================================================
// MarketStatusFeedResolverTests — [v12.18.50-DEV] 장운영 세션 · VI/정지 이벤트 → AI 파이프라인 거래 상태(δ=0) · H0STMKO0 판독
//
//   §1 세션: KST 평일 09:00 ≤ t < 15:20 경계 · 15:30 장외 · 주말 · 휴장일 판정기 · 판정기 예외 = 휴장(내장 달력 · 동시호가 = KrxTradingCalendarTests)
//   §2 전이: 피드 생존 + 재동기 보류 만료 → Normal → 인계 · 동적/정적 VI · 정지 = 즉시 차단 · ★VI 중 호가 Normal 무력 · 옛 해제 거부 ·
//      해제 이벤트로만 복귀 · 장외(15:30) = 이벤트 해제 · 호가와 무관하게 Closed · 피드 단절 = Unknown · 재접속 보류(프레임 수신 종목만 조기 해제) ·
//      판독 실패 = 전 종목 재동기 · 미등록 종목 = 차단 유지
//   §3 판독기: 11필드 · 다건 · 사상(정지 Y · VI N/1/2/3 · 미분류 = Unknown) · 잘림 · 종목코드 이탈 · 실 어댑터 프레임 → 리졸버
//   §4 δ=0 · 적대: 10만 이벤트+평가+신호 ZeroAllocProbe 0B · 적대 이벤트/시각/피드 스트림 = 장외 · VI · 정지 · 미상 틱 인계 0
// ============================================================================
using System;
using System.Text;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Adapters.Kis;
using StockManager.Engines;
using StockManager.Engines.Feeds;
using StockManager.Engines.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Feeds
{
    [Collection("ConcurrencyStress")]
    public sealed class MarketStatusFeedResolverTests
    {
        private const long S = KrxTickAligner.Scale;
        private const long A = 5930, B = 660;
        private const long Min = 60_000_000L, Sec = 1_000_000L;
        private static readonly long Base = 70_000 * S;
        // 2026-10-21(수) KST 09:00 = UTC 00:00
        private static readonly long Open = (new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        private readonly ITestOutputHelper _out;
        public MarketStatusFeedResolverTests(ITestOutputHelper o) => _out = o;

        private sealed class CountingSink : IAiProposalSink
        {
            public long Count;
            public bool TrySubmit(in OrderProposal p) { Count++; return true; }
        }

        private sealed class Rig
        {
            public MarketStatusFeedResolver R = null!;
            public AiTradingPipelineService P = null!;
            public CountingSink Sink = null!;
            public long Now;                                                                  // 게이트 벽시계(테스트 제어)
        }

        private static Rig Make(long hold = 150 * Sec, Func<long, bool>? holiday = null, params long[] symbols)
        {
            var anomaly = new StrategyAnomalyInterlock();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            anomaly.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            Assert.True(mask.Seal(out _));
            var sink = new CountingSink();
            var p = new AiTradingPipelineService(new AiExecutionSupervisor(new MultiSymbolAiSignalRouter(anomaly, 16, 1_000, 10), mask), sink);
            Assert.True(p.TryActivate(true, true));
            var g = new Rig { P = p, Sink = sink };
            var r = new MarketStatusFeedResolver(p, holiday, hold, utcNowMicros: () => System.Threading.Volatile.Read(ref g.Now));
            foreach (var s in symbols.Length == 0 ? new[] { A, B } : symbols) Assert.True(r.Register(s));
            p.AttachStatusGate(r);
            g.R = r;
            return g;
        }

        // 호가(상태 미제공 = Unknown · 호스트와 동일) + 신호 → 인계 여부
        private static bool Trade(Rig g, long sym, long t, MarketTradingStatus quoteStatus = MarketTradingStatus.Unknown)
        {
            g.Now = t;
            g.P.OnQuote(sym, Base, 69_900 * S, 70_000 * S, quoteStatus, t);
            return g.P.OnSignal(new AiSignal(sym, 0.9, 0.5, 0.5, t), t) == AiPipelineVerdict.Submitted;
        }

        // 생존 + 보류 만료까지 진행한 장중 시각
        private static long Live(Rig g, long t0)
        {
            g.R.SetFeedAlive(true, t0);
            long t = t0 + 150 * Sec;
            g.R.Evaluate(t);
            return t;
        }

        // ════════════ §1 세션 ════════════

        [Theory]
        [InlineData(0, 0, true)]                       // 09:00:00
        [InlineData(-1, 0, false)]                     // 08:59:59.999999
        [InlineData(379, 59_999_999, true)]            // 15:19:59.999999
        [InlineData(380, 0, false)]                    // 15:20 (종가 단일가 시작)
        [InlineData(390, 0, false)]                    // 15:30 장외
        [InlineData(-60, 0, false)]                    // 08:00
        public void Continuous_Session_Boundaries_Kst(long minutesFromOpen, long extraMicros, bool expect)
        {
            var g = Make();
            Assert.Equal(expect, g.R.InContinuousSession(Open + minutesFromOpen * Min + extraMicros));
        }

        [Fact]
        public void Weekend_Holiday_And_Faulty_Holiday_Oracle_Are_Closed()
        {
            var g = Make();
            Assert.False(g.R.InContinuousSession(Open + 3 * 1440 * Min + 60 * Min));        // 토 10:00
            Assert.False(g.R.InContinuousSession(Open + 4 * 1440 * Min + 60 * Min));        // 일 10:00
            Assert.True(g.R.InContinuousSession(Open + 5 * 1440 * Min + 60 * Min));         // 월 10:00
            long wedDay = (Open + 9 * 60 * Min) / (1440 * Min);
            var h = Make(holiday: d => d == wedDay);
            Assert.False(h.R.InContinuousSession(Open + 60 * Min));                          // 휴장 수요일
            var bad = Make(holiday: _ => throw new InvalidOperationException());
            Assert.False(bad.R.InContinuousSession(Open + 60 * Min));                        // 판정 예외 = 휴장(fail-closed)
        }

        // ════════════ §2 전이 ════════════

        [Fact]
        public void Live_Feed_After_Resync_Hold_Normalizes_And_Trades()
        {
            var g = Make();
            long t = Open + 10 * Min;
            g.R.SetFeedAlive(true, t);
            g.R.Evaluate(t);
            Assert.False(Trade(g, A, t));                                                    // 보류 중 = Unknown
            Assert.Equal(MarketTradingStatus.Unknown, g.R.EffectiveStatus(A, t));
            t += 150 * Sec;
            g.R.Evaluate(t);
            Assert.Equal(MarketTradingStatus.Normal, g.R.EffectiveStatus(A, t));
            Assert.True(Trade(g, A, t));
            Assert.Equal(1, g.Sink.Count);
        }

        [Theory]
        [InlineData(MarketTradingStatus.DynamicVi)]
        [InlineData(MarketTradingStatus.StaticVi)]
        [InlineData(MarketTradingStatus.Halted)]
        [InlineData(MarketTradingStatus.Unknown)]
        public void Restrictive_Event_Blocks_Quote_Normal_Cannot_Lift_Only_Release_Event_Can(MarketTradingStatus st)
        {
            var g = Make();
            long t = Live(g, Open + 10 * Min);
            Assert.True(Trade(g, A, t));
            Assert.Equal(MarketStatusEventVerdict.Applied, g.R.OnStatusEvent(A, st, t + 1, t + 1));
            for (int i = 2; i < 50; i++)
            {
                Assert.False(Trade(g, A, t + i, MarketTradingStatus.Normal));                // ★호가가 Normal 을 실어 와도 해제 0
                g.R.Evaluate(t + i);                                                         // 세션/생존 재평가도 해제 0
            }
            Assert.True(Trade(g, B, t + 60));                                                // 다른 종목 무영향
            Assert.Equal(MarketStatusEventVerdict.StaleRelease, g.R.OnStatusEvent(A, MarketTradingStatus.Normal, t, t + 61));   // 옛 해제 거부
            Assert.False(Trade(g, A, t + 62));
            Assert.Equal(MarketStatusEventVerdict.Applied, g.R.OnStatusEvent(A, MarketTradingStatus.Normal, t + 63, t + 63));
            Assert.True(Trade(g, A, t + 64));
            Assert.Equal(1, g.R.StaleReleases);
        }

        [Fact]
        public void After_Close_1530_Everything_Blocked_Even_With_Release_Event_And_Normal_Quote()
        {
            var g = Make();
            long t = Live(g, Open + 6 * 60 * Min);                                           // 15:00 + 보류
            Assert.True(Trade(g, A, t));
            long late = Open + 390 * Min;                                                    // 15:30
            g.R.Evaluate(late);
            Assert.Equal(MarketTradingStatus.Closed, g.R.EffectiveStatus(A, late));
            Assert.False(Trade(g, A, late, MarketTradingStatus.Normal));
            g.R.OnStatusEvent(A, MarketTradingStatus.Normal, late + 1, late + 1);
            Assert.False(Trade(g, A, late + 2));
            Assert.False(Trade(g, B, late + 3));
            long nextOpen = Open + 1440 * Min + 30 * Sec;                                    // 다음 날 09:00:30
            g.R.Evaluate(nextOpen);
            Assert.True(Trade(g, A, nextOpen));                                              // 생존 유지 · 보류 만료 = 개장 즉시 정상
        }

        [Fact]
        public void Feed_Loss_Is_Unknown_And_Reconnect_Holds_Until_Frame_Or_Expiry()
        {
            var g = Make();
            long t = Live(g, Open + 30 * Min);
            Assert.True(Trade(g, A, t) && Trade(g, B, t));
            g.R.SetFeedAlive(false, t + 1);
            g.R.Evaluate(t + 1);
            Assert.False(Trade(g, A, t + 2));
            Assert.Equal(MarketTradingStatus.Unknown, g.R.EffectiveStatus(B, t + 2));
            g.R.SetFeedAlive(true, t + 10 * Sec);
            g.R.Evaluate(t + 10 * Sec);
            Assert.False(Trade(g, A, t + 11 * Sec));                                         // 재접속 보류
            g.R.OnStatusEvent(A, MarketTradingStatus.Normal, t + 12 * Sec, t + 12 * Sec);    // A 프레임 수신 = A 만 조기 해제
            Assert.True(Trade(g, A, t + 13 * Sec));
            Assert.False(Trade(g, B, t + 13 * Sec));
            long exp = t + 10 * Sec + 150 * Sec;
            g.R.Evaluate(exp);
            Assert.True(Trade(g, B, exp));
        }

        [Fact]
        public void Malformed_Frame_Restarts_Resync_For_All_Symbols()
        {
            var g = Make();
            long t = Live(g, Open + 30 * Min);
            Assert.True(Trade(g, A, t));
            g.R.NoteFeedFault(t + 1);
            Assert.False(Trade(g, A, t + 2) || Trade(g, B, t + 2));
            g.R.Evaluate(t + 1 + 150 * Sec);
            Assert.True(Trade(g, A, t + 1 + 150 * Sec));
            Assert.Equal(1, g.R.FeedFaults);
        }

        [Fact]
        public void Unregistered_Symbol_Never_Normal_And_Slots_Cap_At_64()
        {
            var g = Make(symbols: A);
            long t = Live(g, Open + 30 * Min);
            Assert.False(Trade(g, B, t));                                                    // 미등록 = 상태 통지 0 = Unknown
            var full = Make(symbols: A);
            for (long s = 100_000; s < 100_063; s++) Assert.True(full.R.Register(s));
            Assert.False(full.R.Register(999_999));
            Assert.Equal(MarketStatusEventVerdict.SlotsExhausted, full.R.OnStatusEvent(999_998, MarketTradingStatus.Halted, t, t));
            Assert.Equal(MarketStatusEventVerdict.InvalidSymbol, full.R.OnStatusEvent(0, MarketTradingStatus.Halted, t, t));
        }

        // ════════════ §3 판독기 ════════════

        [Theory]
        [InlineData("N", "N", MarketTradingStatus.Normal)]
        [InlineData("N", "1", MarketTradingStatus.StaticVi)]
        [InlineData("N", "2", MarketTradingStatus.DynamicVi)]
        [InlineData("N", "3", MarketTradingStatus.StaticVi)]
        [InlineData("Y", "N", MarketTradingStatus.Halted)]
        [InlineData("Y", "2", MarketTradingStatus.Halted)]
        [InlineData("N", "4", MarketTradingStatus.Unknown)]
        [InlineData("N", "", MarketTradingStatus.Unknown)]
        [InlineData("", "N", MarketTradingStatus.Unknown)]
        [InlineData("X", "N", MarketTradingStatus.Unknown)]
        [InlineData(" N ", " N ", MarketTradingStatus.Normal)]
        public void Status_Mapping_Is_Fail_Closed(string trht, string vi, MarketTradingStatus expect)
            => Assert.Equal(expect, KisMarketStatusParser.MapStatus(Encoding.ASCII.GetBytes(trht), Encoding.ASCII.GetBytes(vi)));

        private static string Rec(string code, string trht, string vi) => $"{code}^{trht}^^20^^^^00^{vi}^N^K";

        [Fact]
        public void Parser_Reads_Multi_Record_Body_And_Rejects_Truncation_And_Bad_Code()
        {
            ReadOnlySpan<byte> body = Encoding.ASCII.GetBytes(Rec("005930", "N", "2") + "^" + Rec("000660", "Y", "N"));
            Assert.True(KisMarketStatusParser.TryReadRecord(ref body, out long s1, out var st1, out _));
            Assert.True(KisMarketStatusParser.TryReadRecord(ref body, out long s2, out var st2, out _));
            Assert.Equal((5930L, MarketTradingStatus.DynamicVi, 660L, MarketTradingStatus.Halted), (s1, st1, s2, st2));
            Assert.True(body.IsEmpty);
            ReadOnlySpan<byte> trunc = Encoding.ASCII.GetBytes("005930^N^^20^^^^00^2");
            Assert.False(KisMarketStatusParser.TryReadRecord(ref trunc, out _, out _, out var r1));
            Assert.Equal(KisMarketStatusReject.Truncated, r1);
            ReadOnlySpan<byte> bad = Encoding.ASCII.GetBytes(Rec("05930X", "N", "N"));
            Assert.False(KisMarketStatusParser.TryReadRecord(ref bad, out _, out _, out var r2));
            Assert.Equal(KisMarketStatusReject.BadCode, r2);
            Assert.True(KisMarketStatusParser.IsMarketStatusTr("H0STMKO0"u8));
            Assert.False(KisMarketStatusParser.IsMarketStatusTr("H0STCNI0"u8));               // 체결통보 ≠ 장운영
        }

        [Fact]
        public void Real_Adapter_Routes_H0STMKO0_Frames_To_Resolver_And_Faults_Restart_Resync()
        {
            var g = Make();
            long t = Live(g, Open + 30 * Min);
            var session = new KisSessionInterlock(null, 1, () => 1);
            using var a = new KisOpenApiAdapter(session) { MarketStatusSink = g.R };
            Assert.True(a.OnFrame(Encoding.ASCII.GetBytes("0|H0STMKO0|002|" + Rec("005930", "N", "2") + "^" + Rec("000660", "Y", "N"))));
            Assert.Equal(2, a.MarketStatusRecords);
            Assert.Equal(MarketTradingStatus.DynamicVi, g.R.EffectiveStatus(A, t + 1));        // 래치는 벽시계와 무관(장중 시각으로 판정)
            Assert.Equal(MarketTradingStatus.Halted, g.R.EffectiveStatus(B, t + 1));
            Assert.False(Trade(g, A, t + 1) || Trade(g, B, t + 1));
            Assert.False(a.OnFrame(Encoding.ASCII.GetBytes("0|H0STMKO0|001|005930^N^^20")));   // 잘림 = 거부 + 재동기
            Assert.Equal((1L, 1L), (a.MarketStatusRejects, g.R.FeedFaults));
            Assert.False(a.OnFrame(Encoding.ASCII.GetBytes("1|H0STMKO0|001|" + Rec("005930", "N", "N"))));   // 암호 플래그 = 형식 이탈
            Assert.Equal(2, a.MarketStatusRejects);
        }

        [Fact]
        public void Signal_Time_Gate_Closes_Timer_Gap_At_1520_And_On_Feed_Loss()
        {
            var g = Make();
            long t = Live(g, Open + 10 * Min);
            Assert.True(Trade(g, A, t));
            long close = Open + 380 * Min;                                                    // 15:20:00 — Evaluate 없이(타이머 틈)
            g.P.OnQuote(A, Base, 69_900 * S, 70_000 * S, MarketTradingStatus.Unknown, close);
            g.Now = close;
            Assert.Equal(AiPipelineVerdict.StatusGateBlocked, g.P.OnSignal(new AiSignal(A, 0.9, 0.5, 0.5, close), close));
            Assert.True(g.P.Supervisor.TryGetMarketContext(A, out var ctx) && ctx.Status == MarketTradingStatus.Normal);   // 슬롯은 아직 Normal(통지 전)
            g.R.SetFeedAlive(false, t + 1);                                                    // 생존 전이 = 즉시 통지
            Assert.True(g.P.Supervisor.TryGetMarketContext(A, out ctx) && ctx.Status == MarketTradingStatus.Unknown);
        }

        // ════════════ §4 δ=0 · 적대 ════════════

        [Fact]
        public void HundredK_Status_Events_Evaluate_And_Signals_Are_Zero_Alloc()
        {
            var syms = new long[16];
            for (int k = 0; k < 16; k++) syms[k] = 500_000 + k * 7;
            var g = Make(symbols: syms);
            long t = Live(g, Open + 20 * Min);
            var cycle = new[] { MarketTradingStatus.DynamicVi, MarketTradingStatus.Normal, MarketTradingStatus.StaticVi, MarketTradingStatus.Normal,
                                MarketTradingStatus.Halted, MarketTradingStatus.Normal };
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    int k = i & 15;
                    t += 10;
                    g.Now = t;                                                                // 게이트 벽시계 = 신호 시각(미설정 0 = 1970 = 달력 범위 밖 차단)
                    g.R.OnStatusEvent(syms[k], cycle[(i >> 4) % cycle.Length], t, t);
                    if ((i & 255) == 0) g.R.Evaluate(t);
                    g.P.OnQuote(syms[k], Base, 69_900 * S, 70_000 * S, MarketTradingStatus.Unknown, t);
                    g.P.OnSignal(new AiSignal(syms[k], 0.9, 0.5, 0.5, t), t);
                }
            }
            Run();
            long min = ZeroAllocProbe.MinAllocated(Run);
            _out.WriteLine($"[δ] 16종목 10만 상태 이벤트+평가+호가+신호 = {min}B · 반영 {g.R.Applied} · 통지 {g.R.Pushes} · 인계 {g.Sink.Count}");
            Assert.True(g.Sink.Count > 1_000 && g.R.Pushes > 10_000);
            Assert.Equal(0, min);
        }

        [Fact]
        public void Adversarial_Stream_Never_Submits_Outside_Session_Or_Under_Restriction()
        {
            var rng = new Random(20261021);
            var statuses = new[] { MarketTradingStatus.Normal, MarketTradingStatus.Normal, MarketTradingStatus.DynamicVi, MarketTradingStatus.StaticVi,
                                   MarketTradingStatus.Halted, MarketTradingStatus.Unknown, (MarketTradingStatus)9 };
            long submitted = 0, blocked = 0;
            for (int run = 0; run < 60; run++)
            {
                var g = Make(hold: rng.Next(0, 3) * 30 * Sec, symbols: new long[] { 1, 1_013, 2_026, 3_039 });
                long t = Open - 20 * Min + rng.Next(0, 400) * Min;                               // 08:40 ~ 15:20 + 이후
                for (int i = 0; i < 600; i++)
                {
                    t += rng.Next(1, 40) * Sec;
                    long sym = 1 + rng.Next(4) * 1_013;
                    int op = rng.Next(10);
                    if (op < 3) g.R.OnStatusEvent(sym, statuses[rng.Next(statuses.Length)], t - rng.Next(0, 5) * Sec, t);
                    else if (op == 3) g.R.SetFeedAlive(rng.Next(4) != 0, t);
                    else if (op == 4) g.R.NoteFeedFault(t);
                    if (rng.Next(3) == 0) g.R.Evaluate(t);
                    var eff = g.R.EffectiveStatus(sym, t);
                    bool inSession = g.R.InContinuousSession(t);
                    long before = g.Sink.Count;
                    bool ok = Trade(g, sym, t, rng.Next(2) == 0 ? MarketTradingStatus.Normal : MarketTradingStatus.Unknown);
                    if (ok)
                    {
                        Assert.True(inSession);                                                   // ★장외 인계 0
                        Assert.Equal(MarketTradingStatus.Normal, eff);                            // ★VI · 정지 · 미상 · 단절 · 보류 인계 0
                        submitted++;
                    }
                    else { Assert.Equal(before, g.Sink.Count); if (eff != MarketTradingStatus.Normal) blocked++; }
                }
            }
            _out.WriteLine($"[적대] 60회 × 600틱 · 인계 {submitted}건(전부 장중 · 유효 Normal) · 비정상 차단 {blocked}건");
            Assert.True(submitted > 100 && blocked > 1_000);
        }
    }
}
