// ============================================================================
// UiPortabilityAnalyzer — Phase 94-A: UI 이주 가능성 정적 분석기 (Think Beyond Gemini)
//
//   ★제미나이 A(이벤트 멀티플렉서)/B(경계 센티널)와 이미 구축된 UiEventBoundaryBroker(런타임 이벤트 δ=0)·
//   Phase94UiLiberation 순수성 테스트(0 Engine Ref 강제)를 '초월'한다 — 이들은 '이미 분리된' UI 를 지키지만,
//   Total UI Liberation 의 진짜 병목은 '아직 호스트에 남은 96개 중 무엇을 지금 옮길 수 있는가'다. 본 분석기는
//   각 컴포넌트를 δ=0·결정론으로 LeafReady(즉시 git-move 가능)·PortBlocked(콘크리트 의존 → Port 추상화 선행)·
//   HostShell(호스트 잔류)로 분류해, 이주 아크를 tractable·우선순위화한다. 즉 '이주 계획 메타문제'를 자동화한다.
//   ★이것이 '@inject 0 == 이주 가능' 같은 순진한 가정을 깨는 근거: [CascadingParameter] 콘크리트 결합은 @inject
//   없이도 PortBlocked 다(본 코드베이스 RiskPage/ScenarioPage 실증).
//
//   ★불변: δ=0(Classify/신호 감지 핫패스 힙 0 — ReadOnlySpan.Contains(Ordinal)·enum·bool, string/배열 미생성) ·
//     결정론(순수 소스 신호 규칙·시계/난수 0) · liveArmed:false(순수 정적 분석 — 발주/소켓 유발 0) · fail-safe
//     (불확실 → PortBlocked 보수 분류: 순수성을 깨느니 '이주 보류'로 안전하게 기운다).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    /// <summary>UI 컴포넌트의 MS2E.UI.Portable 이주 가능성 판정.</summary>
    public enum UiPortability : byte
    {
        LeafReady   = 0,   // 콘크리트 의존 0 → 지금 git-move 가능(HubTabs 패턴)
        PortBlocked = 1,   // @inject/콘크리트 참조/콘크리트 캐스케이드 → Port 추상화 선행 필요
        HostShell   = 2,   // App/_Imports/Layout/HostPage/Nav → 호스트 잔류(이주 대상 아님)
    }

    public static class UiPortabilityAnalyzer
    {
        // ── 핵심 분류(δ=0·결정론) ────────────────────────────────────────────
        /// <summary>
        /// 추출된 소스 신호로 이주 가능성 판정. 우선순위: HostShell → (콘크리트 결합 있으면)PortBlocked → LeafReady.
        /// ★fail-safe: 어떤 콘크리트 신호든 하나라도 있으면 PortBlocked(순수성 파괴 대신 이주 보류로 보수 기움).
        /// </summary>
        public static UiPortability Classify(bool isHostShell, bool hasInject, bool refsConcreteEngine, bool hasConcreteCascade)
        {
            if (isHostShell) return UiPortability.HostShell;
            if (hasInject || refsConcreteEngine || hasConcreteCascade) return UiPortability.PortBlocked;
            return UiPortability.LeafReady;
        }

        /// <summary>경로+소스에서 신호를 직접 감지해 판정(δ=0 편의 오버로드). ref-struct 캡처 없음.</summary>
        public static UiPortability ClassifySource(ReadOnlySpan<char> path, ReadOnlySpan<char> src)
            => Classify(IsHostShell(path), HasInject(src), RefsConcreteEngine(src), HasConcreteCascade(src));

        // ── δ=0 신호 감지기(ReadOnlySpan.Contains — 할당 0) ────────────────────

        /// <summary>호스트 셸(이주 비대상): 엔트리·임포트·레이아웃·호스트 페이지 shim·네비.</summary>
        public static bool IsHostShell(ReadOnlySpan<char> path)
            => Has(path, "App.razor") || Has(path, "_Imports.razor") || Has(path, "Layout.razor")
               || Has(path, "HostPage.razor") || Has(path, "Routes.razor") || Has(path, "NavMenu.razor");

        /// <summary>@inject 지시자 존재(콘크리트 서비스 주입 신호).</summary>
        public static bool HasInject(ReadOnlySpan<char> src) => Has(src, "@inject");

        /// <summary>
        /// 콘크리트 엔진 네임스페이스 직접 참조(MS2E.Core/MS2E.Infrastructure — 5레이어 개편 후 엔진 콘크리트).
        /// ★MS2E.UI.Portable(순수 UI)은 '콘크리트'가 아니므로 신호 아님(그 접두는 MS2E.Core/Infrastructure 와 별개).
        /// </summary>
        public static bool RefsConcreteEngine(ReadOnlySpan<char> src)
            => Has(src, "MS2E.Core") || Has(src, "MS2E.Infrastructure");

        /// <summary>콘크리트 타입 캐스케이딩 결합(@inject 없이도 PortBlocked — 예: [CascadingParameter] PortfolioData).</summary>
        public static bool HasConcreteCascade(ReadOnlySpan<char> src) => Has(src, "CascadingParameter");

        private static bool Has(ReadOnlySpan<char> hay, ReadOnlySpan<char> needle)
            => hay.Contains(needle, StringComparison.Ordinal);

        // ── 표시 헬퍼 ─────────────────────────────────────────────────────────
        public static string Label(UiPortability p) => p switch
        {
            UiPortability.LeafReady => "즉시 이주 가능(Leaf)",
            UiPortability.PortBlocked => "Port 추상화 선행 필요",
            UiPortability.HostShell => "호스트 잔류(셸)",
            _ => "미상",
        };
    }

    /// <summary>이주 아크 진행 집계기(δ=0 Ingest) — 컴포넌트 판정을 흘려 넣어 LeafReady/PortBlocked/HostShell 카운트.</summary>
    public sealed class UiMigrationBacklog
    {
        private int _leafReady, _portBlocked, _hostShell, _total;

        public int LeafReady   => System.Threading.Volatile.Read(ref _leafReady);
        public int PortBlocked => System.Threading.Volatile.Read(ref _portBlocked);
        public int HostShell   => System.Threading.Volatile.Read(ref _hostShell);
        public int Total       => System.Threading.Volatile.Read(ref _total);

        /// <summary>판정 1건 반영(δ=0).</summary>
        public void Ingest(UiPortability p)
        {
            System.Threading.Interlocked.Increment(ref _total);
            switch (p)
            {
                case UiPortability.LeafReady:   System.Threading.Interlocked.Increment(ref _leafReady); break;
                case UiPortability.PortBlocked: System.Threading.Interlocked.Increment(ref _portBlocked); break;
                default:                        System.Threading.Interlocked.Increment(ref _hostShell); break;
            }
        }
    }
}
