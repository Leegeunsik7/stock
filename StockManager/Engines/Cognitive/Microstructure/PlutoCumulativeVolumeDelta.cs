// ============================================================================
// PlutoCumulativeVolumeDelta — v12.18.75-DEV: 플루토 브레인 4단계 — 체결강도 누적 델타(CVD) + 가격-CVD 다이버전스 판정(값 타입 상태 · 정수 산술 · δ=0)
//
//   입력 = 체결 1건(공격 방향 side +1 매수 테이커(매도호가 체결) · −1 매도 테이커(매수호가 체결) · 수량 · 가격 · 시각 ticks).
//     업비트 = 체결 스트림 ask_bid 실측 방향 · KRX/US = 피드에 체결 방향 없음 → 틱 룰(TickRuleSide · 중간가 상승 = 매수 · 하락 = 매도 · 보합 = 직전 방향) 근사(정직 표기).
//   누적: Cvd = Σ side × 수량(±long.MaxValue 포화) · 1분 시간 봉(BarTicks)마다 봉 델타 · 봉 거래량 · 봉 종가를 Window(10) 봉 인라인 링에 확정.
//   판독(Read · 완성 봉 MinBars(3) 이상):
//     CvdSlopeBps = 창 Σ델타 × 10000 / 창 Σ거래량(−10000~+10000 · 창 거래량 중 순공격 매수 비중) ·
//     PriceChangeBps = (현재가 − 창 최고(最古) 봉 종가) × 10000 / 창 최고 봉 종가(±1,000,000 포화).
//     강세 다이버전스 = 가격 횡보/하락(PriceChangeBps ≤ +FlatBps) ∧ CVD 상승(CvdSlopeBps ≥ +DivergenceSlopeBps) → +20 ~ +35(기울기 비례).
//     약세 다이버전스 = 가격 상승(PriceChangeBps ≥ +RiseBps) ∧ CVD 급락(CvdSlopeBps ≤ −DivergenceSlopeBps) → −25(악마의 변호인 ⑤ 동반).
//     그 밖 = CvdSlopeBps / 1000(±10 · 다이버전스 하한 20 미만 → 다이버전스 점수와 분리).
//   ★δ=0: OnTrade/TickRuleSide/Read 힙 할당 0(InlineArray 링 · 값 타입 · Int128 · LINQ/박싱/클로저/문자열 0). 단일 작성자(정산 스레드).
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace StockManager.Engines.Cognitive.Microstructure
{
    /// <summary>완성 봉 1개(봉 델타 · 봉 거래량 · 봉 종가).</summary>
    public struct PlutoCvdBar
    {
        public long Delta, Volume, Close;
    }

    /// <summary>최근 10 봉 고정 인라인 링(힙 0).</summary>
    [InlineArray(PlutoCumulativeVolumeDelta.Window)]
    public struct PlutoCvdBarRing
    {
        private PlutoCvdBar _e0;
    }

    public enum PlutoDivergenceType : byte
    {
        None = 0,
        Bullish = 1,
        Bearish = 2,
    }

    /// <summary>종목 1개 CVD 상태(값 타입 · 엔진 종목 표에 내장 · default = 데이터 없음). 단일 작성자(정산 스레드).</summary>
    public struct PlutoCvdState
    {
        public PlutoCvdBarRing Bars;
        public int Count, Head;
        public long Cvd;
        public long BarIndex, BarDelta, BarVolume, BarClose;
        public bool HasBar;
        /// <summary>틱 룰 직전 가격 · 직전 방향(0 = 미정).</summary>
        public long LastPrice;
        public int LastSide;

        public readonly bool Ready => Count >= PlutoCumulativeVolumeDelta.MinBars;
    }

    /// <summary>현재가 기준 CVD 판독(값 타입 · Ready = false ↔ 판정 불가).</summary>
    public readonly struct PlutoCvdReading
    {
        public readonly bool Ready;
        public readonly long Cvd;
        public readonly int Bars, CvdSlopeBps, PriceChangeBps, Score;
        public readonly PlutoDivergenceType Divergence;

        public PlutoCvdReading(bool ready, long cvd, int bars, int cvdSlopeBps, int priceChangeBps, PlutoDivergenceType divergence, int score)
        {
            Ready = ready; Cvd = cvd; Bars = bars; CvdSlopeBps = cvdSlopeBps; PriceChangeBps = priceChangeBps; Divergence = divergence; Score = score;
        }
    }

    public static class PlutoCumulativeVolumeDelta
    {
        public const int Window = 10, MinBars = 3;
        public static readonly long BarTicks = TimeSpan.TicksPerMinute;
        /// <summary>다이버전스 문턱: 창 순공격 비중 ±25% · 가격 횡보 상한 +0.10% · 가격 상승 하한 +0.20%.</summary>
        public const int DivergenceSlopeBps = 2500, FlatBps = 10, RiseBps = 20;
        public const int BullishBaseScore = 20, BullishMaxScore = 35, BearishScore = -25;
        public const int NeutralSlopePerPoint = 1000;
        public const int MaxPriceChangeBps = 1_000_000;

        /// <summary>포화 덧셈(±long.MaxValue · 오버플로 0).</summary>
        public static long SatAdd(long a, long b)
        {
            long r = unchecked(a + b);
            if (((a ^ r) & (b ^ r)) < 0) return b > 0 ? long.MaxValue : -long.MaxValue;
            return r == long.MinValue ? -long.MaxValue : r;
        }

        /// <summary>
        /// 틱 룰 방향(δ=0): 직전 가격 대비 상승 = +1 · 하락 = −1 · 보합 = 직전 방향 · 첫 가격/방향 미정 = 0(호출 측 = 누적 생략).
        /// 무효 가격 = 0(상태 불변).
        /// </summary>
        public static int TickRuleSide(ref PlutoCvdState s, long price)
        {
            if (price <= 0) return 0;
            int side = s.LastPrice <= 0 ? 0 : price > s.LastPrice ? 1 : price < s.LastPrice ? -1 : s.LastSide;
            s.LastPrice = price;
            if (side != 0) s.LastSide = side;
            return side;
        }

        /// <summary>체결 1건 누적 · 분 경계 통과 = 직전 봉 확정(δ=0). 무효 방향/수량/가격/시각 = 무시. 시각 역행 = 진행 봉에 합산.</summary>
        public static void OnTrade(ref PlutoCvdState s, int side, long volume, long price, long timeTicks)
        {
            if ((side != 1 && side != -1) || volume <= 0 || price <= 0 || timeTicks <= 0) return;
            long idx = timeTicks / BarTicks;
            if (s.HasBar && idx > s.BarIndex) Commit(ref s);
            if (!s.HasBar) { s.BarIndex = idx; s.BarDelta = 0; s.BarVolume = 0; s.HasBar = true; }
            long signed = side > 0 ? volume : -volume;
            s.Cvd = SatAdd(s.Cvd, signed);
            s.BarDelta = SatAdd(s.BarDelta, signed);
            s.BarVolume = SatAdd(s.BarVolume, volume);
            s.BarClose = price;
        }

        private static void Commit(ref PlutoCvdState s)
        {
            ref PlutoCvdBar b = ref s.Bars[s.Head];
            b.Delta = s.BarDelta; b.Volume = s.BarVolume; b.Close = s.BarClose;
            s.Head = (s.Head + 1) % Window;
            if (s.Count < Window) s.Count++;
            s.HasBar = false;
        }

        /// <summary>강세 다이버전스 점수: 문턱 기울기 = +20 · 기울기 비례 · 전량 매수(+10000) = +35.</summary>
        public static int BullishScore(int slopeBps)
        {
            int over = Math.Clamp(slopeBps - DivergenceSlopeBps, 0, 10000 - DivergenceSlopeBps);
            return BullishBaseScore + over * (BullishMaxScore - BullishBaseScore) / (10000 - DivergenceSlopeBps);
        }

        /// <summary>현재가 기준 판독(순수 · 할당 0). 완성 봉 MinBars 미만 ∨ 무효 가격 = 판정 불가(Ready = false · 점수 0).</summary>
        public static PlutoCvdReading Read(in PlutoCvdState s, long price)
        {
            if (!s.Ready || price <= 0) return new PlutoCvdReading(false, s.Cvd, s.Count, 0, 0, PlutoDivergenceType.None, 0);
            Int128 delta = 0, volume = 0;
            int oldest = (s.Head - s.Count + Window) % Window;
            for (int k = 0; k < s.Count; k++)
            {
                ref readonly PlutoCvdBar b = ref s.Bars[(oldest + k) % Window];
                delta += b.Delta; volume += b.Volume;
            }
            long refClose = s.Bars[oldest].Close;
            int slope = volume <= 0 ? 0 : (int)Int128.Clamp(delta * 10000 / volume, -10000, 10000);
            int dp = refClose <= 0 ? 0 : (int)Int128.Clamp((Int128)(price - refClose) * 10000 / refClose, -MaxPriceChangeBps, MaxPriceChangeBps);
            var div = PlutoDivergenceType.None;
            int score;
            if (dp <= FlatBps && slope >= DivergenceSlopeBps) { div = PlutoDivergenceType.Bullish; score = BullishScore(slope); }
            else if (dp >= RiseBps && slope <= -DivergenceSlopeBps) { div = PlutoDivergenceType.Bearish; score = BearishScore; }
            else score = slope / NeutralSlopePerPoint;
            return new PlutoCvdReading(true, s.Cvd, s.Count, slope, dp, div, score);
        }
    }
}
