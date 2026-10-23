// ============================================================================
// ArenaPreferencePort — v12.18.69-DEV: 아레나 사용자 선호 영속화 UI 경계(플루토 프로 거버너 토글 · 순수 · DB 타입 참조 0)
//
//   /dualarena 토글 결과를 호스트 로컬 DB 설정 테이블(%LOCALAPPDATA%\StockManager\portfolio.db · 키 Arena.PlutoGovernanceEnabled)에 비동기 저장한다.
//   기동 시 복원은 호스트 RuntimeConfig(환경변수 → DB 설정 → 기본 true)가 같은 키를 읽어 3대 아레나 엔진에 주입 — UI 는 저장만 한다.
//   ★경계: 이 포트는 고정 키 1개에 불리언만 기입한다(임의 키 기입 메서드 없음 → UiSettingWriteGuard 허용 목록 확장 불필요 · 안전 설정 덮어쓰기 경로 0).
//   ★비동기: 저장은 스레드 풀에서 실행(UI 렌더 무차단) · 연속 토글 = 단일 작성자가 최종값으로 수렴(역순 기입 0) · 실패 = 계수 + false(자동 재시도 없음).
//   [미결선] UnwiredArenaPreferencePort = 저장소 없는 셸의 null 객체(페이지는 포트를 직접 주입 — 서비스 로케이터 0).
//   ★liveArmed:false — 선호는 AI 가상 원장 진입 판정 스위치뿐(무장 · 브로커 · 실주문 키 기입 수단 0).
// ============================================================================
using System.Threading.Tasks;

namespace MS2E.UI.Portable.Ports.Trading
{
    public interface IArenaPreferencePort
    {
        /// <summary>저장된 거버너 선호(미저장 · 해석 불가 · 판독 실패 = null).</summary>
        bool? StoredGovernanceEnabled { get; }
        /// <summary>거버너 선호 비동기 저장. 완료 = true · 실패 = false(이전 저장값 유지). 연속 호출 = 마지막 값으로 수렴.</summary>
        Task<bool> SaveGovernanceEnabledAsync(bool enabled);
        /// <summary>누적 저장 실패 건수(관측 전용).</summary>
        long SaveFailures { get; }
        /// <summary>저장소 결선 여부(false = 미결선 null 객체 — UI 는 '재기동 시 설정 기본값으로 복귀'를 정직 표시). 기본 구현 = true.</summary>
        bool IsWired => true;
    }

    /// <summary>저장소 미결선 null 객체(호스트 DB 없는 셸 · 테스트 하네스). 저장 = 항상 false · 기록 0.</summary>
    public sealed class UnwiredArenaPreferencePort : IArenaPreferencePort
    {
        public static readonly UnwiredArenaPreferencePort Instance = new();
        private UnwiredArenaPreferencePort() { }
        public bool? StoredGovernanceEnabled => null;
        public Task<bool> SaveGovernanceEnabledAsync(bool enabled) => Task.FromResult(false);
        public long SaveFailures => 0;
        public bool IsWired => false;
    }
}
