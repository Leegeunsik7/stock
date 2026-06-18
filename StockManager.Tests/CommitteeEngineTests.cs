// ============================================================================
// CommitteeEngineTests — FactSheet 조립·포맷·페르소나 프롬프트 생성 검증
//  · 외부 의존 0, 결정론 합성 AlphaReport, LLM 호출 없음
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class CommitteeEngineTests
    {
        // ── 헬퍼 ──────────────────────────────────────────────────────────────
        private static AlphaReport MakeAlpha(string sym) =>
            new(true, sym, "SPY", 120, 1.2, 78.5, 18.4, 15.3, 3.1, 3.2, 0.75,
                Array.Empty<AlphaPoint>(), "test");

        private static TailBudgetResult MakeTail() =>
            new(true,
                Array.Empty<TailAllocation>(),
                Array.Empty<TailScenario>(),
                -35.0, -24.5, "COVID-19", "test");

        private static BacktestSummary MakeBacktest() =>
            new() { TotalReturn = 42.5m, MaxDrawdown = 12.3m, WinRate = 58.0m, TradeCount = 30, Sharpe = 1.4m };

        // ─────────────────────────────────────────────────────────────────────
        [Fact]
        public void BuildFactSheet_AlphaOnly_NullOptionals()
        {
            var alpha = MakeAlpha("AAPL");
            var fs = CommitteeEngine.BuildFactSheet("AAPL", alpha, null, null);
            Assert.Equal("AAPL", fs.Symbol);
            Assert.Same(alpha, fs.Alpha);
            Assert.Null(fs.TailRisk);
            Assert.Null(fs.Backtest);
        }

        [Fact]
        public void BuildFactSheet_AllSources_RecordsRetained()
        {
            var alpha = MakeAlpha("005930");
            var tail  = MakeTail();
            var back  = MakeBacktest();
            var fs = CommitteeEngine.BuildFactSheet("005930", alpha, tail, back, "MA(5,20)");
            Assert.Same(alpha, fs.Alpha);
            Assert.Same(tail,  fs.TailRisk);
            Assert.Same(back,  fs.Backtest);
            Assert.Equal("MA(5,20)", fs.BacktestLabel);
        }

        [Fact]
        public void FormatFactSheet_ContainsSymbolAndKeyMetrics()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, null);
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("AAPL", text);
            Assert.Contains("알파", text);
            Assert.Contains("베타", text);
            Assert.Contains("IR",   text);
        }

        [Fact]
        public void FormatFactSheet_NoTailRisk_ShowsMissingNote()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, null);
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("비중 데이터 없음", text);
        }

        [Fact]
        public void FormatFactSheet_WithTailRisk_ContainsCrisisAndMdd()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), MakeTail(), null);
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("COVID-19", text);
            Assert.Contains("MDD",      text);
        }

        [Fact]
        public void FormatFactSheet_NoBacktest_ShowsMissingNote()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, null);
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("카트리지 데이터 없음", text);
        }

        [Fact]
        public void FormatFactSheet_WithBacktest_ContainsSharpe()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, MakeBacktest(), "MA(5,20)");
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("MA(5,20)", text);
            Assert.Contains("샤프",     text);
        }

        [Fact]
        public void BullPrompt_ContainsRoleAndFactData()
        {
            var fs     = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, null);
            var prompt = CommitteeEngine.BullPrompt(fs);
            Assert.Contains("Bull",  prompt);
            Assert.Contains("AAPL",  prompt);
            Assert.NotEmpty(prompt);
        }

        [Fact]
        public void BearPrompt_ContainsRoleAndFactData()
        {
            var fs     = CommitteeEngine.BuildFactSheet("TSLA", MakeAlpha("TSLA"), null, null);
            var prompt = CommitteeEngine.BearPrompt(fs);
            Assert.Contains("Bear", prompt);
            Assert.Contains("TSLA", prompt);
        }

        [Fact]
        public void CroPrompt_IncludesBullAndBearArguments()
        {
            var fs     = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, null);
            var prompt = CommitteeEngine.CroPrompt(fs, "Bull이 말하길 X", "Bear이 말하길 Y");
            Assert.Contains("Bull이 말하길 X", prompt);
            Assert.Contains("Bear이 말하길 Y", prompt);
            Assert.Contains("CRO",             prompt);
        }

        [Fact]
        public void Prompts_PureCSharp_NoAsyncOrSideEffect()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, null);
            var bull = CommitteeEngine.BullPrompt(fs);
            var bear = CommitteeEngine.BearPrompt(fs);
            var cro  = CommitteeEngine.CroPrompt(fs, bull, bear);
            Assert.NotEmpty(bull);
            Assert.NotEmpty(bear);
            Assert.NotEmpty(cro);
        }

        [Fact]
        public void FormatFactSheet_Deterministic_SameOutput()
        {
            var fs = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), MakeTail(), null);
            var t1 = CommitteeEngine.FormatFactSheet(fs);
            var t2 = CommitteeEngine.FormatFactSheet(fs);
            Assert.Equal(t1, t2);
        }

        // C8-a: TailRisk.IsValid=false → else 분기 "비중 데이터 없음" 경로 검증
        [Fact]
        public void FormatFactSheet_InvalidTailRisk_ShowsMissingNote()
        {
            var invalidTail = new TailBudgetResult(false,
                Array.Empty<TailAllocation>(), Array.Empty<TailScenario>(),
                0, 0, "", "invalid");
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), invalidTail, null);
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("비중 데이터 없음", text);
        }

        // C8-b: 빈 BacktestLabel → "전략" 폴백 검증
        [Fact]
        public void FormatFactSheet_EmptyBacktestLabel_ShowsFallback()
        {
            var fs   = CommitteeEngine.BuildFactSheet("AAPL", MakeAlpha("AAPL"), null, MakeBacktest(), "");
            var text = CommitteeEngine.FormatFactSheet(fs);
            Assert.Contains("전략", text);
            Assert.Contains("샤프", text);
        }
    }
}
