// ============================================================================
// ShadowMetricsBridge — S21 #2: 그림자 추적기 + 실거래 단방향 물리 밸브
//
//  "눈은 뜨되 손은 묶인" 완전 격리 섀도우 파이프라인:
//    [키움 프록시] ──실시간 틱──> ShadowMetricsBridge ──> RingBufferCore (락-프리 미러)
//                                        │ 실 체결(FillReceived) → 실측 슬리피지 → SlippageSentinel
//                                        │ IBroker.Place → 물리 발주 차단(단방향 밸브)
//                                        ▼ 와이어 송신 0%
//
//  ── 단방향 밸브 (One-Way Valve) ────────────────────────────────────────────
//  IBroker 로서 Place() 는 BlockPhysicalOrder 박제 하에 물리 발주를 차단한다. 게이트가
//  IsDryRun=false(라이브 논리)로 전 과정을 실전처럼 연산해도, 최종 발주 패킷은 이 밸브에서
//  끊긴다. ⚠️ 청사진의 `const` 대신 `static readonly` 사용 — const+조건문은 CS0162(미달 코드)
//  경고로 하드 게이트를 위반하기 때문. 런타임 박제 보장(코드 변경 없인 못 엶)은 동일하다.
//
//  ── 정직 경계: 마찰의 출처 ─────────────────────────────────────────────────
//  SlippageSentinel 은 '체결' 슬리피지를 측정한다. 섀도우는 우리 발주가 0이므로 우리 체결이
//  없다. 따라서 마찰은 틱(호가)이 아니라 프록시가 전하는 '실 체결(FillReceived)'에서만 나온다
//  (체결가 vs 직전 시장 틱가 = 실측 슬리피지). 틱으로 슬리피지를 날조하지 않는다 —
//  틱은 링버퍼 미러 + 틱레이트(실 시장 맥박)로만 쓴다.
//
//  ── 와이어 태핑 단일 생산자 계약 ───────────────────────────────────────────
//  Ingest 는 KiwoomBridgeClient 읽기 루프(단일 스레드)에서만 호출된다(RingBufferCore SPMC).
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace StockManager.Common
{
    public sealed class ShadowMetricsBridge : IBroker, IDisposable
    {
        // 단방향 밸브 박제 — true 인 한 물리 발주 통로는 원천 차단된다(코드 변경 없인 못 엶).
        private static readonly bool BlockPhysicalOrder = true;

        private readonly RingBufferCore<TickPayload>       _ring;
        private readonly SlippageSentinel?                 _sentinel;
        private readonly IBroker?                          _realBroker;   // 밸브 개방 시에만 위임(현재 도달 불가)
        private readonly ConcurrentDictionary<long, double> _lastPrice = new();

        private KiwoomBridgeClient? _source;
        private long _ticks, _blocked, _firstRecvTicks, _lastRecvTicks;

        public ShadowMetricsBridge(
            RingBufferCore<TickPayload> ring,
            SlippageSentinel?           sentinel   = null,
            IBroker?                    realBroker = null)
        {
            _ring       = ring ?? throw new ArgumentNullException(nameof(ring));
            _sentinel   = sentinel;
            _realBroker = realBroker;
        }

        // ── 와이어 태핑 (구독/해제) ────────────────────────────────────────────

        /// <summary>키움 브리지의 실시간 틱·체결 스트림을 미러링하도록 구독한다.</summary>
        public void Attach(KiwoomBridgeClient source)
        {
            if (source is null) throw new ArgumentNullException(nameof(source));
            Detach();
            _source = source;
            source.TickReceived += Ingest;
            source.FillReceived += IngestFill;
        }

        public void Detach()
        {
            var s = _source;
            if (s is null) return;
            s.TickReceived -= Ingest;
            s.FillReceived -= IngestFill;
            _source = null;
        }

        /// <summary>틱 미러링 진입점. 단일 생산자(읽기 루프) 전용 — 동시 호출 금지(SPMC).</summary>
        public void Ingest(TickPayload tick)
        {
            _ring.Publish(tick);                       // 락-프리 미러
            _lastPrice[tick.SymbolCode] = tick.Price;  // 실측 슬리피지 기준가 추적
            Interlocked.Increment(ref _ticks);
            long now = DateTime.UtcNow.Ticks;
            Interlocked.CompareExchange(ref _firstRecvTicks, now, 0L);
            Interlocked.Exchange(ref _lastRecvTicks, now);
        }

        /// <summary>실 체결 진입점 — 체결가 vs 직전 시장 틱가로 실측 슬리피지를 센티넬에 적재.</summary>
        public void IngestFill(FillPayload fill)
        {
            if (_sentinel is null) return;
            if (_lastPrice.TryGetValue(fill.SymbolCode, out var refPrice) && refPrice > 0.0)
                _sentinel.RecordFill(fill, refPrice);
        }

        // ── 메트릭 (관제탑 가시성) ─────────────────────────────────────────────
        public bool   Connected     => _source?.IsProxyAlive ?? false;
        public long   TicksReceived => Interlocked.Read(ref _ticks);
        public long   BlockedOrders => Interlocked.Read(ref _blocked);
        public double LastZ         => _sentinel?.LastZ ?? 0.0;

        public double TicksPerSecond
        {
            get
            {
                long f = Interlocked.Read(ref _firstRecvTicks);
                long l = Interlocked.Read(ref _lastRecvTicks);
                long c = Interlocked.Read(ref _ticks);
                if (c <= 1 || l <= f) return 0.0;
                double sec = (l - f) / (double)TimeSpan.TicksPerSecond;
                return sec > 1e-9 ? c / sec : 0.0;
            }
        }

        // ── IBroker — 단방향 물리 밸브 ─────────────────────────────────────────
        public string Name   => "ShadowValve";
        public bool   IsLive => false;   // 실거래 아님 — 그림자

        public (bool ok, string msg) Place(
            PaperAsset a, string s, string n, PaperSide sd, PaperKind k,
            decimal q, decimal lp, decimal cp)
        {
            Interlocked.Increment(ref _blocked);
            if (BlockPhysicalOrder)
                return (true, "[SHADOW] 물리 발주 차단 — 와이어 송신 0%");
            // 밸브 개방 시에만 실 브로커로 위임 — BlockPhysicalOrder=true 박제 하에선 도달 불가.
            return _realBroker is not null
                ? _realBroker.Place(a, s, n, sd, k, q, lp, cp)
                : (false, "밸브 개방이나 실 브로커 미결속");
        }

        public void    Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;

        public void Dispose() => Detach();
    }
}
