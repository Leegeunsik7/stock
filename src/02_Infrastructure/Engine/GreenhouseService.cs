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

namespace MS2E.Infrastructure
{
    public sealed record GreenhouseReport(
        bool        Running,
        long        Cycles,
        long        Mutations,
        long        Errors,
        double      BestFitness,
        string?     BestSpecId,
        MacroRegime Regime,
        DateTime?   LastCycleUtc);

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
        /// <summary>S20 #2 — 거시 매크로 피드(주입식). 결속 시 레짐 스트레스로 거부 게이트를 실시간 수축.</summary>
        public Func<MacroSnapshot>? MacroFeed { get; init; }
        public MacroRegimeBounds   MacroBounds { get; init; } = MacroRegimeBounds.Default;
        /// <summary>테스트용 — 배양 사이클 시작 지점에 결함을 주입해 격리 방화벽을 검증한다.</summary>
        public Action? CycleFaultHook { get; init; }

        /// <summary>S24-b — 새 챔피언 제출 직후 호출(플러시 펌프 RequestFlush 결속용). null이면 무동작.</summary>
        public Action? OnChampionPromoted { get; init; }

        // ── S23-b 라이브 피드 게이트 (옵트인 · 기본 OFF = 현행 동작) ────────────────
        /// <summary>규칙1 — true면 데이터 세대 불변 시 재진화 숏서킷(할당·CPU 0).</summary>
        public bool          SkipUnchanged   { get; init; }
        /// <summary>규칙2 — 마지막 Prime 이후 이 한도 초과 시 배양 fail-closed. null이면 게이팅 없음.</summary>
        public TimeSpan?     StalenessWindow { get; init; }
        /// <summary>테스트용 주입 클록. null이면 UtcNow.</summary>
        public Func<DateTime>? Clock         { get; init; }

        private DateTime Now() => (Clock ?? (static () => DateTime.UtcNow))();

        // ── S26-b C-1 두-갈래 신선도 펜스 (옵트인 · 기본 OFF = 현행 동작) ─────────────────
        //   시간 도메인 3분할: Market(캔들 마켓시각) / Mono(단조 클록) / Wall(시스템 시계).
        /// <summary>C-1 ① 데이터 시장-나이 한도(W_data) — now − 최신캔들 마켓시각 초과 시 fail-closed. null이면 OFF.</summary>
        public TimeSpan? DataFreshnessWindow { get; init; }
        /// <summary>C-1 ② 인제스트 liveness 한도(W_live) — 단조 클록 측정(NTP 스텝/스큐 면역). null이면 OFF.</summary>
        public TimeSpan? LivenessWindow      { get; init; }
        /// <summary>C-1 미래-데이터 허용오차 — 마켓시각이 now+이 값 초과(미래)면 시간 도메인 스큐(KST↔UTC)·시계역행으로 판정·fail-closed.</summary>
        public TimeSpan  FutureSkewTolerance { get; init; } = TimeSpan.FromMinutes(2);
        /// <summary>테스트용 단조 클록 주입(Stopwatch.GetTimestamp 단위). null이면 Stopwatch.GetTimestamp.</summary>
        public Func<long>? MonoClock         { get; init; }
        /// <summary>MonoClock 의 초당 틱(주파수). 0이면 Stopwatch.Frequency.</summary>
        public long      MonoFrequency       { get; init; }

        private long MonoNow()  => MonoClock?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp();
        private long MonoFreq   => MonoFrequency > 0 ? MonoFrequency : System.Diagnostics.Stopwatch.Frequency;

        // ── 불변 입력 스냅샷 (메인과 공유 0) ──────────────────────────────────
        private StrategySpec[] _seeds   = Array.Empty<StrategySpec>();
        private Candle[]       _candles = Array.Empty<Candle>();
        private Market         _market;

        // ── 원자 상태 ─────────────────────────────────────────────────────────
        private Thread?       _thread;
        private volatile bool _running;
        private long          _cycles, _mutations, _errors;
        private long          _bestBits = BitConverter.DoubleToInt64Bits(double.NegativeInfinity);
        private volatile int  _regimeInt = (int)MacroRegime.Calm;   // 최근 사이클의 매크로 레짐
        private volatile string? _bestSpecId;
        private long          _lastCycleTicks;
        private ResourceLease? _lease;

        // S23-b — 피드 신선도·세대 추적 (volatile long 불법 → Volatile.Read/Write(ref long))
        private long _fedGen, _evolvedGen, _lastFreshTicks, _stalePauses;
        // S26-b C-1 — 시간 도메인 분리 신선도 펜싱 (단일-long 원자 — Volatile.Read/Write, 무할당)
        private long _lastMarketTicks;   // 최신 캔들 마켓 시각(As-Of 근거·데이터 나이 — 인제스트 wall-clock 아님)
        private long _lastIngestMono;    // 인제스트 단조 순간(Stopwatch 단위, liveness·wall-clock 스큐 면역)

        /// <summary>규칙2 — 데이터 staleness 로 배양이 fail-closed 된 누적 횟수(관제탑 진단).</summary>
        public long StalePauses => Interlocked.Read(ref _stalePauses);

        /// <summary>규칙2 — 현재 캔들 피드가 staleness 한도를 넘었는가(관제탑 트립와이어).</summary>
        public bool FeedStale
        {
            get
            {
                if (StalenessWindow is not TimeSpan w) return false;
                long f = Volatile.Read(ref _lastFreshTicks);
                return f == 0 || Now() - new DateTime(f, DateTimeKind.Utc) > w;
            }
        }

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
            _seeds   = seeds is null ? Array.Empty<StrategySpec>() : ToArray(seeds);   // StrategySpec=불변 record, 참조 공유 무해
            // ★S25-a 적대적 리뷰 수정 — Candle 은 가변 class 라 얕은 복사(ToArray)는 참조만 공유한다. 라이브 인그레스가
            //   재사용 버퍼 인스턴스를 in-place 덮어쓰므로, 얕으면 온실 _candles 가 그 버퍼와 별칭이 되어 배양 중
            //   torn 필드 읽기(결정 오염)가 발생한다. '값 복제(deep copy)'로 별칭을 끊어 온실 스레드를 격리한다.
            //   Volatile.Write 로 완성된 배열을 안전 발행(x64 store 순서 + 명시 release).
            Volatile.Write(ref _candles, DeepCopyCandles(candles));
            _market  = market;
            // S23-b — 새 데이터 윈도우: 신선도 갱신 + 세대 증가(isDirty 신호)
            Volatile.Write(ref _lastFreshTicks, Now().Ticks);
            // S26-b C-1 — 데이터의 '진짜 나이'(마켓시각) + 단조 인제스트(liveness). 쓰기 순서 candles→fresh→market→mono
            //   를 RunCycleOnce 가 역순 독출해 release-acquire fail-closed 편향을 확보한다(과대신선 봉인).
            //   마켓시각은 인제스트 wall-clock 이 아니라 캔들 고유의 시장 생성 시각 → 네트워크 지연 Δt_network 자동 반영.
            Volatile.Write(ref _lastMarketTicks, candles.Count > 0 ? candles[candles.Count - 1].Date.Ticks : 0L);
            Volatile.Write(ref _lastIngestMono, MonoNow());
            Interlocked.Increment(ref _fedGen);
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

            // S23-b 규칙2 — 캔들 staleness fail-closed: 척추(데이터)가 낡으면 배양 즉시 정지.
            // 매크로(필터)와 달리 캔들(피사체)은 강등 불가 — 사라진 시장에 적합 금지.
            if (StalenessWindow is TimeSpan sw)
            {
                long fresh = Volatile.Read(ref _lastFreshTicks);
                if (fresh == 0 || Now() - new DateTime(fresh, DateTimeKind.Utc) > sw)
                {
                    Interlocked.Increment(ref _stalePauses);
                    return;
                }
            }

            // S23-b 규칙1 — isDirty 숏서킷: 데이터 세대 불변 시 재진화 회피(모집단·백테스트 할당 0).
            if (SkipUnchanged)
            {
                long fed = Interlocked.Read(ref _fedGen);
                if (fed == Interlocked.Read(ref _evolvedGen)) return;
                Interlocked.Exchange(ref _evolvedGen, fed);
            }

            var seeds = _seeds;
            // ★ S25-c Lock-Free Catch-Up + As-Of 원자 박제 — 글로벌 _candles 를 사이클 진입점에서 '단 한 번'
            //   Volatile.Read 로 로컬 snap 에 가둔다. (a) acquire 배리어가 Prime 의 Volatile.Write(release)와 짝지어
            //   ARM64 등 약메모리에서도 배열 참조+심층복제된 원소를 일관 가시화한다. (b) 사이클 전체가 이 단일
            //   스냅샷만 참조하므로 인그레스가 도중에 _candles 를 스왑해도 시간 왜곡(과거/미래 윈도우 혼입)이 없다.
            //   이후 _candles 직간접 재참조 전면 금지 — Evolve/Backtest 는 snap 에서 파생된 로컬만 받는다.
            //
            //   ★적대적 리뷰(major) 수정 — 챔피언에 박제할 As-Of(snapAsOf)를 snap '보다 먼저' 독출한다.
            //   Prime 은 _candles(release) → _lastFreshTicks(release) 순으로 쓰므로, 독출을 역순
            //   (_lastFreshTicks acquire → _candles acquire)으로 하면 release-acquire 에 의해 "신선 시각 +
            //   오래된 데이터"(과대신선·fail-OPEN)가 원천 불가능해진다: snapAsOf 가 새 시각을 관측하면 그 acquire 가
            //   직전 _candles 쓰기를 happens-before 로 끌어와 snap 도 반드시 새 데이터다. 최악은 "오래된 시각 +
            //   새 데이터"(과소신선·fail-CLOSED) — 안전 측 편향. 과거엔 As-Of 를 사이클 '끝'에서 재독출해
            //   snap(과거 윈도우) 으로 진화한 챔피언이 도중 Prime 의 더 신선한 시각을 달고 나가 신선도 게이트를
            //   부당 통과(fail-open)했다 — catch-up 이 봉인한 시간왜곡을 As-Of 가 도로 은폐하던 자가당착.
            //   ★S26-b C-1 — 역순 독출 체인. 쓰기 순서(Prime): _candles → _lastFreshTicks → _lastMarketTicks
            //   → _lastIngestMono. 독출은 그 역순(마지막 쓰인 것부터)으로 _candles 를 가장 나중에 읽는다.
            //   release-acquire 에 의해 어떤 타임스탬프든 '새 값'을 관측하면 그보다 먼저 쓰인 _candles 도 반드시
            //   새 값이다 → "신선시각+오래된데이터"(과대신선·fail-open) 원천 불가, 최악은 과소신선(fail-closed).
            long snapAsOf = Volatile.Read(ref _lastFreshTicks);    // 레거시 As-Of(StalenessWindow 경로)
            long ingMono  = Volatile.Read(ref _lastIngestMono);    // C-1 ② liveness(단조)
            long market   = Volatile.Read(ref _lastMarketTicks);   // C-1 ① As-Of/데이터 시장-나이
            var  snap     = Volatile.Read(ref _candles);           // 단일 스냅샷(마지막 — release-acquire 앵커)
            if (seeds.Length == 0 || snap.Length < 60) return;

            // ── S26-b C-1 두-갈래 신선도 펜스(옵트인) ──────────────────────────────────────
            //   ① 데이터 시장-나이: 마켓시각 기준(인제스트 wall-clock 아님) → 네트워크 지연 Δt_network 자동 반영.
            //      음수 나이(미래 데이터)는 시간 도메인 스큐(KST↔UTC)·시계 역행 → 도메인 정준화 가드로 fail-closed.
            if (DataFreshnessWindow is TimeSpan wData)
            {
                long marketAge = Now().Ticks - market;
                if (market == 0L || marketAge > wData.Ticks || marketAge < -FutureSkewTolerance.Ticks)
                {
                    Interlocked.Increment(ref _stalePauses);
                    return;
                }
            }
            //   ② 인제스트 liveness: 단조 클록 측정 → NTP wall-clock 스텝에는 면역(전진 가정 시).
            //   ★적대적 리뷰(major) 수정 — '단조'는 전진 가정일 뿐, 역행은 NTP 가 아닌 하드웨어/주입 경로로 들어온다:
            //   멀티코어 TSC/QPC 미동기·VM live-migration·주입된 비단조 클록. 그때 monoΔ<0 이면 과거의
            //   `Δ>임계값`은 false → 미발화 → stale 진행(fail-OPEN). "모호함 앞의 Fail-Closed"상 음수 Δ = 클록 상태
            //   모호 → 반드시 트립해야 한다. 임계값도 정수(long) 도메인으로 끌어와 double 정밀 손실/fail-open 편향 제거.
            if (LivenessWindow is TimeSpan wLive)
            {
                long monoDelta     = MonoNow() - ingMono;
                long thresholdMono = (long)(wLive.TotalSeconds * MonoFreq);   // 절사 → 작은 임계값 = fail-closed 편향
                if (ingMono == 0L || monoDelta < 0L || monoDelta > thresholdMono)
                {
                    Interlocked.Increment(ref _stalePauses);
                    return;
                }
            }

            var candles = new List<Candle>(snap);   // 단일 스냅샷에서만 파생(글로벌 재독출 0)
            var mkt     = _market;                  // ★적대적 리뷰(minor) — _market 도 cycle-start 1회 포획
                                                    //   (Evolve/Backtest 가 글로벌 _market 재독출 시 사이클 도중 Prime 의
                                                    //   중간값 혼입 가능 → 단일 스냅샷 격리 불변식 일관성 유지).
            var result  = CartridgeGenome.Evolve(
                seeds, candles, mkt, Generations, PopulationSize, _attestation);
            if (!result.IsValid || result.Ranked.Count == 0) return;

            double       bestF    = double.NegativeInfinity;
            StrategySpec? bestSpec = null;

            // S19 #5 — 실시간 마찰: Sentinel 결속 시 실거래 LastZ, 아니면 백테스트 프록시
            double meanAbsZ = Sentinel is not null ? Math.Abs(Sentinel.LastZ) : ProxyMeanAbsZ;

            // S20 #2 — 거시 매크로 레짐: 피드 결속 시 스트레스로 거부 게이트 허용오차 수축
            var fb = _fitBounds;
            if (MacroFeed is not null)
            {
                try
                {
                    var    macro  = MacroFeed();
                    double stress = MacroRegimeFusion.StressScore(macro, MacroBounds);
                    _regimeInt = (int)MacroRegimeFusion.Classify(stress, MacroBounds);
                    fb = MacroRegimeFusion.Modulate(_fitBounds, stress, MacroBounds);
                }
                catch { /* 매크로 피드 실패 → 비변조(평온) 폴백, 그레이스풀 강등 */ }
            }

            foreach (var entry in result.Ranked)
            {
                var rec = MutationBoundsMatrix.Reconcile(entry.Spec, _mutBounds);
                if (!rec.Ok) continue;
                Interlocked.Increment(ref _mutations);

                var bt = StrategyCompiler.Backtest(rec.Repaired, candles, mkt);
                double f = FitnessEvaluator.BaseFitness(bt, meanAbsZ, ProxyExposure, fb);
                if (f > bestF) { bestF = f; bestSpec = rec.Repaired; }
            }

            Interlocked.Exchange(ref _lastCycleTicks, DateTime.UtcNow.Ticks);

            // 최고 적응도를 경신한 유효 개체만 승급 게이트에 제출
            if (bestSpec is not null && bestF > CurrentBest())
            {
                Interlocked.Exchange(ref _bestBits, BitConverter.DoubleToInt64Bits(bestF));
                _bestSpecId = bestSpec.Id;
                // S23-b 규칙3 — 제안에 데이터 As-Of 박제 → 승인·활성 시점 신선도 검문 근거.
                // ★S25-c — cycle-start 에서 snap 과 역순으로 가둔 값 사용(사이클 끝 재독출 금지): 챔피언이 실제
                //   진화한 윈도우의 신선도와 일관 → 잔여 시간왜곡 시 신선도 게이트가 fail-closed.
                // ★S26-b C-1 — 펜스 옵트인 시 As-Of=마켓시각(market): now−As-Of 가 네트워크 지연 Δt_network 까지
                //   포함한 데이터의 진짜 나이가 된다. 미옵트인이면 레거시 인제스트 시각(snapAsOf, 후방호환).
                long asOf = DataFreshnessWindow is not null ? market : snapAsOf;
                _gateway.Submit(bestSpec, bestF, asOf);   // Candidate ➔ PendingApproval
                OnChampionPromoted?.Invoke();   // S24-b — 핫패스 dirty 트리거(펌프 RequestFlush, 무할당 Interlocked)
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
                Regime:       (MacroRegime)_regimeInt,
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

        // S25-a — 캔들 깊은 복사: 호출자(라이브 인그레스 재사용 버퍼)와의 별칭을 끊어 배양 스레드를 격리한다.
        // Candle 은 가변 class 라 값 복제만이 동시 in-place 덮어쓰기로부터 _candles 를 보호한다(얕은 ToArray 불가).
        private static Candle[] DeepCopyCandles(IReadOnlyList<Candle>? src)
        {
            if (src is null || src.Count == 0) return Array.Empty<Candle>();
            var arr = new Candle[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var c = src[i];
                arr[i] = c is null
                    ? new Candle()
                    : new Candle { Date = c.Date, Open = c.Open, High = c.High, Low = c.Low, Close = c.Close, Volume = c.Volume };
            }
            return arr;
        }
    }
}
