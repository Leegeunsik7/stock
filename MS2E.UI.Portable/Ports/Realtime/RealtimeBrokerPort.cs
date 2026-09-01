// ============================================================================
// RealtimeBrokerPort + WatchlistPort — Phase 94-O: 실시간 시세 관제 Port (RealtimePage RCL 이주 경계)
//   RealtimePage(886줄 白鯨)를 엔진 구상(IQuoteProvider·LocalDatabase·WatchlistStore·ExtremeScanner·
//   SampleData·AlertCenter·Quote/Candle/ExtremeEvent·Formatter)에서 물리 격리.
//   ★IRealtimeBrokerPort = 시세/이력/52주 스캔(어댑터가 스캔+알림 발생 내재화).
//   ★IWatchlistPort = 워치리스트 DB 영속(카테고리/종목/재정렬/시드) + 알림규칙 + 테마설정.
//   AsOf(ITimeTravelAsOfPort)·심볼검색(ISymbolSearchPort)은 기존 포트 재사용. ★liveArmed:false: 조회/표시 전용.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket 재사용

namespace MS2E.UI.Portable.Ports.Realtime
{
    /// <summary>실시간 시세 행 뷰(Quote 공변).</summary>
    public interface IRealtimeQuoteView
    {
        string Symbol { get; }
        string Name { get; }
        decimal Price { get; }
        decimal PreviousClose { get; }
        decimal ChangeRate { get; }   // % (부호 포함)
        long Volume { get; }
        bool IsKrw { get; }           // 통화 표기(₩ vs $)
    }

    /// <summary>캔들 뷰(Candle 공변).</summary>
    public interface ICandleView
    {
        DateTime Date { get; }
        decimal Open { get; }
        decimal High { get; }
        decimal Low { get; }
        decimal Close { get; }
        long Volume { get; }
    }

    /// <summary>52주 신고/신저 이벤트 뷰(ExtremeEvent 공변).</summary>
    public interface IExtremeEventView
    {
        string Symbol { get; }
        string Name { get; }
        bool IsHigh { get; }
        decimal CurrentPrice { get; }
        decimal Extreme52 { get; }
        decimal ChangePct { get; }
    }

    /// <summary>실시간 브로커 Port — 시세/이력/52주 스캔. 도메인 구상 미유입.</summary>
    public interface IRealtimeBrokerPort
    {
        Task<IReadOnlyList<IRealtimeQuoteView>> GetQuotesAsync(IEnumerable<string> symbols, PfMarket market);
        Task<IReadOnlyList<ICandleView>> GetHistoryAsync(string symbol, PfMarket market, DateTime from, DateTime to);
        /// <summary>52주 신고/신저가 스캔 — 어댑터가 스캔 + 이벤트별 알림 발생 내재화. 반환=탐지 이벤트(상태표시용).</summary>
        Task<IReadOnlyList<IExtremeEventView>> Scan52Async(CancellationToken ct);
    }

    /// <summary>워치리스트 영속 Port — 카테고리/종목/재정렬/시드 + 알림규칙 + 테마설정(LocalDatabase·WatchlistStore 격리).</summary>
    public interface IWatchlistPort
    {
        IReadOnlyList<string> LoadCategories();                 // 빌트인 + 영속 커스텀
        bool IsBuiltIn(string category);
        IReadOnlyList<WatchItem> LoadSymbols(string group);     // SortOrder 순
        IReadOnlyList<WatchItem> DefaultsFor(string group);     // 빈 그룹 큐레이션 시드(SampleData 격리)
        void AddSymbol(string group, string code, string name, PfMarket market);
        void RemoveSymbol(string group, string sym);
        void Reorder(string group, IReadOnlyList<string> order);
        bool AddCategory(string name);                          // 빈/빌트인/중복 → false
        bool RemoveCategory(string category);                   // 빌트인 → false
        /// <summary>알림 규칙 등록(cond 0=가≥/1=가≤/2=율≥/3=율≤, channel 0=데스크톱/1=텔레그램/2=전체).</summary>
        void AddAlertRule(string symbol, int condition, decimal target, int channel);
        bool IsDarkTheme();                                     // 고급차트 테마
    }

    /// <summary>워치리스트 항목(종목코드+이름) — 포터블 값 뷰.</summary>
    public readonly record struct WatchItem(string Sym, string Name);
}
