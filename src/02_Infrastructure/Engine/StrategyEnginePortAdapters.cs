// ============================================================================
// StrategyEnginePortAdapters — Phase 94-D: 전략/백테스트 엔진 Port 호스트 어댑터 (DIP 구현)
//   순수 UI 의 IStrategyLabPort/IBacktestExecutionPort 를 콘크리트(StrategyOptimizer·BacktestEngine)와 시세/DB
//   (IQuoteProvider·LocalDatabase)에 결속한다. ★UI 는 시세/DB/엔진 콘크리트를 0개 참조 — 어댑터가 내부 fetch +
//   Task.Run 오프로드. 반환은 읽기 전용 계약(IOptimizationReport/IBacktestResultSnapshot).
//   ★liveArmed:false: 순수 조회/계산 — 발주·DCA·소켓 무접촉(StrategyOptimizer 는 '발굴'만).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Strategy;

namespace MS2E.Infrastructure
{
    /// <summary>전략 카트리지 조회/바인딩 어댑터. MA-크로스 격자를 카트리지로 노출.</summary>
    public sealed class StrategyLabAdapter : IStrategyLabPort
    {
        private const string MaCrossId = "ma-cross";

        private static readonly StrategyCartridge[] _cartridges =
        {
            new StrategyCartridge(
                MaCrossId, "MA 골든/데드크로스",
                "단·장기 이동평균 교차 기간을 격자 전수 백테스트해 종목별 최적값 발굴(정보 제공용).",
                StrategyOptimizer.ShortGrid, StrategyOptimizer.LongGrid, StrategyOptimizer.GridSize),
        };

        private static readonly MarketOption[] _markets =
        {
            new MarketOption((int)Market.KRX_KOSPI,  "코스피"),
            new MarketOption((int)Market.KRX_KOSDAQ, "코스닥"),
            new MarketOption((int)Market.US_NASDAQ,  "나스닥"),
            new MarketOption((int)Market.US_NYSE,    "뉴욕"),
            new MarketOption((int)Market.US_AMEX,    "아멕스"),
        };

        public IReadOnlyList<StrategyCartridge> GetCartridges() => _cartridges;
        public IReadOnlyList<MarketOption> GetMarkets() => _markets;

        public bool TryBind(string cartridgeId, int shortParam, int longParam, out string error)
        {
            if (!string.Equals(cartridgeId, MaCrossId, StringComparison.OrdinalIgnoreCase))
            { error = $"알 수 없는 카트리지: {cartridgeId}"; return false; }
            if (!StrategyOptimizer.ShortGrid.Contains(shortParam))
            { error = $"short 파라미터 {shortParam} 가 격자에 없습니다."; return false; }
            if (!StrategyOptimizer.LongGrid.Contains(longParam))
            { error = $"long 파라미터 {longParam} 가 격자에 없습니다."; return false; }
            if (shortParam >= longParam)
            { error = "short < long 이어야 합니다."; return false; }
            error = "";
            return true;
        }
    }

    /// <summary>백테스트 연산 어댑터 — 시세를 내부 조회 후 격자탐색을 백그라운드 오프로드.</summary>
    public sealed class BacktestExecutionAdapter : IBacktestExecutionPort
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;

        public BacktestExecutionAdapter(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
        }

        public async Task<IOptimizationReport> OptimizeAsync(
            string symbol, string name, int marketCode, int months, CancellationToken ct = default)
        {
            var market = (Market)marketCode;
            int m = months <= 0 ? 18 : months;
            // 시세 조회(콜드) → 격자탐색 오프로드(고부하). UI 는 IQuoteProvider/Candle/Market 무참조.
            var candles = await _provider.GetHistoryAsync(symbol, market, DateTime.Now.AddMonths(-m), DateTime.Now)
                                         .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await Task.Run<IOptimizationReport>(
                () => StrategyOptimizer.Optimize(symbol, string.IsNullOrWhiteSpace(name) ? symbol : name, candles, market), ct)
                .ConfigureAwait(false);
        }

        public async Task<IBacktestResultSnapshot> RunPortfolioSweepAsync(
            int months, int maxSymbols, IProgress<IBacktestResultRow>? progress = null, CancellationToken ct = default)
        {
            int m = months <= 0 ? 18 : months;
            int cap = maxSymbols <= 0 ? 25 : maxSymbols;

            var holdings = _db.GetAllHoldings()
                .Where(h => h.Market != Market.CRYPTO && !string.IsNullOrWhiteSpace(h.Symbol))
                .GroupBy(h => h.Symbol, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .Take(cap).ToList();

            var snap = new OptSnapshot { GeneratedAt = DateTime.Now.ToString("o") };
            foreach (var h in holdings)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var candles = await _provider.GetHistoryAsync(h.Symbol, h.Market, DateTime.Now.AddMonths(-m), DateTime.Now)
                                                 .ConfigureAwait(false);
                    var r = await Task.Run(
                        () => StrategyOptimizer.Optimize(h.Symbol, string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name, candles, h.Market), ct)
                        .ConfigureAwait(false);
                    var row = StrategyOptimizer.ToRow(r);
                    snap.Rows.Add(row);
                    progress?.Report(row);   // 스트리밍: 종목별 완료분 즉시 수신
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 개별 종목 실패는 스윕을 중단하지 않음(fail-soft) */ }
            }
            return snap;
        }
    }
}
