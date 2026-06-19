// ============================================================================
// SystemStatusAggregator — S18 #2: 전체 관제 탑 중앙 허브 (v3.96.0)
//
//  시스템 전역의 4대 핵심 센서를 단일 불변 스냅샷(SystemHealthSnapshot)으로 취합한다.
//    ① 원장 정합성      — OrderAttestationService.Mode (GatingMode)
//    ② 메모리 릭 프로브  — MemoryLeakGuard.Probe()
//    ③ 어댑티브 튜너     — AdaptiveExecutionTuner.IsFrozen / Slices / Duration
//    ④ 파이프 연결       — KiwoomBridgeClient.IsProxyAlive
//
//  ── 스레드 안전 · Zero-Allocation 지향 ─────────────────────────────────────
//  · 복잡한 동기화 락 없음. 각 센서는 자체 volatile/Interlocked 원자 읽기를 제공하므로
//    Capture() 는 락 없이 순간값을 복사해 readonly struct 로 반환한다.
//  · SystemHealthSnapshot 은 값 타입(struct) — GC 힙 할당 0 (3초 타이머 틱당 1회 복사).
//  · 모든 센서 참조는 nullable — 미주입/크래시 시 NRE 대신 안전 기본값(Notice)로 폴백.
//
//  ── 심각도 전역 동기화 ─────────────────────────────────────────────────────
//  하부 4개 중 단 하나라도 동결/누수 상태면 Overall = max(심각도) 로 즉시 승격된다.
// ============================================================================
using System;

namespace StockManager.Common
{
    // ── 심각도 (정렬 가능: Ok < Notice < Warning < Critical) ────────────────────

    public enum SystemSeverity
    {
        Ok       = 0,   // 정상
        Notice   = 1,   // 주의 — 미배선/대기 등 비오류 상태
        Warning  = 2,   // 경고 — 누수 의심 등
        Critical = 3    // 위험 — 동결 등 즉시 개입 필요
    }

    // ── 불변 헬스 스냅샷 (readonly struct) ──────────────────────────────────────

    /// <summary>
    /// 시스템 4대 센서의 한 순간 상태. 값 타입이며 모든 필드는 원자 읽기 결과.
    /// 파생 심각도(Overall 등)는 원시 필드에서 결정론적으로 계산된다(단일 진실 원천).
    /// </summary>
    public readonly struct SystemHealthSnapshot
    {
        // ① 원장 정합성
        public bool       AttestationPresent { get; init; }
        public GatingMode GatingMode         { get; init; }

        // ② 메모리
        public bool MemoryPresent    { get; init; }
        public int  LiveLeases       { get; init; }
        public long WorkingSetBytes  { get; init; }
        public long ManagedHeapBytes { get; init; }
        public int  Gen2Collections  { get; init; }
        public bool LeakSuspected    { get; init; }

        // ③ 어댑티브 튜너
        public bool TunerPresent     { get; init; }
        public bool TunerFrozen      { get; init; }
        public int  TunerSlices      { get; init; }
        public int  TunerDurationSec { get; init; }

        // ④ 파이프 브리지
        public bool BridgePresent { get; init; }
        public bool BridgeAlive   { get; init; }

        // 취합 시각
        public DateTime CapturedUtc { get; init; }

        // ── 파생 심각도 (원시 필드 기반 결정론 계산) ───────────────────────────

        public SystemSeverity AttestationSeverity =>
            !AttestationPresent                       ? SystemSeverity.Notice
          : GatingMode == GatingMode.EmergencyFreeze  ? SystemSeverity.Critical
          :                                             SystemSeverity.Ok;

        public SystemSeverity MemorySeverity =>
            !MemoryPresent ? SystemSeverity.Notice
          : LeakSuspected  ? SystemSeverity.Warning
          :                  SystemSeverity.Ok;

        public SystemSeverity TunerSeverity =>
            !TunerPresent ? SystemSeverity.Notice
          : TunerFrozen   ? SystemSeverity.Critical
          :                 SystemSeverity.Ok;

        public SystemSeverity BridgeSeverity =>
            !BridgePresent ? SystemSeverity.Notice
          : BridgeAlive    ? SystemSeverity.Ok
          :                  SystemSeverity.Notice;   // 미연결은 대기(비오류)

        /// <summary>4개 사분면 중 최고 심각도 — 중앙 인디케이터 색상 결정.</summary>
        public SystemSeverity Overall
        {
            get
            {
                var m = AttestationSeverity;
                if (MemorySeverity > m) m = MemorySeverity;
                if (TunerSeverity  > m) m = TunerSeverity;
                if (BridgeSeverity > m) m = BridgeSeverity;
                return m;
            }
        }
    }

    // ── 중앙 허브 ────────────────────────────────────────────────────────────────

    public sealed class SystemStatusAggregator
    {
        private readonly OrderAttestationService? _attestation;
        private readonly MemoryLeakGuard?         _memGuard;
        private readonly AdaptiveExecutionTuner?  _tuner;
        private readonly KiwoomBridgeClient?      _bridge;

        /// <summary>
        /// 모든 센서는 선택적. null이면 해당 사분면은 안전 기본값(Notice·미배선)으로 폴백한다.
        /// </summary>
        public SystemStatusAggregator(
            OrderAttestationService? attestation = null,
            MemoryLeakGuard?         memGuard    = null,
            AdaptiveExecutionTuner?  tuner       = null,
            KiwoomBridgeClient?      bridge      = null)
        {
            _attestation = attestation;
            _memGuard    = memGuard;
            _tuner       = tuner;
            _bridge      = bridge;
        }

        /// <summary>
        /// 현재 센서 상태를 한 순간으로 취합한다. 락 없음·예외 없음(개별 센서 읽기 실패는
        /// 안전 기본값으로 흡수). 반환 struct 는 GC 힙 할당이 없다.
        /// </summary>
        public SystemHealthSnapshot Capture()
        {
            // ① 원장 정합성
            bool       attPresent = _attestation is not null;
            GatingMode mode       = GatingMode.Normal;
            try { if (attPresent) mode = _attestation!.Mode; }
            catch { attPresent = false; }

            // ② 메모리 — Probe()는 GC/WorkingSet 포함 단일 스냅샷
            bool memPresent = _memGuard is not null;
            int  liveLeases = 0; long ws = 0L, heap = 0L; int gen2 = 0; bool leak = false;
            if (memPresent)
            {
                try
                {
                    var r = _memGuard!.Probe();
                    liveLeases = r.LiveLeases;
                    ws         = r.WorkingSetBytes;
                    heap       = r.ManagedHeapBytes;
                    gen2       = r.Gen2Collections;
                    leak       = r.LeakSuspected;
                }
                catch { memPresent = false; }
            }

            // ③ 어댑티브 튜너
            bool tunerPresent = _tuner is not null;
            bool tunerFrozen  = false; int slices = 0, durSec = 0;
            if (tunerPresent)
            {
                try
                {
                    tunerFrozen = _tuner!.IsFrozen;
                    slices      = _tuner.CurrentSlices;
                    durSec      = (int)_tuner.CurrentDuration.TotalSeconds;
                }
                catch { tunerPresent = false; }
            }

            // ④ 파이프 브리지
            bool bridgePresent = _bridge is not null;
            bool bridgeAlive   = false;
            if (bridgePresent)
            {
                try { bridgeAlive = _bridge!.IsProxyAlive; }
                catch { bridgePresent = false; }
            }

            return new SystemHealthSnapshot
            {
                AttestationPresent = attPresent,
                GatingMode         = mode,
                MemoryPresent      = memPresent,
                LiveLeases         = liveLeases,
                WorkingSetBytes    = ws,
                ManagedHeapBytes   = heap,
                Gen2Collections    = gen2,
                LeakSuspected      = leak,
                TunerPresent       = tunerPresent,
                TunerFrozen        = tunerFrozen,
                TunerSlices        = slices,
                TunerDurationSec   = durSec,
                BridgePresent      = bridgePresent,
                BridgeAlive        = bridgeAlive,
                CapturedUtc        = DateTime.UtcNow,
            };
        }

        // ── UI 매핑 (순수 함수 — 테스트 가능) ──────────────────────────────────

        public static string SeverityCssClass(SystemSeverity s) => s switch
        {
            SystemSeverity.Critical => "sev-critical",
            SystemSeverity.Warning  => "sev-warning",
            SystemSeverity.Notice   => "sev-notice",
            _                       => "sev-ok",
        };

        public static string SeverityLabel(SystemSeverity s) => s switch
        {
            SystemSeverity.Critical => "위험",
            SystemSeverity.Warning  => "경고",
            SystemSeverity.Notice   => "주의",
            _                       => "정상",
        };
    }
}
