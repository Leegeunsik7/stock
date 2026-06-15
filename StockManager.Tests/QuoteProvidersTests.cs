// ============================================================================
// QuoteProvidersTests - 네이버 siseJson 파서 (순수). 실제 응답이 값 사이 공백을
//  포함(", ")하여 기존 정규식이 0건 매치 → 차트 공백 버그가 있었음. 그 회귀 방지.
// ============================================================================
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class QuoteProvidersTests
    {
        // 네이버 실제 응답 형태: 헤더는 작은따옴표, 데이터 행은 큰따옴표 + 콤마 뒤 공백
        private const string Sample =
            "[['날짜', '시가', '고가', '저가', '종가', '거래량', '외국인소진율'],\n" +
            "[\"20240102\", 78200, 79800, 78200, 79600, 17142847, 54.05],\n" +
            "[\"20240103\", 78500, 78800, 77000, 77000, 21753644, 54.04]]";

        [Fact]
        public void ParseSiseJson_HandlesSpacedRows_AndSkipsHeader()
        {
            var cs = NaverFinanceProvider.ParseSiseJson(Sample);
            Assert.Equal(2, cs.Count);                 // 헤더 행 제외, 데이터 2건
            Assert.Equal(78200m, cs[0].Open);
            Assert.Equal(79800m, cs[0].High);
            Assert.Equal(78200m, cs[0].Low);
            Assert.Equal(79600m, cs[0].Close);
            Assert.Equal(17142847, cs[0].Volume);
            Assert.Equal(new System.DateTime(2024, 1, 2), cs[0].Date);
            Assert.Equal(77000m, cs[1].Close);
        }

        [Fact]
        public void ParseSiseJson_AlsoHandlesNoSpaceFormat()
        {
            var compact = "[[\"날짜\"],[\"20240102\",78200,79800,78200,79600,17142847,54.05]]";
            var cs = NaverFinanceProvider.ParseSiseJson(compact);
            Assert.Single(cs);
            Assert.Equal(79600m, cs[0].Close);
        }

        [Fact]
        public void ParseSiseJson_EmptyOrGarbage_ReturnsEmpty()
        {
            Assert.Empty(NaverFinanceProvider.ParseSiseJson(""));
            Assert.Empty(NaverFinanceProvider.ParseSiseJson("not json at all"));
        }
    }
}
