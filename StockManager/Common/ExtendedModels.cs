// ============================================================================
// StockManager.Common - 확장 모델
// 배당 캘린더, 섹터 분류, 알림, 통합 계좌, 세금 시나리오
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    /// <summary>섹터 분류 (GICS 11섹터 + 한국 보완)</summary>
    public enum Sector
    {
        InformationTechnology,    // IT
        Financials,               // 금융
        HealthCare,               // 헬스케어
        ConsumerDiscretionary,    // 경기소비재
        ConsumerStaples,          // 필수소비재
        Industrials,              // 산업재
        Energy,                   // 에너지
        Materials,                // 소재
        Utilities,                // 유틸리티
        CommunicationServices,    // 통신
        RealEstate,               // 부동산
        // 한국 특화
        Semiconductor,            // 반도체
        SecondaryBattery,         // 2차전지
        Biopharma,                // 바이오
        Unknown
    }

    /// <summary>배당 이벤트</summary>
    public class DividendEvent
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime ExDate { get; set; }           // 배당락일
        public DateTime RecordDate { get; set; }       // 기준일
        public DateTime PayDate { get; set; }          // 지급일
        public decimal DividendPerShare { get; set; }  // 주당 배당금
        public Currency Currency { get; set; }
        public int OwnedQuantity { get; set; }         // 보유 수량
        public decimal ExpectedAmount => DividendPerShare * OwnedQuantity;
        public decimal YieldPct { get; set; }          // 배당수익률
    }

    /// <summary>통합 알림 항목</summary>
    public class AlertItem
    {
        public DateTime Time { get; set; } = DateTime.Now;
        public AlertSource Source { get; set; }
        public AlertLevel Level { get; set; } = AlertLevel.Info;
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public string Symbol { get; set; } = "";
        public bool IsRead { get; set; }
        public string ActionUrl { get; set; } = "";
    }

    public enum AlertSource { Price, News, Signal, Dividend, System }
    public enum AlertLevel { Info, Warning, Critical }

    /// <summary>세금 시나리오</summary>
    public class TaxScenario
    {
        public string Symbol { get; set; } = "";
        public int Quantity { get; set; }
        public decimal CurrentPrice { get; set; }
        public decimal AvgCost { get; set; }
        public decimal UnrealizedGain { get; set; }
        public decimal TaxIfSoldNow { get; set; }
        public decimal NetProceeds { get; set; }
        public string Recommendation { get; set; } = "";
    }

    /// <summary>52주 신고가/신저가 스캔 결과</summary>
    public class ExtremeEvent
    {
        public string Symbol { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsHigh { get; set; }      // true=신고가, false=신저가
        public decimal CurrentPrice { get; set; }
        public decimal Extreme52 { get; set; }
        public decimal ChangePct { get; set; }
        public DateTime DetectedAt { get; set; } = DateTime.Now;
    }

    /// <summary>상관관계 페어</summary>
    public class CorrelationPair
    {
        public string SymbolA { get; set; } = "";
        public string SymbolB { get; set; } = "";
        public decimal Coefficient { get; set; }  // -1 ~ +1
    }

    /// <summary>섹터별 RS(상대강도)</summary>
    public class SectorStrength
    {
        public Sector Sector { get; set; }
        public string Name { get; set; } = "";
        public decimal ReturnPct { get; set; }     // 구간 수익률
        public decimal RelativeStrength { get; set; } // vs 시장 기준
        public int MemberCount { get; set; }
    }

    /// <summary>주간 리포트 데이터</summary>
    public class WeeklyReport
    {
        public DateTime WeekStart { get; set; }
        public DateTime WeekEnd { get; set; }
        public decimal StartingValue { get; set; }
        public decimal EndingValue { get; set; }
        public decimal WeeklyReturn { get; set; }
        public decimal WeeklyReturnPct { get; set; }
        public List<Holding> TopGainers { get; set; } = new();
        public List<Holding> TopLosers { get; set; } = new();
        public List<Transaction> Transactions { get; set; } = new();
        public List<TradeSignal> StrongSignals { get; set; } = new();
        public List<NewsArticle> KeyNews { get; set; } = new();
        public decimal TotalCommission { get; set; }
        public decimal TotalTax { get; set; }
    }

    /// <summary>증권사 계좌 메타 (다계좌 통합용)</summary>
    public class BrokerAccount
    {
        public int Id { get; set; }
        public string Broker { get; set; } = "";       // 키움/한투/삼성/토스
        public string AccountNo { get; set; } = "";    // 계좌번호
        public string DisplayName { get; set; } = ""; // 별명
        public bool IsActive { get; set; } = true;
        public DateTime LastSyncAt { get; set; }
    }

    /// <summary>섹터 매핑 테이블</summary>
    public static class SectorMap
    {
        public static readonly Dictionary<string, (Sector sector, string name)> Map = new()
        {
            // 한국 대표주
            ["005930"] = (Sector.Semiconductor, "삼성전자"),
            ["000660"] = (Sector.Semiconductor, "SK하이닉스"),
            ["035420"] = (Sector.InformationTechnology, "NAVER"),
            ["035720"] = (Sector.InformationTechnology, "카카오"),
            ["207940"] = (Sector.Biopharma, "삼성바이오로직스"),
            ["068270"] = (Sector.Biopharma, "셀트리온"),
            ["373220"] = (Sector.SecondaryBattery, "LG에너지솔루션"),
            ["006400"] = (Sector.SecondaryBattery, "삼성SDI"),
            ["051910"] = (Sector.Materials, "LG화학"),
            ["005380"] = (Sector.ConsumerDiscretionary, "현대차"),
            ["000270"] = (Sector.ConsumerDiscretionary, "기아"),
            ["055550"] = (Sector.Financials, "신한지주"),
            ["105560"] = (Sector.Financials, "KB금융"),
            ["015760"] = (Sector.Utilities, "한국전력"),
            ["096770"] = (Sector.Energy, "SK이노베이션"),

            // 미국 대표주
            ["AAPL"] = (Sector.InformationTechnology, "Apple"),
            ["MSFT"] = (Sector.InformationTechnology, "Microsoft"),
            ["NVDA"] = (Sector.Semiconductor, "NVIDIA"),
            ["AMD"]  = (Sector.Semiconductor, "AMD"),
            ["INTC"] = (Sector.Semiconductor, "Intel"),
            ["TSM"]  = (Sector.Semiconductor, "TSMC"),
            ["GOOGL"] = (Sector.CommunicationServices, "Alphabet"),
            ["META"] = (Sector.CommunicationServices, "Meta"),
            ["TSLA"] = (Sector.ConsumerDiscretionary, "Tesla"),
            ["AMZN"] = (Sector.ConsumerDiscretionary, "Amazon"),
            ["JPM"]  = (Sector.Financials, "JPMorgan"),
            ["JNJ"]  = (Sector.HealthCare, "Johnson & Johnson"),
            ["XOM"]  = (Sector.Energy, "Exxon"),
            ["WMT"]  = (Sector.ConsumerStaples, "Walmart"),
            ["PG"]   = (Sector.ConsumerStaples, "Procter & Gamble"),
        };

        public static Sector Resolve(string symbol) =>
            Map.TryGetValue(symbol, out var v) ? v.sector : Sector.Unknown;

        public static string NameOf(string symbol) =>
            Map.TryGetValue(symbol, out var v) ? v.name : symbol;

        public static string Korean(Sector s) => s switch
        {
            Sector.InformationTechnology => "정보기술",
            Sector.Financials => "금융",
            Sector.HealthCare => "헬스케어",
            Sector.ConsumerDiscretionary => "경기소비재",
            Sector.ConsumerStaples => "필수소비재",
            Sector.Industrials => "산업재",
            Sector.Energy => "에너지",
            Sector.Materials => "소재",
            Sector.Utilities => "유틸리티",
            Sector.CommunicationServices => "통신",
            Sector.RealEstate => "부동산",
            Sector.Semiconductor => "반도체",
            Sector.SecondaryBattery => "2차전지",
            Sector.Biopharma => "바이오",
            _ => "기타"
        };
    }

    /// <summary>공통 샘플 데이터 (4개 프로그램 중복 제거)</summary>
    public static class SampleData
    {
        public static readonly (string sym, string name, Market mkt)[] KoreanTop = new[]
        {
            ("005930", "삼성전자", Market.KRX_KOSPI),
            ("000660", "SK하이닉스", Market.KRX_KOSPI),
            ("035420", "NAVER", Market.KRX_KOSPI),
            ("035720", "카카오", Market.KRX_KOSPI),
            ("207940", "삼성바이오로직스", Market.KRX_KOSPI),
            ("068270", "셀트리온", Market.KRX_KOSPI),
            ("373220", "LG에너지솔루션", Market.KRX_KOSPI),
            ("006400", "삼성SDI", Market.KRX_KOSPI),
            ("005380", "현대차", Market.KRX_KOSPI),
            ("000270", "기아", Market.KRX_KOSPI),
        };

        public static readonly (string sym, string name, Market mkt)[] USTop = new[]
        {
            ("AAPL", "Apple Inc.", Market.US_NASDAQ),
            ("MSFT", "Microsoft", Market.US_NASDAQ),
            ("NVDA", "NVIDIA", Market.US_NASDAQ),
            ("GOOGL", "Alphabet", Market.US_NASDAQ),
            ("META", "Meta Platforms", Market.US_NASDAQ),
            ("TSLA", "Tesla", Market.US_NASDAQ),
            ("AMZN", "Amazon", Market.US_NASDAQ),
            ("AMD", "AMD", Market.US_NASDAQ),
            ("INTC", "Intel", Market.US_NASDAQ),
            ("JPM", "JPMorgan Chase", Market.US_NYSE),
        };

        public static IEnumerable<(string, string, Market)> All()
        {
            foreach (var s in KoreanTop) yield return s;
            foreach (var s in USTop) yield return s;
        }
    }

    /// <summary>거래 비용 모델 (수수료·세금·슬리피지)</summary>
    public class TradingCost
    {
        public decimal CommissionRateKR { get; set; } = 0.00015m;    // 0.015%
        public decimal CommissionFlatUS { get; set; } = 0.99m;       // $0.99/거래
        public decimal TransactionTaxKOSPI { get; set; } = 0.0018m;  // 0.18%
        public decimal TransactionTaxKOSDAQ { get; set; } = 0.0023m; // 0.23%
        public decimal SlippageRate { get; set; } = 0.001m;          // 0.1%

        public decimal Commission(Market market, decimal amount) => market switch
        {
            Market.US_NASDAQ or Market.US_NYSE or Market.US_AMEX => CommissionFlatUS,
            _ => amount * CommissionRateKR
        };

        public decimal SellTax(Market market, decimal amount) => market switch
        {
            Market.KRX_KOSPI => amount * TransactionTaxKOSPI,
            Market.KRX_KOSDAQ => amount * TransactionTaxKOSDAQ,
            _ => 0m
        };

        public decimal Slippage(decimal amount) => amount * SlippageRate;

        public static readonly TradingCost Default = new();
    }
}
