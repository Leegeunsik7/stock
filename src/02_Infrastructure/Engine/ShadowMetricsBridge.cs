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

namespace MS2E.Infrastructure
{
    public sealed class ShadowMetricsBridge : IBroker, IDisposable
    {
        // v5.0.0-c(안전형) — 2중 런타임 게이트 인터록이 정적 차단막을 대체한다.
        // 실 위임 = (_liveArmed 마스터 플래그) AND (_ignition 런타임 점화 허용) AND (실 브로커 결속).
        // _liveArmed 기본 false → 호스트는 실거래 0(최종 플래그 flip 전까지 비가역 선 미교차).
        private readonly RingBufferCore<TickPayload>       _ring;
        private readonly SlippageSentinel?                 _sentinel;
        private readonly IBroker?                          _realBroker;   // 게이트 통과 시에만 위임
        private readonly IgnitionLock?                     _ignition;     // 런타임 점화 게이트
        private readonly bool                              _liveArmed;    // 마스터 플래그(기본 false)
        private readonly ConcurrentDictionary<long, double> _lastPrice = new();

        private KiwoomBridgeClient? _source;
        private long _ticks, _attempted, _delegated, _blocked, _firstRecvTicks, _lastRecvTicks;

        public ShadowMetricsBridge(
            RingBufferCore<TickPayload> ring,
            SlippageSentinel?           sentinel   = null,
            IBroker?                    realBroker = null,
            IgnitionLock?               ignition   = null,
            bool                        liveArmed  = false)
        {
            _ring       = ring ?? throw new ArgumentNullException(nameof(ring));
            _sentinel   = sentinel;
            _realBroker = realBroker;
            _ignition   = ignition;
            _liveArmed  = liveArmed;
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
        public long   Attempted     => Interlocked.Read(ref _attempted);
        public long   Delegated     => Interlocked.Read(ref _delegated);   // 실 위임 건수(호스트=0)
        public long   BlockedOrders => Interlocked.Read(ref _blocked);
        public bool   LiveArmed     => _liveArmed;                          // 마스터 플래그 상태
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

        // ── IBroker — 2중 런타임 게이트 핫-밸브 ────────────────────────────────
        public string Name   => "ShadowValve";
        public bool   IsLive => _liveArmed;   // 마스터 플래그가 켜졌을 때만 라이브 가능

        public (bool ok, string msg) Place(
            PaperAsset a, string s, string n, PaperSide sd, PaperKind k,
            decimal q, decimal lp, decimal cp)
        {
            Interlocked.Increment(ref _attempted);
            // 2중 게이트 인터록: 마스터 플래그 AND 런타임 점화 허용 AND 실 브로커 결속.
            // && 단락 평가로 마스터 OFF 시 TryConsumePhysical(부수효과) 미호출 → 점화 상태 불변.
            if (_liveArmed && _realBroker is not null && _ignition is not null && _ignition.TryConsumePhysical())
            {
                Interlocked.Increment(ref _delegated);
                return _realBroker.Place(a, s, n, sd, k, q, lp, cp);   // ⚠️ 실 위임 — 비가역 와이어
            }
            // 게이트 하나라도 미충족 → 차단(그림자). 와이어 송신 0.
            Interlocked.Increment(ref _blocked);
            return (true, "[SHADOW] 발주 차단 — 와이어 송신 0% (2중 게이트 미충족)");
        }

        public void    Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;

        public void Dispose() => Detach();
    }
}
