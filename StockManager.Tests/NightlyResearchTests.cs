// ============================================================================
// NightlyResearchTests - 자율 야간 연구 루프 순수 헬퍼 검증 (네트워크 0 · 결정론)
//  DailyReturns 단위(소수)·경계, Build 병합/널, ToStrategies 중복제거·증명 일관성, 텍스트 합성.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace StockManager.Tests
{
    public class NightlyResearchTests
    {
        private static Candle C(decimal close) => new() { Date = DateTime.Today, Close = close, Open = close, High = close, Low = close, Volume = 0 };

        [Fact]
        public void DailyReturns_AreFractions_AndLengthMinusOne()
        {
            var candles = new List<Candle> { C(100), C(110), C(99) };  // +10%, -10%
            var r = NightlyResearch.DailyReturns(candles);
            Assert.Equal(2, r.Length);
            Assert.Equal(0.10, r[0], 6);
            Assert.Equal(-0.10, r[1], 6);
        }

        [Fact]
        public void DailyReturns_TooFew_Empty()
        {
            Assert.Empty(NightlyResearch.DailyReturns(new List<Candle> { C(100) }));
            Assert.Empty(NightlyResearch.DailyReturns(new List<Candle>()));
        }

        [Fact]
        public void Build_NullWhenForgeInvalid()
        {
            var bad = new ForgeResult(false, Array.Empty<ForgeCandidate>(), 0, 4, "x");
            Assert.Null(NightlyResearch.Build("A", "A", bad, null));
        }

        [Fact]
        public void Build_MergesTopCandidate_AndConeFlag()
        {
            var top = new ForgeCandidate(5, 20, 3.2, 1.1, 2.65, 7, 12.4, true);
            var forge = new ForgeResult(true, new[] { top }, 30, 4, "ok");
            // cone 없음
            var f0 = NightlyResearch.Build("005930", "삼성전자", forge, null);
            Assert.NotNull(f0);
            Assert.Equal(5, f0!.Short);
            Assert.Equal(20, f0.Long);
            Assert.Equal(2.65, f0.Score, 6);
            Assert.False(f0.HasCone);

            // cone 있음
            var cone = new FutureConeResult(true, 21, 500, Array.Empty<ConeBand>(),
                4.5, 4.0, -8.0, 18.0, 33.0, 10.0, 41.0, -6.0, "ok");
            var f1 = NightlyResearch.Build("005930", "삼성전자", forge, cone);
            Assert.True(f1!.HasCone);
            Assert.Equal(4.5, f1.ProjExpPct, 6);
            Assert.Equal(33.0, f1.ProbLossPct, 6);
            Assert.Equal(41.0, f1.GoalHitPct, 6);
        }

        [Fact]
        public void ToStrategies_DedupesByMa_AndIsDeterministic()
        {
            var fs = new List<ResearchFinding>
            {
                new("A", "A", 5, 20, 3, 1, 2.5, 5, 10, false, 0, 0, 0, 0, 0),
                new("B", "B", 5, 20, 2, 1, 1.5, 4, 8, false, 0, 0, 0, 0, 0),   // 같은 MA → 중복 제거
                new("C", "C", 10, 60, 1, 1, 0.5, 3, 5, false, 0, 0, 0, 0, 0),
                new("D", "D", 30, 10, 9, 1, 9.0, 9, 9, false, 0, 0, 0, 0, 0),   // long<=short → 제외
            };
            var s1 = NightlyResearch.ToStrategies(fs);
            var s2 = NightlyResearch.ToStrategies(fs);
            Assert.Equal(2, s1.Count);
            Assert.Contains(s1, x => x.Short == 5 && x.Long == 20);
            Assert.Contains(s1, x => x.Short == 10 && x.Long == 60);
            // 결정론: 동일 입력 동일 증명 수치
            Assert.Equal(s1[0].AttestTotalReturnPct, s2[0].AttestTotalReturnPct, 9);
            Assert.Equal(NightlyResearch.AttestSeed, s1[0].Seed);
        }

        [Fact]
        public void Headline_EmptyVsFound()
        {
            Assert.Contains("발견 없음", NightlyResearch.Headline(Array.Empty<ResearchFinding>(), 7));
            var fs = new[] { new ResearchFinding("005930", "삼성전자", 5, 20, 3.1, 1, 2.5, 5, 10, true, 4.0, -8, 18, 30, 40) };
            var hl = NightlyResearch.Headline(fs, 12);
            Assert.Contains("삼성전자", hl);
            Assert.Contains("MA5/20", hl);
            Assert.Contains("12종목", hl);
        }

        [Fact]
        public void Brief_JsonRoundTrip_PreservesFindingsAndProposals()
        {
            // 영속화 경로(Research.Brief/History) 검증: IReadOnlyList 멤버를 가진 positional record가
            // System.Text.Json으로 왕복되는지 — LoadLatest/LoadHistory가 의존하는 핵심 가정.
            var fs = new[]
            {
                new ResearchFinding("005930", "삼성전자", 5, 20, 3.1, 1.0, 2.5, 5, 10.0, true, 4.0, -8.0, 18.0, 30.0, 40.0),
                new ResearchFinding("000660", "SK하이닉스", 10, 60, 1.2, 0.8, 0.8, 3, 6.0, false, 0, 0, 0, 0, 0),
            };
            var b = new ResearchBrief("2026-06-16T05:00:00", 12, 2, 10, "x.ms2cart", fs,
                new[] { "변동성 목표 초과 — 검토" }, "head", "note");

            var json = System.Text.Json.JsonSerializer.Serialize(b);
            var back = System.Text.Json.JsonSerializer.Deserialize<ResearchBrief>(json);

            Assert.NotNull(back);
            Assert.Equal(2, back!.Findings.Count);
            Assert.Equal("삼성전자", back.Findings[0].Name);
            Assert.Equal(5, back.Findings[0].Short);
            Assert.True(back.Findings[0].HasCone);
            Assert.False(back.Findings[1].HasCone);
            Assert.Single(back.Proposals);
            Assert.Equal("x.ms2cart", back.CartridgeFile);
            Assert.Equal(12, back.Scanned);

            // 리스트 왕복(History)
            var hist = new List<ResearchBrief> { b, b };
            var hjson = System.Text.Json.JsonSerializer.Serialize(hist);
            var hback = System.Text.Json.JsonSerializer.Deserialize<List<ResearchBrief>>(hjson);
            Assert.Equal(2, hback!.Count);
            Assert.Equal(2, hback[0].Findings.Count);
        }

        [Fact]
        public void ComposeText_ContainsHeaderAndDisclaimer()
        {
            var fs = new[] { new ResearchFinding("005930", "삼성전자", 5, 20, 3.1, 1, 2.5, 5, 10, true, 4.0, -8, 18, 30, 40) };
            var b = new ResearchBrief("2026-06-16T05:00:00", 12, 1, 10, "", fs, new[] { "변동성 목표 초과 — 검토" }, "head", "note");
            var txt = NightlyResearch.ComposeText(b);
            Assert.Contains("밤사이 연구", txt);
            Assert.Contains("삼성전자", txt);
            Assert.Contains("코파일럿 제안", txt);
            Assert.Contains("자동 주문은 없습니다", txt);
        }
    }
}
