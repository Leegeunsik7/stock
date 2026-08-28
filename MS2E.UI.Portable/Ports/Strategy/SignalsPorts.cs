// ============================================================================
// SignalsPorts — Phase 94-F: 시그널 저장소 + 서비스 Port (원시 SQL/인프라 심층 해체)
//   SignalPage 의 Db.Query/Execute(원시 SQL)·DataTable·DashboardService·SampleData·AlertCenter·BacktestEngine
//   직결을 이 경계 뒤로 밀어낸다. 어댑터(호스트)가 Dapper/LocalDB/AlertCenter/엔진 콘크리트를 전량 격리.
//   ★liveArmed:false: 조회/스캔/백테스트 — 발주·소켓·IBroker 무접촉.
// ============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Strategy
{
    /// <summary>시그널 이력 저장소 Port — 원시 SQL/DataTable 을 어댑터로 격리, 읽기는 불변 스냅샷.</summary>
    public interface ISignalsRepositoryPort
    {
        /// <summary>최근 시그널 이력(최신순 최대 limit 건) — 읽기 전용 스냅샷.</summary>
        IReadOnlyList<ISignalRecordSnapshot> GetRecent(int limit);

        /// <summary>지정 일수 이전 시그널 정리. 반환=삭제 건수(추정).</summary>
        int DeleteOlderThan(int days);
    }

    /// <summary>시그널 서비스 Port — 코인 임박 신호·주식 전체 스캔·백테스트·샘플 종목/전략명.</summary>
    public interface ISignalsServicePort
    {
        /// <summary>코인 임박 신호 갱신(대시보드 LoadAsync 위임).</summary>
        Task RefreshCoinSignalsAsync();

        /// <summary>현재 코인 임박 신호(읽기 전용).</summary>
        IReadOnlyList<ICoinSignalView> GetCoinSignals();

        /// <summary>주식 전체 스캔(SampleData 순회 + 시세 조회 + 골든/데드크로스 판정 + 저장 + 매수 알림). 반환=감지 건수.</summary>
        Task<int> ScanStocksAsync(CancellationToken ct = default);

        /// <summary>자유입력 datalist 용 샘플 종목.</summary>
        IReadOnlyList<ISampleSymbol> GetSampleSymbols();

        /// <summary>백테스트 전략명 목록.</summary>
        IReadOnlyList<string> GetStrategyNames();

        /// <summary>비용 반영 백테스트. 히스토리 부족(30봉 미만) 시 null.</summary>
        Task<IBacktestSummaryView?> BacktestAsync(string symbol, int strategyIndex, int months, CancellationToken ct = default);
    }
}
