// ============================================================================
// PlutoVolatilitySqueezeAnalyzer — v12.18.73-DEV: 플루토 브레인 3단계 — 볼린저 밴드 스퀴즈(변동성 응축 → 폭발) 판정(값 타입 상태 · 정수 산술 · δ=0)
//
//   입력 = 완성 봉 1개(고가 · 저가 · 종가 · 가격 단위 정수). 봉 구성은 PlutoTechnicalAnalyzer(1분 시간 봉 · 아레나 호가 중간가)가 담당.
//   지표(최근 Period=20 봉 · 전부 Int128 중간값 — 원 raw 10^8 가상자산 가격 제곱도 오버플로 0):
//     중심선 = SMA20(종가) · σ = 모표준편차(정수 제곱근 · 내림) · 볼린저 = 중심 ± 2σ ·
//     ATR20 = 참값 범위(TR = max(고−저, |고−전종가|, |저−전종가|) · 첫 봉 = 고−저) 단순 평균 · 켈트너 = 중심 ± 1.5 ATR(TTM 방식 · 같은 중심선).
//   스퀴즈 = 볼린저 밴드가 켈트너 채널 안으로 수렴(2σ &lt; 1.5 ATR ⇔ 4σ &lt; 3 ATR) · 봉마다 연속 응축 봉 수 집계.
//   현재가 판정(Read): 응축 '확립' = 연속 MinSqueezeBars(3) 봉 이상 — 응축 중이거나 해제 후 ReleaseWindowBars(5) 봉 이내.
//     확립 ∧ 현재가 &gt; 볼린저 상단 = 상방 돌파(+20 + min(20, 2 × (응축 봉 − 3)) = +20~+40) ·
//     확립 ∧ 현재가 &lt; 볼린저 하단 = 하방 이탈(−20~−40 대칭) · 그 밖 응축 중 = 방향 미정(0점) · 미확립 = 중립(0점).
//   밴드폭(bp) = 4σ × 10000 / 중심선(±1,000,000 포화). 워밍업 20 봉 전 = 판정 없음(Unknown · fail-closed).
//   ★δ=0: Push/Read 힙 할당 0(InlineArray 링 · 값 타입 · Int128 · LINQ/박싱/클로저/문자열 0).
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace StockManager.Engines.Cognitive.Technical
{
    /// <summary>완성 봉 1개(종가 · 참값 범위).</summary>
    public struct PlutoBar
    {
        public long Close, TrueRange;
    }

    /// <summary>최근 20 봉 고정 인라인 링(힙 0).</summary>
    [InlineArray(PlutoVolatilitySqueezeAnalyzer.Period)]
    public struct PlutoBarRing
    {
        private PlutoBar _e0;
    }

    public enum PlutoSqueezePhase : byte
    {
        Unknown = 0,
        Neutral = 1,
        Squeezing = 2,
        BreakoutUp = 3,
        BreakoutDown = 4,
    }

    /// <summary>종목 1개 스퀴즈 상태(값 타입 · 엔진 종목 표에 내장 · default = 워밍업 전). 단일 작성자(정산 스레드).</summary>
    public struct PlutoSqueezeState
    {
        public PlutoBarRing Bars;
        public int Count, Head;
        public long PrevClose;
        /// <summary>최근 완성 봉 기준 중심선 · σ · ATR(가격 단위).</summary>
        public long Mid, Sigma, Atr;
        public bool Squeezing;
        /// <summary>진행 중 연속 응축 봉 수 · 직전 종료된 응축 구간 길이 · 해제 후 경과 봉 수.</summary>
        public int SqueezeBars, LastSqueezeRun, BarsSinceRelease;

        public readonly bool Ready => Count >= PlutoVolatilitySqueezeAnalyzer.Period;
    }

    /// <summary>현재가 기준 스퀴즈 판독(값 타입 · Phase = Unknown ↔ 판정 불가).</summary>
    public readonly struct PlutoSqueezeReading
    {
        public readonly PlutoSqueezePhase Phase;
        public readonly bool IsSqueezing;
        public readonly int SqueezeBars, BandWidthBps, Score;
        public readonly long Mid, Upper, Lower, KeltnerUpper, KeltnerLower;

        public PlutoSqueezeReading(PlutoSqueezePhase phase, bool isSqueezing, int squeezeBars, int bandWidthBps, int score,
            long mid, long upper, long lower, long keltnerUpper, long keltnerLower)
        {
            Phase = phase; IsSqueezing = isSqueezing; SqueezeBars = squeezeBars; BandWidthBps = bandWidthBps; Score = score;
            Mid = mid; Upper = upper; Lower = lower; KeltnerUpper = keltnerUpper; KeltnerLower = keltnerLower;
        }
    }

    public static class PlutoVolatilitySqueezeAnalyzer
    {
        public const int Period = 20;
        /// <summary>볼린저 2σ · 켈트너 1.5 ATR(분수 = 3/2).</summary>
        public const int BandSigmas = 2, KeltnerAtrNum = 3, KeltnerAtrDen = 2;
        public const int MinSqueezeBars = 3, ReleaseWindowBars = 5;
        public const int BreakoutBaseScore = 20, BreakoutMaxScore = 40, BreakoutPerBar = 2;
        public const int MaxBandWidthBps = 1_000_000;

        /// <summary>완성 봉 1개 반영(δ=0). 무효 봉(가격 ≤ 0 · 고 &lt; 저 · 종가 범위 밖) = 무시(상태 불변).</summary>
        public static void Push(ref PlutoSqueezeState s, long high, long low, long close)
        {
            if (low <= 0 || high < low || close < low || close > high) return;
            long tr = high - low;
            if (s.PrevClose > 0) tr = Math.Max(tr, Math.Max(Math.Abs(high - s.PrevClose), Math.Abs(low - s.PrevClose)));
            s.Bars[s.Head] = new PlutoBar { Close = close, TrueRange = tr };
            s.Head = (s.Head + 1) % Period;
            if (s.Count < Period) s.Count++;
            s.PrevClose = close;
            if (s.Count < Period) return;

            Int128 sum = 0, trSum = 0;
            for (int i = 0; i < Period; i++) { sum += s.Bars[i].Close; trSum += s.Bars[i].TrueRange; }
            long mid = (long)(sum / Period);
            Int128 var = 0;
            for (int i = 0; i < Period; i++)
            {
                Int128 d = s.Bars[i].Close - mid;
                var += d * d;
            }
            s.Mid = mid;
            s.Sigma = ISqrt(var / Period);
            s.Atr = (long)(trSum / Period);
            bool sq = (Int128)s.Sigma * BandSigmas * KeltnerAtrDen < (Int128)s.Atr * KeltnerAtrNum;
            if (sq)
            {
                if (!s.Squeezing) s.SqueezeBars = 0;
                if (s.SqueezeBars < int.MaxValue) s.SqueezeBars++;
            }
            else if (s.Squeezing)
            {
                s.LastSqueezeRun = s.SqueezeBars;
                s.SqueezeBars = 0;
                s.BarsSinceRelease = 0;
            }
            else if (s.BarsSinceRelease < int.MaxValue) s.BarsSinceRelease++;
            s.Squeezing = sq;
        }

        /// <summary>응축 확립 구간 길이(응축 중 = 진행 봉 수 · 해제 후 창 이내 = 직전 구간 길이 · 그 밖 = 0).</summary>
        public static int ArmedRun(in PlutoSqueezeState s)
        {
            int run = s.Squeezing ? s.SqueezeBars : s.BarsSinceRelease <= ReleaseWindowBars ? s.LastSqueezeRun : 0;
            return run >= MinSqueezeBars ? run : 0;
        }

        /// <summary>돌파 가산점 크기(+20~+40 · 응축 길수록 강함).</summary>
        public static int BreakoutScore(int run) => run < MinSqueezeBars ? 0
            : (int)Math.Min(BreakoutMaxScore, BreakoutBaseScore + (long)(run - MinSqueezeBars) * BreakoutPerBar);

        /// <summary>현재가 기준 판정(순수 · 할당 0). 워밍업 전 · 무효 가격 = default(Unknown).</summary>
        public static PlutoSqueezeReading Read(in PlutoSqueezeState s, long price)
        {
            if (!s.Ready || price <= 0 || s.Mid <= 0) return default;
            long band = (long)Int128.Min((Int128)s.Sigma * BandSigmas, long.MaxValue / 4);
            long kc = (long)Int128.Min((Int128)s.Atr * KeltnerAtrNum / KeltnerAtrDen, long.MaxValue / 4);
            long upper = s.Mid + band, lower = s.Mid - band;
            int width = (int)Int128.Min((Int128)band * 2 * 10000 / s.Mid, MaxBandWidthBps);
            int run = ArmedRun(in s);
            var phase = run > 0 && price > upper ? PlutoSqueezePhase.BreakoutUp
                      : run > 0 && price < lower ? PlutoSqueezePhase.BreakoutDown
                      : s.Squeezing ? PlutoSqueezePhase.Squeezing
                      : PlutoSqueezePhase.Neutral;
            int score = phase == PlutoSqueezePhase.BreakoutUp ? BreakoutScore(run)
                      : phase == PlutoSqueezePhase.BreakoutDown ? -BreakoutScore(run) : 0;
            return new PlutoSqueezeReading(phase, s.Squeezing, s.Squeezing ? s.SqueezeBars : run, width, score,
                s.Mid, upper, lower, s.Mid + kc, s.Mid - kc);
        }

        /// <summary>정수 제곱근(내림 · δ=0). v ≤ 0 = 0.</summary>
        public static long ISqrt(Int128 v)
        {
            if (v <= 0) return 0;
            UInt128 x = (UInt128)v;
            UInt128 r = (UInt128)Math.Sqrt((double)x);
            if (r == 0) r = 1;
            r = (r + x / r) >> 1;
            r = (r + x / r) >> 1;
            while (r * r > x) r--;
            while ((r + 1) * (r + 1) <= x) r++;
            return (long)r;
        }
    }
}
