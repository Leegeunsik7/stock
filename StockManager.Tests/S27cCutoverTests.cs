using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    // S27-c — 단계적 섀도우 컷오버 게이트. §1 INERT(기본 OFF·무영향) §2 enabled 병렬 대조·동결 §3 무할당·락-프리 동시성.
    public sealed class S27cCutoverTests
    {
        private static QTickSpec Spec() => new(QPrice.Of(0.01m), QQty.Of(0.0001m));

        // ═══════════════════ §1 INERT (기본 OFF) ═══════════════════

        [Fact]
        public void Disabled_ReturnsLegacy_NoShadow_NoFreeze()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: false, Spec(), att);
            Assert.Equal(100.10m * 1.0559m, gate.Clear(100.10m, 1.0559m, OrderSide.Buy));   // 레거시 그대로
            Assert.Equal(0, gate.Observed);                                                  // 섀도우 미수행
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Disabled_PoisonInput_StillInert_NoFreeze()
        {
            // INERT 는 레거시 동작을 1비트도 안 바꾼다 — 음수 입력도 레거시처럼 그대로 통과(동결 안 함).
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: false, Spec(), att);
            Assert.Equal(100m * -5m, gate.Clear(100m, -5m, OrderSide.Buy));
            Assert.Equal(0, gate.Observed);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §2 enabled 병렬 대조 ═══════════════════

        [Fact]
        public void Enabled_Parity_ReturnsLegacy_ObservesNoFreeze()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: true, Spec(), att);
            Assert.Equal(100.10m * 1.0559m, gate.Clear(100.10m, 1.0559m, OrderSide.Buy));   // 라이브 반환은 항상 레거시
            Assert.Equal(1, gate.Observed);
            Assert.Equal(0, gate.FrozenCount);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Enabled_PoisonInput_Freezes_ButReturnsLegacy()
        {
            // 음수 수량 → 신 엔진 ACL 경계 fail-closed → ForceFreeze. 단 라이브 반환은 레거시(컷오버 전 권위 불변).
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: true, Spec(), att);
            decimal ret = gate.Clear(100m, -5m, OrderSide.Buy);
            Assert.Equal(100m * -5m, ret);
            Assert.Equal(1, gate.Observed);
            Assert.Equal(1, gate.FrozenCount);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Enabled_LastDivergence_Tracked()
        {
            var gate = new ShadowCutoverGate(enabled: true, Spec(), new OrderAttestationService());
            gate.Clear(100.10m, 1.0559m, OrderSide.Buy);
            Assert.True(Math.Abs(gate.LastDivergence) < 0.01);   // 정렬 입력 — 신·구 차 ≈0(보수 1raw 이내)
        }

        // ═══════════════════ §3 무할당 · 락-프리 동시성 ═══════════════════

        [Fact]
        public void Disabled_ZeroAllocation()
        {
            var gate = new ShadowCutoverGate(enabled: false, Spec(), null);
            for (int i = 0; i < 10_000; i++) { var _ = gate.Clear(100.10m, 1.0559m, OrderSide.Buy); }
            decimal sink = 0m;
            Assert.Equal(0, ZeroAllocProbe.MinAllocated(() =>
            {
                sink = 0m;
                for (int i = 0; i < 50_000; i++) sink = gate.Clear(100.10m, 1.0559m, OrderSide.Buy);
                Assert.True(sink > 0m);
            }));
        }

        [Fact]
        public void Enabled_ZeroAllocation()
        {
            var gate = new ShadowCutoverGate(enabled: true, Spec(), new OrderAttestationService());
            for (int i = 0; i < 10_000; i++) { var _ = gate.Clear(100.10m, 1.0559m, OrderSide.Sell); }   // 워밍업
            decimal sink = 0m;
            Assert.Equal(0, ZeroAllocProbe.MinAllocated(() =>
            {
                sink = 0m;
                for (int i = 0; i < 50_000; i++) sink = gate.Clear(100.10m, 1.0559m, OrderSide.Sell);
                Assert.True(sink > 0m);
            })); // 신·구 병렬 대조 핫패스 δ=0
        }

        // DI 기본 spec(BlazorHomeForm) — KRW 정수단가 전용 격자.
        private static QTickSpec KrwIntegerSpec() => new(QPrice.Of(1m), QQty.Of(0.00000001m));

        [Fact]
        public void Overflow_ThrowsIdentically_EnabledOrDisabled()
        {
            // ★적대적 리뷰(INERT 엣지) — Clear 는 legacy 곱을 _enabled 검사 전 실행하므로, 거대 입력 decimal
            //   오버플로는 enabled/disabled 모두 동일 throw(라이브 포트폴리오 CurrentPrice*Quantity 와 동치).
            var off = new ShadowCutoverGate(false, Spec(), null);
            var on  = new ShadowCutoverGate(true,  Spec(), new OrderAttestationService());
            Assert.Throws<OverflowException>(() => off.Clear(decimal.MaxValue, 2m, OrderSide.Buy));
            Assert.Throws<OverflowException>(() => on.Clear(decimal.MaxValue, 2m, OrderSide.Buy));
        }

        [Fact]
        public void ProductionSpec_KrwInteger_DecimalPrice_SilentlyDistorts_NoFreeze()
        {
            // ★적대적 리뷰(멀티자산 spec) 회귀 잠금 — DI 기본 spec(tick=1원)에 소수단가($100.10)를 넣으면
            //   ConservativeAlign 이 IsAligned 보다 먼저 101원으로 '조용히 반올림'하고 밴드(틱×수량=1)가 0.90 델타를
            //   흡수해 동결하지 '않는다'. 즉 위험은 '거짓 동결'이 아니라 '왜곡 무동결' — 이 spec 을 USD/코인에 직접
            //   쓰면 안 되며 실 컷오버 전 자산별 spec 이 필수임을 명시 박제(리뷰 예측의 기제를 실거동으로 정정).
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, KrwIntegerSpec(), att);
            gate.Clear(100.10m, 1m, OrderSide.Buy);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);              // 동결 안 함(밴드 흡수)
            Assert.Equal(0, gate.FrozenCount);
            Assert.True(Math.Abs(gate.LastDivergence - 0.90) < 0.01);          // 신(101)−구(100.10)≈+0.90 왜곡 노출
        }

        [Fact]
        public void ProductionSpec_KrwInteger_IntegerPrice_Parity_NoFreeze()
        {
            // 대조 — 정수 KRW 단가는 1원 격자에 정렬돼 정상 패리티(왜곡 0·무동결). spec 이 KRW 정수에 한해 유효함 확인.
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, KrwIntegerSpec(), att);
            gate.Clear(70000m, 10m, OrderSide.Buy);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(0, gate.FrozenCount);
            Assert.True(Math.Abs(gate.LastDivergence) < 0.01);
        }

        [Fact]
        public void Enabled_ConcurrentClears_LockFree_ObservedExact()
        {
            // 락-프리 다중 라이터 — 4 스레드 동시 청산, Observed 카운트 무손실(Interlocked)·동결 0.
            var gate = new ShadowCutoverGate(enabled: true, Spec(), new OrderAttestationService());
            const int threads = 4, per = 25_000;
            var ts = new Thread[threads];
            for (int t = 0; t < threads; t++)
                ts[t] = new Thread(() => { for (int i = 0; i < per; i++) gate.Clear(100.10m, 1.0559m, OrderSide.Buy); }) { IsBackground = true };
            foreach (var th in ts) th.Start();
            foreach (var th in ts) Assert.True(th.Join(15000));
            Assert.Equal(threads * per, gate.Observed);
            Assert.Equal(0, gate.FrozenCount);
        }
    }
}
