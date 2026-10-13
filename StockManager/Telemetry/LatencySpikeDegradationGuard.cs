// ============================================================================
// LatencySpikeDegradationGuard — [v12.17.5-DEV · 방향 B] 주문 경로 미세 정체 시 관제 갱신 주기 자율 감속(50ms → 200ms)
//
//  왜: DashboardSnapshotHub 펌프는 System.Threading.Timer 콜백(스레드 풀)이고, 프레임 게시마다 UI 가 InvokeAsync 로 렌더를 예약한다
//   (역시 스레드 풀 · 회로 디스패처). 주문 경로 p99(시그널 → 발주)가 1ms 를 넘는다는 것은 드레인 스레드가 무언가와 다투고 있다는 신호다 —
//   그때 관제 화면이 초당 20회 갱신을 고집할 이유가 없다. 이 가드는 '보는 쪽'이 물러나 부하를 덜어 준다(매매 경로 판정 · 송출에는 손대지 않음).
//
//  상태 기계(순수 계산 · 시계 주입 · 펌프 단일 스레드):
//   · 정상 → 감속: 새 표본이 들어온 회차(fresh)의 최근 창 p99 > SpikeNanos(기본 1ms) 1회 = 즉시 감속(부하 완화는 빠를수록 좋다).
//   · 감속 → 정상: 마지막 '정체 관측' 이후 RecoverHold(기본 2초) 동안 정체 재관측 0 = 복귀. 새 주문이 끊겨 창이 갱신되지 않아도
//     2초 뒤 복귀한다(주문이 없으면 다툴 드레인도 없다) — 오래된 정체 표본에 갇혀 영구 감속되는 것을 막는다.
//   · 감속 중 게시 허가: 마지막 게시로부터 DegradedPeriod(기본 200ms) 경과 시에만. 미허가 회차는 Suppressed 계수 · 허브는 dirty 를 보류해
//     다음 허가 회차에 반드시 게시한다(변경 유실 0 — 늦게 보일 뿐).
//  δ=0: Observe · AllowPublish · MarkPublished 힙 0. 히스테리시스는 '시간 기반' — 경계 p99 진동이 50ms/200ms 를 깜빡이게 하지 않는다.
// ============================================================================
using System;

namespace StockManager.Telemetry
{
    public sealed class LatencySpikeDegradationGuard
    {
        public const long DefaultSpikeNanos = 1_000_000;                             // 1ms
        public const int DefaultNormalPeriodMs = 50, DefaultDegradedPeriodMs = 200, DefaultRecoverHoldMs = 2_000;
        private const long TicksPerMs = 10_000;                                      // 100ns 틱(허브 시계 = DateTime.UtcNow.Ticks)

        private readonly long _spikeNanos, _degradedTicks, _recoverTicks;
        private long _lastSpikeTicks, _lastPublishTicks = long.MinValue;
        private long _degradations, _recoveries, _suppressed, _spikeObservations;

        public LatencySpikeDegradationGuard(long spikeNanos = DefaultSpikeNanos, int normalPeriodMs = DefaultNormalPeriodMs,
                                            int degradedPeriodMs = DefaultDegradedPeriodMs, int recoverHoldMs = DefaultRecoverHoldMs)
        {
            if (spikeNanos <= 0) throw new ArgumentOutOfRangeException(nameof(spikeNanos));
            if (normalPeriodMs <= 0 || degradedPeriodMs <= normalPeriodMs) throw new ArgumentOutOfRangeException(nameof(degradedPeriodMs), "감속 주기 > 정상 주기 > 0");
            if (recoverHoldMs < degradedPeriodMs) throw new ArgumentOutOfRangeException(nameof(recoverHoldMs), "복귀 유지 ≥ 감속 주기");
            _spikeNanos = spikeNanos;
            NormalPeriodMs = normalPeriodMs; DegradedPeriodMs = degradedPeriodMs;
            _degradedTicks = degradedPeriodMs * TicksPerMs; _recoverTicks = recoverHoldMs * TicksPerMs;
        }

        public long SpikeNanos => _spikeNanos;
        public int NormalPeriodMs { get; }
        public int DegradedPeriodMs { get; }
        public bool Degraded { get; private set; }
        public int PeriodMs => Degraded ? DegradedPeriodMs : NormalPeriodMs;
        public long Degradations => _degradations;
        public long Recoveries => _recoveries;
        /// <summary>감속 중 게시가 보류된 펌프 회차 수.</summary>
        public long Suppressed => _suppressed;
        public long SpikeObservations => _spikeObservations;

        /// <summary>
        /// 펌프 1회차 관측. fresh = 이번 회차에 최근 창이 갱신됨(새 부모 완료) · p99Nanos = 창 p99(시그널 → 발주) · samples = 창 표본 수.
        /// 반환 = 이번 관측으로 모드가 바뀌었는가(호출측이 타이머 주기를 바꾼다).
        /// </summary>
        public bool Observe(bool fresh, long p99Nanos, long samples, long nowTicks)
        {
            if (fresh && samples > 0 && p99Nanos > _spikeNanos)
            {
                _spikeObservations++;
                _lastSpikeTicks = nowTicks;
                if (!Degraded) { Degraded = true; _degradations++; return true; }
                return false;
            }
            if (Degraded && nowTicks - _lastSpikeTicks >= _recoverTicks) { Degraded = false; _recoveries++; return true; }
            return false;
        }

        /// <summary>게시 허가(정상 = 항상 · 감속 = 마지막 게시 + 감속 주기 경과). 미허가 = Suppressed 계수.</summary>
        public bool AllowPublish(long nowTicks)
        {
            if (!Degraded || _lastPublishTicks == long.MinValue || nowTicks - _lastPublishTicks >= _degradedTicks) return true;
            _suppressed++;
            return false;
        }

        public void MarkPublished(long nowTicks) => _lastPublishTicks = nowTicks;
    }
}
