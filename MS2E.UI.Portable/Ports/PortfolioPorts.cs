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
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports
{
    /// <summary>
    /// 포트폴리오 읽기 전용 스냅샷(UI 소비 계약). 배열은 계약상 읽기 전용 — 변조 금지(Sentinel 감시).
    /// 필드: 로드상태·에러·종목명·일수익률행렬(%)·시총비중·보유종목수·종목별 USD 여부.
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
    }

    /// <summary>포트폴리오 조회 Port(로딩 책임 역전). 어댑터가 시세·DB·FX 콘크리트를 이 뒤로 격리.</summary>
    public interface IPortfolioQueryPort
    {
        /// <summary>지정 일수 윈도로 스냅샷 1회 로드(콜드). 실패 시 Loaded=false·Error 채운 스냅샷(fail-closed).</summary>
        Task<IPortfolioSnapshot> LoadAsync(int days);
    }
}
