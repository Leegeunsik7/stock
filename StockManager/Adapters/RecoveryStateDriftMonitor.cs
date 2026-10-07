// ============================================================================
// RecoveryStateDriftMonitor — [Long-Soak · 방향 A] 다중 브로커 세션 복구 상태 전이 · 격리 작동 표류 포렌식 모니터(MMF 블랙박스 · δ=0)
//
//  문제(실측 · v12.17.0-GM): SessionRecoveryManager(키움 수동 · KIS 자동)의 전이(단절 → 백오프 → 재접속 → 대사 → 해제 · 소진/불일치 래치)는
//    카운터로만 남고 블랙박스에 기록되지 않았다 — 장중 사고 후 '언제 격리됐고 얼마나 머물렀나'를 재구성할 수 없었다.
//
//  Pump(50ms 평가 타이머 · 복구 관리자 펌프 '뒤'):
//   ① 브로커마다 상태 묶음(상태 · Held · Isolated · Exhausted · Discrepancy · 연속 실패)을 64비트로 묶어 직전 값과 비교 —
//      바뀌면 SessionRecoveryTransition(100) 기록: arg0 = 직전 묶음 · arg1 = 새 묶음 · arg2 = 직전 상태 체류 시간(Stopwatch 틱) · symbol = 브로커.
//      Held 체류 최댓값(브로커별) 추적 — 재접속 + 대사까지 걸린 최장 격리 시간.
//   ② 작동 표류: 한도 피드 세션 격리 비트 ≠ 관리자 Isolated 가 '연속 2 펌프' 지속 = SessionRecoveryDrift(101) 1회 기록 + 계수
//      (1회 판독 차이는 관리자 락과 피드 판독 사이 경합일 수 있다 — 디바운스). 외부가 격리 비트를 지웠는데 재주장이 안 되는 결함을 잡는다.
//  ★정직(해상도): 지시서의 '0.1ns' 는 불가 — 이 호스트의 단조 시계(Stopwatch/QPC) 해상도는 Stopwatch.Frequency 에 따른다(일반적으로 100ns).
//    체류 시간은 원시 Stopwatch 틱으로 기록하고 TicksPerSecond 를 함께 노출(변환은 판독 측 · 손실 없음).
//  δ=0: 고정 배열 · 속성 판독(관리자 락 · 무할당) · 블랙박스 Record(무할당) — 테스트 실측.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public sealed class RecoveryStateDriftMonitor
    {
        public const int SourceCode = 0x5352;                                      // 'SR' — 블랙박스 발원 식별
        public const int DriftDebouncePumps = 2;

        private readonly SessionRecoveryManager[] _managers;
        private readonly MmfFlightRecorder? _blackbox;
        private readonly BrokerLimitFeedProvider? _limits;
        private readonly Func<long> _nowTicks;
        private readonly long[] _last, _since, _maxHeld, _transitions;
        private readonly int[] _driftStreak;
        private readonly bool[] _driftOpen;
        private long _drifts, _pumps;
        private int _pumping;

        public RecoveryStateDriftMonitor(MmfFlightRecorder? blackbox, BrokerLimitFeedProvider? limits, Func<long>? nowTicks, params SessionRecoveryManager?[] managers)
        {
            int n = 0;
            foreach (var m in managers ?? Array.Empty<SessionRecoveryManager?>()) if (m is not null) n++;
            _managers = new SessionRecoveryManager[n];
            n = 0;
            foreach (var m in managers ?? Array.Empty<SessionRecoveryManager?>()) if (m is not null) _managers[n++] = m;
            _blackbox = blackbox; _limits = limits;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _last = new long[n]; _since = new long[n]; _maxHeld = new long[n]; _transitions = new long[n];
            _driftStreak = new int[n]; _driftOpen = new bool[n];
            long now = _nowTicks();
            for (int i = 0; i < n; i++) { _last[i] = Pack(_managers[i]); _since[i] = now; }
        }

        /// <summary>체류 시간 틱의 초당 틱 수(기본 = Stopwatch.Frequency · 주입 시계면 주입측 단위).</summary>
        public static long TicksPerSecond => Stopwatch.Frequency;
        public int Count => _managers.Length;
        public long Drifts => Interlocked.Read(ref _drifts);
        public long Pumps => Interlocked.Read(ref _pumps);
        public long Transitions(int index) => Interlocked.Read(ref _transitions[index]);
        public long MaxHeldTicks(int index) => Interlocked.Read(ref _maxHeld[index]);
        public long LastPacked(int index) => Interlocked.Read(ref _last[index]);

        /// <summary>상태 묶음: [0..7] 상태 · [8] Held · [9] Isolated · [10] Exhausted · [11] Discrepancy · [16..23] 연속 실패 · [32..39] 브로커.</summary>
        public static long Pack(SessionRecoveryManager m)
            => (long)m.State | (m.Held ? 1L << 8 : 0) | (m.Isolated ? 1L << 9 : 0) | (m.Exhausted ? 1L << 10 : 0)
               | (m.Discrepancy ? 1L << 11 : 0) | ((long)Math.Min(m.ConsecutiveFailures, 255) << 16) | ((long)(m.Broker & 0xFF) << 32);

        public static bool PackedHeld(long packed) => (packed & (1L << 8)) != 0;
        public static SessionRecoveryState PackedState(long packed) => (SessionRecoveryState)(packed & 0xFF);

        /// <summary>전이 · 표류 판정(단일 진입 · δ=0).</summary>
        public void Pump()
        {
            if (Interlocked.Exchange(ref _pumping, 1) == 1) return;
            try
            {
                Interlocked.Increment(ref _pumps);
                long now = _nowTicks();
                for (int i = 0; i < _managers.Length; i++)
                {
                    var m = _managers[i];
                    long packed = Pack(m);
                    long prev = _last[i];
                    if (packed != prev)
                    {
                        long dwell = now - _since[i];
                        if (PackedHeld(prev) && dwell > Interlocked.Read(ref _maxHeld[i])) Interlocked.Exchange(ref _maxHeld[i], dwell);
                        _blackbox?.Record(FlightEventKind.SessionRecoveryTransition, m.Broker, prev, packed, dwell, SourceCode);
                        Interlocked.Exchange(ref _last[i], packed);
                        _since[i] = now;
                        Interlocked.Increment(ref _transitions[i]);
                    }
                    if (_limits is null) continue;
                    bool limitIsolated = ((_limits.SessionIsolatedMask >> m.Broker) & 1) != 0;
                    if (limitIsolated != m.Isolated)
                    {
                        if (++_driftStreak[i] >= DriftDebouncePumps && !_driftOpen[i])
                        {
                            _driftOpen[i] = true;
                            Interlocked.Increment(ref _drifts);
                            _blackbox?.Record(FlightEventKind.SessionRecoveryDrift, m.Broker, limitIsolated ? 1 : 0, m.Isolated ? 1 : 0, _driftStreak[i], SourceCode);
                        }
                    }
                    else { _driftStreak[i] = 0; _driftOpen[i] = false; }
                }
            }
            finally { Volatile.Write(ref _pumping, 0); }
        }
    }
}
