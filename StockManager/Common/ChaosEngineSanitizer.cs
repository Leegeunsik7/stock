// ============================================================================
// ChaosEngineSanitizer — v9.1.0 무할당 카오스 결함 주입 엔진
//
//  핫패스 연산과 소켓/메모리 계층 사이에 삽입되어, 힙 할당 없이(δ=0) 의도적 재난 결함을 동적으로 주입하는
//  카오스 샌드박스 엔진. 극단적 재난 하에서도 시스템의 fail-closed 안전 불변식이 100% 사수됨을 실증한다.
//
//  주입 가능 결함(비트마스크):
//    · F_SOCKET_JITTER   — 소켓 RTT 지터 스파이크 + 무작위 패킷 드롭
//    · F_MEMORY_PRESSURE — MMF 페이지 폴트 지연 + 힙 압박
//    · F_THERMAL_DROP    — CPU 유효주파수 급락 + 서멀 스로틀 격발
//    · F_TORN_READ       — RingBuffer seqlock 읽기 중단/지연 경합
//
//  ★안전 계약: 결함이 주입되면 즉시 SafeHoldActive(가역·tighten-only) 로 무장을 하향 조율하고, 파멸 강도
//    (intensity ≥ catastrophic) 결함은 주입식 onForceFreeze 로 원자 하드 동결을 격발한다. 이 엔진은 IBroker·
//    소켓·onSend·ring 참조가 0 — '결함 결정'만 산출하며, 실제 주입은 샌드박스 인터셉터가 소비한다(발주 유발 0).
//
//  ★불변: δ=0(Inject/질의 핫패스 힙 0·Interlocked/Volatile) · 결정론(주입 시드 SplitMix64·벽시계 무참조) ·
//    liveArmed:false(SafeHold tighten-only·소켓 미접촉) · fail-closed(패킷 드롭=미발송·파멸=ForceFreeze).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>무할당 카오스 결함 주입 엔진(δ=0·결정론). Inject(결함,강도)→SafeHold/ForceFreeze·질의로 결함 효과 산출.</summary>
    public sealed class ChaosEngineSanitizer
    {
        // 결함 시나리오 비트.
        public const long F_SOCKET_JITTER   = 1L << 0;   // RTT 지터 스파이크 + 무작위 패킷 드롭
        public const long F_MEMORY_PRESSURE = 1L << 1;   // MMF 페이지 폴트 지연 + 힙 압박
        public const long F_THERMAL_DROP    = 1L << 2;   // CPU 주파수 급락 + 서멀 스로틀
        public const long F_TORN_READ       = 1L << 3;   // Seqlock 읽기 중단/지연 경합
        public const long F_ALL             = F_SOCKET_JITTER | F_MEMORY_PRESSURE | F_THERMAL_DROP | F_TORN_READ;

        private const long Golden = unchecked((long)0x9E3779B97F4A7C15UL);

        private readonly Action? _onForceFreeze;    // 파멸 결함 → 하드 동결(주입식·테스트=페이크·프로세스 미살)
        private readonly long _catastrophicPermille; // 이 강도 이상 = 파멸(ForceFreeze)
        private long _faultMask;           // 현재 활성 결함
        private long _intensityPermille;   // 현재 결함 강도(0~1000)
        private long _injections, _packetDrops, _jitterInjections, _safeHolds, _forceFreezes;
        private long _prngState;           // δ=0 결정 스트림(Interlocked 전진 SplitMix64)

        public ChaosEngineSanitizer(Action? onForceFreeze = null, ulong decisionSeed = 0xC4A05EEDUL,
                                    long catastrophicPermille = 950)
        {
            _onForceFreeze = onForceFreeze;
            _catastrophicPermille = Math.Clamp(catastrophicPermille, 1, 1000);
            _prngState = unchecked((long)decisionSeed);
        }

        // ── 읽기(δ=0·락프리) ──
        public long FaultMask          => Volatile.Read(ref _faultMask);
        public long IntensityPermille  => Volatile.Read(ref _intensityPermille);
        public bool ChaosActive        => Volatile.Read(ref _faultMask) != 0L;
        /// <summary>★blockWhile — 결함 활성 시 무장 하향(가역·tighten-only). 결함 해제 시 자동 복원.</summary>
        public bool SafeHoldActive     => Volatile.Read(ref _faultMask) != 0L;
        public long StateCode          => Volatile.Read(ref _faultMask) != 0L ? 1L : 0L;   // 0 standby / 1 injecting
        public long Injections         => Interlocked.Read(ref _injections);
        public long PacketDrops        => Interlocked.Read(ref _packetDrops);
        public long JitterInjections   => Interlocked.Read(ref _jitterInjections);
        public long SafeHolds          => Interlocked.Read(ref _safeHolds);
        public long ForceFreezes       => Interlocked.Read(ref _forceFreezes);

        /// <summary>결함 주입(δ=0). faultMask=시나리오 비트, intensityPermille=강도(0~1000). 강도≥임계면 파멸→ForceFreeze.</summary>
        public void Inject(long faultMask, long intensityPermille)
        {
            long m = faultMask & F_ALL;
            long inten = Math.Clamp(intensityPermille, 0, 1000);
            Volatile.Write(ref _faultMask, m);
            Volatile.Write(ref _intensityPermille, inten);
            if (m != 0L)
            {
                Interlocked.Increment(ref _injections);
                Interlocked.Increment(ref _safeHolds);      // 주입 즉시 SafeHold(무장 하향)
                if (inten >= _catastrophicPermille)          // 파멸 강도 → 하드 동결(원자·상수 사유·δ=0)
                {
                    Interlocked.Increment(ref _forceFreezes);
                    try { _onForceFreeze?.Invoke(); } catch { }
                }
            }
        }

        /// <summary>카오스 해제(δ=0). 결함 제거 → SafeHold 가역 해제. 무장 재개는 복구 스로틀러가 별도 게이팅.</summary>
        public void Clear()
        {
            Volatile.Write(ref _faultMask, 0L);
            Volatile.Write(ref _intensityPermille, 0L);
        }

        // ── δ=0 결함 효과 질의(샌드박스 인터셉터가 소켓/메모리 대신 소비) — 결정론 시드 스트림 ──
        private long NextDecision()   // Interlocked 전진 SplitMix64(락프리·δ=0·음수 제거)
        {
            long s = Interlocked.Add(ref _prngState, Golden);
            ulong z = (ulong)s;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return (long)((z ^ (z >> 31)) & 0x7FFFFFFFFFFFFFFFUL);
        }

        /// <summary>소켓 패킷 드롭 여부(δ=0). F_SOCKET_JITTER 활성 시 강도 비율로 드롭 — 드롭=미발송(fail-safe).</summary>
        public bool ShouldDropPacket()
        {
            if ((Volatile.Read(ref _faultMask) & F_SOCKET_JITTER) == 0L) return false;
            bool drop = (NextDecision() % 1000L) < Volatile.Read(ref _intensityPermille);
            if (drop) Interlocked.Increment(ref _packetDrops);
            return drop;
        }

        /// <summary>주입 RTT 지터(ns·δ=0). F_SOCKET_JITTER 활성 시 강도 비례 지터(실제 대기 아님 — 산출값만).</summary>
        public long InjectedJitterNanos(long maxJitterNanos = 5_000_000L)
        {
            if ((Volatile.Read(ref _faultMask) & F_SOCKET_JITTER) == 0L) return 0L;
            Interlocked.Increment(ref _jitterInjections);
            long span = Math.Max(1L, maxJitterNanos);
            return (NextDecision() % span) * Volatile.Read(ref _intensityPermille) / 1000L;
        }

        /// <summary>메모리 압박 페이지폴트 지연(ns·δ=0). F_MEMORY_PRESSURE 활성 시 강도 비례.</summary>
        public long MemoryPressureDelayNanos(long maxDelayNanos = 2_000_000L)
        {
            if ((Volatile.Read(ref _faultMask) & F_MEMORY_PRESSURE) == 0L) return 0L;
            long span = Math.Max(1L, maxDelayNanos);
            return (NextDecision() % span) * Volatile.Read(ref _intensityPermille) / 1000L;
        }

        /// <summary>서멀 드롭 유효주파수(‰·δ=0). F_THERMAL_DROP 활성 시 강도만큼 하락(1000=정상·0=완전 스로틀).</summary>
        public long ThermalFreqPermille()
        {
            if ((Volatile.Read(ref _faultMask) & F_THERMAL_DROP) == 0L) return 1000L;
            return Math.Max(0L, 1000L - Volatile.Read(ref _intensityPermille));
        }

        /// <summary>Torn read 경합 주입 여부(δ=0). F_TORN_READ 활성 시 강도 비율로 seqlock 재시도 유발.</summary>
        public bool ShouldStallSeqlockRead()
        {
            if ((Volatile.Read(ref _faultMask) & F_TORN_READ) == 0L) return false;
            return (NextDecision() % 1000L) < Volatile.Read(ref _intensityPermille);
        }
    }
}
