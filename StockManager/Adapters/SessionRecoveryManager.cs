// ============================================================================
// SessionRecoveryManager — [세션 자동 복구 · 추천 A] 브로커 세션 단절 감지 · 지수 백오프 재접속 · 재구독 후 미체결 대사 결속 · 소진 격리
//
//  문제(실측 · v12.16.1-GM):
//   ① 침묵 단절(half-open) 미복구 — KisSessionInterlock.Evaluate 가 장중 3초 침묵에 Disarm 비트를 올리지만 소켓은 아무도 닫지 않는다.
//      KisRealtimeSession 은 죽은 연결의 ReceiveAsync 를 계속 기다려(TCP 가 알아챌 때까지 수 분~무한) 재접속 · 재구독이 일어나지 않았다.
//   ② 재접속 = 고정 5초 무한 반복(백오프 · 소진 판정 없음).
//   ③ 재접속 직후 '단절 동안의 미체결 원장'을 대조하는 주체 없음 — 대사기의 확정 비트는 한 번 서면 재접속 후에도 남아 있었다.
//
//  상태기계(브로커 1개당 1 인스턴스 · 전이 = Monitor 락):
//    Connecting ─(OnConnected · 최초)→ Connected
//    Connected ─(단절 보고 · 침묵 강제 종료 · 링크 프로브 거짓)→ Backoff  [격리 ON: 한도 0 · 블랙리스트]
//    Backoff ─(Pump: 기한 도래)→ Connecting  [재접속 허가 신호]
//    Connecting ─(OnAttemptFailed)→ Backoff(다음 지연 1s·2s·4s·8s·16s 상한) · 재접속 연속 실패 ≥ MaxFailures → Exhausted 래치
//    Connecting ─(OnConnected · 재접속)→ Reconciling  [재구독은 세션이 송신 · 대사 확정 철회 + 즉시 조회를 '먼저' 끝낸 뒤 전이]
//    Reconciling ─(Pump: 요청 이후 대사 확정)→ Connected  [격리 OFF(소진 래치 아니면) · 연속 실패 0]
//    Reconciling ─(단절)→ Backoff(★연속 실패 +1 — 대사 전 재단절 반복(플래핑)도 소진으로 수렴)
//   수동 모드(autoReconnect=false · 키움): 재접속을 시도하지 않는다 — 링크 프로브가 참으로 돌아오면(운용자 재기동) Reconciling.
//
//  ★격리 작동은 상태 락 '안'에서 현재 상태를 그대로 반영한다(desired = 세션 수립 이력 ∧ Held). 락 밖에서 하면 '해제(false)'와
//    동시 단절의 '격리(true)'가 뒤집힌 순서로 적용돼 단절된 브로커가 한 펌프 주기 동안 열릴 수 있다(설계 검토 중 실측 결함).
//    → isolate 델리게이트는 비차단(원자 비트 연산)이어야 하며 관리자를 다시 호출하면 안 된다. 강제 종료 · 대사 요청 · 소진 통지는 락 밖.
//
//  [방향 A] 무할당 백오프 타이머 펌프: 재시도마다 Timer/Task.Delay 를 만들지 않는다. 기존 50ms 평가 타이머가 Pump() 로 단조 시각을
//    스캔해 기한 도래 시 재사용 대기 원천(ManualResetValueTaskSourceCore — 세션 루프의 await 대상)을 완료한다. 기한 · 연속 실패 · 지연은
//    정수 필드(시프트) — Pump · 전이 · 지연 계산 · 동기 허가 = 0B(테스트 실측).
//  [방향 B] 소진 서킷브레이커: 재접속 연속 MaxFailures(기본 5)회 실패 = Exhausted 래치(sticky) → onExhausted(호스트: 자식 매트릭스
//    IsolateBroker — 미송출 회수 · 송출분 취소 · 비트 23 sticky Disarm) · 이후 대사가 확정돼도 격리 유지. 해제 = 물리 이중키(TryReset).
//    재접속 시도 자체는 16초 간격으로 계속(체결 통보 · 대사 가시성 유지 — 격리와 별개).
//
//  ★Zero Stale Order: Held = Connected 가 아닌 모든 상태 ∨ Exhausted. 격리는 세션이 한 번이라도 수립된 뒤부터 작동(최초 미연결은 세션
//    인터락 !Connected 가 차단 · 블랙리스트는 이중키 재무장으로만 지워지므로 '장 전 미접속' 브로커를 영구 배제하지 않기 위함).
//    Pump 마다 격리 재주장(멱등 — 재무장이 블랙리스트를 지워도 다시 세운다) · KisSessionInterlock 재무장 거부(SetRearmHold). 큐잉 · 재송출 0.
//  ★정직(δ=0 범위): 소켓 재접속 자체(ClientWebSocket · 업그레이드 · 구독 JSON)는 BCL 이 할당한다 — 0B 보증 = 감지 · 백오프 · 전이 ·
//    격리 재주장 · 동기 허가. 비동기 허가(대기자 깨움)는 스레드 풀 큐잉(재접속 시도당 1회)이 할당할 수 있다.
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace StockManager.Adapters
{
    public enum SessionRecoveryState : byte { Connecting = 0, Connected = 1, Backoff = 2, Reconciling = 3 }

    /// <summary>[v12.18.7-DEV] 관제 사본(락 1회 일관 판독 · 값 형식 · 할당 0) — 상태 · 래치 · 연속 실패 · 누적 계수.</summary>
    public readonly record struct SessionRecoverySnapshot(SessionRecoveryState State, bool EverConnected, bool Exhausted, bool Discrepancy, bool Isolated,
                                                          int ConsecutiveFailures, int MaxFailures, long Losses, long Recoveries, long Attempts);

    public sealed class SessionRecoveryManager : IValueTaskSource<bool>
    {
        public const int DefaultMaxFailures = 5;
        public static readonly TimeSpan DefaultBaseBackoff = TimeSpan.FromSeconds(1), DefaultMaxBackoff = TimeSpan.FromSeconds(16);

        private readonly int _broker, _maxFailures;
        private readonly long _baseTicks, _maxTicks;
        private readonly bool _auto;
        private readonly Func<long> _nowTicks;
        private readonly Action<bool>? _isolate;
        private readonly Action? _requestReconcile;
        private readonly Func<bool>? _reconciled, _silent, _linkProbe;
        private readonly Action? _onExhausted;
        private Action? _abort;

        private readonly object _gate = new();
        private SessionRecoveryState _state = SessionRecoveryState.Connecting;
        private bool _everConnected, _exhausted, _attemptDue, _waiting, _shutdown, _isolated, _discrepancy;
        private int _failures, _pumping;
        private long _nextAttemptTicks;
        private long _losses, _attempts, _failuresTotal, _recoveries, _exhaustions, _silenceAborts, _reasserts, _isolations, _releases;
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = true };   // 재접속을 펌프 타이머 스레드에서 돌리지 않는다

        /// <param name="broker">브로커 인덱스(라우터 스위치보드 · 한도 피드와 동일).</param>
        /// <param name="isolate">격리 ON/OFF(호스트: 한도 피드 세션 격리 + ON 이면 스위치보드 블랙리스트). ★상태 락 안에서 호출 — 비차단 · 재진입 금지.</param>
        /// <param name="requestReconcile">재접속 직후 1회 — 미체결 대사 확정 철회 + 즉시 조회(호스트: BrokerOpenOrderReconciler.RequestReconcile).</param>
        /// <param name="reconciled">요청 이후 그 브로커 대사가 확정됐는가(호스트: IsConfirmed). null = 대사 수단 없음 → 격리 유지(fail-closed).</param>
        /// <param name="silent">연결 중 침묵 판정(호스트: KisSessionInterlock.SilentNow) — 참 = 소켓 강제 종료 후 재접속.</param>
        /// <param name="linkProbe">수동 모드 링크 생존 프로브(키움: 브리지 실 프록시 링크). 자동 모드는 세션 이벤트로 구동(null).</param>
        public SessionRecoveryManager(int broker, Action<bool>? isolate, Action? requestReconcile, Func<bool>? reconciled,
                                      Func<bool>? silent = null, Func<bool>? linkProbe = null, Action? onExhausted = null,
                                      bool autoReconnect = true, int maxFailures = DefaultMaxFailures,
                                      TimeSpan? baseBackoff = null, TimeSpan? maxBackoff = null, Func<long>? nowTicks = null)
        {
            if (broker < 0 || broker > 63) throw new ArgumentOutOfRangeException(nameof(broker));
            if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
            _baseTicks = (baseBackoff ?? DefaultBaseBackoff).Ticks; _maxTicks = (maxBackoff ?? DefaultMaxBackoff).Ticks;
            if (_baseTicks <= 0 || _maxTicks < _baseTicks) throw new ArgumentOutOfRangeException(nameof(baseBackoff));
            if (!autoReconnect && linkProbe is null) throw new ArgumentException("수동 모드는 링크 프로브 필요", nameof(linkProbe));
            _broker = broker; _maxFailures = maxFailures; _auto = autoReconnect;
            _isolate = isolate; _requestReconcile = requestReconcile; _reconciled = reconciled; _silent = silent; _linkProbe = linkProbe;
            _onExhausted = onExhausted;
            _nowTicks = nowTicks ?? MonotonicTicks;
        }

        /// <summary>단조 시각(ms 해상도 · TimeSpan 틱) — 벽시계 보정 · 오버플로 무관 · δ=0.</summary>
        public static long MonotonicTicks() => Environment.TickCount64 * TimeSpan.TicksPerMillisecond;

        // ── 관측 ──
        public int Broker => _broker;
        public bool AutoReconnect => _auto;
        public SessionRecoveryState State { get { lock (_gate) return _state; } }
        public bool Exhausted { get { lock (_gate) return _exhausted; } }
        /// <summary>격리 대상(신규 발주 불가) — Connected 가 아니거나 소진 래치.</summary>
        public bool Held { get { lock (_gate) return HeldLocked; } }
        private bool HeldLocked => _state != SessionRecoveryState.Connected || _exhausted || _discrepancy;
        /// <summary>[KIS 미체결 대사] 원장 불일치/유령 래치(sticky — 이중키 TryReset 전 격리 유지).</summary>
        public bool Discrepancy { get { lock (_gate) return _discrepancy; } }
        private long _discrepancies, _pushedReleases;
        public long Discrepancies => Interlocked.Read(ref _discrepancies);
        public long PushedReleases => Interlocked.Read(ref _pushedReleases);

        /// <summary>
        /// [KIS 미체결 대사] 대사기 패스 확정 통지(대사기 onPass · 같은 스레드) — 펌프 주기(≤50ms)를 기다리지 않고 락 안에서 즉시 해제.
        /// ★확정 프로브를 락 안에서 다시 확인한다(통지 자체를 믿지 않음): 재접속 '후' RequestReconcile 이 철회한 확정이 새 패스로 다시 섰을 때만 참.
        /// Reconciling 이 아니면 무동작(최초 접속 · 평시 주기 패스).
        /// </summary>
        public void OnReconciliationCompleted()
        {
            lock (_gate)
            {
                if (_shutdown || _state != SessionRecoveryState.Reconciling || !Probe(_reconciled)) return;
                _state = SessionRecoveryState.Connected;
                _failures = 0;
                Interlocked.Increment(ref _recoveries);
                Interlocked.Increment(ref _pushedReleases);
                ActuateLocked();
            }
        }

        /// <summary>
        /// [KIS 미체결 대사 · 방향 B] 원장 불일치(수량 · 가격 · 방향) 또는 유령 주문 — 그 브로커 영구 격리 래치(상태 무관 · 대사가 다시 확정돼도
        /// 유지). 해제 = 물리 이중키 TryReset. 격리 작동은 세션 수립 이력과 무관하게 즉시(불일치는 '이미 노출'의 증거).
        /// </summary>
        public void OnDiscrepancy()
        {
            lock (_gate)
            {
                if (!_discrepancy) Interlocked.Increment(ref _discrepancies);
                _discrepancy = true;
                ActuateLocked();
            }
        }
        /// <summary>현재 격리 작동 중(한도 0 · 블랙리스트) — 세션 수립 이력 ∧ Held.</summary>
        public bool Isolated { get { lock (_gate) return _isolated; } }
        public int ConsecutiveFailures { get { lock (_gate) return _failures; } }
        public long NextAttemptTicks { get { lock (_gate) return _nextAttemptTicks; } }
        public long Losses => Interlocked.Read(ref _losses);
        public long Attempts => Interlocked.Read(ref _attempts);
        public long FailuresTotal => Interlocked.Read(ref _failuresTotal);
        public long Recoveries => Interlocked.Read(ref _recoveries);
        public long Exhaustions => Interlocked.Read(ref _exhaustions);
        public long SilenceAborts => Interlocked.Read(ref _silenceAborts);
        public long IsolationReasserts => Interlocked.Read(ref _reasserts);
        public long Isolations => Interlocked.Read(ref _isolations);
        public long Releases => Interlocked.Read(ref _releases);
        public int MaxFailures => _maxFailures;

        /// <summary>[v12.18.7-DEV] 관제 사본 — 상태 · 래치 · 연속 실패를 같은 락 1회로 판독(찢김 0 · 할당 0 · 대시보드 펌프용).</summary>
        public SessionRecoverySnapshot Snapshot()
        {
            lock (_gate)
                return new SessionRecoverySnapshot(_state, _everConnected, _exhausted, _discrepancy, _isolated, _failures, _maxFailures,
                                                   Interlocked.Read(ref _losses), Interlocked.Read(ref _recoveries), Interlocked.Read(ref _attempts));
        }

        /// <summary>연속 실패 n 회 뒤의 대기(1s · 2s · 4s · 8s · 16s 상한) — 시프트 정수 연산 · δ=0.</summary>
        public long BackoffTicksFor(int failures)
        {
            int shift = Math.Min(Math.Max(failures, 0), 30);
            long t = _baseTicks << shift;
            return t <= 0 || t > _maxTicks ? _maxTicks : t;
        }

        /// <summary>세션의 강제 종료 동작(침묵 단절 시 죽은 소켓을 닫아 재접속 경로로) — 세션 생성 후 1회 결속.</summary>
        public void BindAbort(Action abortConnection) => Volatile.Write(ref _abort, abortConnection ?? throw new ArgumentNullException(nameof(abortConnection)));

        // ── 격리 작동(락 안 · 상태 반영) ──

        private void ActuateLocked()
        {
            bool want = (_everConnected && HeldLocked) || _discrepancy;
            if (want)
            {
                if (!_isolated) Interlocked.Increment(ref _isolations); else Interlocked.Increment(ref _reasserts);
                _isolated = true;
                try { _isolate?.Invoke(true); } catch (Exception) { }                 // 매번 재주장(멱등) — 재무장의 블랙리스트 소거 복원
            }
            else if (_isolated)
            {
                _isolated = false;
                Interlocked.Increment(ref _releases);
                try { _isolate?.Invoke(false); } catch (Exception) { }
            }
        }

        // ── 세션 이벤트(자동 모드 · 세션 루프 단일 스레드) ──

        /// <summary>접속 + 재구독 송신 완료. 최초 = Connected · 재접속 = 대사 요청 후 Reconciling(격리 유지).</summary>
        public void OnConnected()
        {
            bool reconcile;
            lock (_gate)
            {
                if (_shutdown) return;
                reconcile = _everConnected;
                if (!reconcile) { _everConnected = true; _state = SessionRecoveryState.Connected; _failures = 0; ActuateLocked(); }
            }
            if (!reconcile) return;
            // ★순서: 대사 확정 철회(요청)를 '먼저' 끝낸 뒤 Reconciling 으로 — 반대 순서면 그 사이 펌프가 단절 '전'의 확정을 읽고
            //   격리를 풀 수 있다(오대사 발주 창). 요청 중 상태는 Connecting(Held) 그대로라 펌프가 풀지 못한다.
            RequestReconcile();
            lock (_gate) { if (!_shutdown) { _state = SessionRecoveryState.Reconciling; ActuateLocked(); } }
        }

        /// <summary>연결 수립 후 단절(수신 종료 · 예외 · 강제 종료). 이미 Backoff 면 무시(멱등).</summary>
        public void OnConnectionLost()
        {
            bool exhaustedNow = false;
            lock (_gate)
            {
                if (_shutdown || _state == SessionRecoveryState.Backoff) return;
                Interlocked.Increment(ref _losses);
                if (_state == SessionRecoveryState.Reconciling || _state == SessionRecoveryState.Connecting) exhaustedNow = CountFailureLocked();   // 대사 전 재단절(플래핑)
                ScheduleLocked();
                ActuateLocked();
            }
            if (exhaustedNow) FireExhausted();
        }

        /// <summary>접속 시도 실패(접속키 · 소켓 · 구독 송신). 최초 접속 전 실패는 백오프만(소진 래치 · 격리 제외).</summary>
        public void OnAttemptFailed()
        {
            bool exhaustedNow;
            lock (_gate)
            {
                if (_shutdown) return;
                if (_everConnected) { exhaustedNow = CountFailureLocked(); ScheduleLocked(); }
                else
                {
                    // 최초 접속 전: 첫 실패 = 1s 부터(재접속 경로의 '단절 직후 1s' 와 같은 수열) · 소진 계수 없음
                    exhaustedNow = false;
                    ScheduleLocked();
                    _failures = Math.Min(_failures + 1, 30);
                }
                ActuateLocked();
            }
            if (exhaustedNow) FireExhausted();
        }

        private bool CountFailureLocked()
        {
            Interlocked.Increment(ref _failuresTotal);
            _failures = Math.Min(_failures + 1, 30);
            if (_exhausted || _failures < _maxFailures) return false;
            _exhausted = true;
            Interlocked.Increment(ref _exhaustions);
            return true;
        }

        private void ScheduleLocked()
        {
            _state = SessionRecoveryState.Backoff;
            // 단절 직후 첫 시도 = 1s(실패 0) · 이후 연속 실패 수만큼 배증 · 16s 상한
            _nextAttemptTicks = _nowTicks() + BackoffTicksFor(_failures);
            _attemptDue = false;
        }

        // ── [방향 A] 무할당 재접속 허가(세션 루프의 await 대상) ──

        /// <summary>다음 재접속 허가까지 대기. true = 시도 · false = 종료(Shutdown). 허가가 이미 났으면 동기 완료(0B).</summary>
        public ValueTask<bool> WaitForAttemptAsync()
        {
            lock (_gate)
            {
                if (_shutdown) return new ValueTask<bool>(false);
                if (_attemptDue || _state == SessionRecoveryState.Connecting) { _attemptDue = false; _state = SessionRecoveryState.Connecting; return new ValueTask<bool>(true); }
                if (_waiting) throw new InvalidOperationException("재접속 대기자는 1개(세션 루프)만");
                _core.Reset();
                _waiting = true;
                return new ValueTask<bool>(this, _core.Version);
            }
        }

        /// <summary>종료 — 대기 중인 세션 루프를 false 로 깨운다(세션이 취소 토큰 등록 1회로 결속).</summary>
        public void Shutdown()
        {
            bool wake;
            lock (_gate) { _shutdown = true; wake = _waiting; _waiting = false; }
            if (wake) _core.SetResult(false);
        }

        // ── 펌프(50ms 평가 타이머 · δ=0) ──

        /// <summary>단조 시각 스캔: 침묵 → 강제 종료 · 링크 프로브 전이 · 백오프 기한 → 허가 · 대사 확정 → 격리 해제 · 격리 재주장.</summary>
        public void Pump()
        {
            if (Interlocked.Exchange(ref _pumping, 1) == 1) return;                     // 타이머 콜백 중첩 방지(단일 진입)
            try { PumpCore(); }
            finally { Volatile.Write(ref _pumping, 0); }
        }

        private void PumpCore()
        {
            long now = _nowTicks();
            bool abort = false, wake = false, reconcile = false;
            SessionRecoveryState s;
            lock (_gate)
            {
                if (_shutdown) return;
                s = _state;
            }
            // 1) 침묵(half-open) — 연결 상태에서 프로브 참 = 소켓 강제 종료(세션 finally → OnConnectionLost → 격리 · 백오프)
            if (_auto && (s == SessionRecoveryState.Connected || s == SessionRecoveryState.Reconciling) && Probe(_silent))
            {
                abort = true;
                Interlocked.Increment(ref _silenceAborts);
            }
            bool up = !_auto && Probe(_linkProbe);
            lock (_gate)
            {
                if (_shutdown) return;
                // 2) 수동 모드 링크 프로브(키움): 거짓 = 단절(재접속 없음) · 참 복귀 = 대사
                if (!_auto)
                {
                    if ((_state == SessionRecoveryState.Connected || _state == SessionRecoveryState.Reconciling) && !up)
                    {
                        Interlocked.Increment(ref _losses);
                        _state = SessionRecoveryState.Backoff; _nextAttemptTicks = long.MaxValue;
                    }
                    else if ((_state == SessionRecoveryState.Backoff || _state == SessionRecoveryState.Connecting) && up)
                    {
                        if (_everConnected) reconcile = true;                           // 대사 요청 후 Reconciling(아래 락에서)
                        else { _everConnected = true; _state = SessionRecoveryState.Connected; }
                    }
                }
                // 3) 백오프 기한 도래 → 허가(대기자 있으면 깨움 · 없으면 다음 WaitForAttemptAsync 가 동기 통과)
                if (_auto && _state == SessionRecoveryState.Backoff && now >= _nextAttemptTicks)
                {
                    _state = SessionRecoveryState.Connecting;
                    Interlocked.Increment(ref _attempts);
                    if (_waiting) { _waiting = false; wake = true; } else _attemptDue = true;
                }
                // 4) 재접속 후 대사 확정 → Connected(소진 래치면 격리 유지)
                if (_state == SessionRecoveryState.Reconciling && Probe(_reconciled))
                {
                    _state = SessionRecoveryState.Connected;
                    _failures = 0;
                    Interlocked.Increment(ref _recoveries);
                }
                ActuateLocked();                                                       // 상태 반영(격리 재주장 · 해제)
            }
            if (abort) { try { Volatile.Read(ref _abort)?.Invoke(); } catch (Exception) { } }
            if (reconcile)
            {
                RequestReconcile();                                                    // 확정 철회 '후' Reconciling(OnConnected 와 같은 순서)
                lock (_gate) { if (!_shutdown && _state != SessionRecoveryState.Connected) { _state = SessionRecoveryState.Reconciling; ActuateLocked(); } }
            }
            if (wake) _core.SetResult(true);
        }

        /// <summary>[방향 B] 소진 래치 해제 — 물리 이중키. 격리 해제는 Connected(대사 확정) 일 때만.</summary>
        public bool TryReset(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            lock (_gate)
            {
                _exhausted = false; _discrepancy = false; _failures = 0;
                ActuateLocked();
            }
            return true;
        }

        private static bool Probe(Func<bool>? f)
        {
            if (f is null) return false;                                               // 수단 없음 = 거짓(대사 미확정 · 링크 단절 · 침묵 아님)
            try { return f(); } catch (Exception) { return false; }
        }

        private void RequestReconcile() { try { _requestReconcile?.Invoke(); } catch (Exception) { } }
        private void FireExhausted() { try { _onExhausted?.Invoke(); } catch (Exception) { } }

        // ── IValueTaskSource<bool> ──
        bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);
        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);
        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
