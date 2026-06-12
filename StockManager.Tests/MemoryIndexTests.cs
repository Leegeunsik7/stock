// ============================================================================
// MemoryIndexTests - 로컬 RAG BM25 검색기 토큰화/랭킹/경계 테스트 (DB 불필요·순수)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class MemoryIndexTests
    {
        private static MemoryDoc Doc(string id, string text, string symbol = "", int dayOffset = 0)
            => new() { Id = id, Text = text, Symbol = symbol, When = new DateTime(2025, 1, 1).AddDays(dayOffset), Kind = "테스트" };

        [Fact]
        public void Tokenize_AsciiLowercased_KoreanBigrams()
        {
            var toks = MemoryIndex.Tokenize("삼성전자 AAPL");
            Assert.Contains("삼성", toks);   // 한글 bigram
            Assert.Contains("성전", toks);
            Assert.Contains("전자", toks);
            Assert.Contains("aapl", toks);   // ASCII 소문자화
        }

        [Fact]
        public void Tokenize_DigitsKeptAsToken()
        {
            Assert.Contains("005930", MemoryIndex.Tokenize("종목 005930 매수"));
        }

        [Fact]
        public void Search_RanksRelevantDocHigher()
        {
            var idx = MemoryIndex.Build(new[]
            {
                Doc("a", "삼성전자 매수 시그널 발생"),
                Doc("b", "애플 배당 알림"),
                Doc("c", "카카오 급락 뉴스"),
            });
            var hits = idx.Search("삼성전자 매수");
            Assert.NotEmpty(hits);
            Assert.Equal("a", hits[0].Doc.Id);   // 질의어 포함 문서가 최상위
        }

        [Fact]
        public void Search_SymbolCodeMatches()
        {
            var idx = MemoryIndex.Build(new[]
            {
                Doc("a", "거래 매수", "005930"),
                Doc("b", "거래 매도", "000660"),
            });
            var hits = idx.Search("005930");
            Assert.Equal("a", hits[0].Doc.Id);
        }

        [Fact]
        public void Search_RareTerm_OutweighsCommon()
        {
            // "거래"는 흔함(낮은 idf), "헬리코박터"는 희귀(높은 idf) → 희귀어 포함 문서가 상위
            var docs = new List<MemoryDoc>();
            for (int i = 0; i < 10; i++) docs.Add(Doc($"common{i}", "거래 거래 거래", "", i));
            docs.Add(Doc("rare", "거래 헬리코박터", "", 99));
            var hits = MemoryIndex.Build(docs).Search("거래 헬리코박터");
            Assert.Equal("rare", hits[0].Doc.Id);
        }

        [Fact]
        public void Search_TieBreak_NewerFirst()
        {
            var idx = MemoryIndex.Build(new[]
            {
                Doc("old", "동일한 문장", "", 0),
                Doc("new", "동일한 문장", "", 30),
            });
            var hits = idx.Search("동일한 문장");
            Assert.Equal(2, hits.Count);
            Assert.Equal("new", hits[0].Doc.Id);   // 점수 동률 → 최신 우선
        }

        [Fact]
        public void EmptyIndex_OrEmptyQuery_NoThrow_Empty()
        {
            Assert.Empty(MemoryIndex.Build(Array.Empty<MemoryDoc>()).Search("아무거나"));
            Assert.Empty(MemoryIndex.Build(new[] { Doc("a", "내용") }).Search(""));
            Assert.Empty(MemoryIndex.Build(new[] { Doc("a", "내용") }).Search("매칭없는질의xyz"));
        }

        [Fact]
        public void TopK_LimitsResults()
        {
            var docs = Enumerable.Range(0, 20).Select(i => Doc($"d{i}", "공통 키워드", "", i)).ToList();
            Assert.Equal(5, MemoryIndex.Build(docs).Search("공통 키워드", topK: 5).Count);
        }

        [Fact]
        public void Deterministic_SameInput_SameOrder()
        {
            var docs = new[] { Doc("a", "삼성전자 매수"), Doc("b", "삼성 매도"), Doc("c", "애플 매수") };
            var h1 = MemoryIndex.Build(docs).Search("삼성 매수");
            var h2 = MemoryIndex.Build(docs).Search("삼성 매수");
            Assert.Equal(h1.Select(h => h.Doc.Id), h2.Select(h => h.Doc.Id));
        }
    }
}
