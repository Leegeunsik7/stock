// ============================================================================
// HealthMonitoringAgent — [헬스 · 추천 A] 프로세스 자원 폭주 · 스레드풀 정체 · GC 정지 무할당 스캔 + Disarm
//
//  ★수정 전 공백: LiveSoakSentinel(v12.5.0 · 메모리 팽창/좀비 스레드/OOM/GC 추세)은 소스에만 있고 어디서도 생성되지 않았다 —
//    호스트에는 자원 폭주를 무장 차단으로 잇는 경로가 0 이었다.
//  스캔 항목(전용 스레드 · 기본 1초 · 스레드풀이 굶어도 스캔은 계속된다):
//   · Working Set — Environment.WorkingSet(관리 힙 0 · Process 객체 생성 없음) > 상한(기본 1GB) = 폭주.
//   · 스레드풀 정체 — 재사용 IThreadPoolWorkItem 탐침을 큐에 넣고 '실행까지 걸린 시간'을 잰다. 완료 지연 또는 아직 미실행인 탐침의
//     대기 시간이 임계(기본 100ms)를 넘으면 정체(스레드 고갈·동기 대기 폭주 — 타이머/비동기 콜백이 제때 못 도는 상태).
//   · GC 정지 — GC.GetTotalPauseDuration()(누적 · 힙 0)의 스캔 간 증가분 > 임계(기본 스캔 주기의 50%) = 정지 폭주.
//   · CPU — GetProcessTimes(현재 프로세스 의사 핸들 · LibraryImport) 커널+사용자 시간 증가분 / 벽시계 / 코어 수(‰) — 판정 아님(관측·하트비트).
//  위반 = sticky 트립(사유 비트) → ArmingInterlockSource.HealthAgent 비트 Raise(0ms · 발주 술어 차단). 복구 = 원인 제거 후 재시작
//  (운용자 재무장 UI 없음 · TryRearm 은 물리 이중키 계약 제공).
//  방향 A: 스캔마다 HostHeartbeatWriter 로 비관리 고정 64B 하트비트 사상 → 프록시 HostHeartbeatEgressGate 가 송신 직전 판독(방향 B).
//  δ=0: Scan() 경로 관리 힙 0(테스트 실측). 탐침 객체·스레드는 생성 시 1회.
//  [Long-Soak · 방향 B] 미세 누수 예측(MemoryTrend): 스캔 중 표본 간격(기본 1분)마다 Working Set 을 고정 링(128점)에 적재 → 표본 구간이
//    최소 창(기본 1시간) 이상이면 최소제곱 기울기 · R² · 상한 도달 예상 시간(ETA) 산출. 꾸준한 우상향(R² ≥ 0.9 · 기울기 > 0)이면서
//    ETA ≤ 예측 지평(기본 1시간)이면 = OutOfMemory 전에 발주 중단(사유 MemoryTrend · 같은 비트 21). 계단식 상한 초과(WorkingSet)를
//    기다리지 않는다. 기동 직후 성장 · GC 톱니(R² 낮음) · 상한까지 여유가 큰 완만한 증가는 무트립(오격발 방지 — 테스트 실측).
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Telemetry
{
    [Flags]
    public enum HealthBreach : int { None = 0, WorkingSet = 1, ThreadPoolStall = 2, GcPause = 4, MemoryTrend = 8 }

    public sealed partial class HealthMonitoringAgent : IArmingBlockSource, IDisposable
    {
        public const long DefaultMaxWorkingSetBytes = 1L << 30;      // 1 GB
        public const long DefaultMaxStallMicros = 100_000;           // 100 ms

        private sealed class PoolProbe : IThreadPoolWorkItem
        {
            public long EnqueuedTs;
            public long LatencyTs;        // 완료 시 큐 대기(타임스탬프 틱)
            public int Pending;           // 1 = 큐에 있음
            public void Execute()
            {
                Volatile.Write(ref LatencyTs, Stopwatch.GetTimestamp() - Volatile.Read(ref EnqueuedTs));
                Volatile.Write(ref Pending, 0);
            }
        }

        private readonly long _maxWs, _maxStallTicks, _maxPausePerScanTicks;
        private readonly Func<long> _workingSet, _gcPauseTicks, _now;
        private readonly HostHeartbeatWriter? _heartbeat;
        private readonly PoolProbe _probe = new();
        private readonly object _lifecycle = new();
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly bool _useThreadPoolProbe;
        private readonly Action<IThreadPoolWorkItem> _enqueue;   // 기본 = 스레드풀 · 테스트 = 실행 지연 주입
        private Thread? _thread;
        private ArmingInterlockLink? _link;
        private int _breach, _disposed;
        private long _lastPause = -1, _lastCpuTicks = -1, _lastWallTs, _scans, _lastWs, _lastStallMicros, _lastPauseDeltaMicros;
        private int _lastCpuPermille;
        // [방향 B] 미세 누수 추세(고정 링 · 스캔 스레드 단일 작성)
        public const int TrendCapacity = 128;
        private readonly long[] _trendTs = new long[TrendCapacity], _trendWs = new long[TrendCapacity];
        private readonly long _trendSampleTicks, _trendMinWindowTicks, _trendHorizonTicks;
        private readonly double _trendMinR2;
        private int _trendCount, _trendHead;
        private long _lastTrendTs = long.MinValue;
        private double _lastSlopePerSec, _lastR2, _lastEtaSec = double.PositiveInfinity;
        public double LastTrendSlopeBytesPerHour => Volatile.Read(ref _lastSlopePerSec) * 3600.0;
        public double LastTrendR2 => Volatile.Read(ref _lastR2);
        public double LastTrendEtaSeconds => Volatile.Read(ref _lastEtaSec);
        public int TrendSamples => Volatile.Read(ref _trendCount);
        private readonly long _warmupTicks;   // 기동 직후 유예(부팅 초기화가 스레드풀을 점유 — 정상 기동을 sticky 트립으로 오판 방지)
        private long _firstScanTs = long.MinValue;

        /// <param name="workingSet">주입(테스트) — 기본 Environment.WorkingSet.</param>
        /// <param name="gcPauseTicks">주입(테스트) — 기본 GC.GetTotalPauseDuration().Ticks.</param>
        /// <param name="maxGcPausePerScan">스캔 간 GC 정지 누적 증가 상한 — 기본 스캔 주기의 50%.</param>
        public HealthMonitoringAgent(long maxWorkingSetBytes = DefaultMaxWorkingSetBytes, long maxStallMicros = DefaultMaxStallMicros,
                                     TimeSpan? maxGcPausePerScan = null, HostHeartbeatWriter? heartbeat = null,
                                     Func<long>? workingSet = null, Func<long>? gcPauseTicks = null, Func<long>? nowTimestamp = null,
                                     bool useThreadPoolProbe = true, TimeSpan? warmup = null,
                                     Action<IThreadPoolWorkItem>? enqueueProbe = null,
                                     TimeSpan? trendSampleInterval = null, TimeSpan? trendMinWindow = null, TimeSpan? trendHorizon = null, double trendMinR2 = 0.9)
        {
            _trendSampleTicks = (long)((trendSampleInterval ?? TimeSpan.FromMinutes(1)).TotalSeconds * Stopwatch.Frequency);
            _trendMinWindowTicks = (long)((trendMinWindow ?? TimeSpan.FromHours(1)).TotalSeconds * Stopwatch.Frequency);
            _trendHorizonTicks = (long)((trendHorizon ?? TimeSpan.FromHours(1)).TotalSeconds * Stopwatch.Frequency);
            if (_trendSampleTicks <= 0 || _trendMinWindowTicks <= 0 || _trendHorizonTicks <= 0) throw new ArgumentOutOfRangeException(nameof(trendSampleInterval));
            if (!(trendMinR2 > 0 && trendMinR2 <= 1)) throw new ArgumentOutOfRangeException(nameof(trendMinR2));
            _trendMinR2 = trendMinR2;
            _enqueue = enqueueProbe ?? (static w => ThreadPool.UnsafeQueueUserWorkItem(w, preferLocal: false));
            _warmupTicks = (long)((warmup ?? TimeSpan.Zero).TotalSeconds * Stopwatch.Frequency);
            if (maxWorkingSetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxWorkingSetBytes));
            if (maxStallMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxStallMicros));
            _maxWs = maxWorkingSetBytes;
            _now = nowTimestamp ?? Stopwatch.GetTimestamp;
            _maxStallTicks = maxStallMicros * Stopwatch.Frequency / 1_000_000;
            _maxPausePerScanTicks = (maxGcPausePerScan ?? TimeSpan.FromMilliseconds(500)).Ticks;
            _workingSet = workingSet ?? (static () => Environment.WorkingSet);
            _gcPauseTicks = gcPauseTicks ?? (static () => GC.GetTotalPauseDuration().Ticks);
            _heartbeat = heartbeat;
            _useThreadPoolProbe = useThreadPoolProbe;
        }

        public HealthBreach Breach => (HealthBreach)Volatile.Read(ref _breach);
        public bool Tripped => Volatile.Read(ref _breach) != 0;
        public bool ArmingBlocked => Tripped;
        public long Scans => Interlocked.Read(ref _scans);
        public long LastWorkingSetBytes => Interlocked.Read(ref _lastWs);
        public long LastStallMicros => Interlocked.Read(ref _lastStallMicros);
        public long LastGcPauseDeltaMicros => Interlocked.Read(ref _lastPauseDeltaMicros);
        public int LastCpuPermille => Volatile.Read(ref _lastCpuPermille);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>1회 스캔(δ=0). 반환 = 이번 스캔에서 새로 감지된 위반.</summary>
        public HealthBreach Scan()
        {
            Interlocked.Increment(ref _scans);
            long now = _now();
            HealthBreach found = HealthBreach.None;

            long ws = _workingSet();
            Interlocked.Exchange(ref _lastWs, ws);
            if (ws > _maxWs) found |= HealthBreach.WorkingSet;
            if (TrendBreached(now, ws)) found |= HealthBreach.MemoryTrend;

            if (_useThreadPoolProbe)
            {
                long stallTicks;
                if (Volatile.Read(ref _probe.Pending) == 1) stallTicks = now - Volatile.Read(ref _probe.EnqueuedTs);   // 아직 미실행 — 대기 중 시간
                else
                {
                    stallTicks = Volatile.Read(ref _probe.LatencyTs);
                    Volatile.Write(ref _probe.EnqueuedTs, _now());
                    Volatile.Write(ref _probe.Pending, 1);
                    _enqueue(_probe);
                }
                Interlocked.Exchange(ref _lastStallMicros, stallTicks * 1_000_000 / Stopwatch.Frequency);
                if (stallTicks > _maxStallTicks) found |= HealthBreach.ThreadPoolStall;
            }

            long pause = _gcPauseTicks();
            if (_lastPause >= 0)
            {
                long delta = pause - _lastPause;
                Interlocked.Exchange(ref _lastPauseDeltaMicros, delta / 10);
                if (delta > _maxPausePerScanTicks) found |= HealthBreach.GcPause;
            }
            _lastPause = pause;

            SampleCpu(now);
            if (_firstScanTs == long.MinValue) _firstScanTs = now;
            if (now - _firstScanTs < _warmupTicks) found = HealthBreach.None;      // 유예 중 — 관측값만 갱신
            if (found != HealthBreach.None && (Interlocked.Or(ref _breach, (int)found) & (int)found) != (int)found)
                Volatile.Read(ref _link)?.Resync();                                  // 새 사유 → 비트 Raise(0ms)
            _heartbeat?.Beat(now, Stopwatch.Frequency, ws, Volatile.Read(ref _lastCpuPermille), Tripped);
            return found;
        }

        // [방향 B] 표본 적재 + 최소제곱 추세(δ=0 · 스캔 스레드). 반환 = 예측 누수(ETA ≤ 지평).
        private bool TrendBreached(long now, long ws)
        {
            if (_lastTrendTs != long.MinValue && now - _lastTrendTs < _trendSampleTicks) return false;
            _lastTrendTs = now;
            _trendTs[_trendHead] = now; _trendWs[_trendHead] = ws;
            _trendHead = (_trendHead + 1) % TrendCapacity;
            if (_trendCount < TrendCapacity) Volatile.Write(ref _trendCount, _trendCount + 1);
            int n = _trendCount;
            if (n < 8) return false;
            int oldest = (_trendHead - n + TrendCapacity) % TrendCapacity;
            long t0 = _trendTs[oldest];
            if (now - t0 < _trendMinWindowTicks) return false;
            double f = Stopwatch.Frequency, sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
            for (int k = 0; k < n; k++)
            {
                int i = (oldest + k) % TrendCapacity;
                double x = (_trendTs[i] - t0) / f, y = _trendWs[i];
                sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
            }
            double dx = n * sxx - sx * sx, dy = n * syy - sy * sy;
            if (dx <= 0) return false;
            double slope = (n * sxy - sx * sy) / dx;
            double r2 = dy <= 0 ? 0 : (n * sxy - sx * sy) * (n * sxy - sx * sy) / (dx * dy);
            double eta = slope > 0 ? Math.Max(0, (_maxWs - ws) / slope) : double.PositiveInfinity;
            Volatile.Write(ref _lastSlopePerSec, slope); Volatile.Write(ref _lastR2, r2); Volatile.Write(ref _lastEtaSec, eta);
            return slope > 0 && r2 >= _trendMinR2 && eta * f <= _trendHorizonTicks;
        }

        public void Start(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            lock (_lifecycle)                                                        // 시작·처분 상호 배제(미시작 스레드 처분 경합 교훈)
            {
                if (_disposed == 1) throw new ObjectDisposedException(nameof(HealthMonitoringAgent));
                if (_thread is not null) return;
                int ms = (int)Math.Max(1, interval.TotalMilliseconds);
                var t = new Thread(() =>
                {
                    try { while (!_stop.Wait(ms)) { try { Scan(); } catch (Exception) { } } }
                    catch (ObjectDisposedException) { }
                }) { IsBackground = true, Name = "MS2E.HealthAgent", Priority = ThreadPriority.AboveNormal };
                t.Start();
                _thread = t;
            }
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _breach, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        public void Dispose()
        {
            Thread? t;
            lock (_lifecycle)
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
                _stop.Set();
                t = _thread;
            }
            if (t is not null && t != Thread.CurrentThread) t.Join();
            _stop.Dispose();
        }

        // ── CPU(관측 전용) ──

        [LibraryImport("kernel32.dll")]
        private static partial IntPtr GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

        private void SampleCpu(long nowTs)
        {
            if (!OperatingSystem.IsWindows() || !GetProcessTimes(GetCurrentProcess(), out _, out _, out long k, out long u)) return;
            long cpu = k + u;                                                        // 100ns 단위
            if (_lastCpuTicks >= 0)
            {
                long wall100ns = (nowTs - _lastWallTs) * 10_000_000 / Stopwatch.Frequency;
                if (wall100ns > 0)
                    Volatile.Write(ref _lastCpuPermille, (int)Math.Clamp((cpu - _lastCpuTicks) * 1000 / (wall100ns * Environment.ProcessorCount), 0, 1000));
            }
            _lastCpuTicks = cpu; _lastWallTs = nowTs;
        }
    }
}
