// ============================================================================
// QuantSignalStrategyTests — [AI 전략 · 추천 A]
//
//  §1 시그널 수학 — 5호가 불균형 · [방향 A] 10호가 마이크로 추세(정적 불균형만으로는 임계 미달 · 잔량이 기울어야 선행 통과) ·
//     변동성 스파이크 거부 · VWAP 이격(누적 거래량 증분) · 무효 호가 무시 · 원시 바이트 프레임 입력.
//  §2 신뢰도 게이트 — 신호 없음 · 만료 · 확신 &lt; 0.85 · 방향 불일치 = 전방 거부 · 통과 조건 충족 시만 수용.
//  §3 [방향 B] 전략 이상 — 수량 0 이하 제안 · 연산 &gt; 10ms · 산출 이상 = 비트 27 sticky Disarm · 이후 전 제안 거부 · 재무장 이중키.
//  §4 관제 피딩 — 판단 상태 변화 시에만 사출(동일 호가 100회 = 1~2건) · /aidash 프레임에 진입 근거 수렴.
//  §5 E2E — 모의투자 하네스(실 게이팅 사슬) 라우터 게이트 결속: 약한 신호 = 송출 0 · 강한 매수 신호 = 매수만 송출 · 이상 격발 = 집계어 차단.
//  §6 δ=0 · 호스트 결선 · 오중 빌드.
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Blazor.Services;
using StockManager.Engines;
using StockManager.Simulation;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class QuantSignalStrategyTests
    {
        private const long Px = 70_000, Tick = 100, Sym = 5930;

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

        private sealed class Clock { public long Now = 1_000_000; public long Step; public long Read() { long n = Now; Now += Step; return n; } }

        /// <summary>10호가 — 매수 잔량 bid[i] · 매도 잔량 ask[i](균일) · 중간가 mid.</summary>
        private static OrderBookPayload Book(long sym, long mid, long bidQty, long askQty, long deepBidQty = -1, long deepAskQty = -1)
        {
            var b = new OrderBookPayload { SymbolCode = sym, TimestampTicks = 1 };
            for (int i = 0; i < 10; i++)
            {
                b.AskPrice[i] = mid + Tick * (i + 1); b.BidPrice[i] = mid - Tick * i;
                b.AskQty[i] = i < 5 || deepAskQty < 0 ? askQty : deepAskQty;
                b.BidQty[i] = i < 5 || deepBidQty < 0 ? bidQty : deepBidQty;
            }
            return b;
        }

        private static OrderProposal Prop(OrderSide side, long shares = 1, long priceWon = Px) => new(1, 0, Sym, side, 1, new QPrice(priceWon * 100_000_000), new QQty(shares * 100_000_000), 1);

        /// <summary>
        /// 매수 쪽으로 기우는 호가 — 1~5호가 고정(매수 950 · 매도 50 = imb5 0.9) · 6~10호가 잔량을 옮겨 10호가 불균형을 −0.05 → 0.95(스냅샷마다 +0.2).
        /// 총잔량 10,000 고정(가격 불변 = 변동성 0) — 마이크로 추세만 상승.
        /// </summary>
        private static OrderBookPayload Tilt(int k, long mid = Px)
        {
            double imb10 = -0.05 + 0.2 * Math.Min(k, 5);
            long bid10 = (long)Math.Round(5_000 * (1 + imb10)), ask10 = 10_000 - bid10;
            return Book(Sym, mid, 950, 50, (bid10 - 4_750) / 5, (ask10 - 250) / 5);
        }

        private static void RisingBids(QuantSignalProvider q, int steps, long mid = Px)
        {
            for (int k = 0; k < steps; k++) { var b = Tilt(k, mid); q.OnBook(in b); }
        }

        // ════════════ §1 시그널 수학 ════════════

        [Fact]
        public void Imbalance_MicroTrend_StaticImbalanceAloneNeverPassesThreshold()
        {
            var clk = new Clock();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: clk.Read);
            for (int i = 0; i < 20; i++) { var b = Book(Sym, Px, 900, 100); q.OnBook(in b); }   // 정적 불균형 0.8
            Assert.True(q.TryGetSignal(Sym, out var s));
            Assert.Equal(0.8, s.Imbalance5, 9);
            Assert.True(Math.Abs(s.MicroTrend) < 1e-9);                                           // 잔량 변화 0 = 마이크로 추세 0
            Assert.Equal(0.48, s.Confidence, 9);                                                  // 0.6 × 0.8 < 0.85 — ★정적 불균형만으로는 통과 불가
            Assert.Equal((1, (int)QuantReason.ImbalanceBuy), (s.Side, s.Reason));

            var q2 = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: new Clock().Read);
            RisingBids(q2, 6);                                                                    // [방향 A] 매수 잔량이 기우는 중(틱 없음)
            Assert.True(q2.TryGetSignal(Sym, out var r));
            Assert.True(r.MicroTrend > 0.6, "micro " + r.MicroTrend);
            Assert.True(r.Confidence >= 0.85, "conf " + r.Confidence);
            Assert.Equal(1, r.Side);
        }

        [Fact]
        public void VolatilitySpike_Vetoes_VwapUsesCumulativeIncrements_InvalidBooksIgnored()
        {
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: new Clock().Read);
            RisingBids(q, 6);
            var spike = Tilt(5, Px + 2_000);                                                      // 중간가 +2.8% 급변
            q.OnBook(in spike);
            Assert.True(q.TryGetSignal(Sym, out var s));
            Assert.True(s.VolatilityBp > QuantSignalProvider.DefaultVolSpikeBp);
            Assert.Equal((0.0, (int)QuantReason.VolatilitySpikeVeto), (s.Confidence, s.Reason));

            var v = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: new Clock().Read);
            v.OnTrade(Sym, 70_000, 1_000);                                                        // 기준선(누적 1,000 — 체결 아님)
            v.OnTrade(Sym, 70_000, 1_100);                                                        // +100 @70,000
            v.OnTrade(Sym, 69_000, 1_200);                                                        // +100 @69,000 → VWAP 69,500
            var b = Book(Sym, 70_145, 200, 100);                                                  // mid = (70,145 + 70,245)/2 = 70,195(+100bp 이격)
            v.OnBook(in b);
            Assert.True(v.TryGetSignal(Sym, out var w));
            Assert.InRange(w.VwapDeviationBp, 99.9, 100.1);
            Assert.Equal(3, v.Trades);

            long invalid = v.InvalidBooks;
            var crossed = Book(Sym, Px, 100, 100); crossed.AskPrice[0] = Px - 500;                  // 교차
            v.OnBook(in crossed);
            var empty = Book(Sym, Px, 0, 0);
            v.OnBook(in empty);
            v.OnBookFrame(new byte[16]);                                                          // 길이 미달 원시 프레임
            Assert.Equal(invalid + 3, v.InvalidBooks);
            var good = Book(Sym, Px, 300, 100);
            v.OnBookFrame(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref good, 1)));   // 원시 바이트 입력
            Assert.True(v.TryGetSignal(Sym, out var g) && g.Imbalance5 > 0.49);
        }

        // ════════════ §2 신뢰도 게이트 ════════════

        [Fact]
        public void Gate_RequiresFreshConfidentSameSideSignal()
        {
            var clk = new Clock();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), nowMicros: clk.Read, maxSignalAgeMicros: 500_000);
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy)));                                     // 신호 없음
            Assert.Equal(1, q.RejectedNoSignal);
            var weak = Book(Sym, Px, 300, 100); q.OnBook(in weak);
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy)));                                     // 확신 미달
            Assert.Equal(1, q.RejectedLowConfidence);
            RisingBids(q, 6);
            Assert.True(q.AdmitSignal(Prop(OrderSide.Buy)));                                      // ★강한 매수 신호 = 매수만
            Assert.False(q.AdmitSignal(Prop(OrderSide.Sell)));
            Assert.Equal(1, q.RejectedSideMismatch);
            clk.Now += 600_000;
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy)));                                     // 만료
            Assert.Equal(1, q.RejectedStale);
            Assert.Equal(1, q.Admitted);
        }

        // ════════════ §3 [방향 B] 전략 이상 ════════════

        [Fact]
        public void Anomaly_NonPositiveQty_Stall_Disarms_Sticky_DualKeyRearm()
        {
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            var a = new StrategyAnomalyInterlock();
            a.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            var q = new QuantSignalProvider(a, nowMicros: new Clock().Read);
            RisingBids(q, 6);
            Assert.True(q.AdmitSignal(Prop(OrderSide.Buy)));
            Assert.False(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy, shares: 0)));                          // ★수량 0 제안
            Assert.True(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            Assert.Equal(StrategyAnomalyKind.NonPositiveQty, a.FirstCause);
            Assert.False(q.AdmitSignal(Prop(OrderSide.Buy)));                                     // 이후 전 제안 거부(sticky)
            Assert.False(a.TryRearm(true, false));
            Assert.True(a.TryRearm(true, true));
            Assert.False(mask.IsRaised(ArmingInterlockSource.StrategyAnomaly));

            var slow = new Clock { Step = 20_000 };                                                // 판독마다 20ms 경과 = 연산 지연
            var a2 = new StrategyAnomalyInterlock();
            var q2 = new QuantSignalProvider(a2, nowMicros: slow.Read);
            var b = Book(Sym, Px, 300, 100); q2.OnBook(in b);
            Assert.True(a2.Tripped);
            Assert.Equal(StrategyAnomalyKind.ComputeStall, a2.FirstCause);
            Assert.True(q2.TryGetSignal(Sym, out var s) && s.Confidence == 0);                    // 지연 판단 확신 0

            var a3 = new StrategyAnomalyInterlock();
            a3.Trip(StrategyAnomalyKind.NonFinite);
            a3.Trip(StrategyAnomalyKind.OutOfRange);
            Assert.Equal((StrategyAnomalyKind.NonFinite, 1L, 1L, 1L), (a3.FirstCause, a3.Trips, a3.NonFinite, a3.OutOfRange));   // 최초 원인 각인 · 1회 트립
        }

        // ════════════ §4 관제 피딩 ════════════

        [Fact]
        public void DashboardFeed_PublishesOnStateChangeOnly_EntryRationaleConverges()
        {
            var clk = new Clock();
            var hub = new DashboardSnapshotHub();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), hub, nowMicros: clk.Read);
            for (int i = 0; i < 100; i++) { var b = Book(Sym, Px, 300, 100); q.OnBook(in b); }   // 동일 상태 100회
            Assert.InRange(q.Published, 1, 2);                                                    // ★상태 불변 = 재사출 0(관제 링 보호)
            RisingBids(q, 6);
            hub.Pump();
            var f = hub.Acquire()!;
            var entry = f.Decisions.Take(f.DecisionCount).FirstOrDefault(d => d.Kind == DashDecisionKind.Entry);
            Assert.Equal(Sym, entry.Symbol);
            Assert.True(entry.ScaleBp >= 8_500);
            Assert.Contains(entry.Reason, new[] { (int)QuantReason.ImbalanceBuy, (int)QuantReason.MicroTrendBuy });
            Assert.Contains(f.Decisions.Take(f.DecisionCount), d => d.Kind == DashDecisionKind.Hold);
            hub.Release(f);
        }

        // ════════════ §5 E2E ════════════

        [Fact]
        public void E2E_Harness_SignalGate_WeakBlocks_StrongBuyOnly_AnomalyDisarms()
        {
            using var h = new PaperTradingSimulationHarness();
            var anomaly = new StrategyAnomalyInterlock();
            anomaly.BindInterlock(h.Interlocks, ArmingInterlockSource.StrategyAnomaly);
            var q = new QuantSignalProvider(anomaly);
            h.Gate.EnableSignalGate(q);
            void Feed(OrderBookPayload b) { h.OnTossTick(Sym, Px); h.OnKiwoomBook(in b); q.OnBook(in b); }
            Feed(Book(Sym, Px, 300, 100));
            h.Pump();
            Assert.Equal(0L, h.Interlocks.Mask);
            for (int i = 0; i < 5; i++) { h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 1); h.Pump(); }
            Assert.Equal(0, h.Broker.Placed);                                                     // ★약한 신호 = 송출 0
            Assert.Equal(5, h.Gate.SignalRejects);
            for (int k = 0; k < 6; k++) Feed(Tilt(k));
            h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 2); h.Submit(Sym, OrderSide.Sell, 1, Px, 2);
            h.Pump();
            Assert.Equal(1, h.Broker.Placed);                                                     // 매수만(매도 = 방향 불일치)
            h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 0);                                        // 수량 0 제안 = 전략 이상
            h.Pump();
            Assert.True(h.Interlocks.IsRaised(ArmingInterlockSource.StrategyAnomaly));
            long placed = h.Broker.Placed;
            for (int i = 0; i < 5; i++) { Feed(Tilt(5)); h.Submit(Sym, OrderSide.Buy, 1, Px + Tick, 1); h.Pump(); }
            Assert.Equal(placed, h.Broker.Placed);                                                // ★Disarm 후 송출 0
            Assert.True(h.Worker.InterlockVetoes > 0);
        }

        // ════════════ §6 δ=0 · 결선 · 오중 빌드 ════════════

        [Fact]
        public void HotPaths_ZeroAlloc()
        {
            var hub = new DashboardSnapshotHub();
            var q = new QuantSignalProvider(new StrategyAnomalyInterlock(), hub, publishIntervalMicros: 1);   // 매 호가 사출(최악)
            var books = Enumerable.Range(0, 16).Select(k => Tilt(k % 6, Px + (k & 1) * Tick)).ToArray();
            byte[] frame = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref books[3], 1)).ToArray();
            var buy = Prop(OrderSide.Buy);
            for (int i = 0; i < 100; i++) { q.OnBook(in books[i & 15]); q.OnTrade(Sym, Px, 1_000 + i); q.AdmitSignal(in buy); }
            Assert.Equal(0, MinAllocated(() =>
            {
                for (int i = 0; i < 2_000; i++)
                {
                    q.OnBook(in books[i & 15]);
                    q.OnBookFrame(frame);
                    q.OnTrade(Sym, Px + (i & 3), 2_000 + i);
                    q.AdmitSignal(in buy);
                    q.TryGetSignal(Sym, out _);
                }
            }));
            Assert.False(q.Anomaly.Tripped);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void Host_Wiring_StrategyAnomalyRequired_GateOptIn_FiveBuilds()
        {
            string root = RepoRoot();
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("MS2E.Core.ArmingInterlockSource.StrategyAnomaly, //", form);
            Assert.Contains("strategyAnomaly.BindInterlock(interlockMask, MS2E.Core.ArmingInterlockSource.StrategyAnomaly);", form);
            Assert.Contains("new StockManager.Engines.QuantSignalProvider(strategyAnomaly, dashHub,", form);
            Assert.Contains("bridge.OrderBookReceived += b => quantSignals.OnBook(in b);", form);
            Assert.Contains("desired: () => cfg.GetBool(\"Strategy.SignalGateEnabled\", false),", form);   // [v12.18.2] 런타임 토글(SignalGateRuntimeSwitch)
            Assert.Contains("ringDispatcher.ProposalObserver = quantSignals;", form);
            Assert.True(form.IndexOf("strategyAnomaly.BindInterlock", StringComparison.Ordinal) < form.IndexOf("interlockMask.Seal(out long missingInterlocks)", StringComparison.Ordinal));
            Assert.Equal((27, 21), ((int)ArmingInterlockSource.StrategyAnomaly, (int)OrderRouteVerdict.SignalRejected));
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            foreach (var proj in new[] { @"StockManager\StockManager.csproj", @"StockManager.PrimesBench\StockManager.PrimesBench.csproj",
                                         @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(proj, sln);
            Assert.True(PureShellFitnessSentinel.RatchetCeiling <= 40);
        }
    }
}
