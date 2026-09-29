// ============================================================================
// AiCoPilotTests — v12.4.0 AI Quantitative Selection & Microstructure Analysis Co-Pilot 적대검증
//   Section 1: FastFactorSieve(SIMD Vector<float> 합성 점수 + 단일패스 Top-N 선별).
//   Section 2: AiRecommendationAdapter(결정론적 등급/목표가/손절가 + Advisory-Only 구조적 격리) —
//     AI 생성 시그널이 발주 파이프라인을 무장/우회할 수 없음을 리플렉션으로 검증.
//   Think Beyond: ZeroAllocTokenStreamBuffer(ArrayPool<char>) · AiModelFallbackCircuitBreaker(0ms 폴백+Suspend).
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Collections.Generic;
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
    internal sealed record StubCandidateInput(string Symbol, decimal CurrentPrice, decimal CompositeFactorScore, decimal VolatilityPct) : IAiCandidateInput;

    public sealed class AiCoPilotTests
    {
        private readonly ITestOutputHelper _out;
        public AiCoPilotTests(ITestOutputHelper o) => _out = o;

        // ══ FastFactorSieve — SIMD 합성 점수 ══════════════════════════════════
        [Theory]
        [InlineData(3)] [InlineData(8)] [InlineData(17)] [InlineData(64)]
        public void ComputeCompositeScores_MatchesScalarReference_AcrossSizes(int n)
        {
            var rsi = new float[n]; var boll = new float[n]; var vol = new float[n]; var fund = new float[n];
            for (int i = 0; i < n; i++) { rsi[i] = i * 0.01f; boll[i] = -i * 0.02f; vol[i] = i * 0.03f; fund[i] = i * 0.005f; }
            var outScores = new float[n];
            FastFactorSieve.ComputeCompositeScores(rsi, boll, vol, fund, 0.4f, 0.3f, 0.2f, 0.1f, outScores);

            for (int i = 0; i < n; i++)
            {
                float expected = rsi[i] * 0.4f + boll[i] * 0.3f + vol[i] * 0.2f + fund[i] * 0.1f;
                Assert.Equal(expected, outScores[i], precision: 4);
            }
        }

        [Fact]
        public void ComputeCompositeScores_ShortInput_Throws()
        {
            var shortArr = new float[2]; var outScores = new float[5];
            Assert.Throws<ArgumentException>(() => FastFactorSieve.ComputeCompositeScores(shortArr, shortArr, shortArr, shortArr, 1, 1, 1, 1, outScores));
        }

        // ══ FastFactorSieve — Top-N 단일패스 선별 ═════════════════════════════
        [Fact]
        public void SelectTopN_PicksHighestScores_Descending()
        {
            float[] scores = { 0.1f, 0.9f, 0.3f, 0.7f, 0.2f, 0.95f, 0.05f };
            var idx = new int[3]; var top = new float[3];
            int filled = FastFactorSieve.SelectTopN(scores, idx, top);
            Assert.Equal(3, filled);
            Assert.Equal(new[] { 0.95f, 0.9f, 0.7f }, top);
            Assert.Equal(5, idx[0]);   // scores[5]=0.95 최고점
        }

        [Fact]
        public void SelectTopN_FewerCandidatesThanN_FillsPartial()
        {
            float[] scores = { 0.5f, 0.1f };
            var idx = new int[5]; var top = new float[5];
            int filled = FastFactorSieve.SelectTopN(scores, idx, top);
            Assert.Equal(2, filled);
            Assert.Equal(0.5f, top[0]); Assert.Equal(0.1f, top[1]);
        }

        // ══ AiModelFallbackCircuitBreaker — 0ms 폴백 스위칭 ═══════════════════
        [Fact]
        public void Breaker_InitialState_Remote_CanEmit()
        {
            var b = new AiModelFallbackCircuitBreaker();
            Assert.Equal(AiModelMode.Remote, b.Mode);
            Assert.True(b.CanEmitSignal);
            Assert.False(b.IsSuspended);
        }

        [Fact]
        public void Breaker_RemoteFailure_SwitchesToLocalFallback_Once()
        {
            var b = new AiModelFallbackCircuitBreaker();
            b.NoteRemoteFailure();
            Assert.Equal(AiModelMode.LocalFallback, b.Mode);
            Assert.Equal(1, b.FallbackSwitches);
            b.NoteRemoteFailure();   // 이미 LocalFallback — 재전이 카운트 안 됨
            Assert.Equal(1, b.FallbackSwitches);
            Assert.Equal(2, b.RemoteFailures);
        }

        [Fact]
        public void Breaker_RestoreRemote_ReturnsToRemoteMode()
        {
            var b = new AiModelFallbackCircuitBreaker();
            b.NoteRemoteFailure();
            b.RestoreRemote();
            Assert.Equal(AiModelMode.Remote, b.Mode);
        }

        [Fact]
        public void Breaker_LowConfidence_Suspends_Sticky_UntilReArm()
        {
            var b = new AiModelFallbackCircuitBreaker(confidenceThresholdPpm: 500_000);
            b.NoteConfidence(300_000);   // 30% < 50% 임계
            Assert.True(b.IsSuspended);
            Assert.False(b.CanEmitSignal);

            b.NoteConfidence(900_000);   // 신뢰도 회복해도 sticky — 해제 안 됨
            Assert.Equal(900_000, b.ConfidencePpm);
            Assert.True(b.IsSuspended);

            b.ReArm();
            Assert.False(b.IsSuspended);
            Assert.True(b.CanEmitSignal);
        }

        // ══ ZeroAllocTokenStreamBuffer — ArrayPool 스트리밍 렌더 버퍼 ══════════
        [Fact]
        public void TokenBuffer_Append_AccumulatesInOrder()
        {
            using var buf = new ZeroAllocTokenStreamBuffer(initialCapacity: 16);
            buf.Append("Hello, ");
            buf.Append("MS2E ");
            buf.Append("Co-Pilot!");
            Assert.Equal("Hello, MS2E Co-Pilot!", buf.View.ToString());
        }

        [Fact]
        public void TokenBuffer_ExceedsCapacity_GrowsAndPreservesContent()
        {
            using var buf = new ZeroAllocTokenStreamBuffer(initialCapacity: 4);
            Assert.Equal(0, buf.GrowthCount);
            buf.Append("this is longer than four chars");
            Assert.True(buf.GrowthCount > 0);
            Assert.Equal("this is longer than four chars", buf.View.ToString());
        }

        [Fact]
        public void TokenBuffer_Clear_ResetsLengthWithoutGrowth()
        {
            using var buf = new ZeroAllocTokenStreamBuffer(initialCapacity: 64);
            buf.Append("abc");
            buf.Clear();
            Assert.Equal(0, buf.Length);
            buf.Append("xyz");
            Assert.Equal("xyz", buf.View.ToString());
            Assert.Equal(0, buf.GrowthCount);   // 재사용 — 재대여 없음
        }

        [Fact]
        public void TokenBuffer_AfterDispose_ThrowsOnAppend()
        {
            var buf = new ZeroAllocTokenStreamBuffer();
            buf.Dispose();
            Assert.Throws<ObjectDisposedException>(() => buf.Append("x"));
        }

        // ══ AiRecommendationAdapter — 결정론적 등급/목표가/손절가 ═══════════════
        [Fact]
        public async Task RecommendAsync_StrongBuyScore_YieldsTargetAboveAndStopBelowPrice()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var adapter = new AiRecommendationAdapter(breaker);
            var candidates = new List<IAiCandidateInput> { new StubCandidateInput("005930", 100_000m, 0.7m, 2m) };

            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);

            Assert.Single(result);
            var r = result[0];
            Assert.Equal(AiRecommendationGrade.StrongBuy, r.Grade);
            Assert.True(r.TargetPrice > 100_000m);
            Assert.True(r.StopLossPrice < 100_000m);
            Assert.True(r.IsAdvisoryOnly);
        }

        [Fact]
        public async Task RecommendAsync_StrongSellScore_YieldsTargetBelowPrice()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            var adapter = new AiRecommendationAdapter(breaker);
            var candidates = new List<IAiCandidateInput> { new StubCandidateInput("000660", 50_000m, -0.8m, 3m) };

            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);

            Assert.Equal(AiRecommendationGrade.StrongSell, result[0].Grade);
            Assert.True(result[0].TargetPrice < 50_000m);
        }

        [Fact]
        public async Task RecommendAsync_BreakerSuspended_AllSignalsNeutralized()
        {
            var breaker = new AiModelFallbackCircuitBreaker(confidenceThresholdPpm: 500_000);
            breaker.NoteConfidence(100_000);   // Suspend
            var adapter = new AiRecommendationAdapter(breaker);
            var candidates = new List<IAiCandidateInput> { new StubCandidateInput("005930", 100_000m, 0.9m, 2m) };

            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);

            Assert.Equal(AiRecommendationGrade.Hold, result[0].Grade);
            Assert.Equal(0m, result[0].Confidence);
            Assert.True(result[0].IsAdvisoryOnly);   // ★Suspend 상태에서도 구조적 표식은 불변
        }

        [Fact]
        public async Task RecommendAsync_NarrativeProvider_IsInvoked_ForRationale()
        {
            var breaker = new AiModelFallbackCircuitBreaker();
            bool invoked = false;
            var adapter = new AiRecommendationAdapter(breaker, (symbol, ct) => { invoked = true; return Task.FromResult($"[LLM] {symbol} 분석 완료"); });
            var candidates = new List<IAiCandidateInput> { new StubCandidateInput("005930", 100_000m, 0.1m, 1m) };

            var result = await adapter.RecommendAsync(candidates, CancellationToken.None);

            Assert.True(invoked);
            Assert.Equal("[LLM] 005930 분석 완료", result[0].Rationale);
        }

        // ══ liveArmed:false — Advisory-Only 구조적 격리(리플렉션) ══════════════
        [Fact]
        public void AiRecommendationAdapter_NeverReferences_LiveArmingOrDispatchTypes()
        {
            string[] forbidden =
            {
                "OrderDispatchRing", "GatedOrderDispatcher", "CanaryHealthSentinel", "HardwareKillSwitchBridge",
                "HardwareMasterKeyHandshake", "LockFreeBrokerSwitchboard", "HardwarePulseAntiReplayGuard",
                "IHardwareKillSwitchPort", "HardwareInterruptSpinLock", "HardwarePulseGuard",
            };

            var adapterType = typeof(AiRecommendationAdapter);
            var offenders = new List<string>();

            foreach (var ctor in adapterType.GetConstructors())
                foreach (var p in ctor.GetParameters())
                    if (Array.IndexOf(forbidden, p.ParameterType.Name) >= 0)
                        offenders.Add($"ctor param {p.Name}:{p.ParameterType.Name}");

            foreach (var f in adapterType.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                if (Array.IndexOf(forbidden, f.FieldType.Name) >= 0)
                    offenders.Add($"field {f.Name}:{f.FieldType.Name}");

            foreach (var m in adapterType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (Array.IndexOf(forbidden, m.ReturnType.Name) >= 0) offenders.Add($"method {m.Name} returns {m.ReturnType.Name}");
                foreach (var p in m.GetParameters())
                    if (Array.IndexOf(forbidden, p.ParameterType.Name) >= 0)
                        offenders.Add($"method {m.Name} param {p.Name}:{p.ParameterType.Name}");
            }

            _out.WriteLine($"[AiCoPilot] scanned {adapterType.FullName} — offenders: {offenders.Count}");
            Assert.Empty(offenders);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class AiCoPilotConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public AiCoPilotConcurrencyTests(ITestOutputHelper o) => _out = o;

        private const int N = 256;
        private static readonly float[] _rsi = new float[N], _boll = new float[N], _vol = new float[N], _fund = new float[N];
        private static readonly float[] _scoresOut = new float[N];

        private static void ComputeHot() => FastFactorSieve.ComputeCompositeScores(_rsi, _boll, _vol, _fund, 0.4f, 0.3f, 0.2f, 0.1f, _scoresOut);

        [Fact]
        public void ComputeCompositeScores_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) ComputeHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10_000; i++) ComputeHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★Vector<float> SIMD 합성 점수 힙 0
        }

        [Fact]
        public void SelectTopN_ZeroAllocation()
        {
            var idx = new int[10]; var top = new float[10];
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000; i++) FastFactorSieve.SelectTopN(_scoresOut, idx, top);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 10_000; i++) FastFactorSieve.SelectTopN(_scoresOut, idx, top);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        private static readonly ZeroAllocTokenStreamBuffer _hotBuf = new(initialCapacity: 8192);
        private static void AppendHot()
        {
            _hotBuf.Append("tok ");
            if (_hotBuf.Length > 6000) _hotBuf.Clear();   // 용량 내 순환(성장 없이 δ=0 유지)
        }

        [Fact]
        public void TokenBuffer_Append_ZeroAllocation_WithinCapacity()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) AppendHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) AppendHot();
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            _out.WriteLine($"[TokenBuffer] growths={_hotBuf.GrowthCount}");
            Assert.Equal(0L, alloc);   // ★용량 내 순환 Append 힙 0(성장 없음)
        }

        private static readonly AiModelFallbackCircuitBreaker _hotBreaker = new();
        private static long _hotConfidence = 1_000_000;
        private static void BreachHot()
        {
            long c = Interlocked.Decrement(ref _hotConfidence);
            if (c <= 0) Interlocked.Exchange(ref _hotConfidence, 1_000_000);
            _hotBreaker.NoteConfidence(c);
            _ = _hotBreaker.CanEmitSignal;
        }

        [Fact]
        public void Breaker_NoteConfidence_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) BreachHot();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) BreachHot();
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
