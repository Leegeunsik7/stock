// ============================================================================
// FrictionAdapter — S16 #1: 자가 적응형 마찰 제어 루프 코어
//
//  FrictionSnapshot          — 제로-할당 readonly struct (스택 전달)
//  FrictionFeedbackBridge    — SlippageSentinel 통계를 스냅샷으로 노출하는 고속 어댑터
//  AdaptiveExecutionTuner    — Z-score 추이 기반 결정론적 파라미터 조정기
//
//  ┌─────────────────────────────────────────────────────────────────────────┐
//  │ SlippageSentinel ─(LastZ·μ·σ)──► FrictionFeedbackBridge                │
//  │                                         │                              │
//  │                                  FrictionSnapshot (stack struct)       │
//  │                                         │                              │
//  │                                  AdaptiveExecutionTuner                │
//  │                                  (Interlocked CAS, zero-alloc)         │
//  │                                         │                              │
//  │                                  CurrentSlices · CurrentDuration       │
//  │                                         │                              │
//  │                                  TwapVwapExecutor.Place()              │
//  └─────────────────────────────────────────────────────────────────────────┘
//
//  Thread-Safety 보장:
//  · SlippageSentinel 필드(mean, m2, n)는 eventually-consistent 읽기 허용 —
//    적응형 튜너에 필요한 정밀도는 정확한 순간 동기화가 아닌 추세이기 때문.
//  · LastZ, CurrentSlices, CurrentDuration: Interlocked 원자 읽기/쓰기 보장.
//  · AdaptiveExecutionTuner._packed: 단일 long CAS — 슬라이스·시간을 항상 일관된 쌍으로 갱신.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StockManager.Common
{
    // ── 제로-할당 통계 스냅샷 (readonly struct → 스택 전달) ──────────────────

    /// <summary>
    /// 마찰 피드백 파이프라인을 통해 전달되는 슬리피지 통계 순간 값.
    /// readonly struct 이므로 스택에서만 생성되며 GC 힙 할당이 0 입니다.
    /// </summary>
    public readonly struct FrictionSnapshot
    {
        public readonly double Mean;         // 슬리피지 잔차 실행 평균 (Welford μ)
        public readonly double Sigma;        // 슬리피지 잔차 표본 표준편차 (Welford σ)
        public readonly double LastZ;        // 최근 체결의 절대 Z-score
        public readonly int    SampleCount;  // 누적 측정 건수

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FrictionSnapshot(double mean, double sigma, double lastZ, int sampleCount)
        {
            Mean = mean; Sigma = sigma; LastZ = lastZ; SampleCount = sampleCount;
        }
    }

    // ── FrictionFeedbackBridge ────────────────────────────────────────────────

    /// <summary>
    /// SlippageSentinel 의 내부 통계를 FrictionSnapshot 으로 노출하는 고속 어댑터.
    /// GetSnapshot() 은 힙 할당 없이 호출 스레드 스택에서만 동작합니다.
    /// </summary>
    public sealed class FrictionFeedbackBridge
    {
        private readonly SlippageSentinel _sentinel;

        public FrictionFeedbackBridge(SlippageSentinel sentinel)
            => _sentinel = sentinel ?? throw new ArgumentNullException(nameof(sentinel));

        /// <summary>
        /// 현재 센티넬 통계를 FrictionSnapshot 으로 반환합니다.
        /// Zero-alloc: struct 는 호출 스레드 스택에 생성됩니다.
        /// eventually-consistent: mean/sigma 는 락 없이 읽으므로 미세한 스테일 허용.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FrictionSnapshot GetSnapshot() => new FrictionSnapshot(
            _sentinel.RunningMean,
            _sentinel.RunningStdDev,
            _sentinel.LastZ,
            _sentinel.SampleCount);
    }

    // ── AdaptiveExecutionTuner ────────────────────────────────────────────────

    /// <summary>
    /// 슬리피지 Z-score 추이를 분석해 TwapVwapExecutor 의 Slices 와 Duration 을 실시간 보정.
    ///
    /// 결정론적 보정 규칙:
    ///   |Z| > HighZThreshold → Slices += SliceStepUp,  Duration += DurationStep  (마찰 강화)
    ///   |Z| &lt; LowZThreshold  → Slices -= SliceStepDown, Duration -= DurationStep (마찰 약화)
    ///   otherwise            → 유지
    ///
    /// 하드 캘리브레이션 캡:
    ///   Slices   ∈ [MinSlices(2) .. MaxSlices(64)]
    ///   Duration ∈ [MinDuration(10s) .. MaxDuration(300s)]
    ///
    /// Calibrate() 는 GC 힙 할당 0 — Interlocked CAS + 비트패킹만 사용.
    /// </summary>
    public sealed class AdaptiveExecutionTuner
    {
        // ── 하드 캘리브레이션 캡 ───────────────────────────────────────────────

        public int      MinSlices    { get; init; } = 2;
        public int      MaxSlices    { get; init; } = 64;
        public TimeSpan MinDuration  { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan MaxDuration  { get; init; } = TimeSpan.FromSeconds(300);

        // ── 보정 스텝 크기 ────────────────────────────────────────────────────

        public int    SliceStepUp    { get; init; } = 2;
        public int    SliceStepDown  { get; init; } = 1;
        public double DurationStepSec { get; init; } = 10.0;

        // ── Z-score 임계치 ────────────────────────────────────────────────────

        /// <summary>이 값을 초과하면 마찰 강화 (슬라이스·시간 확장).</summary>
        public double HighZThreshold { get; init; } = 1.5;
        /// <summary>이 값 미만이면 마찰 약화 (슬라이스·시간 축소).</summary>
        public double LowZThreshold  { get; init; } = 0.5;
        /// <summary>이 건수 미만이면 통계 불충분으로 Calibrate() 를 건너뜀.</summary>
        public int    MinSamples     { get; init; } = 4;

        // ── S17 #2: 캡 연속 위반 감시 + 안전 동결 ────────────────────────────
        // MaxSlices 또는 MaxDuration 캡에 CapBreachThreshold 회 연속 도달하면
        // _frozenFlag = 1 로 설정하고 OnCapBreached 콜백을 발행.
        // IsFrozen = true 이후 Calibrate() 및 TwapVwapExecutor.Place() 모두 차단.

        /// <summary>이 횟수 연속으로 하드 캡에 도달하면 안전 동결 및 CRITICAL 경보 발행.</summary>
        public int CapBreachThreshold { get; init; } = 5;

        /// <summary>캡 연속 위반 확정 시 발행되는 콜백. (symbol, message) 쌍. ComplianceBridge 가 배선.</summary>
        public Action<string, string>? OnCapBreached { get; set; }

        private int          _capHitCount;  // Interlocked — 연속 캡 도달 횟수
        private volatile int _frozenFlag;   // 0=정상  1=안전 동결

        /// <summary>안전 동결 상태. true 이면 Calibrate 및 알고 집행이 즉시 차단됩니다.</summary>
        public bool IsFrozen => _frozenFlag != 0;

        // ── 내부 상태 (비트패킹, Interlocked CAS) ─────────────────────────────
        // _packed: 상위 32비트 = DurationSec(int), 하위 32비트 = Slices(int)
        // 두 값을 단일 long CAS 로 항상 일관된 쌍으로 원자 갱신.

        private long _packed;
        private readonly FrictionFeedbackBridge _bridge;

        public AdaptiveExecutionTuner(
            FrictionFeedbackBridge bridge,
            int                    initialSlices,
            TimeSpan               initialDuration)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _packed = Pack(
                Math.Clamp(initialSlices, MinSlices, MaxSlices),
                ClampDurSec(initialDuration));
        }

        // ── 현재 파라미터 읽기 (Interlocked, zero-alloc) ─────────────────────

        public int      CurrentSlices
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => UnpackSlices(Interlocked.Read(ref _packed));
        }

        public TimeSpan CurrentDuration
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TimeSpan.FromSeconds(UnpackDurSec(Interlocked.Read(ref _packed)));
        }

        // ── 결정론적 보정 (Calibrate) — Zero-alloc ───────────────────────────

        /// <summary>
        /// FrictionFeedbackBridge 에서 최신 스냅샷을 읽어 파라미터를 보정합니다.
        /// 이 메서드는 GC 힙 할당이 정확히 0 입니다 (Interlocked + 비트패킹만 사용).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Calibrate()
        {
            // S17 #2: 안전 동결 상태이면 즉시 차단
            if (_frozenFlag != 0) return;

            FrictionSnapshot snap = _bridge.GetSnapshot();  // stack-allocated struct

            // 통계 불충분 또는 표준편차 미형성 → 건너뜀
            if (snap.SampleCount < MinSamples || snap.Sigma < 1e-12) return;

            long cur    = Interlocked.Read(ref _packed);
            int  slices = UnpackSlices(cur);
            int  durSec = UnpackDurSec(cur);

            double z = Math.Abs(snap.LastZ);

            if (z > HighZThreshold)
            {
                // 마찰 강화: 슬라이스 세분화 + 집행 시간 연장
                slices = Math.Min(slices + SliceStepUp,              MaxSlices);
                durSec = Math.Min(durSec + (int)DurationStepSec, (int)MaxDuration.TotalSeconds);
            }
            else if (z < LowZThreshold)
            {
                // 마찰 약화: 기회비용 절감을 위해 점진적 축소
                slices = Math.Max(slices - SliceStepDown,            MinSlices);
                durSec = Math.Max(durSec - (int)DurationStepSec, (int)MinDuration.TotalSeconds);
            }
            // else: 유지 구간 — CAS 없이 그대로

            Interlocked.Exchange(ref _packed, Pack(slices, durSec));

            // S17 #2: 캡 연속 위반 감시
            // MaxSlices 또는 MaxDuration 에 도달했는지 확인
            bool atCap = slices >= MaxSlices || durSec >= (int)MaxDuration.TotalSeconds;
            int  count = atCap
                ? Interlocked.Increment(ref _capHitCount)
                : Interlocked.Exchange(ref _capHitCount, 0);  // 연속 깨짐 → 카운터 리셋

            if (atCap && count >= CapBreachThreshold && _frozenFlag == 0)
            {
                // 안전 동결 + CRITICAL 경보 — 한 번만 실행 (CAS 로 중복 방지)
                if (Interlocked.CompareExchange(ref _frozenFlag, 1, 0) == 0)
                {
                    OnCapBreached?.Invoke("SYSTEM",
                        $"캡 연속 {count}회 도달 — Slices={slices}/{MaxSlices} DurSec={durSec}/{(int)MaxDuration.TotalSeconds}");
                }
            }
        }

        // ── 비트패킹 유틸리티 ─────────────────────────────────────────────────

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long Pack(int slices, int durSec)
            => ((long)durSec << 32) | (uint)slices;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int UnpackSlices(long p) => (int)(p & 0xFFFF_FFFF);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int UnpackDurSec(long p) => (int)(p >> 32);

        private int ClampDurSec(TimeSpan d)
            => Math.Clamp((int)d.TotalSeconds,
                          (int)MinDuration.TotalSeconds,
                          (int)MaxDuration.TotalSeconds);
    }
}
