// ============================================================================
// ToastService - 액션형 전역 토스트 알림 (Blazor Hybrid, v3.0)
//  · 단순 텍스트 토스트 + '버튼 액션'(C# 델리게이트) 지원.
//  · 싱글톤 DI 등록 → 어느 페이지/서비스에서도 Toasts.Show(...) 호출.
//  · UI/자동 닫힘은 ToastHost.razor가 담당 (이 클래스는 상태 + 이벤트만).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace StockManager.Blazor
{
    public enum ToastLevel { Info, Success, Warning, Error }

    /// <summary>토스트 버튼 — 라벨 + 실행 델리게이트(Func&lt;Task&gt;). Primary면 강조 스타일.</summary>
    public record ToastButton(string Label, Func<Task> OnClick, bool Primary = false)
        : MS2E.UI.Portable.Ports.Shell.IToastButtonView   // Phase 94-K: Label/Primary 공변
    {
        // 동기 액션 편의 오버로드: Toast.Button("승인", () => Approve())
        public static ToastButton Of(string label, Action onClick, bool primary = false)
            => new(label, () => { onClick(); return Task.CompletedTask; }, primary);
    }

    public class ToastMessage : MS2E.UI.Portable.Ports.Shell.IToastView   // Phase 94-K: ToastHost 공변
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Title { get; init; } = "";
        public string Message { get; init; } = "";
        public ToastLevel Level { get; init; } = ToastLevel.Info;
        public List<ToastButton> Buttons { get; init; } = new();
        public int TimeoutMs { get; init; } = 5000;   // 0 = 자동으로 닫지 않음(액션 대기형)

        // Level(enum 정수정렬)·Buttons(List→IReadOnlyList 공변) 명시 구현. Id/Title/Message/TimeoutMs 는 암시.
        MS2E.UI.Portable.Ports.Shell.ToastKind MS2E.UI.Portable.Ports.Shell.IToastView.Level
            => (MS2E.UI.Portable.Ports.Shell.ToastKind)(int)Level;
        IReadOnlyList<MS2E.UI.Portable.Ports.Shell.IToastButtonView> MS2E.UI.Portable.Ports.Shell.IToastView.Buttons => Buttons;
    }

    public class ToastService
    {
        private const int MaxVisible = 6;
        public List<ToastMessage> Active { get; } = new();
        public event Action? Changed;

        /// <summary>토스트 표시. 버튼이 있으면 액션형으로 렌더된다.</summary>
        public ToastMessage Show(string title, string message = "", ToastLevel level = ToastLevel.Info,
            List<ToastButton>? buttons = null, int timeoutMs = 5000)
        {
            // 버튼이 있는데 호출자가 타임아웃을 명시하지 않은 경우, 액션을 놓치지 않도록 더 길게.
            if (buttons is { Count: > 0 } && timeoutMs == 5000) timeoutMs = 10000;

            var t = new ToastMessage
            {
                Title = title,
                Message = message,
                Level = level,
                Buttons = buttons ?? new List<ToastButton>(),
                TimeoutMs = timeoutMs
            };
            Active.Insert(0, t);                                   // 최신 토스트가 위로
            while (Active.Count > MaxVisible) Active.RemoveAt(Active.Count - 1);
            Changed?.Invoke();
            return t;
        }

        // 레벨별 단축 메서드
        public ToastMessage Info(string title, string message = "", List<ToastButton>? buttons = null, int timeoutMs = 5000)
            => Show(title, message, ToastLevel.Info, buttons, timeoutMs);
        public ToastMessage Success(string title, string message = "", List<ToastButton>? buttons = null, int timeoutMs = 5000)
            => Show(title, message, ToastLevel.Success, buttons, timeoutMs);
        public ToastMessage Warning(string title, string message = "", List<ToastButton>? buttons = null, int timeoutMs = 6000)
            => Show(title, message, ToastLevel.Warning, buttons, timeoutMs);
        public ToastMessage Error(string title, string message = "", List<ToastButton>? buttons = null, int timeoutMs = 8000)
            => Show(title, message, ToastLevel.Error, buttons, timeoutMs);

        public void Dismiss(Guid id)
        {
            if (Active.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
        }

        public void Clear() { Active.Clear(); Changed?.Invoke(); }
    }
}
