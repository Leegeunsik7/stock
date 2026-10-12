// ============================================================================
// ProductionDryRunRoutingTests — [드라이런 · 추천 A] 키움 + KIS 2 브로커 분할 라우팅 통합 모의 가동(MultiBrokerDryRunHarness)
//
//  §1 승인 가동(이중키 on · 가상 브로커만): 확신 ≥ 0.85 매수 시그널 → 1~3호가 × 한도(60/40) 분할 → 두 브로커 송출 · 자식 합 = min(부모 · 깊이 · 한도)
//     · 체결 수렴 반납 · 미승인 도달 0 · 구간 계측(시그널→게이트→출구) 1회씩.
//  §2 확신 게이트: 균형 호가(확신 미달) · 방향 불일치 = 전방 거부 · 송출 0.
//  §3 불균형 차단기 50ms(호스트 Routing.ImbalanceMs 하한): 키움 체결 · KIS 무체결 49ms = 취소 0 / 51ms = KIS 잔여 전량 취소 1회 + 비트 23 ·
//     이후 주문 = 인터락 veto(송출 0).
//  §4 ★liveArmed:false: 이중키 off(둘 다 · 한쪽만) = 디스패처 차단 · 센티널 도달 0 / ★상류 오작동 주입(디스패처 강제 무장) = 센티널이 첫 자식에서
//     차단(실 브로커 전달 0) · EgressBypass sticky · 동결 · 미송출 형제 회수 · 이후 송출 0.
//  §5 TOCTOU: 형제 송출 사이 인터락 격발 → 다음 자식은 센티널 집계어 veto(전달 0) · 분할 회로 Disarm.
//  §6 퍼징: 2,000 사이클 무작위(이중키 · 한도 · 시그널 세기 · 체결 보류 · 시간 · 재무장) — 미승인 도달 0 · 센티널 전달 = 가상 브로커 도달 ·
//     키 off 사이클 송출 0 · 사이클 송출 주수 ≤ 사이클 제출 주수(No Over-Dispatch).
//  §7 δ=0: 호가 → 시그널 → 제안 → 드레인(게이트 · 분할 · 자식 2+ 송출 · 센티널) → 체결 → 대사 · 매트릭스 — 정상 상태 0B.
//  §8 [방향 A] OrderPathStageProfiler 결정론(주입 시계) · 축출 · 고아 출구 · δ=0.
//  §9 결선 · 격리: 하네스 객체 그래프에 실 브로커 형식 0 · 호스트 상수(확신 8,500bp · 불균형 하한 50ms · 센티널 brokerMap) · 오중 빌드 구성.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Engines;
using StockManager.Simulation;
using StockManager.Telemetry;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class ProductionDryRunRoutingTests
    {
        private const long Sym = 5930, Mid = 70_000, Tick = 100;
        private readonly ITestOutputHelper _out;
        public ProductionDryRunRoutingTests(ITestOutputHelper o) => _out = o;

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

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        /// <summary>매수 쪽으로 기우는 호가 k 단계(QuantSignalStrategyTests.Tilt 와 동일 형상 · 1~5호가 950/50 고정 · 6~10호가로 10호가 불균형 상승).</summary>
        private static void Tilt(MultiBrokerDryRunHarness h, int k)
        {
            double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
            long bid10 = (long)Math.Round(5_000 * (1 + imb10)), ask10 = 10_000 - bid10;
            h.OnBook(Sym, Mid, 950, 50, (bid10 - 4_750) / 5, (ask10 - 250) / 5);
        }

        private static void StrongBuy(MultiBrokerDryRunHarness h) { for (int k = 0; k < 6; k++) Tilt(h, k); }

        private static MultiBrokerDryRunHarness Armed(long kiwoomCap = 60, long kisCap = 40)
        {
            var h = new MultiBrokerDryRunHarness();
            h.SetPhysicalKeys(true, true);
            h.SetCaps(kiwoomCap, kisCap);
            StrongBuy(h);
            return h;
        }

        // ════════════ §1 승인 가동 ════════════

        [Fact]
        public void Sanctioned_StrongSignal_SplitsAcrossBothBrokers_ConvergesOnFills_StagesProfiled()
        {
            using var h = Armed();
            Assert.True(h.Signals.TryGetSignal(Sym, out var s) && s.Side == 1 && s.Confidence >= 0.85, $"확신 {s.Confidence:F3}");
            long id = h.SubmitFromSignal(Sym, OrderSide.Buy, 100, Mid + 3 * Tick);
            Assert.True(id > 0);
            h.Pump();

            Assert.Equal(60, h.Kiwoom.SharesPlaced);                                          // 한도 비례(60:40)
            Assert.Equal(40, h.Kis.SharesPlaced);
            Assert.True(h.Kiwoom.Placed >= 1 && h.Kis.Placed >= 1);
            Assert.Equal(1, h.Worker.SplitParents);
            Assert.Equal(h.InnerPlaces, h.Worker.ChildOrdersSent);
            Assert.Equal(0, h.UnsanctionedPlaces);
            Assert.Equal(h.InnerPlaces, h.KiwoomSentinel.Forwarded + h.KisSentinel.Forwarded);
            Assert.Equal(0, h.Interlocks.Mask);
            Assert.Equal(1, h.Matrix.ReleasedGroups);                                          // 전량 체결 → AllFilled 반납(같은 Pump)

            Assert.Equal(1, h.Stages.Stage(OrderPathStage.SignalToGate).Count);
            Assert.Equal(1, h.Stages.Stage(OrderPathStage.GateToEgress).Count);
            Assert.Equal(1, h.Stages.Stage(OrderPathStage.SignalToEgress).Count);
            Assert.Equal(h.InnerPlaces, h.Stages.EgressForwarded);
            Assert.Equal(0, h.Stages.EgressOrphans);
            var all = h.Stages.Stage(OrderPathStage.SignalToEgress);
            Assert.True(all.MaxNanos >= h.Stages.Stage(OrderPathStage.SignalToGate).MaxNanos);
        }

        [Fact]
        public void Sanctioned_DepthBelowParent_SendsOnlyFillableDepth_NoOverDispatch()
        {
            using var h = Armed(kiwoomCap: 1_000, kisCap: 1_000);
            h.SubmitFromSignal(Sym, OrderSide.Buy, 1_000, Mid + Tick);                        // 지정가 = 매도1호가 → 1단계 잔량 50주만
            h.Pump();
            Assert.Equal(50, h.Kiwoom.SharesPlaced + h.Kis.SharesPlaced);
        }

        // ════════════ §2 확신 게이트 ════════════

        [Fact]
        public void SignalGate_LowConfidence_Or_SideMismatch_ForwardRejected_ZeroPlaces()
        {
            using var h = new MultiBrokerDryRunHarness();
            h.SetPhysicalKeys(true, true);
            h.SetCaps(60, 40);
            for (int i = 0; i < 6; i++) h.OnBook(Sym, Mid, 500, 500);                         // 균형 = 확신 0
            Assert.Equal(0, h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + Tick));           // 전략은 발행 안 함
            h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + Tick, force: true);              // 강제 발행 → 라우터 전방 거부
            h.Pump();
            Assert.Equal(0, h.InnerPlaces);
            Assert.Equal(1, h.SignalRejects);

            StrongBuy(h);
            h.SubmitFromSignal(Sym, OrderSide.Sell, 10, Mid, force: true);                     // 강한 매수 시그널인데 매도 = 방향 불일치
            h.Pump();
            Assert.Equal(0, h.InnerPlaces);
            Assert.Equal(2, h.SignalRejects);
            Assert.Equal(0, h.Stages.Stage(OrderPathStage.SignalToGate).Count);               // 게이트 승인 0
        }

        // ════════════ §3 불균형 차단기 50ms ════════════

        [Fact]
        public void Imbalance50ms_KisLagging_CancelsKisRemainderOnce_Bit23_ThenVetoes()
        {
            using var h = Armed();
            h.Kis.HoldFills = true;
            h.SubmitFromSignal(Sym, OrderSide.Buy, 100, Mid + 3 * Tick);
            h.Pump();                                                                          // 키움 체결 · KIS 보류
            long kisChildren = h.Kis.Placed;
            Assert.True(kisChildren >= 1);
            h.Advance(49_000); h.Pump();
            Assert.Equal(0, h.Kis.Cancels);                                                    // 임계 이내 = 취소 0
            h.Advance(2_000); h.Pump();
            Assert.Equal(kisChildren, h.Kis.Cancels);                                          // ★51ms = KIS 잔여 전량 취소
            Assert.Equal(0, h.Kiwoom.Cancels);                                                 // 이미 체결된 쪽은 취소 0
            Assert.Equal(1, h.Matrix.ImbalanceTrips);
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.ChildSlotRejection));
            h.Advance(10_000); h.Pump();
            Assert.Equal(kisChildren, h.Kis.Cancels);                                          // 같은 자식 재취소 0

            long before = h.InnerPlaces;
            StrongBuy(h);
            h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + 3 * Tick);
            h.Pump();
            Assert.Equal(before, h.InnerPlaces);                                               // 비트 23 = 송출 직전 veto
            Assert.True(h.Worker.InterlockVetoes >= 1);
            Assert.Equal(0, h.UnsanctionedPlaces);
        }

        // ════════════ §4 liveArmed:false ════════════

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void KeysOff_DispatcherGatesOut_SentinelNeverReached(bool valve, bool liveArming)
        {
            using var h = Armed();
            h.SetPhysicalKeys(valve, liveArming);
            h.SubmitFromSignal(Sym, OrderSide.Buy, 100, Mid + 3 * Tick);
            h.Pump();
            Assert.Equal(0, h.InnerPlaces);
            Assert.Equal(1, h.Dispatcher.GatedOut);
            Assert.Equal(0, h.KiwoomSentinel.BypassAttempts + h.KisSentinel.BypassAttempts);
            Assert.Equal(0, h.Freezes);
        }

        [Fact]
        public void UpstreamFault_KeysOff_SentinelBlocksFirstChild_ZeroDelivered_StickyDisarm_Freeze()
        {
            using var h = Armed();
            h.SetPhysicalKeys(false, false);
            h.InjectUpstreamFault(true);                                                       // 디스패처가 무장으로 오판(상류 전부 오작동)
            h.SubmitFromSignal(Sym, OrderSide.Buy, 100, Mid + 3 * Tick);
            h.Pump();

            Assert.Equal(0, h.InnerPlaces);                                                    // ★가상(=실 자리) 브로커 전달 0
            Assert.Equal(0, h.UnsanctionedPlaces);
            Assert.Equal(1, h.KiwoomSentinel.BypassAttempts);                                  // 첫 자식에서 차단 → 이후 형제는 미송출
            Assert.Equal(0, h.KisSentinel.BypassAttempts);
            Assert.True(h.Worker.ChildReclaimed >= 1);
            Assert.True(h.KiwoomSentinel.Tripped);
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.EgressBypass));
            Assert.True(h.Freezes >= 1);
            Assert.Equal(GatingMode.EmergencyFreeze, h.Attestation.Mode);
            Assert.Equal(1, h.Stages.EgressVetoed);

            h.SetPhysicalKeys(true, true);                                                     // 키를 다시 꽂아도 동결 · sticky — 송출 0
            StrongBuy(h);
            h.SubmitFromSignal(Sym, OrderSide.Buy, 10, Mid + 3 * Tick);
            h.Pump();
            Assert.Equal(0, h.InnerPlaces);
        }

        // ════════════ §5 TOCTOU ════════════

        [Fact]
        public void InterlockTripsBetweenSiblings_NextChildVetoedAtSentinel_ZeroLeak()
        {
            using var h = Armed();
            h.Kiwoom.AfterPlace = () => h.Anomaly.Trip(StrategyAnomalyKind.ComputeStall);   // 첫 자식 송출 직후 비트 27 격발
            h.SubmitFromSignal(Sym, OrderSide.Buy, 100, Mid + 3 * Tick);
            h.Pump();

            Assert.True(h.Kiwoom.Placed >= 1);
            Assert.Equal(0, h.Kis.Placed);                                                     // ★다음 자식(KIS)은 센티널 집계어 veto
            Assert.True(h.KisSentinel.InterlockVetoes + h.KiwoomSentinel.InterlockVetoes >= 1);
            Assert.Equal(0, h.UnsanctionedPlaces);
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.ChildSlotRejection));     // 불완전 분할 = 분할 회로 Disarm
            Assert.Equal(GatingMode.EmergencyFreeze, h.Attestation.Mode);                       // 자식 거부 = Mission-A 벽
        }

        // ════════════ §6 퍼징 ════════════

        [Fact]
        public void Fuzz2000Cycles_NoUnsanctionedPlace_ForwardedEqualsDelivered_NoOverDispatch()
        {
            using var h = new MultiBrokerDryRunHarness();
            var rng = new Random(20261012);
            long submittedShares = 0, keysOffCycles = 0, sendingCycles = 0;
            for (int c = 0; c < 2_000; c++)
            {
                bool valve = rng.Next(4) != 0, arming = rng.Next(4) != 0;
                h.SetPhysicalKeys(valve, arming);
                h.SetCaps(rng.Next(0, 80), rng.Next(0, 80), rng.Next(10) != 0, rng.Next(10) != 0);
                h.Kis.HoldFills = rng.Next(8) == 0;
                h.Kiwoom.HoldFills = rng.Next(16) == 0;
                if (rng.Next(3) == 0) StrongBuy(h); else h.OnBook(Sym, Mid, rng.Next(1, 1_000), rng.Next(1, 1_000));
                if (valve && arming && rng.Next(20) == 0) { h.Matrix.TryRearm(true, true); h.Anomaly.TryRearm(true, true); }

                long placesBefore = h.InnerPlaces, sharesBefore = h.Kiwoom.SharesPlaced + h.Kis.SharesPlaced;
                long shares = rng.Next(1, 200);
                long id = h.SubmitFromSignal(Sym, rng.Next(5) == 0 ? OrderSide.Sell : OrderSide.Buy, shares, Mid + rng.Next(0, 4) * Tick, force: rng.Next(4) == 0);
                if (id > 0) submittedShares += shares;
                h.Pump();
                h.Advance(rng.Next(1_000, 80_000));

                Assert.Equal(0, h.UnsanctionedPlaces);
                Assert.Equal(h.InnerPlaces, h.KiwoomSentinel.Forwarded + h.KisSentinel.Forwarded);
                long sentShares = h.Kiwoom.SharesPlaced + h.Kis.SharesPlaced - sharesBefore;
                Assert.True(sentShares <= (id > 0 ? shares : 0), $"cycle {c}: 송출 {sentShares} > 제출 {(id > 0 ? shares : 0)}");
                if (!(valve && arming)) { keysOffCycles++; Assert.Equal(placesBefore, h.InnerPlaces); }
                else if (h.InnerPlaces > placesBefore) sendingCycles++;
                if (h.Attestation.Mode == GatingMode.EmergencyFreeze) break;                   // 동결 = 이후 송출 0(별도 증명) · 루프 종료
            }
            Assert.True(keysOffCycles > 100, "키 off 사이클 " + keysOffCycles);
            Assert.True(sendingCycles > 10, "송출 사이클 " + sendingCycles);                  // 비공허성: 승인 경로가 실제로 송출
            Assert.Equal(0, h.Matrix.Overfills);
        }

        // ════════════ §7 δ=0 ════════════

        [Fact]
        public void HotPath_BookSignalSplitTwoBrokersFillReconcile_ZeroAlloc()
        {
            // 같은 호가를 반복하면 마이크로 추세(Δ10호가 불균형 EWMA)가 0 으로 감쇠해 확신이 0.85 아래로 내려간다(정적 imb5 0.9 → 점수 0.54).
            // 이 테스트의 목적은 '송출 경로 할당 0' — 게이트는 매 주문 평가하되 임계를 0.5 로 둬 송출이 실제로 일어나게 한다(비공허성 단언).
            using var h = new MultiBrokerDryRunHarness(confidenceThreshold: 0.5);
            h.SetPhysicalKeys(true, true);
            h.SetCaps(60, 40);
            StrongBuy(h);
            void Cycle()
            {
                Tilt(h, 5);
                h.SubmitFromSignal(Sym, OrderSide.Buy, 100, Mid + 3 * Tick);
                h.Pump();
                h.Advance(1_000);
            }
            for (int i = 0; i < 64; i++) Cycle();                                              // 워밍업(심볼 문자열 캐시 · JIT 티어)
            long sent0 = h.Worker.ChildOrdersSent;
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 100; i++) Cycle(); }));
            Assert.True(h.Worker.ChildOrdersSent - sent0 >= 200, "자식 송출 " + (h.Worker.ChildOrdersSent - sent0));   // 비공허성: 사이클당 2+ 자식
            foreach (var st in new[] { OrderPathStage.SignalToGate, OrderPathStage.GateToEgress, OrderPathStage.SignalToEgress })
            {
                var d = h.Stages.Stage(st);
                _out.WriteLine($"[방향 A] {st}: n={d.Count} mean={d.MeanNanos / 1000.0:F1}µs p50≤{d.P50Nanos / 1000.0:F1}µs p99≤{d.P99Nanos / 1000.0:F1}µs max={d.MaxNanos / 1000.0:F1}µs");
            }
            Assert.Equal(0, h.UnsanctionedPlaces);
            Assert.Equal(0, h.Interlocks.Mask);
        }

        // ════════════ §8 [방향 A] 구간 계측기 ════════════

        [Fact]
        public void StageProfiler_Deterministic_Evict_Orphan_ZeroAlloc()
        {
            long t = 0;
            var p = new OrderPathStageProfiler(16, () => t, timestampFrequency: 1_000_000_000);   // 1 tick = 1ns
            p.BeginOrder(1, 0);
            t = 100; p.MarkGate(1);
            p.MarkEgress(150, forwarded: true);                                                   // 첫 자식 출구 진입 150 → S1 = 50
            t = 300; p.MarkEgress(210, forwarded: false);
            p.EndOrder();                                                                          // S2 = 마지막 판정 300 − 시그널 0
            Assert.Equal(100, p.Stage(OrderPathStage.SignalToGate).MaxNanos);
            Assert.Equal(50, p.Stage(OrderPathStage.GateToEgress).MaxNanos);
            Assert.Equal(300, p.Stage(OrderPathStage.SignalToEgress).MaxNanos);
            Assert.Equal((1L, 1L), (p.EgressForwarded, p.EgressVetoed));

            p.MarkEgress(t, true);                                                                 // 현재 부모 없음
            Assert.Equal(1, p.EgressOrphans);
            p.MarkGate(99);                                                                        // 시작 안 된 주문
            Assert.Equal(1, p.UnknownGate);
            p.BeginOrder(2, 0); p.BeginOrder(18, 0);                                               // 같은 슬롯(16 mod) = 축출
            Assert.Equal(1, p.Evicted);
            Assert.Throws<ArgumentOutOfRangeException>(() => new OrderPathStageProfiler(10));

            var q = new OrderPathStageProfiler(1024);
            q.BeginOrder(1, q.Timestamp()); q.MarkGate(1); q.MarkEgress(q.Timestamp(), true); q.EndOrder(); q.Stage(OrderPathStage.SignalToEgress);
            Assert.Equal(0, MinAllocated(() =>
            {
                for (long id = 2; id < 2_000; id++) { q.BeginOrder(id, q.Timestamp()); q.MarkGate(id); q.MarkEgress(q.Timestamp(), true); q.MarkEgress(q.Timestamp(), false); q.EndOrder(); }
                q.Stage(OrderPathStage.SignalToGate); q.Stage(OrderPathStage.GateToEgress); q.Stage(OrderPathStage.SignalToEgress);
            }));
            var d = q.Stage(OrderPathStage.SignalToEgress);
            Assert.True(d.Count >= 1_999 && d.P50Nanos <= d.P99Nanos && d.P99Nanos <= d.MaxNanos);
        }

        // ════════════ §9 결선 · 격리 ════════════

        [Fact]
        public void Harness_HasNoLiveBrokerTypes_HostConstants_FiveBuildConfig()
        {
            using var h = Armed();
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var forbidden = new[] { "KisLiveBroker", "ExecutionBridgeBroker", "KiwoomBridgeClient", "ProductionKiwoomApi", "KiwoomBridgeLink" };
            void Walk(object? o, int depth)
            {
                if (o is null || depth > 6 || o is string || o.GetType().IsPrimitive || !seen.Add(o)) return;
                Assert.DoesNotContain(o.GetType().Name, forbidden);
                if (o is IBroker b) Assert.False(b.IsLive, b.Name);
                if (o is Array arr) { foreach (var e in arr) Walk(e, depth + 1); return; }
                for (var t = o.GetType(); t is not null && t != typeof(object); t = t.BaseType)
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                        if (!f.FieldType.IsValueType || f.FieldType.IsGenericType) Walk(f.GetValue(o), depth + 1);
            }
            Walk(h, 0);
            Assert.True(seen.OfType<IBroker>().Count() >= 6);                                 // 가상 2 · 센티널 2 · 출구 계측 2

            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("cfg.GetInt(\"Strategy.ConfidenceThresholdBp\", 8_500,", form);     // 확신 0.85
            Assert.Contains("cfg.GetInt(\"Routing.ImbalanceMs\", 500, 50,", form);              // 불균형 기본 500ms · 하한 50ms
            Assert.Contains("var brokerMap = new IBroker?[] { egressSentinel, kisEgress };", form);   // 두 브로커 모두 센티널 경유
            Assert.Contains("interlockBlocked: () => interlockMask.Blocked", form);
            Assert.Equal(QuantSignalProvider.DefaultConfidenceThreshold, h.Signals.ConfidenceThreshold);

            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
        }
    }
}
