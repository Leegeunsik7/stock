// ============================================================================
// BootIntegrityChecker — S18 #1: 시스템 콜드 하든 · 부팅 시점 정합성 검사 (v3.95.0)
//
//  StateHydrationEngine(MMF 스냅샷) ↔ OrderAttestationService(게이팅 상태기계)를
//  부팅 최선행 시점에 처음으로 결속한다. 그 어떤 브로커 파이프라인·트레이딩 루프가
//  시작되기 전에 호출되어, 이전 세션의 GatingMode를 복원하고 위험 상태면 동결한다.
//
//  ── 결함 동기 ──────────────────────────────────────────────────────────────
//  OrderAttestationService._modeInt 는 volatile int 인메모리 상태다. 앱이
//  EmergencyFreeze 상태에서 비정상 충돌 후 재부팅되면 _modeInt 가 Normal(0)로
//  초기화되어, MMF 스냅샷에 동결 흔적이 남아 있어도 시스템이 무방비로 열린다.
//  BootIntegrityChecker 가 이 무음 탈동기화(silent desync)를 봉쇄한다.
//
//  ── Fail-Closed 원칙 (실패 시 폐쇄) ────────────────────────────────────────
//  트레이딩 게이트에서 "안전 기본값(Safe Default)"은 항상 '닫힘(=동결)'이다.
//    · 복원 모드가 EmergencyFreeze       → ForceFreeze (이전 동결 상태 복원)
//    · 스냅샷 손상/변조 (CRC·스키마 실패) → ForceFreeze (상태 미상 ⇒ 보수적 동결)
//    · 스냅샷 부재 (클린 최초 부팅)        → Normal 유지 (신규 설치를 동결하지 않음)
//    · 정상 복원 (모드=Normal)            → Normal 유지
//  손상 스냅샷을 'Normal'로 간주하면 fail-open 이 되므로, 검증 불가 = 동결이다.
//
//  ── 외부 의존성 0 · 결정론 ─────────────────────────────────────────────────
//  파일 I/O는 StateHydrationEngine 내부에 캡슐화. 본 검사기는 순수 제어 흐름만 담당.
// ============================================================================
using System;

namespace StockManager.Common
{
    // ── 부팅 복원 상태 ─────────────────────────────────────────────────────────

    /// <summary>부팅 시 MMF 스냅샷 복원 결과 분류.</summary>
    public enum HydrationStatus
    {
        /// <summary>스냅샷 파일 없음 — 클린 최초 부팅.</summary>
        NoSnapshot,
        /// <summary>유효 스냅샷에서 상태 복원 완료.</summary>
        Recovered,
        /// <summary>스냅샷 존재하나 무결성 검증 실패(CRC·스키마·변조) — fail-closed 동결.</summary>
        Corrupted,
        /// <summary>유효 복원이나 신선도 한도를 초과한 오래된 스냅샷 — 진단 라벨.</summary>
        Stale
    }

    // ── 부팅 리포트 ────────────────────────────────────────────────────────────

    /// <summary>CheckAndHydrate 1회 실행의 정직한 결과 기록.</summary>
    public sealed record BootReport(
        HydrationStatus Status,
        GatingMode      RecoveredMode,  // 복원 직후 attestation.Mode
        bool            FrozeOnBoot,    // 부팅 무결성 검사가 동결을 강제 적용했는가
        DateTime?       SnapshotUtc,    // 스냅샷 기록 시각(UTC), 없으면 null
        string          Detail);

    // ── 검사기 ─────────────────────────────────────────────────────────────────

    public static class BootIntegrityChecker
    {
        /// <summary>
        /// 부팅 최선행 시점에 호출. 스냅샷을 정직하게 복원해 GatingMode 정합성을 강제한다.
        /// 브로커/체결 파이프라인을 구성하기 <b>이전</b>에 호출해야 fail-closed 보장이 성립한다.
        /// </summary>
        /// <param name="hydration">상태 복원 엔진 (gatingSlot이 등록되어 있어야 함).</param>
        /// <param name="attestation">게이팅 상태기계 — 위험 복원 시 ForceFreeze 대상.</param>
        /// <param name="gatingSlot">Hydrate() 후 복원된 GatingMode(int)를 담는 슬롯.</param>
        /// <param name="freshnessWindow">이 기간을 초과한 스냅샷은 Stale로 분류(진단용). null이면 신선도 검사 생략.</param>
        public static BootReport CheckAndHydrate(
            StateHydrationEngine    hydration,
            OrderAttestationService attestation,
            HydrationSlot<int>      gatingSlot,
            TimeSpan?               freshnessWindow = null)
        {
            if (hydration   is null) throw new ArgumentNullException(nameof(hydration));
            if (attestation is null) throw new ArgumentNullException(nameof(attestation));
            if (gatingSlot  is null) throw new ArgumentNullException(nameof(gatingSlot));

            // ── 1. 스냅샷 부재 → 클린 부팅 ──────────────────────────────────────
            if (!hydration.HasValidSnapshot)
                return new BootReport(
                    HydrationStatus.NoSnapshot, attestation.Mode, false, null,
                    "이전 스냅샷 없음 — 클린 부팅 (정상 모드 유지)");

            // 신선도는 복원 시도 전에 헤더만 읽어 확보 (CRC 검증 포함)
            DateTime? snapUtc = hydration.SnapshotTimeUtc();

            // ── 2. 복원 시도 ────────────────────────────────────────────────────
            bool ok = hydration.Hydrate();
            if (!ok)
            {
                // 파일은 있었으나 무결성 실패 → 상태 미상 ⇒ fail-closed 동결
                attestation.ForceFreeze(
                    "부팅 무결성: 상태 스냅샷 손상·변조 감지 — 안전 동결(fail-closed). " +
                    "/broker/gating 에서 브로커 정합 확인 후 수동 서명 해제 필요.");
                hydration.ClearSnapshot();  // 판독 불가 스냅샷 폐기 (다음 부팅서 재처리 방지)
                return new BootReport(
                    HydrationStatus.Corrupted, attestation.Mode, true, snapUtc,
                    "스냅샷 CRC/스키마 검증 실패 — 안전 동결 후 스냅샷 폐기");
            }

            // ── 3. 복원된 GatingMode 평가 ───────────────────────────────────────
            var  recovered = (GatingMode)gatingSlot.Value;
            bool froze     = false;

            if (recovered == GatingMode.EmergencyFreeze &&
                attestation.Mode != GatingMode.EmergencyFreeze)
            {
                // 이전 세션이 동결 상태였음 — 파이프라인 구성 전에 동결 복원
                attestation.ForceFreeze(
                    "부팅 무결성: 이전 세션 비상 동결 상태 복원 — 트레이딩 파이프라인 차단. " +
                    "/broker/gating 에서 수동 서명 해제 필요.");
                froze = true;
            }

            // ── 4. 신선도 분류 (진단 전용 — 게이트 결정에는 영향 없음) ─────────────
            // 오래된 스냅샷이라도 '동결'은 항상 보존한다(fail-closed). Stale은 정보 라벨일 뿐.
            bool stale = freshnessWindow.HasValue && snapUtc.HasValue &&
                         (DateTime.UtcNow - snapUtc.Value) > freshnessWindow.Value;

            var status = stale ? HydrationStatus.Stale : HydrationStatus.Recovered;

            string detail = froze
                ? $"이전 동결 상태 복원 — 파이프라인 차단 (스냅샷 {Age(snapUtc)})"
                : $"정상 복원 모드={recovered} (스냅샷 {Age(snapUtc)})";

            return new BootReport(status, attestation.Mode, froze, snapUtc, detail);
        }

        private static string Age(DateTime? snapUtc)
            => snapUtc.HasValue
                ? $"{(DateTime.UtcNow - snapUtc.Value).TotalSeconds:N0}초 전"
                : "시각 미상";
    }
}
