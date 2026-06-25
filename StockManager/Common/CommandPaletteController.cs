// ============================================================================
// CommandPaletteController — Mission C: 명령 팔레트 '순수 C#' 열기 인터페이스
//
//  배경: 기존 setFocus 미존재 JS 버그는 v3.5.3(6a2c576)에서 이미 dashboard.openPalette 로 교체됐다(콘솔 에러 0).
//  다만 검색바(kbar) 클릭 경로는 여전히 C# → JS(dashboard.openPalette) → window._cmdRef.invokeMethodAsync
//  ('OpenPalette') 의 JS 왕복이며, window._cmdRef 가 아직 미설정(첫 렌더 전)이면 조용히 무동작한다.
//  본 컨트롤러는 스펙이 가정한 'CommandPalette.OpenPalette 호출 인터페이스'를 순수 C# 싱글톤 브로커로 실체화해
//  NavMenu 등이 JS 홉 없이 RequestOpen() 한 번으로 팔레트를 연다 — JS 의존·무동작 취약성 제거.
//
//  CommandPalette(MainLayout 단일 인스턴스)가 OpenRequested 를 구독하고, 발화 시 InvokeAsync 로 UI 스레드에
//  마샬링해 연다. Ctrl+K 전역 핫키는 기존 JS 경로 유지(정상 작동·미변경) — 본 컨트롤러는 클릭 경로만 정화한다.
// ============================================================================
using System;

namespace StockManager.Common
{
    /// <summary>명령 팔레트 열기 요청을 C# 컴포넌트 간 전달하는 경량 싱글톤 브로커(JS 왕복 없음). 구독자는 CommandPalette.</summary>
    public sealed class CommandPaletteController
    {
        /// <summary>팔레트가 구독. 발화 시 열기. UI 스레드 마샬링(InvokeAsync)은 구독자 책임.</summary>
        public event Action? OpenRequested;

        /// <summary>팔레트 열기 요청(NavMenu 검색바 클릭 등). 구독자 없으면 무해히 무동작.</summary>
        public void RequestOpen()
        {
            var handler = OpenRequested;   // 경합 안전 — 지역 캡처 후 호출(구독 해지와의 레이스 차단)
            handler?.Invoke();
        }
    }
}
