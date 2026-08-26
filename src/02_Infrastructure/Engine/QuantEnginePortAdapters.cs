// ============================================================================
// QuantEnginePortAdapters — Phase 94-C: Risk/Quant 엔진 Port 호스트 어댑터 (DIP 구현)
//   순수 UI(RCL)의 IRiskAnalysisPort/IMonteCarloPort/IScenarioPort 를 콘크리트 엔진(RiskAttribution/MonteCarlo/
//   ScenarioStudio)에 결속. 고부하 연산을 Task.Run 으로 백그라운드 오프로드하고 CancellationToken 을 전달한다
//   (QuantComputeInterlock 이 이탈/재설정 시 취소·stale 드롭). 입력은 IPortfolioSnapshot(읽기 전용), 반환은
//   콘크리트 결과를 읽기 전용 인터페이스로 업캐스트. ★liveArmed:false: 순수 조회/계산 — 발주/소켓 무접촉.
//   ※ .NET 동기 연산은 시작 후 중단 불가 — ct 는 '미시작 큐 취소' + 인터락의 결과-드롭으로 UI측 유출을 봉쇄(정직).
// ============================================================================
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports;
using MS2E.UI.Portable.Ports.Quant;

namespace MS2E.Infrastructure
{
    public sealed class RiskAnalysisAdapter : IRiskAnalysisPort
    {
        public Task<IRiskReport> AnalyzeAsync(IPortfolioSnapshot p, CancellationToken ct = default)
            => Task.Run<IRiskReport>(() => RiskAttribution.Analyze(p.R, p.Names, p.Weights), ct);
    }

    public sealed class MonteCarloAdapter : IMonteCarloPort
    {
        public Task<IMcResult> SimulateAsync(IPortfolioSnapshot p, int horizonDays, int sims, int seed = 12345, CancellationToken ct = default)
            => Task.Run<IMcResult>(() => MonteCarlo.Simulate(p.R, p.Weights, horizonDays, sims, seed), ct);
    }

    public sealed class ScenarioAdapter : IScenarioPort
    {
        public Task<IScenarioResult> ApplyShockAsync(IPortfolioSnapshot p, double factorShockPct, CancellationToken ct = default)
            => Task.Run<IScenarioResult>(() => ScenarioStudio.ApplyShock(p.R, p.Names, p.Weights, factorShockPct), ct);
    }
}
