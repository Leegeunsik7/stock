// ============================================================================
// DeterministicChaosGrid — v4.91.0: 결정론적 카오스 테스팅 그리드 커널
//
//  가상 시간 패브릭(VirtualClock+VirtualScheduler+DeterministicPrng) 위에서 시드 기반 재현 가능한 복합 장애
//  스트림을 투하하고, 카오스 폭격 후 시스템의 fail-closed/멱등/원장 정합을 검증한다.
//
//  ── 장애 3벡터(전부 인메모리·실 패킷 0바이트) ─────────────────────────────────
//   · KiwoomProxyDown : x86 프록시 IPC 링크 단절(VirtualProxyLink) → 가상 하트비트 타임아웃 → ForceFreeze.
//   · TossAuth401     : 토스 WSS/토큰 인증 실패 수렴 → ForceFreeze.
//   · FxSpike/FxStale : QFxCircuitBreaker(실 latch) 3σ 급등/1s 스테일 → ForceFreeze(멱등 1회).
//   세 벡터 모두 OrderAttestationService.ForceFreeze 단일 소스로 수렴(관측=Mode==EmergencyFreeze).
//
//  ── 고도화(Think Further) ───────────────────────────────────────────────────
//   1) 가상 비동기 지연 큐: 네트워크/IPC 레이턴시를 VirtualScheduler 예약 이벤트로 모사(실 대기 0·δ=0).
//   2) PBT+Shrinking: 수천 시드로 결정론·무행·원장 정합을 자동 수색(기본 InMemory 싱크는 fail-closed). fail-open 경계는
//      sinkFactory 로 fail-open '가능' 싱크를 주입할 때 수색되며, 실패 시 ChaosShrinker 로 최소 재현 시퀀스 역추적.
//   3) 격리 스텁 가상화: VirtualProxyLink 로 x86↔AnyCPU IPC 단절을 메모리 훼손 0(관리형 volatile)으로 모사.
//
//  ── 불변식 ──────────────────────────────────────────────────────────────────
//   liveArmed:false : 실 소켓/파이프/프로세스 미생성 — 카오스는 인메모리 상태 전이에만 작용(외부 API 0바이트).
//   fail-closed     : 장애 투하 → ForceFreeze 단일 소스. 동결 중 신규 체결 수용 금지(수용 시 FailOpen 적발).
//   δ=0             : naive 명목은 QFixed raw 정수(QMath.Notional·Int128). 벽시계/실난수 미사용(시드 PRNG 만).
//   방어            : 시간 역전(VirtualClock)·무한루프(VirtualScheduler maxSteps) 물리 차단(테스트 hang 원천 봉쇄).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>카오스 장애 벡터(비트 플래그 — 복합 투하 누적).</summary>
    [Flags]
    public enum ChaosFault : long
    {
        None           = 0,
        KiwoomProxyDown = 1,
        TossAuth401     = 2,
        FxSpike         = 4,
        FxStale         = 8,
    }

    public enum ChaosEventKind : byte { Fill = 0, Fault = 1 }

    /// <summary>결정론 시나리오의 단일 이벤트(체결 or 장애). 시드로 생성·shrinking 대상.</summary>
    public readonly record struct ChaosEventRecord(
        long DueTicks, ChaosEventKind Kind, ChaosFault Fault,
        long SymbolCode, long QtyRaw, long PriceRaw, byte Side);

    /// <summary>한 시드 카오스 실행 결과(불변 진단).</summary>
    public sealed record ChaosRunResult(
        int Seed, long VirtualTicksElapsed, int FaultsInjected, long ChaosFlags,
        bool Froze, bool WarmRestartIdempotent, bool FailOpen, int RejectedFills,
        long ExpectedNotionalRaw, long LedgerNotionalRaw, long NotionalDiffRaw,
        bool Passed, string Detail, IReadOnlyList<ChaosEventRecord> Events);

    /// <summary>속성 기반 테스트(PBT) 결과 + 최소 재현 시퀀스(shrinking).</summary>
    public sealed record PbtResult(
        int SeedsRun, int Passes, int Failures, int FirstFailingSeed,
        IReadOnlyList<ChaosEventRecord> MinimalFailingSequence, string Detail);

    /// <summary>원장 명목 싱크 — 카오스 커널이 체결을 영속화하고 최종 명목을 회수하는 추상(디퍼런셜 어설션용).</summary>
    public interface IChaosLedgerSink
    {
        /// <summary>체결 영속화 시도. 동결 등으로 거부되면 false(수용 안 됨).</summary>
        bool Persist(in DbTransactionPayload p);
        /// <summary>최종 원장 명목 합계(raw·10^8).</summary>
        long LedgerNotionalRaw();
    }

    /// <summary>
    /// ★x86 프록시 IPC 링크 격리 스텁(가상화) — 실 프로세스/파이프 없이 단절을 모사. 관리형 volatile 로 메모리 훼손 0.
    /// KiwoomBridgeClient.IsHeartbeatTimedOut 규약(연결 끊김 or 하트비트 타임아웃)을 순수·결정론으로 미러.
    /// </summary>
    public sealed class VirtualProxyLink
    {
        private volatile bool _connected = true;
        private long _lastHeartbeatTicks;

        public bool Connected => _connected;
        public long LastHeartbeatTicks => Interlocked.Read(ref _lastHeartbeatTicks);

        public void Heartbeat(long nowTicks) => Interlocked.Exchange(ref _lastHeartbeatTicks, nowTicks);
        public void Down() => _connected = false;
        public void Up(long nowTicks) { Interlocked.Exchange(ref _lastHeartbeatTicks, nowTicks); _connected = true; }

        /// <summary>링크 상실 판정(끊김 or 타임아웃 초과). 순수·결정론(주입 nowTicks 기준).</summary>
        public bool IsLost(long nowTicks, long timeoutTicks) => !_connected || (nowTicks - LastHeartbeatTicks) > timeoutTicks;
    }

    public sealed class ChaosGridConfig
    {
        public int    FillCount        { get; init; } = 40;
        public int    FaultCount       { get; init; } = 4;
        public long   HorizonTicks     { get; init; } = TimeSpan.TicksPerHour;   // 가상 1시간(1000× 압축 시 현실 3.6초)
        public int    MaxSteps         { get; init; } = 200_000;                 // 무한루프 방어 예산
        public long   StartTicks       { get; init; } = ChaosDefaults.Y2026Ticks;
        public long   SymbolCode       { get; init; } = 5930;                    // KSE 6자리(005930)
        public long   HeartbeatTimeoutTicks { get; init; } = 5_000 * TimeSpan.TicksPerMillisecond;
        public string ReleaseSignature { get; init; } = "CHAOS-WARM-RESTART";
    }

    internal static class ChaosDefaults
    {
        // 고정 2026 기준 tick(결정론 — 벽시계 아님). SafeUtc/DateTime 유효 범위 보장.
        internal static readonly long Y2026Ticks = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
    }

    /// <summary>델타-디버깅 shrinker — 실패를 재현하는 최소 이벤트 부분집합을 결정론적으로 역추적.</summary>
    public static class ChaosShrinker
    {
        public static IReadOnlyList<T> Shrink<T>(IReadOnlyList<T> failing, Func<IReadOnlyList<T>, bool> stillFails)
        {
            if (failing is null) throw new ArgumentNullException(nameof(failing));
            if (stillFails is null) throw new ArgumentNullException(nameof(stillFails));
            var cur = new List<T>(failing);
            bool changed = true;
            int guard = 0, maxGuard = failing.Count * failing.Count + 8;   // O(n²) 상한(행 방지)
            while (changed && guard++ < maxGuard)
            {
                changed = false;
                for (int i = 0; i < cur.Count; i++)
                {
                    var trial = new List<T>(cur);
                    trial.RemoveAt(i);
                    if (stillFails(trial)) { cur = trial; changed = true; break; }
                }
            }
            return cur;
        }
    }

    public sealed class DeterministicChaosGrid
    {
        // ── 라이브 관제 텔레메트리(마지막 실행/누적) ─────────────────────────────
        private long _lastVirtualTicks, _lastChaosFlags, _cumPbtPasses, _lastQueueDepth;
        public long VirtualTimeTicks   => Interlocked.Read(ref _lastVirtualTicks);
        public long ChaosFlags         => Interlocked.Read(ref _lastChaosFlags);
        public long PbtPasses          => Interlocked.Read(ref _cumPbtPasses);
        public long VirtualQueueDepth  => Interlocked.Read(ref _lastQueueDepth);
        public ChaosRunResult? LastResult { get; private set; }

        // ── 시나리오 이벤트 생성(시드 결정론) ────────────────────────────────────
        public IReadOnlyList<ChaosEventRecord> GenerateEvents(int seed, ChaosGridConfig cfg)
        {
            var prng = new DeterministicPrng(unchecked((ulong)seed * 0x9E3779B97F4A7C15UL + 0xD1B54A32D192ED03UL));
            var list = new List<ChaosEventRecord>(cfg.FillCount + cfg.FaultCount);
            for (int i = 0; i < cfg.FillCount; i++)
            {
                long due   = prng.NextLong(cfg.HorizonTicks);
                long qty   = 1 + prng.NextLong(100);              // 1..100주(정수 — 명목 정확)
                long price = 5_000 + prng.NextLong(195_000);      // 5000..199999원(정수)
                list.Add(new ChaosEventRecord(due, ChaosEventKind.Fill, ChaosFault.None,
                    cfg.SymbolCode, QQty.Of(qty).Raw, QPrice.Of(price).Raw, (byte)TransactionType.Buy));
            }
            for (int i = 0; i < cfg.FaultCount; i++)
            {
                long due   = prng.NextLong(cfg.HorizonTicks);
                var  fault = (ChaosFault)(1L << prng.NextInt(4));   // 1/2/4/8 중 하나
                list.Add(new ChaosEventRecord(due, ChaosEventKind.Fault, fault, 0, 0, 0, 0));
            }
            return list.OrderBy(e => e.DueTicks).ToList();          // due 안정 정렬(동일 시각=생성 순서)
        }

        // ── 시나리오 실행 ─────────────────────────────────────────────────────────
        /// <summary>시드로 이벤트 생성 후 실행(인메모리 싱크 기본). UI/PBT 고속 경로.</summary>
        public ChaosRunResult RunScenario(int seed, ChaosGridConfig cfg, Func<OrderAttestationService, IChaosLedgerSink>? sinkFactory = null)
            => RunWithEvents(seed, GenerateEvents(seed, cfg), cfg, sinkFactory);

        /// <summary>명시 이벤트 목록 실행(shrinking 재실행·디퍼런셜 원장 테스트에 사용).</summary>
        public ChaosRunResult RunWithEvents(int seed, IReadOnlyList<ChaosEventRecord> events, ChaosGridConfig cfg,
                                            Func<OrderAttestationService, IChaosLedgerSink>? sinkFactory = null)
        {
            var clock    = new VirtualClock(cfg.StartTicks);
            var sched    = new VirtualScheduler(clock);
            var att      = new OrderAttestationService();
            var breaker  = new QFxCircuitBreaker(clock.TickSource, att);
            var proxy    = new VirtualProxyLink();
            proxy.Heartbeat(clock.NowTicks);
            IChaosLedgerSink sink = sinkFactory?.Invoke(att) ?? new InMemorySink(att);

            long expected = 0, chaosFlags = 0, lastFillTicks = long.MinValue;
            int  faultsInjected = 0, rejectedFills = 0;
            bool froze = false, failOpen = false;

            foreach (var e in events)
            {
                var ev = e;   // 클로저 캡처 안전
                sched.Schedule(ev.DueTicks, () =>
                {
                    if (ev.Kind == ChaosEventKind.Fill)
                    {
                        long ticks = clock.NowTicks;
                        // ★시간 역전 맹점 방어: 체결 시각 단조. 후퇴하면 SlippageSentinel 과거/미래 오인 전에 fail-closed.
                        if (ticks < lastFillTicks) { att.ForceFreeze("시간 역전 감지 — 체결 시각 후퇴"); return; }
                        lastFillTicks = ticks;
                        var p = new DbTransactionPayload(ev.SymbolCode, ev.Side, ev.QtyRaw, ev.PriceRaw, 0, 0, ticks);
                        bool accepted = sink.Persist(in p);
                        // ★fail-open 적발: 동결 상태인데 체결이 수용됐다면 불변식 붕괴.
                        if (accepted && att.Mode == GatingMode.EmergencyFreeze) failOpen = true;
                        if (!accepted) rejectedFills++;   // 동결 게이트가 실제로 체결을 거부한 횟수(fail-closed 실행 증거)
                        if (accepted)
                            expected = checked(expected + QMath.Notional(ev.PriceRaw, ev.QtyRaw, roundUp: ev.Side == (byte)TransactionType.Buy));
                    }
                    else // Fault
                    {
                        faultsInjected++;
                        chaosFlags |= (long)ev.Fault;
                        InjectFault(ev.Fault, att, breaker, proxy, clock.NowTicks, cfg.HeartbeatTimeoutTicks);
                        if (att.Mode == GatingMode.EmergencyFreeze) froze = true;
                    }
                });
            }

            sched.DrainAll(cfg.MaxSteps);   // ★maxSteps 초과 시 ChaosGridHangException(행 차단)

            long ledger = sink.LedgerNotionalRaw();
            long diff   = Math.Abs(expected - ledger);
            (sink as IDisposable)?.Dispose();

            // ── 멱등 검증(웜스타트 코어=ForceFreeze/ManualRelease 멱등) ──────────
            bool idem = true;
            if (att.Mode == GatingMode.EmergencyFreeze)
            {
                var m0 = att.Mode;
                att.ForceFreeze("dup-idempotency-check");
                idem &= att.Mode == m0;                                   // 중복 동결 멱등(여전히 Frozen)
                att.ManualRelease(cfg.ReleaseSignature, "chaos-recover");
                idem &= att.Mode == GatingMode.Normal;                    // 해제
                att.ManualRelease(cfg.ReleaseSignature, "chaos-recover-again");
                idem &= att.Mode == GatingMode.Normal;                    // 중복 해제 멱등
            }

            bool anyFault = faultsInjected > 0;
            // Passed: fail-open 없음 ∧ (장애 있으면 반드시 동결됨) ∧ 멱등 ∧ 원장 정합(≤1raw).
            bool passed = !failOpen && (!anyFault || froze) && idem && diff <= 1L;
            string detail = passed
                ? $"통과 — 장애 {faultsInjected} 투하, 동결={froze}, 원장 오차 {diff}raw"
                : $"실패 — failOpen={failOpen}, froze={froze}(장애 {faultsInjected}), idem={idem}, diff={diff}raw";

            var result = new ChaosRunResult(seed, clock.NowTicks - cfg.StartTicks, faultsInjected, chaosFlags,
                froze, idem, failOpen, rejectedFills, expected, ledger, diff, passed, detail, events);

            // 라이브 텔레메트리 갱신.
            Interlocked.Exchange(ref _lastVirtualTicks, result.VirtualTicksElapsed);
            Interlocked.Exchange(ref _lastChaosFlags, chaosFlags);
            Interlocked.Exchange(ref _lastQueueDepth, 0);   // 인메모리 싱크는 큐 없음(mesh 경로는 테스트가 별도 관측)
            if (passed) Interlocked.Increment(ref _cumPbtPasses);
            LastResult = result;
            return result;
        }

        // ── 장애 주입(전부 인메모리·실 패킷 0) ────────────────────────────────────
        private static void InjectFault(ChaosFault f, OrderAttestationService att, QFxCircuitBreaker breaker,
                                        VirtualProxyLink proxy, long nowTicks, long heartbeatTimeoutTicks)
        {
            switch (f)
            {
                case ChaosFault.KiwoomProxyDown:
                    proxy.Down();   // x86 프록시 링크 단절(격리 스텁)
                    // 하트비트 타임아웃 판정(실 async 워치독 대신 결정론 동기 판정) → 브리지 규약과 동일 수렴.
                    if (proxy.IsLost(nowTicks, heartbeatTimeoutTicks))
                        att.ForceFreeze("Kiwoom IPC ProxyLost — 가상 하트비트 타임아웃");
                    break;
                case ChaosFault.TossAuth401:
                    att.ForceFreeze("Toss WSS 401 — 가상 인증 실패(토큰 없음)");
                    break;
                case ChaosFault.FxSpike:
                    breaker.TripExternal("FX 3σ 급변동 — 가상");   // 실 latch(Interlocked)·ForceFreeze 수렴 1회
                    break;
                case ChaosFault.FxStale:
                    breaker.TripExternal("FX 1s 스테일 — 가상");
                    break;
            }
        }

        // ── PBT(속성 기반 테스트) — 수천 시드 자동 수색 + shrinking ──
        //   ★정직 범위: 기본(InMemorySink)은 구조적 fail-closed(동결 시 수용 거부 내장)라, 기본 스윕이 증명하는 것은
        //   '결정론·무행(no-hang)·원장 정합(diff≤1raw)·멱등'이다. fail-open 경계 수색은 sinkFactory 로 fail-open '가능'
        //   싱크(예: MeshLedgerSink 또는 게이트 제거 뮤테이션 스텁)를 주입해 스윕할 때 유효하다(탐지기 실효성은 음성 테스트로 증명).
        public PbtResult Explore(ChaosGridConfig cfg, int seedCount,
                                 Func<OrderAttestationService, IChaosLedgerSink>? sinkFactory = null)
        {
            if (seedCount <= 0) throw new ArgumentOutOfRangeException(nameof(seedCount));
            int passes = 0, failures = 0, firstFail = -1;
            IReadOnlyList<ChaosEventRecord> minimal = Array.Empty<ChaosEventRecord>();

            for (int seed = 0; seed < seedCount; seed++)
            {
                var r = RunScenario(seed, cfg, sinkFactory);
                if (r.Passed) passes++;
                else
                {
                    failures++;
                    if (firstFail < 0)
                    {
                        firstFail = seed;
                        // ★최소 재현 시퀀스 역추적 — 이벤트를 제거해도 여전히 실패하는 최소 부분집합.
                        minimal = ChaosShrinker.Shrink(r.Events,
                            subset => !RunWithEvents(seed, subset, cfg, sinkFactory).Passed);
                    }
                }
            }
            string detail = failures == 0
                ? $"{seedCount} 시드 전부 통과 — 결정론·무행·원장 정합 확인(주입 싱크 fail-closed)"
                : $"{failures}/{seedCount} 실패 — 최초 실패 시드 {firstFail}, 최소 재현 이벤트 {minimal.Count}건";
            return new PbtResult(seedCount, passes, failures, firstFail, minimal, detail);
        }

        // ── UI 재현 트리거(단일 백그라운드 스레드) ───────────────────────────────
        public void ReproduceInBackground(int seed, ChaosGridConfig cfg)
        {
            var t = new Thread(() => { try { RunScenario(seed, cfg); } catch { /* 방어 throw 는 내부 수렴 */ } })
            { IsBackground = true, Name = "ChaosReproduce", Priority = ThreadPriority.BelowNormal };
            t.Start();
        }

        // ── 기본 인메모리 싱크(동결 게이트 + δ=0 명목 누적) ───────────────────────
        private sealed class InMemorySink : IChaosLedgerSink
        {
            private readonly OrderAttestationService _att;
            private long _notional;
            public InMemorySink(OrderAttestationService att) => _att = att;
            public bool Persist(in DbTransactionPayload p)
            {
                if (_att.Mode != GatingMode.Normal) return false;   // ★동결 중 수용 금지(fail-closed)
                _notional = checked(_notional + QMath.Notional(p.PriceRaw, p.QtyRaw, roundUp: p.TypeByte == (byte)TransactionType.Buy));
                return true;
            }
            public long LedgerNotionalRaw() => _notional;
        }
    }

    /// <summary>
    /// 실 원장 싱크 — LedgerProtectionMesh + SQLite 로 디퍼런셜 어설션(원장 명목 vs naive)을 REAL 컴포넌트로 수행.
    /// per-row 재정수화로 double 왕복 손실을 회피(정수 입력은 ≤1raw 정확 수렴). Start() 미호출(결정론 동기 드레인).
    /// </summary>
    public sealed class MeshLedgerSink : IChaosLedgerSink, IDisposable
    {
        private readonly LocalDatabase        _db;
        private readonly LedgerProtectionMesh _mesh;
        public MeshLedgerSink(LocalDatabase db, OrderAttestationService att)
        {
            _db   = db ?? throw new ArgumentNullException(nameof(db));
            _mesh = new LedgerProtectionMesh(db, att);   // Start 안 함 — 시뮬이 DrainAvailable 직접 구동
        }
        public int QueueDepth => _mesh.QueueDepth;
        public bool Persist(in DbTransactionPayload p)
        {
            if (!_mesh.Enqueue(p)) return false;   // 동결 시 false
            _mesh.DrainAvailable();                 // 즉시 순차 영속화(AddTransaction→RecomputeAverage)
            return true;
        }
        public long LedgerNotionalRaw()
        {
            var t = _db.Query("SELECT Type, Quantity, Price FROM Transactions");
            long sum = 0;
            foreach (DataRow r in t.Rows)
            {
                long qtyRaw   = QMath.FromDecimal(Safe.Dec(r["Quantity"]));
                long priceRaw = QMath.FromDecimal(Safe.Dec(r["Price"]));
                bool buy      = Safe.Int(r["Type"]) == (int)TransactionType.Buy;
                sum = checked(sum + QMath.Notional(priceRaw, qtyRaw, roundUp: buy));
            }
            return sum;
        }
        public void Dispose() { try { _mesh.Dispose(); } catch { } }
    }
}
