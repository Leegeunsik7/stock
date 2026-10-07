// ============================================================================
// ChaosStormInjector — [카오스 스톰 · 추천 A] 다중 벡터 장애 주입기(모의투자 하네스 = 실 게이팅 사슬 + 가상 브로커 위에서)
//
//  벡터 9종 → 각 벡터는 '실제 Trip 경로'로 격발한다(비트를 직접 세우지 않음):
//    PriceSpike(+50% 틱 점프 → MarketSpike) · ProxyKill(체결 피드 단절 + 실 COM 링크 소실 → RouterFeed · KiwoomBridgeLink) ·
//    SilentTimeout(KIS WebSocket 수신 침묵 → KisSession) · ClockRegression(벽시계 역행 → PreFlightBoot) · MmfCorruption(IPC 라이터 침묵 →
//    MmfFabric) · CredentialLeak(원문 자격증명 로그 등장 → CredentialLeak) · OrderbookCross(호가 교차 → OrderbookIlliquidity) ·
//    ServerError(키움 중대 오류 → KiwoomServerError) · LivelockEscape(스핀 상한 소진 → ChaosProgress).
//  하네스 집계어(7 필수)에 4개 계층(PreFlightBoot · KiwoomBridgeLink · KisSession)을 비필수로 추가 결속 — 결속 비트도 서면 차단(집계어 규칙).
//  ★0ms 원자 Disarm 판정: Inject 는 벡터를 동기 격발한 뒤 '반환 전에' 해당 비트가 집계어에 서 있는지 재판독(Verified/Unverified 계수).
//    주입 스레드에서 동기 통지된 전이(집계어 → 포렌식 기록기 → 이 체인)는 주입 시작~전이 Stopwatch 간격(µs)을 최대값으로 기록.
//  포렌식 상호 검증: 주입마다 블랙박스(MMF 비행 기록계)에 ChaosInjected(벡터 · 직전/직후 마스크) 1건 — 기록기가 남기는 InterlockRaised 와
//    시퀀스 순서로 대조 가능(주입 → 격발). 난수 = xorshift64*(시드 결정론 · 할당 0).
//  δ=0: Inject/Storm/OnTransition 전 구간 관리 힙 0(엔진 Trip 경로 포함 — 테스트 실측). 재무장 경로는 다루지 않는다(물리 이중키 = 운용자).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Security;

namespace StockManager.Simulation
{
    public enum ChaosVector : byte
    {
        PriceSpike = 0, ProxyKill = 1, SilentTimeout = 2, ClockRegression = 3, MmfCorruption = 4,
        CredentialLeak = 5, OrderbookCross = 6, ServerError = 7, LivelockEscape = 8,
    }

    public sealed class ChaosStormInjector : IInterlockTransitionSink
    {
        public const int VectorCount = 9;
        private const long SessionSilenceMicros = 1_000_000;

        private readonly PaperTradingSimulationHarness _h;
        private readonly MmfFlightRecorder? _blackbox;
        private int _proxyAlive = 1;
        private long _sessionNowMicros = 1;
        private readonly long _wall0, _mono0;
        private ulong _rng;
        private long _injected, _verified, _unverified, _transitions, _syncTransitions, _maxSyncTicks;
        private readonly long[] _perVector = new long[VectorCount];
        [ThreadStatic] private static long t_injectStart;

        public BootPreFlightSentinel Clock { get; }
        public KiwoomBridgeLinkInterlock ProxyLink { get; }
        public KisSessionInterlock Session { get; }

        /// <param name="harness">대상 하네스(인터락 집계어 · 7계층 주입기 · 가상 브로커).</param>
        /// <param name="blackbox">MMF 비행 기록계(하네스와 같은 인스턴스 권장 — 주입 ↔ 격발 상호 검증).</param>
        public ChaosStormInjector(PaperTradingSimulationHarness harness, MmfFlightRecorder? blackbox = null, ulong seed = 0x9E3779B97F4A7C15UL)
        {
            _h = harness ?? throw new ArgumentNullException(nameof(harness));
            _blackbox = blackbox;
            _rng = seed == 0 ? 1UL : seed;
            Clock = new BootPreFlightSentinel(clockTolerance: TimeSpan.FromSeconds(1));
            _wall0 = DateTime.UtcNow.Ticks; _mono0 = Stopwatch.GetTimestamp();
            Clock.ObserveClock(_wall0, _mono0, Stopwatch.Frequency);                // 기준선
            ProxyLink = new KiwoomBridgeLinkInterlock(() => Volatile.Read(ref _proxyAlive) == 1, required: true);
            Session = new KisSessionInterlock(null, 1, () => Volatile.Read(ref _sessionNowMicros), silenceMicros: SessionSilenceMicros);
            Session.MarkConnected();
            var mask = _h.Interlocks;
            Clock.BindInterlock(mask, ArmingInterlockSource.PreFlightBoot);
            ProxyLink.BindInterlock(mask, ArmingInterlockSource.KiwoomBridgeLink);
            ProxyLink.Evaluate();                                                    // 링크 수립(생존) → 비차단
            Session.BindInterlock(mask, ArmingInterlockSource.KisSession);
            _h.Recorder.Chain(this);                                                 // 포렌식 기록기(해시 체인 · 블랙박스) 뒤 체인
        }

        public long Injected => Interlocked.Read(ref _injected);
        /// <summary>주입 반환 전에 해당 비트가 집계어에 선 횟수(0ms 원자 Disarm 증명).</summary>
        public long Verified => Interlocked.Read(ref _verified);
        public long Unverified => Interlocked.Read(ref _unverified);
        public long Transitions => Interlocked.Read(ref _transitions);
        /// <summary>주입 스레드에서 동기 통지된 전이 수(격발이 주입 호출 안에서 완결).</summary>
        public long SyncTransitions => Interlocked.Read(ref _syncTransitions);
        public long MaxInjectToTransitionMicros => Interlocked.Read(ref _maxSyncTicks) * 1_000_000 / Stopwatch.Frequency;
        public long InjectedOf(ChaosVector v) => Interlocked.Read(ref _perVector[(int)v]);

        /// <summary>벡터 → 격발해야 할 집계어 비트.</summary>
        public static ArmingInterlockSource BitOf(ChaosVector v) => v switch
        {
            ChaosVector.PriceSpike => ArmingInterlockSource.MarketSpike,
            ChaosVector.ProxyKill => ArmingInterlockSource.KiwoomBridgeLink,
            ChaosVector.SilentTimeout => ArmingInterlockSource.KisSession,
            ChaosVector.ClockRegression => ArmingInterlockSource.PreFlightBoot,
            ChaosVector.MmfCorruption => ArmingInterlockSource.MmfFabric,
            ChaosVector.CredentialLeak => ArmingInterlockSource.CredentialLeak,
            ChaosVector.OrderbookCross => ArmingInterlockSource.OrderbookIlliquidity,
            ChaosVector.ServerError => ArmingInterlockSource.KiwoomServerError,
            _ => ArmingInterlockSource.ChaosProgress,
        };

        /// <summary>벡터 1개 동기 격발(스레드 안전). 반환 = 반환 직전 해당 비트가 집계어에 서 있음(0ms 원자 Disarm).</summary>
        public bool Inject(ChaosVector v)
        {
            var mask = _h.Interlocks;
            long before = mask.Mask;
            t_injectStart = Stopwatch.GetTimestamp();
            try
            {
                switch (v)
                {
                    case ChaosVector.PriceSpike: _h.Injector.Inject(InterlockLayer.MarketDataSpike); break;
                    case ChaosVector.ProxyKill:
                        Volatile.Write(ref _proxyAlive, 0);                             // 프록시 프로세스 소실(파이프 · 하트비트)
                        _h.Injector.Inject(InterlockLayer.RouterFeed);                  // 체결 피드도 함께 단절
                        ProxyLink.Evaluate();
                        break;
                    case ChaosVector.SilentTimeout:
                        Interlocked.Add(ref _sessionNowMicros, SessionSilenceMicros * 2);   // 수신 침묵 > 임계
                        Session.Evaluate();
                        break;
                    case ChaosVector.ClockRegression:
                        Clock.ObserveClock(_wall0 - TimeSpan.TicksPerMinute * 10, Stopwatch.GetTimestamp(), Stopwatch.Frequency);   // 벽시계 10분 역행
                        break;
                    case ChaosVector.MmfCorruption: _h.Injector.Inject(InterlockLayer.MmfFabric); break;
                    case ChaosVector.CredentialLeak: _h.Injector.Inject(InterlockLayer.Pii); break;
                    case ChaosVector.OrderbookCross: _h.Injector.Inject(InterlockLayer.OrderbookIlliquidity); break;
                    case ChaosVector.ServerError: _h.Injector.Inject(InterlockLayer.KiwoomServerError); break;
                    default: _h.Injector.Inject(InterlockLayer.ChaosProgress); break;
                }
            }
            finally { t_injectStart = 0; }
            Interlocked.Increment(ref _injected);
            Interlocked.Increment(ref _perVector[(int)v]);
            bool ok = mask.IsRaised(BitOf(v)) && mask.Blocked;                      // ★반환 전 재판독
            Interlocked.Increment(ref ok ? ref _verified : ref _unverified);
            try { _blackbox?.Record(FlightEventKind.ChaosInjected, (long)v, before, mask.Mask, ok ? 1 : 0); } catch (Exception) { }
            return ok;
        }

        /// <summary>무작위 벡터 n개 순차 격발(시드 결정론 · δ=0). 반환 = 미검증(비트 미격발) 수.</summary>
        public int Storm(int count)
        {
            int bad = 0;
            for (int i = 0; i < count; i++) if (!Inject(NextVector())) bad++;
            return bad;
        }

        /// <summary>xorshift64* 다음 벡터(호출 스레드 단일 사용 전제 — 다중 스레드 스톰은 스레드별 인스턴스 시드 대신 고정 순열 권장).</summary>
        public ChaosVector NextVector()
        {
            ulong x = _rng;
            x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
            _rng = x;
            return (ChaosVector)((x * 0x2545F4914F6CDD1DUL >> 33) % VectorCount);
        }

        public void OnTransition(long raisedBits, long clearedBits, long maskAfter)
        {
            Interlocked.Increment(ref _transitions);
            long t0 = t_injectStart;
            if (t0 == 0 || raisedBits == 0) return;
            Interlocked.Increment(ref _syncTransitions);
            long dt = Stopwatch.GetTimestamp() - t0;
            long cur = Interlocked.Read(ref _maxSyncTicks);
            while (dt > cur)
            {
                long seen = Interlocked.CompareExchange(ref _maxSyncTicks, dt, cur);
                if (seen == cur) break;
                cur = seen;
            }
        }
    }
}
