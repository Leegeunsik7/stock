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
        private long _faults;

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
            var (el, kind, _) = _controls[index];
            try
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
            catch (Exception ex)
            {
                _faults++;
                LastFault ??= ex.GetType().Name + ": " + ex.Message;
            }
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

        public ulong RootFingerprint(int index) => UiStateFingerprint.HashMarkup(_roots[_controls[index].Root].Cut.Markup);

        public void Dispose() => _ctx.Dispose();
    }
}
