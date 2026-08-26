// ============================================================================
// Avx512MicroAlphaEngine — v8.0.0 AVX-512 무할당 초저지연 미시 알파 엔진
//
//  외부 Python/C++ DLL·GPU IPC 통신 오버헤드 없이, L1/L2 캐시 내에서 L2 10호가 피처(잔량 불균형·스프레드·
//  미시 모멘텀)를 16×float 특징벡터로 추출해 가중치와 512-bit ZMM 병렬 점적(FMA)으로 알파를 δ=0 완사출한다.
//
//  ── 티어드 SIMD(무손실 하드웨어 안심 강등) ─────────────────────────────────
//   · Tier 1  Avx512F.IsSupported : ★Avx512F.FusedMultiplyAdd 직접 바인딩(16 lane) — 512-bit ZMM.
//   · Tier 2  Avx2 ∧ Fma          : ★Fma.MultiplyAdd 직접 바인딩(8 lane ×2) — 256-bit YMM.
//   · Tier 3  Scalar              : MathF.FusedMultiplyAdd 순차 — 전 CPU.
//   세 티어는 동일 수식(FMA·점적) — 부동소수 리듀스 순서 차이로 비트-완전 대신 tight 허용오차 내 등가(무손실 강등).
//   ★로드/수평합은 하우스 관례(Vector512.LoadUnsafe·Vector512.Sum·Vector*.IsHardwareAccelerated) 따름 —
//   SimdRiskInterlock.ScanOutOfRange 의 검증된 δ=0 패턴. 곱셈-누산(FMA) 코어만 커맨더 지시대로 인트린식 직접 바인딩.
//
//  ★불변: δ=0(Dot/ComputeMicroAlpha 핫패스 힙 0·stackalloc·스택 Vector·Interlocked) · 결정론(입력 순수 함수·
//    wall-clock/RNG 0) · liveArmed:false(IBroker·소켓·onSend·ring 참조 0 — 알파 '수치'만 산출. 발주는 하류
//    OrderDispatchRing→GatedOrderDispatcher 무장 게이트가 전담) · fail-closed(NaN/미지원 → 스칼라 안심 강등).
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>SIMD 가속 티어(활성 실행 경로 보고용).</summary>
    public enum AlphaAccelTier { Scalar = 0, Avx2 = 1, Avx512 = 2 }

    /// <summary>AVX-512 무할당 미시 알파 엔진(δ=0·티어드·결정론·liveArmed:false). 10호가 피처 → 16-wide ZMM 점적.</summary>
    public sealed class Avx512MicroAlphaEngine
    {
        public const int FeatureCount = 16;   // 정확히 1 ZMM(16×float) — 피처: [0..9]호가별 불균형·[10]총심도불균형·[11]스프레드·[12]마이크로프라이스·[13..15]근접 모멘텀

        private long _computations;
        private int  _lastTier;               // AlphaAccelTier
        private long _lastAlphaMilli;         // 최근 알파×1000(텔레메트리·정수 스냅샷)

        public long           Computations => Interlocked.Read(ref _computations);
        public AlphaAccelTier LastTier     => (AlphaAccelTier)Volatile.Read(ref _lastTier);
        public long           LastAlphaMilli => Interlocked.Read(ref _lastAlphaMilli);
        /// <summary>부팅 시점 최고 가용 티어(진단). 실행 티어는 오토튜너가 좁힐 수 있음.</summary>
        public static AlphaAccelTier BestAvailableTier =>
            Avx512F.IsSupported ? AlphaAccelTier.Avx512 : ((Avx2.IsSupported && Fma.IsSupported) ? AlphaAccelTier.Avx2 : AlphaAccelTier.Scalar);

        /// <summary>
        /// L2 10호가 스팬에서 16 피처를 추출해 가중치와 ZMM 점적 → 알파 산출(δ=0). preferMaxWidth=false 면 오토튜너/호출자가
        /// YMM 로 강등 지시(다운클럭/웜업 회피). 반환: 알파 스코어. tier: 실제 실행 경로. 스팬 부족 시 0(fail-closed).
        /// </summary>
        public float ComputeMicroAlpha(ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                                       ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty,
                                       ReadOnlySpan<float> weights, out AlphaAccelTier tier, bool preferMaxWidth = true)
        {
            tier = AlphaAccelTier.Scalar;
            if (bidPx.Length < 10 || bidQty.Length < 10 || askPx.Length < 10 || askQty.Length < 10 || weights.Length < FeatureCount)
                return 0f;   // fail-closed(불완전 호가/가중치)

            Span<float> f = stackalloc float[FeatureCount];   // δ=0(스택)
            ExtractFeatures(bidPx, bidQty, askPx, askQty, f);

            float alpha = Dot(f, weights.Slice(0, FeatureCount), out tier, preferMaxWidth);
            if (float.IsNaN(alpha) || float.IsInfinity(alpha)) alpha = 0f;   // fail-closed(수치 파열 → 중립)

            Interlocked.Increment(ref _computations);
            Volatile.Write(ref _lastTier, (int)tier);
            Interlocked.Exchange(ref _lastAlphaMilli, (long)(alpha * 1000f));
            return alpha;
        }

        // ── 10호가 → 16 피처(δ=0·스칼라 전처리). 고정소수점 raw long → 정규화 float. ──
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ExtractFeatures(ReadOnlySpan<long> bidPx, ReadOnlySpan<long> bidQty,
                                             ReadOnlySpan<long> askPx, ReadOnlySpan<long> askQty, Span<float> f)
        {
            long totBid = 0, totAsk = 0;
            for (int i = 0; i < 10; i++)
            {
                long bq = bidQty[i], aq = askQty[i];
                f[i] = (float)(bq - aq) / (bq + aq + 1L);   // 호가별 잔량 불균형 [-1,1](분모 +1 = 0/0 방지)
                totBid += bq; totAsk += aq;
            }
            f[10] = (float)(totBid - totAsk) / (totBid + totAsk + 1L);          // 총심도 불균형
            long bestBid = bidPx[0], bestAsk = askPx[0];
            long mid = (bestBid + bestAsk) / 2L;
            f[11] = mid > 0 ? (float)(bestAsk - bestBid) / mid : 0f;            // 정규화 스프레드
            long bq0 = bidQty[0], aq0 = askQty[0], den0 = bq0 + aq0 + 1L;
            long micro = (bestBid * aq0 + bestAsk * bq0) / den0;               // 마이크로프라이스(반대편 잔량 가중)
            f[12] = mid > 0 ? (float)(micro - mid) / mid : 0f;                 // 마이크로프라이스 틸트
            // 근접 3호가 모멘텀(감쇠 가중 잔량 불균형 — 상위 호가 우선)
            f[13] = (float)(bidQty[0] - askQty[0]) / (bidQty[0] + askQty[0] + 1L);
            f[14] = (float)(bidQty[1] - askQty[1]) / (bidQty[1] + askQty[1] + 1L) * 0.6f;
            f[15] = (float)(bidQty[2] - askQty[2]) / (bidQty[2] + askQty[2] + 1L) * 0.36f;
        }

        /// <summary>
        /// 16-wide(임의 길이) 점적 곱(δ=0·티어드). preferMaxWidth=false → ZMM 건너뛰고 YMM(다운클럭/웜업 회피 지시).
        /// ★FMA 코어만 인트린식 직접 바인딩(Avx512F.FusedMultiplyAdd/Fma.MultiplyAdd), 로드/리듀스는 하우스 관례.
        /// </summary>
        public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b, out AlphaAccelTier tier, bool preferMaxWidth = true)
        {
            int n = Math.Min(a.Length, b.Length);
            if (preferMaxWidth && Avx512F.IsSupported && n >= Vector512<float>.Count)
            { tier = AlphaAccelTier.Avx512; return DotAvx512(a, b, n); }
            if (Avx2.IsSupported && Fma.IsSupported && n >= Vector256<float>.Count)
            { tier = AlphaAccelTier.Avx2; return DotAvx2(a, b, n); }
            tier = AlphaAccelTier.Scalar; return DotScalar(a, b, n);
        }

        // Tier 1 — 512-bit ZMM. ★Avx512F.FusedMultiplyAdd 직접 바인딩(16 lane). 로드 Vector512.LoadUnsafe·리듀스 Vector512.Sum(하우스).
        private static float DotAvx512(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int n)
        {
            ref float pa = ref MemoryMarshal.GetReference(a);
            ref float pb = ref MemoryMarshal.GetReference(b);
            var acc = Vector512<float>.Zero;
            int i = 0, lanes = Vector512<float>.Count;   // 16
            for (; i <= n - lanes; i += lanes)
            {
                var va = Vector512.LoadUnsafe(ref pa, (nuint)i);
                var vb = Vector512.LoadUnsafe(ref pb, (nuint)i);
                acc = Avx512F.FusedMultiplyAdd(va, vb, acc);   // ★직접 바인딩: acc += va*vb(fused)
            }
            float sum = Vector512.Sum(acc);
            for (; i < n; i++) sum = MathF.FusedMultiplyAdd(Unsafe.Add(ref pa, i), Unsafe.Add(ref pb, i), sum);
            return sum;
        }

        // Tier 2 — 256-bit YMM. ★Fma.MultiplyAdd 직접 바인딩(8 lane). 로드 Vector256.LoadUnsafe·리듀스 Vector256.Sum.
        private static float DotAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int n)
        {
            ref float pa = ref MemoryMarshal.GetReference(a);
            ref float pb = ref MemoryMarshal.GetReference(b);
            var acc = Vector256<float>.Zero;
            int i = 0, lanes = Vector256<float>.Count;   // 8
            for (; i <= n - lanes; i += lanes)
            {
                var va = Vector256.LoadUnsafe(ref pa, (nuint)i);
                var vb = Vector256.LoadUnsafe(ref pb, (nuint)i);
                acc = Fma.MultiplyAdd(va, vb, acc);   // ★직접 바인딩: acc += va*vb(fused)
            }
            float sum = Vector256.Sum(acc);
            for (; i < n; i++) sum = MathF.FusedMultiplyAdd(Unsafe.Add(ref pa, i), Unsafe.Add(ref pb, i), sum);
            return sum;
        }

        // Tier 3 — 순차 FMA(전 CPU 기준·결정론 레퍼런스·검증용 공개).
        public static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int n)
        {
            float sum = 0f;
            for (int i = 0; i < n; i++) sum = MathF.FusedMultiplyAdd(a[i], b[i], sum);
            return sum;
        }

        // 이식형 512-bit(Vector512<float> — AVX-512 미지원 호스트에선 소프트웨어 에뮬레이트). 16-wide 로직 검증용 공개.
        public static float DotVector512Portable(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int n)
        {
            ref float pa = ref MemoryMarshal.GetReference(a);
            ref float pb = ref MemoryMarshal.GetReference(b);
            var acc = Vector512<float>.Zero;
            int i = 0, lanes = Vector512<float>.Count;
            for (; i <= n - lanes; i += lanes)
                acc += Vector512.LoadUnsafe(ref pa, (nuint)i) * Vector512.LoadUnsafe(ref pb, (nuint)i);
            float sum = Vector512.Sum(acc);
            for (; i < n; i++) sum = MathF.FusedMultiplyAdd(Unsafe.Add(ref pa, i), Unsafe.Add(ref pb, i), sum);
            return sum;
        }
    }
}
