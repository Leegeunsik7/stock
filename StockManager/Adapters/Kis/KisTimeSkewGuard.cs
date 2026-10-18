// ============================================================================
// KisTimeSkewGuard — [v12.18.16-DEV] KIS 시세 전문 시각 ↔ 호스트 벽시계 편차 감시 → 비트 28(OrderPathLatency) 래치(Fail-Closed · δ=0)
//
//  입력: Observe(스트림 · HHmmss) — KIS 수신 스레드(단일 작성자)가 판독 성공 전문마다 호출(호가 = BSOP_HOUR · 체결 = 레코드별 STCK_CNTG_HOUR).
//  측정(정직 — 전문 시각 = 초 해상도 · KST): 호스트 KST 자정 이후 ms = H, 전문 초 = S 이면 생성 시각 ∈ [S, S + 999ms] →
//    지연 하한 = H − (S×1000 + 999) · 상한 = H − S×1000. 자정 경계 = ±12시간 밖 차이를 하루 보정.
//  격발(먼저 충족 · 위반은 래치 중에도 전부 계수 — 미포착 누락 0):
//    ① 지연 하한 > 50ms(지연이 확실히 50ms 초과) ② 스트림별 전문 시각 역전(직전보다 이른 초) ③ 상한 < −50ms(전문이 호스트보다 50ms 초과 미래 = 시계 편차)
//    ④ 전문 시각 형식 이탈(판정 불가 = fail-closed).
//  래치: 격발 = 비트 28 Raise(OrderPathLatencyInterlock 2차 입력 — 비트 1 소스 1 링크 계약 · 인터락 정의 불변) · 자동 해제 없음.
//    해제 = TryRearm(물리 이중키) ∧ 격발 후 연속 정상 전문 ≥ RecoveryFrames(기본 100) — 회복 전 해제 불가.
//  ★한계(정직): 50ms < 실지연 ≤ 1,050ms 구간은 전문 초 내 위치에 따라 미포착될 수 있다(초 해상도 한계). 호스트 시계가 KIS 보다 앞서면
//    하한이 부풀어 격발(안전 방향) · 뒤처지면 실지연을 과소평가 — 호스트 NTP 동기가 전제. 시세 수신 전용 — 주문/취소 경로 참조 0.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Telemetry;

namespace StockManager.Adapters.Kis
{
    public sealed class KisTimeSkewGuard : IArmingBlockSource, IKisFeedLatencyPort
    {
        public const int StreamBook = 0, StreamTrade = 1, Streams = 2;
        public const long DefaultThresholdMs = 50;
        public const int DefaultRecoveryFrames = 100;
        private const long DayMs = 86_400_000, HalfDayMs = DayMs / 2, KstOffsetMs = 9 * 3_600_000L;

        private readonly Func<long> _utcTicks;
        private readonly long _thresholdMs;
        private readonly int _recoveryFrames;
        private readonly int[] _lastSec = { -1, -1 };
        private int _tripped, _reason;
        private long _frames, _violations, _trips, _lastViolation, _lagLower, _lagUpper, _maxLagLower = long.MinValue;
        private OrderPathLatencyInterlock? _bit28;

        /// <param name="utcTicks">호스트 벽시계(UTC ticks · 기본 DateTime.UtcNow — 고정밀 시스템 시각).</param>
        /// <param name="thresholdMs">지연/편차 임계(초과 = 격발).</param>
        /// <param name="recoveryFrames">재무장 전제 연속 정상 전문 수.</param>
        public KisTimeSkewGuard(Func<long>? utcTicks = null, long thresholdMs = DefaultThresholdMs, int recoveryFrames = DefaultRecoveryFrames)
        {
            if (thresholdMs <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdMs));
            if (recoveryFrames < 1) throw new ArgumentOutOfRangeException(nameof(recoveryFrames));
            _utcTicks = utcTicks ?? (static () => DateTime.UtcNow.Ticks);
            _thresholdMs = thresholdMs; _recoveryFrames = recoveryFrames;
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public int LastTripReason => Volatile.Read(ref _reason);
        public long Frames => Interlocked.Read(ref _frames);
        public long Violations => Interlocked.Read(ref _violations);
        public long Trips => Interlocked.Read(ref _trips);
        /// <summary>격발 중 마지막 위반 이후 연속 정상 전문 수(미격발 = 0).</summary>
        public long HealthyStreak => Tripped ? Interlocked.Read(ref _frames) - Interlocked.Read(ref _lastViolation) : 0;
        public long LagLowerMs => Interlocked.Read(ref _lagLower);
        public long LagUpperMs => Interlocked.Read(ref _lagUpper);
        public long ThresholdMs => _thresholdMs;

        /// <summary>비트 28 결속 — OrderPathLatencyInterlock 의 2차 차단 입력으로 붙는다(비트 재결속 0 · 콜드 1회).</summary>
        public void BindLatencyInterlock(OrderPathLatencyInterlock interlock)
        {
            ArgumentNullException.ThrowIfNull(interlock);
            interlock.AttachFeedGuard(this);
            Volatile.Write(ref _bit28, interlock);
        }

        /// <summary>전문 1건 판정(δ=0 · 단일 작성자). 반환 = 이번 판정으로 격발했는가.</summary>
        public bool Observe(int stream, long hhmmss)
        {
            long n = Interlocked.Increment(ref _frames);
            if (!KisWebSocketOrderbookParser.TryHhmmssToSeconds(hhmmss, out int sec)) return Violate(KisSkewReason.BadTimestamp, n);
            long hostMs = (_utcTicks() / TimeSpan.TicksPerMillisecond + KstOffsetMs) % DayMs;
            long upper = hostMs - sec * 1_000L;
            if (upper < -HalfDayMs) upper += DayMs; else if (upper > HalfDayMs) upper -= DayMs;
            long lower = upper - 999;
            Interlocked.Exchange(ref _lagLower, lower);
            Interlocked.Exchange(ref _lagUpper, upper);
            if (lower > _maxLagLower) Interlocked.Exchange(ref _maxLagLower, lower);
            int s = (uint)stream < Streams ? stream : StreamTrade;
            int last = _lastSec[s];
            _lastSec[s] = sec;
            if (last >= 0 && sec < last && last - sec < HalfDayMs / 1_000) return Violate(KisSkewReason.Inversion, n);
            if (lower > _thresholdMs) return Violate(KisSkewReason.Latency, n);
            if (upper < -_thresholdMs) return Violate(KisSkewReason.ClockAhead, n);
            return false;
        }

        // 위반 표지(전문 순번) 기록 '후' 격발 CAS — 재무장과 경합해도 위반이 사라지지 않는다(TryRearm 재확인 참조)
        private bool Violate(int reason, long frame)
        {
            Interlocked.Increment(ref _violations);
            Interlocked.Exchange(ref _lastViolation, frame);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return false;
            Volatile.Write(ref _reason, reason);
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _bit28)?.Resync();                                    // ★비트 28 Raise — 무장 차단(liveArmed:false)
            return true;
        }

        /// <summary>
        /// 재무장(물리 이중키 재확인 + 회복 표본 충족 필수). 반환 = 해제(또는 원래 미격발). 회복 전 = false(래치 유지).
        /// </summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            if (Volatile.Read(ref _tripped) == 0) return true;
            long lv = Interlocked.Read(ref _lastViolation);
            if (Interlocked.Read(ref _frames) - lv < _recoveryFrames) return false;
            if (Interlocked.CompareExchange(ref _tripped, 0, 1) != 1) return true;
            // 해제 직전/직후 끼어든 위반: 표지가 바뀌었으면 즉시 재래치(위반 측 CAS 가 먼저 실패했어도 여기서 복원 — 위반 유실 0)
            if (Interlocked.Read(ref _lastViolation) != lv)
            {
                Interlocked.CompareExchange(ref _tripped, 1, 0);
                Volatile.Read(ref _bit28)?.Resync();
                return false;
            }
            Volatile.Read(ref _bit28)?.Resync();
            return true;
        }

        public KisFeedLatencyStatus Status
        {
            get
            {
                long frames = Interlocked.Read(ref _frames);
                int state = Tripped ? DashKisFeed.Latched : frames == 0 ? DashKisFeed.Waiting : DashKisFeed.Ok;
                long max = Interlocked.Read(ref _maxLagLower);
                return new KisFeedLatencyStatus(state, LagLowerMs, LagUpperMs, max == long.MinValue ? 0 : max, frames, Violations, Trips,
                                                LastTripReason, HealthyStreak, _recoveryFrames);
            }
        }
    }
}
