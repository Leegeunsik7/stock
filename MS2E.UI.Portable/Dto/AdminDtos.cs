// ============================================================================
// AdminDtos — v10.1.0 Phase 93-B: 순수 Admin DTO (Pure DTO Transformation Arc Slice 2)
//
//  UI ↔ Core 경계를 넘는 순수 페이로드. ★엔진 타입 참조 0(값타입·record struct·δ=0). Mail.razor 교훈: UI 낙관적
//  업데이트용 '가변' 뷰모델(AdminViewModel)과 Core 의 '불변' DTO(AdminCommandIntent/AdminAuditEntryDto)를 명명·역할로
//  명확히 분리 — 가변/불변 혼동으로 인한 상태 파손을 원천 차단.
// ============================================================================
namespace MS2E.UI.Portable.Dto
{
    /// <summary>Admin 권한 명령 종류.</summary>
    public enum AdminCommandKind : byte
    {
        None            = 0,
        QuantumVaultArm = 1,   // 퀀텀 금고 수동 무장
        QuantumVaultDisarm = 2, // 퀀텀 금고 수동 해제
        AiCacheInvalidate = 3, // AI 캐시 무효화
        AuditLogSeal    = 4,   // 감사 로그 봉인
        SecretRotate    = 5,   // 시크릿 회전
    }

    /// <summary>★불변 Intent DTO(δ=0·값타입). UI→Port 전송 페이로드(순수·엔진 타입 0).</summary>
    public readonly record struct AdminCommandIntent(AdminCommandKind Kind, long Arg0, long Arg1, long IssuedMicros);

    /// <summary>Port 실행 결과(불변).</summary>
    public readonly record struct AdminCommandResult(bool Ok, long Code, long DetailA);

    /// <summary>★불변 감사 엔트리 DTO(Core→UI 표시용·순수). 엔진 감사 레코드를 UI 로 안전 투영.</summary>
    public readonly record struct AdminAuditEntryDto(long Sequence, long TimestampMicros, long EventCode, long Detail, long ForensicHash);

    /// <summary>
    /// ★가변 UI 뷰모델(낙관적 in-place mutation 전용). ★불변 DTO 와 명명·역할 분리(Mail.razor 교훈): 이 타입만 razor 에서
    /// 필드 변이하고, Port 로 넘기는 것은 항상 불변 Intent DTO 로 스냅샷한다. Frozen 은 freeze-safe purge 시 세팅.
    /// </summary>
    public sealed class AdminViewModel
    {
        public bool VaultArmedOptimistic;   // 낙관적 표시(Port 확정 전 즉시 반영)
        public long PendingCommands;        // 인-플라이트 명령 수
        public long LastResultCode;         // 최근 Port 결과 코드
        public bool Frozen;                 // ★비상 동결/revoke 시 true → 안전 관제 모드
        public int  MaskedSecretLen;        // 마스킹된 시크릿 표시 길이(plain 미보관)
    }
}
