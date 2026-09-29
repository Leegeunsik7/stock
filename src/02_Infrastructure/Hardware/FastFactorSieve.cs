// ============================================================================
// FastFactorSieve — v12.4.0 SIMD-Accelerated Factor Pre-Filter Engine
//
//   전 종목(수천 심볼) RSI/Bollinger%B/Volume-Surge/기본적 팩터를 System.Numerics.Vector&lt;float&gt; 로 심볼 축을
//   따라 병렬 합성 점수화한다. ★Avx512MicroAlphaEngine(호가 피처 SIMD)과 달리 여기서는 명시적 X86 인트린식
//   (Avx512F/Avx2)을 쓰지 않고 이식형 Vector&lt;T&gt; 를 쓴다 — JIT 이 호스트 SIMD 폭(SSE2~AVX-512)에 맞춰 자동
//   벡터화하므로 AVX2/AVX-512 미지원 개발/CI 호스트에서도 항상 정확히 폴백(별도 IsSupported 가드 불필요).
//   ★정직한 복잡도 표기: 심볼별 합성 점수 산출은 SIMD 레인 분할로 O(1) 상수 시간에 근접(레인당 병렬)하지만,
//   Top-N 선별 자체는 전 종목 단일 패스 O(symbolCount) 다(진짜 O(1) 은 원리상 불가능 — 과장 없이 명기).
//
//   불변: 결정론(순수 함수·wall-clock/RNG 0) · δ=0(ComputeCompositeScores/SelectTopN 핫패스 힙 0 — 모든 버퍼
//        호출자 제공) · fail-closed(입력 span 길이 불일치 → 즉시 예외) · liveArmed:false(팩터 '수치'만 산출 —
//        발주 경로 무접촉).
// ============================================================================
using System;
using System.Numerics;

namespace MS2E.Infrastructure
{
    /// <summary>SIMD(Vector&lt;float&gt;) 팩터 사전 필터 — 순수 정적 유틸(상태 없음).</summary>
    public static class FastFactorSieve
    {
        /// <summary>
        /// 심볼별 합성 팩터 점수 = wRsi·rsiNorm + wBoll·bollNorm + wVol·volSurgeNorm + wFund·fundNorm(★SIMD 레인
        /// 병렬·δ=0). 4개 입력 span 은 모두 scoresOut 이상 길이여야 한다(짧으면 즉시 예외 — fail-closed).
        /// </summary>
        public static void ComputeCompositeScores(
            ReadOnlySpan<float> rsiNorm, ReadOnlySpan<float> bollNorm,
            ReadOnlySpan<float> volSurgeNorm, ReadOnlySpan<float> fundNorm,
            float wRsi, float wBoll, float wVol, float wFund,
            Span<float> scoresOut)
        {
            int n = scoresOut.Length;
            if (rsiNorm.Length < n || bollNorm.Length < n || volSurgeNorm.Length < n || fundNorm.Length < n)
                throw new ArgumentException("모든 입력 span 길이는 scoresOut 이상이어야 합니다.");

            int width = Vector<float>.Count;
            var vwRsi = new Vector<float>(wRsi);
            var vwBoll = new Vector<float>(wBoll);
            var vwVol = new Vector<float>(wVol);
            var vwFund = new Vector<float>(wFund);

            int i = 0;
            for (; i <= n - width; i += width)
            {
                var vr = new Vector<float>(rsiNorm.Slice(i, width));
                var vb = new Vector<float>(bollNorm.Slice(i, width));
                var vv = new Vector<float>(volSurgeNorm.Slice(i, width));
                var vf = new Vector<float>(fundNorm.Slice(i, width));
                var vs = vr * vwRsi + vb * vwBoll + vv * vwVol + vf * vwFund;
                vs.CopyTo(scoresOut.Slice(i, width));
            }
            for (; i < n; i++)   // 잔여(SIMD 폭 미만) 스칼라 정리
                scoresOut[i] = rsiNorm[i] * wRsi + bollNorm[i] * wBoll + volSurgeNorm[i] * wVol + fundNorm[i] * wFund;
        }

        /// <summary>
        /// 점수 상위 outIndices.Length 개를 단일 O(symbolCount) 패스로 선별(δ=0 — outIndices/outScores 는 호출자
        /// 제공, 내부 스크래치도 힙 0). 내림차순 삽입정렬 유지(topN 이 작다는 전제하에 비용 O(symbolCount·topN)).
        /// 반환값 = 실제로 채워진 개수(scores.Length &lt; topN 이면 그보다 작을 수 있음).
        /// </summary>
        public static int SelectTopN(ReadOnlySpan<float> scores, Span<int> outIndices, Span<float> outScores)
        {
            int topN = outIndices.Length;
            if (outScores.Length != topN) throw new ArgumentException("outIndices/outScores 길이가 일치해야 합니다.");
            int filled = 0;
            for (int i = 0; i < scores.Length; i++)
            {
                float s = scores[i];
                if (filled < topN)
                {
                    int pos = filled++;
                    while (pos > 0 && outScores[pos - 1] < s)
                    { outScores[pos] = outScores[pos - 1]; outIndices[pos] = outIndices[pos - 1]; pos--; }
                    outScores[pos] = s; outIndices[pos] = i;
                }
                else if (s > outScores[topN - 1])
                {
                    int pos = topN - 1;
                    while (pos > 0 && outScores[pos - 1] < s)
                    { outScores[pos] = outScores[pos - 1]; outIndices[pos] = outIndices[pos - 1]; pos--; }
                    outScores[pos] = s; outIndices[pos] = i;
                }
            }
            return filled;
        }
    }
}
