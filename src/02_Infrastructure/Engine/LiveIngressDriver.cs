// ============================================================================
// LiveIngressDriver — S25-a: 격리형 라이브 인그레스 펌프 + 피드 단절 트립와이어
//
//  방벽(LiveCandleFeed) 안쪽으로 실 캔들을 주기적으로 흘려보내는 전용 백그라운드 스레드.
//  외부 API push 스레드(BulkheadCandleSource 내부)와 BelowNormal 온실 배양 루프를 물리 격리한다.
//
//  ── 물리 격리·무할당·락프리 핸드오프(미션1) ────────────────────────────────
//  드라이버(이 스레드)는 _feed.Refresh() 만 호출 → 변화 시에만 GreenhouseService.Prime 이 _candles
//  참조를 원자 스왑(락프리)하고 _fedGen 을 Interlocked 증가(isDirty). 온실 배양 루프는 자기 스냅샷만
//  읽어 단 1바이트도 할당하지 않는다. 새 링버퍼는 불필요 — Prime 의 방어 복사가 곧 단일-라이터 격리.
//
//  ── 트립와이어(미션2) ──────────────────────────────────────────────────────
//  매 펌프마다 staleProbe(=greenhouse.FeedStale)를 확인하고, 상태가 '전이'할 때만 onStaleChanged 를
//  발화한다(edge-triggered). 호스트가 이를 관제탑 [⚠️ 온실 데이터 피드 단절] 경보에 결속한다.
//  단일 호출자(드라이버 스레드 또는 동기 PumpOnce)라 _lastStale 레이스 없음.
//
//  Stop 은 Join 으로 합류(JournalFlushPump 와 동형). 핫패스 무접촉.
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class LiveIngressDriver : IDisposable
    {
        private readonly LiveCandleFeed _feed;
        private readonly int            _intervalMs;
        private readonly Func<bool>?    _staleProbe;      // () => greenhouse.FeedStale
        private readonly Action<bool>?  _onStaleChanged;  // edge-triggered 콜백(host → AlertCenter)

        private long          _refreshes, _drops, _pumps;
        private volatile bool _running;
        private bool          _lastStale;
        private Thread?       _thread;

        public LiveIngressDriver(
            LiveCandleFeed feed,
            int            intervalMs     = 60_000,
            Func<bool>?    staleProbe     = null,
            Action<bool>?  onStaleChanged = null)
        {
            _feed           = feed ?? throw new ArgumentNullException(nameof(feed));
            _intervalMs     = intervalMs < 100 ? 100 : intervalMs;
            _staleProbe     = staleProbe;
            _onStaleChanged = onStaleChanged;
        }

        public long Refreshes => Interlocked.Read(ref _refreshes);
        public long Drops     => Interlocked.Read(ref _drops);
        public long Pumps     => Interlocked.Read(ref _pumps);
        public bool Running   => _running;

        /// <summary>1회 인그레스(동기·단일 호출자). 소스 fetch→검증→isDirty 면 재-Prime, 트립와이어 점검.</summary>
        public FeedOutcome PumpOnce()
        {
            Interlocked.Increment(ref _pumps);
            FeedOutcome outcome;
            try { outcome = _feed.Refresh(); }
            catch { outcome = FeedOutcome.SourceError; }   // 이중 방어(Refresh 자체도 흡수하나)

            if (outcome == FeedOutcome.Reprimed) Interlocked.Increment(ref _refreshes);
            else if (outcome is FeedOutcome.Insufficient or FeedOutcome.Corrupt or FeedOutcome.SourceError)
                Interlocked.Increment(ref _drops);

            CheckTripwire();
            return outcome;
        }

        private void CheckTripwire()
        {
            var probe = _staleProbe;
            var cb    = _onStaleChanged;
            if (probe is null || cb is null) return;
            bool now;
            try { now = probe(); } catch { return; }
            if (now != _lastStale)
            {
                _lastStale = now;
                try { cb(now); } catch { /* 경보 발화 실패 흡수 */ }
            }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread  = new Thread(Loop)
            {
                IsBackground = true,
                Priority     = ThreadPriority.BelowNormal,
                Name         = "LiveIngress",
            };
            _thread.Start();
        }

        private void Loop()
        {
            while (_running)
            {
                try { PumpOnce(); } catch { /* 격리 — 인그레스 예외는 메인에 전파 안 함 */ }
                for (int slept = 0; slept < _intervalMs && _running; slept += 25)
                    Thread.Sleep(Math.Min(25, _intervalMs - slept));
            }
        }

        public void Stop()
        {
            _running = false;
            var t = _thread;
            if (t is not null && t.IsAlive) { try { t.Join(2000); } catch { } }
            _thread = null;
        }

        public void Dispose() => Stop();
    }
}
