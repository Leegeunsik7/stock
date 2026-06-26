// ============================================================================
// MacroChaosTailReplayer — v4.72.0: 꼬리 리스크 가상 클럭 재현기
//
//  역사적 위기 시나리오(GFC 2008, LUNA 2022)의 슬리피지 패턴을
//  가상 클럭(long virtualTick)으로 1000x 압축 재생하여
//  SlippageSentinel의 3σ 탐지 → ForceFreeze 회로를 단위 테스트 속도로 검증.
//
//  설계 원칙:
//   · DateTime.UtcNow 미사용 — FillPayload.TimestampTicks 에 가상 틱 기록
//   · SpeedMultiplier = 1000: 1일 → TimeSpan.TicksPerDay/1000 틱 간격
//   · 10건 기준선(베이스라인 σ 확립) → 위기 3건 연속 초과 → AlertTriggered
//   · attestation?.ForceFreeze() 호출 → ReplayResult.FreezeTriggered = true
//
//  슬리피지 정의: |fillPrice - expectedPrice| / expectedPrice
//   기준선: ~0.001 (0.1%)  GFC 위기: 0.10 → 0.20 → 0.40
//   LUNA 위기: 0.30 → 0.60 → 0.90  정상: 0.0008 ~ 0.0012
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    // ChaosScenario(QChaos.cs 포트폴리오 스트레스) · MacroCrisisScenario(TailBudget.cs record) 와 별개:
    // 이 enum 은 슬리피지 탐지 꼬리 리스크 재현 전용.
    public enum MacroCrisisScenario { Normal, Gfc2008, Luna2022 }

    public sealed record TailReplayResult(
        MacroCrisisScenario Scenario,
        int           TotalEvents,
        int           AlertsFired,
        bool          FreezeTriggered,
        long          FinalVirtualTickMs);

    public static class MacroChaosTailReplayer
    {
        private const double BasePrice       = 50_000.0;  // 가상 종목 기준가 (원)
        private const long   SymCode         = 5930;       // BridgeSerializer 6자리 허용 범위
        private const int    SpeedMultiplier = 1000;       // 1000배 압축 (1일 → ~86ms)

        private static readonly long TicksPerEvent =
            TimeSpan.TicksPerDay / SpeedMultiplier;        // 86_400_000_000 / 1000 = 86_400_000 틱

        // ── 시나리오 슬리피지 배열 ─────────────────────────────────────────────
        // 원소: fillPrice = BasePrice * (1 - slippage) → residual = -slippage
        // 기준선 10건: 교대 0.0008 / 0.0012 (평균 0.001, σ ≈ 0.0002)
        // 위기 구간: 100x 이상 이상치 → Z >> AlertThresholdSigma 연속 3회 보장

        private static readonly double[] NormalSlippages =
        {
            0.00080, 0.00120, 0.00090, 0.00110, 0.00100,
            0.00075, 0.00125, 0.00085, 0.00115, 0.00100,
            0.00080, 0.00120, 0.00090, 0.00110, 0.00100,
            0.00075, 0.00125, 0.00085, 0.00115, 0.00100,
        };

        // GFC 2008 아날로그: 기준선 10 + Lehman 급락 에스컬레이션 3 + 여진 7
        private static readonly double[] Gfc2008Slippages =
        {
            // 기준선 (SlippageSentinel σ 확립)
            0.00080, 0.00120, 0.00090, 0.00110, 0.00100,
            0.00080, 0.00120, 0.00090, 0.00110, 0.00100,
            // Lehman 첫째 날: Z ≈ 500
            0.10,
            // 둘째 날: 기준선 오염 후에도 Z > 2 유지 (에스컬레이션)
            0.20,
            // 셋째 날: 서킷 브레이커 → 연속 3회 충족 → AlertTriggered
            0.40,
            // 여진 (동결 이후 검증 불필요이나 시나리오 완전성 위해 포함)
            0.35, 0.28, 0.32, 0.25, 0.30, 0.22, 0.18,
        };

        // LUNA 2022 아날로그: 기준선 10 + UST 디페그 나선 3 + 사후
        private static readonly double[] Luna2022Slippages =
        {
            // 기준선
            0.00080, 0.00120, 0.00090, 0.00110, 0.00100,
            0.00080, 0.00120, 0.00090, 0.00110, 0.00100,
            // UST 디페그 시작: Z ≈ 1500
            0.30,
            // 죽음 나선 가속
            0.60,
            // 거의 0에 수렴: 연속 3회 → AlertTriggered
            0.90,
            // 사후 변동성
            0.50, 0.65,
        };

        // ── 재현 실행 ────────────────────────────────────────────────────────────
        public static TailReplayResult Replay(
            MacroCrisisScenario scenario,
            SlippageSentinel sentinel,
            OrderAttestationService? attestation = null,
            long startTicks = 0)
        {
            double[] slippages = scenario switch
            {
                MacroCrisisScenario.Gfc2008  => Gfc2008Slippages,
                MacroCrisisScenario.Luna2022 => Luna2022Slippages,
                _                     => NormalSlippages,
            };

            bool freezeTriggered = false;
            int  alertsFired     = 0;

            void OnAlert(SlippageAlert alert)
            {
                alertsFired++;
                // ForceFreeze: 한 번으로 충분 — 이후 추가 호출은 멱등
                if (!freezeTriggered)
                {
                    attestation?.ForceFreeze(
                        $"[TailReplay:{scenario}] " +
                        $"Z={alert.ZScore:F1}σ × {alert.ConsecutiveExceedances}연속");
                    freezeTriggered = true;
                }
            }

            sentinel.AlertTriggered += OnAlert;

            long virtualTick = startTicks;
            for (int i = 0; i < slippages.Length; i++)
            {
                virtualTick += TicksPerEvent;

                // 하방 슬리피지: 체결가 < 기준가 (매수 불리 방향)
                double fillPrice = BasePrice * (1.0 - slippages[i]);

                var fill = new FillPayload
                {
                    SymbolCode     = SymCode,
                    FillPrice      = fillPrice,
                    FillQty        = 100,
                    Side           = 1,    // +1 = 매수
                    TimestampTicks = virtualTick,
                };

                sentinel.RecordFill(fill, expectedPrice: BasePrice);
            }

            sentinel.AlertTriggered -= OnAlert;

            return new TailReplayResult(
                scenario,
                slippages.Length,
                alertsFired,
                freezeTriggered,
                virtualTick / TimeSpan.TicksPerMillisecond);
        }

        // ── 시나리오 슬리피지 배열 노출 (테스트 화이트박스 접근용) ──────────────
        public static IReadOnlyList<double> GetScenarioSlippages(MacroCrisisScenario scenario) =>
            scenario switch
            {
                MacroCrisisScenario.Gfc2008  => Gfc2008Slippages,
                MacroCrisisScenario.Luna2022 => Luna2022Slippages,
                _                     => NormalSlippages,
            };
    }
}
