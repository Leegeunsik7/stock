// ============================================================================
// PortfolioRiskUpdater — v7.2.0 포트폴리오 VaR 주기 구동기(콜드 백그라운드)
//
//  PortfolioRiskVaREngine 은 순수 δ=0 커널(연산만)이다. 이 구동기가 주기적으로 보유·수익률·환율을
//  수집(PortfolioData.LoadAsync)해 엔진에 주입(LoadReturns)하고 VaR 을 산출(ComputeVar)해 리스크 밸브
//  (LimitBreached)와 텔레메트리 캐시를 갱신한다. 텔레메트리 Publisher.Sample 은 그 캐시만 δ=0 로 읽는다.
//
//  · 전용 BelowNormal 스레드 + 인터럽터블 대기(50ms 발주 드레인 핫패스와 스레드 분리 — 수집 I/O 지터 무전파).
//  · fail-safe: 수집/연산 실패는 삼키고 다음 주기 재시도(리스크 관측이 앱을 흔들지 않음).
//  · liveArmed:false: 밸브는 tighten-only(신규 발주 억제만) — 이 구동기에서 발주로 가는 경로는 존재하지 않는다.
// ============================================================================
using System;
using System.Threading;
using StockManager.Common;

namespace StockManager.Blazor
{
    /// <summary>VaR 엔진 주기 구동기(콜드). Start() → 백그라운드 갱신, Dispose() → 정지·조인.</summary>
    public sealed class PortfolioRiskUpdater : IDisposable
    {
        private readonly PortfolioRiskVaREngine _engine;
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase  _db;
        private readonly FxConverter    _fx;
        private readonly double _var99LimitKrw;
        private readonly int    _days, _intervalMs;

        private Thread? _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private int  _started;
        private long _updates, _errors;

        public PortfolioRiskUpdater(PortfolioRiskVaREngine engine, IQuoteProvider provider, LocalDatabase db,
                                    FxConverter fx, double var99LimitKrw, int days = 180, int intervalMs = 15000)
        {
            _engine   = engine   ?? throw new ArgumentNullException(nameof(engine));
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _db       = db       ?? throw new ArgumentNullException(nameof(db));
            _fx       = fx       ?? throw new ArgumentNullException(nameof(fx));
            _var99LimitKrw = var99LimitKrw;
            _days = Math.Max(30, days);
            _intervalMs = Math.Max(2000, intervalMs);
        }

        public long Updates => Interlocked.Read(ref _updates);
        public long Errors  => Interlocked.Read(ref _errors);

        public void Start()
        {
            if (Interlocked.Exchange(ref _started, 1) == 1) return;
            _thread = new Thread(Loop) { IsBackground = true, Name = "PortfolioRiskUpdater", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
        }

        private void Loop()
        {
            // 최초 대기 후 주기 갱신(부팅 안정화). _stop.Wait 가 true 면 정지 신호.
            while (!_stop.Wait(_intervalMs))
            {
                try
                {
                    var data = PortfolioData.LoadAsync(_provider, _db, _fx, _days).GetAwaiter().GetResult();
                    if (data != null && data.Loaded && data.R.Length > 0 && _engine.LoadReturns(data.R, data.Weights))
                    {
                        double usdFrac = UsdFraction(data);
                        _engine.ComputeVar((decimal)data.Total, _var99LimitKrw, StressScenario.Crash10(usdFrac));
                        Interlocked.Increment(ref _updates);
                    }
                }
                catch { Interlocked.Increment(ref _errors); }   // fail-safe — 다음 주기 재시도
            }
        }

        // 포트 USD 익스포저 비중(스트레스 환율 시나리오용) = Σ weights[i where Currency==USD].
        private static double UsdFraction(PortfolioData d)
        {
            double usd = 0;
            int n = Math.Min(d.Used.Count, d.Weights.Length);
            for (int i = 0; i < n; i++) if (d.Used[i].Currency == Currency.USD) usd += d.Weights[i];
            return usd < 0 ? 0 : (usd > 1 ? 1 : usd);
        }

        public void Dispose()
        {
            _stop.Set();
            try { _thread?.Join(2000); } catch { }
            try { _stop.Dispose(); } catch { }
        }
    }
}
