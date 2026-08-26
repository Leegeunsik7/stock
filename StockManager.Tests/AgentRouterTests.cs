// ============================================================================
// AgentRouterTests - 에이전트 의도 라우터 검증 (순수·결정론·네트워크 0)
// ============================================================================
using Xunit;

namespace StockManager.Tests
{
    public class AgentRouterTests
    {
        [Theory]
        [InlineData("포트폴리오 위기 시뮬레이션", AgentTool.BlackSwan)]
        [InlineData("블랙스완 닥치면 어떻게 돼?", AgentTool.BlackSwan)]
        [InlineData("005930 미래 전망 알려줘", AgentTool.Future)]
        [InlineData("종합 건강검진 해줘", AgentTool.Health)]
        [InlineData("내 매매 회고 복기", AgentTool.Coach)]
        [InlineData("전략 토너먼트 돌려줘", AgentTool.Tournament)]
        [InlineData("리스크 기여 분해", AgentTool.Risk)]
        [InlineData("뭐 할 수 있어?", AgentTool.Help)]
        [InlineData("오늘 날씨 어때", AgentTool.Unknown)]
        public void Route_MapsToExpectedTool(string nl, AgentTool expected)
        {
            Assert.Equal(expected, AgentRouter.Route(nl).Tool);
        }

        [Fact]
        public void Route_StrategyPhrase_GoesToCompile_WithRawStrategy()
        {
            var r = AgentRouter.Route("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%");
            Assert.Equal(AgentTool.Compile, r.Tool);
            Assert.Contains("골든크로스", r.Strategy);
        }

        [Fact]
        public void Route_RsiBuyRule_GoesToCompile()
        {
            Assert.Equal(AgentTool.Compile, AgentRouter.Route("rsi 30 이하면 매수, 70 이상이면 매도").Tool);
        }

        [Fact]
        public void Route_TournamentBeatsStrategyKeyword()
        {
            // '전략' 단어가 있어도 토너먼트 의도면 토너먼트로
            Assert.Equal(AgentTool.Tournament, AgentRouter.Route("저장된 전략 토너먼트 랭킹 보여줘").Tool);
        }

        [Fact]
        public void Route_ExtractsKrxSymbol()
        {
            Assert.Equal("005930", AgentRouter.Route("005930 미래").Symbol);
        }

        [Fact]
        public void Route_ExtractsUsTicker_NotStopWord()
        {
            Assert.Equal("AAPL", AgentRouter.Route("AAPL 위기 시뮬").Symbol);
            // 'RSI' 같은 불용어는 종목으로 오인하지 않음
            Assert.Equal("", AgentRouter.Route("RSI 30 이하 매수 전략").Symbol);
        }

        [Fact]
        public void Route_Empty_Unknown()
        {
            Assert.Equal(AgentTool.Unknown, AgentRouter.Route("").Tool);
            Assert.Equal(AgentTool.Unknown, AgentRouter.Route("   ").Tool);
        }

        // ===== 적대적 리뷰 회귀 가드 =====

        [Fact]
        public void Route_LeadingFiller_DoesNotShadowTicker()
        {
            var r = AgentRouter.Route("the AAPL future");
            Assert.Equal("AAPL", r.Symbol);          // 선행 'the'가 진짜 티커를 가리지 않음
            Assert.Equal(AgentTool.Future, r.Tool);
        }

        [Fact]
        public void Route_LowercaseEnglish_NoBogusSymbol()
        {
            Assert.Equal("", AgentRouter.Route("buy gold now").Symbol);   // 소문자 영단어는 티커 아님
        }

        [Fact]
        public void Route_DollarTicker()
        {
            Assert.Equal("TSLA", AgentRouter.Route("$tsla 미래 전망").Symbol);
        }

        [Theory]
        [InlineData("콘텐츠 추천해줘")]    // bare "콘" 제거 → Future 오인 안 함
        [InlineData("아이콘 바꿔줘")]
        public void Route_KonSubstring_NotFuture(string nl)
        {
            Assert.NotEqual(AgentTool.Future, AgentRouter.Route(nl).Tool);
        }

        [Fact]
        public void Route_LeagueWord_NotTournament()
        {
            Assert.NotEqual(AgentTool.Tournament, AgentRouter.Route("리그 오브 레전드 얘기").Tool);   // bare "리그" 제거
        }

        [Fact]
        public void Route_JipjungSubstring_NotRisk()
        {
            Assert.NotEqual(AgentTool.Risk, AgentRouter.Route("결집중 분석").Tool);   // bare "집중" 제거
        }
    }
}
