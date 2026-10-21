// ============================================================================
// Phase94UiPortabilityTests — Phase 94-A UI 이주 가능성 분석기 적대적 검증(100% 단언)
//   🧭 Classify: 신호 조합 → LeafReady/PortBlocked/HostShell (fail-safe: 콘크리트 신호 하나라도 → PortBlocked)
//   🔎 신호 감지기: IsHostShell·HasInject·RefsConcreteEngine(MS2E.Core/Infra, UI.Portable 제외)·HasConcreteCascade
//   📊 UiMigrationBacklog: 판정 집계
//   ⚡ δ=0: Classify/ClassifySource/감지기/Ingest 핫패스 힙 0
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94UiPortabilityTests
    {
        // ══ 🧭 핵심 분류 ══════════════════════════════════════════════════════
        [Fact]
        public void Classify_SignalCombos()
        {
            // HostShell 우선(다른 신호 무관)
            Assert.Equal(UiPortability.HostShell, UiPortabilityAnalyzer.Classify(true, true, true, true));
            Assert.Equal(UiPortability.HostShell, UiPortabilityAnalyzer.Classify(true, false, false, false));
            // 콘크리트 신호 0 → LeafReady
            Assert.Equal(UiPortability.LeafReady, UiPortabilityAnalyzer.Classify(false, false, false, false));
            // 각 콘크리트 신호 하나라도 → PortBlocked(fail-safe)
            Assert.Equal(UiPortability.PortBlocked, UiPortabilityAnalyzer.Classify(false, true, false, false));   // @inject
            Assert.Equal(UiPortability.PortBlocked, UiPortabilityAnalyzer.Classify(false, false, true, false));   // 콘크리트 참조
            Assert.Equal(UiPortability.PortBlocked, UiPortabilityAnalyzer.Classify(false, false, false, true));   // 콘크리트 캐스케이드
        }

        // ══ 🔎 신호 감지기 ════════════════════════════════════════════════════
        [Theory]
        [InlineData("StockManager/Blazor/App.razor", true)]
        [InlineData("StockManager/Blazor/_Imports.razor", true)]
        [InlineData("StockManager/Blazor/MainLayout.razor", true)]
        [InlineData("StockManager/Blazor/Pages/AdminHostPage.razor", true)]
        [InlineData("StockManager/Blazor/NavMenu.razor", true)]
        [InlineData("StockManager/Blazor/Pages/RiskPage.razor", false)]
        [InlineData("MS2E.UI.Portable/Components/HubTabs.razor", false)]
        public void IsHostShell_Detects(string path, bool expected)
            => Assert.Equal(expected, UiPortabilityAnalyzer.IsHostShell(path.AsSpan()));

        [Theory]
        [InlineData("@inject SecurityAssetVault Vault", true)]
        [InlineData("<div>순수 마크업</div>", false)]
        public void HasInject_Detects(string src, bool expected)
            => Assert.Equal(expected, UiPortabilityAnalyzer.HasInject(src.AsSpan()));

        [Theory]
        [InlineData("@using MS2E.Infrastructure", true)]
        [InlineData("var v = MS2E.Core.QMoney.Of(1);", true)]
        [InlineData("@using MS2E.UI.Portable.Components", false)]   // ★순수 UI 는 콘크리트 아님
        [InlineData("<div class=\"x\">no engine</div>", false)]
        public void RefsConcreteEngine_Detects(string src, bool expected)
            => Assert.Equal(expected, UiPortabilityAnalyzer.RefsConcreteEngine(src.AsSpan()));

        [Theory]
        [InlineData("[CascadingParameter] public PortfolioData Data { get; set; }", true)]
        [InlineData("[Parameter] public string Tab { get; set; }", false)]
        public void HasConcreteCascade_Detects(string src, bool expected)
            => Assert.Equal(expected, UiPortabilityAnalyzer.HasConcreteCascade(src.AsSpan()));

        // ══ 통합 판정(실 소스 패턴) ════════════════════════════════════════════
        [Theory]
        // App/HostPage → HostShell
        [InlineData("StockManager/Blazor/App.razor", "<Router AppAssembly=\"@typeof(App).Assembly\" />", UiPortability.HostShell)]
        // RiskPage: @inject 0 이나 [CascadingParameter] PortfolioData → PortBlocked(순진한 '@inject 0=이주가능' 반증)
        [InlineData("StockManager/Blazor/Pages/RiskPage.razor", "[CascadingParameter] public PortfolioData Data { get; set; }", UiPortability.PortBlocked)]
        // @inject 콘크리트 → PortBlocked
        [InlineData("StockManager/Blazor/Pages/TelemetryMonitor.razor", "@inject RingTelemetryPublisher Pub", UiPortability.PortBlocked)]
        // 순수 프레젠테이션 leaf(콘크리트 신호 0) → LeafReady
        [InlineData("MS2E.UI.Portable/Components/PureBadge.razor", "<span class=\"badge\">@Text</span>", UiPortability.LeafReady)]
        public void ClassifySource_RealPatterns(string path, string src, UiPortability expected)
            => Assert.Equal(expected, UiPortabilityAnalyzer.ClassifySource(path.AsSpan(), src.AsSpan()));

        // ══ 📊 백로그 집계 ════════════════════════════════════════════════════
        [Fact]
        public void Backlog_Counts()
        {
            var b = new UiMigrationBacklog();
            b.Ingest(UiPortability.LeafReady);
            b.Ingest(UiPortability.PortBlocked);
            b.Ingest(UiPortability.PortBlocked);
            b.Ingest(UiPortability.HostShell);
            Assert.Equal(4, b.Total);
            Assert.Equal(1, b.LeafReady);
            Assert.Equal(2, b.PortBlocked);
            Assert.Equal(1, b.HostShell);
        }

        // ══ ⚡ δ=0 무할당 증명 ═════════════════════════════════════════════════
        [Fact]
        public void Classify_ClassifySource_ZeroAllocation()
        {
            var backlog = new UiMigrationBacklog();
            for (int i = 0; i < 10_000; i++)   // 워밍업
            {
                ReadOnlySpan<char> w = "StockManager/Blazor/Pages/RiskPage.razor".AsSpan();
                backlog.Ingest(UiPortabilityAnalyzer.ClassifySource(w, "[CascadingParameter] PortfolioData D".AsSpan()));
            }
            long Measure()
            {
                long bAlloc = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++)
                {
                    // ★string 리터럴 .AsSpan() 은 무할당·Measure 내부 선언(외부 ref-struct 캡처 회피)
                    ReadOnlySpan<char> path = "StockManager/Blazor/Pages/RiskPage.razor".AsSpan();
                    ReadOnlySpan<char> src = "@inject Foo Bar\n[CascadingParameter] PortfolioData D".AsSpan();
                    var p = UiPortabilityAnalyzer.ClassifySource(path, src);
                    backlog.Ingest(p);
                    _ = UiPortabilityAnalyzer.Classify(false, true, true, true);
                }
                return GC.GetAllocatedBytesForCurrentThread() - bAlloc;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★분류·감지·집계 핫패스 힙 0
        }
    }
}
