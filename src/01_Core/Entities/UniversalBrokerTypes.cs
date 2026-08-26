// ============================================================================
// UniversalBrokerTypes — v10.2.0: 범용 다중 증권사 게이트웨이 Ports & DTO & 스키마
//
//   토스·KIS·LS 등 국내외 모든 증권사 Open API 를 '단일 규격'으로 수용하는 경계. 브로커 무관 순수 DTO(PortfolioSummary·
//   OrderBookTick)와 스키마 독립 매퍼용 BrokerSchema(각 증권사 JSON 필드명 맵)를 정의한다. ★DTO 는 전부 readonly record
//   struct(값타입·힙0), 금액=고정소수 raw, 종목=FNV 해시.
//
//   ★불변: liveArmed:false(조회 전용 — 발주 메서드 없음) · fail-closed(미동기화 → TryGet false).
// ============================================================================
using System;
using System.Text;

namespace StockManager.Common
{
    /// <summary>증권사 식별자.</summary>
    public enum BrokerId : byte { None = 0, Toss = 1, Kis = 2, Ls = 3 }

    /// <summary>브로커 무관 포트폴리오 요약(δ=0 값타입).</summary>
    public readonly record struct PortfolioSummary(
        BrokerId Broker, long TotalEvalRaw, long TotalPnlRaw, long DepositRaw, long BuyingPowerRaw, int HoldingCount, long AsOfMicros);

    /// <summary>브로커 무관 호가/체결 틱(δ=0 값타입). ExchangeMicros=거래소 타임스탬프, RecvMicros=수신 시각.</summary>
    public readonly record struct OrderBookTick(
        BrokerId Broker, long SymbolHash, long Seq, long PriceRaw, long Qty, long ExchangeMicros, long RecvMicros);

    /// <summary>실시간 시세 스트림 Port(브로커 무관). 조회 전용(발주 없음).</summary>
    public interface IMarketDataStreamPort
    {
        BrokerId Broker { get; }
        bool Connected { get; }
        long LastRttMicros { get; }
        long TicksIngested { get; }
        bool TryGetLatest(out OrderBookTick tick);   // δ=0 최신 틱(미수신 → false)
    }

    /// <summary>
    /// 스키마 독립 매퍼용 필드명 맵 — 증권사별 상이한 JSON 키를 사전 UTF-8 바이트로 보관(콜드 1회 생성 → 매핑은 δ=0).
    /// ★실 스펙에 맞춰 프리셋 조정 가능. 누락 필드는 빈 배열(매칭 안 됨 → 0).
    /// </summary>
    public sealed class BrokerSchema
    {
        public readonly BrokerId Broker;
        public readonly byte[] AccountObj, HoldingsArr, TotalEval, TotalPnl, Deposit, BuyingPower;
        public readonly byte[] Symbol, Qty, AvgPrice, CurPrice, Pnl, Overseas;

        public BrokerSchema(BrokerId broker, string accountObj, string holdingsArr, string totalEval, string totalPnl,
                            string deposit, string buyingPower, string symbol, string qty, string avgPrice,
                            string curPrice, string pnl, string overseas)
        {
            Broker = broker;
            AccountObj  = Encoding.UTF8.GetBytes(accountObj);
            HoldingsArr = Encoding.UTF8.GetBytes(holdingsArr);
            TotalEval   = Encoding.UTF8.GetBytes(totalEval);
            TotalPnl    = Encoding.UTF8.GetBytes(totalPnl);
            Deposit     = Encoding.UTF8.GetBytes(deposit);
            BuyingPower = Encoding.UTF8.GetBytes(buyingPower);
            Symbol      = Encoding.UTF8.GetBytes(symbol);
            Qty         = Encoding.UTF8.GetBytes(qty);
            AvgPrice    = Encoding.UTF8.GetBytes(avgPrice);
            CurPrice    = Encoding.UTF8.GetBytes(curPrice);
            Pnl         = Encoding.UTF8.GetBytes(pnl);
            Overseas    = Encoding.UTF8.GetBytes(overseas);
        }

        /// <summary>토스증권 스키마 프리셋(TossJsonParser 와 동일 키).</summary>
        public static BrokerSchema Toss() => new(BrokerId.Toss,
            "account", "holdings", "totalEval", "totalPnl", "deposit", "buyingPower",
            "symbol", "qty", "avgPrice", "curPrice", "pnl", "overseas");

        /// <summary>한국투자증권(KIS) 스키마 프리셋(실 스펙에 맞춰 조정 — 잔고조회 output 구조 근사).</summary>
        public static BrokerSchema Kis() => new(BrokerId.Kis,
            "output2", "output1", "tot_evlu_amt", "evlu_pfls_smtl_amt", "dnca_tot_amt", "nrcvb_buy_amt",
            "pdno", "hldg_qty", "pchs_avg_pric", "prpr", "evlu_pfls_amt", "ovrs_yn");
    }
}
