// TradingView Lightweight Charts 래퍼 (v4 API) — 엔터프라이즈 차트 융합 (Season 6 #1)
//  · lib: wwwroot/lib/lightweight-charts.standalone.production.js (로컬 번들, 런타임 CDN 미사용)
//  · 미배치 시 available()=false → Blazor가 기존 SVG 차트로 자동 폴백.
//  · 색: 새 엔터프라이즈 팔레트 — 상승=네온그린 / 하락=뮤트레드 (테마 동기화).
//  · render: 캔들 + MA5/20/60 오버레이 + 거래량 히스토그램 + 크로스헤어 + 반응형.
//  · renderLine: 에쿼티/누적수익 등 라인(area) 차트 — 다른 패널에서 재사용.
window.tvchart = (function () {
  let chart = null, ro = null, elRef = null;

  function clear() {
    if (ro) { try { ro.disconnect(); } catch (_) {} ro = null; }
    if (chart) { try { chart.remove(); } catch (_) {} chart = null; }
    elRef = null;
  }
  function pal(dark) {
    return {
      up: dark ? '#34d399' : '#0f9d6b',
      dn: dark ? '#f87171' : '#e0524d',
      accent: dark ? '#7c8aff' : '#4f5bd5',
      grid: dark ? 'rgba(255,255,255,0.06)' : '#e8ebf2',
      text: dark ? '#8a8d96' : '#697586',
    };
  }
  function baseOpts(p, h) {
    return {
      height: h,
      layout: { background: { color: 'transparent' }, textColor: p.text, fontFamily: 'Roboto Mono, Consolas, monospace' },
      grid: { vertLines: { color: p.grid }, horzLines: { color: p.grid } },
      rightPriceScale: { borderVisible: false },
      timeScale: { borderVisible: false, timeVisible: false },
      crosshair: { mode: (LightweightCharts.CrosshairMode ? LightweightCharts.CrosshairMode.Normal : 0) },
    };
  }
  function attachResize(el) {
    try { ro = new ResizeObserver(function () { if (chart && elRef) chart.applyOptions({ width: elRef.clientWidth }); }); ro.observe(el); } catch (_) {}
    if (el.clientWidth) chart.applyOptions({ width: el.clientWidth });
  }
  function sma(vals, period) {
    var out = [], sum = 0;
    for (var i = 0; i < vals.length; i++) {
      sum += vals[i];
      if (i >= period) sum -= vals[i - period];
      if (i >= period - 1) out.push({ i: i, v: sum / period });
    }
    return out;
  }

  return {
    available: function () { return typeof LightweightCharts !== 'undefined'; },

    // candles: [{time:'yyyy-MM-dd', open, high, low, close, volume}]
    render: function (elId, candles, dark) {
      if (typeof LightweightCharts === 'undefined') return false;
      var el = document.getElementById(elId);
      if (!el || !candles || candles.length < 2) return false;
      try {
        clear(); el.innerHTML = ''; elRef = el;
        var p = pal(dark);
        chart = LightweightCharts.createChart(el, baseOpts(p, 400));

        // 거래량 (하단 오버레이)
        var vol = chart.addHistogramSeries({ priceFormat: { type: 'volume' }, priceScaleId: '', lastValueVisible: false, priceLineVisible: false });
        vol.priceScale().applyOptions({ scaleMargins: { top: 0.84, bottom: 0 } });
        vol.setData(candles.map(function (c) { return { time: c.time, value: c.volume || 0, color: (c.close >= c.open ? p.up : p.dn) }; }));

        // 캔들
        var cs = chart.addCandlestickSeries({
          upColor: p.up, wickUpColor: p.up, borderUpColor: p.up,
          downColor: p.dn, wickDownColor: p.dn, borderDownColor: p.dn,
        });
        cs.setData(candles.map(function (c) { return { time: c.time, open: c.open, high: c.high, low: c.low, close: c.close }; }));

        // 이동평균 오버레이 (MA5/20/60)
        var closes = candles.map(function (c) { return c.close; });
        [[5, '#d8b021'], [20, '#c850c8'], [60, '#28aaaa']].forEach(function (d) {
          var period = d[0], col = d[1];
          if (closes.length >= period) {
            var ls = chart.addLineSeries({ color: col, lineWidth: 1, priceLineVisible: false, lastValueVisible: false, crosshairMarkerVisible: false });
            ls.setData(sma(closes, period).map(function (o) { return { time: candles[o.i].time, value: o.v }; }));
          }
        });

        chart.timeScale().fitContent();
        attachResize(el);
        return true;
      } catch (e) { return false; }
    },

    // points: [{time:'yyyy-MM-dd', value}] — 에쿼티/누적수익 등 라인(area) 차트
    renderLine: function (elId, points, dark) {
      if (typeof LightweightCharts === 'undefined') return false;
      var el = document.getElementById(elId);
      if (!el || !points || points.length < 2) return false;
      try {
        clear(); el.innerHTML = ''; elRef = el;
        var p = pal(dark);
        chart = LightweightCharts.createChart(el, baseOpts(p, 300));
        var area = chart.addAreaSeries({
          lineColor: p.accent, lineWidth: 2,
          topColor: dark ? 'rgba(124,138,255,0.28)' : 'rgba(79,91,213,0.20)',
          bottomColor: 'rgba(0,0,0,0)', priceLineVisible: false,
        });
        area.setData(points);
        chart.timeScale().fitContent();
        attachResize(el);
        return true;
      } catch (e) { return false; }
    },

    dispose: function () { clear(); }
  };
})();
