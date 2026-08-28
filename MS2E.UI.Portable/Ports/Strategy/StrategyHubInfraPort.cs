// ============================================================================
// StrategyHubInfraPort — Phase 94-E: 전략 허브 인프라 통합 파사드 Port (심층 의존성 해체)
//   레거시 전략 페이지가 호스트로부터 직접 주입받던 4대 콘크리트 종속(LocalDatabase·IQuoteProvider·ToastService·
//   SymbolContext)을 이 Port 뒤로 밀어낸다. 순수 UI 는 이 계약만 알고, 어댑터(호스트)가 구상 클래스를 격리한다.
//     · 시세 이력 조회      — StreamHistoryAsync(★대용량을 배열로 반환하지 않고 경계 고정 BoundedQuoteWindow 에 적재)
//     · 최적화 후보군 DB 적재 — GetSetting/SetSetting
//     · 비동기 사용자 알림   — Notify
//     · 전역 활성 종목       — HasSymbol/CurrentSymbol/CurrentMarketCode/SetSymbol/SymbolChanged
//   ★불변: liveArmed:false(순수 조회/알림/상태 — 발주·소켓 무접촉) · fail-closed(미배선/실패 시 조용한 no-op·N/A).
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Infrastructure;

namespace MS2E.UI.Portable.Ports.Strategy
{
    /// <summary>사용자 알림 레벨(ToastService 추상화).</summary>
    public enum StrategyNotifyLevel { Info = 0, Success = 1, Warning = 2, Error = 3 }

    public interface IStrategyHubInfraPort
    {
        /// <summary>
        /// 시세 이력을 경계 고정 창(sink)에 스트리밍 적재. ★대용량 Array 를 Port 경계 밖으로 반환하지 않는다
        /// (LOH/off-screen 방어) — 어댑터가 청크를 sink.Push 로 밀어넣고, sink 는 항상 ≤2N 로 상한된다.
        /// </summary>
        Task StreamHistoryAsync(string symbol, int marketCode, int months,
            BoundedQuoteWindow sink, IProgress<int>? progress = null, CancellationToken ct = default);

        /// <summary>설정/최적화 후보군 DB 판독(LocalDatabase 추상화). 미배선/부재 → null.</summary>
        string? GetSetting(string key);

        /// <summary>설정/최적화 후보군 DB 적재(LocalDatabase 추상화).</summary>
        void SetSetting(string key, string value);

        /// <summary>비동기 사용자 알림(ToastService 추상화).</summary>
        void Notify(string title, string message, StrategyNotifyLevel level = StrategyNotifyLevel.Info);

        // ── 전역 활성 종목(SymbolContext 추상화) ──
        bool   HasSymbol         { get; }
        string CurrentSymbol     { get; }
        int    CurrentMarketCode { get; }
        void   SetSymbol(string symbol, int marketCode);
        event Action? SymbolChanged;
    }
}
