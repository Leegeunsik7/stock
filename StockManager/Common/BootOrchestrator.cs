// ============================================================================
// BootOrchestrator — S24-b 미션1: 가역적 안전 부팅 시퀀스 (결정론 합성)
//
//  CrashRecoveryJournaler(저널 무결성) → BootIntegrityChecker(하이드레이션 게이트)를 단조-동결
//  순서로 합성한다. BootIntegrityChecker/BootReport/CheckAndHydrate 시그니처는 한 글자도 불변.
//  저널은 옵트인 — journaler==null 이면 게이트를 건너뛰어 기존 CheckAndHydrate 단독과 바이트 동일.
//
//  ── 단방향·무순환·단조-동결 순서 ───────────────────────────────────────────
//  (1) journaler.RecoverOrFreeze : 내용 오염(Corrupt)이면 ForceFreeze(파일 보존). I/O 접근불가
//      (Inaccessible)는 동결하지 않음 — 권위 있는 게이팅 검증은 (3)이 담당(자가당착 차단).
//  (3) BootIntegrityChecker.CheckAndHydrate : 하이드레이션 스냅샷으로 게이팅 모드 복원·검증.
//      (1)이 이미 EmergencyFreeze 면 내부 동결 분기가 자연 단락(ForceFreeze monotone).
//  순서 역전 금지 근거: (3)을 먼저 돌리면 클린 복원이 Normal 을 스냅샷에 박은 직후 (1) 동결 →
//  그 사이 재충돌 시 디스크엔 Normal 만 남아 fail-open. (1)→(3) 고정이 그 창을 제거한다.
//
//  ── 가역성 ──────────────────────────────────────────────────────────────────
//  부팅은 '동결만 적용·해제는 절대 안 함'(monotone). 해제는 OrderAttestationService.ManualRelease
//  단일 서명 경로. 오염 저널은 보존(비파괴) → 멱등 재부팅. 클린 부팅 후 플러시 펌프가 덮어쓴다.
// ============================================================================
using System;

namespace StockManager.Common
{
    public enum BootSafety : byte { Clean, Recovered, Frozen }

    public sealed record BootSequenceReport(
        BootReport    Integrity,    // 기존 record 재사용 — 시그니처 불변
        JournalStatus Journal,
        BootSafety    Safety,
        string        Detail);

    public static class BootOrchestrator
    {
        /// <summary>
        /// 안전 부팅 시퀀스를 단조-동결 순서로 실행. 브로커 파이프라인 구성 '이전'에 호출해야 한다.
        /// journaler==null 이면 저널 게이트 스킵(후방호환 100% — CheckAndHydrate 단독과 동일).
        /// </summary>
        public static BootSequenceReport RunSafeBoot(
            StateHydrationEngine    hydration,
            OrderAttestationService attestation,
            HydrationSlot<int>      gatingSlot,
            CrashRecoveryJournaler? journaler   = null,
            Span<JournalRecord>     journalDest = default,
            TimeSpan?               freshness   = null)
        {
            if (hydration   is null) throw new ArgumentNullException(nameof(hydration));
            if (attestation is null) throw new ArgumentNullException(nameof(attestation));
            if (gatingSlot  is null) throw new ArgumentNullException(nameof(gatingSlot));

            // ── (1) 저널 무결성 게이트 — 가장 먼저. Corrupt → 동결(파일 보존), Inaccessible → 비동결 ──
            var jStatus = JournalStatus.Empty;
            if (journaler is not null)
                jStatus = journaler.RecoverOrFreeze(attestation, journalDest, out _);

            // ── (3) 하이드레이션 게이트 — 시그니처 불변 호출. (1)이 동결시켰으면 분기 자연 단락 ──
            var integ = BootIntegrityChecker.CheckAndHydrate(hydration, attestation, gatingSlot, freshness);

            // ── 합산 : 둘 중 하나라도 동결이면 Frozen(fail-closed 우선) ──
            var safety =
                attestation.Mode == GatingMode.EmergencyFreeze ? BootSafety.Frozen
              : integ.Status == HydrationStatus.NoSnapshot      ? BootSafety.Clean
              :                                                   BootSafety.Recovered;

            return new BootSequenceReport(integ, jStatus, safety,
                $"무결성={integ.Status} 저널={jStatus} → {safety}");
        }
    }
}
