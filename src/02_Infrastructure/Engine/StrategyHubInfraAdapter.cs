// ============================================================================
// StrategyHubInfraAdapter — Phase 94-E: 전략 허브 인프라 파사드 호스트 어댑터 (DIP 구현)
//   순수 UI 의 IStrategyHubInfraPort 를 콘크리트(IQuoteProvider·LocalDatabase·ToastService·SymbolContext)에 결속.
//   ★시세 이력은 어댑터 내부에서만 큰 리스트로 존재하고, Port 경계로는 경계 고정 BoundedQuoteWindow 에 청크로만
//   흘려보낸다(대용량 Array 가 어셈블리 경계를 넘지 않음 — LOH/off-screen 방어).
//   ★liveArmed:false: 순수 조회/알림/상태 — 발주·소켓·IBroker 무접촉.
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Infrastructure;
using MS2E.UI.Portable.Ports.Strategy;
using StockManager.Blazor;   // ToastService

namespace MS2E.Infrastructure
{
    public sealed class StrategyHubInfraAdapter : IStrategyHubInfraPort
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly ToastService _toasts;
        private readonly SymbolContext _ctx;

        public StrategyHubInfraAdapter(IQuoteProvider provider, LocalDatabase db, ToastService toasts, SymbolContext ctx)
        {
            _provider = provider;
            _db = db;
            _toasts = toasts;
            _ctx = ctx;
            _ctx.OnChange += () => SymbolChanged?.Invoke();   // 호스트 컨텍스트 변경 → 포트 이벤트 재발화(싱글턴 수명)
        }

        public async Task StreamHistoryAsync(string symbol, int marketCode, int months,
            BoundedQuoteWindow sink, IProgress<int>? progress = null, CancellationToken ct = default)
        {
            if (sink is null || string.IsNullOrWhiteSpace(symbol)) return;   // fail-closed
            int m = months <= 0 ? 18 : months;
            // 프로바이더의 리스트는 어댑터 내부(호스트)에만 존재. Port 로는 sink 에 청크로만 흘린다.
            var candles = await _provider
                .GetHistoryAsync(symbol, (Market)marketCode, DateTime.Now.AddMonths(-m), DateTime.Now, ct)
                .ConfigureAwait(false);

            int i = 0;
            foreach (var c in candles)
            {
                ct.ThrowIfCancellationRequested();
                sink.Push(new QuotePoint(c.Date.Ticks, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close, c.Volume));
                if ((++i & 1023) == 0) progress?.Report(i);   // 1024점마다 진행 보고(backpressure 훅)
            }
            progress?.Report(i);
        }

        public string? GetSetting(string key) => _db.GetSetting(key);
        public void SetSetting(string key, string value) { if (MS2E.Infrastructure.UiSettingWriteGuard.Admit(key)) _db.SetSetting(key, value); }   // ★허용 목록 밖 폐기

        public void Notify(string title, string message, StrategyNotifyLevel level = StrategyNotifyLevel.Info)
        {
            switch (level)
            {
                case StrategyNotifyLevel.Success: _toasts.Success(title, message); break;
                case StrategyNotifyLevel.Warning: _toasts.Warning(title, message); break;
                case StrategyNotifyLevel.Error:   _toasts.Error(title, message);   break;
                default:                          _toasts.Info(title, message);    break;
            }
        }

        public bool   HasSymbol         => _ctx.HasSymbol;
        public string CurrentSymbol     => _ctx.Symbol;
        public int    CurrentMarketCode => (int)_ctx.Market;
        public void   SetSymbol(string symbol, int marketCode) => _ctx.Set(symbol, (Market)marketCode);
        public event Action? SymbolChanged;
    }
}
