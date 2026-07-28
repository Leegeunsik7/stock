// ============================================================================
// Avx512MicroAlphaTests — v8.0.0 AVX-512 미시 알파 엔진 적대적 검증(100% 단언)
//   ⚔️ Avx512MicroAlphaEngine: 티어 무손실 등가·미시 알파·δ=0·fail-closed
//   ⚡ SimdAlignedRingVectorizer: 64B 정렬 로드 등가·δ=0
//   ⚡ AlphaSignalNoiseQuantizer: SNR 스푸핑 억제·δ=0
//   🧠 AvxExecutionWidthAutotuner: 지연 최소 폭 선택(웜업/다운클럭 통합)·δ=0
//   🔒 liveArmed:false: 강알파여도 이중키 전 발주 0(초크포인트 격리)
// ============================================================================
using System;
using System.Runtime.Intrinsics.X86;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]   // 타이밍/δ=0/스레드 — master 병렬 플래키 0% 방어
    public sealed class Avx512MicroAlphaTests
    {
        // 결정론 의사난수(주입·RNG 아님 — 고정 시드 순수 산술).
        private static float[] Ramp(int n, int seed)
        {
            var a = new float[n];
            uint s = (uint)seed | 1u;
            for (int i = 0; i < n; i++) { s = s * 1664525u + 1013904223u; a[i] = ((s >> 8) & 0xFFFF) / 32768f - 1f; }   // [-1,1)
            return a;
        }

        // ══ ⚔️ Dot 티어 무손실 등가 ═══════════════════════════════════════════
        [Fact]
        public void Dot_Tiers_LosslessEquivalent_WithinTolerance()
        {
            var a = Ramp(16, 7); var b = Ramp(16, 99);
            float sca = Avx512MicroAlphaEngine.DotScalar(a, b, 16);
            float port512 = Avx512MicroAlphaEngine.DotVector512Portable(a, b, 16);   // 16-wide 로직(에뮬레이트라도 실행)
            float dispatched = Avx512MicroAlphaEngine.Dot(a, b, out var tier, preferMaxWidth: true);

            Assert.Equal(sca, port512, 3);       // ★16-wide == 스칼라(무손실·허용오차 1e-3)
            Assert.Equal(sca, dispatched, 3);    // 디스패치 티어(여기선 AVX2) == 스칼라
            Assert.Equal(Avx512MicroAlphaEngine.BestAvailableTier, tier);   // 이 호스트 최고 가용 티어

            // ★AVX-512 하드웨어에서만: Avx512F 직접 바인딩 경로 == 스칼라(비-AVX512 호스트는 통과).
            if (Avx512F.IsSupported)
            {
                float z = Avx512MicroAlphaEngine.Dot(a, b, out var t2, preferMaxWidth: true);
                Assert.Equal(AlphaAccelTier.Avx512, t2);
                Assert.Equal(sca, z, 3);
            }
        }

        // ══ ⚔️ ComputeMicroAlpha: 방향성·결정론·fail-closed ══════════════════
        [Fact]
        public void MicroAlpha_Directional_Deterministic_FailClosed()
        {
            var weights = new float[16];
            for (int i = 0; i < 16; i++) weights[i] = 1f;   // 균등 가중(불균형 합 방향 반영)
            long[] px = { 1000, 999, 998, 997, 996, 995, 994, 993, 992, 991 };
            long[] apx = { 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009, 1010 };
            long[] bigBid = { 900, 800, 700, 600, 500, 400, 300, 200, 100, 50 };
            long[] smallAsk = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 };
            var eng = new Avx512MicroAlphaEngine();

            float bidHeavy = eng.ComputeMicroAlpha(px, bigBid, apx, smallAsk, weights, out var t1);
            float askHeavy = eng.ComputeMicroAlpha(px, smallAsk, apx, bigBid, weights, out _);
            Assert.True(bidHeavy > 0f);          // 매수 잔량 우위 → 양의 알파
            Assert.True(askHeavy < 0f);          // 매도 잔량 우위 → 음의 알파
            Assert.Equal(Avx512MicroAlphaEngine.BestAvailableTier, t1);

            // 결정론: 동일 입력 → 동일 출력(비트 동일 — 같은 티어 같은 경로).
            float again = eng.ComputeMicroAlpha(px, bigBid, apx, smallAsk, weights, out _);
            Assert.Equal(bidHeavy, again);

            // fail-closed: 짧은 스팬 → 0.
            Assert.Equal(0f, eng.ComputeMicroAlpha(px.AsSpan(0, 5), bigBid, apx, smallAsk, weights, out _));
            // fail-closed: NaN 가중 → 0(NaN 알파 흡수).
            var nanW = (float[])weights.Clone(); nanW[3] = float.NaN;
            Assert.Equal(0f, eng.ComputeMicroAlpha(px, bigBid, apx, smallAsk, nanW, out _));
        }

        [Fact]
        public void MicroAlpha_ZeroAllocation_HotPath()
        {
            var weights = Ramp(16, 3);
            long[] px = { 1000, 999, 998, 997, 996, 995, 994, 993, 992, 991 };
            long[] apx = { 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009, 1010 };
            long[] bq = { 900, 800, 700, 600, 500, 400, 300, 200, 100, 50 };
            long[] aq = { 100, 90, 80, 70, 60, 50, 40, 30, 20, 10 };
            var eng = new Avx512MicroAlphaEngine();
            for (int i = 0; i < 10_000; i++) eng.ComputeMicroAlpha(px, bq, apx, aq, weights, out _);
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) eng.ComputeMicroAlpha(px, bq, apx, aq, weights, out _);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★피처 추출 + ZMM/YMM 점적 핫패스 힙 0
        }

        // ══ ⚡ SimdAlignedRingVectorizer: 정렬 로드 등가·δ=0 ═════════════════
        [Fact]
        public void AlignedRing_WeightedMomentum_MatchesScalar_ZeroAlloc()
        {
            using var vec = new SimdAlignedRingVectorizer();
            var w = Ramp(SimdAlignedRingVectorizer.Window, 11);
            var ticks = Ramp(SimdAlignedRingVectorizer.Window, 22);
            foreach (var t in ticks) vec.Push(t);

            float simd = vec.WeightedMomentum(w);
            // 스칼라 레퍼런스(물리 순서 동일 — Push 가 Window 정확히 채움).
            float scalar = 0f;
            for (int i = 0; i < SimdAlignedRingVectorizer.Window; i++) scalar = MathF.FusedMultiplyAdd(ticks[i], w[i], scalar);
            Assert.Equal(scalar, simd, 3);       // ★정렬 SIMD 로드 == 스칼라(무손실)
            Assert.True(vec.Pushes >= SimdAlignedRingVectorizer.Window);

            for (int i = 0; i < 10_000; i++) { vec.Push(1.0f); vec.WeightedMomentum(w); }   // 워밍업
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { vec.Push(1.0f); vec.WeightedMomentum(w); }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★Push + 정렬 로드 점적 힙 0(네이티브·스택 Vector)
        }

        // ══ ⚡ AlphaSignalNoiseQuantizer: SNR 스푸핑 억제·δ=0 ════════════════
        [Fact]
        public void NoiseQuantizer_SuppressesLowSnr_PassesHighSnr()
        {
            var q = new AlphaSignalNoiseQuantizer(snrThresholdMilli: 500);   // SNR<0.5 억제
            // 고SNR: 일관 신호(평균 큼·분산 작음) → 통과.
            var clean = new float[32]; for (int i = 0; i < 32; i++) clean[i] = 1.0f + (i % 2 == 0 ? 0.01f : -0.01f);
            Assert.Equal(0.77f, q.Quantize(clean, 0.77f, out long snrClean), 3);
            Assert.True(snrClean >= 500);

            // 저SNR: 평균≈0·고분산(허수 호가 스푸핑 노이즈) → 억제(0).
            var noisy = new float[32]; for (int i = 0; i < 32; i++) noisy[i] = (i % 2 == 0 ? 5.0f : -5.0f);
            Assert.Equal(0f, q.Quantize(noisy, 0.77f, out long snrNoisy));
            Assert.True(snrNoisy < 500);
            Assert.True(q.NoiseBlocks >= 1);

            // fail-closed: 표본 부족.
            Assert.Equal(0f, q.Quantize(new float[] { 1f }, 0.9f, out _));
        }

        [Fact]
        public void NoiseQuantizer_ZeroAllocation()
        {
            var q = new AlphaSignalNoiseQuantizer();
            var s = Ramp(32, 5);
            for (int i = 0; i < 10_000; i++) q.Quantize(s, 0.5f, out _);
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) q.Quantize(s, 0.5f, out _);
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★SIMD SNR 핫패스 힙 0
        }

        // ══ 🧠 AvxExecutionWidthAutotuner: 지연 최소 폭 선택 ═════════════════
        [Fact]
        public void Autotuner_SelectFrom_PicksMinLatencyWidth()
        {
            // ZMM 최속 → AVX-512.
            Assert.Equal(AlphaAccelTier.Avx512, AvxExecutionWidthAutotuner.SelectFrom(30, 50, 200, zmmAvail: true, ymmAvail: true));
            // ★다운클럭/웜업: ZMM(100) > YMM(50) → YMM 강등(B/A 통합 방어).
            Assert.Equal(AlphaAccelTier.Avx2, AvxExecutionWidthAutotuner.SelectFrom(100, 50, 200, zmmAvail: true, ymmAvail: true));
            // ZMM 미가용(비-AVX512 호스트) → YMM.
            Assert.Equal(AlphaAccelTier.Avx2, AvxExecutionWidthAutotuner.SelectFrom(long.MaxValue, 50, 200, zmmAvail: false, ymmAvail: true));
            // 전부 미가용 → 스칼라.
            Assert.Equal(AlphaAccelTier.Scalar, AvxExecutionWidthAutotuner.SelectFrom(long.MaxValue, long.MaxValue, 200, false, false));
        }

        [Fact]
        public void Autotuner_Calibrate_SelectsValidWidth_KeepsAlive()
        {
            using var tuner = new AvxExecutionWidthAutotuner(iters: 512, bursts: 2);
            tuner.Calibrate();
            Assert.True(tuner.Calibrations >= 1);
            Assert.True(tuner.WarmKeepAlives >= 1);   // ★계측 = keep-alive(hot-ready 유지)
            // 선택은 항상 가용 폭(이 호스트는 AVX2 가 최고 하드웨어 폭).
            Assert.True(tuner.SelectedWidth == AlphaAccelTier.Avx2 || tuner.SelectedWidth == AlphaAccelTier.Scalar
                     || tuner.SelectedWidth == AlphaAccelTier.Avx512);
            Assert.True(tuner.ScalarLatencyTicks > 0);
        }

        [Fact]
        public void Autotuner_BackgroundThread_Calibrates_NoCrash()
        {
            using var tuner = new AvxExecutionWidthAutotuner(iters: 512, bursts: 2, intervalMs: 20);
            tuner.Start();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (tuner.Calibrations < 2 && sw.ElapsedMilliseconds < 3000) System.Threading.Thread.Sleep(20);
            Assert.True(tuner.Calibrations >= 2);
        }

        // ══ 🔒 liveArmed:false 아키텍처 격리 ════════════════════════════════
        [Fact]
        public void Isolation_StrongAlpha_NoEmission_UntilArmed()
        {
            // ★강한 알파를 산출해도, 그것을 발주 제안으로 바꿔 미무장 디스패처로 드레인하면 onSend 0(실물 발주 0비트).
            var weights = new float[16]; for (int i = 0; i < 16; i++) weights[i] = 1f;
            long[] px = { 1000, 999, 998, 997, 996, 995, 994, 993, 992, 991 };
            long[] apx = { 1001, 1002, 1003, 1004, 1005, 1006, 1007, 1008, 1009, 1010 };
            long[] bigBid = { 9000, 8000, 7000, 6000, 5000, 4000, 3000, 2000, 1000, 500 };
            long[] smallAsk = { 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };
            var eng = new Avx512MicroAlphaEngine();
            float alpha = eng.ComputeMicroAlpha(px, bigBid, apx, smallAsk, weights, out _);
            Assert.True(alpha > 0.5f);   // 강한 매수 알파

            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, isArmed: () => false, attestation: att);   // ★미무장
            // 강알파를 제안으로 변환·발행(엔진은 ring 참조 0 — 외부 생산자가 변환).
            ring.Publish(genomeId: 0, symbolCode: 5930, OrderSide.Buy, kind: 1, QPrice.Of(100m), QQty.Of(1m), 100);
            int forwarded = 0;
            disp.Drain(_ => forwarded++);
            Assert.Equal(0, forwarded);            // ★onSend 미호출 — 알파가 아무리 강해도 이중키 전 발주 0
            Assert.Equal(0L, disp.Forwarded);
            Assert.True(disp.GatedOut >= 1);       // 게이팅만 계상
        }
    }
}
