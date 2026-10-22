// ============================================================================
// SecEdgarFeeder — v12.18.64-DEV: 플루토 브레인 2단계 — SEC EDGAR 최신 공시 Atom 피드(getcurrent · 8-K) 폴링 피더(미국 · 콜드)
//
//   요청: GET {BaseUri}/cgi-bin/browse-edgar?action=getcurrent&type=8-K&owner=include&count=40&output=atom
//     헤더 User-Agent = 운영자 연락처(SEC 공정 접근 정책 필수 — 미설정 = 호스트가 피더를 만들지 않음) · gzip 수용.
//   응답(Atom): entry{title "8-K - APPLE INC (0000320193) (Filer)" · updated ISO-8601 · summary(HTML 이스케이프 · Item 목록) · id(액세션 urn)}.
//     XmlReader(DTD 금지 · 외부 리졸버 0 · 문서 문자 상한) 순차 판독 — 구조 위반 = Malformed.
//   항목: 헤드라인 = "8-K 회사명" + 고확신 Item 번역(1.03 → bankruptcy · 3.01 → delisting · 4.02 → restatement) · 본문 = summary 태그 제거 텍스트 ·
//     SymbolHint = CIK → 티커 표(대결 종목 5종) · 발생 시각 = updated(UTC) · 중복 키 = id.
//   ★Item 번역은 구조적 악재가 공시 항목 번호로 확정되는 3종만(나머지 항목 = 원문 그대로 · 키워드 없으면 분석기가 노이즈 처리 — 과대 해석 0).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Xml;

namespace StockManager.Engines.Cognitive.News
{
    public sealed class SecEdgarFeeder : PollingNewsFeeder
    {
        public static readonly Uri DefaultBaseUri = new("https://www.sec.gov/");
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(60);
        private const string AtomNs = "http://www.w3.org/2005/Atom";

        // CIK → 티커(아레나 대결 종목).
        private static readonly (long Cik, string Ticker)[] s_cik =
        {
            (320193, "AAPL"), (1045810, "NVDA"), (789019, "MSFT"), (1018724, "AMZN"), (1318605, "TSLA"),
        };

        // 8-K 항목 → 분석기 규칙 키워드(구조적 악재 확정 항목만).
        private static readonly (string Item, string Phrase)[] s_items =
        {
            ("Item 1.03", "bankruptcy"), ("Item 3.01", "delisting"), ("Item 4.02", "restatement"),
        };

        private readonly string _userAgent;
        private readonly Uri _baseUri;

        public SecEdgarFeeder(string userAgent, Func<PlutoNewsItem, bool> sink, IVirtualClock clock, HttpMessageHandler? handler = null,
            TimeSpan? pollInterval = null, TimeSpan? timeout = null, Uri? baseUri = null, int maxPayloadBytes = DefaultMaxPayloadBytes)
            : base("SEC EDGAR", PlutoSource.Sec, sink, clock, handler, pollInterval ?? DefaultInterval, timeout, maxPayloadBytes)
        {
            if (string.IsNullOrWhiteSpace(userAgent)) throw new ArgumentException("SEC User-Agent(연락처) 필요", nameof(userAgent));
            _userAgent = userAgent.Trim();
            _baseUri = baseUri ?? DefaultBaseUri;
        }

        protected override HttpRequestMessage BuildRequest(long nowUtcTicks)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, new Uri(_baseUri, "cgi-bin/browse-edgar?action=getcurrent&type=8-K&owner=include&count=40&output=atom"));
            req.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/atom+xml");
            return req;
        }

        protected override PlutoFeederFault Parse(ReadOnlySpan<byte> payload, long nowUtcTicks, List<ParsedItem> output)
        {
            try { return ParseAtom(payload, output); }
            catch (XmlException) { output.Clear(); return PlutoFeederFault.Malformed; }
            catch (FormatException) { output.Clear(); return PlutoFeederFault.Malformed; }
        }

        /// <summary>Atom 원문 → 항목. 시험 · 리플레이 공용.</summary>
        public static PlutoFeederFault ParseAtomForTest(ReadOnlySpan<byte> payload, List<PlutoNewsItem> output)
        {
            var tmp = new List<ParsedItem>();
            PlutoFeederFault f;
            try { f = ParseAtom(payload, tmp); }
            catch (XmlException) { return PlutoFeederFault.Malformed; }
            catch (FormatException) { return PlutoFeederFault.Malformed; }
            foreach (var p in tmp) output.Add(p.Item);
            return f;
        }

        /// <summary>CIK → 대결 종목 티커(표 밖 = null).</summary>
        public static string? TickerForCik(long cik)
        {
            foreach (var (c, t) in s_cik) if (c == cik) return t;
            return null;
        }

        private static PlutoFeederFault ParseAtom(ReadOnlySpan<byte> payload, List<ParsedItem> output)
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = DefaultMaxPayloadBytes * 2L,
                IgnoreComments = true,
                IgnoreWhitespace = true,
            };
            using var ms = new MemoryStream(payload.ToArray(), writable: false);
            using var x = XmlReader.Create(ms, settings);
            if (!x.ReadToFollowing("feed", AtomNs)) return PlutoFeederFault.Malformed;
            while (x.ReadToFollowing("entry", AtomNs))
            {
                string? title = null, updated = null, summary = null, id = null;
                using (var e = x.ReadSubtree())
                {
                    e.Read();                                   // <entry>
                    e.Read();                                   // 첫 자식
                    // ReadElementContentAsString · Skip 은 다음 형제로 이미 전진한다 — 그 경우 Read 를 다시 부르지 않는다(형제 누락 방지).
                    while (!e.EOF)
                    {
                        if (e.NodeType != XmlNodeType.Element || e.Depth != 1) { e.Read(); continue; }
                        if (e.NamespaceURI != AtomNs) { e.Skip(); continue; }
                        switch (e.LocalName)
                        {
                            case "title": title = e.ReadElementContentAsString(); break;
                            case "updated": updated = e.ReadElementContentAsString(); break;
                            case "summary": summary = e.ReadElementContentAsString(); break;
                            case "id": id = e.ReadElementContentAsString(); break;
                            default: e.Skip(); break;
                        }
                    }
                }
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(updated)) continue;
                if (!DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) continue;
                string body = StripTags(summary);
                string headline = Headline(title, body);
                string? hint = TryCik(title, out long cik) ? TickerForCik(cik) : null;
                output.Add(new ParsedItem(id.Trim(), new PlutoNewsItem(PlutoSource.Sec, headline, at.UtcTicks, body.Length == 0 ? null : body, hint)));
            }
            return PlutoFeederFault.None;
        }

        // "8-K - APPLE INC (0000320193) (Filer)" → "8-K APPLE INC" + 확정 항목 키워드.
        private static string Headline(string title, string body)
        {
            string t = title.Trim();
            int paren = t.IndexOf(" (", StringComparison.Ordinal);
            if (paren > 0) t = t[..paren];
            t = t.Replace(" - ", " ", StringComparison.Ordinal);
            var sb = new StringBuilder(t);
            foreach (var (item, phrase) in s_items)
                if (body.Contains(item, StringComparison.OrdinalIgnoreCase)) sb.Append(" — ").Append(phrase);
            return sb.ToString();
        }

        private static bool TryCik(string title, out long cik)
        {
            cik = 0;
            int open = title.IndexOf('(');
            while (open >= 0)
            {
                int close = title.IndexOf(')', open + 1);
                if (close < 0) return false;
                var inner = title.AsSpan(open + 1, close - open - 1);
                if (inner.Length == 10 && long.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out cik)) return cik > 0;
                open = title.IndexOf('(', close + 1);
            }
            return false;
        }

        private static string StripTags(string? html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var sb = new StringBuilder(html.Length);
            bool inTag = false;
            foreach (char c in html)
            {
                if (c == '<') { inTag = true; sb.Append(' '); continue; }
                if (c == '>') { inTag = false; continue; }
                if (!inTag) sb.Append(c);
            }
            return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
