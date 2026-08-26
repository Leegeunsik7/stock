// ============================================================================
// SymbolContextTests - 전역 활성 종목 상태 (Set 가드·OnChange) 테스트 (순수)
// ============================================================================
using Xunit;

namespace StockManager.Tests
{
    public class SymbolContextTests
    {
        [Fact]
        public void Set_UpdatesState_AndFiresOnChange()
        {
            var c = new SymbolContext();
            int fired = 0; c.OnChange += () => fired++;

            c.Set("005930", Market.KRX_KOSPI, "삼성전자");
            Assert.True(c.HasSymbol);
            Assert.Equal("005930", c.Symbol);
            Assert.Equal("삼성전자", c.Name);
            Assert.Equal(Market.KRX_KOSPI, c.Market);
            Assert.Equal(1, fired);
        }

        [Fact]
        public void Set_SameValue_DoesNotFire()   // 재진입·무한루프 방지(write-back ↔ 구독)
        {
            var c = new SymbolContext();
            int fired = 0; c.OnChange += () => fired++;
            c.Set("AAPL", Market.US_NASDAQ, "Apple");
            c.Set("AAPL", Market.US_NASDAQ, "Apple");   // 동일 → 무시
            Assert.Equal(1, fired);
        }

        [Fact]
        public void Set_ChangingAnyField_Fires()
        {
            var c = new SymbolContext();
            int fired = 0; c.OnChange += () => fired++;
            c.Set("AAPL", Market.US_NASDAQ);
            c.Set("AAPL", Market.US_NYSE);     // 시장만 변경 → 발생
            Assert.Equal(2, fired);
        }

        [Fact]
        public void Set_TrimsAndTakesFirstToken()
        {
            var c = new SymbolContext();
            c.Set("  005930 삼성전자 ", Market.KRX_KOSPI);
            Assert.Equal("005930", c.Symbol);   // 공백 트림 + 첫 토큰
        }

        [Fact]
        public void Empty_NoSymbol()
        {
            var c = new SymbolContext();
            Assert.False(c.HasSymbol);
            Assert.Equal("", c.Symbol);
        }
    }
}
