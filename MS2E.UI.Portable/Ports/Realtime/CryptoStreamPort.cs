// ============================================================================
// CryptoStreamPort — Phase 94-O: 코인 실시간 관제 Port (CoinPage RCL 이주 경계)
//   CoinPage 를 엔진 구상(DashboardService·BithumbProvider·UpbitProvider·BinanceProvider·FxConverter·
//   CryptoTick/BithumbTick/BinanceTick)에서 물리 격리. ★프리젠터 패턴: 어댑터가 4거래소+환율+보유를 병합해
//   포터블 스냅샷을 반환 → 페이지는 필터/정렬/김프·차익 표시만(콘크리트 0).
//   ★liveArmed:false: QuickPaper 는 '모의(paper)' 주문일 뿐 실 발주 아님. 조회 실패 시 직전 값 유지(fail-closed).
// ============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Realtime
{
    /// <summary>병합된 단일 코인 시세 행(업비트·빗썸·바이낸스 교차).</summary>
    public interface ICoinQuoteView
    {
        string Symbol { get; }      // "BTC"
        string Name { get; }
        decimal UpbitPx { get; }    // KRW (0=미표시)
        decimal BithumbPx { get; }  // KRW
        decimal BnUsdt { get; }     // USDT (김프 교차계산용)
        decimal Change { get; }     // 24h 등락률(%, 부호 포함)
        decimal TradeVal { get; }   // 24h 거래대금(KRW)
    }

    /// <summary>코인 보드 스냅샷 — 어댑터가 4거래소+환율+보유를 병합해 산출.</summary>
    public interface ICryptoBoardSnapshot
    {
        IReadOnlyList<ICoinQuoteView> Quotes { get; }
        IReadOnlyDictionary<string, decimal> Held { get; }   // "KRW-BTC" → 보유수량
        decimal UsdKrw { get; }
        int UpbitCount { get; }
        int BithumbCount { get; }
        int BinanceCount { get; }
        int AllSymbols { get; }
    }

    /// <summary>코인 실시간 스트림 Port — 보드 로드(병합) + 모의 주문. 도메인 구상 미유입.</summary>
    public interface ICryptoStreamPort
    {
        /// <summary>업비트·빗썸·바이낸스·환율·보유를 병합한 보드 스냅샷 로드(실패 소스는 직전 값 유지·fail-closed).</summary>
        Task<ICryptoBoardSnapshot> LoadBoardAsync(CancellationToken ct);

        /// <summary>모의(paper) 주문 — 실 발주 아님(liveArmed:false). market 예 "KRW-BTC".</summary>
        (bool ok, string message) QuickPaper(string market, decimal krw, bool isBuy);
    }
}
