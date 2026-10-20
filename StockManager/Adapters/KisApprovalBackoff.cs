// ============================================================================
// KisApprovalBackoff — [v12.18.31-DEV] KIS WebSocket 접속키(POST /oauth2/Approval) 발급 실패 → 지수 백오프 + 자격증명 거부 sticky 래치
//
//  문제(실측 · §99 VTS 옵트인 소크 · 사용자 보고): 접속키 발급 실패 시 고정 5초 간격 무한 재요청 → 1시간 682회 REST 호출(재시도 폭풍 · IP 차단 위험).
//  백오프: n 번째 연속 실패 → 기준 지연 = min(5s × 2^(n−1), 60s) (5 · 10 · 20 · 40 · 60 · 60 …) · 지터 = 기준 × [+12.5%, +25%] 무할당 의사난수
//          (SplitMix64 · 주입 시드). 지터 하한(+12.5%)이 상한 구간 최소 간격 67.5s 를 보장 → 1시간 발급 시도 ≤ 57회(시드 무관 · 결정론 상한).
//  래치: 자격증명 거부(KisApprovalRejectedException · HTTP 401/403) = 즉시 sticky 래치 → 발급 시도 0(수신 루프 종료 · 미연결 = 세션 인터락 차단 유지).
//        해제 = 프로세스 재시작(자격증명 교체 전제) — 자동 해제 · 재시도 없음(fail-closed).
//  성공 = 연속 실패 리셋 · 대기 창 해제.
//  δ=0: NoteFailure · NoteSuccess · RemainingMicros · BaseDelayMicros 는 long 연산 + Interlocked/Volatile 만. 대기(Task.Delay)는 실패당 1회 콜드 경로.
//  ★liveArmed:false 무관 — 접속키는 수신 전용 WebSocket 용(주문 송신 경로 0).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Adapters
{
    /// <summary>접속키 발급 실패 지수 백오프(5s × 2ⁿ · 60s 상한 · +12.5~25% 지터) + 자격증명 거부 sticky 래치(δ=0 판정 · 콜드 대기).</summary>
    public sealed class KisApprovalBackoff
    {
        public static readonly TimeSpan DefaultBase = TimeSpan.FromSeconds(5), DefaultCap = TimeSpan.FromSeconds(60);

        private readonly long _baseUs, _capUs;
        private readonly Func<long> _nowMicros;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly ulong _seed;
        private long _consecutive, _notBeforeUs, _rejected, _rng;
        private long _failures, _rejections, _lastDelayUs, _lastRejectStatus;

        /// <param name="baseDelay">1회 실패 기준 지연(기본 5s · &gt; 0).</param>
        /// <param name="cap">기준 지연 상한(기본 60s · ≥ baseDelay).</param>
        /// <param name="nowMicros">단조 시계(µs · 기본 Stopwatch).</param>
        /// <param name="delay">대기 함수(기본 Task.Delay · 테스트는 가상 시계 전진).</param>
        /// <param name="jitterSeed">지터 시드(결정론 테스트용).</param>
        public KisApprovalBackoff(TimeSpan? baseDelay = null, TimeSpan? cap = null, Func<long>? nowMicros = null,
                                  Func<TimeSpan, CancellationToken, Task>? delay = null, ulong jitterSeed = 0x9E37_79B9_7F4A_7C15UL)
        {
            _baseUs = (long)(baseDelay ?? DefaultBase).TotalMicroseconds;
            _capUs = (long)(cap ?? DefaultCap).TotalMicroseconds;
            if (_baseUs <= 0) throw new ArgumentOutOfRangeException(nameof(baseDelay));
            if (_capUs < _baseUs) throw new ArgumentOutOfRangeException(nameof(cap));
            _nowMicros = nowMicros ?? StopwatchMicros;
            _delay = delay ?? Task.Delay;
            _seed = jitterSeed;
            _notBeforeUs = long.MinValue;
        }

        /// <summary>자격증명 거부 래치(sticky) — 발급 시도 금지.</summary>
        public bool Rejected => Volatile.Read(ref _rejected) == 1;
        public long Consecutive => Interlocked.Read(ref _consecutive);
        public long Failures => Interlocked.Read(ref _failures);
        public long Rejections => Interlocked.Read(ref _rejections);
        public long LastDelayMicros => Interlocked.Read(ref _lastDelayUs);
        /// <summary>마지막 거부 HTTP 상태(0 = 없음).</summary>
        public int LastRejectStatus => (int)Interlocked.Read(ref _lastRejectStatus);

        /// <summary>n 번째 연속 실패의 기준 지연(지터 전 · µs) — base × 2^(n−1) · cap 상한(시프트 정수 연산 · δ=0).</summary>
        public long BaseDelayMicros(long n)
        {
            if (n <= 1) return _baseUs;
            if (n > 62) return _capUs;
            int shift = (int)(n - 1);
            return _baseUs > (_capUs >> shift) ? _capUs : Math.Min(_baseUs << shift, _capUs);
        }

        /// <summary>일시 실패 1건 — 연속 계수 증가 · 다음 발급 가능 시각 = 지금 + 기준 × (1 + [1/8, 1/4] 지터). 반환 = 적용 지연(µs).</summary>
        public long NoteFailure()
        {
            Interlocked.Increment(ref _failures);
            long n = Interlocked.Increment(ref _consecutive);
            long b = BaseDelayMicros(n);
            long lo = b >> 3, span = (b >> 2) - lo;                                  // +12.5% ~ +25%
            long jitter = lo + (long)(Mix(_seed + (ulong)Interlocked.Increment(ref _rng)) % (ulong)(span + 1));
            long d = b + jitter;
            Interlocked.Exchange(ref _lastDelayUs, d);
            Volatile.Write(ref _notBeforeUs, _nowMicros() + d);
            return d;
        }

        /// <summary>자격증명 거부(401/403) — sticky 래치(발급 시도 영구 금지 · 재시작 전까지).</summary>
        public void NoteRejected(int httpStatus)
        {
            Interlocked.Increment(ref _failures);
            Interlocked.Exchange(ref _lastRejectStatus, httpStatus);
            if (Interlocked.CompareExchange(ref _rejected, 1, 0) == 0) Interlocked.Increment(ref _rejections);
        }

        /// <summary>발급 성공 — 연속 실패 리셋 · 대기 창 해제(래치는 유지 — 래치 상태에선 발급 자체가 없음).</summary>
        public void NoteSuccess()
        {
            Interlocked.Exchange(ref _consecutive, 0);
            Volatile.Write(ref _notBeforeUs, long.MinValue);
        }

        /// <summary>다음 발급까지 남은 대기(µs · 0 = 즉시 가능).</summary>
        public long RemainingMicros()
        {
            long nb = Volatile.Read(ref _notBeforeUs), now = _nowMicros();
            return nb > now ? nb - now : 0;                                          // 비교 후 감산(해제 표지 long.MinValue − now 오버플로 방지)
        }

        /// <summary>남은 대기만큼 대기(콜드). 반환 false = 취소.</summary>
        public async Task<bool> WaitAsync(CancellationToken ct)
        {
            long us = RemainingMicros();
            if (us <= 0) return !ct.IsCancellationRequested;
            try { await _delay(TimeSpan.FromMicroseconds(us), ct).ConfigureAwait(false); return !ct.IsCancellationRequested; }
            catch (OperationCanceledException) { return false; }
        }

        private static long StopwatchMicros()
        {
            long t = Stopwatch.GetTimestamp(), f = Stopwatch.Frequency;
            return t / f * 1_000_000 + t % f * 1_000_000 / f;
        }

        private static ulong Mix(ulong z)                                            // SplitMix64 최종화(무할당)
        {
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }
}
