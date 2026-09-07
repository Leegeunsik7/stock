// ============================================================================
// HardwarePulseGuard + ZeroAllocMemoryProfiler — v12.0.2 Phase Field Ops Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 런북+워치독'이 다루지 않는 실전 파멸 리스크):
//     ① "하드웨어 접점 바운싱(Jitter)으로 인한 미세 무장/격발 오작동" — 물리 버튼/릴레이 접점은 한 번 눌러도
//        수 ms 동안 수십 개의 전기 에지를 튀긴다(chatter). 이를 그대로 인터럽트로 받으면 하나의 물리 동작이
//        수십 개 격발/토글로 증폭되어 상태기계가 요동친다. 더 나쁜 것은 전기 노이즈/위조 신호가 정당한
//        인터럽트로 위장해 주입되는 경우다.
//     ② "장기 관제 중 미세 메모리 누수 누적" — nightly 검증에서 1 Byte 가비지도 누적되면 24시간×수일 후 GC 스톨.
//
//   ── Think Beyond (이중 방어 엔진) ─────────────────────────────────────────────
//     ① Physical Hardware Pulse Guard — 외부 하드웨어 인터럽트에 ★단조 증가 펄스 토큰★ + ★디바운스 창★ 을 결속.
//        · 역행/재생 토큰(seq ≤ 마지막 수용) = 위조/재생 공격 → Tamper 판정(fail-closed: 즉시 킬스위치 격발).
//        · 창(debounceTicks) 안의 후속 펄스 = 접점 바운스 → Debounced(억제, 증폭 차단).
//        · 창 밖의 단조 전진 펄스 = 정당한 물리 동작 → Accept(격발). 판정 δ=0·락프리·결정론(주입 tick).
//     ② Zero-Alloc Nightly Memory Profiler — 스레드 누적할당 + 세대(Gen0/1/2) 수집 카운트를 O(1) δ=0 스냅샷으로
//        포착·비교. nightly 검증 시 워크로드 전후 1 Byte 누출도 불허(Gen2 델타 = LOH/Gen2 압력 프록시).
//
//   불변: 결정론(주입 tick·순수 비교) · δ=0(Evaluate/Capture 핫패스 힙 0) · fail-closed(위조/미초기화 → 안전측) ·
//        락프리(Interlocked/Volatile) · liveArmed:false 무관(신호 무결성·측정 전용).
//   ※ MS2E.Core(최내부·순수) — 상위 레이어가 하드웨어 ISR → PulseGuard.ShouldFire → HardwareKillSwitchBridge.Signal 로 결선.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    public enum PulseVerdict : byte
    {
        Accept    = 0,   // 정당한 물리 동작(단조 전진 + 디바운스 창 밖) → 격발
        Debounced = 1,   // 접점 바운스(창 안 후속 펄스) → 억제
        Tamper    = 2,   // 역행/재생 토큰(위조 신호) → fail-closed 격발
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Physical Hardware Pulse Guard (단조 토큰 + 디바운스 안티탬퍼)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class HardwarePulseGuard
    {
        private readonly long _debounceTicks;   // 디바운스 창(주입 tick 단위 — 호스트 tick 도메인과 동일)
        private long _lastSeq;                   // 마지막 수용 펄스 시퀀스(단조)
        private long _lastAcceptTicks;
        private long _initialized;               // 0=미초기화(첫 펄스는 무조건 수용)
        private long _accepted, _debounced, _tampers;

        public HardwarePulseGuard(long debounceTicks)
            => _debounceTicks = debounceTicks > 0 ? debounceTicks : 1;

        /// <summary>
        /// 하드웨어 펄스 1건 판정(δ=0·락프리). pulseSeq=하드웨어 단조 카운터, nowTicks=수신 tick.
        ///   · 초기화 후 pulseSeq ≤ 마지막수용 → Tamper(역행/재생 = 위조).
        ///   · 디바운스 창 안(now-lastAccept < debounceTicks) → Debounced(바운스 억제·seq 단조 전진만).
        ///   · 창 밖 단조 전진 → Accept(정당 동작·상태 갱신).
        /// </summary>
        public PulseVerdict Evaluate(long pulseSeq, long nowTicks)
        {
            var sw = new SpinWait();
            while (true)
            {
                long last = Volatile.Read(ref _lastSeq);
                bool init = Volatile.Read(ref _initialized) == 1;

                if (init && pulseSeq <= last)   // ★역행/재생 = 위조 신호(fail-closed)
                {
                    Interlocked.Increment(ref _tampers);
                    return PulseVerdict.Tamper;
                }
                if (init && (nowTicks - Volatile.Read(ref _lastAcceptTicks)) < _debounceTicks)
                {
                    // 접점 바운스 — 단조성 유지 위해 seq 만 best-effort 전진(격발 억제).
                    Interlocked.CompareExchange(ref _lastSeq, pulseSeq, last);
                    Interlocked.Increment(ref _debounced);
                    return PulseVerdict.Debounced;
                }
                // 정당 동작 — 단조 전진 + 수용 시각/초기화 원자 갱신.
                if (Interlocked.CompareExchange(ref _lastSeq, pulseSeq, last) == last)
                {
                    Volatile.Write(ref _lastAcceptTicks, nowTicks);
                    Volatile.Write(ref _initialized, 1);
                    Interlocked.Increment(ref _accepted);
                    return PulseVerdict.Accept;
                }
                sw.SpinOnce();   // CAS 경합 재시도
            }
        }

        /// <summary>
        /// 이 펄스가 킬스위치를 격발해야 하는가. ★정당 동작(Accept)과 위조(Tamper)는 모두 격발(fail-closed —
        /// 위조 신호도 안전측 Disarm 으로 귀결). 바운스(Debounced)만 억제. verdict 로 사유 반환.
        /// </summary>
        public bool ShouldFire(long pulseSeq, long nowTicks, out PulseVerdict verdict)
        {
            verdict = Evaluate(pulseSeq, nowTicks);
            return verdict != PulseVerdict.Debounced;
        }

        public long AcceptedCount  => Interlocked.Read(ref _accepted);
        public long DebouncedCount => Interlocked.Read(ref _debounced);
        public long TamperCount    => Interlocked.Read(ref _tampers);
        public long LastSeq        => Volatile.Read(ref _lastSeq);

        public void Reset()
        {
            Volatile.Write(ref _lastSeq, 0);
            Volatile.Write(ref _lastAcceptTicks, 0);
            Volatile.Write(ref _initialized, 0);
            Interlocked.Exchange(ref _accepted, 0);
            Interlocked.Exchange(ref _debounced, 0);
            Interlocked.Exchange(ref _tampers, 0);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Zero-Alloc Nightly Memory Profiler (O(1) δ=0 누수 스냅샷)
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>메모리 스냅샷(unmanaged — 힙 0 판독). Gen2 델타 = LOH/Gen2 압력 프록시(LOH 는 Gen2 에서 수집).</summary>
    public readonly record struct MemSnapshot(long ThreadAllocBytes, int Gen0, int Gen1, int Gen2);

    public static class ZeroAllocMemoryProfiler
    {
        /// <summary>O(1) δ=0 스냅샷 — 스레드 누적할당 + 세대 수집 카운트(전부 힙 0 판독).</summary>
        public static MemSnapshot Capture()
            => new(GC.GetAllocatedBytesForCurrentThread(),
                   GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));

        public static long LeakBytes(in MemSnapshot before, in MemSnapshot after)
            => after.ThreadAllocBytes - before.ThreadAllocBytes;

        public static int Gen2Delta(in MemSnapshot before, in MemSnapshot after)
            => after.Gen2 - before.Gen2;

        /// <summary>nightly 무결성: 워크로드 전후 1 Byte 누출도 없어야 true.</summary>
        public static bool NoLeak(in MemSnapshot before, in MemSnapshot after)
            => LeakBytes(before, after) == 0;

        /// <summary>
        /// δ=0 워크로드 누수 측정 헬퍼 — warmup 후 iters 구간의 힙 증가 바이트 반환(0 이어야 함).
        /// ★hot 델리게이트는 캡처 이전 1회 생성(측정 구간 밖) → hot 자체의 할당만 측정.
        /// </summary>
        public static long MeasureLeak(int warmup, int iters, Action hot)
        {
            if (hot is null) return 0;
            for (int i = 0; i < warmup; i++) hot();
            var before = Capture();
            for (int i = 0; i < iters; i++) hot();
            var after = Capture();
            return LeakBytes(before, after);
        }
    }
}
