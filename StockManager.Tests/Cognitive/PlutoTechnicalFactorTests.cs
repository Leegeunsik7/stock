// ============================================================================
// PlutoTechnicalFactorTests — v12.18.73-DEV: 플루토 브레인 3단계(볼린저 스퀴즈 · 매물대 POC 프로파일 · 기술적 팩터 · 3대 아레나 결속) 검증
//
//   ① 정수 제곱근 · 볼린저/켈트너/ATR/밴드폭 산출 · 스퀴즈 판정 · 연속 응축 봉 · 해제 창 · 상/하방 돌파 가산점(+20~+40).
//   ② 프로파일 격자(폭 25bp · 이동 · 내림 나눗셈 · 대점프 재격자) · POC · 밸류 에어리어 70% · 구역별 점수 · 감쇄 · 포화 절반 축소.
//   ③ 1분 봉 시계 · 합성 점수 · 무결성 스윕 · 악마의 변호인 ④ POC 매물벽(감점 · 거부권 · 거버너 태그/문장).
//   ④ 3대 아레나: 시그널 가산/주도 사유 · 응축 → 돌파 시나리오 TryReadTechnical · 근거 카드 뱃지/문장 · 정지 = 기여 0.
//   ⑤ δ=0: 분석기 10만 회 · KRX 정산 10만 회(기술 가동) · 판독 10만 회 = 0B(MinAllocated · Release).
// ============================================================================
using System;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Regime;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분
using StockManager.Engines.Cognitive.Technical;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoTechnicalFactorTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Kst10 = new(2026, 10, 22, 10, 0, 0);            // 목 10:00 KST(KRX 정규장)
        private static readonly DateTime KstUsReg = new(2026, 10, 22, 23, 0, 0);         // 목 10:00 EDT(미국 정규장)
        private static readonly long Nvda = UsTechUniverse.Pack("NVDA");
        private const long S = UpbitTickAligner.Scale;
        private static readonly long Sat = (new DateTime(2026, 10, 24, 3, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
        private readonly ITestOutputHelper _out;

        public PlutoTechnicalFactorTests(ITestOutputHelper o) => _out = o;

        // ── ① 볼린저 스퀴즈 ─────────────────────────────────────────────────────

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(3, 1)]
        [InlineData(4, 2)]
        [InlineData(99, 9)]
        [InlineData(100, 10)]
        [InlineData(1_000_000_000_000_000_000, 1_000_000_000)]
        public void ISqrt_Is_Exact_Floor(long v, long expected) => Assert.Equal(expected, PlutoVolatilitySqueezeAnalyzer.ISqrt(v));

        [Fact]
        public void ISqrt_Large_Int128_Is_Floor_Root()
        {
            var rng = new Random(73);
            for (int i = 0; i < 2000; i++)
            {
                long r = rng.NextInt64(1, 3_000_000_000_000_000_000);
                Int128 sq = (Int128)r * r;
                Assert.Equal(r, PlutoVolatilitySqueezeAnalyzer.ISqrt(sq));
                Assert.Equal(r, PlutoVolatilitySqueezeAnalyzer.ISqrt(sq + r));            // (r+1)² − 1 = r² + 2r 미만 → 여전히 r
                Assert.Equal(r - 1, PlutoVolatilitySqueezeAnalyzer.ISqrt(sq - 1));
            }
            Assert.Equal(0, PlutoVolatilitySqueezeAnalyzer.ISqrt(-5));
        }

        /// <summary>종가 100/102 교대 · 고저 ±5 → SMA 101 · σ 1 · TR 10 → 2σ(2) &lt; 1.5 ATR(15) = 응축.</summary>
        private static PlutoSqueezeState Tight(int bars)
        {
            var s = new PlutoSqueezeState();
            for (int i = 0; i < bars; i++)
            {
                long c = i % 2 == 0 ? 100 : 102;
                PlutoVolatilitySqueezeAnalyzer.Push(ref s, c + 5, c - 5, c);
            }
            return s;
        }

        [Fact]
        public void Bollinger_Keltner_Atr_And_BandWidth_Are_Exact()
        {
            var s = Tight(19);
            Assert.False(s.Ready);
            Assert.Equal(PlutoSqueezePhase.Unknown, PlutoVolatilitySqueezeAnalyzer.Read(in s, 101).Phase);
            PlutoVolatilitySqueezeAnalyzer.Push(ref s, 107, 97, 102);
            Assert.True(s.Ready);
            Assert.Equal((101L, 1L, 10L), (s.Mid, s.Sigma, s.Atr));
            var r = PlutoVolatilitySqueezeAnalyzer.Read(in s, 101);
            Assert.Equal((103L, 99L, 116L, 86L), (r.Upper, r.Lower, r.KeltnerUpper, r.KeltnerLower));   // 켈트너 = 101 ± 15
            Assert.Equal(4 * 10000 / 101, r.BandWidthBps);
            Assert.True(r.IsSqueezing);
            Assert.Equal(1, r.SqueezeBars);
            Assert.Equal(PlutoSqueezePhase.Squeezing, r.Phase);                         // 1 봉 = 미확립 · 응축 중 0점
            Assert.Equal(0, r.Score);
        }

        [Fact]
        public void Squeeze_Condition_Is_Two_Sigma_Inside_One_And_Half_Atr()
        {
            // 종가 ±100 교대 · 고저 = 종가 → σ 100 · ATR 190(첫 봉 TR 0) → 4σ 400 < 3 ATR 570 = 응축(종가 갭이 TR 을 키움).
            var s = new PlutoSqueezeState();
            for (int i = 0; i < 20; i++)
            {
                long c = 1000 + (i % 2 == 0 ? 100 : -100);
                PlutoVolatilitySqueezeAnalyzer.Push(ref s, c, c, c);
            }
            Assert.Equal((100L, 190L), (s.Sigma, s.Atr));
            Assert.True(s.Squeezing);
            // 고저 0 · 종가 동일 · 큰 분산 = TR 0 → 비응축(ATR 0)
            var flat = new PlutoSqueezeState();
            for (int i = 0; i < 20; i++) PlutoVolatilitySqueezeAnalyzer.Push(ref flat, 500, 500, 500);
            Assert.Equal((0L, 0L), (flat.Sigma, flat.Atr));
            Assert.False(flat.Squeezing);
            Assert.Equal(PlutoSqueezePhase.Neutral, PlutoVolatilitySqueezeAnalyzer.Read(in flat, 600).Phase);   // 미확립 → 돌파 판정 없음
        }

        [Fact]
        public void Wide_Bands_Over_Narrow_Ranges_Are_Not_Squeezing()
        {
            var s = new PlutoSqueezeState();
            for (int i = 0; i < 20; i++)
            {
                long c = 1000 + i * 50;                                                 // 추세 · σ ≈ 288 · TR = 50
                PlutoVolatilitySqueezeAnalyzer.Push(ref s, c, c, c);
            }
            Assert.False(s.Squeezing);
            Assert.Equal(0, s.SqueezeBars);
            Assert.Equal(PlutoSqueezePhase.Neutral, PlutoVolatilitySqueezeAnalyzer.Read(in s, s.Mid).Phase);
        }

        [Fact]
        public void Invalid_Bars_Are_Ignored()
        {
            var s = new PlutoSqueezeState();
            PlutoVolatilitySqueezeAnalyzer.Push(ref s, 10, 20, 15);                      // 고 < 저
            PlutoVolatilitySqueezeAnalyzer.Push(ref s, 10, 0, 5);                        // 저 ≤ 0
            PlutoVolatilitySqueezeAnalyzer.Push(ref s, 10, 5, 11);                       // 종가 범위 밖
            Assert.Equal(0, s.Count);
        }

        [Theory]
        [InlineData(1, 0)]
        [InlineData(2, 0)]
        [InlineData(3, 20)]
        [InlineData(4, 22)]
        [InlineData(13, 40)]
        [InlineData(500, 40)]
        public void Breakout_Score_Scales_With_Squeeze_Duration(int run, int expected) => Assert.Equal(expected, PlutoVolatilitySqueezeAnalyzer.BreakoutScore(run));

        [Fact]
        public void Breakout_Up_And_Down_After_Established_Squeeze()
        {
            var s = Tight(29);                                                          // 응축 10 봉
            Assert.Equal(10, s.SqueezeBars);
            var up = PlutoVolatilitySqueezeAnalyzer.Read(in s, 104);
            Assert.Equal((PlutoSqueezePhase.BreakoutUp, 34), (up.Phase, up.Score));      // 20 + 2 × (10 − 3)
            var dn = PlutoVolatilitySqueezeAnalyzer.Read(in s, 98);
            Assert.Equal((PlutoSqueezePhase.BreakoutDown, -34), (dn.Phase, dn.Score));
            var inside = PlutoVolatilitySqueezeAnalyzer.Read(in s, 103);                 // = 상단(초과 아님)
            Assert.Equal((PlutoSqueezePhase.Squeezing, 0), (inside.Phase, inside.Score));
        }

        [Fact]
        public void Release_Window_Keeps_Breakout_Armed_Then_Expires()
        {
            var s = Tight(29);
            s.Squeezing = false; s.LastSqueezeRun = s.SqueezeBars; s.SqueezeBars = 0; s.BarsSinceRelease = PlutoVolatilitySqueezeAnalyzer.ReleaseWindowBars;
            Assert.Equal(10, PlutoVolatilitySqueezeAnalyzer.ArmedRun(in s));
            Assert.Equal(PlutoSqueezePhase.BreakoutUp, PlutoVolatilitySqueezeAnalyzer.Read(in s, 104).Phase);
            s.BarsSinceRelease++;
            Assert.Equal(0, PlutoVolatilitySqueezeAnalyzer.ArmedRun(in s));
            Assert.Equal(PlutoSqueezePhase.Neutral, PlutoVolatilitySqueezeAnalyzer.Read(in s, 104).Phase);
        }

        [Fact]
        public void Squeeze_Release_Transition_Records_Run()
        {
            var s = Tight(25);                                                          // 응축 6 봉
            Assert.True(s.Squeezing);
            for (int i = 0; i < 20; i++) PlutoVolatilitySqueezeAnalyzer.Push(ref s, 2000 + i * 300, 2000 + i * 300, 2000 + i * 300);
            Assert.False(s.Squeezing);
            Assert.True(s.LastSqueezeRun >= 6);
            Assert.True(s.BarsSinceRelease > PlutoVolatilitySqueezeAnalyzer.ReleaseWindowBars);
        }

        [Fact]
        public void Crypto_Raw_Scale_Prices_Do_Not_Overflow()
        {
            var s = new PlutoSqueezeState();
            long b = 150_000_000L * S;                                                  // 1.5억 원 × 10^8
            for (int i = 0; i < 30; i++)
            {
                long c = b + (i % 2 == 0 ? 1000 * S : -1000 * S);
                PlutoVolatilitySqueezeAnalyzer.Push(ref s, c + 50_000 * S, c - 50_000 * S, c);
            }
            Assert.Equal(b, s.Mid);
            Assert.Equal(1000 * S, s.Sigma);
            Assert.True(s.Squeezing);
            var r = PlutoVolatilitySqueezeAnalyzer.Read(in s, b + 3000 * S);
            Assert.Equal(PlutoSqueezePhase.BreakoutUp, r.Phase);
        }

        // ── ② 매물대 프로파일 ───────────────────────────────────────────────────

        /// <summary>폭 25 · 기준 9588(16번 구간 = [9988, 10013) 중앙 10000) · 10000 × 20건(100) + 10050 × 10건(10).</summary>
        private static PlutoVolumeProfileState Profile()
        {
            var s = new PlutoVolumeProfileState();
            for (int i = 0; i < 20; i++) PlutoVolumeProfileScanner.Add(ref s, 10000, 100);
            for (int i = 0; i < 10; i++) PlutoVolumeProfileScanner.Add(ref s, 10050, 10);
            return s;
        }

        [Fact]
        public void Grid_Poc_ValueArea_And_Share_Are_Exact()
        {
            var s = Profile();
            Assert.Equal((25L, 9588L, 30), (s.Width, s.Base, s.Samples));
            Assert.Equal(2000, s.Volume[16]);
            Assert.Equal(100, s.Volume[18]);
            var r = PlutoVolumeProfileScanner.Read(in s, 10000);
            Assert.Equal((10000L, 10013L, 9988L), (r.PocPrice, r.ValueAreaHigh, r.ValueAreaLow));
            Assert.Equal(2000 * 10000 / 2100, r.PocShareBp);
            Assert.Equal(PlutoProfileZone.AtPoc, r.Zone);
        }

        [Theory]
        [InlineData(10005, PlutoProfileZone.AtPoc, 0)]
        [InlineData(10020, PlutoProfileZone.PocSupport, 88)]           // d = 80 → 100 − 55 × 50 / 225
        [InlineData(10006, PlutoProfileZone.AtPoc, 0)]                 // d = 24 → 균형점(사각대 25 미만)
        [InlineData(9990, PlutoProfileZone.PocResistance, -97)]        // d = −40 → −100 + 15 × 50 / 225
        [InlineData(10100, PlutoProfileZone.AboveValue, 50)]
        [InlineData(9900, PlutoProfileZone.BelowValue, -50)]
        public void Zones_And_Scores_Follow_Matrix(long price, PlutoProfileZone zone, int score)
        {
            var r = PlutoVolumeProfileScanner.Read(Profile(), price);
            Assert.Equal((zone, score), (r.Zone, r.Score));
        }

        [Fact]
        public void Classify_Boundaries_Are_Continuous_Linear()
        {
            Assert.Equal((PlutoProfileZone.AtPoc, 0), PlutoVolumeProfileScanner.Classify(24, false, false));
            Assert.Equal((PlutoProfileZone.PocSupport, 100), PlutoVolumeProfileScanner.Classify(25, false, false));
            Assert.Equal((PlutoProfileZone.PocSupport, 50), PlutoVolumeProfileScanner.Classify(250, false, false));
            Assert.Equal((PlutoProfileZone.UpperValue, 30), PlutoVolumeProfileScanner.Classify(251, false, false));
            Assert.Equal((PlutoProfileZone.AboveValue, 50), PlutoVolumeProfileScanner.Classify(251, true, false));
            Assert.Equal((PlutoProfileZone.AtPoc, 0), PlutoVolumeProfileScanner.Classify(-24, false, false));
            Assert.Equal((PlutoProfileZone.PocResistance, -100), PlutoVolumeProfileScanner.Classify(-25, false, false));
            Assert.Equal((PlutoProfileZone.PocResistance, -50), PlutoVolumeProfileScanner.Classify(-250, false, false));
            Assert.Equal((PlutoProfileZone.LowerValue, -30), PlutoVolumeProfileScanner.Classify(-251, false, false));
            Assert.Equal((PlutoProfileZone.BelowValue, -50), PlutoVolumeProfileScanner.Classify(-251, false, true));
        }

        [Fact]
        public void Diffuse_Profile_Weakens_Score_And_Value_Area_Spans_Seventy_Percent()
        {
            var s = new PlutoVolumeProfileState();
            for (int k = 0; k < 10; k++)
                for (int j = 0; j < 17; j++)
                {
                    int b = 8 + (j + 8) % 17;                                           // 첫 체결 = 16번(10000) → 격자 기준 9588 · 폭 25
                    PlutoVolumeProfileScanner.Add(ref s, 9588 + b * 25 + 12, b == 16 ? 30 : 10);
                }
            var r = PlutoVolumeProfileScanner.Read(in s, 10000 + 2 * 25);               // d = 200
            Int128 total = 0, inside = 0;
            for (int b = 0; b < PlutoVolumeProfileScanner.Bins; b++)
            {
                total += s.Volume[b];
                long lo = 9588 + b * 25;
                if (lo >= r.ValueAreaLow && lo < r.ValueAreaHigh) inside += s.Volume[b];
            }
            Assert.True(inside * 100 >= total * 70, $"VA {inside}/{total}");
            Assert.Equal(PlutoProfileZone.PocSupport, r.Zone);
            int strength = PlutoVolumeProfileScanner.StrengthPct(r.PocShareBp);
            Assert.True(strength < 100);
            Assert.Equal((100 - (200 - 25) * 50 / 225) * strength / 100, r.Score);
            var upper = PlutoVolumeProfileScanner.Read(in s, 10000 + 4 * 25);           // d = 400 · VAH 안
            Assert.Equal(PlutoProfileZone.UpperValue, upper.Zone);
        }

        [Fact]
        public void Too_Few_Samples_And_Zero_Weights_Yield_No_Reading()
        {
            var s = new PlutoVolumeProfileState();
            for (int i = 0; i < 29; i++) PlutoVolumeProfileScanner.Add(ref s, 10000, 5);
            for (int i = 0; i < 50; i++) PlutoVolumeProfileScanner.Add(ref s, 10000, 0);  // 거래량 0 = 표본 아님
            Assert.Equal(PlutoProfileZone.None, PlutoVolumeProfileScanner.Read(in s, 10000).Zone);
            PlutoVolumeProfileScanner.Add(ref s, 10000, 5);
            Assert.NotEqual(PlutoProfileZone.None, PlutoVolumeProfileScanner.Read(in s, 10000).Zone);
            Assert.Equal(PlutoProfileZone.None, PlutoVolumeProfileScanner.Read(in s, 0).Zone);
        }

        [Fact]
        public void Grid_Shifts_Up_Preserving_Volume_And_Drops_Overflow()
        {
            var s = Profile();
            PlutoVolumeProfileScanner.Add(ref s, 10400, 1);                             // 구간 32 → 16 칸 위로 이동
            Assert.Equal(9988, s.Base);
            Assert.Equal(2000, s.Volume[0]);                                            // 옛 16번 → 0번
            Assert.Equal(100, s.Volume[2]);
            Assert.Equal(1, s.Volume[16]);
            PlutoVolumeProfileScanner.Add(ref s, 10850, 1);                             // 구간 34 → 18 칸 → 옛 0번 탈락
            Assert.Equal(0, s.Volume[0]);
        }

        [Fact]
        public void Grid_Shifts_Down_With_Floor_Division_And_Regrids_On_Jump()
        {
            var s = Profile();
            PlutoVolumeProfileScanner.Add(ref s, 9500, 7);                              // (9500 − 9588)/25 = −3.52 → 내림 −4 → 20 칸 아래
            Assert.Equal(9588 - 20 * 25, s.Base);
            Assert.Equal(7, s.Volume[16]);
            long sum = 0;
            for (int i = 0; i < PlutoVolumeProfileScanner.Bins; i++) sum += s.Volume[i];
            Assert.Equal(7, sum);                                                       // 옛 16/18번 → 36/38(탈락)
            PlutoVolumeProfileScanner.Add(ref s, 20000, 3);                             // 대점프 = 재격자(폭 50)
            Assert.Equal(50, s.Width);
            Assert.Equal(1, s.Samples);
            Assert.Equal(3, s.Volume[16]);
        }

        [Fact]
        public void Decay_And_Saturation_Halving_Preserve_Ratios()
        {
            var s = Profile();
            PlutoVolumeProfileScanner.Decay(ref s);
            Assert.Equal(2000 - 2000 / 16, s.Volume[16]);
            Assert.Equal(100 - 100 / 16, s.Volume[18]);
            var big = new PlutoVolumeProfileState();
            PlutoVolumeProfileScanner.Add(ref big, 10000, long.MaxValue);              // 1건 상한 2^48
            Assert.Equal(PlutoVolumeProfileScanner.MaxWeight, big.Volume[16]);
            for (int i = 0; i < 300; i++) PlutoVolumeProfileScanner.Add(ref big, 10000, long.MaxValue);
            Assert.True(big.Volume[16] <= PlutoVolumeProfileScanner.BinCap);
            Assert.True(big.Volume[16] > 0);
        }

        // ── ③ 봉 시계 · 팩터 · 악마의 변호인 ───────────────────────────────────

        [Fact]
        public void Minute_Bar_Clock_Builds_Ohlc_And_Completes_On_Boundary()
        {
            var st = new PlutoTechnicalState();
            long t = Kst10.Ticks;
            PlutoTechnicalAnalyzer.OnPrice(ref st, 100, t);
            PlutoTechnicalAnalyzer.OnPrice(ref st, 105, t + TimeSpan.TicksPerSecond * 10);
            PlutoTechnicalAnalyzer.OnPrice(ref st, 97, t + TimeSpan.TicksPerSecond * 20);
            PlutoTechnicalAnalyzer.OnPrice(ref st, 101, t + TimeSpan.TicksPerSecond * 59);
            Assert.Equal((105L, 97L, 101L, 0L), (st.High, st.Low, st.Close, st.CompletedBars));
            PlutoTechnicalAnalyzer.OnPrice(ref st, 99, t + TimeSpan.TicksPerSecond * 5);    // 시각 역행 = 진행 봉 합산
            Assert.Equal(99, st.Close);
            PlutoTechnicalAnalyzer.OnPrice(ref st, 102, t + TimeSpan.TicksPerMinute);
            Assert.Equal(1, st.CompletedBars);
            Assert.Equal(1, st.Squeeze.Count);
            Assert.Equal(99, st.Squeeze.PrevClose);
            Assert.Equal(8, st.Squeeze.Bars[0].TrueRange);                              // 첫 봉 = 고 − 저
            Assert.Equal((102L, 102L, 102L), (st.High, st.Low, st.Close));
            PlutoTechnicalAnalyzer.OnPrice(ref st, 0, t + TimeSpan.TicksPerMinute * 5);    // 무효 가격 = 무시
            Assert.Equal(1, st.CompletedBars);
        }

        [Theory]
        [InlineData(40, 100, 90)]
        [InlineData(-40, -100, -90)]
        [InlineData(40, 50, 65)]
        [InlineData(0, -97, -48)]
        public void Combined_Score_Is_Squeeze_Plus_Half_Profile(int squeeze, int profile, int expected)
            => Assert.Equal(expected, PlutoTechnicalAnalyzer.Combine(squeeze, profile));

        [Fact]
        public void Assess_Outputs_Are_Well_Formed_Across_Random_Sweep()
        {
            var rng = new Random(1273);
            int squeezeSeen = 0, upSeen = 0, downSeen = 0, supSeen = 0, resSeen = 0;
            for (int trial = 0; trial < 40; trial++)
            {
                var st = new PlutoTechnicalState();
                long px = rng.Next(1_000, 2_000_000), t = Kst10.Ticks;
                int amp = rng.Next(1, 40);
                for (int i = 0; i < 1500; i++)
                {
                    t += TimeSpan.TicksPerSecond * rng.Next(1, 20);
                    px = Math.Max(10, px + px * rng.Next(-amp, amp + 1) / 10000);
                    PlutoTechnicalAnalyzer.OnPrice(ref st, px, t);
                    PlutoTechnicalAnalyzer.OnVolume(ref st, px, rng.Next(0, 500));
                    var f = PlutoTechnicalAnalyzer.Assess(in st, Samsung, px + px * rng.Next(-300, 301) / 10000);
                    Assert.True(f.IsWellFormed, $"trial {trial} i {i} {f.SqueezePhase} {f.ProfileZone} {f.SqueezeScore} {f.VolumeProfileScore}");
                    if (f.IsSqueezing) squeezeSeen++;
                    if (f.IsBreakoutUp) upSeen++;
                    if (f.IsBreakoutDown) downSeen++;
                    if (f.IsPocSupport) supSeen++;
                    if (f.IsPocResistance) resSeen++;
                }
            }
            _out.WriteLine($"squeeze={squeezeSeen} up={upSeen} down={downSeen} support={supSeen} resistance={resSeen}");
            Assert.True(squeezeSeen > 0 && upSeen > 0 && downSeen > 0 && supSeen > 0 && resSeen > 0);
            Assert.True(default(PlutoTechnicalFactor).IsWellFormed);
            Assert.False(default(PlutoTechnicalFactor).HasTechnical);
            Assert.False(PlutoTechnicalAnalyzer.Assess(new PlutoTechnicalState(), Samsung, 100).HasTechnical);
        }

        [Fact]
        public void Badges_Reflect_Factor_State()
        {
            var f = Breakout(Samsung, out _);
            string b = PlutoTechnicalText.Badges(in f);
            Assert.Contains(PlutoTechnicalText.SqueezeBadge, b);
            Assert.Contains(PlutoTechnicalText.BreakoutUpBadge, b);
            Assert.DoesNotContain(PlutoTechnicalText.PocResistanceBadge, b);
            Assert.Equal("", PlutoTechnicalText.Badges(default));
        }

        [Fact]
        public void Devils_Advocate_Poc_Wall_Penalizes_And_Joins_Veto()
        {
            var solo = PlutoDevilsAdvocateFilter.Assess(60, 50, 10000, 1000, 1000, 0, 0, pocResistance: true);
            Assert.Equal((CounterThesis.PocResistance, -20, 40, false), (solo.Theses, solo.Penalty, solo.Confidence, solo.Blocked));
            var veto = PlutoDevilsAdvocateFilter.Assess(90, 50, 10000, 1000, 3000, 0, 0, pocResistance: true);
            Assert.Equal((CounterThesis.OverheadSupplyWall | CounterThesis.PocResistance, -40, true), (veto.Theses, veto.Penalty, veto.Blocked));
            Assert.Equal(2, veto.Count);
            var off = PlutoDevilsAdvocateFilter.Assess(60, 50, 10000, 1000, 1000, 0, 0);
            Assert.Equal(CounterThesis.None, off.Theses);
        }

        [Fact]
        public void Governor_Tag_Carries_Poc_Thesis_And_Sentence()
        {
            var regime = new PlutoRegimeState();
            for (int i = 0; i < 20; i++) PlutoRegimeClassifier.Update(ref regime, 70_000, 70_100, 1000, 1000);
            Assert.Equal(MarketRegime.ChoppyRange, regime.Regime);
            var r = PlutoCognitiveGovernor.Approve(in regime, 6000, 0, 25, 0, 35, 3000, 1000, 1000, 0, 0, default, Kst10.Ticks, out var tag, pocResistance: true);
            Assert.Equal(PlutoBlockReason.None, r);
            Assert.Equal(CounterThesis.PocResistance, tag.Theses);
            Assert.Equal(-20, tag.Penalty);
            Assert.True(tag.IsWellFormed);
            Assert.Contains("POC 매물벽 저항", PlutoGovernanceText.Compose(in tag));
            var r2 = PlutoCognitiveGovernor.Approve(in regime, 6000, 0, 25, 0, 35, 3000, 1000, 3000, 0, 0, default, Kst10.Ticks, out _, pocResistance: true);
            Assert.Equal(PlutoBlockReason.CounterThesisVeto, r2);
        }

        // ── ④ 3대 아레나 결속 ───────────────────────────────────────────────────

        [Fact]
        public void Evaluate_Adds_Technical_Contribution_And_Picks_Trigger()
        {
            long midFx = 70_050L << 10, volFx = 100L << 10;
            var k0 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx);
            var k1 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx, 0, 0, 6000, squeezeBreakout: true);
            var k2 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx, 0, 0, 6000);
            Assert.Equal(0, k0.Score);
            Assert.Equal((6000 * KrxArenaEngine.TechnicalWeight / 100, 6000, ArenaTrigger.VolatilitySqueezeBreakout), (k1.Score, k1.TechnicalBp, k1.Dominant));
            Assert.Equal(ArenaTrigger.VolumeProfileSupport, k2.Dominant);
            var k3 = KrxArenaEngine.Evaluate(0, 1000, 1000, volFx, volFx, midFx, midFx, 0, 0, 20_000);
            Assert.Equal((10000 * KrxArenaEngine.TechnicalWeight / 100, 10000), (k3.Score, k3.TechnicalBp));   // ±10000 클램프

            var u = UsTechArenaEngine.Evaluate(0, 0, 0, 0, 0, 1000, 1000, midFx, midFx, 0, 0, 6000, true);
            Assert.Equal((6000 * UsTechArenaEngine.TechnicalWeight / 100, ArenaTrigger.VolatilitySqueezeBreakout), (u.Score, u.Dominant));
            var c = UpbitCryptoArenaEngine.Evaluate(0, 0, 0, 1000, 1000, 1000 * S, 1000 * S, 0, 0, 0, 6000, false);
            Assert.Equal((6000 * UpbitCryptoArenaEngine.TechnicalWeight / 100, ArenaTrigger.VolumeProfileSupport, 6000), (c.Score, c.Dominant, c.TechnicalBp));
            Assert.True(ArenaTriggerText.IsEntry(ArenaTrigger.VolatilitySqueezeBreakout) && ArenaTriggerText.IsEntry(ArenaTrigger.VolumeProfileSupport));
            Assert.False(ArenaTriggerText.IsExit(ArenaTrigger.VolatilitySqueezeBreakout));
        }

        /// <summary>분석기 단독 응축 → 돌파(분당 5 호가 · 30 봉 응축 · 31번째 분 첫 호가 = 상단 돌파).</summary>
        private static PlutoTechnicalFactor Breakout(long symbol, out PlutoTechnicalState st)
        {
            st = new PlutoTechnicalState();
            long t = Kst10.Ticks;
            for (int m = 0; m < 30; m++)
                for (int q = 0; q < 5; q++)
                {
                    long px = Pattern(70_000, 100, m, q);
                    PlutoTechnicalAnalyzer.OnPrice(ref st, px, t + m * TimeSpan.TicksPerMinute + q * 10 * TimeSpan.TicksPerSecond);
                    PlutoTechnicalAnalyzer.OnVolume(ref st, px, 100);
                }
            PlutoTechnicalAnalyzer.OnPrice(ref st, 71_050, t + 30 * TimeSpan.TicksPerMinute);
            return PlutoTechnicalAnalyzer.Assess(in st, symbol, 71_050);
        }

        /// <summary>분 내 호가 패턴(기준 b · 단위 u): +3u · −3u · +2u · −2u · 종가 0/+1u 교대(종가 σ 작음 · 봉 진폭 큼 = 응축).</summary>
        private static long Pattern(long b, long u, int minute, int q) => q switch
        {
            0 => b + 3 * u,
            1 => b - 3 * u,
            2 => b + 2 * u,
            3 => b - 2 * u,
            _ => b + (minute % 2) * u,
        };

        [Fact]
        public void Analyzer_Squeeze_To_Breakout_Scenario()
        {
            var f = Breakout(Samsung, out var st);
            Assert.Equal(30, st.CompletedBars);
            Assert.True(f.IsSqueezing);
            Assert.True(f.IsBreakoutUp);
            Assert.Equal(11, f.SqueezeBars);                                            // 20번째 봉부터 응축 집계 → 30 봉 = 11
            Assert.Equal(PlutoVolatilitySqueezeAnalyzer.BreakoutScore(11), f.SqueezeScore);
            Assert.Equal(PlutoProfileZone.AboveValue, f.ProfileZone);
            Assert.True(f.VolumeProfileScore > 0 && f.PocPrice > 0 && f.PocPrice < 71_050);
            Assert.Equal(PlutoTechnicalAnalyzer.Combine(f.SqueezeScore, f.VolumeProfileScore), f.TechnicalScore);
            Assert.True(f.IsWellFormed);
        }

        private static void KrxScenario(KrxArenaEngine e, out DateTime breakoutAt)
        {
            for (int m = 0; m < 30; m++)
                for (int q = 0; q < 5; q++)
                {
                    long bid = Pattern(70_000, 100, m, q);
                    e.OnQuote(Samsung, bid, bid + 100, 3000, 3000, 100, Kst10.AddMinutes(m).AddSeconds(q * 10));
                }
            breakoutAt = Kst10.AddMinutes(30);
            e.OnQuote(Samsung, 71_000, 71_100, 9000, 1000, 100, breakoutAt);              // 호가 불균형 +8000bp → 정지 엔진도 진입(기여 비교 기준선)
        }

        private static AiTradeRationale[] Rationales(KrxArenaEngine e)
        {
            var buf = new AiTradeRationale[KrxArenaEngine.RationaleCapacity];
            return buf.AsSpan(0, e.CopyRationales(buf)).ToArray();
        }

        [Fact]
        public void Krx_Arena_Publishes_Factor_And_Binds_Rationale_When_Enabled()
        {
            var on = new KrxArenaEngine();
            on.EnableTechnical(true);
            Assert.True(on.TechnicalEnabled);
            var off = new KrxArenaEngine();
            KrxScenario(on, out _);
            KrxScenario(off, out _);

            Assert.True(on.TryReadTechnical(Samsung, out var f));
            Assert.True(f.IsBreakoutUp && f.IsSqueezing && f.IsWellFormed);
            Assert.True(off.TryReadTechnical(Samsung, out var g));                     // 팩터 게시는 상시(가산만 정지)
            Assert.Equal(f.TechnicalScore, g.TechnicalScore);
            Assert.False(on.TryReadTechnical(373220, out _));

            var ron = Rationales(on);
            var roff = Rationales(off);
            Assert.Single(ron);
            Assert.Single(roff);
            var a = ron[0];
            Assert.True(a.IsWellFormed, a.HumanReadableSummary);
            Assert.True(a.Technical.IsBreakoutUp);
            Assert.False(roff[0].Technical.HasTechnical);                               // 정지 = 근거 미결속
            int lift = a.SentimentScore - roff[0].SentimentScore;
            int expected = f.TechnicalBp * KrxArenaEngine.TechnicalWeight / 100;
            Assert.InRange(lift, expected - 1, expected + 1);
            string text = a.HumanReadableSummary;
            _out.WriteLine(text);
            Assert.Contains(PlutoTechnicalText.SqueezeBadge, text);
            Assert.Contains(PlutoTechnicalText.BreakoutUpBadge, text);
            Assert.Contains("플루토 기술적 분석", text);
            Assert.Contains("매물대 POC", text);
            Assert.DoesNotContain("플루토 기술적 분석", roff[0].HumanReadableSummary);

            on.SetCapital(KrxArenaEngine.DefaultCapitalWon);                            // 초기화 = 봉 · 프로파일 · 팩터 소거
            Assert.False(on.TryReadTechnical(Samsung, out _));
        }

        [Fact]
        public void Rationale_Rejects_Inconsistent_Technical_Trigger()
        {
            var f = Breakout(Samsung, out _);
            var ok = new AiTradeRationale(1, Samsung, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.VolatilitySqueezeBreakout, 4000, 40,
                72_000, 70_500, 150, 0, 0, 0, 0, 0, technical: in f);
            Assert.True(ok.IsWellFormed);
            var noTech = new AiTradeRationale(1, Samsung, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.VolatilitySqueezeBreakout, 4000, 40, 72_000, 70_500, 150, 0, 0, 0, 0, 0);
            Assert.False(noTech.IsWellFormed);
            var other = new AiTradeRationale(1, 660, 1, 10, 71_100, Kst10.Ticks, ArenaTrigger.MomentumBreakout, 4000, 40, 72_000, 70_500, 150, 0, 0, 0, 0, 0, technical: in f);
            Assert.False(other.IsWellFormed);                                           // 판정 종목 ≠ 체결 종목
        }

        [Fact]
        public void Us_Arena_Uses_Tpo_Profile_And_Publishes_Breakout()
        {
            var e = new UsTechArenaEngine();
            e.EnableTechnical(true);
            for (int m = 0; m < 30; m++)
                for (int q = 0; q < 5; q++)
                {
                    long bid = Pattern(50_000, 100, m, q);
                    e.OnQuote(Nvda, bid, bid + 1, 3000, 3000, KstUsReg.AddMinutes(m).AddSeconds(q * 10));
                }
            e.OnQuote(Nvda, 51_000, 51_001, 3000, 3000, KstUsReg.AddMinutes(30));
            Assert.True(e.TryReadTechnical(Nvda, out var f));
            Assert.True(f.IsBreakoutUp && f.IsWellFormed);
            Assert.NotEqual(PlutoProfileZone.None, f.ProfileZone);                      // 호가 1틱 = 가중 1(TPO) → 표본 151
            Assert.False(e.TryReadTechnical(UsTechUniverse.Pack("ZZZZ"), out _));
        }

        [Fact]
        public void Upbit_Arena_Feeds_Profile_From_Trades_And_Publishes_Breakout()
        {
            var e = new UpbitCryptoArenaEngine();
            e.EnableTechnical(true);
            long btc = CryptoArenaUniverse.Symbol(CryptoArenaUniverse.Btc);
            long b = UpbitTickAligner.AlignNearest(CryptoArenaUniverse.SyntheticStartWon(CryptoArenaUniverse.Btc) * S);
            long u = UpbitTickAligner.TickFor(b) * 100;
            long t = Sat;
            for (int m = 0; m < 30; m++)
                for (int q = 0; q < 5; q++)
                {
                    long bid = UpbitTickAligner.AlignNearest(Pattern(b, u, m, q));
                    long ts = t + (m * 60L + q * 10) * 1_000_000;
                    e.OnTrade(btc, bid, S / 100, q % 2 == 0 ? 1 : -1, ts);
                    e.OnQuote(btc, bid, UpbitTickAligner.AlignUp(bid + 1), 10 * S, 10 * S, ts);
                }
            long up = UpbitTickAligner.AlignNearest(b + 10 * u);
            e.OnQuote(btc, up, UpbitTickAligner.AlignUp(up + 1), 10 * S, 10 * S, t + 30 * 60L * 1_000_000);
            Assert.True(e.TryReadTechnical(btc, out var f));
            Assert.True(f.IsBreakoutUp && f.IsWellFormed, $"{f.SqueezePhase} {f.SqueezeBars} {f.UpperBand} {f.Price}");
            Assert.NotEqual(PlutoProfileZone.None, f.ProfileZone);
            Assert.False(e.TryReadTechnical(999_999, out _));
        }

        // ── ⑤ δ=0 ──────────────────────────────────────────────────────────────

        [Fact]
        public void Analyzer_Settle_And_Lookup_Are_Zero_Alloc()
        {
            var st = new PlutoTechnicalState();
            long t = Kst10.Ticks, acc = 0;
            int k = 0;
            void Analyze(int n)
            {
                for (int i = 0; i < n; i++, k++)
                {
                    t += TimeSpan.TicksPerSecond * 7;
                    long px = 70_000 + ((k >> 4) & 31) * 50 - ((k & 3) * 120);
                    PlutoTechnicalAnalyzer.OnPrice(ref st, px, t);
                    PlutoTechnicalAnalyzer.OnVolume(ref st, px, 100 + (k & 63));
                    var f = PlutoTechnicalAnalyzer.Assess(in st, Samsung, px + 300);
                    acc += f.TechnicalBp + (f.IsPocResistance ? 1 : 0) + f.BandWidthBps;
                }
            }
            Analyze(10_000);
            long analyzeBytes = MinAllocated(() => Analyze(100_000));

            var e = new KrxArenaEngine();
            e.EnableTechnical(true);
            e.EnableGovernance(true);
            var kt = Kst10;
            int j = 0;
            void Settle(int n)
            {
                for (int i = 0; i < n; i++, j++)
                {
                    kt = kt.AddTicks(TimeSpan.TicksPerSecond * 2);
                    if (kt >= Kst10.AddHours(5)) kt = Kst10;
                    long px = 68_000 + ((j >> 6) & 63) * 100;
                    e.OnQuote(Samsung + (j & 3), px, px + 100, 3000 + (j & 1023) * 10, 3000, 100 + (j & 63), kt);
                    if ((j & 15) == 0) e.SubmitHuman(Samsung, (j & 16) == 0 ? 1 : -1, 1);
                }
            }
            Settle(20_000);
            long settleBytes = MinAllocated(() => Settle(100_000));

            long hits = 0;
            void Lookups(int n)
            {
                for (int i = 0; i < n; i++)
                    if (e.TryReadTechnical(Samsung + (i & 3), out var f)) hits += f.TechnicalScore + 1;
            }
            Lookups(10_000);
            long lookupBytes = MinAllocated(() => Lookups(100_000));

            Assert.True(e.TryReadSnapshot(out var snap));
            _out.WriteLine($"analyze δ={analyzeBytes}B acc={acc} · settle δ={settleBytes}B ticks={snap.Ticks} aiFills={snap.Ai.Fills} · lookup δ={lookupBytes}B hits={hits}");
            Assert.Equal(0, analyzeBytes);
            Assert.Equal(0, settleBytes);
            Assert.Equal(0, lookupBytes);
            Assert.True(acc != 0 && hits != 0);
            var rs = Rationales(e);
            Assert.Contains(rs, r => r.Technical.HasTechnical);
            Assert.All(rs, r => Assert.True(r.IsWellFormed, r.HumanReadableSummary));
        }
    }
}
