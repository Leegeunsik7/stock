// ============================================================================
// NanoPipelineBenchmark — v6.2.0 Task 1: 나노 파이프라인 마이크로-벤치마크 하네스
//
//  ★도구 선택 정직 고지: 이 프로젝트(StockManager.PrimesBench)는 csproj 에 명시된 '무의존 원칙'에 따라
//    BenchmarkDotNet 을 의도적으로 배제하고 Stopwatch 기반 경량 하니스를 쓴다. 본 하네스는 그 관례를 존중해
//    BenchmarkDotNet 과 동일 지표([Mean ns/Op], [Allocated Bytes], [Throughput MPS], AVX-512 vs AVX2 티어)를
//    Stopwatch + GC.GetAllocatedBytesForCurrentThread 로 재현한다(오프라인 복원 리스크 0·외부 의존 0).
//
//  측정 파이프라인(End-to-End): L3 메시지 → ZeroCopyOrderBook.Apply → SpeculativeOrderBook.TryResolve →
//    SimdRiskInterlock.Validate → NvmeAuditJournal MMF 직기입 (NanoPipelineIntegrator 오케스트레이션).
//
//  실행:  NanoPipelineBenchmark.Run();   (Program 에서 호출 — 기존 Program.cs 는 무수정, 필요 시 1줄 배선)
//  AVX-512 vs AVX2 비교: 두 번 실행하되 한 번은 `set DOTNET_EnableAVX512F=0` 로 런칭(런타임 피처 플래그).
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.Intrinsics;

namespace StockManager.PrimesBench
{
    public static class NanoPipelineBenchmark
    {
        private const int Levels = 10;

        public static void Run(int iterations = 1_000_000)
        {
            string simdTier = Vector512.IsHardwareAccelerated ? "AVX-512 (Vector512<long>, 8 lane/op)"
                            : Vector256.IsHardwareAccelerated ? "AVX2 (Vector256<long>, 4 lane/op)"
                            : "Scalar (no SIMD)";
            Console.WriteLine("== MS2E Nano-Pipeline Benchmark (Stopwatch harness, no external deps) ==");
            Console.WriteLine($"  SIMD tier      : {simdTier}");
            Console.WriteLine($"  Iterations     : {iterations:N0}");

            string path = Path.Combine(Path.GetTempPath(), "ms2e_bench_" + Guid.NewGuid().ToString("N") + ".mmf");
            // ★예외누수 가드: 디스포저블을 try 밖 null 선언·try 안 생성 → 생성자 throw 시에도 finally 가 null-guard 로 안전 해제.
            ZeroCopyOrderBook? book = null;
            ClaudeBlackSwanEngine? swan = null;
            NvmeAuditJournal? journal = null;
            try
            {
                book = new ZeroCopyOrderBook();
                swan = new ClaudeBlackSwanEngine();
                var risk = new SimdRiskInterlock();
                var spec = new SpeculativeOrderBook();
                var vault = new CausalAuditVault(iterations + 300_000);
                journal = new NvmeAuditJournal(vault, path, iterations + 300_000);
                for (int i = 0; i < Levels; i++) { book.Set(0, i, 10000 - i, 100 + i); book.Set(1, i, 10010 + i, 100 + i); }
                var integ = new NanoPipelineIntegrator(book, risk, spec, swan, SimdRiskLimits.Default, journal);

                Span<byte> msg = stackalloc byte[20];
                msg[0] = 1; msg[1] = 0;   // Modify bid(잔량만 변동 → 책 유효 유지)

                // ── 웜업(티어드 JIT/OSR 흡수) ──
                for (int i = 0; i < 200_000; i++) { Fill(msg, i); integ.Process(msg); }

                // ── 측정 ──
                long allocBefore = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iterations; i++) { Fill(msg, i); integ.Process(msg); }
                sw.Stop();
                long allocDelta = GC.GetAllocatedBytesForCurrentThread() - allocBefore;

                double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
                double mps     = iterations / sw.Elapsed.TotalSeconds / 1_000_000.0;

                Console.WriteLine($"  Mean           : {nsPerOp:F2} ns/op");
                Console.WriteLine($"  Allocated      : {allocDelta} B   {(allocDelta == 0 ? "(delta=0 OK)" : "(delta!=0 FAIL)")}");
                Console.WriteLine($"  Throughput     : {mps:F2} MPS (million ops/sec)");
                Console.WriteLine($"  Journaled      : {journal.RecordsJournaled:N0} records -> MMF");
                Console.WriteLine($"  SwanHits/SpecHits/RiskVetoes : {integ.SwanHits:N0} / {integ.SpecHits:N0} / {integ.RiskVetoes:N0}");
            }
            finally { journal?.Dispose(); book?.Dispose(); swan?.Dispose(); try { File.Delete(path); } catch { /* best-effort */ } }
        }

        /// <summary>SIMD 10호가 검증 단독 마이크로-벤치(AVX-512 vs AVX2 레인 속도 비교용·δ=0).</summary>
        public static void RunSimdOnly(int iterations = 20_000_000)
        {
            string tier = Vector512.IsHardwareAccelerated ? "AVX-512" : Vector256.IsHardwareAccelerated ? "AVX2" : "Scalar";
            Span<long> bp = stackalloc long[Levels], bq = stackalloc long[Levels], ap = stackalloc long[Levels], aq = stackalloc long[Levels];
            for (int i = 0; i < Levels; i++) { bp[i] = 10000 - i; ap[i] = 10010 + i; bq[i] = 100 + i; aq[i] = 100 + i; }
            var lim = SimdRiskLimits.Default;

            for (int i = 0; i < 500_000; i++) SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim);   // 웜업
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) SimdRiskInterlock.ValidateCore(bp, bq, ap, aq, lim);
            sw.Stop();
            double nsPerOp = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / iterations;
            Console.WriteLine($"== SIMD ValidateCore ({tier}) : {nsPerOp:F2} ns/op, {iterations / sw.Elapsed.TotalSeconds / 1e6:F1} M-validate/s ==");
        }

        private static void Fill(Span<byte> msg, int i)
        {
            msg[2] = (byte)(i % Levels);
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(4), 0);            // price 0 -> 유지
            BinaryPrimitives.WriteInt64LittleEndian(msg.Slice(12), 100 + (i % 37));
        }
    }
}
