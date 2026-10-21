// ============================================================================
// VolatilityCalibrationTests — [v12.18.1-DEV · 추천 A] 변동성 적응 확신 임계 · 정수 EMV · 승인 서킷 브레이커
//
//  §1 정수 수학: ISqrt = ⌊√v⌋ 전수/경계 · EMV(시프트 근사) ↔ 부동소수점 기준 지수 이동 분산 상대 오차 · 효율비/노이즈 극단값.
//  §2 국면 판독: 워밍업(< 2^k) = 9500 · 추세 = 8500 · 회귀 = 9000 · 횡보 = 9500 · 횡보 이탈 히스테리시스(16 호가) · k=9(≈1,000틱) 지연 대조.
//  §3 ★Zero Whipsaw Bleed(드라이런 실사슬 · 물리 키 on): 횡보 휩소 호가열 — 고정 임계 = 0.85 이상 신호가 실제로 생겨 라우터 승인 · 송출 발생(대조) ·
//     적응 임계 = 라우터 승인 0 · 분할 0 · 송출 0 · 거부 사유 = 국면 · 추세 호가열 = 적응 임계에서도 승인 유지(전면 차단 아님) · 키 off = 송출 0.
//  §4 [방향 B] 승인 서킷 브레이커: 소프트 억제 · 하드 = 비트 27 Disarm(ModelBreakdown) · 60초 창 만료 · 시계 역행 · 재무장 이중키 · 드라이런 통합.
//  §5 δ=0: Observe · OnBook(캘리브레이터 결속) · AdmitSignal(브레이커 결속) · 허브 PublishCalibration · 라벨 조회.
//  §6 관제: 허브 프레임(미결선 = Unbound · 결선 전 = 워밍업 최대 · 통지 반영 · 브레이커 수치) · 라벨 표 ↔ 엔진 열거형.
//  §7 회귀: 호스트 결선 소스 · 오중 빌드 · RatchetCeiling · 인터락 30 불변(신규 비트 없음 — 출하 명세 유지).
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Simulation;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class VolatilityCalibrationTests
    {
        private const long Sym = 5930, Mid = 70_000, Tick = 100;
        private readonly ITestOutputHelper _out;
        public VolatilityCalibrationTests(ITestOutputHelper o) => _out = o;

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        // 호가 1장(캘리브레이터 단위 테스트용 — 최우선 호가 · 5호가 합)
        private static int Feed(VolatilityAdaptiveCalibrator c, ref CalibrationState s, long mid, long bid5, long ask5)
            => c.Observe(ref s, mid, mid + Tick, bid5, ask5);

        // ════════════ §1 정수 수학 ════════════

        [Fact]
        public void ISqrt_IsExactFloor_AcrossRangeAndBoundaries()
        {
            for (long v = 0; v < 100_000; v++)
            {
                long r = VolatilityAdaptiveCalibrator.ISqrt(v);
                Assert.True(r * r <= v && (r + 1) * (r + 1) > v, "v=" + v);
            }
            foreach (long v in new[] { (1L << 50) - 1, 1L << 50, (1L << 62) + 12345, long.MaxValue, 999_999_999_999L })
            {
                long r = VolatilityAdaptiveCalibrator.ISqrt(v);
                Assert.True((ulong)r * (ulong)r <= (ulong)v && (ulong)(r + 1) * (ulong)(r + 1) > (ulong)v, "v=" + v);
            }
            Assert.Equal(0, VolatilityAdaptiveCalibrator.ISqrt(-5));
        }

        [Fact]
        public void IntegerEmv_TracksFloatingReference_WithinShiftBound()
        {
            // 같은 수익률 열(결정론 의사난수 ± 0~40bp)을 정수 EMV 와 부동소수점 기준 지수 이동 분산에 넣어 변동성(bp)을 대조
            var c = new VolatilityAdaptiveCalibrator(shift: 5);
            var s = new CalibrationState();
            double alpha = 1.0 / 32, mean = 0, variance = 0, prevMid = 0;
            long mid = 7_000_000, rng = 0x2545F4914F6CDD1D;
            for (int i = 0; i < 4_000; i++)
            {
                rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
                long step = (rng & 0xFF) % 81 - 40;                                       // ±40 원 @ 7,000,000 ≈ ±0.057bp … 스케일 확대
                mid += step * 70;                                                          // ±2,800 원 ≈ ±4bp
                c.Observe(ref s, mid, mid + 2, 500, 500);
                double m2 = mid * 2.0 + 2;
                if (prevMid > 0)
                {
                    double r = (m2 - prevMid) / prevMid * 10_000.0;
                    double d = r - mean;
                    mean += alpha * d;
                    variance = (1 - alpha) * (variance + alpha * d * d);
                }
                prevMid = m2;
            }
            double refVol = Math.Sqrt(variance);
            double intVol = s.VolatilityQ8 / 256.0;
            _out.WriteLine($"[EMV] 정수 {intVol:F3}bp(Q8 {s.VolatilityQ8}) · 기준 {refVol:F3}bp · 오차 {(intVol - refVol) / refVol * 100:F2}%");
            Assert.True(refVol > 1, "기준 변동성 너무 작음 — 시나리오 무효");
            Assert.InRange(intVol, refVol * 0.95, refVol * 1.05);                          // α² 항 생략(≤ α ≈ 3%) + Q8 절단 + 산술 시프트 내림
            Assert.Equal(s.VolatilityQ8 >> 8, s.VolatilityBp);
        }

        [Fact]
        public void Efficiency_And_Noise_Extremes()
        {
            var c = new VolatilityAdaptiveCalibrator();
            var up = new CalibrationState();
            for (int i = 0; i < 200; i++) Feed(c, ref up, Mid + i * Tick, 900, 100);   // 한 방향 · 한쪽 우위 지속
            Assert.InRange(up.EfficiencyBp, 9_000, 10_000);
            Assert.Equal(0, up.ImbalanceNoiseBp);
            var chop = new CalibrationState();
            for (int i = 0; i < 200; i++) Feed(c, ref chop, Mid + (i % 2) * Tick, i % 2 == 0 ? 900 : 100, i % 2 == 0 ? 100 : 900);   // 오르내림 · 우위 매 장 뒤집힘
            Assert.InRange(chop.EfficiencyBp, 0, 500);
            Assert.InRange(chop.ImbalanceNoiseBp, 9_000, 10_000);
            Assert.True(chop.SpreadBp > 0);
        }

        // ════════════ §2 국면 판독 ════════════

        [Fact]
        public void Regimes_Warmup_Trend_MeanReverting_Choppy_Thresholds()
        {
            var c = new VolatilityAdaptiveCalibrator();
            Assert.Equal((8_500, 9_000, 9_500, 9_500), (c.ThresholdFor(MicroRegime.Trend), c.ThresholdFor(MicroRegime.MeanReverting),
                                                       c.ThresholdFor(MicroRegime.Choppy), c.ThresholdFor(MicroRegime.Warmup)));
            var s = new CalibrationState();
            for (int i = 0; i < c.WarmupBooks - 1; i++) Assert.Equal(9_500, Feed(c, ref s, Mid + i * Tick, 900, 100));   // 판단 근거 부족 = 최대
            Assert.Equal(MicroRegime.Warmup, s.Regime);
            for (int i = 0; i < 64; i++) Feed(c, ref s, Mid + (32 + i) * Tick, 900, 100);
            Assert.Equal((MicroRegime.Trend, 8_500), (s.Regime, s.RequiredBp));

            // 회귀: 가격은 오르내리며 제자리(ER 낮음)인데 잔량 우위는 한쪽 지속(노이즈 낮음) — 횡보 조건(노이즈) 불충족
            var mr = new CalibrationState();
            for (int i = 0; i < 128; i++) Feed(c, ref mr, Mid + (i % 4 < 2 ? 0 : Tick), 800, 200);
            Assert.Equal((MicroRegime.MeanReverting, 9_000), (mr.Regime, mr.RequiredBp));

            var chop = new CalibrationState();
            for (int i = 0; i < 128; i++) Feed(c, ref chop, Mid + (i % 2) * Tick, i % 2 == 0 ? 900 : 100, i % 2 == 0 ? 100 : 900);
            Assert.Equal((MicroRegime.Choppy, 9_500), (chop.Regime, chop.RequiredBp));
            Assert.Equal(0, MinAllocated(() => Feed(c, ref chop, Mid, 900, 100)));
        }

        [Fact]
        public void ChoppyExit_RequiresHoldStreak_EntryIsImmediate_LongWindowLagsMeasured()
        {
            var c = new VolatilityAdaptiveCalibrator();
            var s = new CalibrationState();
            for (int i = 0; i < 128; i++) Feed(c, ref s, Mid + (i % 2) * Tick, i % 2 == 0 ? 900 : 100, i % 2 == 0 ? 100 : 900);
            Assert.Equal(MicroRegime.Choppy, s.Regime);
            long entries = c.ChoppyEntries;
            // 추세 전환: 후보가 비-횡보가 된 뒤에도 16 호가 동안은 횡보(최대 임계) 유지
            int firstNonChop = -1, candidateTurned = -1;
            for (int i = 0; i < 400 && firstNonChop < 0; i++)
            {
                Feed(c, ref s, Mid + (i + 2) * Tick, 900, 100);
                bool candidateNonChop = s.EfficiencyBp >= 1_500 || s.ImbalanceNoiseBp < 6_000;
                if (candidateNonChop && candidateTurned < 0) candidateTurned = i;
                if (s.Regime != MicroRegime.Choppy) firstNonChop = i;
            }
            _out.WriteLine($"[히스테리시스] 후보 전환 {candidateTurned} → 국면 이탈 {firstNonChop}");
            Assert.True(candidateTurned >= 0 && firstNonChop - candidateTurned >= VolatilityAdaptiveCalibrator.DefaultExitHoldBooks - 1);
            // 재진입은 즉시(첫 후보 = 횡보 → 그 장에서 최대 임계)
            int reentry = -1;
            for (int i = 0; i < 200 && reentry < 0; i++)
            {
                Feed(c, ref s, Mid + (i % 2) * Tick, i % 2 == 0 ? 900 : 100, i % 2 == 0 ? 100 : 900);
                if (s.Regime == MicroRegime.Choppy) reentry = i;
            }
            Assert.True(reentry >= 0);
            Assert.Equal(entries + 1, c.ChoppyEntries);

            // k = 9(≈ 1,023 호가 창 · 오더의 '1,000틱') — 긴 추세 직후 휩소로 바뀔 때 '임계 상향(≥ 9000)'과 '횡보 판정'이 k = 5 보다 훨씬 늦다(기본을 짧게 둔 근거)
            (int Raise, int Chop) Detect(int shift)
            {
                var cc = new VolatilityAdaptiveCalibrator(shift: shift);
                var st = new CalibrationState();
                for (int i = 0; i < 4_000; i++) Feed(cc, ref st, Mid + i * Tick, 900, 100);
                Assert.Equal(MicroRegime.Trend, st.Regime);
                int raise = -1;
                for (int i = 0; i < 20_000; i++)
                {
                    Feed(cc, ref st, Mid + (i % 2) * Tick, i % 2 == 0 ? 900 : 100, i % 2 == 0 ? 100 : 900);
                    if (raise < 0 && st.RequiredBp >= 9_000) raise = i;
                    if (st.Regime == MicroRegime.Choppy) return (raise, i);
                }
                return (raise, int.MaxValue);
            }
            var fast = Detect(5); var slow = Detect(9);
            _out.WriteLine($"[창 길이] 긴 추세 → 휩소: 임계 상향 k=5 {fast.Raise} · k=9 {slow.Raise} 호가 / 횡보 판정 k=5 {fast.Chop} · k=9 {slow.Chop} 호가");
            Assert.True(fast.Raise >= 0 && fast.Raise < 32, "k=5 임계 상향 " + fast.Raise);
            Assert.True(slow.Raise > 4 * fast.Raise && slow.Chop > 4 * fast.Chop, $"k=5 {fast} · k=9 {slow}");
        }

        [Fact]
        public void Constructor_RejectsInvalidConfig()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new VolatilityAdaptiveCalibrator(baseBp: 9_600, maxBp: 9_500));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VolatilityAdaptiveCalibrator(maxBp: 10_001));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VolatilityAdaptiveCalibrator(shift: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new VolatilityAdaptiveCalibrator(trendEfficiencyBp: 1_000, chopEfficiencyBp: 1_500));
            var c = new VolatilityAdaptiveCalibrator();
            var s = new CalibrationState();
            Assert.Equal(9_500, c.Observe(ref s, 0, 100, 1, 1));                          // 무효 호가 = 최대 · 상태 불변
            Assert.Equal(9_500, c.Observe(ref s, 100, 100, 1, 1));
            Assert.Equal(9_500, c.Observe(ref s, 100, 200, 0, 0));
            Assert.Equal(0, s.SampleCount);
        }

        // ════════════ §3 Zero Whipsaw Bleed(드라이런 실사슬) ════════════

        // 매수 쪽 고확신 램프(ProductionDryRunRoutingTests.Tilt 와 같은 형상) — sign = +1 매수 · −1 매도. 1~5호가 950/50 · 6~10호가로 10호가 불균형 상승.
        private static void Ramp(MultiBrokerDryRunHarness h, int sign, long mid)
        {
            for (int k = 0; k < 6; k++)
            {
                double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
                long strong10 = (long)Math.Round(5_000 * (1 + imb10)), weak10 = 10_000 - strong10;
                long deepStrong = (strong10 - 4_750) / 5, deepWeak = (weak10 - 250) / 5;
                if (sign > 0) h.OnBook(Sym, mid, 950, 50, deepStrong, deepWeak);
                else h.OnBook(Sym, mid, 50, 950, deepWeak, deepStrong);
                h.Advance(1_000);
            }
        }

        private readonly record struct Run(long Proposed, long RouterAdmitted, long SplitAdmitted, long Places, long Unsanctioned, long RegimeRejects,
                                           long LowRejects, long SignalRejects, double MaxConf, MicroRegime LastRegime);

        /// <summary>휩소(choppy = true): 매수 램프 ↔ 매도 램프 교대 · 중간가 ±1틱 왕복. 추세: 매수 램프 반복 · 중간가 램프마다 +1틱.</summary>
        private static Run Drive(MultiBrokerDryRunHarness h, bool choppy, int cycles)
        {
            long proposed = 0; double maxConf = 0;
            for (int c = 0; c < cycles; c++)
            {
                int sign = choppy && c % 2 == 1 ? -1 : 1;
                long mid = choppy ? Mid + (c % 2 == 0 ? Tick : 0) : Mid + c * Tick;
                Ramp(h, sign, mid);
                if (h.Signals.TryGetSignal(Sym, out var sig))
                {
                    maxConf = Math.Max(maxConf, sig.Confidence);
                    var side = sig.Side > 0 ? OrderSide.Buy : OrderSide.Sell;
                    // 전략 = 고정 임계(0.85) 신호를 보면 제안(라우터 전방 게이트가 국면 임계로 다시 거른다)
                    if (sig.Confidence >= 0.85 && h.SubmitFromSignal(Sym, side, 10, side == OrderSide.Buy ? mid + Tick : mid) != 0) proposed++;
                }
                h.Pump();
                h.Advance(50_000);
            }
            h.Signals.TryGetSignal(Sym, out var last);
            return new Run(proposed, h.Gate.Admitted, h.Gate.SplitAdmitted, h.InnerPlaces, h.UnsanctionedPlaces, h.Signals.RejectedRegime,
                           h.Signals.RejectedLowConfidence, h.Gate.SignalRejects, maxConf, last.Regime);
        }

        [Fact]
        public void ZeroWhipsawBleed_ChoppyTape_FixedGateLeaks_AdaptiveGateAdmitsNothing_TrendStillTrades()
        {
            const int Cycles = 80;
            Run fixedRun, adaptive, trend, keysOff;
            using (var h = new MultiBrokerDryRunHarness()) { h.SetPhysicalKeys(true, true); h.SetCaps(60, 40); fixedRun = Drive(h, choppy: true, Cycles); }
            using (var h = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator()))
            {
                h.SetPhysicalKeys(true, true); h.SetCaps(60, 40);
                adaptive = Drive(h, choppy: true, Cycles);
            }
            using (var h = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator()))
            {
                h.SetPhysicalKeys(true, true); h.SetCaps(60, 40);
                trend = Drive(h, choppy: false, Cycles);
            }
            using (var h = new MultiBrokerDryRunHarness()) { h.SetCaps(60, 40); keysOff = Drive(h, choppy: true, Cycles); }   // liveArmed:false 기본
            _out.WriteLine($"[휩소 · 고정 0.85] {fixedRun}");
            _out.WriteLine($"[휩소 · 적응]     {adaptive}");
            _out.WriteLine($"[추세 · 적응]     {trend}");
            _out.WriteLine($"[휩소 · 키 off]   {keysOff}");

            // 대조: 시나리오가 실제로 0.85 이상 · 0.95 미만 휩소 신호를 만들고 고정 임계는 그것을 라우터로 통과시킨다(누출 존재)
            Assert.True(fixedRun.MaxConf >= 0.85 && fixedRun.MaxConf < 0.95, "휩소 확신 범위 " + fixedRun.MaxConf);
            Assert.True(fixedRun.Proposed > 10 && fixedRun.SplitAdmitted + fixedRun.RouterAdmitted > 10 && fixedRun.Places > 10, "고정 임계 누출 대조 실패");
            // ★적응: 같은 호가열 · 같은 제안 — 라우터 승인 0 · 분할(자식) 0 · 송출 0 · 거부 사유 = 국면
            Assert.Equal(fixedRun.Proposed, adaptive.Proposed);
            Assert.Equal(0, adaptive.RouterAdmitted);
            Assert.Equal(0, adaptive.SplitAdmitted);
            Assert.Equal(0, adaptive.Places);
            Assert.Equal(adaptive.Proposed, adaptive.RegimeRejects);
            Assert.Equal(MicroRegime.Choppy, adaptive.LastRegime);
            // 추세: 적응 임계에서도 승인 · 송출 유지(전면 차단 장치가 아님)
            Assert.Equal(MicroRegime.Trend, trend.LastRegime);
            Assert.True(trend.SplitAdmitted + trend.RouterAdmitted > 10 && trend.Places > 10, "추세 승인 소실");
            // liveArmed:false = 무엇도 출구 도달 0
            Assert.Equal(0, keysOff.Places);
            Assert.Equal(0, fixedRun.Unsanctioned + adaptive.Unsanctioned + trend.Unsanctioned + keysOff.Unsanctioned);
        }

        [Fact]
        public void Provider_RegimeThresholdNotMet_Reason_And_Signal_Carries_RequiredBp()
        {
            var anomaly = new StrategyAnomalyInterlock();
            long now = 1;
            var q = new QuantSignalProvider(anomaly, nowMicros: () => now, calibrator: new VolatilityAdaptiveCalibrator());
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int i = 0; i < 10; i++) { b.BidPrice[i] = Mid - Tick * i; b.AskPrice[i] = Mid + Tick * (i + 1); b.BidQty[i] = 500; b.AskQty[i] = 500; }
            q.OnBook(in b);
            Assert.True(q.TryGetSignal(Sym, out var s));
            Assert.Equal((9_500, MicroRegime.Warmup), (s.RequiredBp, s.Regime));        // 워밍업 = 최대
            var fixedQ = new QuantSignalProvider(anomaly, nowMicros: () => now);
            fixedQ.OnBook(in b);
            Assert.True(fixedQ.TryGetSignal(Sym, out var f));
            Assert.Equal(0, f.RequiredBp);                                                  // 미결속 = 고정 임계(기존 동작)
            Assert.Null(fixedQ.Calibrator);
        }

        // ════════════ §4 [방향 B] 승인 서킷 브레이커 ════════════

        [Fact]
        public void Churn_SoftSuppresses_HardTripsBit27_WindowExpires_ClockRegression_RearmNeedsKeys()
        {
            var anomaly = new StrategyAnomalyInterlock();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            anomaly.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            Assert.True(mask.Seal(out _));
            var cb = new SignalChurnCircuitBreaker(anomaly, softPerMinute: 3, hardPerMinute: 5);
            long t = 10_000_000;
            for (int i = 0; i < 3; i++) Assert.Equal(ChurnVerdict.Allowed, cb.OnApproved(t += 1_000_000));
            Assert.Equal(ChurnVerdict.Suppressed, cb.OnApproved(t += 1_000_000));          // 4 > 3
            Assert.Equal(ChurnVerdict.Suppressed, cb.OnApproved(t += 1_000_000));          // 5
            Assert.False(anomaly.Tripped);
            // 60초 창 만료 → 다시 허용(일시 몰림 자동 해소)
            t += 61_000_000;
            Assert.Equal(ChurnVerdict.Allowed, cb.OnApproved(t));
            Assert.Equal(1, cb.LastWindowCount);
            Assert.Equal(ChurnVerdict.ClockRegressed, cb.OnApproved(t - 2_000_000));
            // 하드: 억제 중에도 승인은 계수 → 6 > 5 = 모델 붕괴 · 비트 27
            for (int i = 0; i < 4; i++) cb.OnApproved(t += 100_000);
            Assert.Equal(ChurnVerdict.Breakdown, cb.OnApproved(t += 100_000));
            Assert.True(anomaly.Tripped);
            Assert.Equal(StrategyAnomalyKind.ModelBreakdown, anomaly.FirstCause);
            Assert.True(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(1, anomaly.ModelBreakdowns);
            Assert.False(cb.TryRearm(true, false));
            Assert.True(anomaly.Tripped);
            Assert.True(cb.TryRearm(true, true));
            Assert.False(anomaly.Tripped);
            Assert.False(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(0, cb.LastWindowCount);
            Assert.Equal(ChurnVerdict.Allowed, cb.OnApproved(t += 100_000));               // 창 소거 = 즉시 재격발 없음
            Assert.Throws<ArgumentOutOfRangeException>(() => new SignalChurnCircuitBreaker(anomaly, 5, 5));
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 100; i++) cb.OnApproved(t); }));
        }

        [Fact]
        public void Churn_DryRunChain_ModelBreakdown_DisarmsAndStopsEgress()
        {
            using var h = new MultiBrokerDryRunHarness(churnSoftPerMinute: 2, churnHardPerMinute: 4);
            h.SetPhysicalKeys(true, true); h.SetCaps(60, 40);
            long placesAt2 = -1;
            for (int c = 0; c < 12; c++)
            {
                Ramp(h, +1, Mid + c * Tick);
                h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + (c + 1) * Tick);
                h.Pump();
                h.Advance(50_000);
                if (c == 1) placesAt2 = h.InnerPlaces;
            }
            var churn = h.Signals.Churn!;
            _out.WriteLine($"[붕괴] 승인 {churn.Approved} · 허용 {churn.Allowed} · 억제 {churn.Suppressed} · 붕괴 {churn.Breakdowns} · 송출 {h.InnerPlaces} · 거부 {h.Signals.RejectedChurn}");
            Assert.Equal(2, churn.Allowed);                                                  // 소프트 2 까지만 통과
            Assert.True(placesAt2 > 0);
            Assert.Equal(placesAt2, h.InnerPlaces);                                           // 이후 송출 0
            Assert.True(h.Anomaly.Tripped);
            Assert.Equal(StrategyAnomalyKind.ModelBreakdown, h.Anomaly.FirstCause);
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(0, h.UnsanctionedPlaces);
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void HotPath_ZeroAlloc_OnBook_Admit_HubPublish_Labels()
        {
            var anomaly = new StrategyAnomalyInterlock();
            long now = 1_000_000;
            var hub = new DashboardSnapshotHub();
            var cal = new VolatilityAdaptiveCalibrator();
            var churn = new SignalChurnCircuitBreaker(anomaly, 1_000_000, 2_000_000);
            hub.AttachCalibration(cal, churn);
            var q = new QuantSignalProvider(anomaly, hub, nowMicros: () => now, calibrator: cal, churn: churn);
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int i = 0; i < 10; i++) { b.BidPrice[i] = Mid - Tick * i; b.AskPrice[i] = Mid + Tick * (i + 1); b.BidQty[i] = 950; b.AskQty[i] = 50; }
            var p = new OrderProposal(1, 0, Sym, OrderSide.Buy, 1, new QPrice(Mid * 100_000_000L), new QQty(10 * 100_000_000L), 0);
            for (int i = 0; i < 200; i++) { b.BidQty[0] = 900 + i % 100; q.OnBook(in b); q.AdmitSignal(in p); now += 1_000; }   // 워밍업 · JIT
            long onBook = MinAllocated(() => { for (int i = 0; i < 1_000; i++) { b.BidQty[0] = 900 + i % 100; b.AskPrice[0] = Mid + Tick * (1 + i % 2); q.OnBook(in b); now += 1_000; } });
            long admit = MinAllocated(() => { for (int i = 0; i < 1_000; i++) { q.AdmitSignal(in p); now += 1_000; } });
            long publish = MinAllocated(() => { for (int i = 0; i < 1_000; i++) hub.PublishCalibration(Sym, i % 4, 8_500 + i % 3 * 500); });
            long labels = MinAllocated(() =>
            {
                for (int i = 0; i < 1_000; i++)
                {
                    _ = SignalLabelMapper.RegimeLabel(i % 6); _ = SignalLabelMapper.RegimeClass(i % 6);
                    _ = SignalLabelMapper.RequiredText(i * 10); _ = SignalLabelMapper.Reason(16);
                }
            });
            _out.WriteLine($"[δ=0] OnBook {onBook}B · Admit {admit}B · 허브 통지 {publish}B · 라벨 {labels}B · 캘리브레이션 사출 {q.CalibrationsPublished}");
            Assert.Equal(0, onBook);
            Assert.Equal(0, admit);
            Assert.Equal(0, publish);
            Assert.Equal(0, labels);
            Assert.True(q.CalibrationsPublished > 0);
        }

        // ════════════ §6 관제 ════════════

        [Fact]
        public void Hub_Frame_Unbound_Warmup_Notified_Churn()
        {
            long ticks = 1;
            var unbound = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            Assert.True(unbound.Pump());
            var f0 = unbound.Acquire()!;
            Assert.False(f0.CalibrationBound);
            Assert.Equal(DashRegime.Unbound, f0.Regime);
            Assert.Equal("미결선", SignalLabelMapper.RegimeLabel(f0.Regime));
            unbound.Release(f0);

            var anomaly = new StrategyAnomalyInterlock();
            var hub = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            var churn = new SignalChurnCircuitBreaker(anomaly, 3, 5);
            hub.AttachCalibration(new VolatilityAdaptiveCalibrator(), churn);
            Assert.True(hub.Pump());
            var f1 = hub.Acquire()!;
            Assert.True(f1.CalibrationBound && f1.ChurnBound);
            Assert.Equal((DashRegime.Warmup, 9_500, 8_500), (f1.Regime, f1.RequiredConfidenceBp, f1.BaseConfidenceBp));   // 관측 전 = 최대
            Assert.Equal((3, 5), (f1.ChurnSoft, f1.ChurnHard));
            hub.Release(f1);

            hub.PublishCalibration(5930, (int)MicroRegime.Choppy, 9_500);
            churn.OnApproved(1_000_000); churn.OnApproved(1_100_000);
            Assert.True(hub.Pump());
            var f2 = hub.Acquire()!;
            Assert.Equal((DashRegime.Choppy, 9_500, 5930L, 2L), (f2.Regime, f2.RequiredConfidenceBp, f2.CalibrationSymbol, f2.ChurnWindow));
            Assert.Equal("횡보(노이즈)", SignalLabelMapper.RegimeLabel(f2.Regime));
            Assert.Equal("chop", SignalLabelMapper.RegimeClass(f2.Regime));
            Assert.Equal("95%", SignalLabelMapper.RequiredText(f2.RequiredConfidenceBp));
            hub.Release(f2);
            hub.PublishCalibration(5930, (int)MicroRegime.Trend, 8_500);
            Assert.True(hub.Pump());                                                          // 통지 = 변경 감지(게시)
            var f3 = hub.Acquire()!;
            Assert.Equal((DashRegime.Trend, 8_500), (f3.Regime, f3.RequiredConfidenceBp));
            hub.Release(f3);
        }

        [Fact]
        public void Labels_RegimeCodes_MatchEngineEnum_ReasonTableSynced()
        {
            foreach (MicroRegime r in Enum.GetValues<MicroRegime>())
            {
                Assert.NotEqual(SignalLabelMapper.Unknown, SignalLabelMapper.RegimeLabel((int)r));
                Assert.NotEqual(SignalLabelMapper.RegimeLabel(DashRegime.Unbound), SignalLabelMapper.RegimeLabel((int)r));
            }
            Assert.Equal((0, 1, 2, 3), (DashRegime.Warmup, DashRegime.Trend, DashRegime.MeanReverting, DashRegime.Choppy));
            Assert.Equal(((int)MicroRegime.Warmup, (int)MicroRegime.Trend, (int)MicroRegime.MeanReverting, (int)MicroRegime.Choppy),
                         (DashRegime.Warmup, DashRegime.Trend, DashRegime.MeanReverting, DashRegime.Choppy));
            Assert.Equal(Enum.GetValues<MicroRegime>().Length + 1, SignalLabelMapper.RegimeCount);
            Assert.Equal(Enum.GetValues<QuantReason>().Length, SignalLabelMapper.ReasonCount);
            Assert.Equal("국면 임계 미달 — 전방 거부/관망", SignalLabelMapper.Reason((int)QuantReason.RegimeThresholdNotMet));
            Assert.Equal("-", SignalLabelMapper.RequiredText(0));
        }

        // ════════════ §7 회귀 ════════════

        [Fact]
        public void HostWiring_FiveBuilds_Ratchet_InterlockSpecUnchanged()
        {
            string root = RepoRoot();
            string host = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("calibrator: adaptiveCalibrator, churn: signalChurn);", host);
            Assert.Contains("dashHub.AttachCalibration(adaptiveCalibrator, signalChurn);", host);
            Assert.Contains("cfg.GetBool(\"Strategy.AdaptiveThreshold\", true)", host);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            // 신규 인터락 비트 없음 — 모델 붕괴는 기존 비트 27(StrategyAnomaly) · 출하 명세(30 · $ExpectedInterlocks) 유지
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);
            // 엔진 파일 부동소수점 0(정수 · 고정소수점 표준)
            string eng = File.ReadAllText(Path.Combine(root, "StockManager", "Engines", "VolatilityAdaptiveCalibrator.cs"));
            string code = string.Join("\n", eng.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
            foreach (var banned in new[] { "double", "float", "Math.Pow", "Math.Sqrt", "Math.Log", "decimal" })
                Assert.DoesNotContain(banned, code);
        }
    }
}
