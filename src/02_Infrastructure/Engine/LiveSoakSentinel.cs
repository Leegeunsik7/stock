// ============================================================================
// LiveSoakSentinel — v12.5.0 Unattended Live-Soak Memory Sentinel (Section 2)
//
//   72시간+ 무인 운용 중 LOH 파편화/Gen2 팽창/할당 폭주(★기존 v11.6.0 LongRunningMemoryGcSentinel 재사용 —
//   기존엔 어디에도 배선되어 있지 않았음, 이번에 최초로 Disarm 에 결속) + 메모리 베이스라인 대비 지속 팽창
//   (>5%, 연속 표본) + Zombie 스레드(ThreadHeartbeatMonitor) + OOM 임박(OomImminentGuard) + GC 정지추세
//   (GcPauseTrendGuard) 5원 신호를 단일 비트마스크로 합성해 0ms 원자 Disarm 을 격발한다
//   (MultiTelemetryDisarmInterlock/DynamicRiskInterlock 과 동형의 sticky 비트마스크 패턴).
//
//   불변: 결정론(전부 외부 주입 값만) · δ=0(Sample*/NoteZombie/NoteOom/NoteGcPauseTrend 핫패스 힙 0) ·
//        fail-closed(베이스라인 미설정=드리프트 판정 생략) · liveArmed:false(관측+Disarm 게이트일 뿐 무장 인가 아님).
//   ★조립: onBreach 콜백으로 BlackBoxDumpTriggerBridge 등 블랙박스 덤프 트리거에 결속 가능(DIP — 본 파일은
//     구체 덤퍼 타입에 의존하지 않고 Action&lt;LiveSoakBreachSource&gt; 팬인만 노출).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    public enum LiveSoakBreachSource { GcPressure = 0, BaselineDrift = 1, ZombieThread = 2, OomImminent = 3, GcPauseTrend = 4 }

    public sealed class LiveSoakSentinel
    {
        private const long DisarmBit = 1L << 8;
        private const long DriftBpsThreshold = 500;         // 5%(=500bps) 팽창
        private const long DriftConsecutiveThreshold = 3;   // "지속" — 연속 3표본

        private readonly MS2E.Core.LongRunningMemoryGcSentinel _gcSentinel;
        private readonly Action<LiveSoakBreachSource>? _onBreach;

        private long _state;
        private long _baselineBytes = -1;
        private long _sustainedDriftCount;
        private long _lastDriftBps;

        public LiveSoakSentinel(MS2E.Core.LongRunningMemoryGcSentinel gcSentinel, Action<LiveSoakBreachSource>? onBreach = null)
        {
            _gcSentinel = gcSentinel ?? throw new ArgumentNullException(nameof(gcSentinel));
            _onBreach = onBreach;
        }

        /// <summary>메모리 베이스라인(정상 운용 중 대표 평가 시점의 총 할당 바이트) 설정/재설정.</summary>
        public void SetBaseline(long baselineBytes) => Volatile.Write(ref _baselineBytes, baselineBytes);

        /// <summary>
        /// 메모리 압력 표본(δ=0). LongRunningMemoryGcSentinel(Gen2/할당률)을 내부 위임 + 베이스라인 대비 팽창비를
        /// 산출해 ★연속 3표본 이상 5%(500bps) 초과 지속 시에만 Trip(단발 스파이크는 무시 — 히스테리시스).
        /// </summary>
        public bool SampleMemory(long nowTicks, int gen2Count, long allocBytes, long currentTotalBytes)
        {
            _gcSentinel.Sample(nowTicks, gen2Count, allocBytes);
            if (_gcSentinel.ShouldThrottle) Trip(LiveSoakBreachSource.GcPressure, allocBytes);

            long baseline = Volatile.Read(ref _baselineBytes);
            if (baseline > 0)
            {
                long driftBps = (currentTotalBytes - baseline) * 10_000 / baseline;
                Volatile.Write(ref _lastDriftBps, driftBps);
                if (driftBps > DriftBpsThreshold)
                {
                    long c = Interlocked.Increment(ref _sustainedDriftCount);
                    if (c >= DriftConsecutiveThreshold) Trip(LiveSoakBreachSource.BaselineDrift, driftBps);
                }
                else Interlocked.Exchange(ref _sustainedDriftCount, 0);
            }
            return IsDisarmed;
        }

        public bool NoteZombieThread(int zombieCount)
        {
            if (zombieCount > 0) Trip(LiveSoakBreachSource.ZombieThread, zombieCount);
            return IsDisarmed;
        }

        public bool NoteOomImminent(bool imminent)
        {
            if (imminent) Trip(LiveSoakBreachSource.OomImminent, 1);
            return IsDisarmed;
        }

        public bool NoteGcPauseTrend(bool tripped)
        {
            if (tripped) Trip(LiveSoakBreachSource.GcPauseTrend, 1);
            return IsDisarmed;
        }

        private void Trip(LiveSoakBreachSource source, long value)
        {
            Interlocked.Or(ref _state, (1L << (int)source) | DisarmBit);   // ★0ms 원자 격발
            _onBreach?.Invoke(source);                                     // → 블랙박스 덤프 등 팬인
        }

        public bool IsDisarmed => (Volatile.Read(ref _state) & DisarmBit) != 0;
        public bool CanEmit => !IsDisarmed;
        public bool BreachedSource(LiveSoakBreachSource s) => (Volatile.Read(ref _state) & (1L << (int)s)) != 0;
        public long LastDriftBps => Volatile.Read(ref _lastDriftBps);
        public long RawState => Volatile.Read(ref _state);

        /// <summary>물리/운영자 재무장(원인 규명 후에만). 베이스라인은 보존 — 다시 SetBaseline 호출 시에만 갱신.</summary>
        public void ReArm()
        {
            Interlocked.Exchange(ref _state, 0);
            Interlocked.Exchange(ref _sustainedDriftCount, 0);
        }
    }
}
