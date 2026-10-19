// ============================================================================
// ExecutionQualityTests — [v12.18.25-DEV] 체결 품질 포렌식(ArenaExecutionQualityTracker) · 포렌식 모달
//
//  §1 롱 — 급락/급등 틱에서 MAE(진입 − 최저 매수호가) · MFE(최고 매수호가 − 진입) · bp · 원 · 청산 효율 · 보유 시간(시장 시각)
//  §2 숏 — 대칭(MAE = 최고 매도호가 − 진입 · MFE = 진입 − 최저 매도호가) · 추가 진입 평균가 · 부분 커버
//  §3 요약 — 승률 · 손익비(이익 합 ÷ |손실 합| · 손실 0 = 무한) · 평균 MAE/MFE · MAE 대비 실현 · 기록 링 순서/덮어씀
//  §4 거버너/UI — 스냅샷 Gross/Net/청산가치 · 복기 포렌식 필드 · 종료 시 포렌식 모달 렌더(닫기) · 진행 중 미표시
//  §5 Fail-Closed — 추적기/시뮬레이터 IL 허용목록(브로커/디스패처/무장 형식 0)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Arena;
using Xunit;

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ExecutionQualityTests
    {
        private const long Sym = 5930, T0 = 638_000_000_000_000_000, Ms = 10_000;
        private long _now = T0;

        private static OrderBookPayload Book(long ask, long bid, long qty = 1_000_000)
        {
            var b = new OrderBookPayload { SymbolCode = Sym };
            for (int l = 0; l < 10; l++)
            {
                b.AskPrice[l] = ask + l * 100; b.AskQty[l] = qty;
                b.BidPrice[l] = bid - l * 100; b.BidQty[l] = qty;
            }
            return b;
        }

        private static DualBookSimulator Sim()
        {
            var sim = new DualBookSimulator(0, 0);
            sim.Reset(1_000_000_000);
            return sim;
        }

        private static void Mkt(DualBookSimulator sim, int dir, long qty, long now)
        {
            sim.SetClock(now);
            Assert.Equal(ArenaReject.None, sim.Submit(ArenaSide.Human, Sym, dir, qty, ArenaOrderKind.Market, 0, now));
            sim.Process(now);
        }

        // ════════════ §1 롱 ════════════

        [Fact]
        public void Long_Crash_Then_Spike_Tracks_Mae_Mfe_Per_Tick()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 69_900));
            Mkt(sim, 1, 100, T0);                                                 // 진입 70,000
            sim.OnQuote(Book(68_100, 68_000));                                    // 급락 — 최저 매수호가 68,000
            sim.OnQuote(Book(69_100, 69_000));
            sim.OnQuote(Book(72_600, 72_500));                                    // 급등 — 최고 72,500
            sim.OnQuote(Book(71_100, 71_000));
            Mkt(sim, -1, 100, T0 + 30 * 10_000_000);                              // 청산 71,000 · 30초 보유
            Assert.True(h.Quality.TryLast(out var r));
            Assert.Equal((1, 70_000L, 71_000L, 2_000L, 2_500L), (r.Side, r.EntryPx, r.ExitPx, r.MaePx, r.MfePx));
            Assert.Equal((285L, 357L, 200_000L, 250_000L), (r.MaeBp, r.MfeBp, r.MaeWon, r.MfeWon));   // 2,000/70,000 · 2,500/70,000
            Assert.Equal(4_000, r.ExitEfficiencyBp);                              // 실현 이동 1,000 ÷ MFE 2,500 = 40%
            Assert.Equal(30 * 10_000_000L, r.HoldTicks);
            Assert.True(h.Quality.Marks >= 4);
        }

        [Fact]
        public void Long_NeverAdverse_MaeZero_EntryTickIncluded()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 69_900));
            Mkt(sim, 1, 10, T0);
            sim.OnQuote(Book(70_600, 70_500));
            Mkt(sim, -1, 10, T0 + 1);
            Assert.True(h.Quality.TryLast(out var r));
            Assert.Equal((0L, 500L, 70_500L), (r.MaePx, r.MfePx, r.ExitPx));    // 진입 체결가가 첫 표본 → 체결 전 매수호가(69,900)는 MAE 아님
        }

        // ════════════ §2 숏 ════════════

        [Fact]
        public void Short_Spike_Then_Drop_Symmetric_Mae_Mfe_AddAndPartialCover()
        {
            var sim = Sim();
            var h = sim.Book(ArenaSide.Human);
            sim.OnQuote(Book(70_000, 69_900));
            Mkt(sim, -1, 100, T0);                                                // 숏 69,900
            sim.OnQuote(Book(70_100, 70_000));
            Mkt(sim, -1, 100, T0 + 1);                                            // 추가 숏 70,000 → 평균 69,950
            sim.OnQuote(Book(73_000, 72_900));                                    // 급등 — 최고 매도호가 73,000(MAE)
            sim.OnQuote(Book(67_000, 66_900));                                    // 급락 — 최저 매도호가 67,000(MFE)
            Mkt(sim, 1, 50, T0 + 2);                                              // 부분 커버 67,000
            Assert.Equal(1, h.Quality.OpenTrips);
            Assert.False(h.Quality.TryLast(out _));
            sim.OnQuote(Book(68_000, 67_900));
            Mkt(sim, 1, 150, T0 + 3);                                             // 잔여 커버 68,000
            Assert.True(h.Quality.TryLast(out var r));
            Assert.Equal((-1, 200L, 69_950L), (r.Side, r.Qty, r.EntryPx));
            Assert.Equal((3_050L, 2_950L), (r.MaePx, r.MfePx));                  // 73,000 − 69,950 · 69,950 − 67,000
            Assert.Equal((67_000L * 50 + 68_000L * 150) / 200, r.ExitPx);
            Assert.Equal(r.NetWon + r.FeesWon + r.TaxWon, r.GrossWon);
            Assert.Equal(h.RealizedWon, r.NetWon);
        }

        // ════════════ §3 요약 ════════════

        [Fact]
        public void Summary_WinRate_ProfitFactor_MaeToRealized_RingOrder()
        {
            var t = new ArenaExecutionQualityTracker();
            Assert.Equal(0, t.Summary().ProfitFactorBp);
            // 이익 trip: 롱 100 @ 1,000 → 1,100 (net +9,000 가정)
            t.OnFill(1, 0, 100, 100, 100_000, 15, 0, 0, 0); t.OnMark(1, 950, 960); t.OnFill(1, 100, 0, 100, 110_000, 16, 198, 9_000, 10);
            Assert.Equal(-1, t.Summary().ProfitFactorBp);                          // 손실 0 = 무한
            // 손실 trip: 숏 50 @ 2,000 → 2,100 (net −6,000)
            t.OnFill(2, 0, -50, 50, 100_000, 15, 180, 0, 20); t.OnMark(2, 2_090, 2_100); t.OnFill(2, -50, 0, 50, 105_000, 15, 0, -6_000, 30);
            var s = t.Summary();
            Assert.Equal((2, 1, 1), (s.Trips, s.Wins, s.Losses));
            Assert.Equal(5_000, s.WinRateBp);
            Assert.Equal(15_000, s.ProfitFactorBp);                                // 9,000 ÷ 6,000 = 1.50
            Assert.Equal(3_000, s.NetWon);
            Assert.Equal(10_000, s.SumMaeWon);                                     // 롱 MAE 50 × 100 + 숏 MAE 100 × 50
            Assert.Equal(3_000, s.MaeToRealizedBp);                                // Σ Net 3,000 ÷ Σ MAE 10,000 = 30%
            Span<TradeExecutionRecord> buf = stackalloc TradeExecutionRecord[4];
            Assert.Equal(2, t.CopyRecent(buf));
            Assert.Equal((2L, 1L), (buf[0].Symbol, buf[1].Symbol));               // 최신 → 과거
            for (int i = 0; i < ArenaExecutionQualityTracker.RecordCapacity + 5; i++) { t.OnFill(9, 0, 1, 1, 10, 0, 0, 0, i); t.OnFill(9, 1, 0, 1, 11, 0, 0, 1, i); }
            Assert.Equal(ArenaExecutionQualityTracker.RecordCapacity, t.CopyRecent(new TradeExecutionRecord[300]));
        }

        // ════════════ §4 거버너 · UI ════════════

        private void Step(ArenaMatchGovernor g) { _now += Ms; g.Step(_now); }

        private ArenaMatchGovernor PlayedMatch()
        {
            var g = new ArenaMatchGovernor(() => _now, 1, latencyMinTicks: 0, latencyMaxTicks: 0, riskLimitBp: 0);
            Assert.True(g.Start(100_000_000));
            Step(g); g.SetFocus(Sym);
            g.OnQuote(Book(70_000, 69_900)); Step(g);
            g.SubmitHuman(Sym, 1, 100); Step(g);
            g.OnQuote(Book(68_100, 68_000)); Step(g);
            g.OnQuote(Book(71_100, 71_000)); Step(g);
            g.SubmitHuman(Sym, -1, 100); Step(g);
            g.SubmitHuman(Sym, -1, 50); Step(g);                                   // 숏
            g.OnQuote(Book(72_100, 72_000)); Step(g);
            g.SubmitHuman(Sym, 1, 50); Step(g);                                    // 손실 커버
            return g;
        }

        [Fact]
        public void Governor_Snapshot_And_Debrief_Carry_Forensics()
        {
            var g = PlayedMatch();
            Assert.True(g.TryRead(out var s));
            var hu = s.Human;
            Assert.Equal(hu.EquityWon - s.CapitalWon, hu.NetPnlWon);
            Assert.Equal(hu.NetPnlWon + hu.FeesWon + hu.TaxWon, hu.GrossPnlWon);
            Assert.Equal(hu.EquityWon, hu.LiquidationEquityWon);                   // 무포지션 = 청산가치 = 자산
            Assert.True(g.TryReadDebrief(ArenaSide.Human, out var d));
            Assert.Equal((2, 1, 1), (d.QualityTrips, d.Wins, d.Losses));
            Assert.Equal(5_000, d.WinRateBp);
            Assert.True(d.ProfitFactorBp > 0);
            Assert.Equal((hu.GrossPnlWon, hu.NetPnlWon, hu.FeesWon, hu.TaxWon), (d.GrossPnlWon, d.NetPnlWon, d.FeesWon, d.TaxWon));
            Assert.Equal(-1, d.LastSide);                                          // 마지막 = 숏 trip
            Assert.Equal(72_100L - 71_000L, d.LastMaePx);                          // 숏 진입 = 매수 1호가 71,000 · 최고 매도호가 72,100
            Assert.True(d.AvgMaeBp > 0);
        }

        private static Bunit.TestContext Ctx(ArenaMatchGovernor g, ArenaTickReplayEngine replay, ArenaTapeRecorder rec)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IArenaPort>(g);
            ctx.Services.AddSingleton<IArenaReplayPort>(replay);
            ctx.Services.AddSingleton<IArenaTapePort>(rec);
            ctx.Services.AddSingleton<IKisFeedLatencyPort>(new StockManager.Adapters.Kis.KisTimeSkewGuard());
            return ctx;
        }

        [Fact]
        public void Headless_Forensic_Modal_Shows_After_Finish_And_Closes()
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ms2e_forensic_" + Guid.NewGuid().ToString("N"));
            using var replay = new ArenaTickReplayEngine(() => SyntheticTickSource.Create(100), ownThread: false);
            using var rec = new ArenaTapeRecorder(dir, 10);
            try
            {
                var g = PlayedMatch();
                using var ctx = Ctx(g, replay, rec);
                var cut = ctx.RenderComponent<MS2E.UI.Portable.Pages.Trading.ArenaBattleView>();
                Assert.Empty(cut.FindAll(".ar-forensic"));                         // 진행 중 = 미표시
                g.Stop(); Step(g);
                cut.Render();
                var modal = cut.Find(".ar-forensic");
                Assert.Contains("포렌식 성과 분석", modal.TextContent);
                Assert.Contains("손익비", modal.TextContent);
                Assert.Contains("거래세", modal.TextContent);
                Assert.Contains("Net", modal.TextContent);
                Assert.Contains("MAE 대비 실현", modal.TextContent);
                Assert.True(g.TryReadDebrief(ArenaSide.Human, out var d));
                Assert.Contains(d.TaxWon.ToString("N0"), modal.TextContent);
                cut.Find(".ar-forensic-close").Click();
                Assert.Empty(cut.FindAll(".ar-forensic"));
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch (System.IO.IOException) { } catch (UnauthorizedAccessException) { } }
        }

        // ════════════ §5 Fail-Closed ════════════

        private static bool AllowedNs(Type t)
        {
            while (t.HasElementType) t = t.GetElementType()!;
            if (t.IsGenericType && !t.IsGenericTypeDefinition) return AllowedNs(t.GetGenericTypeDefinition()) && t.GetGenericArguments().All(AllowedNs);
            if (t.IsGenericParameter || t.FullName == "<PrivateImplementationDetails>") return true;
            if (t == typeof(OrderBookPayload) || t == typeof(OrderBookLevels10)) return true;
            if (t.DeclaringType is { } d && AllowedNs(d.IsGenericType ? d.GetGenericTypeDefinition() : d)) return true;
            string ns = t.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.Runtime", StringComparison.Ordinal) || ns.StartsWith("System.Numerics", StringComparison.Ordinal)
                || ns == "MS2E.UI.Portable.Ports.Trading" || ns == "StockManager.Engines.Arena" || ns == "StockManager.Engines.Arena.Strategy";
        }

        [Fact]
        public void Tracker_And_Simulator_Il_No_Broker_Dispatcher_Or_Arming_Types()
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var bad = new List<string>();
            foreach (var type in new[] { typeof(ArenaExecutionQualityTracker), typeof(TradeExecutionRecord), typeof(ExecutionQualitySummary), typeof(DualBookSimulator), typeof(ArenaBook) })
            {
                foreach (var f in type.GetFields(All)) if (!AllowedNs(f.FieldType)) bad.Add(type.Name + "." + f.Name + " : " + f.FieldType.FullName);
                foreach (var m in type.GetMethods(All).Cast<MethodBase>().Concat(type.GetConstructors(All)))
                    foreach (var r in ZeroAllocIlInspector.ReferencedMembers(m))
                    {
                        var owner = r as Type ?? r.DeclaringType;
                        if (owner is not null && !AllowedNs(owner)) bad.Add(type.Name + "." + m.Name + " → " + owner.FullName + "::" + r.Name);
                    }
            }
            Assert.True(bad.Count == 0, string.Join("\n", bad));
        }
    }
}
