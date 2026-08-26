// ============================================================================
// TossLiveApiGovernor — v10.2.1: Live REST 통신 3중 방어 거버너 (Think Beyond Gemini)
//
//   ★제미나이의 '토큰 자동 갱신(아이디어 A)'은 이미 TossTokenService(락-프리 핫패스·백그라운드 선제 갱신)가
//   해결한다. 본 거버너는 제미나이 제안을 초월해, 실 증권사 REST 통신의 '나머지 파멸 리스크'인 (2)초당 호출 제한
//   초과 시 서버 IP 차단(HTTP 429 Rate-Limit Lockout)을 영구 격리하는 **3중 방어 엔진**을 창안한다:
//     ① Token-Bucket Rate Limiter — 초당 허용 횟수를 정수 고정소수로 리필/소비(δ=0). 버스트 캡.
//     ② 429 Adaptive Lockout — 서버가 429 를 주면 Retry-After 만큼 '완전 봉쇄'해 IP 차단을 원천 회피.
//     ③ Circuit Breaker — 연속 실패 N회 → 회로 개방(쿨다운 봉쇄) → half-open 단일 탐침 → 성공 시 폐쇄.
//   세 방어가 하나의 TryAcquire() O(1) 게이트로 합류해, 어떤 상황에서도 '서버를 두드려 IP 를 태우는' 경로를 봉쇄한다.
//
//   ★불변: δ=0(TryAcquire/Note* 핫패스 힙 0 — 정수·Int128 값타입·lock[무할당]만, string/배열/박싱 0) · 결정론
//     (주입 clock ticks·시계/난수 0) · fail-closed(개방/봉쇄/버킷 고갈 → false = 요청 억제, '모호한 통과' 0) ·
//     liveArmed:false(순수 통신 게이트 — 발주 유발 0).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>거버너 회로 상태(관측).</summary>
    public enum GovernorCircuit : byte { Closed = 0, Open = 1, HalfOpen = 2 }

    public sealed class TossLiveApiGovernor
    {
        private const long Milli = 1000L;                       // 토큰 고정소수 스케일(밀리-토큰)
        private static readonly long TicksPerSecond = TimeSpan.TicksPerSecond;

        private readonly Func<long> _clock;                    // 현재 시각(ticks·100ns) — 주입 필수(결정론)
        private readonly long _capacityMilli;                  // 버킷 용량(밀리-토큰) = 버스트 최대
        private readonly long _refillMilliPerSecond;           // 초당 리필(밀리-토큰) = 허용 rate
        private readonly long _failThreshold;                  // 연속 실패 임계(초과 시 회로 개방)
        private readonly long _openCooldownTicks;              // 회로 개방 쿨다운
        private readonly object _gate = new();                 // lock = 무할당 원자 구획(δ=0)

        // 보호 상태(모두 _gate 하에서만 접근)
        private long _tokensMilli;
        private long _lastRefillTicks;
        private long _lockoutUntilTicks;                       // 429/회로개방 봉쇄 종료 시각
        private long _consecutiveFailures;
        private GovernorCircuit _circuit;

        // 텔레메트리(원자·관측 전용)
        private long _requests, _allowed, _rateBlocked, _lockoutBlocked, _circuitBlocked, _http429, _circuitOpens;

        /// <param name="clock">현재 시각(ticks·100ns) 공급 — 주입 필수.</param>
        /// <param name="maxPerSecond">초당 허용 요청 수(예: 토스 10). ≤0 → 1 로 보정.</param>
        /// <param name="burst">순간 버스트 허용(버킷 용량). ≤0 → maxPerSecond.</param>
        /// <param name="failThreshold">연속 실패 임계(초과 시 회로 개방). ≤0 → 5.</param>
        /// <param name="openCooldownSeconds">회로 개방 봉쇄 시간(초). ≤0 → 5.</param>
        public TossLiveApiGovernor(Func<long> clock, long maxPerSecond = 8, long burst = 0,
                                   long failThreshold = 5, long openCooldownSeconds = 5)
        {
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            long rate = maxPerSecond <= 0 ? 1 : maxPerSecond;
            _refillMilliPerSecond = rate * Milli;
            long cap = burst <= 0 ? rate : burst;
            _capacityMilli = cap * Milli;
            _failThreshold = failThreshold <= 0 ? 5 : failThreshold;
            _openCooldownTicks = (openCooldownSeconds <= 0 ? 5 : openCooldownSeconds) * TicksPerSecond;

            _tokensMilli = _capacityMilli;      // 초기 만충(콜드 스타트 즉시 1회 허용)
            _lastRefillTicks = _clock();
            _circuit = GovernorCircuit.Closed;
        }

        // ── 텔레메트리(관제) ──────────────────────────────────────────────────
        public long Requests       => Interlocked.Read(ref _requests);
        public long Allowed        => Interlocked.Read(ref _allowed);
        public long RateBlocked    => Interlocked.Read(ref _rateBlocked);
        public long LockoutBlocked => Interlocked.Read(ref _lockoutBlocked);
        public long CircuitBlocked => Interlocked.Read(ref _circuitBlocked);
        public long Http429        => Interlocked.Read(ref _http429);
        public long CircuitOpens   => Interlocked.Read(ref _circuitOpens);
        public GovernorCircuit Circuit { get { lock (_gate) return _circuit; } }

        /// <summary>
        /// ★요청 1건 허용 여부(δ=0·O(1)). false = 억제(rate 초과/429 봉쇄/회로 개방/버킷 고갈). fail-closed.
        /// 순서: (0)텔레메트리 (1)봉쇄창(429/회로개방) 검사 (2)회로 half-open 승격 (3)버킷 리필·소비.
        /// </summary>
        public bool TryAcquire()
        {
            long now = _clock();
            lock (_gate)
            {
                Interlocked.Increment(ref _requests);

                // (1) 봉쇄창(429 Retry-After 또는 회로 개방 쿨다운) — 완전 차단(IP 보호)
                if (now < _lockoutUntilTicks)
                {
                    if (_circuit == GovernorCircuit.Open) Interlocked.Increment(ref _circuitBlocked);
                    else Interlocked.Increment(ref _lockoutBlocked);
                    return false;
                }

                // (2) 봉쇄창 경과: 회로가 개방이었다면 half-open(단일 탐침 허용)으로 승격
                if (_circuit == GovernorCircuit.Open) _circuit = GovernorCircuit.HalfOpen;

                // (3) 토큰 버킷 리필(정수 고정소수·Int128 로 오버플로 차단) 후 1토큰 소비
                long elapsed = now - _lastRefillTicks;
                if (elapsed > 0)
                {
                    // refill = elapsed * refillMilliPerSecond / TicksPerSecond  (Int128 중간곱 — long 오버플로 방지)
                    long refill = (long)((Int128)elapsed * _refillMilliPerSecond / TicksPerSecond);
                    if (refill > 0)
                    {
                        long t = _tokensMilli + refill;
                        _tokensMilli = t > _capacityMilli ? _capacityMilli : t;   // 캡
                        _lastRefillTicks = now;
                    }
                    // refill==0(경과가 1밀리토큰 미만) 이면 _lastRefillTicks 유지 → 미세 경과 누적(리필 손실 0)
                }

                if (_tokensMilli >= Milli)
                {
                    _tokensMilli -= Milli;
                    Interlocked.Increment(ref _allowed);
                    return true;
                }
                Interlocked.Increment(ref _rateBlocked);
                return false;   // 버킷 고갈 → 억제(rate 초과 방지)
            }
        }

        /// <summary>요청 성공 통보 — 실패 스트릭 리셋·회로 폐쇄(half-open 탐침 성공 포함).</summary>
        public void NoteSuccess()
        {
            lock (_gate)
            {
                _consecutiveFailures = 0;
                _circuit = GovernorCircuit.Closed;
            }
        }

        /// <summary>요청 실패 통보 — 연속 실패 임계 초과 시 회로 개방(쿨다운 봉쇄). half-open 탐침 실패도 즉시 재개방.</summary>
        public void NoteFailure()
        {
            long now = _clock();
            lock (_gate)
            {
                _consecutiveFailures++;
                if (_circuit == GovernorCircuit.HalfOpen || _consecutiveFailures >= _failThreshold)
                {
                    if (_circuit != GovernorCircuit.Open) Interlocked.Increment(ref _circuitOpens);
                    _circuit = GovernorCircuit.Open;
                    long until = now + _openCooldownTicks;
                    if (until > _lockoutUntilTicks) _lockoutUntilTicks = until;
                }
            }
        }

        /// <summary>★HTTP 429 통보 — Retry-After(초) 만큼 완전 봉쇄(서버 IP 차단 회피). 값 없으면 쿨다운 기본.</summary>
        public void NoteRateLimited(long retryAfterSeconds)
        {
            long now = _clock();
            long addTicks;
            if (retryAfterSeconds <= 0) addTicks = _openCooldownTicks;
            else
            {
                // 오버플로 차단(Int128) — 거대 Retry-After 도 안전.
                Int128 t = (Int128)retryAfterSeconds * TicksPerSecond;
                addTicks = t > long.MaxValue / 2 ? long.MaxValue / 2 : (long)t;
            }
            lock (_gate)
            {
                Interlocked.Increment(ref _http429);
                long until = now + addTicks;
                if (until > _lockoutUntilTicks) _lockoutUntilTicks = until;   // 단조 연장(단축 금지)
            }
        }
    }
}
