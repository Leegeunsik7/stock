// ============================================================================
// CoinMiniWidget - 런처 상주 미니 코인 시세 위젯 (v2.7)
//  · BTC/ETH 업비트 시세를 4초 폴링해 작게 표시. 클릭 시 코인 모듈 오픈
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.CryptoWatcher
{
    public class CoinMiniWidget : Panel
    {
        private readonly UpbitProvider _upbit = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 4000 };
        private readonly string[] _markets = { "KRW-BTC", "KRW-ETH" };
        private readonly Dictionary<string, CryptoTick> _ticks = new(StringComparer.OrdinalIgnoreCase);

        public Action? ClickAction { get; set; }

        public CoinMiniWidget()
        {
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            _timer.Tick += async (s, e) => await RefreshAsync();
            HandleCreated += async (s, e) => { await RefreshAsync(); _timer.Start(); };
            HandleDestroyed += (s, e) => _timer.Stop();
            Click += (s, e) => ClickAction?.Invoke();
        }

        private async Task RefreshAsync()
        {
            try
            {
                var ts = await _upbit.GetTickersAsync(_markets);
                foreach (var t in ts) _ticks[t.Market] = t;
                if (!IsDisposed) Invalidate();
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(ThemeManager.Panel);
            g.DrawString("🪙 코인 (클릭)", GdiCache.SmallBold, GdiCache.Brush(ThemeManager.SubText), 10, 6);

            int y = 28;
            foreach (var m in _markets)
            {
                var name = m.Replace("KRW-", "");
                if (_ticks.TryGetValue(m, out var t))
                {
                    var col = t.ChangeRate > 0 ? ThemeManager.Up : t.ChangeRate < 0 ? ThemeManager.Down : ThemeManager.Text;
                    g.DrawString(name, GdiCache.NormalBold, GdiCache.Brush(ThemeManager.Text), 10, y);
                    var price = t.Price >= 100 ? t.Price.ToString("N0") : t.Price.ToString("0.##");
                    g.DrawString(price, GdiCache.Normal, GdiCache.Brush(col), 58, y);
                    g.DrawString((t.ChangeRate >= 0 ? "+" : "") + (t.ChangeRate * 100).ToString("F1") + "%",
                        GdiCache.Small, GdiCache.Brush(col), 158, y + 2);
                }
                else g.DrawString(name + "  …", GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), 10, y);
                y += 24;
            }
        }
    }
}
