// ============================================================================
// MacroChaosReplayTests — v4.72.0
//
//  MacroChaosTailReplayer 의 3σ 탐지 → ForceFreeze 회로를 결정론 검증.
//  모든 테스트: 가상 클럭(long startTicks) 기반, DateTime.UtcNow 미사용.
//
//  알람 설계:
//   · SlippageSentinel { AlertThresholdSigma=2.0, ConsecutiveAlertCount=3 }
//   · 기준선 10건 (σ≈0.0002) → 위기 3건 에스컬레이션 → Z > 2.0 연속 3회
//   · GFC 첫 위기: slippage 0.10→0.20→0.40 (기준선 σ 오염 후에도 Z > 2)
//   · LUNA 첫 위기: slippage 0.30→0.60→0.90 (Z > 100)
//   · 정상 시나리오: 슬리피지 0.00075~0.00125 → Z < 1.5 → 경보 없음
// ============================================================================
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class MacroChaosReplayTests
    {
        private static SlippageSentinel MakeSentinel() => new()
        {
            AlertThresholdSigma   = 2.0,
            ConsecutiveAlertCount = 3,
            WindowSize            = 20,
        };

        // ── GFC 2008: 기준선 10건 + Lehman 에스컬레이션 3건 → ForceFreeze ────
        [Fact]
        public void Gfc2008_EscalatingSlippage_TriggersFreeze()
        {
            var sentinel    = MakeSentinel();
            var attestation = new OrderAttestationService();

            var result = MacroChaosTailReplayer.Replay(
                MacroCrisisScenario.Gfc2008, sentinel, attestation, startTicks: 0);

            Assert.True(result.FreezeTriggered,
                $"GFC 시나리오: ForceFreeze 미발동 (AlertsFired={result.AlertsFired})");
            Assert.Equal(GatingMode.EmergencyFreeze, attestation.Mode);
            Assert.True(result.AlertsFired >= 1,
                "AlertTriggered 이벤트 최소 1회 이상 발생해야 함");
        }

        // ── LUNA 2022: UST 디페그 나선 → 기준선 대비 수백 배 슬리피지 ─────────
        [Fact]
        public void Luna2022_DeathSpiral_TriggersEarlyFreeze()
        {
            var sentinel    = MakeSentinel();
            var attestation = new OrderAttestationService();

            var result = MacroChaosTailReplayer.Replay(
                MacroCrisisScenario.Luna2022, sentinel, attestation, startTicks: 0);

            Assert.True(result.FreezeTriggered,
                $"LUNA 시나리오: ForceFreeze 미발동 (AlertsFired={result.AlertsFired})");
            Assert.Equal(GatingMode.EmergencyFreeze, attestation.Mode);

            int slippageCount = MacroChaosTailReplayer.GetScenarioSlippages(MacroCrisisScenario.Luna2022).Count;
            Assert.Equal(slippageCount, result.TotalEvents);
        }

        // ── 정상 시나리오: 20건 모두 기준 범위 내 → 동결 없음 ─────────────────
        [Fact]
        public void Normal_AllWithinBaseline_NoFreeze()
        {
            var sentinel    = MakeSentinel();
            var attestation = new OrderAttestationService();

            var result = MacroChaosTailReplayer.Replay(
                MacroCrisisScenario.Normal, sentinel, attestation, startTicks: 0);

            Assert.False(result.FreezeTriggered,
                $"정상 시나리오: 의도치 않은 ForceFreeze 발동 (AlertsFired={result.AlertsFired})");
            Assert.Equal(GatingMode.Normal, attestation.Mode);
            Assert.Equal(0, result.AlertsFired);
        }

        // ── 가상 클럭: startTicks 가 FinalVirtualTickMs 에 반영되는지 확인 ──────
        [Fact]
        public void VirtualClock_StartTicksIsHonored()
        {
            long startTicks = System.TimeSpan.TicksPerDay * 100L;   // 100일 오프셋
            var sentinel    = MakeSentinel();

            var result = MacroChaosTailReplayer.Replay(
                MacroCrisisScenario.Normal, sentinel, startTicks: startTicks);

            long startMs = startTicks / System.TimeSpan.TicksPerMillisecond;
            Assert.True(result.FinalVirtualTickMs > startMs,
                "가상 클럭이 startTicks 이후로 진행되어야 함");
        }

        // ── attestation 없이도 sentinel 탐지 자체는 동작해야 함 ─────────────────
        [Fact]
        public void Gfc2008_WithoutAttestation_StillDetectsAlert()
        {
            var sentinel = MakeSentinel();

            var result = MacroChaosTailReplayer.Replay(
                MacroCrisisScenario.Gfc2008, sentinel, attestation: null, startTicks: 0);

            Assert.True(result.FreezeTriggered,
                "attestation 없이도 AlertTriggered 이벤트 발생 → FreezeTriggered 설정");
            Assert.True(result.AlertsFired >= 1);
        }
    }
}
