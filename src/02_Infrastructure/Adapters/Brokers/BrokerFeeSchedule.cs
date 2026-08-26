using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    // ============================================================================
    //  증권사별 수수료·세금 추정 — 매수/매도 기록 시 자동 채움(사용자 수정 가능).
    //
    //  ⚠️ 정직 경계: 여기 수치는 '온라인 위탁수수료 + 법정 거래세'의 대표 추정치다.
    //     실제 체결 비용은 (1) 약정·이벤트·계좌유형별 수수료 차등, (2) 거래세율의 연도별
    //     변동, (3) 유관기관 제비용으로 달라진다. 따라서 폼은 이 값을 '초기 추정'으로 넣고
    //     사용자가 최종 수정한다. 환율은 여기서 다루지 않는다(시세/평가 단계의 FX 책임).
    //
    //  세율 근거(보수적 대표값):
    //   · KRX 증권거래세: 2025년 이후 코스피 0%(농특세 0.15%만)/코스닥 0.15% → 매도분 약 0.15% 단일 적용.
    //   · 미국주식: SEC fee + FINRA TAF 합산 ≈ 0.0023%(매도분, 소액) — 대표 추정.
    //   · 코인(업비트류): 체결 수수료 ≈ 0.05%(매수·매도 공통), 거래세 없음.
    //  수수료율은 주요 증권사 온라인 영웅문/MTS 대표값(≈0.014~0.015%).
    // ============================================================================
    public static class BrokerFeeSchedule
    {
        // 증권사명 → 위탁수수료율(거래대금 대비). 미등록 증권사는 시장별 기본값으로 폴백.
        private static readonly Dictionary<string, decimal> Commission = new(StringComparer.Ordinal)
        {
            ["키움증권"]    = 0.00015m,
            ["한국투자증권"] = 0.00014m,
            ["삼성증권"]    = 0.00014m,
            ["토스증권"]    = 0.00015m,
            ["NH투자증권"]  = 0.00014m,
            ["미래에셋"]    = 0.00014m,
        };

        private const decimal DefaultStockCommission = 0.00015m;   // 미등록 증권사(주식) 기본 ≈0.015%
        private const decimal CryptoFeeRate          = 0.0005m;    // 코인 체결수수료 ≈0.05%(매수·매도)
        private const decimal KrxSellTaxRate          = 0.0015m;    // KRX 증권거래세(2025+) ≈0.15%, 매도만
        private const decimal UsSellFeeRate           = 0.000023m;  // 미국 SEC+TAF 합산 ≈0.0023%, 매도만

        /// <summary>등록된(수수료율을 아는) 증권사인지.</summary>
        public static bool IsKnown(string? broker) =>
            !string.IsNullOrWhiteSpace(broker) && Commission.ContainsKey(broker);

        /// <summary>
        /// 수수료·세금 추정. 거래대금(qty×price)이 0 이하이면 (0,0).
        /// 세금은 '매도'에만 부과(KRX 거래세 / 미국 청산제비용). 코인은 거래세 없음.
        /// 반올림: KRW 시장은 원 단위, USD 시장은 센트(2자리) 단위(AwayFromZero).
        /// </summary>
        public static (decimal commission, decimal tax) Estimate(
            string? broker, Market market, TransactionType side, decimal qty, decimal price)
        {
            // 음수 부호쌍 차단: gross<=0 단독 가드는 (qty<0)×(price<0)=양수로 우회돼 허구의 명목금액에
            //   수수료/세금을 산출했다. 수량·단가 각각이 양수여야만 추정한다(아니면 (0,0)).
            if (qty <= 0m || price <= 0m) return (0m, 0m);
            decimal gross = qty * price;

            bool crypto = market == Market.CRYPTO;
            bool usd    = market is Market.US_NYSE or Market.US_NASDAQ or Market.US_AMEX;

            // 수수료율: 등록 증권사 우선, 없으면 코인/주식 기본값.
            decimal rate = (!crypto && !string.IsNullOrWhiteSpace(broker) && Commission.TryGetValue(broker, out var r))
                ? r
                : (crypto ? CryptoFeeRate : DefaultStockCommission);

            decimal commission = Round(gross * rate, usd);

            decimal tax = 0m;
            if (side == TransactionType.Sell)
            {
                bool krx = market is Market.KRX_KOSPI or Market.KRX_KOSDAQ;
                decimal taxRate = krx ? KrxSellTaxRate : usd ? UsSellFeeRate : 0m;   // 코인 거래세 0
                tax = Round(gross * taxRate, usd);
            }
            return (commission, tax);
        }

        private static decimal Round(decimal v, bool usd) =>
            decimal.Round(v, usd ? 2 : 0, MidpointRounding.AwayFromZero);
    }
}
