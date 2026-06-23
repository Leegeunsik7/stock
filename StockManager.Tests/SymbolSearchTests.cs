using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // 라이브 종목 검색 — 네이버 자동완성 JSON 파싱(순수·결정론). 네트워크 왕복은 CI 미검증(사용자 측), 파싱은 단위 검증.
    public sealed class SymbolSearchTests
    {
        // 실제 m.stock.naver.com/front-api/search/autoComplete 응답 형태(캡처). KOSPI·KOSDAQ·NASDAQ·NYSE +
        //   비-stock(coin)·미상 시장(지수) 행을 섞어 필터링까지 검증.
        private const string Sample = @"{""isSuccess"":true,""result"":{""query"":""x"",""items"":[
            {""code"":""005930"",""name"":""삼성전자"",""typeCode"":""KOSPI"",""nationCode"":""KOR"",""category"":""stock""},
            {""code"":""086520"",""name"":""에코프로"",""typeCode"":""KOSDAQ"",""nationCode"":""KOR"",""category"":""stock""},
            {""code"":""AAPL"",""name"":""애플"",""typeCode"":""NASDAQ"",""nationCode"":""USA"",""category"":""stock""},
            {""code"":""BRK.A"",""name"":""버크셔"",""typeCode"":""NYSE"",""nationCode"":""USA"",""category"":""stock""},
            {""code"":""KRW-BTC"",""name"":""비트코인"",""typeCode"":""UPBIT"",""category"":""coin""},
            {""code"":""KOSPI"",""name"":""코스피지수"",""typeCode"":""KOSPI_INDEX"",""category"":""index""}
        ]}}";

        [Fact]
        public void Parse_MapsKospiKosdaqUs_FiltersNonStockAndUnknownMarket()
        {
            var r = NaverSymbolSearch.ParseAutoComplete(Sample);
            Assert.Equal(4, r.Count);   // 코인·지수 제외(category!=stock, 미상 typeCode)

            var ss = r.Single(e => e.Code == "005930");
            Assert.Equal("삼성전자", ss.Name); Assert.Equal(Market.KRX_KOSPI, ss.Market); Assert.Equal(Currency.KRW, ss.Currency);

            Assert.Equal(Market.KRX_KOSDAQ, r.Single(e => e.Code == "086520").Market);

            var ap = r.Single(e => e.Code == "AAPL");
            Assert.Equal("애플", ap.Name); Assert.Equal(Market.US_NASDAQ, ap.Market); Assert.Equal(Currency.USD, ap.Currency);

            Assert.Equal(Market.US_NYSE, r.Single(e => e.Code == "BRK.A").Market);
            Assert.DoesNotContain(r, e => e.Code == "KRW-BTC");   // 코인은 Upbit 담당 → 제외
            Assert.DoesNotContain(r, e => e.IsCrypto);
        }

        [Fact]
        public void Parse_Malformed_ReturnsEmpty_NoThrow()
        {
            Assert.Empty(NaverSymbolSearch.ParseAutoComplete("not json"));
            Assert.Empty(NaverSymbolSearch.ParseAutoComplete("{}"));
            Assert.Empty(NaverSymbolSearch.ParseAutoComplete(@"{""result"":{}}"));
            Assert.Empty(NaverSymbolSearch.ParseAutoComplete(@"{""result"":{""items"":[]}}"));
        }
    }
}
