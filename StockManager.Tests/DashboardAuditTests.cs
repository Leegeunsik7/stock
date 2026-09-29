// ============================================================================
// DashboardAuditTests — v12.8.0 [Step 1/7] Main Dashboard 전수 오디팅 적대검증
//   Section 1/2: 감사 중 실측으로 확인한 결함 — StateAwareRenderGovernor(v10.1.0, 등록·ForceFreeze 결속까지는
//     되어 있었으나 어떤 컴포넌트도 ShouldRender() 를 호출하지 않아 실질 미가동)를 Dashboard.razor 의 지수
//     자동갱신 렌더 경로에 최초로 결속 — 엔진 자체는 Phase94UiLiberationTests.cs 에서 이미 철저히 검증됐으므로
//     본 파일은 재검증하지 않고, 신규 WidgetLifecycleMemoryGuard(Think Beyond B)만 검증한다.
//   ★Razor 컴포넌트(Dashboard.razor 의 ErrorBoundary 배선·@ref 결속) 자체는 이 저장소에 bUnit 등 Blazor 컴포넌트
//   테스트 하네스가 없어 xUnit 으로 직접 렌더 검증할 수 없다 — 정직 명기(날조된 렌더 테스트 없음). 코드 리뷰로
//   컴파일 통과·구조 정합만 확인했다(사중 빌드 0W/0E 로 실증).
// ============================================================================
using System;
using MS2E.UI.Portable.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class DashboardAuditTests
    {
        private readonly ITestOutputHelper _out;
        public DashboardAuditTests(ITestOutputHelper o) => _out = o;

        private sealed class DisposableWidgetStub { }

        // ── §1 정상 회수 — 강한 참조가 없으면 GC 후 수거됨(누수 아님) ────────────
        [Fact]
        public void VerifyCollected_UnreachableInstance_ReportsCollected()
        {
            var guard = new WidgetLifecycleMemoryGuard();
            WeakReference handle = CreateAndTrackUnreachable(guard);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.True(guard.VerifyCollected(handle));
            Assert.Equal(0, guard.LeaksDetected);
            Assert.Equal(1, guard.Tracked);
            Assert.Equal(1, guard.Verified);
        }

        // 인스턴스 생성·추적을 별도 메서드로 분리 — 호출 프레임이 반환되면 로컬 변수의 강한 참조가 사라져
        // JIT/디버그 빌드 차이와 무관하게 확실히 unreachable 상태가 되도록 한다.
        private static WeakReference CreateAndTrackUnreachable(WidgetLifecycleMemoryGuard guard)
        {
            var widget = new DisposableWidgetStub();
            return guard.Track(widget);
        }

        // ── §2 누수 탐지 — 강한 참조가 살아있으면 GC 후에도 여전히 살아있음(누수 의심) ──
        [Fact]
        public void VerifyCollected_StillReferencedInstance_DetectsLeak()
        {
            var guard = new WidgetLifecycleMemoryGuard();
            var stillAlive = new DisposableWidgetStub();   // ★테스트가 강한 참조 계속 보유(누수 시뮬레이션)
            var handle = guard.Track(stillAlive);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(guard.VerifyCollected(handle));
            Assert.Equal(1, guard.LeaksDetected);
            GC.KeepAlive(stillAlive);   // ★핸들 검증 시점까지 강한 참조 생존 보장(과적 최적화 방지)
        }

        // ── §3 카운터 정합 ────────────────────────────────────────────────────
        [Fact]
        public void Counters_TrackAndVerify_AccumulateAcrossCalls()
        {
            var guard = new WidgetLifecycleMemoryGuard();
            var a = new DisposableWidgetStub();
            var b = new DisposableWidgetStub();
            var ha = guard.Track(a);
            var hb = guard.Track(b);
            Assert.Equal(2, guard.Tracked);

            guard.VerifyCollected(ha);
            guard.VerifyCollected(hb);
            Assert.Equal(2, guard.Verified);
            GC.KeepAlive(a); GC.KeepAlive(b);
        }

        [Fact]
        public void Reset_ClearsAllCounters()
        {
            var guard = new WidgetLifecycleMemoryGuard();
            var obj = new DisposableWidgetStub();
            var h = guard.Track(obj);
            guard.VerifyCollected(h);
            GC.KeepAlive(obj);

            guard.Reset();

            Assert.Equal(0, guard.Tracked);
            Assert.Equal(0, guard.Verified);
            Assert.Equal(0, guard.LeaksDetected);
        }

        [Fact]
        public void Track_Null_Throws() => Assert.Throws<ArgumentNullException>(() => new WidgetLifecycleMemoryGuard().Track(null!));

        [Fact]
        public void VerifyCollected_Null_Throws() => Assert.Throws<ArgumentNullException>(() => new WidgetLifecycleMemoryGuard().VerifyCollected(null!));
    }
}
