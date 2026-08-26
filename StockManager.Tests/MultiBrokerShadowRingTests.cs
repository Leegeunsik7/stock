// ============================================================================
// MultiBrokerShadowRingTests — v4.94.0 과제2: 다중 브로커 섀도우 집행 분산 링
//
//  §1 라우팅        — AssetClass 자동 위임·미등록 거부·노드 마스크.
//  §2 페어 원자성    — 양 leg 커밋·legB 실패 롤백·취소 실패 노출(ForceFreeze+Downgrade)·legA 실패 무발주.
//  §3 데드라인·멱등·동결·liveArmed:false.
// ============================================================================
using System;
using System.Collections.Generic;
using Xunit;

namespace StockManager.Tests
{
    public sealed class MultiBrokerShadowRingTests
    {
        private sealed class Clock { public long Ticks; }

        /// <summary>구성 가능한 확장 게이트 더블 — 발주/취소 성공여부·주문ID 반환·레이턴시(clock 진행)를 조절.</summary>
        private sealed class FakeGate : IExtendedExecutionGate
        {
            private readonly Clock? _clock;
            private readonly long   _latencyTicks;
            public bool PlaceOk = true;
            public bool CancelOk = true;
            public bool ReturnOrderId = true;   // false = Ok 이나 주문ID 부재(취소 불가)
            public int  PlaceCalls, CancelCalls;
            public AssetClass Class { get; }
            public string Name => "fake:" + Class;
            public bool IsLive => false;

            public FakeGate(AssetClass cls, Clock? clock = null, long latencyTicks = 0)
            { Class = cls; _clock = clock; _latencyTicks = latencyTicks; }

            public (bool ok, string msg) Place(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
                => PlaceOk ? (true, "ok") : (false, "거부");
            public void Cancel(string orderId) { }
            public decimal HeldQty(PaperAsset a, string s) => 0m;

            public PlaceReceipt PlaceTracked(PaperAsset a, string s, string n, PaperSide side, PaperKind k, decimal q, decimal lp, decimal cp)
            {
                PlaceCalls++;
                if (_clock is { } c) c.Ticks += _latencyTicks;
                return PlaceOk ? new PlaceReceipt(true, ReturnOrderId ? "OID-" + PlaceCalls : "", "ok") : PlaceReceipt.Rejected;
            }
            public bool TryCancel(string brokerOrderId) { CancelCalls++; return CancelOk; }
        }

        private static PairLeg Leg(AssetClass cls, PaperSide side = PaperSide.Buy)
            => new(cls, PaperAsset.Stock, "005930", "삼성", side, PaperKind.Limit, 10m, 100m, 100m);

        private static MultiBrokerShadowRing Ring(out FakeGate dom, out FakeGate foreign,
            OrderAttestationService? att = null, IgnitionLock? ign = null, Clock? clock = null,
            long domLatency = 0, long forLatency = 0, int deadlineMs = 50)
        {
            dom     = new FakeGate(AssetClass.DomesticStock, clock, domLatency);
            foreign = new FakeGate(AssetClass.ForeignStock, clock, forLatency);
            var gates = new Dictionary<AssetClass, IExtendedExecutionGate>
            { [AssetClass.DomesticStock] = dom, [AssetClass.ForeignStock] = foreign };
            Func<long>? now = clock is null ? null : () => clock.Ticks;
            // 주입 시계는 TimeSpan-tick 스케일 → tickFrequency=TicksPerSecond 로 데드라인 환산 정합(50ms → 500,000 tick).
            long freq = clock is null ? 0 : TimeSpan.TicksPerSecond;
            return new MultiBrokerShadowRing(gates, att, ign, now, deadlineMs, freq);
        }

        // ── §1 라우팅 ────────────────────────────────────────────────────────

        [Fact]
        public void Route_ByAssetClass_DelegatesToCorrectGate()
        {
            var r = Ring(out var dom, out var foreign);
            var (ok, _) = r.Route(AssetClass.DomesticStock, PaperAsset.Stock, "005930", "삼성", PaperSide.Buy, PaperKind.Limit, 1m, 100m, 100m);
            Assert.True(ok);
            Assert.Equal(1L, r.Routed);
        }

        [Fact]
        public void Route_UnregisteredClass_Rejected()
        {
            var r = Ring(out _, out _);
            var (ok, msg) = r.Route(AssetClass.Crypto, PaperAsset.Coin, "KRW-BTC", "비트", PaperSide.Buy, PaperKind.Market, 1m, 0m, 0m);
            Assert.False(ok);
            Assert.Contains("미등록", msg);
            Assert.Equal(1L, r.RouteRejected);
            Assert.False(r.HasGate(AssetClass.Crypto));
        }

        [Fact]
        public void ActiveNodeMask_ReflectsRegisteredGates()
        {
            var r = Ring(out _, out _);
            long expect = (1L << (int)AssetClass.DomesticStock) | (1L << (int)AssetClass.ForeignStock);
            Assert.Equal(expect, r.ActiveNodeMask);
            Assert.Equal(2, r.NodeCount);
        }

        // ── §2 페어 원자성 센티널 ─────────────────────────────────────────────

        [Fact]
        public void ExecutePair_BothOk_Committed()
        {
            var r = Ring(out var dom, out var foreign);
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock, PaperSide.Sell));
            Assert.Equal(PairOutcome.Committed, res.Outcome);
            Assert.True(res.Ok);
            Assert.Equal(1, dom.PlaceCalls);
            Assert.Equal(1, foreign.PlaceCalls);
            Assert.Equal(0, dom.CancelCalls);
            Assert.Equal(1L, r.PairsCommitted);
        }

        [Fact]
        public void ExecutePair_LegBFails_RollsBackLegA()
        {
            var r = Ring(out var dom, out var foreign);
            foreign.PlaceOk = false;   // 해외 leg 실패
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock, PaperSide.Sell));
            Assert.Equal(PairOutcome.RolledBack, res.Outcome);
            Assert.Equal(1, dom.PlaceCalls);
            Assert.Equal(1, dom.CancelCalls);        // ★국내 leg 롤백(취소) 호출
            Assert.Equal(1L, r.PairsRolledBack);
            Assert.Equal(0L, r.CancelFailures);
        }

        [Fact]
        public void ExecutePair_LegBFails_CancelFails_Exposed_ForceFreeze_Downgrade()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "SIG"));
            var r = Ring(out var dom, out var foreign, att, ign);
            foreign.PlaceOk = false;   // 해외 실패
            dom.CancelOk   = false;    // ★국내 취소마저 실패 = 화해 불가 노출
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock, PaperSide.Sell));
            Assert.Equal(PairOutcome.Exposed, res.Outcome);
            Assert.True(res.Exposed);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);          // ★하드 단절
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);          // ★MasterIgnited → ProbeReady 강등
            Assert.Equal(1L, r.PairsExposed);
            Assert.Equal(1L, r.CancelFailures);
        }

        [Fact]
        public void ExecutePair_NoOrderId_CannotRollback_Exposed()
        {
            var att = new OrderAttestationService();
            var r = Ring(out var dom, out var foreign, att);
            dom.ReturnOrderId = false;   // 국내 leg 는 Ok 이나 주문ID 부재(취소 불가)
            foreign.PlaceOk   = false;   // 해외 실패 → 국내 취소 시도하나 ID 없음
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock, PaperSide.Sell));
            Assert.Equal(PairOutcome.Exposed, res.Outcome);
            Assert.Equal(0, dom.CancelCalls);   // ID 없어 취소 시도 자체 불가
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ExecutePair_LegAFails_NoLegBPlaced()
        {
            var r = Ring(out var dom, out var foreign);
            dom.PlaceOk = false;   // 국내 leg 실패 → 해외 leg 진입 금지
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock, PaperSide.Sell));
            Assert.Equal(PairOutcome.RejectedNoLeg, res.Outcome);
            Assert.Equal(1, dom.PlaceCalls);
            Assert.Equal(0, foreign.PlaceCalls);   // ★반대편 미발주(깨끗)
            Assert.Equal(1L, r.PairsRejected);
        }

        // ── §3 데드라인 · 동결 · liveArmed ────────────────────────────────────

        [Fact]
        public void ExecutePair_LegBSlowSuccess_CancelFails_Exposed_ForceFreeze()
        {
            // ★적대검증 회귀(결함A): legB 느린 성공 + legB 취소 실패 = 살아있는 단독 노출 → Exposed(RolledBack 오보 금지).
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "SIG"));
            var clock = new Clock { Ticks = 1_000_000 };
            var r = Ring(out var dom, out var foreign, att, ign, clock, domLatency: 0, forLatency: 600_000, deadlineMs: 50);
            foreign.PlaceOk  = true;    // legB 는 Ok 이나 레이턴시 600k>데드라인 500k → 느린 성공(bTimedOut)
            foreign.CancelOk = false;   // ★legB 취소 실패 → 해외 주문 시장 잔존
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock, PaperSide.Sell));
            Assert.Equal(PairOutcome.Exposed, res.Outcome);          // ★깨끗한 RolledBack 으로 은폐 금지
            Assert.Equal(1, foreign.CancelCalls);                    // legB 취소 시도됨(실패)
            Assert.Equal(1, dom.CancelCalls);                        // legA 도 원상복구 시도됨
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);      // ★하드 단절
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);      // ★강등
            Assert.Equal(1L, r.PairsExposed);
        }

        [Fact]
        public void ExecutePair_LegADeadlineExceeded_Cancels_NoLegB()
        {
            var clock = new Clock { Ticks = 1_000_000 };
            // 국내 leg 레이턴시 600k tick > 데드라인 50ms(=500k tick) → 느린 성공 → 취소.
            var r = Ring(out var dom, out var foreign, clock: clock, domLatency: 600_000, deadlineMs: 50);
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock));
            Assert.Equal(PairOutcome.RolledBack, res.Outcome);
            Assert.Equal(1, dom.CancelCalls);       // 데드라인 초과 → 국내 취소
            Assert.Equal(0, foreign.PlaceCalls);    // 해외 leg 미진입
        }

        [Fact]
        public void ExecutePair_WhenFrozen_Rejected()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("외부 동결");
            var r = Ring(out var dom, out var foreign, att);
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock));
            Assert.Equal(PairOutcome.RejectedNoLeg, res.Outcome);
            Assert.Equal(0, dom.PlaceCalls);   // 동결 → 아무 leg 미발주
        }

        [Fact]
        public void LiveArmed_UnarmedGate_RejectsWithoutEmission()
        {
            // 미무장 게이트(LiveBrokerStub 처럼 거부)는 페어를 성립시키지 못한다. 링은 게이트 계약을 우회하지 않음.
            var att = new OrderAttestationService();
            var r = Ring(out var dom, out var foreign, att);
            dom.PlaceOk = false; foreign.PlaceOk = false;
            var res = r.ExecutePair(Leg(AssetClass.DomesticStock), Leg(AssetClass.ForeignStock));
            Assert.NotEqual(PairOutcome.Committed, res.Outcome);
            // 하류 무장 술어도 여전히 수학적 0.
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.False(DispatchArming.ResolvePredicate(wired: false, gate)());
        }
    }
}
