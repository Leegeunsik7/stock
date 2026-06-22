using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // 종목·코인 검색 카탈로그 — 이름→코드 자동 채움의 데이터 소스. 순수·무외부의존이라 결정론 단위 검증.
    public sealed class SymbolCatalogTests
    {
        [Fact]
        public void Search_ByKoreanName_ResolvesCode()
        {
            var top = SymbolCatalog.Search("삼성전자", 5).First();
            Assert.Equal("005930", top.Code);
            Assert.Equal(Market.KRX_KOSPI, top.Market);
            Assert.Equal(Currency.KRW, top.Currency);
        }

        [Fact]
        public void Search_ByExactCode_ResolvesName()
        {
            var top = SymbolCatalog.Search("000660", 5).First();
            Assert.Equal("SK하이닉스", top.Name);
        }

        [Fact]
        public void Search_Crypto_ByKoreanName_ReturnsUpbitMarketCode()
        {
            var res = SymbolCatalog.Search("비트", 9);
            var btc = res.First(e => e.IsCrypto);
            Assert.Equal("KRW-BTC", btc.Code);
            Assert.Equal("비트코인", btc.Name);
            Assert.Equal(Market.CRYPTO, btc.Market);
        }

        [Fact]
        public void Search_UsTicker_CaseInsensitive_CarriesUsd()
        {
            var top = SymbolCatalog.Search("aapl", 5).First();
            Assert.Equal("AAPL", top.Code);
            Assert.Equal(Currency.USD, top.Currency);
            Assert.Equal(Market.US_NASDAQ, top.Market);
        }

        [Fact]
        public void Search_NoMatch_ReturnsEmpty()
            => Assert.Empty(SymbolCatalog.Search("존재하지않는종목ZZZ", 9));

        [Fact]
        public void Search_EmptyQuery_ReturnsBoundedNonEmpty()
        {
            var res = SymbolCatalog.Search("", 8);
            Assert.NotEmpty(res);
            Assert.True(res.Count <= 8);
        }

        [Fact]
        public void Search_RespectsLimit()
            => Assert.True(SymbolCatalog.Search("", 3).Count <= 3);

        [Fact]
        public void Search_LiveCryptoOverride_UsedInsteadOfFallback()
        {
            // 업비트 라이브 마켓 주입 시 내장 폴백 대신 그걸 검색(전 종목 커버).
            var live = new List<SymbolEntry> { new("KRW-PEPE", "페페", Market.CRYPTO, Currency.KRW, true) };
            var res = SymbolCatalog.Search("페페", 9, live);
            Assert.Contains(res, e => e.Code == "KRW-PEPE");
        }

        [Fact]
        public void Search_NamePrefix_OutranksContains()
        {
            // "카카오"는 카카오/카카오뱅크/카카오게임즈 — 접두 일치가 상위.
            var res = SymbolCatalog.Search("카카오", 5);
            Assert.Equal("카카오", res.First().Name);
        }
    }
}
