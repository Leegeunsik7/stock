// ============================================================================
// Program #5: Signal Engine (v2.2)
// [개선점]
//  · 백테스터에 TradingCost 반영 (수수료/세금/슬리피지)
//  · Equity Curve X축 거래일 인덱스 (비영업일 왜곡 제거)
//  · GdiCache/Safe 적용
//  · 섹터 로테이션 탭 추가 (기능 G)
//  · TakeLast 결과 한 번만 materialize
//  · AlertCenter 시그널 통합
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.SignalEngine
{
    public class MainForm : Form
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;

        private TabControl _tabs = null!;
        private DataGridView _signalsGrid = null!;
        private Panel _backtestChart = null!;
        private Panel _sectorChart = null!;

        private List<TradeSignal> _signals = new();
        private BacktestResult? _lastBacktest;
        private List<SectorStrength> _sectorResults = new();

        public MainForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            InitializeUI();
            Load += async (s, e) => await LoadSignalsAsync();
        }

        private void InitializeUI()
        {
            Text = "⚡ 시그널 엔진 - MS2-E Stock Suite";
            Size = new Size(1600, 950);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Appearance = TabAppearance.FlatButtons,
                ItemSize = new Size(150, 34),
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };

            var signalTab = new TabPage("⚡ 오늘의 시그널") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildSignalTab(signalTab);

            var btTab = new TabPage("🔬 백테스팅") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildBacktestTab(btTab);

            var sectorTab = new TabPage("🌐 섹터 로테이션") { BackColor = Color.FromArgb(24, 28, 36) };
            BuildSectorTab(sectorTab);

            _tabs.TabPages.AddRange(new[] { signalTab, btTab, sectorTab });
            Controls.Add(_tabs);
        }

        // ===== 오늘의 시그널 탭 =====
        private void BuildSignalTab(TabPage tab)
        {
            var toolbar = new ToolStrip
            {
                BackColor = Color.FromArgb(40, 46, 58), ForeColor = Color.White,
                GripStyle = ToolStripGripStyle.Hidden,
                Padding = new Padding(8, 4, 8, 4),
                RenderMode = ToolStripRenderMode.System
            };
            var btnScan = new ToolStripButton("🔍 전체 스캔") { ForeColor = Color.Gold };
            btnScan.Click += async (s, e) => await ScanSignalsAsync();
            var btnClear = new ToolStripButton("🗑 과거 시그널 정리") { ForeColor = Color.White };
            btnClear.Click += (s, e) =>
            {
                _db.Execute("DELETE FROM Signals WHERE GeneratedAt < @d",
                    new() { ["@d"] = DateTime.Now.AddDays(-30).ToString("o") });
                _ = LoadSignalsAsync();
            };
            toolbar.Items.AddRange(new ToolStripItem[] { btnScan, btnClear });

            _signalsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White, GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false, AllowUserToAddRows = false,
                ReadOnly = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 32 },
                Font = new Font("맑은 고딕", 10F)
            };
            _signalsGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            _signalsGrid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White
            };
            foreach (var c in new[] { "GeneratedAt", "Symbol", "Name", "Signal", "Strategy",
                "Reason", "TargetPrice", "StopLoss", "Confidence" })
                _signalsGrid.Columns.Add(c, c switch {
                    "GeneratedAt" => "시각", "Symbol" => "종목", "Name" => "이름",
                    "Signal" => "구분", "Strategy" => "전략",
                    "Reason" => "근거", "TargetPrice" => "목표가", "StopLoss" => "손절가",
                    _ => "신뢰도"
                });

            _signalsGrid.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var col = _signalsGrid.Columns[e.ColumnIndex].Name;
                if (col == "Signal" && e.Value != null)
                {
                    var txt = e.Value.ToString() ?? "";
                    e.CellStyle!.Font = GdiCache.MediumBold;
                    e.CellStyle.ForeColor = txt.Contains("매수") || txt.Contains("Buy") ? Color.LightGreen
                        : txt.Contains("매도") || txt.Contains("Sell") ? Color.Salmon : Color.Gold;
                }
            };

            tab.Controls.Add(_signalsGrid);
            tab.Controls.Add(toolbar);
        }

        // ===== 백테스팅 탭 =====
        private ComboBox _cbStrategy = null!;
        private ComboBox _cbSymbol = null!;
        private NumericUpDown _nMonths = null!;
        private Label _lblResult = null!;
        private void BuildBacktestTab(TabPage tab)
        {
            var toolbar = new Panel
            {
                Dock = DockStyle.Top, Height = 70,
                BackColor = Color.FromArgb(35, 40, 55),
                Padding = new Padding(15, 15, 15, 15)
            };

            var lblStrat = new Label { Text = "전략:", Location = new Point(15, 22), AutoSize = true, ForeColor = Color.LightGray };
            _cbStrategy = new ComboBox
            {
                Location = new Point(70, 18), Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cbStrategy.Items.AddRange(new object[] {
                "이동평균 골든/데드크로스", "RSI 역추세", "볼린저밴드 돌파",
                "MACD 시그널", "볼륨 스파이크 추세"
            });
            _cbStrategy.SelectedIndex = 0;

            var lblSym = new Label { Text = "종목:", Location = new Point(260, 22), AutoSize = true, ForeColor = Color.LightGray };
            _cbSymbol = new ComboBox
            {
                Location = new Point(310, 18), Width = 220,
                DropDownStyle = ComboBoxStyle.DropDown,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            foreach (var (sym, name, _) in SampleData.All())
                _cbSymbol.Items.Add($"{sym} - {name}");
            _cbSymbol.SelectedIndex = 0;

            var lblMo = new Label { Text = "기간(월):", Location = new Point(540, 22), AutoSize = true, ForeColor = Color.LightGray };
            _nMonths = new NumericUpDown
            {
                Location = new Point(610, 18), Width = 70,
                Minimum = 1, Maximum = 60, Value = 12,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };

            var btnRun = new Button
            {
                Text = "🚀 실행",
                Location = new Point(700, 16), Size = new Size(100, 34),
                BackColor = Color.FromArgb(70, 130, 200),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            btnRun.Click += async (s, e) => await RunBacktestAsync();

            var btnReport = new Button
            {
                Text = "📊 리포트",
                Location = new Point(700, 52), Size = new Size(100, 30),
                BackColor = Color.FromArgb(90, 150, 110),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnReport.Click += (s, e) =>
            {
                if (_lastBacktest == null) { MessageBox.Show("먼저 백테스트를 실행하세요."); return; }
                var f = new BacktestReportForm(_lastBacktest);
                try { ThemeManager.Apply(f); } catch { }
                f.Show();
            };

            _lblResult = new Label
            {
                Location = new Point(820, 22), AutoSize = true,
                ForeColor = Color.LightGreen,
                Font = new Font("맑은 고딕", 11F, FontStyle.Bold)
            };

            toolbar.Controls.AddRange(new Control[] { lblStrat, _cbStrategy, lblSym, _cbSymbol, lblMo, _nMonths, btnRun, btnReport, _lblResult });

            _backtestChart = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _backtestChart.Paint += DrawBacktestChart;

            tab.Controls.Add(_backtestChart);
            tab.Controls.Add(toolbar);
        }

        // ===== 섹터 로테이션 탭 (기능 G) =====
        private NumericUpDown _nSectorDays = null!;
        private void BuildSectorTab(TabPage tab)
        {
            var toolbar = new Panel
            {
                Dock = DockStyle.Top, Height = 60,
                BackColor = Color.FromArgb(35, 40, 55),
                Padding = new Padding(15, 15, 15, 15)
            };
            var lbl = new Label
            {
                Text = "섹터별 수익률과 시장 대비 상대강도(RS)를 분석합니다.",
                Location = new Point(15, 22), AutoSize = true, ForeColor = Color.LightGray,
                Font = new Font("맑은 고딕", 10F)
            };
            var lblPer = new Label { Text = "기간(일):", Location = new Point(500, 22), AutoSize = true, ForeColor = Color.LightGray };
            _nSectorDays = new NumericUpDown
            {
                Location = new Point(570, 18), Width = 60,
                Minimum = 7, Maximum = 180, Value = 30,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle
            };
            var btn = new Button
            {
                Text = "📊 분석",
                Location = new Point(645, 16), Size = new Size(100, 34),
                BackColor = Color.FromArgb(200, 130, 70),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            btn.Click += async (s, e) => await RunSectorAsync();
            toolbar.Controls.AddRange(new Control[] { lbl, lblPer, _nSectorDays, btn });

            _sectorChart = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _sectorChart.Paint += DrawSectorChart;

            tab.Controls.Add(_sectorChart);
            tab.Controls.Add(toolbar);
        }

        private async Task RunSectorAsync()
        {
            _sectorResults = await SectorAnalyzer.AnalyzeAsync(_provider, SampleData.All()
                .Select(x => (x.Item1, x.Item3)), (int)_nSectorDays.Value);
            _sectorChart.Invalidate();
        }

        private void DrawSectorChart(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _sectorChart.ClientRectangle;

            g.DrawString($"🌐 섹터 로테이션 ({_nSectorDays.Value}일)", GdiCache.Title, Brushes.White, 20, 15);
            if (_sectorResults.Count == 0)
            {
                g.DrawString("\"📊 분석\" 버튼을 눌러주세요.", GdiCache.Large, Brushes.Gray,
                    rect.Width / 2f - 120, rect.Height / 2f);
                return;
            }

            int padL = 160, padT = 60, padR = 80, padB = 30;
            var plotW = rect.Width - padL - padR;
            var plotH = rect.Height - padT - padB;
            var barH = Math.Min(36, plotH / Math.Max(1, _sectorResults.Count));

            var maxRS = (double)Math.Max(1m, _sectorResults.Max(x => Math.Abs(x.RelativeStrength)));
            var zeroX = padL + plotW / 2;

            g.DrawLine(GdiCache.Pen(Color.FromArgb(120, 200, 200, 200)), zeroX, padT, zeroX, padT + plotH);
            g.DrawString("0", GdiCache.Small, Brushes.LightGray, zeroX - 4, padT + plotH + 5);

            for (int i = 0; i < _sectorResults.Count; i++)
            {
                var sr = _sectorResults[i];
                var y = padT + i * (barH + 3);
                g.DrawString(sr.Name, GdiCache.NormalBold, Brushes.White, 15, y + (barH / 2f - 7));

                var w = (float)((double)Math.Abs(sr.RelativeStrength) / maxRS * (plotW / 2));
                var col = sr.RelativeStrength >= 0
                    ? Color.FromArgb(220, 50, 50)
                    : Color.FromArgb(50, 90, 200);
                if (sr.RelativeStrength >= 0)
                    g.FillRectangle(GdiCache.Brush(col), zeroX, y, w, barH);
                else
                    g.FillRectangle(GdiCache.Brush(col), zeroX - w, y, w, barH);

                var lbl = $"{Formatter.Pct(sr.ReturnPct)} (RS {Formatter.Pct(sr.RelativeStrength)}) · {sr.MemberCount}종목";
                g.DrawString(lbl, GdiCache.Normal, Brushes.White,
                    sr.RelativeStrength >= 0 ? zeroX + w + 8 : zeroX - w - 250, y + (barH / 2f - 7));
            }
        }

        // ===== 시그널 스캔 =====
        private Task LoadSignalsAsync()
        {
            var dt = _db.Query("SELECT * FROM Signals ORDER BY GeneratedAt DESC LIMIT 200");
            _signals = new List<TradeSignal>();
            foreach (DataRow r in dt.Rows)
            {
                _signals.Add(new TradeSignal
                {
                    Id = Safe.Int(r["Id"]),
                    Symbol = Safe.Str(r["Symbol"]),
                    Name = Safe.Str(r["Name"]),
                    Signal = (SignalType)Safe.Int(r["Signal"]),
                    Strength = (SignalStrength)Safe.Int(r["Strength"]),
                    Strategy = Safe.Str(r["Strategy"]),
                    Reason = Safe.Str(r["Reason"]),
                    TargetPrice = Safe.Dec(r["TargetPrice"]),
                    StopLoss = Safe.Dec(r["StopLoss"]),
                    Confidence = Safe.Dec(r["Confidence"]),
                    GeneratedAt = Safe.DT(r["GeneratedAt"])
                });
            }
            RefreshSignalsGrid();
            return Task.CompletedTask;
        }

        private void RefreshSignalsGrid()
        {
            _signalsGrid.Rows.Clear();
            foreach (var s in _signals)
                _signalsGrid.Rows.Add(
                    s.GeneratedAt.ToString("MM-dd HH:mm"), s.Symbol, s.Name,
                    SignalText(s.Signal), s.Strategy, s.Reason,
                    s.TargetPrice.ToString("N0"), s.StopLoss.ToString("N0"),
                    $"{s.Confidence:F1}%");
        }

        private static string SignalText(SignalType t) => t switch
        {
            SignalType.StrongBuy => "⭐ 강력매수",
            SignalType.Buy => "🟢 매수",
            SignalType.Hold => "➖ 관망",
            SignalType.Sell => "🔴 매도",
            SignalType.StrongSell => "❌ 강력매도",
            _ => "?"
        };

        private async Task ScanSignalsAsync()
        {
            _signals.Clear();
            foreach (var (sym, name, mkt) in SampleData.All())
            {
                try
                {
                    var candles = await _provider.GetHistoryAsync(sym, mkt,
                        DateTime.Now.AddMonths(-6), DateTime.Now);
                    if (candles.Count < 60) continue;

                    var closes = candles.Select(c => c.Close).ToList();

                    // 이동평균 크로스
                    var ma5 = closes.TakeLast(5).Average();
                    var ma20 = closes.TakeLast(20).Average();
                    var ma5Prev = closes.SkipLast(1).TakeLast(5).Average();
                    var ma20Prev = closes.SkipLast(1).TakeLast(20).Average();

                    if (ma5Prev < ma20Prev && ma5 > ma20)
                    {
                        var sig = new TradeSignal
                        {
                            Symbol = sym, Name = name,
                            Signal = SignalType.Buy,
                            Strength = SignalStrength.Medium,
                            Strategy = "이평 골든크로스",
                            Reason = $"MA5({ma5:N0}) > MA20({ma20:N0}) 돌파",
                            TargetPrice = closes.Last() * 1.05m,
                            StopLoss = closes.Last() * 0.97m,
                            Confidence = 65,
                            GeneratedAt = DateTime.Now
                        };
                        _signals.Add(sig);
                        SaveSignal(sig);
                        AlertCenter.Instance.RaiseSignal(sym,
                            $"🟢 매수 시그널 · {name}", $"골든크로스 ({closes.Last():N0})",
                            AlertLevel.Warning);
                    }
                    else if (ma5Prev > ma20Prev && ma5 < ma20)
                    {
                        var sig = new TradeSignal
                        {
                            Symbol = sym, Name = name,
                            Signal = SignalType.Sell,
                            Strength = SignalStrength.Medium,
                            Strategy = "이평 데드크로스",
                            Reason = $"MA5({ma5:N0}) < MA20({ma20:N0}) 이탈",
                            TargetPrice = closes.Last() * 0.95m,
                            StopLoss = closes.Last() * 1.02m,
                            Confidence = 60,
                            GeneratedAt = DateTime.Now
                        };
                        _signals.Add(sig);
                        SaveSignal(sig);
                    }
                }
                catch (Exception ex) { Log.Warn($"[Scan] {sym}: {ex.Message}"); }
            }
            RefreshSignalsGrid();
            MessageBox.Show($"✅ {_signals.Count}건의 시그널 감지");
        }

        private void SaveSignal(TradeSignal s)
        {
            _db.Execute(@"INSERT INTO Signals(Symbol,Name,Signal,Strength,Strategy,Reason,TargetPrice,StopLoss,Confidence,GeneratedAt)
                VALUES(@s,@n,@sig,@str,@stra,@r,@t,@sl,@c,@g)",
                new() {
                    ["@s"] = s.Symbol, ["@n"] = s.Name,
                    ["@sig"] = (int)s.Signal, ["@str"] = (int)s.Strength,
                    ["@stra"] = s.Strategy, ["@r"] = s.Reason,
                    ["@t"] = (double)s.TargetPrice, ["@sl"] = (double)s.StopLoss,
                    ["@c"] = (double)s.Confidence, ["@g"] = s.GeneratedAt.ToString("o")
                });
        }

        // ===== 백테스팅 실행 =====
        private async Task RunBacktestAsync()
        {
            var selected = _cbSymbol.Text;
            if (string.IsNullOrEmpty(selected)) { MessageBox.Show("종목 선택"); return; }
            var symbol = selected.Split(' ')[0];
            var market = SampleData.All().FirstOrDefault(x => x.Item1 == symbol).Item3;

            var candles = await _provider.GetHistoryAsync(symbol, market,
                DateTime.Now.AddMonths(-(int)_nMonths.Value), DateTime.Now);
            if (candles.Count < 30) { MessageBox.Show("히스토리 부족"); return; }

            var strategy = _cbStrategy.SelectedIndex;
            _lastBacktest = RunStrategyWithCost(candles, strategy, market);
            _lastBacktest.StrategyName = $"{_cbStrategy.Text} · {selected} · {(int)_nMonths.Value}개월";

            _lblResult.Text = $"💰 수익률 {Formatter.Pct(_lastBacktest.TotalReturn)} · MDD {_lastBacktest.MaxDrawdown:F2}% · Sharpe {_lastBacktest.Sharpe:F2} · 승률 {_lastBacktest.WinRate:F1}%";
            _lblResult.ForeColor = _lastBacktest.TotalReturn >= 0 ? Color.LightGreen : Color.Salmon;
            _backtestChart.Invalidate();
        }

        /// <summary>전략별 백테스팅 + 수수료/세금/슬리피지 반영</summary>
        private BacktestResult RunStrategyWithCost(List<Candle> candles, int strategy, Market market)
        {
            var cost = TradingCost.Default;
            decimal equity = 1_000_000m;
            decimal initial = equity;
            int qty = 0;
            decimal entryPrice = 0;
            var equityCurve = new List<decimal>();
            var tradeReturns = new List<decimal>();

            // ⚠️ M1: 매 캔들 재계산 제거 — closes/EMA를 1회만 전체 계산
            var closes = candles.Select(c => c.Close).ToList();
            var ema12 = EmaSeries(closes, 12);
            var ema26 = EmaSeries(closes, 26);

            for (int i = 30; i < candles.Count; i++)
            {
                var sig = GetSignal(candles, closes, ema12, ema26, i, strategy);
                var price = candles[i].Close;

                // 슬리피지 반영한 실행가
                var execPrice = sig switch
                {
                    1 => price * (1 + cost.SlippageRate),   // 매수 시 불리하게
                    -1 => price * (1 - cost.SlippageRate),  // 매도 시 불리하게
                    _ => price
                };

                if (sig == 1 && qty == 0)
                {
                    qty = (int)(equity / execPrice);
                    var amount = qty * execPrice;
                    equity -= amount + cost.Commission(market, amount);
                    entryPrice = execPrice;
                }
                else if (sig == -1 && qty > 0)
                {
                    var amount = qty * execPrice;
                    var commission = cost.Commission(market, amount);
                    var tax = cost.SellTax(market, amount);
                    var netProceeds = amount - commission - tax;
                    equity += netProceeds;
                    if (entryPrice > 0)
                        tradeReturns.Add((execPrice - entryPrice) / entryPrice * 100m);
                    qty = 0;
                }

                equityCurve.Add(equity + qty * price);
            }

            // 마감 시 청산
            if (qty > 0)
            {
                var lastP = candles.Last().Close * (1 - cost.SlippageRate);
                var amount = qty * lastP;
                equity += amount - cost.Commission(market, amount) - cost.SellTax(market, amount);
                if (entryPrice > 0) tradeReturns.Add((lastP - entryPrice) / entryPrice * 100m);
                equityCurve[^1] = equity;
            }

            var totalReturn = (equity - initial) / initial * 100m;
            // MDD 계산 (running max 방식, O(n))
            decimal runningMax = equityCurve.Count > 0 ? equityCurve[0] : initial;
            decimal maxDD = 0m;
            foreach (var e in equityCurve)
            {
                if (e > runningMax) runningMax = e;
                if (runningMax == 0) continue;
                var dd = (runningMax - e) / runningMax * 100m;
                if (dd > maxDD) maxDD = dd;
            }

            var wins = tradeReturns.Where(x => x > 0).ToList();
            var losses = tradeReturns.Where(x => x < 0).ToList();
            decimal grossWin = wins.Sum(), grossLoss = Math.Abs(losses.Sum());

            return new BacktestResult
            {
                TotalReturn = totalReturn,
                MaxDrawdown = maxDD,
                WinRate = tradeReturns.Count == 0 ? 0 :
                    (decimal)tradeReturns.Count(x => x > 0) / tradeReturns.Count * 100m,
                TradeCount = tradeReturns.Count,
                Sharpe = tradeReturns.Count < 2 ? 0 :
                    (decimal)(((double)tradeReturns.Average()) /
                        Math.Max(0.0001, StdDev(tradeReturns.Select(x => (double)x).ToList()))) * (decimal)Math.Sqrt(252),
                EquityCurve = equityCurve,
                TradeReturns = tradeReturns,
                ProfitFactor = grossLoss == 0 ? (grossWin > 0 ? 999m : 0m) : grossWin / grossLoss,
                AvgWin = wins.Count == 0 ? 0 : wins.Average(),
                AvgLoss = losses.Count == 0 ? 0 : losses.Average(),
                BestTrade = tradeReturns.Count == 0 ? 0 : tradeReturns.Max(),
                WorstTrade = tradeReturns.Count == 0 ? 0 : tradeReturns.Min(),
                InitialEquity = initial,
                FinalEquity = equity
            };
        }

        // M1: 사전계산된 closes/EMA를 받아 O(1)~O(period)로 신호 판정
        private static int GetSignal(List<Candle> candles, List<decimal> closes,
            List<decimal> ema12, List<decimal> ema26, int i, int strategy)
        {
            switch (strategy)
            {
                case 0:  // 골든/데드크로스
                    var ma5 = Avg(closes, i - 4, 5);
                    var ma20 = Avg(closes, i - 19, 20);
                    var ma5P = Avg(closes, i - 5, 5);
                    var ma20P = Avg(closes, i - 20, 20);
                    if (ma5P < ma20P && ma5 > ma20) return 1;
                    if (ma5P > ma20P && ma5 < ma20) return -1;
                    return 0;
                case 1:  // RSI
                    var rsi = CalcRsi(closes, i, 14);
                    if (rsi < 30) return 1;
                    if (rsi > 70) return -1;
                    return 0;
                case 2:  // 볼린저
                    var ma = Avg(closes, i - 19, 20);
                    double variance = 0;
                    for (int j = i - 19; j <= i; j++)
                    {
                        var diff = (double)(closes[j] - ma);
                        variance += diff * diff;
                    }
                    variance /= 20;
                    var std = (decimal)Math.Sqrt(variance);
                    if (candles[i].Close < ma - 2 * std) return 1;
                    if (candles[i].Close > ma + 2 * std) return -1;
                    return 0;
                case 3:  // MACD (사전계산 EMA 사용)
                    var macd = ema12[i] - ema26[i];
                    var macdP = ema12[i - 1] - ema26[i - 1];
                    if (macdP < 0 && macd > 0) return 1;
                    if (macdP > 0 && macd < 0) return -1;
                    return 0;
                case 4:  // 볼륨 스파이크 (인덱스 직접 접근, 할당 없음)
                    decimal volSum = 0, high20 = decimal.MinValue, low20 = decimal.MaxValue;
                    for (int j = i - 20; j < i; j++)
                    {
                        volSum += candles[j].Volume;
                        if (candles[j].High > high20) high20 = candles[j].High;
                        if (candles[j].Low < low20) low20 = candles[j].Low;
                    }
                    var avgVol = volSum / 20m;
                    if (candles[i].Volume > avgVol * 2 && candles[i].Close > high20) return 1;
                    if (candles[i].Volume > avgVol * 2 && candles[i].Close < low20) return -1;
                    return 0;
            }
            return 0;
        }

        private static decimal Avg(List<decimal> data, int start, int count)
        {
            if (start < 0) { count += start; start = 0; }
            if (count <= 0) return 0;
            decimal sum = 0;
            for (int j = start; j < start + count && j < data.Count; j++) sum += data[j];
            return sum / count;
        }

        // 전체 시리즈 EMA 1회 계산
        private static List<decimal> EmaSeries(List<decimal> data, int period)
        {
            var result = new List<decimal>(data.Count);
            if (data.Count == 0) return result;
            decimal k = 2m / (period + 1);
            result.Add(data[0]);
            for (int i = 1; i < data.Count; i++)
                result.Add(data[i] * k + result[i - 1] * (1 - k));
            return result;
        }

        private static decimal CalcRsi(List<decimal> closes, int i, int p)
        {
            decimal g = 0, l = 0;
            for (int j = i - p + 1; j <= i; j++)
            {
                var d = closes[j] - closes[j - 1];
                if (d > 0) g += d; else l -= d;
            }
            if (l == 0) return 100;
            var rs = g / p / (l / p);
            return 100 - 100 / (1 + rs);
        }

        private static double StdDev(List<double> v)
        {
            if (v.Count < 2) return 0;
            var avg = v.Average();
            return Math.Sqrt(v.Select(x => (x - avg) * (x - avg)).Sum() / v.Count);
        }

        // ===== Equity Curve Paint (거래일 X축) =====
        private void DrawBacktestChart(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _backtestChart.ClientRectangle;

            g.DrawString("📈 Equity Curve", GdiCache.Title, Brushes.White, 20, 15);
            if (_lastBacktest == null || _lastBacktest.EquityCurve.Count == 0)
            {
                g.DrawString("백테스팅 결과가 없습니다.", GdiCache.Large, Brushes.Gray,
                    rect.Width / 2f - 100, rect.Height / 2f);
                return;
            }

            var curve = _lastBacktest.EquityCurve;
            int padL = 80, padT = 60, padR = 40, padB = 40;
            var plotW = rect.Width - padL - padR;
            var plotH = rect.Height - padT - padB;

            var maxV = (double)curve.Max();
            var minV = (double)curve.Min();
            var range = maxV - minV;
            if (range == 0) range = 1;

            // Y축 레이블
            for (int i = 0; i <= 4; i++)
            {
                var v = maxV - range * i / 4;
                var y = padT + i * plotH / 4f;
                g.DrawLine(GdiCache.Pen(Color.FromArgb(50, 80, 80, 80)), padL, y, padL + plotW, y);
                g.DrawString($"{v:N0}", GdiCache.Small, Brushes.LightGray, 10, y - 8);
            }

            // ⚠️ 수정: X축을 거래일 인덱스 기반으로 (비영업일 왜곡 제거)
            var pen = GdiCache.Pen(_lastBacktest.TotalReturn >= 0 ? Color.LightGreen : Color.Salmon, 2);
            Point? prev = null;
            for (int i = 0; i < curve.Count; i++)
            {
                var x = padL + (float)i / (curve.Count - 1) * plotW;
                var y = padT + (float)((maxV - (double)curve[i]) / range) * plotH;
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, cur);
                prev = cur;
            }

            // X축 레이블 (거래일 기준)
            for (int i = 0; i <= 4; i++)
            {
                var idx = i * (curve.Count - 1) / 4;
                var x = padL + (float)idx / (curve.Count - 1) * plotW;
                g.DrawString($"Day {idx}", GdiCache.Small, Brushes.LightGray, x - 15, padT + plotH + 5);
            }

            // 요약 박스
            var info = $"시작 1,000,000 → 종료 {curve.Last():N0}\n수익률 {Formatter.Pct(_lastBacktest.TotalReturn)}\nMDD {_lastBacktest.MaxDrawdown:F2}%\n거래 {_lastBacktest.TradeCount}회 · 승률 {_lastBacktest.WinRate:F1}%\nSharpe {_lastBacktest.Sharpe:F2}\n(수수료·세금·슬리피지 반영)";
            using var box = new SolidBrush(Color.FromArgb(200, 40, 46, 58));
            g.FillRectangle(box, rect.Width - 260, 45, 240, 120);
            g.DrawString(info, GdiCache.Normal, Brushes.White, rect.Width - 250, 50);
        }
    }

    public class BacktestResult
    {
        public decimal TotalReturn { get; set; }
        public decimal MaxDrawdown { get; set; }
        public decimal WinRate { get; set; }
        public int TradeCount { get; set; }
        public decimal Sharpe { get; set; }
        public List<decimal> EquityCurve { get; set; } = new();

        // 리포트 대시보드용
        public List<decimal> TradeReturns { get; set; } = new();  // 거래별 수익률(%)
        public decimal ProfitFactor { get; set; }                 // 총이익/총손실
        public decimal AvgWin { get; set; }
        public decimal AvgLoss { get; set; }
        public decimal BestTrade { get; set; }
        public decimal WorstTrade { get; set; }
        public decimal InitialEquity { get; set; } = 1_000_000m;
        public decimal FinalEquity { get; set; }
        public string StrategyName { get; set; } = "";
    }
}
