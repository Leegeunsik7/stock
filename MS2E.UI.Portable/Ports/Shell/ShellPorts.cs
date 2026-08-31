// ============================================================================
// ShellPorts — Phase 94-K: Main Shell & Dashboard 위젯 Ports (순수 UI 경계)
//
//   NavMenu·ToastHost·SymbolBar·CrossLinks·AsOfBar·IndexTickerBar·MarketTicker·Dashboard 등 셸/관제 위젯을
//   엔진 구상(SymbolContext·ToastService·GlobalIndexService·IQuoteProvider·LocalDatabase·CommandPaletteController·
//   ModuleBridge·DashboardService)에서 물리 격리한다. 읽기 전용 뷰 + 상태 관제 명령.
//   ★엔진 타입 참조 0(RCL 순수성) · liveArmed:false(조회/네비/토스트 전용) · fail-closed.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Portfolio;   // PfMarket 재사용

namespace MS2E.UI.Portable.Ports.Shell
{
    // ── 심볼 컨텍스트(SymbolBar·CrossLinks) ─────────────────────────────────────
    public interface ISymbolContextPort
    {
        bool HasSymbol { get; }
        string Symbol { get; }
        PfMarket Market { get; }
        event Action? OnChange;
        void Set(string symbol, PfMarket market);
    }

    // ── 토스트(ToastHost) ───────────────────────────────────────────────────────
    public enum ToastKind { Info = 0, Success = 1, Warning = 2, Error = 3 }

    public interface IToastButtonView { string Label { get; } bool Primary { get; } }
    public interface IToastView
    {
        Guid Id { get; }
        string Title { get; }
        string Message { get; }
        ToastKind Level { get; }
        IReadOnlyList<IToastButtonView> Buttons { get; }
        int TimeoutMs { get; }
    }
    public interface IToastPort
    {
        IReadOnlyList<IToastView> Active { get; }
        event Action? Changed;
        void Dismiss(Guid id);
        /// <summary>토스트 버튼 액션 실행(엔진 Func 를 RCL 로 노출하지 않고 인덱스로 위임).</summary>
        Task InvokeButtonAsync(Guid toastId, int buttonIndex);
    }

    // ── 메인 셸 내비게이션(NavMenu) ────────────────────────────────────────────
    public interface IMainShellNavigationPort
    {
        int AlertUnreadCount { get; }
        int FraudUnreadCount { get; }
        string AppVersion { get; }
        bool IsAuthenticated { get; }   // Phase 94-L: MainLayout 로그인 게이트(SessionState 격리)
        string? GetSetting(string key);
        /// <summary>커맨드 팔레트 열기 요청(CommandPaletteController).</summary>
        void RequestOpenPalette();
    }

    // ── Phase 94-L: 커맨드 팔레트(CommandPalette) ───────────────────────────────
    /// <summary>팔레트 명령 1개의 읽기 뷰(Id 로 실행 위임 — 엔진 Func 미노출).</summary>
    public interface ICommandView { int Id { get; } string Icon { get; } string Label { get; } string Cat { get; } }

    /// <summary>
    /// 커맨드 팔레트 Port — 명령 레지스트리(모듈이동·모의주문·알림·함수문법·매크로·테마)를 어댑터가 소유하고,
    /// UI 는 뷰 + Id 실행만 안다. ★엔진(DashboardService·CartridgeService·ModuleBridge·SymbolContext·AsOfContext·
    /// ToastService·NavigationManager) 참조 0. ★실 발주 경로 0(모의/네비/알림/테마뿐 — liveArmed:false 무관).
    /// </summary>
    public interface ICommandPalettePort
    {
        void RefreshMacros();                                  // 팔레트 열 때 카트리지 매크로 갱신
        IReadOnlyList<ICommandView> Filter(string query);      // 질의 필터링(액션은 어댑터가 캐시)
        Task ExecuteAsync(int id);                             // 캐시된 명령 실행(Id 로 위임)
        string CurrentTheme { get; }
        string ToggleTheme();                                  // 테마 토글 + 새 모드 반환(JS setTheme 용)
    }

    // ── Phase 94-M: 대시보드 심층(Activity·Layout·Paper) ────────────────────────
    /// <summary>사용자 활동 로그 1건(엔진 ActItem 추상).</summary>
    public interface IActivityView { string Text { get; } string When { get; } }

    /// <summary>사용자 활동 로그 Port(DashboardService.Activity 격리).</summary>
    public interface IUserActivityPort
    {
        IReadOnlyList<IActivityView> Recent { get; }
    }

    /// <summary>UI 레이아웃 영속 Port(위젯 배치 저장/복원 — DashboardService.LoadLayout/SaveLayout 격리).</summary>
    public interface ILayoutPersistencePort
    {
        IReadOnlyList<string> Load();
        void Save(IReadOnlyList<string> layout);
    }

    /// <summary>대시보드 액션 Port(모의주문+토스트-네비 — scoped·NavigationManager 결합). ★실 발주 아님(paper).</summary>
    public interface IDashboardActionPort
    {
        Task QuickPaperAsync(string market, decimal amount, bool isBuy);
    }

    // ── Phase 94-L: 핫키 브릿지 + JS Interop 안전(Think-Beyond) ──────────────────
    public interface IHotkeyBridgePort
    {
        /// <summary>팔레트 열기 요청 이벤트(CommandPaletteController — NavMenu 검색바/핫키).</summary>
        event Action? OpenRequested;

        // ★DotNetObjectReference GCHandle 누수 방어(등록/해제 균형 추적).
        void RegisterHandle();
        void ReleaseHandle();
        long ActiveHandles { get; }

        // ★[JSInvokable] 콜백 변조 방어(토큰 발급/검증/파기 — fail-closed).
        long IssueToken();
        bool VerifyToken(long token);
        void RevokeToken();
    }

    // ── 대시보드 위젯(IndexTickerBar·MarketTicker) ──────────────────────────────
    public interface IIndexRowView
    {
        string Symbol { get; } string Name { get; } string Flag { get; } string Region { get; }
        decimal Last { get; } decimal ChgPct { get; } bool Up { get; }
    }
    public interface ITickerRowView { string Name { get; } string Px { get; } string Rate { get; } bool Up { get; } }

    public interface IWeeklyPointView { string Label { get; } decimal Value { get; } }

    public interface IDashboardWidgetPort
    {
        // 지수 티커(IndexTickerBar)
        IReadOnlyList<IIndexRowView> Indices { get; }
        event Action? IndicesRefreshed;

        // 마켓 티커(MarketTicker) — 고정 감시목록 시세를 행으로 투사(엔진 IQuoteProvider/watchlist 격리).
        Task<IReadOnlyList<ITickerRowView>> GetTickerRowsAsync(CancellationToken ct);

        // ── Phase 94-L: 대시보드 KPI(Dashboard·DashboardHome) ──
        Task LoadAsync();
        decimal Total { get; }
        decimal Ret { get; }
        decimal Pnl { get; }
        decimal CoinValue { get; }
        decimal AllocKrwStock { get; }
        decimal AllocUsdStock { get; }
        decimal AllocCoin { get; }
        decimal PaperCash { get; }
        decimal PaperPosVal { get; }
        decimal PaperPnl { get; }
        decimal PaperRet { get; }
        bool WeeklySample { get; }
        string Theme { get; set; }
        IReadOnlyList<ICoinHoldingView> Coins { get; }                   // CoinRow(94-I·Ports) 재사용
        IReadOnlyList<IWeeklyPointView> Weekly { get; }
        IReadOnlyList<Strategy.ICoinSignalView> Signals { get; }         // SignalRow(94-F·Ports.Strategy) 재사용
        void OpenModule(string key);
    }
}
