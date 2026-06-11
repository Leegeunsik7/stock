// ============================================================================
// Program #1: ChartForm (v2.2)
// [개선점]
//  · GdiCache 적용 (Font/Brush 누수 제거)
//  · TakeLast 한 번만 materialize
//  · using var로 Pen/Brush 명시 해제
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.RealtimeWatcher
{
    public class ChartForm : Form
    {
        private readonly IQuoteProvider? _provider;
        private readonly string _symbol;
        private readonly Market _market;
        private List<Candle> _candles = new();
        private readonly bool _external;
        private readonly List<Candle> _externalCandles = new();

        private ComboBox _periodCombo = null!;
        private ComboBox _indicatorCombo = null!;
        private Panel _chartArea = null!;
        private Panel _volumeArea = null!;
        private Panel _indicatorArea = null!;
        private Label _title = null!;

        private int _visibleCount = 100;
        private int _scrollOffset = 0;
        private Point _mousePos;

        public ChartForm(IQuoteProvider provider, string symbol, Market market)
        {
            _provider = provider;
            _symbol = symbol;
            _market = market;
            InitializeUI();
            Load += async (s, e) => await LoadDataAsync();
        }

        /// <summary>외부에서 준비한 캔들로 차트 표시 (코인 등 비주식 데이터 공유용)</summary>
        public ChartForm(string title, List<Candle> candles)
        {
            _provider = null;
            _symbol = title;
            _market = Market.KRX_KOSPI;   // 미사용
            _external = true;
            _externalCandles = candles ?? new List<Candle>();
            InitializeUI();
            Load += async (s, e) => await LoadDataAsync();
        }

        private void InitializeUI()
        {
            Text = $"📊 차트 - {_symbol}";
            Size = new Size(1400, 850);
            BackColor = Color.FromArgb(20, 24, 32);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            var toolbar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 50,
                BackColor = Color.FromArgb(35, 40, 52)
            };

            _title = new Label
            {
                Text = _symbol,
                Font = new Font("맑은 고딕", 14F, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(15, 12),
                AutoSize = true
            };
            toolbar.Controls.Add(_title);

            _periodCombo = new ComboBox
            {
                Location = new Point(250, 15),
                Width = 80,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _periodCombo.Items.AddRange(new[] { "3개월", "6개월", "1년", "3년", "5년" });
            _periodCombo.SelectedIndex = 1;
            _periodCombo.SelectedIndexChanged += async (s, e) => await LoadDataAsync();
            toolbar.Controls.Add(_periodCombo);

            _indicatorCombo = new ComboBox
            {
                Location = new Point(340, 15),
                Width = 120,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _indicatorCombo.Items.AddRange(new[] { "RSI", "MACD", "Stochastic", "볼린저밴드" });
            _indicatorCombo.SelectedIndex = 0;
            _indicatorCombo.SelectedIndexChanged += (s, e) => Invalidate(true);
            toolbar.Controls.Add(_indicatorCombo);

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                BackColor = Color.FromArgb(20, 24, 32)
            };
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 17));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 18));

            _chartArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _chartArea.Paint += DrawMainChart;
            _chartArea.MouseMove += (s, e) => { _mousePos = e.Location; _chartArea.Invalidate(); };
            _chartArea.MouseWheel += (s, e) =>
            {
                _visibleCount = Math.Max(20, Math.Min(_candles.Count, _visibleCount + (e.Delta > 0 ? -10 : 10)));
                _chartArea.Invalidate();
                _volumeArea.Invalidate();
                _indicatorArea.Invalidate();
            };

            _volumeArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _volumeArea.Paint += DrawVolumeChart;

            _indicatorArea = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _indicatorArea.Paint += DrawIndicator;

            main.Controls.Add(_chartArea, 0, 0);
            main.Controls.Add(_volumeArea, 0, 1);
            main.Controls.Add(_indicatorArea, 0, 2);

            Controls.Add(main);
            Controls.Add(toolbar);
        }

        private async Task LoadDataAsync()
        {
            if (_external)
            {
                _candles = _externalCandles;
            }
            else
            {
                var months = _periodCombo.SelectedIndex switch { 0 => 3, 1 => 6, 2 => 12, 3 => 36, _ => 60 };
                _candles = await _provider!.GetHistoryAsync(_symbol, _market,
                    DateTime.Now.AddMonths(-months), DateTime.Now);
            }
            _visibleCount = Math.Min(100, _candles.Count);
            _scrollOffset = 0;

            if (_candles.Count > 0)
            {
                var last = _candles[^1];
                var prev = _candles.Count > 1 ? _candles[^2] : last;
                var rate = prev.Close == 0 ? 0 : (last.Close - prev.Close) / prev.Close * 100m;
                _title.Text = $"{_symbol}   {last.Close:N0}   {Formatter.Pct(rate)}";
                _title.ForeColor = Formatter.ColorForChange(rate);
            }
            Invalidate(true);
        }

        private void DrawMainChart(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.HighQuality;
            var rect = _chartArea.ClientRectangle;

            if (_candles.Count == 0)
            {
                g.DrawString("데이터 로딩 중...", GdiCache.Title, Brushes.Gray,
                    rect.Width / 2f - 50, rect.Height / 2f);
                return;
            }

            var start = Math.Max(0, _candles.Count - _visibleCount - _scrollOffset);
            var end = Math.Min(_candles.Count, start + _visibleCount);
            var visible = _candles.GetRange(start, end - start);
            if (visible.Count == 0) return;

            var maxP = visible.Max(c => c.High);
            var minP = visible.Min(c => c.Low);
            var range = maxP - minP;
            if (range == 0) range = 1;
            var margin = range * 0.05m;
            maxP += margin; minP -= margin;

            var padL = 60; var padR = 80; var padT = 20; var padB = 20;
            var plotW = rect.Width - padL - padR;
            var plotH = rect.Height - padT - padB;
            var cellW = (float)plotW / visible.Count;

            // 그리드
            using var gridPen = new Pen(Color.FromArgb(40, 45, 55)) { DashStyle = DashStyle.Dot };
            for (int i = 0; i <= 5; i++)
            {
                var y = padT + i * plotH / 5f;
                g.DrawLine(gridPen, padL, y, padL + plotW, y);
                var price = maxP - (maxP - minP) * i / 5;
                g.DrawString($"{price:N0}", GdiCache.Small, Brushes.LightGray, padL + plotW + 4, y - 7);
            }

            DrawMA(g, visible, start, 5, Color.Yellow, padL, padT, cellW, plotH, maxP, minP);
            DrawMA(g, visible, start, 20, Color.Magenta, padL, padT, cellW, plotH, maxP, minP);
            DrawMA(g, visible, start, 60, Color.Cyan, padL, padT, cellW, plotH, maxP, minP);

            for (int i = 0; i < visible.Count; i++)
            {
                var c = visible[i];
                var x = padL + i * cellW + cellW / 2;
                var yHigh = padT + (float)((double)(maxP - c.High) / (double)(maxP - minP)) * plotH;
                var yLow = padT + (float)((double)(maxP - c.Low) / (double)(maxP - minP)) * plotH;
                var yOpen = padT + (float)((double)(maxP - c.Open) / (double)(maxP - minP)) * plotH;
                var yClose = padT + (float)((double)(maxP - c.Close) / (double)(maxP - minP)) * plotH;

                var isUp = c.Close >= c.Open;
                var col = isUp ? Color.FromArgb(220, 50, 50) : Color.FromArgb(50, 90, 200);
                g.DrawLine(GdiCache.Pen(col), x, yHigh, x, yLow);

                var bodyW = Math.Max(1, cellW * 0.7f);
                var bodyTop = Math.Min(yOpen, yClose);
                var bodyH = Math.Max(1, Math.Abs(yClose - yOpen));
                g.FillRectangle(GdiCache.Brush(col), x - bodyW / 2, bodyTop, bodyW, bodyH);
            }

            // 크로스헤어
            if (_mousePos.X > padL && _mousePos.X < padL + plotW &&
                _mousePos.Y > padT && _mousePos.Y < padT + plotH)
            {
                using var p = new Pen(Color.FromArgb(120, 200, 200, 200)) { DashStyle = DashStyle.Dash };
                g.DrawLine(p, _mousePos.X, padT, _mousePos.X, padT + plotH);
                g.DrawLine(p, padL, _mousePos.Y, padL + plotW, _mousePos.Y);

                var idx = (int)((_mousePos.X - padL) / cellW);
                if (idx >= 0 && idx < visible.Count)
                {
                    var c = visible[idx];
                    var tip = $"{c.Date:yyyy-MM-dd}  O:{c.Open:N0} H:{c.High:N0} L:{c.Low:N0} C:{c.Close:N0}  Vol:{c.Volume:N0}";
                    using var br = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
                    g.FillRectangle(br, padL + 4, padT + 4, 380, 22);
                    g.DrawString(tip, GdiCache.Normal, Brushes.White, padL + 6, padT + 6);
                }
            }

            g.DrawString("MA5", GdiCache.SmallBold, Brushes.Yellow, padL + plotW - 130, padT);
            g.DrawString("MA20", GdiCache.SmallBold, Brushes.Magenta, padL + plotW - 90, padT);
            g.DrawString("MA60", GdiCache.SmallBold, Brushes.Cyan, padL + plotW - 45, padT);
        }

        private void DrawMA(Graphics g, List<Candle> visible, int offset, int period, Color color,
            int padL, int padT, float cellW, float plotH, decimal maxP, decimal minP)
        {
            if (_candles.Count < period) return;
            var pen = GdiCache.Pen(color, 1.5f);
            Point? prev = null;
            for (int i = 0; i < visible.Count; i++)
            {
                var globalIdx = offset + i;
                if (globalIdx < period - 1) continue;
                decimal sum = 0;
                for (int j = 0; j < period; j++) sum += _candles[globalIdx - j].Close;
                var ma = sum / period;
                var x = padL + i * cellW + cellW / 2;
                var y = padT + (float)((double)(maxP - ma) / (double)(maxP - minP)) * plotH;
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, cur);
                prev = cur;
            }
        }

        private void DrawVolumeChart(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = _volumeArea.ClientRectangle;
            if (_candles.Count == 0) return;

            var start = Math.Max(0, _candles.Count - _visibleCount - _scrollOffset);
            var end = Math.Min(_candles.Count, start + _visibleCount);
            var visible = _candles.GetRange(start, end - start);
            if (visible.Count == 0) return;

            var maxV = visible.Max(c => c.Volume);
            if (maxV == 0) maxV = 1;

            int padL = 60, padR = 80, padT = 5, padB = 5;
            var plotW = rect.Width - padL - padR;
            var plotH = rect.Height - padT - padB;
            var cellW = (float)plotW / visible.Count;

            g.DrawString("거래량", GdiCache.SmallBold, Brushes.LightGray, 8, padT);

            for (int i = 0; i < visible.Count; i++)
            {
                var c = visible[i];
                var h = (float)c.Volume / maxV * plotH;
                var x = padL + i * cellW + cellW / 2;
                var col = c.Close >= c.Open
                    ? Color.FromArgb(180, 220, 50, 50)
                    : Color.FromArgb(180, 50, 90, 200);
                g.FillRectangle(GdiCache.Brush(col), x - cellW * 0.35f, padT + plotH - h, cellW * 0.7f, h);
            }
        }

        private void DrawIndicator(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = _indicatorArea.ClientRectangle;
            if (_candles.Count < 30) return;

            int padL = 60, padR = 80, padT = 10, padB = 10;
            var plotW = rect.Width - padL - padR;
            var plotH = rect.Height - padT - padB;

            var indicator = _indicatorCombo.SelectedItem?.ToString() ?? "RSI";
            if (indicator == "RSI") DrawRSI(g, padL, padT, plotW, plotH);
            else if (indicator == "MACD") DrawMACD(g, padL, padT, plotW, plotH);
            else if (indicator == "Stochastic") DrawStochastic(g, padL, padT, plotW, plotH);
            else if (indicator == "볼린저밴드") DrawBollingerB(g, padL, padT, plotW, plotH);
        }

        // ===== Stochastic %K/%D (H3) =====
        private void DrawStochastic(Graphics g, int padL, int padT, int plotW, int plotH)
        {
            var (k, d) = CalcStochastic(14, 3);
            var start = Math.Max(0, _candles.Count - _visibleCount - _scrollOffset);
            var end = Math.Min(_candles.Count, start + _visibleCount);
            var cellW = (float)plotW / (end - start);

            using var pen20 = new Pen(Color.FromArgb(100, 50, 90, 200)) { DashStyle = DashStyle.Dot };
            using var pen80 = new Pen(Color.FromArgb(100, 220, 50, 50)) { DashStyle = DashStyle.Dot };
            var y20 = padT + plotH * 0.8f;
            var y80 = padT + plotH * 0.2f;
            g.DrawLine(pen20, padL, y20, padL + plotW, y20);
            g.DrawLine(pen80, padL, y80, padL + plotW, y80);
            g.DrawString("20", GdiCache.Small, Brushes.LightSkyBlue, padL + plotW + 4, y20 - 6);
            g.DrawString("80", GdiCache.Small, Brushes.Salmon, padL + plotW + 4, y80 - 6);
            g.DrawString("Stochastic(14,3)", GdiCache.SmallBold, Brushes.LightGray, 8, padT);

            DrawSeries(g, k, start, end, padL, padT, plotW, plotH, Color.Orange);  // %K
            DrawSeries(g, d, start, end, padL, padT, plotW, plotH, Color.Cyan);    // %D

            g.DrawString("%K", GdiCache.SmallBold, Brushes.Orange, padL + plotW - 70, padT);
            g.DrawString("%D", GdiCache.SmallBold, Brushes.Cyan, padL + plotW - 40, padT);
        }

        // 0~100 스케일 시리즈 라인
        private void DrawSeries(Graphics g, List<decimal> data, int start, int end,
            int padL, int padT, int plotW, int plotH, Color color)
        {
            var cellW = (float)plotW / (end - start);
            var pen = GdiCache.Pen(color, 1.5f);
            Point? prev = null;
            for (int i = 0; i < end - start; i++)
            {
                var idx = start + i;
                if (idx >= data.Count) break;
                var v = (float)data[idx];
                var x = padL + i * cellW + cellW / 2;
                var y = padT + (100 - v) / 100f * plotH;
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, cur);
                prev = cur;
            }
        }

        private (List<decimal> k, List<decimal> d) CalcStochastic(int period, int smooth)
        {
            var k = new List<decimal>();
            for (int i = 0; i < _candles.Count; i++)
            {
                if (i < period - 1) { k.Add(50); continue; }
                decimal hh = decimal.MinValue, ll = decimal.MaxValue;
                for (int j = i - period + 1; j <= i; j++)
                {
                    if (_candles[j].High > hh) hh = _candles[j].High;
                    if (_candles[j].Low < ll) ll = _candles[j].Low;
                }
                var rng = hh - ll;
                k.Add(rng == 0 ? 50 : (_candles[i].Close - ll) / rng * 100m);
            }
            // %D = %K의 smooth기간 단순이동평균
            var d = new List<decimal>();
            for (int i = 0; i < k.Count; i++)
            {
                if (i < smooth - 1) { d.Add(k[i]); continue; }
                decimal sum = 0;
                for (int j = 0; j < smooth; j++) sum += k[i - j];
                d.Add(sum / smooth);
            }
            return (k, d);
        }

        // ===== 볼린저밴드 %B (H3) =====
        private void DrawBollingerB(Graphics g, int padL, int padT, int plotW, int plotH)
        {
            var pctB = CalcBollingerPctB(20, 2m);
            var start = Math.Max(0, _candles.Count - _visibleCount - _scrollOffset);
            var end = Math.Min(_candles.Count, start + _visibleCount);
            var cellW = (float)plotW / (end - start);

            // 0 / 0.5 / 1.0 가이드 (밴드 하단/중앙/상단)
            using var penMid = new Pen(Color.FromArgb(100, 180, 180, 180)) { DashStyle = DashStyle.Dot };
            using var penEdge = new Pen(Color.FromArgb(80, 200, 130, 80)) { DashStyle = DashStyle.Dot };
            var yTop = padT + plotH * 0.1f;     // %B = 1.0 (상단밴드)
            var yMid = padT + plotH * 0.5f;     // %B = 0.5 (중심선)
            var yBot = padT + plotH * 0.9f;     // %B = 0.0 (하단밴드)
            g.DrawLine(penEdge, padL, yTop, padL + plotW, yTop);
            g.DrawLine(penMid, padL, yMid, padL + plotW, yMid);
            g.DrawLine(penEdge, padL, yBot, padL + plotW, yBot);
            g.DrawString("상단", GdiCache.Small, Brushes.Salmon, padL + plotW + 4, yTop - 6);
            g.DrawString("중심", GdiCache.Small, Brushes.LightGray, padL + plotW + 4, yMid - 6);
            g.DrawString("하단", GdiCache.Small, Brushes.LightSkyBlue, padL + plotW + 4, yBot - 6);
            g.DrawString("볼린저 %B (20,2σ)", GdiCache.SmallBold, Brushes.LightGray, 8, padT);

            var pen = GdiCache.Pen(Color.Violet, 1.8f);
            Point? prev = null;
            for (int i = 0; i < end - start; i++)
            {
                var idx = start + i;
                if (idx >= pctB.Count) break;
                // %B 0..1 → 패널 90%~10% 구간에 매핑
                var v = (float)Math.Clamp(pctB[idx], -0.5m, 1.5m);
                var x = padL + i * cellW + cellW / 2;
                var y = padT + (0.9f - v * 0.8f) * plotH;
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, cur);
                prev = cur;
            }
        }

        private List<decimal> CalcBollingerPctB(int period, decimal mult)
        {
            var closes = _candles.Select(c => c.Close).ToList();
            var result = new List<decimal>();
            for (int i = 0; i < closes.Count; i++)
            {
                if (i < period - 1) { result.Add(0.5m); continue; }
                decimal sum = 0;
                for (int j = i - period + 1; j <= i; j++) sum += closes[j];
                var ma = sum / period;
                double varSum = 0;
                for (int j = i - period + 1; j <= i; j++)
                {
                    var diff = (double)(closes[j] - ma);
                    varSum += diff * diff;
                }
                var std = (decimal)Math.Sqrt(varSum / period);
                var upper = ma + mult * std;
                var lower = ma - mult * std;
                var band = upper - lower;
                result.Add(band == 0 ? 0.5m : (closes[i] - lower) / band);
            }
            return result;
        }

        private void DrawRSI(Graphics g, int padL, int padT, int plotW, int plotH)
        {
            var rsi = CalcRSI(14);
            var start = Math.Max(0, _candles.Count - _visibleCount - _scrollOffset);
            var end = Math.Min(_candles.Count, start + _visibleCount);
            var cellW = (float)plotW / (end - start);

            using var pen30 = new Pen(Color.FromArgb(100, 50, 90, 200)) { DashStyle = DashStyle.Dot };
            using var pen70 = new Pen(Color.FromArgb(100, 220, 50, 50)) { DashStyle = DashStyle.Dot };
            var y30 = padT + plotH * 0.7f;
            var y70 = padT + plotH * 0.3f;
            g.DrawLine(pen30, padL, y30, padL + plotW, y30);
            g.DrawLine(pen70, padL, y70, padL + plotW, y70);
            g.DrawString("30", GdiCache.Small, Brushes.LightSkyBlue, padL + plotW + 4, y30 - 6);
            g.DrawString("70", GdiCache.Small, Brushes.Salmon, padL + plotW + 4, y70 - 6);
            g.DrawString("RSI(14)", GdiCache.SmallBold, Brushes.LightGray, 8, padT);

            var line = GdiCache.Pen(Color.Orange, 1.5f);
            Point? prev = null;
            for (int i = 0; i < end - start; i++)
            {
                var idx = start + i;
                if (idx >= rsi.Count) break;
                var v = rsi[idx];
                var x = padL + i * cellW + cellW / 2;
                var y = padT + (100 - (float)v) / 100f * plotH;
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(line, prev.Value, cur);
                prev = cur;
            }
        }

        private void DrawMACD(Graphics g, int padL, int padT, int plotW, int plotH)
        {
            var (macd, signal, hist) = CalcMACD();
            var start = Math.Max(0, _candles.Count - _visibleCount - _scrollOffset);
            var end = Math.Min(_candles.Count, start + _visibleCount);
            var cellW = (float)plotW / (end - start);

            var range = end - start;
            var macdSlice = macd.Skip(start).Take(range).ToList();
            var signalSlice = signal.Skip(start).Take(range).ToList();
            var histSlice = hist.Skip(start).Take(range).ToList();

            var maxV = Math.Max(
                Math.Max(macdSlice.DefaultIfEmpty(0).Max(x => Math.Abs((double)x)),
                         signalSlice.DefaultIfEmpty(0).Max(x => Math.Abs((double)x))),
                histSlice.DefaultIfEmpty(0).Max(x => Math.Abs((double)x)));
            if (maxV == 0) maxV = 1;

            var zeroY = padT + plotH / 2f;
            g.DrawLine(GdiCache.Pen(Color.FromArgb(100, 180, 180, 180)), padL, zeroY, padL + plotW, zeroY);
            g.DrawString("MACD(12,26,9)", GdiCache.SmallBold, Brushes.LightGray, 8, padT);

            for (int i = 0; i < histSlice.Count; i++)
            {
                var h = (float)((double)histSlice[i] / maxV * (plotH / 2f));
                var x = padL + i * cellW + cellW / 2;
                var col = histSlice[i] >= 0 ? Color.FromArgb(180, 220, 50, 50) : Color.FromArgb(180, 50, 90, 200);
                g.FillRectangle(GdiCache.Brush(col), x - cellW * 0.35f, zeroY - Math.Max(0, h), cellW * 0.7f, Math.Abs(h));
            }

            DrawLine(g, macd, start, end, padL, padT, plotW, plotH, maxV, Color.Cyan);
            DrawLine(g, signal, start, end, padL, padT, plotW, plotH, maxV, Color.Orange);
        }

        private void DrawLine(Graphics g, List<decimal> data, int start, int end,
            int padL, int padT, int plotW, int plotH, double maxV, Color color)
        {
            var cellW = (float)plotW / (end - start);
            var pen = GdiCache.Pen(color, 1.5f);
            Point? prev = null;
            for (int i = 0; i < end - start; i++)
            {
                var idx = start + i;
                if (idx >= data.Count) break;
                var x = padL + i * cellW + cellW / 2;
                var y = padT + plotH / 2f - (float)((double)data[idx] / maxV * (plotH / 2f));
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, cur);
                prev = cur;
            }
        }

        private List<decimal> CalcRSI(int period)
        {
            var result = new List<decimal>();
            var gains = new List<decimal>();
            var losses = new List<decimal>();
            for (int i = 0; i < _candles.Count; i++)
            {
                if (i == 0) { gains.Add(0); losses.Add(0); result.Add(50); continue; }
                var diff = _candles[i].Close - _candles[i - 1].Close;
                gains.Add(Math.Max(0, diff));
                losses.Add(Math.Max(0, -diff));
                if (i < period) { result.Add(50); continue; }
                var avgG = gains.GetRange(i - period + 1, period).Average();
                var avgL = losses.GetRange(i - period + 1, period).Average();
                var rs = avgL == 0 ? 100 : avgG / avgL;
                result.Add(100 - 100 / (1 + rs));
            }
            return result;
        }

        private (List<decimal>, List<decimal>, List<decimal>) CalcMACD(int fast = 12, int slow = 26, int sig = 9)
        {
            var closes = _candles.Select(c => c.Close).ToList();
            var emaF = EMA(closes, fast);
            var emaS = EMA(closes, slow);
            var macd = new List<decimal>();
            for (int i = 0; i < closes.Count; i++) macd.Add(emaF[i] - emaS[i]);
            var signal = EMA(macd, sig);
            var hist = new List<decimal>();
            for (int i = 0; i < macd.Count; i++) hist.Add(macd[i] - signal[i]);
            return (macd, signal, hist);
        }

        private List<decimal> EMA(List<decimal> data, int period)
        {
            var result = new List<decimal>();
            decimal k = 2m / (period + 1);
            for (int i = 0; i < data.Count; i++)
            {
                if (i == 0) result.Add(data[0]);
                else result.Add(data[i] * k + result[i - 1] * (1 - k));
            }
            return result;
        }
    }
}
