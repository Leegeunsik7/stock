// ============================================================================
// MultiFactorConvictionSynthesizer — v12.7.0 SIMD Multi-Factor Synthetic Conviction & XAI Attribution Matrix
//
//   FastFactorSieve(v12.4.0)의 4-입력 Vector&lt;float&gt; SIMD 합성(ComputeCompositeScores)을 4대 축(기술적/
//   기본적/미시구조/수급) 의미론으로 재사용하고(★재구현 아님 — 위임), 추가로 개별 심볼의 팩터 기여도(XAI)를
//   결정론적으로 분해한다. 합성 점수(SIMD, 전 종목 병렬)와 기여도 분해(선별된 Top-N만, 저빈도)를 분리해 —
//   δ=0 요구가 있는 대량 스코어링 경로와 사람이 읽는 설명 경로를 성능 특성에 맞게 나눈다.
//
//   불변: 결정론(순수 함수) · δ=0(ComputeConviction 은 ComputeCompositeScores 위임이라 힙 0 유지 · Attribute
//        는 값타입 반환·힙 0) · liveArmed:false(팩터/설명 수치만 산출 — 발주 경로 무접촉).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    /// <summary>4대 알파 팩터 축.</summary>
    public enum ConvictionFactorAxis { Technical = 0, Fundamental = 1, Microstructure = 2, SupplyDemand = 3 }

    /// <summary>단일 심볼의 팩터 기여도(XAI) — 절대 가중기여 기준 백분율 분해(합 100%, 값타입·힙 0).</summary>
    public readonly record struct FactorAttribution(decimal TechnicalPct, decimal FundamentalPct, decimal MicrostructurePct, decimal SupplyDemandPct);

    public static class MultiFactorConvictionSynthesizer
    {
        /// <summary>4대 축 SIMD 합성 확신도 점수(★FastFactorSieve.ComputeCompositeScores 위임 — δ=0 그대로 승계).</summary>
        public static void ComputeConviction(
            ReadOnlySpan<float> technical, ReadOnlySpan<float> fundamental,
            ReadOnlySpan<float> microstructure, ReadOnlySpan<float> supplyDemand,
            float wTechnical, float wFundamental, float wMicrostructure, float wSupplyDemand,
            Span<float> convictionOut)
            => FastFactorSieve.ComputeCompositeScores(technical, fundamental, microstructure, supplyDemand,
                wTechnical, wFundamental, wMicrostructure, wSupplyDemand, convictionOut);

        /// <summary>
        /// 단일 심볼의 팩터 기여도(XAI) 분해(δ=0·값타입). 각 축의 가중기여(축값×가중치) 절대값 비중을 백분율로
        /// 환산 — 최종 합성 점수에 각 축이 '얼마나' 기여했는지(방향 무관 크기)를 사람이 읽을 수 있게 한다.
        /// 4축 전부 0(가중기여 없음)이면 균등 25%(정보 없음 — fail-closed 중립 표시, 편향 암시 방지).
        /// </summary>
        public static FactorAttribution Attribute(
            float technical, float fundamental, float microstructure, float supplyDemand,
            float wTechnical, float wFundamental, float wMicrostructure, float wSupplyDemand)
        {
            float cT = technical * wTechnical, cF = fundamental * wFundamental,
                  cM = microstructure * wMicrostructure, cS = supplyDemand * wSupplyDemand;
            float denom = Math.Abs(cT) + Math.Abs(cF) + Math.Abs(cM) + Math.Abs(cS);
            if (denom <= 0f) return new FactorAttribution(25m, 25m, 25m, 25m);

            return new FactorAttribution(
                Round1((decimal)(Math.Abs(cT) / denom) * 100m),
                Round1((decimal)(Math.Abs(cF) / denom) * 100m),
                Round1((decimal)(Math.Abs(cM) / denom) * 100m),
                Round1((decimal)(Math.Abs(cS) / denom) * 100m));
        }

        private static decimal Round1(decimal v) => Math.Round(v, 1, MidpointRounding.AwayFromZero);
    }
}
