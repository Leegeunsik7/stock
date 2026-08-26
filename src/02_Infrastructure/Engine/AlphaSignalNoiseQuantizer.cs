// ============================================================================
// AlphaSignalNoiseQuantizer — v8.0.0 SIMD SNR 노이즈 양자화기
//
//  최근 N개 틱(알파/불균형 표본)의 SNR(Signal-to-Noise Ratio)을 SIMD 벡터로 병렬 계산해, 초단기 허수 호가
//  스푸핑이 유발하는 고분산 노이즈를 마이크로초 단위로 판별한다. SNR 이 임계 미만(노이즈 지배)이면 알파를 0 으로
//  양자화(억제)해 스푸핑 유발 오알파가 핫패스 디스패처로 새어나가지 못하게 한다 — 정규화된 신호만 통과.
//
//   SNR = |mean| / (std + ε).  mean=Σx/n, var=Σx²/n − mean², std=√var.
//   ★SIMD: sum·sumsq 이중 누산(Vector512/256/스칼라·FMA), 수평합 후 스칼라 마무리. δ=0.
//
//  ★불변: δ=0(Quantize 핫패스 힙 0·스택 Vector·Interlocked) · 결정론(순수 산술) · liveArmed:false(순수 필터 —
//    IBroker·소켓·onSend 참조 0. 알파 '수치'만 통과/억제) · fail-closed(표본 부족·NaN → 억제[0]).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>SIMD SNR 노이즈 양자화기(δ=0). Quantize(recent, rawAlpha) → SNR&lt;임계면 0 억제(스푸핑 노이즈 소거).</summary>
    public sealed class AlphaSignalNoiseQuantizer
    {
        private readonly long _snrThresholdMilli;   // SNR×1000 임계(미만=노이즈 지배 → 억제)
        private long _inspections, _noiseBlocks, _lastSnrMilli;

        /// <param name="snrThresholdMilli">SNR×1000 임계(기본 500=0.5). 최근 표본 |평균|/표준편차 가 이 미만이면 알파 억제.</param>
        public AlphaSignalNoiseQuantizer(long snrThresholdMilli = 500)
        {
            _snrThresholdMilli = snrThresholdMilli < 0 ? 0 : snrThresholdMilli;
        }

        public long Inspections  => Interlocked.Read(ref _inspections);
        public long NoiseBlocks  => Interlocked.Read(ref _noiseBlocks);
        public long LastSnrMilli => Interlocked.Read(ref _lastSnrMilli);

        /// <summary>
        /// 최근 표본 recent 의 SNR 을 SIMD 로 산출해 rawAlpha 를 통과/억제(δ=0). SNR≥임계 → rawAlpha 통과, 미만 → 0(스푸핑
        /// 노이즈 소거). 표본 &lt;2 또는 NaN → 0(fail-closed). snrMilli 로 SNR×1000 반환(텔레메트리).
        /// </summary>
        public float Quantize(ReadOnlySpan<float> recent, float rawAlpha, out long snrMilli)
        {
            Interlocked.Increment(ref _inspections);
            snrMilli = 0;
            int n = recent.Length;
            if (n < 2 || float.IsNaN(rawAlpha) || float.IsInfinity(rawAlpha))
            { Interlocked.Increment(ref _noiseBlocks); return 0f; }   // fail-closed

            MeanAndStd(recent, n, out float mean, out float std);
            float snr = std > 1e-9f ? MathF.Abs(mean) / std : (MathF.Abs(mean) > 1e-9f ? 1000f : 0f);   // 노이즈 0 & 신호>0 → 최대 SNR
            if (float.IsNaN(snr) || float.IsInfinity(snr)) snr = 0f;
            snrMilli = (long)(snr * 1000f);
            Volatile.Write(ref _lastSnrMilli, snrMilli);

            if (snrMilli < _snrThresholdMilli)   // 노이즈 지배(스푸핑 의심) → 억제
            { Interlocked.Increment(ref _noiseBlocks); return 0f; }
            return rawAlpha;   // 정규화 신호 통과
        }

        // sum·sumsq SIMD 이중 누산 → mean/std(δ=0·스택 Vector·FMA).
        private static void MeanAndStd(ReadOnlySpan<float> x, int n, out float mean, out float std)
        {
            ref float p = ref MemoryMarshal.GetReference(x);
            float sum, sumSq;
            if (Avx512F.IsSupported && n >= Vector512<float>.Count)
            {
                var accS = Vector512<float>.Zero; var accQ = Vector512<float>.Zero;
                int i = 0, lanes = Vector512<float>.Count;
                for (; i <= n - lanes; i += lanes)
                {
                    var v = Vector512.LoadUnsafe(ref p, (nuint)i);
                    accS += v;
                    accQ = Avx512F.FusedMultiplyAdd(v, v, accQ);   // ★직접 바인딩: accQ += v*v
                }
                sum = Vector512.Sum(accS); sumSq = Vector512.Sum(accQ);
                for (; i < n; i++) { float v = System.Runtime.CompilerServices.Unsafe.Add(ref p, i); sum += v; sumSq = MathF.FusedMultiplyAdd(v, v, sumSq); }
            }
            else if (Avx2.IsSupported && Fma.IsSupported && n >= Vector256<float>.Count)
            {
                var accS = Vector256<float>.Zero; var accQ = Vector256<float>.Zero;
                int i = 0, lanes = Vector256<float>.Count;
                for (; i <= n - lanes; i += lanes)
                {
                    var v = Vector256.LoadUnsafe(ref p, (nuint)i);
                    accS += v;
                    accQ = Fma.MultiplyAdd(v, v, accQ);   // ★직접 바인딩
                }
                sum = Vector256.Sum(accS); sumSq = Vector256.Sum(accQ);
                for (; i < n; i++) { float v = System.Runtime.CompilerServices.Unsafe.Add(ref p, i); sum += v; sumSq = MathF.FusedMultiplyAdd(v, v, sumSq); }
            }
            else
            {
                sum = 0f; sumSq = 0f;
                for (int i = 0; i < n; i++) { float v = x[i]; sum += v; sumSq = MathF.FusedMultiplyAdd(v, v, sumSq); }
            }
            mean = sum / n;
            float var = sumSq / n - mean * mean;
            std = var > 0f ? MathF.Sqrt(var) : 0f;   // 수치 음수(반올림) → 0
        }
    }
}
