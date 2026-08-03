// ============================================================================
// AdminPorts — v10.1.0 Phase 93-B: Admin Ports & Adapters 인터페이스 (순수 계약)
//
//  최고 권한 Admin UI 가 엔진 내부(퀀텀 금고·SQLite/HMAC 감사·DPAPI 시크릿·AI 캐시)에 '직접' 닿지 않도록 하는 Port
//  경계. 어댑터(구현)는 StockManager 측에 두고, UI 는 이 인터페이스 + 순수 DTO 만 안다. ★엔진 타입 참조 0.
//
//  ★보안: IAdminCommandPort.Execute 는 UI 어셈블리가 발급한 '권한 봉인(seal)' + nonce 를 함께 받는다 — 어댑터가
//    AdminAuthoritySealGuard 로 검증해 비인가/rogue 호출(DI 가로채기 등)을 런타임 차단(★Think-Beyond).
// ============================================================================
using System;
using MS2E.UI.Portable.Dto;

namespace MS2E.UI.Portable.Ports
{
    /// <summary>Admin 권한 명령 Port. 어댑터는 seal/nonce 를 검증한 뒤에만 엔진 명령을 라우팅(비인가 차단).</summary>
    public interface IAdminCommandPort
    {
        AdminCommandResult Execute(in AdminCommandIntent intent, long nonce, long seal);
    }

    /// <summary>감사 로그 Port(SQLite/HMAC 어댑터). 순수 DTO 만 반환 — UI 는 엔진 감사 타입 무참조. 반환=기입 개수.</summary>
    public interface IMailAuditLogPort
    {
        int Recent(Span<AdminAuditEntryDto> dest);
    }

    /// <summary>시크릿 Port. 어댑터(DPAPI 금고)가 시크릿을 호출자 char 버퍼(Span&lt;char&gt;)에 직접 기입 — ★string 미생성.
    /// 프레젠터가 이 버퍼를 마스킹 렌더 후 즉시 0x00 소멸(plain 힙 미잔류). 반환=기입 길이.</summary>
    public interface IAdminSecretPort
    {
        int LoadSecret(long secretId, Span<char> dest);
    }
}
