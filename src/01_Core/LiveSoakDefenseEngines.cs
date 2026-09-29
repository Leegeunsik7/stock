// ============================================================================
// ThreadHeartbeatMonitor + OomImminentGuard + GcPauseTrendGuard — v12.5.0 Live-Soak Defense (Pure Core)
//
//   Unattended Live-Soak Memory Sentinel(Section 2)이 소비하는 순수 판정 유틸 3종. 기존 v11.6.0
//   LongRunningMemoryGcSentinel(Gen2/할당률 압력)을 대체하지 않고 보완한다 — 이 엔진은 무배선 상태였으므로
//   (BlazorHomeForm.cs DI 0건 실측) v12.5.0 은 그 위에 제로/좀비/드리프트/GC정지추세 축을 더해 LiveSoakSentinel
//   (Infrastructure)로 합성한다.
//
//   Think Beyond Idea B(GC Stop-The-World 선제 감지): GcPauseTrendGuard 는 GC.GetTotalPauseDuration() 누적값의
//   ★외부 주입 표본(자체 GC 질의 0 — 결정론·테스트 가능성)을 받아 '연속 상승' 추세를 감지한다. 단발 스파이크가
//   아니라 지속 상승 추세를 보므로, 실제 장기 STW 멈춤이 틱 유실로 전이되기 '전에' 선제 트립할 수 있다.
//
//   불변: 결정론(전부 외부 주입 값만 사용·wall-clock/GC API 직접 호출 0) · δ=0(Beat/Sample/Scan 핫패스 힙 0) ·
//        fail-closed(미상 입력=판정 생략 또는 안전측) · 락프리(Interlocked/Volatile).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Thread Heartbeat Monitor — Zombie 스레드 감지
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>고정 슬롯별 하트비트 타임스탬프를 추적해 정체(Zombie) 스레드를 δ=0 스캔으로 계수한다.</summary>
    public sealed class ThreadHeartbeatMonitor
    {
        private readonly long[] _lastBeatTicks;
        private readonly int _slots;

        public ThreadHeartbeatMonitor(int slots)
        {
            if (slots <= 0) throw new ArgumentOutOfRangeException(nameof(slots));
            _slots = slots;
            _lastBeatTicks = new long[slots];
        }

        /// <summary>슬롯의 생존 신호 갱신(★해당 스레드가 직접 호출·δ=0). 범위 밖은 무시(fail-closed).</summary>
        public void Beat(int slot, long nowTicks)
        {
            if ((uint)slot < (uint)_slots) Volatile.Write(ref _lastBeatTicks[slot], nowTicks);
        }

        /// <summary>마지막 하트비트로부터 timeoutTicks 를 초과한(정체) 슬롯 수를 계수(δ=0 — 0 표본 슬롯은 제외).</summary>
        public int CountZombies(long nowTicks, long timeoutTicks)
        {
            int zombies = 0;
            for (int i = 0; i < _slots; i++)
            {
                long last = Volatile.Read(ref _lastBeatTicks[i]);
                if (last > 0 && nowTicks - last > timeoutTicks) zombies++;
            }
            return zombies;
        }

        public void Reset(int slot)
        {
            if ((uint)slot < (uint)_slots) Volatile.Write(ref _lastBeatTicks[slot], 0);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  OOM Imminent Guard — 메모리 부족 임박 감지
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>GC.GetGCMemoryInfo() 유래 값(★외부 주입 — 자체 GC 질의 0)의 부하비를 감시해 OOM 임박을 sticky 격발.</summary>
    public sealed class OomImminentGuard
    {
        private readonly long _loadRatioThresholdPpm;   // 예: 900,000 = 90%
        private long _imminent;

        public OomImminentGuard(long loadRatioThresholdPpm = 900_000)
            => _loadRatioThresholdPpm = loadRatioThresholdPpm > 0 ? loadRatioThresholdPpm : 900_000;

        /// <summary>현재 메모리 부하/고부하임계 비율(ppm) 산출·판정(δ=0). 임계(highMemoryLoadThresholdBytes)&lt;=0 이면
        /// 미상 입력으로 간주해 판정 생략(fail-closed — 잘못된 트립 방지).</summary>
        public bool Sample(long memoryLoadBytes, long highMemoryLoadThresholdBytes)
        {
            if (highMemoryLoadThresholdBytes <= 0) return IsImminent;
            long ratioPpm = memoryLoadBytes * 1_000_000 / highMemoryLoadThresholdBytes;
            if (ratioPpm >= _loadRatioThresholdPpm) Interlocked.Exchange(ref _imminent, 1);
            return IsImminent;
        }

        public bool IsImminent => Volatile.Read(ref _imminent) == 1;
        public void Reset() => Interlocked.Exchange(ref _imminent, 0);
    }

    // ════════════════════════════════════════════════════════════════════════
    //  GC Pause Trend Guard (Think Beyond Idea B) — STW 멈춤 선제 감지
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>GC.GetTotalPauseDuration() 누적값의 ★외부 주입 표본이 연속 상승하면 선제 트립(sticky). 단발
    /// 스파이크가 아닌 '추세'를 보므로 틱 유실로 전이되기 전에 먼저 감지할 수 있다.</summary>
    public sealed class GcPauseTrendGuard
    {
        private readonly long _consecutiveRisingThreshold;
        private long _lastPauseTicks = -1;   // -1 = 미초기화
        private long _consecutiveRising;
        private long _tripped;

        public GcPauseTrendGuard(long consecutiveRisingThreshold = 3)
            => _consecutiveRisingThreshold = consecutiveRisingThreshold > 0 ? consecutiveRisingThreshold : 3;

        /// <summary>누적 GC 정지시간(ticks) 표본 1건 반영(★단일 라이터·δ=0). 직전 대비 증분이 있으면 연속상승
        /// 카운터 증가, 증분 0 이면 리셋. 연속상승이 임계 도달 시 sticky 트립.</summary>
        public bool Sample(long cumulativePauseTicks)
        {
            if (_lastPauseTicks < 0) { _lastPauseTicks = cumulativePauseTicks; return IsTripped; }
            long delta = cumulativePauseTicks - _lastPauseTicks;
            _lastPauseTicks = cumulativePauseTicks;
            if (delta > 0)
            {
                _consecutiveRising++;
                if (_consecutiveRising >= _consecutiveRisingThreshold) Interlocked.Exchange(ref _tripped, 1);
            }
            else _consecutiveRising = 0;
            return IsTripped;
        }

        public bool IsTripped => Volatile.Read(ref _tripped) == 1;
        public long ConsecutiveRising => _consecutiveRising;

        public void Reset()
        {
            Interlocked.Exchange(ref _tripped, 0);
            _consecutiveRising = 0;
            _lastPauseTicks = -1;
        }
    }
}
