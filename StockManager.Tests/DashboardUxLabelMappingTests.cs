// ============================================================================
// DashboardUxLabelMappingTests — [대시보드 UX · 추천 A]
//
//  §1 라벨 표 ↔ 엔진 열거형 전수 대조 — QuantReason 16 · ArmingInterlockSource 29(0~27 + 63) 전부 고유 한글 라벨(누락 · 순서 이탈 = 실패) ·
//     점멸 대상 비트 = HealthAgent · StrategyAnomaly · EgressBypass · CredentialLeak(열거형 값으로 대조 — 재번호 시 실패).
//  §2 0B — 라벨 · 확신도 텍스트 · 배지 · 점멸 판정 조회 할당 0 · 같은 정적 인스턴스 반환.
//  §3 배지 · 확신도 — 0.85 게이트(8,500bp) 경계 · 매수 적/매도 청/보류 회색/거부 경고 · 범위 밖 클램프.
//  §4 [방향 A] 스파크라인 — 정수 좌표 · 고정 버퍼 작성 0B · 버퍼 부족/값 부족 = 0 · 게이트 선.
//  §5 [방향 B] 열화 거버너 — 지속 지연 3회 = 경량 · 회복 10회 = 복귀 · 경계 진동 무전환(히스테리시스) · 무효 표본 무시.
//  §6 허브 연동 — 교차 피드 꼬임 판단 → 프레임 사유 12 → '키움↔KIS 호가 꼬임' 경고 · 비트 27 격발 = 점멸.
//  §7 래칫(≤40) · RCL 순수성 · 뷰 결선 · 오중 빌드.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines;
using Xunit;

namespace StockManager.Tests
{
    public sealed class DashboardUxLabelMappingTests
    {
        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        // ════════════ §1 라벨 ↔ 열거형 ════════════

        [Fact]
        public void Labels_CoverEveryEngineCode_Uniquely()
        {
            var reasons = Enum.GetValues<QuantReason>();
            Assert.Equal(reasons.Length, SignalLabelMapper.ReasonCount);                                 // 코드 추가 시 표 갱신 강제
            var rl = reasons.Select(r => SignalLabelMapper.Reason((int)r)).ToArray();
            Assert.DoesNotContain(SignalLabelMapper.Unknown, rl);
            Assert.Equal(rl.Length, rl.Distinct().Count());
            Assert.Equal("키움↔KIS 호가 꼬임", SignalLabelMapper.Reason((int)QuantReason.CrossedFeed));
            Assert.Equal("피드 시각 이탈 > 1초 — 거부", SignalLabelMapper.Reason((int)QuantReason.CrossSkewVeto));
            foreach (var w in new[] { QuantReason.VolatilitySpikeVeto, QuantReason.Anomaly, QuantReason.CrossedFeed, QuantReason.CrossSkewVeto })
                Assert.True(SignalLabelMapper.IsWarningReason((int)w), w.ToString());
            Assert.False(SignalLabelMapper.IsWarningReason((int)QuantReason.ImbalanceBuy));

            var bits = Enum.GetValues<ArmingInterlockSource>();
            Assert.Equal(bits.Length, SignalLabelMapper.InterlockCount);                                 // 29(0~27 + 63)
            var il = bits.Select(b => SignalLabelMapper.Interlock((int)b)).ToArray();
            Assert.DoesNotContain(SignalLabelMapper.Unknown, il);
            Assert.Equal(il.Length, il.Distinct().Count());
            Assert.Equal("헬스 에이전트(자원)", SignalLabelMapper.Interlock((int)ArmingInterlockSource.HealthAgent));
            Assert.Equal("전략 이상(호가 꼬임 포함)", SignalLabelMapper.Interlock((int)ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(SignalLabelMapper.StartupSanityBit, (int)ArmingInterlockSource.StartupSanity);
            foreach (var s in new[] { ArmingInterlockSource.HealthAgent, ArmingInterlockSource.StrategyAnomaly, ArmingInterlockSource.EgressBypass, ArmingInterlockSource.CredentialLeak })
            {
                Assert.True(SignalLabelMapper.IsFlashBit((int)s), s.ToString());
                Assert.True(SignalLabelMapper.HasFlash(1L << (int)s));
            }
            Assert.False(SignalLabelMapper.HasFlash(1L << (int)ArmingInterlockSource.RouterFeed));
            Assert.Equal(SignalLabelMapper.Unknown, SignalLabelMapper.Interlock(40));
            Assert.Equal(SignalLabelMapper.Unknown, SignalLabelMapper.Reason(-1));
        }

        // ════════════ §2 0B ════════════

        [Fact]
        public void Lookups_ZeroAlloc_SameStaticInstances()
        {
            Assert.Same(SignalLabelMapper.Reason(12), SignalLabelMapper.Reason(12));
            Assert.Same(SignalLabelMapper.ConfidenceText(8_731), SignalLabelMapper.ConfidenceText(8_799));
            int sink = 0;
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int r = 0; r < 2_000; r++)
                {
                    for (int c = -1; c < 18; c++) sink += SignalLabelMapper.Reason(c).Length + (SignalLabelMapper.IsWarningReason(c) ? 1 : 0);
                    for (int b = 0; b < 64; b++) sink += SignalLabelMapper.Interlock(b).Length + (SignalLabelMapper.IsFlashBit(b) ? 1 : 0);
                    long bp = (r * 37L) % 10_500;
                    sink += SignalLabelMapper.ConfidenceText(bp).Length + SignalLabelMapper.ConfidenceWidth(bp).Length;
                    sink += SignalLabelMapper.BadgeClass(1 + (r & 1), bp, r % 16).Length + SignalLabelMapper.BadgeText(1 + (r & 1), bp, r % 16).Length;
                    sink += SignalLabelMapper.HasFlash(r) ? 1 : 0;
                }
            }));
            Assert.True(sink > 0);
        }

        // ════════════ §3 배지 · 확신도 ════════════

        [Fact]
        public void Badge_GateBoundary_ColorsAndClamp()
        {
            int buy = DashDecisionKind.Entry, sell = DashDecisionKind.Exit, imbBuy = (int)QuantReason.ImbalanceBuy;
            Assert.Equal(("up", "매수 확신"), (SignalLabelMapper.BadgeClass(buy, 8_500, imbBuy), SignalLabelMapper.BadgeText(buy, 8_500, imbBuy)));
            Assert.Equal(("hold", "보류"), (SignalLabelMapper.BadgeClass(buy, 8_499, imbBuy), SignalLabelMapper.BadgeText(buy, 8_499, imbBuy)));   // ★게이트 미달
            Assert.Equal(("down", "매도 확신"), (SignalLabelMapper.BadgeClass(sell, 9_000, (int)QuantReason.ImbalanceSell), SignalLabelMapper.BadgeText(sell, 9_000, 2)));
            Assert.Equal(("warn", "거부"), (SignalLabelMapper.BadgeClass(buy, 9_500, (int)QuantReason.CrossedFeed), SignalLabelMapper.BadgeText(buy, 9_500, 12)));   // 경고 근거 우선
            Assert.Equal("hold", SignalLabelMapper.BadgeClass(DashDecisionKind.Hold, 9_900, 0));
            Assert.Equal(("0%", "85%", "85%", "100%", "0%", "100%"), (SignalLabelMapper.ConfidenceText(0), SignalLabelMapper.ConfidenceText(8_500), SignalLabelMapper.ConfidenceText(8_599),
                SignalLabelMapper.ConfidenceText(10_000), SignalLabelMapper.ConfidenceText(-5), SignalLabelMapper.ConfidenceText(20_000)));
        }

        // ════════════ §4 스파크라인 ════════════

        [Fact]
        public void Sparkline_IntegerPoints_FixedBuffer_ZeroAlloc()
        {
            var buf = new char[256];
            int n = SparklineWriter.Write(new long[] { 0, 10_000, 5_000 }, buf, 300, 40);
            Assert.Equal("0,40 150,0 300,20", new string(buf, 0, n));
            Assert.Equal("0,0 300,40", new string(buf, 0, SparklineWriter.Write(new long[] { 99_999, -7 }, buf, 300, 40)));   // 클램프
            Assert.Equal(0, SparklineWriter.Write(new long[] { 5_000 }, buf, 300, 40));                    // 값 부족
            Assert.Equal(0, SparklineWriter.Write(new long[] { 0, 10_000, 5_000 }, new char[8], 300, 40));  // 버퍼 부족 = 부분 출력 0
            Assert.Equal(6, SparklineWriter.GateY(40));                                                    // 85% 게이트 선
            var vals = Enumerable.Range(0, PaperDashboardFrame.MaxDecisions).Select(i => (long)(i * 211 % 10_001)).ToArray();
            var big = new char[PaperDashboardFrame.MaxDecisions * 12];
            Assert.True(SparklineWriter.Write(vals, big, 300, 40) > 0);                                     // 뷰 버퍼 크기로 최대 48점 수용
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 2_000; i++) SparklineWriter.Write(vals, big, 300, 40); }));
        }

        // ════════════ §5 열화 거버너 ════════════

        [Fact]
        public void Degradation_Hysteresis_EntersOnSustainedLag_RecoversOnSustainedCalm()
        {
            var g = new DashboardDegradationGovernor();
            for (int i = 0; i < 100; i++) Assert.False(g.Observe(60));                                    // 정상 지연(렌더 병합 창 포함)
            Assert.False(g.Degraded);
            Assert.False(g.Observe(400));                                                                 // EWMA 230ms(60 에서 출발) — 아직 임계 미만
            Assert.False(g.Observe(400)); Assert.False(g.Observe(400));                                   // 315 · 358 = 초과 1 · 2회
            Assert.True(g.Observe(400));                                                                  // ★EWMA 초과 연속 3회 = 경량
            Assert.True(g.Degraded);
            int calm = 0;
            while (!g.Observe(20)) calm++;                                                                // EWMA 가 100ms 아래로 내려온 뒤 10회
            Assert.False(g.Degraded);
            Assert.InRange(calm, 9, 14);
            Assert.Equal((1L, 1L), (g.Degradations, g.Recoveries));

            var osc = new DashboardDegradationGovernor();
            for (int i = 0; i < 200; i++) osc.Observe(i % 2 == 0 ? 400 : 0);                                // 경계 진동 — 연속 조건 미충족
            Assert.False(osc.Degraded);
            Assert.False(osc.Observe(double.NaN)); Assert.False(osc.Observe(-1));                            // 무효 표본 무시
            Assert.Equal(200, osc.Samples);
            Assert.Throws<ArgumentOutOfRangeException>(() => new DashboardDegradationGovernor(degradeMs: 100, recoverMs: 100));
        }

        // ════════════ §6 허브 연동 ════════════

        [Fact]
        public void Hub_CrossedFeedDecision_MapsToWarningLabel_StrategyBitFlashes()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            var an = new StrategyAnomalyInterlock();
            an.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            var hub = new DashboardSnapshotHub(mask);
            long now = 1_000_000;
            var q = new QuantSignalProvider(an, hub, nowMicros: () => now, crossConfirm: true);
            OrderBookPayload Book(long mid)
            {
                var b = new OrderBookPayload { SymbolCode = 5930 };
                for (int i = 0; i < 10; i++) { b.AskPrice[i] = mid + 100 * (i + 1); b.BidPrice[i] = mid - 100 * i; b.AskQty[i] = 100; b.BidQty[i] = 900; }
                return b;
            }
            var k = Book(70_000); var hi = Book(70_600);
            q.OnBook(in k, QuantSignalProvider.FeedKiwoom);
            for (int i = 0; i < 3; i++) { q.OnBook(in hi, QuantSignalProvider.FeedKis); now += 1_000; }          // 꼬임 지속 = 비트 27
            hub.Pump();
            var f = hub.Acquire()!;
            var crossed = f.Decisions.Take(f.DecisionCount).First(d => d.Reason == (int)QuantReason.CrossedFeed);
            Assert.Equal("키움↔KIS 호가 꼬임", SignalLabelMapper.Reason(crossed.Reason));
            Assert.Equal("warn", SignalLabelMapper.BadgeClass(crossed.Kind, crossed.ScaleBp, crossed.Reason));
            Assert.True(SignalLabelMapper.HasFlash(f.Mask));                                                // 비트 27 격발 = 점멸
            hub.Release(f);
        }

        // ════════════ §7 래칫 · 순수성 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void Ratchet_Purity_ViewWiring_FiveBuilds()
        {
            var host = typeof(CanaryHealthSentinel).Assembly;
            var r = PureShellFitnessSentinel.Scan(host);
            Assert.True(r.WithinRatchet && r.HostCoupledUi <= 40, $"coupled {r.HostCoupledUi}");          // ★UI 고도화 후에도 결합 UI ≤ 40
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            string root = d!.FullName;
            foreach (var file in new[] { "SignalLabelMapper.cs", "DashboardRenderHelpers.cs" })
            {
                string src = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Helpers", file));
                Assert.DoesNotContain("StockManager.", src);                                                   // RCL 엔진 참조 0
                Assert.DoesNotContain("$\"", src);                                                            // 문자열 보간 0
            }
            string view = File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "Pages", "Trading", "PaperTradingDashboardView.razor"));
            Assert.Equal(2, view.Split('\n').Count(l => l.TrimStart().StartsWith("@inject ")));                // 포트 + 렌더 거버너만
            Assert.Contains("@SignalLabelMapper.Reason(d.Reason)", view);
            Assert.Contains("SignalLabelMapper.Interlock(bit)", view);
            Assert.Contains("SparklineWriter.Write(", view);
            Assert.Contains("_degrade.Observe(", view);
            Assert.DoesNotContain("$\"", view);
            Assert.DoesNotContain("@inject", File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "Pages", "PaperTradingDashboard.razor")));
            Assert.Contains("@using MS2E.UI.Portable.Helpers", File.ReadAllText(Path.Combine(root, "MS2E.UI.Portable", "_Imports.razor")));
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
        }
    }
}
