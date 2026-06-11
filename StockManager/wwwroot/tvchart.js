// TradingView Lightweight Charts 래퍼.
//  · lib(wwwroot/lib/lightweight-charts.standalone.production.js)는 사용자가 배치 (오프라인 원칙: CDN 미사용).
//  · 미배치 시 available()=false → Blazor가 기존 SVG 차트로 자동 폴백.
//  · 한국 관례 색: 상승=빨강, 하락=파랑.
window.tvchart = (function () {
  let chart = null, series = null;
  return {
    available: function () { return typeof LightweightCharts !== 'undefined'; },

    render: function (elId, candles, dark) {
      if (typeof LightweightCharts === 'undefined') return false;
      const el = document.getElementById(elId);
      if (!el || !candles || candles.length < 2) return false;
      try {
        if (chart) { try { chart.remove(); } catch (_) {} chart = null; }
        el.innerHTML = '';
        chart = LightweightCharts.createChart(el, {
          height: 360,
          layout: { background: { color: 'transparent' }, textColor: dark ? '#c9d2e3' : '#3a4252' },
          grid: { vertLines: { color: dark ? '#222a3c' : '#e4e8f0' }, horzLines: { color: dark ? '#222a3c' : '#e4e8f0' } },
          rightPriceScale: { borderVisible: false },
          timeScale: { borderVisible: false, timeVisible: false },
          crosshair: { mode: 0 }
        });
        series = chart.addCandlestickSeries({
          upColor: '#e0344b', wickUpColor: '#e0344b',
          downColor: '#2563eb', wickDownColor: '#2563eb', borderVisible: false
        });
        series.setData(candles);
        chart.timeScale().fitContent();
        return true;
      } catch (e) { return false; }
    },

    dispose: function () { try { if (chart) { chart.remove(); chart = null; } } catch (_) {} }
  };
})();
