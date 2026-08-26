using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    // ============================================================================
    //  자산군 분류 집계 — 보유를 국내주식 / 해외주식 / 코인 3분류로 묶어 KRW 환산 평가·손익·비중을 낸다.
    //   값 환산 규칙은 PortfolioPage.LoadAsync 의 총평가 산식과 동일: USD 종목만 ×환율, 원화·코인은 그대로.
    //   순수 함수(네트워크/DB 없음) — 단위 테스트로 합산·환산·비중을 고정한다.
    // ============================================================================
    public enum AssetClass { DomesticStock = 0, ForeignStock = 1, Crypto = 2 }

    public readonly record struct CategoryAgg(
        AssetClass Class, string Label, decimal ValueKrw, decimal CostKrw, int Count)
    {
        public decimal PnlKrw    => ValueKrw - CostKrw;
        public decimal ReturnPct => CostKrw > 0 ? PnlKrw / CostKrw * 100m : 0m;
        public decimal WeightPct(decimal totalValueKrw) => totalValueKrw > 0 ? ValueKrw / totalValueKrw * 100m : 0m;
    }

    public static class PortfolioBreakdown
    {
        public static AssetClass Classify(Market m) => m switch
        {
            Market.KRX_KOSPI or Market.KRX_KOSDAQ          => AssetClass.DomesticStock,
            Market.US_NYSE or Market.US_NASDAQ or Market.US_AMEX => AssetClass.ForeignStock,
            Market.CRYPTO                                   => AssetClass.Crypto,
            // enum은 정의 외 정수도 가질 수 있어 discard가 필수(컴파일). 새 '시장' 추가 시 위 분기에 명시할 것 —
            //   Classify_EveryMarketEnumValue_IsExplicitlyMapped 테스트가 누락을 잡아 fail-closed로 강제한다.
            _                                               => AssetClass.ForeignStock,
        };

        public static string LabelOf(AssetClass c) => c switch
        {
            AssetClass.DomesticStock => "국내주식",
            AssetClass.ForeignStock  => "해외주식",
            AssetClass.Crypto        => "비트코인·코인",
            _                        => "기타",
        };

        /// <summary>3분류 집계를 항상 같은 순서(국내·해외·코인)로 반환 — 비어 있어도 0 카드로 노출.</summary>
        public static List<CategoryAgg> Compute(IEnumerable<Holding> holdings, decimal usdKrw)
        {
            var val   = new decimal[3];
            var cost  = new decimal[3];
            var count = new int[3];

            foreach (var h in holdings)
            {
                if (h == null) continue;
                int i = (int)Classify(h.Market);
                // 환산은 '통화' 기준(총평가 _totalValue와 동일 규칙) — USD 통화만 ×환율. 코인·국내주식은 보통 KRW라
                //   그대로지만, 통화가 USD면(예외적 CRYPTO+USD) 환산되어 총평가와 일관성을 유지한다.
                decimal fx = h.Currency == Currency.USD ? usdKrw : 1m;
                val[i]  += h.CurrentPrice * h.Quantity * fx;
                cost[i] += h.AvgPrice     * h.Quantity * fx;
                count[i]++;
            }

            var result = new List<CategoryAgg>(3);
            for (int i = 0; i < 3; i++)
            {
                var cls = (AssetClass)i;
                result.Add(new CategoryAgg(cls, LabelOf(cls), val[i], cost[i], count[i]));
            }
            return result;
        }
    }
}
