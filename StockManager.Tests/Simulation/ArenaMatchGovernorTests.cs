// ============================================================================
// ArenaMatchGovernorTests — [v12.18.11-DEV] AI vs Human 듀얼 북 시뮬레이터 적대적 검증
//   §1 체결 정합: 동일 가격 진입(수수료 0.015%) → 매도(거래세 0.18%) 실현손익 · 평가 · 리더 정확값 / 호가 순회 슬리피지 · 부분 체결
//   §2 거부 · 상태 머신: 호가 미수신 · 보유 초과 매도 · 현금 부족 · 미진행 접수 · 종료 후 동결 · 재개시 초기화 · MDD
//   §3 δ=0: 호가 적재 · 휴먼/AI 주문 · 정산 Step · 스냅샷/자산곡선 판독 힙 0B(Release · MinAllocated 3창)
//   §4 Fail-Closed: 아레나 형식 IL 참조 허용목록(브로커/주문 전송 형식 0) + 런타임 객체 그래프 도달 형식 검사 + 허브 결정 분기 · 호스트 셸 래칫
//   [v12.18.13-DEV] 이 파일은 체결 지연 0 구성(즉시 판단)으로 v12.18.11 원장 의미를 보존 — 지연 · 대기열 · 이월은 MicrostructureExecutionTests.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines.Arena;
using Xunit;

namespace StockManager.Tests.Simulation
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaMatchGovernorTests
    {
        private const long Sym = 5930;
        private long _now = 638_000_000_000_000_000;
        private long Now() => _now;

        private ArenaMatchGovernor NewRunning(long capital = 10_000_000, int aiLotBp = 700, int riskLimitBp = ArenaMatchGovernor.DefaultRiskLimitBp)
        {
            var g = new ArenaMatchGovernor(Now, equitySampleEverySteps: 1, aiLotBp: aiLotBp, latencyMinTicks: 0, latencyMaxTicks: 0, riskLimitBp: riskLimitBp);
            Assert.True(g.Start(capital));
            Step(g);
            Assert.Equal(ArenaState.Running, g.State);
            return g;
        }

        private void Step(ArenaMatchGovernor g) { _now += 500_000; g.Step(_now); }

        private static OrderBookPayload Book(long symbol, long ask, long askQty, long bid, long bidQty, long tick = 100)
        {
            var b = new OrderBookPayload { SymbolCode = symbol };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * tick; b.AskQty[l] = askQty;
                b.BidPrice[l] = bid - l * tick; b.BidQty[l] = bidQty;
            }
            return b;
        }

        private static ArenaSnapshot Read(ArenaMatchGovernor g)
        {
            Assert.True(g.TryRead(out var s));
            return s;
        }

        // ═══ §1 체결 정합 ═══

        [Fact]
        public void Fee_And_Tax_Truncate_To_Won()
        {
            Assert.Equal(1_500, DualBookSimulator.Fee(10_000_000));       // 0.015%
            Assert.Equal(18_000, DualBookSimulator.Tax(10_000_000));      // 0.18%
            Assert.Equal(106, DualBookSimulator.Fee(710_000));            // 106.5 → 절사
            Assert.Equal(1_278, DualBookSimulator.Tax(710_000));
            Assert.Equal(1_500_000_000_000L, DualBookSimulator.Fee(10_000_000_000_000_000));   // Int128 중간값(오버플로 0)
        }

        [Fact]
        public void Same_Price_Entry_Books_Equal_Then_Fee_And_Tax_Adjusted_Pnl()
        {
            var g = NewRunning();
            g.OnQuote(Book(Sym, 70_000, 1_000, 69_900, 1_000));
            Step(g);
            // AI 예산 7% = 700,000 ÷ 70,000 = 10주 · 휴먼 10주 — 같은 호가에서 같은 가격
            g.OnAiDecision(DashDecisionKind.Entry, Sym);
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, 1, 10));
            Step(g);
            var s = Read(g);
            foreach (var st in new[] { s.Ai, s.Human })
            {
                Assert.Equal(1, st.Fills);
                Assert.Equal(10_000_000 - 700_000 - 105, st.CashWon);    // 수수료 105원(0.015%)
                Assert.Equal(105, st.FeesWon);
                Assert.Equal(0, st.SlippageWon);
                Assert.Equal(9_299_895 + 699_000, st.EquityWon);          // 평가 = 최우선 매수호가 69,900
                Assert.Equal(699_000 - 700_105, st.UnrealizedWon);
            }
            Assert.Equal(ArenaLeader.Draw, s.Leader);

            // 호가 상승 → 휴먼만 청산
            g.OnQuote(Book(Sym, 71_100, 1_000, 71_000, 1_000));
            Assert.Equal(ArenaReject.None, g.SubmitHuman(Sym, -1, 10));
            Step(g);
            s = Read(g);
            Assert.Equal(710_000 - 106 - 1_278 - 700_105, s.Human.RealizedWon);   // 8,511원
            Assert.Equal(1_278, s.Human.TaxWon);
            Assert.Equal(1, s.Human.Wins);
            Assert.Equal(0, s.Human.OpenPositions);
            Assert.Equal(9_299_895 + 710_000 - 106 - 1_278, s.Human.EquityWon);
            Assert.Equal(9_299_895 + 710_000, s.Ai.EquityWon);                // 미청산 = 매도 비용 미차감(정직한 평가)
            Assert.Equal(ArenaLeader.Ai, s.Leader);
            Assert.Equal(106 + 1_278, s.LeadWon);

            g.OnAiDecision(DashDecisionKind.Exit, Sym);
            Step(g);
            s = Read(g);
            Assert.Equal(s.Human.EquityWon, s.Ai.EquityWon);                   // 같은 가격 청산 = 원 단위 동일
            Assert.Equal(s.Human.RealizedWon, s.Ai.RealizedWon);
            Assert.Equal(ArenaLeader.Draw, s.Leader);
            Assert.Equal(2, s.AiSignals);
        }

        [Fact]
        public void Market_Order_Walks_Depth_With_Slippage_And_Ioc_Partial()
        {
            var g = NewRunning();
            g.OnQuote(Book(Sym, 10_000, 10, 9_990, 10, tick: 10));             // 매도 10,000·10,010… 각 10주
            Step(g);
            g.SubmitHuman(Sym, 1, 15);
            Step(g);
            var h = Read(g).Human;
            Assert.Equal(15, g.Simulator.Book(ArenaSide.Human).Position(Sym));
            Assert.Equal(5 * 10, h.SlippageWon);                                // 5주 × (10,010 − 10,000)
            g.SubmitHuman(Sym, 1, 500);                                         // [v12.18.13] 같은 스냅샷 = 앞 주문이 소진한 15주 제외 85주만(충격) · 잔량 415 이월
            Step(g);
            Assert.Equal(100, g.Simulator.Book(ArenaSide.Human).Position(Sym));
            Assert.Equal(1, Read(g).Human.WorkingOrders);
            g.OnQuote(Book(Sym, 10_000, 10, 9_990, 10, tick: 10));             // 새 스냅샷 = 잔량 재순회(분할 체결)
            Step(g);
            Assert.Equal(200, g.Simulator.Book(ArenaSide.Human).Position(Sym));
        }

        // ═══ §2 거부 · 상태 머신 ═══

        [Fact]
        public void Rejects_Are_Classified_And_Counted()
        {
            var g = new ArenaMatchGovernor(Now, 1, latencyMinTicks: 0, latencyMaxTicks: 0);
            Assert.Equal(ArenaReject.NotRunning, g.SubmitHuman(Sym, 1, 1));
            Assert.False(g.Start(999_999));
            Assert.False(g.Start(10_000_000_001));
            Assert.True(g.Start(1_000_000));
            Step(g);
            Assert.Equal(ArenaReject.Invalid, g.SubmitHuman(Sym, 0, 1));
            Assert.Equal(ArenaReject.Invalid, g.SubmitHuman(Sym, 1, 0));
            Assert.Equal(ArenaReject.Invalid, g.SubmitHuman(-1, 1, 1));

            g.SubmitHuman(Sym, 1, 1);                                           // 호가 없음
            Step(g);
            Assert.Equal(ArenaReject.NoQuote, Read(g).Human.LastReject);
            g.OnQuote(Book(Sym, 70_000, 1_000, 69_900, 1_000));
            g.SubmitHuman(Sym, -1, 100);                                        // [v12.18.23-DEV] 가상 공매도 — 증거금 50%(349.5만) > 현금 100만 = 거부
            Step(g);
            Assert.Equal(ArenaReject.InsufficientCash, Read(g).Human.LastReject);
            g.SubmitHuman(Sym, 1, 15);                                          // 1,050,158원 > 100만 → 전량 거부(축소 0)
            Step(g);
            var h = Read(g).Human;
            Assert.Equal(ArenaReject.InsufficientCash, h.LastReject);
            Assert.Equal(3, h.Rejects);
            Assert.Equal(0, h.Fills);
            Assert.Equal(1_000_000, h.CashWon);
        }

        [Fact]
        public void State_Machine_Freezes_On_Stop_And_Restart_Resets_Books()
        {
            var g = NewRunning();
            g.OnQuote(Book(Sym, 70_000, 1_000, 69_900, 1_000));
            g.SubmitHuman(Sym, 1, 10);
            Step(g);
            g.Stop();
            g.SubmitHuman(Sym, 1, 10);                                          // 같은 회차: Stop 이 먼저 반영 → 폐기
            Step(g);
            var s = Read(g);
            Assert.Equal(ArenaState.Finished, s.State);
            Assert.Equal(1, s.Human.Fills);
            long frozen = s.Human.EquityWon;
            g.OnQuote(Book(Sym, 90_000, 1_000, 89_900, 1_000));                 // 종료 후 시세 변동 = 평가 동결
            Step(g);
            Assert.Equal(frozen, Read(g).Human.EquityWon);
            Assert.Equal(ArenaReject.NotRunning, g.SubmitHuman(Sym, 1, 1));
            g.OnAiDecision(DashDecisionKind.Entry, Sym);                        // 미진행 = 적재 0

            Assert.True(g.Start(20_000_000));
            Step(g);
            s = Read(g);
            Assert.Equal(ArenaState.Running, s.State);
            Assert.Equal(20_000_000, s.CapitalWon);
            Assert.Equal(20_000_000, s.Human.CashWon);
            Assert.Equal(0, s.Human.Fills + s.Ai.Fills + s.AiSignals);
            Assert.Equal(0, g.Simulator.Book(ArenaSide.Human).Position(Sym));
        }

        [Fact]
        public void Drawdown_Tracks_Peak_To_Trough_And_Equity_Curve_Is_Chronological()
        {
            var g = NewRunning();
            g.OnQuote(Book(Sym, 70_000, 1_000, 69_900, 1_000));
            g.SubmitHuman(Sym, 1, 100);
            Step(g);
            g.OnQuote(Book(Sym, 72_100, 1_000, 72_000, 1_000));
            Step(g);
            long peak = Read(g).Human.EquityWon;
            g.OnQuote(Book(Sym, 68_100, 1_000, 68_000, 1_000));
            Step(g);
            var h = Read(g).Human;
            Assert.Equal(peak, h.PeakWon);
            Assert.Equal(100 * 4_000, h.MddWon);
            Assert.Equal((int)(400_000L * 10_000 / peak), h.MddBp);
            var curve = new long[ArenaMatchGovernor.MaxEquity];
            int n = g.CopyEquity(ArenaSide.Human, curve);
            Assert.True(n >= 3);
            Assert.Equal(h.EquityWon, curve[n - 1]);
            Assert.Equal(peak, curve[n - 2]);
        }

        // ═══ §3 δ=0 ═══

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
        public void HotPath_Quote_Orders_Step_And_Snapshot_Read_Are_Zero_Alloc()
        {
            var g = NewRunning(capital: 10_000_000_000, aiLotBp: 10);
            var quote = Book(Sym, 70_000, 1_000_000, 69_900, 1_000_000);
            var curve = new long[ArenaMatchGovernor.MaxEquity];
            long checksum = 0;
            void Loop()
            {
                for (int i = 0; i < 2_000; i++)
                {
                    quote.BidPrice[0] = 69_900 + (i & 7);
                    g.OnQuote(in quote);
                    g.SubmitHuman(Sym, (i & 1) == 0 ? 1 : -1, 3);
                    g.OnAiDecision((i & 1) == 0 ? DashDecisionKind.Entry : DashDecisionKind.Exit, Sym);
                    _now += 500_000;
                    g.Step(_now);
                    if (g.TryRead(out var s)) checksum += s.Version;
                    checksum += g.CopyEquity(i & 1, curve);
                }
            }
            Loop();                                                              // 워밍업(티어드 JIT · 정적 초기화)
            Assert.Equal(0, MinAllocated(Loop));
            var snap = Read(g);
            Assert.True(snap.Human.Fills > 1_000 && snap.Ai.Fills > 1_000, $"human={snap.Human.Fills} ai={snap.Ai.Fills}");
            Assert.True(checksum > 0);
        }

        [Fact]
        public void Concurrent_Readers_Never_Observe_Torn_Snapshot()
        {
            var g = NewRunning(riskLimitBp: 0);                                  // [v12.18.18-DEV] 휴먼 전용 −3% 동결 = 대칭 불변식 밖(동결 경로는 HyperRealismTests)
            g.OnQuote(Book(Sym, 70_000, 1_000_000, 69_900, 1_000_000));
            using var stop = new CancellationTokenSource();
            int torn = 0, reads = 0;
            using var joined = new CountdownEvent(2);
            for (int r = 0; r < 2; r++)
            {
                new Thread(() =>
                {
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            if (!g.TryRead(out var s)) continue;
                            Interlocked.Increment(ref reads);
                            // 불변식: 양측이 회차마다 같은 가격 · 같은 수량(10주)을 대칭 집행 — 찢긴 사본이면 두 진영 값이 어긋난다
                            if (s.Ai.Fills != s.Human.Fills || s.Ai.CashWon != s.Human.CashWon || s.Ai.EquityWon != s.Human.EquityWon
                                || s.Ai.FeesWon != s.Human.FeesWon || s.Version <= 0) Interlocked.Increment(ref torn);
                        }
                    }
                    finally { joined.Signal(); }
                }) { IsBackground = true }.Start();
            }
            for (int i = 0; i < 20_000; i++)
            {
                // AI 예산 7% ÷ 70,000 = 10주와 대칭. [v12.18.23-DEV] 휴먼 매도는 롱 보유 시에만(무보유 매도 = 가상 공매도 → 시그널 AI 청산 무동작과 비대칭)
                if ((i & 1) == 0 || Read(g).Human.OpenPositions > 0) g.SubmitHuman(Sym, (i & 1) == 0 ? 1 : -1, 10);
                g.OnAiDecision((i & 1) == 0 ? DashDecisionKind.Entry : DashDecisionKind.Exit, Sym);
                Step(g);
            }
            stop.Cancel();
            Assert.True(joined.Wait(TimeSpan.FromSeconds(10)));
            Assert.True(reads > 0);
            Assert.Equal(0, torn);
            var fin = Read(g);                                                  // 왕복 비용(스프레드 · 수수료 · 세금)으로 현금 소진 후 양측 동일 거부
            Assert.Equal(fin.Human.Fills, fin.Ai.Fills);
            Assert.True(fin.Human.Fills > 1_000 && fin.Human.Rejects > 0, $"fills={fin.Human.Fills} rejects={fin.Human.Rejects}");
        }

        // ═══ §4 Fail-Closed(브로커 송출 0) ═══

        private static readonly Type[] ArenaTypes =
        {
            typeof(ArenaMatchGovernor), typeof(DualBookSimulator), typeof(ArenaBook), typeof(ArenaOrder),
            typeof(ArenaSnapshot), typeof(ArenaSideStats), typeof(IArenaPort),
        };

        private static bool Allowed(Type t)
        {
            while (t.HasElementType) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition)
                return Allowed(t.GetGenericTypeDefinition()) && t.GetGenericArguments().All(Allowed);
            if (t.IsGenericParameter) return true;
            if (t == typeof(TelemetryRing<>) || t == typeof(OrderBookPayload) || t == typeof(OrderBookLevels10)) return true;
            if (t.FullName == "<PrivateImplementationDetails>") return true;                 // InlineArray 인덱서 컴파일러 도우미
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Threading", StringComparison.Ordinal) || ns.StartsWith("System.Runtime", StringComparison.Ordinal)
                || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arena" || ns == "StockManager.Engines.Arena.Strategy"
                || (t.DeclaringType is { } d && ArenaTypes.Contains(d));                     // 컴파일러 생성 중첩(람다 캐시 등)
        }

        [Fact]
        public void CompileTime_Arena_Il_References_Only_Allowlisted_Types()
        {
            var violations = new List<string>();
            var seen = new HashSet<Type>();
            var queue = new Queue<Type>(ArenaTypes.Concat(ArenaTypes.SelectMany(t => t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))));
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            while (queue.Count > 0)
            {
                var t = queue.Dequeue();
                if (!seen.Add(t)) continue;
                foreach (var f in t.GetFields(All))
                    if (!Allowed(f.FieldType)) violations.Add($"{t.Name}.{f.Name}: {f.FieldType.FullName}");
                foreach (var m in t.GetMethods(All).Cast<MethodBase>().Concat(t.GetConstructors(All)))
                {
                    foreach (var p in m.GetParameters()) if (!Allowed(p.ParameterType)) violations.Add($"{t.Name}.{m.Name}({p.ParameterType.FullName})");
                    if (m is MethodInfo mi && !Allowed(mi.ReturnType)) violations.Add($"{t.Name}.{m.Name} → {mi.ReturnType.FullName}");
                    foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        Type? owner = r as Type ?? r.DeclaringType;
                        if (owner is null) continue;
                        if (!Allowed(owner)) violations.Add($"{t.Name}.{m.Name} → {owner.FullName}::{r.Name}");
                        else if (owner.Namespace == "StockManager.Engines.Arena" && !owner.IsGenericParameter) queue.Enqueue(owner);
                    }
                }
            }
            Assert.True(violations.Count == 0, "아레나가 허용목록 밖 형식(브로커/주문 전송 가능성)을 참조:\n" + string.Join("\n", violations.Distinct()));
            Assert.Contains(typeof(ArenaMatchGovernor), seen);
            Assert.Contains(typeof(DualBookSimulator), seen);
        }

        [Fact]
        public void Runtime_Object_Graph_Reaches_No_Broker_After_Full_Match_Through_Hub()
        {
            var hub = new DashboardSnapshotHub(nowTicks: Now);
            var g = new ArenaMatchGovernor(Now, 1, aiLotBp: 700, latencyMinTicks: 0, latencyMaxTicks: 0);
            hub.AttachArena(g);
            Assert.True(g.Start(10_000_000));
            Step(g);
            g.OnQuote(Book(Sym, 70_000, 1_000, 69_900, 1_000));
            // 시그널 엔진 → 허브 싱크(PublishDecision) → 아레나 AI 링 분기(진입/청산 · 관망/거부 무시)
            hub.PublishDecision(DashDecisionKind.Entry, Sym, 9_000, 100, 0, 0);
            hub.PublishDecision(DashDecisionKind.Hold, Sym, 9_000, 100, 0, 0);
            hub.PublishDecision(DashDecisionKind.Veto, Sym, 9_000, 100, 0, 0);
            g.SubmitHuman(Sym, 1, 5);
            Step(g);
            hub.PublishDecision(DashDecisionKind.Exit, Sym, 9_000, 100, 0, 0);
            g.SubmitHuman(Sym, -1, 5);
            Step(g);
            var s = Read(g);
            Assert.Equal(2, s.AiSignals);
            Assert.Equal(2, s.Ai.Fills);
            Assert.Equal(2, s.Human.Fills);

            // 런타임 도달 객체 전수(필드 재귀) — 허용목록 밖 형식 인스턴스 0 = 송출 객체에 닿을 참조 자체가 없음
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<object>();
            stack.Push(g);
            var bad = new List<string>();
            while (stack.Count > 0)
            {
                var o = stack.Pop();
                if (!visited.Add(o)) continue;
                var t = o.GetType();
                if (!Allowed(t) && !t.IsSubclassOf(typeof(Delegate))) bad.Add(t.FullName!);
                if (o is Delegate dl)
                {
                    if (dl.Target is { } target) stack.Push(target);                 // 시계 람다 캡처 대상까지 추적
                    continue;
                }
                if (t.IsArray)
                {
                    if (!t.GetElementType()!.IsValueType) foreach (var e in (Array)o) if (e is not null) stack.Push(e);
                    continue;
                }
                for (var tt = t; tt is not null && tt != typeof(object); tt = tt.BaseType)
                    foreach (var f in tt.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!f.FieldType.IsValueType && f.GetValue(o) is { } v) stack.Push(v);
            }
            bad.RemoveAll(n => n.StartsWith(typeof(ArenaMatchGovernorTests).FullName!, StringComparison.Ordinal));   // 테스트 시계 람다의 캡처 대상 = 테스트 자신
            Assert.True(bad.Count == 0, "아레나 객체 그래프에 허용목록 밖 인스턴스: " + string.Join(", ", bad.Distinct()));
            hub.Dispose();
        }

        [Fact]
        public void Host_Shell_Is_Inject_Free_And_Port_Is_Wired()
        {
            var host = typeof(DashboardSnapshotHub).Assembly;
            Type[] types;
            try { types = host.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t is not null).ToArray()!; }
            var shell = types.Single(t => t.GetCustomAttributes<Microsoft.AspNetCore.Components.RouteAttribute>().Any(a => a.Template == "/arena"));
            Assert.False(PureShellFitnessSentinel.IsHostCoupledUi(shell));               // Phase114 래칫 불변(≤ 40)
            Assert.True(PureShellFitnessSentinel.Scan(host).WithinRatchet);

            string form = File.ReadAllText(DiContractScanner.FindRepoFile("StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("dashHub.AttachArena(arena);", form);
            Assert.DoesNotContain("StockManager.Simulation", form);                             // 호스트 조립부 = 리허설 사슬 참조 0(격리 ⑥ 와 같은 규칙)
            Assert.All(ArenaTypes, t => Assert.NotEqual("StockManager.Simulation", t.Namespace));
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaPort>(arena);", form);
            Assert.Contains("bridge.OrderBookReceived += b => arena.OnQuote(in b);", form);
            string nav = File.ReadAllText(DiContractScanner.FindRepoFile("MS2E.UI.Portable", "Components", "NavMenu.razor"));
            Assert.Contains("new Item(\"⚔️\", \"AI vs Human\", \"/arena\", NavLinkMatch.All)", nav);
            string view = File.ReadAllText(DiContractScanner.FindRepoFile("MS2E.UI.Portable", "Pages", "Trading", "ArenaBattleView.razor"));
            Assert.Contains("@inject IArenaPort LiveArena", view);
            Assert.DoesNotContain(view.Split('\n'), l =>l.TrimStart().StartsWith("@page", StringComparison.Ordinal));
            // 포트 계약 = 명령 4종(개시 · 종료 · 휴먼 주문 · 판독) — 무장/브로커/실주문 멤버 부재
            var members = typeof(IArenaPort).GetMembers().Select(m => m.Name).ToArray();
            Assert.DoesNotContain(members, n => n.Contains("Arm", StringComparison.OrdinalIgnoreCase) || n.Contains("Broker", StringComparison.OrdinalIgnoreCase) || n.Contains("Live", StringComparison.OrdinalIgnoreCase));
        }
    }
}
