// ============================================================================
// Phase94UiLiberationTests — v10.1.0 Phase 94-A: UI Liberation 인프라 적대적 검증(100% 단언)
//   ⚡ UniversalUiPortRegistry: O(1)/δ=0 조회·fail-closed·Seal 읽기전용
//   🎛️ StateAwareRenderGovernor: Normal 무제한·Frozen 하드 스로틀·δ=0
//   🧵 UiEventBoundaryBroker(Think-Beyond): δ=0 발행/배수·drop-oldest·poll(델리게이트 0)
//   🧱 UiAssemblyPurity: MS2E.UI.Portable 이 엔진 어셈블리를 참조하지 않음(0 Engine Ref 아크 불변식)
// ============================================================================
using System;
using System.Linq;
using System.Reflection;
using MS2E.UI.Portable.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class Phase94UiLiberationTests
    {
        private interface IFooPort { int V(); }
        private sealed class FooPort : IFooPort { public int V() => 42; }
        private interface IBarPort { }

        // ══ ⚡ UniversalUiPortRegistry ══════════════════════════════════════
        [Fact]
        public void Registry_Register_Get_FailClosed_Seal()
        {
            var r = new UniversalUiPortRegistry();
            var foo = new FooPort();
            r.Register<IFooPort>(foo);
            Assert.Equal(1, r.Count);
            Assert.Same(foo, r.Get<IFooPort>());
            Assert.True(r.TryGet<IFooPort>(out var got) && ReferenceEquals(got, foo));
            Assert.Contains("42", r.Get<IFooPort>()!.V().ToString());

            Assert.Null(r.Get<IBarPort>());                 // ★미등록 → null(fail-closed)
            Assert.False(r.TryGet<IBarPort>(out _));        // ★미등록 → false

            r.Seal();
            Assert.True(r.IsSealed);
            Assert.Throws<InvalidOperationException>(() => r.Register<IBarPort>(new BarStub()));   // ★봉인 후 등록 거부
            Assert.Same(foo, r.Get<IFooPort>());            // 봉인 후에도 조회 정상
        }
        private sealed class BarStub : IBarPort { }

        [Fact]
        public void Registry_Get_ZeroAllocation()
        {
            var r = new UniversalUiPortRegistry();
            r.Register<IFooPort>(new FooPort());
            r.Seal();
            for (int i = 0; i < 50_000; i++) { _ = r.Get<IFooPort>(); }
            long Measure()
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                IFooPort? acc = null;
                for (int i = 0; i < 500_000; i++) { acc = r.Get<IFooPort>(); }
                GC.KeepAlive(acc);
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★타입키 조회 핫패스 힙 0
        }

        // ══ 🎛️ StateAwareRenderGovernor ════════════════════════════════════
        [Fact]
        public void Governor_Normal_Always_Frozen_HardThrottle()
        {
            var g = new StateAwareRenderGovernor();
            long slot = 0;
            // Normal: 항상 허용
            Assert.True(g.ShouldRender(ref slot, 1_000_000));
            Assert.True(g.ShouldRender(ref slot, 1_000_001));
            Assert.True(g.ShouldRender(ref slot, 1_000_002));

            // Frozen: 250ms 하드 스로틀
            g.SetFrozen(true);
            slot = 0;
            Assert.True(g.ShouldRender(ref slot, 1_000_000));        // 첫 렌더 허용(gap 충분)
            Assert.False(g.ShouldRender(ref slot, 1_100_000));       // +100ms < 250ms → 스로틀
            Assert.False(g.ShouldRender(ref slot, 1_200_000));       // +200ms < 250ms → 스로틀
            Assert.True(g.ShouldRender(ref slot, 1_260_000));        // +260ms ≥ 250ms → 허용
            Assert.True(g.Throttled >= 2);

            // 동결 해제 → 다시 무제한
            g.SetFrozen(false);
            slot = 0;
            Assert.True(g.ShouldRender(ref slot, 2_000_000));
            Assert.True(g.ShouldRender(ref slot, 2_000_001));

            // High 부하: 100ms 스로틀
            g.SetLoad(UiLoadLevel.High);
            slot = 0;
            Assert.True(g.ShouldRender(ref slot, 3_000_000));
            Assert.False(g.ShouldRender(ref slot, 3_050_000));       // +50ms < 100ms → 스로틀
            Assert.True(g.ShouldRender(ref slot, 3_110_000));        // +110ms ≥ 100ms → 허용
        }

        [Fact]
        public void Governor_ShouldRender_ZeroAllocation()
        {
            var g = new StateAwareRenderGovernor();
            g.SetLoad(UiLoadLevel.High);
            for (int i = 0; i < 50_000; i++) { long s = 0; _ = g.ShouldRender(ref s, i); }
            long Measure()
            {
                long slot = 0, now = 0;
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 500_000; i++) { now += 200_000; _ = g.ShouldRender(ref slot, now); }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★렌더 판정 핫패스 힙 0
        }

        // ══ 🧵 UiEventBoundaryBroker (Think-Beyond) ═════════════════════════
        [Fact]
        public void Broker_Publish_Drain_Roundtrip()
        {
            var brk = new UiEventBoundaryBroker(64);
            for (int i = 0; i < 5; i++) brk.Publish(kind: 7, a: i, b: i * 10);
            Span<UiBoundaryEvent> dest = stackalloc UiBoundaryEvent[16];
            int n = brk.Drain(dest);
            Assert.Equal(5, n);
            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(7L, dest[i].Kind);
                Assert.Equal((long)i, dest[i].A);
                Assert.Equal((long)i * 10, dest[i].B);
                Assert.Equal((long)i, dest[i].Seq);   // 단조 순번
            }
            Assert.Equal(0, brk.Drain(dest));          // 재배수 → 0(커서 전진 확인)
            Assert.Equal(5L, brk.Published);
        }

        [Fact]
        public void Broker_DropOldest_When_ConsumerLags()
        {
            var brk = new UiEventBoundaryBroker(16);   // cap 16
            for (int i = 0; i < 40; i++) brk.Publish(1, i, 0);   // cap 초과 발행(미배수)
            Span<UiBoundaryEvent> dest = stackalloc UiBoundaryEvent[64];
            int n = brk.Drain(dest);
            Assert.True(n <= 16);                      // 최대 cap 만큼만 생존
            Assert.True(brk.Dropped >= 24);            // ★40-16=24 이상 drop-oldest
            // 생존한 것은 최신 쪽(가장 큰 A) — 첫 생존 A ≥ 24
            Assert.True(dest[0].A >= 24);
        }

        [Fact]
        public void Broker_Count_Integrity_No_Double_Count_Monotonic()
        {
            var brk = new UiEventBoundaryBroker(32);
            Span<UiBoundaryEvent> dest = stackalloc UiBoundaryEvent[8];
            long lastSeq = -1L;
            for (int round = 0; round < 100; round++)
            {
                for (int i = 0; i < 7; i++) brk.Publish(1, round * 7 + i, 0);
                int n = brk.Drain(dest);
                for (int i = 0; i < n; i++)
                {
                    Assert.True(dest[i].Seq > lastSeq);   // ★단조·중복 배달(phantom/이중) 없음
                    lastSeq = dest[i].Seq;
                }
            }
            Assert.Equal(700L, brk.Published);
            Assert.True(brk.Drained + brk.Dropped <= brk.Published);   // ★이중 계수 없음(회계 항등 사수)
            Assert.Equal(699L, lastSeq);                               // 전량 순차 배달
        }

        [Fact]
        public void Broker_Publish_Drain_ZeroAllocation()
        {
            var brk = new UiEventBoundaryBroker(256);
            for (int w = 0; w < 5_000; w++) { brk.Publish(1, w, w); }
            long Measure()
            {
                Span<UiBoundaryEvent> dest = stackalloc UiBoundaryEvent[128];
                long b = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 200_000; i++) { brk.Publish(1, i, i); if ((i & 63) == 0) brk.Drain(dest); }
                return GC.GetAllocatedBytesForCurrentThread() - b;
            }
            _ = Measure();
            Assert.Equal(0L, ZeroAllocProbe.MinOf(Measure));   // ★발행/배수 핫패스 힙 0(값타입 struct 링·델리게이트 0)
        }

        // ══ 🧱 UI 어셈블리 순수성 — 0 Engine Ref 아크 불변식 ═══════════════
        [Fact]
        public void Portable_Assembly_Has_Zero_Engine_Reference()
        {
            Assembly portable = typeof(UniversalUiPortRegistry).Assembly;
            Assert.Equal("MS2E.UI.Portable", portable.GetName().Name);

            string[] engineNames = { "MS2E.StockSuite", "StockManager", "StockManager.KiwoomProxy", "MS2E.KiwoomProxyHost" };
            foreach (AssemblyName refd in portable.GetReferencedAssemblies())
            {
                string name = refd.Name ?? "";
                Assert.DoesNotContain(name, engineNames);                          // ★엔진 어셈블리 직접 참조 0
                Assert.False(name.StartsWith("StockManager", StringComparison.OrdinalIgnoreCase),
                    $"Portable 이 엔진 어셈블리 참조: {name}");
            }
        }
    }
}
