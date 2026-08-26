// ============================================================================
// Program #3: AI Analysis (v2.2)
// [개선점]
//  · GdiCache 적용 (Paint 이벤트 GDI+ 누수 제거)
//  · HttpPool.Shared 사용
//  · Safe.Dec 적용
//  · Gemini JSON 강제 응답 (responseMimeType=application/json)
//  · Settings에서 API 키 로드
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace StockManager.AIAnalysis
{
    public class MainForm : Form
    {
        private readonly IQuoteProvider _provider;
        private readonly LocalDatabase _db;

        private TextBox _txtSymbol = null!;
        private ComboBox _cbMarket = null!;
        private Button _btnAnalyze = null!;
        private Panel _radarPanel = null!;
        private DataGridView _peerGrid = null!;
        private RichTextBox _rtxSummary = null!;
        private Label _lblScore = null!;
        private ProgressBar _progress = null!;
        private ComboBox _cbModel = null!;

        private readonly Dictionary<string, decimal> _indicators = new();

        public MainForm(IQuoteProvider provider, LocalDatabase db)
        {
            _provider = provider;
            _db = db;
            InitializeUI();
        }

        private void InitializeUI()
        {
            Text = "🧠 AI 기업 분석 - MS2-E Stock Suite";
            Size = new Size(1500, 900);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            var header = new Panel
            {
                Dock = DockStyle.Top, Height = 70,
                BackColor = Color.FromArgb(35, 40, 55),
                Padding = new Padding(15, 15, 15, 15)
            };

            _txtSymbol = new TextBox
            {
                Location = new Point(20, 20), Width = 160, Height = 32,
                Font = new Font("맑은 고딕", 12F),
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "예: 005930 또는 AAPL"
            };

            _cbMarket = new ComboBox
            {
                Location = new Point(190, 20),
                Width = 130, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 10F)
            };
            _cbMarket.Items.AddRange(new object[] { "KRX-KOSPI", "KRX-KOSDAQ", "US-NYSE", "US-NASDAQ" });
            _cbMarket.SelectedIndex = 0;

            _cbModel = new ComboBox
            {
                Location = new Point(330, 20),
                Width = 180, DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cbModel.Items.AddRange(new object[] { "gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash" });
            _cbModel.SelectedIndex = 0;

            _btnAnalyze = new Button
            {
                Text = "🚀 AI 분석 시작",
                Location = new Point(520, 18), Size = new Size(170, 36),
                BackColor = Color.FromArgb(70, 130, 200),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
                Font = new Font("맑은 고딕", 11F, FontStyle.Bold)
            };
            _btnAnalyze.Click += async (s, e) => await AnalyzeAsync();

            _progress = new ProgressBar
            {
                Location = new Point(700, 30), Width = 250, Height = 14, Visible = false
            };

            header.Controls.AddRange(new Control[] { _txtSymbol, _cbMarket, _cbModel, _btnAnalyze, _progress });

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterWidth = 4
            };

            // 좌측: 레이더 + 스코어
            var leftPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = Color.FromArgb(24, 28, 36)
            };
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            _radarPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _radarPanel.Paint += DrawRadar;

            var scorePanel = new Panel
            {
                Dock = DockStyle.Fill, BackColor = Color.FromArgb(30, 34, 44), Padding = new Padding(20)
            };
            _lblScore = new Label
            {
                Text = "종합 점수: —", Font = new Font("맑은 고딕", 24F, FontStyle.Bold),
                ForeColor = Color.Gold, Location = new Point(30, 30), AutoSize = true
            };
            scorePanel.Controls.Add(_lblScore);
            scorePanel.Paint += DrawScoreDetails;

            leftPanel.Controls.Add(_radarPanel, 0, 0);
            leftPanel.Controls.Add(scorePanel, 0, 1);

            // 우측: 요약 + 동종업계
            var rightPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1,
                BackColor = Color.FromArgb(24, 28, 36)
            };
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            rightPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 40));

            _rtxSummary = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Font = new Font("맑은 고딕", 11F),
                ReadOnly = true, ScrollBars = RichTextBoxScrollBars.Vertical
            };

            _peerGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false, ReadOnly = true,
                AllowUserToAddRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false
            };
            _peerGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White
            };
            _peerGrid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White
            };
            foreach (var c in new[] { "Symbol", "Name", "Price", "PER", "PBR", "ROE" })
                _peerGrid.Columns.Add(c, c);

            rightPanel.Controls.Add(_rtxSummary, 0, 0);
            rightPanel.Controls.Add(_peerGrid, 0, 1);

            split.Panel1.Controls.Add(leftPanel);
            split.Panel2.Controls.Add(rightPanel);
            split.SplitterDistance = 700;

            Controls.Add(split);
            Controls.Add(header);
        }

        private async Task AnalyzeAsync()
        {
            var symbol = _txtSymbol.Text.Trim();
            if (string.IsNullOrEmpty(symbol)) { MessageBox.Show("종목코드 입력"); return; }

            var market = _cbMarket.SelectedIndex switch
            {
                0 => Market.KRX_KOSPI, 1 => Market.KRX_KOSDAQ,
                2 => Market.US_NYSE, _ => Market.US_NASDAQ
            };

            _btnAnalyze.Enabled = false;
            _progress.Visible = true;
            _progress.Value = 10;
            _rtxSummary.Clear();

            try
            {
                var q = await _provider.GetQuoteAsync(symbol, market);
                if (q == null) { MessageBox.Show("시세 조회 실패"); return; }

                _progress.Value = 30;

                // 모의 지표 (실제로는 재무제표 API 연동 필요)
                _indicators.Clear();
                var rnd = new Random(symbol.GetHashCode());
                _indicators["성장성"] = 40 + (decimal)rnd.NextDouble() * 60;
                _indicators["수익성"] = 40 + (decimal)rnd.NextDouble() * 60;
                _indicators["안정성"] = 40 + (decimal)rnd.NextDouble() * 60;
                _indicators["밸류에이션"] = 40 + (decimal)rnd.NextDouble() * 60;
                _indicators["모멘텀"] = 40 + (decimal)rnd.NextDouble() * 60;
                _indicators["배당"] = 30 + (decimal)rnd.NextDouble() * 40;

                _progress.Value = 60;
                _radarPanel.Invalidate();

                var avgScore = _indicators.Values.Average();
                _lblScore.Text = $"종합 점수: {avgScore:F1} / 100";
                _lblScore.ForeColor = avgScore >= 70 ? Color.LightGreen
                    : avgScore >= 50 ? Color.Gold : Color.Salmon;

                // Gemini AI 요약 (JSON 강제 응답)
                var summary = await CallGeminiJsonAsync(symbol, q, _indicators);
                _rtxSummary.Clear();
                AppendColored(_rtxSummary, $"📊 {q.Name} ({symbol})\n", Color.Gold, 14, true);
                AppendColored(_rtxSummary, $"{Formatter.Money(q.Price, q.Currency)}  {Formatter.Pct(q.ChangeRate)}\n\n",
                    Formatter.ColorForChange(q.ChangeRate), 11, true);

                if (summary != null)
                {
                    AppendColored(_rtxSummary, "💡 투자 의견: ", Color.LightGray, 11, false);
                    AppendColored(_rtxSummary, $"{summary.Recommendation}\n\n",
                        summary.Recommendation == "매수" ? Color.LightGreen :
                        summary.Recommendation == "매도" ? Color.Salmon : Color.Gold, 12, true);

                    AppendColored(_rtxSummary, "✅ 긍정 요인\n", Color.LightGreen, 11, true);
                    foreach (var p in summary.Positives)
                        AppendColored(_rtxSummary, $"  • {p}\n", Color.White, 10, false);

                    AppendColored(_rtxSummary, "\n⚠️ 위험 요인\n", Color.Salmon, 11, true);
                    foreach (var n in summary.Negatives)
                        AppendColored(_rtxSummary, $"  • {n}\n", Color.White, 10, false);

                    AppendColored(_rtxSummary, $"\n📝 요약\n{summary.Summary}", Color.LightGray, 10, false);
                }

                // 동종업계 (간이 - 섹터로 필터)
                _peerGrid.Rows.Clear();
                var sector = SectorMap.Resolve(symbol);
                var peers = SectorMap.Map.Where(x => x.Value.sector == sector && x.Key != symbol).Take(5);
                foreach (var p in peers)
                    _peerGrid.Rows.Add(p.Key, p.Value.name, "-", "-", "-", "-");

                _progress.Value = 100;
            }
            catch (Exception ex)
            {
                Log.Error("AI 분석 실패", ex);
                MessageBox.Show($"분석 실패: {ex.Message}", "오류");
            }
            finally
            {
                _btnAnalyze.Enabled = true;
                _progress.Visible = false;
            }
        }

        private void AppendColored(RichTextBox rtx, string text, Color color, float size, bool bold)
        {
            rtx.SelectionStart = rtx.TextLength;
            rtx.SelectionLength = 0;
            rtx.SelectionColor = color;
            rtx.SelectionFont = new Font("맑은 고딕", size, bold ? FontStyle.Bold : FontStyle.Regular);
            rtx.AppendText(text);
        }

        // Gemini JSON 강제 응답 모드
        private async Task<AiSummary?> CallGeminiJsonAsync(string symbol, Quote q, Dictionary<string, decimal> ind)
        {
            var apiKey = _db.GetSecret("Gemini.ApiKey");
            if (string.IsNullOrEmpty(apiKey))
            {
                return new AiSummary
                {
                    Recommendation = "관망",
                    Summary = "⚠️ Gemini API 키가 설정되지 않아 실제 분석을 수행하지 못했습니다. 런처 → 설정에서 키를 입력하세요.",
                    Positives = new() { "재무 안정성 양호", "업종 대표주" },
                    Negatives = new() { "고평가 우려", "외국인 매도세" }
                };
            }

            var model = _cbModel.SelectedItem?.ToString() ?? "gemini-2.0-flash";
            var prompt = $@"다음 종목을 한국 주식투자자 관점에서 분석하고 JSON으로만 응답하세요.
종목: {q.Name} ({symbol})
현재가: {Formatter.Money(q.Price, q.Currency)} ({Formatter.Pct(q.ChangeRate)})
지표: 성장성 {ind["성장성"]:F0}, 수익성 {ind["수익성"]:F0}, 안정성 {ind["안정성"]:F0}, 밸류 {ind["밸류에이션"]:F0}, 모멘텀 {ind["모멘텀"]:F0}

응답은 반드시 다음 JSON 스키마를 따르세요:
{{
  ""recommendation"": ""매수"" | ""관망"" | ""매도"",
  ""summary"": ""2-3문장 요약"",
  ""positives"": [""긍정요인 1"", ""긍정요인 2"", ""긍정요인 3""],
  ""negatives"": [""위험요인 1"", ""위험요인 2"", ""위험요인 3""]
}}";

            try
            {
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
                var body = new
                {
                    contents = new[] {
                        new { parts = new[] { new { text = prompt } } }
                    },
                    generationConfig = new
                    {
                        responseMimeType = "application/json",
                        temperature = 0.3
                    }
                };

                var res = await HttpPool.Shared.PostAsJsonAsync(url, body);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadFromJsonAsync<JsonElement>();
                var text = json.GetProperty("candidates")[0]
                    .GetProperty("content").GetProperty("parts")[0]
                    .GetProperty("text").GetString() ?? "{}";

                // JSON 응답 내부 파싱
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                return new AiSummary
                {
                    Recommendation = Safe.JsonStr(root, "recommendation"),
                    Summary = Safe.JsonStr(root, "summary"),
                    Positives = root.TryGetProperty("positives", out var ps)
                        ? ps.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : new List<string>(),
                    Negatives = root.TryGetProperty("negatives", out var ns)
                        ? ns.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                        : new List<string>()
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"[Gemini] 실패: {ex.Message}");
                return null;
            }
        }

        // 레이더 차트
        private void DrawRadar(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var rect = _radarPanel.ClientRectangle;

            g.DrawString("📊 기업 체력 레이더", GdiCache.Title, Brushes.White, 15, 10);
            if (_indicators.Count == 0) return;

            var cx = rect.Width / 2; var cy = rect.Height / 2 + 10;
            var radius = Math.Min(rect.Width, rect.Height) / 2 - 80;
            var n = _indicators.Count;
            var items = _indicators.ToArray();

            // 배경 격자
            using var gridPen = new Pen(Color.FromArgb(60, 65, 75));
            for (int lv = 1; lv <= 5; lv++)
            {
                var pts = new PointF[n];
                for (int i = 0; i < n; i++)
                {
                    var angle = -Math.PI / 2 + i * 2 * Math.PI / n;
                    var r = radius * lv / 5f;
                    pts[i] = new PointF(cx + (float)(Math.Cos(angle) * r), cy + (float)(Math.Sin(angle) * r));
                }
                g.DrawPolygon(gridPen, pts);
            }

            // 축
            for (int i = 0; i < n; i++)
            {
                var angle = -Math.PI / 2 + i * 2 * Math.PI / n;
                var x2 = cx + (float)(Math.Cos(angle) * radius);
                var y2 = cy + (float)(Math.Sin(angle) * radius);
                g.DrawLine(gridPen, cx, cy, x2, y2);
                var labelX = cx + (float)(Math.Cos(angle) * (radius + 25)) - 25;
                var labelY = cy + (float)(Math.Sin(angle) * (radius + 25)) - 8;
                g.DrawString(items[i].Key, GdiCache.NormalBold, Brushes.White, labelX, labelY);
            }

            // 값 폴리곤
            var valPts = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                var angle = -Math.PI / 2 + i * 2 * Math.PI / n;
                var r = (double)items[i].Value / 100 * radius;
                valPts[i] = new PointF(cx + (float)(Math.Cos(angle) * r), cy + (float)(Math.Sin(angle) * r));
            }
            using var fillBr = new SolidBrush(Color.FromArgb(100, 80, 180, 255));
            using var edgePen = new Pen(Color.FromArgb(80, 180, 255), 2);
            g.FillPolygon(fillBr, valPts);
            g.DrawPolygon(edgePen, valPts);

            foreach (var p in valPts)
                g.FillEllipse(Brushes.White, p.X - 4, p.Y - 4, 8, 8);
        }

        private void DrawScoreDetails(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            var rect = ((Panel)s!).ClientRectangle;
            if (_indicators.Count == 0) return;

            int y = 110;
            foreach (var kv in _indicators)
            {
                g.DrawString(kv.Key, GdiCache.MediumBold, Brushes.White, 30, y);
                var barW = (int)((double)kv.Value / 100 * 200);
                var col = kv.Value >= 70 ? Color.LightGreen : kv.Value >= 50 ? Color.Gold : Color.Salmon;
                g.FillRectangle(GdiCache.Brush(Color.FromArgb(50, 56, 70)), 150, y + 3, 200, 18);
                g.FillRectangle(GdiCache.Brush(col), 150, y + 3, barW, 18);
                g.DrawString($"{kv.Value:F1}", GdiCache.NormalBold, Brushes.White, 360, y);
                y += 30;
            }
        }

        private class AiSummary
        {
            public string Recommendation { get; set; } = "";
            public string Summary { get; set; } = "";
            public List<string> Positives { get; set; } = new();
            public List<string> Negatives { get; set; } = new();
        }
    }
}
