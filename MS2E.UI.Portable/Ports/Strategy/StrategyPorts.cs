// ============================================================================
// StrategyPorts — Phase 94-D: 전략/백테스트 컴퓨트 엔진 Port (순수 계약·0 Engine Ref·비동기·스트리밍)
//
//   전략 UI(StrategyHub/OptimizerPage 계열)가 콘크리트 엔진(StrategyOptimizer·BacktestEngine·WalkForward)과
//   데이터 프로바이더(IQuoteProvider)·DB(LocalDatabase)를 직접 만지지 않도록 하는 경계.
//     · IStrategyLabPort      — 전략 카트리지 조회 + 매개변수 바인딩(검증).
//     · IBacktestExecutionPort — 백테스트 연산 오프로드(어댑터가 시세를 내부 fetch → UI 는 provider 무참조) +
//                                포트폴리오 스윕 스트리밍(IProgress 로 종목별 결과 수신).
//   ★불변: liveArmed:false(순수 조회/계산 — 발주·소켓 무접촉) · 어댑터(호스트)가 엔진·시세·DB 콘크리트를 격리.
// ============================================================================
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Strategy
{
    /// <summary>전략 카트리지 메타데이터(순수 DTO). MA-크로스 격자 = short×long 파라미터 집합.</summary>
    public sealed record StrategyCartridge(
        string Id,
        string Name,
        string Description,
        IReadOnlyList<int> ShortParams,
        IReadOnlyList<int> LongParams,
        int GridSize);

    /// <summary>시장 선택지(순수 DTO). Code = 호스트 Market enum 정수값(UI 는 enum 무참조).</summary>
    public sealed record MarketOption(int Code, string Label);

    /// <summary>전략 카트리지 조회 + 매개변수 바인딩 Port.</summary>
    public interface IStrategyLabPort
    {
        /// <summary>사용 가능한 전략 카트리지 목록.</summary>
        IReadOnlyList<StrategyCartridge> GetCartridges();

        /// <summary>분석 대상 시장 목록(코드+라벨) — UI 가 Market enum 을 하드코딩하지 않도록 어댑터가 제공.</summary>
        IReadOnlyList<MarketOption> GetMarkets();

        /// <summary>선택 파라미터를 카트리지에 바인딩(검증). 유효하지 않으면 false + error.</summary>
        bool TryBind(string cartridgeId, int shortParam, int longParam, out string error);
    }

    /// <summary>백테스트 연산 Port(고부하 — 백그라운드 오프로드·취소·스트리밍).</summary>
    public interface IBacktestExecutionPort
    {
        /// <summary>한 종목 격자탐색 최적화. 어댑터가 marketCode·months 로 시세를 내부 조회 후 실행(UI 는 시세 무참조).</summary>
        Task<IOptimizationReport> OptimizeAsync(
            string symbol, string name, int marketCode, int months, CancellationToken ct = default);

        /// <summary>보유 종목 전수 스윕. 종목별 완료분을 progress 로 스트리밍하고 최종 스냅샷을 반환.</summary>
        Task<IBacktestResultSnapshot> RunPortfolioSweepAsync(
            int months, int maxSymbols, IProgress<IBacktestResultRow>? progress = null, CancellationToken ct = default);
    }
}
