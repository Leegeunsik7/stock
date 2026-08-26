// ============================================================================
// ExecutionMicrostructureGuard — v4.87.0: 체결 미시구조 경보 → IgnitionLock 강등 배선
//
//  SlippageSentinel(δ=0 고정소수점 3σ) + 체결 레이턴시를 관측해, MasterIgnited 상태에서
//   · 슬리피지 3σ 돌파 '2회 연속' 또는
//   · 체결 레이턴시 100ms 초과
//  감지 시 IgnitionLock.Downgrade() 로 한 단 강등(대형 발주 물리 차단). 결정론·핫패스 무할당.
//
//  ★강등은 '완화된 후퇴' — 영구 락업(ForceFreeze)이 아니다. 파이프라인 무결성 붕괴/예외는
//   OrderDrainWorker 가 ForceFreeze 단일 소스로 별도 에스컬레이션(강등 vs 락업 역할 분리).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class ExecutionMicrostructureGuard
    {
        public const long DefaultMaxLatencyMs = 100;

        private readonly SlippageSentinel _sentinel;
        private readonly IgnitionLock     _ignition;
        private readonly long             _maxLatencyTicks;
        private readonly int              _consecutiveTrip;

        private int  _consecutive3Sigma;
        private long _downgrades;
        private long _latencyTrips;

        public ExecutionMicrostructureGuard(
            SlippageSentinel sentinel, IgnitionLock ignition,
            long maxLatencyMs = DefaultMaxLatencyMs, int consecutiveTrip = 2)
        {
            _sentinel        = sentinel ?? throw new ArgumentNullException(nameof(sentinel));
            _ignition        = ignition ?? throw new ArgumentNullException(nameof(ignition));
            _maxLatencyTicks = (maxLatencyMs <= 0 ? DefaultMaxLatencyMs : maxLatencyMs) * TimeSpan.TicksPerMillisecond;
            _consecutiveTrip = consecutiveTrip < 1 ? 1 : consecutiveTrip;
        }

        // ── 락-프리 관제 독출 ──────────────────────────────────────────────────
        public long Downgrades        => Interlocked.Read(ref _downgrades);
        public long LatencyTrips      => Interlocked.Read(ref _latencyTrips);
        public int  Consecutive3Sigma => Volatile.Read(ref _consecutive3Sigma);
        public long LastSlipRaw       => _sentinel.LastSlipRaw;
        public long LastSigmaRaw      => _sentinel.LastSigmaRaw;

        /// <summary>
        /// 체결 1건 관측. expectedRaw/fillRaw = QFixed raw(10^8), latencyTicks = 발주→체결 경과(100ns ticks).
        /// 3σ 2연속 또는 레이턴시 임계 초과 시 IgnitionLock.Downgrade 격발. 반환: 강등 격발 여부.
        /// </summary>
        public bool OnFill(long expectedRaw, long fillRaw, long latencyTicks)
        {
            bool breach        = _sentinel.RecordAndCheck3Sigma(expectedRaw, fillRaw);   // δ=0 고정소수점 3σ
            bool latencyBreach = latencyTicks > _maxLatencyTicks;

            int consec;
            if (breach) consec = Interlocked.Increment(ref _consecutive3Sigma);
            else        { Interlocked.Exchange(ref _consecutive3Sigma, 0); consec = 0; }

            bool trip = consec >= _consecutiveTrip || latencyBreach;
            if (!trip) return false;

            if (latencyBreach) Interlocked.Increment(ref _latencyTrips);
            _ignition.Downgrade(latencyBreach
                ? $"체결 레이턴시 {latencyTicks / (double)TimeSpan.TicksPerMillisecond:F0}ms 초과 — 강등"
                : $"슬리피지 3σ {consec}회 연속 — 강등");
            Interlocked.Increment(ref _downgrades);
            Interlocked.Exchange(ref _consecutive3Sigma, 0);   // 강등 후 카운터 리셋(재무장 후 재평가)
            return true;
        }
    }
}
