// ============================================================================
// PaperTrading - 모의투자 계좌/주문 모듈 (v3.0)
//  · 가상 시드 기반. 요약카드(시드/현금/평가/총자산/총손익) + 포지션 + 주문이력
//  · 매수/매도(주문 다이얼로그) · 지정가 대기/취소 · 시드 재설정 · CSV · 테마반응
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.PaperTrading
{
    public class MainForm : Form
    {
        private static Color Bg => ThemeManager.Bg;
        private static Color Panel2 => ThemeManager.Panel;
        private static Color Up => ThemeManager.Up;
        private static Color Down => ThemeManager.Down;

        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly PaperBroker _broker;
        private readonly UpbitProvider _upbit = new();
        private readonly FxConverter _fx = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 10000 };

        private readonly Dictionary<string, decimal> _coinPrice = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal> _stockPrice = new(StringComparer.OrdinalIgnoreCase);
        private decimal _usdKrw = 1380m;

        private Panel _summary = null!;
        private DataGridView _posGrid = null!;
        private DataGridView _ordGrid = null!;
        private Label _status = null!;
        private decimal _seed, _cash, _posVal, _equity, _pnl, _ret;

        public MainForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            _broker = new PaperBroker(db);
            InitUI();
            ApplyTheme();
            ThemeManager.Changed += ApplyTheme;
            WindowMemory.Attach(this, _db, "Paper");
            _timer.Tick += async (s, e) => await RefreshAsync();
            Load += async (s, e) => { await RefreshAsync(); _timer.Start(); };
            FormClosing += (s, e) => { _timer.Stop(); ThemeManager.Changed -= ApplyTheme; };
        }

        private decimal PriceOf(PaperAsset a, string sym) =>
            a == PaperAsset.Coin
                ? (_coinPrice.TryGetValue(sym, out var p) ? p : 0m)
                : (_stockPrice.TryGetValue(sym, out var s) ? s : 0m);

        private void InitUI()
        {
            Text = "🧪 모의투자 (Paper Trading) — 가상 시드";
            Size = new Size(1160, 780);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("맑은 고딕", 10F);

            var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Renderer = new ToolStripProfessionalRenderer() };
            var bBuy = new ToolStripButton("🟢 매수");
            var bSell = new ToolStripButton("🔴 매도");
            var bCancel = new ToolStripButton("✖ 주문취소");
            var bReset = new ToolStripButton("💰 시드 재설정");
            var bRef = new ToolStripButton("🔄 새로고침");
            var bCsv = new ToolStripButton("⬇ CSV");
            var bBroker = new ToolStripButton("⚙️ 거래설정");
            tb.Items.AddRange(new ToolStripItem[] { bBuy, bSell, bCancel, new ToolStripSeparator(), bReset, bRef, bCsv, bBroker });
            bBuy.Click += (s, e) => OpenOrder(PaperSide.Buy);
            bSell.Click += (s, e) => OpenOrder(PaperSide.Sell);
            bCancel.Click += (s, e) => CancelSelected();
            bReset.Click += (s, e) => ResetSeed();
            bRef.Click += async (s, e) => await RefreshAsync();
            bCsv.Click += (s, e) => CsvExporter.ExportGrid(_posGrid, $"paper_positions_{DateTime.Now:yyyyMMdd}");
            bBroker.Click += (s, e) => { var f = new BrokerSettingsForm(_db); try { ThemeManager.Apply(f); } catch { } f.Show(); };

            _summary = new Panel { Dock = DockStyle.Top, Height = 116 };
            _summary.Paint += DrawSummary;

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
            _posGrid = BuildGrid(new[] { "종류", "종목", "수량", "평단가", "현재가", "평가액", "평가손익" });
            _ordGrid = BuildGrid(new[] { "시간", "종목", "구분", "유형", "수량", "가격", "상태" });
            split.Panel1.Controls.Add(WrapTitled(_posGrid, "📦 보유 포지션"));
            split.Panel2.Controls.Add(WrapTitled(_ordGrid, "🧾 주문 (대기/체결/취소)"));

            var ss = new StatusStrip { SizingGrip = false };
            _status = new Label { Text = "로딩 중...", ForeColor = Color.Gold, AutoSize = true };
            ss.Items.Add(new ToolStripControlHost(_status));

            Controls.Add(split);
            Controls.Add(_summary);
            Controls.Add(ss);
            Controls.Add(tb);
            split.BringToFront();
        }

        private Panel WrapTitled(Control inner, string title)
        {
            var p = new Panel { Dock = DockStyle.Fill };
            var lbl = new Label { Text = title, Dock = DockStyle.Top, Height = 26, Font = new Font("맑은 고딕", 10F, FontStyle.Bold), Padding = new Padding(6, 4, 0, 0) };
            inner.Dock = DockStyle.Fill;
            p.Controls.Add(inner);
            p.Controls.Add(lbl);
            return p;
        }

        private DataGridView BuildGrid(string[] cols)
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false,
                RowHeadersVisible = false, ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                RowTemplate = { Height = 28 }
            };
            foreach (var c in cols) g.Columns.Add(c, c);
            for (int i = 2; i < g.Columns.Count; i++)
                g.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            ThemeManager.StyleGrid(g);
            return g;
        }

        // ===== 가격 갱신 + 집계 =====
        private async Task RefreshAsync()
        {
            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { }

            // 코인 가격
            var coinSyms = _broker.Account.Positions.Where(p => p.Asset == PaperAsset.Coin).Select(p => p.Symbol)
                .Concat(_broker.Account.Orders.Where(o => o.Status == PaperStatus.Pending && o.Asset == PaperAsset.Coin).Select(o => o.Symbol))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (coinSyms.Count > 0)
            {
                try { foreach (var t in await _upbit.GetTickersAsync(coinSyms)) _coinPrice[t.Market] = t.Price; } catch { }
            }

            // 주식 가격 (시장별 그룹, USD는 환산)
            var stockPos = _broker.Account.Positions.Where(p => p.Asset == PaperAsset.Stock)
                .Select(p => (p.Symbol, p.StockMarket))
                .Concat(_broker.Account.Orders.Where(o => o.Status == PaperStatus.Pending && o.Asset == PaperAsset.Stock).Select(o => (o.Symbol, o.StockMarket)))
                .Distinct().ToList();
            foreach (var grp in stockPos.GroupBy(x => x.StockMarket))
            {
                try
                {
                    var quotes = await _provider.GetQuotesAsync(grp.Select(x => x.Symbol), grp.Key);
                    bool usd = grp.Key == Market.US_NYSE || grp.Key == Market.US_NASDAQ || grp.Key == Market.US_AMEX;
                    foreach (var q in quotes) if (q.Price > 0) _stockPrice[q.Symbol] = q.Price * (usd ? _usdKrw : 1m);
                }
                catch { }
            }

            // 지정가 자동 체결
            _broker.ProcessLimits(PriceOf);

            // 집계
            _seed = _broker.Account.Seed;
            _cash = _broker.Account.Cash;
            _posVal = _broker.PositionsValue(PriceOf);
            _equity = _cash + _posVal;
            _pnl = _equity - _seed;
            _ret = _seed > 0 ? _pnl / _seed * 100m : 0m;

            RebuildGrids();
            _status.Text = $"🕒 {DateTime.Now:HH:mm:ss} · 환율 {_usdKrw:N0} · 포지션 {_broker.Account.Positions.Count} · 대기주문 {_broker.Account.Orders.Count(o => o.Status == PaperStatus.Pending)}";
            _summary.Invalidate();
        }

        private void RebuildGrids()
        {
            _posGrid.Rows.Clear();
            foreach (var p in _broker.Account.Positions.OrderByDescending(p => p.Qty * PriceOf(p.Asset, p.Symbol)))
            {
                var cur = PriceOf(p.Asset, p.Symbol);
                var val = p.Qty * cur;
                var pnl = (cur - p.Avg) * p.Qty;
                int i = _posGrid.Rows.Add(p.Asset == PaperAsset.Coin ? "코인" : "주식", $"{p.Name} ({p.Symbol})",
                    Trim(p.Qty), p.Avg.ToString("N0"), cur.ToString("N0"), val.ToString("N0"), pnl.ToString("N0"));
                _posGrid.Rows[i].Cells[6].Style.ForeColor = pnl > 0 ? Up : pnl < 0 ? Down : ThemeManager.Text;
            }

            _ordGrid.Rows.Clear();
            foreach (var o in _broker.Account.Orders.Take(100))
            {
                var price = o.Status == PaperStatus.Filled ? o.FillPrice : o.LimitPrice;
                int i = _ordGrid.Rows.Add(o.Time.ToString("MM-dd HH:mm"), $"{o.Name} ({o.Symbol})",
                    o.Side == PaperSide.Buy ? "매수" : "매도",
                    o.Kind == PaperKind.Market ? "시장가" : "지정가",
                    Trim(o.Qty), price > 0 ? price.ToString("N0") : "-", StatusKr(o.Status));
                _ordGrid.Rows[i].Cells[2].Style.ForeColor = o.Side == PaperSide.Buy ? Up : Down;
                _ordGrid.Rows[i].Tag = o.Id;
            }
        }

        private static string Trim(decimal d) => d == Math.Floor(d) ? d.ToString("N0") : d.ToString("0.########");
        private static string StatusKr(PaperStatus s) => s switch
        {
            PaperStatus.Pending => "대기", PaperStatus.Filled => "체결",
            PaperStatus.Canceled => "취소", _ => "거부"
        };

        // ===== 주문 / 취소 / 시드 =====
        private async void OpenOrder(PaperSide side)
        {
            using var dlg = new OrderDialog(side);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            decimal cur = 0m;
            try
            {
                if (dlg.Asset == PaperAsset.Coin)
                {
                    var ts = await _upbit.GetTickersAsync(new[] { dlg.Symbol });
                    cur = ts.FirstOrDefault()?.Price ?? 0m;
                }
                else
                {
                    var q = await _provider.GetQuoteAsync(dlg.Symbol, dlg.StockMarket);
                    bool usd = dlg.StockMarket == Market.US_NYSE || dlg.StockMarket == Market.US_NASDAQ || dlg.StockMarket == Market.US_AMEX;
                    cur = (q?.Price ?? 0m) * (usd ? _usdKrw : 1m);
                }
            }
            catch { }

            var (ok, msg) = _broker.Place(dlg.Asset, dlg.Symbol, dlg.NameText, side, dlg.Kind, dlg.Qty, dlg.LimitPrice, cur);
            MessageBox.Show(msg, ok ? "주문" : "주문 실패");
            await RefreshAsync();
        }

        private void CancelSelected()
        {
            if (_ordGrid.CurrentRow?.Tag is string id) { _broker.Cancel(id); _ = RefreshAsync(); }
            else MessageBox.Show("취소할 대기 주문을 선택하세요.");
        }

        private void ResetSeed()
        {
            using var dlg = new SeedDialog(_broker.Account.Seed);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            _broker.ResetSeed(dlg.Seed);
            _ = RefreshAsync();
        }

        // ===== 그리기 / 테마 =====
        private void DrawSummary(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            void Card(int x, int w, string label, string value, Color vc)
            {
                g.FillRectangle(GdiCache.Brush(Panel2), x, 14, w, 88);
                g.DrawString(label, GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), x + 16, 26);
                g.DrawString(value, GdiCache.LargeBold, GdiCache.Brush(vc), x + 16, 52);
            }
            var pc = _pnl > 0 ? Up : _pnl < 0 ? Down : ThemeManager.Text;
            Card(20, 215, "시드머니", $"{_seed:N0}", ThemeManager.Text);
            Card(245, 215, "현금", $"{_cash:N0}", ThemeManager.Text);
            Card(470, 215, "포지션 평가", $"{_posVal:N0}", ThemeManager.Text);
            Card(695, 215, "총자산", $"{_equity:N0}", ThemeManager.Text);
            Card(920, 215, "총손익", $"{_pnl:N0} ({_ret:+0.00;-0.00;0.00}%)", pc);
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            ThemeManager.Apply(this);
            ThemeManager.StyleGrid(_posGrid);
            ThemeManager.StyleGrid(_ordGrid);
            _summary?.Invalidate();
            Invalidate(true);
        }
    }
}
