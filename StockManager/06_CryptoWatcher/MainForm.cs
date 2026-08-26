// ============================================================================
// CryptoWatcher - 코인 실시간 감시 + 보유 관리 (v2.5)
//  v2.5 추가:
//   · 업비트 WebSocket 실시간 스트리밍(폴백 REST 폴링 유지)
//   · 바이낸스 USDT 탭 + 김치 프리미엄(환율 v2.3 교차계산)
//   · 코인 지정가 알림 → AlertCenter → 텔레그램(Critical) 자동 전송
//  상태(감시/보유/알림)는 Settings 'Crypto.State'에 JSON 저장
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.CryptoWatcher
{
    public class CryptoHolding
    {
        public decimal Qty { get; set; }
        public decimal Avg { get; set; }
    }

    public class CoinAlert
    {
        public string Market { get; set; } = "";
        public bool Above { get; set; } = true;   // true: 이상, false: 이하
        public decimal Target { get; set; }
        public bool Triggered { get; set; }
    }

    public class CryptoState
    {
        public List<string> Watch { get; set; } = new();
        public Dictionary<string, CryptoHolding> Holdings { get; set; } = new();
        public List<CoinAlert> Alerts { get; set; } = new();
    }

    public class MainForm : Form
    {
        private static Color Bg => ThemeManager.Bg;
        private static Color Panel2 => ThemeManager.Panel;
        private static Color Up => ThemeManager.Up;
        private static Color Down => ThemeManager.Down;

        private readonly LocalDatabase _db;
        private readonly UpbitProvider _upbit = new();
        private readonly BinanceProvider _binance = new();
        private readonly UpbitWebSocketClient _ws = new();
        private readonly FxConverter _fx = new();
        private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };

        private DataGridView _grid = null!;       // 원화(업비트)
        private DataGridView _gridUsdt = null!;   // USDT(바이낸스)
        private Label _status = null!;
        private Label _wsBadge = null!;
        private CryptoState _state = new();
        private decimal _usdKrw = 1380m;

        private readonly Dictionary<string, CryptoTick> _lastKrw = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BinanceTick> _lastUsdt = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, decimal[]> _spark = new(StringComparer.OrdinalIgnoreCase);

        // KRW 그리드 컬럼 인덱스
        private const int C_MARKET = 0, C_NAME = 1, C_PRICE = 2, C_CHG = 3, C_HIGH = 4, C_LOW = 5,
                          C_USDT = 6, C_KIMCHI = 7, C_AMT = 8, C_QTY = 9, C_AVG = 10, C_PNL = 11, C_SPARK = 12;

        public MainForm(LocalDatabase db)
        {
            _db = db;
            LoadState();
            InitUI();
            ApplyTheme();
            _ws.TickReceived += OnWsTick;
            _timer.Tick += async (s, e) => await PollAsync();
            Load += async (s, e) => await BootstrapAsync();
            ThemeManager.Changed += ApplyTheme;
            WindowMemory.Attach(this, _db, "Crypto");
            FormClosing += (s, e) => { _timer.Stop(); _ws.Dispose(); SaveState(); ThemeManager.Changed -= ApplyTheme; };
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            ThemeManager.Apply(this);
            foreach (var g in new[] { _grid, _gridUsdt })
            {
                if (g == null) continue;
                ThemeManager.StyleGrid(g);
            }
            if (_grid != null)
            {
                _grid.Columns[C_QTY].DefaultCellStyle.BackColor = ThemeManager.Field;
                _grid.Columns[C_AVG].DefaultCellStyle.BackColor = ThemeManager.Field;
            }
            Invalidate(true);
        }

        private void InitUI()
        {
            Text = "🪙 코인 실시간 — 업비트(WS) · 바이낸스 · 김치 프리미엄";
            Size = new Size(1180, 740);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);

            var tb = new ToolStrip
            {
                BackColor = Panel2, GripStyle = ToolStripGripStyle.Hidden,
                Renderer = new ToolStripProfessionalRenderer(), ForeColor = Color.White
            };
            var bAdd = new ToolStripButton("➕ 코인 추가") { ForeColor = ThemeManager.Text };
            var bDel = new ToolStripButton("🗑 삭제") { ForeColor = ThemeManager.Text };
            var bAlert = new ToolStripButton("🔔 지정가 알림") { ForeColor = ThemeManager.Text };
            var bRef = new ToolStripButton("🔄 새로고침") { ForeColor = ThemeManager.Text };
            var bCsv = new ToolStripButton("⬇ CSV") { ForeColor = ThemeManager.Text };
            var bComposite = new ToolStripButton("🧠 복합 알림") { ForeColor = ThemeManager.Text };
            tb.Items.AddRange(new ToolStripItem[] { bAdd, bDel, bAlert, bRef, bCsv, bComposite });

            bAdd.Click += async (s, e) => await AddCoinsAsync();
            bDel.Click += async (s, e) => await DeleteSelectedAsync();
            bAlert.Click += (s, e) => AddAlertForSelected();
            bRef.Click += async (s, e) => await PollAsync();
            bCsv.Click += (s, e) => CsvExporter.ExportGrid(_grid, $"coin_holdings_{DateTime.Now:yyyyMMdd}");
            bComposite.Click += (s, e) => { var f = new CompositeAlerts.MainForm(_db); try { ThemeManager.Apply(f); } catch { } f.Show(); };

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var tabKrw = new TabPage("원화 (업비트)") { BackColor = Bg };
            var tabUsdt = new TabPage("USDT (바이낸스)") { BackColor = Bg };

            _grid = BuildKrwGrid();
            _gridUsdt = BuildUsdtGrid();
            tabKrw.Controls.Add(_grid);
            tabUsdt.Controls.Add(_gridUsdt);
            tabs.TabPages.Add(tabKrw);
            tabs.TabPages.Add(tabUsdt);

            var statusStrip = new StatusStrip { BackColor = Panel2, SizingGrip = false };
            _wsBadge = new Label { Text = "● WS 연결 대기", ForeColor = Color.Gray, AutoSize = true };
            _status = new Label { Text = "로딩 중...", ForeColor = Color.Gold, AutoSize = true };
            statusStrip.Items.Add(new ToolStripControlHost(_wsBadge));
            statusStrip.Items.Add(new ToolStripSeparator());
            statusStrip.Items.Add(new ToolStripControlHost(_status));

            Controls.Add(tabs);
            Controls.Add(statusStrip);
            Controls.Add(tb);
        }

        private DataGridView BaseGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = false,
                RowTemplate = { Height = 30 }
            };
            g.ColumnHeadersDefaultCellStyle.Font = new Font("맑은 고딕", 9.5F, FontStyle.Bold);
            g.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            g.DefaultCellStyle.Font = new Font("맑은 고딕", 9.5F);
            ThemeManager.StyleGrid(g);   // 테마 토큰으로 색 일괄 적용(가독성 보장)
            return g;
        }

        private DataGridView BuildKrwGrid()
        {
            var g = BaseGrid();
            g.Columns.Add("market", "코인");
            g.Columns.Add("name", "한글명");
            g.Columns.Add("price", "현재가(₩)");
            g.Columns.Add("chg", "등락률");
            g.Columns.Add("high", "24h고");
            g.Columns.Add("low", "24h저");
            g.Columns.Add("usdt", "바낸($)");
            g.Columns.Add("kimchi", "김프%");
            g.Columns.Add("amt", "거래대금");
            g.Columns.Add("qty", "보유수량");
            g.Columns.Add("avg", "평단가");
            g.Columns.Add("pnl", "평가손익");
            g.Columns.Add("spark", "7일 추세");

            foreach (DataGridViewColumn c in g.Columns) c.ReadOnly = true;
            g.Columns[C_QTY].ReadOnly = false;
            g.Columns[C_AVG].ReadOnly = false;
            g.Columns[C_QTY].DefaultCellStyle.BackColor = ThemeManager.Field;
            g.Columns[C_AVG].DefaultCellStyle.BackColor = ThemeManager.Field;
            for (int i = C_PRICE; i < g.Columns.Count; i++)
                g.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            g.Columns["market"].FillWeight = 70;
            g.Columns["name"].FillWeight = 80;
            g.Columns["spark"].FillWeight = 90;

            g.CellEndEdit += OnHoldingEdited;
            g.CellPainting += OnKrwCellPainting;
            g.CellDoubleClick += async (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex == C_QTY || e.ColumnIndex == C_AVG) return;
                await OpenCoinChartAsync(g.Rows[e.RowIndex].Cells[C_MARKET].Value?.ToString() ?? "");
            };
            return g;
        }

        private void OnKrwCellPainting(object? s, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != C_SPARK) return;
            e.PaintBackground(e.CellBounds, true);
            var market = _grid.Rows[e.RowIndex].Cells[C_MARKET].Value?.ToString() ?? "";
            if (_spark.TryGetValue(market, out var data) && data.Length >= 2)
            {
                var r = e.CellBounds; int pad = 4;
                float x0 = r.X + pad, y0 = r.Y + pad, w = r.Width - pad * 2, h = r.Height - pad * 2;
                decimal min = data.Min(), max = data.Max(); var range = max - min;
                var pts = new System.Drawing.PointF[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    float fx = x0 + (data.Length == 1 ? 0 : w * i / (data.Length - 1));
                    float fy = range == 0 ? y0 + h / 2f : (float)(y0 + h - (double)((data[i] - min) / range) * h);
                    pts[i] = new System.Drawing.PointF(fx, fy);
                }
                var col = data[^1] >= data[0] ? ThemeManager.Up : ThemeManager.Down;
                if (e.Graphics == null) return;
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using var pen = new Pen(col, 1.6f);
                e.Graphics.DrawLines(pen, pts);
            }
            e.Handled = true;
        }

        private async Task LoadSparklinesAsync()
        {
            foreach (var market in _state.Watch.ToList())
            {
                try
                {
                    var candles = await _upbit.GetCandlesAsync(market, 8);
                    if (candles.Count >= 2)
                        _spark[market] = candles.Select(c => c.Close).ToArray();
                }
                catch { }
            }
            if (!IsDisposed) _grid.Invalidate();
        }

        private async Task OpenCoinChartAsync(string market)
        {
            if (string.IsNullOrEmpty(market)) return;
            var candles = await _upbit.GetCandlesAsync(market, 200);
            if (candles.Count < 5) { OpenUpbitChart(market); return; }  // 데이터 부족 시 웹 폴백
            var name = _lastKrw.TryGetValue(market, out var t) ? t.KoreanName : market;
            new RealtimeWatcher.ChartForm($"{name} ({market})", candles).Show();
        }

        private DataGridView BuildUsdtGrid()
        {
            var g = BaseGrid();
            g.Columns.Add("sym", "심볼");
            g.Columns.Add("price", "현재가($)");
            g.Columns.Add("chg", "24h%");
            g.Columns.Add("krw", "원화환산(₩)");
            foreach (DataGridViewColumn c in g.Columns) c.ReadOnly = true;
            for (int i = 1; i < g.Columns.Count; i++)
                g.Columns[i].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            return g;
        }

        // ===== 부트스트랩 / 실시간 =====
        private async Task BootstrapAsync()
        {
            var markets = await _upbit.GetMarketsAsync();
            if (markets.Count > 0)
                _ws.SetNames(markets.ToDictionary(m => m.Market, m => m.KoreanName, StringComparer.OrdinalIgnoreCase));
            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { }
            RebuildKrwRows();
            await PollAsync();                 // 즉시 1회 표시
            await _ws.StartAsync(_state.Watch); // 업비트 실시간 스트리밍
            _ = LoadSparklinesAsync();          // 7일 추세 스파크라인
            _timer.Start();
        }

        private void OnWsTick(CryptoTick t)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => OnWsTick(t))); } catch { } return; }
            _lastKrw[t.Market] = t;
            UpdateKrwRow(t);
            UpdateKimchiFor(t.Market);
            CheckAlerts(t);
            UpdateWsBadge();
        }

        private async Task PollAsync()
        {
            if (_state.Watch.Count == 0) { _status.Text = "코인을 추가하세요  ( ➕ 코인 추가 )"; return; }
            try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { }

            // 업비트: WS 미연결 시 REST 폴백
            if (!_ws.Connected)
            {
                var ticks = await _upbit.GetTickersAsync(_state.Watch);
                foreach (var t in ticks)
                {
                    _lastKrw[t.Market] = t;
                    UpdateKrwRow(t);
                    CheckAlerts(t);
                }
            }

            // 바이낸스 USDT
            var bsyms = _state.Watch.Select(BinanceSym).ToList();
            var bmap = await _binance.Get24hAsync(bsyms);
            foreach (var kv in bmap) _lastUsdt[kv.Key] = kv.Value;

            UpdateUsdtGrid();
            UpdateAllKimchi();
            UpdateWsBadge();
            UpdateStatus();
        }

        private void UpdateWsBadge()
        {
            if (_wsBadge == null) return;
            _wsBadge.Text = _ws.Connected ? "● 업비트 WS 실시간" : "● WS 끊김 (REST 폴백)";
            _wsBadge.ForeColor = _ws.Connected ? Color.FromArgb(70, 200, 120) : Color.Orange;
        }

        private void UpdateStatus()
        {
            decimal totalPnl = 0; bool any = false;
            foreach (var kv in _state.Holdings)
            {
                if (kv.Value.Qty <= 0) continue;
                if (_lastKrw.TryGetValue(kv.Key, out var t)) { totalPnl += (t.Price - kv.Value.Avg) * kv.Value.Qty; any = true; }
            }
            _status.Text = $"🕒 {DateTime.Now:HH:mm:ss} · {_state.Watch.Count}종목 · 환율 {_usdKrw:N0}" +
                (any ? $"   ·   평가손익 합계 {totalPnl:N0} KRW" : "");
        }

        private void UpdateKrwRow(CryptoTick t)
        {
            var row = FindRow(_grid, C_MARKET, t.Market);
            if (row == null) return;
            row.Cells[C_NAME].Value = t.KoreanName;
            row.Cells[C_PRICE].Value = Fmt(t.Price);
            row.Cells[C_CHG].Value = (t.ChangeRate >= 0 ? "+" : "") + (t.ChangeRate * 100m).ToString("F2") + "%";
            row.Cells[C_HIGH].Value = Fmt(t.High24);
            row.Cells[C_LOW].Value = Fmt(t.Low24);
            row.Cells[C_AMT].Value = (t.AccTradePrice24h / 100_000_000m).ToString("N0") + "억";

            var col = t.ChangeRate > 0 ? Up : t.ChangeRate < 0 ? Down : ThemeManager.Text;
            row.Cells[C_PRICE].Style.ForeColor = col;
            row.Cells[C_CHG].Style.ForeColor = col;

            if (_state.Holdings.TryGetValue(t.Market, out var h) && h.Qty > 0)
            {
                var pnl = (t.Price - h.Avg) * h.Qty;
                row.Cells[C_PNL].Value = pnl.ToString("N0");
                row.Cells[C_PNL].Style.ForeColor = pnl > 0 ? Up : pnl < 0 ? Down : ThemeManager.Text;
            }
            else row.Cells[C_PNL].Value = "";
        }

        private void UpdateAllKimchi()
        {
            foreach (var m in _state.Watch) UpdateKimchiFor(m);
        }

        private void UpdateKimchiFor(string market)
        {
            var row = FindRow(_grid, C_MARKET, market);
            if (row == null) return;
            if (!_lastUsdt.TryGetValue(BinanceSym(market), out var b) || b.Price <= 0)
            {
                row.Cells[C_USDT].Value = "-"; row.Cells[C_KIMCHI].Value = "-";
                return;
            }
            row.Cells[C_USDT].Value = "$" + Fmt(b.Price);
            if (_lastKrw.TryGetValue(market, out var k) && k.Price > 0 && _usdKrw > 0)
            {
                var fair = b.Price * _usdKrw;
                var prem = fair > 0 ? (k.Price - fair) / fair * 100m : 0m;
                row.Cells[C_KIMCHI].Value = (prem >= 0 ? "+" : "") + prem.ToString("F2") + "%";
                row.Cells[C_KIMCHI].Style.ForeColor = prem > 0 ? Up : prem < 0 ? Down : ThemeManager.Text;
            }
        }

        private void UpdateUsdtGrid()
        {
            _gridUsdt.Rows.Clear();
            foreach (var m in _state.Watch)
            {
                var sym = BinanceSym(m);
                if (!_lastUsdt.TryGetValue(sym, out var b)) { _gridUsdt.Rows.Add(sym, "-", "-", "-"); continue; }
                int idx = _gridUsdt.Rows.Add(sym, "$" + Fmt(b.Price),
                    (b.ChangePct >= 0 ? "+" : "") + b.ChangePct.ToString("F2") + "%",
                    Fmt(b.Price * _usdKrw));
                var col = b.ChangePct > 0 ? Up : b.ChangePct < 0 ? Down : ThemeManager.Text;
                _gridUsdt.Rows[idx].Cells[1].Style.ForeColor = col;
                _gridUsdt.Rows[idx].Cells[2].Style.ForeColor = col;
            }
        }

        // ===== 알림 =====
        private void CheckAlerts(CryptoTick t)
        {
            bool changed = false;
            foreach (var a in _state.Alerts.Where(a => string.Equals(a.Market, t.Market, StringComparison.OrdinalIgnoreCase)))
            {
                bool cross = a.Above ? t.Price >= a.Target : t.Price <= a.Target;
                if (cross && !a.Triggered)
                {
                    a.Triggered = true; changed = true;
                    var dir = a.Above ? "이상" : "이하";
                    AlertCenter.Instance.RaisePrice(t.Market,
                        $"🪙 {t.KoreanName} 지정가 {dir} 도달",
                        $"{t.KoreanName}({t.Market}) 현재가 {t.Price:N0}원이 목표 {a.Target:N0}원 {dir}입니다.",
                        AlertLevel.Critical);
                }
                else if (!cross && a.Triggered) { a.Triggered = false; changed = true; } // 재무장
            }
            if (changed) SaveState();
        }

        private void AddAlertForSelected()
        {
            if (_grid.SelectedRows.Count == 0) { MessageBox.Show("알림을 설정할 코인을 선택하세요."); return; }
            var market = _grid.SelectedRows[0].Cells[C_MARKET].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(market)) return;
            var cur = _lastKrw.TryGetValue(market, out var t) ? t.Price : 0m;
            using var dlg = new CoinAlertDialog(market, cur);
            if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Result == null) return;
            _state.Alerts.Add(dlg.Result);
            SaveState();
            var dir = dlg.Result.Above ? "이상" : "이하";
            MessageBox.Show($"✅ 알림 등록\n{market} · {dlg.Result.Target:N0}원 {dir}\n(텔레그램 설정 시 도달하면 자동 전송)", "지정가 알림");
        }

        // ===== 감시 목록 =====
        private async Task AddCoinsAsync()
        {
            using var dlg = new AddCoinDialog(_upbit);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            foreach (var m in dlg.Selected)
                if (!_state.Watch.Contains(m)) _state.Watch.Add(m);
            SaveState();
            RebuildKrwRows();
            await _ws.StartAsync(_state.Watch);
            await PollAsync();
        }

        private async Task DeleteSelectedAsync()
        {
            if (_grid.SelectedRows.Count == 0) { MessageBox.Show("삭제할 코인을 선택하세요."); return; }
            var market = _grid.SelectedRows[0].Cells[C_MARKET].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(market)) return;
            _state.Watch.Remove(market);
            _state.Holdings.Remove(market);
            _state.Alerts.RemoveAll(a => string.Equals(a.Market, market, StringComparison.OrdinalIgnoreCase));
            SaveState();
            RebuildKrwRows();
            await _ws.StartAsync(_state.Watch);
            await PollAsync();
        }

        private void RebuildKrwRows()
        {
            _grid.Rows.Clear();
            foreach (var m in _state.Watch)
            {
                var h = _state.Holdings.TryGetValue(m, out var hh) ? hh : null;
                _grid.Rows.Add(m, "", "", "", "", "", "", "", "",
                    h != null && h.Qty > 0 ? h.Qty.ToString(CultureInfo.InvariantCulture) : "",
                    h != null && h.Avg > 0 ? h.Avg.ToString("N0") : "", "");
            }
        }

        private void OnHoldingEdited(object? s, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || (e.ColumnIndex != C_QTY && e.ColumnIndex != C_AVG)) return;
            var market = _grid.Rows[e.RowIndex].Cells[C_MARKET].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(market)) return;
            var qtyStr = (_grid.Rows[e.RowIndex].Cells[C_QTY].Value?.ToString() ?? "").Replace(",", "");
            var avgStr = (_grid.Rows[e.RowIndex].Cells[C_AVG].Value?.ToString() ?? "").Replace(",", "");
            decimal.TryParse(qtyStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var qty);
            decimal.TryParse(avgStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var avg);
            if (qty <= 0 && avg <= 0) _state.Holdings.Remove(market);
            else _state.Holdings[market] = new CryptoHolding { Qty = qty, Avg = avg };
            SaveState();
            if (_lastKrw.TryGetValue(market, out var t)) UpdateKrwRow(t);
            UpdateStatus();
        }

        // ===== 유틸 =====
        private static DataGridViewRow? FindRow(DataGridView g, int col, string val)
        {
            foreach (DataGridViewRow r in g.Rows)
                if (string.Equals(r.Cells[col].Value?.ToString(), val, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        private static string BinanceSym(string krwMarket) =>
            krwMarket.Replace("KRW-", "", StringComparison.OrdinalIgnoreCase).ToUpperInvariant() + "USDT";

        private static string Fmt(decimal p) =>
            p >= 100 ? p.ToString("N0") : p.ToString("0.####", CultureInfo.InvariantCulture);

        private static void OpenUpbitChart(string market)
        {
            if (string.IsNullOrEmpty(market)) return;
            try
            {
                var url = $"https://upbit.com/exchange?code=CRIX.UPBIT.{market}";
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch { }
        }

        private void LoadState()
        {
            try
            {
                var json = _db.GetSetting("Crypto.State");
                if (!string.IsNullOrWhiteSpace(json))
                    _state = JsonSerializer.Deserialize<CryptoState>(json) ?? new CryptoState();
            }
            catch { _state = new CryptoState(); }
            if (_state.Watch.Count == 0)
                _state.Watch = new List<string> { "KRW-BTC", "KRW-ETH", "KRW-XRP", "KRW-SOL", "KRW-DOGE" };
        }

        private void SaveState()
        {
            try { _db.SetSetting("Crypto.State", JsonSerializer.Serialize(_state)); }
            catch (Exception ex) { Log.Warn($"[Crypto] 상태 저장 실패: {ex.Message}"); }
        }
    }

    // ===== 코인 추가 다이얼로그 =====
    public class AddCoinDialog : Form
    {
        public List<string> Selected { get; } = new();
        private readonly UpbitProvider _up;
        private List<CryptoMarket> _all = new();
        private TextBox _search = null!;
        private ListBox _list = null!;

        public AddCoinDialog(UpbitProvider up)
        {
            _up = up;
            InitUI();
            Shown += async (s, e) => await LoadAsync();
        }

        private void InitUI()
        {
            Text = "➕ 코인 추가 (업비트 KRW)";
            Size = new Size(440, 540);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            BackColor = Color.FromArgb(30, 34, 44);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);

            _search = new TextBox
            {
                Location = new Point(16, 16), Width = 396, Height = 26,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            _search.TextChanged += (s, e) => Render(_search.Text.Trim());
            Controls.Add(_search);
            Controls.Add(new Label
            {
                Text = "이름/코드 검색 · Ctrl+클릭으로 다중 선택",
                Location = new Point(16, 46), AutoSize = true, ForeColor = Color.Gray
            });

            _list = new ListBox
            {
                Location = new Point(16, 70), Size = new Size(396, 380),
                SelectionMode = SelectionMode.MultiExtended,
                BackColor = Color.FromArgb(38, 43, 54), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_list);

            var ok = new Button
            {
                Text = "추가", DialogResult = DialogResult.OK,
                Location = new Point(228, 462), Size = new Size(85, 32),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 130, 200), ForeColor = Color.White
            };
            var cancel = new Button
            {
                Text = "취소", DialogResult = DialogResult.Cancel,
                Location = new Point(323, 462), Size = new Size(85, 32),
                FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 66, 80), ForeColor = Color.White
            };
            ok.Click += (s, e) => CollectSelection();
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ThemeManager.Apply(this);   // 테마(블랙/화이트) 일괄 적용
        }

        private async Task LoadAsync()
        {
            _list.Items.Clear();
            _list.Items.Add("불러오는 중...");
            _all = await _up.GetMarketsAsync();
            Render("");
        }

        private void Render(string filter)
        {
            _list.Items.Clear();
            foreach (var m in _all)
            {
                if (filter.Length > 0 &&
                    !m.KoreanName.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !m.Market.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                _list.Items.Add($"{m.KoreanName}  ({m.Market})");
            }
        }

        private void CollectSelection()
        {
            Selected.Clear();
            foreach (var item in _list.SelectedItems)
            {
                var text = item?.ToString() ?? "";
                int a = text.LastIndexOf('('), b = text.LastIndexOf(')');
                if (a >= 0 && b > a) Selected.Add(text.Substring(a + 1, b - a - 1));
            }
        }
    }

    // ===== 코인 지정가 알림 다이얼로그 =====
    public class CoinAlertDialog : Form
    {
        public CoinAlert? Result { get; private set; }
        private readonly string _market;
        private readonly ComboBox _cond;
        private readonly NumericUpDown _target;

        public CoinAlertDialog(string market, decimal current)
        {
            _market = market;
            Text = "🔔 지정가 알림";
            Size = new Size(380, 240);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            BackColor = Color.FromArgb(30, 34, 44);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);

            Controls.Add(new Label { Text = $"{market}  (현재 {current:N0}원)", Location = new Point(20, 18), AutoSize = true, ForeColor = Color.Gainsboro });
            Controls.Add(new Label { Text = "조건", Location = new Point(20, 60), AutoSize = true, ForeColor = Color.LightGray });
            _cond = new ComboBox
            {
                Location = new Point(120, 56), Width = 220, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            _cond.Items.AddRange(new object[] { "현재가 이상", "현재가 이하" });
            _cond.SelectedIndex = 0;
            Controls.Add(_cond);

            Controls.Add(new Label { Text = "목표가(원)", Location = new Point(20, 102), AutoSize = true, ForeColor = Color.LightGray });
            _target = new NumericUpDown
            {
                Location = new Point(120, 98), Width = 220, DecimalPlaces = 0, ThousandsSeparator = true,
                Minimum = 0, Maximum = 1_000_000_000, Increment = 1000, Value = (decimal)Math.Max(0, (double)current),
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_target);

            var ok = new Button { Text = "등록", Location = new Point(150, 150), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 130, 200), ForeColor = Color.White };
            var cancel = new Button { Text = "취소", Location = new Point(250, 150), Size = new Size(90, 32), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 66, 80), ForeColor = Color.White };
            ok.Click += (s, e) =>
            {
                Result = new CoinAlert { Market = _market, Above = _cond.SelectedIndex == 0, Target = _target.Value };
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
            ThemeManager.Apply(this);   // 테마(블랙/화이트) 일괄 적용
        }
    }
}
