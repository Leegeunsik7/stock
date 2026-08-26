// ============================================================================
// NewsService - 구글뉴스 RSS 기반 뉴스 수집 (Blazor/WinForms 공용) (v3.4)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace StockManager.Common
{
    public class NewsService
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

        public async Task<List<NewsArticle>> FetchAsync(string keyword = "")
        {
            var url = string.IsNullOrWhiteSpace(keyword)
                ? "https://news.google.com/rss/search?q=%ED%95%9C%EA%B5%AD+%EC%A6%9D%EC%8B%9C&hl=ko&gl=KR&ceid=KR:ko"
                : $"https://news.google.com/rss/search?q={Uri.EscapeDataString(keyword)}&hl=ko&gl=KR&ceid=KR:ko";
            var list = new List<NewsArticle>();
            try
            {
                var xml = await Http.GetStringAsync(url);
                var doc = XDocument.Parse(xml);
                foreach (var item in doc.Descendants("item").Take(50))
                {
                    var title = item.Element("title")?.Value ?? "";
                    var link = item.Element("link")?.Value ?? "";
                    var desc = item.Element("description")?.Value ?? "";
                    var pub = item.Element("pubDate")?.Value ?? "";
                    var src = item.Element("source")?.Value ?? "Google News";
                    list.Add(new NewsArticle
                    {
                        Title = CleanHtml(title),
                        Summary = CleanHtml(desc),
                        Url = link,
                        Source = string.IsNullOrWhiteSpace(src) ? "Google News" : src,
                        PublishedAt = DateTime.TryParse(pub, out var dt) ? dt.ToLocalTime() : DateTime.Now,
                        Language = "ko",
                        RelatedSymbols = ExtractSymbols(title + " " + desc)   // SectorMap 매칭 (원본 룰)
                    });
                }
            }
            catch { }
            return Deduplicate(list.OrderByDescending(n => n.PublishedAt).ToList());
        }

        /// <summary>본문에서 SectorMap 등록 종목(코드 또는 이름 포함) 추출 — 원본 NewsRadar.ExtractSymbols 룰.</summary>
        private static List<string> ExtractSymbols(string text)
        {
            var list = new List<string>();
            foreach (var k in SectorMap.Map.Keys)
                if (text.Contains(k) || text.Contains(SectorMap.NameOf(k)))
                    list.Add(k);
            return list.Distinct().ToList();
        }

        /// <summary>Gemini 감성분석 — 원본 NewsRadar.RunSentimentAnalysisAsync 룰 보존:
        ///  · 대상은 호출자가 선별(SentimentScore==0 센티널 · 회당 20건)
        ///  · JSON 강제 응답(responseMimeType) · 점수 [-1,1] 클램프 · 호출 간 500ms 레이트 리밋
        ///  · 점수 ≤ -0.7 그리고 관련종목 존재 → 부정 뉴스 알림(RaiseNews)</summary>
        public async Task<(bool ok, string msg, int analyzed)> AnalyzeSentimentAsync(
            LocalDatabase db, List<NewsArticle> targets, CancellationToken ct = default)
        {
            var apiKey = db.GetSecret("Gemini.ApiKey");
            if (string.IsNullOrEmpty(apiKey))
                return (false, "Gemini API 키가 설정되지 않았습니다. 설정에서 키를 입력하세요.", 0);
            if (targets.Count == 0)
                return (true, "이미 모든 뉴스에 감성 점수가 있습니다.", 0);

            int done = 0;
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
                    using var res = await Http.PostAsJsonAsync(url, body, ct);
                    res.EnsureSuccessStatusCode();
                    var json = await res.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                    var txt = json.GetProperty("candidates")[0].GetProperty("content")
                        .GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";

                    using var doc = JsonDocument.Parse(txt);
                    var score = Safe.JsonDec(doc.RootElement, "score");
                    n.SentimentScore = Math.Clamp(score, -1m, 1m);
                    done++;

                    if (n.SentimentScore <= -0.7m && n.RelatedSymbols.Count > 0)
                    {
                        AlertCenter.Instance.RaiseNews(
                            string.Join(",", n.RelatedSymbols),
                            $"⚠️ 부정 뉴스 · {string.Join(",", n.RelatedSymbols)}",
                            n.Title,
                            n.Url);
                    }

                    await Task.Delay(500, ct);   // 레이트 리밋 (원본)
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Log.Warn($"[Sentiment] 실패: {ex.Message}"); }
            }
            return (true, "✅ 감성분석 완료", done);
        }

        /// <summary>중복 제거 — 원본 NewsRadar 룰: URL 정확일치 + 정규화 제목 트라이그램 Jaccard > 0.8.
        /// URL 없는 기사는 제외(원본 동작). 최신순 정렬 후 호출되므로 최신 기사가 살아남는다.</summary>
        private static List<NewsArticle> Deduplicate(List<NewsArticle> list)
        {
            var unique = new List<NewsArticle>();
            foreach (var n in list)
            {
                if (string.IsNullOrEmpty(n.Url)) continue;
                if (unique.Any(x => x.Url == n.Url)) continue;
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

        private static string CleanHtml(string s) =>
            Regex.Replace(System.Net.WebUtility.HtmlDecode(s ?? ""), "<.*?>", " ").Replace("&nbsp;", " ").Trim();
    }
}
