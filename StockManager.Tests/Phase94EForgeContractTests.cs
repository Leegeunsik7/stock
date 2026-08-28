// ============================================================================
// Phase94EForgeContractTests — v11.4.0(94-E) 전략 단조소 읽기 전용 계약 지상 검증
//   이주된 순수 RCL ForgePage 가 소비하는 IForgeReport/IForgeCandidate 를 콘크리트 ForgeResult/ForgeCandidate 가
//   공변으로 정확히 구현하는지(값 보존·업캐스트) 확인. 순수 타입 검증(DI 불요).
// ============================================================================
using MS2E.UI.Portable.Ports.Strategy;
using Xunit;

namespace StockManager.Tests
{
    public sealed class Phase94EForgeContractTests
    {
        [Fact]
        public void ForgeResult_Implements_IForgeReport_Covariant_ValuesPreserved()
        {
            var c0 = new MS2E.Infrastructure.ForgeCandidate(5, 20, 3.5, 1.2, 2.9, 14, 4.0, true);
            var c1 = new MS2E.Infrastructure.ForgeCandidate(8, 30, 2.1, 0.8, 1.7, 9, 2.2, true);
            var fr = new MS2E.Infrastructure.ForgeResult(true, new[] { c0, c1 }, 13, 4, "13조합 × 4폴드");

            IForgeReport r = fr;   // 업캐스트
            Assert.True(r.IsValid);
            Assert.Equal(13, r.Tested);
            Assert.Equal(4, r.Folds);
            Assert.Equal("13조합 × 4폴드", r.Note);
            Assert.Equal(2, r.Ranked.Count);

            IForgeCandidate ic = r.Ranked[0];   // 공변 IReadOnlyList → IForgeCandidate
            Assert.Equal(5, ic.Short);
            Assert.Equal(20, ic.Long);
            Assert.Equal(3.5, ic.OosMeanPct);
            Assert.Equal(1.2, ic.OosStdPct);
            Assert.Equal(2.9, ic.Score);
            Assert.Equal(14, ic.Trades);
            Assert.Equal(4.0, ic.FullReturnPct);
        }

        [Fact]
        public void ForgePick_Struct_Holds_Params()
        {
            var p = new ForgePick(10, 60);
            Assert.Equal(10, p.Short);
            Assert.Equal(60, p.Long);
        }
    }
}
