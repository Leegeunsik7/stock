// ============================================================================
// Program #2: Portfolio Manager (v2.2)
// [개선점]
//  · Safe.Dec/Int 적용 (DBNull 예외 방지)
//  · GdiCache 적용 (Paint 이벤트 GDI+ 누수 제거)
//  · RecomputeAverage 0 나누기 가드
//  · HTS CSV 헤더 매핑 (키움/한투/삼성 자동 감지)
//  · 세금 시뮬레이터 / 배당 캘린더 / 상관관계 매트릭스 탭 추가
//  · 샘플 데이터 → SampleData 공유
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.Portfolio
{
    public class MainForm : Form
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;
        private readonly FxConverter _fx = new();
        private decimal _usdKrw = 1380m;  // RefreshAllAsync에서 비동기 갱신 (Paint/동기 메서드가 공유)

        private TabControl _tabs = null!;
        private DataGridView _holdingsGrid = null!;
        private DataGridView _txGrid = null!;
        private Panel _summaryPanel = null!;
        private Panel _pieChart = null!;
        private Panel _perfChart = null!;

        private Label _lblTotalKrw = null!;
        private Label _lblUnrealized = null!;
        private Label _lblRealized = null!;
        private Label _lblCount = null!;

        public MainForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            InitializeUI();
            Load += async (s, e) => await RefreshAllAsync();
        }

        private void InitializeUI()
        {
            Text = "💼 포트폴리오 매니저 - MS2-E Stock Suite";
            Size = new Size(1600, 950);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            _summaryPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 110,
                BackColor = Color.FromArgb(30, 34, 44),
                Padding = new Padding(15, 10, 15, 10)
            };
            _summaryPanel.Paint += DrawSummaryCards;

            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.FlatButtons,
                ItemSize = new Size(140, 34),
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };

            var holdingsTab = new TabPage("📊 보유 종목") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildHoldingsTab(holdingsTab);

            var txTab = new TabPage("📝 거래 이력") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildTransactionsTab(txTab);

            var perfTab = new TabPage("📈 수익률 분석") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildPerformanceTab(perfTab);

            var allocTab = new TabPage("🥧 자산 배분") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildAllocationTab(allocTab);

            // 기능 C: 세금 시뮬레이터
            var taxTab = new TabPage("💰 세금 시뮬") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildTaxTab(taxTab);

            // 기능 D: 배당 캘린더
            var divTab = new TabPage("💸 배당 캘린더") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildDividendTab(divTab);

            // 기능 F: 상관관계
            var corrTab = new TabPage("🔗 상관관계") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildCorrelationTab(corrTab);

            _tabs.TabPages.AddRange(new[] { holdingsTab, txTab, perfTab, allocTab, taxTab, divTab, corrTab });

            Controls.Add(_tabs);
            Controls.Add(_summaryPanel);
        }

        // ===== 보유 종목 =====
        private void BuildHoldingsTab(TabPage tab)
        {
            var toolbar = new ToolStrip
            {
                BackColor = Color.FromArgb(40, 46, 58),
                ForeColor = Color.White,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(8, 4, 8, 4),
                RenderMode = ToolStripRenderMode.System
            };

            var btnBuy = new ToolStripButton("➕ 매수 기록") { ForeColor = Color.LightGreen };
            var btnSell = new ToolStripButton("➖ 매도 기록") { ForeColor = Color.Salmon };
            var btnRefresh = new ToolStripButton("🔄 시세 갱신") { ForeColor = Color.White };
            var btnExport = new ToolStripButton("📥 내보내기") { ForeColor = Color.White };
            var btnImport = new ToolStripButton("📤 HTS 임포트") { ForeColor = Color.Gold };

            btnBuy.Click += (s, e) => ShowTransactionDialog(TransactionType.Buy);
            btnSell.Click += (s, e) => ShowTransactionDialog(TransactionType.Sell);
            btnRefresh.Click += async (s, e) => await RefreshAllAsync();
            btnExport.Click += (s, e) => ExportHoldings();
            btnImport.Click += (s, e) => ImportFromHts();

            toolbar.Items.AddRange(new ToolStripItem[] { btnBuy, btnSell, new ToolStripSeparator(), btnRefresh, btnImport, btnExport });

            _holdingsGrid = BuildHoldingsGrid();
            tab.Controls.Add(_holdingsGrid);
            tab.Controls.Add(toolbar);
        }

        private DataGridView BuildHoldingsGrid()
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
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 32 },
                Font = new Font("맑은 고딕", 10F)
            };
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleCenter
            };
            g.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(70, 120, 200),
                Padding = new Padding(6, 0, 6, 0)
            };
            g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(36, 40, 52) };

            foreach (var (nm, col) in new[] {
                ("Symbol","종목코드"),("Name","종목명"),("Qty","수량"),("Avg","평균단가"),("Cur","현재가"),
                ("Invested","투자원금"),("Value","평가금액"),("Gain","평가손익"),("Rate","수익률"),("Weight","비중")
            })
                g.Columns.Add(nm, col);
            g.CellFormatting += FormatHoldingsCell;
            return g;
        }

        private void FormatHoldingsCell(object? s, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.Value == null) return;
            var col = _holdingsGrid.Columns[e.ColumnIndex].Name;
            if (col == "Gain" || col == "Rate")
            {
                if (decimal.TryParse(e.Value.ToString()?.Replace(",", "").Replace("%", "").Replace("+", ""), out var v))
                {
                    e.CellStyle!.ForeColor = Formatter.ColorForChange(v);
                    e.CellStyle.Font = GdiCache.MediumBold;
                }
            }
        }

        // ===== 거래 이력 =====
        private void BuildTransactionsTab(TabPage tab)
        {
            _txGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 30 }
            };
            _txGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            _txGrid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(70, 120, 200)
            };
            tab.Controls.Add(_txGrid);
        }

        // ===== 수익률 차트 =====
        private void BuildPerformanceTab(TabPage tab)
        {
            _perfChart = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 28, 36) };
            _perfChart.Paint += DrawPerformanceChart;
            tab.Controls.Add(_perfChart);
        }

        // ===== 자산 배분 =====
        private void BuildAllocationTab(TabPage tab)
        {
            _pieChart = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 28, 36) };
            _pieChart.Paint += DrawAllocation;
            tab.Controls.Add(_pieChart);
        }

        // ===== 세금 시뮬레이터 (기능 C) =====
        private DataGridView _taxGrid = null!;
        private void BuildTaxTab(TabPage tab)
        {
            var info = new Label
            {
                Dock = DockStyle.Top,
                Height = 60,
                Text = "💡 해외주식 양도소득세 시뮬레이션 (연 250만원 공제 · 22% 세율)\n    각 보유 종목을 '지금 전량 매도' 시 예상 세액과 세후 수령액",
                ForeColor = Color.LightGray,
                Padding = new Padding(15, 10, 15, 0),
                Font = new Font("맑은 고딕", 10F)
            };

            _taxGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 32 }
            };
            _taxGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            _taxGrid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(70, 120, 200)
            };
            foreach (var c in new[] { "Symbol", "Qty", "AvgCost", "Current", "Gain", "Tax", "NetKRW", "Recommendation" })
                _taxGrid.Columns.Add(c, c switch {
                    "Symbol" => "종목", "Qty" => "수량", "AvgCost" => "평단",
                    "Current" => "현재가", "Gain" => "평가손익", "Tax" => "예상세액",
                    "NetKRW" => "세후 (KRW)", _ => "추천"
                });

            tab.Controls.Add(_taxGrid);
            tab.Controls.Add(info);
        }

        // ===== 배당 캘린더 (기능 D) =====
        private DataGridView _divGrid = null!;
        private void BuildDividendTab(TabPage tab)
        {
            var header = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = Color.FromArgb(30, 34, 44)
            };
            var lblTitle = new Label
            {
                Text = "💸 향후 60일간 배당 일정",
                Font = new Font("맑은 고딕", 12F, FontStyle.Bold),
                ForeColor = Color.Gold,
                Location = new Point(15, 12),
                AutoSize = true
            };
            var btnRefresh = new Button
            {
                Text = "🔄 새로고침",
                Location = new Point(250, 10),
                Size = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White
            };
            btnRefresh.Click += (s, e) => LoadDividends();
            header.Controls.Add(lblTitle);
            header.Controls.Add(btnRefresh);

            _divGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false,
                AllowUserToAddRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 32 }
            };
            _divGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            _divGrid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White
            };
            foreach (var (k, v) in new[] {
                ("ExDate","배당락일"),("Symbol","종목"),("Name","종목명"),("DPS","주당배당금"),
                ("Qty","보유수량"),("Expected","예상배당"),("PayDate","지급일"),("YieldPct","수익률")})
                _divGrid.Columns.Add(k, v);

            tab.Controls.Add(_divGrid);
            tab.Controls.Add(header);
        }

        // ===== 상관관계 (기능 F) =====
        private Panel _corrPanel = null!;
        private decimal[,]? _corrMatrix;
        private List<string> _corrSymbols = new();
        private void BuildCorrelationTab(TabPage tab)
        {
            var header = new Panel
            {
                Dock = DockStyle.Top, Height = 50,
                BackColor = Color.FromArgb(30, 34, 44)
            };
            var lblTitle = new Label
            {
                Text = "🔗 보유 종목 상관관계 매트릭스 (최근 90일)",
                Font = new Font("맑은 고딕", 12F, FontStyle.Bold),
                ForeColor = Color.LightBlue,
                Location = new Point(15, 12), AutoSize = true
            };
            var btnCalc = new Button
            {
                Text = "📊 계산",
                Location = new Point(360, 10), Size = new Size(100, 30),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White
            };
            btnCalc.Click += async (s, e) => await CalcCorrelationAsync();
            header.Controls.Add(lblTitle); header.Controls.Add(btnCalc);

            _corrPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(24, 28, 36) };
            _corrPanel.Paint += DrawCorrelation;

            tab.Controls.Add(_corrPanel);
            tab.Controls.Add(header);
        }

        private async Task CalcCorrelationAsync()
        {
            var holdings = _db.GetAllHoldings();
            if (holdings.Count < 2)
            {
                MessageBox.Show("최소 2종목 이상 보유해야 합니다.");
                return;
            }
            _corrSymbols = holdings.Select(h => h.Symbol).ToList();
            var returns = await CorrelationAnalyzer.CollectReturnsAsync(_provider,
                holdings.Select(h => (h.Symbol, h.Market)), 90);
            _corrMatrix = CorrelationAnalyzer.BuildMatrix(_corrSymbols, returns);
            _corrPanel.Invalidate();
        }

        private void DrawCorrelation(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _corrPanel.ClientRectangle;
            if (_corrMatrix == null)
            {
                g.DrawString("\"📊 계산\" 버튼을 눌러주세요.", GdiCache.Title, Brushes.Gray,
                    rect.Width / 2f - 100, rect.Height / 2f);
                return;
            }

            var n = _corrSymbols.Count;
            var margin = 80;
            var size = Math.Min(rect.Width, rect.Height) - margin * 2;
            var cell = size / n;
            var x0 = (rect.Width - size) / 2;
            var y0 = margin;

            // 컬럼 헤더
            for (int i = 0; i < n; i++)
            {
                g.DrawString(_corrSymbols[i], GdiCache.SmallBold, Brushes.White,
                    x0 + i * cell + 2, y0 - 20);
                g.DrawString(_corrSymbols[i], GdiCache.SmallBold, Brushes.White,
                    x0 - 55, y0 + i * cell + cell / 2 - 7);
            }

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    var v = _corrMatrix[i, j];
                    Color col;
                    if (v >= 0.7m) col = Color.FromArgb(220, 50, 50);
                    else if (v >= 0.3m) col = Color.FromArgb(200, 130, 80);
                    else if (v >= -0.3m) col = Color.FromArgb(100, 100, 100);
                    else if (v >= -0.7m) col = Color.FromArgb(80, 130, 200);
                    else col = Color.FromArgb(50, 90, 220);

                    g.FillRectangle(GdiCache.Brush(col), x0 + j * cell, y0 + i * cell, cell, cell);
                    g.DrawRectangle(Pens.Black, x0 + j * cell, y0 + i * cell, cell, cell);
                    g.DrawString($"{v:F2}", GdiCache.SmallBold, Brushes.White,
                        x0 + j * cell + cell / 2f - 12, y0 + i * cell + cell / 2f - 7);
                }

            // 범례
            var legY = y0 + size + 20;
            g.DrawString("강한 양의 상관 ── 무상관 ── 강한 음의 상관",
                GdiCache.Normal, Brushes.White, x0, legY);
        }

        // ===== 요약 카드 Paint =====
        private void DrawSummaryCards(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _summaryPanel.ClientRectangle;
            var cardW = (rect.Width - 60) / 4;
            var cardH = 90;

            var cards = new[] {
                ("💰 총 평가금액 (KRW)", _lblTotalKrw?.Text ?? "0원", Color.FromArgb(60,120,200)),
                ("📈 미실현 손익", _lblUnrealized?.Text ?? "0원", Color.FromArgb(220,50,50)),
                ("💵 실현 손익 (누적)", _lblRealized?.Text ?? "0원", Color.FromArgb(50,200,100)),
                ("📦 보유 종목 수", _lblCount?.Text ?? "0", Color.FromArgb(200,150,60))
            };

            for (int i = 0; i < 4; i++)
            {
                var x = 15 + i * (cardW + 15);
                var r = new Rectangle(x, 10, cardW, cardH);
                g.FillRectangle(GdiCache.Brush(Color.FromArgb(40, 46, 58)), r);
                g.FillRectangle(GdiCache.Brush(cards[i].Item3), x, 10, 4, cardH);
                g.DrawString(cards[i].Item1, GdiCache.Normal, Brushes.LightGray, x + 15, 25);
                var valBr = cards[i].Item2.StartsWith("-") ? GdiCache.Brush(Color.FromArgb(50, 90, 200)) :
                           (cards[i].Item2.StartsWith("+") ? GdiCache.Brush(Color.FromArgb(220, 50, 50)) : (Brush)Brushes.White);
                g.DrawString(cards[i].Item2, GdiCache.Header, valBr, x + 15, 50);
            }
        }

        // ===== 자산 배분 Paint =====
        private void DrawAllocation(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _pieChart.ClientRectangle;
            var holdings = _db.GetAllHoldings();
            if (holdings.Count == 0) return;

            var totals = new Dictionary<string, decimal>();
            decimal marketTotal = 0;
            foreach (var h in holdings)
            {
                var val = KrwValue(h);
                var key = h.Market.ToString();
                if (!totals.ContainsKey(key)) totals[key] = 0;
                totals[key] += val;
                marketTotal += val;
            }

            g.DrawString("📊 종목별 비중", GdiCache.Title, Brushes.White, 30, 30);

            var cx = 280; var cy = 340; var r = 180;
            var colors = new[] {
                Color.FromArgb(220,50,50), Color.FromArgb(50,90,200), Color.FromArgb(50,180,80),
                Color.FromArgb(200,150,60), Color.FromArgb(150,80,200), Color.FromArgb(80,200,200)
            };

            float startAngle = -90;
            var sorted = holdings.OrderByDescending(KrwValue).Take(12).ToList();
            var displayTotal = marketTotal > 0 ? marketTotal : 1;

            int i = 0;
            foreach (var h in sorted)
            {
                var val = KrwValue(h);
                var sweep = (float)((double)val / (double)displayTotal * 360);
                g.FillPie(GdiCache.Brush(colors[i % colors.Length]), cx - r, cy - r, r * 2, r * 2, startAngle, sweep);
                startAngle += sweep;
                i++;
            }

            // 레전드
            var lx = 520; var ly = 100;
            i = 0;
            foreach (var h in sorted)
            {
                var val = KrwValue(h);
                var pct = (double)val / (double)displayTotal * 100;
                g.FillRectangle(GdiCache.Brush(colors[i % colors.Length]), lx, ly, 14, 14);
                g.DrawString($"{h.Name} ({pct:F1}%)", GdiCache.Normal, Brushes.White, lx + 22, ly - 2);
                ly += 22; i++;
            }

            // 통화별
            g.DrawString("🌏 통화별 비중", GdiCache.XLarge, Brushes.White, 30, 550);
            var krw = holdings.Where(h => h.Currency == Currency.KRW).Sum(h => h.CurrentPrice * h.Quantity);
            var usd = holdings.Where(h => h.Currency == Currency.USD).Sum(h => h.CurrentPrice * h.Quantity * _usdKrw);
            var tot = krw + usd;
            if (tot > 0)
            {
                g.FillRectangle(GdiCache.Brush(Color.FromArgb(70, 130, 200)), 30, 590, (int)((float)krw / (float)tot * 500), 30);
                g.FillRectangle(GdiCache.Brush(Color.FromArgb(200, 100, 50)),
                    30 + (int)((float)krw / (float)tot * 500), 590, (int)((float)usd / (float)tot * 500), 30);
                g.DrawString($"KRW {(double)krw / (double)tot * 100:F1}%", GdiCache.SmallBold, Brushes.White, 40, 596);
                g.DrawString($"USD {(double)usd / (double)tot * 100:F1}%", GdiCache.SmallBold, Brushes.White,
                    40 + (int)((float)krw / (float)tot * 500), 596);
            }
        }

        // ===== 수익률 차트 Paint =====
        private void DrawPerformanceChart(object? sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _perfChart.ClientRectangle;
            var holdings = _db.GetAllHoldings();

            g.DrawString("📈 종목별 수익률 (%)", GdiCache.Title, Brushes.White, 30, 20);

            if (holdings.Count == 0) return;
            var maxRate = (double)holdings.Max(h => Math.Abs(h.ChangeRate));
            if (maxRate == 0) maxRate = 10;

            var padL = 120; var padT = 80;
            var barH = 26;

            int i = 0;
            foreach (var h in holdings.OrderByDescending(x => x.ChangeRate))
            {
                if (i >= 15) break;
                var y = padT + i * (barH + 6);
                g.DrawString(h.Name.Length > 14 ? h.Name[..14] : h.Name,
                    GdiCache.Normal, Brushes.White, 15, y + 5);

                var w = (float)(Math.Abs((double)h.ChangeRate) / maxRate * 400);
                var col = h.ChangeRate >= 0
                    ? Color.FromArgb(220, 50, 50)
                    : Color.FromArgb(50, 90, 200);
                g.FillRectangle(GdiCache.Brush(col), padL, y, w, barH);
                g.DrawString(Formatter.Pct(h.ChangeRate), GdiCache.SmallBold, Brushes.White, padL + w + 8, y + 5);
                i++;
            }
        }

        // ===== 데이터 연동 =====
        private async Task RefreshAllAsync()
        {
            try
            {
                // 환율은 여기서 한 번만 비동기로 받아 캐시 (UI 스레드 .Result 데드락 제거)
                try { _usdKrw = await _fx.GetUsdKrwAsync(); } catch { /* 캐시값 유지 */ }

                var holdings = _db.GetAllHoldings();
                foreach (var group in holdings.GroupBy(h => h.Market))
                {
                    var quotes = await _provider.GetQuotesAsync(group.Select(h => h.Symbol), group.Key);
                    foreach (var q in quotes)
                    {
                        var h = group.FirstOrDefault(x => x.Symbol == q.Symbol);
                        if (h != null) { h.CurrentPrice = q.Price; _db.SaveHolding(h); }
                    }
                }
                RefreshHoldingsGrid();
                RefreshTransactionsGrid();
                RefreshSummary();
                RefreshTaxGrid();
                LoadDividends();
                _pieChart.Invalidate();
                _perfChart.Invalidate();
                _summaryPanel.Invalidate();
            }
            catch (Exception ex) { Log.Error("Portfolio Refresh 실패", ex); }
        }

        private decimal KrwValue(Holding h) =>
            (h.Currency == Currency.USD ? _usdKrw : 1m) * h.CurrentPrice * h.Quantity;

        private void RefreshHoldingsGrid()
        {
            _holdingsGrid.Rows.Clear();
            var holdings = _db.GetAllHoldings();
            var totalValue = holdings.Sum(KrwValue);   // 통화 환산 후 합산
            if (totalValue == 0) totalValue = 1;
            foreach (var h in holdings)
            {
                var weight = (double)KrwValue(h) / (double)totalValue * 100;
                _holdingsGrid.Rows.Add(h.Symbol, h.Name, h.Quantity, h.AvgPrice.ToString("N0"),
                    h.CurrentPrice.ToString("N0"), h.Invested.ToString("N0"),
                    h.CurrentValue.ToString("N0"), h.UnrealizedPnL.ToString("N0"),
                    Formatter.Pct(h.ChangeRate), $"{weight:F1}%");
            }
        }

        private void RefreshTransactionsGrid()
        {
            _txGrid.Rows.Clear();
            _txGrid.Columns.Clear();
            foreach (var (k, v) in new[] {
                ("ExecutedAt","일시"),("Symbol","종목"),("Type","구분"),
                ("Quantity","수량"),("Price","단가"),("Amount","금액"),("Commission","수수료"),("Tax","세금")})
                _txGrid.Columns.Add(k, v);

            var dt = _db.Query("SELECT * FROM Transactions ORDER BY ExecutedAt DESC LIMIT 200");
            foreach (DataRow r in dt.Rows)
            {
                var price = Safe.Dec(r["Price"]);
                var qty = Safe.Int(r["Quantity"]);
                _txGrid.Rows.Add(
                    Safe.DT(r["ExecutedAt"]).ToString("yyyy-MM-dd HH:mm"),
                    Safe.Str(r["Symbol"]),
                    ((TransactionType)Safe.Int(r["Type"])).ToString(),
                    qty.ToString("N0"), price.ToString("N0"),
                    (qty * price).ToString("N0"),
                    Safe.Dec(r["Commission"]).ToString("N0"),
                    Safe.Dec(r["Tax"]).ToString("N0"));
            }
        }

        private void RefreshSummary()
        {
            var holdings = _db.GetAllHoldings();
            decimal totalKrw = 0, unrealKrw = 0;
            var usdKrw = _usdKrw;
            foreach (var h in holdings)
            {
                var val = h.CurrentPrice * h.Quantity;
                var cost = h.AvgPrice * h.Quantity;
                if (h.Currency == Currency.USD) { val *= usdKrw; cost *= usdKrw; }
                totalKrw += val;
                unrealKrw += (val - cost);
            }

            // 실현손익 (매도 거래)
            var realDt = _db.Query(@"
                SELECT t.Symbol, t.Type, t.Quantity, t.Price, t.Commission, t.Tax,
                       h.AvgPrice, h.Currency
                FROM Transactions t LEFT JOIN Holdings h ON t.Symbol = h.Symbol
                WHERE t.Type = 1");
            decimal realKrw = 0;
            foreach (DataRow r in realDt.Rows)
            {
                var q = Safe.Int(r["Quantity"]);
                var p = Safe.Dec(r["Price"]);
                var avg = Safe.Dec(r["AvgPrice"]);
                var cur = (Currency)Safe.Int(r["Currency"]);
                var gain = (p - avg) * q - Safe.Dec(r["Commission"]) - Safe.Dec(r["Tax"]);
                if (cur == Currency.USD) gain *= usdKrw;
                realKrw += gain;
            }

            _lblTotalKrw ??= new Label();
            _lblUnrealized ??= new Label();
            _lblRealized ??= new Label();
            _lblCount ??= new Label();
            _lblTotalKrw.Text = $"{totalKrw:N0}원";
            _lblUnrealized.Text = $"{(unrealKrw >= 0 ? "+" : "")}{unrealKrw:N0}원";
            _lblRealized.Text = $"{(realKrw >= 0 ? "+" : "")}{realKrw:N0}원";
            _lblCount.Text = $"{holdings.Count}개";
        }

        private void RefreshTaxGrid()
        {
            _taxGrid.Rows.Clear();
            var holdings = _db.GetAllHoldings();
            var usdKrw = _usdKrw;
            decimal runningRealized = 0;
            foreach (var h in holdings.OrderByDescending(x => x.UnrealizedPnL))
            {
                var sim = TaxSimulator.SimulateSell(h, usdKrw, runningRealized);
                _taxGrid.Rows.Add(
                    $"{h.Symbol} {h.Name}",
                    h.Quantity.ToString("N0"),
                    Formatter.Money(h.AvgPrice, h.Currency),
                    Formatter.Money(h.CurrentPrice, h.Currency),
                    sim.UnrealizedGain.ToString("N0"),
                    sim.TaxIfSoldNow.ToString("N0"),
                    sim.NetProceeds.ToString("N0"),
                    sim.Recommendation
                );
                if (h.Currency == Currency.USD) runningRealized += sim.UnrealizedGain * usdKrw;
            }
        }

        private void LoadDividends()
        {
            _divGrid.Rows.Clear();
            var dividends = _db.GetUpcomingDividends(60);
            var holdings = _db.GetAllHoldings().ToDictionary(h => h.Symbol, h => h.Quantity);
            foreach (var d in dividends.OrderBy(x => x.ExDate))
            {
                var qty = holdings.TryGetValue(d.Symbol, out var q) ? q : 0;
                var expected = d.DividendPerShare * qty;
                _divGrid.Rows.Add(
                    d.ExDate.ToString("yyyy-MM-dd"),
                    d.Symbol,
                    d.Name,
                    Formatter.Money(d.DividendPerShare, d.Currency),
                    qty.ToString("N0"),
                    Formatter.Money(expected, d.Currency),
                    d.PayDate.ToString("yyyy-MM-dd"),
                    $"{d.YieldPct:F2}%"
                );
            }
            if (_divGrid.RowCount == 0)
            {
                _divGrid.Rows.Add("-", "-", "배당 일정 없음 (수동 추가하거나 KIS 연동 필요)", "-", "-", "-", "-", "-");
            }
        }

        // ===== 거래 다이얼로그 =====
        private void ShowTransactionDialog(TransactionType type)
        {
            using var dlg = new TransactionDialog(type);
            if (dlg.ShowDialog() == DialogResult.OK && dlg.Result != null)
            {
                _db.AddTransaction(dlg.Result);
                RecomputeAverage(dlg.Result.Symbol);
                _ = RefreshAllAsync();
            }
        }

        /// <summary>매수/매도 누적 계산 - 0 나누기 가드</summary>
        private void RecomputeAverage(string symbol)
        {
            var dt = _db.Query(
                "SELECT * FROM Transactions WHERE Symbol=@s ORDER BY ExecutedAt",
                new() { ["@s"] = symbol });

            int qty = 0;
            decimal cost = 0;
            foreach (DataRow r in dt.Rows)
            {
                var t = (TransactionType)Safe.Int(r["Type"]);
                var q = Safe.Int(r["Quantity"]);
                var p = Safe.Dec(r["Price"]);
                if (t == TransactionType.Buy)
                {
                    cost += p * q;
                    qty += q;
                }
                else if (t == TransactionType.Sell)
                {
                    if (qty == 0) continue;  // 매도할 수량 없음 (이상)
                    if (qty == q) { cost = 0; qty = 0; }  // ⚠️ 수정: 전량 매도 시 평단 0 (0/0 방지)
                    else
                    {
                        cost = cost * (qty - q) / qty;
                        qty -= q;
                    }
                }
            }

            var avg = qty > 0 ? cost / qty : 0;
            var dtH = _db.Query("SELECT * FROM Holdings WHERE Symbol=@s LIMIT 1", new() { ["@s"] = symbol });
            var h = new Holding
            {
                Symbol = symbol,
                Name = dtH.Rows.Count > 0 ? Safe.Str(dtH.Rows[0]["Name"]) : symbol,
                Market = dtH.Rows.Count > 0 ? (Market)Safe.Int(dtH.Rows[0]["Market"]) : Market.KRX_KOSPI,
                Currency = dtH.Rows.Count > 0 ? (Currency)Safe.Int(dtH.Rows[0]["Currency"]) : Currency.KRW,
                Quantity = qty,
                AvgPrice = avg,
                BrokerAccount = dtH.Rows.Count > 0 ? Safe.Str(dtH.Rows[0]["BrokerAccount"]) : "",
            };
            _db.SaveHolding(h);
        }

        // ===== Export/Import =====
        private void ExportHoldings()
        {
            using var dlg = new SaveFileDialog { Filter = "CSV|*.csv", FileName = $"holdings_{DateTime.Now:yyyyMMdd}.csv" };
            if (dlg.ShowDialog() != DialogResult.OK) return;
            var sb = new StringBuilder();
            sb.AppendLine("종목코드,종목명,수량,평단,현재가,투자원금,평가금액,손익,수익률");
            foreach (var h in _db.GetAllHoldings())
                sb.AppendLine($"{h.Symbol},{h.Name},{h.Quantity},{h.AvgPrice},{h.CurrentPrice},{h.Invested},{h.CurrentValue},{h.UnrealizedPnL},{h.ChangeRate:F2}");
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show("CSV 저장 완료", "내보내기");
        }

        /// <summary>HTS CSV 임포트 - 헤더 기반 매핑 (증권사별 컬럼 순서 차이 대응)</summary>
        private void ImportFromHts()
        {
            using var dlg = new OpenFileDialog { Filter = "CSV|*.csv" };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                var lines = File.ReadAllLines(dlg.FileName, Encoding.UTF8);
                if (lines.Length < 2) { MessageBox.Show("빈 CSV"); return; }

                // 헤더 매핑 테이블 (증권사별 별칭)
                var headerMap = new Dictionary<string, string[]>
                {
                    ["Symbol"] = new[] { "종목코드", "단축코드", "종목번호", "Symbol", "티커" },
                    ["Name"] = new[] { "종목명", "종목이름", "Name" },
                    ["Qty"] = new[] { "보유수량", "잔고수량", "수량", "Qty", "현재보유수량" },
                    ["AvgPrice"] = new[] { "평균단가", "매입단가", "평단가", "평균매입가", "AvgPrice" }
                };

                var headers = lines[0].Split(',').Select(h => h.Trim().Trim('"')).ToArray();
                var idx = new Dictionary<string, int>();
                foreach (var (k, aliases) in headerMap)
                {
                    for (int i = 0; i < headers.Length; i++)
                        if (aliases.Any(a => headers[i].Contains(a)))
                        {
                            idx[k] = i; break;
                        }
                }

                if (!idx.ContainsKey("Symbol") || !idx.ContainsKey("Qty") || !idx.ContainsKey("AvgPrice"))
                {
                    MessageBox.Show($"필수 컬럼을 찾을 수 없습니다.\n감지된 헤더: {string.Join(", ", headers)}\n\n지원 별칭: 종목코드/보유수량/평균단가",
                        "임포트 실패", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                int count = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    var cols = lines[i].Split(',').Select(c => c.Trim().Trim('"')).ToArray();
                    if (cols.Length <= idx.Values.Max()) continue;

                    var sym = cols[idx["Symbol"]];
                    var name = idx.TryGetValue("Name", out var ni) && ni < cols.Length ? cols[ni] : sym;
                    var qty = int.TryParse(cols[idx["Qty"]].Replace(",", ""), out var q) ? q : 0;
                    var avg = decimal.TryParse(cols[idx["AvgPrice"]].Replace(",", ""), out var a) ? a : 0;
                    if (string.IsNullOrEmpty(sym) || qty == 0) continue;

                    _db.SaveHolding(new Holding
                    {
                        Symbol = sym, Name = name,
                        Market = sym.Length == 6 && char.IsDigit(sym[0]) ? Market.KRX_KOSPI : Market.US_NASDAQ,
                        Currency = sym.Length == 6 && char.IsDigit(sym[0]) ? Currency.KRW : Currency.USD,
                        Quantity = qty, AvgPrice = avg, CurrentPrice = avg,
                        FirstBuyDate = DateTime.Now,
                        BrokerAccount = "HTS"
                    });
                    count++;
                }
                MessageBox.Show($"✅ {count}개 종목 임포트 완료", "임포트");
                _ = RefreshAllAsync();
            }
            catch (Exception ex)
            {
                Log.Error("HTS Import 실패", ex);
                MessageBox.Show($"실패: {ex.Message}", "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
