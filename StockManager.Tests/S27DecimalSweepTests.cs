using System;
using Xunit;

namespace StockManager.Tests
{
    // Season 27 — 무할당 고정소수점 정밀도 척추(QFixed). §1 산술·역연산 §2 오버플로 트랩·무할당(δ=0)
    //   §3 틱 정렬·보수 편향(거래소 유리) §4 독약→ForceFreeze fail-closed.
    public sealed class S27DecimalSweepTests
    {
        // ═══════════════════ §1 산술 ═══════════════════

        [Fact]
        public void RoundTrip_Decimal_NoDrift()
        {
            Assert.Equal(2.555m, QQty.Of(2.555m).ToDecimal());
            Assert.Equal(4.055m, (QQty.Of(2.555m) + QQty.Of(1.5m)).ToDecimal());
            Assert.Equal(98370000m, QPrice.Of(98370000m).ToDecimal());
        }

        [Fact]
        public void CrossMul_Notional_And_Inverse()
        {
            var notional = QPrice.Of(2_000_000m) * QQty.Of(2.555m);   // 2,000,000 × 2.555 = 5,110,000
            Assert.Equal(5_110_000m, notional.ToDecimal());
            Assert.Equal(2.555m, (notional / QPrice.Of(2_000_000m)).ToDecimal());   // 금액÷단가 = 수량
        }

        [Fact]
        public void CrossMul_BareOperator_FloorsSubTick()
        {
            // 바 연산자(QPrice*QQty)는 0 방향 절사(floor). 정확곱 16자리 → 8자리 잔여 버림.
            var m = QPrice.Of(100.10m) * QQty.Of(1.05500001m);   // 정확 105.6055010010 → floor 105.605501
            Assert.Equal(105.605501m, m.ToDecimal());
        }

        [Fact]
        public void Clear_SubTickNotional_BuyUp_SellDown_ExchangeFavoured()
        {
            // ★적대적 리뷰(major) 회귀 — 정확히 나눠떨어지지 않는 명목곱의 서브-틱 잔여 방향 검증.
            //   매수=올림(비용↑)·매도=내림(수령↓). 곱 절사가 보수 가격정렬(AlignUp)을 무효화하지 않음을 1 raw 차로 증명.
            var att = new OrderAttestationService();
            var price = QPrice.Of(100.10m); var qty = QQty.Of(1.05500001m);
            var tick = new QPrice(1L); var step = new QQty(1L);   // 1e-8 — 입력 정렬 무변
            Assert.True(QLedgerGuard.TryClearNotional(price, qty, tick, step, OrderSide.Buy,  att, out var nb, out _, out _));
            Assert.True(QLedgerGuard.TryClearNotional(price, qty, tick, step, OrderSide.Sell, att, out var ns, out _, out _));
            Assert.Equal(1L, nb.Raw - ns.Raw);             // ceil vs floor — 정확히 1 raw(1e-8) 차
            Assert.Equal(105.60550101m, nb.ToDecimal());   // 매수 올림(비용↑)
            Assert.Equal(105.605501m,   ns.ToDecimal());   // 매도 내림(수령↓)
            Assert.True(nb.Raw > ns.Raw);                  // 매수 명목 > 매도 명목(거래소 유리)
        }

        // ═══════════════════ §2 오버플로 트랩 · 무할당 ═══════════════════

        [Fact]
        public void CrossMul_Overflow_Trapped()
            => Assert.Throws<OverflowException>(() =>
            {
                var _ = new QPrice(5_000_000_000_000_000L) * new QQty(5_000_000_000_000_000L);   // Int128/Scale 후에도 long 초과
            });

        [Fact]
        public void Add_Overflow_Trapped()
            => Assert.Throws<OverflowException>(() => { var _ = new QQty(long.MaxValue) + new QQty(1L); });

        [Fact]
        public void Arithmetic_ZeroAllocation()
        {
            // ★정직성 규율(v4.31.0 계승) — 10k 워밍업으로 tiered JIT 메트릭 교란 배제 후 측정. 값 타입 in/out·Int128
            //   중간 → 박싱·힙 0. 누적 오버플로 회피 위해 XOR 싱크(증가 없음)로 결과 사용(최적화 제거 방지).
            var p = QPrice.Of(98_370_000m); var q = QQty.Of(2.555m);
            var tick = QPrice.Of(1000m); var step = QQty.Of(0.001m);
            for (int i = 0; i < 10_000; i++) { var _ = p.ConservativeAlign(tick, OrderSide.Buy) * q.AlignDown(step); }

            long before = GC.GetAllocatedBytesForCurrentThread();
            long sink = 0;
            for (int i = 0; i < 50_000; i++)
            {
                var m = p.ConservativeAlign(tick, OrderSide.Sell) * q.AlignDown(step);
                sink ^= m.Raw;
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(sink != unchecked((long)0xDEADBEEF));   // 사용(JIT dead-code 제거 방지)
            Assert.Equal(0, delta);
        }

        // ═══════════════════ §3 틱 정렬 · 보수 편향 ═══════════════════

        [Fact]
        public void Alignment_Predicate_FloorCeil()
        {
            var step = QQty.Of(0.001m);
            Assert.False(QQty.Of(1.0559m).IsAligned(step));
            Assert.True(QQty.Of(1.055m).IsAligned(step));
            Assert.Equal(1.055m, QQty.Of(1.0559m).AlignDown(step).ToDecimal());
            Assert.Equal(1.056m, QQty.Of(1.0551m).AlignUp(step).ToDecimal());
        }

        [Fact]
        public void Conservative_Buy_Up_Sell_Down_ExchangeFavoured()
        {
            var tick = QPrice.Of(0.05m);
            var p = QPrice.Of(100.07m);   // 그리드 100.05 / 100.10 사이
            Assert.Equal(100.10m, p.ConservativeAlign(tick, OrderSide.Buy).ToDecimal());    // 매수 올림(비용↑)
            Assert.Equal(100.05m, p.ConservativeAlign(tick, OrderSide.Sell).ToDecimal());   // 매도 내림(수령↓)
        }

        [Fact]
        public void NoPhantomLeak_AlignedSum_StaysAligned_DownNeverOverstates()
        {
            var step = QQty.Of(0.001m);
            var sum = QQty.Of(1.111m) + QQty.Of(2.222m) + QQty.Of(0.667m);
            Assert.True(sum.IsAligned(step));
            Assert.Equal(4.0m, sum.ToDecimal());

            var snapped = QQty.Of(3.9999m).AlignDown(step);   // 비정렬 → 내림으로 그리드 고정
            Assert.True(snapped <= QQty.Of(3.9999m));         // 과대 0(유령 누수 차단)
            Assert.True(snapped.IsAligned(step));
            Assert.Equal(3.999m, snapped.ToDecimal());
        }

        // ═══════════════════ §4 독약 → ForceFreeze (fail-closed) ═══════════════════

        [Fact]
        public void Clear_HappyPath_ConservativeAligned_NoFreeze()
        {
            var att = new OrderAttestationService();
            bool ok = QLedgerGuard.TryClearNotional(
                QPrice.Of(100.07m), QQty.Of(1.0559m), QPrice.Of(0.05m), QQty.Of(0.001m),
                OrderSide.Buy, att, out var notional, out var ap, out var aq);
            Assert.True(ok);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(100.10m, ap.ToDecimal());     // 매수 올림
            Assert.Equal(1.055m, aq.ToDecimal());       // 수량 내림
            Assert.Equal(105.6055m, notional.ToDecimal());
        }

        [Fact]
        public void Poison_NegativeQty_InputGate_ForcesFreeze()
        {
            // 음수 수량은 TryClearNotional 의 IsValid 입력 게이트가 Align 이전에 선차단 → freeze(정직: Align poison 경로 아님).
            var att = new OrderAttestationService();
            bool ok = QLedgerGuard.TryClearNotional(
                QPrice.Of(100m), new QQty(-5L), QPrice.Of(0.01m), QQty.Of(0.001m),
                OrderSide.Buy, att, out _, out _, out _);
            Assert.False(ok);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void CrossMul_NegativeRaw_Trapped()
            => Assert.Throws<ArgumentOutOfRangeException>(() => { var _ = new QPrice(-100L) * new QQty(5L); });   // 음수 fail-closed(AlignDown 과 대칭)

        [Fact]
        public void Poison_ZeroStep_ForcesFreeze()
        {
            var att = new OrderAttestationService();
            bool ok = QLedgerGuard.TryClearNotional(
                QPrice.Of(100m), QQty.Of(1m), QPrice.Of(0.01m), new QQty(0L),
                OrderSide.Buy, att, out _, out _, out _);
            Assert.False(ok);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Poison_OverflowNotional_ForcesFreeze()
        {
            var att = new OrderAttestationService();
            bool ok = QLedgerGuard.TryClearNotional(
                new QPrice(5_000_000_000_000_000L), new QQty(5_000_000_000_000_000L),
                new QPrice(1L), new QQty(1L), OrderSide.Sell, att, out _, out _, out _);
            Assert.False(ok);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 정밀도 오버플로 → 확산 아닌 즉시 동결
        }
    }
}
