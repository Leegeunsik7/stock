// ============================================================================
// AutonomousAlphaFactory — v4.72.0: 자율 알파 공장
//
//  24시간 주기 백그라운드 루프:
//   1. HypothesisMiner.Mine() — OOS 생존 패턴 발굴 (결정론 기준 시계열)
//   2. MaGapPct 조건 파라미터 추출 → 교차선 Short/Long 페어 생성
//   3. CartridgeEngine.Attest() — 결정론 SplitMix64 백테스트 증명서
//   4. CartridgeNumericsGuard.TryValidate() — 서명 전 수치 안전 검증
//   5. CartridgeService.BuildSigned() — ECDsa 서명 + 파일 내보내기
//
//  ·  EmergencyFreeze 상태 → 주기 스킵 (fail-closed)
//  ·  외부 시세 API 의존 0 — CartridgeEngine.ReferenceSeries(seed) 전용
//  ·  CartridgeNumericsGuard 는 이 파일에 함께 선언 (서명 전 게이트)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace StockManager.Common
{
    // ── 수치 안전 검증기 — Attest 결과를 ECDsa 서명 전에 점검 ─────────────────
    public static class CartridgeNumericsGuard
    {
        public static bool TryValidate(CartStrategy s, out string violation)
        {
            violation = string.Empty;

            if (s.Short <= 0)
                { violation = $"Short={s.Short}≤0"; return false; }
            if (s.Long <= 0)
                { violation = $"Long={s.Long}≤0"; return false; }
            if (s.Short >= s.Long)
                { violation = $"Short({s.Short})≥Long({s.Long}) — 교차선 역전"; return false; }
            if (s.AttestTrades <= 0)
                { violation = "거래 0 — 전략 기간 내 미발동"; return false; }
            if (!double.IsFinite(s.AttestTotalReturnPct))
                { violation = $"TotalReturn 비유한값 ({s.AttestTotalReturnPct})"; return false; }
            if (!double.IsFinite(s.AttestMaxDrawdownPct))
                { violation = $"MaxDrawdown 비유한값 ({s.AttestMaxDrawdownPct})"; return false; }
            if (s.AttestMaxDrawdownPct > 0)
                { violation = $"MDD={s.AttestMaxDrawdownPct:+0.00;-0.00}>0 (부호 오류: 손실은 음수)"; return false; }
            // 결정론 재검증 — 성과 주장과 재실행 결과 정합 확인
            if (!CartridgeEngine.VerifyStrategy(s))
                { violation = "결정론 재검증 실패 — 백테스트 디싱크 (데이터 변조 의심)"; return false; }

            return true;
        }
    }

    // ── 자율 알파 공장 ─────────────────────────────────────────────────────────
    public sealed class AutonomousAlphaFactory : IDisposable
    {
        private readonly LocalDatabase            _db;
        private readonly OrderAttestationService  _gate;
        private readonly Thread                   _worker;
        private volatile bool                     _stop;

        public int CyclesCompleted   { get; private set; }
        public int StrategiesSigned  { get; private set; }
        public int GuardRejections   { get; private set; }

        public AutonomousAlphaFactory(LocalDatabase db, OrderAttestationService gate)
        {
            _db   = db   ?? throw new ArgumentNullException(nameof(db));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _worker = new Thread(RunLoop)
            {
                IsBackground = true,
                Name         = "AutonomousAlphaFactory",
                Priority     = ThreadPriority.BelowNormal,
            };
        }

        public void Start()
        {
            // IsAlive guard: DI singleton 이므로 이중 호출은 없어야 하나, ThreadStateException 방어.
            if (_worker.IsAlive) return;
            _worker.Start();
        }

        private void RunLoop()
        {
            while (!_stop)
            {
                try { RunCycle(); }
                catch { /* 사이클 예외는 루프를 중단하지 않음 */ }

                // 24h 대기 — 1분 간격으로 stop 신호 확인
                for (int m = 0; m < 1440 && !_stop; m++)
                    Thread.Sleep(TimeSpan.FromMinutes(1));
            }
        }

        // 테스트에서 직접 호출 가능 (public)
        public List<CartStrategy> RunCycle()
        {
            // EmergencyFreeze → 발굴·서명 모두 차단 (fail-closed)
            if (_gate.Mode == GatingMode.EmergencyFreeze)
                return new List<CartStrategy>();

            // 결정론 기준 시계열 (seed=42, 300봉) — 외부 API 의존 0
            var candles = CartridgeEngine.ReferenceSeries(seed: 42, n: 300);

            // 1. 가설 발굴
            var mined = HypothesisMiner.Mine(candles,
                horizon: 10, trainFrac: 0.7, minOccurrences: 8, baseEdgeBar: 0.5);

            // 2. 생존 가설에서 MA 기간 추출 → Short/Long 페어
            var pairs = ExtractPeriodPairs(mined.Pool).ToList();

            var signed = new List<CartStrategy>();
            foreach (var (shortP, longP) in pairs)
            {
                if (_stop) break;

                // 3. 결정론 증명서
                var strategy = CartridgeEngine.Attest(
                    $"AlphaFact-S{shortP}L{longP}",
                    shortP, longP,
                    seed: shortP * 1000 + longP);

                // 4. 수치 안전 검증 (서명 전 게이트)
                if (!CartridgeNumericsGuard.TryValidate(strategy, out _))
                {
                    GuardRejections++;
                    continue;
                }

                signed.Add(strategy);
            }

            // 5. 서명 + 파일 내보내기 — EmergencyFreeze 재확인 (Drain 중 상태 변화 방어)
            if (signed.Count > 0 && _gate.Mode != GatingMode.EmergencyFreeze)
            {
                var cart = CartridgeService.BuildSigned(
                    _db,
                    $"AlphaFactory-{CyclesCompleted}",
                    $"자율 알파 공장 자동 생성 — 사이클 {CyclesCompleted}",
                    signed);
                CartridgeService.ExportToFile(cart);
                StrategiesSigned += signed.Count;
            }

            CyclesCompleted++;
            return signed;
        }

        private static IEnumerable<(int shortP, int longP)> ExtractPeriodPairs(
            List<Hypothesis> survivors)
        {
            // MaGapPct 조건의 Param(= MA기간)을 교차선 Long 기간으로 매핑
            var maperiods = survivors
                .SelectMany(h => h.Rule.Conditions)
                .Where(c => c.Metric == CondMetric.MaGapPct)
                .Select(c => c.Param)
                .Where(p => p is >= 5 and <= 200)
                .Distinct()
                .OrderBy(p => p);

            foreach (int longP in maperiods)
            {
                int shortP = Math.Max(3, longP / 3);
                if (shortP < longP) yield return (shortP, longP);
            }

            // 시장 대표 MA 교차 기준 페어 (항상 포함)
            yield return (5, 20);
            yield return (8, 34);
            yield return (13, 55);
        }

        public void Dispose()
        {
            _stop = true;
            try { _worker.Join(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
