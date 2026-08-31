// ============================================================================
// StockManager.Common - 공통 데이터 모델
// 5개 프로그램이 공유하는 표준 모델 클래스
// ============================================================================
using System;
using System.Collections.Generic;
using System.ComponentModel;
using MS2E.UI.Portable.Ports.Portfolio;   // Phase 94-J — IHoldingView/IQuoteTickView 공변 구현

namespace MS2E.Infrastructure
{
    /// <summary>시장 구분</summary>
    public enum Market
    {
        KRX_KOSPI,      // 코스피
        KRX_KOSDAQ,     // 코스닥
        US_NYSE,        // 뉴욕
        US_NASDAQ,      // 나스닥
        US_AMEX,        // 아멕스
        CRYPTO          // 암호화폐 (확장용)
    }

    /// <summary>통화</summary>
    public enum Currency { KRW, USD }

    /// <summary>데이터 소스</summary>
    public enum DataSource
    {
        Naver,          // 네이버 금융 (무료, 한국)
        YahooFinance,   // Yahoo Finance (무료, 글로벌)
        Kiwoom,         // 키움 OpenAPI+
        KIS,            // 한국투자증권 KIS Developers
        Alpha,          // Alpha Vantage (백업용)
        Manual          // 수동 입력
    }

    /// <summary>실시간 시세 데이터 (Tick)</summary>
    public class Quote : INotifyPropertyChanged, IQuoteTickView   // Phase 94-J: Price/ChangeRate/Session 암시 구현
    {
        public string Symbol { get; set; } = "";      // AAPL, 005930 등
        public string Name { get; set; } = "";        // Apple Inc., 삼성전자
        public Market Market { get; set; }
        public Currency Currency { get; set; }

        private decimal _price;
        public decimal Price
        {
            get => _price;
            set { _price = value; OnChanged(nameof(Price)); OnChanged(nameof(ChangeRate)); }
        }

        public decimal PreviousClose { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public long Volume { get; set; }
        public decimal TradingValue { get; set; }    // 거래대금
        public decimal MarketCap { get; set; }        // 시가총액
        public DateTime Timestamp { get; set; }
        public DataSource Source { get; set; }

        // v4.89.0 — 시세 세션 구분(장 마감 후 시간외/애프터 지속 갱신 표시용). "" 정규장, "AFTER" KRX 시간외 단일가,
        //   "PRE" 미국 프리마켓, "POST" 미국 애프터마켓. Price 는 해당 세션의 최신가(시간외가 있으면 그 값), PreviousClose 는
        //   정규장 전일종가 유지 → ChangeRate 가 '전일종가 대비 시간외 포함 등락'을 나타낸다.
        public string Session { get; set; } = "";

        // 계산 속성
        public decimal Change => Price - PreviousClose;
        public decimal ChangeRate =>
            PreviousClose == 0 ? 0 : (Price - PreviousClose) / PreviousClose * 100m;

        // 호가 정보 (Level 2)
        public decimal[] BidPrices { get; set; } = new decimal[10];
        public long[] BidVolumes { get; set; } = new long[10];
        public decimal[] AskPrices { get; set; } = new decimal[10];
        public long[] AskVolumes { get; set; } = new long[10];

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>일봉 캔들 (OHLCV)</summary>
    public class Candle
    {
        public DateTime Date { get; set; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public long Volume { get; set; }
    }

    /// <summary>보유 종목 (포트폴리오 항목)</summary>
    public class Holding : IHoldingView
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public Market Market { get; set; }
        public Currency Currency { get; set; }

        // Phase 94-J: IHoldingView 공변 구현 — 엔진 enum→RCL enum(정수정렬). 나머지 멤버는 동일 시그니처로 암시 구현.
        PfMarket IHoldingView.Market => (PfMarket)(int)Market;
        PfCurrency IHoldingView.Currency => (PfCurrency)(int)Currency;

        public decimal Quantity { get; set; }         // 보유 수량 (코인 소수점 지원 — v4.30.0 int→decimal)
        public decimal AvgPrice { get; set; }         // 평균 매입가
        public decimal CurrentPrice { get; set; }     // 현재가 (실시간 업데이트)

        public DateTime FirstBuyDate { get; set; }
        public string BrokerAccount { get; set; } = ""; // 증권사 계좌
        public string Memo { get; set; } = "";
        public string[] Tags { get; set; } = Array.Empty<string>(); // 태그 (섹터, 전략 등)

        // 누적 수수료·세금 (v4.38.0). 직접 등록 종목은 수정 폼에서 직접 보정, 매수/매도로 기록한 종목은
        //   거래 이력 합계가 RecomputeAverage에서 자동 반영된다.
        public decimal Commission { get; set; }
        public decimal Tax { get; set; }

        // 계산 속성
        public decimal TotalCost => Quantity * AvgPrice;
        public decimal MarketValue => Quantity * CurrentPrice;
        public decimal ProfitLoss => MarketValue - TotalCost;
        public decimal ReturnRate =>
            TotalCost == 0 ? 0 : (MarketValue - TotalCost) / TotalCost * 100m;

        // 별칭 프로퍼티 (v2.2 네이밍 일관성)
        public decimal Invested => TotalCost;
        public decimal CurrentValue => MarketValue;
        public decimal UnrealizedPnL => ProfitLoss;
        public decimal ChangeRate => ReturnRate;
    }

    /// <summary>거래 내역 (Transaction)</summary>
    public class Transaction
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public TransactionType Type { get; set; }
        public decimal Quantity { get; set; }          // 코인 소수점 지원 — v4.30.0 int→decimal
        public decimal Price { get; set; }
        public decimal Commission { get; set; }       // 수수료
        public decimal Tax { get; set; }              // 세금 (양도세, 증권거래세)
        public DateTime ExecutedAt { get; set; }
        public string BrokerAccount { get; set; } = "";
        public string Memo { get; set; } = "";

        public decimal NetAmount => Type == TransactionType.Buy
            ? Quantity * Price + Commission + Tax
            : Quantity * Price - Commission - Tax;
    }

    public enum TransactionType { Buy, Sell, Dividend, Split, Deposit, Withdraw }

    /// <summary>매매 신호</summary>
    public class TradeSignal
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public SignalType Signal { get; set; }
        public SignalStrength Strength { get; set; }
        public string Strategy { get; set; } = "";   // "MACD Golden", "RSI Oversold" 등
        public string Reason { get; set; } = "";
        public decimal TargetPrice { get; set; }
        public decimal StopLoss { get; set; }
        public decimal Confidence { get; set; }       // 0~100
        public DateTime GeneratedAt { get; set; }
    }

    public enum SignalType { StrongBuy, Buy, Hold, Sell, StrongSell }
    public enum SignalStrength { Weak, Medium, Strong, VeryStrong }

    /// <summary>뉴스 기사</summary>
    public class NewsArticle
    {
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public string Url { get; set; } = "";
        public string Source { get; set; } = "";      // 매일경제, Reuters 등
        public DateTime PublishedAt { get; set; }
        public List<string> RelatedSymbols { get; set; } = new();
        public decimal SentimentScore { get; set; }  // -1 (부정) ~ +1 (긍정)
        public string Language { get; set; } = "ko";
    }

    /// <summary>알림 규칙</summary>
    public class AlertRule
    {
        public int Id { get; set; }
        public string Symbol { get; set; } = "";
        public AlertCondition Condition { get; set; }
        public decimal TargetValue { get; set; }
        public bool IsActive { get; set; } = true;
        public bool IsTriggered { get; set; }
        public DateTime CreatedAt { get; set; }
        public AlertChannel Channel { get; set; } = AlertChannel.Desktop;
    }

    public enum AlertCondition
    {
        PriceAbove, PriceBelow,
        ChangeRateAbove, ChangeRateBelow,
        VolumeAbove, RSIAbove, RSIBelow,
        MACDCross, GoldenCross, DeadCross
    }

    public enum AlertChannel { Desktop, Email, Telegram, KakaoTalk, All }
}
