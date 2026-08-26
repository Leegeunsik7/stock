// ============================================================================
// QuantPorts — Phase 94-C: Risk/Quant 컴퓨트 엔진 Port (순수 계약·0 Engine Ref·비동기 취소 지원)
//
//   3대 엔진(RiskAttribution·MonteCarlo·ScenarioStudio)을 UI 가 콘크리트로 직접 호출하지 않도록 하는 경계.
//   ★비동기 + CancellationToken: 몬테카를로 등 고부하 연산을 백그라운드로 오프로드하고, 페이지 이탈 시
//   QuantComputeInterlock 이 토큰을 취소해 자원 유출을 차단한다(Think-Beyond 와 결속). 입력은 IPortfolioSnapshot
//   (읽기 전용), 출력은 IRiskReport/IMcResult/IScenarioResult(읽기 전용). 어댑터(호스트)가 엔진 콘크리트를 격리.
// ============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Quant
{
    /// <summary>위험 요인 분해(Euler) Port.</summary>
    public interface IRiskAnalysisPort
    {
        Task<IRiskReport> AnalyzeAsync(IPortfolioSnapshot portfolio, CancellationToken ct = default);
    }

    /// <summary>몬테카를로 시뮬레이션 Port(고부하 — 백그라운드 오프로드·취소 가능).</summary>
    public interface IMonteCarloPort
    {
        Task<IMcResult> SimulateAsync(IPortfolioSnapshot portfolio, int horizonDays, int sims, int seed = 12345, CancellationToken ct = default);
    }

    /// <summary>시나리오 스트레스(시장 팩터 충격) Port.</summary>
    public interface IScenarioPort
    {
        Task<IScenarioResult> ApplyShockAsync(IPortfolioSnapshot portfolio, double factorShockPct, CancellationToken ct = default);
    }
}
