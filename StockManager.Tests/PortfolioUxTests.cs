// ============================================================================
// PortfolioUxTests — v4.89.0: 보유·배분 UX (다축 정렬 + 시간외/애프터 시세 파싱)
//
//  §1 정렬(HoldingSort)      — 평가금·총수익률·일간·가나다·직접설정 / 높은순·낮은순.
//  §2 Naver 파싱             — 하락일 전일종가 부호 교정 + 시간외 단일가(overMarketPriceInfo) 채택.
//  §3 Yahoo 파싱             — includePrePost 프리/애프터 채택 + 시간외 부재 시 정규장 폴백.
// ============================================================================
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class PortfolioUxTests
    {
        private static Holding H(string sym, string name, decimal qty = 1, decimal avg = 100, decimal cur = 100,
                                 Currency c = Currency.KRW, Market m = Market.KRX_KOSPI)
            => new() { Symbol = sym, Name = name, Quantity = qty, AvgPrice = avg, CurrentPrice = cur, Currency = c, Market = m };

        // ── §1 정렬 ──────────────────────────────────────────────────────────

        [Fact]
        public void Sort_ByValue_DescAsc()
        {
            var hs = new[] { H("A", "에이", 1, 100, 100), H("B", "비", 10, 100, 100), H("C", "씨", 5, 100, 100) };
            var desc = HoldingSort.Sort(hs, "val", true).Select(h => h.Symbol).ToArray();
            Assert.Equal(new[] { "B", "C", "A" }, desc);   // 평가금 1000 > 500 > 100
            var asc = HoldingSort.Sort(hs, "val", false).Select(h => h.Symbol).ToArray();
            Assert.Equal(new[] { "A", "C", "B" }, asc);
        }

        [Fact]
        public void Sort_ByValue_UsdConvertedByFx()
        {
            var kr = H("KR", "국내", 10, 1000, 1000);                                   // 10,000 KRW
            var us = H("US", "해외", 1, 5, 8, Currency.USD, Market.US_NASDAQ);           // 8 USD × 1380 = 11,040 KRW
            var desc = HoldingSort.Sort(new[] { kr, us }, "val", true, usdKrw: 1380m).Select(h => h.Symbol).ToArray();
            Assert.Equal(new[] { "US", "KR" }, desc);   // 환율 환산 후 US 가 더 큼
        }

        [Fact]
        public void Sort_ByReturn_Desc()
        {
            var win = H("W", "이익", 1, 100, 150);   // +50%
            var los = H("L", "손실", 1, 100, 80);    // -20%
            var desc = HoldingSort.Sort(new[] { los, win }, "ret", true).Select(h => h.Symbol).ToArray();
            Assert.Equal(new[] { "W", "L" }, desc);
        }

        [Fact]
        public void Sort_ByDailyChange_Desc()
        {
            var hs = new[] { H("A", "에이", 1, 100, 100), H("B", "비", 1, 100, 100) };
            var day = new Dictionary<string, decimal> { ["A"] = -1.5m, ["B"] = 3.2m };
            var desc = HoldingSort.Sort(hs, "day", true, dayPct: day).Select(h => h.Symbol).ToArray();
            Assert.Equal(new[] { "B", "A" }, desc);
        }

        [Fact]
        public void Sort_ByName_Hangul_Ascending()
        {
            var hs = new[] { H("1", "다"), H("2", "가"), H("3", "나") };
            var asc = HoldingSort.Sort(hs, "name", false).Select(h => h.Name).ToArray();
            Assert.Equal(new[] { "가", "나", "다" }, asc);   // 가나다순
            var desc = HoldingSort.Sort(hs, "name", true).Select(h => h.Name).ToArray();
            Assert.Equal(new[] { "다", "나", "가" }, desc);
        }

        [Fact]
        public void Sort_Custom_OrderThenUnlistedLast()
        {
            var hs = new[] { H("A", "에이"), H("B", "비"), H("C", "씨") };
            var order = new List<string> { "C", "A" };   // B 미지정 → 뒤로
            var got = HoldingSort.Sort(hs, "custom", true, customOrder: order).Select(h => h.Symbol).ToArray();
            Assert.Equal(new[] { "C", "A", "B" }, got);
        }

        [Fact]
        public void MoveInView_FilteredView_SwapsVisibleNeighbors_HiddenUntouched()
        {
            // 전역 순서에 숨은 항목이 끼어 있어도 '화면 순서' 기준으로 정확히 한 칸 이동(먹통 클릭 회귀 방지).
            var order = new List<string> { "StockA", "CoinX", "StockB", "CoinY" };
            var view = new[] { "CoinX", "CoinY" };   // 코인 필터 화면
            var moved = HoldingSort.MoveInView(order, view, "CoinY", -1);   // CoinY 위로
            Assert.True(moved);
            // 코인 투영이 [CoinY, CoinX]로 뒤집힘 + 숨은 StockA/StockB 상대순서 보존.
            var coinProjection = order.Where(s => s.StartsWith("Coin")).ToArray();
            Assert.Equal(new[] { "CoinY", "CoinX" }, coinProjection);
            var stockProjection = order.Where(s => s.StartsWith("Stock")).ToArray();
            Assert.Equal(new[] { "StockA", "StockB" }, stockProjection);   // 숨은 항목 상대순서 불변
        }

        [Fact]
        public void MoveInView_AtBoundary_NoOp()
        {
            var order = new List<string> { "A", "B", "C" };
            Assert.False(HoldingSort.MoveInView(order, new[] { "A", "B", "C" }, "A", -1));   // 맨 위 위로 → no-op
            Assert.False(HoldingSort.MoveInView(order, new[] { "A", "B", "C" }, "C", 1));     // 맨 아래 아래로 → no-op
            Assert.Equal(new[] { "A", "B", "C" }, order.ToArray());
        }

        // ── §2 Naver 파싱 (하락 부호 + 시간외) ────────────────────────────────

        private static JsonElement Datas(string inner)
            => JsonDocument.Parse("{\"datas\":[" + inner + "]}").RootElement.GetProperty("datas")[0];

        [Fact]
        public void Naver_DownDay_PreviousCloseSignCorrect()
        {
            // 하락: 종가 70,000, 변동액 1,000, 방향 FALLING → 전일종가 71,000, 등락 음수.
            var data = Datas("{\"stockName\":\"테스트\",\"closePrice\":\"70,000\",\"compareToPreviousClosePrice\":\"1,000\"," +
                             "\"compareToPreviousPrice\":{\"code\":\"5\",\"name\":\"FALLING\"}}");
            var q = NaverFinanceProvider.ParseRealtime("005930", Market.KRX_KOSPI, data);
            Assert.Equal(70000m, q.Price);
            Assert.Equal(71000m, q.PreviousClose);
            Assert.True(q.ChangeRate < 0);
            Assert.Equal("", q.Session);
        }

        [Fact]
        public void Naver_AfterMarket_UsesOverPrice()
        {
            // 정규장 종가 278,000(+500 상승) → 전일종가 277,500. 시간외 OPEN·overPrice 282,000 → 현재가 교체·Session=AFTER.
            var data = Datas("{\"stockName\":\"삼성전자\",\"closePrice\":\"278,000\",\"compareToPreviousClosePrice\":\"500\"," +
                             "\"compareToPreviousPrice\":{\"code\":\"2\",\"name\":\"RISING\"}," +
                             "\"overMarketPriceInfo\":{\"overMarketStatus\":\"OPEN\",\"overPrice\":\"282,000\"}}");
            var q = NaverFinanceProvider.ParseRealtime("005930", Market.KRX_KOSPI, data);
            Assert.Equal(282000m, q.Price);           // 시간외가로 교체
            Assert.Equal(277500m, q.PreviousClose);   // 전일종가는 정규장값 유지
            Assert.Equal("AFTER", q.Session);
            Assert.True(q.ChangeRate > 0);            // 277,500 → 282,000 상승
        }

        [Fact]
        public void Naver_AfterMarket_Closed_KeepsRegular()
        {
            // 시간외 CLOSE 이면 정규장 종가 유지(시간외가 미채택).
            var data = Datas("{\"stockName\":\"삼성전자\",\"closePrice\":\"278,000\",\"compareToPreviousClosePrice\":\"500\"," +
                             "\"compareToPreviousPrice\":{\"code\":\"2\",\"name\":\"RISING\"}," +
                             "\"overMarketPriceInfo\":{\"overMarketStatus\":\"CLOSE\",\"overPrice\":\"282,000\"}}");
            var q = NaverFinanceProvider.ParseRealtime("005930", Market.KRX_KOSPI, data);
            Assert.Equal(278000m, q.Price);
            Assert.Equal("", q.Session);
        }

        // ── §3 Yahoo 파싱 (프리/애프터 + 폴백) ────────────────────────────────

        private static JsonElement Meta(string inner)
            => JsonDocument.Parse("{" + inner + "}").RootElement;

        [Fact]
        public void Yahoo_Regular_NoExtended()
        {
            var m = Meta("\"regularMarketPrice\":313.39,\"previousClose\":310.66");
            var q = YahooFinanceProvider.ParseChartMeta("AAPL", "AAPL", Market.US_NASDAQ, m);
            Assert.Equal(313.39m, q.Price);
            Assert.Equal("", q.Session);
        }

        [Fact]
        public void Yahoo_PostMarket_UsesPost()
        {
            var m = Meta("\"regularMarketPrice\":313.39,\"previousClose\":310.66,\"marketState\":\"POST\",\"postMarketPrice\":315.50");
            var q = YahooFinanceProvider.ParseChartMeta("AAPL", "AAPL", Market.US_NASDAQ, m);
            Assert.Equal(315.50m, q.Price);
            Assert.Equal("POST", q.Session);
        }

        [Fact]
        public void Yahoo_PreMarket_UsesPre()
        {
            var m = Meta("\"regularMarketPrice\":313.39,\"previousClose\":310.66,\"marketState\":\"PRE\",\"preMarketPrice\":308.00");
            var q = YahooFinanceProvider.ParseChartMeta("AAPL", "AAPL", Market.US_NASDAQ, m);
            Assert.Equal(308.00m, q.Price);
            Assert.Equal("PRE", q.Session);
        }

        [Fact]
        public void Yahoo_PostState_ButNoPostPrice_FallsBackRegular()
        {
            // marketState POST 여도 postMarketPrice 0/부재 → 정규장가 폴백(fail-safe), Session 없음.
            var m = Meta("\"regularMarketPrice\":313.39,\"previousClose\":310.66,\"marketState\":\"POST\"");
            var q = YahooFinanceProvider.ParseChartMeta("AAPL", "AAPL", Market.US_NASDAQ, m);
            Assert.Equal(313.39m, q.Price);
            Assert.Equal("", q.Session);
        }
    }
}
