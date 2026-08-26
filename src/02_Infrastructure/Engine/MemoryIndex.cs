// ============================================================================
// MemoryIndex - 로컬 RAG "투자 두뇌": 무의존 BM25 검색기 🅗 (v3.18.0)
//  로컬 DB의 내 과거 기록(거래·시그널·알림·뉴스)을 의미 검색해, AI 비서가
//  "지난번 비슷한 국면에서 나는 어땠는지"를 근거로 답하게 한다.
//  외부 패키지 0("Primes 패턴"). 형태소 분석기 없이 한국어를 다루기 위해
//  CJK는 문자 bigram, ASCII/숫자는 단어 토큰으로 색인(검색업계 표준 트릭).
//
//  설계 노트 / 숨은 룰:
//   · BM25 (k1=1.5, b=0.75), idf = log(1 + (N-n+0.5)/(n+0.5)) → 항상 양수(BM25+ 변형)
//   · 토큰: ASCII 영숫자 런(소문자화) + CJK 문자 bigram(런 길이 1이면 단일문자)
//   · Symbol도 본문에 합쳐 색인 → "005930"·"AAPL" 코드 검색 가능
//   · 점수 동률 시 최신(When) 우선. 빈 색인/빈 질의 → 빈 결과(예외 없음)
//   · 결정적: 동일 입력 → 동일 순위 (난수 없음)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace StockManager.Common
{
    public class MemoryDoc
    {
        public string Id { get; set; } = "";
        public DateTime When { get; set; }
        public string Kind { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public class MemoryHit
    {
        public MemoryDoc Doc { get; set; } = new();
        public double Score { get; set; }
    }

    public class MemoryIndex
    {
        private const double K1 = 1.5, B = 0.75;
        private readonly List<MemoryDoc> _docs;
        private readonly List<Dictionary<string, int>> _tf;   // 문서별 단어빈도
        private readonly Dictionary<string, int> _df;          // 단어별 문서빈도
        private readonly double[] _len;                        // 문서 길이(토큰 수)
        private readonly double _avgLen;

        public int Count => _docs.Count;

        private MemoryIndex(List<MemoryDoc> docs, List<Dictionary<string, int>> tf,
            Dictionary<string, int> df, double[] len, double avgLen)
        {
            _docs = docs; _tf = tf; _df = df; _len = len; _avgLen = avgLen;
        }

        public static MemoryIndex Build(IEnumerable<MemoryDoc> docs)
        {
            var list = docs?.Where(d => d != null).ToList() ?? new List<MemoryDoc>();
            var tf = new List<Dictionary<string, int>>(list.Count);
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            var len = new double[list.Count];

            for (int i = 0; i < list.Count; i++)
            {
                var toks = Tokenize(list[i].Text + " " + list[i].Symbol);
                var d = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var t in toks) d[t] = d.TryGetValue(t, out var c) ? c + 1 : 1;
                tf.Add(d);
                len[i] = toks.Count;
                foreach (var k in d.Keys) df[k] = df.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            double avg = list.Count > 0 ? len.Average() : 0;
            return new MemoryIndex(list, tf, df, len, avg);
        }

        public List<MemoryHit> Search(string query, int topK = 8)
        {
            if (_docs.Count == 0) return new();
            var q = Tokenize(query).Distinct().ToList();
            if (q.Count == 0) return new();
            int n = _docs.Count;
            double avg = _avgLen <= 0 ? 1 : _avgLen;

            var scored = new List<MemoryHit>();
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                foreach (var term in q)
                {
                    if (!_tf[i].TryGetValue(term, out var f) || f == 0) continue;
                    int dfc = _df.TryGetValue(term, out var c) ? c : 0;
                    double idf = Math.Log(1 + (n - dfc + 0.5) / (dfc + 0.5));
                    double denom = f + K1 * (1 - B + B * (_len[i] / avg));
                    s += idf * (f * (K1 + 1)) / denom;
                }
                if (s > 0) scored.Add(new MemoryHit { Doc = _docs[i], Score = s });
            }
            return scored.OrderByDescending(h => h.Score)
                         .ThenByDescending(h => h.Doc.When)
                         .Take(Math.Max(1, topK)).ToList();
        }

        /// <summary>ASCII 영숫자 런(소문자) + CJK 문자 bigram으로 토큰화.</summary>
        public static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(text)) return tokens;
            var sb = new StringBuilder();
            var cjk = new List<char>();

            void FlushAscii() { if (sb.Length > 0) { tokens.Add(sb.ToString()); sb.Clear(); } }
            void FlushCjk()
            {
                if (cjk.Count == 1) tokens.Add(cjk[0].ToString());
                for (int i = 0; i + 1 < cjk.Count; i++) tokens.Add(new string(new[] { cjk[i], cjk[i + 1] }));
                cjk.Clear();
            }

            foreach (var ch in text)
            {
                if (IsCjk(ch)) { FlushAscii(); cjk.Add(ch); }
                else if (char.IsLetterOrDigit(ch)) { FlushCjk(); sb.Append(char.ToLowerInvariant(ch)); }
                else { FlushAscii(); FlushCjk(); }
            }
            FlushAscii(); FlushCjk();
            return tokens;
        }

        private static bool IsCjk(char c) =>
            (c >= 0xAC00 && c <= 0xD7A3) ||   // 한글 음절
            (c >= 0x1100 && c <= 0x11FF) ||   // 한글 자모
            (c >= 0x3130 && c <= 0x318F) ||   // 한글 호환 자모
            (c >= 0x4E00 && c <= 0x9FFF) ||   // CJK 통합 한자
            (c >= 0x3040 && c <= 0x30FF);     // 히라가나/가타카나
    }
}
