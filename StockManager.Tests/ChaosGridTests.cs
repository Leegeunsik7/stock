// ============================================================================
// ChaosGridTests — v4.91.0: 결정론적 카오스 테스팅 그리드
//
//  §1 가상 시간 패브릭 — 시간 역전 throw·스케줄러 due 순서/무한루프 방어·PRNG 결정론.
//  §2 카오스 실행       — 인메모리/실메시 디퍼런셜 원장 ≤1raw·동결·멱등·fail-open 적발.
//  §3 PBT + shrinking   — 2000 시드 전부 통과(fail-closed)·최소 재현 시퀀스 역추적.
//  §4 격리 스텁·텔레메트리 — VirtualProxyLink 상실 판정·RingTelemetry 카오스 필드/헬퍼/퍼블리셔.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    // v4.91.0 — PBT 2000시드·실메시 SQLite 디퍼런셜은 CPU 헤비 → 병렬 구간에서 초과구독을 유발해 타 클래스의
    //   δ=0 알로케이션 측정(GC.GetAllocatedBytesForCurrentThread==0)을 교란(플래키)한다. 확립된 house 격리
    //   ConcurrencyStress(DisableParallelization) 편입 — 단독 직렬 구간 실행으로 초과구독 소거(안전 단언은 무관).
    [Collection("ConcurrencyStress")]
    public sealed class ChaosGridTests
    {
        // ── §1 가상 시간 패브릭 ────────────────────────────────────────────────

        [Fact]
        public void VirtualClock_TimeReversal_Throws_MonotonicOnly()
        {
            var c = new VirtualClock(1000);
            Assert.Throws<InvalidOperationException>(() => c.Advance(-1));      // ★음수 전진 금지
            Assert.Throws<InvalidOperationException>(() => c.AdvanceTo(999));   // ★과거 전진 금지(시간 역전 맹점 차단)
            c.Advance(500);
            Assert.Equal(1500, c.NowTicks);
            c.AdvanceTo(1500);   // 동일 시각 재전진 허용(비후퇴)
            Assert.Equal(1500, c.NowTicks);
        }

        [Fact]
        public void Scheduler_RunsInDueOrder_VirtualTimeAdvances_NoRealWait()
        {
            var clock = new VirtualClock(0);
            var sched = new VirtualScheduler(clock);
            var log = new List<long>();
            sched.Schedule(300, () => log.Add(clock.NowTicks));
            sched.Schedule(100, () => log.Add(clock.NowTicks));
            sched.Schedule(200, () => log.Add(clock.NowTicks));
            int n = sched.DrainAll(maxSteps: 100);
            Assert.Equal(3, n);
            Assert.Equal(new long[] { 100, 200, 300 }, log.ToArray());   // due 오름차순
            Assert.Equal(300, clock.NowTicks);                          // 가상 시간 전진
        }

        [Fact]
        public void Scheduler_SameDueTie_DeterministicBySeq()
        {
            var clock = new VirtualClock(0);
            var sched = new VirtualScheduler(clock);
            var log = new List<int>();
            for (int i = 0; i < 5; i++) { int k = i; sched.Schedule(100, () => log.Add(k)); }
            sched.DrainAll(100);
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, log.ToArray());   // 동일 시각 = 삽입 seq 순서(결정론)
        }

        [Fact]
        public void Scheduler_InfiniteReschedule_HangGuardThrows()
        {
            var clock = new VirtualClock(0);
            var sched = new VirtualScheduler(clock);
            Action loop = null!;
            loop = () => sched.Schedule(0, loop);   // ★동일 시각 무한 재예약
            sched.Schedule(0, loop);
            // 행(hang)에 빠지지 않고 maxSteps 초과로 즉시 예외 — 테스트 러너 무한 대기 원천 차단.
            Assert.Throws<ChaosGridHangException>(() => sched.DrainAll(maxSteps: 1000));
        }

        [Fact]
        public void Prng_SameSeed_Reproducible_DiffSeed_Differs()
        {
            var a = new DeterministicPrng(42);
            var b = new DeterministicPrng(42);
            for (int i = 0; i < 16; i++) Assert.Equal(a.NextULong(), b.NextULong());
            Assert.NotEqual(new DeterministicPrng(42).NextULong(), new DeterministicPrng(43).NextULong());
        }

        // ── §2 카오스 실행 ─────────────────────────────────────────────────────

        [Fact]
        public void ChaosRun_InMemory_Freezes_DifferentialZero_Idempotent()
        {
            var grid = new DeterministicChaosGrid();
            var r = grid.RunScenario(7, new ChaosGridConfig());
            Assert.True(r.Passed, r.Detail);
            Assert.True(r.Froze);                     // 장애 4건 투하 → 반드시 동결(fail-closed)
            Assert.Equal(0L, r.NotionalDiffRaw);      // 인메모리 원장 == naive 명목
            Assert.True(r.WarmRestartIdempotent);     // ForceFreeze/ManualRelease 멱등
            Assert.False(r.FailOpen);                 // 동결 중 신규 체결 수용 없음
        }

        [Fact]
        public void ChaosRun_RealMesh_DifferentialLedger_MatchesNaive_Within1Raw()
        {
            var path = Path.Combine(Path.GetTempPath(), $"chaos_{Guid.NewGuid():N}.db");
            try
            {
                using var db = new LocalDatabase(path);
                var grid = new DeterministicChaosGrid();
                // 실 LedgerProtectionMesh + SQLite 로 디퍼런셜 어설션(per-row 재정수화).
                var r = grid.RunScenario(7, new ChaosGridConfig(), att => new MeshLedgerSink(db, att));
                Assert.True(r.Passed, r.Detail);
                Assert.True(r.NotionalDiffRaw <= 1L, $"원장 명목 오차 {r.NotionalDiffRaw}raw > 1raw");
                Assert.Equal(r.ExpectedNotionalRaw, r.LedgerNotionalRaw);   // 정수 입력 → 정확 일치
                Assert.True(r.ExpectedNotionalRaw > 0L);                    // 최소 일부 체결은 영속화됨
                // ★mesh fail-closed 게이트가 실제로 실행됐음을 명시 단언(암묵 의존 제거): 동결 후 체결이 거부됨.
                Assert.True(r.Froze);
                Assert.True(r.RejectedFills > 0, "동결 후 체결이 하나도 거부되지 않음 — mesh 동결 게이트 미실행");
            }
            finally { try { File.Delete(path); } catch { } }
        }

        [Fact]
        public void ChaosRun_KiwoomProxyDown_FreezesInMemory_RejectsPostFreezeFill()
        {
            var grid = new DeterministicChaosGrid();
            var events = new List<ChaosEventRecord>
            {
                new(0,    ChaosEventKind.Fill,  ChaosFault.None,            5930, QQty.Of(10).Raw, QPrice.Of(70000).Raw, (byte)TransactionType.Buy),
                new(1000, ChaosEventKind.Fault, ChaosFault.KiwoomProxyDown, 0, 0, 0, 0),
                new(2000, ChaosEventKind.Fill,  ChaosFault.None,            5930, QQty.Of(5).Raw,  QPrice.Of(71000).Raw, (byte)TransactionType.Buy),
            };
            var r = grid.RunWithEvents(99, events, new ChaosGridConfig());
            Assert.True(r.Froze);
            Assert.True((r.ChaosFlags & (long)ChaosFault.KiwoomProxyDown) != 0);
            Assert.False(r.FailOpen);                  // 동결 후 체결(2000) 거부 — 수용 안 됨
            Assert.Equal(1, r.RejectedFills);          // 동결 후 체결 1건이 실제로 거부됨(fail-closed 게이트 실행)
            // 동결 전 체결(10주@70000원)만 명목 계상. Notional = price×qty×Scale(정수라 정확).
            Assert.Equal(70000L * 10L * 100_000_000L, r.ExpectedNotionalRaw);
            Assert.True(r.Passed);
        }

        // ── §3 PBT + shrinking ─────────────────────────────────────────────────

        [Fact]
        public void Pbt_2000Seeds_NoFailOpen_AllPass()
        {
            var grid = new DeterministicChaosGrid();
            var r = grid.Explore(new ChaosGridConfig(), seedCount: 2000);
            Assert.Equal(0, r.Failures);       // ★fail-open 경계 미발견(fail-closed 견고)
            Assert.Equal(2000, r.Passes);
        }

        // ★fail-open 버그 모사 싱크 — 동결 여부를 무시하고 항상 수용(정상 싱크라면 동결 시 false 여야 함).
        //   정수 입력 명목을 커널 QMath.Notional 과 동일하게 누적(price×qty×Scale)해 diff=0 → 실패 사유를 오직 failOpen 으로 고립.
        private sealed class FailOpenProbeSink : IChaosLedgerSink
        {
            private const long Scale = 100_000_000L;
            private long _n;
            public bool Persist(in DbTransactionPayload p)
            { _n = checked(_n + (p.PriceRaw / Scale) * (p.QtyRaw / Scale) * Scale); return true; }
            public long LedgerNotionalRaw() => _n;
        }

        [Fact]
        public void FailOpenDetector_IsLive_CatchesFailOpenSink()
        {
            // ★탐지기가 사문(dead code)이 아님을 증명(뮤테이션/음성 테스트): fail-open 싱크를 물리면 반드시 적발.
            var grid = new DeterministicChaosGrid();
            var r = grid.RunScenario(7, new ChaosGridConfig(), _ => new FailOpenProbeSink());
            Assert.True(r.FailOpen, "탐지기가 fail-open(동결 중 수용)을 적발하지 못함");
            Assert.False(r.Passed);
        }

        [Fact]
        public void Pbt_WithFailOpenSink_SweepIsNotVacuous_DetectsAndShrinks()
        {
            // ★PBT 수색이 공허하지 않음을 증명: fail-open 가능 싱크를 주입해 스윕하면 실패가 검출되고 최소 재현 시퀀스가 나온다.
            var grid = new DeterministicChaosGrid();
            var r = grid.Explore(new ChaosGridConfig(), seedCount: 20, sinkFactory: _ => new FailOpenProbeSink());
            Assert.True(r.Failures >= 1, "fail-open 싱크인데 스윕이 하나도 적발 못함 — 수색이 공허(사문)");
            Assert.True(r.MinimalFailingSequence.Count >= 1);   // shrinking 이 최소 재현 이벤트 산출
        }

        [Fact]
        public void Shrinker_IsolatesMinimalFailingSubset()
        {
            var input = new[] { "A", "B", "C", "D", "E" };
            // 실패 조건: 부분집합이 C 와 E 를 모두 포함(최소 재현 = [C,E]).
            var min = ChaosShrinker.Shrink(input, s => s.Contains("C") && s.Contains("E"));
            Assert.Equal(2, min.Count);
            Assert.Contains("C", min);
            Assert.Contains("E", min);
        }

        [Fact]
        public void GenerateEvents_SameSeed_Deterministic()
        {
            var grid = new DeterministicChaosGrid();
            var cfg = new ChaosGridConfig();
            var a = grid.GenerateEvents(123, cfg);
            var b = grid.GenerateEvents(123, cfg);
            Assert.Equal(a.Count, b.Count);
            for (int i = 0; i < a.Count; i++) Assert.Equal(a[i], b[i]);   // 동일 시드 → 동일 이벤트 스트림
        }

        // ── §4 격리 스텁 · 텔레메트리 ──────────────────────────────────────────

        [Fact]
        public void VirtualProxyLink_IsolationStub_LostSemantics()
        {
            var p = new VirtualProxyLink();
            p.Heartbeat(1000);
            Assert.False(p.IsLost(1000, timeoutTicks: 500));   // 연결·최근 하트비트 → 정상
            Assert.True(p.IsLost(2000, 500));                   // 타임아웃 초과 → 상실
            p.Down();
            Assert.True(p.IsLost(1000, 500));                   // ★단절 → 상실(타임아웃 무관)
            p.Up(3000);
            Assert.False(p.IsLost(3000, 500));                  // 재연결 → 정상
        }

        [Fact]
        public void RingTelemetry_ChaosFields_And_Helpers()
        {
            var t = new RingTelemetry(0, 0, 0, 0, 0, 0.0,
                virtualTimeTicks: TimeSpan.TicksPerSecond * 3,
                chaosFlags: (long)(ChaosFault.KiwoomProxyDown | ChaosFault.FxSpike),
                pbtPasses: 1234, virtualQueueDepth: 5);
            Assert.True(t.ChaosActive);
            Assert.True(t.ChaosKiwoomProxyDown);
            Assert.True(t.ChaosFxSpike);
            Assert.False(t.ChaosTossAuth401);
            Assert.Equal(1234L, t.PbtPasses);
            Assert.Equal(3.0, t.VirtualSeconds, precision: 3);
        }

        [Fact]
        public void Publisher_ReflectsChaosGrid()
        {
            var grid = new DeterministicChaosGrid();
            grid.RunScenario(1, new ChaosGridConfig());   // 라이브 텔레메트리 갱신
            var att = new OrderAttestationService();
            var ring = new OrderDispatchRing(8);
            var disp = new GatedOrderDispatcher(ring, () => false, att);
            var ch = new RingTelemetryChannel();
            var pub = new RingTelemetryPublisher(ring, disp, ch, () => 0L,
                null, null, null, null, null, null, null, grid);
            pub.Sample();
            Assert.True(ch.TryRead(out var snap));
            Assert.Equal(grid.PbtPasses, snap.PbtPasses);
            Assert.Equal(grid.VirtualTimeTicks, snap.VirtualTimeTicks);
        }
    }
}
