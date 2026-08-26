// ============================================================================
// PreTradeRiskShieldTests — v4.94.0 과제1: 발주 전 리스크 실드
//
//  §1 원자 예약·경량 필터    — admit/증거금/당일한도/롤백/release(revolving).
//  §2 매도·공매도·시장가      — 매도 무소비·무차입 공매도·시장가 기준가 fail-closed.
//  §3 에스컬레이션 펜스        — 연속 3회 위반 → ForceFreeze + Downgrade·성공 시 리셋·오염 무스트라이크.
//  §4 오버플로 안전·텔레메트리·liveArmed:false.
//  §5(별도 클래스) TOCTOU 동시성·δ=0 무할당(ConcurrencyStress 편입).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    public sealed class PreTradeRiskShieldTests
    {
        private static OrderProposal Buy(decimal price, decimal qty, long symbol = 5930, byte kind = 1)
            => new(1, 0, symbol, OrderSide.Buy, kind, QPrice.Of(price), QQty.Of(qty), 0);
        private static OrderProposal Sell(decimal price, decimal qty, long symbol = 5930, byte kind = 1)
            => new(1, 0, symbol, OrderSide.Sell, kind, QPrice.Of(price), QQty.Of(qty), 0);

        // ── §1 원자 예약 · 경량 필터 ───────────────────────────────────────────

        [Fact]
        public void Admit_WithinBudget_ReservesConservativeNotional()
        {
            var s = new PreTradeRiskShield(QMoney.Of(1_000_000m).Raw, QMoney.Of(5_000_000m).Raw);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out var d));
            Assert.Equal(RiskVerdict.Admit, d.Verdict);
            Assert.Equal(QMoney.Of(100_000m).Raw, d.ReservedNotionalRaw);
            Assert.Equal(QMoney.Of(100_000m).Raw, s.MarginReservedRaw);
            Assert.Equal(QMoney.Of(100_000m).Raw, s.DailyReservedRaw);
            Assert.Equal(1L, s.AdmitCount);
        }

        [Fact]
        public void Reject_OverMargin_DropsOrder_StateUnchanged()
        {
            var s = new PreTradeRiskShield(QMoney.Of(300_000m).Raw, QMoney.Of(10_000_000m).Raw);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));               // 300k == 예산(초과 아님)
            Assert.False(s.TryAdmit(Buy(100_000m, 1m), out var d));          // 400k > 300k → 거부
            Assert.Equal(RiskVerdict.RejectMargin, d.Verdict);
            Assert.Equal(QMoney.Of(300_000m).Raw, s.MarginReservedRaw);      // ★상태 무변경
            Assert.Equal(1L, s.RejectCount);
        }

        [Fact]
        public void Reject_OverDailyLimit_RollsBackMargin()
        {
            var s = new PreTradeRiskShield(QMoney.Of(10_000_000m).Raw, QMoney.Of(150_000m).Raw);   // 증거금 큼·당일 150k
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));               // 당일 100k·증거금 100k
            Assert.False(s.TryAdmit(Buy(100_000m, 1m), out var d));          // 당일 200k>150k → 거부·증거금 롤백
            Assert.Equal(RiskVerdict.RejectDailyLimit, d.Verdict);
            Assert.Equal(QMoney.Of(100_000m).Raw, s.MarginReservedRaw);      // ★증거금 롤백(200k 아님)
            Assert.Equal(QMoney.Of(100_000m).Raw, s.DailyReservedRaw);
        }

        [Fact]
        public void Release_RevolvingMargin_FreesForReuse()
        {
            var s = new PreTradeRiskShield(QMoney.Of(100_000m).Raw, QMoney.Of(10_000_000m).Raw);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out var d));           // 만재
            Assert.False(s.TryAdmit(Buy(1m, 1m), out _));                    // 잔여 0 → 거부
            s.Release(d.ReservedNotionalRaw);
            Assert.Equal(0L, s.MarginReservedRaw);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));               // 반납 후 재예약 성공
        }

        [Fact]
        public void Release_NeverGoesNegative()
        {
            var s = new PreTradeRiskShield(QMoney.Of(100_000m).Raw, QMoney.Of(10_000_000m).Raw);
            s.Release(QMoney.Of(999_999m).Raw);    // 과다 반납 시도
            Assert.Equal(0L, s.MarginReservedRaw); // 0 클램프(음수 부패 차단)
        }

        // ── §2 매도 · 무차입 공매도 · 시장가 ──────────────────────────────────

        [Fact]
        public void Sell_NoMarginConsumption()
        {
            var s = new PreTradeRiskShield(QMoney.Of(100m).Raw, QMoney.Of(100m).Raw);   // 초소 예산
            Assert.True(s.TryAdmit(Sell(100_000m, 5m), out var d));          // 매도는 예산 무소비 → 통과
            Assert.Equal(RiskVerdict.Admit, d.Verdict);
            Assert.Equal(0L, d.ReservedNotionalRaw);
            Assert.Equal(0L, s.MarginReservedRaw);
        }

        [Fact]
        public void NakedShort_ExceedsHeld_Rejected()
        {
            var s = new PreTradeRiskShield(QMoney.Of(10_000_000m).Raw, QMoney.Of(10_000_000m).Raw,
                                           heldQtyRaw: _ => QQty.Of(3m).Raw);
            Assert.True(s.TryAdmit(Sell(1000m, 3m), out _));                 // 보유 3 이내 — 통과
            Assert.False(s.TryAdmit(Sell(1000m, 4m), out var d));            // 보유 초과 — 무차입 공매도 거부
            Assert.Equal(RiskVerdict.RejectNakedShort, d.Verdict);
        }

        [Fact]
        public void MarketBuy_NoOracle_FailClosed_NoStrike()
        {
            var att = new OrderAttestationService();
            var s = new PreTradeRiskShield(QMoney.Of(10_000_000m).Raw, QMoney.Of(10_000_000m).Raw, att, escalationStrikes: 3);
            Assert.False(s.TryAdmit(Buy(0m, 1m, kind: 0), out var d));       // 시장가·기준가 부재
            Assert.Equal(RiskVerdict.RejectMarketNoPrice, d.Verdict);
            for (int i = 0; i < 10; i++) s.TryAdmit(Buy(0m, 1m, kind: 0), out _);
            Assert.False(s.Escalated);                                       // ★구조적 거부는 무스트라이크(계좌 동결 안 됨)
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void MarketBuy_WithOracle_UsesRefPrice()
        {
            var s = new PreTradeRiskShield(QMoney.Of(10_000_000m).Raw, QMoney.Of(10_000_000m).Raw,
                                           marketRefPriceRaw: _ => QPrice.Of(50_000m).Raw);
            Assert.True(s.TryAdmit(Buy(0m, 2m, kind: 0), out var d));        // 시장가·기준가 50k·수량 2 → 명목 100k
            Assert.Equal(RiskVerdict.Admit, d.Verdict);
            Assert.Equal(QMoney.Of(100_000m).Raw, d.ReservedNotionalRaw);
        }

        // ── §3 에스컬레이션 펜스(경량 필터와 분리) ─────────────────────────────

        [Fact]
        public void ThreeConsecutiveBreaches_Escalate_ForceFreeze_And_Downgrade()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "SIG"));
            var s = new PreTradeRiskShield(QMoney.Of(100_000m).Raw, QMoney.Of(10_000_000m).Raw, att, ign, escalationStrikes: 3);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));               // 만재
            Assert.False(s.TryAdmit(Buy(1m, 1m), out _)); Assert.False(s.Escalated);   // strike 1
            Assert.False(s.TryAdmit(Buy(1m, 1m), out _)); Assert.False(s.Escalated);   // strike 2
            Assert.False(s.TryAdmit(Buy(1m, 1m), out _));                              // strike 3 → 에스컬레이션
            Assert.True(s.Escalated);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);              // ★계좌 전체 동결
            Assert.Equal(IgnitionStage.ProbeReady, ign.Stage);              // ★MasterIgnited → ProbeReady 강등
            Assert.False(s.TryAdmit(Buy(1m, 1m), out var d));               // 이후 전량 Frozen
            Assert.Equal(RiskVerdict.Frozen, d.Verdict);
        }

        [Fact]
        public void SuccessfulAdmit_ResetsConsecutiveStrikes()
        {
            var s = new PreTradeRiskShield(QMoney.Of(200_000m).Raw, QMoney.Of(10_000_000m).Raw, escalationStrikes: 3);
            Assert.True(s.TryAdmit(Buy(200_000m, 1m), out var full));        // 만재
            Assert.False(s.TryAdmit(Buy(1m, 1m), out _));                    // strike 1
            Assert.False(s.TryAdmit(Buy(1m, 1m), out _));                    // strike 2
            Assert.Equal(2L, s.ConsecutiveStrikes);
            s.Release(full.ReservedNotionalRaw);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));               // 성공 admit → 리셋
            Assert.Equal(0L, s.ConsecutiveStrikes);
            Assert.False(s.Escalated);
        }

        [Fact]
        public void Poison_ZeroQty_Dropped_NoStrike()
        {
            var s = new PreTradeRiskShield(QMoney.Of(100_000m).Raw, QMoney.Of(10_000_000m).Raw, escalationStrikes: 3);
            var poison = new OrderProposal(1, 0, 5930, OrderSide.Buy, 1, QPrice.Of(100m), QQty.Of(0m), 0);
            Assert.False(s.TryAdmit(poison, out var d));
            Assert.Equal(RiskVerdict.RejectPoison, d.Verdict);
            for (int i = 0; i < 10; i++) s.TryAdmit(poison, out _);
            Assert.False(s.Escalated);   // ★오염(상류 버그)은 무스트라이크
        }

        [Fact]
        public void AlreadyFrozen_RejectsAll_AsFrozen()
        {
            var att = new OrderAttestationService();
            att.ForceFreeze("외부 동결");
            var s = new PreTradeRiskShield(QMoney.Of(10_000_000m).Raw, QMoney.Of(10_000_000m).Raw, att);
            Assert.False(s.TryAdmit(Buy(100m, 1m), out var d));
            Assert.Equal(RiskVerdict.Frozen, d.Verdict);
        }

        // ── §4 오버플로 안전 · 텔레메트리 · liveArmed:false ────────────────────

        [Fact]
        public void Ctor_RejectsUnsafeBudgets()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreTradeRiskShield(0, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreTradeRiskShield(PreTradeRiskShield.MaxSafeBudgetRaw + 1, 100));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreTradeRiskShield(100, 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreTradeRiskShield(100, PreTradeRiskShield.MaxSafeBudgetRaw + 1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PreTradeRiskShield(100, 100, escalationStrikes: 0));
        }

        [Fact]
        public void MarginUtilizationRaw_Tracks_ReservedOverBudget()
        {
            var s = new PreTradeRiskShield(QMoney.Of(1_000_000m).Raw, QMoney.Of(10_000_000m).Raw);
            Assert.Equal(0L, s.MarginUtilizationRaw);
            Assert.True(s.TryAdmit(Buy(500_000m, 1m), out _));
            Assert.Equal(50_000_000L, s.MarginUtilizationRaw);              // 0.5 × 10^8
        }

        [Fact]
        public void LiveArmed_ShieldAdmit_NeverEnablesLiveRouting()
        {
            // ★실드는 '허용'만 — 발주 0바이트(IBroker 참조 자체가 없음). 하류 무장 술어는 여전히 수학적 0.
            var att = new OrderAttestationService();
            var s = new PreTradeRiskShield(QMoney.Of(10_000_000m).Raw, QMoney.Of(10_000_000m).Raw, att);
            Assert.True(s.TryAdmit(Buy(100_000m, 1m), out _));             // admit
            var ign = new IgnitionLock(sig => sig == "SIG", att);
            var gate = new LiveArmingGate(ign, att, () => true);           // 브로커 '무장'이라 쳐도…
            var pred = DispatchArming.ResolvePredicate(wired: false, gate); // …미배선 → AlwaysFalse
            Assert.False(pred());                                          // 실주문 술어 수학적 0
            Assert.Equal(GatingMode.Normal, att.Mode);                     // 실드 admit 은 동결/무장에 무영향
        }
    }

    // ── §5 TOCTOU 동시성 · δ=0 무할당(부하/타이밍 민감 → 직렬화 컬렉션 편입) ────
    [Collection("ConcurrencyStress")]
    public sealed class PreTradeRiskShieldConcurrencyTests
    {
        private static OrderProposal Buy(decimal price, decimal qty)
            => new(1, 0, 5930, OrderSide.Buy, 1, QPrice.Of(price), QQty.Of(qty), 0);

        [Fact]
        public void Concurrent_Reservation_NeverOverAdmits_ToctouFree()
        {
            // ★예산 = 정확히 units 개. 8스레드 × units 시도(반납 없음) → 정확히 units 만 admit, 예약=예산(초과 0).
            const int units = 500;
            long unit = QMoney.Of(1_000m).Raw;
            long marginBudget = unit * units;
            long dailyBudget  = unit * units * 4;   // 당일은 여유 — 증거금이 유일 제약
            var s = new PreTradeRiskShield(marginBudget, dailyBudget);
            int admitted = 0;
            var buy = Buy(1_000m, 1m);   // 명목 = unit

            var threads = new List<Thread>();
            for (int t = 0; t < 8; t++)
            {
                var th = new Thread(() =>
                {
                    for (int i = 0; i < units; i++)
                        if (s.TryAdmit(buy, out _)) Interlocked.Increment(ref admitted);
                });
                threads.Add(th);
            }
            foreach (var th in threads) th.Start();
            foreach (var th in threads) Assert.True(th.Join(30_000));

            Assert.Equal(units, admitted);                 // ★정확히 예산만큼만 admit(over-admit 0 = TOCTOU-free)
            Assert.Equal(marginBudget, s.MarginReservedRaw); // 예약 == 예산(초과 없음)
            Assert.True(s.MarginReservedRaw <= marginBudget);
        }

        [Fact]
        public void HotPath_AdmitRelease_ZeroAllocation()
        {
            var s = new PreTradeRiskShield(QMoney.Of(1_000_000_000m).Raw, QMoney.Of(1_000_000_000m).Raw);
            var buy = Buy(100m, 1m);   // 미소 명목 — 항상 예산 내
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++)   // 워밍업(tier-JIT/OSR settle)
                        if (s.TryAdmit(buy, out var w)) s.Release(w.ReservedNotionalRaw);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long sink = 0;
                    for (int i = 0; i < 100_000; i++)
                        if (s.TryAdmit(buy, out var d)) { sink += d.ReservedNotionalRaw; s.Release(d.ReservedNotionalRaw); }
                    long after = GC.GetAllocatedBytesForCurrentThread();
                    GC.KeepAlive(sink);
                    alloc = after - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★admit/release 핫패스 힙 할당 0(δ=0)
        }
    }
}
