// ============================================================================
// HardwareCampaignTests — v7.3.0 하드웨어 대진격 적대적 검증(100% 단언)
//   ⚡ CpuAffinityPinningGrid: P-Core 감지·핀·δ=0 코어 폴·fail-safe
//   🌡️ ThermalFrequencyJitterGuard: 유효주파수 스로틀 감지·가역 홀드·δ=0(주입 시계 결정론)
//   🧠 CacheLineCoherenceSentinel: 캐시라인 격리 증명·아레나 카나리·코어 바운싱·δ=0
// ============================================================================
using System;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class HardwareCampaignTests
    {
        // ══ ⚡ CpuAffinityPinningGrid ═══════════════════════════════════════
        [Fact]
        public void CpuAffinity_ConstructsConsistently_AndSafeFallback()
        {
            var grid = new CpuAffinityPinningGrid();
            Assert.True(grid.PCoreCount >= 0);
            Assert.True(grid.PinFailures >= 0);
            // 감지 성공 시 마스크 팝카운트 == P-Core 수(정합). 비-Windows/구형 → Supported=false·마스크 0(안심 강등).
            if (grid.Supported)
            {
                Assert.NotEqual((nuint)0, grid.PCoreMask);
                Assert.Equal(grid.PCoreCount, System.Numerics.BitOperations.PopCount((ulong)grid.PCoreMask));
            }
            else
            {
                Assert.Equal((nuint)0, grid.PCoreMask);
            }
        }

        [Fact]
        public void CpuAffinity_CurrentCore_And_Poll_ZeroAllocation()
        {
            var grid = new CpuAffinityPinningGrid();
            for (int i = 0; i < 10_000; i++) { grid.CurrentCore(); grid.AttestPinnedCore(); }   // 워밍업
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { grid.CurrentCore(); grid.AttestPinnedCore(); }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★코어 폴 힙 0
        }

        [Fact]
        public void CpuAffinity_PinOnSpawnedThread_NoCrash()
        {
            var grid = new CpuAffinityPinningGrid();
            bool ran = false;
            var t = new Thread(() =>
            {
                grid.PinCurrentThread();          // 성공/실패 무관 — 예외 없이 반환(fail-safe)
                grid.AttestPinnedCore();
                ran = true;
            }) { IsBackground = true, Name = "PinTest" };
            t.Start();
            Assert.True(t.Join(5000));
            Assert.True(ran);
            Assert.True(grid.MigrationEvents >= 0);
        }

        // ══ 🌡️ ThermalFrequencyJitterGuard (주입 시계 결정론) ═══════════════
        [Fact]
        public void Thermal_ThrottleDetected_SustainedThenReversible()
        {
            long fake = 0, dur = 100;                       // dur = 버스트당 주입 소요(주파수 반비례)
            Func<long> clk = () => { long v = fake; fake += dur; return v; };
            var g = new ThermalFrequencyJitterGuard(iters: 1000, bursts: 2, throttlePermille: 880, sustainSamples: 2, nowTicks: clk);

            g.Sample();                                     // 기준선 확립(dt=100 → 유효주파수 1000‰)
            Assert.Equal(1000L, g.EffectiveFreqPermille);
            Assert.False(g.SafeHoldActive);

            dur = 200;                                      // 2배 느려짐(스로틀 → 500‰)
            g.Sample();                                     // 연속 1 — 아직 미지속
            Assert.False(g.SafeHoldActive);
            g.Sample();                                     // 연속 2 ≥ sustain → 홀드
            Assert.True(g.SafeHoldActive);                  // ★서멀 스로틀 안전 홀드
            Assert.Equal(500L, g.EffectiveFreqPermille);
            Assert.True(g.ThrottleHolds >= 1);

            dur = 100;                                      // 회복 → 홀드 해제(가역·자기 만료)
            g.Sample();
            Assert.False(g.SafeHoldActive);
            Assert.Equal(1000L, g.EffectiveFreqPermille);
        }

        [Fact]
        public void Thermal_Sample_ZeroAllocation_HotPath()
        {
            var g = new ThermalFrequencyJitterGuard(iters: 2000, bursts: 2);   // 실 QPC·실 워크로드
            for (int i = 0; i < 200; i++) g.Sample();   // 워밍업(tier-up·기준선)
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2_000; i++) g.Sample();
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★유효주파수 실측 핫패스 힙 0
        }

        // ══ 🧠 CacheLineCoherenceSentinel ═══════════════════════════════════
        // 두 필드가 서로 다른 64B 라인에 있는지 정적 증명 검증용 레이아웃.
        private struct SameLine  { public long A; public long B; }                 // 8B 이격(같은 라인)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct DiffLine  { [System.Runtime.InteropServices.FieldOffset(0)] public long A;
                                   [System.Runtime.InteropServices.FieldOffset(64)] public long B; }   // 64B 이격(다른 라인)

        [Fact]
        public void Coherence_AttestIsolated_DetectsCacheLineSeparation()
        {
            var same = new SameLine();
            Assert.False(CacheLineCoherenceSentinel.AttestIsolated(ref same.A, ref same.B));   // 8B → 같은 라인(False Sharing 위험)
            var diff = new DiffLine();
            Assert.True(CacheLineCoherenceSentinel.AttestIsolated(ref diff.A, ref diff.B));    // 64B → 다른 라인(격리)
        }

        [Fact]
        public void Coherence_RecordCounter_And_ArenaIntact()
        {
            using var s = new CacheLineCoherenceSentinel(slots: 8);
            for (int i = 0; i < 1000; i++) s.Record(3);
            Assert.Equal(1000L, s.CounterOf(3));    // 슬롯 카운터 정확
            Assert.Equal(0L, s.CounterOf(4));       // 다른 슬롯 무간섭(격리)
            Assert.True(s.AttestArena());            // 카나리 무결(파열 없음)
            Assert.False(s.CoherenceFuseTripped);
            s.Record(999);                           // 범위초과 → 무시(무해·OOB 없음)
        }

        [Fact]
        public void Coherence_ConcurrentDistinctSlots_NoFalseSharing_CountsExact()
        {
            using var s = new CacheLineCoherenceSentinel(slots: 4);
            // 4개 코어-격리 슬롯을 4스레드가 각자 전용으로 증가 → False Sharing 구조적 불가·계수 정확.
            var threads = new Thread[4];
            for (int t = 0; t < 4; t++)
            {
                int slot = t;
                threads[t] = new Thread(() => { for (int i = 0; i < 100_000; i++) s.Record(slot); }) { IsBackground = true };
            }
            foreach (var th in threads) th.Start();
            foreach (var th in threads) Assert.True(th.Join(15000));
            for (int slot = 0; slot < 4; slot++) Assert.Equal(100_000L, s.CounterOf(slot));   // 원자 정확
            Assert.True(s.AttestArena());   // 동시 기록에도 카나리 무결
        }

        [Fact]
        public void Coherence_Record_And_Attest_ZeroAllocation()
        {
            using var s = new CacheLineCoherenceSentinel(slots: 8);
            for (int i = 0; i < 10_000; i++) { s.Record(i & 7); s.AttestArena(); }   // 워밍업
            long Measure()
            {
                long b0 = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { s.Record(i & 7); s.AttestArena(); }
                return GC.GetAllocatedBytesForCurrentThread() - b0;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★Record+AttestArena 힙 0(네이티브 포인터·Interlocked)
        }
    }
}
