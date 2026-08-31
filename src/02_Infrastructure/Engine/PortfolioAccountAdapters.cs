// ============================================================================
// PortfolioAccountAdapters — Phase 94-I: 포트폴리오/계좌 Port 호스트 어댑터 (DIP 구현)
//   순수 UI(TotalAssetPage·RebalancePage)를 엔진 구상(DashboardService·MarkowitzOptimizer·LocalDatabase)에 결속.
//   ★liveArmed:false / fail-closed: 조회·최적화(정보 제공)·설정 저장 전용 — 발주·소켓·브로커 무접촉.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports;
using StockManager.Blazor;   // DashboardService, CoinRow

namespace MS2E.Infrastructure
{
    /// <summary>계좌/총자산 요약 어댑터 — DashboardService(싱글턴) 격리. Coins 는 공변 반환(사본 0).</summary>
    public sealed class AccountSummaryAdapter : IAccountSummaryPort
    {
        private readonly DashboardService _svc;
        public AccountSummaryAdapter(DashboardService svc) => _svc = svc;

        public Task LoadAsync() => _svc.LoadAsync();
        public decimal Total         => _svc.Total;
        public decimal AllocKrwStock => _svc.AllocKrwStock;
        public decimal AllocUsdStock => _svc.AllocUsdStock;
        public decimal AllocCoin     => _svc.AllocCoin;
        public decimal CoinValue     => _svc.CoinValue;
        public decimal UsdKrw        => _svc.UsdKrw;

        // List<CoinRow> → IReadOnlyList<ICoinHoldingView> : 공변 참조 변환(CoinRow : ICoinHoldingView).
        public IReadOnlyList<ICoinHoldingView> Coins => _svc.Coins;
    }

    /// <summary>포트폴리오 허브 어댑터 — MPT 최적화(MarkowitzOptimizer) + 타겟 저장(LocalDatabase) 격리.</summary>
    public sealed class PortfolioHubAdapter : IPortfolioHubPort
    {
        private readonly LocalDatabase _db;
        public PortfolioHubAdapter(LocalDatabase db) => _db = db;

        public IMpoResultView Optimize(IPortfolioSnapshot data, int targetVolPct, int maxWeightPct)
            => MarkowitzOptimizer.Optimize(data.R, data.Names, targetVolPct, Math.Clamp(maxWeightPct, 10, 100) / 100.0);

        public void SaveRebalanceTarget(IReadOnlyList<string> symbols, IReadOnlyList<double> weights)
        {
            var dict = new Dictionary<string, double>();
            int n = Math.Min(symbols.Count, weights.Count);
            for (int i = 0; i < n; i++) dict[symbols[i]] = weights[i];
            _db.SetSetting("Rebalance.Target", JsonSerializer.Serialize(dict));
        }
    }
}
