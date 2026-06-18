// ============================================================================
// MultiverseEngineTests — 시나리오 이동·블록부트스트랩·BlackSwan 오버레이·결정론 검증
//  · 외부 의존 0, 결정론 합성 수익률, 네트워크 호출 없음
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class MultiverseEngineTests
    {
        // ── 헬퍼 ──────────────────────────────────────────────────────────────
        private static MultiverseAsset MakeAsset(string sym, string name, double weight,
            int length = 100)
        {
            // 순수 결정론 합성 수익률: 홀짝 번갈아 (+0.5% / -0.3%) 약한 양의 드리프트
            var rets = new double[length];
            for (int i = 0; i < length; i++)
                rets[i] = i % 2 == 0 ? 0.005 : -0.003;
            return new MultiverseAsset(sym, name, weight, rets);
        }

        // ─────────────────────────────────────────────────────────────────────
        [Fact]
        public void Project_EmptyAssets_Invalid()
        {
            var r = MultiverseEngine.Project(Array.Empty<MultiverseAsset>(), MultiverseScenario.Live);
            Assert.False(r.IsValid);
        }

        [Fact]
        public void Project_InsufficientHistory_Invalid()
        {
            var asset = new MultiverseAsset("A", "Test", 100.0, new double[20]);
            var r = MultiverseEngine.Project(new[] { asset }, MultiverseScenario.Live);
            Assert.False(r.IsValid);
        }

        [Fact]
        public void Project_Live_ValidBands()
        {
            var r = MultiverseEngine.Project(new[] { MakeAsset("A", "T", 100) }, MultiverseScenario.Live);
            Assert.True(r.IsValid);
            Assert.Equal(MultiverseScenario.Live, r.Scenario);
            Assert.NotEmpty(r.Bands);
        }

        [Fact]
        public void Project_BullRun_HigherNavThanDeepRecession()
        {
            var assets = new[] { MakeAsset("A", "T", 100) };
            var bull      = MultiverseEngine.Project(assets, MultiverseScenario.BullRun);
            var recession = MultiverseEngine.Project(assets, MultiverseScenario.DeepRecession);
            Assert.True(bull.IsValid && recession.IsValid);
            Assert.True(bull.ProjectedNavPct > recession.ProjectedNavPct,
                $"Bull={bull.ProjectedNavPct:0.0}% expected > Recession={recession.ProjectedNavPct:0.0}%");
        }

        [Fact]
        public void Project_DeepRecession_HasCrisisOverlay()
        {
            var assets = new[] { MakeAsset("A", "T", 100) };
            var r = MultiverseEngine.Project(assets, MultiverseScenario.DeepRecession);
            Assert.True(r.IsValid);
            Assert.NotNull(r.CrisisOverlay);
            Assert.True(r.CrisisOverlay!.IsValid);
        }

        [Fact]
        public void Project_LiveScenario_NoCrisisOverlay()
        {
            var r = MultiverseEngine.Project(new[] { MakeAsset("A", "T", 100) }, MultiverseScenario.Live);
            Assert.True(r.IsValid);
            Assert.Null(r.CrisisOverlay);
        }

        [Fact]
        public void Project_Deterministic_SameResult()
        {
            var assets = new[] { MakeAsset("A", "T", 60), MakeAsset("B", "U", 40) };
            var r1 = MultiverseEngine.Project(assets, MultiverseScenario.Stagflation);
            var r2 = MultiverseEngine.Project(assets, MultiverseScenario.Stagflation);
            Assert.True(r1.IsValid);
            Assert.Equal(r1.ProjectedNavPct,   r2.ProjectedNavPct);
            Assert.Equal(r1.AnnualizedSharpe,  r2.AnnualizedSharpe);
            Assert.Equal(r1.ProjectedMddPct,   r2.ProjectedMddPct);
        }

        [Fact]
        public void Project_TwoAssets_WeightsPreserved()
        {
            var assets = new[] { MakeAsset("A", "T", 60), MakeAsset("B", "U", 40) };
            var r = MultiverseEngine.Project(assets, MultiverseScenario.BullRun);
            Assert.True(r.IsValid);
            Assert.Equal(2, r.Assets.Count);
            Assert.Equal(60.0, r.Assets.First(a => a.Symbol == "A").WeightPct, 3);
            Assert.Equal(40.0, r.Assets.First(a => a.Symbol == "B").WeightPct, 3);
        }

        [Fact]
        public void ScenarioTable_HasFourEntries()
        {
            Assert.Equal(4, MultiverseEngine.ScenarioTable.Count);
        }

        [Fact]
        public void Project_Stagflation_HigherMddThanBullRun()
        {
            var assets = new[] { MakeAsset("A", "T", 100) };
            var bull = MultiverseEngine.Project(assets, MultiverseScenario.BullRun);
            var stag = MultiverseEngine.Project(assets, MultiverseScenario.Stagflation);
            Assert.True(bull.IsValid && stag.IsValid);
            Assert.True(stag.ProjectedMddPct >= bull.ProjectedMddPct,
                $"Stag MDD={stag.ProjectedMddPct:0.0}% expected >= Bull MDD={bull.ProjectedMddPct:0.0}%");
        }

        [Fact]
        public void Project_AllScenarios_Valid()
        {
            var assets = new[] { MakeAsset("A", "T", 100) };
            foreach (MultiverseScenario s in Enum.GetValues<MultiverseScenario>())
            {
                var r = MultiverseEngine.Project(assets, s);
                Assert.True(r.IsValid, $"Scenario {s} should produce valid result");
            }
        }

        // GAP-03: sigma=0 경로 (모든 수익률 0) — 동작 핀
        [Fact]
        public void Project_ConstantZeroReturns_ZeroSharpe()
        {
            var zeroReturns = new double[100];   // 전부 0.0 → StdDev=0, sigma=0 경로
            var asset = new MultiverseAsset("ZERO", "ZeroAsset", 100.0, zeroReturns);
            var r = MultiverseEngine.Project(new[] { asset }, MultiverseScenario.Live);
            if (r.IsValid)
                Assert.Equal(0.0, r.AnnualizedSharpe);
            else
                Assert.False(string.IsNullOrEmpty(r.Note));
        }

        // BUG-003: sims 가드 — FutureCone 계약(100)과 일치 확인
        [Fact]
        public void Project_SimsDefaultAbove100()
        {
            var assets = new[] { MakeAsset("A", "T", 100) };
            // 기본 sims=200이므로 FutureCone 최솟값(100) 이상 → 결과 유효
            var r = MultiverseEngine.Project(assets, MultiverseScenario.Live);
            Assert.True(r.IsValid, "sims 기본값이 FutureCone 최솟값 이상이어야 유효 결과를 반환해야 함");
        }
    }
}
