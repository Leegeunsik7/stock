// ============================================================================
// AdminSettingWidgetPort — Phase 94-H: 설정/관리자 위젯 Port (순수 UI 경계)
//
//   SettingsPage(거래모드·API키·LAN 상태서버·LLM 백엔드·각종 감시 설정)를 엔진 구상(LocalDatabase 설정/시크릿·
//   BrokerHub·ApiKeyVault·LanStatusServer·AiService)에서 물리 격리한다. 읽기(상태 조회)와 명령(설정 기입)을 분리.
//
//   ★보안: API 키는 '마스킹된 문자열'로만 노출한다(UpbitAccessKeyMasked/KisAccessKeyMasked) — 원본 키/시크릿은
//     RCL 로 유입되지 않는다. 비밀번호류는 SetSecret 으로만 기입(어댑터가 DPAPI 암호화). 평문 키를 순수 UI 가 보유 0.
//   ★liveArmed:false: 설정 조회/기입 전용 — 발주·소켓·브로커 발주 API 무접촉. SetLive 는 '모드 플래그(paper/live)'
//     설정일 뿐 실 발주 무장이 아니다(무장은 물리 이중 키 — CanaryHealthSentinel).
// ============================================================================
namespace MS2E.UI.Portable.Ports
{
    /// <summary>설정/관리자 위젯 Port — 콘크리트(DB/금고/LAN/LLM)를 은닉한 상태 조회 + 설정 기입.</summary>
    public interface IAdminSettingWidgetPort
    {
        // ── 설정 값 (읽기/쓰기) ────────────────────────────────
        string? GetSetting(string key);
        void SetSetting(string key, string value);
        /// <summary>비밀번호/시크릿 기입 — 어댑터가 DPAPI 암호화 저장(평문 미보관).</summary>
        void SetSecret(string key, string value);

        // ── 거래 모드(paper/live 플래그 — 실 발주 무장과 무관) ──
        bool IsLive { get; }
        void SetLive(bool live);

        // ── API 키(마스킹된 읽기 전용 — 원본 미노출) ──────────
        string UpbitAccessKeyMasked { get; }
        string KisAccessKeyMasked { get; }

        // ── LAN 상태 서버 ──────────────────────────────────────
        bool LanRunning { get; }
        int LanPort { get; }
        string LanLastError { get; }
        void RestartLan();

        // ── LLM 백엔드 ─────────────────────────────────────────
        string ActiveLlm { get; }
    }
}
