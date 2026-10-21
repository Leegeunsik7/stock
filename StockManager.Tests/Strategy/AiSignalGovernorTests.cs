// ============================================================================
// AiSignalGovernorTests — [v12.18.43-DEV] AI 신호 정제 · 플립플롭 비트 27 래치 · 30ms 알파 시효성 · 확신도 비례 정수 사이저 · δ=0
//
//   §1 정제: NaN/±∞(3필드 각각) = NonFinite + 비트 27 · 범위 이탈(경계 ±ε) = OutOfRange + 비트 27 · 래치 후 정상 신호도 전부 거부
//   §2 플립플롭: 100ms 안 극단 반전 3회 = 임계 회차에 즉시 0x08000000 래치 · 창 밖 반전 = 무래치 · 약한(비극단) 진동 = 무반전 ·
//      재무장 = 이중키 필수 + 링 소거(재무장 직후 첫 반전 재격발 0)
//   §3 시효성: 30ms 경계(=30ms 통과 · 30ms+1µs 폐기) · 미래 스탬프 · 시계 역행 거부 · 폐기는 인터락 무관
//   §4 사이저: 확신 비례 · lot 내림 · 하한 미달 = 0 · 입력 이탈 = 0 · 상한 10⁹ × 1.0 오버플로 0
//   §5 δ=0: 100만 회 평가/사이징 힙 0B(Release · 3창 최솟값) · 적대 혼합 스트림 = 승인 주문의 수치 이상 유출 0
// ============================================================================
using System;
using MS2E.Core;
using StockManager.Engines;
using StockManager.Engines.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Strategy
{
    [Collection("ConcurrencyStress")]
    public sealed class AiSignalGovernorTests
    {
        private const long Bit27 = 0x08000000L;
        private readonly ITestOutputHelper _out;
        public AiSignalGovernorTests(ITestOutputHelper o) => _out = o;

        private static (AiSignalGovernor G, StrategyAnomalyInterlock A, ArmingInterlockBitmask M) Make(long maxQty = 1_000, long lot = 10, int maxFlips = 3)
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            var a = new StrategyAnomalyInterlock();
            a.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            return (new AiSignalGovernor(a, maxQty, lot, maxFlips: maxFlips), a, mask);
        }

        private static AiSignal Sig(double conf, long t, double pos = 0.5, double urg = 0.5) => new(5930, conf, pos, urg, t);

        // ════════════ §1 정제 ════════════

        [Fact]
        public void Bit27_Is_0x08000000()
        {
            Assert.Equal(27, (int)ArmingInterlockSource.StrategyAnomaly);
            var (g, _, m) = Make();
            Assert.Equal(0L, m.Mask & Bit27);
            Assert.Equal(AiSignalVerdict.NonFinite, g.Evaluate(Sig(double.NaN, 0), 0, out _));
            Assert.Equal(Bit27, m.Mask & Bit27);
        }

        [Theory]
        [InlineData(double.NaN, 0.5, 0.5)]
        [InlineData(double.PositiveInfinity, 0.5, 0.5)]
        [InlineData(double.NegativeInfinity, 0.5, 0.5)]
        [InlineData(0.9, double.NaN, 0.5)]
        [InlineData(0.9, double.PositiveInfinity, 0.5)]
        [InlineData(0.9, 0.5, double.NaN)]
        [InlineData(0.9, 0.5, double.NegativeInfinity)]
        public void NonFinite_Rejected_And_Latches_Bit27(double c, double p, double u)
        {
            var (g, a, m) = Make();
            Assert.Equal(AiSignalVerdict.NonFinite, g.Evaluate(Sig(c, 1_000, p, u), 1_000, out var o));
            Assert.Equal(0, o.Quantity);
            Assert.True(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(StrategyAnomalyKind.NonFinite, a.FirstCause);
            Assert.Equal(AiSignalVerdict.Latched, g.Evaluate(Sig(0.9, 1_001), 1_001, out o));    // 이후 정상 신호도 sticky 거부
            Assert.Equal(0, o.Quantity);
        }

        [Theory]
        [InlineData(1.0000001, 0.5, 0.5)]
        [InlineData(-1.0000001, 0.5, 0.5)]
        [InlineData(0.9, 1.0000001, 0.5)]
        [InlineData(0.9, -1.0000001, 0.5)]
        [InlineData(0.9, 0.5, -0.0000001)]
        [InlineData(0.9, 0.5, 1.0000001)]
        public void OutOfRange_Rejected_And_Latches_Bit27(double c, double p, double u)
        {
            var (g, a, m) = Make();
            Assert.Equal(AiSignalVerdict.OutOfRange, g.Evaluate(Sig(c, 1_000, p, u), 1_000, out var o));
            Assert.Equal(0, o.Quantity);
            Assert.True(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(StrategyAnomalyKind.OutOfRange, a.FirstCause);
        }

        [Fact]
        public void Range_Boundaries_Inclusive_Accepted()
        {
            var (g, _, m) = Make();
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(1.0, 0, 1.0, 1.0), 0, out var o));
            Assert.Equal(1_000, o.Quantity);
            Assert.Equal(1, o.Side);
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(-1.0, 0, -1.0, 0.0), 0, out o));
            Assert.Equal(-1, o.Side);
            Assert.Equal(0L, m.Mask & Bit27);
        }

        [Fact]
        public void Latched_By_Foreign_Cause_Blocks_Governor()
        {
            var (g, a, _) = Make();
            a.Trip(StrategyAnomalyKind.ComputeStall);                                             // 다른 비트 27 원인
            Assert.Equal(AiSignalVerdict.Latched, g.Evaluate(Sig(0.9, 0), 0, out var o));
            Assert.Equal(0, o.Quantity);
        }

        // ════════════ §2 플립플롭 ════════════

        [Fact]
        public void FlipFlop_Within_100ms_Latches_On_Threshold_Tick()
        {
            var (g, a, m) = Make();
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(0.9, 0), 0, out _));
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(-0.9, 10_000), 10_000, out _)); // 반전 1
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(0.9, 20_000), 20_000, out _));  // 반전 2
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(AiSignalVerdict.FlipFlop, g.Evaluate(Sig(-0.9, 30_000), 30_000, out var o)); // ★반전 3 = 그 회차 래치
            Assert.Equal(0, o.Quantity);
            Assert.Equal(Bit27, m.Mask & Bit27);
            Assert.Equal(StrategyAnomalyKind.SignalFlipFlop, a.FirstCause);
            Assert.Equal(1, a.FlipFlops);
            Assert.Equal(AiSignalVerdict.Latched, g.Evaluate(Sig(0.9, 31_000), 31_000, out _));
        }

        [Fact]
        public void SingleFlip_Threshold_Latches_First_Reversal()
        {
            var (g, _, m) = Make(maxFlips: 1);
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(0.8, 0), 0, out _));
            Assert.Equal(AiSignalVerdict.FlipFlop, g.Evaluate(Sig(-0.8, 1), 1, out _));          // 1틱 만에 래치
            Assert.True(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
        }

        [Fact]
        public void Reversals_Spread_Beyond_Window_Do_Not_Latch()
        {
            var (g, _, m) = Make();
            long t = 0;
            for (int i = 0; i < 20; i++, t += 60_000)                                              // 반전 간격 60ms → 3반전 = 120ms > 100ms
                Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(i % 2 == 0 ? 0.9 : -0.9, t), t, out _));
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(19, g.Reversals);
        }

        [Fact]
        public void Weak_Oscillation_Is_Not_Reversal()
        {
            var (g, _, m) = Make();
            for (int i = 0; i < 100; i++)
                g.Evaluate(Sig(i % 2 == 0 ? 0.3 : -0.3, i), i, out _);                            // |c| < 0.5 = 비극단
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(0, g.Reversals);
        }

        [Fact]
        public void Rearm_Requires_DualKey_And_Clears_Ring()
        {
            var (g, _, m) = Make();
            for (int i = 0; i < 4; i++) g.Evaluate(Sig(i % 2 == 0 ? 0.9 : -0.9, i * 1_000), i * 1_000, out _);
            Assert.True(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.False(g.TryRearm(true, false));
            Assert.False(g.TryRearm(false, true));
            Assert.True(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.True(g.TryRearm(true, true));
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(-0.9, 5_000), 5_000, out _));  // 직전 방향 소거 → 반전 아님
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(0.9, 6_000), 6_000, out _));   // 반전 1(링 소거 → 즉시 재격발 0)
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
        }

        // ════════════ §3 시효성 · 시계 ════════════

        [Fact]
        public void AlphaDecay_30ms_Boundary()
        {
            var (g, _, m) = Make();
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(0.9, 0), 30_000, out _));      // 정확히 30ms = 허용
            Assert.Equal(AiSignalVerdict.Stale, g.Evaluate(Sig(0.9, 0), 30_001, out var o));     // 30ms 초과 = 폐기
            Assert.Equal(0, o.Quantity);
            Assert.Equal(1, g.StaleDrops);
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));                      // 지연 ≠ 모델 고장
        }

        [Fact]
        public void Future_Stamp_And_Clock_Regression_Rejected()
        {
            var (g, _, m) = Make();
            Assert.Equal(AiSignalVerdict.FutureTimestamp, g.Evaluate(Sig(0.9, 1_001), 1_000, out _));
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(0.9, 2_000), 2_000, out _));
            Assert.Equal(AiSignalVerdict.ClockRegressed, g.Evaluate(Sig(0.9, 1_500), 1_999, out var o));
            Assert.Equal(0, o.Quantity);
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
        }

        // ════════════ §4 사이저 ════════════

        [Theory]
        [InlineData(1_000, 10, 10_000, 1_000)]
        [InlineData(1_000, 10, 8_765, 870)]      // 876.5 → 876 → lot 10 내림 870
        [InlineData(1_000, 1, 1, 0)]             // 0.1 → 0
        [InlineData(1_000, 100, 999, 0)]         // 99 → lot 100 내림 0
        [InlineData(1_000_000_000, 1, 10_000, 1_000_000_000)]
        [InlineData(1_000, 10, 0, 0)]
        [InlineData(1_000, 10, 10_001, 0)]
        [InlineData(0, 1, 5_000, 0)]
        [InlineData(1_000_000_001, 1, 5_000, 0)]
        [InlineData(1_000, 0, 5_000, 0)]
        public void SizeQuantity_Proportional_LotSnapped_FailClosed(long max, long lot, int bp, long expected)
            => Assert.Equal(expected, AiSignalGovernor.SizeQuantity(max, lot, bp));

        [Fact]
        public void Evaluate_Sizes_By_Confidence_And_Rejects_Below_Threshold()
        {
            var (g, _, _) = Make(maxQty: 1_000, lot: 10);
            Assert.Equal(AiSignalVerdict.Accepted, g.Evaluate(Sig(-0.4567, 0), 0, out var o));
            Assert.Equal(-1, o.Side);
            Assert.Equal(450, o.Quantity);
            Assert.Equal(4_567, o.ConfidenceBp);
            Assert.Equal(AiSignalVerdict.BelowThreshold, g.Evaluate(Sig(0.0999, 1), 1, out o));  // < 1,000bp
            Assert.Equal(0, o.Quantity);
            Assert.Equal(AiSignalVerdict.BelowThreshold, g.Evaluate(Sig(0.0, 2), 2, out _));
            Assert.Equal(AiSignalVerdict.BelowThreshold, g.Evaluate(Sig(-0.0, 3), 3, out _));
        }

        [Fact]
        public void Ctor_Rejects_Invalid_Config()
        {
            var a = new StrategyAnomalyInterlock();
            Assert.Throws<ArgumentNullException>(() => new AiSignalGovernor(null!, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, AiSignalGovernor.MaxQuantityCap + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, 10, lotSize: 11));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, 10, maxFlips: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, 10, maxFlips: AiSignalGovernor.MaxFlipCapacity + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, 10, flipExtreme: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new AiSignalGovernor(a, 10, minConfidenceBp: 0));
        }

        // ════════════ §5 δ=0 · 적대 혼합 ════════════

        [Fact]
        public void Million_Evaluations_And_Sizing_ZeroAlloc()
        {
            var (g, _, m) = Make(maxQty: 10_000, lot: 1);
            long sink = 0, t = 0;
            void Run()
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    t += 1_000;                                                                   // 1ms 간격 · 같은 방향(무반전) · 확신 변동
                    double c = 0.2 + (i & 1023) / 1280.0;
                    if (g.Evaluate(new AiSignal(5930, c, 0.5, 0.5, t - 500), t, out var o) == AiSignalVerdict.Accepted) sink += o.Quantity;
                    sink += AiSignalGovernor.SizeQuantity(10_000, 7, (i & 8191) + 1);
                }
            }
            Run();                                                                                // JIT 예열
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Run();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            _out.WriteLine($"[δ] 100만 AI 신호 평가+사이징 × 3창 최소 = {min}B · sink {sink}");
            Assert.False(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(4_000_000, g.AcceptedCount);
            Assert.Equal(0, min);
        }

        [Fact]
        public void Adversarial_Mixed_Stream_Leaks_No_Anomalous_Order()
        {
            var rng = new Random(20261021);
            double[] poison = { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 1.5, -2.0 };
            for (int run = 0; run < 200; run++)
            {
                var (g, a, m) = Make();
                long t = 0;
                bool tripped = false;
                for (int i = 0; i < 500; i++)
                {
                    t += rng.Next(1, 40_000);
                    double c = rng.Next(10) == 0 ? poison[rng.Next(poison.Length)] : rng.NextDouble() * 2 - 1;
                    var v = g.Evaluate(new AiSignal(1, c, 0, 0.5, t - rng.Next(0, 40_000)), t, out var o);
                    if (v == AiSignalVerdict.Accepted)
                    {
                        Assert.False(tripped);                                                    // 래치 이후 승인 0
                        Assert.True(double.IsFinite(c) && c >= -1 && c <= 1);
                        Assert.True(o.Quantity > 0 && o.Quantity <= 1_000 && o.Quantity % 10 == 0);
                        Assert.Equal(c > 0 ? 1 : -1, o.Side);
                    }
                    else Assert.Equal(0, o.Quantity);
                    if (v is AiSignalVerdict.NonFinite or AiSignalVerdict.OutOfRange or AiSignalVerdict.FlipFlop)
                    {
                        Assert.True(m.IsRaised(ArmingInterlockSource.StrategyAnomaly));            // 같은 회차 래치
                        tripped = true;
                    }
                    Assert.Equal(a.Tripped, tripped);
                }
            }
        }
    }
}
