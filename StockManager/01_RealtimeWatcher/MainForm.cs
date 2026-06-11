// ============================================================================
// Program #1: RealtimeWatcher (v2.2)
// [개선점]
//  · GdiCache 사용 (Font/Brush 누수 제거)
//  · 타이머 Interval 변경 시 즉시 재시작
//  · AlertCenter 통합 (가격 알림)
//  · 52주 신고가 스캔 버튼 (기능 E)
//  · 죽은 코드 Let<T> 제거
//  · 타임스탬프 실제 DataSource.Timestamp 반영
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.RealtimeWatcher
{
    public class MainForm : Form
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly FxConverter _fx = new();
        private readonly ConcurrentDictionary<string, Quote> _quotes = new();
        private readonly ConcurrentDictionary<string, decimal> _prevPrices = new();

        private TabControl _tabGroups = null!;
        private DataGridView _grid = null!;
        private Panel _heatmapPanel = null!;
        private ToolStripStatusLabel _slStatus = null!;
        private ToolStripStatusLabel _slFx = null!;
        private ToolStripStatusLabel _slAlerts = null!;
        private ToolStripStatusLabel _slClock = null!;
        private ToolStrip _toolbar = null!;
        private System.Windows.Forms.Timer _refreshTimer = null!;
        private System.Windows.Forms.Timer _flashTimer = null!;
        private System.Windows.Forms.Timer _clockTimer = null!;
        private CancellationTokenSource _cts = new();

        private readonly Dictionary<string, DateTime> _flashUntil = new();

        public MainForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            InitializeComponent();
            _provider.QuoteUpdated += OnRealtimeQuote;
            AlertCenter.Instance.AlertRaised += OnAlert;
        }

        private void InitializeComponent()
        {
            Text = "📈 실시간 시세 대시보드 - MS2-E Stock Suite";
            Size = new Size(1600, 950);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            // ===== 툴바 =====
            _toolbar = new ToolStrip
            {
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(8, 4, 8, 4),
                ImageScalingSize = new Size(20, 20),
                RenderMode = ToolStripRenderMode.System
            };

            var btnAdd = new ToolStripButton("➕ 종목 추가") { ForeColor = Color.White };
            var btnDel = new ToolStripButton("🗑 삭제") { ForeColor = Color.White };
            var btnChart = new ToolStripButton("📊 차트") { ForeColor = Color.White };
            var btnAlert = new ToolStripButton("🔔 알림 규칙") { ForeColor = Color.White };
            var btnScan52 = new ToolStripButton("🔥 52주 신고/신저가 스캔") { ForeColor = Color.Gold };
            btnScan52.Click += async (s, e) => await ScanExtremesAsync();
            btnChart.Click += (s, e) => OpenChartForSelectedRow();
            btnAdd.Click += async (s, e) => await AddSymbolAsync();
            btnDel.Click += async (s, e) => await DeleteSelectedSymbolAsync();
            btnAlert.Click += (s, e) => AddAlertRuleForSelection();

            _toolbar.Items.AddRange(new ToolStripItem[] {
                btnAdd, btnDel, new ToolStripSeparator(),
                btnChart, btnScan52, btnAlert, new ToolStripSeparator(),
                new ToolStripLabel("갱신주기:") { ForeColor = Color.LightGray }
            });

            var intervalCombo = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            intervalCombo.Items.AddRange(new[] { "1초", "3초", "5초", "10초", "30초" });
            intervalCombo.SelectedIndex = 1;
            intervalCombo.SelectedIndexChanged += async (s, e) =>
            {
                var sec = int.Parse(intervalCombo.SelectedItem!.ToString()!.Replace("초", ""));
                // ⚠️ 수정: Interval 변경 즉시 반영되도록 Stop → 재시작
                _refreshTimer.Stop();
                _refreshTimer.Interval = sec * 1000;
                _refreshTimer.Start();
                await RefreshQuotesAsync(); // 즉시 한 번 갱신
            };
            _toolbar.Items.Add(intervalCombo);

            // ===== 좌측 탭 =====
            _tabGroups = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.FlatButtons,
                SizeMode = TabSizeMode.Fixed,
                ItemSize = new Size(120, 32),
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };

            foreach (var group in new[] { "⭐ 즐겨찾기", "🇰🇷 국내주식", "🇺🇸 미국주식", "💎 반도체", "🔋 2차전지" })
                _tabGroups.TabPages.Add(new TabPage(group) { BackColor = Color.FromArgb(30, 34, 44) });
            _tabGroups.SelectedIndexChanged += async (s, e) =>
            {
                MoveGridToCurrentTab();   // ⚠️ 수정(C2): 선택된 탭으로 그리드 이동 (빈 화면 방지)
                await ReloadGroupAsync();
            };

            _grid = BuildGrid();
            _tabGroups.TabPages[0].Controls.Add(_grid);

            // ===== 우측 히트맵 =====
            _heatmapPanel = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 22, 30),
                Padding = new Padding(10)
            };
            _heatmapPanel.Paint += DrawHeatmap;

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 4,
                BackColor = Color.FromArgb(60, 65, 75)
            };
            split.Panel1.Controls.Add(_tabGroups);
            split.Panel2.Controls.Add(_heatmapPanel);
            split.SplitterDistance = 1100;

            // ===== 상태바 =====
            var statusStrip = new StatusStrip
            {
                BackColor = Color.FromArgb(40, 46, 58),
                SizingGrip = false,
                RenderMode = ToolStripRenderMode.System
            };
            _slStatus = new ToolStripStatusLabel("준비됨") { ForeColor = Color.LightGreen };
            var spring = new ToolStripStatusLabel { Spring = true };
            _slAlerts = new ToolStripStatusLabel("🔔 0") { ForeColor = Color.Gold };
            _slAlerts.Click += (s, e) => ShowAlertCenter();
            _slAlerts.IsLink = true;
            _slFx = new ToolStripStatusLabel("USD/KRW: --") { ForeColor = Color.Gold };
            _slClock = new ToolStripStatusLabel(DateTime.Now.ToString("F")) { ForeColor = Color.LightGray };
            statusStrip.Items.AddRange(new ToolStripItem[] { _slStatus, spring, _slAlerts, _slFx, _slClock });

            Controls.Add(split);
            Controls.Add(statusStrip);
            Controls.Add(_toolbar);

            // ===== 타이머 =====
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _refreshTimer.Tick += async (s, e) => await RefreshQuotesAsync();

            _flashTimer = new System.Windows.Forms.Timer { Interval = 100 };
            _flashTimer.Tick += (s, e) => _grid.Invalidate();

            _clockTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _clockTimer.Tick += (s, e) =>
            {
                _slClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                _slAlerts.Text = $"🔔 {AlertCenter.Instance.UnreadCount}";
            };

            Load += async (s, e) =>
            {
                _slFx.Text = $"USD/KRW: {await _fx.GetUsdKrwAsync():N2}";
                await ReloadGroupAsync();
                _refreshTimer.Start();
                _flashTimer.Start();
                _clockTimer.Start();
            };
            FormClosing += (s, e) =>
            {
                _cts.Cancel();
                _refreshTimer.Stop(); _flashTimer.Stop(); _clockTimer.Stop();
                _provider.QuoteUpdated -= OnRealtimeQuote;
                AlertCenter.Instance.AlertRaised -= OnAlert;
            };
        }

        private DataGridView BuildGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                RowTemplate = { Height = 32 },
                Font = new Font("맑은 고딕", 10F)
            };

            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter,
                Padding = new Padding(4)
            };
            g.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(70, 120, 200),
                SelectionForeColor = Color.White,
                Padding = new Padding(6, 0, 6, 0)
            };
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(36, 40, 52)
            };

            string[] cols = { "종목코드", "종목명", "현재가", "전일대비", "등락률",
                              "시가", "고가", "저가", "거래량", "시가총액", "소스", "시각" };
            string[] names = { "Symbol", "Name", "Price", "Change", "ChangeRate",
                               "Open", "High", "Low", "Volume", "MarketCap", "Source", "Time" };
            for (int i = 0; i < cols.Length; i++)
                g.Columns.Add(names[i], cols[i]);

            g.CellFormatting += FormatCell;
            g.CellPainting += PaintFlash;
            g.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var sym = g.Rows[e.RowIndex].Cells["Symbol"].Value?.ToString() ?? "";
                ShowChartForSymbol(sym);
            };
            return g;
        }

        private void FormatCell(object? s, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.Value == null) return;
            var col = _grid.Columns[e.ColumnIndex].Name;
            var sym = _grid.Rows[e.RowIndex].Cells["Symbol"].Value?.ToString() ?? "";
            if (!_quotes.TryGetValue(sym, out var q)) return;

            if (col == "Change" || col == "ChangeRate")
            {
                e.CellStyle!.ForeColor = Formatter.ColorForChange(q.ChangeRate);
                e.CellStyle.Font = GdiCache.MediumBold;
                e.Value = col == "Change"
                    ? $"{(q.Change >= 0 ? "▲" : "▼")} {Math.Abs(q.Change):N0}"
                    : Formatter.Pct(q.ChangeRate);
                e.FormattingApplied = true;
            }
            else if (col == "Price")
            {
                e.CellStyle!.Font = GdiCache.MediumBold;
                e.CellStyle.ForeColor = Formatter.ColorForChange(q.ChangeRate);
                e.Value = Formatter.Money(q.Price, q.Currency);
                e.FormattingApplied = true;
            }
            else if (col == "Volume")
            {
                e.Value = q.Volume.ToString("N0");
                e.FormattingApplied = true;
            }
            else if (col == "MarketCap")
            {
                e.Value = Formatter.HumanizeNumber(q.MarketCap);
                e.FormattingApplied = true;
            }
            else if (col == "Time")
            {
                e.Value = q.Timestamp == default ? "" : q.Timestamp.ToString("HH:mm:ss");
                e.FormattingApplied = true;
            }
        }

        private void PaintFlash(object? s, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
            var sym = _grid.Rows[e.RowIndex].Cells["Symbol"].Value?.ToString() ?? "";
            if (!_flashUntil.TryGetValue(sym, out var until) || DateTime.Now >= until) return;

            if (_quotes.TryGetValue(sym, out var q))
            {
                var alpha = (int)(255 * (until - DateTime.Now).TotalMilliseconds / 800);
                alpha = Math.Clamp(alpha, 0, 180);
                var col = q.ChangeRate > 0
                    ? Color.FromArgb(alpha, 220, 50, 50)
                    : Color.FromArgb(alpha, 50, 90, 200);
                // Brush는 캐시 불가 (alpha 변함) → using으로 명시 해제
                using var br = new SolidBrush(col);
                e.Graphics!.FillRectangle(br, e.CellBounds);
                e.PaintContent(e.CellBounds);
                e.Handled = true;
            }
        }

        private async Task ReloadGroupAsync()
        {
            var group = _tabGroups.SelectedTab?.Text ?? "";
            _grid.Rows.Clear();

            var watchlist = _db.Query(
                "SELECT Symbol, Name, Market FROM Watchlist WHERE GroupName=@g ORDER BY SortOrder",
                new() { ["@g"] = group });

            if (watchlist.Rows.Count == 0)
            {
                var samples = group.Contains("미국") ? SampleData.USTop : SampleData.KoreanTop;
                foreach (var (s, n, _) in samples.Take(10))
                    _grid.Rows.Add(s, n, "", "", "", "", "", "", "", "", "", "");
            }
            else
            {
                foreach (System.Data.DataRow r in watchlist.Rows)
                    _grid.Rows.Add(Safe.Str(r["Symbol"]), Safe.Str(r["Name"]), "", "", "", "", "", "", "", "", "", "");
            }
            await RefreshQuotesAsync();
        }

        private async Task RefreshQuotesAsync()
        {
            if (_cts.IsCancellationRequested) return;
            try
            {
                var symbols = new List<(string sym, Market mkt)>();
                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var s = row.Cells["Symbol"].Value?.ToString() ?? "";
                    if (string.IsNullOrEmpty(s)) continue;
                    symbols.Add((s, DetectMarket(s)));
                }

                foreach (var group in symbols.GroupBy(x => x.mkt))
                {
                    var quotes = await _provider.GetQuotesAsync(
                        group.Select(x => x.sym), group.Key, _cts.Token);
                    foreach (var q in quotes)
                    {
                        if (_prevPrices.TryGetValue(q.Symbol, out var prev) && prev != q.Price)
                        {
                            _flashUntil[q.Symbol] = DateTime.Now.AddMilliseconds(800);
                            CheckPriceAlerts(q, prev);
                        }
                        _prevPrices[q.Symbol] = q.Price;
                        _quotes[q.Symbol] = q;
                    }
                }

                foreach (DataGridViewRow row in _grid.Rows)
                {
                    var sym = row.Cells["Symbol"].Value?.ToString() ?? "";
                    if (_quotes.TryGetValue(sym, out var q))
                    {
                        row.Cells["Name"].Value = q.Name;
                        row.Cells["Price"].Value = q.Price;
                        row.Cells["Change"].Value = q.Change;
                        row.Cells["ChangeRate"].Value = q.ChangeRate;
                        row.Cells["Open"].Value = q.Open.ToString("N0");
                        row.Cells["High"].Value = q.High.ToString("N0");
                        row.Cells["Low"].Value = q.Low.ToString("N0");
                        row.Cells["Volume"].Value = q.Volume;
                        row.Cells["MarketCap"].Value = q.MarketCap;
                        row.Cells["Source"].Value = q.Source.ToString();
                        row.Cells["Time"].Value = q.Timestamp;
                    }
                }
                _heatmapPanel.Invalidate();
                _slStatus.Text = $"✅ {_quotes.Count}개 종목 갱신됨 · {DateTime.Now:HH:mm:ss}";
                _slStatus.ForeColor = Color.LightGreen;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Error("Refresh 실패", ex);
                _slStatus.Text = "⚠️ 갱신 실패";
                _slStatus.ForeColor = Color.Salmon;
            }
        }

        private void CheckPriceAlerts(Quote q, decimal prev)
        {
            // 5% 이상 급변
            if (prev > 0)
            {
                var pct = (q.Price - prev) / prev * 100m;
                if (Math.Abs(pct) >= 5)
                {
                    AlertCenter.Instance.RaisePrice(q.Symbol,
                        $"급변동 감지 · {q.Symbol} {q.Name}",
                        $"{Formatter.Pct(pct)} ({Formatter.Money(prev, q.Currency)} → {Formatter.Money(q.Price, q.Currency)})",
                        AlertLevel.Warning);
                }
            }

            // DB 등록된 알림 규칙 체크
            var rules = _db.Query(
                "SELECT * FROM AlertRules WHERE Symbol=@s AND IsActive=1 AND IsTriggered=0",
                new() { ["@s"] = q.Symbol });
            foreach (System.Data.DataRow r in rules.Rows)
            {
                var cond = (AlertCondition)Safe.Int(r["Condition"]);
                var target = Safe.Dec(r["TargetValue"]);
                bool hit = cond switch
                {
                    AlertCondition.PriceAbove => q.Price >= target,
                    AlertCondition.PriceBelow => q.Price <= target,
                    AlertCondition.ChangeRateAbove => q.ChangeRate >= target,
                    AlertCondition.ChangeRateBelow => q.ChangeRate <= target,
                    _ => false
                };
                if (hit)
                {
                    AlertCenter.Instance.RaisePrice(q.Symbol,
                        $"알림 달성 · {q.Name}",
                        $"{cond} {target} (현재 {Formatter.Money(q.Price, q.Currency)})",
                        AlertLevel.Critical);
                    _db.Execute("UPDATE AlertRules SET IsTriggered=1 WHERE Id=@id",
                        new() { ["@id"] = Safe.Int(r["Id"]) });
                }
            }
        }

        private void OnRealtimeQuote(object? sender, Quote q)
        {
            if (InvokeRequired) { BeginInvoke(() => OnRealtimeQuote(sender, q)); return; }
            _quotes[q.Symbol] = q;
            _flashUntil[q.Symbol] = DateTime.Now.AddMilliseconds(800);
        }

        private void OnAlert(object? sender, AlertItem a)
        {
            if (InvokeRequired) { BeginInvoke(() => OnAlert(sender, a)); return; }
            if (a.Level >= AlertLevel.Warning)
                TrayNotifier.Show(a);
        }

        private static Market DetectMarket(string symbol) =>
            symbol.Length == 6 && char.IsDigit(symbol[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ;

        private void DrawHeatmap(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _heatmapPanel.ClientRectangle;

            g.DrawString("🔥 실시간 히트맵", GdiCache.XLarge, Brushes.White, 10, 10);

            var quotes = _quotes.Values.Where(q => q.Price > 0).ToList();
            if (quotes.Count == 0) return;

            var y = 50;
            var cellW = (rect.Width - 20) / 3;
            var cellH = 80;
            int i = 0;
            foreach (var q in quotes.OrderByDescending(x => Math.Abs(x.ChangeRate)).Take(12))
            {
                var x = 10 + (i % 3) * cellW;
                var yy = y + (i / 3) * (cellH + 5);
                var intensity = (int)Math.Min(255, 100 + Math.Abs(q.ChangeRate) * 20);
                var color = q.ChangeRate > 0 ? Color.FromArgb(intensity, 200, 50, 50)
                         : q.ChangeRate < 0 ? Color.FromArgb(intensity, 50, 90, 200)
                         : Color.Gray;

                g.FillRectangle(GdiCache.Brush(color), x, yy, cellW - 5, cellH);
                g.DrawRectangle(Pens.Black, x, yy, cellW - 5, cellH);
                g.DrawString(q.Name, GdiCache.NormalBold, Brushes.White, x + 5, yy + 5);
                g.DrawString(Formatter.Pct(q.ChangeRate), GdiCache.LargeBold, Brushes.White, x + 5, yy + 25);
                g.DrawString(Formatter.Money(q.Price, q.Currency), GdiCache.Normal, Brushes.White, x + 5, yy + 50);
                i++;
            }
        }

        private void OpenChartForSelectedRow()
        {
            if (_grid.SelectedRows.Count == 0) return;
            var sym = _grid.SelectedRows[0].Cells["Symbol"].Value?.ToString() ?? "";
            if (!string.IsNullOrEmpty(sym)) ShowChartForSymbol(sym);
        }

        private void ShowChartForSymbol(string symbol)
        {
            var form = new ChartForm(_provider, symbol, DetectMarket(symbol));
            form.Show();
        }

        private async Task ScanExtremesAsync()
        {
            _slStatus.Text = "🔍 52주 신고/신저가 스캔 중...";
            _slStatus.ForeColor = Color.Gold;
            var events = await ExtremeScanner.ScanAsync(_provider, SampleData.All(), _cts.Token);
            foreach (var ev in events)
            {
                AlertCenter.Instance.RaisePrice(ev.Symbol,
                    $"{(ev.IsHigh ? "📈 52주 신고가" : "📉 52주 신저가")} · {ev.Name}",
                    $"현재 {ev.CurrentPrice:N0} · 기존 {ev.Extreme52:N0} (Δ {Formatter.Pct(ev.ChangePct)})",
                    AlertLevel.Warning);
            }
            _slStatus.Text = $"✅ 52주 스캔 완료: 신고가 {events.Count(x => x.IsHigh)} · 신저가 {events.Count(x => !x.IsHigh)}";
            _slStatus.ForeColor = Color.LightGreen;
        }

        private void ShowAlertCenter()
        {
            new AlertCenterForm(_db).Show();
        }

        // ===== C2: 그리드를 현재 선택 탭으로 이동 =====
        private void MoveGridToCurrentTab()
        {
            var tab = _tabGroups.SelectedTab;
            if (tab == null || _grid.Parent == tab) return;
            _grid.Parent?.Controls.Remove(_grid);
            tab.Controls.Add(_grid);
        }

        // ===== H1: 종목 추가 =====
        private async Task AddSymbolAsync()
        {
            var sym = Prompt("종목 추가", "종목코드 입력 (예: 005930 또는 AAPL)");
            if (string.IsNullOrWhiteSpace(sym)) return;
            sym = sym.Trim().ToUpperInvariant();
            var group = _tabGroups.SelectedTab?.Text ?? "";
            var name = SectorMap.NameOf(sym);
            _db.Execute(@"INSERT INTO Watchlist(Symbol,Name,Market,GroupName,SortOrder)
                          VALUES(@s,@n,@m,@g,0)
                          ON CONFLICT(Symbol,GroupName) DO UPDATE SET Name=@n",
                new() { ["@s"] = sym, ["@n"] = name, ["@m"] = (int)DetectMarket(sym), ["@g"] = group });
            await ReloadGroupAsync();
        }

        // ===== H1: 선택 종목 삭제 =====
        private async Task DeleteSelectedSymbolAsync()
        {
            if (_grid.SelectedRows.Count == 0) { MessageBox.Show("삭제할 종목을 선택하세요."); return; }
            var sym = _grid.SelectedRows[0].Cells["Symbol"].Value?.ToString() ?? "";
            if (string.IsNullOrEmpty(sym)) return;
            var group = _tabGroups.SelectedTab?.Text ?? "";
            _db.Execute("DELETE FROM Watchlist WHERE Symbol=@s AND GroupName=@g",
                new() { ["@s"] = sym, ["@g"] = group });
            await ReloadGroupAsync();
        }

        // ===== H1: 선택 종목 알림 규칙 등록 =====
        private void AddAlertRuleForSelection()
        {
            var sym = _grid.SelectedRows.Count > 0
                ? _grid.SelectedRows[0].Cells["Symbol"].Value?.ToString() ?? "" : "";
            using var dlg = new AlertRuleDialog(sym);
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
            {
                var r = dlg.Result;
                _db.Execute(@"INSERT INTO AlertRules(Symbol,Condition,TargetValue,IsActive,IsTriggered,CreatedAt,Channel)
                              VALUES(@s,@c,@t,1,0,@d,@ch)",
                    new() {
                        ["@s"] = r.Symbol, ["@c"] = (int)r.Condition,
                        ["@t"] = (double)r.TargetValue, ["@d"] = DateTime.Now.ToString("o"),
                        ["@ch"] = (int)r.Channel
                    });
                MessageBox.Show($"✅ 알림 규칙 등록\n{r.Symbol} · {r.Condition} · {r.TargetValue:N0}", "알림 규칙");
            }
        }

        // 간단 입력 프롬프트
        private static string Prompt(string title, string label)
        {
            using var f = new Form
            {
                Text = title, Size = new Size(420, 170),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false, MinimizeBox = false,
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White
            };
            var lbl = new Label { Text = label, Location = new Point(15, 15), AutoSize = true, ForeColor = Color.LightGray };
            var txt = new TextBox
            {
                Location = new Point(15, 45), Width = 370,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Location = new Point(210, 85), Size = new Size(85, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 130, 200), ForeColor = Color.White };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(300, 85), Size = new Size(85, 30), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 70), ForeColor = Color.White };
            f.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? txt.Text : "";
        }
    }

    // ===== 알림 규칙 입력 다이얼로그 (H1) =====
    public class AlertRuleDialog : Form
    {
        public AlertRule? Result { get; private set; }
        private readonly TextBox _txtSymbol;
        private readonly ComboBox _cbCond;
        private readonly NumericUpDown _nTarget;
        private readonly ComboBox _cbChannel;

        public AlertRuleDialog(string symbol)
        {
            Text = "🔔 알림 규칙 추가";
            Size = new Size(440, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            BackColor = Color.FromArgb(30, 34, 44);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 10F);

            void Lbl(string t, int y) => Controls.Add(new Label { Text = t, Location = new Point(20, y + 4), AutoSize = true, ForeColor = Color.LightGray });

            Lbl("종목코드", 20);
            _txtSymbol = new TextBox
            {
                Location = new Point(140, 18), Width = 250, Text = symbol,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_txtSymbol);

            Lbl("조건", 60);
            _cbCond = new ComboBox
            {
                Location = new Point(140, 58), Width = 250,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            _cbCond.Items.AddRange(new object[] { "현재가 이상", "현재가 이하", "등락률 이상(%)", "등락률 이하(%)" });
            _cbCond.SelectedIndex = 0;
            Controls.Add(_cbCond);

            Lbl("기준값", 100);
            _nTarget = new NumericUpDown
            {
                Location = new Point(140, 98), Width = 250,
                Minimum = -1_000_000_000, Maximum = 1_000_000_000, DecimalPlaces = 2, Increment = 100,
                ThousandsSeparator = true,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(_nTarget);

            Lbl("채널", 140);
            _cbChannel = new ComboBox
            {
                Location = new Point(140, 138), Width = 250,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(45, 50, 62), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            _cbChannel.Items.AddRange(new object[] { "데스크톱", "텔레그램", "전체" });
            _cbChannel.SelectedIndex = 0;
            Controls.Add(_cbChannel);

            var ok = new Button { Text = "✅ 등록", Location = new Point(190, 200), Size = new Size(95, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(70, 130, 200), ForeColor = Color.White };
            var cancel = new Button { Text = "취소", Location = new Point(295, 200), Size = new Size(95, 34), FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(60, 60, 70), ForeColor = Color.White };
            ok.Click += OnOk;
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { ok, cancel });
            AcceptButton = ok; CancelButton = cancel;
        }

        private void OnOk(object? s, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_txtSymbol.Text)) { MessageBox.Show("종목코드를 입력하세요."); return; }
            var cond = _cbCond.SelectedIndex switch
            {
                0 => AlertCondition.PriceAbove,
                1 => AlertCondition.PriceBelow,
                2 => AlertCondition.ChangeRateAbove,
                _ => AlertCondition.ChangeRateBelow
            };
            var channel = _cbChannel.SelectedIndex switch
            {
                1 => AlertChannel.Telegram,
                2 => AlertChannel.All,
                _ => AlertChannel.Desktop
            };
            Result = new AlertRule
            {
                Symbol = _txtSymbol.Text.Trim().ToUpperInvariant(),
                Condition = cond,
                TargetValue = _nTarget.Value,
                Channel = channel,
                IsActive = true,
                CreatedAt = DateTime.Now
            };
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
