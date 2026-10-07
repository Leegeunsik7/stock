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
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Blazor.Services;
using StockManager.Buffers;
using StockManager.Engines;
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

        public PaperTradingSimulationHarness Sim { get; }
        public QuantSignalProvider Quant { get; }
        public StrategyAnomalyInterlock Anomaly { get; }
        public DashboardSnapshotHub Hub { get; }
        public HealthMonitoringAgent Health { get; }
        public CascadingStormFreezeGate Storm { get; }
        public UnmanagedOrderBookBuffer NativeBooks { get; }
        public MmfFlightRecorder Blackbox { get; }
        public long SimSecond => _simSecond;
        public ReadOnlySpan<SoakSample> Samples => _samples.AsSpan(0, _sampleCount);

        public LongSoakProfileHarness(ulong seed = 0x5EED_2026_1007UL, long[]? codes = null, long maxWorkingSetBytes = 1L << 30)
        {
            _codes = codes ?? DefaultCodes;
            _px = new long[_codes.Length]; _base = new long[_codes.Length]; _cumVol = new long[_codes.Length];
            _books = new OrderBookPayload[_codes.Length];
            for (int i = 0; i < _codes.Length; i++) { _px[i] = _base[i] = Px + Tick * 10 * i; _books[i].SymbolCode = _codes[i]; }
            _rng = seed == 0 ? 1 : seed;
            Blackbox = new MmfFlightRecorder(1 << 14);
            DashboardSnapshotHub? hub = null;
            Sim = new PaperTradingSimulationHarness(nowMicros: () => Volatile.Read(ref _nowMicros), blackbox: Blackbox,
                                                    fillObserver: f => hub!.OnFill(f, 2));
            Hub = hub = new DashboardSnapshotHub(Sim.Interlocks, nowTicks: () => Volatile.Read(ref _nowMicros) * 10);
            Sim.Recorder.Chain(Hub);
            Anomaly = new StrategyAnomalyInterlock();
            Anomaly.BindInterlock(Sim.Interlocks, ArmingInterlockSource.StrategyAnomaly);
            Quant = new QuantSignalProvider(Anomaly, Hub, nowMicros: () => Volatile.Read(ref _nowMicros));
            Health = new HealthMonitoringAgent(maxWorkingSetBytes,
                workingSet: () => { long o = Volatile.Read(ref _wsOverride); return o > 0 ? o : Environment.WorkingSet; });
            Health.BindInterlock(Sim.Interlocks, ArmingInterlockSource.HealthAgent);
            Storm = new CascadingStormFreezeGate(Blackbox);
            Sim.Recorder.Chain(Storm);
            NativeBooks = new UnmanagedOrderBookBuffer(64, nowMicros: () => Volatile.Read(ref _nowMicros));
        }

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
            bool book = _simSecond % 5 == 0, order = _simSecond % 60 == 0;
            if (_simSecond == 43_200) for (int i = 0; i < _cumVol.Length; i++) _cumVol[i] = 0;   // 거래일 경계(누적 거래량 리셋)
            for (int i = 0; i < _codes.Length; i++)
            {
                long step = (long)(Next() % 3) - 1;                                    // −1 · 0 · +1 틱
                long dev = _px[i] - _base[i];
                if (dev > _base[i] / 20) step = -1; else if (dev < -_base[i] / 20) step = 1;   // ±5% 경계 반사
                _px[i] += step * Tick;
                _cumVol[i] += 1 + (long)(Next() % 50);
                Sim.OnTossTick(_codes[i], _px[i]);
                Quant.OnTrade(_codes[i], _px[i], _cumVol[i]);
                Hub.OnTick(_codes[i], _px[i]);
                _ticks++;
                if (book || _simSecond <= 3)
                {
                    ref var b = ref _books[i];
                    for (int l = 0; l < 10; l++)
                    {
                        b.AskPrice[l] = _px[i] + Tick * (l + 1); b.BidPrice[l] = _px[i] - Tick * l;
                        b.AskQty[l] = 500 + (long)(Next() % 500); b.BidQty[l] = 500 + (long)(Next() % 500);
                    }
                    b.TimestampTicks = _nowMicros * 10;
                    Sim.OnKiwoomBook(in b);
                    Quant.OnBook(in b);
                    NativeBooks.Write(in b);
                    _booksFed++;
                }
            }
            if (order)
            {
                int i = (int)((_simSecond / 60) % _codes.Length);
                Sim.Submit(_codes[i], OrderSide.Buy, 1, _px[i] + Tick, 1);
                Sim.Submit(_codes[i], OrderSide.Sell, 1, _px[i], 1);
                _orders += 2;
            }
            Sim.Pump();
            Hub.Pump();
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
            NativeBooks.Dispose();
            Sim.Dispose();
            Hub.Dispose();
        }
    }
}
