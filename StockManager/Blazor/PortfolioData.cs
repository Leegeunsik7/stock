// ============================================================================
// PortfolioData - 포트폴리오 허브 공통 컨텍스트 (IA 개편 1단계, v3.29.0)
//  HubPage가 OnInitializedAsync에서 1회 로드해 CascadingValue로 자식 탭에 내려준다.
//  자식(리밸런싱·리스크 분해)은 이 안의 R/Names/Weights만 소비 → 탭 전환 시 재수집 0.
//  ⚠️ 새 로드마다 '새 인스턴스'를 할당해야 CascadingValue가 자식에 변경을 전파한다.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StockManager.Common;

namespace StockManager.Blazor
{
    public class PortfolioData
    {
        public bool Loaded { get; set; }
        public string Error { get; set; } = "";
        public int Days { get; set; } = 180;
        public decimal UsdKrw { get; set; } = 1380m;

        public List<Holding> Used { get; set; } = new();         // 수익률 ≥2 보유 종목
        public double[][] R { get; set; } = Array.Empty<double[]>(); // 일수익률 행렬(%)
        public string[] Names { get; set; } = Array.Empty<string>();
        public double[] Weights { get; set; } = Array.Empty<double>(); // 시총 비중(합 1)
        public double Total { get; set; }                         // 총 평가액(KRW)

        /// <summary>보유·환율·수익률을 1회 수집해 컨텍스트로 빌드(허브 공용 로더, DRY).</summary>
        public static async Task<PortfolioData> LoadAsync(IQuoteProvider provider, LocalDatabase db, FxConverter fx, int days)
        {
            var d = new PortfolioData { Days = days };
            try
            {
                var holdings = db.GetAllHoldings();
                if (holdings.Count == 0) { d.Error = "보유 종목이 없습니다. 포트폴리오에 종목을 추가하세요."; return d; }

                decimal usd = 1380m; try { usd = await fx.GetUsdKrwAsync(); } catch { }
                d.UsdKrw = usd;
                var returns = await CorrelationAnalyzer.CollectReturnsAsync(
                    provider, holdings.Select(h => (h.Symbol, h.Market)), Math.Max(30, days));

                var used = holdings.Where(h => returns.ContainsKey(h.Symbol) && returns[h.Symbol].Count >= 2).ToList();
                if (used.Count == 0) { d.Error = "수익률 히스토리를 수집하지 못했습니다(시세 제공자 확인)."; return d; }

                d.Used = used;
                d.R = used.Select(h => returns[h.Symbol].Select(x => (double)x).ToArray()).ToArray();
                d.Names = used.Select(h => string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name).ToArray();
                var rawW = used.Select(h => (double)(h.CurrentPrice * h.Quantity * (h.Currency == Currency.USD ? usd : 1m))).ToArray();
                var sum = rawW.Sum();
                d.Total = sum;
                d.Weights = sum > 0 ? rawW.Select(w => w / sum).ToArray() : rawW.Select(_ => 1.0 / rawW.Length).ToArray();
                d.Loaded = true;
            }
            catch (Exception ex) { d.Error = "오류: " + ex.Message; }
            return d;
        }
    }
}
