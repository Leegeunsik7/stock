// ============================================================================
// PortfolioPorts — Phase 94-B: 포트폴리오 스냅샷/조회 Port (순수 계약·0 Engine Ref)
//
//   Risk/Quant UI(RiskPage·ScenarioPage·MonteCarloPage)가 호스트의 콘크리트 PortfolioData 에 직접 닿지 않도록 하는
//   경계. UI 는 IPortfolioSnapshot(읽기 전용)만 소비하고, 데이터 로딩은 IPortfolioQueryPort 뒤로 역전(DIP) —
//   구현(어댑터)은 StockManager 호스트가 담당한다. ★엔진/인프라 타입 참조 0(RCL 순수성 불변).
//
//   ★핵심: IPortfolioSnapshot 은 setter 가 없다 → UI 는 '읽기'만 가능(컴파일 타임 불변). 배열 원소 변조 같은
//   캐스트-백 공격은 PortfolioSnapshotIntegritySentinel(Think-Beyond)이 렌더 전후 δ=0 해시로 탐지·fail-closed.
// ============================================================================
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports
{
    /// <summary>
    /// 포트폴리오 읽기 전용 스냅샷(UI 소비 계약). 배열은 계약상 읽기 전용 — 변조 금지(Sentinel 감시).
    /// 필드: 로드상태·에러·종목명·일수익률행렬(%)·시총비중·보유종목수·종목별 USD 여부·총평가액·종목코드.
    /// </summary>
    public interface IPortfolioSnapshot
    {
        bool       Loaded       { get; }
        string     Error        { get; }
        string[]   Names        { get; }   // 종목명(라벨)
        double[][] R            { get; }   // 일수익률 행렬(%) — 리스크/MC 계산 입력
        double[]   Weights      { get; }   // 시총 비중(합 1)
        int        HoldingCount { get; }   // 사용 종목 수(= 옛 Used.Count)
        bool[]     UsdMask      { get; }   // 종목별 USD 통화 여부(콘크리트 Holding/Currency 노출 없이 통화노출 제공)
        double     Total        { get; }   // Phase 94-I: 총 평가액(KRW) — 리밸런싱 조정금액 산정
        string[]   Symbols      { get; }   // Phase 94-I: 종목 코드(드리프트 타겟 저장 키·Weights 정렬)
    }

    /// <summary>포트폴리오 조회 Port(로딩 책임 역전). 어댑터가 시세·DB·FX 콘크리트를 이 뒤로 격리.</summary>
    public interface IPortfolioQueryPort
    {
        /// <summary>지정 일수 윈도로 스냅샷 1회 로드(콜드). 실패 시 Loaded=false·Error 채운 스냅샷(fail-closed).</summary>
        Task<IPortfolioSnapshot> LoadAsync(int days);
    }

    // ── Phase 94-I: 계좌 요약 Port(TotalAssetPage) ──────────────────────────────

    /// <summary>코인 보유 1건 읽기 전용 뷰(DashboardService.CoinRow 추상).</summary>
    public interface ICoinHoldingView
    {
        string   Name       { get; }
        decimal  Price      { get; }
        decimal  ChangeRate { get; }   // 24h 등락(비율) — 한국 관례 색상: ≥0 빨강(up)·<0 파랑(down)
        double[] Spark      { get; }   // 스파크라인 표본
    }

    /// <summary>계좌/총자산 요약 Port — 총자산·자산배분·코인보유를 읽기 전용으로 투사(콘크리트 DashboardService 격리).</summary>
    public interface IAccountSummaryPort
    {
        Task LoadAsync();
        decimal Total         { get; }
        decimal AllocKrwStock { get; }
        decimal AllocUsdStock { get; }
        decimal AllocCoin     { get; }
        decimal CoinValue     { get; }
        decimal UsdKrw        { get; }
        IReadOnlyList<ICoinHoldingView> Coins { get; }
    }

    // ── Phase 94-I: 포트폴리오 허브 Port(RebalancePage) ─────────────────────────

    /// <summary>효율 프론티어 점 읽기 전용 뷰(FrontierPoint 추상).</summary>
    public interface IFrontierPointView
    {
        double VolPct { get; }
        double RetPct { get; }
    }

    /// <summary>MPT 최적화 결과 읽기 전용 뷰(MpoResult 추상 — 도메인 구상 미유입).</summary>
    public interface IMpoResultView
    {
        bool     IsValid      { get; }
        string   Note         { get; }
        string[] Names        { get; }
        double[] Weights      { get; }   // 타겟 비중(합=1)
        double   ExpReturnPct { get; }
        double   VolPct       { get; }
        double   Sharpe       { get; }
        IReadOnlyList<IFrontierPointView> Frontier { get; }
    }

    /// <summary>포트폴리오 허브 Port — MPT 리밸런싱 최적화 + 타겟 저장(MarkowitzOptimizer/LocalDatabase 격리).</summary>
    public interface IPortfolioHubPort
    {
        /// <summary>스냅샷의 수익률행렬/종목으로 목표 변동성 이하 최적 비중을 역산(읽기 전용 결과).</summary>
        IMpoResultView Optimize(IPortfolioSnapshot data, int targetVolPct, int maxWeightPct);

        /// <summary>드리프트 알림 기준 타겟 비중 저장(종목코드↔비중, 어댑터가 JSON 직렬화·DB 기입).</summary>
        void SaveRebalanceTarget(IReadOnlyList<string> symbols, IReadOnlyList<double> weights);
    }
}
