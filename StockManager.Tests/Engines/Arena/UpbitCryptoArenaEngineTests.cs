// ============================================================================
// UpbitCryptoArenaEngineTests — v12.18.66-DEV: 업비트 가상자산 24/7 아레나(휴먼 vs AI · 가상 1억 원 듀얼 원장 · 수수료 0.05%) · 설명 가능한 AI 근거(Crypto)
//
//   §1 24/7 세션     주말 · 자정 · 새벽 모두 체결 가능 · 시각 ≤0/범위 밖 = Invalid(폐기 · 휴먼 거부) · KST 변환
//   §2 근거 무결성   오더북 불균형 주도 진입 근거(원화 목표/손절 · 호가 격자 정렬 · 오더북 두께) · 익절/손절 · 무작위 장세 1:1 · 한국어 원화 요약
//   §3 매칭 결속     휴먼 시장가 체결가/대금/수수료 = CryptoPaperExchange 동일 산식(슬리피지 2bp · 0.05% 올림) · 최소 주문 · 호가 지연 거부 · 듀얼 원장 독립
//   §4 피드          UpbitWebSocketFeedAdapter tap → 주입기 → 엔진(호가 · 테이커 체결) · 출처 전환 초기화 · 합성 24/7 구동 1:1 · 허브 펌프
//   §5 동시성        4 생산자 스레드 동시 호가 주입 + 휴먼 주문 + 펌프 + 판독: 호가 찢김 0 · 원장/근거 불일치 0
//   §6 δ=0           시그널 평가 · 가상 체결 10만 회 0B · 업비트 프레임/합성 피드 10만 회 0B · 핫패스 IL 할당 지점 0
//   §7 Pure Shell    ICryptoArenaPort 무장/브로커/송출 멤버 0 · DTO 포터블 · 엔진/주입기 네트워크 필드 0 · 어댑터 투영 · DI 등록
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Brokers.Crypto;
using StockManager.Engines.Feeds;
using StockManager.Engines.Feeds.Crypto;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class UpbitCryptoArenaEngineTests
    {
        private const long S = UpbitTickAligner.Scale;
        private const int Btc = CryptoArenaUniverse.Btc, Eth = CryptoArenaUniverse.Eth;
        // 2026-10-24(토) 03:00 UTC = 12:00 KST — 주식 휴장일에도 24/7 체결
        private static readonly long Sat = UtcMicros(new DateTime(2026, 10, 24, 3, 0, 0));

        private readonly ITestOutputHelper _out;
        public UpbitCryptoArenaEngineTests(ITestOutputHelper o) => _out = o;

        private static long UtcMicros(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        private static long Sym(int i) => CryptoArenaUniverse.Symbol(i);
        private static CryptoArenaSnapshot Snap(UpbitCryptoArenaEngine e) { Assert.True(e.TryReadSnapshot(out var s)); return s; }

        private static void Q(UpbitCryptoArenaEngine e, int idx, long bidWon, long askWon, ref long t, long bidQty = 1 * S, long askQty = 1 * S)
        {
            t += 100_000;
            e.OnQuote(Sym(idx), bidWon * S, askWon * S, bidQty, askQty, t);
        }

        private static void Warm(UpbitCryptoArenaEngine e, ref long t)
        {
            for (int i = 0; i < UpbitCryptoArenaEngine.WarmupTicks + 1; i++) Q(e, Btc, 150_000_000, 150_001_000, ref t);
        }

        // 글로벌 +300bp · 매수 테이커 우세 · 매수1 잔량 9 BTC vs 매도1 1 BTC · 가격 상승 → 점수 ≥ 3000(진입 · 주도 = 오더북 불균형)
        private static void Spike(UpbitCryptoArenaEngine e, ref long t)
        {
            e.SetMarketContext(0, 300);
            for (int i = 0; i < 4; i++) { t += 1_000; e.OnTrade(Sym(Btc), 150_001_000 * S, S / 2, 1, t); }
            Q(e, Btc, 150_100_000, 150_101_000, ref t, 9 * S, 1 * S);
        }

        private static AiTradeRationale[] Rationales(UpbitCryptoArenaEngine e)
        {
            var buf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            int n = e.CopyRationales(buf);
            return buf[..n];
        }

        // ── §1 24/7 세션 ─────────────────────────────────────────────────────────

        [Theory]
        [InlineData(2026, 10, 24, 3, 0)]     // 토요일
        [InlineData(2026, 10, 25, 15, 0)]    // 일요일 자정 KST
        [InlineData(2026, 12, 25, 0, 0)]     // 성탄절
        [InlineData(2027, 1, 1, 0, 0)]       // 신정
        public void Session_Is_24x7_On_Weekends_And_Holidays(int y, int mo, int d, int h, int mi)
        {
            long t = UtcMicros(new DateTime(y, mo, d, h, mi, 0));
            Assert.Equal(CryptoSessionPhase.ContinuousTrading, UpbitCryptoArenaEngine.PhaseAt(t));
            var e = new UpbitCryptoArenaEngine();
            Warm(e, ref t);
            Spike(e, ref t);
            var s = Snap(e);
            Assert.True(s.Tradable);
            Assert.Equal(1, s.Ai.Fills);
            Assert.Equal(new DateTime(t * 10 + DateTime.UnixEpoch.Ticks + TimeSpan.TicksPerHour * 9), new DateTime(s.LastKstTicks));
        }

        [Fact]
        public void Invalid_Time_Is_FailClosed_Quotes_Dropped_And_Human_Rejected()
        {
            Assert.Equal(CryptoSessionPhase.Invalid, UpbitCryptoArenaEngine.PhaseAt(0));
            Assert.Equal(CryptoSessionPhase.Invalid, UpbitCryptoArenaEngine.PhaseAt(-1));
            Assert.Equal(CryptoSessionPhase.Invalid, UpbitCryptoArenaEngine.PhaseAt(long.MaxValue));
            Assert.Equal(0, UpbitCryptoArenaEngine.KstTicksOf(0));
            var e = new UpbitCryptoArenaEngine();
            e.OnQuote(Sym(Btc), 150_000_000 * S, 150_001_000 * S, S, S, 0);
            e.OnTrade(Sym(Btc), 150_000_000 * S, S, 1, -5);
            var s = Snap(e);
            Assert.Equal(0, s.Ticks);
            Assert.Equal(2, s.DroppedQuotes);
            Assert.False(s.Tradable);

            long t = Sat;
            Q(e, Btc, 150_000_000, 150_001_000, ref t);
            Assert.Equal(CryptoArenaReject.None, e.SubmitHuman(Sym(Btc), 1, S / 100));
            e.Drain(0);                                                  // 시계 무효 = 세션 무효
            s = Snap(e);
            Assert.Equal(1, s.Human.Rejects);
            Assert.Equal(0, s.Human.Fills);
        }

        // ── §2 근거 무결성 ───────────────────────────────────────────────────────

        [Fact]
        public void Entry_And_TakeProfit_Rationales_Are_Complete_Krw_Tick_Aligned_With_Depth()
        {
            var e = new UpbitCryptoArenaEngine();
            long t = Sat;
            Warm(e, ref t);
            Assert.Equal(0, Snap(e).Ai.Fills);
            Spike(e, ref t);

            var entry = Assert.Single(Rationales(e));
            _out.WriteLine(entry.HumanReadableSummary);
            Assert.True(entry.IsWellFormed);
            Assert.Equal(ArenaCurrency.Crypto, entry.Currency);
            Assert.Equal(ArenaTrigger.OrderbookImbalance, entry.TriggerType);
            Assert.Equal(1, entry.Side);
            Assert.Equal(150_132_000 * S, entry.PriceWon);                // AlignUp(150,101,000 × 1.0002) = 1,000원 격자
            Assert.True(UpbitTickAligner.IsAligned(entry.TargetPrice) && UpbitTickAligner.IsAligned(entry.StopLossPrice));
            Assert.Equal(UpbitCryptoArenaEngine.AlignDownRaw(150_101_000 * S, -UpbitCryptoArenaEngine.StopBp), entry.StopLossPrice);
            Assert.Equal(UpbitCryptoArenaEngine.AlignUpRaw(150_101_000 * S, UpbitCryptoArenaEngine.TargetBaseBp + UpbitCryptoArenaEngine.TargetPerConfidenceBp * entry.ConfidenceScore), entry.TargetPrice);
            Assert.True(entry.ConfidenceScore >= 30 && entry.SentimentScore >= UpbitCryptoArenaEngine.EntryScore);
            Assert.Equal(8000, entry.ImbalanceBp);
            Assert.Equal(10000, entry.SurgeBp);                            // 테이커 매수 100%
            Assert.Equal(3000, entry.MacroBp);                             // 글로벌 +300bp × 베타 1.00 × 10
            Assert.Equal(1_350_900_000, entry.BidDepthWon);                // 9 BTC × 150,100,000원
            Assert.Equal(150_101_000, entry.AskDepthWon);
            long qty = entry.Qty;
            Assert.True(qty > 0 && qty < S);                               // 30% 비중 ≈ 0.2 BTC
            Assert.Contains("KRW-BTC", entry.HumanReadableSummary);
            Assert.Contains("BTC @ 150,132,000원", entry.HumanReadableSummary);
            Assert.Contains("주도 근거: 호가 수급 불균형", entry.HumanReadableSummary);
            Assert.Contains("오더북 두께: 매수1 잔량 1,350,900,000원 vs 매도1 잔량 150,101,000원(매수 비중 89%)", entry.HumanReadableSummary);
            Assert.Contains("김치프리미엄/변동성 감응 +3,000bp", entry.HumanReadableSummary);

            long tp = entry.TargetPrice / S;
            Q(e, Btc, tp, UpbitTickAligner.AlignUp(entry.TargetPrice + 1) / S, ref t);
            var rs = Rationales(e);
            Assert.Equal(2, rs.Length);
            var exit = rs[1];
            Assert.True(exit.IsWellFormed);
            Assert.Equal(ArenaTrigger.TakeProfit, exit.TriggerType);
            Assert.Equal(-1, exit.Side);
            Assert.Equal(qty, exit.Qty);
            Assert.Equal(entry.TargetPrice, exit.TargetPrice);
            Assert.True(exit.RealizedPnlWon > 0);
            Assert.Contains("청산 사유: 목표가 도달(익절)", exit.HumanReadableSummary);
            Assert.Contains("실현손익 +", exit.HumanReadableSummary);
            var s = Snap(e);
            Assert.Equal(1, s.Ai.Wins);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
            Assert.Equal(0, s.Ai.OpenPositions);
            Assert.True(s.Ai.CashWon > s.Ai.CapitalWon);
            Assert.True(s.Ai.FeesWon > 0);
        }

        [Fact]
        public void StopLoss_Exit_Counts_Loss_And_Fees_Are_005_Percent()
        {
            var e = new UpbitCryptoArenaEngine();
            long t = Sat;
            Warm(e, ref t); Spike(e, ref t);
            var entry = Rationales(e)[0];
            Q(e, Btc, entry.StopLossPrice / S, UpbitTickAligner.AlignUp(entry.StopLossPrice + 1) / S, ref t);
            var rs = Rationales(e);
            Assert.Equal(ArenaTrigger.StopLoss, rs[1].TriggerType);
            Assert.True(rs[1].RealizedPnlWon < 0);
            var s = Snap(e);
            Assert.Equal(1, s.Ai.Losses);
            // 수수료 = 매수 대금 · 매도 대금 각각 × 5bp 올림
            CryptoPaperExchange.NotionalWithFee(CryptoOrderSide.Buy, entry.PriceWon, entry.Qty, 5, out long bn, out long bf);
            CryptoPaperExchange.NotionalWithFee(CryptoOrderSide.Sell, rs[1].PriceWon, rs[1].Qty, 5, out long sn, out long sf);
            Assert.Equal((bn * 5 + 9999) / 10000, bf);
            Assert.Equal(bf + sf, s.Ai.FeesWon);
            Assert.Equal(s.Ai.CapitalWon - bn - bf + sn - sf, s.Ai.CashWon);
        }

        [Fact]
        public void Every_Ai_Fill_Has_One_WellFormed_Rationale_Under_Random_Tape()
        {
            var e = new UpbitCryptoArenaEngine();
            var rng = new Random(1866);
            var px = new long[CryptoArenaUniverse.Count];
            for (int k = 0; k < px.Length; k++) px[k] = CryptoArenaUniverse.SyntheticStartWon(k) * S;
            long t = Sat;
            for (int i = 0; i < 60_000; i++)
            {
                int k = rng.Next(CryptoArenaUniverse.Count);
                if (i % 400 == 0) e.SetMarketContext(rng.Next(-200, 400), rng.Next(-500, 500));
                long unit = Math.Max(UpbitTickAligner.TickFor(px[k]), px[k] / 3000);
                long start = CryptoArenaUniverse.SyntheticStartWon(k) * S;
                px[k] = UpbitTickAligner.AlignNearest(Math.Clamp(px[k] + rng.Next(-4, 5) * unit, start * 8 / 10, start * 12 / 10));
                long ask = UpbitTickAligner.AlignUp(px[k] + 1);
                t += 50_000;
                if (rng.Next(3) == 0) e.OnTrade(Sym(k), ask, S / 10 * rng.Next(1, 20), rng.Next(2) == 0 ? 1 : -1, t);
                e.OnQuote(Sym(k), px[k], ask, S / 10 * rng.Next(1, 100), S / 10 * rng.Next(1, 100), t);
            }
            var s = Snap(e);
            _out.WriteLine($"aiFills={s.Ai.Fills} wins={s.Ai.Wins} losses={s.Ai.Losses} ret={s.Ai.EquityWon - s.Ai.CapitalWon}");
            Assert.True(s.Ai.Fills >= 4, "무작위 장세에서 AI 체결이 발생해야 한다");
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
            var rs = Rationales(e);
            Assert.All(rs, r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
            Assert.All(rs, r => Assert.Equal(ArenaCurrency.Crypto, r.Currency));
            Assert.All(rs, r => Assert.True(UpbitTickAligner.IsAligned(r.PriceWon)));
            Assert.All(rs, r => Assert.NotEqual(ArenaTrigger.SessionClose, r.TriggerType));   // 24/7 = 마감 청산 없음
            for (int i = 1; i < rs.Length; i++) Assert.Equal(rs[i - 1].Seq + 1, rs[i].Seq);
        }

        [Fact]
        public void Evaluate_Is_Bounded_Neutral_On_Flat_Tape_And_Kimchi_Premium_Is_Bearish()
        {
            var flat = UpbitCryptoArenaEngine.Evaluate(Btc, 0, 0, S, S, 100 * S, 100 * S, 0, 0, 0);
            Assert.Equal(0, flat.Score);
            var kimchi = UpbitCryptoArenaEngine.Evaluate(Btc, 500, 0, S, S, 100 * S, 100 * S, 0, 0, 0);
            Assert.True(kimchi.KimchiVolBp < 0 && kimchi.Score < 0);
            Assert.Equal(ArenaTrigger.KimchiVolatility, kimchi.Dominant);
            var vol = UpbitCryptoArenaEngine.Evaluate(Btc, 0, 0, S, S, 100 * S, 100 * S, 0, 0, 200);
            Assert.Equal(-10000, vol.KimchiVolBp);                         // 초과 변동성 185bp × 100 → 하한
            var flow = UpbitCryptoArenaEngine.Evaluate(Eth, 0, 0, S, S, 100 * S, 100 * S, 10 * S, 0, 0);
            Assert.Equal(10000, flow.FlowBp);
            Assert.Equal(ArenaTrigger.TradeFlowMomentum, flow.Dominant);
            var max = UpbitCryptoArenaEngine.Evaluate(Btc, -10000, 10000, long.MaxValue / 4, 0, long.MaxValue / 4, 1, long.MaxValue / 4, 0, 0);
            Assert.InRange(max.Score, -10000, 10000);
            Assert.Equal(default, UpbitCryptoArenaEngine.Evaluate(9, 0, 0, S, S, S, S, 0, 0, 0));
        }

        // ── §3 매칭 결속 · 듀얼 원장 ─────────────────────────────────────────────

        [Fact]
        public void Human_Market_Fill_Matches_CryptoPaperExchange_Formula_Exactly()
        {
            var e = new UpbitCryptoArenaEngine();
            var x = new CryptoPaperExchange();
            long t = Sat;
            Q(e, Btc, 150_000_000, 150_001_000, ref t, 3 * S, 3 * S);
            Assert.True(x.UpdateQuote(150_000_000 * S, 150_001_000 * S, t));

            Assert.Equal(CryptoArenaReject.None, e.SubmitHuman(Sym(Btc), 1, 12_345_678));
            e.Drain(t + 1);
            var fx = x.Submit(CryptoOrderSide.Buy, CryptoOrderType.Market, 12_345_678, 0, t + 1);
            Assert.True(fx.Filled);
            var s = Snap(e);
            Assert.Equal(1, s.Human.Fills);
            Assert.Equal(fx.KrwAfterRaw, s.Human.CashWon);                 // 1억 − (대금 + 수수료) 동일
            Assert.Equal(fx.FeeRaw, s.Human.FeesWon);
            var pos = new DuelPosition[4];
            Assert.Equal(1, e.CopyPositions(ArenaSide.Human, pos));
            Assert.Equal(fx.PriceRaw, pos[0].AvgPriceWon);
            Assert.Equal(fx.BtcAfterRaw, pos[0].Qty);

            Assert.Equal(CryptoArenaReject.None, e.SubmitHuman(Sym(Btc), -1, 12_345_678));
            e.Drain(t + 2);
            var fs = x.Submit(CryptoOrderSide.Sell, CryptoOrderType.Market, 12_345_678, 0, t + 2);
            s = Snap(e);
            Assert.Equal(fs.KrwAfterRaw, s.Human.CashWon);
            Assert.Equal(fx.FeeRaw + fs.FeeRaw, s.Human.FeesWon);
            Assert.Equal(0, s.Human.OpenPositions);
            Assert.Equal(0, s.Ai.Fills);                                   // 휴먼 체결 = AI 원장 무영향
            Assert.Equal(s.Ai.CapitalWon, s.Ai.CashWon);
        }

        [Fact]
        public void Human_Orders_FailClosed_Stale_MinOrder_NoCoin_Liquidity_And_Invalid()
        {
            var e = new UpbitCryptoArenaEngine();
            long t = Sat;
            Assert.Equal(CryptoArenaReject.None, e.SubmitHuman(Sym(Btc), 1, S / 100));
            e.Drain(t);                                                    // 호가 미수신
            Q(e, Btc, 150_000_000, 150_001_000, ref t, S / 10, S / 10);
            e.SubmitHuman(Sym(Btc), 1, S / 100);
            e.Drain(t + UpbitCryptoArenaEngine.MaxQuoteAgeMicros + 1);    // 호가 지연 2초 초과
            e.SubmitHuman(Sym(Btc), 1, S / 100);
            e.Drain(t - 1);                                                // 호가 시각보다 과거(시계 역행)
            e.SubmitHuman(Sym(Btc), 1, 1_000);                             // 0.00001 BTC ≈ 1,500원 < 5,000원
            e.SubmitHuman(Sym(Btc), -1, S / 100);                          // 미보유 매도
            e.SubmitHuman(Sym(Btc), 1, 5_000 * S);                         // 잔량 0.1 BTC 로 절단 → 1,500만원 체결(예수금 충분)
            e.Drain(t + 1);
            var s = Snap(e);
            Assert.Equal(5, s.Human.Rejects);
            Assert.Equal(1, s.Human.Fills);
            var pos = new DuelPosition[4];
            Assert.Equal(1, e.CopyPositions(ArenaSide.Human, pos));
            Assert.Equal(S / 10, pos[0].Qty);                              // 최우선 잔량 상한(부분 체결 · 잔량 폐기)

            Assert.Equal(CryptoArenaReject.Invalid, e.SubmitHuman(Sym(Btc), 0, S));
            Assert.Equal(CryptoArenaReject.Invalid, e.SubmitHuman(Sym(Btc), 1, 0));
            Assert.Equal(CryptoArenaReject.Invalid, e.SubmitHuman(Sym(Btc), 1, UpbitCryptoArenaEngine.MaxOrderQtyRaw + 1));
            Assert.Equal(CryptoArenaReject.Invalid, e.SubmitHuman(CryptoArenaUniverse.Pack("KRW-DOGE"), 1, S));

            // 무효 호가 = 폐기(비정렬 · 교차 · 음수 잔량 · 표 밖)
            long d0 = Snap(e).DroppedQuotes;
            e.OnQuote(Sym(Btc), 150_000_500 * S, 150_001_000 * S, S, S, t + 2);
            e.OnQuote(Sym(Btc), 150_001_000 * S, 150_001_000 * S, S, S, t + 3);
            e.OnQuote(Sym(Btc), 150_000_000 * S, 150_001_000 * S, -1, S, t + 4);
            e.OnQuote(CryptoArenaUniverse.Pack("KRW-DOGE"), 100 * S, 101 * S, S, S, t + 5);
            Assert.Equal(d0 + 4, Snap(e).DroppedQuotes);
        }

        [Fact]
        public void Ai_Partial_Exit_Never_Leaves_Sub_Minimum_Dust()
        {
            long bid = 3_500 * S;                                           // KRW-XRP 3,500원 · 최소 주문 2배 = 10,000원 ≈ 2.86 XRP 보존
            long keep = (long)((Int128)CryptoPaperExchange.MinOrderKrwRaw * 2 * S / bid + 1);
            Assert.Equal(1_000 * S, UpbitCryptoArenaEngine.ExitQty(1_000 * S, 5_000 * S, bid));          // 잔량 충분 = 전량
            Assert.Equal(10 * S, UpbitCryptoArenaEngine.ExitQty(1_000 * S, 10 * S, bid));                // 부분 = 잔량 상한
            Assert.Equal(1_000 * S - keep, UpbitCryptoArenaEngine.ExitQty(1_000 * S, 999 * S + S / 2, bid)); // 잔여 < 2배 최소 = 축소
            Assert.Equal(0, UpbitCryptoArenaEngine.ExitQty(3 * S, 2 * S, bid));                           // 축소 불가 = 보류(먼지 생성 0)
            Assert.Equal(0, UpbitCryptoArenaEngine.ExitQty(0, S, bid));
            Assert.Equal(0, UpbitCryptoArenaEngine.ExitQty(S, 0, bid));
            Assert.True((Int128)keep * bid / S >= CryptoPaperExchange.MinOrderKrwRaw * 2);
        }

        [Fact]
        public void Human_Queue_Full_Drops_Without_Retry()
        {
            var e = new UpbitCryptoArenaEngine();
            for (int i = 0; i < UpbitCryptoArenaEngine.HumanQueueCapacity; i++) Assert.Equal(CryptoArenaReject.None, e.SubmitHuman(Sym(Eth), 1, S));
            Assert.Equal(CryptoArenaReject.QueueFull, e.SubmitHuman(Sym(Eth), 1, S));
            e.Drain(Sat);
            Assert.Equal(UpbitCryptoArenaEngine.HumanQueueCapacity, Snap(e).Human.Rejects);
            Assert.Equal(CryptoArenaReject.None, e.SubmitHuman(Sym(Eth), 1, S));
        }

        [Fact]
        public void Market_Pack_Roundtrip_And_Capital_Bounds()
        {
            for (int i = 0; i < CryptoArenaUniverse.Count; i++)
            {
                Assert.Equal(i, CryptoArenaUniverse.IndexOf(CryptoArenaUniverse.Pack(CryptoArenaUniverse.Market(i))));
                Assert.Equal(CryptoArenaUniverse.Market(i), CryptoArenaUniverse.Unpack(CryptoArenaUniverse.Symbol(i)));
                Assert.True(UpbitTickAligner.IsAligned(CryptoArenaUniverse.SyntheticStartWon(i) * S));
            }
            Assert.Equal(new[] { "KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL" }, Enumerable.Range(0, 4).Select(CryptoArenaUniverse.Market));
            Assert.Equal(0, CryptoArenaUniverse.Pack("krw-btc"));
            Assert.Equal(0, CryptoArenaUniverse.Pack("KRW-BTCXXX"));
            Assert.Equal(0, CryptoArenaUniverse.Pack(""));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UpbitCryptoArenaEngine(999_999));
            Assert.Throws<ArgumentOutOfRangeException>(() => new UpbitCryptoArenaEngine(UpbitCryptoArenaEngine.MaxCapitalWon + 1));
            var e = new UpbitCryptoArenaEngine();
            Assert.Equal(100_000_000, e.CapitalWon);
            Assert.Equal(100_000_000 * S, Snap(e).Ai.CashWon);
            Assert.Equal(100_000_000 * S, Snap(e).Human.CashWon);
        }

        // ── §4 피드 ────────────────────────────────────────────────────────────

        private static string ObAt(long tsMs, long bidWon, long askWon) =>
            "{\"type\":\"orderbook\",\"code\":\"KRW-BTC\",\"timestamp\":" + tsMs + ",\"orderbook_units\":[{\"ask_price\":" + askWon + ",\"bid_price\":" + bidWon +
            ",\"ask_size\":0.5,\"bid_size\":0.25}],\"stream_type\":\"REALTIME\"}";

        private static string TrAt(long tsMs, long seq, long priceWon, string side) =>
            "{\"type\":\"trade\",\"code\":\"KRW-BTC\",\"timestamp\":" + tsMs + ",\"trade_timestamp\":" + tsMs + ",\"trade_price\":" + priceWon +
            ",\"trade_volume\":0.001,\"ask_bid\":\"" + side + "\",\"sequential_id\":" + seq + ",\"stream_type\":\"REALTIME\"}";

        [Fact]
        public void Upbit_Adapter_Tap_Feeds_Orderbook_And_Taker_Trades_Into_Engine()
        {
            var e = new UpbitCryptoArenaEngine();
            var f = new CryptoArenaQuoteFeeder(e);
            var ws = new UpbitWebSocketFeedAdapter(new UpbitMarketState(), tap: f.OnUpbitFrame);
            Assert.False(f.StreamAttached);
            f.AttachStream(ws);
            Assert.True(f.StreamAttached);
            Assert.False(f.StreamConnected);

            long ms = 1_761_274_800_000;
            Assert.Equal(UpbitStreamKind.Orderbook, ws.OnFrame(Encoding.UTF8.GetBytes(ObAt(ms, 150_000_000, 150_001_000))));
            Assert.Equal(UpbitStreamKind.Trade, ws.OnFrame(Encoding.UTF8.GetBytes(TrAt(ms + 1, 1, 150_001_000, "BID"))));
            Assert.Equal(UpbitStreamKind.Trade, ws.OnFrame(Encoding.UTF8.GetBytes(TrAt(ms + 2, 2, 150_000_000, "ASK"))));
            Assert.Equal(3, f.StreamUpdates);
            long utc = new DateTime(2026, 10, 24, 3, 0, 0).Ticks;
            f.Pump(utc);
            var s = Snap(e);
            Assert.Equal(1, s.Ticks);
            Assert.Equal(2, s.Trades);
            Assert.True(s.Tradable);
            var q = new DuelQuote[4];
            Assert.Equal(1, e.CopyQuotes(q));
            Assert.Equal((Sym(Btc), 150_000_000 * S, 150_001_000 * S, S / 4, S / 2), (q[0].Symbol, q[0].BidWon, q[0].AskWon, q[0].BidQty, q[0].AskQty));

            f.Pump(utc + TimeSpan.TicksPerMillisecond * 50);               // 갱신 0 = Drain
            Assert.Equal(1, Snap(e).Ticks);
            // 스트림 침묵 3초 후 휴먼 주문 = 호가 지연 거부(단절 = 체결 0)
            e.SubmitHuman(Sym(Btc), 1, S / 100);
            f.Pump(utc + TimeSpan.TicksPerSecond * 3);
            Assert.Equal(1, Snap(e).Human.Rejects);
            // 무효 프레임/인덱스 = 폐기
            Assert.False(f.OnFrame(9, default));
            Assert.False(f.OnFrame(Btc, default));
            Assert.False(f.OnBook(Btc, 150_000_500 * S, 150_001_000 * S, S, S));
            Assert.Equal(3, f.DroppedQuotes);
        }

        [Fact]
        public void Synthetic_Feed_Runs_24x7_With_One_To_One_Rationales_And_Source_Switch_Resets()
        {
            var e = new UpbitCryptoArenaEngine();
            var f = new CryptoArenaQuoteFeeder(e);
            Assert.False(f.SelectSource(0));
            Assert.True(f.SelectSource(CryptoArenaFeed.Synthetic));
            long utc = new DateTime(2026, 10, 24, 14, 0, 0).Ticks;         // 토요일 밤
            for (int i = 0; i < 40_000 && Snap(e).RationaleCount < 6; i++) f.Pump(utc + i * TimeSpan.TicksPerMillisecond * 50);
            var s = Snap(e);
            _out.WriteLine($"synTicks={s.Ticks} trades={s.Trades} aiFills={s.Ai.Fills} kimchi={s.KimchiPremiumBp} global={s.GlobalChangeBp}");
            Assert.Equal(CryptoArenaFeed.Synthetic, f.AppliedSource);
            Assert.True(s.Ticks > 0 && s.Trades > 0 && s.Tradable);
            Assert.True(s.RationaleCount >= 6, "합성 24/7 장세에서 AI 체결이 발생해야 한다");
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
            Assert.All(Rationales(e), r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
            Assert.True(f.SyntheticUtcMicros > 0);

            Assert.True(f.SelectSource(CryptoArenaFeed.UpbitStream));
            f.Pump(utc);
            s = Snap(e);
            Assert.Equal(CryptoArenaFeed.UpbitStream, f.AppliedSource);
            Assert.Equal(0, s.Ticks);
            Assert.Equal(0, s.RationaleCount);
            Assert.Equal(s.Ai.CapitalWon, s.Ai.CashWon);
            Assert.Equal(0, f.SyntheticUtcMicros);
        }

        [Fact]
        public void Dashboard_Hub_Drives_Crypto_Feeder()
        {
            var e = new UpbitCryptoArenaEngine();
            var f = new CryptoArenaQuoteFeeder(e);
            f.OnBook(Eth, 5_500_000 * S, 5_501_000 * S, S, S);
            var hub = typeof(DashboardSnapshotHub);
            Assert.NotNull(hub.GetMethod("AttachCryptoArena"));
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "Services", "DashboardSnapshotHub.cs"));
            Assert.Contains("Volatile.Read(ref _cryptoArena)?.Pump(nowT);", src);
            f.Pump(new DateTime(2026, 10, 24, 3, 0, 0).Ticks);
            Assert.Equal(1, f.Pumps);
            Assert.Equal(1, Snap(e).Ticks);
        }

        // ── §5 동시성 ──────────────────────────────────────────────────────────

        private static long QtyFor(long bidRaw) => (bidRaw / S % 997 + 1) * (S / 100);

        [Fact]
        public void Four_Producer_Threads_Inject_Quotes_Without_Tearing_Or_Ledger_Corruption()
        {
            const int Producers = 4, PerProducer = 40_000;
            var e = new UpbitCryptoArenaEngine();
            var f = new CryptoArenaQuoteFeeder(e);
            int done = 0; long torn = 0, reads = 0;
            var producers = Enumerable.Range(0, Producers).Select(p => new Thread(() =>
            {
                var rng = new Random(p * 7919);
                for (int i = 0; i < PerProducer; i++)
                {
                    int k = rng.Next(CryptoArenaUniverse.Count);
                    long start = CryptoArenaUniverse.SyntheticStartWon(k) * S;
                    long bid = UpbitTickAligner.AlignNearest(start + (rng.Next(-200, 200) * UpbitTickAligner.TickFor(start)));
                    f.OnBook(k, bid, UpbitTickAligner.AlignUp(bid + 1), QtyFor(bid), QtyFor(bid) / 2 + 1);
                    if ((i & 63) == 0) e.SubmitHuman(Sym(k), (i & 64) == 0 ? 1 : -1, S / 1000);
                }
                Interlocked.Increment(ref done);
            })).ToArray();
            long utc = new DateTime(2026, 10, 24, 3, 0, 0).Ticks;
            var pump = new Thread(() =>
            {
                long i = 0;
                while (Volatile.Read(ref done) < Producers) f.Pump(utc + ++i * TimeSpan.TicksPerMillisecond);
                f.Pump(utc + ++i * TimeSpan.TicksPerMillisecond);
            });
            var reader = new Thread(() =>
            {
                var q = new DuelQuote[4];
                var r = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
                while (Volatile.Read(ref done) < Producers)
                {
                    int n = e.CopyQuotes(q);
                    for (int i = 0; i < n; i++)
                        if (q[i].AskWon != UpbitTickAligner.AlignUp(q[i].BidWon + 1) || q[i].BidQty != QtyFor(q[i].BidWon) || q[i].AskQty != q[i].BidQty / 2 + 1) Interlocked.Increment(ref torn);
                    if (e.TryReadSnapshot(out var s) && s.Ai.Fills != s.RationaleCount) Interlocked.Increment(ref torn);
                    int m = e.CopyRationales(r);
                    for (int i = 0; i < m; i++) if (!r[i].IsWellFormed) Interlocked.Increment(ref torn);
                    Interlocked.Increment(ref reads);
                }
            });
            foreach (var t in producers) t.Start();
            pump.Start(); reader.Start();
            foreach (var t in producers) t.Join();
            pump.Join(); reader.Join();
            var fin = Snap(e);
            _out.WriteLine($"reads={reads} torn={torn} ticks={fin.Ticks} aiFills={fin.Ai.Fills} humanFills={fin.Human.Fills} humanRejects={fin.Human.Rejects}");
            Assert.Equal(0, torn);
            Assert.True(reads > 0 && fin.Ticks > 0);
            Assert.Equal(fin.Ai.Fills, fin.RationaleCount);
            Assert.True(fin.Ai.CashWon >= 0 && fin.Human.CashWon >= 0);
        }

        // ── §6 δ=0 ─────────────────────────────────────────────────────────────

        [Fact]
        public void HotPath_100k_Signal_And_Virtual_Fills_Allocate_Zero_Bytes()
        {
            var e = new UpbitCryptoArenaEngine();
            var rationales = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            var positions = new DuelPosition[UpbitCryptoArenaEngine.MaxPositions];
            var quotes = new DuelQuote[CryptoArenaUniverse.Count];
            ulong x = 0x9E3779B97F4A7C15;
            var px = new long[CryptoArenaUniverse.Count];
            for (int k = 0; k < px.Length; k++) px[k] = CryptoArenaUniverse.SyntheticStartWon(k) * S;
            long t = Sat, i0 = 0;

            void Run(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    int k = (int)(x % CryptoArenaUniverse.Count);
                    long start = CryptoArenaUniverse.SyntheticStartWon(k) * S;
                    long tick = UpbitTickAligner.TickFor(px[k]);
                    px[k] = UpbitTickAligner.AlignNearest(Math.Clamp(px[k] + ((long)(x >> 8 & 7) - 3) * Math.Max(tick, px[k] / 4000), start * 8 / 10, start * 12 / 10));
                    if ((i0 & 511) == 0) e.SetMarketContext((int)(x >> 20 & 0x1FF) - 200, (int)(x >> 30 & 0x3FF) - 512);
                    long bq = S / 100 * (1 + (long)(x >> 24 & 1023)), aq = S / 100 * (1 + (long)(x >> 36 & 1023));
                    t += 50_000;
                    if ((i0 & 3) == 0) e.OnTrade(Sym(k), px[k], S / 100 * (1 + (long)(x >> 44 & 63)), (x & 16) == 0 ? 1 : -1, t);
                    e.OnQuote(Sym(k), px[k], UpbitTickAligner.AlignUp(px[k] + 1), bq, aq, t);
                    if ((i0 & 7) == 0) e.SubmitHuman(Sym(k), (i0 & 8) == 0 ? 1 : -1, S / 1000 * (1 + (long)(x >> 50 & 7)));
                    if ((i0 & 63) == 0)
                    {
                        e.TryReadSnapshot(out _);
                        e.CopyRationales(rationales);
                        e.CopyPositions(ArenaSide.Ai, positions);
                        e.CopyQuotes(quotes);
                    }
                    if ((i0 & 1023) == 0) e.Drain(t);
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
        public void Upbit_Frame_And_Synthetic_Feed_100k_Allocate_Zero_Bytes()
        {
            var e = new UpbitCryptoArenaEngine();
            var f = new CryptoArenaQuoteFeeder(e);
            var ob = new UpbitStreamFrame(UpbitStreamKind.Orderbook, 1, 150_000_000 * S, 150_001_000 * S, S / 4, S / 2, 0, 0, UpbitTradeSide.None, 0);
            var tb = new UpbitStreamFrame(UpbitStreamKind.Trade, 1, 0, 0, 0, 0, 150_001_000 * S, S / 1000, UpbitTradeSide.Bid, 1);
            var ta = new UpbitStreamFrame(UpbitStreamKind.Trade, 1, 0, 0, 0, 0, 150_000_000 * S, S / 1000, UpbitTradeSide.Ask, 2);
            long utc = new DateTime(2026, 10, 24, 3, 0, 0).Ticks, i0 = 0;

            void RunStream(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    long r = i0 & 3;
                    if (r == 0) f.OnUpbitFrame(in ob);
                    else if (r == 1) f.OnUpbitFrame(in tb);
                    else if (r == 2) f.OnUpbitFrame(in ta);
                    else f.OnBook(Eth, 5_500_000 * S, 5_501_000 * S, S, S);
                    f.Pump(utc + i0 * TimeSpan.TicksPerMillisecond);
                }
            }

            RunStream(20_000);
            long streamBytes = MinAllocated(() => RunStream(100_000));
            long ticksAfterStream = Snap(e).Ticks;

            f.SelectSource(CryptoArenaFeed.Synthetic);
            f.Pump(utc);
            long j = 0;
            void RunSyn(int n) { for (int i = 0; i < n; i++, j++) f.Pump(utc + j * TimeSpan.TicksPerMillisecond * 50); }
            RunSyn(20_000);
            long synBytes = MinAllocated(() => RunSyn(100_000));
            var s = Snap(e);
            _out.WriteLine($"streamδ={streamBytes}B synδ={synBytes}B streamUpdates={f.StreamUpdates} ticksAfterStream={ticksAfterStream} synTicks={s.Ticks} aiFills={s.Ai.Fills}");
            Assert.Equal(0, streamBytes);
            Assert.Equal(0, synBytes);
            Assert.True(f.StreamUpdates >= 90_000 && ticksAfterStream > 0);
            Assert.True(s.Ticks > 0);
            Assert.Equal(s.Ai.Fills, s.RationaleCount);
        }

        [Fact]
        public void HotPath_Methods_Have_No_Il_Allocation_Sites()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var targets = new (Type t, string[] names)[]
            {
                (typeof(UpbitCryptoArenaEngine), new[] { "OnQuote", "OnTrade", "Drain", "Clock", "Evaluate", "StepAi", "DrainHuman", "Buy", "Sell", "Record",
                    "Equity", "Stats", "PublishSnapshot", "SubmitHuman", "TryDequeue", "TryReadSnapshot", "CopyPositions", "CopyQuotes", "CopyRationales",
                    "FindPos", "AlignUpRaw", "AlignDownRaw", "DepthWon", "ExitQty", "PhaseAt", "KstTicksOf", "MapStatus", "SetMarketContext" }),
                (typeof(CryptoArenaQuoteFeeder), new[] { "OnUpbitFrame", "OnFrame", "OnBook", "Lock", "Pump", "TryReadSlot", "MarkAllSeen", "StepSynthetic", "UtcMicrosOf" }),
                (typeof(CryptoArenaUniverse), new[] { "IndexOf", "Symbol", "GlobalBetaX100", "KimchiSensX100", "SyntheticStartWon" }),
                (typeof(CryptoPaperExchange), new[] { "SlippedMarketPrice", "NotionalWithFee" }),
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
            string[] bad = { "Arm", "Broker", "Live", "Send", "Route", "Valve", "Order(" };
            foreach (var type in new[] { typeof(ICryptoArenaPort), typeof(UpbitCryptoArenaEngine), typeof(CryptoArenaQuoteFeeder) })
            {
                var names = type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Select(m => m.Name).ToArray();
                foreach (var b in bad) Assert.DoesNotContain(names, n => n.Contains(b, StringComparison.Ordinal));
            }
            foreach (var type in new[] { typeof(UpbitCryptoArenaEngine), typeof(CryptoArenaQuoteFeeder), typeof(CryptoArenaPortAdapter) })
                foreach (var fi in type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    string ns = fi.FieldType.Namespace ?? "";
                    Assert.False(ns.StartsWith("System.Net", StringComparison.Ordinal), $"{type.Name}.{fi.Name}");
                    Assert.DoesNotContain("Broker", fi.FieldType.Name, StringComparison.Ordinal);
                    Assert.DoesNotContain("Transport", fi.FieldType.Name, StringComparison.Ordinal);
                }

            var portable = typeof(ICryptoArenaPort).Assembly;
            Assert.DoesNotContain(portable.GetReferencedAssemblies(), a => a.Name == typeof(UpbitCryptoArenaEngine).Assembly.GetName().Name);
            foreach (var dto in new[] { typeof(CryptoArenaView), typeof(CryptoArenaBookView), typeof(CryptoRationaleView), typeof(CryptoArenaQuoteView), typeof(CryptoArenaPositionView), typeof(CryptoArenaFeedStatus) })
                Assert.Equal(portable, dto.Assembly);
            Assert.Equal(CryptoArenaUnits.Scale, UpbitTickAligner.Scale);
            Assert.Equal(5, UpbitCryptoArenaEngine.FeeBp);                // 업비트 KRW 마켓 0.05%
        }

        [Fact]
        public void Adapter_Projects_Krw_Snapshot_Quotes_Positions_And_Korean_Rationales_Newest_First()
        {
            var e = new UpbitCryptoArenaEngine();
            ICryptoArenaPort port = new CryptoArenaPortAdapter(e);
            Assert.False(port.Fed);
            Assert.False(port.SelectSource(CryptoArenaFeed.Synthetic));    // 주입기 미결속
            Assert.False(port.Feed.Pumped);
            Assert.False(port.Feed.StreamAttached);
            Assert.True(port.TryRead(out var v0));
            Assert.Equal(0, v0.Ticks);
            Assert.Equal(ArenaLeader.Draw, v0.Leader);
            Assert.Empty(port.RecentRationales(10));
            Assert.Empty(port.Quotes());
            Assert.Equal(new[] { "KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL" }, port.Markets);
            Assert.Equal("가상자산(Upbit ₿)", port.ModeName);

            long t = Sat;
            Warm(e, ref t); Spike(e, ref t);
            Assert.True(port.Fed);
            Assert.True(port.TryRead(out var v));
            Assert.True(v.Tradable);
            Assert.Equal(300, v.GlobalChangeBp);
            Assert.Equal(4, v.Trades);
            Assert.Equal(100_000_000 * S, v.Ai.CapitalRaw);
            var pos = Assert.Single(port.Positions(ArenaSide.Ai));
            Assert.Equal(("KRW-BTC", "BTC"), (pos.Market, pos.Coin));
            Assert.True(pos.TargetPriceRaw > pos.MarkRaw && pos.StopLossPriceRaw < pos.MarkRaw);
            Assert.Equal((long)((Int128)pos.QtyRaw * pos.MarkRaw / S), pos.EvalRaw);
            var quote = Assert.Single(port.Quotes());
            Assert.Equal(("KRW-BTC", 150_100_000 * S, 150_101_000 * S), (quote.Market, quote.BidRaw, quote.AskRaw));

            var entry = Rationales(e)[0];
            Q(e, Btc, entry.TargetPrice / S, UpbitTickAligner.AlignUp(entry.TargetPrice + 1) / S, ref t);
            var feed = port.RecentRationales(10);
            Assert.Equal(2, feed.Count);
            Assert.Equal(-1, feed[0].Side);
            Assert.Equal("목표가 도달(익절)", feed[0].Trigger);
            Assert.Equal("호가 수급 불균형", feed[1].Trigger);
            Assert.Equal(("KRW-BTC", "BTC"), (feed[1].Market, feed[1].Coin));
            Assert.Equal(entry.ImbalanceBp, feed[1].ImbalanceBp);
            Assert.Equal(entry.MacroBp, feed[1].KimchiVolBp);
            Assert.Equal(entry.SurgeBp, feed[1].FlowBp);
            Assert.Equal(1_350_900_000, feed[1].BidDepthWon);
            Assert.All(feed, r => Assert.Contains("원", r.Summary));
            Assert.All(feed, r => Assert.Contains("오더북 두께", r.Summary));
            Assert.True(port.TryRead(out var v2));
            Assert.Equal(ArenaLeader.Ai, v2.Leader);
            Assert.True(v2.Ai.ReturnBp > 0);
            Assert.Equal(CryptoArenaPortAdapter.ReturnBp(v2.Ai.EquityRaw, v2.Ai.CapitalRaw), v2.Ai.ReturnBp);
            Assert.Equal(10_000, CryptoArenaPortAdapter.ReturnBp(2_000_000_000L * S, 1_000_000_000L * S));   // 원 raw × 10000 = long 초과 구간도 정확

            Assert.Equal(CryptoArenaReject.None, port.SubmitHuman("KRW-ETH", 1, S));
            Assert.Equal(CryptoArenaReject.Invalid, port.SubmitHuman("KRW-DOGE", 1, S));
            Assert.Equal(CryptoArenaReject.Invalid, port.SubmitHuman("krw-btc", 1, S));
            Assert.Equal(CryptoArenaReject.Invalid, port.SubmitHuman("", 1, S));
            Assert.Equal(CryptoArenaReject.Invalid, port.SubmitHuman(null!, 1, S));
            Assert.Throws<ArgumentException>(() => new CryptoArenaPortAdapter(e, new CryptoArenaQuoteFeeder(new UpbitCryptoArenaEngine())));
        }

        [Fact]
        public void Existing_Krw_And_Usd_Rationale_Summaries_Unchanged_By_Crypto_Extension()
        {
            var krw = new AiTradeRationale(1, 5930, 1, 10, 70_100, Sat * 10, ArenaTrigger.OrderbookImbalance, 5000, 50, 71_000, 69_500, 150, 0, 8000, 0, 0, 0);
            Assert.Equal(ArenaCurrency.Krw, krw.Currency);
            Assert.Equal(0, krw.BidDepthWon);
            Assert.Contains("005930 10주 @ 70,100원", krw.HumanReadableSummary);
            Assert.DoesNotContain("오더북 두께", krw.HumanReadableSummary);
            Assert.True(ArenaTriggerText.IsEntry(ArenaTrigger.TradeFlowMomentum) && ArenaTriggerText.IsEntry(ArenaTrigger.KimchiVolatility));
            Assert.False(ArenaTriggerText.IsExit(ArenaTrigger.KimchiVolatility));
            Assert.Equal("1,234.5원", AiRationaleText.WonRaw(123_450_000_000));
            Assert.Equal("0.00000001", AiRationaleText.CoinQty(1));
        }

        [Fact]
        public void Host_Di_Registers_Crypto_Arena_Port_Via_Adapter_And_Gated_Upbit_Stream()
        {
            string form = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.ICryptoArenaPort>(new StockManager.Adapters.CryptoArenaPortAdapter(cryptoArena, cryptoArenaFeeder));", form);
            Assert.Contains("dashHub.AttachCryptoArena(cryptoArenaFeeder);", form);
            Assert.Contains("if (cfg.GetBool(\"Arena.UpbitStreamEnabled\", false))", form);   // 기본 꺼짐 = 네트워크 호출 0
            Assert.Contains("tap: cryptoArenaFeeder.OnUpbitFrame", form);
        }

        // ── §8 /dualarena 3번째 탭 ──────────────────────────────────────────────

        [Fact]
        public void DualArena_Crypto_Tab_Renders_Honest_Empty_Then_Krw_Scoreboard_Ticket_And_Rationale_Cards()
        {
            var krx = new StockManager.Engines.Arena.Domestic.KrxArenaEngine();
            var e = new UpbitCryptoArenaEngine();
            var f = new CryptoArenaQuoteFeeder(e);
            using var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx));
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new StockManager.Engines.Arena.Us.UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(e, f));
            var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.DualArenaPage>();
            Assert.Empty(cut.FindAll(".da-crypto"));

            cut.Find(".da-mode-tab[data-mode=crypto]").Click();
            Assert.Contains("on", cut.Find(".da-mode-tab[data-mode=crypto]").ClassList);
            Assert.Single(cut.FindAll(".da-crypto"));
            Assert.Empty(cut.FindAll(".da-krx"));
            Assert.Contains("정산 펌프 미결선", cut.Markup);
            Assert.Empty(cut.FindAll(".da-book"));                          // 가짜 손익 0

            long utc = new DateTime(2026, 10, 24, 14, 0, 0).Ticks;
            f.Pump(utc);
            cut.Render();
            Assert.Contains("업비트 스트림 미결선", cut.Markup);            // 설정 꺼짐 = 정직 렌더
            Assert.Empty(cut.FindAll(".da-card"));

            cut.FindAll(".da-crypto .ar-tab")[1].Click();                   // 합성 시세(데모)
            Assert.Equal(CryptoArenaFeed.Synthetic, f.RequestedSource);
            for (int i = 1; i < 40_000 && Snap(e).RationaleCount < 2; i++) f.Pump(utc + i * TimeSpan.TicksPerMillisecond * 50);
            cut.Render();
            Assert.Contains("합성 시세(데모 · 24/7 ×20)", cut.Markup);
            Assert.Contains("24/7 연속매매 가동", cut.Find(".da-phase").TextContent);
            var books = cut.FindAll(".da-book");
            Assert.Equal(2, books.Count);
            Assert.Contains("자본금 100,000,000원", books[0].TextContent);
            var cards = cut.FindAll(".da-card");
            Assert.True(cards.Count >= 2);
            Assert.Contains("KRW-", cards[0].TextContent);
            Assert.Contains("오더북 두께", cards[0].QuerySelector(".da-depth")!.TextContent);
            Assert.Contains("김프/변동성", cards[0].TextContent);
            Assert.Equal(4, cut.FindAll(".da-mkt option").Count);

            long humanBefore = Snap(e).Human.Fills;
            cut.Find(".da-crypto .ar-btn.buy").Click();
            Assert.Contains("가상 체결(KRW)", cut.Find(".da-msg").TextContent);
            f.Pump(utc + 40_001 * TimeSpan.TicksPerMillisecond * 50);
            Assert.Equal(humanBefore + 1, Snap(e).Human.Fills);
            Assert.Equal(0, krx.TryReadSnapshot(out var ks) ? ks.Human.Fills : -1);   // 국내 원장 무영향
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("StockManager.sln");
        }
    }
}
