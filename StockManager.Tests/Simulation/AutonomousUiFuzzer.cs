// ============================================================================
// AutonomousUiFuzzer — [자율 UI 검증 · 추천 A] bUnit 헤드리스 클릭 주입 표면(테스트 전용 격리)
//
//  bUnit TestContext 로 순수 RCL 실 컴포넌트(SettingsPage · NavMenu · PaperTradingDashboardView)를 메모리 렌더하고, 렌더된 HTML DOM
//  (AngleSharp)에서 조작 가능 요소를 자동 탐색한다:
//    · input[type=checkbox] + blazor:onchange → Checkbox(현재 checked 반전값으로 Change 주입)
//    · blazor:onclick 보유 요소(button · 클릭 가능 span 등)  → Click
//    · a[href](핸들러 없음 = NavLink)                        → NavigationManager.NavigateTo(href)
//  지문은 마크업을 UiStateFingerprint.HashMarkup(δ=0 · blazor:* 렌더러 내부 속성 생략)으로 해싱.
//  호스트 CLI 의 HeadlessUiSurface(렌더 트리 프레임 직접 판독)와 같은 IUiClickSurface 계약 — 같은 퍼저 루프 · 같은 무장 프로브를 공유하고,
//  두 탐색기(DOM 파싱 ↔ 프레임 판독)의 조작 요소 수 일치를 교차 검증한다.
//  ★bUnit 은 이 테스트 프로젝트에만 참조(호스트 · RCL 무참조 — Pure RCL 래칫 무영향).
// ============================================================================
using System;
using System.Collections.Generic;
using AngleSharp.Dom;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using StockManager.Simulation;
using StockManager.Tools;

namespace StockManager.Tests.Simulation
{
    public sealed class BunitClickSurface : IUiClickSurface, IDisposable
    {
        private readonly Bunit.TestContext _ctx = new();
        private readonly List<(IRenderedFragment Cut, bool Volatile)> _roots = new();
        private readonly List<(IElement El, UiControlKind Kind, int Root)> _controls = new();
        private long _faults, _stale;
        public long StaleTargets => _stale;
        public bool IsVolatile(int index) => _roots[_controls[index].Root].Volatile;

        public BunitClickSurface(UiFuzzRig rig)
        {
            rig.Register(_ctx.Services);
            _ctx.JSInterop.Mode = JSRuntimeMode.Strict;   // RCL 대상 화면은 JS 무사용 — 호출 발생 시 즉시 예외(=UI 결함으로 계수)
        }

        public NavigationManager Navigation => _ctx.Services.GetRequiredService<NavigationManager>();
        public long Faults => _faults;
        public string? LastFault { get; private set; }

        public void Mount<TComponent>(bool volatileRoot = false) where TComponent : IComponent
            => _roots.Add((_ctx.RenderComponent<TComponent>(), volatileRoot));

        /// <summary>CLI 와 같은 3 루트(설정 · 내비 · 대시보드=변동성).</summary>
        public static BunitClickSurface CreateStandard(UiFuzzRig rig)
        {
            var s = new BunitClickSurface(rig);
            s.Mount<MS2E.UI.Portable.Pages.Settings.SettingsPage>();
            s.Mount<MS2E.UI.Portable.Components.NavMenu>();
            s.Mount<MS2E.UI.Portable.Pages.Trading.PaperTradingDashboardView>(volatileRoot: true);
            return s;
        }

        public int Refresh()
        {
            _controls.Clear();
            for (int r = 0; r < _roots.Count; r++)
            {
                foreach (var el in _roots[r].Cut.FindAll("*"))
                {
                    bool checkbox = el.LocalName == "input" && string.Equals(el.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase);
                    if (checkbox && el.HasAttribute("blazor:onchange")) _controls.Add((el, UiControlKind.Checkbox, r));
                    else if (el.HasAttribute("blazor:onclick")) _controls.Add((el, UiControlKind.Click, r));
                    else if (el.LocalName == "a" && el.HasAttribute("href")) _controls.Add((el, UiControlKind.NavLink, r));
                }
            }
            return _controls.Count;
        }

        public UiControlKind KindOf(int index) => _controls[index].Kind;

        public string Describe(int index) => _controls[index].Kind + "<" + _controls[index].El.LocalName + "> root=" + _controls[index].Root;

        public void Activate(int index)
        {
            var (el, kind, root) = _controls[index];
            try
            {
                Dispatch(el, kind);
                if (kind == UiControlKind.Checkbox) Settle(root, OrdinalOf(index), !el.HasAttribute("checked"));
            }
            catch (Bunit.Rendering.UnknownEventHandlerIdException)
            {
                // [v12.18.8-DEV] 탐색(DOM 사본)과 클릭 사이 비동기 재렌더(라이브 프레임 · 컴포넌트 await 후속)로 핸들러 id 교체 —
                //  사용자는 항상 '현재' DOM 을 누른다: 같은 루트 · 종류 · 태그 · 순번의 요소를 재탐색해 1회 재시도. 재시도 실패 = 결함.
                _stale++;
                int ordinal = OrdinalOf(index);
                Refresh();
                string ctx = Context(el);
                int again = Find(root, kind, el.LocalName, ordinal);
                if (again >= 0 && Context(_controls[again].El) != ctx) again = -1;          // 구조 변경으로 순번이 다른 요소를 가리키면 소멸로 취급(오표적 조작 0)
                if (again < 0) return;                                                    // 요소 소멸(재렌더로 사라짐) = 조작 대상 없음
                try
                {
                    var re = _controls[again].El;
                    Dispatch(re, kind);
                    if (kind == UiControlKind.Checkbox) Settle(root, ordinal, !re.HasAttribute("checked"));   // ★재시도도 자기 렌더 반영 대기(누락 시 대칭 위양성 — 전체 스위트 실측)
                }
                catch (Exception ex) { _faults++; LastFault ??= "retry " + ex.GetType().Name + ": " + ex.Message; }
            }
            catch (Exception ex)
            {
                _faults++;
                LastFault ??= ex.GetType().Name + ": " + ex.Message;
            }
        }

        private void Dispatch(IElement el, UiControlKind kind)
        {
            switch (kind)
            {
                case UiControlKind.Checkbox: el.Change(!el.HasAttribute("checked")); break;
                case UiControlKind.Click: el.Click(); break;
                default:
                    string href = el.GetAttribute("href")!;
                    _ctx.Renderer.Dispatcher.InvokeAsync(() => Navigation.NavigateTo(href)).GetAwaiter().GetResult();
                    break;
            }
        }

        // [v12.18.8-DEV] checkbox 반전값은 DOM 사본의 checked 로 정한다 — 부하(라이브 텔레메트리 · 전이 스레드) 중엔 직전 반전의 렌더가
        //  사본에 닿기 전에 다음 판독이 일어나 같은 값을 두 번 보낼 수 있다(실측: '서버 사용' @bind 토글 대칭 위양성 파손 · 헤드리스 렌더러 0).
        //  브라우저는 살아 있는 DOM 을 반전하므로 이 경합이 없다 → 하네스가 자기 조작의 렌더 반영을 기다린다(≤2s — 전체 스위트 부하 대비). 컴포넌트가 거부하면 시간 초과 계수만.
        private long _settleTimeouts;
        public long SettleTimeouts => _settleTimeouts;

        // [v12.18.9-DEV] 루트 마크업이 연속 2회(25ms 간격) 같아질 때까지 대기(≤2s) — 극부하(27/s · --diag)에서 이전 조작의 비동기 후속 렌더가
        //  대칭 창(토글 2회) 안에 착지해 위양성 파손 3건(실측 · 반영 대기 초과 0). 시간 초과 = QuiesceTimeouts 계수 후 판정 진행.
        private long _quiesceTimeouts;
        public long QuiesceTimeouts => _quiesceTimeouts;

        public bool Quiesce(int index)
        {
            var cut = _roots[_controls[index].Root].Cut;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string last = cut.Markup;
            while (sw.ElapsedMilliseconds < 2_000)
            {
                System.Threading.Thread.Sleep(25);
                string now = cut.Markup;
                if (string.Equals(now, last, StringComparison.Ordinal)) return true;
                last = now;
            }
            _quiesceTimeouts++;
            return false;
        }

        private void Settle(int root, int ordinal, bool target)
        {
            try { _roots[root].Cut.WaitForState(() => CheckedAt(root, ordinal) == target, TimeSpan.FromSeconds(2)); }
            catch (Bunit.Extensions.WaitForHelpers.WaitForFailedException) { _settleTimeouts++; }
        }

        private bool? CheckedAt(int root, int ordinal)
        {
            int n = 0;
            foreach (var el in _roots[root].Cut.FindAll("input"))
            {
                if (!string.Equals(el.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase) || !el.HasAttribute("blazor:onchange")) continue;
                if (n++ == ordinal) return el.HasAttribute("checked");
            }
            return null;
        }

        // 같은 (루트 · 종류 · 태그) 안 순번 — 재렌더 후 같은 요소 재식별
        private int OrdinalOf(int index)
        {
            var (el, kind, root) = _controls[index];
            int n = 0;
            for (int i = 0; i < index; i++) if (_controls[i].Root == root && _controls[i].Kind == kind && _controls[i].El.LocalName == el.LocalName) n++;
            return n;
        }

        // 재식별 문맥 = 부모 요소 텍스트(체크박스 라벨 · 버튼 행) — 같은 순번이라도 다른 요소면 다르다.
        private static string Context(IElement el) => el.ParentElement?.TextContent ?? string.Empty;

        private int Find(int root, UiControlKind kind, string tag, int ordinal)
        {
            int n = 0;
            for (int i = 0; i < _controls.Count; i++)
                if (_controls[i].Root == root && _controls[i].Kind == kind && _controls[i].El.LocalName == tag && n++ == ordinal) return i;
            return -1;
        }

        public ulong Fingerprint()
        {
            var h = Fnv64.Create();
            for (int r = 0; r < _roots.Count; r++)
            {
                if (_roots[r].Volatile) continue;
                ulong v = UiStateFingerprint.HashMarkup(_roots[r].Cut.Markup);
                for (int b = 0; b < 4; b++) h.Add((char)(v >> (16 * b)));
                h.Separator();
            }
            return UiStateFingerprint.Remap(h.Value);
        }

        public ulong RootFingerprint(int index)
        {
            string markup = _roots[_controls[index].Root].Cut.Markup;
            return UiStateFingerprint.HashMarkup(markup);
        }

        public void Dispose() => _ctx.Dispose();
    }
}
