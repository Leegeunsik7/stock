// ============================================================================
// PlutoTechnicalFactor — v12.18.73-DEV: 플루토 브레인 3단계 — 기술적 팩터(스퀴즈 + 매물대 프로파일 합성 · 불변 값 타입 · 정수 필드 · 할당 0)
//
//   상태(PlutoTechnicalState · 엔진 종목별 값 타입 · 정산 스레드 단일 작성자):
//     1분 시간 봉(BarTicks · 아레나 시각 기준 · 결측 분 = 채움 없음) — 호가 중간가로 고가/저가/종가 구성 → 봉 완성 = 스퀴즈 Push + 프로파일 감쇄.
//     거래량 프로파일 = 체결/틱 거래량(PlutoVolumeProfileScanner 헤더의 시장별 출처).
//   팩터(Assess · 현재가 재판정): IsSqueezing · BandWidthBps · PocPrice · VolumeProfileScore(−100~+100) + 스퀴즈 국면/점수 · 밴드 · 밸류 에어리어.
//     합성 점수 TechnicalScore = clamp(스퀴즈 점수 + 프로파일 점수 / 2, −100, +100) · 시그널 = 점수 × 100bp(아레나 TechnicalWeight 가중).
//   게시: 아레나 정산 1회의 seqlock 쓰기 창 안에서 종목 슬롯에 기록 → TryReadTechnical(같은 버전 검증 · δ=0).
//   ★liveArmed:false — 팩터는 가상 대결 시그널 입력일 뿐 주문 수단 0. ★QuantumVault · 피어 · 외부 네트워크 접근 0.
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Technical
{
    /// <summary>종목 1개 기술적 분석 상태(봉 시계 + 스퀴즈 + 프로파일 · default = 데이터 없음).</summary>
    public struct PlutoTechnicalState
    {
        public PlutoSqueezeState Squeeze;
        public PlutoVolumeProfileState Profile;
        public long BarIndex, High, Low, Close;
        public bool HasBar;
        public long CompletedBars;
    }

    public static class PlutoTechnicalAnalyzer
    {
        public static readonly long BarTicks = TimeSpan.TicksPerMinute;

        /// <summary>가격 1건(호가 중간가) 반영 · 분 경계 통과 = 직전 봉 완성(δ=0). 무효 가격/시각 = 무시. 시각 역행 = 진행 봉에 합산.</summary>
        public static void OnPrice(ref PlutoTechnicalState s, long price, long timeTicks)
        {
            if (price <= 0 || timeTicks <= 0) return;
            long idx = timeTicks / BarTicks;
            if (!s.HasBar) { Open(ref s, price, idx); return; }
            if (idx > s.BarIndex)
            {
                PlutoVolatilitySqueezeAnalyzer.Push(ref s.Squeeze, s.High, s.Low, s.Close);
                PlutoVolumeProfileScanner.Decay(ref s.Profile);
                if (s.CompletedBars < long.MaxValue) s.CompletedBars++;
                Open(ref s, price, idx);
                return;
            }
            if (price > s.High) s.High = price;
            if (price < s.Low) s.Low = price;
            s.Close = price;
        }

        /// <summary>체결 1건(가격 · 거래량 가중) 프로파일 누적(δ=0).</summary>
        public static void OnVolume(ref PlutoTechnicalState s, long price, long weight) => PlutoVolumeProfileScanner.Add(ref s.Profile, price, weight);

        private static void Open(ref PlutoTechnicalState s, long price, long idx)
        {
            s.BarIndex = idx; s.High = s.Low = s.Close = price; s.HasBar = true;
        }

        /// <summary>합성 점수 = clamp(스퀴즈 + 프로파일 / 2, ±100).</summary>
        public static int Combine(int squeezeScore, int profileScore) => Math.Clamp(squeezeScore + profileScore / 2, -100, 100);

        /// <summary>현재가 기준 팩터(순수 · 할당 0). 데이터 부족 = 판정 없음(HasTechnical = false · 기여 0).</summary>
        public static PlutoTechnicalFactor Assess(in PlutoTechnicalState s, long symbol, long price)
        {
            if (symbol <= 0 || price <= 0) return default;
            var sq = PlutoVolatilitySqueezeAnalyzer.Read(in s.Squeeze, price);
            var vp = PlutoVolumeProfileScanner.Read(in s.Profile, price);
            if (sq.Phase == PlutoSqueezePhase.Unknown && vp.Zone == PlutoProfileZone.None) return default;
            return new PlutoTechnicalFactor(symbol, price, in sq, in vp);
        }
    }

    /// <summary>종목 1개 기술적 팩터(불변 값 타입 · 정수 필드). default = 판정 없음.</summary>
    public readonly struct PlutoTechnicalFactor
    {
        /// <summary>점수 1점 시그널 100bp(±100 → ±10000bp).</summary>
        public const int BpPerPoint = 100;

        public readonly long Symbol, Price;
        public readonly bool IsSqueezing;
        public readonly PlutoSqueezePhase SqueezePhase;
        public readonly int SqueezeBars, BandWidthBps, SqueezeScore;
        public readonly long MidBand, UpperBand, LowerBand, KeltnerUpper, KeltnerLower;
        public readonly PlutoProfileZone ProfileZone;
        public readonly long PocPrice, ValueAreaHigh, ValueAreaLow;
        public readonly int PocShareBp, VolumeProfileScore, TechnicalScore;

        public PlutoTechnicalFactor(long symbol, long price, in PlutoSqueezeReading sq, in PlutoVolumeProfileReading vp)
        {
            Symbol = symbol; Price = price;
            IsSqueezing = sq.IsSqueezing; SqueezePhase = sq.Phase; SqueezeBars = sq.SqueezeBars; BandWidthBps = sq.BandWidthBps; SqueezeScore = sq.Score;
            MidBand = sq.Mid; UpperBand = sq.Upper; LowerBand = sq.Lower; KeltnerUpper = sq.KeltnerUpper; KeltnerLower = sq.KeltnerLower;
            ProfileZone = vp.Zone; PocPrice = vp.PocPrice; ValueAreaHigh = vp.ValueAreaHigh; ValueAreaLow = vp.ValueAreaLow;
            PocShareBp = vp.PocShareBp; VolumeProfileScore = vp.Score;
            TechnicalScore = PlutoTechnicalAnalyzer.Combine(sq.Score, vp.Score);
        }

        public bool HasTechnical => Symbol > 0;
        public int TechnicalBp => TechnicalScore * BpPerPoint;
        public bool IsBreakoutUp => SqueezePhase == PlutoSqueezePhase.BreakoutUp;
        public bool IsBreakoutDown => SqueezePhase == PlutoSqueezePhase.BreakoutDown;
        public bool IsPocSupport => ProfileZone == PlutoProfileZone.PocSupport;
        /// <summary>현재가 바로 위 최대 매물대(상단 매물벽) — 악마의 변호인 반대 논리(PocResistance).</summary>
        public bool IsPocResistance => ProfileZone == PlutoProfileZone.PocResistance;

        /// <summary>
        /// 무결성: default = 판정 없음(전 필드 0) · 판정 = 식별/가격 양수 · 점수 범위 · 합성 점수 재계산 일치 ·
        /// 스퀴즈 판정 시 하단 ≤ 중심 ≤ 상단 · 국면-점수 부호 일치 · 프로파일 판정 시 POC 양수 ∧ VAL &lt; VAH ∧ POC 가 밸류 에어리어 안 · 구역-점수 부호 일치.
        /// </summary>
        public bool IsWellFormed
        {
            get
            {
                if (Symbol == 0)
                    return Price == 0 && SqueezePhase == PlutoSqueezePhase.Unknown && ProfileZone == PlutoProfileZone.None && !IsSqueezing
                        && SqueezeScore == 0 && VolumeProfileScore == 0 && TechnicalScore == 0 && PocPrice == 0 && BandWidthBps == 0;
                if (Symbol < 0 || Price <= 0) return false;
                if (SqueezeScore is < -PlutoVolatilitySqueezeAnalyzer.BreakoutMaxScore or > PlutoVolatilitySqueezeAnalyzer.BreakoutMaxScore) return false;
                if (VolumeProfileScore is < -100 or > 100 || TechnicalScore != PlutoTechnicalAnalyzer.Combine(SqueezeScore, VolumeProfileScore)) return false;
                if (SqueezePhase == PlutoSqueezePhase.Unknown)
                {
                    if (IsSqueezing || SqueezeScore != 0 || BandWidthBps != 0) return false;
                }
                else
                {
                    if (MidBand <= 0 || LowerBand > MidBand || MidBand > UpperBand || BandWidthBps < 0) return false;
                    bool scoreOk = SqueezePhase switch
                    {
                        PlutoSqueezePhase.BreakoutUp => SqueezeScore >= PlutoVolatilitySqueezeAnalyzer.BreakoutBaseScore && Price > UpperBand,
                        PlutoSqueezePhase.BreakoutDown => SqueezeScore <= -PlutoVolatilitySqueezeAnalyzer.BreakoutBaseScore && Price < LowerBand,
                        PlutoSqueezePhase.Squeezing => SqueezeScore == 0 && IsSqueezing,
                        PlutoSqueezePhase.Neutral => SqueezeScore == 0 && !IsSqueezing,
                        _ => false,
                    };
                    if (!scoreOk) return false;
                }
                if (ProfileZone == PlutoProfileZone.None) return VolumeProfileScore == 0 && PocPrice == 0;
                if (PocPrice <= 0 || ValueAreaLow >= ValueAreaHigh || PocPrice < ValueAreaLow || PocPrice > ValueAreaHigh) return false;
                return ProfileZone switch
                {
                    PlutoProfileZone.AtPoc => VolumeProfileScore == 0,
                    PlutoProfileZone.PocSupport or PlutoProfileZone.UpperValue or PlutoProfileZone.AboveValue => VolumeProfileScore > 0 && Price > PocPrice,
                    PlutoProfileZone.PocResistance or PlutoProfileZone.LowerValue or PlutoProfileZone.BelowValue => VolumeProfileScore < 0 && Price < PocPrice,
                    _ => false,
                };
            }
        }
    }

    /// <summary>기술적 팩터 한국어 표기(콜드 경로 — 호출마다 문자열 조립 · 가격 표기는 호출 측 통화 포매터).</summary>
    public static class PlutoTechnicalText
    {
        public const string SqueezeBadge = "[🌀 스퀴즈 응축]", BreakoutUpBadge = "[⚡ 볼밴 상단 돌파]", BreakoutDownBadge = "[🔻 볼밴 하단 이탈]";
        public const string PocSupportBadge = "[🧱 POC 매물대 지지]", PocResistanceBadge = "[🧱 POC 매물벽 저항]";

        /// <summary>뱃지 열(해당 없음 = 빈 문자열).</summary>
        public static string Badges(in PlutoTechnicalFactor f)
        {
            if (!f.HasTechnical) return "";
            string b = "";
            if (f.IsSqueezing) b += SqueezeBadge;
            if (f.IsBreakoutUp) b += (b.Length > 0 ? " " : "") + BreakoutUpBadge;
            if (f.IsBreakoutDown) b += (b.Length > 0 ? " " : "") + BreakoutDownBadge;
            if (f.IsPocSupport) b += (b.Length > 0 ? " " : "") + PocSupportBadge;
            if (f.IsPocResistance) b += (b.Length > 0 ? " " : "") + PocResistanceBadge;
            return b;
        }

        public static string PhaseLabel(PlutoSqueezePhase p) => p switch
        {
            PlutoSqueezePhase.Neutral => "응축 없음",
            PlutoSqueezePhase.Squeezing => "변동성 응축 중(방향 미정)",
            PlutoSqueezePhase.BreakoutUp => "응축 후 상단 돌파",
            PlutoSqueezePhase.BreakoutDown => "응축 후 하단 이탈",
            _ => "워밍업(20봉 미만)",
        };

        public static string ZoneLabel(PlutoProfileZone z) => z switch
        {
            PlutoProfileZone.AtPoc => "POC 균형점",
            PlutoProfileZone.PocSupport => "POC 상단 지지",
            PlutoProfileZone.UpperValue => "밸류 에어리어 상단부",
            PlutoProfileZone.AboveValue => "밸류 에어리어 상단 이탈(매물 공백)",
            PlutoProfileZone.PocResistance => "POC 매물벽 직하(저항)",
            PlutoProfileZone.LowerValue => "밸류 에어리어 하단부",
            PlutoProfileZone.BelowValue => "밸류 에어리어 하단 이탈",
            _ => "표본 부족",
        };
    }
}
