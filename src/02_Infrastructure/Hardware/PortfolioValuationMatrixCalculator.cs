// ============================================================================
// PortfolioValuationMatrixCalculator — v12.9.0 [Step 3/7] Think Beyond Gemini Idea A:
//   Zero-Alloc Portfolio Valuation & PnL Matrix Calculator
//
//   보유 종목 N개의 평가액/손익/수익률/비중 배열을 힙 할당 없이 산출한다. 호출자가 제공한 Span 에 직접 투사
//   (FastFactorSieve/MultiFactorConvictionSynthesizer 와 동형의 '호출자 버퍼 제공' 관례). decimal 은 SIMD
//   Vector&lt;T&gt; 미지원(정직 명기 — Vector&lt;decimal&gt; 은 .NET 이 지원하지 않음) 이라 스칼라 단일 패스지만,
//   힙 할당은 호출자 제공 Span 뿐이라 δ=0 은 그대로 성립한다.
//
//   불변: 결정론(순수 함수) · δ=0(Compute/Summarize 핫패스 힙 0 — 값타입 지역변수만) · fail-closed(span 길이
//        불일치 → 즉시 예외, avgPrice≤0 종목은 수익률 0%로 안전 처리) · liveArmed:false(평가 수치만 산출).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    public static class PortfolioValuationMatrixCalculator
    {
        /// <summary>
        /// 보유 종목별 평가액/손익/수익률(%)/비중(%)을 단일 패스로 산출(δ=0 — 6개 span 모두 동일 길이 요구,
        /// 불일치 시 즉시 예외). avgPrice≤0 종목은 수익률 0%(fail-closed — 0으로 나누지 않음).
        /// </summary>
        public static void Compute(
            ReadOnlySpan<decimal> quantity, ReadOnlySpan<decimal> avgPrice, ReadOnlySpan<decimal> currentPrice,
            Span<decimal> valuationOut, Span<decimal> pnlOut, Span<decimal> retPctOut, Span<decimal> weightPctOut)
        {
            int n = quantity.Length;
            if (avgPrice.Length != n || currentPrice.Length != n || valuationOut.Length != n
                || pnlOut.Length != n || retPctOut.Length != n || weightPctOut.Length != n)
                throw new ArgumentException("모든 span 길이가 일치해야 합니다.");

            decimal total = 0m;
            for (int i = 0; i < n; i++)
            {
                decimal val = quantity[i] * currentPrice[i];
                decimal cost = quantity[i] * avgPrice[i];
                valuationOut[i] = val;
                pnlOut[i] = val - cost;
                retPctOut[i] = avgPrice[i] > 0 ? (currentPrice[i] - avgPrice[i]) / avgPrice[i] * 100m : 0m;
                total += val;
            }
            for (int i = 0; i < n; i++)
                weightPctOut[i] = total > 0 ? valuationOut[i] / total * 100m : 0m;
        }

        /// <summary>Compute 결과(평가액/손익 배열)로부터 총평가액/총손익/총수익률을 재순회 없이 요약(δ=0).</summary>
        public static (decimal TotalValuation, decimal TotalPnl, decimal TotalRetPct) Summarize(
            ReadOnlySpan<decimal> valuation, ReadOnlySpan<decimal> pnl)
        {
            if (valuation.Length != pnl.Length) throw new ArgumentException("valuation/pnl span 길이가 일치해야 합니다.");
            decimal totalVal = 0m, totalPnl = 0m;
            for (int i = 0; i < valuation.Length; i++) { totalVal += valuation[i]; totalPnl += pnl[i]; }
            decimal cost = totalVal - totalPnl;
            decimal retPct = cost > 0 ? totalPnl / cost * 100m : 0m;
            return (totalVal, totalPnl, retPct);
        }
    }
}
