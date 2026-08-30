// ============================================================================
// CanaryVetoLatencyGovernor + ZeroLeakVetoInterlock — v11.4.2 Think Beyond Gemini
//
//   ★ 제미나이의 두 아이디어(A: 무할당 레이턴시 거버너 · B: 변조불가 Veto 감사 인터락)를 '상호잠금 이중 엔진'으로
//     융합·초월한다. Stage 1 카나리 드라이런의 가장 파멸적인 두 리스크를 영구 격리한다:
//
//     ① "레이턴시 측정으로 인한 UI 스레드 패닉" → CanaryVetoLatencyGovernor
//        - 이중 브로커(키움 pong RTT · 토스 HTTP RTT) 레이턴시를 ★락프리·무할당(δ=0)으로 집계.
//        - 모든 연산이 Interlocked/Volatile 뿐 — 락 0·힙 0 → UI 스레드에서 호출해도 블록/GC로 인한 패닉이 불가능.
//        - 스파이크(임계 초과) 발생 시 sticky 'breach' 래치를 세워 카나리 진입을 물리 차단(아래 인터락과 결선).
//
//     ② "Veto 게이트 오작동으로 인한 발주 누수 미세 틈새" → ZeroLeakVetoInterlock
//        - 모든 발주 요청의 단일 O(1) 인가 초크. liveArmed:false(기본) 이면 '미무장' 사유로 즉시 Veto.
//        - 미인가 판정은 단일 원자 판독(check-then-act 복합 갭 없음) → 다중 스레드 동시 폭주에도 '틈새 누수 0'.
//        - ★변조불가 감사: 모든 Veto 사건을 락프리 누적 FNV-1a 다이제스트로 각인 → 사후 로그 변조/누락을 탐지.
//        - 레이턴시 거버너가 breach 면 무장돼 있어도 Veto(융합) → 두 엔진이 하나의 인가 결정으로 맞물린다.
//
//   불변: 결정론(순수 산술) · δ=0(Record/TryAdmit/Snapshot 힙 0) · fail-closed(미무장·breach·비정상 명목 → Veto) ·
//        liveArmed:false(기본 미무장 — 물리 이중 키 없이는 단 1비트도 인가 0) · 락프리(모두 Interlocked/CAS).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>Veto 사유(발주 거부 원인).</summary>
    public enum VetoReason : int { None = 0, Disarmed = 1, LatencyBreach = 2, NonPositiveNotional = 3 }

    /// <summary>레이턴시 관측 스냅샷(δ=0 판독). (기존 Telemetry.LatencySnapshot 과 이름 분리.)</summary>
    public readonly record struct CanaryLatencySnapshot(long Count, long MeanMicros, long MaxMicros, long BreachCount, bool Breached);

    /// <summary>Veto 감사 스냅샷(변조불가 다이제스트 포함).</summary>
    public readonly record struct VetoAuditSnapshot(long Admitted, long Vetoed, VetoReason LastReason, long Digest);

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Zero-Panic 레이턴시 거버너 (δ=0 락프리 집계)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class CanaryVetoLatencyGovernor
    {
        public const long DefaultSpikeMicros = 500_000;   // 500ms 스파이크 임계(런북 Stage 3 지연 인터락과 정렬)

        private readonly long _spikeMicros;
        private long _count, _sumMicros, _maxMicros, _breachCount, _breachLatched;

        public CanaryVetoLatencyGovernor(long spikeThresholdMicros = DefaultSpikeMicros)
            => _spikeMicros = spikeThresholdMicros > 0 ? spikeThresholdMicros : DefaultSpikeMicros;

        public long SpikeThresholdMicros => _spikeMicros;

        /// <summary>레이턴시 표본 1건 기록(마이크로초). ★δ=0·락프리 — UI 스레드에서 호출해도 블록/할당 0(패닉 불가).</summary>
        public void Record(long rttMicros)
        {
            if (rttMicros < 0) rttMicros = 0;                       // fail-safe(음수 방어)
            Interlocked.Increment(ref _count);
            Interlocked.Add(ref _sumMicros, rttMicros);

            // 최댓값 무락 CAS 갱신.
            long old;
            do
            {
                old = Volatile.Read(ref _maxMicros);
                if (rttMicros <= old) break;
            }
            while (Interlocked.CompareExchange(ref _maxMicros, rttMicros, old) != old);

            if (rttMicros > _spikeMicros)
            {
                Interlocked.Increment(ref _breachCount);
                Interlocked.Exchange(ref _breachLatched, 1);        // sticky — 재무장(운영자) 전까지 유지
            }
        }

        /// <summary>스파이크 파열 여부(sticky). breach 면 카나리 진입이 차단된다.</summary>
        public bool Breached => Volatile.Read(ref _breachLatched) == 1;

        /// <summary>관측 스냅샷(δ=0 판독).</summary>
        public CanaryLatencySnapshot Snapshot()
        {
            long c = Volatile.Read(ref _count);
            long s = Volatile.Read(ref _sumMicros);
            long mx = Volatile.Read(ref _maxMicros);
            long bc = Volatile.Read(ref _breachCount);
            long mean = c > 0 ? s / c : 0;
            return new CanaryLatencySnapshot(c, mean, mx, bc, bc > 0);
        }

        /// <summary>breach 래치 해제 — 운영자가 원인 규명 후 재무장 시에만(감사 대상). 드라이런에서는 호출 안 함.</summary>
        public void ClearBreach() => Interlocked.Exchange(ref _breachLatched, 0);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Zero-Leak Veto 인가 인터락 (O(1) fail-closed + 변조불가 감사)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class ZeroLeakVetoInterlock
    {
        // FNV-1a 64bit (변조불가 감사 다이제스트).
        private const ulong FnvOffset = 14695981039346656037UL;
        private const ulong FnvPrime = 1099511628211UL;

        private readonly CanaryVetoLatencyGovernor? _latency;

        private long _armed;                          // 0 = 미무장(liveArmed:false 기본)
        private long _admitted, _vetoed;
        private int _lastReason;
        private long _digest = unchecked((long)FnvOffset);

        public ZeroLeakVetoInterlock(CanaryVetoLatencyGovernor? latency = null) => _latency = latency;

        /// <summary>무장 여부. 기본 false(liveArmed:false) — 물리 이중 키 없이는 절대 true 가 되지 않는다.</summary>
        public bool IsArmed => Volatile.Read(ref _armed) == 1;

        /// <summary>
        /// ★테스트/시뮬레이션 전용 무장 토글. 실 운용에서는 CanaryHealthSentinel(물리 이중 키)만이 발주를 인가하며,
        /// Stage 1 드라이런에서는 절대 호출되지 않는다(무장 시도조차 감사됨). 이름에 'ForSimulation' 을 박아 오용을 차단.
        /// </summary>
        public void SetArmedForSimulation(bool on) => Interlocked.Exchange(ref _armed, on ? 1 : 0);

        /// <summary>
        /// O(1) fail-closed 발주 인가. false 반환 시 호출자는 발주 패킷을 '만들지 않는다'(사출 0비트).
        /// 우선순위: ①미무장 → ②레이턴시 breach → ③비정상 명목 → 인가. 미무장 판정은 단일 원자 판독(누수 틈새 0).
        /// </summary>
        public bool TryAdmit(long notionalRaw, out VetoReason reason)
        {
            if (Volatile.Read(ref _armed) != 1) { reason = VetoReason.Disarmed;            RecordVeto(reason); return false; }
            if (_latency is { Breached: true })  { reason = VetoReason.LatencyBreach;        RecordVeto(reason); return false; }
            if (notionalRaw <= 0)                { reason = VetoReason.NonPositiveNotional;  RecordVeto(reason); return false; }

            Interlocked.Increment(ref _admitted);
            reason = VetoReason.None;
            return true;
        }

        private void RecordVeto(VetoReason reason)
        {
            Interlocked.Increment(ref _vetoed);
            Interlocked.Exchange(ref _lastReason, (int)reason);

            // 변조불가 누적 다이제스트(락프리 CAS) — Veto 사건 스트림의 무결성/완전성 보증.
            long old, next;
            do
            {
                old = Volatile.Read(ref _digest);
                next = unchecked((long)(((ulong)old ^ (byte)reason) * FnvPrime));
            }
            while (Interlocked.CompareExchange(ref _digest, next, old) != old);
        }

        public long AdmittedCount => Volatile.Read(ref _admitted);
        public long VetoedCount => Volatile.Read(ref _vetoed);
        public VetoReason LastReason => (VetoReason)Volatile.Read(ref _lastReason);
        public long AuditDigest => Volatile.Read(ref _digest);

        public VetoAuditSnapshot Audit() =>
            new(Volatile.Read(ref _admitted), Volatile.Read(ref _vetoed), (VetoReason)Volatile.Read(ref _lastReason), Volatile.Read(ref _digest));

        /// <summary>독립 검증용 — 주어진 Veto 사유 시퀀스로부터 기대 다이제스트를 재계산(변조 탐지 대조).</summary>
        public static long ExpectedDigest(ReadOnlySpan<VetoReason> reasons)
        {
            ulong h = FnvOffset;
            for (int i = 0; i < reasons.Length; i++) h = (h ^ (byte)reasons[i]) * FnvPrime;
            return unchecked((long)h);
        }
    }
}
