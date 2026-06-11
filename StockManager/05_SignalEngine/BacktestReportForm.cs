// ============================================================================
// BacktestReportForm - 백테스트 리포트 대시보드 (v3.2)
//  · 통계 카드(수익률/MDD/승률/손익비/Sharpe/거래수) + 누적수익곡선 + 낙폭(언더워터)
//    + 거래 수익률 분포 히스토그램.  외부 의존성 없음(owner-draw).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using StockManager.Common;

namespace StockManager.SignalEngine
{
    public class BacktestReportForm : Form
    {
        private readonly BacktestResult _r;
        private Panel _cards = null!, _equity = null!, _under = null!, _hist = null!;

        public BacktestReportForm(BacktestResult r)
        {
            _r = r;
            Text = "📊 백테스트 리포트 — " + (string.IsNullOrEmpty(r.StrategyName) ? "전략 결과" : r.StrategyName);
            Size = new Size(1080, 760);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("맑은 고딕", 10F);
            BackColor = ThemeManager.Bg;
            ForeColor = ThemeManager.Text;

            _cards = new Panel { Dock = DockStyle.Top, Height = 120 };
            _cards.Paint += DrawCards;

            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            split.RowStyles.Add(new RowStyle(SizeType.Percent, 44));

            _equity = Titled("📈 누적 수익 곡선 (Equity Curve)", DrawEquity);
            _under = Titled("🌊 낙폭 (Underwater / Drawdown)", DrawUnderwater);
            _hist = Titled("📊 거래별 수익률 분포", DrawHistogram);

            split.Controls.Add(_equity, 0, 0);
            split.SetRowSpan(_equity, 1);
            split.Controls.Add(_hist, 1, 0);
            split.SetRowSpan(_hist, 2);
            split.Controls.Add(_under, 0, 1);

            Controls.Add(split);
            Controls.Add(_cards);
            split.BringToFront();
        }

        private Panel Titled(string title, PaintEventHandler paint)
        {
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var lbl = new Label { Text = title, Dock = DockStyle.Top, Height = 24, Font = new Font("맑은 고딕", 10F, FontStyle.Bold), ForeColor = ThemeManager.Text };
            var canvas = new Panel { Dock = DockStyle.Fill, BackColor = ThemeManager.Card };
            canvas.Paint += paint;
            host.Controls.Add(canvas);
            host.Controls.Add(lbl);
            return host;
        }

        // ===== 통계 카드 =====
        private void DrawCards(object? s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var items = new (string lab, string val, Color col)[]
            {
                ("총수익률", _r.TotalReturn.ToString("+0.00;-0.00") + "%", _r.TotalReturn >= 0 ? ThemeManager.Up : ThemeManager.Down),
                ("MDD", "-" + _r.MaxDrawdown.ToString("0.00") + "%", ThemeManager.Down),
                ("승률", _r.WinRate.ToString("0.0") + "%", ThemeManager.Text),
                ("손익비", _r.ProfitFactor >= 999 ? "∞" : _r.ProfitFactor.ToString("0.00"), _r.ProfitFactor >= 1 ? ThemeManager.Up : ThemeManager.Down),
                ("Sharpe", _r.Sharpe.ToString("0.00"), ThemeManager.Text),
                ("거래수", _r.TradeCount + "회", ThemeManager.Text),
            };
            int n = items.Length, pad = 16, w = (Width - pad * (n + 1)) / n;
            for (int i = 0; i < n; i++)
            {
                int x = pad + i * (w + pad);
                g.FillRectangle(GdiCache.Brush(ThemeManager.Card), x, 16, w, 88);
                using (var pen = new Pen(ThemeManager.Grid)) g.DrawRectangle(pen, x, 16, w, 88);
                g.FillRectangle(GdiCache.Brush(items[i].col), x, 16, 4, 88);
                g.DrawString(items[i].lab, GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), x + 16, 30);
                g.DrawString(items[i].val, GdiCache.LargeBold, GdiCache.Brush(items[i].col), x + 16, 54);
            }
            g.DrawString($"시작 {_r.InitialEquity:N0} → 종료 {_r.FinalEquity:N0}   ·   평균이익 {_r.AvgWin:+0.0}% / 평균손실 {_r.AvgLoss:0.0}%   ·   최고 {_r.BestTrade:+0.0}% / 최악 {_r.WorstTrade:0.0}%",
                GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), pad, 0);
        }

        // ===== Equity Curve =====
        private void DrawEquity(object? s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = (Control)s!; var R = c.ClientRectangle;
            var curve = _r.EquityCurve;
            if (curve.Count < 2) { g.DrawString("데이터 없음", GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), 12, 12); return; }
            float m = 28, w = R.Width - m * 2, h = R.Height - m * 2;
            decimal mn = curve.Min(), mx = curve.Max(); var rng = mx - mn == 0 ? 1 : mx - mn;

            // 기준선(초기자본)
            float y0 = (float)(m + h - (double)((_r.InitialEquity - mn) / rng) * h);
            using (var dash = new Pen(ThemeManager.Grid) { DashStyle = DashStyle.Dash })
                g.DrawLine(dash, m, y0, m + w, y0);

            var pts = new PointF[curve.Count];
            for (int i = 0; i < curve.Count; i++)
                pts[i] = new PointF(m + w * i / (curve.Count - 1), (float)(m + h - (double)((curve[i] - mn) / rng) * h));

            // 면적 채움
            var fill = new List<PointF>(pts) { new PointF(m + w, m + h), new PointF(m, m + h) };
            var col = _r.TotalReturn >= 0 ? ThemeManager.Up : ThemeManager.Down;
            using (var br = new SolidBrush(Color.FromArgb(28, col))) g.FillPolygon(br, fill.ToArray());
            using (var pen = new Pen(col, 2)) g.DrawLines(pen, pts);

            g.DrawString($"{curve.Last():N0}", GdiCache.SmallBold, GdiCache.Brush(col), m + w - 70, pts[^1].Y - 16);
        }

        // ===== Underwater (drawdown) =====
        private void DrawUnderwater(object? s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = (Control)s!; var R = c.ClientRectangle;
            var curve = _r.EquityCurve;
            if (curve.Count < 2) { g.DrawString("데이터 없음", GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), 12, 12); return; }
            float m = 28, w = R.Width - m * 2, h = R.Height - m * 2;

            var dd = new double[curve.Count]; decimal peak = curve[0]; double maxdd = 0;
            for (int i = 0; i < curve.Count; i++)
            {
                if (curve[i] > peak) peak = curve[i];
                dd[i] = peak == 0 ? 0 : -(double)((peak - curve[i]) / peak) * 100;
                if (-dd[i] > maxdd) maxdd = -dd[i];
            }
            double span = maxdd == 0 ? 1 : maxdd;
            g.DrawLine(new Pen(ThemeManager.Grid), m, m, m + w, m); // 0% 상단
            var pts = new PointF[curve.Count];
            for (int i = 0; i < curve.Count; i++)
                pts[i] = new PointF(m + w * i / (curve.Count - 1), (float)(m + (-dd[i] / span) * h));
            var fill = new List<PointF> { new PointF(m, m) };
            fill.AddRange(pts); fill.Add(new PointF(m + w, m));
            using (var br = new SolidBrush(Color.FromArgb(50, ThemeManager.Down))) g.FillPolygon(br, fill.ToArray());
            using (var pen = new Pen(ThemeManager.Down, 1.5f)) g.DrawLines(pen, pts);
            g.DrawString($"최대 낙폭 -{maxdd:0.00}%", GdiCache.SmallBold, GdiCache.Brush(ThemeManager.Down), m + 4, m + 4);
        }

        // ===== Histogram of trade returns =====
        private void DrawHistogram(object? s, PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var c = (Control)s!; var R = c.ClientRectangle;
            var tr = _r.TradeReturns;
            if (tr.Count == 0) { g.DrawString("거래 없음", GdiCache.Normal, GdiCache.Brush(ThemeManager.SubText), 12, 12); return; }
            const int BINS = 9;
            decimal lo = tr.Min(), hi = tr.Max(); var rng = hi - lo == 0 ? 1 : hi - lo;
            var bins = new int[BINS];
            foreach (var v in tr) { int b = (int)((v - lo) / rng * (BINS - 1)); bins[Math.Clamp(b, 0, BINS - 1)]++; }
            int maxC = bins.Max(); if (maxC == 0) maxC = 1;
            float m = 28, w = R.Width - m * 2, h = R.Height - m * 2, bw = w / BINS;
            for (int i = 0; i < BINS; i++)
            {
                float bh = (float)bins[i] / maxC * h;
                float x = m + i * bw;
                var center = lo + rng * ((decimal)i + 0.5m) / BINS;
                var col = center >= 0 ? ThemeManager.Up : ThemeManager.Down;
                g.FillRectangle(GdiCache.Brush(col), x + 2, m + h - bh, bw - 4, bh);
                if (bins[i] > 0)
                    g.DrawString(bins[i].ToString(), GdiCache.Small, GdiCache.Brush(ThemeManager.Text), x + bw / 2 - 5, m + h - bh - 14);
            }
            g.DrawString($"{lo:0.0}%", GdiCache.Small, GdiCache.Brush(ThemeManager.SubText), m, m + h + 4);
            g.DrawString($"{hi:0.0}%", GdiCache.Small, GdiCache.Brush(ThemeManager.SubText), m + w - 36, m + h + 4);
        }
    }
}
