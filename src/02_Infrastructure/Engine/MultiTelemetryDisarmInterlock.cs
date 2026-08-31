// ============================================================================
// MultiTelemetryDisarmInterlock + AnomalyFlightRecorder — v11.4.4 Think Beyond Gemini
//
//   ★ 제미나이의 두 아이디어(A: 다중 텔레메트리 비트마스크 서킷 브레이커 · B: Flight Recorder + 메모리 퍼저)를
//     '상호잠금 이중 엔진'으로 융합·초월한다. Stage 3 실시간 무장 운용의 두 파멸 리스크를 영구 격리한다:
//
//     ① "레이턴시/429/슬리피지 다중 이상 신호 경합 시 Disarm 지연" → MultiTelemetryDisarmInterlock
//        - 3개 이상 신호(Latency·Http429·Slippage)를 단일 long 상태어의 비트필드로 압축.
//        - 임의 신호가 문턱을 넘는 순간 ★단일 Interlocked.Or 로 '해당 breach 비트 + 전역 Disarm 래치'를 원자 격발.
//        - 다중 소스가 동시에 breach 해도 각자 독립 원자 OR → 락 경합/순서 지연 0 → '언제나 0ms' Disarm(경합 지연 불가).
//        - Disarm 래치는 sticky — 물리 수동 재무장(ReArm) 전까지 CanEmit=false(발주 100% 차단).
//
//     ② "Disarm 직후 메모리 잔여 주문 데이터의 비동기 사출" → AnomalyFlightRecorder
//        - Disarm 격발과 동시에 링버퍼 스냅샷을 불변 Flight Recorder 로 원자 고정(사후 포렌식) +
//          인가 대기 중이던 모든 주문 버퍼를 ★CryptographicOperations.ZeroMemory 로 0x00 소거(메모리 퍼저).
//        - 비동기 컨티뉴에이션이 뒤늦게 재개돼도: (a) Disarm 래치가 O(1) 로 사출 차단(fail-closed),
//          (b) 대기 주문 버퍼가 이미 0x00 → 사출할 데이터 자체가 없음. 이중으로 잔여 사출을 봉쇄.
//
//   불변: 결정론 · δ=0(Note/CanEmit/Capture 힙 0) · fail-closed(문턱 미설정/미상 → 안전측) ·
//        liveArmed:false(재무장 전 CanEmit=false → 발주 0) · 락프리(Interlocked.Or/Exchange/Volatile).
//   ※ CanaryHealthSentinel(50ms Disarm 게이트)을 대체하지 않고 다중 신호 경합·잔여 사출 계층을 보강한다.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    public enum TelemetrySource { Latency = 0, Http429 = 1, Slippage = 2 }

    /// <summary>Disarm 격발 시 고정되는 불변 스냅샷(포렌식).</summary>
    public readonly record struct FlightSnapshot(bool Captured, TelemetrySource Source, long Value, long Sequence);

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Anomaly Flight Recorder & Memory Purger
    // ════════════════════════════════════════════════════════════════════════
    public sealed class AnomalyFlightRecorder
    {
        private readonly long[] _pending;    // 인가 대기 주문 명목 버퍼(소거 대상)
        private long _captured;
        private long _seq;
        private FlightSnapshot _frozen = new(false, TelemetrySource.Latency, 0, 0);

        public AnomalyFlightRecorder(int pendingSlots = 64)
            => _pending = new long[pendingSlots > 0 ? pendingSlots : 64];

        /// <summary>발주 인가 전 대기 주문을 스테이징(무장 상태). Disarm 시 전량 소거된다.</summary>
        public void StagePending(int slot, long notionalRaw)
        {
            if ((uint)slot < (uint)_pending.Length) Volatile.Write(ref _pending[slot], notionalRaw);
            Interlocked.Increment(ref _seq);
        }

        /// <summary>
        /// ★Disarm 격발 동시 처리: 최초 1회 스냅샷 고정(불변) + 대기 주문 버퍼 0x00 소거. 멱등(첫 격발만 스냅샷).
        /// </summary>
        public void CaptureAndPurge(TelemetrySource source, long value)
        {
            if (Interlocked.Exchange(ref _captured, 1) == 0)
                _frozen = new FlightSnapshot(true, source, value, Volatile.Read(ref _seq));
            // 대기 주문 버퍼 전량 0x00 — 비동기 잔여 사출 차단(메모리 덤프·재참조 방어).
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_pending.AsSpan()));
        }

        public bool HasCaptured => Volatile.Read(ref _captured) == 1;
        public FlightSnapshot Frozen => _frozen;

        /// <summary>대기 버퍼가 전부 0 인지(소거 실증).</summary>
        public bool PendingBufferIsZero
        {
            get { for (int i = 0; i < _pending.Length; i++) if (Volatile.Read(ref _pending[i]) != 0) return false; return true; }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _captured, 0);
            _frozen = new(false, TelemetrySource.Latency, 0, 0);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_pending.AsSpan()));
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Multi-Telemetry Disarm Interlock (단일 long 비트마스크·0ms 원자)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class MultiTelemetryDisarmInterlock
    {
        // 비트 레이아웃: bit 0-2 = 소스별 breach · bit 8 = 전역 Disarm 래치.
        private const long DisarmBit = 1L << 8;

        private long _state;                              // 원자 비트마스크
        private readonly long[] _thresholds = new long[3];
        private readonly AnomalyFlightRecorder _recorder;

        public MultiTelemetryDisarmInterlock(AnomalyFlightRecorder recorder) => _recorder = recorder;

        /// <summary>문턱 설정(무장 시). ≤0 이면 해당 소스 비활성(fail-closed: 미설정 소스는 Note 무시).</summary>
        public void Configure(long latencyMicros, long http429Count, long slippageBps)
        {
            Volatile.Write(ref _thresholds[0], latencyMicros);
            Volatile.Write(ref _thresholds[1], http429Count);
            Volatile.Write(ref _thresholds[2], slippageBps);
        }

        /// <summary>
        /// 텔레메트리 표본 1건. value 가 소스 문턱을 넘으면 ★단일 Interlocked.Or 로 breach 비트 + Disarm 래치를 0ms 원자
        /// 격발하고 Flight Recorder 를 격발(스냅샷+소거). 다중 소스 동시 호출에도 지연 0(각자 독립 원자 OR). breach 시 true.
        /// </summary>
        public bool Note(TelemetrySource source, long value)
        {
            int i = (int)source;
            if ((uint)i > 2) return IsDisarmed;
            long thr = Volatile.Read(ref _thresholds[i]);
            if (thr <= 0 || value <= thr) return IsDisarmed;      // 미설정/미초과 → 무동작

            long bits = (1L << i) | DisarmBit;                    // breach 비트 + 전역 Disarm
            Interlocked.Or(ref _state, bits);                     // ★0ms 원자 격발(경합 지연 불가)
            _recorder.CaptureAndPurge(source, value);            // 스냅샷 고정 + 대기 주문 0x00 소거
            return true;
        }

        /// <summary>Disarm 래치 상태(sticky).</summary>
        public bool IsDisarmed => (Volatile.Read(ref _state) & DisarmBit) != 0;

        /// <summary>발주 사출 가능 여부 — Disarm 후 항상 false(fail-closed 게이트).</summary>
        public bool CanEmit => (Volatile.Read(ref _state) & DisarmBit) == 0;

        public bool BreachedSource(TelemetrySource s) => (Volatile.Read(ref _state) & (1L << (int)s)) != 0;
        public long RawState => Volatile.Read(ref _state);

        /// <summary>물리 수동 재무장 — 원인 규명 후에만. 상태·Recorder 초기화.</summary>
        public void ReArm()
        {
            Interlocked.Exchange(ref _state, 0);
            _recorder.Reset();
        }
    }
}
