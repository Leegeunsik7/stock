// ============================================================================
// MultiSymbolAiSignalRouterTests — [v12.18.45-DEV] 다종목 AI 신호 격리 · 전역 비트 27 동결 · KRX 호가 보호 정렬 · δ=0
//
//   §1 격리: A 매수 ↔ B 매도 교차 스트림 = 단일 관문은 플립플롭 오탐 래치 · 라우터는 래치 0(대조) · 종목별 링/시계/계수 독립 ·
//      A 반전 2회 + B 반전 2회(합 4 > 임계 3) = 래치 0 · 64종목 동시 운용 · 슬롯 소진 = 신규만 거부(기존 무영향)
//   §2 전역 동결: A 플립플롭 / NaN / 범위 이탈 → 0x08000000 래치 → B · C 및 미배정 신규 종목까지 Latched(발주 후보 0) · 이중키 재무장 + 전 슬롯 링 소거
//   §3 정렬: 매수 내림 · 매도 올림 · 격자 무변경 · 7구간 경계 · 결과 = KrxTickLadder.IsOnTick · 참조가 ≤0/포화 = InvalidPrice(이력 무변경)
//   §4 입력: 종목 코드 범위 · 티커 파서 · 판정 서수 핀 · 생성 인자 검증
//   §5 δ=0: 32종목 100만 라우팅+사이징+정렬 힙 0B(Release · 3창 최솟값) · 적대 혼합 다종목 스트림 = 래치 후 승인 0 · 승인 = 항상 격자
// ============================================================================
using System;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Engines;
using StockManager.Engines.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Strategy
{
    [Collection("ConcurrencyStress")]
    public sealed class MultiSymbolAiSignalRouterTests
    {
        private const long Bit27 = 0x08000000L;
        private const long S = KrxTickAligner.Scale;
        private const long A = 5930, B = 660, C = 35420;
        private static readonly long Px = 70_000 * S;
        private readonly ITestOutputHelper _out;
        public MultiSymbolAiSignalRouterTests(ITestOutputHelper o) => _out = o;

        private static (MultiSymbolAiSignalRouter R, StrategyAnomalyInterlock An, ArmingInterlockBitmask M) Make(int slots = 8, long maxQty = 1_000, long lot = 10)
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            var a = new StrategyAnomalyInterlock();
            a.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            return (new MultiSymbolAiSignalRouter(a, slots, maxQty, lot), a, mask);
        }

        private static AiSignal Sig(long sym, double c, long t) => new(sym, c, 0.5, 0.5, t);
        private static bool Raised(ArmingInterlockBitmask m) => (m.Mask & Bit27) == Bit27;

        // ════════════ §1 격리 ════════════

        [Fact]
        public void Cross_Symbol_Opposite_Sides_Trip_Single_Governor_But_Not_Router()
        {
            var (r, _, m) = Make();
            var lone = new AiSignalGovernor(new StrategyAnomalyInterlock(), 1_000, 10);   // 대조군: 종목 구분 없는 단일 관문
            var loneVerdict = AiSignalVerdict.Accepted;
            for (int i = 0; i < 10; i++)
            {
                long t = i * 1_000;
                var s = Sig(i % 2 == 0 ? A : B, i % 2 == 0 ? 0.9 : -0.9, t);                // A 매수 · B 매도 교대(각 종목은 방향 일정)
                if (loneVerdict != AiSignalVerdict.FlipFlop && loneVerdict != AiSignalVerdict.Latched) loneVerdict = lone.Evaluate(s, t, out _);
                Assert.Equal(AiRouteVerdict.Accepted, r.Route(s, Px, t, out _));
            }
            Assert.Equal(AiSignalVerdict.FlipFlop, loneVerdict);                              // 단일 관문 = 교차 종목 간섭 오탐
            Assert.False(Raised(m));                                                         // 라우터 = 간섭 0
            Assert.Equal(0, r.GovernorFor(A)!.Reversals);
            Assert.Equal(0, r.GovernorFor(B)!.Reversals);
            Assert.Equal(2, r.SymbolsInUse);
        }

        [Fact]
        public void Reversal_Counts_Do_Not_Pool_Across_Symbols()
        {
            var (r, _, m) = Make();
            long t = 0;
            foreach (var (sym, c) in new[] { (A, 0.9), (B, 0.9), (A, -0.9), (B, -0.9), (A, 0.9), (B, 0.9) })   // A 반전 2 · B 반전 2 (각 < 3)
                Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(sym, c, t), Px, t += 1_000, out _));
            Assert.False(Raised(m));
            Assert.Equal((2L, 2L), (r.GovernorFor(A)!.Reversals, r.GovernorFor(B)!.Reversals));
        }

        [Fact]
        public void Per_Symbol_Clock_And_Staleness_Are_Independent()
        {
            var (r, _, _) = Make();
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, 0.9, 50_000), Px, 50_000, out _));
            Assert.Equal(AiRouteVerdict.Stale, r.Route(Sig(B, 0.9, 10_000), Px, 50_000, out _));       // B 만 40ms 지연 = B 폐기
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, 0.9, 51_000), Px, 51_000, out _));    // A 무영향
            Assert.Equal(1, r.GovernorFor(B)!.StaleDrops);
            Assert.Equal(0, r.GovernorFor(A)!.StaleDrops);
        }

        [Fact]
        public void SixtyFour_Symbols_Then_Exhaustion_Rejects_Only_New()
        {
            var (r, _, m) = Make(slots: MultiSymbolAiSignalRouter.MaxSlots);
            for (int i = 0; i < 64; i++)
                Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(100_000 + i * 7, i % 2 == 0 ? 0.8 : -0.8, i), Px, i, out var o));
            Assert.Equal(64, r.SymbolsInUse);
            Assert.Equal(AiRouteVerdict.SlotsExhausted, r.Route(Sig(999_999, 0.8, 100), Px, 100, out var none));
            Assert.Equal(0, none.Quantity);
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(100_000, 0.8, 101), Px, 101, out _));     // 기존 종목 계속
            Assert.False(Raised(m));
            for (int i = 0; i < 64; i++) Assert.Equal(100_000 + i * 7, r.SymbolAt(i));
            Assert.Equal(0, r.SymbolAt(64));
        }

        // ════════════ §2 전역 동결 ════════════

        [Fact]
        public void FlipFlop_On_A_Latches_Bit27_And_Freezes_B_C_And_New_Symbols()
        {
            var (r, an, m) = Make();
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(B, 0.9, 0), Px, 0, out _));
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, 0.9, 1_000), Px, 1_000, out _));
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, -0.9, 2_000), Px, 2_000, out _));
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, 0.9, 3_000), Px, 3_000, out _));
            Assert.False(Raised(m));
            Assert.Equal(AiRouteVerdict.FlipFlop, r.Route(Sig(A, -0.9, 4_000), Px, 4_000, out var o));   // ★A 3번째 반전 = 그 회차 래치
            Assert.Equal(0, o.Quantity);
            Assert.True(Raised(m));
            Assert.Equal(StrategyAnomalyKind.SignalFlipFlop, an.FirstCause);
            Assert.Equal(AiRouteVerdict.Latched, r.Route(Sig(B, 0.9, 5_000), Px, 5_000, out o));         // B 후속 = 전면 차단
            Assert.Equal(0, o.Quantity);
            Assert.Equal(AiRouteVerdict.Latched, r.Route(Sig(C, 0.9, 5_001), Px, 5_001, out _));         // 미배정 신규 종목도 차단
            Assert.Equal(2, r.SymbolsInUse);                                                             // 래치 중 슬롯 배정 0
            Assert.Equal(2, r.LatchedRejects);
        }

        [Theory]
        [InlineData(double.NaN, StrategyAnomalyKind.NonFinite, AiRouteVerdict.NonFinite)]
        [InlineData(double.PositiveInfinity, StrategyAnomalyKind.NonFinite, AiRouteVerdict.NonFinite)]
        [InlineData(1.5, StrategyAnomalyKind.OutOfRange, AiRouteVerdict.OutOfRange)]
        public void Numeric_Anomaly_On_One_Symbol_Freezes_All(double c, StrategyAnomalyKind kind, AiRouteVerdict v)
        {
            var (r, an, m) = Make();
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(B, 0.9, 0), Px, 0, out _));
            Assert.Equal(v, r.Route(Sig(A, c, 1), Px, 1, out _));
            Assert.True(Raised(m));
            Assert.Equal(kind, an.FirstCause);
            Assert.Equal(AiRouteVerdict.Latched, r.Route(Sig(B, 0.9, 2), Px, 2, out _));
        }

        [Fact]
        public void Rearm_Requires_DualKey_And_Clears_Every_Slot_Ring()
        {
            var (r, _, m) = Make();
            long t = 0;
            foreach (var c in new[] { 0.9, -0.9, 0.9 }) r.Route(Sig(B, c, t), Px, t += 1_000, out _);   // B 반전 2(임계 직전)
            foreach (var c in new[] { 0.9, -0.9, 0.9, -0.9 }) r.Route(Sig(A, c, t), Px, t += 1_000, out _);   // A 래치
            Assert.True(Raised(m));
            Assert.False(r.TryRearm(true, false));
            Assert.False(r.TryRearm(false, true));
            Assert.True(Raised(m));
            Assert.True(r.TryRearm(true, true));
            Assert.False(Raised(m));
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(B, -0.9, t), Px, t += 1_000, out _));     // B 링 소거됨 → 즉시 재격발 0
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(B, 0.9, t), Px, t += 1_000, out _));
            Assert.False(Raised(m));
            Assert.Equal(2, r.SymbolsInUse);                                                             // 배정 유지
        }

        // ════════════ §3 정렬 ════════════

        [Theory]
        [InlineData(70_050, 0.9, 70_000)]     // 100원 구간 매수 내림
        [InlineData(70_050, -0.9, 70_100)]    // 매도 올림
        [InlineData(70_000, 0.9, 70_000)]     // 격자 무변경
        [InlineData(70_000, -0.9, 70_000)]
        [InlineData(1_999, -0.9, 1_999)]      // 1원 구간
        [InlineData(2_003, 0.9, 2_000)]       // 5원 구간
        [InlineData(4_997, -0.9, 5_000)]      // 5원 → 경계 올림(5,000 = 10원 격자점)
        [InlineData(19_995, 0.9, 19_990)]     // 10원 구간
        [InlineData(49_990, -0.9, 50_000)]    // 50원 → 경계 올림
        [InlineData(199_950, 0.9, 199_900)]   // 100원 구간
        [InlineData(250_200, -0.9, 250_500)]  // 500원 구간
        [InlineData(500_001, 0.9, 500_000)]   // 1,000원 구간
        [InlineData(1_234_567, -0.9, 1_235_000)]
        public void Accepted_Order_Is_Snapped_Protectively_To_Krx_2023_Grid(long refWon, double c, long expectWon)
        {
            var (r, _, _) = Make();
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, c, 0), refWon * S, 0, out var o));
            Assert.Equal(expectWon * S, o.LimitRaw);
            Assert.True(KrxTickLadder.IsOnTick(o.LimitRaw / S));                                         // decimal 사다리와 교차 확인
            Assert.Equal(0, o.LimitRaw % S);
            if (c > 0) Assert.True(o.LimitRaw <= refWon * S); else Assert.True(o.LimitRaw >= refWon * S);   // 가격 한도 넘는 쪽 이동 0
            Assert.Equal(c > 0 ? 1 : -1, o.Side);
            Assert.Equal(900, o.Quantity);
        }

        [Fact]
        public void Sub_Won_Reference_Is_Snapped_Too()
        {
            var (r, _, _) = Make();
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, 0.9, 0), 70_000 * S + 1, 0, out var o));   // 70,000.00000001원
            Assert.Equal(70_000 * S, o.LimitRaw);
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(B, -0.9, 0), 70_000 * S + 1, 0, out o));
            Assert.Equal(70_100 * S, o.LimitRaw);
            Assert.Equal((1L, 1L), (r.SnappedDown, r.SnappedUp));
        }

        [Fact]
        public void Invalid_Reference_Price_Rejects_Without_Touching_Signal_History()
        {
            var (r, _, m) = Make();
            Assert.Equal(AiRouteVerdict.InvalidPrice, r.Route(Sig(A, 0.9, 0), 0, 0, out var o));
            Assert.Equal(AiRouteVerdict.InvalidPrice, r.Route(Sig(A, -0.9, 1), -5 * S, 1, out _));
            Assert.Equal(AiRouteVerdict.InvalidPrice, r.Route(Sig(A, -0.9, 2), long.MaxValue, 2, out _));   // 올림 포화
            Assert.Equal(0, o.Quantity);
            Assert.Equal(3, r.InvalidPriceRejects);
            Assert.False(Raised(m));
            Assert.Null(r.GovernorFor(A));                                                                // 평가 전 거부 = 슬롯 배정 · 이력 0
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, 0.9, 3), Px, 3, out o));
            Assert.Equal(AiRouteVerdict.Accepted, r.Route(Sig(A, -0.9, 4), Px, 4, out _));              // 반전 1(거부 3건은 이력 무영향)
            Assert.Equal(1, r.GovernorFor(A)!.Reversals);
        }

        // ════════════ §4 입력 ════════════

        [Theory]
        [InlineData(0L)]
        [InlineData(-5930L)]
        [InlineData(1_000_000L)]
        public void Out_Of_Range_Symbol_Code_Rejected(long sym)
        {
            var (r, _, m) = Make();
            Assert.Equal(AiRouteVerdict.InvalidSymbol, r.Route(Sig(sym, 0.9, 0), Px, 0, out _));
            Assert.Equal(0, r.SymbolsInUse);
            Assert.False(Raised(m));
        }

        [Theory]
        [InlineData("005930", 5930L)]
        [InlineData("A005930", 5930L)]
        [InlineData(" 000660 ", 660L)]
        [InlineData("999999", 999_999L)]
        [InlineData("05930", 0L)]
        [InlineData("0059300", 0L)]
        [InlineData("00593X", 0L)]
        [InlineData("B005930", 0L)]
        [InlineData("", 0L)]
        [InlineData("-05930", 0L)]
        public void ParseTicker_Krx_Six_Digits(string s, long expected) => Assert.Equal(expected, MultiSymbolAiSignalRouter.ParseTicker(s));

        [Fact]
        public void Verdict_Ordinals_Match_Governor()
        {
            foreach (AiSignalVerdict v in Enum.GetValues<AiSignalVerdict>())
                Assert.Equal(v.ToString(), ((AiRouteVerdict)(byte)v).ToString());
        }

        [Fact]
        public void Ctor_Rejects_Invalid_Config()
        {
            var a = new StrategyAnomalyInterlock();
            Assert.Throws<ArgumentNullException>(() => new MultiSymbolAiSignalRouter(null!, 4, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultiSymbolAiSignalRouter(a, 0, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultiSymbolAiSignalRouter(a, MultiSymbolAiSignalRouter.MaxSlots + 1, 10));
            Assert.Throws<ArgumentOutOfRangeException>(() => new MultiSymbolAiSignalRouter(a, 4, 0));      // 관문 검증 전파
        }

        // ════════════ §5 δ=0 · 적대 ════════════

        [Fact]
        public void Million_Multi_Symbol_Routes_Sizing_And_Snap_ZeroAlloc()
        {
            var (r, _, m) = Make(slots: 32, maxQty: 10_000, lot: 1);
            var syms = new long[32];
            var refs = new long[32];
            for (int k = 0; k < 32; k++) { syms[k] = 100_000 + k * 13; refs[k] = (1_500 + k * 15_331) * S + k * 37; }
            long sink = 0, t = 0;
            void Run()
            {
                for (int i = 0; i < 1_000_000; i++)
                {
                    int k = i & 31;
                    t += 40;                                                                     // 종목당 1.28ms 간격 · 방향은 종목별 고정(무반전)
                    double c = (k & 1) == 0 ? 0.2 + (i & 1023) / 1280.0 : -(0.2 + (i & 511) / 640.0);
                    if (r.Route(new AiSignal(syms[k], c, 0.5, 0.5, t - 200), refs[k], t, out var o) == AiRouteVerdict.Accepted) sink += o.Quantity + (o.LimitRaw & 0xFF);
                }
            }
            Run();                                                                               // JIT 예열 + 슬롯 배정
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Run();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            _out.WriteLine($"[δ] 32종목 100만 라우팅+사이징+정렬 × 3창 최소 = {min}B · 승인 {r.AcceptedCount} · 내림 {r.SnappedDown} · 올림 {r.SnappedUp} · sink {sink}");
            Assert.False(Raised(m));
            Assert.Equal(32, r.SymbolsInUse);
            Assert.Equal(4_000_000, r.AcceptedCount);
            Assert.Equal(0, min);
        }

        [Fact]
        public void Adversarial_Multi_Symbol_Stream_Never_Leaks_After_Latch_And_Always_On_Grid()
        {
            var rng = new Random(20261021);
            double[] poison = { double.NaN, double.NegativeInfinity, 1.01 };
            int latchedRuns = 0, accepted = 0;
            for (int run = 0; run < 200; run++)
            {
                var (r, an, m) = Make(slots: 16);
                long t = 0;
                bool tripped = false;
                for (int i = 0; i < 400; i++)
                {
                    t += rng.Next(1, 20_000);
                    long sym = 1 + rng.Next(20) * 1_111;                                              // 20종목(슬롯 16 → 일부 소진)
                    double c = rng.Next(40) == 0 ? poison[rng.Next(poison.Length)] : rng.NextDouble() * 2 - 1;
                    long refRaw = rng.Next(10) == 0 ? -1 : (long)(rng.Next(1_000, 900_000)) * S + rng.Next(0, (int)S);
                    var v = r.Route(new AiSignal(sym, c, 0.5, 0.5, t - rng.Next(0, 35_000)), refRaw, t, out var o);
                    if (v == AiRouteVerdict.Accepted)
                    {
                        Assert.False(tripped);
                        Assert.True(KrxTickLadder.IsOnTick(o.LimitRaw / S) && o.LimitRaw % S == 0);
                        Assert.True(o.Side > 0 ? o.LimitRaw <= refRaw : o.LimitRaw >= refRaw);
                        Assert.True(o.Quantity > 0 && o.Quantity % 10 == 0);
                        accepted++;
                    }
                    else Assert.Equal(0, o.Quantity);
                    if (v is AiRouteVerdict.NonFinite or AiRouteVerdict.OutOfRange or AiRouteVerdict.FlipFlop) { Assert.True(Raised(m)); tripped = true; }
                    Assert.Equal(an.Tripped, tripped);
                }
                if (tripped) latchedRuns++;
            }
            _out.WriteLine($"[적대] 200회 × 400신호 · 래치 발생 {latchedRuns}회 · 래치 전 승인 {accepted}건(전부 격자 · 보호 방향)");
            Assert.True(latchedRuns > 0);
            Assert.True(accepted > 200);                                                                  // 격자 단언이 공허하지 않음
        }
    }
}
