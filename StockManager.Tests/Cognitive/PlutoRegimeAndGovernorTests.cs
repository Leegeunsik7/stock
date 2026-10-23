// ============================================================================
// PlutoRegimeAndGovernorTests — v12.18.67-DEV: 플루토 브레인 고도화(국면 판정 · 악마의 변호인 · 동적 사이징 · 자가 회고 · 3대 아레나 결속)
//
//  §1 국면 — 워밍업 Unknown · 상승/하락/박스/패닉 판정 · 패닉 즉시 진입(fail-closed) · 박스권 무플래핑 · 급등 = 과열 지표(RSI · 이격도) · 재가중 산식
//  §2 악마의 변호인 — 반대 논리 3종 경계값 · 감점(−35/−20/−15) · 확신도 하한 차단 · 2종 이상 거부권 · 거버너 차단 순서(점수 → 패닉 → 쿨다운 → 거부권)
//  §3 동적 사이징 — 5%~25% 확신도 선형 · ATR 역비례 · 연패 ≥ 2 = 50% 축소 · 15분 쿨다운 경계 · 이익 = 연패 리셋 · 본전 = 손실
//  §4 자가 회고 — 교훈 우선순위 · 메모 없음 = 회고 없음 · 무결성 · 한국어 문장
//  §5 엔진 결속 — KRX 패닉 반등: 미가동 쌍둥이는 진입 · 가동 엔진은 패닉 차단 · KRX 손절 회고 1:1(진입 순번) + 연패 2회 → 쿨다운 차단 → 15분 뒤 50% 축소 진입 ·
//     US/Crypto 가동 진입 근거 무결 · 미가동 = 거버넌스 태그 default(기존 동작)
//  §6 δ=0 — 국면/거버너/사이저/회고 10만 회 루프 0B · KRX/Crypto 가동 엔진 정산 10만 틱 0B(ZeroAllocProbe)
// ============================================================================
using System;
using System.Linq;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Regime;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoRegimeAndGovernorTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);   // 목요일 10:00 KST
        private readonly ITestOutputHelper _out;
        private DateTime _t = Open10;

        public PlutoRegimeAndGovernorTests(ITestOutputHelper o) => _out = o;

        // ── 헬퍼 ────────────────────────────────────────────────────────────────

        private static MarketRegime Feed(ref PlutoRegimeState s, long bid, long bidQty = 1000, long askQty = 1000) =>
            PlutoRegimeClassifier.Update(ref s, bid, bid + 10, bidQty, askQty);

        /// <summary>틱마다 stepBp 복리 이동(n틱) 후 국면.</summary>
        private static MarketRegime Trend(ref PlutoRegimeState s, double stepBp, int n, double start = 100_000)
        {
            double px = start;
            var r = MarketRegime.Unknown;
            for (int i = 0; i < n; i++)
            {
                px *= 1 + stepBp / 10000;
                r = Feed(ref s, (long)px);
            }
            return r;
        }

        private void Q(KrxArenaEngine e, long bid, long ask, long bidQty = 1000, long askQty = 1000, long vol = 100)
        {
            _t = _t.AddMilliseconds(100);
            e.OnQuote(Samsung, bid, ask, bidQty, askQty, vol, _t);
        }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        private static PlutoGovernorStats Gov(KrxArenaEngine e)
        {
            Assert.True(e.TryReadGovernor(out var g));
            return g;
        }

        private static PlutoRegimeState StateOf(MarketRegime r, int atrCbp = 0) => new() { Regime = r, AtrX16 = atrCbp << 4, Seen = 100 };

        // ── §1 국면 ─────────────────────────────────────────────────────────────

        [Fact]
        public void Regime_Unknown_During_Warmup_Then_Classified()
        {
            var s = new PlutoRegimeState();
            for (int i = 0; i < PlutoRegimeClassifier.WarmupTicks - 1; i++) Assert.Equal(MarketRegime.Unknown, Feed(ref s, 100_000));
            Assert.Equal(MarketRegime.ChoppyRange, Feed(ref s, 100_000));
            Assert.Equal(MarketRegime.ChoppyRange, Feed(ref s, 0));          // 무효 호가 = 상태 불변
            Assert.Equal(MarketRegime.ChoppyRange, PlutoRegimeClassifier.Update(ref s, 100_000, 99_000, 1, 1));   // 역전 호가
        }

        [Fact]
        public void Regime_Classifies_Bull_Bear_Range()
        {
            var bull = new PlutoRegimeState();
            Assert.Equal(MarketRegime.TrendingBull, Trend(ref bull, +10, 40));
            Assert.True(bull.SpreadBp >= PlutoRegimeClassifier.TrendSpreadBp);

            var bear = new PlutoRegimeState();
            Assert.Equal(MarketRegime.TrendingBear, Trend(ref bear, -10, 40));
            Assert.True(bear.SpreadBp <= -PlutoRegimeClassifier.TrendSpreadBp);

            var range = new PlutoRegimeState();
            int flips = 0;
            var prev = MarketRegime.Unknown;
            for (int i = 0; i < 200; i++)
            {
                var r = Feed(ref range, i % 2 == 0 ? 99_950 : 100_050);
                if (i >= PlutoRegimeClassifier.WarmupTicks && r != prev) flips++;
                prev = r;
            }
            Assert.Equal(MarketRegime.ChoppyRange, prev);
            Assert.True(flips <= 1, $"박스권 플래핑 {flips}");
            _out.WriteLine($"bull spread {bull.SpreadBp}bp · bear {bear.SpreadBp}bp · range RSI {range.Rsi}");
        }

        [Fact]
        public void Regime_Bull_To_Panic_Is_Immediate_And_Bear_Transition_Is_Confirmed()
        {
            var s = new PlutoRegimeState();
            Assert.Equal(MarketRegime.TrendingBull, Trend(ref s, +10, 40));
            long px = s.LastMid;
            var seen = new MarketRegime[3];
            for (int i = 0; i < 3; i++) { px = px * 97 / 100; seen[i] = Feed(ref s, px, 100, 9900); }   // −3% × 3 · 매도 잔량 99%
            _out.WriteLine($"crash: {string.Join(",", seen)} ATR {s.AtrBp}bp OBI {s.ObiEma}");
            Assert.Equal(MarketRegime.PanicLiquidation, seen[2]);
            Assert.True(MarketRegimeText.BlocksEntry(s.Regime));

            // 상승 → 하락 전이: 같은 후보 3틱 확인 전엔 국면 유지(히스테리시스)
            var t = new PlutoRegimeState();
            Trend(ref t, +10, 40);
            double p = t.LastMid;
            int switchedAt = -1, candidateAt = -1;
            for (int i = 0; i < 60 && switchedAt < 0; i++)
            {
                p *= 1 - 10 / 10000.0;
                var r = Feed(ref t, (long)p);
                if (candidateAt < 0 && t.Candidate != MarketRegime.TrendingBull && t.CandidateRun > 0) candidateAt = i;
                if (r != MarketRegime.TrendingBull) switchedAt = i;
            }
            Assert.True(candidateAt >= 0 && switchedAt >= candidateAt + PlutoRegimeClassifier.ConfirmTicks - 1, $"후보 {candidateAt} · 전이 {switchedAt}");
            Assert.NotEqual(MarketRegime.PanicLiquidation, t.Regime);
        }

        [Fact]
        public void Regime_Steep_Rally_Exposes_Overheat_Indicators()
        {
            var s = new PlutoRegimeState();
            Assert.Equal(MarketRegime.TrendingBull, Trend(ref s, +60, 60));
            _out.WriteLine($"RSI {s.Rsi} · 이격도 {s.DisparityX100} · ATR {s.AtrBp}bp");
            Assert.True(PlutoDevilsAdvocateFilter.IsOverheated(s.Rsi, s.DisparityX100));
        }

        [Fact]
        public void Reweight_Applies_Regime_Factor_Profile()
        {
            // 엔진 기여 = bp × 가중/100. 상승: 모멘텀 130% → 8000×25×30/10000 = +600
            Assert.Equal(4600, PlutoRegimeClassifier.Reweight(4000, 8000, 25, 5000, 35, MarketRegime.TrendingBull));
            // 박스: 모멘텀 50%(−1000) · 불균형 130%(+525)
            Assert.Equal(3525, PlutoRegimeClassifier.Reweight(4000, 8000, 25, 5000, 35, MarketRegime.ChoppyRange));
            // 하락: 모멘텀 130%(+600) · 불균형 80%(−350)
            Assert.Equal(4250, PlutoRegimeClassifier.Reweight(4000, 8000, 25, 5000, 35, MarketRegime.TrendingBear));
            Assert.Equal(4000, PlutoRegimeClassifier.Reweight(4000, 8000, 25, 5000, 35, MarketRegime.Unknown));
            Assert.Equal(10000, PlutoRegimeClassifier.Reweight(9900, 10000, 25, 0, 35, MarketRegime.PanicLiquidation));
            Assert.Equal(-10000, PlutoRegimeClassifier.Reweight(-9900, -10000, 25, 0, 35, MarketRegime.PanicLiquidation));
        }

        // ── §2 악마의 변호인 ─────────────────────────────────────────────────────

        [Fact]
        public void DevilsAdvocate_Thesis_Boundaries()
        {
            Assert.False(PlutoDevilsAdvocateFilter.IsOverheated(70, 10900));
            Assert.False(PlutoDevilsAdvocateFilter.IsOverheated(71, 10800));
            Assert.True(PlutoDevilsAdvocateFilter.IsOverheated(71, 10801));
            Assert.True(PlutoDevilsAdvocateFilter.IsSupplyWall(100, 300));
            Assert.False(PlutoDevilsAdvocateFilter.IsSupplyWall(100, 299));
            Assert.True(PlutoDevilsAdvocateFilter.IsSupplyWall(0, 1));
            Assert.False(PlutoDevilsAdvocateFilter.IsSupplyWall(0, 0));
            Assert.False(PlutoDevilsAdvocateFilter.IsSupplyWall(-1, 10));
            Assert.True(PlutoDevilsAdvocateFilter.IsSupplyWall(long.MaxValue / 3, long.MaxValue));    // 곱셈 오버플로 0
            Assert.True(PlutoDevilsAdvocateFilter.IsMacroHeadwind(10, 0));
            Assert.False(PlutoDevilsAdvocateFilter.IsMacroHeadwind(9, 49));
            Assert.True(PlutoDevilsAdvocateFilter.IsMacroHeadwind(-50, 50));
        }

        [Fact]
        public void DevilsAdvocate_Penalizes_And_Blocks_Overheated_Entry()
        {
            var clean = PlutoDevilsAdvocateFilter.Assess(60, 55, 10100, 1000, 1000, 0, 0);
            Assert.Equal((CounterThesis.None, 0, 60, false), (clean.Theses, clean.Penalty, clean.Confidence, clean.Blocked));

            var hot = PlutoDevilsAdvocateFilter.Assess(60, 85, 11000, 1000, 1000, 0, 0);       // 과열 −35 → 25 < 30 = 차단
            Assert.Equal((CounterThesis.Overheat, -35, 25, true), (hot.Theses, hot.Penalty, hot.Confidence, hot.Blocked));

            var strong = PlutoDevilsAdvocateFilter.Assess(80, 85, 11000, 1000, 1000, 0, 0);    // 과열이지만 원확신도 80 → 45 통과(감점 기록)
            Assert.Equal((-35, 45, false), (strong.Penalty, strong.Confidence, strong.Blocked));

            var wall = PlutoDevilsAdvocateFilter.Assess(50, 50, 10000, 100, 500, 0, 0);
            Assert.Equal((CounterThesis.OverheadSupplyWall, -20, 30, false), (wall.Theses, wall.Penalty, wall.Confidence, wall.Blocked));
            var macro = PlutoDevilsAdvocateFilter.Assess(44, 50, 10000, 100, 100, 0, 60);
            Assert.Equal((CounterThesis.MacroHeadwind, -15, 29, true), (macro.Theses, macro.Penalty, macro.Confidence, macro.Blocked));

            var veto = PlutoDevilsAdvocateFilter.Assess(100, 50, 10000, 100, 500, 25, 0);      // 2종 → 100−35 = 65 이어도 거부권
            Assert.Equal((CounterThesis.OverheadSupplyWall | CounterThesis.MacroHeadwind, -35, 65, true), (veto.Theses, veto.Penalty, veto.Confidence, veto.Blocked));
            var all = PlutoDevilsAdvocateFilter.Assess(100, 90, 12000, 0, 10, 20, 80);
            Assert.Equal((3, -70, 30, true), (all.Count, all.Penalty, all.Confidence, all.Blocked));
            Assert.Equal(0, PlutoDevilsAdvocateFilter.Assess(10, 90, 12000, 1, 1, 0, 0).Confidence);   // 0 하한
        }

        [Fact]
        public void Governor_Block_Order_Panic_Cooldown_Veto()
        {
            var cool = default(PlutoSizerState);
            long now = Open10.Ticks;
            Assert.Equal(PlutoBlockReason.PanicRegime, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.PanicLiquidation), 10000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in cool, now, out var t0));
            Assert.False(t0.Governed);
            Assert.Equal(PlutoBlockReason.BelowEntryScore, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.PanicLiquidation), 2999, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in cool, now, out _));
            // 박스권 재가중으로 점수 미달(모멘텀 50%): 3200 − 8000×25×50/10000 = 2200
            Assert.Equal(PlutoBlockReason.BelowEntryScore, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.ChoppyRange), 3200, 8000, 25, 0, 35, 3000, 1000, 1000, 0, 0, in cool, now, out _));

            var losing = default(PlutoSizerState);
            losing.OnClose(-1, now); losing.OnClose(-1, now);
            Assert.Equal(PlutoBlockReason.Cooldown, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.TrendingBull), 6000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, in losing, now + 1, out _));
            Assert.Equal(PlutoBlockReason.CounterThesisVeto, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.TrendingBull), 6000, 0, 25, 0, 35, 3000, 100, 900, 30, 0, in cool, now, out _));

            Assert.Equal(PlutoBlockReason.None, PlutoCognitiveGovernor.Approve(StateOf(MarketRegime.TrendingBull), 6000, 0, 25, 0, 35, 3000, 100, 900, 0, 0, in cool, now, out var ok));
            Assert.True(ok.Governed && ok.IsWellFormed && ok.IsEntry);
            Assert.Equal((MarketRegime.TrendingBull, CounterThesis.OverheadSupplyWall, -20, 60, 40), (ok.Regime, ok.Theses, ok.Penalty, ok.RawConfidence, ok.Confidence));
            Assert.Equal(PlutoDynamicSizer.WeightBp(40, 0, 0), ok.WeightBp);
            string text = PlutoGovernanceText.Compose(in ok);
            _out.WriteLine(text);
            Assert.Contains("[국면 BULL]", text);
            Assert.Contains("상단 매도 호가벽 -20점", text);
            Assert.Contains("확신도 60→40/100", text);
        }

        // ── §3 동적 사이징 ───────────────────────────────────────────────────────

        [Fact]
        public void Sizer_Scales_With_Confidence_And_Atr_And_Cuts_On_Streak()
        {
            Assert.Equal(500, PlutoDynamicSizer.WeightBp(30, 0, 0));
            Assert.Equal(500, PlutoDynamicSizer.WeightBp(0, 0, 0));
            Assert.Equal(1500, PlutoDynamicSizer.WeightBp(65, 0, 0));          // 500 + 2000 × 35/70
            Assert.Equal(2500, PlutoDynamicSizer.WeightBp(100, 2000, 0));      // ATR = 목표 → 배율 100%
            Assert.Equal(2500, PlutoDynamicSizer.WeightBp(150, 0, 0));
            Assert.Equal(1500, PlutoDynamicSizer.WeightBp(100, 4000, 0));      // ATR 2배 → 변동 구간 50%
            Assert.Equal(500, PlutoDynamicSizer.WeightBp(100, int.MaxValue, 0));
            Assert.Equal(2500, PlutoDynamicSizer.WeightBp(100, 0, 1));
            Assert.Equal(1250, PlutoDynamicSizer.WeightBp(100, 0, 2));         // 연패 2 → 50%
            Assert.Equal(250, PlutoDynamicSizer.WeightBp(30, 0, 5));
            for (int c = 0; c <= 100; c++)
                for (int atr = 0; atr <= 20_000; atr += 997)
                {
                    int w = PlutoDynamicSizer.WeightBp(c, atr, 0);
                    Assert.InRange(w, PlutoDynamicSizer.MinWeightBp, PlutoDynamicSizer.MaxWeightBp);
                    Assert.Equal(w * 50 / 100, PlutoDynamicSizer.WeightBp(c, atr, 2));
                    if (c > 0) Assert.True(w >= PlutoDynamicSizer.WeightBp(c - 1, atr, 0));
                }
        }

        [Fact]
        public void Sizer_Streak_Cooldown_Boundaries()
        {
            var s = default(PlutoSizerState);
            long t0 = Open10.Ticks;
            s.OnClose(-100, t0);
            Assert.Equal((1, false, false), (s.LossStreak, s.StreakCut, s.InCooldown(t0)));
            s.OnClose(0, t0 + 10);                                             // 본전 = 손실(엔진 승패 집계 기준)
            Assert.Equal((2, true), (s.LossStreak, s.StreakCut));
            long until = t0 + 10 + PlutoDynamicSizer.CooldownTicks;
            Assert.Equal(until, s.CooldownUntilTicks);
            Assert.True(s.InCooldown(until - 1));
            Assert.False(s.InCooldown(until));
            s.OnClose(-1, t0 + 20);                                            // 쿨다운 중 추가 손실 = 재기산
            Assert.Equal(t0 + 20 + PlutoDynamicSizer.CooldownTicks, s.CooldownUntilTicks);
            s.OnClose(+1, t0 + 30);
            Assert.Equal((0, false, 4L, 3L), (s.LossStreak, s.StreakCut, s.Closes, s.LossCloses));
        }

        // ── §4 자가 회고 ─────────────────────────────────────────────────────────

        private static PlutoEntryMemo Memo(MarketRegime regime, CounterThesis theses = CounterThesis.None) => new()
        {
            EntrySeq = 7, EntryTicks = Open10.Ticks, EntryCost = 10_000_000, Regime = regime, Theses = theses, Confidence = 50, WeightBp = 1000,
            Penalty = PlutoDevilsAdvocateFilter.PenaltyOf(theses),
        };

        [Theory]
        [InlineData(MarketRegime.TrendingBull, CounterThesis.None, ArenaTrigger.TakeProfit, MarketRegime.TrendingBull, 50_000, 0, PlutoLesson.TrendFollow)]
        [InlineData(MarketRegime.ChoppyRange, CounterThesis.OverheadSupplyWall, ArenaTrigger.TakeProfit, MarketRegime.ChoppyRange, 50_000, 0, PlutoLesson.ContrarianWin)]
        [InlineData(MarketRegime.ChoppyRange, CounterThesis.None, ArenaTrigger.SignalReversal, MarketRegime.ChoppyRange, 1, 0, PlutoLesson.DisciplinedExit)]
        [InlineData(MarketRegime.TrendingBull, CounterThesis.Overheat, ArenaTrigger.StopLoss, MarketRegime.TrendingBear, -80_000, 0, PlutoLesson.RespectCounterThesis)]
        [InlineData(MarketRegime.TrendingBull, CounterThesis.None, ArenaTrigger.StopLoss, MarketRegime.TrendingBear, -80_000, 0, PlutoLesson.RegimeShiftExit)]
        [InlineData(MarketRegime.ChoppyRange, CounterThesis.None, ArenaTrigger.StopLoss, MarketRegime.ChoppyRange, -80_000, 5000, PlutoLesson.VolatilityStop)]
        [InlineData(MarketRegime.ChoppyRange, CounterThesis.Overheat, ArenaTrigger.NewsShock, MarketRegime.ChoppyRange, -80_000, 0, PlutoLesson.ExogenousShock)]
        [InlineData(MarketRegime.ChoppyRange, CounterThesis.None, ArenaTrigger.SessionClose, MarketRegime.ChoppyRange, 0, 0, PlutoLesson.SessionTimebox)]
        [InlineData(MarketRegime.ChoppyRange, CounterThesis.None, ArenaTrigger.SignalReversal, MarketRegime.ChoppyRange, 0, 0, PlutoLesson.SignalDecay)]
        public void PostMortem_Lesson_Priority(MarketRegime entry, CounterThesis theses, ArenaTrigger exit, MarketRegime exitRegime, long pnl, int atrCbp, PlutoLesson lesson)
        {
            var memo = Memo(entry, theses);
            var pm = PlutoPostMortemReviewer.Review(in memo, exit, exitRegime, pnl, true, Open10.Ticks + 5 * TimeSpan.TicksPerMinute, atrCbp);
            Assert.True(pm.IsWellFormed);
            Assert.Equal(lesson, pm.Lesson);
            Assert.Equal(pnl > 0 ? PostMortemVerdict.Win : PostMortemVerdict.Loss, pm.Verdict);
            Assert.Equal((7L, 5 * TimeSpan.TicksPerMinute, (int)(pnl * 10000 / 10_000_000)), (pm.EntrySeq, pm.HoldTicks, pm.ReturnBp));
            string text = PlutoPostMortemText.Compose(in pm);
            _out.WriteLine(text);
            Assert.Contains("플루토 자가 회고", text);
            Assert.Contains(PlutoPostMortemText.LessonText(lesson), text);
        }

        [Fact]
        public void PostMortem_Requires_Memo_And_Exit_Trigger()
        {
            var none = default(PlutoEntryMemo);
            Assert.False(PlutoPostMortemReviewer.Review(in none, ArenaTrigger.StopLoss, MarketRegime.ChoppyRange, -1, true, 1, 0).HasReview);
            var memo = Memo(MarketRegime.TrendingBull);
            Assert.False(PlutoPostMortemReviewer.Review(in memo, ArenaTrigger.VolumeSurge, MarketRegime.ChoppyRange, -1, true, 1, 0).HasReview);
            Assert.True(default(PlutoPostMortem).IsWellFormed);
            Assert.True(default(PlutoGovernanceTag).IsWellFormed);
            var partial = PlutoPostMortemReviewer.Review(in memo, ArenaTrigger.StopLoss, MarketRegime.TrendingBull, -1, false, Open10.Ticks, 0);
            Assert.False(partial.IsFinal);
            Assert.Contains("부분 청산 중간 회고", PlutoPostMortemText.Compose(in partial));
        }

        // ── §5 엔진 결속 ─────────────────────────────────────────────────────────

        /// <summary>평온 10틱 → −3% × 3 투매(매도 잔량 99%) → 침체 5틱 → 반등 틱(거시 +10000 · 매수 잔량 99%).</summary>
        private void PanicBounce(KrxArenaEngine e, bool assertPanic)
        {
            e.SetOvernightMacro(0);
            for (int i = 0; i < 10; i++) Q(e, 100_000, 100_100);
            Q(e, 97_000, 97_100, 100, 9900); Q(e, 94_100, 94_200, 100, 9900); Q(e, 91_300, 91_400, 100, 9900);
            if (assertPanic) Assert.Equal(MarketRegime.PanicLiquidation, e.RegimeOf(Samsung));
            for (int i = 0; i < 5; i++) Q(e, 91_300, 91_400, 100, 9900);
            e.SetOvernightMacro(10000);
            Q(e, 91_300, 91_400, 9900, 100);
        }

        [Fact]
        public void Krx_Panic_Regime_Blocks_Entry_That_Ungoverned_Twin_Takes()
        {
            var plain = new KrxArenaEngine();
            _t = Open10;
            PanicBounce(plain, assertPanic: false);
            var entry = Assert.Single(Rationales(plain));                     // 미가동 = 기존 동작(반등 매수)
            Assert.Equal(1, entry.Side);
            Assert.False(entry.Governance.Governed);
            Assert.False(plain.GovernanceEnabled);

            var gov = new KrxArenaEngine();
            gov.EnableGovernance(true);
            _t = Open10;
            PanicBounce(gov, assertPanic: true);
            Assert.Equal(MarketRegime.PanicLiquidation, gov.RegimeOf(Samsung));
            Assert.Empty(Rationales(gov));
            var g = Gov(gov);
            Assert.True(g.Enabled);
            Assert.Equal((1L, 0L), (g.PanicBlocks, g.Approved));
            Assert.True(gov.TryReadSnapshot(out var snap));
            Assert.Equal(0, snap.Ai.OpenPositions);
            Assert.Equal(MarketRegime.Unknown, gov.RegimeOf(999_999));
        }

        /// <summary>진입(Spike) → 손절 → 침체 n틱. 반환 = (진입, 청산) 근거.</summary>
        private (AiTradeRationale Entry, AiTradeRationale Exit) LosingRoundTrip(KrxArenaEngine e, long bid, int macro)
        {
            int before = Rationales(e).Length;
            e.SetOvernightMacro(macro);
            Q(e, bid, bid + 100, 9000, 1000, before == 0 ? 1000 : 100);
            var rs = Rationales(e);
            Assert.Equal(before + 1, rs.Length);
            var entry = rs[^1];
            Assert.Equal(1, entry.Side);
            long crash = entry.StopLossPrice - 600;
            Q(e, crash, crash + 100);
            rs = Rationales(e);
            Assert.Equal(before + 2, rs.Length);
            var exit = rs[^1];
            Assert.Equal(ArenaTrigger.StopLoss, exit.TriggerType);
            for (int i = 0; i < 10; i++) Q(e, crash, crash + 100);
            return (entry, exit);
        }

        [Fact]
        public void Krx_StopLoss_PostMortem_Binds_One_To_One_And_Streak_Cools_Down_Then_Halves()
        {
            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, 70_000, 70_100);

            var (en1, ex1) = LosingRoundTrip(e, 70_100, 3000);
            foreach (var r in new[] { en1, ex1 })
            {
                _out.WriteLine(r.HumanReadableSummary);
                Assert.True(r.IsWellFormed);
                Assert.True(r.Governance.Governed);
            }
            Assert.True(en1.Governance.IsEntry);
            Assert.Equal(en1.ConfidenceScore, en1.Governance.Confidence);
            Assert.InRange(en1.Governance.WeightBp, PlutoDynamicSizer.MinWeightBp, PlutoDynamicSizer.MaxWeightBp);
            var pm1 = ex1.Governance.PostMortem;
            Assert.True(pm1.HasReview && pm1.IsFinal);
            Assert.Equal(en1.Seq, pm1.EntrySeq);                              // 근거 카드 1:1 결속
            Assert.Equal(PostMortemVerdict.Loss, pm1.Verdict);
            Assert.Equal(ex1.RealizedPnlWon, pm1.TradePnl);
            Assert.Equal(ArenaTrigger.StopLoss, pm1.ExitTrigger);
            Assert.True(PlutoPostMortemReviewer.IsLossLesson(pm1.Lesson));
            Assert.Equal(1, ex1.Governance.LossStreak);
            Assert.Contains("[국면", en1.HumanReadableSummary);
            Assert.Contains("악마의 변호인", en1.HumanReadableSummary);
            Assert.Contains("플루토 자가 회고", ex1.HumanReadableSummary);
            Assert.Contains($"진입 근거 #{en1.Seq}", ex1.HumanReadableSummary);

            var (en2, ex2) = LosingRoundTrip(e, ex1.PriceWon + 100, 10000);
            Assert.Equal(en2.Seq, ex2.Governance.PostMortem.EntrySeq);
            Assert.False(en2.Governance.StreakCut);
            Assert.Equal(2, ex2.Governance.LossStreak);
            var g = Gov(e);
            Assert.Equal((2, 2L, 2L), (g.LossStreak, g.Closes, g.LossCloses));
            Assert.Equal(ex2.KstTicks + PlutoDynamicSizer.CooldownTicks, g.CooldownUntilTicks);

            // 쿨다운 중 = 신규 진입 0
            long px = ex2.PriceWon;
            Q(e, px + 100, px + 200, 9000, 1000);
            Assert.Equal(4, Rationales(e).Length);
            Assert.Equal(1L, Gov(e).CooldownBlocks);

            // 15분 뒤 = 50% 축소 진입
            _t = _t.AddMinutes(15);
            Q(e, px + 100, px + 200, 9000, 1000);
            var rs = Rationales(e);
            Assert.Equal(5, rs.Length);
            var en3 = rs[^1];
            _out.WriteLine(en3.HumanReadableSummary);
            Assert.True(en3.IsWellFormed);
            Assert.True(en3.Governance.StreakCut);
            Assert.Equal(PlutoDynamicSizer.WeightBp(en3.Governance.Confidence, en3.Governance.AtrCbp, 2), en3.Governance.WeightBp);
            Assert.Equal(PlutoDynamicSizer.WeightBp(en3.Governance.Confidence, en3.Governance.AtrCbp, 0) / 2, en3.Governance.WeightBp);
            Assert.Contains("비중 50% 축소", en3.HumanReadableSummary);
            Assert.Equal(3L, Gov(e).Approved);

            e.SetCapital(KrxArenaEngine.DefaultCapitalWon);                    // 원장 초기화 = 연패/쿨다운/계수 초기화
            var reset = Gov(e);
            Assert.Equal((0, 0L, 0L, 0L), (reset.LossStreak, reset.CooldownUntilTicks, reset.Approved, reset.CooldownBlocks));
            Assert.True(reset.Enabled);
        }

        [Fact]
        public void Us_And_Crypto_Governed_Entries_Bind_Wellformed_Governance()
        {
            var us = new UsTechArenaEngine();
            us.EnableGovernance(true);
            var t = new DateTime(2026, 10, 22, 23, 0, 0);
            long nvda = UsTechUniverse.Symbol(UsTechUniverse.Nvda);
            for (int i = 0; i < UsTechArenaEngine.WarmupTicks + 1; i++) { t = t.AddMilliseconds(100); us.OnQuote(nvda, 13_000, 13_001, 1000, 1000, t); }
            us.SetMarketContext(300, 300, -10);
            t = t.AddMilliseconds(100);
            us.OnQuote(nvda, 13_050, 13_051, 9000, 1000, t);
            var ub = new AiTradeRationale[UsTechArenaEngine.RationaleCapacity];
            var ur = Assert.Single(ub.AsSpan(0, us.CopyRationales(ub)).ToArray());
            _out.WriteLine(ur.HumanReadableSummary);
            Assert.True(ur.IsWellFormed && ur.Governance.IsEntry);
            Assert.Equal(ur.ConfidenceScore, ur.Governance.Confidence);
            Assert.True(us.TryReadGovernor(out var ug) && ug.Approved == 1);

            const long S = UpbitTickAligner.Scale;
            var cx = new UpbitCryptoArenaEngine();
            cx.EnableGovernance(true);
            long btc = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
            long ts = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
            for (int i = 0; i < UpbitCryptoArenaEngine.WarmupTicks + 1; i++) { ts += 100_000; cx.OnQuote(btc, 150_000_000 * S, 150_001_000 * S, S, S, ts); }
            cx.SetMarketContext(0, 300);
            for (int i = 0; i < 4; i++) { ts += 1_000; cx.OnTrade(btc, 150_001_000 * S, S / 2, 1, ts); }
            ts += 100_000;
            cx.OnQuote(btc, 150_100_000 * S, 150_101_000 * S, 9 * S, S, ts);
            var cb = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];
            var cr = Assert.Single(cb.AsSpan(0, cx.CopyRationales(cb)).ToArray());
            _out.WriteLine(cr.HumanReadableSummary);
            Assert.True(cr.IsWellFormed && cr.Governance.IsEntry);
            Assert.Equal(cr.Governance.AdjustedScore, cr.SentimentScore);
            Assert.True(cx.TryReadRegime(btc, out var rs) && rs.Seen == UpbitCryptoArenaEngine.WarmupTicks + 2);
        }

        [Fact]
        public void Rationale_Rejects_Inconsistent_Governance()
        {
            var e = new KrxArenaEngine();
            e.EnableGovernance(true);
            for (int i = 0; i < KrxArenaEngine.WarmupTicks + 1; i++) Q(e, 70_000, 70_100);
            e.SetOvernightMacro(3000);
            Q(e, 70_100, 70_200, 9000, 1000, 1000);
            var r = Assert.Single(Rationales(e));
            Assert.True(r.IsWellFormed);
            var forged = new AiTradeRationale(r.Seq, r.Symbol, r.Side, r.Qty, r.PriceWon, r.KstTicks, r.TriggerType, r.SentimentScore, r.ConfidenceScore + 1,
                r.TargetPrice, r.StopLossPrice, r.RiskRewardX100, r.MacroBp, r.ImbalanceBp, r.SurgeBp, r.MomentumBp, 0, r.Currency, r.News, r.Valuation, 0, 0, r.Governance);
            Assert.False(forged.IsWellFormed);                                 // 근거 확신도 ≠ 거버넌스 감점 후 확신도
            var exitWithoutReview = new AiTradeRationale(r.Seq, r.Symbol, -1, r.Qty, r.PriceWon, r.KstTicks, ArenaTrigger.StopLoss, r.SentimentScore, 100,
                r.TargetPrice, r.StopLossPrice, r.RiskRewardX100, r.MacroBp, r.ImbalanceBp, r.SurgeBp, r.MomentumBp, -1, r.Currency, r.News, r.Valuation, 0, 0, r.Governance);
            Assert.False(exitWithoutReview.IsWellFormed);                      // 거버넌스 가동 청산 = 회고 필수
        }

        // ── §6 δ=0 ──────────────────────────────────────────────────────────────

        [Fact]
        public void ZeroAlloc_Governor_Pipeline_100k()
        {
            var s = new PlutoRegimeState();
            var sizer = new PlutoSizerState();
            var memo = Memo(MarketRegime.TrendingBull, CounterThesis.Overheat);
            ulong x = 0x9E3779B97F4A7C15;
            long px = 100_000, now = Open10.Ticks, sink = 0;

            void Run(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    px = Math.Clamp(px + ((long)(x >> 8 & 7) - 3) * 50, 50_000, 150_000);
                    long bq = 1 + (long)(x >> 16 & 4095), aq = 1 + (long)(x >> 28 & 4095);
                    var regime = PlutoRegimeClassifier.Update(ref s, px, px + 100, bq, aq);
                    int score = (int)(x >> 40 & 0x3FFF) - 4000;
                    now += TimeSpan.TicksPerSecond;
                    var r = PlutoCognitiveGovernor.Approve(in s, score, (int)(x >> 20 & 0x1FFF) - 4096, 25, (int)(x >> 33 & 0x1FFF) - 4096, 35, 3000, bq, aq,
                        (int)(x >> 50 & 31) - 8, (int)(x >> 55 & 127) - 32, in sizer, now, out var tag);
                    sink += (int)r + tag.WeightBp + PlutoDynamicSizer.WeightBp(score / 100, s.AtrCbp, sizer.LossStreak);
                    var v = PlutoDevilsAdvocateFilter.Assess(score / 100, s.Rsi, s.DisparityX100, bq, aq, 0, 0);
                    sink += v.Confidence + (int)regime;
                    if ((i & 15) == 0)
                    {
                        long pnl = (long)(x >> 12 & 1023) - 600;
                        sizer.OnClose(pnl, now);
                        var pm = PlutoPostMortemReviewer.Review(in memo, ArenaTrigger.StopLoss, s.Regime, pnl, true, now, s.AtrCbp);
                        var exitTag = PlutoGovernanceTag.ForExit(in s, sizer.LossStreak, in pm);
                        sink += pm.ReturnBp + (exitTag.IsWellFormed ? 1 : 0);
                    }
                }
            }

            Run(20_000);
            long bytes = MinAllocated(() => Run(100_000));
            _out.WriteLine($"governor pipeline 100k = {bytes}B · sink {sink}");
            Assert.Equal(0, bytes);
        }

        [Fact]
        public void ZeroAlloc_Governed_Krx_And_Crypto_Settlement_100k()
        {
            var krx = new KrxArenaEngine();
            krx.EnableGovernance(true);
            krx.SetMacroHeadwind(5, 20);
            var rationales = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            ulong x = 0x2545F4914F6CDD1D;
            long p0 = 70_000, i0 = 0, baseTicks = Open10.Ticks;

            void RunKrx(int n)
            {
                for (int i = 0; i < n; i++, i0++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    p0 = Math.Clamp(p0 + ((long)(x >> 8 & 3) - 1) * 100, 60_000, 80_000);
                    if ((i0 & 511) == 0) krx.SetOvernightMacro((int)(x >> 20 & 0x1FFF) - 2048);
                    long bq = 100 + (long)(x >> 24 & 4095), aq = 100 + (long)(x >> 36 & 4095);
                    long vol = (x >> 48 & 15) == 0 ? 3000 : 50 + (long)(x >> 52 & 255);
                    krx.OnQuote(Samsung, p0, p0 + 100, bq, aq, vol, new DateTime(baseTicks + i0 * TimeSpan.TicksPerMillisecond * 50));
                    if ((i0 & 63) == 0)
                    {
                        krx.TryReadGovernor(out _);
                        krx.TryReadRegime(Samsung, out _);
                        krx.CopyRationales(rationales);
                    }
                }
            }

            RunKrx(20_000);
            long krxBefore = Gov(krx).Approved;
            long krxBytes = MinAllocated(() => RunKrx(100_000));
            var kg = Gov(krx);
            _out.WriteLine($"KRX governed 100k = {krxBytes}B · approved {kg.Approved - krxBefore} · veto {kg.VetoBlocks} · cooldown {kg.CooldownBlocks} · panic {kg.PanicBlocks} · closes {kg.Closes}");
            Assert.Equal(0, krxBytes);
            Assert.True(kg.Approved > krxBefore, "측정 구간에 거버넌스 승인 진입 발생");
            Assert.All(Rationales(krx), r => Assert.True(r.IsWellFormed && r.Governance.Governed));

            const long S = UpbitTickAligner.Scale;
            var cx = new UpbitCryptoArenaEngine();
            cx.EnableGovernance(true);
            long btc = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
            long ts = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10, cp = 150_000_000;
            var cbuf = new AiTradeRationale[UpbitCryptoArenaEngine.RationaleCapacity];

            void RunCx(int n)
            {
                for (int i = 0; i < n; i++)
                {
                    x ^= x << 13; x ^= x >> 7; x ^= x << 17;
                    cp = Math.Clamp(cp + ((long)(x >> 8 & 3) - 1) * 50_000, 140_000_000, 160_000_000);
                    ts += 50_000;
                    if ((i & 1023) == 0) cx.SetMarketContext((int)(x >> 30 & 255) - 64, (int)(x >> 40 & 1023) - 256);
                    if ((i & 3) == 0) cx.OnTrade(btc, cp * S, S / 4, (x & 1) == 0 ? 1 : -1, ts);
                    cx.OnQuote(btc, cp * S, (cp + 1000) * S, (1 + (long)(x >> 20 & 15)) * S, (1 + (long)(x >> 28 & 15)) * S, ts);
                    if ((i & 63) == 0) { cx.TryReadGovernor(out _); cx.CopyRationales(cbuf); }
                }
            }

            RunCx(20_000);
            long cxBytes = MinAllocated(() => RunCx(100_000));
            Assert.True(cx.TryReadGovernor(out var cg));
            _out.WriteLine($"Crypto governed 100k = {cxBytes}B · approved {cg.Approved} · closes {cg.Closes}");
            Assert.Equal(0, cxBytes);
        }
    }
}
