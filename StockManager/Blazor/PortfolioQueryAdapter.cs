// ============================================================================
// PortfolioQueryAdapter — Phase 94-B: IPortfolioQueryPort 호스트 어댑터 (의존성 역전 DIP)
//   순수 UI(RCL)는 IPortfolioQueryPort(RCL 계약)만 알고, 실제 로딩(시세·DB·FX 콘크리트)은 이 어댑터가 담당한다.
//   기존 PortfolioData.LoadAsync 를 그대로 위임(로직 보존) → IPortfolioSnapshot(PortfolioData 가 구현) 반환.
//   ★liveArmed:false: 조회 전용 로딩(발주/소켓 무접촉).
// ============================================================================
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports;

namespace StockManager.Blazor
{
    public sealed class PortfolioQueryAdapter : IPortfolioQueryPort
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase  _db;
        private readonly FxConverter    _fx;

        public PortfolioQueryAdapter(IQuoteProvider provider, LocalDatabase db, FxConverter fx)
        {
            _provider = provider;
            _db = db;
            _fx = fx;
        }

        public async Task<IPortfolioSnapshot> LoadAsync(int days)
            => await PortfolioData.LoadAsync(_provider, _db, _fx, days);
    }
}
