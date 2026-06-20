// ============================================================================
// GreenhouseTests — S19 #4: 격리 배양 스레드 + 관제탑 5번째 센서 검증 (16개)
//
//  §1 GreenhouseService (8) — 배양 파이프라인·빈 씨앗·동결 정지·개선시만 제출
//                              ·스레드 수명·격리 방화벽·Dispose·이중 Start
//  §2 관제탑 센서 (5)        — 게이트웨이 스냅샷·null 안전·Overall 승격·심각도·라이프
//  §3 Fail-Closed 통합 (3)   — 승인대기 중 동결 → 승급 차단(게이트웨이/E2E)
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class GreenhouseTests
    {
        // ── 빌더 ──────────────────────────────────────────────────────────────────
        private static StrategySpec Spec(string id) => new StrategySpec(
            id, id,
            new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Rsi, 14), SpecOp.Lt, true, 30, null) },
            SpecLogic.And, new List<SpecCond>(), SpecLogic.And, 5, 10);

        private static List<StrategySpec> Seeds() => new()
        {
            new StrategySpec("seed", "씨앗",
                new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossAbove, false, 0, new SpecTerm(SpecKind.Sma, 20)) },
                SpecLogic.And,
                new List<SpecCond> { new SpecCond(new SpecTerm(SpecKind.Sma, 5), SpecOp.CrossBelow, false, 0, new SpecTerm(SpecKind.Sma, 20)) },
                SpecLogic.And, 5, 10),
        };

        // 진동+드리프트 종가 80봉 — MA 교차 발생으로 백테스트가 거래를 생성
        private static List<Candle> Candles(int n = 80)
        {
            var list = new List<Candle>(n);
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 10000m + (decimal)(Math.Sin(i * 0.5) * 800.0) + i * 12m;
                list.Add(new Candle
                {
                    Date = start.AddDays(i),
                    Open = close, High = close * 1.01m, Low = close * 0.99m,
                    Close = close, Volume = 1000,
                });
            }
            return list;
        }

        private static bool WaitFor(Func<bool> cond, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; Thread.Sleep(10); }
            return cond();
        }

        // ════════════════════════ §1 GreenhouseService ════════════════════════════

        [Fact]
        public void RunCycleOnce_ValidSeeds_SubmitsChampionToGateway()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();

            Assert.Equal(PromotionState.PendingApproval, gw.State);
            Assert.NotNull(gw.Current);
            var p = gh.Probe();
            Assert.True(p.Cycles >= 1);
            Assert.True(p.Mutations >= 1);
            Assert.NotNull(p.BestSpecId);
        }

        [Fact]
        public void RunCycleOnce_EmptySeeds_NoSubmit()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw);
            gh.Prime(new List<StrategySpec>(), Candles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();

            Assert.Equal(PromotionState.Idle, gw.State);
        }

        [Fact]
        public void RunCycleOnce_DuringEmergencyFreeze_NoSubmit()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("배양 정지 테스트");
            var gw = new PromotionGateway(attestation: att);
            var gh = new GreenhouseService(gw, att);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();

            Assert.Equal(PromotionState.Idle, gw.State);   // 동결 중 배양·제출 정지(fail-closed)
        }

        [Fact]
        public void RunCycleOnce_OnlySubmitsWhenFitnessImproves()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();
            double f1 = gh.Probe().BestFitness;
            Assert.Equal(PromotionState.PendingApproval, gw.State);

            gh.RunCycleOnce();   // 결정론 — 동일 챔피언, 개선 없음 → 최고치 불변
            var p2 = gh.Probe();
            Assert.Equal(2, p2.Cycles);
            Assert.Equal(f1, p2.BestFitness, 9);
        }

        [Fact]
        public void Start_Stop_ThreadLifecycle()
        {
            var gw = new PromotionGateway();
            using var gh = new GreenhouseService(gw) { CycleIntervalMs = 10 };
            gh.Start(Seeds(), Candles(), Market.KRX_KOSPI);

            Assert.True(WaitFor(() => gh.Probe().Cycles > 0), "배양 사이클이 진행되어야 한다");
            Assert.True(gh.Running);

            gh.Stop();
            Assert.False(gh.Running);
        }

        [Fact]
        public void IsolationFirewall_CycleThrows_NoCrash_GatingIntact()
        {
            var att = new OrderAttestationService();
            var gw  = new PromotionGateway(attestation: att);
            using var gh = new GreenhouseService(gw, att)
            {
                CycleIntervalMs = 10,
                CycleFaultHook  = () => throw new InvalidOperationException("주입 결함"),
            };
            gh.Start(new List<StrategySpec>(), new List<Candle>(), Market.KRX_KOSPI);

            // 방화벽이 사이클 예외를 흡수 — Errors 증가하나 루프·메인 게이트는 무사
            Assert.True(WaitFor(() => gh.Probe().Errors > 0), "방화벽이 예외를 흡수해야 한다");
            Assert.True(gh.Running);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 메인 GatingMode 무사

            gh.Stop();
            Assert.False(gh.Running);
        }

        [Fact]
        public void Dispose_StopsThread()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw) { CycleIntervalMs = 10 };
            gh.Start(Seeds(), Candles(), Market.KRX_KOSPI);
            Assert.True(WaitFor(() => gh.Probe().Cycles > 0));

            gh.Dispose();
            Assert.False(gh.Running);
        }

        [Fact]
        public void Start_WhenAlreadyRunning_IsIdempotent()
        {
            var gw = new PromotionGateway();
            using var gh = new GreenhouseService(gw) { CycleIntervalMs = 10 };
            gh.Start(Seeds(), Candles(), Market.KRX_KOSPI);
            gh.Start(Seeds(), Candles(), Market.KRX_KOSPI);   // 두 번째는 no-op
            Assert.True(gh.Running);
            gh.Stop();
        }

        // ════════════════════════ §2 관제탑 5번째 센서 ════════════════════════════

        [Fact]
        public void Capture_GatewayPending_ShowsCandidate()
        {
            var gw = new PromotionGateway();
            gw.Submit(Spec("champ-x"), 0.7);   // → PendingApproval
            var agg = new SystemStatusAggregator(gateway: gw);

            var s = agg.Capture();

            Assert.True(s.GreenhousePresent);
            Assert.Equal(PromotionState.PendingApproval, s.Promotion);
            Assert.Equal("champ-x", s.CandidateSpecId);
            Assert.Equal(0.7, s.CandidateFitness, 6);
            Assert.Equal(SystemSeverity.Notice, s.GreenhouseSeverity);
        }

        [Fact]
        public void Capture_NullGateway_SafeDefault_Ok()
        {
            var agg = new SystemStatusAggregator();   // 게이트웨이 미주입
            var s = agg.Capture();
            Assert.False(s.GreenhousePresent);
            Assert.Equal(SystemSeverity.Ok, s.GreenhouseSeverity);   // 미배선 = 양성
        }

        [Fact]
        public void Snapshot_GreenhousePending_ElevatesOverallToNotice()
        {
            // 다른 4센서 전부 Ok, 온실만 승인 대기 → Overall = Notice
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.Normal,
                MemoryPresent = true, TunerPresent = true,
                BridgePresent = true, BridgeAlive = true,
                GreenhousePresent = true, Promotion = PromotionState.PendingApproval,
            };
            Assert.Equal(SystemSeverity.Notice, s.GreenhouseSeverity);
            Assert.Equal(SystemSeverity.Notice, s.Overall);
        }

        [Fact]
        public void Snapshot_GreenhouseActive_SeverityOk()
        {
            var s = new SystemHealthSnapshot
            {
                AttestationPresent = true, GatingMode = GatingMode.Normal,
                MemoryPresent = true, TunerPresent = true,
                BridgePresent = true, BridgeAlive = true,
                GreenhousePresent = true, Promotion = PromotionState.Active,
            };
            Assert.Equal(SystemSeverity.Ok, s.GreenhouseSeverity);   // 가동 중(승인대기 아님) = Ok
        }

        [Fact]
        public void Capture_GatewayStateReadLockFree_ReflectsTransition()
        {
            var gw  = new PromotionGateway(signatureValidator: x => x == "OK");
            var agg = new SystemStatusAggregator(gateway: gw);

            gw.Submit(Spec("c"), 0.9);
            Assert.Equal(PromotionState.PendingApproval, agg.Capture().Promotion);

            gw.Approve("OK");
            Assert.Equal(PromotionState.Approved, agg.Capture().Promotion);
        }

        // ════════════════════════ §3 Fail-Closed 통합 ═════════════════════════════

        [Fact]
        public void PendingApproval_ThenFreeze_ApproveBlocked()
        {
            var att = new OrderAttestationService();
            var gw  = new PromotionGateway(null, att, x => x == "OK");
            gw.Submit(Spec("c"), 0.9);
            Assert.Equal(PromotionState.PendingApproval, gw.State);

            att.ForceFreeze("승인 직전 동결");
            Assert.False(gw.Approve("OK"));                    // fail-closed
            Assert.Equal(PromotionState.PendingApproval, gw.State);
        }

        [Fact]
        public void Greenhouse_Cultivates_Pending_ThenFreeze_SignOffBlocked()
        {
            var att = new OrderAttestationService();
            var gw  = new PromotionGateway(null, att, x => x == "OK");
            var gh  = new GreenhouseService(gw, att);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);

            gh.RunCycleOnce();   // att Normal 동안 배양 → PendingApproval
            Assert.Equal(PromotionState.PendingApproval, gw.State);

            att.ForceFreeze("관제탑 서명 직전 동결");
            Assert.False(gw.Approve("OK"));                    // 동결 중 승급 차단
            Assert.Equal(PromotionState.PendingApproval, gw.State);
        }

        [Fact]
        public void Probe_BeforeAnyCycle_ReportsZeroes()
        {
            var gw = new PromotionGateway();
            var gh = new GreenhouseService(gw);
            var p = gh.Probe();
            Assert.False(p.Running);
            Assert.Equal(0, p.Cycles);
            Assert.Equal(0.0, p.BestFitness, 9);
            Assert.Null(p.BestSpecId);
            Assert.Null(p.LastCycleUtc);
        }
    }
}
