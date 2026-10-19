// ============================================================================
// BrokerBackoffController — [v12.18.21-DEV] 브로커 한도 초과 응답 → 적응형 지수 백오프 + 연속 3회 sticky 트래픽 차단 래치
//
//  입력: HTTP 429(KIS · REST) · 키움 OpenAPI 반환 코드 −308(주문 과부하) · −200(조회 과부하). 그 밖 응답 = 성공(연속 리셋).
//  백오프: n 번째 연속 초과 → 기준 지연 = min(100ms × 2^(n−1), 3,000ms) · 지터 = [0, 기준/4] 무할당 의사난수(SplitMix64 · 원자 카운터) ·
//          합계 3,000ms 상한. 차단 종료 시각 = max(기존, 지금 + 지연)(CAS · 겹친 초과가 차단을 앞당기지 않음).
//  래치: 연속 초과 ≥ 3 → sticky 차단(ArmingBlocked) + 경보 콜백 1회(콜드 · 예외 흡수). 해제 = TryRearm(물리 이중키 · CLAUDE.md §1).
//  ★재시도 없음: 이 형식은 '지금 보내도 되는가'만 답한다(TryEnter). 지연 동안의 요청은 호출측이 즉시 실패시킨다 — 대기열 · 자동 재송신 0
//   (CLAUDE.md §2 · 주문 중복 위험). 위험 감소 경로(취소)는 TryEnterRiskReducing — 백오프 창은 따르되 래치는 무시(노출 해소를 막지 않음).
//  δ=0: TryEnter · Note* · 판독은 Interlocked/Volatile 만(경보 콜백 호출 제외 — 래치 전이 1회 · 콜드). 결정론: 주입 시계 · 주입 시드.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>한도 초과 응답 → 지수 백오프(100ms × 2ⁿ · 3s 상한 · 지터) + 연속 3회 sticky 래치(δ=0 · 재시도 없음). 프록시 링크 소스 — Core 형식 무참조.</summary>
    public sealed class BrokerBackoffController
    {
        public const long InitialDelayMicros = 100_000, MaxDelayMicros = 3_000_000;
        public const int DefaultLatchThreshold = 3;
        public const int HttpTooManyRequests = 429;
        public const int KiwoomOrderOverload = -308, KiwoomQueryOverload = -200;

        private readonly Func<long> _nowTicks;
        private readonly long _freq;
        private readonly int _latchThreshold;
        private readonly ulong _seed;
        private readonly Action<long>? _onLatch;
        private long _consecutive, _blockedUntilTicks, _latched, _rng;
        private long _limitHits, _successes, _backoffDenied, _latchDenied, _latches, _lastDelayMicros;

        /// <param name="nowTicks">단조 시계(기본 Stopwatch.GetTimestamp).</param>
        /// <param name="tickFrequency">시계 주파수(기본 Stopwatch.Frequency).</param>
        /// <param name="latchThreshold">연속 초과 래치 임계(기본 3 · ≥ 1).</param>
        /// <param name="jitterSeed">지터 시드(결정론 테스트용).</param>
        /// <param name="onLatch">래치 경보(연속 초과 수) — 전이 1회 · 예외 흡수.</param>
        public BrokerBackoffController(Func<long>? nowTicks = null, long tickFrequency = 0, int latchThreshold = DefaultLatchThreshold,
                                       ulong jitterSeed = 0x9E37_79B9_7F4A_7C15UL, Action<long>? onLatch = null)
        {
            if (latchThreshold < 1) throw new ArgumentOutOfRangeException(nameof(latchThreshold));
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _latchThreshold = latchThreshold;
            _seed = jitterSeed;
            _onLatch = onLatch;
            _blockedUntilTicks = long.MinValue;
        }

        public bool Latched => Volatile.Read(ref _latched) == 1;
        public bool ArmingBlocked => Latched;
        public long Consecutive => Interlocked.Read(ref _consecutive);
        public long LimitHits => Interlocked.Read(ref _limitHits);
        public long Successes => Interlocked.Read(ref _successes);
        public long BackoffDenied => Interlocked.Read(ref _backoffDenied);
        public long LatchDenied => Interlocked.Read(ref _latchDenied);
        public long Latches => Interlocked.Read(ref _latches);
        public long LastDelayMicros => Interlocked.Read(ref _lastDelayMicros);

        /// <summary>남은 백오프(µs · 0 = 없음 · 관측).</summary>
        public long RemainingBackoffMicros()
        {
            long d = Volatile.Read(ref _blockedUntilTicks) - _nowTicks();
            return d <= 0 ? 0 : d / _freq * 1_000_000 + d % _freq * 1_000_000 / _freq;
        }

        /// <summary>한도 초과 응답 판별(HTTP 429 · 키움 −308/−200).</summary>
        public static bool IsRateLimitCode(int code) => code == HttpTooManyRequests || code == KiwoomOrderOverload || code == KiwoomQueryOverload;

        /// <summary>n 번째 연속 초과의 기준 지연(지터 전 · µs).</summary>
        public static long BaseDelayMicros(long n)
        {
            if (n <= 1) return InitialDelayMicros;
            if (n > 6) return MaxDelayMicros;                                 // 100 · 2^5 = 3,200 > 3,000
            return Math.Min(InitialDelayMicros << (int)(n - 1), MaxDelayMicros);
        }

        /// <summary>송신 허가(δ=0). false = 래치 또는 백오프 창 — 호출측 즉시 실패(대기 · 재시도 금지).</summary>
        public bool TryEnter()
        {
            if (Volatile.Read(ref _latched) == 1) { Interlocked.Increment(ref _latchDenied); return false; }
            return InWindowCheck();
        }

        /// <summary>위험 감소(취소) 송신 허가 — 백오프 창은 따르고 래치는 무시(노출 해소를 막지 않음).</summary>
        public bool TryEnterRiskReducing() => InWindowCheck();

        /// <summary>브로커 응답 1건 기록(δ=0). 429 · −308 · −200 = 초과 · 그 밖 = 성공(연속 리셋). 반환 = 초과였는가.</summary>
        public bool Note(int code)
        {
            if (!IsRateLimitCode(code)) { NoteSuccess(); return false; }
            NoteLimitExceeded();
            return true;
        }

        public void NoteSuccess()
        {
            Interlocked.Exchange(ref _consecutive, 0);
            Interlocked.Increment(ref _successes);
        }

        /// <summary>한도 초과 1건 — 지수 백오프 연장 · 임계 도달 시 sticky 래치.</summary>
        public void NoteLimitExceeded()
        {
            Interlocked.Increment(ref _limitHits);
            long n = Interlocked.Increment(ref _consecutive);
            long baseUs = BaseDelayMicros(n);
            long jitterUs = (long)(Mix(_seed + (ulong)Interlocked.Increment(ref _rng)) % (ulong)(baseUs / 4 + 1));
            long delayUs = Math.Min(baseUs + jitterUs, MaxDelayMicros);
            Interlocked.Exchange(ref _lastDelayMicros, delayUs);
            long until = _nowTicks() + delayUs / 1_000_000 * _freq + delayUs % 1_000_000 * _freq / 1_000_000;
            long cur = Volatile.Read(ref _blockedUntilTicks);
            while (until > cur)
            {
                long seen = Interlocked.CompareExchange(ref _blockedUntilTicks, until, cur);
                if (seen == cur) break;
                cur = seen;
            }
            if (n >= _latchThreshold && Interlocked.CompareExchange(ref _latched, 1, 0) == 0)
            {
                Interlocked.Increment(ref _latches);
                try { _onLatch?.Invoke(n); } catch { }                         // 경보(콜드) — 실패가 래치를 무르지 않음
            }
        }

        /// <summary>래치 해제 — 물리 이중키(Valve ∧ LiveArming) 모두 결선일 때만. 연속 계수 · 백오프 창도 초기화.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!valveWired || !liveArmingWired) return false;
            Interlocked.Exchange(ref _consecutive, 0);
            Volatile.Write(ref _blockedUntilTicks, long.MinValue);
            Volatile.Write(ref _latched, 0);
            return true;
        }

        private bool InWindowCheck()
        {
            if (_nowTicks() < Volatile.Read(ref _blockedUntilTicks)) { Interlocked.Increment(ref _backoffDenied); return false; }
            return true;
        }

        private static ulong Mix(ulong z)                                        // SplitMix64 최종화(무할당)
        {
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }
}
