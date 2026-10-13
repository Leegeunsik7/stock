// ============================================================================
// LongSoakProfileHarness — [Long-Soak · 추천 A] 24시간 가속(Time-Warp) 모의투자 장기 구동 프로파일러
//
//  ★정직: 이 저장소에는 '실제 24시간 분량의 키움/KIS VTS 틱 기록'이 없다 — 시드 결정론 합성 시세(종목별 ±1틱 무작위 보행 · 평균 회귀 ·
//    ±5% 경계)로 24시간(86,400 모의 초)을 메모리 위에서 재생한다. 시계는 주입 µs(모의 시간) — 실시간 1초 미만으로 하루를 압축.
//
//  구성(실 게이팅 사슬 · 실 브로커 0): PaperTradingSimulationHarness(7계층 인터락 · 라우터 게이트 · 드레인 · 가상 브로커) +
//    QuantSignalProvider(전략 이상 비트 27) + DashboardSnapshotHub(관제 · 포렌식 체인) + HealthMonitoringAgent(실 Working Set · GC 정지 ·
//    스레드풀 탐침 · 비트 21) + CascadingStormFreezeGate(연쇄 격발 · 블랙박스 봉인) + UnmanagedOrderBookBuffer(GC 밖 호가 표) + MMF 블랙박스.
//  1 모의 초 = 종목별 틱 1 + (5초마다) 10호가 + (60초마다) 매수/매도 1주 지정가 + 드레인 펌프 + 관제 펌프. 12시간째 누적 거래량 리셋(거래일 경계).
//  1 모의 시간마다 표본(관리 힙 · Working Set · Gen0/1/2 · 이 스레드 누적 할당 · 스레드풀 대기 · 집계어)을 고정 배열에 기록 + 헬스 스캔.
//  ★오작동(False-Positive) 판정: 워밍업(피드 수립) 이후 집계어 raised 전이 0 · 블랙박스 InterlockRaised 0 · 스톰 게이트 미격발.
//  δ=0: RunSeconds 정상 경로 관리 힙 0(표본화 판독기 포함 — GC.GetGCMemoryInfo 는 내부 객체를 할당하므로 시작/종료에서만).
//
//  [v12.18.4-DEV · 전 사슬 가속 Soak] fullChain = 호스트(BlazorHomeForm)와 같은 선택 결속을 전부 건다:
//    VolatilityAdaptiveCalibrator + SignalChurnCircuitBreaker → QuantSignalProvider(국면 임계 · 승인 1분 창) · 허브 AttachCalibration ·
//    OrderPathStageProfiler → 디스패치 링 PathObserver(발행 · 종결 스탬프) + 허브 orderPath(지연 관제) ·
//    MonotonicSettingAuditLog(휘발 · 같은 StrategyAnomalyInterlock = 비트 27) → 허브 AttachAuditLedger(배지 · seqlock 스냅샷) ·
//    MicrosecondJitterSentinel(방향 A) → 종목 틱 · 호가 · 펌프 구간마다 실시간 지연 표본(500µs 초과 = 비관리 링 · 상위 16 꼬리).
//   regimeCycleSeconds > 0 = 국면 교차 시세: 짝수 구간 = 추세(종목별 한 방향 ±1틱 · ±5% 반사 · 매수/매도 잔량 한쪽 우세) ·
//    홀수 구간 = 횡보(틱마다 ±1 교대 · 잔량 무작위 넓은 잡음). 보정기가 Trend ↔ Choppy 를 실제로 오가는지(ChoppyEntries) 실측한다.
//   RunTicks(n) = 종목 틱 n 회 이상(종목 수 단위 올림). RunOrderBurst = 주문 경로 스레드용 고속 제안 루프(모의 시계 제안당 전진 · 펌프 주기).
//   ★스레드 계약(기존과 같음): Run* / Warmup / RunOrderBurst = 한 스레드(소유 스레드). 다른 스레드는 허브 Acquire/Release ·
//    원장 Snapshot · TightenOnlyUiSettingWriter.TryWrite(원장 = 자체 잠금) 만 — 실제 호스트의 UI · 설정 스레드와 같은 경계.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Blazor.Services;
using StockManager.Buffers;
using StockManager.Engines;
using StockManager.Security;
using StockManager.Telemetry;

namespace StockManager.Simulation
{
    public struct SoakSample
    {
        public long SimSecond, ManagedHeap, WorkingSet, Gen0, Gen1, Gen2, ThreadAllocated, PendingWorkItems, Mask;
    }

    public readonly record struct LongSoakReport(long SimSeconds, long Ticks, long Books, long Orders, long Placed, long Filled,
        long RaisedAfterWarmup, long BlackboxRaisedAfterWarmup, long FinalMask, bool StormTripped, int HealthBreach,
        long Gen0Delta, long Gen1Delta, long Gen2Delta, long ThreadAllocatedDelta, long ManagedHeapDelta, long WorkingSetDelta, int Samples);

    public sealed class LongSoakProfileHarness : IDisposable
    {
        public const long Px = 70_000, Tick = 100;
        private static readonly long[] DefaultCodes = { 5930, 660, 35420, 51910, 5380, 207940, 68270, 105560 };

        private readonly long[] _codes, _px, _base, _cumVol;
        private readonly OrderBookPayload[] _books;
        private readonly SoakSample[] _samples = new SoakSample[64];
        private int _sampleCount;
        private long _nowMicros = 1_000_000, _simSecond, _ticks, _booksFed, _orders, _wsOverride;
        private ulong _rng;
        private long _warmRaised, _warmBoxSeq;
        // [v12.18.4-DEV] 전 사슬 · 국면 교차 · 지연 표본
        private readonly long[] _dir;
        private readonly long _regimeCycle, _bookEvery;
        private long _proposals;
        private long _excludedPauseTicks;                // [v12.18.4-DEV] 하네스 Settle 이 유발한 GC 정지(헬스 GC 정지 판정에서 제외)
        public const byte KindTick = 0, KindBook = 1, KindPump = 2, KindOrder = 3;

        public PaperTradingSimulationHarness Sim { get; }
        public QuantSignalProvider Quant { get; }
        public StrategyAnomalyInterlock Anomaly { get; }
        public DashboardSnapshotHub Hub { get; }
        public HealthMonitoringAgent Health { get; }
        public CascadingStormFreezeGate Storm { get; }
        public UnmanagedOrderBookBuffer NativeBooks { get; }
        public MmfFlightRecorder Blackbox { get; }
        public VolatilityAdaptiveCalibrator? Calibrator { get; }
        public SignalChurnCircuitBreaker? Churn { get; }
        public OrderPathStageProfiler? Stages { get; }
        public MonotonicSettingAuditLog? Ledger { get; }
        public MicrosecondJitterSentinel? Jitter { get; }
        public long Ticks => _ticks;
        public long Proposals => _proposals;
        /// <summary>현재 국면 구간(국면 교차 꺼짐 = false).</summary>
        public bool ChoppyPhase => _regimeCycle > 0 && ((_simSecond / _regimeCycle) & 1) == 1;
        public long SimSecond => _simSecond;
        public ReadOnlySpan<SoakSample> Samples => _samples.AsSpan(0, _sampleCount);

        public LongSoakProfileHarness(ulong seed = 0x5EED_2026_1007UL, long[]? codes = null, long maxWorkingSetBytes = 1L << 30,
                                      bool fullChain = false, int ledgerCapacity = MonotonicSettingAuditLog.DefaultCapacity,
                                      long regimeCycleSeconds = 0, int bookEverySeconds = 5)
        {
            if (bookEverySeconds < 1) throw new ArgumentOutOfRangeException(nameof(bookEverySeconds));
            _codes = codes ?? DefaultCodes;
            _px = new long[_codes.Length]; _base = new long[_codes.Length]; _cumVol = new long[_codes.Length]; _dir = new long[_codes.Length];
            _books = new OrderBookPayload[_codes.Length];
            for (int i = 0; i < _codes.Length; i++) { _px[i] = _base[i] = Px + Tick * 10 * i; _books[i].SymbolCode = _codes[i]; _dir[i] = (i & 1) == 0 ? 1 : -1; }
            _rng = seed == 0 ? 1 : seed;
            _regimeCycle = Math.Max(0, regimeCycleSeconds);
            _bookEvery = bookEverySeconds;
            Blackbox = new MmfFlightRecorder(1 << 14);
            if (fullChain) Stages = new OrderPathStageProfiler(1024);
            DashboardSnapshotHub? hub = null;
            Sim = new PaperTradingSimulationHarness(nowMicros: () => Volatile.Read(ref _nowMicros), blackbox: Blackbox,
                                                    fillObserver: f => hub!.OnFill(f, 2), pathObserver: Stages);
            Hub = hub = new DashboardSnapshotHub(Sim.Interlocks, nowTicks: () => Volatile.Read(ref _nowMicros) * 10, orderPath: Stages);
            Sim.Recorder.Chain(Hub);
            Anomaly = new StrategyAnomalyInterlock();
            Anomaly.BindInterlock(Sim.Interlocks, ArmingInterlockSource.StrategyAnomaly);
            if (fullChain)
            {
                Calibrator = new VolatilityAdaptiveCalibrator();
                Churn = new SignalChurnCircuitBreaker(Anomaly);
                Ledger = MonotonicSettingAuditLog.CreateVolatile(Anomaly, ledgerCapacity, utcTicks: () => Volatile.Read(ref _nowMicros) * 10);
                Hub.AttachCalibration(Calibrator, Churn);
                Hub.AttachAuditLedger(Ledger);
                Jitter = new MicrosecondJitterSentinel();
            }
            Quant = new QuantSignalProvider(Anomaly, Hub, nowMicros: () => Volatile.Read(ref _nowMicros), calibrator: Calibrator, churn: Churn);
            Health = new HealthMonitoringAgent(maxWorkingSetBytes,
                workingSet: () => { long o = Volatile.Read(ref _wsOverride); return o > 0 ? o : Environment.WorkingSet; },
                gcPauseTicks: () => GC.GetTotalPauseDuration().Ticks - Volatile.Read(ref _excludedPauseTicks));
            Health.BindInterlock(Sim.Interlocks, ArmingInterlockSource.HealthAgent);
            Storm = new CascadingStormFreezeGate(Blackbox);
            Sim.Recorder.Chain(Storm);
            NativeBooks = new UnmanagedOrderBookBuffer(64, nowMicros: () => Volatile.Read(ref _nowMicros));
        }

        /// <summary>
        /// [v12.18.4-DEV] 측정 창 직전 정리(콜드) — 강제 전체 GC(압축 · 종료자 대기 · 재수집) 후, 그 정지 시간을 헬스 GC 정지 판정에서 뺀다.
        ///  왜: 헬스 에이전트는 스캔 간 GC 정지 누적 증가 > 500ms 를 '정지 폭주'(비트 21)로 본다. 테스트가 스스로 일으킨 강제 GC 는
        ///  엔진 부하가 아니다 — 스위트 후반의 큰 힙 · 메모리 압박(페이징)에서 압축 GC 가 그 한도에 닿으면 오작동 판정이 섞인다.
        ///  제외는 이 호출 구간의 정지 증가분만(그 밖의 GC 정지는 그대로 판정) · 프로덕션 에이전트 코드 무변경(주입 시계만).
        /// </summary>
        public void Settle()
        {
            long before = GC.GetTotalPauseDuration().Ticks;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            Interlocked.Add(ref _excludedPauseTicks, GC.GetTotalPauseDuration().Ticks - before);
        }

        public long ExcludedPauseMicros => Volatile.Read(ref _excludedPauseTicks) / 10;

        /// <summary>인위적 자원 폭주 주입(Working Set 보고값 덮어쓰기 · 0 = 실측 복귀). 다음 헬스 스캔에서 판정.</summary>
        public void InjectWorkingSet(long bytes) => Volatile.Write(ref _wsOverride, bytes);

        /// <summary>피드 수립(참조가 · 깊이) + 1회 펌프 → 워밍업 종료 기준선(이후 raised 전이 = 오작동 후보).</summary>
        public void Warmup()
        {
            for (int s = 0; s < 3; s++) StepSecond();
            Storm.Arm();
            _warmRaised = Sim.Recorder.RaisedEvents;
            _warmBoxSeq = Blackbox.ClaimedSeq;
        }

        public long RaisedSinceWarmup => Sim.Recorder.RaisedEvents - _warmRaised;
        public long BlackboxSeqAtWarmup => _warmBoxSeq;

        /// <summary>종목 틱이 n 회 이상 될 때까지 모의 초 구동(δ=0 · 종목 수 단위 올림). 반환 = 이번 호출의 틱 수.</summary>
        public long RunTicks(long ticks, long sampleEverySeconds = 3_600)
        {
            long start = _ticks;
            while (_ticks - start < ticks)
            {
                StepSecond();
                if (_simSecond % sampleEverySeconds == 0) { Health.Scan(); Sample(); }
            }
            return _ticks - start;
        }

        /// <summary>
        /// [v12.18.4-DEV] 주문 경로 고속 제안 루프(소유 스레드 · δ=0) — 제안마다 모의 시계 advanceMicros 전진 · 종목 순환 틱 1 + 매수/매도 교대 지정가 1주 ·
        /// bookEvery 제안마다 그 종목 10호가 · pumpEvery 제안마다 드레인 + 관제 펌프. 반환 = 제안 수.
        /// </summary>
        public int RunOrderBurst(int proposals, int pumpEvery = 64, int bookEvery = 256, long advanceMicros = 20)
        {
            if (pumpEvery < 1 || bookEvery < 1 || advanceMicros < 1) throw new ArgumentOutOfRangeException(nameof(pumpEvery));
            for (int k = 0; k < proposals; k++)
            {
                Interlocked.Add(ref _nowMicros, advanceMicros);
                long p = ++_proposals;
                int i = (int)(p % _codes.Length);
                long t0 = MicrosecondJitterSentinel.Begin(out int g0);
                Tickle(i, (long)(Next() % 3) - 1);
                if (p % bookEvery == 0) FeedBook(i);
                if ((p & 1) == 0) Sim.Submit(_codes[i], OrderSide.Buy, 1, _px[i] + Tick, 1);
                else Sim.Submit(_codes[i], OrderSide.Sell, 1, _px[i], 1);
                _orders++;
                if (p % pumpEvery == 0) { Sim.Pump(); Hub.Pump(); }
                Jitter?.End(t0, g0, KindOrder, _nowMicros);
            }
            return proposals;
        }

        /// <summary>모의 n 초 구동(δ=0). sampleEverySeconds 마다 표본 + 헬스 스캔.</summary>
        public void RunSeconds(long seconds, long sampleEverySeconds = 3_600)
        {
            for (long k = 0; k < seconds; k++)
            {
                StepSecond();
                if (_simSecond % sampleEverySeconds == 0) { Health.Scan(); Sample(); }
            }
        }

        private void StepSecond()
        {
            _simSecond++;
            Interlocked.Add(ref _nowMicros, 1_000_000);
            bool book = _simSecond % _bookEvery == 0, order = _simSecond % 60 == 0;
            if (_simSecond == 43_200) for (int i = 0; i < _cumVol.Length; i++) _cumVol[i] = 0;   // 거래일 경계(누적 거래량 리셋)
            bool regimes = _regimeCycle > 0, choppy = ChoppyPhase;
            for (int i = 0; i < _codes.Length; i++)
            {
                long t0 = MicrosecondJitterSentinel.Begin(out int g0);
                long step = !regimes ? (long)(Next() % 3) - 1                          // −1 · 0 · +1 틱
                          : choppy ? (((_simSecond + i) & 1) == 0 ? 1 : -1)            // 횡보: 틱마다 교대(효율 ≈ 0)
                          : _dir[i];                                                     // 추세: 한 방향
                Tickle(i, step);
                Jitter?.End(t0, g0, KindTick, _nowMicros);
                if (book || _simSecond <= 3)
                {
                    t0 = MicrosecondJitterSentinel.Begin(out g0);
                    FeedBook(i);
                    Jitter?.End(t0, g0, KindBook, _nowMicros);
                }
            }
            if (order)
            {
                int i = (int)((_simSecond / 60) % _codes.Length);
                Sim.Submit(_codes[i], OrderSide.Buy, 1, _px[i] + Tick, 1);
                Sim.Submit(_codes[i], OrderSide.Sell, 1, _px[i], 1);
                _orders += 2;
            }
            long tp = MicrosecondJitterSentinel.Begin(out int gp);
            Sim.Pump();
            Hub.Pump();
            Jitter?.End(tp, gp, KindPump, _nowMicros);
        }

        // 종목 i 1틱 이동(±5% 경계 반사 — 추세 방향도 뒤집음) → Toss 틱 · 퀀트 체결 · 관제 틱
        private void Tickle(int i, long step)
        {
            long dev = _px[i] - _base[i];
            if (dev > _base[i] / 20) { step = -1; _dir[i] = -1; } else if (dev < -_base[i] / 20) { step = 1; _dir[i] = 1; }
            _px[i] += step * Tick;
            _cumVol[i] += 1 + (long)(Next() % 50);
            Sim.OnTossTick(_codes[i], _px[i]);
            Quant.OnTrade(_codes[i], _px[i], _cumVol[i]);
            Hub.OnTick(_codes[i], _px[i]);
            _ticks++;
        }

        // 종목 i 10호가. 국면 교차: 추세 = 진행 방향 잔량 우세(일관 불균형) · 횡보 = 넓은 무작위 잔량(불균형 잡음). 기본 = 500~999 균등.
        private void FeedBook(int i)
        {
            ref var b = ref _books[i];
            bool regimes = _regimeCycle > 0, choppy = ChoppyPhase;
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = _px[i] + Tick * (l + 1); b.BidPrice[l] = _px[i] - Tick * l;
                if (!regimes) { b.AskQty[l] = 500 + (long)(Next() % 500); b.BidQty[l] = 500 + (long)(Next() % 500); continue; }
                long heavy = 900 + (long)(Next() % 200), light = 300 + (long)(Next() % 200);
                if (choppy) { b.AskQty[l] = 100 + (long)(Next() % 1_900); b.BidQty[l] = 100 + (long)(Next() % 1_900); }
                else if (_dir[i] > 0) { b.BidQty[l] = heavy; b.AskQty[l] = light; }
                else { b.BidQty[l] = light; b.AskQty[l] = heavy; }
            }
            b.TimestampTicks = _nowMicros * 10;
            Sim.OnKiwoomBook(in b);
            Quant.OnBook(in b);
            NativeBooks.Write(in b);
            _booksFed++;
        }

        private void Sample()
        {
            if (_sampleCount == _samples.Length) return;
            _samples[_sampleCount++] = new SoakSample
            {
                SimSecond = _simSecond, ManagedHeap = GC.GetTotalMemory(false), WorkingSet = Environment.WorkingSet,
                Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
                ThreadAllocated = GC.GetAllocatedBytesForCurrentThread(), PendingWorkItems = ThreadPool.PendingWorkItemCount, Mask = Sim.Interlocks.Mask,
            };
        }

        /// <summary>측정 기준선 재설정(콜드) — 표본을 비우고 지금 1건 기록. 호출측이 직전에 GC 를 정리하면 이전 테스트 가비지가 측정 창 GC 로 섞이지 않는다.</summary>
        public void ResetBaseline() { _sampleCount = 0; Sample(); }

        /// <summary>보고(콜드). 표본 0 · 1 이면 델타 0.</summary>
        public LongSoakReport Report()
        {
            var s = Samples;
            ref readonly var a = ref s[0];
            ref readonly var z = ref s[^1];
            long boxRaised = 0;
            var snap = new FlightRecordEvent[Blackbox.Capacity];
            int n = Blackbox.SnapshotRecent(snap);
            for (int i = 0; i < n; i++) if (snap[i].Event == FlightEventKind.InterlockRaised && snap[i].Sequence > _warmBoxSeq) boxRaised++;
            return new LongSoakReport(_simSecond, _ticks, _booksFed, _orders, Sim.Broker.Placed, Sim.Broker.Filled,
                RaisedSinceWarmup, boxRaised, Sim.Interlocks.Mask, Storm.Tripped, (int)Health.Breach,
                z.Gen0 - a.Gen0, z.Gen1 - a.Gen1, z.Gen2 - a.Gen2, z.ThreadAllocated - a.ThreadAllocated,
                z.ManagedHeap - a.ManagedHeap, z.WorkingSet - a.WorkingSet, s.Length);
        }

        private ulong Next()
        {
            ulong x = _rng;
            x ^= x >> 12; x ^= x << 25; x ^= x >> 27;
            _rng = x;
            return x * 0x2545F4914F6CDD1DUL;
        }

        public void Dispose()
        {
            Health.Dispose();
            Jitter?.Dispose();
            Ledger?.Dispose();
            NativeBooks.Dispose();
            Sim.Dispose();
            Hub.Dispose();
        }
    }
}
