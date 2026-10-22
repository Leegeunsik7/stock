// ============================================================================
// UsTechArenaEngineTests — v12.18.61-DEV: 미국 빅테크 아레나(휴먼 vs AI · 가상 $100,000 듀얼 USD 원장) · 설명 가능한 AI 근거(Usd)
//
//   §1 세션 게이트   프리마켓 = 탐색/준비(AI · 휴먼 체결 0) → 정규장 자동 진입 체결 · 마감 10분 전 청산 · 애프터/휴장/달력 밖 차단 ·
//                    EDT/EST 경계 · 미 동부 날짜 변경 = 전일 종가 확정 → 프리마켓 갭/수급 주도 진입
//   §2 근거 무결성   진입/익절 근거 필드 · $0.01 격자 정렬(UsTickAligner) · 무작위 장세 · 합성 피드 다일 구동에서 AI 체결 == 근거 수(1:1) ·
//                    모든 근거 = 정규장 시각 · 한국어 USD 요약
//   §3 듀얼 원장     휴먼 체결이 AI 현금/보유에 영향 0 · 표 밖 티커/무효 호가 폐기
//   §4 피드          KIS HHDFS00000300 응답 → $0.01 정렬 센트 → 엔진 · rsym 불일치/체결가 부재 폐기 · 출처 전환 초기화 · 허브 펌프 구동
//   §5 동시성        4 생산자 스레드 동시 호가 주입 + 펌프 + 판독: 호가 찢김 0 · 원장/근거 불일치 0
//   §6 δ=0           시그널 평가 · 가상 체결 10만 회 0B · KIS/합성 피드 10만 회 0B(ZeroAllocProbe) · 핫패스 IL 할당 지점 0
//   §7 Pure Shell    IUsArenaPort 무장/브로커/송출 멤버 0 · DTO 포터블 · 엔진/주입기 네트워크 필드 0 · 어댑터 투영 · DI 등록
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class UsTechArenaEngineTests
    {
        private const int Aapl = UsTechUniverse.Aapl, Nvda = UsTechUniverse.Nvda;
        // 2026-10-22(목) — 미 동부 EDT(KST − 13h): 프리 17:00~22:30 · 정규 22:30~익일 05:00 KST
        private static readonly DateTime Pre21 = new(2026, 10, 22, 21, 0, 0);
        private static readonly DateTime Reg23 = new(2026, 10, 22, 23, 0, 0);
        private static readonly DateTime Cutoff0451 = new(2026, 10, 23, 4, 51, 0);    // 15:51 ET
        private static readonly DateTime After0530 = new(2026, 10, 23, 5, 30, 0);     // 16:30 ET
        private static readonly long UtcReg23 = Reg23.Ticks - TimeSpan.TicksPerHour * 9;

        private readonly ITestOutputHelper _out;
        public UsTechArenaEngineTests(ITestOutputHelper o) => _out = o;

        private static long Sym(int i) => UsTechUniverse.Symbol(i);

        private static void Q(UsTechArenaEngine e, int idx, long bid, long ask, ref DateTime t, long bidQty = 1000, long askQty = 1000)
        {
            t = t.AddMilliseconds(100);
            e.OnQuote(Sym(idx), bid, ask, bidQty, askQty, t);
        }

        private static void Warm(UsTechArenaEngine e, ref DateTime t, int idx = Nvda, long bid = 13_000, long ask = 13_001)
        {
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks + 1; i++) Q(e, idx, bid, ask, ref t);
        }

        // QQQ +300 · SOXX +300 · 10년물 −10bp · 불균형 +8000 · 가격 상승 → NVDA 점수 ≈ 3700(진입 · 주도 ETF 베타)
        private static void Spike(UsTechArenaEngine e, ref DateTime t, int idx = Nvda, long bid = 13_050, long ask = 13_051)
        {
            e.SetMarketContext(300, 300, -10);
            Q(e, idx, bid, ask, ref t, bidQty: 9000, askQty: 1000);
        }

        private static UsArenaSnapshot Snap(UsTechArenaEngine e)
        {
            Assert.True(e.TryReadSnapshot(out var s));
            return s;
        }

        private static AiTradeRationale[] Rationales(UsTechArenaEngine e)
        {
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            int n = e.CopyRationales(buf);
            return buf.AsSpan(0, n).ToArray();
        }

        private static bool CentsAligned(long cents) => UsTickAligner.IsAligned(cents * UsTickAligner.PennyRaw);

        // ── §1 세션 게이트 ───────────────────────────────────────────────────────

        [Fact]
        public void PreMarket_Explores_Only_Then_Regular_Session_Auto_Enters()
        {
            var e = new UsTechArenaEngine();
            var t = Pre21;
            Warm(e, ref t);
            Spike(e, ref t);
            Assert.Equal(UsArenaReject.None, e.SubmitHuman(Sym(Nvda), 1, 10));
            Q(e, Nvda, 13_050, 13_051, ref t, 9000, 1000);
            var pre = Snap(e);
            Assert.Equal(UsSessionPhase.PreMarket, pre.Phase);
            Assert.False(pre.Tradable);
            Assert.Equal(0, pre.Ai.Fills);
            Assert.Equal(0, pre.RationaleCount);
            Assert.Equal(0, pre.Human.Fills);
            Assert.Equal(1, pre.Human.Rejects);                       // SessionClosed
            Assert.Equal(UsTechArenaEngine.DefaultCapitalCents, pre.Ai.CashWon);

            t = Reg23;                                                // 정규장 자동 진입(같은 시그널)
            Spike(e, ref t, bid: 13_060, ask: 13_061);
            var reg = Snap(e);
            Assert.Equal(UsSessionPhase.ContinuousTrading, reg.Phase);
            Assert.True(reg.Tradable);
            Assert.Equal(1, reg.Ai.Fills);
            Assert.Equal(1, reg.RationaleCount);
            Assert.Equal(1, reg.Ai.OpenPositions);
            Assert.Equal(UsTechArenaEngine.DefaultCapitalCents, reg.Human.CashWon);
        }

        [Fact]
        public void Cutoff_Flattens_And_AfterHours_Holiday_Uncovered_Block_All_Fills()
        {
            var e = new UsTechArenaEngine();
            var t = Reg23;
            Warm(e, ref t); Spike(e, ref t);
            Assert.Equal(1, Snap(e).Ai.Fills);

            t = Cutoff0451;
            Q(e, Nvda, 13_052, 13_053, ref t, 9000, 1000);
            var r = Rationales(e);
            Assert.Equal(2, r.Length);
            Assert.Equal(ArenaTrigger.SessionClose, r[1].TriggerType);
            Assert.Equal(-1, r[1].Side);
            Spike(e, ref t, bid: 13_100, ask: 13_101);                // 마감 10분 전 = 신규 진입 0
            Assert.Equal(2, Snap(e).Ai.Fills);

            t = After0530;
            Spike(e, ref t, bid: 13_200, ask: 13_201);
            Assert.Equal(UsSessionPhase.AfterHours, Snap(e).Phase);
            Assert.Equal(2, Snap(e).Ai.Fills);

            t = new DateTime(2026, 11, 27, 0, 30, 0);                 // 추수감사절 2026-11-26 10:30 EST
            Spike(e, ref t, bid: 13_300, ask: 13_301);
            Assert.Equal(UsSessionPhase.HolidayOrWeekend, Snap(e).Phase);
            t = new DateTime(2030, 1, 3, 0, 30, 0);                   // 달력 표 범위(2024~2028) 밖
            Spike(e, ref t, bid: 13_400, ask: 13_401);
            Assert.Equal(UsSessionPhase.CalendarUncovered, Snap(e).Phase);
            Assert.Equal(2, Snap(e).Ai.Fills);
            Assert.Equal(2, Snap(e).RationaleCount);
        }

        [Theory]
        [InlineData(2026, 10, 22, 16, 59, UsSessionPhase.Closed)]
        [InlineData(2026, 10, 22, 17, 0, UsSessionPhase.PreMarket)]
        [InlineData(2026, 10, 22, 22, 29, UsSessionPhase.PreMarket)]
        [InlineData(2026, 10, 22, 22, 30, UsSessionPhase.ContinuousTrading)]
        [InlineData(2026, 10, 23, 4, 59, UsSessionPhase.ContinuousTrading)]
        [InlineData(2026, 10, 23, 5, 0, UsSessionPhase.AfterHours)]
        [InlineData(2026, 11, 5, 22, 45, UsSessionPhase.PreMarket)]          // EST: 정규 23:30 KST
        [InlineData(2026, 11, 5, 23, 30, UsSessionPhase.ContinuousTrading)]
        [InlineData(2026, 10, 24, 23, 0, UsSessionPhase.HolidayOrWeekend)]   // 토요일
        public void Phase_Binds_UsSessionStateMachine_With_Dst(int y, int mo, int d, int h, int mi, UsSessionPhase expected)
        {
            var kst = new DateTime(y, mo, d, h, mi, 0);
            Assert.Equal(expected, UsTechArenaEngine.PhaseAt(kst));
            Assert.Equal(UsSessionStateMachine.GetSessionPhase(kst), UsTechArenaEngine.PhaseAt(kst));
            var e = new UsTechArenaEngine();
            e.Drain(kst);
            Assert.Equal(expected, Snap(e).Phase);
        }

        [Fact]
        public void Day_Roll_Fixes_Prev_Close_And_PreMarket_Gap_Flow_Leads_Entry()
        {
            var e = new UsTechArenaEngine();
            var t = Reg23;                                            // 1일차 정규장 · 중립(맥락 0 · 균형 호가) → 진입 0 · 종가 $100.00
            for (int i = 0; i < 10; i++) Q(e, Aapl, 10_000, 10_001, ref t);
            Assert.Equal(0, Snap(e).Ai.Fills);

            t = new DateTime(2026, 10, 23, 21, 0, 0);                 // 2일차 프리마켓(08:00 ET) · +5% 갭 · 매수 우위
            e.SetMarketContext(300, 0, -10);
            for (int i = 0; i < 20; i++) Q(e, Aapl, 10_500, 10_501, ref t, 39_000, 1_000);
            Assert.Equal(UsSessionPhase.PreMarket, Snap(e).Phase);
            Assert.Equal(0, Snap(e).Ai.Fills);

            t = new DateTime(2026, 10, 23, 22, 31, 0);                // 정규장 개장
            Q(e, Aapl, 10_550, 10_551, ref t, 39_000, 1_000);
            var r = Assert.Single(Rationales(e));
            _out.WriteLine(r.HumanReadableSummary);
            Assert.Equal(ArenaTrigger.PremarketGapFlow, r.TriggerType);
            Assert.True(r.ImbalanceBp > 4000, $"갭/수급 {r.ImbalanceBp}");   // (갭 499bp × 10 + 불균형 9500)/2
            Assert.True(r.SurgeBp > 0 && r.MacroBp > 0 && r.MomentumBp > 0);
        }

        // ── §2 근거 무결성 ─────────────────────────────────────────────────────

        [Fact]
        public void Entry_And_TakeProfit_Rationales_Are_Complete_Usd_And_Penny_Aligned()
        {
            var e = new UsTechArenaEngine();
            var t = Reg23;
            Warm(e, ref t); Spike(e, ref t);
            var entry = Assert.Single(Rationales(e));
            _out.WriteLine(entry.HumanReadableSummary);
            Assert.True(entry.IsWellFormed);
            Assert.Equal(ArenaCurrency.Usd, entry.Currency);
            Assert.Equal(ArenaTrigger.EtfBeta, entry.TriggerType);
            Assert.Equal(Sym(Nvda), entry.Symbol);
            Assert.Equal(1, entry.Side);
            Assert.Equal(13_051, entry.PriceWon);                    // 센트 = 매도1호가
            int conf = entry.SentimentScore / 100;
            Assert.Equal(conf, entry.ConfidenceScore);
            Assert.Equal(UsTechArenaEngine.AlignUpCents(13_051, UsTechArenaEngine.TargetBaseBp + UsTechArenaEngine.TargetPerConfidenceBp * conf), entry.TargetPrice);
            Assert.Equal(UsTechArenaEngine.AlignDownCents(13_051, -UsTechArenaEngine.StopBp), entry.StopLossPrice);
            Assert.True(CentsAligned(entry.PriceWon) && CentsAligned(entry.TargetPrice) && CentsAligned(entry.StopLossPrice));
            Assert.Equal((entry.TargetPrice - 13_051) * 100 / (13_051 - entry.StopLossPrice), entry.RiskRewardX100);
            Assert.True(entry.SurgeBp > 0 && entry.MacroBp > 0 && entry.ImbalanceBp > 0);
            Assert.Equal(new DateTime(entry.KstTicks), entry.AtKst);
            Assert.Contains("NVDA(NASD)", entry.HumanReadableSummary);
            Assert.Contains("QQQ/반도체 ETF 베타", entry.HumanReadableSummary);
            Assert.Contains("$130.51", entry.HumanReadableSummary);
            long qty = entry.Qty;
            Assert.True(qty > 0 && qty * 13_051 <= UsTechArenaEngine.DefaultCapitalCents * UsTechArenaEngine.PositionWeightBp / 10000);

            Q(e, Nvda, entry.TargetPrice + 5, entry.TargetPrice + 6, ref t);
            var r = Rationales(e);
            Assert.Equal(2, r.Length);
            var exit = r[1];
            Assert.True(exit.IsWellFormed);
            Assert.Equal(ArenaTrigger.TakeProfit, exit.TriggerType);
            Assert.Equal(entry.TargetPrice, exit.TargetPrice);
            Assert.Equal(entry.StopLossPrice, exit.StopLossPrice);
            Assert.Equal(qty, exit.Qty);
            Assert.True(exit.RealizedPnlWon > 0);
            Assert.Contains("실현손익 +$", exit.HumanReadableSummary);
            var s = Snap(e);
            Assert.Equal(1, s.Ai.Wins);
            Assert.Equal(exit.RealizedPnlWon, s.Ai.RealizedWon);
            Assert.True(s.Ai.TaxWon > 0);                              // SEC 수수료(매도)
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
        }

        [Fact]
        public void StopLoss_Exit_Counts_Loss()
        {
            var e = new UsTechArenaEngine();
            var t = Reg23;
            Warm(e, ref t); Spike(e, ref t);
            var entry = Rationales(e)[0];
            Q(e, Nvda, entry.StopLossPrice - 1, entry.StopLossPrice, ref t);
            var x = Rationales(e)[1];
            Assert.Equal(ArenaTrigger.StopLoss, x.TriggerType);
            Assert.True(x.RealizedPnlWon < 0);
            Assert.Equal(1, Snap(e).Ai.Losses);
            Assert.True(Snap(e).Ai.MddBp > 0);
        }

        [Fact]
        public void Every_Ai_Fill_Has_One_WellFormed_Regular_Session_Rationale_Under_Random_Tape()
        {
            var e = new UsTechArenaEngine();
            var rnd = new Random(61);
            var px = new long[UsTechUniverse.Count];
            for (int k = 0; k < px.Length; k++) px[k] = UsTechUniverse.SyntheticStartCents(k);
            var t = new DateTime(2026, 10, 22, 20, 0, 0);            // 프리마켓 → 정규 → 마감 → 애프터 → 익일 프리/정규
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            long checkedCount = 0;
            for (int i = 0; i < 40_000; i++)
            {
                t = t.AddSeconds(2);
                if (i % 500 == 0) e.SetMarketContext(rnd.Next(-600, 601), rnd.Next(-800, 801), rnd.Next(-20, 21));
                int k = rnd.Next(UsTechUniverse.Count);
                px[k] = Math.Clamp(px[k] + rnd.Next(-4, 5), UsTechUniverse.SyntheticStartCents(k) * 8 / 10, UsTechUniverse.SyntheticStartCents(k) * 12 / 10);
                e.OnQuote(Sym(k), px[k], px[k] + rnd.Next(1, 3), rnd.Next(100, 6000), rnd.Next(100, 6000), t);
                if ((i & 1) == 0) e.SubmitHuman(Sym(rnd.Next(UsTechUniverse.Count)), rnd.Next(2) == 0 ? 1 : -1, rnd.Next(1, 20));
                if (i % 100 == 99)
                {
                    int n = e.CopyRationales(buf);
                    for (int j = 0; j < n; j++)
                    {
                        var r = buf[j];
                        Assert.True(r.IsWellFormed);
                        Assert.Equal(ArenaCurrency.Usd, r.Currency);
                        Assert.True(CentsAligned(r.PriceWon) && CentsAligned(r.TargetPrice) && CentsAligned(r.StopLossPrice));
                        Assert.Equal(UsSessionPhase.ContinuousTrading, UsTechArenaEngine.PhaseAt(r.AtKst));
                        checkedCount++;
                    }
                }
            }
            var s = Snap(e);
            _out.WriteLine($"aiFills={s.Ai.Fills} rationales={s.RationaleCount} humanFills={s.Human.Fills} humanRejects={s.Human.Rejects} aiRet={s.Ai.ReturnBp}bp checked={checkedCount}");
            Assert.True(s.Ai.Fills > 10, "무작위 장세에서 AI 체결이 발생해야 한다");
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
            Assert.True(s.Human.Fills > 0 && s.Human.Rejects > 0);   // 정규장 체결 + 장외 SessionClosed
            Assert.True(s.Ai.CashWon >= 0 && s.Human.CashWon >= 0);
        }

        [Fact]
        public void Evaluate_Is_Bounded_Neutral_On_Flat_Tape_And_Rate_Up_Is_Bearish()
        {
            Assert.Equal(0, UsTechArenaEngine.Evaluate(Nvda, 0, 0, 0, 0, 1000, 1000, 1024, 1024).Score);
            Assert.Equal(0, UsTechArenaEngine.Evaluate(99, 9999, 9999, 0, 0, 1, 0, 1, 1).Score);   // 표 밖 = 중립
            var max = UsTechArenaEngine.Evaluate(Nvda, int.MaxValue, int.MaxValue, int.MinValue, int.MaxValue, long.MaxValue / 4, 0, long.MaxValue / 4, 1);
            Assert.InRange(max.Score, -10000, 10000);
            Assert.Equal(10000, max.EtfBetaBp);
            var rateUp = UsTechArenaEngine.Evaluate(Aapl, 0, 0, 10, 0, 1000, 1000, 1024, 1024);
            Assert.True(rateUp.RateBp < 0 && rateUp.Score < 0);
            Assert.Equal(ArenaTrigger.RateSensitivity, rateUp.Dominant);
            var tsla = UsTechArenaEngine.Evaluate(UsTechUniverse.Tsla, 0, 0, 10, 0, 1000, 1000, 1024, 1024);
            Assert.True(tsla.RateBp < rateUp.RateBp);                  // 금리 감응 TSLA 150 > AAPL 80
        }

        // ── §3 듀얼 원장 ───────────────────────────────────────────────────────

        [Fact]
        public void Human_And_Ai_Books_Are_Independent_And_Invalid_Inputs_FailClosed()
        {
            var e = new UsTechArenaEngine();
            var t = Reg23;
            Warm(e, ref t, Aapl, 22_500, 22_501);
            var before = Snap(e);
            Assert.Equal(UsArenaReject.None, e.SubmitHuman(Sym(Aapl), 1, 40));
            e.Drain(t);
            var s = Snap(e);
            Assert.Equal(1, s.Human.Fills);
            long notional = 40 * 22_501;
            Assert.Equal(UsTechArenaEngine.DefaultCapitalCents - notional - notional * UsTechArenaEngine.FeePer100k / 100_000, s.Human.CashWon);
            Assert.Equal(before.Ai.CashWon, s.Ai.CashWon);
            Assert.Equal(0, s.Ai.OpenPositions);

            Assert.Equal(UsArenaReject.None, e.SubmitHuman(Sym(Aapl), -1, 41));      // 보유 초과
            Assert.Equal(UsArenaReject.None, e.SubmitHuman(Sym(Nvda), 1, 1));        // 호가 미수신
            e.Drain(t);
            Assert.Equal(s.Human.Rejects + 2, Snap(e).Human.Rejects);

            Assert.Equal(UsArenaReject.Invalid, e.SubmitHuman(UsTechUniverse.Pack("GOOG"), 1, 1));
            Assert.Equal(UsArenaReject.Invalid, e.SubmitHuman(Sym(Aapl), 0, 1));
            Assert.Equal(UsArenaReject.Invalid, e.SubmitHuman(Sym(Aapl), 1, UsTechArenaEngine.MaxOrderQty + 1));
            long dropped = Snap(e).DroppedQuotes;
            e.OnQuote(UsTechUniverse.Pack("GOOG"), 17_000, 17_001, 10, 10, t);       // 표 밖
            e.OnQuote(Sym(Aapl), 99, 100, 10, 10, t);                                // $1 미만
            e.OnQuote(Sym(Aapl), 22_502, 22_501, 10, 10, t);                         // 역전
            e.OnQuote(Sym(Aapl), 22_500, 22_501, -1, 10, t);                         // 음수 잔량
            Assert.Equal(dropped + 4, Snap(e).DroppedQuotes);
        }

        [Fact]
        public void Human_Queue_Full_Drops_Without_Retry()
        {
            var e = new UsTechArenaEngine();
            for (int i = 0; i < UsTechArenaEngine.HumanQueueCapacity; i++) Assert.Equal(UsArenaReject.None, e.SubmitHuman(Sym(Aapl), 1, 1));
            Assert.Equal(UsArenaReject.QueueFull, e.SubmitHuman(Sym(Aapl), 1, 1));
            e.Drain(Pre21);
            Assert.Equal(UsTechArenaEngine.HumanQueueCapacity, Snap(e).Human.Rejects);   // 프리마켓 = 전량 SessionClosed
            Assert.Equal(UsArenaReject.None, e.SubmitHuman(Sym(Aapl), 1, 1));
        }

        [Fact]
        public void Ticker_Pack_Roundtrip_And_Universe_Exchange_Mapping()
        {
            for (int i = 0; i < UsTechUniverse.Count; i++)
            {
                Assert.Equal(UsTechUniverse.Ticker(i), UsTechUniverse.Unpack(UsTechUniverse.Symbol(i)));
                Assert.Equal(i, UsTechUniverse.IndexOf(UsTechUniverse.Pack(UsTechUniverse.Ticker(i))));
                Assert.Equal("NASD", StockManager.Engines.Brokers.Kis.UsExchangeCode.TradeCode(UsTechUniverse.Exchange(i)));
                Assert.Equal(UsTechUniverse.Ticker(i), Encoding.ASCII.GetString(UsTechUniverse.TickerUtf8(i)));
            }
            Assert.Equal(new[] { "AAPL", "NVDA", "MSFT", "AMZN", "TSLA" }, Enumerable.Range(0, 5).Select(UsTechUniverse.Ticker));
            Assert.Equal(0, UsTechUniverse.Pack("aapl"));
            Assert.Equal(0, UsTechUniverse.Pack("TOOLONGXX"));
            Assert.Equal(0, UsTechUniverse.Pack("A A"));
            Assert.Equal("BRK.B", UsTechUniverse.Unpack(UsTechUniverse.Pack("BRK.B")));
        }

        // ── §4 피드 ────────────────────────────────────────────────────────────

        private static byte[] KisBody(string rsym, string last)
            => Encoding.UTF8.GetBytes("{\"output\":{\"rsym\":\"" + rsym + "\",\"zdiv\":\"4\",\"base\":\"129.0000\",\"pvol\":\"41000000\",\"last\":\"" + last + "\",\"sign\":\"2\","
             + "\"diff\":\"1.4800\",\"rate\":\"+0.65\",\"tvol\":\"38123456\",\"tamt\":\"8730000000\",\"ordy\":\"매수불가\"},"
             + "\"rt_cd\":\"0\",\"msg_cd\":\"MCA00000\",\"msg1\":\"정상처리 되었습니다.\"}");

        [Fact]
        public void Kis_HHDFS00000300_Body_Feeds_Penny_Aligned_Last_Trade_And_Mismatch_Is_Dropped()
        {
            var e = new UsTechArenaEngine();
            var f = new UsArenaQuoteFeeder(e);
            Assert.True(f.OnKisQuoteBody(Nvda, KisBody("DNASNVDA", "130.4567")));
            f.Pump(UtcReg23);
            var s = Snap(e);
            Assert.Equal(1, s.Ticks);
            Assert.Equal(UsSessionPhase.ContinuousTrading, s.Phase);
            var q = new DuelQuote[UsTechUniverse.Count];
            Assert.Equal(1, e.CopyQuotes(q));
            Assert.Equal(Sym(Nvda), q[0].Symbol);
            Assert.Equal(13_046, q[0].BidWon);                         // $130.4567 → $130.46(AlignNearest)
            Assert.Equal(13_046, q[0].AskWon);
            Assert.Equal(UsArenaQuoteFeeder.KisLastTradeDepth, q[0].AskQty);
            Assert.Equal(1, f.KisUpdates);

            Assert.False(f.OnKisQuoteBody(Nvda, KisBody("DNYSNVDA", "131.0000")));   // 시세 거래소 코드 불일치
            Assert.False(f.OnKisQuoteBody(Nvda, KisBody("DNASAAPL", "131.0000")));   // 심볼 불일치
            Assert.False(f.OnKisQuoteBody(Nvda, KisBody("DNASNVDA", "")));           // 체결가 부재
            Assert.False(f.OnKisQuoteBody(Nvda, KisBody("DNASNVDA", "0.5000")));     // $1 미만
            Assert.False(f.OnKisQuoteBody(9, KisBody("DNASNVDA", "131.0000")));      // 표 밖 인덱스
            Assert.False(f.OnKisQuoteBody(Nvda, "{\"output\":"u8));                   // 구조 이탈
            Assert.Equal(6, f.DroppedQuotes);
            f.Pump(UtcReg23 + TimeSpan.TicksPerSecond);                 // 갱신 0 = Drain
            Assert.Equal(1, Snap(e).Ticks);
        }

        [Fact]
        public void Synthetic_Feed_Runs_PreMarket_Then_Regular_Over_Days_With_One_To_One_Rationales()
        {
            var e = new UsTechArenaEngine();
            var f = new UsArenaQuoteFeeder(e);
            Assert.False(f.SelectSource(0));
            Assert.True(f.SelectSource(UsArenaFeed.Synthetic));
            long utc = new DateTime(2026, 10, 22, 12, 0, 0).Ticks;
            bool sawPre = false, sawReg = false;
            long prevFills = 0, offSessionFills = 0;
            for (int i = 0; i < 100_000; i++)
            {
                f.Pump(utc + i * TimeSpan.TicksPerMillisecond * 50);
                var s = Snap(e);
                if (s.Phase == UsSessionPhase.PreMarket) sawPre = true;
                if (s.Phase == UsSessionPhase.ContinuousTrading) sawReg = true;
                else if (s.Ai.Fills != prevFills) offSessionFills++;   // 프리마켓 등 정규장 외 정산에서 AI 체결 증가 = 위반
                prevFills = s.Ai.Fills;
                Assert.Equal(s.Ai.Fills, s.RationaleCount);
            }
            var fin = Snap(e);
            var virt = new DateTime(f.SyntheticUtcTicks);
            _out.WriteLine($"virtUtc={virt:yyyy-MM-dd HH:mm} aiFills={fin.Ai.Fills} wins={fin.Ai.Wins} losses={fin.Ai.Losses} ret={fin.Ai.ReturnBp}bp ctx=({fin.QqqBp},{fin.SoxxBp},{fin.Us10yChangeBp})");
            Assert.True(sawPre && sawReg);
            Assert.Equal(0, offSessionFills);
            Assert.True(fin.Ai.Fills > 0);
            Assert.Equal(fin.Ai.Fills, fin.RationaleCount);
            Assert.All(Rationales(e), r =>
            {
                Assert.True(r.IsWellFormed);
                Assert.Equal(UsSessionPhase.ContinuousTrading, UsTechArenaEngine.PhaseAt(r.AtKst));
            });
            Assert.True(virt > new DateTime(2026, 10, 26), "가상 세션이 여러 매매일(주말 건너뜀)을 지나야 한다");
            Assert.Equal(new DateTime(2026, 10, 23, 13, 0, 0), UsArenaQuoteFeeder.NextSyntheticDayStartUtc(new DateTime(2026, 10, 23)));   // 09:00 EDT
            Assert.Equal(new DateTime(2026, 11, 2, 14, 0, 0), UsArenaQuoteFeeder.NextSyntheticDayStartUtc(new DateTime(2026, 10, 31)));   // 토 → 월 09:00 EST
        }

        [Fact]
        public void Source_Switch_Resets_Books_And_Dashboard_Hub_Drives_Feeder()
        {
            long now = UtcReg23;
            var e = new UsTechArenaEngine();
            var f = new UsArenaQuoteFeeder(e);
            using var hub = new DashboardSnapshotHub(nowTicks: () => now);
            Assert.Throws<ArgumentNullException>(() => hub.AttachUsArena(null!));
            hub.AttachUsArena(f);
            Assert.True(f.OnBook(Aapl, 22_500, 22_501, 500, 500));
            hub.Pump();
            Assert.Equal(1, f.Pumps);
            Assert.Equal(1, Snap(e).Ticks);
            Assert.True(Snap(e).Tradable);
            e.SubmitHuman(Sym(Aapl), 1, 5);
            f.Pump(now);
            Assert.Equal(1, Snap(e).Human.Fills);

            f.SelectSource(UsArenaFeed.Synthetic);
            f.Pump(now);
            var s = Snap(e);
            Assert.Equal(UsArenaFeed.Synthetic, f.AppliedSource);
            Assert.Equal(0, s.Human.Fills);
            Assert.Equal(UsTechArenaEngine.DefaultCapitalCents, s.Human.CashWon);
            Assert.Equal(UsTechUniverse.Count, s.Ticks);

            f.SelectSource(UsArenaFeed.KisOverseas);
            f.Pump(now);
            Assert.Equal(0, Snap(e).Ticks);                            // 묵은 슬롯 재주입 0
            Assert.False(f.OnBook(Aapl, 22_501, 22_500, 1, 1));
            Assert.False(f.OnBook(7, 22_500, 22_501, 1, 1));
        }

        // ── §5 동시성 ─────────────────────────────────────────────────────────

        private static long BidOf(int t, int i) => 10_000 + (t * 1_000_003L + i) % 5_000;
        private static long AskOf(long bid) => bid + 1 + bid % 3;
        private static long BidQtyOf(long bid) => bid * 3 % 997 + 1;
        private static long AskQtyOf(long ask) => ask * 7 % 991 + 1;

        [Fact]
        public void Four_Producer_Threads_Inject_Quotes_Without_Tearing_Or_Ledger_Corruption()
        {
            var e = new UsTechArenaEngine();
            var f = new UsArenaQuoteFeeder(e);
            e.SetMarketContext(200, 200, -5);
            const int Producers = 4, PerProducer = 50_000;
            long torn = 0, reads = 0, rejectedWrites = 0, pumps = 0;
            int done = 0;

            var producers = Enumerable.Range(0, Producers).Select(p => new Thread(() =>
            {
                for (int i = 0; i < PerProducer; i++)
                {
                    long bid = BidOf(p, i), ask = AskOf(bid);
                    if (!f.OnBook((p + i) % UsTechUniverse.Count, bid, ask, BidQtyOf(bid), AskQtyOf(ask))) Interlocked.Increment(ref rejectedWrites);
                    if ((i & 15) == 0) e.SubmitHuman(Sym(i % UsTechUniverse.Count), (i & 16) == 0 ? 1 : -1, 1);
                }
            })).ToArray();
            var pump = new Thread(() =>
            {
                long k = 0;
                while (Volatile.Read(ref done) == 0) { f.Pump(UtcReg23 + (k++ % 600_000) * TimeSpan.TicksPerMillisecond * 10); pumps++; }
                f.Pump(UtcReg23 + TimeSpan.TicksPerHour);
            });
            var reader = new Thread(() =>
            {
                var q = new DuelQuote[UsTechUniverse.Count];
                var rb = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
                while (Volatile.Read(ref done) == 0)
                {
                    int n = e.CopyQuotes(q);
                    for (int i = 0; i < n; i++)
                        if (q[i].AskWon != AskOf(q[i].BidWon) || q[i].BidQty != BidQtyOf(q[i].BidWon) || q[i].AskQty != AskQtyOf(q[i].AskWon)) Interlocked.Increment(ref torn);
                    if (e.TryReadSnapshot(out var s))
                    {
                        reads++;
                        if (s.Ai.Fills != s.RationaleCount || s.Ai.CashWon < 0 || s.Human.CashWon < 0) Interlocked.Increment(ref torn);
                    }
                    int m = e.CopyRationales(rb);
                    for (int i = 0; i < m; i++) if (!rb[i].IsWellFormed) Interlocked.Increment(ref torn);
                }
            });

            pump.Start(); reader.Start();
            foreach (var t in producers) t.Start();
            foreach (var t in producers) t.Join();
            Volatile.Write(ref done, 1);
            pump.Join(); reader.Join();

            var fin = Snap(e);
            _out.WriteLine($"pumps={pumps} engineTicks={fin.Ticks} reads={reads} aiFills={fin.Ai.Fills} humanFills={fin.Human.Fills} humanRejects={fin.Human.Rejects}");
            Assert.Equal(0, rejectedWrites);
            Assert.Equal(0, f.DroppedQuotes);
            Assert.Equal(0, fin.DroppedQuotes);
            Assert.Equal(0, torn);
            Assert.Equal(fin.Ai.Fills, fin.RationaleCount);
            Assert.True(fin.Ticks > 0 && reads > 0);
            var q2 = new DuelQuote[UsTechUniverse.Count];
            Assert.Equal(UsTechUniverse.Count, e.CopyQuotes(q2));
            Assert.All(q2, x => Assert.Equal(AskOf(x.BidWon), x.AskWon));
        }

        // ── §6 δ=0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_100k_Signal_And_Virtual_Fills_Allocate_Zero_Bytes()
        {
            var e = new UsTechArenaEngine();
            var rationales = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            var positions = new DuelPosition[UsTechArenaEngine.MaxPositions];
            var quotes = new DuelQuote[UsTechUniverse.Count];
            long baseTicks = new DateTime(2026, 10, 22, 21, 30, 0).Ticks;   // 프리마켓 1시간 → 정규장 2시간 순환
            ulong x = 0x9E3779B97F4A7C15;
            var px = new long[UsTechUniverse.Count];
            for (int k = 0; k < px.Length; k++) px[k] = UsTechUniverse.SyntheticStartCents(k);
            long i0 = 0;

            void Run(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    int k = (int)(x % UsTechUniverse.Count);
                    long start = UsTechUniverse.SyntheticStartCents(k);
                    px[k] = Math.Clamp(px[k] + (long)(x >> 8 & 7) - 3, start * 8 / 10, start * 12 / 10);
                    if ((i0 & 511) == 0) e.SetMarketContext((int)(x >> 20 & 0x3FF) - 512, (int)(x >> 30 & 0x3FF) - 512, (int)(x >> 40 & 31) - 16);
                    long bq = 100 + (long)(x >> 24 & 4095), aq = 100 + (long)(x >> 36 & 4095);
                    var kst = new DateTime(baseTicks + (i0 % 108_000) * TimeSpan.TicksPerMillisecond * 100);
                    e.OnQuote(UsTechUniverse.Symbol(k), px[k], px[k] + 1, bq, aq, kst);
                    if ((i0 & 7) == 0) e.SubmitHuman(UsTechUniverse.Symbol(k), (i0 & 8) == 0 ? 1 : -1, 1 + (long)(x >> 44 & 7));
                    if ((i0 & 63) == 0)
                    {
                        e.TryReadSnapshot(out _);
                        e.CopyRationales(rationales);
                        e.CopyPositions(ArenaSide.Ai, positions);
                        e.CopyQuotes(quotes);
                    }
                    if ((i0 & 1023) == 0) e.Drain(kst);
                }
            }

            Run(20_000);   // JIT · 티어링 워밍업
            long before = Snap(e).Ai.Fills;
            long bytes = MinAllocated(() => Run(100_000));
            var s = Snap(e);
            _out.WriteLine($"δ={bytes}B aiFills={s.Ai.Fills} humanFills={s.Human.Fills} humanRejects={s.Human.Rejects} rationales={s.RationaleCount}");
            Assert.Equal(0, bytes);
            Assert.True(s.Ai.Fills > before, "측정 루프에서 AI 가상 체결이 발생해야 한다(빈 루프 측정 금지)");
            Assert.True(s.Human.Fills > 0 && s.Human.Rejects > 0);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
        }

        [Fact]
        public void Kis_And_Synthetic_Feed_100k_Allocate_Zero_Bytes()
        {
            var e = new UsTechArenaEngine();
            var f = new UsArenaQuoteFeeder(e);
            var bodies = new byte[UsTechUniverse.Count][];
            for (int k = 0; k < bodies.Length; k++) bodies[k] = KisBody("DNAS" + UsTechUniverse.Ticker(k), "150.1234");
            long utc = UtcReg23, i0 = 0;

            void RunKis(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    int k = (int)(i0 % UsTechUniverse.Count);
                    if ((i0 & 1) == 0) f.OnKisQuoteBody(k, bodies[k]);
                    else f.OnBook(k, 15_000 + (i0 & 31), 15_001 + (i0 & 31), 300 + (i0 & 255), 300);
                    f.Pump(utc + (i0 % 100_000) * TimeSpan.TicksPerMillisecond);
                }
            }

            RunKis(20_000);
            long kisBytes = MinAllocated(() => RunKis(100_000));
            long ticksAfterKis = Snap(e).Ticks;

            f.SelectSource(UsArenaFeed.Synthetic);
            f.Pump(utc);
            long j = 0;
            void RunSyn(int n) { for (int i = 0; i < n; i++, j++) f.Pump(utc + j * TimeSpan.TicksPerMillisecond * 50); }
            RunSyn(20_000);
            long synBytes = MinAllocated(() => RunSyn(100_000));
            var s = Snap(e);
            _out.WriteLine($"kisδ={kisBytes}B synδ={synBytes}B kisUpdates={f.KisUpdates} ticksAfterKis={ticksAfterKis} synTicks={s.Ticks} aiFills={s.Ai.Fills}");
            Assert.Equal(0, kisBytes);
            Assert.Equal(0, synBytes);
            Assert.True(f.KisUpdates >= 60_000 && ticksAfterKis > 0);
            Assert.True(s.Ticks > 0);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
        }

        [Fact]
        public void HotPath_Methods_Have_No_Il_Allocation_Sites()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var targets = new (Type t, string[] names)[]
            {
                (typeof(UsTechArenaEngine), new[] { "OnQuote", "Drain", "Clock", "RollDay", "Evaluate", "StepAi", "DrainHuman", "Buy", "Sell", "Record",
                    "Equity", "Stats", "PublishSnapshot", "SubmitHuman", "TryDequeue", "TryReadSnapshot", "CopyPositions", "CopyQuotes", "CopyRationales",
                    "FindPos", "AlignUpCents", "AlignDownCents", "PhaseAt", "SetMarketContext" }),
                (typeof(UsArenaQuoteFeeder), new[] { "OnKisQuoteBody", "OnKisQuote", "OnBook", "Write", "Lock", "Pump", "TryReadSlot", "MarkAllSeen",
                    "StepSynthetic", "NewVirtualDay", "EasternToUtc", "NextSyntheticDayStartUtc" }),
                (typeof(UsTechUniverse), new[] { "IndexOf", "Symbol", "TickerUtf8", "Exchange", "QqqBetaX100", "SemiBetaX100", "RateSensX100" }),
            };
            foreach (var (t, names) in targets)
                foreach (var name in names)
                {
                    var m = t.GetMethod(name, all);
                    Assert.True(m is not null, $"{t.Name}.{name}");
                    var sites = ZeroAllocIlInspector.Inspect(m!);
                    Assert.True(sites.Count == 0, $"{t.Name}.{name}\n" + string.Join("\n", sites));
                }
        }

        // ── §7 Pure Shell · Fail-Closed ────────────────────────────────────────

        [Fact]
        public void Port_Surface_Has_No_Broker_Arm_Or_Live_Path_And_Engine_Has_No_Network_Fields()
        {
            string[] bad = { "Arm", "Broker", "Live", "Send", "Route", "Valve" };
            foreach (var type in new[] { typeof(IUsArenaPort), typeof(UsTechArenaEngine), typeof(UsArenaQuoteFeeder) })
            {
                var names = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(m => m.Name).ToArray();
                foreach (var b in bad) Assert.DoesNotContain(names, n => n.Contains(b, StringComparison.Ordinal));   // 대소문자 구분(WarmupTicks 의 'arm' 오탐 회피)
            }
            foreach (var type in new[] { typeof(UsTechArenaEngine), typeof(UsArenaQuoteFeeder), typeof(UsArenaPortAdapter) })
                foreach (var fi in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    string ns = fi.FieldType.Namespace ?? "";
                    Assert.False(ns.StartsWith("System.Net", StringComparison.Ordinal), $"{type.Name}.{fi.Name}");
                    Assert.DoesNotContain("Broker", fi.FieldType.Name, StringComparison.Ordinal);
                }

            var portable = typeof(IUsArenaPort).Assembly;
            Assert.DoesNotContain(portable.GetReferencedAssemblies(), a => a.Name == typeof(UsTechArenaEngine).Assembly.GetName().Name);
            foreach (var dto in new[] { typeof(UsArenaView), typeof(UsArenaBookView), typeof(UsRationaleView), typeof(UsArenaQuoteView), typeof(UsArenaPositionView), typeof(UsArenaFeedStatus) })
                Assert.Equal(portable, dto.Assembly);
            for (int p = 0; p <= 5; p++) Assert.Equal(((UsSessionPhase)p).ToString(), new[] { "HolidayOrWeekend", "Closed", "PreMarket", "ContinuousTrading", "AfterHours", "CalendarUncovered" }[p]);
            Assert.Equal(UsArenaPhase.ContinuousTrading, (int)UsSessionPhase.ContinuousTrading);
            Assert.Equal(UsArenaPhase.CalendarUncovered, (int)UsSessionPhase.CalendarUncovered);
        }

        [Fact]
        public void Adapter_Projects_Usd_Snapshot_Quotes_Positions_And_Korean_Rationales_Newest_First()
        {
            var e = new UsTechArenaEngine();
            IUsArenaPort port = new UsArenaPortAdapter(e);
            Assert.False(port.Fed);
            Assert.False(port.SelectSource(UsArenaFeed.Synthetic));    // 주입기 미결속
            Assert.False(port.Feed.Pumped);
            Assert.True(port.TryRead(out var v0));
            Assert.Equal(0, v0.Ticks);
            Assert.Equal(ArenaLeader.Draw, v0.Leader);
            Assert.Empty(port.RecentRationales(10));
            Assert.Empty(port.Quotes());
            Assert.Equal(new[] { "AAPL", "NVDA", "MSFT", "AMZN", "TSLA" }, port.Tickers);
            Assert.Equal("미국 빅테크(US Tech)", port.ModeName);

            var t = Reg23;
            Warm(e, ref t); Spike(e, ref t);
            Assert.True(port.Fed);
            Assert.True(port.TryRead(out var v));
            Assert.True(v.Tradable);
            Assert.Equal(UsArenaPhase.ContinuousTrading, v.Phase);
            Assert.Equal(300, v.QqqBp);
            Assert.Equal(-10, v.Us10yChangeBp);
            Assert.Equal(UsTechArenaEngine.DefaultCapitalCents, v.Ai.CapitalCents);
            var pos = Assert.Single(port.Positions(ArenaSide.Ai));
            Assert.Equal("NVDA", pos.Ticker);
            Assert.True(pos.TargetPriceCents > pos.MarkCents && pos.StopLossPriceCents < pos.MarkCents);
            var quote = Assert.Single(port.Quotes());
            Assert.Equal(("NVDA", "NASD", 13_050L, 13_051L), (quote.Ticker, quote.Exchange, quote.BidCents, quote.AskCents));

            var entry = Rationales(e)[0];
            Q(e, Nvda, entry.TargetPrice, entry.TargetPrice + 1, ref t);
            var feed = port.RecentRationales(10);
            Assert.Equal(2, feed.Count);
            Assert.Equal(-1, feed[0].Side);
            Assert.Equal("목표가 도달(익절)", feed[0].Trigger);
            Assert.Equal("QQQ/반도체 ETF 베타", feed[1].Trigger);
            Assert.Equal("NVDA", feed[1].Ticker);
            Assert.Equal(entry.SurgeBp, feed[1].EtfBetaBp);
            Assert.Equal(entry.MacroBp, feed[1].RateBp);
            Assert.Equal(entry.ImbalanceBp, feed[1].GapFlowBp);
            Assert.All(feed, r => Assert.Contains("$", r.Summary));
            Assert.Single(port.RecentRationales(1));
            Assert.True(port.TryRead(out var v2));
            Assert.Equal(ArenaLeader.Ai, v2.Leader);

            Assert.Equal(UsArenaReject.None, port.SubmitHuman("AAPL", 1, 1));
            Assert.Equal(UsArenaReject.Invalid, port.SubmitHuman("GOOG", 1, 1));
            Assert.Equal(UsArenaReject.Invalid, port.SubmitHuman("aapl", 1, 1));
            Assert.Equal(UsArenaReject.Invalid, port.SubmitHuman("", 1, 1));
            Assert.Equal(UsArenaReject.Invalid, port.SubmitHuman(null!, 1, 1));
            Assert.Throws<ArgumentException>(() => new UsArenaPortAdapter(e, new UsArenaQuoteFeeder(new UsTechArenaEngine())));
        }

        [Fact]
        public void Krx_Rationale_Summary_Unchanged_By_Usd_Extension()
        {
            var krw = new AiTradeRationale(1, 5930, 1, 10, 70_100, Reg23.Ticks, ArenaTrigger.OrderbookImbalance, 5000, 50, 71_000, 69_500, 150, 0, 8000, 0, 0, 0);
            Assert.Equal(ArenaCurrency.Krw, krw.Currency);
            Assert.Contains("005930 10주 @ 70,100원", krw.HumanReadableSummary);
            Assert.True(ArenaTriggerText.IsEntry(ArenaTrigger.EtfBeta) && ArenaTriggerText.IsEntry(ArenaTrigger.PremarketGapFlow));
            Assert.False(ArenaTriggerText.IsExit(ArenaTrigger.RateSensitivity));
        }

        [Fact]
        public void Host_Di_Registers_Us_Arena_Port_Via_Adapter_And_Hub_Pump()
        {
            string form = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IUsArenaPort>(new StockManager.Adapters.UsArenaPortAdapter(usArena, usArenaFeeder));", form);
            Assert.Contains("dashHub.AttachUsArena(usArenaFeeder);", form);
            string hub = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "Services", "DashboardSnapshotHub.cs"));
            Assert.Contains("Volatile.Read(ref _usArena)?.Pump(nowT);", hub);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }
    }
}
