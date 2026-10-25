// ============================================================================
// PlutoMicrostructureTests — v12.18.75-DEV: 플루토 브레인 4단계(체결강도 누적 델타 CVD · 10호가 지수 가중 뎁스 · 미시구조 팩터 · 3대 아레나 결속) 검증
//
//   ① CVD: 포화 덧셈 · 틱 룰 · 체결 방향별 누적 · 1분 봉 확정 · 10봉 창 · 강세/약세 다이버전스 판정 · 점수 범위(+20~+35 / −25).
//   ② 뎁스: 10호가 지수 가중합(‰ 가중표) · 비율 계산 · 제공 범위 비례(L1 · 길이 불일치) · 매수벽 지지 · 매도벽 · 허매수 · fail-closed.
//   ③ 팩터 무결성 스윕 · 악마의 변호인 ⑤ 약세 다이버전스 −25 · ⑥ 뎁스 함정 −20 · 거부권 · 거버너 태그/문장.
//   ④ 3대 아레나: 시그널 가산/주도 사유 · TryReadMicrostructure · 근거 카드 뱃지/문장 · 정지 = 기여 0 · 초기화.
//   ⑤ δ=0: 분석기 10만 회 · KRX 정산 10만 회 · 업비트 정산 10만 회 · 판독 10만 회 = 0B(MinAllocated · Release).
// ============================================================================
using System;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Microstructure;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoMicrostructureTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);            // 목 10:00 KST(KRX 정규장)
        private static readonly DateTime KstUsReg = new(2026, 10, 22, 23, 0, 0);         // 목 10:00 EDT(미국 정규장)
        private static readonly long Nvda = UsTechUniverse.Pack("NVDA");
        private const long S = UpbitTickAligner.Scale;
        private static readonly long Sat = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        private static readonly long Min = TimeSpan.TicksPerMinute, Sec = TimeSpan.TicksPerSecond;
        private readonly ITestOutputHelper _out;

        public PlutoMicrostructureTests(ITestOutputHelper o) => _out = o;

        // ── ① CVD ───────────────────────────────────────────────────────────────

        [Fact]
        public void SatAdd_Saturates_Symmetrically()
        {
            Assert.Equal(5, PlutoCumulativeVolumeDelta.SatAdd(2, 3));
            Assert.Equal(long.MaxValue, PlutoCumulativeVolumeDelta.SatAdd(long.MaxValue - 1, 10));
            Assert.Equal(-long.MaxValue, PlutoCumulativeVolumeDelta.SatAdd(-long.MaxValue, -10));
            Assert.Equal(-long.MaxValue, PlutoCumulativeVolumeDelta.SatAdd(-long.MaxValue, -1));
            Assert.Equal(0, PlutoCumulativeVolumeDelta.SatAdd(long.MaxValue, -long.MaxValue));
        }

        [Fact]
        public void Tick_Rule_Classifies_Up_Down_And_Carries_Zero_Tick()
        {
            var s = new PlutoCvdState();
            Assert.Equal(0, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 100));     // 첫 가격 = 방향 미정
            Assert.Equal(0, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 100));     // 보합 · 직전 방향 없음 = 미정
            Assert.Equal(1, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 101));
            Assert.Equal(1, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 101));     // 보합 = 직전(매수)
            Assert.Equal(-1, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 99));
            Assert.Equal(-1, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 99));
            Assert.Equal(0, PlutoCumulativeVolumeDelta.TickRuleSide(ref s, 0));       // 무효 = 상태 불변
            Assert.Equal(99, s.LastPrice);
        }

        /// <summary>분 m 마다 매수 buy · 매도 sell 체결(가격 = close(m)).</summary>
        private static PlutoCvdState Bars(int minutes, Func<int, long> close, long buy, long sell, long t0 = 0)
        {
            var s = new PlutoCvdState();
            long t = t0 == 0 ? Kst10.Ticks : t0;
            for (int m = 0; m < minutes; m++)
            {
                if (buy > 0) PlutoCumulativeVolumeDelta.OnTrade(ref s, 1, buy, close(m), t + m * Min + Sec);
                if (sell > 0) PlutoCumulativeVolumeDelta.OnTrade(ref s, -1, sell, close(m), t + m * Min + 2 * Sec);
            }
            return s;
        }

        [Fact]
        public void Cvd_Accumulates_By_Direction_And_Commits_Minute_Bars()
        {
            var s = new PlutoCvdState();
            long t = Kst10.Ticks;
            PlutoCumulativeVolumeDelta.OnTrade(ref s, 1, 300, 10_000, t);
            PlutoCumulativeVolumeDelta.OnTrade(ref s, -1, 120, 10_010, t + 10 * Sec);
            PlutoCumulativeVolumeDelta.OnTrade(ref s, 0, 999, 10_010, t + 11 * Sec);    // 무효 방향
            PlutoCumulativeVolumeDelta.OnTrade(ref s, 1, 0, 10_010, t + 12 * Sec);      // 무효 수량
            PlutoCumulativeVolumeDelta.OnTrade(ref s, 1, 10, -1, t + 13 * Sec);         // 무효 가격
            PlutoCumulativeVolumeDelta.OnTrade(ref s, 1, 10, 10_000, 0);                // 무효 시각
            Assert.Equal(180, s.Cvd);
            Assert.Equal((180L, 420L, 10_010L, 0), (s.BarDelta, s.BarVolume, s.BarClose, s.Count));
            PlutoCumulativeVolumeDelta.OnTrade(ref s, -1, 50, 10_020, t + Min);         // 분 경계 = 직전 봉 확정
            Assert.Equal(1, s.Count);
            Assert.Equal((180L, 420L, 10_010L), (s.Bars[0].Delta, s.Bars[0].Volume, s.Bars[0].Close));
            Assert.Equal(130, s.Cvd);
            PlutoCumulativeVolumeDelta.OnTrade(ref s, 1, 5, 10_020, t + 30 * Sec);       // 시각 역행 = 진행 봉 합산
            Assert.Equal((1, -45L), (s.Count, s.BarDelta));
            Assert.False(PlutoCumulativeVolumeDelta.Read(in s, 10_020).Ready);          // 완성 봉 1 < 3
        }

        [Fact]
        public void Bullish_Divergence_Price_Falling_While_Cvd_Rises()
        {
            var s = Bars(6, m => 10_000 - m * 10, 80, 20);                              // 완성 5봉 · 봉당 +60/100
            Assert.Equal(5, s.Count);
            Assert.Equal(360, s.Cvd);
            var r = PlutoCumulativeVolumeDelta.Read(in s, 9_950);
            Assert.True(r.Ready);
            Assert.Equal((6000, -50), (r.CvdSlopeBps, r.PriceChangeBps));
            Assert.Equal(PlutoDivergenceType.Bullish, r.Divergence);
            Assert.Equal(20 + 3500 * 15 / 7500, r.Score);
            Assert.InRange(r.Score, 20, 35);
        }

        [Fact]
        public void Bearish_Divergence_Price_Rising_While_Cvd_Plunges()
        {
            var s = Bars(6, m => 10_000 + m * 10, 20, 80);
            var r = PlutoCumulativeVolumeDelta.Read(in s, 10_060);
            Assert.Equal((-6000, 60), (r.CvdSlopeBps, r.PriceChangeBps));
            Assert.Equal(PlutoDivergenceType.Bearish, r.Divergence);
            Assert.Equal(PlutoCumulativeVolumeDelta.BearishScore, r.Score);
            Assert.Equal(-25, r.Score);
        }

        [Theory]
        [InlineData(10_060, 80, 20, 6)]          // 가격 상승 ∧ 매수 우위 = 추세 확인(다이버전스 아님) · 6000/1000
        [InlineData(9_950, 20, 80, -6)]          // 가격 하락 ∧ 매도 우위 = 추세 확인
        [InlineData(10_010, 80, 20, 27)]         // 가격 +10bp(횡보 상한) = 강세 다이버전스 포함
        [InlineData(10_011, 80, 20, 6)]          // +11bp = 횡보 이탈
        [InlineData(10_019, 20, 80, -6)]         // +19bp = 상승 하한 미만 → 약세 다이버전스 아님
        [InlineData(10_020, 20, 80, -25)]        // +20bp = 약세 다이버전스
        [InlineData(9_900, 62, 38, 2)]           // 기울기 2400 < 2500 = 문턱 미만
        [InlineData(9_900, 63, 37, 20)]          // 기울기 2600 = 문턱 이상 → +20
        public void Divergence_Thresholds_And_Neutral_Score(long price, long buy, long sell, int expectedScore)
        {
            var s = Bars(6, _ => 10_000, buy, sell);
            var r = PlutoCumulativeVolumeDelta.Read(in s, price);
            Assert.Equal(expectedScore, r.Score);
        }

        [Fact]
        public void Window_Keeps_Last_Ten_Completed_Bars()
        {
            var s = new PlutoCvdState();
            long t = Kst10.Ticks;
            for (int m = 0; m < 16; m++)
                PlutoCumulativeVolumeDelta.OnTrade(ref s, m < 5 ? -1 : 1, 100, 10_000, t + m * Min);
            Assert.Equal(PlutoCumulativeVolumeDelta.Window, s.Count);
            Assert.Equal(-500 + 1100, s.Cvd);
            var r = PlutoCumulativeVolumeDelta.Read(in s, 10_000);
            Assert.Equal((10000, 0, 10), (r.CvdSlopeBps, r.PriceChangeBps, r.Bars));   // 매도 5봉 = 창 밖
            Assert.Equal(PlutoCumulativeVolumeDelta.BullishMaxScore, r.Score);
        }

        [Fact]
        public void Bullish_Score_Is_Linear_And_Clamped()
        {
            Assert.Equal(20, PlutoCumulativeVolumeDelta.BullishScore(2500));
            Assert.Equal(35, PlutoCumulativeVolumeDelta.BullishScore(10000));
            Assert.Equal(35, PlutoCumulativeVolumeDelta.BullishScore(int.MaxValue));
            Assert.Equal(20, PlutoCumulativeVolumeDelta.BullishScore(-5000));
            for (int v = 2500; v <= 10000; v += 37) Assert.InRange(PlutoCumulativeVolumeDelta.BullishScore(v), 20, 35);
        }

        // ── ② 10호가 뎁스 ───────────────────────────────────────────────────────

        [Fact]
        public void Weight_Table_Halves_Every_Three_Tiers()
        {
            for (int i = 0; i < PlutoOrderbookDepthAnalyzer.MaxTiers; i++)
            {
                double ideal = 1000 * Math.Pow(2, -i / 3.0);
                Assert.InRange(PlutoOrderbookDepthAnalyzer.WeightOf(i), ideal - 0.5, ideal + 0.5);
                if (i > 0) Assert.True(PlutoOrderbookDepthAnalyzer.WeightOf(i) < PlutoOrderbookDepthAnalyzer.WeightOf(i - 1));
            }
            Assert.Equal(0, PlutoOrderbookDepthAnalyzer.WeightOf(PlutoOrderbookDepthAnalyzer.MaxTiers));
            Assert.Equal(0, PlutoOrderbookDepthAnalyzer.WeightOf(-1));
        }

        [Fact]
        public void Exponential_Weighted_Sum_And_Ratio_Match_Reference()
        {
            var rng = new Random(75);
            for (int it = 0; it < 3000; it++)
            {
                int nb = rng.Next(1, 18), na = rng.Next(1, 18);
                var bid = new long[nb];
                var ask = new long[na];
                for (int i = 0; i < nb; i++) bid[i] = rng.NextInt64(0, 1_000_000_000_000);
                for (int i = 0; i < na; i++) ask[i] = rng.NextInt64(0, 1_000_000_000_000);
                int n = Math.Min(Math.Min(nb, na), PlutoOrderbookDepthAnalyzer.MaxTiers);
                Int128 wb = 0, wa = 0;
                for (int i = 0; i < n; i++) { wb += (Int128)bid[i] * PlutoOrderbookDepthAnalyzer.WeightOf(i); wa += (Int128)ask[i] * PlutoOrderbookDepthAnalyzer.WeightOf(i); }
                var r = PlutoOrderbookDepthAnalyzer.Analyze(bid, ask);
                Assert.Equal(n, r.Tiers);
                Assert.Equal((long)(wb / 1000), r.WeightedBid);
                Assert.Equal((long)(wa / 1000), r.WeightedAsk);
                int ratio = wb + wa == 0 ? 0 : (int)((wb - wa) * 10000 / (wb + wa));
                Assert.Equal(ratio, r.DepthRatioBps);
                Assert.Equal(PlutoOrderbookDepthAnalyzer.ScoreOf(r.DepthRatioBps, r.Flags), r.Score);
                Assert.InRange(r.Score, -50, 50);
            }
        }

        [Fact]
        public void Near_Tier_Outweighs_Far_Tier()
        {
            var bid = new long[10];
            var ask = new long[10];
            bid[0] = 100; ask[9] = 100;
            var r = PlutoOrderbookDepthAnalyzer.Analyze(bid, ask);
            Assert.Equal((100L, 12L), (r.WeightedBid, r.WeightedAsk));
            Assert.Equal((int)(87_500L * 10000 / 112_500), r.DepthRatioBps);              // 7777
            Assert.Equal(PlutoDepthFlags.BidSupportWall | PlutoDepthFlags.AskWall, r.Flags);
            Assert.Equal(7777 / 250 + 10 - 15, r.Score);
        }

        [Fact]
        public void Provided_Range_Is_Proportionally_Weighted()
        {
            var l1 = PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 300 }, new long[] { 100 });
            Assert.Equal((1, 5000, PlutoDepthFlags.None, 20), (l1.Tiers, l1.DepthRatioBps, l1.Flags, l1.Score));
            var st = new PlutoMicrostructureState();
            PlutoMicrostructureAnalyzer.OnL1(ref st, 300, 100);
            Assert.Equal(l1.DepthRatioBps, st.Depth.DepthRatioBps);
            var mixed = PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10, 10 }, new long[] { 10, 10, 9000 });
            Assert.Equal(3, mixed.Tiers);                                               // 공통 제공 범위 = 3호가
            Assert.True(mixed.DepthRatioBps < 0 && (mixed.Flags & PlutoDepthFlags.AskWall) != 0);
            var two = PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 10, 9000 }, new long[] { 10, 10 });
            Assert.Equal(PlutoDepthFlags.None, two.Flags);                              // 3호가 미만 = 벽 판정 없음
            var even = PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 }, new long[] { 5, 5, 5, 5, 5, 5, 5, 5, 5, 5 });
            Assert.Equal((0, PlutoDepthFlags.None, 0), (even.DepthRatioBps, even.Flags, even.Score));
            var zero = PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 0, 0, 0 }, new long[] { 0, 0, 0 });
            Assert.Equal((3, 0, PlutoDepthFlags.None), (zero.Tiers, zero.DepthRatioBps, zero.Flags));
        }

        [Fact]
        public void Invalid_Depth_Fails_Closed()
        {
            Assert.Equal(0, PlutoOrderbookDepthAnalyzer.Analyze(ReadOnlySpan<long>.Empty, new long[] { 1 }).Tiers);
            Assert.Equal(0, PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 1, -1 }, new long[] { 1, 1 }).Tiers);
            Assert.Equal(0, PlutoOrderbookDepthAnalyzer.Analyze(new long[] { 1, 1 }, new long[] { -5, 1 }).Tiers);
        }

        [Fact]
        public void Bid_Support_Wall_Near_Touch_Adds_Bonus()
        {
            var bid = new long[] { 50, 2000, 50, 50, 50, 50, 50, 50, 50, 50 };
            var ask = new long[] { 50, 50, 50, 50, 50, 50, 50, 50, 50, 50 };
            var r = PlutoOrderbookDepthAnalyzer.Analyze(bid, ask);
            Assert.Equal(PlutoDepthFlags.BidSupportWall, r.Flags);
            Assert.True(r.DepthRatioBps > 0);
            Assert.Equal(r.DepthRatioBps / 250 + PlutoOrderbookDepthAnalyzer.SupportWallBonus, r.Score);
        }

        [Fact]
        public void Far_Bid_Bait_Is_Flagged_As_Spoof_And_Stripped()
        {
            var bid = new long[] { 50, 50, 50, 50, 50, 50, 50, 5000, 50, 50 };
            var ask = new long[] { 50, 50, 50, 50, 50, 50, 50, 50, 50, 50 };
            var r = PlutoOrderbookDepthAnalyzer.Analyze(bid, ask);
            Assert.Equal(PlutoDepthFlags.BidSpoof, r.Flags);
            Assert.Equal((218_300L + 4950L * 198) / 1000, r.WeightedBid);
            Assert.True(r.DepthRatioBps > 0);                                           // 가중 비율은 매수 우위로 보이지만
            Assert.Equal(PlutoOrderbookDepthAnalyzer.SpoofPenalty, r.Score);           // 미끼 물량 → 양수 박탈 −10
            var share49 = new long[] { 300, 300, 300, 300, 300, 300, 300, 2600, 300, 300 }; // 원거리 벽이지만 원잔량 50% 미만 = 판정 보류
            var r2 = PlutoOrderbookDepthAnalyzer.Analyze(share49, ask);
            Assert.Equal(PlutoDepthFlags.None, r2.Flags);
        }

        [Fact]
        public void Ask_Wall_Anywhere_Penalizes()
        {
            var bid = new long[] { 100, 100, 100, 100, 100, 100, 100, 100, 100, 100 };
            var ask = new long[] { 100, 100, 100, 100, 100, 100, 3000, 100, 100, 100 };
            var r = PlutoOrderbookDepthAnalyzer.Analyze(bid, ask);
            Assert.Equal(PlutoDepthFlags.AskWall, r.Flags);
            Assert.Equal(r.DepthRatioBps / 250 + PlutoOrderbookDepthAnalyzer.AskWallPenalty, r.Score);
            var st = new PlutoMicrostructureState();
            PlutoMicrostructureAnalyzer.OnDepth(ref st, bid, ask);
            var f = PlutoMicrostructureAnalyzer.Assess(in st, Samsung, 70_000);
            Assert.True(f.HasAskWall && f.IsDepthTrap && !f.CvdReady && f.IsWellFormed);
            Assert.Equal(7, f.AskWallTier);
            Assert.Contains("[🧱 7호가 매도벽 -15]", PlutoMicrostructureText.Badges(in f));
        }

        // ── ③ 팩터 · 악마의 변호인 ──────────────────────────────────────────────

        [Fact]
        public void Factor_Default_And_Combine()
        {
            var st = new PlutoMicrostructureState();
            var none = PlutoMicrostructureAnalyzer.Assess(in st, Samsung, 70_000);
            Assert.False(none.HasMicrostructure);
            Assert.True(none.IsWellFormed);
            Assert.Equal(0, none.MicrostructureBp);
            Assert.Equal("", PlutoMicrostructureText.Badges(in none));
            Assert.Equal(100, PlutoMicrostructureAnalyzer.Combine(80, 50));
            Assert.Equal(-100, PlutoMicrostructureAnalyzer.Combine(-80, -50));
            Assert.Equal(-15, PlutoMicrostructureAnalyzer.Combine(-25, 10));

            var bull = new PlutoMicrostructureState { Cvd = Bars(6, m => 10_000 - m * 10, 80, 20) };
            PlutoMicrostructureAnalyzer.OnDepth(ref bull, new long[] { 50, 2000, 50, 50, 50, 50, 50, 50, 50, 50 }, new long[] { 50, 50, 50, 50, 50, 50, 50, 50, 50, 50 });
            var f = PlutoMicrostructureAnalyzer.Assess(in bull, 7, 9_950);
            Assert.True(f.IsWellFormed && f.IsBullishDivergence && f.IsCvdBuyDominant && f.HasBidSupportWall && !f.IsDepthTrap);
            Assert.Equal(PlutoMicrostructureAnalyzer.Combine(f.CvdScore, f.DepthScore), f.MicrostructureScore);
            Assert.Equal(f.MicrostructureScore * 100, f.MicrostructureBp);
            string badges = PlutoMicrostructureText.Badges(in f);
            Assert.Contains(PlutoMicrostructureText.CvdBuyBadge, badges);
            Assert.Contains(PlutoMicrostructureText.BullishBadge, badges);
            Assert.Contains("[🛡️ 2호가 매수벽]", badges);
            Assert.False(PlutoMicrostructureAnalyzer.Assess(in bull, 0, 9_950).HasMicrostructure);
            Assert.False(PlutoMicrostructureAnalyzer.Assess(in bull, 7, 0).HasMicrostructure);
        }

        [Fact]
        public void Factor_Is_Well_Formed_Across_Random_Streams()
        {
            var rng = new Random(1275);
            var bid = new long[10];
            var ask = new long[10];
            for (int run = 0; run < 40; run++)
            {
                var st = new PlutoMicrostructureState();
                long t = Kst10.Ticks, px = 10_000;
                for (int i = 0; i < 400; i++)
                {
                    t += rng.Next(1, 40) * Sec;
                    px = Math.Max(1, px + rng.Next(-30, 31));
                    if (rng.Next(3) == 0) PlutoMicrostructureAnalyzer.OnTickRuleTrade(ref st, rng.Next(0, 500), px, t);
                    else PlutoMicrostructureAnalyzer.OnTrade(ref st, rng.Next(2) == 0 ? 1 : -1, rng.Next(1, 500), px, t);
                    int n = rng.Next(0, 11);
                    for (int k = 0; k < 10; k++) { bid[k] = rng.Next(0, 4) == 0 ? rng.Next(0, 20_000) : rng.Next(0, 500); ask[k] = rng.Next(0, 4) == 0 ? rng.Next(0, 20_000) : rng.Next(0, 500); }
                    PlutoMicrostructureAnalyzer.OnDepth(ref st, bid.AsSpan(0, n), ask.AsSpan(0, rng.Next(0, 11)));
                    var f = PlutoMicrostructureAnalyzer.Assess(in st, 7, px + rng.Next(-60, 61));
                    Assert.True(f.IsWellFormed, $"{run}/{i} {f.DivergenceType} {f.CvdSlopeBps} {f.PriceChangeBps} {f.CvdScore} {f.DepthFlags} {f.DepthScore}");
                    Assert.InRange(f.MicrostructureScore, -100, 100);
                }
            }
        }

        [Fact]
        public void Devils_Advocate_Penalizes_Bearish_Divergence_And_Depth_Trap()
        {
            var bear = PlutoDevilsAdvocateFilter.Assess(60, 50, 10000, 1000, 1000, 0, 0, cvdBearish: true);
            Assert.Equal((CounterThesis.CvdBearishDivergence, -25, 35, false), (bear.Theses, bear.Penalty, bear.Confidence, bear.Blocked));
            var trap = PlutoDevilsAdvocateFilter.Assess(60, 50, 10000, 1000, 1000, 0, 0, depthTrap: true);
            Assert.Equal((CounterThesis.DepthTrap, -20, 40, false), (trap.Theses, trap.Penalty, trap.Confidence, trap.Blocked));
            var both = PlutoDevilsAdvocateFilter.Assess(100, 50, 10000, 1000, 1000, 0, 0, cvdBearish: true, depthTrap: true);
            Assert.Equal((-45, 2, true), (both.Penalty, both.Count, both.Blocked));       // 독립 반증 2종 = 거부권
            var weak = PlutoDevilsAdvocateFilter.Assess(50, 50, 10000, 1000, 1000, 0, 0, cvdBearish: true);
            Assert.True(weak.Blocked);                                                  // 50 − 25 = 25 < 30
            Assert.Equal(-25, PlutoDevilsAdvocateFilter.PenaltyOf(CounterThesis.CvdBearishDivergence));
            Assert.Equal(-20, PlutoDevilsAdvocateFilter.PenaltyOf(CounterThesis.DepthTrap));
            Assert.Equal(6, PlutoDevilsAdvocateFilter.CountOf((CounterThesis)63));
            Assert.Contains("CVD 약세 다이버전스", PlutoDevilsAdvocateFilter.Label(CounterThesis.CvdBearishDivergence));
        }

        [Fact]
        public void Governor_Tag_Carries_Bearish_Divergence_Penalty_And_Sentence()
        {
            var regime = new PlutoRegimeState();
            for (int i = 0; i < 20; i++) PlutoRegimeClassifier.Update(ref regime, 70_000, 70_100, 1000, 1000);
            Assert.Equal(MarketRegime.ChoppyRange, regime.Regime);
            var r = PlutoCognitiveGovernor.Approve(in regime, 6000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, default, Kst10.Ticks, out var tag, cvdBearish: true);
            Assert.Equal(PlutoBlockReason.None, r);
            Assert.Equal(CounterThesis.CvdBearishDivergence, tag.Theses);
            Assert.Equal(-25, tag.Penalty);
            Assert.Equal(tag.RawConfidence - 25, tag.Confidence);
            Assert.True(tag.IsWellFormed);
            string text = PlutoGovernanceText.Compose(in tag);
            Assert.Contains("CVD 약세 다이버전스", text);
            Assert.Contains("-25점", text);
            var r2 = PlutoCognitiveGovernor.Approve(in regime, 6000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, default, Kst10.Ticks, out _, pocResistance: true, cvdBearish: true);
            Assert.Equal(PlutoBlockReason.CounterThesisVeto, r2);
            var r3 = PlutoCognitiveGovernor.Approve(in regime, 6000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, default, Kst10.Ticks, out var t3, depthTrap: true);
            Assert.Equal((PlutoBlockReason.None, CounterThesis.DepthTrap, -20), (r3, t3.Theses, t3.Penalty));
            Assert.Contains("15호가 매도벽/허매수", PlutoGovernanceText.Compose(in t3));
        }

        // ── ④ 3대 아레나 결속 ───────────────────────────────────────────────────

        [Fact]
        public void Evaluate_Adds_Microstructure_Contribution_And_Picks_Trigger()
        {
            long midFx = 70_050L << 10, volFx = 100L << 10;
            var k0 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx);
            var k1 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx, 0, 0, 0, false, 6000, cvdDivergence: true);
            var k2 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx, 0, 0, 0, false, 6000);
            Assert.Equal(6000 * KrxArenaEngine.MicrostructureWeight / 100, k1.Score - k0.Score);
            Assert.Equal(ArenaTrigger.CvdBullishDivergence, k1.Dominant);
            Assert.Equal(ArenaTrigger.OrderbookDepthSupport, k2.Dominant);
            Assert.Equal(6000, k1.MicrostructureBp);
            var k3 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx, 0, 0, 7000, true, 6000, true);
            Assert.Equal(ArenaTrigger.VolatilitySqueezeBreakout, k3.Dominant);         // 기술적 기여(7000×20) > 미시구조(6000×20)

            var u0 = UsTechArenaEngine.Evaluate(0, 0, 0, 0, 0, 1000, 1000, midFx, midFx);
            var u1 = UsTechArenaEngine.Evaluate(0, 0, 0, 0, 0, 1000, 1000, midFx, midFx, 0, 0, 0, false, 6000, true);
            Assert.Equal(6000 * UsTechArenaEngine.MicrostructureWeight / 100, u1.Score - u0.Score);
            Assert.Equal(ArenaTrigger.CvdBullishDivergence, u1.Dominant);

            long mid = 100_000_000 * S;
            var c0 = UpbitCryptoArenaEngine.Evaluate(0, 0, 0, S, S, mid, mid, 0, 0, 0);
            var c1 = UpbitCryptoArenaEngine.Evaluate(0, 0, 0, S, S, mid, mid, 0, 0, 0, 0, false, 6000);
            Assert.Equal(6000 * UpbitCryptoArenaEngine.MicrostructureWeight / 100, c1.Score - c0.Score);
            Assert.Equal(ArenaTrigger.OrderbookDepthSupport, c1.Dominant);
            var cNeg = UpbitCryptoArenaEngine.Evaluate(0, 0, 0, S, S, mid, mid, 0, 0, 0, 0, false, -2500);
            Assert.Equal(-2500 * UpbitCryptoArenaEngine.MicrostructureWeight / 100, cNeg.Score - c0.Score);
        }

        [Fact]
        public void Rationale_Rejects_Inconsistent_Microstructure_Trigger()
        {
            var st = new PlutoMicrostructureState { Cvd = Bars(6, m => 70_000 - m * 50, 80, 20) };
            var f = PlutoMicrostructureAnalyzer.Assess(in st, Samsung, 69_750);
            Assert.True(f.IsBullishDivergence);
            var ok = new AiTradeRationale(1, Samsung, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.CvdBullishDivergence, 4000, 40,
                72_000, 70_500, 150, 0, 0, 0, 0, 0, microstructure: in f);
            Assert.True(ok.IsWellFormed);
            var depthOk = new AiTradeRationale(1, Samsung, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.OrderbookDepthSupport, 4000, 40,
                72_000, 70_500, 150, 0, 0, 0, 0, 0, microstructure: in f);
            Assert.True(depthOk.IsWellFormed);
            var none = new AiTradeRationale(1, Samsung, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.CvdBullishDivergence, 4000, 40, 72_000, 70_500, 150, 0, 0, 0, 0, 0);
            Assert.False(none.IsWellFormed);
            var other = new AiTradeRationale(1, 660, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.MomentumBreakout, 4000, 40, 72_000, 70_500, 150, 0, 0, 0, 0, 0, microstructure: in f);
            Assert.False(other.IsWellFormed);                                           // 판정 종목 ≠ 체결 종목
            var bearSt = new PlutoMicrostructureState { Cvd = Bars(6, m => 70_000 + m * 50, 20, 80) };
            var bear = PlutoMicrostructureAnalyzer.Assess(in bearSt, Samsung, 70_300);
            var neg = new AiTradeRationale(1, Samsung, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.OrderbookDepthSupport, 4000, 40, 72_000, 70_500, 150, 0, 0, 0, 0, 0, microstructure: in bear);
            Assert.False(neg.IsWellFormed);                                             // 미시구조 점수 음수 = 우위 사유 불가
            Assert.True(ArenaTriggerText.IsEntry(ArenaTrigger.CvdBullishDivergence) && ArenaTriggerText.IsEntry(ArenaTrigger.OrderbookDepthSupport));
            Assert.False(ArenaTriggerText.IsExit(ArenaTrigger.CvdBullishDivergence));
            string text = ok.HumanReadableSummary;
            _out.WriteLine(text);
            Assert.Contains("플루토 CVD 강세 다이버전스", text);
            Assert.Contains(PlutoMicrostructureText.BullishBadge, text);
            Assert.Contains("플루토 미시구조", text);
        }

        /// <summary>
        /// KRX 강세 다이버전스 시나리오(틱 룰): 분마다 중간가 p → p+100(틱 거래량 900 = 매수) → p(100 = 매도) → p−100(100 = 매도) → 다음 분 p−100 시작.
        /// 가격 분당 −100 하락 · CVD 분당 +700 상승 → 12분 후 강세 다이버전스.
        /// </summary>
        private static void KrxBullish(KrxArenaEngine e, out DateTime lastAt, out long lastBid)
        {
            long p = 70_000;
            var at = Kst10;
            for (int m = 0; m < 12; m++)
            {
                at = Kst10.AddMinutes(m);
                e.OnQuote(Samsung, p - 50, p + 50, 3000, 3000, 100, at);
                e.OnQuote(Samsung, p + 50, p + 150, 3000, 3000, 900, at.AddSeconds(10));
                e.OnQuote(Samsung, p - 50, p + 50, 3000, 3000, 100, at.AddSeconds(20));
                e.OnQuote(Samsung, p - 150, p - 50, 3000, 3000, 100, at.AddSeconds(30));
                p -= 100;
            }
            lastBid = p - 50;
            lastAt = Kst10.AddMinutes(12);
            e.OnQuote(Samsung, lastBid, lastBid + 100, 9000, 1000, 100, lastAt);         // 보합(직전 방향 = 매도) · 호가 불균형 +8000bp
        }

        private static AiTradeRationale[] Rationales(IArenaRationaleSource e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        private interface IArenaRationaleSource { int CopyRationales(Span<AiTradeRationale> dst); }

        private sealed class KrxSrc(KrxArenaEngine e) : IArenaRationaleSource { public int CopyRationales(Span<AiTradeRationale> d) => e.CopyRationales(d); }

        [Fact]
        public void Krx_Arena_Tick_Rule_Cvd_Publishes_Bullish_Divergence_And_Binds_Rationale()
        {
            var on = new KrxArenaEngine();
            on.EnableMicrostructure(true);
            Assert.True(on.MicrostructureEnabled);
            var off = new KrxArenaEngine();
            KrxBullish(on, out _, out long lastBid);
            KrxBullish(off, out _, out _);

            Assert.True(on.TryReadMicrostructure(Samsung, out var f));
            _out.WriteLine($"cvd={f.Cvd} slope={f.CvdSlopeBps} dp={f.PriceChangeBps} div={f.DivergenceType} cvdScore={f.CvdScore} depth={f.DepthRatioBps}/{f.DepthTiers} score={f.MicrostructureScore}");
            Assert.True(f.IsWellFormed && f.IsBullishDivergence && f.IsCvdBuyDominant);
            Assert.Equal(1, f.DepthTiers);                                              // KRX 피드 = L1 → 1호가 비례 가중
            Assert.Equal(8000, f.DepthRatioBps);
            Assert.Equal(PlutoDepthFlags.None, f.DepthFlags);                           // 3호가 미만 = 벽 판정 없음(정직)
            Assert.True(off.TryReadMicrostructure(Samsung, out var g));                 // 팩터 게시는 상시(가산만 정지)
            Assert.Equal(f.MicrostructureScore, g.MicrostructureScore);
            Assert.False(on.TryReadMicrostructure(373220, out _));

            var ron = Rationales(new KrxSrc(on));
            var roff = Rationales(new KrxSrc(off));
            Assert.All(roff, r => Assert.False(r.Microstructure.HasMicrostructure));   // 정지 = 근거 미결속
            Assert.All(ron, r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
            var a = Array.Find(ron, r => r.Side == 1 && r.Microstructure.IsBullishDivergence);
            Assert.True(a.Seq > 0, "강세 다이버전스 진입 근거 없음");
            Assert.Equal(lastBid + 100, a.PriceWon);
            string text = a.HumanReadableSummary;
            _out.WriteLine(text);
            Assert.Contains(PlutoMicrostructureText.CvdBuyBadge, text);
            Assert.Contains(PlutoMicrostructureText.BullishBadge, text);
            Assert.Contains("1호가 지수 가중 뎁스", text);
            Assert.Contains("강세 다이버전스(가격 횡보/하락 · CVD 상승)", text);
            Assert.DoesNotContain(roff, r => r.Side == 1 && r.KstTicks == a.KstTicks);  // 정지 엔진 = 같은 틱 미진입(가산분이 진입 문턱 결정)

            on.SetCapital(KrxArenaEngine.DefaultCapitalWon);                            // 초기화 = CVD · 뎁스 · 팩터 소거
            Assert.False(on.TryReadMicrostructure(Samsung, out _));
        }

        [Fact]
        public void Krx_Governed_Bearish_Divergence_Is_Penalized_By_Devils_Advocate()
        {
            var e = new KrxArenaEngine();
            e.EnableMicrostructure(true);
            e.EnableGovernance(true);
            long p = 70_000;
            for (int m = 0; m < 12; m++)                                                 // 분당 +100 상승 · 하락 틱 거래량 900 → CVD 급락
            {
                var at = Kst10.AddMinutes(m);
                e.OnQuote(Samsung, p - 50, p + 50, 3000, 3000, 100, at);
                e.OnQuote(Samsung, p - 150, p - 50, 3000, 3000, 900, at.AddSeconds(10));
                e.OnQuote(Samsung, p - 50, p + 50, 3000, 3000, 100, at.AddSeconds(20));
                e.OnQuote(Samsung, p + 50, p + 150, 3000, 3000, 100, at.AddSeconds(30));
                p += 100;
            }
            Assert.True(e.TryReadMicrostructure(Samsung, out var f));
            _out.WriteLine($"slope={f.CvdSlopeBps} dp={f.PriceChangeBps} div={f.DivergenceType} score={f.MicrostructureScore}");
            Assert.True(f.IsBearishDivergence && f.IsWellFormed);
            Assert.Equal(PlutoCumulativeVolumeDelta.BearishScore, f.CvdScore);
            foreach (var r in Rationales(new KrxSrc(e)))
            {
                Assert.True(r.IsWellFormed, r.HumanReadableSummary);
                if (r.Side == 1 && r.Microstructure.IsBearishDivergence)               // 약세 다이버전스 중 진입 = 반드시 −25 감점 동반
                {
                    Assert.True((r.Governance.Theses & CounterThesis.CvdBearishDivergence) != 0);
                    Assert.Contains("CVD 약세 다이버전스", r.HumanReadableSummary);
                    Assert.Contains(PlutoMicrostructureText.BearishBadge, r.HumanReadableSummary);
                }
            }
        }

        [Fact]
        public void Us_Arena_Uses_Tick_Count_Cvd_And_Publishes_Divergence()
        {
            var e = new UsTechArenaEngine();
            e.EnableMicrostructure(true);
            long p = 50_000;
            for (int m = 0; m < 8; m++)                                                  // 분마다 +1 · +1 · +1 틱 상승 후 −4 = 순 −1 · 틱 카운트 +3 / −1
            {
                var at = KstUsReg.AddMinutes(m);
                for (int q = 0; q < 4; q++)
                {
                    long bid = q < 3 ? p + q + 1 : p - 1;
                    e.OnQuote(Nvda, bid, bid + 1, 3000, 3000, at.AddSeconds(q * 10));
                }
                p -= 1;
            }
            Assert.True(e.TryReadMicrostructure(Nvda, out var f));
            _out.WriteLine($"slope={f.CvdSlopeBps} dp={f.PriceChangeBps} div={f.DivergenceType} score={f.MicrostructureScore}");
            Assert.True(f.IsWellFormed && f.IsBullishDivergence);
            Assert.Equal((1, 0), (f.DepthTiers, f.DepthRatioBps));
            Assert.False(e.TryReadMicrostructure(UsTechUniverse.Pack("ZZZZ"), out _));
        }

        [Fact]
        public void Upbit_Arena_Uses_Real_Taker_Side_For_Cvd()
        {
            var e = new UpbitCryptoArenaEngine();
            e.EnableMicrostructure(true);
            Assert.True(e.MicrostructureEnabled);
            long btc = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
            long b = UpbitTickAligner.AlignNearest(CryptoArenaUniverse.SyntheticStartWon(CryptoArenaUniverse.Btc) * S);
            long t = Sat;
            for (int m = 0; m < 8; m++)                                                  // 가격 분당 +25bp 상승 · 매도 테이커 우위 → 약세 다이버전스
            {
                long bid = UpbitTickAligner.AlignNearest(b + m * (b / 400));
                long ts = t + m * 60L * 1_000_000;
                e.OnQuote(btc, bid, UpbitTickAligner.AlignUp(bid + 1), 10 * S, 10 * S, ts);
                e.OnTrade(btc, bid, S / 10, 1, ts + 1_000_000);
                e.OnTrade(btc, bid, S / 2, -1, ts + 2_000_000);
            }
            Assert.True(e.TryReadMicrostructure(btc, out var f));
            _out.WriteLine($"cvd={f.Cvd} slope={f.CvdSlopeBps} dp={f.PriceChangeBps} div={f.DivergenceType} score={f.MicrostructureScore}");
            Assert.True(f.IsWellFormed && f.IsBearishDivergence);
            Assert.Equal(8 * (S / 10 - S / 2), f.Cvd);
            Assert.Equal((1, 0), (f.DepthTiers, f.DepthRatioBps));
            Assert.False(e.TryReadMicrostructure(999_999, out _));
        }

        // ── ⑤ δ=0 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Analyzer_Settle_And_Lookup_Are_Zero_Alloc()
        {
            var st = new PlutoMicrostructureState();
            var bidD = new long[10];
            var askD = new long[10];
            long t = Kst10.Ticks, acc = 0;
            int k = 0;
            void Analyze(int n)
            {
                for (int i = 0; i < n; i++, k++)
                {
                    t += Sec * 7;
                    long px = 70_000 + ((k >> 4) & 31) * 50 - ((k & 3) * 120);
                    if ((k & 1) == 0) PlutoMicrostructureAnalyzer.OnTrade(ref st, (k & 2) == 0 ? 1 : -1, 100 + (k & 63), px, t);
                    else PlutoMicrostructureAnalyzer.OnTickRuleTrade(ref st, 50 + (k & 31), px, t);
                    for (int j = 0; j < 10; j++) { bidD[j] = 100 + ((k + j * 7) & 255) * ((j == (k & 7)) ? 40 : 1); askD[j] = 100 + ((k * 3 + j) & 511); }
                    PlutoMicrostructureAnalyzer.OnDepth(ref st, bidD, askD);
                    PlutoMicrostructureAnalyzer.OnL1(ref st, bidD[0], askD[0]);
                    PlutoMicrostructureAnalyzer.OnDepth(ref st, bidD, askD);
                    var f = PlutoMicrostructureAnalyzer.Assess(in st, Samsung, px + 30);
                    acc += f.MicrostructureBp + (f.IsBearishDivergence ? 1 : 0) + (f.IsDepthTrap ? 2 : 0) + f.DepthRatioBps;
                }
            }
            Analyze(10_000);
            long analyzeBytes = MinAllocated(() => Analyze(100_000));

            var e = new KrxArenaEngine();
            e.EnableMicrostructure(true);
            e.EnableTechnical(true);
            e.EnableGovernance(true);
            var kt = Kst10;
            int j2 = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, j2++)
                {
                    kt = kt.AddTicks(Sec * 2);
                    if (kt >= Kst10.AddHours(5)) kt = Kst10;
                    long px = 68_000 + ((j2 >> 6) & 63) * 100 - (j2 & 1) * 100;
                    e.OnQuote(Samsung + (j2 & 3), px, px + 100, 3000 + (j2 & 1023) * 10, 3000, 100 + (j2 & 63) * ((j2 & 1) + 1), kt);
                    if ((j2 & 15) == 0) e.SubmitHuman(Samsung, (j2 & 16) == 0 ? 1 : -1, 1);
                }
            }
            Settle(20_000);
            long settleBytes = MinAllocated(() => Settle(100_000));

            var c = new UpbitCryptoArenaEngine();
            c.EnableMicrostructure(true);
            c.EnableGovernance(true);
            long btc = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
            long cb = UpbitTickAligner.AlignNearest(CryptoArenaUniverse.SyntheticStartWon(CryptoArenaUniverse.Btc) * S);
            long cu = UpbitTickAligner.TickFor(cb);
            long ct = Sat;
            int j3 = 0;
            void SettleCrypto(int n)
            {
                for (int i = 0; i < n; i++, j3++)
                {
                    ct += 3_000_000;
                    long bid = UpbitTickAligner.AlignNearest(cb + (((j3 >> 5) & 31) - 16) * cu);
                    c.OnTrade(btc, bid, S / 100 * (1 + (j3 & 7)), (j3 & 3) == 0 ? -1 : 1, ct);
                    c.OnQuote(btc, bid, UpbitTickAligner.AlignUp(bid + 1), (10 + (j3 & 15)) * S, 10 * S, ct);
                }
            }
            SettleCrypto(20_000);
            long cryptoBytes = MinAllocated(() => SettleCrypto(100_000));

            long hits = 0;
            void Lookups(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    if (e.TryReadMicrostructure(Samsung + (i & 3), out var f)) hits += f.MicrostructureScore + 1;
                    if (c.TryReadMicrostructure(btc, out var g)) hits += g.CvdSlopeBps + 1;
                }
            }
            Lookups(10_000);
            long lookupBytes = MinAllocated(() => Lookups(100_000));

            Assert.True(e.TryReadSnapshot(out var snap));
            _out.WriteLine($"analyze δ={analyzeBytes}B acc={acc} · krx settle δ={settleBytes}B ticks={snap.Ticks} aiFills={snap.Ai.Fills} · " +
                           $"upbit settle δ={cryptoBytes}B · lookup δ={lookupBytes}B hits={hits}");
            Assert.Equal(0, analyzeBytes);
            Assert.Equal(0, settleBytes);
            Assert.Equal(0, cryptoBytes);
            Assert.Equal(0, lookupBytes);
            Assert.True(acc != 0 && hits != 0);
            var rs = Rationales(new KrxSrc(e));
            Assert.Contains(rs, r => r.Microstructure.HasMicrostructure);
            Assert.All(rs, r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
        }
    }
}
