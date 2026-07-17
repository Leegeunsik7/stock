// ============================================================================
// LiveSessionWatchdog — v5.1.0 과제1: 실시간 세션 하트비트 감시견
//
//  실 토스/키움 실전 라인의 20ms 핑퐁 텔레메트리(KiwoomBridgeClient.CurrentIpcRttTicks·pingIntervalMs=20 주입)를 폴해,
//  네트워크 단선·브로커 프리징(Hang)으로 세션 왕복 지연이 데드라인(기본 100ms)을 '연속 3회' 초과하면 '인프라 혼수상태
//  (Coma)'로 진단하고 IgnitionLock.Downgrade(핫패스 강제 하향) + 격리 소켓 리셋 콜백을 격발한다.
//
//  ── 정직 회계(정찰) ─────────────────────────────────────────────────────────
//  '연속 N회 초과 → IgnitionLock.Downgrade' 판정 원형은 ExecutionMicrostructureGuard(_consecutiveTrip·Interlocked
//  Increment/Exchange 리셋)에 이미 존재 — 그 관용구를 그대로 미러링(재발명 금지). RTT 소스·핑퐁·ProxyLost·소켓 abort
//  경로는 KiwoomBridgeClient 재사용. ★신규 델타: 'RTT/liveness 지연이 데드라인 연속 초과'를 세는 Coma FSM(기존 RTT 는
//  산출만 하고 임계 카운팅 안 함).
//
//  ── δ=0 / 결정론 / 격리 ─────────────────────────────────────────────────────
//  Observe(핫 판정): long 비교 + Interlocked 만(힙 0). 시계는 주입(Func<long>)·단조(Stopwatch 도메인 권장) — 판정은 호출부가
//  건넨 관측 지연값으로만(테스트 결정론). Downgrade 는 lock() 이라 콜드 경로(Coma 격발 1회)에서만 호출. ★인프라 지연값은
//  텔레메트리+인프라 액션(Downgrade/리셋)에만 흐르고 트레이딩 판정 입력(QMath/주문)엔 절대 유입 안 됨.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public enum SessionHealth : byte { Healthy = 0, Coma = 1 }

    /// <summary>세션 왕복 지연 데드라인 연속 초과 → Coma 진단 → 강등/소켓 리셋. 무할당 판정·주입 시계.</summary>
    public sealed class LiveSessionWatchdog : IDisposable
    {
        private readonly IgnitionLock?      _ignition;
        private readonly Action?            _resetSocket;    // 격리 소켓 리셋(운영자 배선·KiwoomBridgeClient 파이프 Dispose 등)
        private readonly Func<long>?        _latencyProbe;   // 현재 세션 왕복 지연(ticks·RTT 도메인) 프로브(Timer 구동용)
        private readonly MmfFlightRecorder? _blackbox;
        private readonly long               _deadlineTicks;  // 데드라인(RTT 도메인 ticks). 기본 100ms=1_000_000(100ns ticks).
        private readonly int                _comaThreshold;  // 연속 초과 임계(기본 3)
        private readonly int                _pollMs;

        private int  _consecutiveMiss;
        private int  _state = (int)SessionHealth.Healthy;   // Volatile.Read/Write(ref)
        private long _comaCount, _lastLatencyTicks, _totalMisses;
        private System.Threading.Timer? _timer;

        /// <param name="deadlineTicks">지연 데드라인(RTT 도메인 ticks). 키움 100ns 도메인 기준 100ms=1_000_000.</param>
        public LiveSessionWatchdog(IgnitionLock? ignition = null, Action? resetSocket = null,
                                   Func<long>? latencyProbe = null, long deadlineTicks = 1_000_000L,
                                   int comaThreshold = 3, int pollMs = 20, MmfFlightRecorder? blackbox = null)
        {
            _ignition     = ignition;
            _resetSocket  = resetSocket;
            _latencyProbe = latencyProbe;
            _blackbox     = blackbox;
            _deadlineTicks = deadlineTicks < 1 ? 1 : deadlineTicks;
            _comaThreshold = comaThreshold < 1 ? 1 : comaThreshold;
            _pollMs        = pollMs < 1 ? 1 : pollMs;
        }

        public SessionHealth Health   => (SessionHealth)Volatile.Read(ref _state);
        public int  ConsecutiveMiss   => Volatile.Read(ref _consecutiveMiss);
        public long ComaCount         => Interlocked.Read(ref _comaCount);
        public long LastLatencyTicks  => Interlocked.Read(ref _lastLatencyTicks);
        public long TotalMisses       => Interlocked.Read(ref _totalMisses);

        /// <summary>감시 시작(20ms Timer 폴). latencyProbe 주입 필수.</summary>
        public void Start()
        {
            if (_timer is not null || _latencyProbe is null) return;
            _timer = new System.Threading.Timer(_ => { try { Observe(_latencyProbe()); } catch { } }, null, _pollMs, _pollMs);
        }

        /// <summary>
        /// ★핫 판정(δ=0) — 관측 세션 지연을 1건 평가. 데드라인 초과면 연속 카운트++, 이내면 0 리셋(ExecutionMicrostructureGuard 미러).
        /// 연속 임계 도달 시 Coma 격발(Downgrade+소켓 리셋). 반환=이번 관측으로 Coma 진단됐는지.
        /// </summary>
        public bool Observe(long latencyTicks)
        {
            if (latencyTicks < 0) latencyTicks = 0;
            Interlocked.Exchange(ref _lastLatencyTicks, latencyTicks);

            if (latencyTicks <= _deadlineTicks)
            {
                Interlocked.Exchange(ref _consecutiveMiss, 0);   // 정상 응답 — 연속 카운트 리셋
                return false;
            }

            Interlocked.Increment(ref _totalMisses);
            int consec = Interlocked.Increment(ref _consecutiveMiss);
            if (consec < _comaThreshold) return false;

            // ★연속 임계 도달 — Coma 진단. CAS 로 1회만 격발(Healthy→Coma). 이미 Coma면 무동작.
            if (Interlocked.CompareExchange(ref _state, (int)SessionHealth.Coma, (int)SessionHealth.Healthy) != (int)SessionHealth.Healthy)
                return false;

            Interlocked.Increment(ref _comaCount);
            try { _blackbox?.Record(FlightEventKind.ComaDiagnosed, symbolCode: 0L, arg0: latencyTicks, arg1: consec, arg2: _deadlineTicks, sourceCode: 60); } catch { }
            try { _ignition?.Downgrade($"LiveSessionWatchdog Coma — 세션 지연 {latencyTicks}ticks 연속 {consec}회 데드라인({_deadlineTicks}) 초과"); } catch { }
            try { _resetSocket?.Invoke(); } catch { }            // 격리 소켓 리셋(콜드 경로)
            return true;
        }

        /// <summary>세션 회복 확인 시 Coma 해제(운영자/상위 재무장 후). 연속 카운트도 리셋.</summary>
        public void ClearComa()
        {
            Interlocked.Exchange(ref _consecutiveMiss, 0);
            Volatile.Write(ref _state, (int)SessionHealth.Healthy);
        }

        public void Dispose() { try { _timer?.Dispose(); } catch { } _timer = null; }
    }
}
