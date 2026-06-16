// ============================================================================
// StrategyTournamentTests - 스펙 디코더 왕복 + 전략 토너먼트 리그 (순수·결정론·네트워크 0)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class StrategyTournamentTests
    {
        private static List<Candle> Synth(int n = 300)
        {
            var list = new List<Candle>(n);
            var d = new DateTime(2021, 1, 1);
            for (int i = 0; i < n; i++)
            {
                double p = 100 + i * 0.12 + 14 * Math.Sin(i / 11.0) + 6 * Math.Sin(i / 4.0);
                decimal c = (decimal)Math.Round(p, 2);
                list.Add(new Candle { Date = d.AddDays(i), Open = c, High = c * 1.01m, Low = c * 0.99m, Close = c, Volume = 1000 + i });
            }
            return list;
        }

        // ===== 스펙 디코더 왕복 (Canonical ↔ StrategySpec) =====

        [Fact]
        public void TryParseCanonical_RoundTrip_MaCross()
        {
            var spec = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도, 손절 8%").Spec!;
            var canon = StrategyCompiler.Canonical(spec);
            Assert.True(StrategyCompiler.TryParseCanonical(canon, out var back));
            Assert.NotNull(back);
            Assert.Equal(canon, StrategyCompiler.Canonical(back!));   // 정준 동일
            Assert.Equal(spec.Id, back!.Id);                          // id 일치
        }

        [Fact]
        public void TryParseCanonical_RoundTrip_RsiConstAndLogic()
        {
            var json = @"{ ""entryLogic"":""or"",
                ""entry"":[{""left"":{""kind"":""rsi"",""param"":14},""op"":""lt"",""const"":30},{""left"":{""kind"":""price""},""op"":""crossAbove"",""right"":{""kind"":""sma"",""param"":20}}],
                ""exitLogic"":""and"", ""exit"":[{""left"":{""kind"":""rsi"",""param"":14},""op"":""gt"",""const"":70}],
                ""stopLossPct"":5, ""takeProfitPct"":12 }";
            var spec = StrategyParser.ParseJson(json).Spec!;
            var canon = StrategyCompiler.Canonical(spec);
            Assert.True(StrategyCompiler.TryParseCanonical(canon, out var back));
            Assert.Equal(canon, StrategyCompiler.Canonical(back!));
            Assert.Equal(SpecLogic.Or, back!.EntryLogic);
            Assert.Equal(SpecLogic.And, back.ExitLogic);
            Assert.Equal(2, back.Entry.Count);
            Assert.Equal(5, back.StopLossPct, 3);
            Assert.Equal(12, back.TakeProfitPct, 3);
        }

        [Fact]
        public void TryParseCanonical_RejectsGarbage()
        {
            Assert.False(StrategyCompiler.TryParseCanonical("not a spec", out _));
            Assert.False(StrategyCompiler.TryParseCanonical("", out _));
            Assert.False(StrategyCompiler.TryParseCanonical("SPEC1;EN0=;EX1=;SL=0;TP=0", out _));   // 진입 없음 → 검증 실패
        }

        // ===== 토너먼트 리그 =====

        private static Entrant Ma(int s, int l) => new($"MA {s}/{l}", "테스트", EntryKind.MaCross, s, l, -1, null);

        [Fact]
        public void Run_RanksValidEntrants_Deterministic()
        {
            var entrants = new List<Entrant> { Ma(5, 20), Ma(10, 60), Ma(3, 8) };
            var candles = Synth();
            var a = StrategyTournament.Run(entrants, candles, Market.KRX_KOSPI);
            var b = StrategyTournament.Run(entrants, candles, Market.KRX_KOSPI);
            Assert.True(a.IsValid);
            Assert.Equal(a.Rows.Count, b.Rows.Count);
            // 결정론: 동일 순위·점수
            Assert.Equal(a.Rows[0].Name, b.Rows[0].Name);
            Assert.Equal(a.Rows[0].Score, b.Rows[0].Score, 9);
            // 유효 출전은 점수 내림차순 + Rank 1부터
            var valids = a.Rows.Where(r => r.Valid).ToList();
            Assert.Equal(1, valids[0].Rank);
            for (int i = 1; i < valids.Count; i++) Assert.True(valids[i - 1].Score >= valids[i].Score);
        }

        [Fact]
        public void Run_TooFewCandles_Invalid()
        {
            var r = StrategyTournament.Run(new List<Entrant> { Ma(5, 20) }, Synth(50), Market.KRX_KOSPI);
            Assert.False(r.IsValid);
            Assert.Contains("캔들", r.Note);
        }

        [Fact]
        public void Run_NoEntrants_Invalid()
        {
            Assert.False(StrategyTournament.Run(new List<Entrant>(), Synth(), Market.KRX_KOSPI).IsValid);
        }

        [Fact]
        public void Run_SpecEntrant_Evaluated()
        {
            var spec = StrategyParser.ParseNl("5일선이 20일선을 골든크로스하면 매수, 데드크로스하면 매도").Spec!;
            var entrants = new List<Entrant>
            {
                new("스펙 " + spec.Id, "카트리지", EntryKind.Spec, 0, 0, -1, spec),
                Ma(10, 60),
            };
            var r = StrategyTournament.Run(entrants, Synth(), Market.KRX_KOSPI);
            Assert.True(r.IsValid);
            Assert.Contains(r.Rows, x => x.Source == "카트리지");
        }

        [Fact]
        public void Run_LongLookbackSpec_DroppedLikeMa()
        {
            // 폴드 길이(75)보다 큰 룩백(SMA200) → 스펙도 MA와 동일하게 탈락해야 함(공정성: 동일 폴드)
            var spec = StrategyParser.ParseNl("5/200 골든크로스 매수 데드크로스 매도").Spec!;
            var entrants = new List<Entrant>
            {
                new("스펙200", "c", EntryKind.Spec, 0, 0, -1, spec),
                Ma(5, 200),
                Ma(5, 20),
            };
            var r = StrategyTournament.Run(entrants, Synth(300), Market.KRX_KOSPI, folds: 4);
            Assert.False(r.Rows.First(x => x.Name == "스펙200").Valid);
            Assert.False(r.Rows.First(x => x.Name == "MA 5/200").Valid);
            Assert.True(r.Rows.First(x => x.Name == "MA 5/20").Valid);
        }

        [Fact]
        public void Run_InvalidEntrant_RankedZero_NotCrash()
        {
            // long<=short → RunMaCross 빈 결과 → 유효 폴드 부족 → 무효 출전(Rank 0), 크래시 없음
            var r = StrategyTournament.Run(new List<Entrant> { Ma(5, 20), Ma(60, 10) }, Synth(), Market.KRX_KOSPI);
            Assert.Contains(r.Rows, x => !x.Valid && x.Rank == 0);
            Assert.Contains(r.Rows, x => x.Valid);
        }
    }
}
