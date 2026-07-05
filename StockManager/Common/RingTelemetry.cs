// ============================================================================
// RingTelemetry — S29 Path Gamma: SPMC 주문 디스패치 링 → UI 락-프리 단방향 텔레메트리
//
//  v4.50 OrderDispatchRing/GatedOrderDispatcher 의 실시간 지표(처리량·무장송출·게이트차단·추월드롭·
//  CAS 낙방)를, UI 렌더링 스레드가 가변 주기로 '무할당·락-프리'로 읽어 관제탑에 비춘다. 금융 핫패스
//  (발행/드레인)와 UI 독출을 공간 격리 — UI 는 코어를 절대 블록하지 않고, 경합 시 직전 프레임을 유지한다.
//
//  ── seqlock(단일 라이터) — ForensicTelemetryChannel 검증 idiom 계승 ──────────
//  멀티필드 값 타입은 원자 쓰기가 불가하므로 짝/홀 버전 커서로 보호한다. 라이터(샘플러)는 홀(쓰기 시작)→
//  payload→짝(완료) 순으로 발행하고, 리더(UI)는 v1(짝) 독출→payload 복사→v2 재독출→v1==v2 검증으로
//  torn read 를 격리한다. x64 전용(TSO + Volatile 배리어). 거짓 공유는 Version↔Data 캐시라인 격리로 차단.
//  ★제네릭 미사용 사유: 기존 채널은 [StructLayout(Explicit)] Cell 에 구체 Data 필드를 둔다 — 제네릭
//  타입파라미터엔 FieldOffset 을 못 박으므로, 검증된 idiom 을 링 프레임 전용으로 미러링한다(재발명 아님).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>관제탑이 비추는 링 디스패처 계기 스냅샷(블리터블 값 타입·참조 0·힙 0).</summary>
    public readonly struct RingTelemetry
    {
        public readonly long   Published;         // 링 최고 발행 시퀀스(-1 = 미발행)
        public readonly long   Forwarded;         // 무장 상태 실 송출 수(onSend 성공)
        public readonly long   GatedOut;          // 미무장(fail-closed) 차단 수
        public readonly long   Dropped;           // 추월 유실(소비 지연 계측)
        public readonly long   WriterCollisions;  // 단일 생산자 CAS 낙방(동시 발행 위반)
        public readonly double ThroughputPerSec;  // 직전 샘플 대비 발행 처리량(건/초)
        public readonly long   SendErrors;        // Mission A — downstream throw 누적(브로커 단절 등). 0 이 정상.
        // v4.63.0 — 다중 seqlock 통합: 시나리오 변이 seqlock(ScenarioPublisher) 채널. Version 0 = 시뮬 페이지 비활성(미보고).
        public readonly long   ScenarioVersion;          // 시나리오 seqlock 버전(0=미보고)
        public readonly long   ScenarioWriterCollisions; // 시나리오 단일 라이터 경합(슬라이더 변이)
        public readonly long   ScenarioFrozen;           // 시나리오 산출 동결(음수 배수·오버플로 등)
        // v4.73.0 — 자율 알파 공장 카운터 통합. 공장 미배선(null) 시 전부 0 — UI "—" 표시.
        public readonly long   AlphaFactoryCycles;    // 완료 사이클 수
        public readonly long   AlphaFactorySigned;    // ECDsa 서명 완료 전략 수(누적)
        public readonly long   AlphaFactoryRejected;  // 수치 가드(CartridgeNumericsGuard) 거부 수(누적)
        // v4.85.0 — 자기 제어 밸브: 최근 창(≤1000 시행) 롤링 거부율 + 자동/수동 일시정지 상태(Amber Alert).
        public readonly double AlphaFactoryRecentRejRate;   // 0..1 (누적 아님·롤링)
        public readonly long   AlphaFactorySuspended;       // 0=가동, 1=일시정지(밸브 트립)

        public RingTelemetry(long published, long forwarded, long gatedOut, long dropped,
                             long writerCollisions, double throughputPerSec, long sendErrors = 0L,
                             long scenarioVersion = 0L, long scenarioWriterCollisions = 0L, long scenarioFrozen = 0L,
                             long alphaFactoryCycles = 0L, long alphaFactorySigned = 0L, long alphaFactoryRejected = 0L,
                             double alphaFactoryRecentRejRate = 0.0, long alphaFactorySuspended = 0L)
        {
            Published        = published;
            Forwarded        = forwarded;
            GatedOut         = gatedOut;
            Dropped          = dropped;
            WriterCollisions = writerCollisions;
            ThroughputPerSec = throughputPerSec;
            SendErrors       = sendErrors;
            ScenarioVersion          = scenarioVersion;
            ScenarioWriterCollisions = scenarioWriterCollisions;
            ScenarioFrozen           = scenarioFrozen;
            AlphaFactoryCycles   = alphaFactoryCycles;
            AlphaFactorySigned   = alphaFactorySigned;
            AlphaFactoryRejected = alphaFactoryRejected;
            AlphaFactoryRecentRejRate = alphaFactoryRecentRejRate;
            AlphaFactorySuspended     = alphaFactorySuspended;
        }

        /// <summary>활동 존재 여부(UI '데이터 없음' 판별). 양의 카운터로 판정 — default(struct)(전부 0)는 false.
        /// ★정직 한계: 정확히 1건만 발행되고 미드레인인 링(Published=0·나머지 0)은 false 로 보인다(극단 엣지·무해).</summary>
        public bool HasActivity => Published > 0 || Forwarded > 0 || GatedOut > 0
                                || Dropped > 0 || WriterCollisions > 0 || SendErrors > 0 || ScenarioVersion > 0;
        /// <summary>비상 경보 여부 — SendErrors/시나리오 동결 양수면 비상 확인 필수.</summary>
        public bool HasAlert    => SendErrors > 0 || ScenarioFrozen > 0;
        /// <summary>시나리오 변이 seqlock 활성(시뮬 페이지가 보고 중).</summary>
        public bool ScenarioActive => ScenarioVersion > 0;
        /// <summary>알파 공장 배선 여부(사이클 1 이상 완료된 경우). 미배선·미시작이면 false.</summary>
        public bool AlphaFactoryActive => AlphaFactoryCycles > 0;
        /// <summary>알파 공장 앰버 경보 — 자동/수동 일시정지 중이거나 최근 거부율 95% 이상(전략 고갈 의심).</summary>
        public bool AlphaFactoryAmber  => AlphaFactorySuspended != 0
                                       || (AlphaFactoryActive && AlphaFactoryRecentRejRate >= 0.95);
    }

    /// <summary>단일 라이터 seqlock 채널 — 캐시라인 격리. Publish(샘플러 단일 라이터)/TryRead(UI 락-프리·무할당).</summary>
    public sealed class RingTelemetryChannel
    {
        private const int CacheLine = 64;

        // Version(라이터 고빈도)과 Data(리더)를 서로 다른 64B 라인에 두고 앞 패딩+사이 갭으로 거짓 공유 차단.
        [StructLayout(LayoutKind.Explicit, Size = 256)]
        private struct Cell
        {
            [FieldOffset(CacheLine)]     public long          Version;
            [FieldOffset(CacheLine * 2)] public RingTelemetry Data;
        }

        private int  _writerTid;   // DEBUG 단일 라이터 회귀 가드(릴리스 비용 0)
        private Cell _cell;

        /// <summary>현재 seqlock 커서(진단). 짝=안정, 홀=발행 중.</summary>
        public long Version => Volatile.Read(ref _cell.Version);

        /// <summary>텔레메트리 발행 — '단일 라이터'(샘플러)에서만. 홀→payload→짝. 무할당(구조체 복사).</summary>
        public void Publish(in RingTelemetry t)
        {
            AssertSingleWriter();
            long ver = _cell.Version;                       // 단일 라이터 → 자기 마지막 값
            Volatile.Write(ref _cell.Version, ver + 1);     // 홀 — 발행 시작(리더 무효화)
            _cell.Data = t;                                  // payload 구조체 복사(x64 store 순서 보존)
            Volatile.Write(ref _cell.Version, ver + 2);     // 짝 — 발행 완료
        }

        /// <summary>락-프리 무할당 독출 — UI 스레드. torn read 면 재시도(최대 8회), 끝내 경합이면 false +
        /// out=default(UI 가 직전 프레임 유지·코어 무블록). out 한정자 → 박싱·힙 0.</summary>
        public bool TryRead(out RingTelemetry t)
        {
            for (int i = 0; i < 8; i++)
            {
                long v1 = Volatile.Read(ref _cell.Version);
                if ((v1 & 1L) == 0L)                         // 짝수 = 안정 스냅샷
                {
                    RingTelemetry local = _cell.Data;        // 구조체 복사(스택)
                    long v2 = Volatile.Read(ref _cell.Version);
                    if (v1 == v2) { t = local; return true; } // 두 독출 사이 발행 없음 → 일관
                }
            }
            t = default;                                     // 경합 지속 → 직전 프레임 유지 신호(비블록)
            return false;
        }

        [Conditional("DEBUG")]
        private void AssertSingleWriter()
        {
            int tid  = Environment.CurrentManagedThreadId;
            int prev = Interlocked.CompareExchange(ref _writerTid, tid, 0);
            if (prev != 0 && prev != tid)
                throw new InvalidOperationException(
                    "RingTelemetryChannel: 단일 라이터 위반 — 둘째 Publish 스레드 감지(seqlock torn write 위험).");
        }
    }

    /// <summary>
    /// 링/디스패처 카운터를 주기 스냅샷해 채널에 발행하는 '단일 라이터' 샘플러. UI 는 채널만 TryRead(단방향).
    /// 처리량은 직전 샘플 대비 발행 델타/경과초로 산출(Stopwatch 단위·주입 가능 — 결정론 테스트). 무할당.
    /// </summary>
    public sealed class RingTelemetryPublisher
    {
        private readonly OrderDispatchRing       _ring;
        private readonly GatedOrderDispatcher    _dispatcher;
        private readonly RingTelemetryChannel    _channel;
        private readonly Func<long>              _nowTicks;   // 기본 Stopwatch.GetTimestamp(테스트는 주입)
        private readonly ScenarioSeqlockMonitor? _scenario;   // v4.63.0 — 시나리오 변이 seqlock 통합(옵션·기본 null)
        private readonly AutonomousAlphaFactory? _alphaFactory; // v4.73.0 — 자율 알파 공장 카운터 통합(옵션·기본 null)
        private long _lastPublished;
        private long _lastTicks;
        private int  _sampling;   // 단일 라이터 CAS 가드(동시 Sample 금지)

        public RingTelemetryPublisher(OrderDispatchRing ring, GatedOrderDispatcher dispatcher,
                                      RingTelemetryChannel channel, Func<long>? nowTicks = null,
                                      ScenarioSeqlockMonitor? scenario = null,
                                      AutonomousAlphaFactory? alphaFactory = null)
        {
            _ring         = ring         ?? throw new ArgumentNullException(nameof(ring));
            _dispatcher   = dispatcher   ?? throw new ArgumentNullException(nameof(dispatcher));
            _channel      = channel      ?? throw new ArgumentNullException(nameof(channel));
            _nowTicks     = nowTicks     ?? Stopwatch.GetTimestamp;
            _scenario     = scenario;
            _alphaFactory = alphaFactory;
            _lastPublished = _ring.Published;
            _lastTicks     = _nowTicks();
        }

        /// <summary>1회 스냅샷 → 채널 발행(단일 라이터 전용·CAS 가드·무할당). 동시 Sample 은 fail-closed throw.</summary>
        public void Sample()
        {
            if (Interlocked.CompareExchange(ref _sampling, 1, 0) != 0)
                throw new InvalidOperationException("RingTelemetryPublisher: 동시 Sample 위반 — 단일 라이터 전용");
            try
            {
                long published = _ring.Published;
                long now       = _nowTicks();
                long dPub      = published - _lastPublished;
                long dTick     = now - _lastTicks;
                // 음수 델타(역행) 방어 + 0-경과 div 차단. Stopwatch.Frequency = ticks/sec.
                double tps = (dTick > 0 && dPub > 0) ? dPub * (double)Stopwatch.Frequency / dTick : 0.0;
                // v4.63.0 — 시나리오 seqlock 카운터 합류(미배선이면 0). 무할당(long 3개 Volatile 읽기).
                long sVer = 0L, sWc = 0L, sFr = 0L;
                if (_scenario is { } sc) { sVer = sc.Version; sWc = sc.WriterCollisions; sFr = sc.Frozen; }
                // v4.73.0 — 자율 알파 공장 카운터 합류(미배선이면 0). int→long 캐스팅, 힙 할당 0.
                // v4.85.0 — 자기 제어 밸브 상태(롤링 거부율·일시정지) 합류.
                long afCycles = 0L, afSigned = 0L, afRejected = 0L, afSuspended = 0L;
                double afRejRate = 0.0;
                if (_alphaFactory is { } af)
                {
                    afCycles = af.CyclesCompleted; afSigned = af.StrategiesSigned; afRejected = af.GuardRejections;
                    afRejRate = af.RecentRejectionRate; afSuspended = af.IsSuspended ? 1L : 0L;
                }
                _channel.Publish(new RingTelemetry(
                    published, _dispatcher.Forwarded, _dispatcher.GatedOut, _dispatcher.Dropped,
                    _ring.WriterCollisions, tps, _dispatcher.SendErrors, sVer, sWc, sFr,
                    afCycles, afSigned, afRejected, afRejRate, afSuspended));
                _lastPublished = published;
                _lastTicks     = now;
            }
            finally { Volatile.Write(ref _sampling, 0); }
        }
    }
}
