// ============================================================================
// Phase112ArchitectureFitnessTests — v11.2.0 Clean Architecture 적합도 함수 검증(100% 단언)
//   🏛 CanReference: 의존 방향 규칙(안으로만) — Core→Infra/UI 위반, UI→엔진(Infra) 위반, Host/Tests 전허용
//   🧭 ClassifyLayer/Domain: 경로 역할 기반 결정론 분류(대표 경로 박제)
//   📍 IsMisplaced: 의도 레이어 ≠ 물리 위치 → 재배치 대상 탐지(현 모놀리식 Host 오배치)
//   ⚡ δ=0: Classify/CanReference/Ingest 핫패스 힙 0(Span·enum·정수 카운터)
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase112ArchitectureFitnessTests
    {
        // ══ 🏛 의존 방향 규칙(핵심 — 안으로만) ════════════════════════════════
        [Fact]
        public void CanReference_EnforcesInwardDependencyRule()
        {
            // Core = 가장 안쪽 → 바깥 참조 불가
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Core, ArchLayer.Core));
            Assert.False(ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Core, ArchLayer.Infrastructure));
            Assert.False(ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Core, ArchLayer.UiPortable));
            Assert.False(ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Core, ArchLayer.Host));

            // Infrastructure → Core/Infra 허용, UI/Host 불가
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Infrastructure, ArchLayer.Core));
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Infrastructure, ArchLayer.Infrastructure));
            Assert.False(ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Infrastructure, ArchLayer.UiPortable));

            // ★UI 포터블 → 엔진(Infrastructure) 무참조 불변(순수 UI)
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.UiPortable, ArchLayer.Core));
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.UiPortable, ArchLayer.UiPortable));
            Assert.False(ArchitectureFitnessAnalyzer.CanReference(ArchLayer.UiPortable, ArchLayer.Infrastructure));

            // Host(합성 루트)/Tests → 전 레이어 허용
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Host, ArchLayer.Core));
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Host, ArchLayer.Infrastructure));
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Host, ArchLayer.UiPortable));
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Tests, ArchLayer.Infrastructure));

            // Unknown 관여 → fail-open(분류 불확실을 위반으로 오판하지 않음)
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Unknown, ArchLayer.Core));
            Assert.True (ArchitectureFitnessAnalyzer.CanReference(ArchLayer.Core, ArchLayer.Unknown));
        }

        // ══ 🧭 경로 → 의도 레이어 분류(결정론) ═════════════════════════════════
        [Theory]
        [InlineData("StockManager.Tests/Phase110GenesisFleetTests.cs", ArchLayer.Tests)]
        [InlineData("MS2E.UI.Portable/Pages/Admin.razor",             ArchLayer.UiPortable)]
        [InlineData("StockManager/Blazor/NavMenu.razor",              ArchLayer.Host)]         // 네비 셸
        [InlineData("StockManager/Blazor/MainLayout.razor",           ArchLayer.Host)]         // 레이아웃 셸
        [InlineData("StockManager/Blazor/Pages/PortfolioHub.razor",   ArchLayer.UiPortable)]   // 페이지=UI 의도
        [InlineData("StockManager/Program.cs",                        ArchLayer.Host)]
        [InlineData("StockManager/Common/QFixed.cs",                  ArchLayer.Core)]         // 값객체
        [InlineData("StockManager/Common/IBrokerPort.cs",             ArchLayer.Core)]         // Port
        [InlineData("StockManager/Common/UniversalBrokerTypes.cs",    ArchLayer.Core)]         // DTO/Types
        [InlineData("StockManager/Common/TossPortfolioSyncAdapter.cs",ArchLayer.Infrastructure)]
        [InlineData("StockManager/Common/SecurityAssetVault.cs",      ArchLayer.Infrastructure)]
        [InlineData("StockManager/Common/RingTelemetry.cs",           ArchLayer.Infrastructure)]
        public void ClassifyLayer_Representative(string path, ArchLayer expected)
            => Assert.Equal(expected, ArchitectureFitnessAnalyzer.ClassifyLayer(path.AsSpan()));

        [Theory]
        [InlineData("StockManager/Common/IBrokerPort.cs",              ArchDomain.Ports)]
        [InlineData("StockManager/Common/QMoney.cs",                   ArchDomain.ValueObjects)]
        [InlineData("StockManager/Common/TossPortfolioSyncAdapter.cs", ArchDomain.BrokerAdapters)]
        [InlineData("StockManager/Common/SecurityAssetVault.cs",       ArchDomain.Security)]
        [InlineData("StockManager/Common/RingTelemetry.cs",            ArchDomain.Telemetry)]
        [InlineData("StockManager/Blazor/Pages/PortfolioHub.razor",    ArchDomain.Pages)]
        public void ClassifyDomain_Representative(string path, ArchDomain expected)
            => Assert.Equal(expected, ArchitectureFitnessAnalyzer.ClassifyDomain(path.AsSpan()));

        // ══ 📍 오배치 탐지(재배치 대상) ════════════════════════════════════════
        [Theory]
        [InlineData("StockManager/Common/QFixed.cs",                 true)]   // Core 의도·Host 물리 → 오배치
        [InlineData("StockManager/Common/SecurityAssetVault.cs",     true)]   // Infra 의도·Host 물리 → 오배치
        [InlineData("StockManager/Blazor/Pages/PortfolioHub.razor",  true)]   // UI 의도·Host 물리 → 오배치
        [InlineData("MS2E.UI.Portable/Pages/Admin.razor",            false)]  // UI 의도·UI 물리 → 정상
        [InlineData("StockManager/Program.cs",                       false)]  // Host 의도·Host 물리 → 정상
        [InlineData("StockManager.Tests/FooTests.cs",                false)]  // Tests → 정상
        public void IsMisplaced_DetectsRestructureTargets(string path, bool expected)
            => Assert.Equal(expected, ArchitectureFitnessAnalyzer.IsMisplaced(path.AsSpan()));

        // ══ 📊 FitnessScan 집계 ════════════════════════════════════════════════
        [Fact]
        public void FitnessScan_CountsViolations_AndCleanFlag()
        {
            var scan = new FitnessScan();
            Assert.True (scan.Ingest(ArchLayer.Infrastructure, ArchLayer.Core));        // 준수
            Assert.True (scan.Ingest(ArchLayer.Host, ArchLayer.UiPortable));            // 준수
            Assert.False(scan.Ingest(ArchLayer.Core, ArchLayer.Infrastructure));        // 위반(안→바깥)
            Assert.False(scan.Ingest(ArchLayer.UiPortable, ArchLayer.Infrastructure));  // 위반(엔진 참조)
            Assert.Equal(4L, scan.Edges);
            Assert.Equal(2L, scan.Violations);
            Assert.False(scan.Clean);

            scan.Reset();
            Assert.Equal(0L, scan.Edges);
            Assert.Equal(0L, scan.Violations);
            Assert.True(scan.Clean);
        }

        // ══ ⚡ δ=0 무할당 증명 ═════════════════════════════════════════════════
        [Fact]
        public void Classify_CanReference_Ingest_ZeroAllocation()
        {
            var scan = new FitnessScan();
            // 워밍업(JIT·string 인터닝)
            for (int i = 0; i < 10_000; i++)
            {
                ReadOnlySpan<char> w = "StockManager/Common/TossPortfolioSyncAdapter.cs".AsSpan();
                scan.Ingest(ArchitectureFitnessAnalyzer.ClassifyLayer(w), ArchLayer.Core);
            }
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++)
                {
                    // ★ref-struct(span)는 로컬 함수 내부에서 선언(외부 캡처=CS8175 회피)
                    ReadOnlySpan<char> p = "StockManager/Common/SecurityAssetVault.cs".AsSpan();
                    ArchLayer layer = ArchitectureFitnessAnalyzer.ClassifyLayer(p);
                    ArchDomain dom  = ArchitectureFitnessAnalyzer.ClassifyDomain(p);
                    bool misplaced  = ArchitectureFitnessAnalyzer.IsMisplaced(p);
                    bool ok         = ArchitectureFitnessAnalyzer.CanReference(layer, ArchLayer.Core);
                    scan.Ingest(layer, ArchLayer.Core);
                    _ = dom; _ = ok; _ = misplaced;
                }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★분류·의존규칙·오배치·집계 핫패스 힙 0
        }
    }
}
