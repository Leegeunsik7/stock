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

// v4.63.0 — δ=0 미니 라인 차트. C#은 매 틱 스칼라 2개만 push(SVG <path> 문자열 KB 폭발 함정 제거).
//   JS가 고정크기 Float64Array ring(init 1회 할당)을 유지하고 canvas 2D로 직접 드로잉 — push/draw 경로 문자열·할당 0.
//   색상 리터럴은 인터닝 상수(매 프레임 재생성 없음). 두 시리즈는 각자 최대값으로 정규화(스케일 상이).
window.dashboard.telemetryChart = (function () {
  const S = {};   // canvasId -> { cap, a:Float64Array, b:Float64Array, head, count }
  function get(id) { return S[id]; }
  function drawSeries(ctx, s, buf, max, color, W, H, pad) {
    if (max <= 0) max = 1e-9;
    ctx.beginPath(); ctx.strokeStyle = color; ctx.lineWidth = 1.5;
    const span = (s.count <= 1) ? 1 : (s.count - 1);
    for (let i = 0; i < s.count; i++) {
      const idx = (s.head - s.count + i + s.cap * 2) % s.cap;   // 오래된→최신
      const x = pad + (i / span) * (W - pad * 2);
      const y = H - pad - (buf[idx] / max) * (H - pad * 2);
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.stroke();
  }
  function draw(id) {
    const cv = document.getElementById(id); const s = S[id]; if (!cv || !s) return;
    const ctx = cv.getContext('2d'); const W = cv.width, H = cv.height, pad = 3;
    ctx.clearRect(0, 0, W, H);
    if (s.count < 2) return;
    let maxA = 1e-9, maxB = 1e-9;
    for (let i = 0; i < s.count; i++) { const idx = (s.head - s.count + i + s.cap * 2) % s.cap;
      if (s.a[idx] > maxA) maxA = s.a[idx]; if (s.b[idx] > maxB) maxB = s.b[idx]; }
    drawSeries(ctx, s, s.a, maxA, '#4caf50', W, H, pad);   // series A (throughput)
    drawSeries(ctx, s, s.b, maxB, '#f44336', W, H, pad);   // series B (collisions)
  }
  return {
    init: function (id, cap) {
      cap = cap || 60;
      S[id] = { cap: cap, a: new Float64Array(cap), b: new Float64Array(cap), head: 0, count: 0 };
      const cv = document.getElementById(id); if (cv) cv.getContext('2d').clearRect(0, 0, cv.width, cv.height);
    },
    push: function (id, a, b) {
      const s = S[id]; if (!s) return;                      // init 전 push 무시(무할당·무예외)
      s.a[s.head] = a; s.b[s.head] = b;
      s.head = (s.head + 1) % s.cap; if (s.count < s.cap) s.count++;
      draw(id);                                             // canvas 직접 갱신(Blazor 렌더 트리 미경유)
    }
  };
})();
