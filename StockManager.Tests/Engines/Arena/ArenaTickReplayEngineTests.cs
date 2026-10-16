// ============================================================================
// ArenaTickReplayEngineTests — [v12.18.12-DEV] 아레나 고속 틱 리플레이 적대적 검증
//   §1 배속 스케줄: 1 · 5 · 10 · 50 · 100x = 벽시계 Δ × 배속 만큼 정확한 틱 수 · 배속 변경 재기준 · 최대 = 회차당 MaxBatch · 완료 상태
//   §2 상태 머신: 일시정지 = 포지션/손익 동결 + 정지 시간 미보상(재개 재기준) · 일시정지 중 휴먼 주문 대기 → 재개 첫 틱 체결 · 리셋 · 결정론
//   §3 체결 규칙 공유: 리플레이 휴먼 체결 = 해당 틱 호가 · DualBookSimulator 수수료 · 합성 테이프에서 AI(정량 시그널 엔진) 실매매
//   §4 δ=0: 100x 재생(틱 주입 · AI · 체결 · 정산 · 게시 · 판독 · 휴먼 주문) 0B · 최대 배속 0B · 실 재생 스레드 1M 틱 누적 할당 상한
//   §5 게시 디커플링: 최대 배속에서도 스냅샷 게시 = 50ms 주기 · 허브 펌프 요청 즉시 게시
//   §6 Fail-Closed: IL 참조 허용목록 · 런타임 객체 그래프(브로커/무장 인스턴스 0) · 호스트 결선 · 셸 래칫 · 헤드리스 UI 결속(bUnit)
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Engines.Arena;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaTickReplayEngineTests
    {
        private const long Freq = 10_000_000, Sec = Freq;
        private const long Capital = 100_000_000;
        private readonly ITestOutputHelper _out;
        private long _now = 1_000 * Sec;

        public ArenaTickReplayEngineTests(ITestOutputHelper o) => _out = o;

        // [v12.18.13-DEV] 이 파일은 체결 지연 0(v12.18.12 재생 의미 보존) — 지연 · 대기열 · 테이프 파일은 MicrostructureExecutionTests.
        private ArenaTickReplayEngine Manual(ArenaTickSource tape) => new(() => tape, () => _now, Freq, ownThread: false, latencyMinTicks: 0, latencyMaxTicks: 0);
        private static SyntheticTickSource Tape(int count, long intervalTicks = 1_000_000) => SyntheticTickSource.Create(count, intervalTicks: intervalTicks);

        private static ArenaSnapshot Snap(ArenaTickReplayEngine e)
        {
            Assert.True(e.Arena.TryRead(out var s));
            return s;
        }

        // ═══ §1 배속 스케줄 ═══

        [Theory]
        [InlineData(1)] [InlineData(5)] [InlineData(10)] [InlineData(50)] [InlineData(100)]
        public void Speed_Schedules_Exact_Ticks_Per_Wall_Interval(int speed)
        {
            var e = Manual(Tape(50_000));                                        // 간격 100ms
            Assert.True(e.SetSpeed(speed));
            Assert.True(e.Play(Capital));
            Assert.Equal(1, e.Pump(_now));                                       // 틱 0 = 즉시
            _now += 2 * Sec;
            Assert.Equal(2 * speed * 10, e.Pump(_now));                          // 2초 × 배속 ÷ 100ms
            _now += Sec / 2;
            Assert.Equal(speed * 5, e.Pump(_now));
            Assert.Equal(1 + 25 * speed, e.Status.Cursor);
            Assert.Equal(ArenaReplayState.Playing, e.Status.State);
            Assert.Equal(speed, e.Status.Speed);
        }

        [Fact]
        public void Speed_Change_Reanchors_And_Max_Runs_Batches_To_Finish()
        {
            var tape = Tape(20_000);
            var e = Manual(tape);
            e.Play(Capital);
            e.Pump(_now);
            _now += Sec; Assert.Equal(10, e.Pump(_now));                         // 1x
            Assert.True(e.SetSpeed(10));
            _now += Sec / 2; Assert.Equal(5, e.Pump(_now));                      // 변경 반영 회차 = 그때까지 기존 1x 로 처리 후 재기준
            _now += Sec; Assert.Equal(100, e.Pump(_now));                        // 이후 10x
            Assert.False(e.SetSpeed(7));
            Assert.True(e.SetSpeed(ArenaReplaySpeed.Max));
            Assert.Equal(0, e.Pump(_now));                                       // 전환 회차(도래분 없음)
            Assert.Equal(ArenaTickReplayEngine.MaxBatch, e.Pump(_now));          // 시각 무시 · 회차 상한
            int total = 116 + ArenaTickReplayEngine.MaxBatch;
            while (e.State == ArenaReplayState.Playing) total += e.Pump(_now);
            Assert.Equal(tape.Count, total);
            Assert.Equal(ArenaReplayState.Finished, e.Status.State);
            Assert.Equal(10_000, e.Status.ProgressBp);
            Assert.Equal(ArenaState.Finished, Snap(e).State);
            Assert.Equal(0, e.Pump(_now));                                       // 완료 후 처리 0
        }

        // ═══ §2 상태 머신 ═══

        [Fact]
        public void Pause_Freezes_Books_Exactly_Queues_Human_Order_And_Resume_Does_Not_Replay_Paused_Time()
        {
            var tape = Tape(50_000);
            var e = Manual(tape);
            e.SetSpeed(100);
            e.Play(Capital);
            e.Pump(_now);
            Assert.Equal(ArenaReject.None, e.Arena.SubmitHuman(5930, 1, 7));
            _now += Sec; e.Pump(_now);
            var before = Snap(e);
            Assert.Equal(1, before.Human.Fills);
            e.Pause();
            e.Pump(_now);
            Assert.Equal(ArenaReplayState.Paused, e.Status.State);
            long cursor = e.Status.Cursor;
            tape.Read((int)cursor, out var next);                               // 재개 첫 틱 호가
            Assert.Equal(ArenaReject.None, e.Arena.SubmitHuman(next.SymbolCode, 1, 3));   // 일시정지 중 주문 = 대기
            for (int i = 0; i < 40; i++) { _now += Sec; Assert.Equal(0, e.Pump(_now)); }
            var paused = Snap(e);
            Assert.Equal(cursor, e.Status.Cursor);
            Assert.Equal((before.Ai.EquityWon, before.Ai.CashWon, before.Ai.Fills, before.Ai.OpenPositions),
                         (paused.Ai.EquityWon, paused.Ai.CashWon, paused.Ai.Fills, paused.Ai.OpenPositions));
            Assert.Equal((before.Human.EquityWon, before.Human.CashWon, before.Human.Fills, before.Human.UnrealizedWon),
                         (paused.Human.EquityWon, paused.Human.CashWon, paused.Human.Fills, paused.Human.UnrealizedWon));

            Assert.True(e.Play(Capital / 2));                                    // 일시정지 = 재개(자본금 무시 · 원장 유지)
            Assert.Equal(0, e.Pump(_now));                                       // 정지 40초 미보상(재기준)
            _now += Sec / 1000;
            long v = e.Arena.Version;
            int n = e.Pump(_now);
            Assert.Equal(1, n);
            Assert.Equal(v, e.Arena.Version);                                    // 1ms 뒤 = 50ms 게시 주기 미도래(틱 정산은 게시하지 않음)
            e.OnHubPump();
            e.Pump(_now);                                                        // 허브 요청 = 즉시 게시(처리 틱 0)
            var resumed = Snap(e);
            Assert.Equal(2, resumed.Human.Fills);
            long held = next.SymbolCode == 5930 ? 7 + 3 : 3;
            Assert.Equal(held, e.Governor.Simulator.Book(ArenaSide.Human).Position(next.SymbolCode));
            long notional = Walk(in next, 3);
            Assert.Equal(paused.Human.CashWon - notional - DualBookSimulator.Fee(notional), resumed.Human.CashWon);
        }

        // 리플레이 체결 기대값 = 호가 1→10 순회(시뮬레이터와 같은 규칙의 독립 재계산)
        private static long Walk(in OrderBookPayload b, long qty)
        {
            long notional = 0;
            for (int l = 0; l < 10 && qty > 0; l++) { long take = Math.Min(qty, b.AskQty[l]); notional += take * b.AskPrice[l]; qty -= take; }
            return notional;
        }

        [Fact]
        public void Reset_Rewinds_To_Idle_And_Replays_Are_Deterministic()
        {
            var tape = Tape(30_000, SyntheticTickSource.DefaultIntervalTicks);
            var e = Manual(tape);
            e.SetSpeed(ArenaReplaySpeed.Max);
            e.Play(Capital);
            do e.Pump(_now); while (e.State == ArenaReplayState.Playing);
            var first = Snap(e);
            Assert.True(first.Ai.Fills > 0, "합성 테이프에서 AI 체결 0 — 시그널 엔진 미반응");

            e.Reset();
            e.Pump(_now);
            var idle = Snap(e);
            Assert.Equal(ArenaReplayState.Idle, e.Status.State);
            Assert.Equal(0, e.Status.Cursor);
            Assert.Equal(ArenaState.Idle, idle.State);
            Assert.Equal(Capital, idle.Ai.CashWon);
            Assert.Equal(0, idle.Ai.Fills + idle.Human.Fills);
            Assert.Equal(ArenaReject.NotRunning, e.Arena.SubmitHuman(5930, 1, 1));

            e.Play(Capital);
            do e.Pump(_now); while (e.State == ArenaReplayState.Playing);
            var second = Snap(e);
            Assert.Equal((first.Ai.EquityWon, first.Ai.Fills, first.Ai.FeesWon, first.Ai.TaxWon, first.AiSignals),
                         (second.Ai.EquityWon, second.Ai.Fills, second.Ai.FeesWon, second.Ai.TaxWon, second.AiSignals));
            _out.WriteLine($"[결정론] AI 체결 {second.Ai.Fills} · 시그널 {second.AiSignals} · 자산 {second.Ai.EquityWon:N0} · MDD {second.Ai.MddWon:N0}");
        }

        [Fact]
        public void Finished_Then_Play_Restarts_New_Match_With_New_Capital()
        {
            var e = Manual(Tape(2_000));
            e.SetSpeed(ArenaReplaySpeed.Max);
            e.Play(Capital);
            do e.Pump(_now); while (e.State == ArenaReplayState.Playing);
            Assert.False(e.Play(999));
            Assert.True(e.Play(20_000_000));
            e.Pump(_now);
            var s = Snap(e);
            Assert.Equal(20_000_000, s.CapitalWon);
            Assert.True(e.Status.Cursor > 0);
        }

        // ═══ §3 체결 규칙 공유 ═══

        [Fact]
        public void Recorded_Tape_Fills_Use_Simulator_Fee_And_Tax_Rules()
        {
            var books = new OrderBookPayload[4];
            for (int i = 0; i < books.Length; i++)
            {
                books[i].SymbolCode = 5930; books[i].TimestampTicks = 1_000_000L * (i + 1);
                for (int l = 0; l < 10; l++)
                {
                    books[i].AskPrice[l] = 70_000 + i * 1_000 + l * 100; books[i].AskQty[l] = 5;
                    books[i].BidPrice[l] = 69_900 + i * 1_000 - l * 100; books[i].BidQty[l] = 5;
                }
            }
            Assert.Throws<ArgumentException>(() => new RecordedTickSource(new[] { books[1], books[0] }));
            var e = Manual(new RecordedTickSource(books));
            e.Play(Capital);
            e.Pump(_now);                                                        // 틱 0(70,000)
            e.Arena.SubmitHuman(5930, 1, 7);                                     // 다음 틱(71,000) 호가 순회: 5 × 71,000 + 2 × 71,100
            _now += Sec / 10; e.Pump(_now);
            long buy = 5 * 71_000 + 2 * 71_100;
            var s = Snap(e);
            Assert.Equal(Capital - buy - DualBookSimulator.Fee(buy), s.Human.CashWon);
            Assert.Equal(2 * 100, s.Human.SlippageWon);
            e.Arena.SubmitHuman(5930, -1, 7);                                    // 다음 틱(매수호가 71,900 · 71,800)
            _now += Sec / 10; e.Pump(_now);
            long sell = 5 * 71_900 + 2 * 71_800;
            s = Snap(e);
            Assert.Equal(DualBookSimulator.Tax(sell), s.Human.TaxWon);
            Assert.Equal(sell - DualBookSimulator.Fee(sell) - DualBookSimulator.Tax(sell) - (buy + DualBookSimulator.Fee(buy)), s.Human.RealizedWon);
            _now += Sec; e.Pump(_now);
            Assert.Equal(ArenaReplayState.Finished, e.State);
        }

        // ═══ §4 δ=0 ═══

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return min;
        }

        [Fact]
        public void Playback_At_100x_And_Max_Is_Zero_Alloc_Including_Ai_Fills_Publish_And_Reads()
        {
            var tape = SyntheticTickSource.Create(400_000);
            var hub = new DashboardSnapshotHub(nowTicks: () => _now);
            var e = Manual(tape);
            hub.AttachArenaReplay(e);
            e.SetSpeed(100);
            e.Play(Capital);
            var curve = new long[ArenaMatchGovernor.MaxEquity];
            long sink = 0;
            int i = 0;
            void Loop100x()
            {
                for (int k = 0; k < 100; k++, i++)
                {
                    _now += Sec / 20;                                            // 50ms 벽시계 × 100x = 약 213틱/회
                    e.Arena.SubmitHuman(5930, (i & 1) == 0 ? 1 : -1, 2);
                    sink += e.Pump(_now);
                    e.OnHubPump();
                    if (e.Arena.TryRead(out var s)) sink += s.Version;
                    sink += e.Status.Cursor + e.Arena.CopyEquity(i & 1, curve);
                }
            }
            Loop100x();                                                          // 워밍업
            Assert.Equal(0, MinAllocated(Loop100x));
            long at100 = e.Status.Cursor;
            Assert.True(at100 > 40_000, "100x 처리 틱 " + at100);                 // ≥ 200회 × 약 213틱
            e.SetSpeed(ArenaReplaySpeed.Max);
            void LoopMax() { for (int k = 0; k < 4; k++) { _now += Sec / 1000; sink += e.Pump(_now); } }
            LoopMax();
            Assert.Equal(0, MinAllocated(LoopMax));
            var snap = Snap(e);
            Assert.True(snap.Ai.Fills > 0 && snap.Human.Fills > 100, $"ai={snap.Ai.Fills} human={snap.Human.Fills}");
            Assert.True(sink > 0);
            hub.Dispose();
        }

        [Fact]
        public void Real_Thread_Replays_1M_Ticks_At_Max_Without_Per_Tick_Allocation()
        {
            var tape = SyntheticTickSource.Create(1_000_000);                   // 테이프 생성(콜드)은 측정 밖
            using var e = new ArenaTickReplayEngine(() => tape);
            e.SetSpeed(ArenaReplaySpeed.Max);
            GC.Collect(); GC.WaitForPendingFinalizers();
            long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
            int gen0 = GC.CollectionCount(0);
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            Assert.True(e.Play(Capital));
            long versions = 0, lastV = -1;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (e.State != ArenaReplayState.Finished && DateTime.UtcNow < deadline)
            {
                long v = e.Status.Version;                                       // UI 폴링 흉내(값 복사 판독)
                if (v != lastV) { versions++; lastV = v; }
                Thread.Sleep(5);
            }
            double sec = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            long alloc = GC.GetTotalAllocatedBytes(precise: true) - alloc0;
            int gcs = GC.CollectionCount(0) - gen0;
            var s = Snap(e);
            _out.WriteLine($"[1M 최대 배속] {sec:F2}s · {1_000_000 / sec:N0} 틱/s · 누적 할당 {alloc:N0}B · Gen0 {gcs} · 게시 관측 {versions} · AI 체결 {s.Ai.Fills} · 시그널 {s.AiSignals}");
            Assert.Equal(ArenaReplayState.Finished, e.State);
            Assert.Equal(1_000_000, e.Processed);
            Assert.True(alloc < 1_000_000, $"1M 틱 누적 할당 {alloc:N0}B ≥ 1MB(틱당 할당 의심) — 콜드: 스레드 · AI 인스턴스 · 명령");
            Assert.True(s.AiSignals > 0);
        }

        // ═══ §5 게시 디커플링 ═══

        [Fact]
        public void Snapshot_Publish_Is_50ms_Cadence_At_Max_Speed_And_Hub_Request_Is_Immediate()
        {
            var e = Manual(SyntheticTickSource.Create(2_000_000));
            e.SetSpeed(ArenaReplaySpeed.Max);
            e.Play(Capital);
            e.Pump(_now);
            long v0 = e.Status.Version;
            for (int k = 0; k < 100; k++) { _now += Sec / 100; e.Pump(_now); }      // 1초(10ms 회차 · 회차당 8,192틱 = 약 82만 틱)
            long published = e.Status.Version - v0;
            Assert.InRange(published, 19, 21);
            Assert.Equal(ArenaReplayState.Playing, e.State);
            Assert.Equal(Capital, Snap(e).CapitalWon);
            long v1 = e.Status.Version;
            _now += Sec / 1000;
            e.OnHubPump();
            e.Pump(_now);
            Assert.Equal(v1 + 1, e.Status.Version);                              // 허브 요청 = 50ms 미경과여도 게시
            Assert.InRange(e.Status.TicksPerSecond, 700_000, 900_000);           // 처리율 = 주입 벽시계 기준(819,200/s)
        }

        // ═══ §6 Fail-Closed ═══

        private static readonly Type[] VettedEngineTypes = { typeof(QuantSignalProvider), typeof(StrategyAnomalyInterlock), typeof(IDecisionSink) };

        private static Type Outermost(Type t) { while (t.DeclaringType is { } d) t = d; return t; }

        private static bool Allowed(Type t)
        {
            while (t.HasElementType) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) return Allowed(t.GetGenericTypeDefinition()) && t.GetGenericArguments().All(Allowed);
            if (t.IsGenericParameter) return true;
            if (t == typeof(TelemetryRing<>) || t == typeof(OrderBookPayload) || t == typeof(OrderBookLevels10)) return true;
            if (t.FullName == "<PrivateImplementationDetails>") return true;
            if (VettedEngineTypes.Contains(Outermost(t))) return true;
            string ns = Outermost(t).Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Threading", StringComparison.Ordinal) || ns.StartsWith("System.Runtime", StringComparison.Ordinal)
                || ns == "System.Diagnostics" || ns == "Microsoft.Win32.SafeHandles" || ns == "System.IO" || ns == "System.IO.MemoryMappedFiles" || ns == "System.Buffers.Binary" || ns == "System.Collections.Generic"
                || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arena";
        }

        [Fact]
        public void CompileTime_Replay_Il_References_Only_Allowlisted_Types()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var roots = new[] { typeof(ArenaTickReplayEngine), typeof(ArenaTickSource), typeof(SyntheticTickSource), typeof(RecordedTickSource), typeof(ArenaMatchGovernor), typeof(DualBookSimulator),
                                typeof(ArenaTapeReader), typeof(ArenaTapeWriter), typeof(ArenaTapeRecorder), typeof(ArenaTapeStore), typeof(CumulativeVolumeTracker) };
            var violations = new List<string>();
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>(roots.Concat(roots.SelectMany(t => t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))));
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (!seen.Add(t)) continue;
                foreach (var n in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) queue.Enqueue(n);
                foreach (var f in t.GetFields(All)) if (!Allowed(f.FieldType)) violations.Add($"{t.Name}.{f.Name}: {f.FieldType.FullName}");
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                {
                    foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        Type? owner = r as Type ?? r.DeclaringType;
                        if (owner is null) continue;
                        if (!Allowed(owner)) violations.Add($"{t.Name}.{m.Name} → {owner.FullName}::{r.Name}");
                        else if (Outermost(owner).Namespace == "StockManager.Engines.Arena") queue.Enqueue(owner);
                    }
                }
            }
            Assert.True(violations.Count == 0, "리플레이가 허용목록 밖 형식(브로커/주문 전송 가능성)을 참조:\n" + string.Join("\n", violations.Distinct()));
            Assert.Contains(typeof(ArenaTickReplayEngine), seen);
            // 검수된 의존(QuantSignalProvider) 자체도 브로커/디스패처 필드 0 — AI 결정은 IDecisionSink 로만 나간다
            foreach (var f in typeof(QuantSignalProvider).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                Assert.DoesNotMatch("Broker|Dispatch|Order|Router|Egress", f.FieldType.Name);
        }

        [Fact]
        public void Runtime_Object_Graph_After_Full_Replay_Reaches_No_Broker()
        {
            var e = Manual(Tape(5_000, SyntheticTickSource.DefaultIntervalTicks));
            e.SetSpeed(ArenaReplaySpeed.Max);
            e.Play(Capital);
            e.Arena.SubmitHuman(5930, 1, 1);
            do e.Pump(_now); while (e.State == ArenaReplayState.Playing);
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(e);
            var bad = new List<string>();
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (!visited.Add(o)) continue;
                var t = o.GetType();
                if (o is Delegate dl) { if (dl.Target is { } target) stack.Push(target); continue; }
                if (Outermost(t) == typeof(ArenaTickReplayEngineTests)) continue;    // 테스트 시계 람다 캡처 대상
                if (!Allowed(t)) bad.Add(t.FullName!);
                if (t.IsArray) { if (!t.GetElementType()!.IsValueType) foreach (var x in (Array)o) if (x is not null) stack.Push(x); continue; }
                if (t.Namespace?.StartsWith("System", StringComparison.Ordinal) == true || t.Namespace?.StartsWith("Microsoft", StringComparison.Ordinal) == true) continue;
                for (var tt = t; tt is not null && tt != typeof(object); tt = tt.BaseType)
                    foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!f.FieldType.IsValueType && f.GetValue(o) is { } v) stack.Push(v);
            }
            Assert.True(bad.Count == 0, "리플레이 객체 그래프에 허용목록 밖 인스턴스: " + string.Join(", ", bad.Distinct()));
            Assert.Contains(visited, o => o is QuantSignalProvider);
            Assert.False(visited.OfType<QuantSignalProvider>().Single().Anomaly.Tripped);
        }

        [Fact]
        public void Host_Wiring_And_Shell_Ratchet()
        {
            string form = File.ReadAllText(DiContractScanner.FindRepoFile("StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("dashHub.AttachArenaReplay(arenaReplay);", form);
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaReplayPort>(arenaReplay);", form);
            Assert.Contains("ShutdownTrace.Step(\"arenaReplay\", arenaReplay.Dispose)", form);
            Assert.DoesNotContain("StockManager.Simulation", form);
            Assert.True(File.Exists(DiContractScanner.FindRepoFile("StockManager", "Engines", "Arena", "ArenaTickReplayEngine.cs")));
            string view = File.ReadAllText(DiContractScanner.FindRepoFile("MS2E.UI.Portable", "Pages", "Trading", "ArenaBattleView.razor"));
            Assert.Contains("@inject IArenaReplayPort Replay", view);
            Assert.True(PureShellFitnessSentinel.Scan(typeof(DashboardSnapshotHub).Assembly).WithinRatchet);
            var members = typeof(IArenaReplayPort).GetMembers().Select(m => m.Name).ToArray();
            Assert.DoesNotContain(members, n => n.Contains("Arm", StringComparison.OrdinalIgnoreCase) || n.Contains("Broker", StringComparison.OrdinalIgnoreCase) || n.Contains("Live", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void Headless_Ui_Replay_Controls_Drive_Engine_And_Render_Progress()
        {
            var live = new ArenaMatchGovernor(() => _now);
            var e = Manual(Tape(20_000));
            using var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IArenaPort>(live);
            ctx.Services.AddSingleton<IArenaReplayPort>(e);
            string tapeDir = Path.Combine(Path.GetTempPath(), "ms2e_arena_ui_" + Guid.NewGuid().ToString("N"));
            using var recorder = new ArenaTapeRecorder(tapeDir, 1_000);
            ctx.Services.AddSingleton<IArenaTapePort>(recorder);
            var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
            Assert.Contains("매치 개시", cut.Markup);                                       // 기본 = 정규장 라이브
            Assert.Contains("틱 녹화 시작", cut.Markup);
            cut.FindAll("button.ar-tab")[1].Click();
            Assert.Contains("ar-progress", cut.Markup);
            cut.FindAll("button.ar-speed").Single(b => b.TextContent == "100x").Click();
            Assert.Equal(100, e.Status.Speed);
            cut.FindAll("button.ar-btn.go").Single(b => b.TextContent.Contains("시작")).Click();                                  // ▶ 시작
            e.Pump(_now);
            _now += Sec; e.Pump(_now);
            cut.Render();
            Assert.Equal(1 + 1_000, e.Status.Cursor);
            Assert.Contains("틱 1,001 / 20,000", cut.Markup);
            Assert.Contains("재생 중", cut.Markup);
            cut.FindAll("button.ar-btn.stop").Single().Click();                                // ⏸
            e.Pump(_now);
            cut.Render();
            Assert.Equal(ArenaReplayState.Paused, e.Status.State);
            Assert.Contains("재개", cut.Markup);
            Assert.Equal(ArenaState.Idle, live.State);                                         // 라이브 원장 무간섭
            Assert.False(recorder.Recording);
            try { Directory.Delete(tapeDir, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }
}
