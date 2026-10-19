// ============================================================================
// ArenaUIStressTests — [v12.18.24-DEV] /arena HMI 렌더 펌프 · 핫키 입력 지연 헤드리스 스트레스(bUnit)
//
//  §1 렌더 펌프 — 호가 1,000+ TPS(별도 생산 스레드) + 정산 Step 고빈도 중 뷰 렌더 횟수 ≤ 키 입력 렌더 + 경과/50ms + 여유(20 FPS 상한) ·
//     렌더 펌프가 실제로 갱신을 그림(≥ 1) · 마지막 게시 버전이 결국 그려짐(후행 유실 0) · 변경 없으면 렌더 0
//  §2 입력 지연 — 위 부하 중 S(무포지션 = 숏) → B(숏 = 커버) → S → Space(비상 양방향 청산) 순환 120회:
//     ① 키다운 디스패치(핸들러 + 재렌더 포함) → 포트 접수 = 벽시계 p99 ≤ 16ms ② 접수 → 상태 전이 = 바로 다음 정산 Step 1회(결정론 · 지연 0 설정) —
//     Step 실측 p99 ≤ 16ms · 매 회차 휴먼 포커스 방향(FLAT/SHORT) 기대 전이 일치
//     ★정직: 운용 종단 지연 = ① + 정산 펌프 주기(≤ 50ms) + 모의 체결 지연(30~50ms · 현실성 설계) — 이 테스트는 ① 과 '1 Step' 을 분리 계측한다.
//  §3 엔진 δ=0 — UI 부하 중 정산 스레드 Step 구간 관리 힙 할당 0B(정산 스레드 계수 · 렌더러 할당은 다른 스레드/구간)
//  §4 누수 0 — 뷰 50회 마운트/해제 → 전체 GC 후 컴포넌트 약참조 전부 회수(활성 타이머 = GC 루트 → 해제 누락 시 회수 실패로 검출)
//  §5 Fail-Closed — 스트레스 전 구간 포트 외 송출 수단 0(IArenaPort 계약 멤버에 브로커/무장/실주문 부재) · 주문 유실 0
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Blazor
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaUIStressTests
    {
        private const long Sym = 5930;
        private readonly ITestOutputHelper _out;
        public ArenaUIStressTests(ITestOutputHelper o) => _out = o;

        private static long Now() => DateTime.UtcNow.Ticks;

        private static OrderBookPayload Book(long ask, long bid, long qty = 100_000)
        {
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * 100; b.AskQty[l] = qty;
                b.BidPrice[l] = bid - l * 100; b.BidQty[l] = qty;
            }
            return b;
        }

        /// <summary>포트 데코레이터 — 휴먼 명령이 포트에 '도달한' 순간(Stopwatch)을 찍는다(키다운 → 핸들러 → 포트 접수 구간만 계측 · 뒤따르는 재렌더 제외).</summary>
        private sealed class TimedPort : IArenaPort
        {
            private readonly IArenaPort _p;
            public long LastAcceptTs;
            public TimedPort(IArenaPort p) => _p = p;
            private int Mark(int r) { Volatile.Write(ref LastAcceptTs, Stopwatch.GetTimestamp()); return r; }
            public bool Pumped => _p.Pumped;
            public long Version => _p.Version;
            public bool TryRead(out ArenaSnapshot snapshot) => _p.TryRead(out snapshot);
            public int CopyEquity(int side, Span<long> destination) => _p.CopyEquity(side, destination);
            public bool Start(long capitalWon) => _p.Start(capitalWon);
            public void Stop() => _p.Stop();
            public int SubmitHuman(long symbol, int side, long qty) => Mark(_p.SubmitHuman(symbol, side, qty));
            public int SubmitHumanLimit(long symbol, int side, long qty, long priceWon) => Mark(_p.SubmitHumanLimit(symbol, side, qty, priceWon));
            public int SubmitHumanIceberg(long symbol, int side, long qty, long priceWon, long peakQty) => Mark(_p.SubmitHumanIceberg(symbol, side, qty, priceWon, peakQty));
            public int CancelHuman() => Mark(_p.CancelHuman());
            public int FlattenHuman() => Mark(_p.FlattenHuman());
            public void SetFocus(long symbol) => _p.SetFocus(symbol);
            public bool TryReadDepth(out ArenaDepth depth) => _p.TryReadDepth(out depth);
            public bool TryReadDebrief(int side, out ArenaDebrief debrief) => _p.TryReadDebrief(side, out debrief);
            public bool SetPersona(int persona) => _p.SetPersona(persona);
        }

        private sealed class Harness : IDisposable
        {
            public readonly ArenaMatchGovernor Gov;
            public readonly TimedPort Port;
            public readonly ArenaTickReplayEngine Replay;
            public readonly ArenaTapeRecorder Tapes;
            public readonly Bunit.TestContext Ctx;
            private readonly string _dir;
            public Harness()
            {
                Gov = new ArenaMatchGovernor(Now, 1, latencyMinTicks: 0, latencyMaxTicks: 0, riskLimitBp: 0);   // 지연 0 = 접수 다음 Step 에 결정론 체결
                _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_hmi_" + Guid.NewGuid().ToString("N"));
                Replay = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(100), ownThread: false);
                Tapes = new ArenaTapeRecorder(_dir, 10);
                Ctx = new Bunit.TestContext();
                Port = new TimedPort(Gov);
                Ctx.Services.AddSingleton<IArenaPort>(Port);
                Ctx.Services.AddSingleton<IArenaReplayPort>(Replay);
                Ctx.Services.AddSingleton<IArenaTapePort>(Tapes);
                Ctx.Services.AddSingleton<IKisFeedLatencyPort>(new StockManager.Adapters.Kis.KisTimeSkewGuard());
                Assert.True(Gov.Start(100_000_000));
                Gov.Step(Now());
                Gov.OnQuote(Book(70_000, 69_900));
                Gov.Step(Now());
            }
            /// <summary>마운트 → 포커스 설정(SyncFocus) 뒤 정산 1회 = 래더 투영 게시 → 래더 렌더 대기.</summary>
            public IRenderedComponent<ArenaBattleView> Mount()
            {
                var cut = Ctx.RenderComponent<ArenaBattleView>();
                cut.WaitForAssertion(() => { Gov.Step(Now()); Assert.NotEmpty(cut.FindAll("td.px")); }, TimeSpan.FromSeconds(10));
                return cut;
            }
            public void Dispose()
            {
                Ctx.Dispose(); Tapes.Dispose(); Replay.Dispose();
                try { System.IO.Directory.Delete(_dir, true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static ArenaSnapshot Snap(ArenaMatchGovernor g) { Assert.True(g.TryRead(out var s)); return s; }

        private static long P99(List<double> xs) { var a = xs.OrderBy(x => x).ToArray(); return (long)Math.Ceiling(a[(int)Math.Min(a.Length - 1, Math.Ceiling(a.Length * 0.99) - 1)] * 1000); }

        // 1,000+ TPS 호가 생산자(벽시계 기준 부족분 보충 · 가격 ±1틱 진동 = 매 Step 버전 변화)
        private static Thread QuoteProducer(ArenaMatchGovernor g, CancellationToken ct, long[] produced)
        {
            var t = new Thread(() =>
            {
                var up = Book(70_000, 69_900, 100_000); var dn = Book(70_000, 69_900, 100_001);   // 가격 고정 · 잔량만 변화(지정가 핫키 결정론 · 게시 버전은 매 Step 증가)
                var sw = Stopwatch.StartNew();
                long n = 0, atMs = 0;
                while (!ct.IsCancellationRequested)
                {
                    long el = sw.ElapsedTicks;
                    long due = el * 1_000 / Stopwatch.Frequency + 1;                  // 목표 1,000/s
                    while (n < due) { g.OnQuote((n & 1) == 0 ? up : dn); n++; }
                    atMs = el * 1_000 / Stopwatch.Frequency;                          // 측정 경계 = 마지막 보충 시점(취소 후 경과 미포함)
                    Thread.Yield();
                }
                Volatile.Write(ref produced[0], n);
                Volatile.Write(ref produced[1], Math.Max(1, atMs));
            }) { IsBackground = true, Name = "hmi-quotes" };
            t.Start();
            return t;
        }

        // ════════════ §1 · §2 · §3 · §5 ════════════

        [Fact]
        public async Task HighFrequencyQuotes_Hotkeys_S_B_Space_InputLatency_Within_OneFrame_EngineZeroAlloc()
        {
            using var h = new Harness();
            var g = h.Gov;
            using var cts = new CancellationTokenSource();
            var produced = new long[2];
            var cut = h.Mount();
            var producer = QuoteProducer(g, cts.Token, produced);

            // 순환: S = 숏 진입/추가 → B = 숏커버(비중 수량 · 숏 감소) → S = 숏 추가 → Space = 비상 양방향 청산(전량 커버 = FLAT)
            string[] cycle = { "KeyS", "KeyB", "KeyS", "Space" };
            var dispatchMs = new List<double>(); var stepMs = new List<double>(); var acceptMs = new List<double>();
            long stepAlloc = 0, rendersAtStart = cut.RenderCount, mismatches = 0;
            int keys = 0;
            var wall = Stopwatch.StartNew();
            for (int i = 0; i < 120; i++)
            {
                for (int k = 0; k < 4; k++)                                          // 키 사이 고빈도 정산(호가 소비 · 게시 버전 증가)
                {
                    long a = GC.GetAllocatedBytesForCurrentThread();
                    g.Step(Now());
                    stepAlloc += GC.GetAllocatedBytesForCurrentThread() - a;
                    await Task.Delay(1);
                }
                string code = cycle[i % cycle.Length];
                Assert.True(g.TryReadDepth(out var before));
                long t0 = 0;
                await cut.InvokeAsync(() =>
                {
                    var el = cut.Find(".ar-console");                               // 하네스 비용(bUnit 마크업 → DOM 파싱)은 계측 밖
                    t0 = Stopwatch.GetTimestamp();                                   // ★키 이벤트 디스패치 순간
                    el.KeyDown(new KeyboardEventArgs { Code = code, Key = code == "Space" ? " " : "x" });
                });
                long t1 = Stopwatch.GetTimestamp();
                keys++;
                acceptMs.Add((Volatile.Read(ref h.Port.LastAcceptTs) - t0) * 1000.0 / Stopwatch.Frequency);
                long a2 = GC.GetAllocatedBytesForCurrentThread();
                g.Step(Now());                                                       // ② 접수 → 상태 전이 = 바로 다음 Step 1회
                long t2 = Stopwatch.GetTimestamp();
                stepAlloc += GC.GetAllocatedBytesForCurrentThread() - a2;
                dispatchMs.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
                stepMs.Add((t2 - t1) * 1000.0 / Stopwatch.Frequency);
                Assert.True(g.TryReadDepth(out var after));
                bool ok = code switch
                {
                    "KeyS" => after.PositionQty < before.PositionQty && after.PositionQty < 0,             // 숏 진입/추가
                    "KeyB" => after.PositionQty > before.PositionQty && after.PositionQty <= 0,            // 숏 감소(커버 · 0 교차 없음)
                    _ => after.PositionQty == 0 && Snap(g).HumanFocusSide == ArenaPositionSide.Flat,       // Space = 전량
                };
                if (!ok) mismatches++;
            }
            wall.Stop();
            cts.Cancel();
            Assert.True(producer.Join(5_000));

            // 후행 유실 0: 부하 종료 후 마지막 게시 버전이 결국 그려진다
            long lastVer = g.Version;
            cut.WaitForAssertion(() => Assert.Contains("[FLAT]", cut.Markup), TimeSpan.FromSeconds(5));
            var view = cut.Instance;
            long renders = cut.RenderCount - rendersAtStart;
            double tps = produced[0] * 1000.0 / Math.Max(1, produced[1]);
            long p99Dispatch = P99(dispatchMs), p99Step = P99(stepMs), p99Accept = P99(acceptMs);
            var renderMs = new List<double>();                                       // 뷰 1회 렌더 비용(bUnit = 렌더 트리 diff + AngleSharp DOM 갱신 — WebView 보다 무거움)
            for (int r = 0; r < 40; r++) { long a = Stopwatch.GetTimestamp(); cut.Render(); renderMs.Add((Stopwatch.GetTimestamp() - a) * 1000.0 / Stopwatch.Frequency); }
            _out.WriteLine($"[HMI] 호가 {produced[0]:N0}건/{produced[1]:N0}ms = {tps:N0} TPS · 키 {keys} · 경과 {wall.ElapsedMilliseconds:N0}ms · 렌더 {renders}(펌프 요청 {view.PumpRenders} · 진행 중 건너뜀 {view.PumpSkippedInFlight})");
            _out.WriteLine($"[HMI] ① 키다운→포트 접수 p50 {acceptMs.OrderBy(x => x).ElementAt(acceptMs.Count / 2):F2}ms · p99 {p99Accept / 1000.0:F2}ms · 최대 {acceptMs.Max():F2}ms");
            _out.WriteLine($"[HMI] ①' 키다운→핸들러+재렌더 완료 p50 {dispatchMs.OrderBy(x => x).ElementAt(dispatchMs.Count / 2):F2}ms · p99 {p99Dispatch / 1000.0:F2}ms · 최대 {dispatchMs.Max():F2}ms");
            _out.WriteLine($"[HMI] 뷰 1회 렌더(bUnit) p50 {renderMs.OrderBy(x => x).ElementAt(renderMs.Count / 2):F2}ms · 최대 {renderMs.Max():F2}ms");
            _out.WriteLine($"[HMI] ② 접수→상태 전이(Step 1회) p50 {stepMs.OrderBy(x => x).ElementAt(stepMs.Count / 2):F3}ms · p99 {p99Step / 1000.0:F3}ms · 정산 스레드 Step 할당 {stepAlloc}B · 전이 불일치 {mismatches}");
            var s = Snap(g);
            _out.WriteLine($"[HMI] 휴먼 체결 {s.Human.Fills} · 거부 {s.Human.Rejects} · 주문 유실 {s.OrdersLost} · 호가 유실 {s.QuotesLost} · 마지막 버전 {lastVer}");

            Assert.True(tps >= 1_000, $"호가 생산 {tps:N0} TPS < 1,000");
            Assert.True(p99Accept <= 16_000, $"① 키다운→포트 접수 p99 {p99Accept / 1000.0:F2}ms > 16ms");
            Assert.True(p99Step <= 16_000, $"② 접수→상태 전이 p99 {p99Step / 1000.0:F3}ms > 16ms");
            Assert.Equal(0, mismatches);                                             // ★매 키 = 다음 Step 에서 기대 방향 전이(S 숏 · B 커버 · Space 청산)
            Assert.Equal(0, stepAlloc);                                              // ★UI 부하 중 엔진 정산 δ=0
            Assert.Equal(0, s.OrdersLost);
            Assert.True(s.Human.Fills >= keys, $"체결 {s.Human.Fills} < 키 {keys}");
            Assert.Equal(ArenaPositionSide.Flat, s.HumanFocusSide);
            // 20 FPS 상한: 렌더 ≤ 키 이벤트 렌더 + 경과/50ms + 여유 8 · 펌프가 실제로 그림
            long cap = keys + wall.ElapsedMilliseconds / ArenaBattleView.RenderPumpMs + 8;
            Assert.True(renders <= cap, $"렌더 {renders} > 상한 {cap}(20 FPS 스로틀 위반)");
            Assert.True(view.PumpRenders >= 1, "렌더 펌프 무동작");
        }

        [Fact]
        public void NoChange_NoRender_ThenSingleChange_RendersOnce_Eventually()
        {
            using var h = new Harness();
            var cut = h.Mount();
            Thread.Sleep(300);                                                       // 초기 정착
            int before = cut.RenderCount;
            long pumpBefore = cut.Instance.PumpRenders;
            Thread.Sleep(400);                                                       // 변경 0 → 8회 폴링 = 렌더 0
            Assert.Equal(before, cut.RenderCount);
            Assert.Equal(pumpBefore, cut.Instance.PumpRenders);
            h.Gov.OnQuote(Book(71_000, 70_900));
            h.Gov.Step(Now());                                                       // 버전 1 증가
            cut.WaitForAssertion(() => Assert.Contains("71,000", cut.Markup), TimeSpan.FromSeconds(5));
            Assert.InRange(cut.Instance.PumpRenders - pumpBefore, 1, 2);
        }

        // ════════════ §4 누수 0 ════════════

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference MountAndDispose(Harness h)
        {
            var cut = h.Ctx.RenderComponent<ArenaBattleView>();
            var wr = new WeakReference(cut.Instance);
            h.Ctx.DisposeComponents();                                               // 컴포넌트 Dispose → 타이머 해제
            return wr;
        }

        [Fact]
        public void MountDispose_50Times_AllViewsCollected_TimersReleased()
        {
            using var h = new Harness();
            var refs = new List<WeakReference>();
            for (int i = 0; i < 50; i++) refs.Add(MountAndDispose(h));
            for (int r = 0; r < 3; r++) { GC.Collect(2, GCCollectionMode.Forced, true, true); GC.WaitForPendingFinalizers(); }
            int alive = refs.Count(w => w.IsAlive);
            _out.WriteLine($"[HMI] 마운트/해제 50 · 생존 {alive}");
            Assert.Equal(0, alive);                                                  // 활성 타이머는 GC 루트 — 해제 누락이면 생존
        }

        // ════════════ §5 Fail-Closed ════════════

        [Fact]
        public void ArenaPort_Surface_Has_No_Broker_Arming_Or_Live_Members()
        {
            var names = typeof(IArenaPort).GetMembers().Select(m => m.Name).ToArray();
            Assert.DoesNotContain(names, n => n.Contains("Broker", StringComparison.OrdinalIgnoreCase) || n.Contains("Arm", StringComparison.OrdinalIgnoreCase)
                                           || n.Contains("Live", StringComparison.OrdinalIgnoreCase) || n.Contains("Dispatch", StringComparison.OrdinalIgnoreCase));
            var injected = typeof(ArenaBattleView).GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .Where(p => p.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.InjectAttribute), false).Length > 0).Select(p => p.PropertyType).ToArray();
            Assert.All(injected, t => Assert.Equal("MS2E.UI.Portable.Ports.Trading", t.Namespace));   // 뷰 주입 = 포트 계약만
        }
    }
}
