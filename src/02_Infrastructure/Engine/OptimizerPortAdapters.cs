// ============================================================================
// OptimizerPortAdapters — Phase 94-F: 옵티마이저 실행 + 복합 알림 Port 호스트 어댑터 (DIP)
//   OptimizerPage 의 IOptimizerExecutionPort/ICompositeAlertPort 를 콘크리트(StrategyOptimizer·WalkForward·
//   CompositeAlertEngine)와 시세/DB(IQuoteProvider·LocalDatabase)에 결속. ★UI 는 엔진/도메인/시세/DB 콘크리트를
//   0개 참조 — 어댑터가 내부 fetch + Task.Run 오프로드 + CompositeRule 조립을 격리.
//   ★liveArmed:false: 순수 최적화/검증/알림 규칙 조립 — 발주·소켓·IBroker 무접촉.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MS2E.UI.Portable.Ports.Strategy;

namespace MS2E.Infrastructure
{
    public sealed class OptimizerExecutionAdapter : IOptimizerExecutionPort
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;

        public OptimizerExecutionAdapter(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
        }

        public async Task<IOptimizationReport> OptimizeAsync(string symbol, string name, int marketCode, int months, CancellationToken ct = default)
        {
            var market = (Market)marketCode;
            int m = months <= 0 ? 18 : months;
            var candles = await _provider.GetHistoryAsync(symbol, market, DateTime.Now.AddMonths(-m), DateTime.Now, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await Task.Run<IOptimizationReport>(
                () => StrategyOptimizer.Optimize(symbol, string.IsNullOrWhiteSpace(name) ? symbol : name, candles, market), ct).ConfigureAwait(false);
        }

        public async Task<IWalkForwardReport> WalkForwardAsync(string symbol, string name, int marketCode, int months, int trainBars, int testBars, CancellationToken ct = default)
        {
            var market = (Market)marketCode;
            int m = months <= 0 ? 18 : months;
            var candles = await _provider.GetHistoryAsync(symbol, market, DateTime.Now.AddMonths(-m), DateTime.Now, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return await Task.Run<IWalkForwardReport>(
                () => WalkForward.Analyze(symbol, string.IsNullOrWhiteSpace(name) ? symbol : name, candles, market, trainBars, testBars), ct).ConfigureAwait(false);
        }

        public async Task<IBacktestResultSnapshot> RunPortfolioSweepAsync(int months, int maxSymbols, IProgress<IBacktestResultRow>? progress = null, CancellationToken ct = default)
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
                    var candles = await _provider.GetHistoryAsync(h.Symbol, h.Market, DateTime.Now.AddMonths(-m), DateTime.Now, ct).ConfigureAwait(false);
                    var r = await Task.Run(() => StrategyOptimizer.Optimize(h.Symbol, string.IsNullOrWhiteSpace(h.Name) ? h.Symbol : h.Name, candles, h.Market), ct).ConfigureAwait(false);
                    var row = StrategyOptimizer.ToRow(r);
                    snap.Rows.Add(row);
                    progress?.Report(row);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* 개별 실패는 스윕을 중단하지 않음 */ }
            }
            // ★영속은 어댑터가 소유 — 순수 UI 는 OptSnapshot 을 직렬화하지 않는다.
            try
            {
                _db.SetSetting("Optimizer.Results", System.Text.Json.JsonSerializer.Serialize(snap));
                _db.SetSetting("Optimizer.LastRun", DateTime.Now.ToString("o"));
            }
            catch { /* 영속 실패는 결과 반환을 막지 않음 */ }
            return snap;
        }

        public IBacktestResultSnapshot? LoadLastSnapshot()
        {
            try
            {
                var j = _db.GetSetting("Optimizer.Results");
                if (string.IsNullOrWhiteSpace(j)) return null;
                return System.Text.Json.JsonSerializer.Deserialize<OptSnapshot>(j);
            }
            catch { return null; }
        }
    }

    public sealed class CompositeAlertAdapter : ICompositeAlertPort
    {
        private readonly LocalDatabase _db;
        public CompositeAlertAdapter(LocalDatabase db) => _db = db;

        // ★CompositeRule/CompositeCondition/CondMetric 조립은 전량 어댑터 내부 — RCL 로 도메인 구상 미유입.
        public void InjectTrendAlert(string symbol, string name, int longP)
        {
            var rules = CompositeAlertEngine.Load(_db);
            rules.Add(new CompositeRule
            {
                Name = $"🅔 {name} MA{longP} 추세",
                AssetKind = "stock",
                Target = symbol,
                Logic = RuleLogic.And,
                Conditions = new() { new CompositeCondition { Metric = CondMetric.MaGapPct, Param = longP, Op = CondOp.Gte, Threshold = 0 } }
            });
            CompositeAlertEngine.Save(_db, rules);
        }

        public IReadOnlyList<ICompositeRuleContract> ListRules() => CompositeAlertEngine.Load(_db);
    }
}
