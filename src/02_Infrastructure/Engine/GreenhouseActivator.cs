// ============================================================================
// GreenhouseActivator — S23: 자율 배양 엔진 점등 (안전 활성화 오케스트레이터)
//
//  GreenhouseService 백그라운드 스레드를 '안전하게' 깨운다. 휴면 상태였던 자율 진화
//  루프를 호스트에서 처음으로 가동하되, 다음 안전 계약을 전부 보존한다:
//
//  ── 안전 계약 (불변) ────────────────────────────────────────────────────────
//   · 온실은 '제안'만 한다 — RunCycleOnce 의 종착점은 PromotionGateway.Submit(승인 대기).
//     실행은 인간 승인 → StrategySlot → AlgoExecutionGate(IsDryRun) → ShadowMetricsBridge
//     (liveArmed=false) 를 거쳐야 하므로, 자율 루프가 돌아도 실자본 위험은 0%.
//   · EmergencyFreeze 시 배양 자동 정지(fail-closed). 예외는 격리 방화벽이 흡수.
//   · 전용 BelowNormal·IsBackground 스레드 — 메인 핫패스 무간섭, 프로세스 수명 미점유.
//
//  ── 부트스트랩 데이터 (정직 경계) ───────────────────────────────────────────
//  DefaultSeeds + BootstrapCandles 는 결정론적 '평행우주' 참조 입력이다. BootstrapCandles
//  는 라이브 시장이 아니라 진동+드리프트 합성 시리즈 — 엔진이 살아 진화·제안함을 즉시·
//  결정론적으로 입증하기 위한 출발점이다. 라이브 캔들 피드 결속(주기적 재-Prime)은 후속.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    public sealed class GreenhouseActivator
    {
        private readonly GreenhouseService _gh;

        public GreenhouseActivator(GreenhouseService greenhouse)
            => _gh = greenhouse ?? throw new ArgumentNullException(nameof(greenhouse));

        /// <summary>결정론 기본 씨앗 — 단순·유효한 전략 개체군(MA 교차 2종 + RSI 회귀). 배양 출발점.</summary>
        public static IReadOnlyList<StrategySpec> DefaultSeeds() => new[]
        {
            MaCross("seed-ma-5-20",  5, 20),
            MaCross("seed-ma-10-40", 10, 40),
            RsiRevert("seed-rsi-14", 14),
        };

        /// <summary>
        /// 결정론 부트스트랩 캔들 — 진동+드리프트 합성 시리즈(MA 교차·추세를 모두 포함).
        /// ⚠️ 라이브 시장 아님. 평행우주 샌드박스의 참조 입력. 같은 호출 → 같은 시리즈.
        /// </summary>
        public static IReadOnlyList<Candle> BootstrapCandles(int n = 120)
        {
            if (n < 60) n = 60;
            var list  = new List<Candle>(n);
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 10000m + (decimal)(Math.Sin(i * 0.5) * 800.0) + i * 12m;
                list.Add(new Candle
                {
                    Date   = start.AddDays(i),
                    Open   = close,
                    High   = close * 1.01m,
                    Low    = close * 0.99m,
                    Close  = close,
                    Volume = 1000,
                });
            }
            return list;
        }

        /// <summary>
        /// 자율 배양 루프를 점등한다(멱등). 이미 가동 중이면 무시.
        /// 안전: 제안 전용·동결 시 자동 정지. 예외는 흡수해 부팅을 막지 않는다.
        /// </summary>
        public bool Activate(Market market)
        {
            if (_gh.Running) return false;
            try
            {
                _gh.Start(DefaultSeeds(), BootstrapCandles(), market);
                Log.Info($"[S23] 자율 배양 엔진 점등 — 시장={market} (제안 전용·실거래 0%)");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[S23] 온실 활성화 실패 — 휴면 유지", ex);
                return false;
            }
        }

        /// <summary>배양 루프를 정지한다(스레드 합류·리소스 반납). 호스트 종료 시 호출.</summary>
        public void Deactivate()
        {
            try { _gh.Stop(); } catch { /* 종료 경로 — 흡수 */ }
        }

        // ── 결정론 씨앗 빌더 ────────────────────────────────────────────────────

        private static StrategySpec MaCross(string id, int shortP, int longP) => new StrategySpec(
            id, $"MA {shortP}/{longP} 교차",
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, shortP), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, longP)) },
            SpecLogic.And,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, shortP), SpecOp.CrossBelow, false, 0, new SpecTerm(SpecKind.Sma, longP)) },
            SpecLogic.And, 5, 10);

        private static StrategySpec RsiRevert(string id, int period) => new StrategySpec(
            id, $"RSI {period} 회귀",
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, period), SpecOp.Lt, true, 30, null) },
            SpecLogic.And,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, period), SpecOp.Gt, true, 70, null) },
            SpecLogic.And, 5, 10);
    }
}
