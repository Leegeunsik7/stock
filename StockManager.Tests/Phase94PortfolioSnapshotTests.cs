// ============================================================================
// Phase94PortfolioSnapshotTests — Phase 94-B 포트폴리오 스냅샷 추상화 + 무결성 센티널 검증(100% 단언)
//   🛡 IPortfolioSnapshot: 읽기 전용 계약(setter 없음 = 컴파일 타임 UI 불변)
//   🔬 PortfolioSnapshotIntegritySentinel(Think-Beyond): 결정론 해시·변조(배열 원소 캐스트-백) 탐지·fail-closed
//   ⚡ δ=0: ComputeHash 핫패스 힙 0
// ============================================================================
using System;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94PortfolioSnapshotTests
    {
        // 테스트용 IPortfolioSnapshot 구현(배열 변조 시뮬레이션용 가변 필드).
        private sealed class FakeSnapshot : IPortfolioSnapshot
        {
            public bool Loaded { get; set; } = true;
            public string Error { get; set; } = "";
            public string[] Names { get; set; } = { "삼성전자", "AAPL" };
            public double[][] R { get; set; } = { new[] { 0.1, -0.2, 0.05 }, new[] { 0.3, 0.0, -0.1 } };
            public double[] Weights { get; set; } = { 0.6, 0.4 };
            public int HoldingCount { get; set; } = 2;
            public bool[] UsdMask { get; set; } = { false, true };
            public double Total { get; set; } = 1_000_000d;                 // Phase 94-I
            public string[] Symbols { get; set; } = { "005930", "AAPL" };   // Phase 94-I
        }

        [Fact]
        public void Hash_Deterministic_And_Verify()
        {
            var s = new FakeSnapshot();
            long h1 = PortfolioSnapshotIntegritySentinel.ComputeHash(s);
            long h2 = PortfolioSnapshotIntegritySentinel.ComputeHash(s);
            Assert.Equal(h1, h2);                                        // 동일 내용 → 동일 해시(결정론)
            Assert.True(PortfolioSnapshotIntegritySentinel.Verify(s, h1)); // 무변조 → 검증 통과
        }

        [Fact]
        public void Hash_Detects_Array_Element_Tampering()
        {
            var s = new FakeSnapshot();
            long pre = PortfolioSnapshotIntegritySentinel.ComputeHash(s);   // 렌더 전 캡처
            s.R[0][0] = 999.0;                                             // ★UI가 캐스트-백으로 수익률 행렬 변조
            Assert.False(PortfolioSnapshotIntegritySentinel.Verify(s, pre)); // → 탐지·fail-closed
        }

        [Fact]
        public void Hash_Detects_Weight_Tampering()
        {
            var s = new FakeSnapshot();
            long pre = PortfolioSnapshotIntegritySentinel.ComputeHash(s);
            s.Weights[1] = 0.999;                                         // 비중 변조
            Assert.False(PortfolioSnapshotIntegritySentinel.Verify(s, pre));
        }

        [Fact]
        public void Hash_DifferentContent_DifferentHash()
        {
            var a = new FakeSnapshot();
            var b = new FakeSnapshot { HoldingCount = 3 };
            Assert.NotEqual(PortfolioSnapshotIntegritySentinel.ComputeHash(a),
                            PortfolioSnapshotIntegritySentinel.ComputeHash(b));
        }

        [Fact]
        public void Hash_Null_And_Empty_FailSafe()
        {
            Assert.Equal(0L, PortfolioSnapshotIntegritySentinel.ComputeHash(null!));   // null 스냅샷 → 0(NRE 없음)
            var empty = new FakeSnapshot { Names = Array.Empty<string>(), R = Array.Empty<double[]>(),
                                           Weights = Array.Empty<double>(), UsdMask = Array.Empty<bool>(), HoldingCount = 0 };
            long h = PortfolioSnapshotIntegritySentinel.ComputeHash(empty);   // 빈 배열도 결정론(throw 0)
            Assert.Equal(h, PortfolioSnapshotIntegritySentinel.ComputeHash(empty));
        }

        [Fact]
        public void ComputeHash_ZeroAllocation()
        {
            var s = new FakeSnapshot();
            for (int i = 0; i < 10_000; i++) _ = PortfolioSnapshotIntegritySentinel.ComputeHash(s);  // 워밍업
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                long acc = 0;
                for (int i = 0; i < 200_000; i++) acc ^= PortfolioSnapshotIntegritySentinel.ComputeHash(s);
                GC.KeepAlive(acc);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, Measure());   // ★해시 핫패스 힙 0(FNV·DoubleToInt64Bits·순회, string/배열 미생성)
        }
    }
}
