// ============================================================================
// DualArenaInspectorTests — v12.18.92-DEV: 시세 출처 시인성 · 진화 진행도(ETA) · 쿨다운 다이얼 · 플루토 인지 데이터 인스펙터
//
//  §1 진행도 계산 — 첫 심사 N / 20 · 이후 새 표본 n / 10 · 기준점 미상 = 미표기 · 엔진 상수 미러(보정기 최소 표본 · 재보정 주기 · 쿨다운 15분)
//  §2 텐서 어댑터 — 39셀 투사(국면 1~12 행 → 기준선 행 · KRX · US · Upbit 순) · 셀 실측 승패 · 미결선 = 빈 목록 · 스웜 심사 기준점 투사
//  §3 HUD — 1.0s 스캔 하트비트(이력 없음 = 정직 문구) · 진화 진행도 바 · 인스펙터 버튼(OnInspect 결선 시만)
//  §4 페이지 출처 안내 — 실시간 = 실수신 상태(0건 = 미연결) · 합성 = ⚠️ 모의 가격 경고 배너
//  §5 인스펙터 모달 — 분석 피처 표 · 행 클릭 근거 원문 · 39셀 실측표(활성 국면 강조) · 4대 위원회 채점표 · 미결선 = 정직 문구 · 닫기
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.UI.Portable.Components;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Common;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using StockManager.Engines.Cognitive.Council;
using StockManager.Engines.Cognitive.Governance;
using StockManager.Engines.Cognitive.Learning;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class DualArenaInspectorTests
    {
        private const long Samsung = 5930;
        private static readonly DateTime Open10 = new(2026, 10, 22, 10, 0, 0);   // 목요일 10:00 KST
        private static readonly long T0 = new DateTime(2026, 10, 27, 0, 0, 0, DateTimeKind.Utc).Ticks;

        // ── §1 진행도 계산 ──────────────────────────────────────────────────

        [Fact]
        public void Evolution_FirstGate_Then_Every10_Unknown_Anchor_Hidden()
        {
            var a = PlutoEvolutionEta.Of(7, 0);
            Assert.True(a.Known); Assert.True(a.FirstGate);
            Assert.Equal((7L, 20L, 35), (a.Have, a.Need, a.Pct));
            Assert.Equal("차세대 진화 첫 심사까지 표본: 7 / 20 (35%)", a.Text);

            var b = PlutoEvolutionEta.Of(25, 20);
            Assert.False(b.FirstGate);
            Assert.Equal((5L, 10L, 50), (b.Have, b.Need, b.Pct));
            Assert.Equal("다음 진화 심사까지 새 표본: 5 / 10 (50%)", b.Text);

            Assert.Equal(100, PlutoEvolutionEta.Of(99, 20).Pct);   // 스냅샷 경합 초과 = 포화
            Assert.Equal(0, PlutoEvolutionEta.Of(30, 40).Pct);     // 기준점 > 표본(다른 스냅샷) = 0 하한
            Assert.Equal((0L, 20L), (PlutoEvolutionEta.Of(-5, -1).Have, PlutoEvolutionEta.Of(-5, -1).Need));

            var u = PlutoEvolutionEta.Of(25, -1);
            Assert.False(u.Known);
            Assert.Equal(0, u.Pct);
            Assert.Contains("재보정 주기", u.Text);
        }

        [Fact]
        public void Mirrors_Engine_Constants_And_Cooldown_Dial()
        {
            Assert.Equal(PlutoWeightCalibrator.MinSamples, PlutoEvolutionEta.FirstGate);
            Assert.Equal(PlutoContinuousLearningSwarm.RecalibrateEvery, PlutoEvolutionEta.Every);
            Assert.Equal(PlutoDynamicSizer.CooldownTicks, PlutoCooldownDial.Minutes * TimeSpan.TicksPerMinute);

            Assert.Equal(100, PlutoCooldownDial.Pct(15));
            Assert.Equal(53, PlutoCooldownDial.Pct(8));
            Assert.Equal(0, PlutoCooldownDial.Pct(-3));
            Assert.Equal(100, PlutoCooldownDial.Pct(99));
            Assert.Equal("background:conic-gradient(#E53935 0 53%, #1F2937 0)", PlutoCooldownDial.Ring(8));

            Assert.Equal(PlutoSlotMaturityCode.Calibrated, (int)PlutoSlotMaturity.Calibrated);
            Assert.Equal(PlutoSlotMaturityCode.Accumulating, (int)PlutoSlotMaturity.Accumulating);
            Assert.Equal("미학습", PlutoSlotMaturityCode.Label(77));
        }

        // ── §2 텐서 어댑터 ──────────────────────────────────────────────────

        private static PlutoPostMortem Win(long seq) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryCost = 100_000, Regime = MarketRegime.TrendingBull },
                ArenaTrigger.TakeProfit, MarketRegime.TrendingBull, 1000, true, TimeSpan.TicksPerMinute, 0);

        private static PlutoPostMortem Loss(long seq) =>
            PlutoPostMortemReviewer.Review(new PlutoEntryMemo { EntrySeq = seq, EntryCost = 100_000, Regime = MarketRegime.ChoppyRange, Theses = CounterThesis.Overheat },
                ArenaTrigger.StopLoss, MarketRegime.ChoppyRange, -1000, true, TimeSpan.TicksPerMinute, 0);

        [Fact]
        public void Adapter_Projects_39_Cells_In_Matrix_Order_With_Measured_Edge()
        {
            var t = new PlutoCognitiveTensorLake();
            for (int i = 1; i <= 7; i++) Assert.True(t.Observe(PlutoAssetClass.Upbit, 3, Win(i), 80, 0));
            for (int i = 8; i <= 12; i++) Assert.True(t.Observe(PlutoAssetClass.Upbit, 3, Loss(i), 80, 0));

            var cells = PlutoLearningPortAdapter.MapCells(t);
            Assert.Equal(PlutoCognitiveTensorLake.Cells, cells.Length);
            for (int k = 0; k < 36; k++)
            {
                Assert.Equal(k / 3 + 1, cells[k].RegimeId);
                Assert.Equal(k % 3, cells[k].Asset);
                Assert.False(string.IsNullOrEmpty(cells[k].RegimeName));
            }
            for (int k = 36; k < 39; k++)
            {
                Assert.True(cells[k].IsBaseline);
                Assert.Equal("자산 기준선", cells[k].RegimeName);
                Assert.Equal(k - 36, cells[k].Asset);
            }

            var slot = cells[(3 - 1) * 3 + PlutoRecoMarket.Crypto];
            Assert.Equal((3, 7, 5), (slot.RegimeId, slot.Wins, slot.Losses));
            Assert.Equal("58.3% (7승 5패)", slot.WinText);
            Assert.Equal(PlutoSlotMaturityCode.Accumulating, slot.Maturity);   // 12 < 20
            Assert.Equal(12, slot.Lifetime);
            var upbitBase = cells[36 + PlutoRecoMarket.Crypto];
            Assert.Equal(12, upbitBase.Samples);                               // 기준선 = 자산군 전 표본 동시 관측
            Assert.Equal("—", cells[0].WinText);
            Assert.Equal(PlutoSlotMaturityCode.Untrained, cells[0].Maturity);
        }

        [Fact]
        public void Adapter_ReadTensorCells_Unwired_Empty_And_Anchor_Projected()
        {
            var swarm = new PlutoContinuousLearningSwarm(new PlutoGlobalDataIngestor(() => T0), new PlutoEpisodicMemoryStore(), new PlutoWeightCalibrator(),
                new PlutoCognitiveWeightSeqlock(), () => T0);
            Assert.Empty(new PlutoLearningPortAdapter(swarm).ReadTensorCells());
            Assert.Empty(((IPlutoLearningPort)UnwiredPlutoLearningPort.Instance).ReadTensorCells());   // 기본 인터페이스 구현 = 빈 목록
            Assert.Equal(39, new PlutoLearningPortAdapter(swarm, tensor: new PlutoCognitiveTensorLake()).ReadTensorCells().Count);

            swarm.Step();
            var v = new PlutoLearningPortAdapter(swarm).Current;
            Assert.Equal(0, v.EvolveAnchor);
            Assert.Equal("0/20", $"{v.Evolution.Have}/{v.Evolution.Need}");
            Assert.Equal(-1, UnwiredPlutoLearningPort.Instance.Current.EvolveAnchor);
        }

        // ── §3 HUD ──────────────────────────────────────────────────────────

        private static PlutoLearningView Learning(long samples, long anchor) =>
            new(true, PlutoLearningHealth.Live, null, -1, false, 0, 0, 0, samples, 0, 0, EvolveAnchor: anchor);

        [Fact]
        public void Hud_Heartbeat_Evolution_Bar_And_Inspect_Button()
        {
            using var ctx = new Bunit.TestContext();
            int opened = 0;
            var cut = ctx.RenderComponent<PlutoCognitiveHud>(p => p
                .Add(x => x.Wired, true).Add(x => x.View, Learning(7, 0))
                .Add(x => x.OnInspect, () => opened++));
            Assert.Equal("none", cut.Find(".hud-pulse").GetAttribute("data-hud-pulse"));
            Assert.Contains("1.0s 스캔 하트비트 · 위원회 스캔 이력 없음", cut.Find(".hud-pulse").TextContent);
            Assert.Equal("7/20", cut.Find(".hud-evo").GetAttribute("data-hud-evo"));
            Assert.Equal("35", cut.Find(".hud-evo").GetAttribute("data-hud-evo-pct"));
            Assert.Contains("width:35%", cut.Find(".hud-evo .hud-gauge span").GetAttribute("style"));
            cut.Find("[data-hud-inspect]").Click();
            Assert.Equal(1, opened);

            cut.SetParametersAndRender(p => p.Add(x => x.View, Learning(31, -1)).Add(x => x.ScanUtc, DateTime.UtcNow.AddSeconds(-4)).Add(x => x.ScanGeneration, 12));
            Assert.Equal("live", cut.Find(".hud-pulse").GetAttribute("data-hud-pulse"));
            Assert.Contains("(스캔 #12)", cut.Find(".hud-pulse").TextContent);
            Assert.Equal("unknown", cut.Find(".hud-evo").GetAttribute("data-hud-evo"));
            Assert.Empty(cut.FindAll(".hud-evo .hud-gauge"));                  // 기준점 미상 = 가짜 진행률 바 0

            var bare = ctx.RenderComponent<PlutoCognitiveHud>(p => p.Add(x => x.Wired, false));
            Assert.Empty(bare.FindAll("[data-hud-inspect]"));                   // OnInspect 미결선 = 버튼 미표시
            Assert.Empty(bare.FindAll(".hud-evo"));
        }

        // ── §4 페이지 출처 안내 ──────────────────────────────────────────────

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) Render(IKrxArenaPort krx, IPlutoLearningPort? learning = null, IPlutoRecommendationPort? reco = null)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton(krx);
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton<IArenaPreferencePort>(UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton(learning ?? UnwiredPlutoLearningPort.Instance);
            ctx.Services.AddSingleton(reco ?? UnwiredPlutoRecommendationPort.Instance);
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        private static KrxArenaPortAdapter QuotedKrx()
        {
            var e = new KrxArenaEngine();
            e.OnQuote(Samsung, 70_000, 70_100, 1000, 1000, 100, Open10);
            var f = new KrxArenaQuoteFeeder(e);
            f.Pump(Open10.AddHours(-9).Ticks);
            return new KrxArenaPortAdapter(e, f);
        }

        [Fact]
        public void Page_Bridge_Shows_Real_Receive_State_And_Synthetic_Shows_Mock_Price_Warning()
        {
            var krx = QuotedKrx();
            var (ctx, cut) = Render(krx);
            using (ctx)
            {
                Assert.Equal(KrxArenaFeed.Bridge, krx.Feed.Requested);
                Assert.Empty(cut.FindAll("[data-syn-warn]"));
                var bridge = cut.Find(".da-krx .da-bridge");
                bool live = krx.Feed.BridgeUpdates > 0;
                Assert.Equal(live ? "ok" : "down", bridge.GetAttribute("data-bridge"));
                Assert.Contains(live ? "키움 브리지 실시세 정상 수신" : "키움 브리지 미연결 — 실제 호가 0건", bridge.TextContent);

                cut.FindAll(".da-krx .ar-tab").Single(b => b.TextContent.Contains("합성 시세")).Click();
                Assert.Equal(KrxArenaFeed.Synthetic, krx.Feed.Requested);
                var warn = cut.Find(".da-krx [data-syn-warn]");
                Assert.Equal(DualArenaPage.SyntheticWarning, warn.TextContent.Trim());
                Assert.Contains("모의 가격", warn.TextContent);
                Assert.Contains("실제 시장 주가와 다름", warn.TextContent);
                Assert.Empty(cut.FindAll(".da-krx .da-bridge"));
            }
        }

        [Fact]
        public void Page_Krx_Bridge_With_Zero_Updates_Is_Honestly_Unconnected()
        {
            var e = new KrxArenaEngine();
            var (ctx, cut) = Render(new KrxArenaPortAdapter(e, new KrxArenaQuoteFeeder(e)));
            using (ctx)
            {
                var bridge = cut.Find(".da-krx .da-bridge");
                Assert.Equal("down", bridge.GetAttribute("data-bridge"));
                Assert.Contains("가짜 시세로 대체하지 않습니다", bridge.TextContent);
            }
        }

        // ── §5 인스펙터 모달 ────────────────────────────────────────────────

        private sealed class FakeLearning : IPlutoLearningPort
        {
            public PlutoTensorCellView[] Cells = Array.Empty<PlutoTensorCellView>();
            public int Reads;
            public bool IsWired => true;
            public PlutoLearningView Current { get; set; } = new(true, PlutoLearningHealth.Live, null, -1, false, 0, 0, 0, 0, 0, 0);
            public IReadOnlyList<PlutoTensorCellView> ReadTensorCells() { Reads++; return Cells; }
        }

        private sealed class FakeReco : IPlutoRecommendationPort
        {
            public bool IsWired => true;
            public PlutoRecoBoardView Current { get; set; } = PlutoRecoBoardView.None;
        }

        private static PlutoTensorCellView[] Matrix()
        {
            var cells = new List<PlutoTensorCellView>();
            for (int r = 1; r <= 12; r++)
                for (int a = 0; a < 3; a++)
                    cells.Add(r == 4 && a == 2
                        ? new PlutoTensorCellView(r, "국면 4", a, 2, 12, 2, PlutoSlotMaturityCode.Calibrated, 40, 7, 5, 5833)
                        : new PlutoTensorCellView(r, "국면 " + r, a, 0, 0, 0, PlutoSlotMaturityCode.Untrained, 0, 0, 0, 0));
            for (int a = 0; a < 3; a++) cells.Add(new PlutoTensorCellView(-1, "자산 기준선", a, a == 0 ? 1 : 0, 0, a == 0 ? 8 : 0, a == 0 ? 3 : 0, 10, 5, 5, 5000));
            return cells.ToArray();
        }

        private static PlutoRecoBoardView Board()
        {
            var card = new PlutoRecommendationCard(PlutoRecoMarket.Krx, 1, 5380, "005380", "현대차", 78, 20, 22, 18, 10,
                new[] { "CVD 순매수 우위 · 체결강도 108%", "볼린저 밴드 상단 돌파 시도" }, true, 1500, "240,000원", "276,000원", true, 10, 0, "")
            { LearnMomentumPts = 5, LearnDevilPts = 2, CouncilWired = true, CouncilConsensus = 41, LearnText = "학습 조정 +3" };
            var krx = new PlutoRecoMarketView(new[] { card }, 200, 3, 0, 10, 200, 1);
            var lanes = new[]
            {
                new PlutoCouncilLaneView(PlutoRecoMarket.Krx, 3500, 2000, 2500, 2000, PlutoKellyCode.Insufficient, 0, 5000, 150, 10),
                new PlutoCouncilLaneView(PlutoRecoMarket.Crypto, 2000, 2000, 4000, 2000, PlutoKellyCode.Insufficient, 0, 5833, 120, 12),
            };
            var council = new PlutoCouncilView(true, true, 4, "국면 4", lanes, "", 0, false, "", 2);
            return new PlutoRecoBoardView(true, 9, DateTime.UtcNow, krx, PlutoRecoMarketView.None) { Council = council };
        }

        [Fact]
        public void Inspector_Renders_Features_Tensor_Matrix_And_Council_Scorecard()
        {
            var learning = new FakeLearning
            {
                Cells = Matrix(),
                Current = new PlutoLearningView(true, PlutoLearningHealth.Live, null, -1, false, 1, 0, 8, 12, 1, 0,
                    RegimeName: "국면 4", RegimeSimilarityX10: 940, Tensor: new PlutoTensorView(4, "국면 4", 1, new[]
                    {
                        new PlutoTensorLaneView(0, 1, 1, 0, 8, PlutoWarmCode.Ready, 3, 30),
                        new PlutoTensorLaneView(1, 0, 0, 0, 0, PlutoWarmCode.NoData, 0, 0),
                        new PlutoTensorLaneView(2, 2, 2, 12, 2, PlutoWarmCode.Ready, 5, 50),
                    })),
            };
            var (ctx, cut) = Render(QuotedKrx(), learning, new FakeReco { Current = Board() });
            using (ctx)
            {
                Assert.Empty(cut.FindAll("[data-pci]"));
                Assert.Equal(0, learning.Reads);                                   // 닫힘 = 텐서 판독 0
                cut.Find("[data-hud-inspect]").Click();
                Assert.Equal(1, learning.Reads);
                Assert.Equal("on", cut.Find("[data-pci-regime]").GetAttribute("data-pci-regime"));
                Assert.Contains("유사도 94.0%", cut.Find("[data-pci-regime]").TextContent);

                // ① 분석 피처
                Assert.Equal("1", cut.Find("table[data-pci-features]").GetAttribute("data-pci-features"));
                Assert.Empty(cut.FindAll("[data-pci-detail]"));
                cut.Find("tr[data-pci-card=\"005380\"]").Click();
                var detail = cut.Find("[data-pci-detail=\"005380\"]");
                Assert.Contains("CVD 순매수 우위 · 체결강도 108%", detail.TextContent);
                Assert.Contains("볼린저 밴드 상단 돌파 시도", detail.TextContent);
                Assert.Contains("+41", cut.Find("tr[data-pci-card=\"005380\"]").TextContent);
                cut.Find("[data-pci-market=\"1\"]").Click();
                Assert.Equal("empty", cut.Find("[data-pci-features]").GetAttribute("data-pci-features"));

                // ② 39셀 실측표
                Assert.Equal("39", cut.Find("table[data-pci-tensor]").GetAttribute("data-pci-tensor"));
                Assert.Equal(13, cut.FindAll("tr[data-pci-regime-row]").Count);
                Assert.Contains("on", cut.Find("tr[data-pci-regime-row=\"4\"]").ClassName);
                var hot = cut.Find("[data-pci-cell=\"4:2\"]");
                Assert.Equal("on", hot.GetAttribute("data-pci-maturity"));
                Assert.Contains("+12%", hot.TextContent);
                Assert.Contains("+2점(방어)", hot.TextContent);
                Assert.Contains("58.3% (7승 5패)", hot.TextContent);
                Assert.Contains("가중치 미보정", cut.Find("[data-pci-cell=\"1:0\"]").TextContent);
                Assert.Equal("WarmReady", cut.Find("[data-pci-warm=\"2\"]").TextContent);

                // ③ 위원회 채점표
                Assert.Equal("2", cut.Find("table[data-pci-council]").GetAttribute("data-pci-council"));
                var krxLane = cut.Find("tr[data-pci-lane=\"0\"]");
                Assert.Equal("3500", krxLane.QuerySelector("[data-pci-agent=\"0\"]")!.GetAttribute("data-pci-agent-bp"));
                Assert.Contains("lead", krxLane.QuerySelector("[data-pci-agent=\"0\"]")!.ClassName);
                Assert.Contains("lead", cut.Find("tr[data-pci-lane=\"2\"] [data-pci-agent=\"2\"]").ClassName);
                Assert.Equal("off", cut.Find("[data-pci-veto]").GetAttribute("data-pci-veto"));

                cut.Find("[data-pci-refresh]").Click();
                Assert.Equal(2, learning.Reads);
                cut.Find(".pci-x").Click();
                Assert.Empty(cut.FindAll("[data-pci]"));
            }
        }

        [Fact]
        public void Inspector_Unwired_Hosts_Render_Honest_Empty_States()
        {
            var (ctx, cut) = Render(QuotedKrx());
            using (ctx)
            {
                cut.Find("[data-hud-inspect]").Click();
                Assert.Equal("off", cut.Find("[data-pci-regime]").GetAttribute("data-pci-regime"));
                Assert.Contains("학습 엔진 미결선", cut.Find("[data-pci-regime]").TextContent);
                Assert.Equal("unwired", cut.Find("[data-pci-features]").GetAttribute("data-pci-features"));
                Assert.Equal("unwired", cut.Find("[data-pci-tensor]").GetAttribute("data-pci-tensor"));
                Assert.Equal("unwired", cut.Find("[data-pci-council]").GetAttribute("data-pci-council"));
                Assert.Empty(cut.FindAll(".pci-matrix"));
                cut.Find(".pci-scrim").Click();
                Assert.Empty(cut.FindAll("[data-pci]"));
            }
        }
    }
}
