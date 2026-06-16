// ============================================================================
// StrategyCompilerTests - LLM 전략 컴파일러 순수 코어 검증 (결정론·오프라인·네트워크 0)
//  NL 파서·JSON 검증·백테스트 결정론·정준 안정성·MA크로스 환원·검증 경계.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class StrategyCompilerTests
    {
        // 결정론 합성 캔들(난수 시드 없이 사인파+드리프트) — 테스트 재현용
        private static List<Candle> Synth(int n = 260)
        {
            var list = new List<Candle>(n);
            var d = new DateTime(2022, 1, 1);
            for (int i = 0; i < n; i++)
            {
                double p = 100 + i * 0.15 + 12 * Math.Sin(i / 9.0) + 5 * Math.Sin(i / 3.0);
                decimal c = (decimal)Math.Round(p, 2);
                list.Add(new Candle { Date = d.AddDays(i), Open = c, High = c * 1.01m, Low = c * 0.99m, Close = c, Volume = 1000 + i });
            }
            return list;
        }

        [Fact]
        public void ParseNl_GoldenDeadCross_WithStop()
        {
            var r = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%");
            Assert.True(r.IsValid);
            var s = r.Spec!;
            Assert.Single(s.Entry);
            Assert.Equal(SpecOp.CrossAbove, s.Entry[0].Op);
            Assert.Equal(SpecKind.Sma, s.Entry[0].Left.Kind);
            Assert.Equal(5, s.Entry[0].Left.Param);
            Assert.Equal(20, s.Entry[0].Right!.Param);
            Assert.Single(s.Exit);
            Assert.Equal(SpecOp.CrossBelow, s.Exit[0].Op);
            Assert.Equal(8, s.StopLossPct, 3);
        }

        [Fact]
        public void ParseNl_Rsi_Thresholds()
        {
            var r = StrategyParser.ParseNl("RSI 30 이하면 매수, 70 이상이면 매도");
            Assert.True(r.IsValid);
            var s = r.Spec!;
            Assert.Contains(s.Entry, c => c.Left.Kind == SpecKind.Rsi && c.Op == SpecOp.Lt && Math.Abs(c.Const - 30) < 1e-9);
            Assert.Contains(s.Exit, c => c.Left.Kind == SpecKind.Rsi && c.Op == SpecOp.Gt && Math.Abs(c.Const - 70) < 1e-9);
        }

        [Fact]
        public void ParseNl_Unrecognized_FailsWithHint()
        {
            var r = StrategyParser.ParseNl("아무말이나 적어봅니다 전략 아님");
            Assert.False(r.IsValid);
            Assert.Contains("인식", r.Note);
        }

        [Fact]
        public void Validate_RejectsNoExitNoStop()
        {
            var entry = new List<SpecCond> { new(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, 20)) };
            var spec = new StrategySpec("x", "x", entry, SpecLogic.And, new List<SpecCond>(), SpecLogic.Or, 0, 0);
            var (ok, err) = StrategyCompiler.Validate(spec);
            Assert.False(ok);
            Assert.Contains("청산", err);
        }

        [Fact]
        public void Validate_RejectsBadPeriod()
        {
            var entry = new List<SpecCond> { new(new SpecTerm(SpecKind.Sma, 1), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, 20)) };
            var spec = new StrategySpec("x", "x", entry, SpecLogic.And, entry, SpecLogic.Or, 0, 0);
            Assert.False(StrategyCompiler.Validate(spec).ok);
        }

        [Fact]
        public void Backtest_Deterministic_AndShapes()
        {
            var spec = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도").Spec!;
            var candles = Synth();
            var a = StrategyCompiler.Backtest(spec, candles, Market.KRX_KOSPI);
            var b = StrategyCompiler.Backtest(spec, candles, Market.KRX_KOSPI);
            Assert.Equal(a.TotalReturn, b.TotalReturn);                 // 결정론
            Assert.Equal(a.TradeCount, b.TradeCount);
            Assert.True(a.MaxDrawdown >= 0);
            Assert.InRange(a.WinRate, 0, 100);
            Assert.True(a.EquityCurve.Count > 0);
        }

        [Fact]
        public void Backtest_TooFewCandles_EmptySummary()
        {
            var spec = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도").Spec!;
            var bt = StrategyCompiler.Backtest(spec, Synth(3), Market.KRX_KOSPI);
            Assert.Equal(0, bt.TradeCount);
            Assert.Empty(bt.EquityCurve);
        }

        [Fact]
        public void Canonical_StableAndSameId()
        {
            var r1 = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%");
            var r2 = StrategyParser.ParseNl("5/20 골든크로스 매수 데드크로스 매도 손절 8%");
            Assert.Equal(StrategyCompiler.Canonical(r1.Spec!), StrategyCompiler.Canonical(r2.Spec!));
            Assert.Equal(r1.Spec!.Id, r2.Spec!.Id);                     // 동일 스펙 동일 id(FNV)
        }

        [Fact]
        public void ReducesToMaCross_DetectsAndRejects()
        {
            var ma = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도").Spec!;
            Assert.True(StrategyCompiler.ReducesToMaCross(ma, out var sp, out var lp));
            Assert.Equal(5, sp); Assert.Equal(20, lp);

            var withStop = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%").Spec!;
            Assert.False(StrategyCompiler.ReducesToMaCross(withStop, out _, out _));   // 손절 있으면 환원 불가

            var rsi = StrategyParser.ParseNl("RSI 30 이하면 매수, 70 이상이면 매도").Spec!;
            Assert.False(StrategyCompiler.ReducesToMaCross(rsi, out _, out _));
        }

        [Fact]
        public void ParseJson_ValidSpec()
        {
            string json = @"{ ""title"":""t"", ""entryLogic"":""and"",
                ""entry"":[{""left"":{""kind"":""sma"",""param"":5},""op"":""crossAbove"",""right"":{""kind"":""sma"",""param"":20}}],
                ""exitLogic"":""or"",
                ""exit"":[{""left"":{""kind"":""rsi"",""param"":14},""op"":""gt"",""const"":70}],
                ""stopLossPct"":5, ""takeProfitPct"":0 }";
            var r = StrategyParser.ParseJson(json);
            Assert.True(r.IsValid);
            Assert.Equal(SpecKind.Sma, r.Spec!.Entry[0].Left.Kind);
            Assert.Equal(70, r.Spec.Exit[0].Const, 3);
            Assert.Equal(5, r.Spec.StopLossPct, 3);
        }

        [Fact]
        public void ParseJson_StripsFences_AndRejectsInvalid()
        {
            // 코드블록 펜스 제거
            var fenced = StrategyParser.ParseJson("```json\n{ \"entry\":[{\"left\":{\"kind\":\"price\"},\"op\":\"crossAbove\",\"right\":{\"kind\":\"sma\",\"param\":20}}], \"exit\":[{\"left\":{\"kind\":\"price\"},\"op\":\"crossBelow\",\"right\":{\"kind\":\"sma\",\"param\":20}}] }\n```");
            Assert.True(fenced.IsValid);

            // 진입 없음 → 검증 실패
            var bad = StrategyParser.ParseJson(@"{ ""entry"":[], ""exit"":[] }");
            Assert.False(bad.IsValid);

            // 깨진 JSON → 실패(예외 아님)
            var broken = StrategyParser.ParseJson("not json at all");
            Assert.False(broken.IsValid);
        }

        // ===== 적대적 리뷰 회귀 가드 =====

        [Fact]
        public void ParseNl_DeadCrossOnly_DoesNotFabricateEntry()
        {
            var r = StrategyParser.ParseNl("20일선 데드크로스하면 매도");   // 매도만 → 진입 날조 금지
            Assert.False(r.IsValid);
            Assert.Contains("인식", r.Note);
        }

        [Fact]
        public void ParseNl_GoldenOnly_IsComplete()
        {
            var r = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수");
            Assert.True(r.IsValid);
            Assert.Single(r.Spec!.Entry);
            Assert.Single(r.Spec.Exit);   // 매수엔 상보 데드크로스 청산
        }

        [Fact]
        public void ParseNl_Rsi_SingleNumber_NotDoubleCountedAsPeriod()
        {
            var r = StrategyParser.ParseNl("rsi 20 이하면 매수, rsi 80 이상이면 매도");
            Assert.True(r.IsValid);
            var en = r.Spec!.Entry.First(c => c.Left.Kind == SpecKind.Rsi);
            Assert.Equal(14, en.Left.Param);          // 20을 기간으로 오인하지 않음
            Assert.Equal(20, en.Const, 3);            // 20은 임계
        }

        [Fact]
        public void ExtractPct_DoesNotGrabNeighborNumber()
        {
            var r = StrategyParser.ParseNl("골든크로스 매수, 데드크로스 매도, 손절 없이 익절 20%");
            Assert.True(r.IsValid);
            Assert.Equal(0, r.Spec!.StopLossPct, 3);  // '손절 없이' → 익절의 20을 손절로 오취하지 않음
            Assert.Equal(20, r.Spec.TakeProfitPct, 3);
        }

        [Fact]
        public void MakeId_ReflectsLogic()
        {
            string Mk(string enLogic) => $@"{{ ""entryLogic"":""{enLogic}"",
                ""entry"":[{{""left"":{{""kind"":""rsi"",""param"":14}},""op"":""lt"",""const"":30}},{{""left"":{{""kind"":""price""}},""op"":""crossAbove"",""right"":{{""kind"":""sma"",""param"":20}}}}],
                ""exitLogic"":""or"", ""exit"":[{{""left"":{{""kind"":""rsi"",""param"":14}},""op"":""gt"",""const"":70}}] }}";
            var a = StrategyParser.ParseJson(Mk("and"));
            var b = StrategyParser.ParseJson(Mk("or"));
            Assert.True(a.IsValid); Assert.True(b.IsValid);
            Assert.NotEqual(StrategyCompiler.Canonical(a.Spec!), StrategyCompiler.Canonical(b.Spec!));
            Assert.NotEqual(a.Spec!.Id, b.Spec!.Id);   // logic 차이가 id에 반영됨(충돌 없음)
        }

        [Fact]
        public void Validate_RejectsHugeConst()
        {
            var bad = StrategyParser.ParseJson(@"{ ""entry"":[{""left"":{""kind"":""rsi"",""param"":14},""op"":""lt"",""const"":1e30}],
                ""exit"":[{""left"":{""kind"":""rsi"",""param"":14},""op"":""gt"",""const"":70}] }");
            Assert.False(bad.IsValid);   // 유한하지만 거대한 상수 → decimal 캐스트 전에 거부
        }
    }
}
