// ============================================================================
// PlutoRegimePriors — v12.18.93-DEV: 39슬롯 인지 텐서 사전 가중치(Pre-trained Base Weights · 12대 국면 × 3대 자산군 · 내장 상수 테이블 · δ=0 판독)
//
//   목적: 실측 표본 0 인 국면 슬롯이 '가중치 미보정' 으로 비지 않게 국면 × 자산군 기본 해석 가중치(모멘텀 tilt % · 악마의 변호인 엄격도 점)를 제공.
//   값 = 국면 유형(패닉 · 약세 · 박스 · 강세 · 유동성) × 자산군 민감도(코인 = 고변동 → 패닉 방어 · 유동성 추세 가중 / US 기술주 = 금리 · 버블 국면 민감)의
//     정성 휴리스틱 — 백테스트 적합값이 아니다(정직 표기 · UI '사전 가중치(실측 아님)').
//   ★국면 편향(PlutoRegimeMatch.Bias · 유사도 비례 ±20% / +10점)과 별개 · 합산 후 PlutoCognitiveWeights 상·하한 포화 — 크기는 편향의 절반 이하(±10% / +5점)로 묶는다.
//   ★세대 0(보정 아님) — 실측(국면 슬롯 → 자산 기준선) · 전역 보정 가중치가 있으면 항상 그쪽이 우선(PlutoCognitiveTensorLake.Resolve).
//   기준선 행(국면 판정 전) = 사전 가중치 없음(중립).
// ============================================================================
namespace StockManager.Engines.Cognitive.Learning
{
    public static class PlutoRegimePriors
    {
        public const int MaxTiltPct = 10, MaxStrictnessPts = 5;

        // [국면 id − 1] × 자산(KRX · US · Upbit) × (tilt %, 엄격도 점)
        private static readonly sbyte[] s_table =
        {
            //  KRX       US        Upbit
            -5,  2,   -10,  3,    -5,  3,    // 1 닷컴 버블 붕괴(약세 · 기술주 밸류 붕괴)
            -5,  4,    -5,  4,   -10,  5,    // 2 서브프라임(패닉)
            -5,  4,    -5,  3,   -10,  5,    // 3 신용등급 강등(패닉)
            -5,  4,    -5,  3,   -10,  5,    // 4 중국 증시 쇼크(패닉 · 신흥국 이탈)
             5, -1,     5, -1,     5,  0,    // 5 저변동 골디락스(강세)
            -5,  2,    -5,  2,    -5,  3,    // 6 미중 무역분쟁(약세)
            -5,  0,    -5,  0,    -5,  1,    // 7 연준 피벗 박스권(평균 회귀)
            -5,  5,    -5,  5,   -10,  5,    // 8 코로나 폭락(패닉)
             5, -1,     5, -1,    10, -1,    // 9 V자 유동성 장세(유동성 추세)
            -5,  2,   -10,  3,    -5,  3,    // 10 금리인상 충격(약세 · 성장주 압축)
            -5,  2,    -5,  3,    -5,  3,    // 11 SVB 뱅크런(약세 단기 충격)
             5, -1,    10, -1,     5,  0,    // 12 AI 유동성 랠리(강세 · 기술주 주도)
        };

        /// <summary>국면 1~12 × 자산군 사전 가중치(세대 0 · 범위 밖 = 중립 · δ=0).</summary>
        public static PlutoCognitiveWeights Of(int regimeId, PlutoAssetClass asset)
        {
            if (regimeId is < 1 or > PlutoRegimeVectorLake.Count) return default;
            int i = ((regimeId - 1) * PlutoCognitiveTensorLake.Assets + ((int)asset % PlutoCognitiveTensorLake.Assets)) * 2;
            return new PlutoCognitiveWeights(0, s_table[i], s_table[i + 1]);
        }
    }
}
