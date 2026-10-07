// ============================================================================
// SimdAlignedRingVectorizer — v8.0.0 64Byte 정렬 SIMD 링 익스트랙터
//
//  원시 호가 틱(마이크로프라이스)을 64Byte 캐시라인 경계에 정밀 정렬된 언매니지드 링버퍼에 적재하고, ZMM 정렬
//  로드(Avx512F.LoadAlignedVector512)로 창(window)을 읽어 '비정렬 메모리 읽기 페널티'를 박멸한다. 정렬 창을
//  가중치와 SIMD 점적해 '가중 모멘텀' 피처를 δ=0 로 산출 — 알파 엔진의 모멘텀 축에 무복사 공급.
//
//  ── 티어드 정렬 로드(무손실 강등) ─────────────────────────────────────────
//   · Avx512F : ★Avx512F.LoadAlignedVector512 직접 바인딩(64B 정렬 필수 — 버퍼 base 64B 정렬·16 float 청크 경계).
//   · Avx2    : Avx.LoadAlignedVector256(32B 정렬).
//   · Scalar  : 순차.
//   창 크기 N=64(2^k·float) — base 64B 정렬이면 0/16/32/48 오프셋 로드가 전부 64B 정렬(ZMM=64B).
//
//  ★불변: δ=0(Push/WeightedMomentum 핫패스 힙 0·네이티브 포인터·스택 Vector) · 결정론(순수) · liveArmed:false
//    (순수 피처 추출 — IBroker·소켓·onSend 참조 0) · fail-closed(미지원 → 스칼라). ★단일 라이터(Push).
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>64B 정렬 SIMD 틱 링 익스트랙터(δ=0·정렬 로드). Push(tick) → WeightedMomentum(weights). IDisposable.</summary>
    public sealed unsafe class SimdAlignedRingVectorizer : IDisposable
    {
        public const int Window = 64;   // 2^k float — base 64B 정렬 시 16-float(ZMM) 청크 경계 정렬

        private float* _buf;            // 64B 정렬 네이티브(Window float)
        private long   _pushes;
        private long   _alignedLoads;
        private int    _disposed;
        private int    _gate, _leaked;   // [크래시 트리아지] 수명 게이트 — 앱 종료 시 링 샘플 타이머가 해제 후 판독하던 경로 차단

        public SimdAlignedRingVectorizer()
        {
            _buf = (float*)NativeMemory.AlignedAlloc((nuint)(Window * sizeof(float)), 64);   // ★64B 캐시라인 정렬
            NativeMemory.Clear(_buf, (nuint)(Window * sizeof(float)));
        }

        public long Pushes       => Interlocked.Read(ref _pushes);
        public long AlignedLoads => Interlocked.Read(ref _alignedLoads);
        public bool LeakedOnDispose => Volatile.Read(ref _leaked) == 1;
        internal bool NativeReleased => _buf == null;
        internal bool HoldForTest() => NativeLifetimeGate.TryEnter(ref _gate);
        internal void ReleaseHoldForTest() => NativeLifetimeGate.Exit(ref _gate);

        /// <summary>틱 1건 적재(δ=0·단일 라이터). 링 물리 순서로 순환 기록.</summary>
        public void Push(float tick)
        {
            if (!NativeLifetimeGate.TryEnter(ref _gate)) return;
            try
            {
                float* b = _buf; if (b == null) return;
                long idx = Interlocked.Read(ref _pushes);
                b[(int)(idx & (Window - 1))] = tick;
                Interlocked.Increment(ref _pushes);
            }
            finally { NativeLifetimeGate.Exit(ref _gate); }
        }

        /// <summary>
        /// 정렬 창(전체 Window)과 가중치의 SIMD 점적 → 가중 모멘텀(δ=0). weights.Length &lt; Window 면 0(fail-closed).
        /// ★Avx512F.LoadAlignedVector512(64B 정렬 로드) 직접 바인딩 — 비정렬 페널티 박멸. 미지원 → YMM/스칼라.
        /// </summary>
        public float WeightedMomentum(ReadOnlySpan<float> weights)
        {
            if (!NativeLifetimeGate.TryEnter(ref _gate)) return 0f;   // 해제됨 = 0(fail-closed)
            try { return WeightedMomentumCore(weights); }
            finally { NativeLifetimeGate.Exit(ref _gate); }
        }

        private float WeightedMomentumCore(ReadOnlySpan<float> weights)
        {
            float* b = _buf;
            if (b == null || weights.Length < Window) return 0f;
            ref float pw = ref MemoryMarshal.GetReference(weights);

            float sum;
            if (Avx512F.IsSupported)
            {
                var acc = Vector512<float>.Zero;
                int lanes = Vector512<float>.Count;   // 16
                for (int i = 0; i <= Window - lanes; i += lanes)
                {
                    var vb = Avx512F.LoadAlignedVector512(b + i);   // ★64B 정렬 로드 직접 바인딩(base·오프셋 모두 64B 정렬)
                    var vw = Vector512.LoadUnsafe(ref pw, (nuint)i);
                    acc = Avx512F.FusedMultiplyAdd(vb, vw, acc);
                }
                sum = Vector512.Sum(acc);
                Interlocked.Increment(ref _alignedLoads);
            }
            else if (Avx2.IsSupported && Fma.IsSupported)
            {
                var acc = Vector256<float>.Zero;
                int lanes = Vector256<float>.Count;   // 8
                for (int i = 0; i <= Window - lanes; i += lanes)
                {
                    var vb = Avx.LoadAlignedVector256(b + i);       // ★32B 정렬 로드(base 64B 정렬 → 8-float 경계도 32B 정렬)
                    var vw = Vector256.LoadUnsafe(ref pw, (nuint)i);
                    acc = Fma.MultiplyAdd(vb, vw, acc);
                }
                sum = Vector256.Sum(acc);
                Interlocked.Increment(ref _alignedLoads);
            }
            else
            {
                sum = 0f;
                for (int i = 0; i < Window; i++) sum = MathF.FusedMultiplyAdd(b[i], Unsafe.Add(ref pw, i), sum);
            }
            return float.IsNaN(sum) || float.IsInfinity(sum) ? 0f : sum;   // fail-closed
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            if (!NativeLifetimeGate.CloseAndDrain(ref _gate, NativeLifetimeGate.DefaultDrainTimeout)) { Volatile.Write(ref _leaked, 1); return; }
            float* b = _buf; _buf = null;
            if (b != null) NativeMemory.AlignedFree(b);
        }
    }
}
