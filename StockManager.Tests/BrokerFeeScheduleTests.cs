// ============================================================================
// BrokerFeeScheduleTests — 증권사별 수수료·세금 추정(순수·결정론).
//  고정 룰: 세금은 매도에만(KRX 거래세 / US 제비용) · 코인 거래세 0 · KRW 원단위/USD 센트단위 반올림 ·
//           gross<=0 → (0,0) · 미등록 증권사는 시장 기본 수수료율 폴백.
// ============================================================================
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class BrokerFeeScheduleTests
    {
        [Fact]
        public void KrxBuy_CommissionOnly_NoTax()
        {
            // 코스피 매수: 거래대금 1,000,000 × 0.015% = 150원, 매수라 거래세 0.
            var (c, t) = BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Buy, 10, 100_000m);
            Assert.Equal(150m, c);
            Assert.Equal(0m, t);
        }

        [Fact]
        public void KrxSell_AddsTransactionTax()
        {
            // 코스피 매도: 수수료 150 + 거래세 1,000,000 × 0.15% = 1,500.
            var (c, t) = BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Sell, 10, 100_000m);
            Assert.Equal(150m, c);
            Assert.Equal(1_500m, t);
        }

        [Fact]
        public void Crypto_NoTax_BothSides_UsesCryptoRate()
        {
            // 코인: 증권사 무관, 체결수수료 0.05%, 거래세 없음(매수·매도 모두 tax 0).
            var (cb, tb) = BrokerFeeSchedule.Estimate("키움증권", Market.CRYPTO, TransactionType.Buy,  0.1m, 100_000_000m);
            var (cs, ts) = BrokerFeeSchedule.Estimate("",        Market.CRYPTO, TransactionType.Sell, 0.1m, 100_000_000m);
            Assert.Equal(5_000m, cb);   // 10,000,000 × 0.0005
            Assert.Equal(0m, tb);
            Assert.Equal(5_000m, cs);
            Assert.Equal(0m, ts);       // 코인은 매도여도 거래세 0
        }

        [Fact]
        public void Us_RoundsToCents_SellHasSecFee()
        {
            // 미국 매도: 수수료/세금이 센트(2자리)로 떨어진다(원단위 아님). 거래대금 $1,000.
            //   (반올림 '모드'(AwayFromZero) 자체를 시험하진 않음 — 0.023은 미드포인트가 아니라 어떤 모드든 0.02)
            var (c, t) = BrokerFeeSchedule.Estimate("키움증권", Market.US_NASDAQ, TransactionType.Sell, 10, 100m);
            Assert.Equal(0.15m, c);     // 1000 × 0.00015
            Assert.Equal(0.02m, t);     // 1000 × 0.000023 = 0.023 → 0.02
        }

        [Fact]
        public void NegativeSignPair_DoesNotProduceFees()
        {
            // fail-open 회귀: 음수 수량×음수 단가 = 양수 gross 로 'gross<=0' 가드를 우회해 허구 수수료를 산출했다.
            //   이제 수량·단가 각각이 양수여야만 추정 → 모든 음수 조합은 (0,0).
            Assert.Equal((0m, 0m), BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Sell, -10, -100_000m));
            Assert.Equal((0m, 0m), BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Buy,  -10,  100_000m));
            Assert.Equal((0m, 0m), BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Buy,   10, -100_000m));
        }

        [Fact]
        public void ZeroGross_ReturnsZero()
        {
            Assert.Equal((0m, 0m), BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Buy, 0, 100_000m));
            Assert.Equal((0m, 0m), BrokerFeeSchedule.Estimate("키움증권", Market.KRX_KOSPI, TransactionType.Buy, 10, 0m));
        }

        [Fact]
        public void UnknownBroker_FallsBackToDefaultStockRate()
        {
            // 미등록 증권사도 주식 기본 수수료율(0.015%)로 추정 — 200,000 × 0.00015 = 30.
            var (c, _) = BrokerFeeSchedule.Estimate("처음보는증권", Market.KRX_KOSDAQ, TransactionType.Buy, 2, 100_000m);
            Assert.Equal(30m, c);
            Assert.False(BrokerFeeSchedule.IsKnown("처음보는증권"));
            Assert.True(BrokerFeeSchedule.IsKnown("키움증권"));
        }
    }
}
