// ============================================================================
// UiAndDiAuditTests — [Phase 3] Blazor Hybrid UI/RCL 수명주기 누수 · DI 결속 · Fail-Closed UI 감사 (StockManager)
//
//   재현(Red → Green, 수정 전 실측):
//     · DualLiveBrokerControlPanel — 700ms 폴(스레드풀)이 보유종목 List 를 Clear/Add 하는 동안 렌더 스레드가 @foreach 열거
//                                    → "Collection was modified"(보유 ≥1건일 때 렌더 붕괴) · 폴 콜백 중첩 재진입
//     · Portable MainLayout        — @Body/시세 스트립 ErrorBoundary 부재(위젯 1개 예외가 셸 전체 붕괴)
//   전수 무결(정직 보고): 139 컴포넌트·32 구독 누수 0 · 259 [Inject] Orphan 0 · 생성자 폐포/캡티브 0.
//   DI 게이트는 BlazorHomeForm 생성자를 "실행하지 않고"(백그라운드 서비스 ~20종 기동) IL 의 제네릭 등록 인자로 판정한다.
// ============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Ports.Brokers;
using Xunit;

namespace StockManager.Tests
{
    // ── 센티널 양성 대조군 ──
    public sealed class SmFakeEventSource { public event Action? OnChange; public void Raise() => OnChange?.Invoke(); }

    public sealed class SmLeakNoDispose : ComponentBase
    {
        [Inject] public SmFakeEventSource Src { get; set; } = default!;
        protected override void OnInitialized() => Src.OnChange += Handler;
        private void Handler() { }
    }

    public sealed class SmLeakLambdaMismatch : ComponentBase, IDisposable
    {
        [Inject] public SmFakeEventSource Src { get; set; } = default!;
        protected override void OnInitialized() => Src.OnChange += () => StateHasChanged();
        public void Dispose() => Src.OnChange -= () => StateHasChanged();
    }

    public sealed class SmLeakPeriodicTimer : ComponentBase, IDisposable
    {
        private PeriodicTimer? _timer;
        protected override void OnInitialized() => _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        public void Dispose() { }
    }

    public sealed class SmCleanComponent : ComponentBase, IDisposable
    {
        [Inject] public SmFakeEventSource Src { get; set; } = default!;
        private PeriodicTimer? _timer;
        protected override void OnInitialized() { Src.OnChange += Handler; _timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); }
        private void Handler() { }
        public void Dispose() { Src.OnChange -= Handler; _timer?.Dispose(); }
    }

    /// <summary>보유종목 N건을 돌려주는 조회 전용 테스트 더블(liveArmed=false 고정).</summary>
    internal sealed class HoldingsStatusPort : IDualBrokerStatusPort
    {
        public int Holdings = 3;
        public int GetLineStatus(Span<BrokerLineStatus> dest) => 0;
        public int GetAccounts(Span<BrokerAccountLine> dest) => 0;
        public int GetHoldings(BrokerLine line, Span<BrokerHoldingLine> dest)
        {
            int n = Math.Min(Holdings, dest.Length);
            for (int i = 0; i < n; i++) dest[i] = new BrokerHoldingLine(line, 1000 + i, 10, 100m, 110m, 100m, false);
            return n;
        }
        public bool LiveArmed => false;
    }

    public class UiAndDiAuditTests
    {
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private static readonly Assembly[] UiAssemblies =
        {
            typeof(StockManager.Blazor.BlazorHomeForm).Assembly,
            typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly,
        };

        // ═══ §1 / §3-A  수명주기 누수 센티널 ═══

        [Fact]
        public void Sentinel_Detects_Every_Synthetic_Leak_And_Passes_Clean_Control()
        {
            Assert.Contains(UiLifecycleLeakSentinel.Inspect(typeof(SmLeakNoDispose)), f => f.Rule == "event-leak");
            Assert.Contains(UiLifecycleLeakSentinel.Inspect(typeof(SmLeakLambdaMismatch)), f => f.Rule == "event-leak" && f.Detail.Contains("불일치"));
            Assert.Contains(UiLifecycleLeakSentinel.Inspect(typeof(SmLeakPeriodicTimer)), f => f.Rule == "handle-leak" && f.Detail.Contains("PeriodicTimer"));
            Assert.Empty(UiLifecycleLeakSentinel.Inspect(typeof(SmCleanComponent)));
        }

        [Fact]
        public void All_Razor_Components_Release_Every_Subscription_Handle_And_Avoid_Async_Void()
        {
            var findings = new List<UiLeakFinding>();
            int components = 0, subscriptions = 0;
            foreach (var asm in UiAssemblies)
                foreach (var t in UiLifecycleLeakSentinel.ComponentTypes(asm, typeof(IComponent)))
                {
                    components++;
                    findings.AddRange(UiLifecycleLeakSentinel.Inspect(t, out int s));
                    subscriptions += s;
                }
            Assert.True(components >= 100, $"components={components}");     // 비공허성(감사 시점 실측 139)
            Assert.True(subscriptions >= 20, $"subscriptions={subscriptions}"); // (실측 32)
            Assert.True(findings.Count == 0, "UI 수명주기 누수:\n" + string.Join("\n", findings));
        }

        // ═══ §2  DI 결속(IL 등록 추출 — 컴포지션 루트 실행 0) ═══

        private static (IReadOnlyList<DiContractScanner.IlRegistration> Regs, HashSet<Type> Registered) HostRegistrations()
        {
            var ctor = typeof(StockManager.Blazor.BlazorHomeForm).GetConstructors().Single();
            var regs = DiContractScanner.ExtractRegistrations(ctor);
            var registered = new HashSet<Type>(regs.Select(r => r.Service));
            var fw = new ServiceCollection();
            fw.AddWindowsFormsBlazorWebView();   // 프레임워크 등록(IJSRuntime·NavigationManager 등) — 부작용 없는 단독 실행
            foreach (var d in fw) registered.Add(d.ServiceType);
            return (regs, registered);
        }

        [Fact]
        public void Il_Registration_Extractor_Is_Not_Vacuous()
        {
            var (regs, _) = HostRegistrations();
            Assert.True(regs.Count >= 250, $"regs={regs.Count}");   // 원문 263 호출 중 제네릭 262(감사 시점)
            Assert.Contains(regs, r => r.Service == typeof(IQuoteProvider));                                        // AddSingleton<IQuoteProvider>(value)
            Assert.Contains(regs, r => r.Service == typeof(LocalDatabase));                                         // AddSingleton(db) — 정적 형식 추론
            Assert.Contains(regs, r => r.Service == typeof(StockManager.Blazor.IModuleBridge));
            Assert.Contains(regs, r => r.Method == "AddScoped");
        }

        [Fact]
        public void Every_Component_Inject_Is_Registered_No_Orphan()
        {
            var (_, registered) = HostRegistrations();
            var orphans = new List<string>();
            int points = 0;
            foreach (var asm in UiAssemblies)
                foreach (var t in UiLifecycleLeakSentinel.ComponentTypes(asm, typeof(IComponent)))
                    foreach (var ip in DiContractScanner.InjectPoints(t))
                    {
                        points++;
                        var st = ip.ServiceType;
                        if (!registered.Contains(st) && !(st.IsGenericType && registered.Contains(st.GetGenericTypeDefinition())))
                            orphans.Add($"{t.FullName}.{ip.Property}: {st.FullName}");
                    }
            Assert.True(points >= 200, $"inject points={points}");   // (실측 259)
            Assert.True(orphans.Count == 0, "Orphan Inject:\n" + string.Join("\n", orphans));
        }

        [Fact]
        public void Type_Registered_Services_Have_Resolvable_Constructors_And_No_Captive_Scoped()
        {
            var (regs, registered) = HostRegistrations();
            var lifetime = new Dictionary<Type, string>();
            foreach (var r in regs) lifetime[r.Service] = r.Method.Contains("Scoped") ? "Scoped" : r.Method.Contains("Transient") ? "Transient" : "Singleton";
            bool Sat(Type t) => registered.Contains(t) || t == typeof(IServiceProvider)
                || (t.IsGenericType && (registered.Contains(t.GetGenericTypeDefinition()) || t.GetGenericTypeDefinition() == typeof(IEnumerable<>)));
            var defects = new List<string>();
            int checkedImpls = 0;
            foreach (var r in regs.Where(x => x.Implementation is not null))
            {
                var ctors = r.Implementation!.GetConstructors().OrderByDescending(c => c.GetParameters().Length).ToList();
                if (ctors.Count == 0) continue;
                checkedImpls++;
                var ok = ctors.FirstOrDefault(c => c.GetParameters().All(p => p.HasDefaultValue || Sat(p.ParameterType)));
                if (ok is null) { defects.Add($"{r.Service.Name}→{r.Implementation.Name} 생성자 의존 미등록"); continue; }
                if (lifetime[r.Service] == "Singleton")
                    foreach (var p in ok.GetParameters())
                        if (lifetime.TryGetValue(p.ParameterType, out var lt) && lt == "Scoped")
                            defects.Add($"{r.Service.Name}(Singleton) ← {p.ParameterType.Name}(Scoped) 캡티브 의존");
            }
            Assert.True(checkedImpls >= 50, $"checked={checkedImpls}");
            Assert.True(defects.Count == 0, string.Join("\n", defects));
        }

        [Fact]
        public void Portable_Rcl_Has_Zero_Concrete_Engine_Reference()
        {
            var refs = typeof(MS2E.UI.Portable.Ports.Validate.IValidateRepositoryPort).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToList();
            Assert.DoesNotContain("StockManager", refs);
        }

        // ═══ §4  Fail-Closed UI ═══

        [Fact]
        public void Portable_MainLayout_Isolates_Body_And_Ticker_Strip_In_ErrorBoundary()
        {
            var layout = typeof(MS2E.UI.Portable.Components.MainLayout);
            Assert.Contains(typeof(ErrorBoundary), DiContractScanner.ChildComponents(layout));   // 직접 자식(비전이)
            var refs = layout.GetFields(Inst).Count(f => f.FieldType == typeof(ErrorBoundary));
            Assert.Equal(2, refs);                                                                 // 본문 + 시세 스트립
            Assert.NotNull(layout.GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        }

        // ═══ §1  교차 스레드 렌더 경합 ═══

        private static object NewBrokerPanel(IDualBrokerStatusPort port)
        {
            var type = typeof(MS2E.UI.Portable.Components.MainLayout).Assembly.GetTypes().Single(t => t.Name == "DualLiveBrokerControlPanel");
            var panel = Activator.CreateInstance(type)!;
            type.GetProperty("Status", Inst)!.SetValue(panel, port);
            return panel;
        }

        private static void Refresh(object panel) => panel.GetType().GetMethod("RefreshTossHoldings", Inst)!.Invoke(panel, null);
        private static IEnumerable Holdings(object panel) => (IEnumerable)panel.GetType().GetField("_tossHoldings", Inst)!.GetValue(panel)!;

        [Fact]
        public void Broker_Panel_Render_Enumeration_Survives_Concurrent_Poll_Refresh()
        {
            // 렌더 스레드가 보유종목 표를 열거하는 도중(MoveNext 사이) 폴 스레드가 갱신하는 인터리빙을 결정론적으로 재현.
            var panel = NewBrokerPanel(new HoldingsStatusPort { Holdings = 3 });
            Refresh(panel);
            var render = Holdings(panel).GetEnumerator();
            Assert.True(render.MoveNext());
            Refresh(panel);                               // 폴(700ms 타이머) 갱신 — 수정 전: 같은 List 를 Clear/Add
            int rows = 1;
            while (render.MoveNext()) rows++;             // 수정 전: InvalidOperationException("Collection was modified")
            Assert.Equal(3, rows);                        // 렌더는 자기가 잡은 스냅샷을 끝까지 일관되게 그린다
            Assert.Equal(3, Holdings(panel).Cast<object>().Count());
        }

        [Fact]
        public void Broker_Panel_Poll_Is_Not_Reentrant()
        {
            var panel = NewBrokerPanel(new HoldingsStatusPort());
            var polling = panel.GetType().GetField("_polling", Inst);
            Assert.NotNull(polling);
            polling!.SetValue(panel, 1);                  // 이전 폴 진행 중 모사
            panel.GetType().GetMethod("Poll", Inst)!.Invoke(panel, new object?[] { null });
            Assert.Empty(Holdings(panel).Cast<object>()); // 재진입 폴은 공유 버퍼를 건드리지 않고 즉시 반환
            Assert.Equal(1, (int)polling.GetValue(panel)!);
        }
    }
}
