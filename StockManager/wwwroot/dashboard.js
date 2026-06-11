// v3.0: 위젯 드래그 정렬은 Dashboard.razor의 HTML5 네이티브 DnD(@ondragstart/@ondrop)로 이전.
//        여기서는 테마/단축키/포커스 등 순수 DOM 보조만 담당(외부 DnD 라이브러리 없음).
window.dashboard = {
  setTheme: function (mode) {
    document.documentElement.setAttribute('data-theme', mode);
    document.body.setAttribute('data-theme', mode);
  }
};

// Ctrl+K 명령 팔레트 단축키
window.dashboard.initHotkeys = function (dotnetRef) {
  window._cmdRef = dotnetRef;                 // 검색바(kbar) 클릭 등에서 재사용
  if (window._mksHotkey) return; window._mksHotkey = true;
  document.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && (e.key === 'k' || e.key === 'K')) {
      e.preventDefault();
      dotnetRef.invokeMethodAsync('OpenPalette');
    } else if (e.key === 'Escape') {
      dotnetRef.invokeMethodAsync('ClosePalette');
    }
  });
};
// 검색바 클릭 → Ctrl+K와 동일하게 명령 팔레트 열기 (기존 setFocus 미존재 버그 수정)
window.dashboard.openPalette = function () { try { window._cmdRef && window._cmdRef.invokeMethodAsync('OpenPalette'); } catch (_) {} };
window.dashboard.focusEl = function (el) { try { el && el.focus(); } catch (_) {} };
