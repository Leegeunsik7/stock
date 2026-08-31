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
        string? GetSetting(string key);
        /// <summary>커맨드 팔레트 열기 요청(CommandPaletteController).</summary>
        void RequestOpenPalette();
    }

    // ── 대시보드 위젯(IndexTickerBar·MarketTicker) ──────────────────────────────
    public interface IIndexRowView
    {
        string Symbol { get; } string Name { get; } string Flag { get; } string Region { get; }
        decimal Last { get; } decimal ChgPct { get; } bool Up { get; }
    }
    public interface ITickerRowView { string Name { get; } string Px { get; } string Rate { get; } bool Up { get; } }

    public interface IDashboardWidgetPort
    {
        // 지수 티커(IndexTickerBar)
        IReadOnlyList<IIndexRowView> Indices { get; }
        event Action? IndicesRefreshed;

        // 마켓 티커(MarketTicker) — 고정 감시목록 시세를 행으로 투사(엔진 IQuoteProvider/watchlist 격리).
        Task<IReadOnlyList<ITickerRowView>> GetTickerRowsAsync(CancellationToken ct);
    }
}
