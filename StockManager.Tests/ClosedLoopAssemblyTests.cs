// ============================================================================
// ClosedLoopAssemblyTests — S20 #4: 드라이런 종단 조립체 검증 (12개)
//
//  §1 ShadowExecutionRouter (7) — 분배·디싱크·동결·권한·클램프·0·라이브
//  §2 종단 닫힌 고리 (5)        — 배양→승인→활성→라우팅 E2E·디싱크 전파·미승인·지분 분배
//
//  라이브 스위치 0 — 전 경로 IsDryRun 유지. 게이트 거부 시 분배 없음(부품 안전 보존).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class ClosedLoopAssemblyTests
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

        private static List<Candle> Candles(int n = 80)
        {
            var list = new List<Candle>(n);
            var start = new DateTime(2026, 1, 1);
            for (int i = 0; i < n; i++)
            {
                decimal close = 11000m + (decimal)(Math.Sin(i * 0.45) * 2000.0);
                list.Add(new Candle { Date = start.AddDays(i), Open = close, High = close * 1.01m, Low = close * 0.99m, Close = close, Volume = 1000 });
            }
            return list;
        }

        private static MultiAccountMesh Mesh211()
            => new MultiAccountMesh(new[] { new SubAccountSpec("alpha", 2m), new SubAccountSpec("beta", 1m), new SubAccountSpec("gamma", 1m) });

        private static OrderIntent Order(StrategySpec spec, decimal qty, decimal price, bool entry = true, PaperSide side = PaperSide.Buy)
            => new OrderIntent(spec, "A005930", "삼성전자", side, PaperKind.Market, qty, price, entry);

        private static (LocalState, BrokerState) SyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal>()),
                new BrokerState(1_000_000m, new Dictionary<string, decimal>()));

        private static (LocalState, BrokerState) DesyncBooks()
            => (new LocalState(1_000_000m, new Dictionary<string, decimal> { ["A005930"] = 10m }),
                new BrokerState(1_000_000m, new Dictionary<string, decimal> { ["A005930"] = 9m }));

        private static StrategySlot SlotWith(StrategySpec active) { var s = new StrategySlot(); s.Swap(active); return s; }

        // ════════════════════════ §1 ShadowExecutionRouter ════════════════════════

        [Fact]
        public void Route_DryRunApproved_ApportionsAcrossSubAccounts()
        {
            var spec = Spec("s");
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService());  // 드라이런 기본
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = SyncBooks();

            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.DryRunLogged, r.Outcome);
            Assert.Equal(100L, r.Fills.Sum(f => f.Qty));               // 무손실
            Assert.Equal(50L, r.Fills.First(f => f.SubAccountId == "alpha").Qty);  // 2:1:1
            Assert.Equal(25L, r.Fills.First(f => f.SubAccountId == "beta").Qty);
            Assert.Equal(25L, r.Fills.First(f => f.SubAccountId == "gamma").Qty);
        }

        [Fact]
        public void Route_Desync_RejectedNoFills_Frozen()
        {
            var spec = Spec("s");
            var att  = new OrderAttestationService();
            var gate = new AlgoExecutionGate(SlotWith(spec), att);
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = DesyncBooks();

            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.RejectedDesync, r.Outcome);
            Assert.Empty(r.Fills);
            Assert.Equal(AlgoGateState.Frozen, gate.State);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Route_Frozen_RejectedNoFills()
        {
            var spec = Spec("s");
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService());
            gate.Freeze("선동결");
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = SyncBooks();

            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedFrozen, r.Outcome);
            Assert.Empty(r.Fills);
        }

        [Fact]
        public void Route_Unauthorized_NoFills()
        {
            // 그레이스: s1=청산전용(draining), s2=진입전용(active). s1 진입 시도 → 거부
            var s1 = Spec("s1"); var s2 = Spec("s2");
            var slot = new StrategySlot(); slot.Swap(s1); slot.Swap(s2);
            var gate = new AlgoExecutionGate(slot, new OrderAttestationService());
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = SyncBooks();

            var r = router.Route(Order(s1, 100m, 70000m, entry: true), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedUnauthorized, r.Outcome);
            Assert.Empty(r.Fills);
        }

        [Fact]
        public void Route_SizeClamped_ApportionsClampedQty()
        {
            var spec = Spec("s");
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService()) { MaxOrderAbsQty = 60 };
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = SyncBooks();

            // 요청 100 → 절대캡 60으로 클램프 → 60을 2:1:1 분배 = 30/15/15
            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100_000_000m);
            Assert.Equal(60m, r.ApprovedQty);
            Assert.Equal(60L, r.Fills.Sum(f => f.Qty));
            Assert.Equal(30L, r.Fills.First(f => f.SubAccountId == "alpha").Qty);
        }

        [Fact]
        public void Route_ZeroApproved_NoFills()
        {
            var spec = Spec("s");
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService());
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = SyncBooks();

            // 자산 100 × 10% / 70000 → 0주 클램프
            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100m);
            Assert.Equal(0m, r.ApprovedQty);
            Assert.Empty(r.Fills);
        }

        [Fact]
        public void Route_LiveMode_CallsBrokerOnce_AndApportionsLedger()
        {
            var spec = Spec("s");
            var broker = new RecBroker();
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService(), broker) { IsDryRun = false };
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var (l, b) = SyncBooks();

            var r = router.Route(Order(spec, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.Executed, r.Outcome);
            Assert.Equal(1, broker.PlaceCount);             // 실 브로커는 1회(집계 주문)
            Assert.Equal(100L, r.Fills.Sum(f => f.Qty));    // 가상 원장은 계좌별 분배
        }

        // ════════════════════════ §2 종단 닫힌 고리 ═══════════════════════════════

        [Fact]
        public void FullClosedLoop_DryRun_EndToEnd()
        {
            var att  = new OrderAttestationService();
            var gw   = new PromotionGateway(null, att, s => s == "CMDR");
            var slot = new StrategySlot();
            var gate = new AlgoExecutionGate(slot, att);                 // 드라이런
            var mesh = Mesh211();
            var router = new ShadowExecutionRouter(gate, mesh);
            var gh = new GreenhouseService(gw);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);

            // ① 배양 → 승인 대기
            gh.RunCycleOnce();
            Assert.Equal(PromotionState.PendingApproval, gw.State);

            // ② 인간 서명 승인 → ③ 활성 슬롯에 핫스왑
            Assert.True(gw.Approve("CMDR"));
            Assert.True(gw.Activate(slot));
            var champion = slot.Active!;

            // ④ 신호 → 라우팅 (게이트 3중 가드 + 가상 계좌 분배)
            var (l, b) = SyncBooks();
            var r = router.Route(Order(champion, 100m, 70000m), l, b, 100_000_000m);

            Assert.Equal(ExecOutcome.DryRunLogged, r.Outcome);   // 라이브 스위치 0
            Assert.Equal(100L, r.Fills.Sum(f => f.Qty));         // 닫힌 고리 끝까지 무손실 관통
            Assert.Equal(AlgoGateState.Armed, gate.State);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void ClosedLoop_DesyncMidway_FreezesWholeStack()
        {
            var att  = new OrderAttestationService();
            var gw   = new PromotionGateway(null, att, s => s == "CMDR");
            var slot = new StrategySlot();
            var gate = new AlgoExecutionGate(slot, att);
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var gh = new GreenhouseService(gw);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            gh.RunCycleOnce(); gw.Approve("CMDR"); gw.Activate(slot);
            var champion = slot.Active!;

            // 디싱크 라우팅 → 전체 스택 동결
            var (l, b) = DesyncBooks();
            var r = router.Route(Order(champion, 100m, 70000m), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedDesync, r.Outcome);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);

            // 이후 정상 books 라우팅도 동결로 거부
            var (l2, b2) = SyncBooks();
            var r2 = router.Route(Order(champion, 100m, 70000m), l2, b2, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedFrozen, r2.Outcome);
        }

        [Fact]
        public void ClosedLoop_NoApproval_NoActivation_RoutingUnauthorized()
        {
            var att  = new OrderAttestationService();
            var gw   = new PromotionGateway(null, att, s => s == "CMDR");
            var slot = new StrategySlot();           // 활성 전략 없음
            var gate = new AlgoExecutionGate(slot, att);
            var router = new ShadowExecutionRouter(gate, Mesh211());
            var gh = new GreenhouseService(gw);
            gh.Prime(Seeds(), Candles(), Market.KRX_KOSPI);
            gh.RunCycleOnce();                       // PendingApproval 이나 승인·활성화 안 함

            Assert.Equal(PromotionState.PendingApproval, gw.State);
            Assert.Null(slot.Active);

            var champ = gw.Current!.Spec;            // 후보지만 슬롯엔 미활성
            var (l, b) = SyncBooks();
            var r = router.Route(Order(champ, 100m, 70000m), l, b, 100_000_000m);
            Assert.Equal(ExecOutcome.RejectedUnauthorized, r.Outcome);   // 슬롯 미활성 → 권한 없음
        }

        [Fact]
        public void ClosedLoop_MultiSubAccount_SharesRespected()
        {
            var spec = Spec("s");
            var gate = new AlgoExecutionGate(SlotWith(spec), new OrderAttestationService());
            // 5:3:2 지분
            var mesh = new MultiAccountMesh(new[] { new SubAccountSpec("a", 5m), new SubAccountSpec("b", 3m), new SubAccountSpec("c", 2m) });
            var router = new ShadowExecutionRouter(gate, mesh);
            var (l, b) = SyncBooks();

            var r = router.Route(Order(spec, 1000m, 1000m, side: PaperSide.Buy), l, b, 100_000_000m);
            Assert.Equal(1000L, r.Fills.Sum(f => f.Qty));
            Assert.Equal(500L, r.Fills.First(f => f.SubAccountId == "a").Qty);
            Assert.Equal(300L, r.Fills.First(f => f.SubAccountId == "b").Qty);
            Assert.Equal(200L, r.Fills.First(f => f.SubAccountId == "c").Qty);
        }
    }

    // ── 발주 기록 테스트용 브로커 ──────────────────────────────────────────────────
    file sealed class RecBroker : IBroker
    {
        public int PlaceCount;
        public string Name => "Rec"; public bool IsLive => false;
        public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide sd, PaperKind k, decimal q, decimal lp, decimal cp)
        { PlaceCount++; return (true, "ok"); }
        public void Cancel(string id) { }
        public decimal HeldQty(PaperAsset a, string s) => 0m;
    }
}
