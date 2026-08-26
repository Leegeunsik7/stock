// ============================================================================
// Program #4: News Radar (v2.2)
// [개선점]
//  · HttpPool.Shared 사용
//  · Gemini 감성분석 JSON 강제 응답 (정규식 파싱 실패 → 0 문제 해결)
//  · 제목 유사도 기반 중복 필터 (URL만으로 안 됨)
//  · Safe.Dec/Str 적용
//  · AlertCenter로 주요 뉴스 알림
//  · GdiCache
// ============================================================================
using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace StockManager.NewsRadar
{
    public class MainForm : Form
    {
        private readonly HttpClient _http = HttpPool.Shared;
        private readonly LocalDatabase _db;

        private TextBox _txtSearch = null!;
        private ComboBox _cbSource = null!;
        private ComboBox _cbSentiment = null!;
        private DataGridView _newsGrid = null!;
        private Panel _trendPanel = null!;
        private Label _lblStats = null!;
        private Button _btnRefresh = null!;
        private RichTextBox _rtxPreview = null!;

        private System.Windows.Forms.Timer _refreshTimer = null!;
        private CancellationTokenSource _cts = new();
        private List<NewsArticle> _news = new();

        public MainForm(LocalDatabase db)
        {
            _db = db;
            InitializeUI();
            Load += async (s, e) => await RefreshNewsAsync();
        }

        private void InitializeUI()
        {
            Text = "📰 뉴스 레이더 - MS2-E Stock Suite";
            Size = new Size(1600, 900);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(24, 28, 36);
            ForeColor = Color.White;
            Font = new Font("맑은 고딕", 9F);

            // 툴바
            var toolbar = new Panel
            {
                Dock = DockStyle.Top, Height = 60,
                BackColor = Color.FromArgb(35, 40, 55),
                Padding = new Padding(12, 12, 12, 12)
            };

            _txtSearch = new TextBox
            {
                Location = new Point(15, 16), Width = 220, Height = 30,
                Font = new Font("맑은 고딕", 11F),
                BackColor = Color.FromArgb(50, 56, 70),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                PlaceholderText = "키워드 또는 종목코드"
            };

            _cbSource = new ComboBox
            {
                Location = new Point(245, 16), Width = 130,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cbSource.Items.AddRange(new object[] { "전체", "Google News", "Yahoo Finance" });
            _cbSource.SelectedIndex = 0;

            _cbSentiment = new ComboBox
            {
                Location = new Point(385, 16), Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            _cbSentiment.Items.AddRange(new object[] { "전체", "긍정", "부정", "중립" });
            _cbSentiment.SelectedIndex = 0;

            // ⚠️ 수정(H2): 필터 변경 시 그리드 재렌더 (SelectedIndex 설정 후 배선해 초기 오발화 방지)
            _cbSource.SelectedIndexChanged += (s, e) => RenderNewsGrid();
            _cbSentiment.SelectedIndexChanged += (s, e) => RenderNewsGrid();

            _btnRefresh = new Button
            {
                Text = "🔄 새로고침",
                Location = new Point(505, 14), Size = new Size(100, 34),
                BackColor = Color.FromArgb(70, 130, 200),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            _btnRefresh.Click += async (s, e) => await RefreshNewsAsync();

            var btnAi = new Button
            {
                Text = "🧠 AI 감성분석",
                Location = new Point(615, 14), Size = new Size(130, 34),
                BackColor = Color.FromArgb(200, 130, 70),
                ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnAi.Click += async (s, e) => await RunSentimentAnalysisAsync();

            _lblStats = new Label
            {
                Location = new Point(760, 20), AutoSize = true,
                ForeColor = Color.LightGreen,
                Font = new Font("맑은 고딕", 10F)
            };

            toolbar.Controls.AddRange(new Control[] { _txtSearch, _cbSource, _cbSentiment, _btnRefresh, btnAi, _lblStats });

            // Split
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterWidth = 4
            };

            var topSplit = new SplitContainer
            {
                Dock = DockStyle.Fill, Orientation = Orientation.Vertical,
                SplitterWidth = 4
            };

            _newsGrid = BuildNewsGrid();
            topSplit.Panel1.Controls.Add(_newsGrid);

            _rtxPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White,
                BorderStyle = BorderStyle.None, ReadOnly = true,
                Font = new Font("맑은 고딕", 11F)
            };
            topSplit.Panel2.Controls.Add(_rtxPreview);
            topSplit.SplitterDistance = 1100;

            _trendPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(20, 24, 32) };
            _trendPanel.Paint += DrawTrend;

            split.Panel1.Controls.Add(topSplit);
            split.Panel2.Controls.Add(_trendPanel);
            split.SplitterDistance = 600;

            Controls.Add(split);
            Controls.Add(toolbar);

            // 5분마다 자동 새로고침
            _refreshTimer = new System.Windows.Forms.Timer { Interval = 5 * 60 * 1000 };
            _refreshTimer.Tick += async (s, e) => await RefreshNewsAsync();
            _refreshTimer.Start();

            FormClosing += (s, e) => { _cts.Cancel(); _refreshTimer.Stop(); };
        }

        private DataGridView BuildNewsGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 34, 44),
                ForeColor = Color.White,
                GridColor = Color.FromArgb(55, 60, 72),
                BorderStyle = BorderStyle.None,
                RowHeadersVisible = false, AllowUserToAddRows = false,
                ReadOnly = true,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                EnableHeadersVisualStyles = false,
                RowTemplate = { Height = 30 },
                Font = new Font("맑은 고딕", 10F)
            };
            g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(50, 56, 70), ForeColor = Color.White,
                Font = new Font("맑은 고딕", 10F, FontStyle.Bold)
            };
            g.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Color.FromArgb(30, 34, 44), ForeColor = Color.White,
                SelectionBackColor = Color.FromArgb(70, 120, 200)
            };
            foreach (var (k, v, w) in new[] {
                ("Time","시각", 10),("Source","출처", 8),("Title","제목", 50),
                ("Sentiment","감성", 8),("Symbol","관련종목", 12),("Url","링크", 12)})
            {
                g.Columns.Add(k, v);
                g.Columns[k]!.FillWeight = w;
            }

            g.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0) return;
                var col = g.Columns[e.ColumnIndex].Name;
                if (col == "Sentiment" && e.Value != null)
                {
                    var txt = e.Value.ToString() ?? "";
                    e.CellStyle!.ForeColor = txt.StartsWith("😊") ? Color.LightGreen
                        : txt.StartsWith("😟") ? Color.Salmon : Color.LightGray;
                    e.CellStyle.Font = GdiCache.MediumBold;
                }
            };
            g.CellClick += (s, e) =>
            {
                if (e.RowIndex < 0 || g.Rows[e.RowIndex].Tag is not NewsArticle n) return;
                _rtxPreview.Clear();
                _rtxPreview.SelectionFont = new Font("맑은 고딕", 13F, FontStyle.Bold);
                _rtxPreview.SelectionColor = Color.Gold;
                _rtxPreview.AppendText(n.Title + "\n\n");
                _rtxPreview.SelectionFont = new Font("맑은 고딕", 10F);
                _rtxPreview.SelectionColor = Color.LightGray;
                _rtxPreview.AppendText($"{n.Source} · {n.PublishedAt:yyyy-MM-dd HH:mm}\n\n");
                _rtxPreview.SelectionFont = new Font("맑은 고딕", 11F);
                _rtxPreview.SelectionColor = Color.White;
                _rtxPreview.AppendText(n.Summary);
                if (!string.IsNullOrEmpty(n.Url))
                {
                    _rtxPreview.SelectionColor = Color.LightBlue;
                    _rtxPreview.AppendText($"\n\n🔗 {n.Url}");
                }
            };
            g.CellDoubleClick += (s, e) =>
            {
                if (e.RowIndex < 0 || g.Rows[e.RowIndex].Tag is not NewsArticle n) return;
                var url = n.Url;
                if (!string.IsNullOrEmpty(url))
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                    catch { }
            };

            return g;
        }

        private async Task RefreshNewsAsync()
        {
            _btnRefresh.Enabled = false;
            try
            {
                var collected = new List<NewsArticle>();
                var keyword = _txtSearch.Text.Trim();

                collected.AddRange(await FetchNaverRssAsync(keyword));
                collected.AddRange(await FetchYahooRssAsync(keyword));

                // 중복 제거 (URL + 제목 fuzzy)
                var unique = DeduplicateNews(collected);

                _news = unique.OrderByDescending(n => n.PublishedAt).Take(200).ToList();

                RenderNewsGrid();
            }
            catch (Exception ex)
            {
                Log.Error("News Refresh 실패", ex);
                _lblStats.Text = "⚠️ 로드 실패";
            }
            finally { _btnRefresh.Enabled = true; }
        }

        /// <summary>URL 중복 + 제목 80% 이상 유사 시 중복 처리</summary>
        private List<NewsArticle> DeduplicateNews(List<NewsArticle> list)
        {
            var unique = new List<NewsArticle>();
            foreach (var n in list)
            {
                if (string.IsNullOrEmpty(n.Url)) continue;
                if (unique.Any(x => x.Url == n.Url)) continue;
                // 제목 fuzzy
                var title = Normalize(n.Title);
                if (unique.Any(x => Normalize(x.Title) == title ||
                    JaccardSimilarity(Normalize(x.Title), title) > 0.8))
                    continue;
                unique.Add(n);
            }
            return unique;
        }

        private static string Normalize(string s) =>
            Regex.Replace(s.ToLowerInvariant(), @"[^\w가-힣]", "");

        private static double JaccardSimilarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return 0;
            var setA = new HashSet<string>();
            var setB = new HashSet<string>();
            for (int i = 0; i < a.Length - 2; i++) setA.Add(a.Substring(i, 3));
            for (int i = 0; i < b.Length - 2; i++) setB.Add(b.Substring(i, 3));
            if (setA.Count == 0 || setB.Count == 0) return 0;
            var inter = setA.Intersect(setB).Count();
            var union = setA.Union(setB).Count();
            return (double)inter / union;
        }

        private async Task<List<NewsArticle>> FetchNaverRssAsync(string keyword)
        {
            var list = new List<NewsArticle>();
            var url = string.IsNullOrEmpty(keyword)
                ? "https://news.google.com/rss/search?q=%ED%95%9C%EA%B5%AD+%EC%A6%9D%EC%8B%9C&hl=ko&gl=KR"
                : $"https://news.google.com/rss/search?q={Uri.EscapeDataString(keyword)}&hl=ko&gl=KR";
            try
            {
                var xml = await _http.GetStringAsync(url, _cts.Token);
                var doc = XDocument.Parse(xml);
                foreach (var item in doc.Descendants("item").Take(50))
                {
                    var title = item.Element("title")?.Value ?? "";
                    var link = item.Element("link")?.Value ?? "";
                    var desc = item.Element("description")?.Value ?? "";
                    var pub = item.Element("pubDate")?.Value ?? "";
                    list.Add(new NewsArticle
                    {
                        Title = CleanHtml(title), Summary = CleanHtml(desc),
                        Url = link, Source = "Google News",
                        PublishedAt = DateTime.TryParse(pub, out var dt) ? dt : DateTime.Now,
                        Language = "ko",
                        RelatedSymbols = ExtractSymbols(title + " " + desc)
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"[News] Naver 실패: {ex.Message}"); }
            return list;
        }

        private async Task<List<NewsArticle>> FetchYahooRssAsync(string keyword)
        {
            var list = new List<NewsArticle>();
            var url = string.IsNullOrEmpty(keyword)
                ? "https://finance.yahoo.com/news/rssindex"
                : $"https://news.google.com/rss/search?q={Uri.EscapeDataString(keyword)}+stock&hl=en&gl=US";
            try
            {
                var xml = await _http.GetStringAsync(url, _cts.Token);
                var doc = XDocument.Parse(xml);
                foreach (var item in doc.Descendants("item").Take(50))
                {
                    var title = item.Element("title")?.Value ?? "";
                    var link = item.Element("link")?.Value ?? "";
                    var desc = item.Element("description")?.Value ?? "";
                    var pub = item.Element("pubDate")?.Value ?? "";
                    list.Add(new NewsArticle
                    {
                        Title = CleanHtml(title), Summary = CleanHtml(desc),
                        Url = link, Source = "Yahoo Finance",
                        PublishedAt = DateTime.TryParse(pub, out var dt) ? dt : DateTime.Now,
                        Language = "en",
                        RelatedSymbols = ExtractSymbols(title + " " + desc)
                    });
                }
            }
            catch (Exception ex) { Log.Warn($"[News] Yahoo 실패: {ex.Message}"); }
            return list;
        }

        private static string CleanHtml(string s) =>
            Regex.Replace(s, "<[^>]+>", "").Replace("&amp;", "&").Replace("&quot;", "\"").Trim();

        private static List<string> ExtractSymbols(string text)
        {
            var list = new List<string>();
            foreach (var k in SectorMap.Map.Keys)
                if (text.Contains(k) || text.Contains(SectorMap.NameOf(k)))
                    list.Add(k);
            return list.Distinct().ToList();
        }

        // Gemini JSON 강제 응답 감성분석
        private async Task RunSentimentAnalysisAsync()
        {
            var apiKey = _db.GetSecret("Gemini.ApiKey");
            if (string.IsNullOrEmpty(apiKey))
            {
                MessageBox.Show("Gemini API 키가 설정되지 않았습니다.\n런처 → 설정에서 키를 입력하세요.");
                return;
            }

            var targets = _news.Where(n => n.SentimentScore == 0).Take(20).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("이미 모든 뉴스에 감성 점수가 있습니다.");
                return;
            }

            _lblStats.Text = $"🧠 {targets.Count}건 감성분석 중...";
            foreach (var n in targets)
            {
                try
                {
                    var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";
                    var body = new
                    {
                        contents = new[] {
                            new { parts = new[] { new { text =
                                $"다음 뉴스의 주식시장 관점 감성 점수(-1.0~+1.0)를 JSON으로만 응답하세요.\n제목: {n.Title}\n\n스키마: {{\"score\": 숫자, \"reason\": \"한 줄 설명\"}}"
                            } } }
                        },
                        generationConfig = new { responseMimeType = "application/json", temperature = 0.1 }
                    };
                    var res = await HttpPool.Shared.PostAsJsonAsync(url, body, _cts.Token);
                    res.EnsureSuccessStatusCode();
                    var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: _cts.Token);
                    var txt = json.GetProperty("candidates")[0].GetProperty("content")
                        .GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";

                    using var doc = JsonDocument.Parse(txt);
                    var score = Safe.JsonDec(doc.RootElement, "score");
                    n.SentimentScore = Math.Clamp(score, -1m, 1m);

                    // 강한 부정 뉴스는 알림
                    if (n.SentimentScore <= -0.7m && n.RelatedSymbols.Count > 0)
                    {
                        AlertCenter.Instance.RaiseNews(
                            string.Join(",", n.RelatedSymbols),
                            $"⚠️ 부정 뉴스 · {string.Join(",", n.RelatedSymbols)}",
                            n.Title,
                            n.Url);
                    }

                    await Task.Delay(500, _cts.Token); // 레이트 리밋
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"[Sentiment] 실패: {ex.Message}"); }
            }

            // 새로 갱신 (현재 필터 유지)
            RenderNewsGrid();
            _lblStats.Text = "✅ 감성분석 완료";
        }

        /// <summary>현재 출처·감성·키워드 필터를 적용해 그리드 렌더 (행 Tag에 기사 매핑)</summary>
        private void RenderNewsGrid()
        {
            if (_newsGrid == null) return;
            _newsGrid.Rows.Clear();

            var src = _cbSource?.SelectedItem?.ToString() ?? "전체";
            var sent = _cbSentiment?.SelectedItem?.ToString() ?? "전체";
            var kw = _txtSearch?.Text.Trim() ?? "";

            IEnumerable<NewsArticle> view = _news;

            if (src != "전체")
                view = view.Where(n => n.Source.Contains(src, StringComparison.OrdinalIgnoreCase));

            view = sent switch
            {
                "긍정" => view.Where(n => n.SentimentScore > 0.3m),
                "부정" => view.Where(n => n.SentimentScore < -0.3m),
                "중립" => view.Where(n => n.SentimentScore >= -0.3m && n.SentimentScore <= 0.3m),
                _ => view
            };

            if (!string.IsNullOrEmpty(kw))
                view = view.Where(n =>
                    n.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    n.Summary.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                    n.RelatedSymbols.Any(rs => rs.Contains(kw, StringComparison.OrdinalIgnoreCase)));

            int shown = 0, pos = 0, neg = 0;
            foreach (var n in view)
            {
                var s = n.SentimentScore > 0.3m ? "😊 긍정"
                      : n.SentimentScore < -0.3m ? "😟 부정" : "😐 중립";
                var idx = _newsGrid.Rows.Add(n.PublishedAt.ToString("MM-dd HH:mm"),
                    n.Source, n.Title, s, string.Join(",", n.RelatedSymbols), n.Url);
                _newsGrid.Rows[idx].Tag = n;   // ⚠️ 행↔기사 매핑(필터링 후 인덱스 어긋남 방지)
                shown++;
                if (n.SentimentScore > 0.3m) pos++;
                else if (n.SentimentScore < -0.3m) neg++;
            }

            _lblStats.Text = $"📊 표시 {shown}건 (수집 {_news.Count}) · 긍정 {pos} · 부정 {neg}";
            _trendPanel.Invalidate();
        }

        private void DrawTrend(object? s, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            g.DrawString("📈 시간대별 감성 추세", GdiCache.Title, Brushes.White, 15, 15);
            if (_news.Count == 0) return;

            var rect = _trendPanel.ClientRectangle;
            int padL = 60, padR = 30, padT = 50, padB = 30;
            var plotW = rect.Width - padL - padR;
            var plotH = rect.Height - padT - padB;

            // 시간대별 평균
            var groups = _news.GroupBy(n => new DateTime(n.PublishedAt.Year, n.PublishedAt.Month, n.PublishedAt.Day, n.PublishedAt.Hour, 0, 0))
                .OrderBy(g => g.Key).ToList();
            if (groups.Count < 2) return;

            var cellW = (float)plotW / groups.Count;
            var zeroY = padT + plotH / 2f;
            g.DrawLine(GdiCache.Pen(Color.FromArgb(120, 200, 200, 200)), padL, zeroY, padL + plotW, zeroY);
            g.DrawString("0", GdiCache.Small, Brushes.LightGray, padL - 20, zeroY - 8);
            g.DrawString("+1", GdiCache.Small, Brushes.LightGreen, padL - 24, padT);
            g.DrawString("-1", GdiCache.Small, Brushes.Salmon, padL - 24, padT + plotH - 10);

            Point? prev = null;
            var pen = GdiCache.Pen(Color.Cyan, 2);
            for (int i = 0; i < groups.Count; i++)
            {
                var avg = groups[i].Average(n => (double)n.SentimentScore);
                var x = padL + i * cellW + cellW / 2;
                var y = padT + (float)((1 - avg) / 2 * plotH);
                var cur = new Point((int)x, (int)y);
                if (prev.HasValue) g.DrawLine(pen, prev.Value, cur);
                var col = avg > 0.3 ? Color.LightGreen : avg < -0.3 ? Color.Salmon : Color.Gold;
                g.FillEllipse(GdiCache.Brush(col), cur.X - 4, cur.Y - 4, 8, 8);
                prev = cur;
            }

            // x축 시간 라벨 (3개만)
            for (int i = 0; i < groups.Count; i += Math.Max(1, groups.Count / 4))
            {
                var x = padL + i * cellW + cellW / 2;
                g.DrawString(groups[i].Key.ToString("MM-dd HH:mm"), GdiCache.Small, Brushes.LightGray, x - 30, padT + plotH + 5);
            }
        }
    }
}
