// ============================================================================
// GreenhouseService — S19 #4: 격리 배양 스레드 오케스트레이터
//
//  메인 초고속 틱 파이프라인(S13)에 단 1ms의 간섭도 주지 않는 평행우주 온실.
//  전용 백그라운드 스레드(ThreadPriority.BelowNormal, IsBackground)에서 배양 루프를 돌린다:
//    ① CartridgeGenome.Evolve  — 결정론 교차·변이로 후보 게놈 생성
//    ② MutationBoundsMatrix.Reconcile — 구조 수리·정준화(유전자 방화벽 B층)
//    ③ StrategyCompiler.Backtest      — 프록시 시뮬레이션 메트릭
//    ④ FitnessEvaluator.BaseFitness   — 다차원 적응도 평점
//    ⑤ 최고 경신 시 PromotionGateway.Submit → Candidate ➔ PendingApproval
//
//  ── 격리 방화벽 ────────────────────────────────────────────────────────────
//  배양 사이클이 예외를 던지거나 멈춰도 메인 GatingMode·호스트 프로세스는 무사하다.
//  · 루프 본문 try-catch 로 사이클 예외 흡수(Errors 카운트만 증가, 루프 지속)
//  · IsBackground=true → 스레드가 프로세스 수명을 붙잡지 않음
//  · EmergencyFreeze 시 배양 일시정지(fail-closed) — Evolve 에도 attestation 전달(이중 방어)
//
//  ── 정직 경계 (§2.3 · §7) ──────────────────────────────────────────────────
//  섀도우/라이브 집행이 아직 없으므로 마찰(Z̄)·노출(exposure)은 측정 불가 →
//  ProxyMeanAbsZ=0(마찰 게이트 중립)·ProxyExposure=목표(O 중립)를 명시적 프록시로 사용한다.
//  실 측정값은 S19 #5(섀도우 집행) 결속 후 주입된다.
//
//  외부 의존성 0 · 결정론 코어(Evolve) · 메인 핫패스와 가변 상태 공유 0(불변 스냅샷 복사).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace StockManager.Common
{
    public sealed record GreenhouseReport(
        bool      Running,
        long      Cycles,
        long      Mutations,
        long      Errors,
        double    BestFitness,
        string?   BestSpecId,
        DateTime? LastCycleUtc);

    public sealed class GreenhouseService : IDisposable
    {
        private readonly PromotionGateway         _gateway;
        private readonly OrderAttestationService? _attestation;
        private readonly MemoryLeakGuard?         _memGuard;
        private readonly MutationBounds           _mutBounds;
        private readonly FitnessBounds            _fitBounds;

        // ── 설정 ──────────────────────────────────────────────────────────────
        public int    CycleIntervalMs { get; init; } = 1000;   // 사이클 간 휴면(백프레셔)
        public int    Generations     { get; init; } = 4;
        public int    PopulationSize  { get; init; } = 12;
        public double ProxyMeanAbsZ   { get; init; } = 0.0;     // backtest-proxy(마찰 게이트 중립, Sentinel 미결속 시)
        public double ProxyExposure   { get; init; } = 0.5;     // backtest-proxy(노출 O 중립=목표)
        /// <summary>S19 #5 — 실시간 마찰 피드. 결속 시 ProxyMeanAbsZ 대신 실거래 LastZ 를 적응도에 반영.</summary>
        public SlippageSentinel? Sentinel { get; init; }
        /// <summary>테스트용 — 배양 사이클 시작 지점에 결함을 주입해 격리 방화벽을 검증한다.</summary>
        public Action? CycleFaultHook { get; init; }

        // ── 불변 입력 스냅샷 (메인과 공유 0) ──────────────────────────────────
        private StrategySpec[] _seeds   = Array.Empty<StrategySpec>();
        private Candle[]       _candles = Array.Empty<Candle>();
        private Market         _market;

        // ── 원자 상태 ─────────────────────────────────────────────────────────
        private Thread?       _thread;
        private volatile bool _running;
        private long          _cycles, _mutations, _errors;
        private long          _bestBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);
        private volatile string? _bestSpecId;
        private long          _lastCycleTicks;
        private ResourceLease? _lease;

        public GreenhouseService(
            PromotionGateway         gateway,
            OrderAttestationService? attestation = null,
            MemoryLeakGuard?         memGuard    = null,
            MutationBounds?          mutBounds   = null,
            FitnessBounds?           fitBounds   = null)
        {
            _gateway     = gateway ?? throw new ArgumentNullException(nameof(gateway));
            _attestation = attestation;
            _memGuard    = memGuard;
            _mutBounds   = mutBounds ?? MutationBounds.Default;
            _fitBounds   = fitBounds ?? FitnessBounds.Default;
        }

        public bool Running => _running;

        // ── 수명 주기 ─────────────────────────────────────────────────────────

        /// <summary>
        /// 씨앗·캔들의 불변 스냅샷을 복사해 배양 입력을 적재한다(스레드 미가동).
        /// RunCycleOnce 를 결정론적으로 직접 구동하기 전 입력을 준비할 때 사용.
        /// </summary>
        public void Prime(IReadOnlyList<StrategySpec> seeds, IReadOnlyList<Candle> candles, Market market)
        {
            _seeds   = seeds   is null ? Array.Empty<StrategySpec>() : ToArray(seeds);
            _candles = candles is null ? Array.Empty<Candle>()       : ToArray(candles);
            _market  = market;
        }

        /// <summary>씨앗·캔들의 불변 스냅샷을 적재하고 격리 배양 스레드를 가동한다.</summary>
        public void Start(IReadOnlyList<StrategySpec> seeds, IReadOnlyList<Candle> candles, Market market)
        {
            if (_running) return;
            Prime(seeds, candles, market);

            _lease   = _memGuard?.Acquire("greenhouse-thread");
            _running = true;
            _thread  = new Thread(Loop)
            {
                IsBackground = true,                 // 프로세스 수명을 붙잡지 않음
                Priority     = ThreadPriority.BelowNormal,
                Name         = "ShadowGreenhouse",
            };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            var t = _thread;
            if (t is not null && t.IsAlive) { try { t.Join(2000); } catch { } }
            _thread = null;
            _lease?.Dispose();   // 추적 리소스 반납 (MemoryLeakGuard)
            _lease = null;
        }

        public void Dispose() => Stop();

        // ── 배양 루프 (격리 방화벽) ────────────────────────────────────────────

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    RunCycleOnce();
                }
                catch
                {
                    // 격리 방화벽 — 사이클 예외는 메인에 전파하지 않고 카운트만 증가
                    Interlocked.Increment(ref _errors);
                }
                // 백프레셔 — 메인 우선, CPU 양보
                for (int slept = 0; slept < CycleIntervalMs && _running; slept += 25)
                    Thread.Sleep(Math.Min(25, CycleIntervalMs - slept));
            }
        }

        /// <summary>
        /// 단일 배양 사이클 — 결정론적으로 직접 호출 가능(테스트). 예외는 호출자에게 전파된다
        /// (Loop 가 격리 방화벽으로 감싼다).
        /// </summary>
        public void RunCycleOnce()
        {
            Interlocked.Increment(ref _cycles);
            CycleFaultHook?.Invoke();   // 테스트 결함 주입 지점

            // fail-closed — 비상 동결 중에는 배양·제출 모두 정지
            if (_attestation is not null && _attestation.Mode == GatingMode.EmergencyFreeze) return;

            var seeds = _seeds;
            if (seeds.Length == 0 || _candles.Length < 60) return;

            var candles = new List<Candle>(_candles);   // 불변 스냅샷의 작업 복사본
            var result  = CartridgeGenome.Evolve(
                seeds, candles, _market, Generations, PopulationSize, _attestation);
            if (!result.IsValid || result.Ranked.Count == 0) return;

            double       bestF    = double.NegativeInfinity;
            StrategySpec? bestSpec = null;

            // S19 #5 — 실시간 마찰: Sentinel 결속 시 실거래 LastZ, 아니면 백테스트 프록시
            double meanAbsZ = Sentinel is not null ? Math.Abs(Sentinel.LastZ) : ProxyMeanAbsZ;

            foreach (var entry in result.Ranked)
            {
                var rec = MutationBoundsMatrix.Reconcile(entry.Spec, _mutBounds);
                if (!rec.Ok) continue;
                Interlocked.Increment(ref _mutations);

                var bt = StrategyCompiler.Backtest(rec.Repaired, candles, _market);
                double f = FitnessEvaluator.BaseFitness(bt, meanAbsZ, ProxyExposure, _fitBounds);
                if (f > bestF) { bestF = f; bestSpec = rec.Repaired; }
            }

            Interlocked.Exchange(ref _lastCycleTicks, DateTime.UtcNow.Ticks);

            // 최고 적응도를 경신한 유효 개체만 승급 게이트에 제출
            if (bestSpec is not null && bestF > CurrentBest())
            {
                Interlocked.Exchange(ref _bestBits, BitConverter.DoubleToInt64Bits(bestF));
                _bestSpecId = bestSpec.Id;
                _gateway.Submit(bestSpec, bestF);   // Candidate ➔ PendingApproval
            }
        }

        // ── 계측 ──────────────────────────────────────────────────────────────

        public GreenhouseReport Probe()
        {
            double best = CurrentBest();
            long   ticks = Interlocked.Read(ref _lastCycleTicks);
            return new GreenhouseReport(
                Running:      _running,
                Cycles:       Interlocked.Read(ref _cycles),
                Mutations:    Interlocked.Read(ref _mutations),
                Errors:       Interlocked.Read(ref _errors),
                BestFitness:  double.IsNegativeInfinity(best) ? 0.0 : best,
                BestSpecId:   _bestSpecId,
                LastCycleUtc: ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc));
        }

        private double CurrentBest()
            => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _bestBits));

        private static T[] ToArray<T>(IReadOnlyList<T> src)
        {
            var arr = new T[src.Count];
            for (int i = 0; i < src.Count; i++) arr[i] = src[i];
            return arr;
        }
    }
}
