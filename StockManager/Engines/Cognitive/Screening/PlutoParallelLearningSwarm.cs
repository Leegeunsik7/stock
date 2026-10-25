// ============================================================================
// PlutoParallelLearningSwarm — v12.18.80-DEV: 플루토 3대 자산 병렬 학습 스웜(Channels 워커 풀 · 시장별 전용 스레드 BelowNormal 격리 · CPU 듀티 스로틀)
//
//   레인 3개(KRX · US · 업비트 KRW) = 시장별 전용 백그라운드 스레드 1개 + 유계 Channel<int>(카탈로그 인덱스 · 용량 = 카탈로그 수 · DropOldest · 단일 판독자).
//   스윕 = 그 시장 카탈로그 전 종목 인덱스 투입 → 워커가 종목마다:
//     소스 미부착/미결선(IsConnected false) → 매트릭스 '실피드 연결 대기'(조회 0) ·
//     소스 Fetch(동기 · 워커 스레드) → Ok = PlutoBarFactorBuilder 판독 → 매트릭스 Live + 데이터 레이크 봉 교체 ·
//     NoData/Faulted = 그 슬롯 비움(오래된 판독 재사용 0) · RateLimited(HTTP 429) = 그 스윕 즉시 중단(남은 인덱스 폐기 · 재시도/큐잉 0) + 쿨다운(기본 60초 · 스윕 거부).
//   격리: 워커 스레드 우선순위 BelowNormal(체결/정산 스레드 선점 0) · 종목 1건 처리 후 듀티 스로틀(작업 시간 × (100 − 듀티%) ÷ 듀티% 휴지 · 기본 듀티 25%).
//   스케줄 = PeriodicTimer(기본 60초) — 진행 중 스윕 ∨ 쿨다운 레인 = 건너뜀(중복 투입 0).
//   ★날조 0 — 실데이터 판독만 Live. ★liveArmed:false — 조회(GET)만 · 주문/무장/브로커 경로 0. ★QuantumVault/피어 접근 0.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using StockManager.Engines.Cognitive.Valuation;

namespace StockManager.Engines.Cognitive.Screening
{
    /// <summary>레인 1개 상태(판독 스냅샷 · 콜드).</summary>
    public readonly struct PlutoSwarmLaneStatus
    {
        public readonly PlutoScreenMarket Market;
        public readonly bool Attached, Connected, Sweeping, CoolingDown;
        public readonly string Label;
        public readonly int Monitored, Live, Awaiting, NoData, Faulted;
        public readonly PlutoLakeStats Stats;
        public readonly long DroppedOnRateLimit;
        public readonly ThreadPriority Priority;

        public PlutoSwarmLaneStatus(PlutoScreenMarket market, bool attached, bool connected, bool sweeping, bool coolingDown, string label,
            int monitored, int live, int awaiting, int noData, int faulted, in PlutoLakeStats stats, long droppedOnRateLimit, ThreadPriority priority)
        {
            Market = market; Attached = attached; Connected = connected; Sweeping = sweeping; CoolingDown = coolingDown; Label = label;
            Monitored = monitored; Live = live; Awaiting = awaiting; NoData = noData; Faulted = faulted; Stats = stats;
            DroppedOnRateLimit = droppedOnRateLimit; Priority = priority;
        }
    }

    public sealed class PlutoParallelLearningSwarm : IDisposable
    {
        public const int DefaultDutyPercent = 25;
        public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);

        private sealed class Lane
        {
            public readonly PlutoScreenMarket Market;
            public readonly Channel<int> Queue;
            public readonly PlutoAssetSample Sample = new();
            public IPlutoAssetDataSource? Source;
            public PlutoValuationBuffer? Valuation;
            public Thread? Worker;
            public int Sweeping;                 // 0 = 유휴 · 2 = 투입 중 · 1 = 투입 완료 · 미처리(워커 = 1 → 0 만 완료 판정)
            public long CooldownUntilUtc;
            public long Dropped;

            public Lane(PlutoScreenMarket market, int capacity)
            {
                Market = market;
                Queue = Channel.CreateBounded<int>(new BoundedChannelOptions(Math.Max(1, capacity))
                { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
            }
        }

        private readonly PlutoUniverseMatrixBoard _board;
        private readonly PlutoMultiAssetDataLake _lake;
        private readonly Func<long> _utcNow;
        private readonly Lane[] _lanes = new Lane[PlutoRecommendationSnapshot.Markets];
        private readonly int _dutyPercent;
        private readonly TimeSpan _cooldown;
        private readonly object _gate = new();
        private CancellationTokenSource? _cts;
        private Task? _scheduler;
        private long _workerFaults;
        private bool _disposed;

        public PlutoParallelLearningSwarm(PlutoUniverseMatrixBoard board, PlutoMultiAssetDataLake lake,
            int dutyPercent = DefaultDutyPercent, TimeSpan? cooldown = null, Func<long>? utcNow = null)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _lake = lake ?? throw new ArgumentNullException(nameof(lake));
            if (!ReferenceEquals(lake.Layout, board)) throw new ArgumentException("데이터 레이크 슬롯 배치 ≠ 매트릭스 보드", nameof(lake));
            if (dutyPercent is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(dutyPercent));
            _dutyPercent = dutyPercent;
            _cooldown = cooldown ?? DefaultCooldown;
            _utcNow = utcNow ?? (static () => DateTime.UtcNow.Ticks);
            for (int m = 0; m < _lanes.Length; m++) _lanes[m] = new Lane((PlutoScreenMarket)m, board.CountOf((PlutoScreenMarket)m));
        }

        public PlutoUniverseMatrixBoard Board => _board;
        public PlutoMultiAssetDataLake Lake => _lake;
        public int DutyPercent => _dutyPercent;
        public long WorkerFaults => Interlocked.Read(ref _workerFaults);
        public bool Running => Volatile.Read(ref _scheduler) is { IsCompleted: false };

        /// <summary>시장 소스 부착(기동 전 · 같은 시장 재부착 = 교체 · 소스 시장 불일치 = 예외).</summary>
        public void Attach(IPlutoAssetDataSource source, PlutoValuationBuffer? valuation = null)
        {
            ArgumentNullException.ThrowIfNull(source);
            if ((uint)source.Market >= (uint)_lanes.Length) throw new ArgumentOutOfRangeException(nameof(source));
            lock (_gate)
            {
                if (_cts is not null) throw new InvalidOperationException("스웜 기동 후 소스 부착 불가");
                var lane = _lanes[(int)source.Market];
                lane.Source = source;
                lane.Valuation = valuation;
            }
        }

        /// <summary>시장 스윕 투입(진행 중 ∨ 쿨다운 = false · 중복 투입 0).</summary>
        public bool RequestSweep(PlutoScreenMarket market)
        {
            if ((uint)market >= (uint)_lanes.Length) return false;
            var lane = _lanes[(int)market];
            if (_utcNow() < Interlocked.Read(ref lane.CooldownUntilUtc)) return false;
            if (Interlocked.CompareExchange(ref lane.Sweeping, 2, 0) != 0) return false;
            int n = _board.CountOf(market);
            for (int i = 0; i < n; i++) lane.Queue.Writer.TryWrite(i);
            Volatile.Write(ref lane.Sweeping, n == 0 ? 0 : 1);
            return true;
        }

        /// <summary>종목 1건 처리(워커 스레드 · 테스트 직접 구동 가능). 반환 = 결과(RateLimited = 호출측 스윕 중단).</summary>
        public PlutoFetchOutcome ProcessOne(PlutoScreenMarket market, int catalogIndex, CancellationToken ct)
        {
            var lane = _lanes[(int)market];
            int slot = _board.SlotOf(market, catalogIndex);
            if (slot < 0) return PlutoFetchOutcome.Faulted;
            var entry = PlutoMultiAssetUniverseCatalog.Of(market)[catalogIndex];
            var src = lane.Source;
            long now = _utcNow();
            if (src is null || !src.IsConnected)
            {
                _board.Write(slot, PlutoMatrixStatus.Awaiting, default, now, 0);
                _lake.Record(slot, PlutoFetchOutcome.NotConnected, ReadOnlySpan<PlutoOhlcvBar>.Empty, now);
                return PlutoFetchOutcome.NotConnected;
            }
            if (entry.Symbol <= 0)
            {
                _board.Write(slot, PlutoMatrixStatus.NoData, default, now, 0);   // 키 인코딩 불가 마켓(9자 이상) = 점수 식별 불가 → 조회 0(레이트 예산 보존)
                _lake.Record(slot, PlutoFetchOutcome.NoData, ReadOnlySpan<PlutoOhlcvBar>.Empty, now);
                return PlutoFetchOutcome.NoData;
            }
            var sample = lane.Sample;
            sample.Clear();
            PlutoFetchOutcome r;
            try { r = src.Fetch(entry, sample, ct); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { r = PlutoFetchOutcome.Faulted; Interlocked.Increment(ref _workerFaults); }
            now = _utcNow();
            if (r == PlutoFetchOutcome.Ok)
            {
                PlutoValuationTag val = default;
                var inputs0 = PlutoBarFactorBuilder.Build(market, entry.Symbol, sample, in val);
                if (inputs0.Price > 0 && lane.Valuation is { } vb && vb.TryRead(entry.Symbol, out var vf))
                {
                    val = new PlutoValuationTag(in vf, inputs0.Price, now);
                    inputs0 = PlutoBarFactorBuilder.Build(market, entry.Symbol, sample, in val);
                }
                int bars = Math.Clamp(sample.BarCount, 0, sample.Bars.Length);
                if (inputs0.Price <= 0) r = PlutoFetchOutcome.NoData;
                else
                {
                    _board.Write(slot, PlutoMatrixStatus.Live, in inputs0, now, bars);
                    _lake.Record(slot, PlutoFetchOutcome.Ok, sample.Bars.AsSpan(0, bars), now);
                    return r;
                }
            }
            _board.Write(slot, r == PlutoFetchOutcome.NoData ? PlutoMatrixStatus.NoData : PlutoMatrixStatus.Faulted, default, now, 0);
            _lake.Record(slot, r, ReadOnlySpan<PlutoOhlcvBar>.Empty, now);
            if (r == PlutoFetchOutcome.RateLimited) TripCooldown(lane, now);
            return r;
        }

        private void TripCooldown(Lane lane, long now)
        {
            Interlocked.Exchange(ref lane.CooldownUntilUtc, now + _cooldown.Ticks);
            while (lane.Queue.Reader.TryRead(out _)) Interlocked.Increment(ref lane.Dropped);   // 남은 인덱스 폐기(재시도 · 큐잉 0)
        }

        /// <summary>워커 스레드 3개(BelowNormal) + 스윕 스케줄러 기동(중복 = 무시).</summary>
        public void Start(TimeSpan sweepInterval)
        {
            if (sweepInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sweepInterval));
            lock (_gate)
            {
                if (_cts is not null || _disposed) return;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                foreach (var lane in _lanes)
                {
                    var l = lane;
                    l.Worker = new Thread(() => WorkerLoop(l, ct))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.BelowNormal,
                        Name = "PlutoSwarm-" + l.Market.ToString(),
                    };
                    l.Worker.Start();
                }
                _scheduler = Task.Run(() => ScheduleAsync(sweepInterval, ct));
            }
        }

        private async Task ScheduleAsync(TimeSpan interval, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(interval);
            while (!ct.IsCancellationRequested)
            {
                for (int m = 0; m < _lanes.Length; m++) RequestSweep((PlutoScreenMarket)m);
                try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return; }
                catch (OperationCanceledException) { return; }
            }
        }

        private void WorkerLoop(Lane lane, CancellationToken ct)
        {
            var reader = lane.Queue.Reader;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!reader.TryRead(out int idx))
                    {
                        if (Interlocked.CompareExchange(ref lane.Sweeping, 0, 1) == 1) _lake.CountSweep(lane.Market);   // 투입 중(2) = 완료 판정 보류
                        if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult()) return;
                        continue;
                    }
                    long t0 = Stopwatch.GetTimestamp();
                    try { ProcessOne(lane.Market, idx, ct); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception) { Interlocked.Increment(ref _workerFaults); }   // 레인 지속(다음 종목 · 다음 스윕)
                    Throttle(Stopwatch.GetTimestamp() - t0, ct);
                }
            }
            catch (OperationCanceledException) { }
        }

        /// <summary>듀티 스로틀 휴지(ms) = 작업 시간 × (100 − 듀티) ÷ 듀티(상한 2초).</summary>
        public static int ThrottleMillis(long workTicks, int dutyPercent)
        {
            if (workTicks <= 0 || dutyPercent >= 100) return 0;
            long workMs = workTicks * 1000 / Stopwatch.Frequency;
            return (int)Math.Min(2000, workMs * (100 - dutyPercent) / Math.Max(1, dutyPercent));
        }

        private void Throttle(long workTicks, CancellationToken ct)
        {
            int ms = ThrottleMillis(workTicks, _dutyPercent);
            if (ms > 0) ct.WaitHandle.WaitOne(ms);
        }

        public PlutoSwarmLaneStatus StatusOf(PlutoScreenMarket market)
        {
            if ((uint)market >= (uint)_lanes.Length) return default;
            var lane = _lanes[(int)market];
            var src = lane.Source;
            bool cooling = _utcNow() < Interlocked.Read(ref lane.CooldownUntilUtc);
            var w = lane.Worker;
            return new PlutoSwarmLaneStatus(market, src is not null, src is { IsConnected: true }, Volatile.Read(ref lane.Sweeping) != 0, cooling,
                src?.Label ?? "실데이터 소스 미결선",
                _board.CountOf(market), _board.CountOf(market, PlutoMatrixStatus.Live), _board.CountOf(market, PlutoMatrixStatus.Awaiting),
                _board.CountOf(market, PlutoMatrixStatus.NoData), _board.CountOf(market, PlutoMatrixStatus.Faulted),
                _lake.StatsOf(market), Interlocked.Read(ref lane.Dropped), w?.Priority ?? ThreadPriority.BelowNormal);
        }

        public void Dispose()
        {
            CancellationTokenSource? cts;
            Task? scheduler;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                cts = _cts; scheduler = _scheduler;
            }
            if (cts is null) return;
            cts.Cancel();
            foreach (var lane in _lanes) lane.Queue.Writer.TryComplete();
            try { scheduler?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            foreach (var lane in _lanes) lane.Worker?.Join(TimeSpan.FromSeconds(2));
            cts.Dispose();
        }
    }
}
