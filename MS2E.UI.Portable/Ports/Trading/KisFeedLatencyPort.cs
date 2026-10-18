// ============================================================================
// KisFeedLatencyPort — [v12.18.16-DEV] KIS 실시간 시세 지연 · 시각 편차 관제 계약(순수 UI 경계 · 엔진 타입 참조 0)
//
//   호스트 KisTimeSkewGuard 가 H0STASP0(호가) · H0STCNT0(체결) 전문 시각(HHmmss · KST · 1초 해상도)과 호스트 벽시계를 대조한 결과를 값 구조체로 노출.
//   ★정직: KIS 전문 시각은 초 단위다 → 지연은 [하한, 상한] 구간으로만 알 수 있다(하한 = 호스트 − (초 + 999ms) · 상한 = 호스트 − 초).
//   격발(50ms) 판정은 '하한' 기준 — 격발 = 지연이 50ms 를 확실히 넘었음(오격발 0 · 단, 50 ~ 1,050ms 구간 일부는 미포착 가능).
//   ★읽기 전용 — 이 포트에는 재무장 · 주문 경로가 없다(래치 해제는 호스트 물리 이중키 + 회복 표본 충족 시에만).
// ============================================================================
namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>KIS 시세 관제 상태(0 미결선 · 1 수신 대기 · 2 정상 · 3 래치).</summary>
    public static class DashKisFeed
    {
        public const int Unbound = 0, Waiting = 1, Ok = 2, Latched = 3;
        public static string Label(int s) => s switch
        {
            Waiting => "KIS 시세 미수신", Ok => "KIS 시세 정상", Latched => "KIS 시세 지연 래치(비트 28)", _ => "KIS 시세 관제 미결선",
        };
        public static string Css(int s) => s switch { Ok => "ok", Latched => "bad", Waiting => "wait", _ => "off" };
    }

    /// <summary>격발 사유(0 = 없음).</summary>
    public static class KisSkewReason
    {
        public const int None = 0, Latency = 1, Inversion = 2, ClockAhead = 3, BadTimestamp = 4;
        public static string Label(int r) => r switch
        {
            Latency => "지연 하한 > 50ms", Inversion => "전문 시각 역전", ClockAhead => "전문 시각이 호스트보다 50ms 초과 앞섬(시계 편차)",
            BadTimestamp => "전문 시각 형식 이탈", _ => "-",
        };
    }

    /// <summary>KIS 시세 지연 관제 1프레임(ms · 값 복사).</summary>
    public readonly struct KisFeedLatencyStatus
    {
        public readonly int State, LastTripReason, RecoveryFrames;
        public readonly long LagLowerMs, LagUpperMs, MaxLagLowerMs, Frames, Violations, Trips, HealthyStreak;

        public KisFeedLatencyStatus(int state, long lagLowerMs, long lagUpperMs, long maxLagLowerMs, long frames, long violations, long trips,
                                    int lastTripReason, long healthyStreak, int recoveryFrames)
        {
            State = state; LagLowerMs = lagLowerMs; LagUpperMs = lagUpperMs; MaxLagLowerMs = maxLagLowerMs; Frames = frames; Violations = violations;
            Trips = trips; LastTripReason = lastTripReason; HealthyStreak = healthyStreak; RecoveryFrames = recoveryFrames;
        }

        /// <summary>뱃지 표기("12 ~ 1,011ms" — 하한 음수 = 0).</summary>
        public string RangeText => (LagLowerMs < 0 ? 0 : LagLowerMs).ToString("N0") + " ~ " + LagUpperMs.ToString("N0") + "ms";
    }

    public interface IKisFeedLatencyPort
    {
        KisFeedLatencyStatus Status { get; }
    }
}
