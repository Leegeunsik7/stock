// ============================================================================
// AdvancedAiTests — v12.7.0 AI Quantitative Intelligence & Multi-LLM Ensemble Engine 적대검증
//   Section 1: MultiFactorConvictionSynthesizer(4대 축 SIMD 합성 위임 + XAI 기여도 분해).
//   Section 2: MultiLlmConsensusVoter(모델 간 불일치 → Hold 하향) + Advisory-Only 격리 리플렉션 검증.
//   Think Beyond A: RollingWinRateCalibrationEngine(POH 적중률 → 확신도 감쇠).
//   Think Beyond B: RegimeAwareDynamicFactorWeightingEngine(국면 분류 → CAS 원자 가중치 재배치).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Intel;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class AdvancedAiTests
    {
        private readonly ITestOutputHelper _out;
        public AdvancedAiTests(ITestOutputHelper o) => _out = o;

        // ══ §1 MultiFactorConvictionSynthesizer ═══════════════════════════════
        [Fact]
        public void ComputeConviction_DelegatesToFastFactorSieve_SameResult()
        {
            float[] t = { 1f, 0.5f, -1f }, f = { 0.2f, 0.2f, 0.2f }, m = { -0.5f, 0f, 1f }, s = { 0.1f, 0.1f, 0.1f };
            var viaSynth = new float[3]; var viaSieve = new float[3];
            MultiFactorConvictionSynthesizer.ComputeConviction(t, f, m, s, 0.4f, 0.3f, 0.2f, 0.1f, viaSynth);
            FastFactorSieve.ComputeCompositeScores(t, f, m, s, 0.4f, 0.3f, 0.2f, 0.1f, viaSieve);
            Assert.Equal(viaSieve, viaSynth);
        }

        [Fact]
        public void Attribute_SumsToApproximately100Percent()
        {
            var attr = MultiFactorConvictionSynthesizer.Attribute(0.8f, -0.3f, 0.5f, 0.1f, 0.4f, 0.3f, 0.2f, 0.1f);
            decimal sum = attr.TechnicalPct + attr.FundamentalPct + attr.MicrostructurePct + attr.SupplyDemandPct;
            Assert.InRange(sum, 99.5m, 100.5m);
        }

        [Fact]
        public void Attribute_AllZeroContribution_YieldsEqualSplit()
        {
            var attr = MultiFactorConvictionSynthesizer.Attribute(0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f);
            Assert.Equal(25m, attr.TechnicalPct);
            Assert.Equal(25m, attr.FundamentalPct);
            Assert.Equal(25m, attr.MicrostructurePct);
            Assert.Equal(25m, attr.SupplyDemandPct);
        }

        [Fact]
        public void Attribute_DominantAxis_GetsHighestShare()
        {
            // Technical 축만 강한 신호, 나머지는 미약 → Technical 기여도가 최대여야 함.
            var attr = MultiFactorConvictionSynthesizer.Attribute(1.0f, 0.05f, 0.05f, 0.05f, 1f, 1f, 1f, 1f);
            Assert.True(attr.TechnicalPct > attr.FundamentalPct);
            Assert.True(attr.TechnicalPct > attr.MicrostructurePct);
            Assert.True(attr.TechnicalPct > attr.SupplyDemandPct);
        }

        // ══ §2 MultiLlmConsensusVoter ══════════════════════════════════════════
        [Fact]
        public void Consensus_Unanimous_ReturnsThatDirection()
        {
            Assert.Equal(LlmVote.Bullish, MultiLlmConsensusVoter.Consensus(new[] { LlmVote.Bullish, LlmVote.Bullish }));
            Assert.Equal(LlmVote.Bearish, MultiLlmConsensusVoter.Consensus(new[] { LlmVote.Bearish }));
        }

        [Fact]
        public void Consensus_Disagreement_DowngradesToNeutral()
        {
            Assert.Equal(LlmVote.Neutral, MultiLlmConsensusVoter.Consensus(new[] { LlmVote.Bullish, LlmVote.Bearish }));
        }

        [Fact]
        public void Consensus_Empty_ReturnsNeutral()
        {
            Assert.Equal(LlmVote.Neutral, MultiLlmConsensusVoter.Consensus(ReadOnlySpan<LlmVote>.Empty));
        }

        [Fact]
        public void IsUnanimous_DetectsMixedVotes()
        {
            Assert.True(MultiLlmConsensusVoter.IsUnanimous(new[] { LlmVote.Bullish, LlmVote.Bullish }));
            Assert.False(MultiLlmConsensusVoter.IsUnanimous(new[] { LlmVote.Bullish, LlmVote.Neutral }));
            Assert.True(MultiLlmConsensusVoter.IsUnanimous(ReadOnlySpan<LlmVote>.Empty));
        }

        // ══ §3 AiRecommendationAdapter — 멀티 LLM 합의 게이트 결속 ═══════════════
        [Fact]
        public async Task RecommendAsync_DisagreeingLlmVotes_DowngradesToHold()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var adapter = new AiRecommendationAdapter(breaker, multiLlmVoteProvider: _ => new[] { LlmVote.Bullish, LlmVote.Bearish });
            var candidates = new[] { (IAiCandidateInput)new StubCandidateInput("005930", 100_000m, 0.9m, 2m) };   // 정량은 StrongBuy 유력

            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);

            Assert.Equal(AiRecommendationGrade.Hold, result[0].Grade);
            Assert.Contains("불일치", result[0].Rationale);
        }

        [Fact]
        public async Task RecommendAsync_UnanimousLlmVotes_DoesNotOverrideGrade()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var adapter = new AiRecommendationAdapter(breaker, multiLlmVoteProvider: _ => new[] { LlmVote.Bullish, LlmVote.Bullish });
            var candidates = new[] { (IAiCandidateInput)new StubCandidateInput("005930", 100_000m, 0.9m, 2m) };

            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);

            Assert.Equal(AiRecommendationGrade.StrongBuy, result[0].Grade);   // 정량 등급 그대로 유지
        }

        [Fact]
        public async Task RecommendAsync_NoVoteProvider_UnaffectedByConsensus()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var adapter = new AiRecommendationAdapter(breaker);   // v12.4.0 동작 그대로
            var candidates = new[] { (IAiCandidateInput)new StubCandidateInput("005930", 100_000m, 0.9m, 2m) };
            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);
            Assert.Equal(AiRecommendationGrade.StrongBuy, result[0].Grade);
        }

        // ══ §4 RollingWinRateCalibrationEngine(Think Beyond A) ═══════════════════
        [Fact]
        public void WinRate_RecordAndResolve_TracksHitsPerHorizon()
        {
            var e = new RollingWinRateCalibrationEngine(16);
            long s1 = e.RecordPrediction(1);    // 매수 예측
            long s2 = e.RecordPrediction(-1);   // 매도 예측

            Assert.True(e.ResolveOutcome(s1, WinRateHorizon.Day1, actualDirection: 1));    // 적중
            Assert.True(e.ResolveOutcome(s2, WinRateHorizon.Day1, actualDirection: 1));    // 오적중(예측 매도, 실제 매수)

            Assert.Equal(500_000, e.WinRatePpm(WinRateHorizon.Day1));   // 2건 중 1건 적중 = 50%
            Assert.Equal(1_000_000, e.WinRatePpm(WinRateHorizon.Day5));   // 미해소 호라이즌은 정보없음=만점
        }

        [Fact]
        public void WinRate_LappedSlot_ResolveReturnsFalse()
        {
            var e = new RollingWinRateCalibrationEngine(2);   // 용량 2 — 즉시 랩
            long s0 = e.RecordPrediction(1);
            e.RecordPrediction(1); e.RecordPrediction(1); e.RecordPrediction(1);   // s0 슬롯 여러 번 덮어씀
            Assert.False(e.ResolveOutcome(s0, WinRateHorizon.Day1, 1));   // fail-closed — 오귀속 방지
        }

        [Fact]
        public void WinRate_ConfidenceAttenuation_NoAttenuationAboveHalf()
        {
            var e = new RollingWinRateCalibrationEngine(16);
            for (int i = 0; i < 6; i++) { long s = e.RecordPrediction(1); e.ResolveOutcome(s, WinRateHorizon.Day1, 1); }   // 100%
            for (int i = 0; i < 6; i++) { long s = e.RecordPrediction(1); e.ResolveOutcome(s, WinRateHorizon.Day5, 1); }
            for (int i = 0; i < 6; i++) { long s = e.RecordPrediction(1); e.ResolveOutcome(s, WinRateHorizon.Day20, 1); }
            Assert.Equal(1_000_000, e.ConfidenceAttenuationPpm);
        }

        [Fact]
        public void WinRate_ConfidenceAttenuation_DecaysBelowHalf_WithFloor()
        {
            var e = new RollingWinRateCalibrationEngine(64, attenuationFloorPpm: 100_000);
            // 3개 호라이즌 전부 완전 오적중(0%) 주입.
            foreach (var h in new[] { WinRateHorizon.Day1, WinRateHorizon.Day5, WinRateHorizon.Day20 })
                for (int i = 0; i < 4; i++) { long s = e.RecordPrediction(1); e.ResolveOutcome(s, h, -1); }

            Assert.Equal(100_000, e.ConfidenceAttenuationPpm);   // 바닥 하한 적용
        }

        [Fact]
        public async Task RecommendAsync_WinRateEngine_AttenuatesConfidence()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var winRate = new RollingWinRateCalibrationEngine(64, attenuationFloorPpm: 100_000);
            foreach (var h in new[] { WinRateHorizon.Day1, WinRateHorizon.Day5, WinRateHorizon.Day20 })
                for (int i = 0; i < 4; i++) { long s = winRate.RecordPrediction(1); winRate.ResolveOutcome(s, h, -1); }   // 전부 오적중

            var adapterWithoutWinRate = new AiRecommendationAdapter(breaker);
            var adapterWithWinRate = new AiRecommendationAdapter(breaker, winRateEngine: winRate);
            var candidates = new[] { (IAiCandidateInput)new StubCandidateInput("005930", 100_000m, 0.9m, 2m) };

            var withoutR = await adapterWithoutWinRate.RecommendAsync(candidates, CancellationToken.None);
            var withR = await adapterWithWinRate.RecommendAsync(candidates, CancellationToken.None);

            Assert.True(withR[0].Confidence < withoutR[0].Confidence);
        }

        // ══ §5 RegimeAwareDynamicFactorWeightingEngine(Think Beyond B) ═══════════
        [Theory]
        [InlineData(2500, 100, MarketRegime.Bull)]
        [InlineData(-2500, 100, MarketRegime.Bear)]
        [InlineData(10, 50, MarketRegime.Sideways)]
        [InlineData(0, 500, MarketRegime.HighVolatility)]
        public void ClassifyRegime_MapsTrendVolatility_Correctly(long trendBps, long volBps, MarketRegime expected)
            => Assert.Equal(expected, RegimeAwareDynamicFactorWeightingEngine.ClassifyRegime(trendBps, volBps));

        [Fact]
        public void ApplyRegime_AtomicallyReplacesAllFourWeights()
        {
            var e = new RegimeAwareDynamicFactorWeightingEngine();
            var before = e.CurrentWeights();
            e.ApplyRegime(MarketRegime.Bull);
            var after = e.CurrentWeights();
            Assert.Equal(MarketRegime.Bull, e.CurrentRegime);
            Assert.NotEqual(before, after);
            Assert.True(after.Technical > after.Fundamental);   // Bull 국면 = 모멘텀(기술적) 우대
        }

        [Fact]
        public void ApplyRegime_HighVolatility_FavorsMicrostructure()
        {
            var e = new RegimeAwareDynamicFactorWeightingEngine();
            e.ApplyRegime(MarketRegime.HighVolatility);
            var w = e.CurrentWeights();
            Assert.True(w.Microstructure > w.Technical);
        }

        // ══ §6 liveArmed:false — 고도화 AI 엔진 전체 발주 무장 불가(리플렉션) ═══
        [Theory]
        [InlineData(typeof(AiRecommendationAdapter))]
        [InlineData(typeof(DashboardAiFeedAdapter))]
        public void AdvancedAiTypes_NeverReference_LiveArmingOrDispatchTypes(Type target)
        {
            string[] forbidden =
            {
                "OrderDispatchRing", "GatedOrderDispatcher", "CanaryHealthSentinel", "HardwareKillSwitchBridge",
                "HardwareMasterKeyHandshake", "LockFreeBrokerSwitchboard", "HardwarePulseAntiReplayGuard",
                "IHardwareKillSwitchPort", "HardwareInterruptSpinLock", "HardwarePulseGuard",
            };
            var offenders = new System.Collections.Generic.List<string>();

            foreach (var ctor in target.GetConstructors())
                foreach (var p in ctor.GetParameters())
                    if (Array.IndexOf(forbidden, p.ParameterType.Name) >= 0)
                        offenders.Add($"ctor param {p.Name}:{p.ParameterType.Name}");

            foreach (var f in target.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                if (Array.IndexOf(forbidden, f.FieldType.Name) >= 0)
                    offenders.Add($"field {f.Name}:{f.FieldType.Name}");

            foreach (var m in target.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(forbidden, m.ReturnType.Name) >= 0) offenders.Add($"method {m.Name} returns {m.ReturnType.Name}");
                foreach (var p in m.GetParameters())
                    if (Array.IndexOf(forbidden, p.ParameterType.Name) >= 0)
                        offenders.Add($"method {m.Name} param {p.Name}:{p.ParameterType.Name}");
            }

            _out.WriteLine($"[AdvancedAi] scanned {target.FullName} — offenders: {offenders.Count}");
            Assert.Empty(offenders);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §7 적대검증 — δ=0 핫패스 + 락프리 다중스레드
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class AdvancedAiConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public AdvancedAiConcurrencyTests(ITestOutputHelper o) => _out = o;

        private const int N = 256;
        private static readonly float[] _t = new float[N], _f = new float[N], _m = new float[N], _s = new float[N], _outArr = new float[N];

        [Fact]
        public void ComputeConviction_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) MultiFactorConvictionSynthesizer.ComputeConviction(_t, _f, _m, _s, 0.25f, 0.25f, 0.25f, 0.25f, _outArr);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10_000; i++) MultiFactorConvictionSynthesizer.ComputeConviction(_t, _f, _m, _s, 0.25f, 0.25f, 0.25f, 0.25f, _outArr);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly LlmVote[] _votes = { LlmVote.Bullish, LlmVote.Bullish, LlmVote.Neutral };

        [Fact]
        public void Consensus_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) MultiLlmConsensusVoter.Consensus(_votes);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) MultiLlmConsensusVoter.Consensus(_votes);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly RollingWinRateCalibrationEngine _hotWinRate = new(4096);
        private static void RecordResolveHot()
        {
            long s = _hotWinRate.RecordPrediction(1);
            _hotWinRate.ResolveOutcome(s, WinRateHorizon.Day1, 1);
        }

        [Fact]
        public void WinRate_RecordAndResolve_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) RecordResolveHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10_000; i++) RecordResolveHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly RegimeAwareDynamicFactorWeightingEngine _hotRegime = new();
        private static long _hotRegimeSeq;
        private static void RegimeHot()
        {
            long s = Interlocked.Increment(ref _hotRegimeSeq);
            _hotRegime.ApplyRegime((MarketRegime)(s % 4));
            _ = _hotRegime.CurrentWeights();
        }

        [Fact]
        public void Regime_ApplyAndRead_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) RegimeHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) RegimeHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
