// ============================================================================
// PlutoDynamicKellyEngine — v12.18.88-DEV: 변동성 연동 프랙탈 켈리 권장 비중(국면 × 자산군 실측 승률 · 손익비 · ATR · 정수 산술 · δ=0)
//
//   엣지 = 텐서 셀(국면 슬롯 → 자산 기준선) 보정기 링의 실측 청산 회고(승패 · 수익률 bp — 실 에피소드 + 웜스타트 리플레이 표본).
//   켈리 f* = p − (1 − p) / b   (p = 승률 · b = 평균 이익 bp ÷ 평균 손실 bp) → 프랙탈(½ 켈리) → 변동성 배율(PlutoDynamicSizer.VolatilityPct · ATR > 20bp = 축소)
//     → [5%, 25%] 포화. ★상한 25% = 레버리지 0(자본 대비 단일 종목 최대 1/4).
//   정직 상태: 표본 &lt; 20 = Insufficient(비중 미산출 · UI '표본 부족 n/20') · f* ≤ 0 = NoEdge(0% · 진입 비권장 — 음의 엣지를 하한 5% 로 끌어올리지 않음) ·
//     패배 0건(손익비 무한) = b 를 MaxPayoffX100(5.00) 로 포화(과대 확신 억제).
//   ★δ=0: Compute 힙 할당 0(값 타입 · long 산술). ★liveArmed:false — 가상 원장 권장 비중 표시 · 추천 수량 산정뿐(주문 수단 0).
// ============================================================================
using System;
using StockManager.Engines.Cognitive.Governance;

namespace StockManager.Engines.Cognitive.Council
{
    /// <summary>실측 엣지 표본 집계(값 타입 · default = 표본 0). SumWinBp/SumLossBp = 이익/손실 회고 수익률 절대값 합(bp).</summary>
    public readonly struct PlutoEdgeStats
    {
        public readonly int Samples, Wins, Losses;
        public readonly long SumWinBp, SumLossBp;

        public PlutoEdgeStats(int samples, int wins, int losses, long sumWinBp, long sumLossBp)
        {
            Samples = Math.Max(0, samples); Wins = Math.Max(0, wins); Losses = Math.Max(0, losses);
            SumWinBp = Math.Max(0, sumWinBp); SumLossBp = Math.Max(0, sumLossBp);
        }

        /// <summary>승률 bp(표본 0 = 0).</summary>
        public int WinRateBp => Samples <= 0 ? 0 : (int)((long)Wins * 10000 / Samples);
        public long AvgWinBp => Wins <= 0 ? 0 : SumWinBp / Wins;
        public long AvgLossBp => Losses <= 0 ? 0 : SumLossBp / Losses;
    }

    public enum PlutoKellyState : byte
    {
        /// <summary>엣지 원천 미결선(텐서 없음).</summary>
        Unwired = 0,
        /// <summary>표본 &lt; MinSamples — 비중 미산출.</summary>
        Insufficient = 1,
        /// <summary>켈리 f* ≤ 0 — 진입 비권장(0%).</summary>
        NoEdge = 2,
        /// <summary>권장 비중 산출(5~25%).</summary>
        Sized = 3,
    }

    /// <summary>켈리 산출 결과(값 타입). RawKellyBp = f*(음수 허용) · WeightBp = 최종 권장 비중(Sized 만 500~2500 · 그 외 0).</summary>
    public readonly struct PlutoKellyResult
    {
        public readonly PlutoKellyState State;
        public readonly int WeightBp, RawKellyBp, WinRateBp, PayoffX100, VolatilityPct, Samples;

        public PlutoKellyResult(PlutoKellyState state, int weightBp, int rawKellyBp, int winRateBp, int payoffX100, int volatilityPct, int samples)
        {
            State = state; WeightBp = weightBp; RawKellyBp = rawKellyBp; WinRateBp = winRateBp; PayoffX100 = payoffX100; VolatilityPct = volatilityPct; Samples = samples;
        }

        public bool IsSized => State == PlutoKellyState.Sized;
    }

    public static class PlutoDynamicKellyEngine
    {
        public const int MinSamples = 20;
        public const int MinWeightBp = PlutoDynamicSizer.MinWeightBp, MaxWeightBp = PlutoDynamicSizer.MaxWeightBp;
        /// <summary>프랙탈 계수(%) — ½ 켈리.</summary>
        public const int FractionPct = 50;
        /// <summary>손익비 상한(×100) — 패배 0건 · 극단 손익비 포화.</summary>
        public const int MaxPayoffX100 = 500;

        /// <summary>엣지 + ATR(cbp · 0 = 미판독 → 배율 100%) → 권장 비중(δ=0).</summary>
        public static PlutoKellyResult Compute(in PlutoEdgeStats edge, int atrCbp)
        {
            int vol = PlutoDynamicSizer.VolatilityPct(Math.Max(0, atrCbp));
            int p = edge.WinRateBp;
            if (edge.Samples < MinSamples) return new PlutoKellyResult(PlutoKellyState.Insufficient, 0, 0, p, 0, vol, edge.Samples);
            if (edge.Wins == 0) return new PlutoKellyResult(PlutoKellyState.NoEdge, 0, -10000, 0, 0, vol, edge.Samples);
            long avgLoss = edge.AvgLossBp;
            int payoff = edge.Losses == 0 || avgLoss <= 0
                ? MaxPayoffX100
                : (int)Math.Clamp(edge.AvgWinBp * 100 / avgLoss, 0, MaxPayoffX100);
            if (payoff <= 0) return new PlutoKellyResult(PlutoKellyState.NoEdge, 0, -10000, p, 0, vol, edge.Samples);
            // f*(bp) = p − (1 − p) × 100 / payoffX100
            long raw = p - (long)(10000 - p) * 100 / payoff;
            int rawBp = (int)Math.Clamp(raw, -10000, 10000);
            if (rawBp <= 0) return new PlutoKellyResult(PlutoKellyState.NoEdge, 0, rawBp, p, payoff, vol, edge.Samples);
            long w = (long)rawBp * FractionPct / 100 * vol / 100;
            return new PlutoKellyResult(PlutoKellyState.Sized, (int)Math.Clamp(w, MinWeightBp, MaxWeightBp), rawBp, p, payoff, vol, edge.Samples);
        }

        /// <summary>ATR(cbp · 1/100 bp) — 최근 period 봉 True Range 평균 ÷ 종가(δ=0 · 유효 봉 &lt; 2 = 0 미판독).</summary>
        public static int AtrCbp(ReadOnlySpan<Screening.PlutoOhlcvBar> barsOldestFirst, int period = 14)
        {
            int n = barsOldestFirst.Length;
            if (n < 2 || period < 1) return 0;
            int start = Math.Max(1, n - period);
            long sumTr = 0;
            int used = 0;
            for (int i = start; i < n; i++)
            {
                ref readonly var b = ref barsOldestFirst[i];
                ref readonly var prev = ref barsOldestFirst[i - 1];
                if (!b.IsValid || !prev.IsValid) continue;
                long tr = Math.Max(b.High - b.Low, Math.Max(Math.Abs(b.High - prev.Close), Math.Abs(b.Low - prev.Close)));
                sumTr += tr;
                used++;
            }
            long close = barsOldestFirst[n - 1].Close;
            if (used == 0 || close <= 0) return 0;
            return (int)Int128.Clamp((Int128)sumTr * 1_000_000 / used / close, 0, int.MaxValue);
        }

        /// <summary>표시 문구(콜드): "켈리 12.5% (승률 58% · 손익비 1.40 · ½켈리 · 변동성 80%)".</summary>
        public static string Text(in PlutoKellyResult k) => k.State switch
        {
            PlutoKellyState.Sized => "켈리 " + Pct(k.WeightBp) + " (승률 " + Pct(k.WinRateBp) + " · 손익비 " + (k.PayoffX100 / 100m).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                + " · ½켈리 · 변동성 배율 " + k.VolatilityPct.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%)",
            PlutoKellyState.NoEdge => "켈리 0% — 음의 엣지(진입 비권장 · 표본 " + k.Samples.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
            PlutoKellyState.Insufficient => "켈리 미산출 — 표본 부족 " + k.Samples.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + MinSamples.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "켈리 미결선",
        };

        private static string Pct(int bp) => (bp / 100m).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }
}
