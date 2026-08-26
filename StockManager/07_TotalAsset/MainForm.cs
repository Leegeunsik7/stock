// ============================================================================
// TotalAsset - 주식 + 코인 통합 총자산 대시보드 (v2.6, 그룹 2)
//  · 주식 보유(LocalDatabase) + 코인 보유(Crypto.State JSON)를 KRW로 합산
//  · 총자산 파이(주식원화/주식외화/코인) + 통합 평가손익 + 종목 리스트
//  · 환율 v2.3 FxConverter 재사용, 코인 시세 업비트, 주식 시세 기존 Provider
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.TotalAsset
{
    public class MainForm : Form
    {
        private static Color Bg => ThemeManager.Bg;
        private static Color Panel2 => ThemeManager.Panel;
        private static Color Up => ThemeManager.Up;
        private static Color Down => ThemeManager.Down;

        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly UpbitProvider _upbit = new();
        private readonly FxConverter _fx = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };

        private Panel _summary = null!;
        private Panel _pie = null!;
        private DataGridView _grid = null!;
        private Label _status = null!;

        private decimal _usdKrw = 1380m;
        private decimal _totalKrw, _totalPnl, _stockKrw, _coinKrw, _returnPct;
        private readonly List<(string label, decimal value, Color color)> _slices = new();

        private class Row
        {
            public string Kind = "";
            public string Name = "";
            public decimal Value;
            public decimal Pnl;
        }

        public MainForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            InitUI();
            ApplyTheme();
            _timer.Tick += async (s, e) => await RefreshAsync();
            ThemeManager.Changed += ApplyTheme;
            WindowMemory.Attach(this, _db, "TotalAsset");
            Load += async (s, e) => { await RefreshAsync(); _timer.Start(); };
            FormClosing += (s, e) => { _timer.Stop(); ThemeManager.Changed -= ApplyTheme; };
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            ThemeManager.Apply(this);
            if (_grid != null)
            {
                _grid.BackgroundColor = Bg;
                _grid.GridColor = ThemeManager.Grid;
                _grid.ColumnHeadersDefaultCellStyle.BackColor = ThemeManager.HeaderBg;
                _grid.ColumnHeadersDefaultCellStyle.ForeColor = ThemeManager.Text;
                _grid.DefaultCellStyle.BackColor = Bg;
                _grid.DefaultCellStyle.ForeColor = ThemeManager.Text;
                _grid.AlternatingRowsDefaultCellStyle.BackColor = ThemeManager.GridAlt;
                _grid.Invalidate();
            }
            _summary?.Invalidate();
            _pie?.Invalidate();
            Invalidate(true);
        }

        private void InitUI()
        {
            Text = "🧮 총자산 — 주식 + 코인 통합";
            Size = new Size(1180, 760);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);

            var tb = new ToolStrip
            {
                BackColor = Panel2, GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ToolStripProfessionalRenderer(), ForeColor = Color.White
            };
            var bRef = new ToolStripButton("🔄 새로고침") { ForeColor = Color.White };
            bRef.Click += async (s, e) => await RefreshAsync();
            tb.Items.Add(bRef);
            var bCsv = new ToolStripButton("⬇ CSV") { ForeColor = Color.White };
            bCsv.Click += (s, e) => CsvExporter.ExportGrid(_grid, $"total_asset_{DateTime.Now:yyyyMMdd}");
            tb.Items.Add(bCsv);

            _summary = new Panel { Dock = DockStyle.Top, Height = 120, BackColor = Bg };
            _summary.Paint += DrawSummary;

            _pie = new Panel { Dock = DockStyle.Left, Width = 440, BackColor = Bg };
            _pie.Paint += DrawPie;

            _grid = BuildGrid();

            var statusStrip = new StatusStrip { BackColor = Panel2, SizingGrip = false };
            _status = new Label { Text = "로딩 중...", ForeColor = Color.Gold, AutoSize = true };
            statusStrip.Items.Add(new ToolStripControlHost(_status));

            Controls.Add(_grid);
            Controls.Add(_pie);
            Controls.Add(_summary);
            Controls.Add(statusStrip);
            Controls.Add(tb);
            _grid.BringToFront();
        }

        private DataGridView BuildGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, BackgroundColor = Bg, BorderStyle = BorderStyle.None,
                GridColor = Color.FromArgb(55, 60, 72),
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowTemplate = { Height = 28 }
            };
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(40, 46, 58), ForeColor = Color.White,
                Font = new Font("맑은 고딕", 9.5F, FontStyle.Bold), Alignment = DataGridViewContentAlignment.MiddleCenter
            };
            g.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Bg, ForeColor = Color.Gainsboro,
                SelectionBackColor = Color.FromArgb(70, 120, 200), SelectionForeColor = Color.White,
                Font = new Font("맑은 고딕", 9.5F)
            };
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Panel2 };
            g.Columns.Add("kind", "구분");
            g.Columns.Add("name", "종목");
            g.Columns.Add("value", "평가액(₩)");
            g.Columns.Add("pnl", "평가손익(₩)");
            g.Columns.Add("weight", "비중");
            g.Columns["kind"].FillWeight = 60;
            for (int i = 2; i < g.Columns.Count; i++)
                g.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            return g;
        }

        // ===== 집계 =====
        private async Task RefreshAsync()
        {
            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { }

            var rows = new List<Row>();
            decimal stockKrw = 0, stockPnl = 0, krwStock = 0, usdStock = 0, coinKrw = 0, coinPnl = 0;

            // ---- 주식 ----
            var holdings = _db.GetAllHoldings();
            foreach (var grp in holdings.GroupBy(h => h.Market))
            {
                try
                {
                    var quotes = await _provider.GetQuotesAsync(grp.Select(h => h.Symbol), grp.Key);
                    foreach (var q in quotes)
                    {
                        var h = grp.FirstOrDefault(x => x.Symbol == q.Symbol);
                        if (h != null && q.Price > 0) { h.CurrentPrice = q.Price; _db.SaveHolding(h); }
                    }
                }
                catch { /* 시세 실패 시 저장된 CurrentPrice 사용 */ }
            }
            foreach (var h in holdings)
            {
                if (h.Quantity <= 0) continue;
                var fx = h.Currency == Currency.USD ? _usdKrw : 1m;
                var val = h.CurrentPrice * h.Quantity * fx;
                var pnl = (h.CurrentPrice - h.AvgPrice) * h.Quantity * fx;
                stockKrw += val; stockPnl += pnl;
                if (h.Currency == Currency.USD) usdStock += val; else krwStock += val;
                rows.Add(new Row { Kind = h.Currency == Currency.USD ? "주식($)" : "주식", Name = h.Name, Value = val, Pnl = pnl });
            }

            // ---- 코인 ----
            try
            {
                var json = _db.GetSetting("Crypto.State");
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var state = JsonSerializer.Deserialize<CryptoWatcher.CryptoState>(json);
                    var held = state?.Holdings.Where(kv => kv.Value.Qty > 0).ToList()
                               ?? new List<KeyValuePair<string, CryptoWatcher.CryptoHolding>>();
                    if (held.Count > 0)
                    {
                        var ticks = await _upbit.GetTickersAsync(held.Select(kv => kv.Key));
                        var price = ticks.ToDictionary(t => t.Market, t => t.Price, StringComparer.OrdinalIgnoreCase);
                        var names = ticks.ToDictionary(t => t.Market, t => t.KoreanName, StringComparer.OrdinalIgnoreCase);
                        foreach (var kv in held)
                        {
                            if (!price.TryGetValue(kv.Key, out var p) || p <= 0) continue;
                            var val = p * kv.Value.Qty;
                            var pnl = (p - kv.Value.Avg) * kv.Value.Qty;
                            coinKrw += val; coinPnl += pnl;
                            rows.Add(new Row { Kind = "코인", Name = names.TryGetValue(kv.Key, out var n) ? n : kv.Key, Value = val, Pnl = pnl });
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Warn($"[TotalAsset] 코인 집계 실패: {ex.Message}"); }

            _stockKrw = stockKrw;
            _coinKrw = coinKrw;
            _totalKrw = stockKrw + coinKrw;
            _totalPnl = stockPnl + coinPnl;
            var cost = _totalKrw - _totalPnl;
            _returnPct = cost > 0 ? _totalPnl / cost * 100m : 0m;

            _slices.Clear();
            if (krwStock > 0) _slices.Add(("주식(원화)", krwStock, Color.FromArgb(80, 150, 220)));
            if (usdStock > 0) _slices.Add(("주식(외화)", usdStock, Color.FromArgb(120, 100, 210)));
            if (coinKrw > 0) _slices.Add(("코인", coinKrw, Color.FromArgb(240, 170, 40)));

            // 그리드
            _grid.Rows.Clear();
            foreach (var r in rows.OrderByDescending(r => r.Value))
            {
                var w = _totalKrw > 0 ? (double)r.Value / (double)_totalKrw * 100 : 0;
                int idx = _grid.Rows.Add(r.Kind, r.Name, r.Value.ToString("N0"), r.Pnl.ToString("N0"), $"{w:F1}%");
                _grid.Rows[idx].Cells[3].Style.ForeColor = r.Pnl > 0 ? Up : r.Pnl < 0 ? Down : Color.Gainsboro;
            }

            _status.Text = $"🕒 {DateTime.Now:HH:mm:ss} · 환율 {_usdKrw:N0} · 종목 {rows.Count}건";
            _summary.Invalidate();
            _pie.Invalidate();
        }

        // ===== 그리기 =====
        private void DrawSummary(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            void Card(int x, string label, string value, Color valColor)
            {
                g.FillRectangle(GdiCache.Brush(Panel2), x, 16, 270, 88);
                g.DrawString(label, GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), x + 18, 28);
                g.DrawString(value, GdiCache.XLarge, GdiCache.Brush(valColor), x + 18, 54);
            }
            var pnlColor = _totalPnl > 0 ? Up : _totalPnl < 0 ? Down : ThemeManager.Text;
            Card(24, "총자산", $"{_totalKrw:N0} 원", ThemeManager.Text);
            Card(310, "총 평가손익", $"{_totalPnl:N0} 원 ({_returnPct:+0.00;-0.00;0.00}%)", pnlColor);
            Card(596, "주식 평가액", $"{_stockKrw:N0} 원", ThemeManager.Text);
            Card(882, "코인 평가액", $"{_coinKrw:N0} 원", ThemeManager.Text);
        }

        private void DrawPie(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawString("📊 자산 구성", GdiCache.Title, GdiCache.Brush(ThemeManager.Text), 24, 18);

            var total = _slices.Sum(x => x.value);
            if (total <= 0)
            {
                g.DrawString("보유 자산이 없습니다.", GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), 28, 70);
                return;
            }
            int cx = 180, cy = 220, r = 130;
            float start = -90;
            foreach (var sl in _slices)
            {
                var sweep = (float)((double)sl.value / (double)total * 360);
                g.FillPie(GdiCache.Brush(sl.color), cx - r, cy - r, r * 2, r * 2, start, sweep);
                start += sweep;
            }
            g.FillEllipse(GdiCache.Brush(Bg), cx - 70, cy - 70, 140, 140);
            g.DrawString("총자산", GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), cx - 24, cy - 24);
            var t = _totalKrw >= 100_000_000m ? $"{_totalKrw / 100_000_000m:0.0}억" : $"{_totalKrw / 10_000m:N0}만";
            g.DrawString(t, GdiCache.LargeBold, GdiCache.Brush(ThemeManager.Text), cx - 36, cy);

            int ly = 380;
            foreach (var sl in _slices)
            {
                var pct = (double)sl.value / (double)total * 100;
                g.FillRectangle(GdiCache.Brush(sl.color), 40, ly, 16, 16);
                g.DrawString($"{sl.label}   {sl.value:N0}원  ({pct:F1}%)", GdiCache.Normal, GdiCache.Brush(ThemeManager.Text), 64, ly - 1);
                ly += 26;
            }
        }
    }
}
