// ============================================================================
// ComplianceBridge — S17 #2: 자동 경보 생성 브릿지 (v3.93.0)
//
//  두 가지 독립 감시 경로:
//  ① SlippageSentinel.AlertTriggered → FraudAlerts HIGH  (소스: SENTINEL)
//  ② AdaptiveExecutionTuner.OnCapBreached → FraudAlerts CRITICAL (소스: TUNER)
//
//  핫 파이프라인 비차단 설계:
//  · 이벤트 핸들러는 즉시 Task.Run(fire-and-forget)으로 제어 복귀 →
//    트레이딩 틱 루프 레이턴시 0 영향.
//  · DB I/O는 ThreadPool 스레드에서 비동기 처리.
//
//  환각 방화벽: 모든 경로 try-catch → 예외가 트레이딩 코어로 전파되지 않음.
// ============================================================================
using System;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    public sealed class ComplianceBridge
    {
        private volatile LocalDatabase? _db;

        // ── DB 주입 ────────────────────────────────────────────────────────────
        /// <summary>앱 시작 시 1회 호출. 미호출 시 DB 경로는 silently skip.</summary>
        public void Initialize(LocalDatabase db)
            => _db = db ?? throw new ArgumentNullException(nameof(db));

        // ── SlippageSentinel 배선 ──────────────────────────────────────────────
        /// <summary>Sentinel 의 AlertTriggered 이벤트를 구독합니다.</summary>
        public void Attach(SlippageSentinel sentinel)
        {
            if (sentinel is null) throw new ArgumentNullException(nameof(sentinel));
            sentinel.AlertTriggered += OnSentinelAlert;
        }

        /// <summary>Sentinel 이벤트 구독을 해제합니다.</summary>
        public void Detach(SlippageSentinel sentinel)
        {
            if (sentinel is null) return;
            sentinel.AlertTriggered -= OnSentinelAlert;
        }

        // ── AdaptiveExecutionTuner 콜백 배선 ───────────────────────────────────
        /// <summary>Tuner 의 캡 연속 위반 콜백을 CRITICAL 경보 경로로 연결합니다.</summary>
        public void Wire(AdaptiveExecutionTuner tuner)
        {
            if (tuner is null) throw new ArgumentNullException(nameof(tuner));
            tuner.OnCapBreached = (symbol, msg) => PostCapCritical(symbol, msg);
        }

        // ── 내부: Sentinel 경보 핸들러 ────────────────────────────────────────
        private void OnSentinelAlert(SlippageAlert alert)
        {
            // 핫 경로 비차단: DB I/O는 ThreadPool 에서 fire-and-forget
            var db = _db;  // volatile read — snapshot before thread switch
            _ = Task.Run(() =>
            {
                try
                {
                    string msg = FormattableString.Invariant(
                        $"Z={alert.ZScore:F4} μ={alert.Mean:+0.000000;-0.000000} σ={alert.Sigma:F6} 연속={alert.ConsecutiveExceedances}회");
                    db?.AddFraudAlert(alert.Symbol, "SENTINEL", msg, "HIGH");
                }
                catch { }
            });
        }

        // ── 내부: Tuner 캡 위반 CRITICAL 경보 ────────────────────────────────
        private void PostCapCritical(string symbol, string message)
        {
            var db = _db;
            _ = Task.Run(() =>
            {
                try { db?.AddFraudAlert(symbol, "TUNER", message, "CRITICAL"); }
                catch { }
            });
        }
    }
}
