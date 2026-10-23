// ============================================================================
// PlutoRegimeClassifier — v12.18.67-DEV: 플루토 브레인 고도화 — 종목별 실시간 시장 국면 판정(값 타입 상태 · 정수 산술 · δ=0)
//
//   입력 = 최우선 호가 1틱(매수/매도 호가 · 잔량). 상태(PlutoRegimeState)는 엔진 종목 표의 값 타입 필드 — 정산 스레드 단일 작성자.
//   지표(전부 정수 · 가격 EWMA = Int128 ×2^16 고정소수점 — 원 raw 10^8 가상자산 가격도 오버플로 0):
//     빠른 EWMA(α=1/8) · 느린 EWMA(α=1/32) · ATR = |Δ중간가| EWMA(α=1/8 · cbp = 0.01bp) · RSI = 상승/하락폭 EWMA(α=1/16) 비율 ·
//     호가 불균형 지속성 = (매수잔량−매도잔량)/합 EWMA(α=1/8 · bp) · 이격도 = 중간가 / 느린 EWMA(×10000 · 10800 = 108%).
//   후보 국면(우선순위): ① 패닉 = (ATR ≥ 80bp ∧ 중간가 < 느린 EWMA) ∨ (ATR ≥ 40bp ∧ 불균형 지속 ≤ −5000 ∧ 역배열)
//     ② 상승 = 이격(빠른−느린) ≥ +15bp ∧ 중간가 ≥ 빠른 EWMA ∧ 불균형 지속 > −3000 ③ 하락 = 이격 ≤ −15bp ∧ 중간가 ≤ 빠른 EWMA ∧ 불균형 지속 < +3000 ④ 그 외 박스권.
//   히스테리시스: 워밍업 8틱 전 = Unknown · 워밍업 직후 후보 즉시 채택 · 패닉 진입 = 즉시(fail-closed) · 그 밖 전이 = 같은 후보 3틱 연속 확인.
//   국면별 팩터 가중치 동적 전환(Reweight): 엔진 점수에서 모멘텀/호가 불균형 기여를 국면 배율로 재가중(상승·하락 = 모멘텀 130% · 박스 = 모멘텀 50%/불균형 130% ·
//     패닉 = 모멘텀 150%/불균형 50% · 판정 전 = 무변경). 패닉 국면 신규 매수 차단은 PlutoCognitiveGovernor 가 집행.
//   ★δ=0: Update/Reweight/판독 힙 할당 0(값 타입 · Int128 · LINQ/박싱/클로저/문자열 0).
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Regime
{
    /// <summary>종목 1개 국면 판정 상태(값 타입 · 엔진 종목 표에 내장 · default = 판정 전).</summary>
    public struct PlutoRegimeState
    {
        public Int128 FastFx, SlowFx;
        public long LastMid;
        public int AtrX16, GainX16, LossX16, ObiEma;
        public int Seen, CandidateRun;
        public MarketRegime Regime, Candidate;

        /// <summary>틱당 평균 진폭(cbp = 0.01bp · 100 = 1bp).</summary>
        public readonly int AtrCbp => AtrX16 >> 4;
        public readonly int AtrBp => AtrCbp / 100;

        /// <summary>RSI 0~100(상승/하락폭 모두 0 = 50).</summary>
        public readonly int Rsi
        {
            get
            {
                long g = GainX16, l = LossX16, sum = g + l;
                return sum <= 0 ? 50 : (int)(g * 100 / sum);
            }
        }

        /// <summary>이격도(중간가 / 느린 EWMA · ×10000 · 10800 = 108% · 판정 전 = 10000).</summary>
        public readonly int DisparityX100
        {
            get
            {
                if (SlowFx <= 0 || LastMid <= 0) return 10000;
                return (int)Int128.Clamp(((Int128)LastMid << PlutoRegimeClassifier.Fx) * 10000 / SlowFx, 0, 1_000_000);
            }
        }

        /// <summary>빠른 EWMA − 느린 EWMA 이격(bp · 양수 = 정배열 방향).</summary>
        public readonly int SpreadBp => SlowFx <= 0 ? 0 : (int)Int128.Clamp((FastFx - SlowFx) * 10000 / SlowFx, -10000, 10000);
    }

    public static class PlutoRegimeClassifier
    {
        public const int Fx = 16;
        public const int FastShift = 3, SlowShift = 5, AtrShift = 3, RsiShift = 4, ObiShift = 3;
        public const int WarmupTicks = 8, ConfirmTicks = 3;
        public const int TrendSpreadBp = 15, TrendObiVetoBp = 3000;
        public const int PanicAtrCbp = 8000, PanicFlowAtrCbp = 4000, PanicObiBp = -5000;
        /// <summary>틱 진폭 포화(cbp · 100% = 1,000,000).</summary>
        public const int MaxMoveCbp = 1_000_000;

        /// <summary>호가 1틱 반영(δ=0). 무효 호가(가격 ≤ 0 · 역전 · 음수 잔량) = 무시(상태 불변 · 현재 국면 반환).</summary>
        public static MarketRegime Update(ref PlutoRegimeState s, long bid, long ask, long bidQty, long askQty)
        {
            if (bid <= 0 || ask < bid || bidQty < 0 || askQty < 0) return s.Regime;
            long mid = bid + (ask - bid) / 2;
            long depth = bidQty + askQty;
            int obi = depth <= 0 ? 0 : (int)((Int128)(bidQty - askQty) * 10000 / depth);
            Int128 midFx = (Int128)mid << Fx;
            if (s.Seen == 0)
            {
                s.FastFx = s.SlowFx = midFx;
                s.ObiEma = obi;
            }
            else
            {
                int move = (int)Int128.Min(Int128.Abs((Int128)(mid - s.LastMid) * 1_000_000 / s.LastMid), MaxMoveCbp);
                int up = mid > s.LastMid ? move : 0, down = mid < s.LastMid ? move : 0;
                s.AtrX16 += ((move << 4) - s.AtrX16) >> AtrShift;
                s.GainX16 += ((up << 4) - s.GainX16) >> RsiShift;
                s.LossX16 += ((down << 4) - s.LossX16) >> RsiShift;
                s.FastFx += (midFx - s.FastFx) >> FastShift;
                s.SlowFx += (midFx - s.SlowFx) >> SlowShift;
                s.ObiEma += (obi - s.ObiEma) >> ObiShift;
            }
            s.LastMid = mid;
            if (s.Seen < int.MaxValue) s.Seen++;
            if (s.Seen < WarmupTicks) return s.Regime = MarketRegime.Unknown;

            var c = Candidate(in s, midFx);
            if (s.Regime == MarketRegime.Unknown || c == MarketRegime.PanicLiquidation)
            {
                s.Regime = c; s.Candidate = c; s.CandidateRun = 0;
            }
            else if (c == s.Regime) s.CandidateRun = 0;
            else
            {
                if (c == s.Candidate) s.CandidateRun++;
                else { s.Candidate = c; s.CandidateRun = 1; }
                if (s.CandidateRun >= ConfirmTicks) { s.Regime = c; s.CandidateRun = 0; }
            }
            return s.Regime;
        }

        private static MarketRegime Candidate(in PlutoRegimeState s, Int128 midFx)
        {
            int atr = s.AtrCbp, spread = s.SpreadBp;
            bool bearAligned = midFx < s.FastFx && s.FastFx < s.SlowFx;
            if ((atr >= PanicAtrCbp && midFx < s.SlowFx) || (atr >= PanicFlowAtrCbp && s.ObiEma <= PanicObiBp && bearAligned))
                return MarketRegime.PanicLiquidation;
            if (spread >= TrendSpreadBp && midFx >= s.FastFx && s.ObiEma > -TrendObiVetoBp) return MarketRegime.TrendingBull;
            if (spread <= -TrendSpreadBp && midFx <= s.FastFx && s.ObiEma < TrendObiVetoBp) return MarketRegime.TrendingBear;
            return MarketRegime.ChoppyRange;
        }

        /// <summary>국면별 (모멘텀 %, 호가 불균형 %) 배율.</summary>
        public static (int MomentumPct, int ImbalancePct) Profile(MarketRegime r) => r switch
        {
            MarketRegime.TrendingBull => (130, 100),
            MarketRegime.TrendingBear => (130, 80),
            MarketRegime.ChoppyRange => (50, 130),
            MarketRegime.PanicLiquidation => (150, 50),
            _ => (100, 100),
        };

        /// <summary>
        /// 국면 재가중 점수(δ=0): 엔진 점수 기여(bp × 가중/100)에서 모멘텀 · 호가 불균형 기여만 국면 배율로 교체 · ±10000 포화.
        /// momentumWeight/imbalanceWeight = 엔진 점수 산식의 해당 가중(%) — 판정 전 = 원점수 그대로.
        /// </summary>
        public static int Reweight(int score, int momentumBp, int momentumWeight, int imbalanceBp, int imbalanceWeight, MarketRegime r)
        {
            var (mp, ip) = Profile(r);
            long delta = ((long)momentumBp * momentumWeight * (mp - 100) + (long)imbalanceBp * imbalanceWeight * (ip - 100)) / 10000;
            return (int)Math.Clamp(score + delta, -10000, 10000);
        }
    }
}
