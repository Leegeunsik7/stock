// ============================================================================
// HostSignalGateIntegrationTests — [v12.18.2-DEV · 추천 A] 호스트 시그널 게이트 실배선 · 런타임 토글 · 관측 모드 비트 27
//
//  §1 관측 모드(호스트 기본 · 게이트 꺼짐): 드레인 제안 관측 = 무장 판정 '이전' — 키 off 에서도 승인 폭주 = 비트 27(ModelBreakdown) ·
//     수량 0 제안 = 비트 27(NonPositiveQty) · 송출 0 · 그림자 거부 계수(켰다면 거부) · 관측 예외 = 계수만(처분 불변).
//  §2 강제 모드 Zero Leak Under Choppy: 휩소 호가열 · 키 on — 라우터 승인 0 · 자식 그룹 개설(대사 원장) 0 · 송출 0 · 미송출 롤백 0 ·
//     관측 승인 = 브레이커 승인(이중 계수 0) · 국면 거부 판단 근거 사출(거부 · 100ms 상한) · 한글 라벨.
//  §3 런타임 토글: 켜기 즉시 · 끄기는 물리 키 둘 다 꺼졌을 때만 · 판독 예외 = 엄격 쪽 · 토글 직후 같은 호가열 누출 → 0.
//  §4 [방향 A] 승인 창 O(1) 러닝 합 = 기준 모델(60초 전수 합) 무작위 시각 열 전수 일치 · δ=0 · 초 경계 소거 수.
//  §5 [방향 B] 히스테리시스 32 · 플리커 감쇄: 추세 ↔ 노이즈 깜빡임에서 임계 하강 호가 0(16 은 하강 발생 — 실측 대조).
//  §6 δ=0: OnProposal · AdmitSignal(관측 재사용 경로) · 드레인(관측자 결선).
//  §7 UI 기입 가드 · 관제: Strategy.* = UI 기입 거부(호스트 안전 네임스페이스) · 허브 게이트 모드/계수 · 회귀(결선 소스 · 오중 빌드 · 래칫 · 인터락 30).
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Helpers;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Simulation;
using Xunit;
using Xunit.Abstractions;
using static StockManager.Tests.ZeroAllocProbe;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class HostSignalGateIntegrationTests
    {
        private const long Sym = 5930, Mid = 70_000, Tick = 100;
        private readonly ITestOutputHelper _out;
        public HostSignalGateIntegrationTests(ITestOutputHelper o) => _out = o;

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        // 고확신 램프(VolatilityCalibrationTests.Ramp 와 같은 형상)
        private static void Ramp(MultiBrokerDryRunHarness h, int sign, long mid)
        {
            for (int k = 0; k < 6; k++)
            {
                double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
                long strong10 = (long)Math.Round(5_000 * (1 + imb10)), weak10 = 10_000 - strong10;
                long deepStrong = (strong10 - 4_750) / 5, deepWeak = (weak10 - 250) / 5;
                if (sign > 0) h.OnBook(Sym, mid, 950, 50, deepStrong, deepWeak);
                else h.OnBook(Sym, mid, 50, 950, deepWeak, deepStrong);
                h.Advance(1_000);
            }
        }

        /// <summary>휩소(매수 ↔ 매도 램프 교대) 또는 추세(매수 램프 · +1틱) 사이클 — 전략 = 고정 0.85 신호를 보면 제안. 반환 = 제안 수.</summary>
        private static int Drive(MultiBrokerDryRunHarness h, bool choppy, int cycles, int startCycle = 0)
        {
            int proposed = 0;
            for (int c = startCycle; c < startCycle + cycles; c++)
            {
                int sign = choppy && c % 2 == 1 ? -1 : 1;
                long mid = choppy ? Mid + (c % 2 == 0 ? Tick : 0) : Mid + c * Tick;
                Ramp(h, sign, mid);
                if (h.Signals.TryGetSignal(Sym, out var sig) && sig.Confidence >= 0.85)
                {
                    var side = sig.Side > 0 ? OrderSide.Buy : OrderSide.Sell;
                    if (h.SubmitFromSignal(Sym, side, 10, side == OrderSide.Buy ? mid + Tick : mid) != 0) proposed++;
                }
                h.Pump();
                h.Advance(50_000);
            }
            return proposed;
        }

        // ════════════ §1 관측 모드(게이트 꺼짐 · 호스트 기본) ════════════

        [Fact]
        public void ObserveMode_KeysOff_ChurnBreakdown_TripsBit27_ZeroPlaces()
        {
            using var h = new MultiBrokerDryRunHarness(churnSoftPerMinute: 3, churnHardPerMinute: 6, signalGateEnforced: false);
            h.SetCaps(60, 40);                                                                // 물리 키 off(liveArmed:false 기본)
            Assert.False(h.Gate.SignalGateEnabled);
            int proposed = Drive(h, choppy: false, cycles: 12);
            var churn = h.Signals.Churn!;
            _out.WriteLine($"[관측 · 키 off] 제안 {proposed} · 관측 {h.Signals.ObservedProposals} · 관측 승인 {h.Signals.ShadowApproved} · 브레이커 승인 {churn.Approved} · 붕괴 {churn.Breakdowns} · 송출 {h.InnerPlaces} · 미무장 차단 {h.Dispatcher.GatedOut}");
            Assert.True(proposed >= 7);
            Assert.Equal(proposed, h.Signals.ObservedProposals);                              // ★무장 판정 이전 관측 = 키 off 에서도 전수
            Assert.Equal(proposed, h.Dispatcher.GatedOut);                                     // 전부 미무장 차단
            Assert.True(h.Anomaly.Tripped);
            Assert.Equal(StrategyAnomalyKind.ModelBreakdown, h.Anomaly.FirstCause);
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(0, h.InnerPlaces);
            Assert.Equal(0, h.Signals.Admitted);                                              // 라우터 게이트 미결속(관측 모드) — AdmitSignal 호출 0
        }

        [Fact]
        public void ObserveMode_ZeroQtyProposal_TripsBit27_ShadowRejects_ObserverFaultIsolated()
        {
            using (var h = new MultiBrokerDryRunHarness(signalGateEnforced: false))
            {
                h.SetCaps(60, 40);
                h.SubmitFromSignal(Sym, OrderSide.Buy, 0, Mid, force: true);                  // 수량 0 = 전략 이상
                h.Pump();
                Assert.True(h.Anomaly.Tripped);
                Assert.Equal(StrategyAnomalyKind.NonPositiveQty, h.Anomaly.FirstCause);
                Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            }
            using (var h = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator(), signalGateEnforced: false))
            {
                h.SetCaps(60, 40);
                int proposed = Drive(h, choppy: true, cycles: 40);
                _out.WriteLine($"[관측 · 휩소] 제안 {proposed} · 그림자 거부 {h.Signals.ShadowRejects}(국면 {h.Signals.ShadowRegimeRejects})");
                Assert.Equal(proposed, h.Signals.ShadowRejects);                              // 켰다면 전부 거부됐을 제안
                Assert.Equal(proposed, h.Signals.ShadowRegimeRejects);
                Assert.False(h.Anomaly.Tripped);
            }
            // 관측자 예외 = 계수만 · 처분(미무장 차단) 불변
            var ring = new OrderDispatchRing(64);
            var d = new GatedOrderDispatcher(ring, () => false) { ProposalObserver = new Throwing() };
            ring.Publish(0, Sym, OrderSide.Buy, 1, new QPrice(Mid * 100_000_000L), new QQty(10 * 100_000_000L), 1);
            Assert.Equal(1, d.Drain(_ => true));
            Assert.Equal((1L, 1L, 0L), (d.ObserverFaults, d.GatedOut, d.Forwarded));
        }

        private sealed class Throwing : IOrderProposalObserver
        {
            public void OnProposal(in OrderProposal proposal) => throw new InvalidOperationException("관측 실패");
        }

        // ════════════ §2 강제 모드 — Zero Leak Under Choppy ════════════

        [Fact]
        public void EnforceMode_Choppy_ZeroLeak_NoLedgerWrites_SingleChurnCount_RegimeDecisionStream()
        {
            using var h = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator(), churnSoftPerMinute: 1_000, churnHardPerMinute: 2_000);
            h.SetPhysicalKeys(true, true); h.SetCaps(60, 40);
            int proposed = Drive(h, choppy: true, cycles: 80);
            _out.WriteLine($"[강제 · 휩소] 제안 {proposed} · 국면 거부 {h.Signals.RejectedRegime} · 라우터 승인 {h.Gate.Admitted}/{h.Gate.SplitAdmitted} · 그룹 {h.Matrix.Opened} · 송출 {h.InnerPlaces}");
            Assert.True(proposed > 10);
            Assert.Equal(proposed, h.Signals.RejectedRegime);
            Assert.Equal(proposed, h.Gate.SignalRejects);
            Assert.Equal((0L, 0L, 0L, 0L), (h.Gate.Admitted, h.Gate.SplitAdmitted, h.Matrix.Opened, h.InnerPlaces));   // ★예약 · 자식 그룹(대사 원장) · 송출 0
            Assert.Equal(0, h.Gate.NotSentRollbacks);
            Assert.Equal(0, h.Stages.Stage(StockManager.Telemetry.OrderPathStage.SignalToGate).Count);
            Assert.Equal(0, h.UnsanctionedPlaces);

            // 추세 구간: 관측 승인 수 = 브레이커 승인 수(관측 · 강제가 같은 제안을 두 번 세지 않음)
            using var t = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator(), churnSoftPerMinute: 1_000, churnHardPerMinute: 2_000);
            t.SetPhysicalKeys(true, true); t.SetCaps(60, 40);
            Drive(t, choppy: false, cycles: 40);
            Assert.True(t.Signals.Admitted > 10);
            Assert.Equal(t.Signals.ShadowApproved, t.Signals.Churn!.Approved);
            Assert.Equal(t.Signals.Admitted, t.Signals.Churn.Allowed);

            // 판단 근거 스트림: 국면 거부 = Veto(5) · 사유 16 · 100ms 상한
            var hub = new DashboardSnapshotHub();
            long now = 1_000_000;
            var anomaly = new StrategyAnomalyInterlock();
            var q = new QuantSignalProvider(anomaly, hub, nowMicros: () => now, calibrator: new VolatilityAdaptiveCalibrator());
            q.SetGateEnforced(true);
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int i = 0; i < 10; i++) { b.BidPrice[i] = Mid - Tick * i; b.AskPrice[i] = Mid + Tick * (i + 1); b.BidQty[i] = 950; b.AskQty[i] = 50; }
            for (int k = 0; k < 6; k++)                                                        // Ramp 형상(1~5호가 950/50 · 6~10호가로 10호가 불균형 상승) — 워밍업 = 9500 요구
            {
                double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
                long strong10 = (long)Math.Round(5_000 * (1 + imb10)), weak10 = 10_000 - strong10;
                for (int i = 5; i < 10; i++) { b.BidQty[i] = (strong10 - 4_750) / 5; b.AskQty[i] = (weak10 - 250) / 5; }
                q.OnBook(in b); now += 1_000;
            }
            var p = new OrderProposal(1, 0, Sym, OrderSide.Buy, 1, new QPrice(Mid * 100_000_000L), new QQty(10 * 100_000_000L), 0);
            Assert.True(q.TryGetSignal(Sym, out var sig) && sig.Confidence >= 0.85 && sig.Confidence < 0.95, "확신 " + sig.Confidence);
            for (int i = 0; i < 10; i++) { Assert.False(q.AdmitSignal(in p)); now += 10_000; }       // 100ms 동안 10회 거부
            Assert.Equal(10, q.RejectedRegime);
            Assert.Equal(1, q.GateDecisionsPublished);                                        // 100ms 상한 — 1건
            now += 100_000; q.AdmitSignal(in p);
            Assert.Equal(2, q.GateDecisionsPublished);
            Assert.True(hub.Pump());
            var f = hub.Acquire()!;
            var gateDecisions = f.Decisions.Take(f.DecisionCount).Where(x => x.Reason == (int)QuantReason.RegimeThresholdNotMet && x.Kind == DashDecisionKind.Veto).ToArray();
            Assert.Equal(2, gateDecisions.Length);
            Assert.Equal("국면 임계 미달 — 전방 거부/관망", SignalLabelMapper.Reason(gateDecisions[0].Reason));
            hub.Release(f);
        }

        // ════════════ §3 런타임 토글 ════════════

        [Fact]
        public void RuntimeSwitch_EnableImmediate_DisableOnlyWithKeysOff_FaultsFailClosed_ToggleStopsLeak()
        {
            using var h = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator(), churnSoftPerMinute: 1_000, churnHardPerMinute: 2_000,
                                                       signalGateEnforced: false);
            h.SetPhysicalKeys(true, true); h.SetCaps(60, 40);
            bool want = false, keys = true; bool throwWant = false, throwKeys = false;
            var sw = new SignalGateRuntimeSwitch(h.Gate, h.Signals,
                desired: () => throwWant ? throw new InvalidOperationException("DB") : want,
                anyKeyOn: () => throwKeys ? throw new InvalidOperationException("DB") : keys);
            Assert.False(sw.Poll());
            Assert.Equal(SignalGateRuntimeSwitch.ModeObserve, sw.Mode);

            // 관측 모드 = 게이트 미차단 → 휩소 누출(대조)
            Drive(h, choppy: true, cycles: 40);
            long leaked = h.InnerPlaces;
            _out.WriteLine($"[토글 전 · 관측] 송출 {leaked} · 그림자 국면 거부 {h.Signals.ShadowRegimeRejects}");
            Assert.True(leaked > 10);

            want = true;
            Assert.True(sw.Poll());                                                           // ★켜기 즉시
            Assert.True(h.Signals.GateEnforced);
            Drive(h, choppy: true, cycles: 40, startCycle: 40);
            Assert.Equal(leaked, h.InnerPlaces);                                              // ★토글 이후 휩소 송출 0
            Assert.True(h.Signals.RejectedRegime > 10);

            want = false;                                                                     // 키 켜진 채 끄기 = 거부
            Assert.True(sw.Poll());
            Assert.Equal(1, sw.DisableRefused);
            throwKeys = true; keys = false;                                                   // 키 판독 불가 = 켜진 것으로
            Assert.True(sw.Poll());
            Assert.Equal(2, sw.DisableRefused);
            throwKeys = false;
            Assert.False(sw.Poll());                                                          // 키 둘 다 꺼짐 = 해제
            Assert.False(h.Signals.GateEnforced);
            Assert.Equal(1, sw.Disables);
            throwWant = true;                                                                 // 설정 판독 불가 = 켜기(엄격)
            Assert.True(sw.Poll());
            Assert.Equal((2L, 2L), (sw.Enables, sw.ReadFaults));                            // 키 판독 1 + 설정 판독 1
            Assert.Equal(0, h.UnsanctionedPlaces);
        }

        // ════════════ §4 [방향 A] O(1) 승인 창 ════════════

        [Fact]
        public void ChurnRing_RunningSum_MatchesReferenceModel_ZeroAlloc()
        {
            var cb = new SignalChurnCircuitBreaker(new StrategyAnomalyInterlock(), softPerMinute: 1_000_000, hardPerMinute: 2_000_000);
            var history = new List<long>();
            long t = 5_000_000, rng = 0x1E3779B97F4A7C15;
            for (int i = 0; i < 20_000; i++)
            {
                rng ^= rng << 13; rng ^= rng >> 7; rng ^= rng << 17;
                long r = rng & 0xFFFF;
                t += r < 60_000 ? (r % 400) * 1_000 : (r % 130) * 1_000_000;                 // 대부분 ms 간격 · 가끔 수십 초 ~ 2분 공백
                Assert.Equal(ChurnVerdict.Allowed, cb.OnApproved(t));
                history.Add(t / 1_000_000);
                long sec = t / 1_000_000, expected = 0;
                for (int k = history.Count - 1; k >= 0 && sec - history[k] < SignalChurnCircuitBreaker.WindowSeconds; k--) expected++;
                Assert.Equal(expected, cb.LastWindowCount);
            }
            Assert.True(cb.BucketEvictions > 0);
            Assert.Equal(0, cb.WindowCount(t + 61_000_000));                                  // 창 만료 판독
            _out.WriteLine($"[방향 A] 2만 승인 · 기준 모델 전수 일치 · 초 경계 소거 {cb.BucketEvictions}");
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 5_000; i++) cb.OnApproved(t += 7_000); }));
        }

        // ════════════ §5 [방향 B] 히스테리시스 · 플리커 감쇄 ════════════

        // 평균 회귀형 깜빡임: 노이즈(우위 매 장 뒤집힘 · 제자리) 사이사이 짧은 상승 · 하락 분출(우위 한쪽 지속)이 번갈아 끼고 순 이동 0.
        //  국면 후보는 분출 동안 비-횡보로 깜빡이지만 시장 전체는 횡보다 — 임계가 내려가면(< 9500) 휩소에 문이 열린다.
        private static (int Lowered, long Dampened) Flicker(int exitHold, int burst, int noise)
        {
            var c = new VolatilityAdaptiveCalibrator(exitHoldBooks: exitHold);
            var s = new CalibrationState();
            long px = Mid;
            void Noise(int n, ref int low)
            {
                for (int i = 0; i < n; i++)
                    if (c.Observe(ref s, px + (i % 2) * Tick, px + (i % 2) * Tick + Tick, i % 2 == 0 ? 900 : 100, i % 2 == 0 ? 100 : 900) < 9_500) low++;
            }
            int warm = 0;
            Noise(128, ref warm);
            Assert.Equal(MicroRegime.Choppy, s.Regime);
            int lowered = 0;
            for (int round = 0; round < 40; round++)
            {
                int dir = round % 2 == 0 ? 1 : -1;
                for (int i = 0; i < burst; i++) { px += dir * Tick; if (c.Observe(ref s, px, px + Tick, dir > 0 ? 900 : 100, dir > 0 ? 100 : 900) < 9_500) lowered++; }
                Noise(noise, ref lowered);
            }
            return (lowered, c.DampenedBooks);
        }

        [Fact]
        public void Hysteresis32_MeanRevertingFlicker_HoldsChoppy_16Leaks()
        {
            Assert.Equal(32, VolatilityAdaptiveCalibrator.DefaultExitHoldBooks);
            foreach (var (burst, noise) in new[] { (4, 8), (8, 8), (10, 14), (12, 12), (16, 16) })
            {
                var h32 = Flicker(32, burst, noise); var h16 = Flicker(16, burst, noise);
                _out.WriteLine($"[방향 B] 분출 {burst} · 노이즈 {noise} × 40: ExitHold 32 → 임계 하강 {h32.Lowered} 장(감쇄 {h32.Dampened}) · 16 → 하강 {h16.Lowered} 장");
                Assert.Equal(0, h32.Lowered);                                                // ★순 이동 0 깜빡임 = 횡보 유지(9500)
                if (burst >= 10) { Assert.True(h16.Lowered > 0, "16 대조 하강 0"); Assert.True(h32.Dampened > 0); }
            }
            // 한계(정직): 노이즈보다 훨씬 긴 분출(24 장 · 노이즈 8)은 32 로도 소수 장 임계가 내려간다 — 그 구간은 실제 추세에 가깝다.
            var longBurst = Flicker(32, 24, 8);
            _out.WriteLine($"[방향 B] 분출 24 · 노이즈 8: ExitHold 32 → 하강 {longBurst.Lowered} 장 · 16 → {Flicker(16, 24, 8).Lowered} 장");
            Assert.True(longBurst.Lowered < Flicker(16, 24, 8).Lowered);
        }

        // ════════════ §6 δ=0 ════════════

        [Fact]
        public void HotPath_ZeroAlloc_ObserveThenAdmit_And_DrainWithObserver()
        {
            var anomaly = new StrategyAnomalyInterlock();
            long now = 1_000_000;
            var churn = new SignalChurnCircuitBreaker(anomaly, 10_000_000, 20_000_000);
            var q = new QuantSignalProvider(anomaly, nowMicros: () => now, calibrator: new VolatilityAdaptiveCalibrator(), churn: churn);
            q.SetGateEnforced(true);
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int i = 0; i < 10; i++) { b.BidPrice[i] = Mid - Tick * i; b.AskPrice[i] = Mid + Tick * (i + 1); b.BidQty[i] = 950; b.AskQty[i] = 50; }
            for (int i = 0; i < 64; i++) { b.BidPrice[0] = Mid + i * Tick; b.AskPrice[0] = Mid + (i + 1) * Tick; q.OnBook(in b); now += 1_000; }
            long id = 0;
            void Cycle()
            {
                var p = new OrderProposal(++id, 0, Sym, OrderSide.Buy, 1, new QPrice(Mid * 100_000_000L), new QQty(10 * 100_000_000L), 0);
                q.OnProposal(in p); q.AdmitSignal(in p); now += 1_000;
            }
            for (int i = 0; i < 200; i++) Cycle();
            long alloc = MinAllocated(() => { for (int i = 0; i < 2_000; i++) Cycle(); });

            var ring = new OrderDispatchRing(4096);
            var d = new GatedOrderDispatcher(ring, () => false) { ProposalObserver = q };
            Func<OrderProposal, bool> send = static _ => true;
            void Burst() { for (int i = 0; i < 256; i++) ring.Publish(0, Sym, OrderSide.Buy, 1, new QPrice(Mid * 100_000_000L), new QQty(10 * 100_000_000L), now); d.Drain(send); }
            Burst();
            long drainAlloc = MinAllocated(Burst);
            _out.WriteLine($"[δ=0] 관측+강제 2,000회 {alloc}B · 드레인(관측자) 256×{drainAlloc}B · 관측 {q.ObservedProposals}");
            Assert.Equal(0, alloc);
            Assert.Equal(0, drainAlloc);
            Assert.True(q.ObservedProposals > 2_000);
        }

        // ════════════ §7 UI 기입 가드 · 관제 · 회귀 ════════════

        [Fact]
        public void UiWriteGuard_StrategyKeysRejected_HostReadsOperatorPathOnly()
        {
            foreach (var key in new[] { "Strategy.SignalGateEnabled", "Strategy.AdaptiveThreshold", "Strategy.AdaptiveMaxBp", "Strategy.ConfidenceThresholdBp",
                                        "Strategy.ChurnSoftPerMin", "Strategy.ChurnHardPerMin", "Dispatch.ValveWired", "Dispatch.LiveArmingWired" })
                Assert.False(UiSettingWriteGuard.IsUiWritable(key), key);                     // 순수 UI = 게이트 · 임계 · 무장 키 기입 불가
            Assert.True(UiSettingWriteGuard.IsUiWritable("Regime.Lookback"));                 // 기존 UI 네임스페이스는 그대로
            string host = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("ringDispatcher.ProposalObserver = quantSignals;", host);
            Assert.Contains("desired: () => cfg.GetBool(\"Strategy.SignalGateEnabled\", false),", host);
            Assert.Contains("anyKeyOn: () => cfg.GetBool(\"Dispatch.ValveWired\", false) || cfg.GetBool(\"Dispatch.LiveArmingWired\", false),", host);   // v12.18.3: 뒤에 상향 전용 판독 인자
            Assert.Contains("dashHub.AttachSignalGate(signalGateSwitch);", host);
            Assert.DoesNotContain("routerGate.EnableSignalGate(quantSignals)", host);         // 강제 결속은 스위치만
        }

        [Fact]
        public void Hub_GateMode_Counters_Labels()
        {
            long ticks = 1;
            var hub0 = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            Assert.True(hub0.Pump());
            var f0 = hub0.Acquire()!;
            Assert.Equal(DashGateMode.Unbound, f0.GateMode);
            hub0.Release(f0);

            using var h = new MultiBrokerDryRunHarness(calibrator: new VolatilityAdaptiveCalibrator(), signalGateEnforced: false);
            h.SetCaps(60, 40);
            bool want = false;
            var sw = new SignalGateRuntimeSwitch(h.Gate, h.Signals, () => want, () => false);
            sw.Poll();
            var hub = new DashboardSnapshotHub(nowTicks: () => ++ticks);
            hub.AttachSignalGate(sw);
            Drive(h, choppy: true, cycles: 20);
            Assert.True(hub.Pump());
            var f1 = hub.Acquire()!;
            Assert.Equal(DashGateMode.Observe, f1.GateMode);
            Assert.Equal(h.Signals.ObservedProposals, f1.GateObserved);
            Assert.True(f1.GateShadowRegime > 0);
            Assert.Equal("게이트 관측(미차단)", SignalLabelMapper.GateModeLabel(f1.GateMode));
            hub.Release(f1);
            want = true; sw.Poll();
            Assert.True(hub.Pump());
            var f2 = hub.Acquire()!;
            Assert.Equal(DashGateMode.Enforce, f2.GateMode);
            Assert.Equal("enforce", SignalLabelMapper.GateModeClass(f2.GateMode));
            hub.Release(f2);
            Assert.Equal(0, MinAllocated(() => { for (int i = 0; i < 1_000; i++) { _ = SignalLabelMapper.GateModeLabel(i % 4); _ = SignalLabelMapper.GateModeClass(i % 4); } }));
        }

        [Fact]
        public void Regression_FiveBuilds_Ratchet_InterlockSpec()
        {
            string root = RepoRoot();
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
            {
                Assert.Contains(proj, sln);
                Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", File.ReadAllText(Path.Combine(root, proj)));
            }
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
            Assert.Equal(30, Enum.GetValues<ArmingInterlockSource>().Length);                 // 신규 비트 없음 — 출하 명세 유지
        }
    }
}
