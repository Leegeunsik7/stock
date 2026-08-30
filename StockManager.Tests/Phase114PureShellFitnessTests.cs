// ============================================================================
// Phase114PureShellFitnessTests — v11.4.0 PureShellFitnessSentinel 적대적 검증(Think-Beyond)
//   호스트 어셈블리 결합 UI(PortBlocked) 수를 단조 래칫으로 강제 — 신규 UI 콘크리트 오염 시 초과→실패.
//   ★Pure Host Shell 로의 수렴을 CI 가 물리적으로 보증(회귀 0).
// ============================================================================
using System;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class Phase114PureShellFitnessTests
    {
        private readonly ITestOutputHelper _out;
        public Phase114PureShellFitnessTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void HostShell_Coupled_Ui_Within_Ratchet()
        {
            var host = typeof(CanaryHealthSentinel).Assembly;   // MS2E.StockSuite(호스트)
            var r = PureShellFitnessSentinel.Scan(host);
            _out.WriteLine($"[PureShell] total={r.TotalComponents} coupled={r.HostCoupledUi} shell={r.ShellComponents} ratchet={r.Ratchet}");

            Assert.True(r.TotalComponents > 0);   // 컴포넌트 스캔 성공(리플렉션 유효)
            Assert.True(r.WithinRatchet,
                $"호스트 결합 UI {r.HostCoupledUi} > 래칫 {r.Ratchet} — 신규 UI 콘크리트 오염(Pure Host Shell 회귀). 이주하거나 래칫을 정당화하라.");
        }

        [Fact]
        public void Detects_Coupled_Vs_Shell_Component()
        {
            // 결합 UI(호스트) — [Inject] 보유 컴포넌트가 존재해야 정상(아직 Pure Shell 아님)
            var host = typeof(CanaryHealthSentinel).Assembly;
            var r = PureShellFitnessSentinel.Scan(host);
            Assert.True(r.HostCoupledUi > 0);   // 잔여 결합 UI 존재(정직 — 완전 Pure Shell 미달성)

            // 판정 로직: [Inject] 선언 타입 → coupled, 무선언 순수 타입 → shell
            Assert.True(PureShellFitnessSentinel.IsHostCoupledUi(typeof(CoupledProbe)));
            Assert.False(PureShellFitnessSentinel.IsHostCoupledUi(typeof(ShellProbe)));
        }

        // 판정 검증용 프로브(테스트 로컬 컴포넌트).
        private sealed class CoupledProbe : Microsoft.AspNetCore.Components.ComponentBase
        {
            [Microsoft.AspNetCore.Components.Inject] public CanaryHealthSentinel Dep { get; set; } = default!;
        }
        private sealed class ShellProbe : Microsoft.AspNetCore.Components.ComponentBase { }
    }
}
