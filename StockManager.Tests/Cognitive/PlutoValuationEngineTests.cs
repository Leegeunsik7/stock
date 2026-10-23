// ============================================================================
// PlutoValuationEngineTests — v12.18.65-DEV: 플루토 브레인 2단계(재무 밸류에이션 · 안전마진 · seqlock 버퍼 · 아레나 핫패스 결속)
//
// §1 시드 — 국내 5 · 미국 5 프로필 무결 · 적정가/안전마진/배수/등급/점수(독립 오라클 산출값) · 시장 통화 단위
// §2 매트릭스 — 등급 경계(±30% · +10% · −15% · −30%) · 구간 연속 · 단조 · DeepValue 자격 강등 · 적자 상한 · 밴드 이탈 역성장 버블 · 서프라이즈 가감
// §3 프로필 · 공식 — 무결성 거부 · 적자 PBR 단독 · 안전마진 = (적정가 − 현재가)/현재가
// §4 태그 · 버퍼 · 엔진 — 호가 재판정 · 만료/미래 fail-closed · 위조 거부 · Stale · 포화 · Upsert/Revalue
// §5 아레나 결속 — KRX/US ValuationDiscount 진입 · ValuationBubble 청산 · 고평가/과열 진입 보류 · 근거 요약 재무 문장 · 시그널 합성
// §6 δ=0 · 동시성 — 10만 회 조회 0B · 엔진 정산 10만 회 0B · IL 할당 지점 0 · 4 작성자 × 2 판독자 찢김 0
// ============================================================================
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.News;
using StockManager.Engines.Cognitive.Valuation;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoValuationEngineTests
    {
        private const long Samsung = 5930, Hynix = 660, Lges = 373220;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);            // 목 10:00 KST(KRX 정규장)
        private static readonly DateTime KstUsReg = new(2026, 10, 22, 23, 0, 0);         // 목 10:00 EDT(미국 정규장)
        private static long Utc(DateTime kst) => kst.Ticks - PlutoClock.KstOffsetTicks;
        private static readonly long Nvda = UsTechUniverse.Pack("NVDA"), Tsla = UsTechUniverse.Pack("TSLA");
        private readonly ITestOutputHelper _out;

        public PlutoValuationEngineTests(ITestOutputHelper o) => _out = o;

        private static PlutoFundamentalProfile Seed(long symbol) => PlutoFundamentalSeeds.Create().Single(p => p.Symbol == symbol);

        private static PlutoFundamentalProfile P(long symbol = 1, long epsTtm = 900, long fwdEps = 1000, long bps = 10_000, int roeBps = 1000,
            int perLo = 50, int perMid = 100, int perHi = 200, int pbrLo = 50, int pbrMid = 100, int pbrHi = 200, int sectorPer = 150,
            int perWeight = 60, long consensus = 0, long actual = 0, long asOf = 0, PlutoMarket market = PlutoMarket.Krx)
            => new(symbol, market, PlutoFundamentalSource.Manual, asOf == 0 ? PlutoFundamentalSeeds.AsOfUtcTicks : asOf, epsTtm, fwdEps, bps, roeBps,
                perLo, perMid, perHi, pbrLo, pbrMid, pbrHi, sectorPer, perWeight, consensus, actual, 0);

        /// <summary>중립 판정(Fwd PER 10.0배 &lt; 업종 15.0배 · ROE 양수 · 밴드 상단 20.0배 · 성장 양수 · 서프라이즈 0) — 안전마진만 변수.</summary>
        private static PlutoValuationReading AtMos(int mosBps, long fwdEps = 1000, int roeBps = 1000, int sectorPer = 150, int perHi = 200,
            int growthBps = 1000, int surpriseBps = 0)
            => PlutoValuationMath.Assess(10_000 + mosBps, 10_000, fwdEps, 10_000, roeBps, sectorPer, perHi, growthBps, surpriseBps);

        // ── §1 시드 ────────────────────────────────────────────────────────────

        [Theory]
        //           종목      적정가    등급                              점수  MoS(bp) FwdPER×10 PBR×100 성장bp 서프bp
        [InlineData("005930", 88_824, PlutoValuationGrade.FairDiscount, 69, 2689, 92, 114, 3571, 1200)]
        [InlineData("000660", 220_400, PlutoValuationGrade.FairDiscount, 56, 2244, 72, 163, 3888, 600)]
        [InlineData("373220", 276_000, PlutoValuationGrade.Overvalued, -56, -2736, 950, 345, 1428, -444)]
        [InlineData("207940", 876_000, PlutoValuationGrade.FairlyValued, 19, 950, 533, 571, 2500, 250)]
        [InlineData("005380", 264_000, PlutoValuationGrade.FairDiscount, 48, 2000, 45, 57, 666, 400)]
        [InlineData("AAPL", 19_760, PlutoValuationGrade.FairlyValued, -13, -1217, 296, 5000, 1176, 206)]
        [InlineData("NVDA", 15_750, PlutoValuationGrade.FairDiscount, 45, 1666, 300, 7500, 5000, 909)]
        [InlineData("MSFT", 43_500, PlutoValuationGrade.FairlyValued, 10, 357, 289, 1000, 1153, 406)]
        [InlineData("AMZN", 20_460, PlutoValuationGrade.FairlyValued, 19, 768, 306, 791, 1272, 500)]
        [InlineData("TSLA", 17_500, PlutoValuationGrade.ExtremeBubble, -70, -3000, 1000, 1086, -1935, -1724)]
        public void Seed_Profiles_Evaluate_To_Oracle_Values(string key, long fair, PlutoValuationGrade grade, int score, int mos, int perX10, int pbrX100,
            int growthBps, int surpriseBps)
        {
            long symbol = char.IsDigit(key[0]) ? long.Parse(key) : UsTechUniverse.Pack(key);
            var p = Seed(symbol);
            Assert.True(p.IsWellFormed);
            Assert.Equal(PlutoFundamentalSource.Seed, p.Source);
            Assert.Equal(char.IsDigit(key[0]) ? PlutoMarket.Krx : PlutoMarket.Us, p.Market);
            Assert.Equal(PlutoFundamentalSeeds.AsOfUtcTicks, p.AsOfUtcTicks);
            Assert.Equal(growthBps, p.EpsGrowthBps);
            Assert.Equal(surpriseBps, p.SurpriseBps);
            Assert.Equal(fair, PlutoValuationMath.FairPrice(in p));
            Assert.True(PlutoValuationMath.TryEvaluate(in p, p.ReferencePrice, out var f));
            Assert.True(f.IsWellFormed);
            Assert.Equal((symbol, fair, p.ReferencePrice), (f.Symbol, f.FairPrice, f.EvaluatedPrice));
            Assert.Equal((grade, score, mos, perX10, pbrX100), (f.Grade, f.ValuationScore, f.MarginOfSafetyBps, f.FwdPerX10, f.PbrX100));
            Assert.Equal(p.RoeBps, f.RoeBps);
            Assert.True(ValuationText.InBand(f.Grade, f.ValuationScore));
        }

        [Fact]
        public void Seeds_Cover_Arena_Universes_Without_Duplicates()
        {
            var seeds = PlutoFundamentalSeeds.Create();
            Assert.Equal(10, seeds.Length);
            Assert.Equal(10, seeds.Select(s => s.Symbol).Distinct().Count());
            foreach (var k in KrxArenaQuoteFeeder.SyntheticSymbols) Assert.Contains(seeds, s => s.Symbol == k && s.Market == PlutoMarket.Krx);
            for (int i = 0; i < UsTechUniverse.Count; i++) Assert.Contains(seeds, s => s.Symbol == UsTechUniverse.Symbol(i) && s.Market == PlutoMarket.Us);
            Assert.All(seeds, s => Assert.Equal(PlutoFundamentalSource.Seed, s.Source));
            Assert.NotSame(seeds, PlutoFundamentalSeeds.Create());                 // 호출마다 새 배열(공유 가변 상태 0)
            Assert.Contains("시드 프로필", ValuationText.SourceLabel(PlutoFundamentalSource.Seed));
        }

        // ── §2 매트릭스 ────────────────────────────────────────────────────────

        [Theory]
        [InlineData(9000, PlutoValuationGrade.DeepValue, 100)]
        [InlineData(6000, PlutoValuationGrade.DeepValue, 100)]
        [InlineData(4500, PlutoValuationGrade.DeepValue, 85)]
        [InlineData(3000, PlutoValuationGrade.DeepValue, 70)]
        [InlineData(2999, PlutoValuationGrade.FairDiscount, 69)]
        [InlineData(1000, PlutoValuationGrade.FairDiscount, 20)]
        [InlineData(999, PlutoValuationGrade.FairlyValued, 19)]
        [InlineData(0, PlutoValuationGrade.FairlyValued, 0)]
        [InlineData(-1499, PlutoValuationGrade.FairlyValued, -19)]
        [InlineData(-1500, PlutoValuationGrade.Overvalued, -20)]
        [InlineData(-2000, PlutoValuationGrade.Overvalued, -33)]
        [InlineData(-2999, PlutoValuationGrade.Overvalued, -59)]
        [InlineData(-3000, PlutoValuationGrade.ExtremeBubble, -60)]
        [InlineData(-6000, PlutoValuationGrade.ExtremeBubble, -100)]
        [InlineData(-9000, PlutoValuationGrade.ExtremeBubble, -100)]
        public void Grade_Boundaries_And_Score_Interpolation(int mos, PlutoValuationGrade grade, int score)
        {
            var r = AtMos(mos);
            Assert.Equal((grade, score, mos, 100), (r.Grade, r.Score, r.MarginOfSafetyBps, r.FwdPerX10));
            Assert.Equal(100, r.PbrX100);
        }

        [Fact]
        public void Score_Is_Monotone_In_Margin_And_Always_In_Grade_Band()
        {
            int prev = int.MinValue;
            var prevGrade = PlutoValuationGrade.ExtremeBubble;
            for (int mos = -9_900; mos <= 20_000; mos += 7)
            {
                var r = AtMos(mos);
                Assert.True(ValuationText.InBand(r.Grade, r.Score), $"mos={mos} {r.Grade} {r.Score}");
                Assert.True(r.Score >= prev, $"단조 위반 mos={mos} {r.Score} < {prev}");
                Assert.True(r.Grade <= prevGrade, $"등급 역행 mos={mos}");
                prev = r.Score; prevGrade = r.Grade;
            }
            Assert.Equal(-100, AtMos(-9_900).Score);
            Assert.Equal(100, AtMos(20_000).Score);
        }

        [Fact]
        public void Overrides_Demote_DeepValue_Cap_Losses_And_Promote_Band_Breach_Bubble()
        {
            // DeepValue 자격: Fwd PER < 업종 ∧ ROE > 0 — 미달 = FairDiscount 상단으로 포화
            Assert.Equal((PlutoValuationGrade.DeepValue, 90), (AtMos(5000).Grade, AtMos(5000).Score));
            var perAtSector = AtMos(5000, sectorPer: 100);
            Assert.Equal((PlutoValuationGrade.FairDiscount, 69), (perAtSector.Grade, perAtSector.Score));
            var noRoe = AtMos(5000, roeBps: 0);
            Assert.Equal((PlutoValuationGrade.FairDiscount, 69), (noRoe.Grade, noRoe.Score));
            // 선행 적자 — 할인 판정 상한 = FairlyValued(+19) · PER 산출 불가(0)
            foreach (long eps in new long[] { 0, -100 })
            {
                var loss = AtMos(5000, fwdEps: eps);
                Assert.Equal((PlutoValuationGrade.FairlyValued, 19, 0), (loss.Grade, loss.Score, loss.FwdPerX10));
            }
            Assert.Equal(PlutoValuationGrade.Overvalued, AtMos(-2000, fwdEps: 0).Grade);   // 적자 고평가는 그대로(완화 0)
            // 밴드 상단 이탈(PER 10.0 > 9.0) ∧ 역성장 = 극심한 과열(안전마진 양수여도)
            var breach = AtMos(500, perHi: 90, growthBps: -1);
            Assert.Equal((PlutoValuationGrade.ExtremeBubble, -60), (breach.Grade, breach.Score));
            Assert.Equal(PlutoValuationGrade.FairlyValued, AtMos(500, perHi: 90, growthBps: 0).Grade);      // 성장 0 = 승격 없음
            Assert.Equal(PlutoValuationGrade.FairlyValued, AtMos(500, perHi: 100, growthBps: -1).Grade);    // 상단 = 이탈 아님
        }

        [Fact]
        public void Consensus_Surprise_Adjusts_Within_Band_Only()
        {
            Assert.Equal(44, AtMos(2000).Score);
            Assert.Equal(54, AtMos(2000, surpriseBps: 1200).Score);       // +12% → +10 상한
            Assert.Equal(49, AtMos(2000, surpriseBps: 550).Score);        // +5.5% → +5
            Assert.Equal(34, AtMos(2000, surpriseBps: -2500).Score);      // −25% → −10 하한
            Assert.Equal(69, AtMos(2999, surpriseBps: 5000).Score);       // 등급 밴드 상단 포화(등급 불변)
            Assert.Equal(PlutoValuationGrade.FairDiscount, AtMos(2999, surpriseBps: 5000).Grade);
            Assert.Equal(-60, AtMos(-3000, surpriseBps: 5000).Score);     // 버블은 서프라이즈로 탈출 불가
            Assert.Equal(-100, AtMos(-6000, surpriseBps: -5000).Score);
        }

        // ── §3 프로필 · 공식 ───────────────────────────────────────────────────

        [Fact]
        public void Margin_Of_Safety_Is_Fair_Minus_Price_Over_Price()
        {
            Assert.Equal(2571, PlutoValuationMath.MarginOfSafetyBps(88_000, 70_000));    // 18,000/70,000 = 25.714…% → 0 방향 절단
            Assert.Equal(-2222, PlutoValuationMath.MarginOfSafetyBps(70_000, 90_000));
            Assert.Equal(0, PlutoValuationMath.MarginOfSafetyBps(70_000, 70_000));
            Assert.Equal(1_000_000, PlutoValuationMath.MarginOfSafetyBps(PlutoFundamentalProfile.MaxPerShare, 1));   // 포화
            Assert.Equal(0, PlutoValuationMath.MarginOfSafetyBps(0, 70_000));
            Assert.Equal(0, PlutoValuationMath.MarginOfSafetyBps(70_000, 0));
            Assert.Equal(0, PlutoValuationMath.MarginOfSafetyBps(70_000, PlutoFundamentalProfile.MaxPerShare + 1));
            Assert.Equal(default(PlutoValuationReading).Grade, PlutoValuationMath.Assess(0, 10_000, 1000, 10_000, 1000, 150, 200, 0, 0).Grade);
            Assert.Equal(PlutoValuationGrade.Unknown, PlutoValuationMath.Assess(10_000, 0, 1000, 10_000, 1000, 150, 200, 0, 0).Grade);
        }

        [Fact]
        public void Fair_Price_Blends_Per_And_Pbr_And_Falls_Back_To_Pbr_On_Losses()
        {
            Assert.Equal(10_000, PlutoValuationMath.FairPrice(P()));                                         // 1000×10 · 10000×1 → 10000
            Assert.Equal(13_000, PlutoValuationMath.FairPrice(P(fwdEps: 1500)));                              // 15000×0.6 + 10000×0.4
            Assert.Equal(15_000, PlutoValuationMath.FairPrice(P(fwdEps: 1500, perWeight: 100)));
            Assert.Equal(10_000, PlutoValuationMath.FairPrice(P(fwdEps: 1500, perWeight: 0)));
            Assert.Equal(10_000, PlutoValuationMath.FairPrice(P(fwdEps: -500)));                              // 적자 = PBR 단독
            Assert.Equal(0, PlutoValuationMath.FairPrice(P(fwdEps: -500, perWeight: 100)));                   // 산출 불가
            var bad = new[]
            {
                P(symbol: 0), P(perLo: 300), P(perHi: 90), P(pbrMid: 0), P(sectorPer: 0), P(perWeight: 101), P(bps: 0),
                P(fwdEps: -500, perWeight: 100), P(market: PlutoMarket.Unknown), P(fwdEps: PlutoFundamentalProfile.MaxPerShare + 1),
                new PlutoFundamentalProfile(1, PlutoMarket.Krx, PlutoFundamentalSource.Manual, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 50, 0, 0, 0),
            };
            foreach (var b in bad)
            {
                Assert.False(b.IsWellFormed);
                Assert.Equal(0, PlutoValuationMath.FairPrice(in b));
                Assert.False(PlutoValuationMath.TryEvaluate(in b, 10_000, out var f));
                Assert.Equal(0, f.Symbol);
            }
            Assert.True(P(bps: 0, perWeight: 100).IsWellFormed);                                              // PER 단독은 BPS 불요
            Assert.False(PlutoValuationMath.TryEvaluate(P(), 0, out _));
            Assert.False(PlutoValuationMath.TryEvaluate(P(), PlutoFundamentalProfile.MaxPerShare + 1, out _));
        }

        // ── §4 태그 · 버퍼 · 엔진 ──────────────────────────────────────────────

        [Fact]
        public void Tag_Regrades_At_Live_Price_And_Fails_Closed_On_Stale_Or_Future_Data()
        {
            Assert.True(PlutoValuationMath.TryEvaluate(Seed(Samsung), 70_000, out var f));
            long asOf = PlutoFundamentalSeeds.AsOfUtcTicks;
            var live = new PlutoValuationTag(in f, 60_100, asOf + TimeSpan.FromDays(21).Ticks);
            Assert.True(live.HasValuation && live.IsWellFormed);
            Assert.Equal((PlutoValuationGrade.DeepValue, 97, 4779, 79, 98), (live.Grade, live.Score, live.MarginOfSafetyBps, live.Reading.FwdPerX10, live.Reading.PbrX100));
            Assert.Equal(9700, live.ValuationBp);
            Assert.False(live.BlocksEntry || live.IsBubble);
            var hot = new PlutoValuationTag(in f, 130_100, asOf);
            Assert.Equal((PlutoValuationGrade.ExtremeBubble, -60, -3172), (hot.Grade, hot.Score, hot.MarginOfSafetyBps));
            Assert.True(hot.BlocksEntry && hot.IsBubble && hot.IsWellFormed);
            var rich = new PlutoValuationTag(in f, 110_000, asOf);
            Assert.Equal(PlutoValuationGrade.Overvalued, rich.Grade);
            Assert.True(rich.BlocksEntry && !rich.IsBubble);

            Assert.True(new PlutoValuationTag(in f, 70_000, asOf + TimeSpan.FromDays(180).Ticks).HasValuation);
            Assert.True(new PlutoValuationTag(in f, 70_000, asOf - TimeSpan.FromHours(23).Ticks).HasValuation);
            foreach (long now in new[] { asOf + TimeSpan.FromDays(180).Ticks + 1, asOf - TimeSpan.FromDays(1).Ticks - 1, 0L, -1L })
            {
                var t = new PlutoValuationTag(in f, 70_000, now);
                Assert.False(t.HasValuation);
                Assert.True(t.IsWellFormed);
                Assert.Equal(0, t.ValuationBp);
                Assert.False(t.BlocksEntry || t.IsBubble);
            }
            Assert.False(new PlutoValuationTag(in f, 0, asOf).HasValuation);
            Assert.False(new PlutoValuationTag(default, 70_000, asOf).HasValuation);
            PlutoValuationTag none = default;
            Assert.True(none.IsWellFormed);
        }

        [Fact]
        public void Buffer_Rejects_Forged_Factors_Keeps_Newer_Data_And_Saturates()
        {
            var b = new PlutoValuationBuffer();
            var p = Seed(Samsung);
            Assert.True(PlutoValuationMath.TryEvaluate(in p, 70_000, out var f));
            // 위조: 점수가 저장 판정과 불일치(밴드 안이지만 재판정 불일치)
            var forged = new PlutoValuationFactor(in p, f.FairPrice, 70_000, new PlutoValuationReading(PlutoValuationGrade.FairDiscount, 50, f.MarginOfSafetyBps, f.FwdPerX10, f.PbrX100));
            Assert.False(forged.IsWellFormed);
            Assert.Equal(PlutoValuationPublishResult.Rejected, b.Publish(in forged));
            var outOfBand = new PlutoValuationFactor(in p, f.FairPrice, 70_000, new PlutoValuationReading(PlutoValuationGrade.DeepValue, 69, f.MarginOfSafetyBps, f.FwdPerX10, f.PbrX100));
            Assert.Equal(PlutoValuationPublishResult.Rejected, b.Publish(in outOfBand));
            Assert.Equal(PlutoValuationPublishResult.Rejected, b.Publish(default));
            Assert.Equal(0, b.Count);

            Assert.Equal(PlutoValuationPublishResult.Published, b.Publish(in f));
            Assert.True(b.TryRead(Samsung, out var got));
            Assert.Equal((1L, f.FairPrice, f.ValuationScore), (got.Seq, got.FairPrice, got.ValuationScore));
            // 같은 기준일 = 최신 가격 판정이 교체 · 과거 기준일 = Stale
            Assert.True(PlutoValuationMath.TryEvaluate(in p, 60_000, out var f2));
            Assert.Equal(PlutoValuationPublishResult.Published, b.Publish(in f2));
            var older = P(symbol: Samsung, asOf: PlutoFundamentalSeeds.AsOfUtcTicks - TimeSpan.TicksPerDay);
            Assert.True(PlutoValuationMath.TryEvaluate(in older, 60_000, out var f3));
            Assert.Equal(PlutoValuationPublishResult.Stale, b.Publish(in f3));
            Assert.True(b.TryRead(Samsung, out got));
            Assert.Equal((2L, 60_000L), (got.Seq, got.EvaluatedPrice));
            Assert.Equal((2L, 1L, 3L), (b.Published, b.StaleCount, b.RejectedCount));
            Assert.False(b.TryRead(Hynix, out _));
            Assert.False(b.TryRead(0, out _));

            var full = new PlutoValuationBuffer();
            int published = 0, rejectedFull = 0;
            for (long sym = 1; sym <= PlutoValuationBuffer.Capacity + 5; sym++)
            {
                Assert.True(PlutoValuationMath.TryEvaluate(P(symbol: sym), 10_000, out var fs));
                var r = full.Publish(in fs);
                if (r == PlutoValuationPublishResult.Published) published++;
                else if (r == PlutoValuationPublishResult.BufferFull) rejectedFull++;
            }
            Assert.Equal((PlutoValuationBuffer.Capacity, 5, 5L), (published, rejectedFull, full.FullCount));
            for (long sym = 1; sym <= PlutoValuationBuffer.Capacity; sym++) Assert.True(full.TryRead(sym, out _));
        }

        [Fact]
        public void Engine_Upsert_Revalue_Seed_And_Failure_Statuses()
        {
            Assert.Throws<ArgumentNullException>(() => new PlutoValuationEngine(null!));
            var b = new PlutoValuationBuffer();
            var e = new PlutoValuationEngine(b);
            Assert.Same(b, e.Buffer);
            Assert.Equal(10, e.SeedDefaults());
            Assert.Equal(10, e.ProfileCount);
            Assert.Equal(10, b.Count);
            Assert.True(b.TryRead(Tsla, out var t));
            Assert.Equal(PlutoValuationGrade.ExtremeBubble, t.Grade);
            Assert.Equal(PlutoMarket.Us, t.Market);
            Assert.True(e.TryGetProfile(Nvda, out var nv) && nv.FwdEps == 450);

            Assert.Equal(PlutoValuationStatus.Published, e.Revalue(Samsung, 60_000));
            Assert.True(b.TryRead(Samsung, out var s));
            Assert.Equal((60_000L, PlutoValuationGrade.DeepValue), (s.EvaluatedPrice, s.Grade));
            Assert.Equal(PlutoValuationStatus.NoProfile, e.Revalue(999_999, 10_000));
            Assert.Equal(PlutoValuationStatus.InvalidPrice, e.Revalue(Samsung, 0));
            Assert.Equal(PlutoValuationStatus.InvalidProfile, e.Upsert(P(perLo: 300), 10_000));
            Assert.Equal(10, e.ProfileCount);                                                                 // 무효 프로필 = 저장 0
            Assert.Equal(PlutoValuationStatus.Stale, e.Upsert(P(symbol: Samsung, asOf: PlutoFundamentalSeeds.AsOfUtcTicks - 1), 60_000));
            Assert.Equal(4, e.Failures);
            Assert.True(e.TryGetProfile(Samsung, out var kept) && kept.Source == PlutoFundamentalSource.Seed);   // 과거 재무가 보관 프로필을 덮지 않음
            Assert.Equal(PlutoValuationStatus.Published, e.Revalue(Samsung, 61_000));
            Assert.Equal(PlutoValuationStatus.Published, e.Upsert(P(symbol: 42), 10_000));
            Assert.Equal(11, e.ProfileCount);
        }

        // ── §5 아레나 결속 ─────────────────────────────────────────────────────

        private static void KrxQ(KrxArenaEngine e, long sym, ref DateTime t, long bid, long ask, long bq, long aq, long vol = 100)
        {
            t = t.AddSeconds(1);
            e.OnQuote(sym, bid, ask, bq, aq, vol, t);
        }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        private static AiTradeRationale[] Rationales(UsTechArenaEngine e)
        {
            var buf = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        private static PlutoValuationBuffer SeededBuffer()
        {
            var b = new PlutoValuationBuffer();
            Assert.Equal(10, new PlutoValuationEngine(b).SeedDefaults());
            return b;
        }

        [Fact]
        public void Krx_Deep_Value_Drives_Entry_And_Bubble_Forces_Early_Exit()
        {
            int Run(bool attach, out KrxArenaEngine engine, out DateTime clock)
            {
                var e = new KrxArenaEngine();
                if (attach) e.AttachValuation(SeededBuffer());
                e.SetOvernightMacro(500);
                var t = Kst10;
                for (int i = 0; i < KrxArenaEngine.WarmupTicks; i++) KrxQ(e, Samsung, ref t, 60_000, 60_100, 5000, 5000);
                KrxQ(e, Samsung, ref t, 60_000, 60_100, 6500, 3500);         // 매크로 100 + 불균형 1050 + 밸류 9700×20/100 = 3090
                engine = e; clock = t;
                return Rationales(e).Length;
            }
            Assert.Equal(0, Run(attach: false, out _, out _));                // 밸류에이션 없으면 1150 < 3000
            Assert.Equal(1, Run(attach: true, out var e, out var t));
            Assert.True(e.ValuationAttached);
            Assert.Throws<ArgumentNullException>(() => e.AttachValuation(null!));
            var entry = Rationales(e)[0];
            Assert.Equal(ArenaTrigger.ValuationDiscount, entry.TriggerType);
            Assert.Equal(3090, entry.SentimentScore);
            Assert.True(entry.IsWellFormed, entry.HumanReadableSummary);
            Assert.Equal((PlutoValuationGrade.DeepValue, 97, 60_100L), (entry.Valuation.Grade, entry.Valuation.Score, entry.Valuation.Price));
            Assert.False(entry.News.HasCatalyst);
            string s = entry.HumanReadableSummary;
            _out.WriteLine(s);
            Assert.Contains("주도 근거: 플루토 밸류에이션 저평가", s);
            Assert.Contains("플루토 밸류에이션: 극심한 저평가 +97점", s);
            Assert.Contains("Fwd PER 7.9배(업종 평균 14.1배 대비 43% 할인)", s);
            Assert.Contains("PBR 0.98배", s);
            Assert.Contains("ROE +12.4%", s);
            Assert.Contains("안전마진 +47.7%(적정가 88,824원)", s);
            Assert.Contains("분기 영업이익 컨센서스 +12.0% 상회", s);
            Assert.Contains("선행 EPS +35.7%", s);
            Assert.Contains("시드 프로필(실 재무 피드 미결선)", s);

            // 극심한 과열(안전마진 −31.7%) → 목표가 도달보다 우선해 조기 청산
            KrxQ(e, Samsung, ref t, 130_000, 130_100, 5000, 5000);
            var rs = Rationales(e);
            Assert.Equal(2, rs.Length);
            var exit = rs[1];
            Assert.Equal((ArenaTrigger.ValuationBubble, -1, 60), (exit.TriggerType, exit.Side, exit.ConfidenceScore));
            Assert.True(exit.Valuation.IsBubble);
            Assert.True(exit.IsWellFormed, exit.HumanReadableSummary);
            Assert.Contains("청산 사유: 플루토 극심한 과열 조기 청산", exit.HumanReadableSummary);
            Assert.Contains("극심한 과열 -60점", exit.HumanReadableSummary);
            Assert.True(exit.RealizedPnlWon > 0);

            // 과열 동안 강한 수급에도 재진입 0
            for (int i = 0; i < 5; i++) KrxQ(e, Samsung, ref t, 130_000, 130_100, 19_000, 1000, 2000);
            Assert.Equal(2, Rationales(e).Length);
        }

        [Fact]
        public void Krx_Overvalued_Blocks_Entry_That_Signal_Alone_Would_Fire()
        {
            long Run(bool attach)
            {
                var e = new KrxArenaEngine();
                if (attach) e.AttachValuation(SeededBuffer());
                e.SetOvernightMacro(10000);
                var t = Kst10;
                for (int i = 0; i < KrxArenaEngine.WarmupTicks; i++) KrxQ(e, Lges, ref t, 380_000, 380_500, 5000, 5000);
                KrxQ(e, Lges, ref t, 382_000, 382_500, 19_000, 1000, 1000);   // 매크로 + 불균형 + 거래량 급증 + 모멘텀
                return e.TryReadSnapshot(out var s) ? s.RationaleCount : -1;
            }
            Assert.Equal(1, Run(attach: false));
            Assert.Equal(0, Run(attach: true));                              // LG에너지솔루션 시드 = 고평가 경계(−57) → 신규 매수 보류
            // 보류는 점수 감산 때문이 아니다 — 같은 입력에 밸류 −5700bp 를 합성해도 진입 임계 이상
            var sig = KrxArenaEngine.Evaluate(10000, 19_000, 1000, 1000L << 10, 100L << 10, 382_250L << 10, 380_250L << 10, 0, -5700);
            Assert.True(sig.Score >= KrxArenaEngine.EntryScore, sig.Score.ToString());
            Assert.Equal(-5700, sig.ValuationBp);
        }

        [Fact]
        public void Us_Deep_Value_Entry_And_Bubble_Ticker_Never_Bought()
        {
            var e = new UsTechArenaEngine();
            e.AttachValuation(SeededBuffer());
            Assert.True(e.ValuationAttached);
            e.SetMarketContext(100, 0, 0);
            var t = KstUsReg;
            void Q(long sym, long bid, long bq, long aq) { t = t.AddSeconds(1); e.OnQuote(sym, bid, bid + 1, bq, aq, t); }
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks; i++) Q(Nvda, 10_000, 5000, 5000);
            Q(Nvda, 10_000, 8000, 2000);                                     // 갭/수급 900 + ETF 325 + 밸류 100×100×20/100 = 3225
            var entry = Assert.Single(Rationales(e));
            Assert.Equal(ArenaTrigger.ValuationDiscount, entry.TriggerType);
            Assert.Equal(ArenaCurrency.Usd, entry.Currency);
            Assert.True(entry.IsWellFormed, entry.HumanReadableSummary);
            string s = entry.HumanReadableSummary;
            _out.WriteLine(s);
            Assert.Contains("극심한 저평가 +100점", s);
            Assert.Contains("Fwd PER 22.2배(업종 평균 28.0배 대비 20% 할인)", s);
            Assert.Contains("적정가 $157.50", s);
            Assert.Contains("안전마진 +57.4%", s);

            // TSLA 시드 = 극심한 과열(밴드 상단 이탈 ∧ 역성장) — 최강 시장 맥락에서도 매수 0
            e.SetMarketContext(10000, 10000, -10000);
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks + 5; i++) Q(Tsla, 25_000 + i * 50, 9000, 1000);
            Assert.DoesNotContain(Rationales(e), r => r.Symbol == Tsla);
        }

        [Fact]
        public void Triggers_Composite_Signal_And_Default_Valuation_Preserve_Prior_Behaviour()
        {
            Assert.True(ArenaTriggerText.IsEntry(ArenaTrigger.ValuationDiscount));
            Assert.False(ArenaTriggerText.IsExit(ArenaTrigger.ValuationDiscount));
            Assert.True(ArenaTriggerText.IsExit(ArenaTrigger.ValuationBubble));
            Assert.False(ArenaTriggerText.IsEntry(ArenaTrigger.ValuationBubble));
            Assert.NotEqual("미분류", ArenaTriggerText.Label(ArenaTrigger.ValuationDiscount));
            Assert.NotEqual("미분류", ArenaTriggerText.Label(ArenaTrigger.ValuationBubble));
            // 밸류 0 = 시그널 동일(기존 동작 보존) · 가중 20 · ±10000 포화 · 지배 근거
            var k0 = KrxArenaEngine.Evaluate(500, 6500, 3500, 100 << 10, 100 << 10, 70_050 << 10, 70_000 << 10, 1000);
            var k1 = KrxArenaEngine.Evaluate(500, 6500, 3500, 100 << 10, 100 << 10, 70_050 << 10, 70_000 << 10, 1000, 0);
            Assert.Equal((k0.Score, k0.Dominant), (k1.Score, k1.Dominant));
            var kv = KrxArenaEngine.Evaluate(500, 6500, 3500, 100 << 10, 100 << 10, 70_050 << 10, 70_000 << 10, 1000, 8000);
            Assert.Equal(k0.Score + 8000 * KrxArenaEngine.ValuationWeight / 100, kv.Score);
            Assert.Equal(ArenaTrigger.ValuationDiscount, kv.Dominant);
            Assert.Equal(10000, KrxArenaEngine.Evaluate(10000, 10000, 0, 1000 << 10, 100 << 10, 71_000 << 10, 70_000 << 10, 10000, 99_999).Score);
            var u0 = UsTechArenaEngine.Evaluate(0, 0, 0, 0, 0, 8000, 2000, 13_000 << 10, 13_000 << 10);
            var uv = UsTechArenaEngine.Evaluate(0, 0, 0, 0, 0, 8000, 2000, 13_000 << 10, 13_000 << 10, 0, 10000);
            Assert.Equal(u0.Score + 2000, uv.Score);
            Assert.Equal(ArenaTrigger.ValuationDiscount, uv.Dominant);

            // 근거 무결성: ValuationDiscount 는 양수 점수 · ValuationBubble 은 과열 · 판정 종목 = 체결 종목
            Assert.True(PlutoValuationMath.TryEvaluate(Seed(Samsung), 70_000, out var f));
            var tag = new PlutoValuationTag(in f, 70_100, Utc(Kst10));
            var ok = new AiTradeRationale(1, Samsung, 1, 10, 70_100, Kst10.Ticks, ArenaTrigger.ValuationDiscount, 3000, 30, 71_000, 69_500, 150, 0, 0, 0, 0, 0,
                ArenaCurrency.Krw, default, in tag);
            Assert.True(ok.IsWellFormed);
            var badBubble = new AiTradeRationale(2, Samsung, -1, 10, 70_000, Kst10.Ticks, ArenaTrigger.ValuationBubble, -3000, 60, 71_000, 69_500, 150, 0, 0, 0, 0, 0,
                ArenaCurrency.Krw, default, in tag);
            Assert.False(badBubble.IsWellFormed);
            var wrongSymbol = new AiTradeRationale(3, Hynix, 1, 10, 70_100, Kst10.Ticks, ArenaTrigger.OrderbookImbalance, 3000, 30, 71_000, 69_500, 150, 0, 0, 0, 0, 0,
                ArenaCurrency.Krw, default, in tag);
            Assert.False(wrongSymbol.IsWellFormed);
            var noVal = new AiTradeRationale(4, Samsung, 1, 10, 70_100, Kst10.Ticks, ArenaTrigger.ValuationDiscount, 3000, 30, 71_000, 69_500, 150, 0, 0, 0, 0, 0);
            Assert.False(noVal.IsWellFormed);
            Assert.DoesNotContain("플루토 밸류에이션:", noVal.HumanReadableSummary);           // 판정 없음 = 재무 근거 문장 0(사유 라벨만)
        }

        // ── §6 δ=0 · 동시성 ────────────────────────────────────────────────────

        [Fact]
        public void Valuation_Lookup_And_Engine_Settlement_100k_Allocate_Zero_Bytes()
        {
            long t0 = Utc(Kst10);
            var b = SeededBuffer();
            long[] syms = { Samsung, Hynix, 5380, Lges };
            long acc = 0;
            void Lookups(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    long sym = syms[i & 3];
                    if (b.TryRead(sym, out var f))
                    {
                        var tag = new PlutoValuationTag(in f, f.EvaluatedPrice * (60 + (i & 127)) / 100, t0 + (i & 4095) * TimeSpan.TicksPerSecond);
                        acc += tag.ValuationBp + (tag.BlocksEntry ? 1 : 0) + (tag.IsBubble ? 2 : 0);
                    }
                }
            }
            Lookups(10_000);
            long lookupBytes = MinAllocated(() => Lookups(100_000));

            var e = new KrxArenaEngine();
            e.AttachValuation(b);
            var news = new PlutoSentimentBuffer();
            e.AttachPluto(news);
            news.Publish(new PlutoSentimentFactor(Hynix, t0, 35, 70, CatalystCategory.MomentumPositive, PlutoSource.Wire, "목표가 상향"u8), t0);
            var t = Kst10;
            int k = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, k++)
                {
                    t = t.AddTicks(TimeSpan.TicksPerMillisecond * 50);
                    if (t >= Kst10.AddMinutes(5)) t = Kst10;
                    long sym = syms[k & 3];
                    long px = 68_000 + ((k >> 6) & 63) * 100;                 // 톱니 68,000~74,300 → 익절 · 손절 · 반전 청산 유발
                    e.OnQuote(sym, px, px + 100, 3000 + (k & 1023) * 10, 3000, 100 + (k & 63), t);
                    if ((k & 15) == 0) e.SubmitHuman(sym, (k & 16) == 0 ? 1 : -1, 1);
                }
            }
            Settle(20_000);
            Assert.True(e.TryReadSnapshot(out var s0));
            long settleBytes = MinAllocated(() => Settle(100_000));
            Assert.True(e.TryReadSnapshot(out var s));
            _out.WriteLine($"lookup δ={lookupBytes}B acc={acc} · settle δ={settleBytes}B ticks={s.Ticks} aiFills={s.Ai.Fills} rationales={s.RationaleCount}");
            Assert.Equal(0, lookupBytes);
            Assert.Equal(0, settleBytes);
            Assert.True(acc != 0);
            Assert.True(s.Ai.Fills - s0.Ai.Fills >= 10, $"측정 구간에서 밸류에이션 결속 AI 가상 체결이 반복돼야 한다({s.Ai.Fills - s0.Ai.Fills})");
            var rs = Rationales(e);
            Assert.Contains(rs, r => r.Valuation.HasValuation);
            Assert.All(rs, r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
        }

        [Fact]
        public void HotPath_Methods_Have_No_Il_Allocation_Sites()
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var targets = new (Type t, string[] names)[]
            {
                (typeof(PlutoValuationBuffer), new[] { "Publish", "TryRead", "Lock", "Home", "Claim", "Find" }),
                (typeof(PlutoValuationFactor), new[] { "ReadAt", "WithSeq", "get_IsWellFormed" }),
                (typeof(PlutoValuationTag), new[] { "get_HasValuation", "get_ValuationBp", "get_BlocksEntry", "get_IsBubble", "get_IsWellFormed" }),
                (typeof(PlutoValuationMath), new[] { "Assess", "RawScore", "MarginOfSafetyBps", "FairPrice" }),
                (typeof(PlutoFundamentalProfile), new[] { "get_IsWellFormed", "get_EpsGrowthBps", "get_SurpriseBps" }),
                (typeof(ValuationText), new[] { "Band", "InBand" }),
            };
            foreach (var (t, names) in targets)
                foreach (var name in names)
                {
                    var m = t.GetMethod(name, all);
                    Assert.True(m is not null, $"{t.Name}.{name}");
                    var sites = ZeroAllocIlInspector.Inspect(m!);
                    Assert.True(sites.Count == 0, $"{t.Name}.{name}\n" + string.Join("\n", sites));
                }
            foreach (var ctor in typeof(PlutoValuationTag).GetConstructors()
                         .Concat<MethodBase>(typeof(PlutoValuationFactor).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                         .Concat(typeof(PlutoValuationReading).GetConstructors()))
                Assert.Empty(ZeroAllocIlInspector.Inspect(ctor));
        }

        [Fact]
        public void Concurrent_Writers_And_Readers_Never_Observe_Torn_Factors()
        {
            var b = new PlutoValuationBuffer();
            const int Writers = 4, PerWriter = 50_000, Readers = 2, Symbols = 8;
            // 종목마다 서로 다른 재무(EPS = 100×종목) — 찢긴 판독은 종목/EPS/적정가/재판정 일치 검사에 걸린다.
            var profiles = Enumerable.Range(1, Symbols).Select(i => P(symbol: i, epsTtm: 90L * i, fwdEps: 100L * i, bps: 1000L * i)).ToArray();
            var factors = profiles.Select(p => Enumerable.Range(0, 64).Select(j =>
            {
                Assert.True(PlutoValuationMath.TryEvaluate(in p, p.FwdEps * (40 + j * 3) / 10, out var f));
                return f;
            }).ToArray()).ToArray();
            int done = 0;
            long torn = 0, reads = 0;
            var writers = Enumerable.Range(0, Writers).Select(w => new Thread(() =>
            {
                var rnd = new Random(w + 17);
                for (int i = 0; i < PerWriter; i++)
                {
                    var arr = factors[rnd.Next(Symbols)];
                    b.Publish(in arr[rnd.Next(arr.Length)]);
                }
                Interlocked.Increment(ref done);
            })).ToArray();
            var readers = Enumerable.Range(0, Readers).Select(_ => new Thread(() =>
            {
                while (Volatile.Read(ref done) < Writers)
                    for (long sym = 1; sym <= Symbols; sym++)
                    {
                        if (!b.TryRead(sym, out var f)) continue;
                        Interlocked.Increment(ref reads);
                        if (f.Symbol != sym || f.FwdEps != 100 * sym || f.Bps != 1000 * sym || f.FairPrice != PlutoValuationMath.FairPrice(in profiles[sym - 1])
                            || !f.IsWellFormed)
                            Interlocked.Increment(ref torn);
                    }
            })).ToArray();
            foreach (var r in readers) r.Start();
            foreach (var w in writers) w.Start();
            foreach (var w in writers) w.Join();
            foreach (var r in readers) r.Join();
            _out.WriteLine($"reads={reads} published={b.Published} stale={b.StaleCount} contended={b.ContendedReads} lastSeq={b.LastSeq}");
            Assert.Equal(0, torn);
            Assert.True(reads > 0);
            Assert.Equal(Writers * PerWriter, b.Published);               // 같은 기준일 = 전량 교체 경로
            Assert.Equal(0, b.StaleCount);
            Assert.Equal(b.Published, b.LastSeq);
            Assert.Equal(Symbols, b.Count);
        }
    }
}
