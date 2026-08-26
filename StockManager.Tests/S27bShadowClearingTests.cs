using System;
using Xunit;

namespace StockManager.Tests
{
    // S27-b — 청산 마이그레이션 골격. §1 ACL fail-closed 경계 §2 멀티-스케일 정합(전 분기) §3 Shadow(보수 불변식+회귀 밴드).
    //   ★적대적 리뷰 수정 반영: 동어반복 제거(naive decimal 대비), ACL Try fail-closed, 미검증 freeze 분기 전수 단언.
    public sealed class S27bShadowClearingTests
    {
        private static QTickSpec Spec(decimal tick, decimal step) => new(QPrice.Of(tick), QQty.Of(step));

        // ═══════════════════ §1 ACL (fail-closed 경계) ═══════════════════

        [Fact]
        public void Acl_TryImport_Positive_RoundTrip()
        {
            Assert.True(QAcl.TryImportPrice(98_370_000m, null, out var p));
            Assert.Equal(98_370_000m, QAcl.ExportPrice(p));
            Assert.True(QAcl.TryImportQty(2.555m, null, out var q));
            Assert.Equal(2.555m, QAcl.ExportQty(q));
        }

        [Fact]
        public void Acl_TryImport_Negative_Freezes()
        {
            var att = new OrderAttestationService();
            Assert.False(QAcl.TryImportPrice(-5m, att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            var att2 = new OrderAttestationService();
            Assert.False(QAcl.TryImportQty(-0.001m, att2, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att2.Mode);
        }

        [Fact]
        public void Acl_TryImport_Overflow_Freezes()
        {
            var att = new OrderAttestationService();
            Assert.False(QAcl.TryImportPrice(1_000_000_000_000m, att, out _));   // ×10^8 → long 초과
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Acl_TryImport_ZeroAllocation()
        {
            decimal pv = 98_370_000m, qv = 2.555m;
            for (int i = 0; i < 10_000; i++) { QAcl.TryImportPrice(pv, null, out var p); QAcl.TryImportQty(qv, null, out var q); var _ = QAcl.ExportMoney(p * q); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            decimal sink = 0m;
            for (int i = 0; i < 50_000; i++) { QAcl.TryImportPrice(pv, null, out var p); QAcl.TryImportQty(qv, null, out var q); sink = QAcl.ExportMoney(p * q); }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(sink > 0m);
            Assert.Equal(0, delta);   // Try 패턴(정상 경로)·값 변환 — 박싱·힙 0
        }

        // ═══════════════════ §2 멀티-스케일 정합 (전 freeze 분기 비공허 검증) ═══════════════════

        [Fact]
        public void ScaleGuard_Consistent_NoFreeze()
        {
            var att = new OrderAttestationService();
            var price = QPrice.Of(2_000_000m); var qty = QQty.Of(2.555m); var spec = Spec(1m, 0.001m);
            Assert.True(QScaleGuard.ReconcileOrFreeze(price, qty, price * qty, spec, att));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Theory]
        [InlineData(100)]   // 거대 스케일 폭주
        [InlineData(2)]     // 중간 2x — 밴드 사이로 새지 않음(리뷰 반증)
        public void ScaleGuard_ScaleCollision_Freezes(int factor)
        {
            var att = new OrderAttestationService();
            var price = QPrice.Of(2_000_000m); var qty = QQty.Of(2.555m); var spec = Spec(1m, 0.001m);
            var corrupt = new QMoney((price * qty).Raw * factor);
            Assert.False(QScaleGuard.ReconcileOrFreeze(price, qty, corrupt, spec, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ScaleGuard_MisalignedPrice_Direct_Freezes()
        {
            // 비정렬 price 직접 주입 → IsAligned freeze 분기(standalone API 방어) 비공허 검증.
            var att = new OrderAttestationService();
            var price = new QPrice(10_010_000_001L);   // tick(0.01=raw 1e6)의 정수배 아님
            var qty = QQty.Of(1m); var spec = Spec(0.01m, 0.0001m);
            Assert.False(QScaleGuard.ReconcileOrFreeze(price, qty, price * qty, spec, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ScaleGuard_DriftExactlyOneStep_Freezes()
        {
            // ★off-by-one — back 이 정확히 1 step 어긋남(drift==step.Raw)도 동결(>= 경계).
            var att = new OrderAttestationService();
            var price = QPrice.Of(2m); var qty = QQty.Of(1m); var spec = Spec(1m, 1m);
            var corrupt = new QMoney(4 * QPrice.Of(1m).Raw);   // raw 4e8 → back=2e8 → drift=1e8=step.Raw
            Assert.False(QScaleGuard.ReconcileOrFreeze(price, qty, corrupt, spec, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ScaleGuard_NegativeNotional_Trap_Freezes()
        {
            // 음수 명목 → CrossDiv 음수 트랩(ArgumentOutOfRange) → catch → freeze.
            var att = new OrderAttestationService();
            var price = QPrice.Of(2m); var qty = QQty.Of(1m); var spec = Spec(1m, 1m);
            Assert.False(QScaleGuard.ReconcileOrFreeze(price, qty, new QMoney(-1L), spec, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §3 Shadow Tracing ═══════════════════

        [Fact]
        public void Shadow_AlignedInputs_Parity_NoFreeze()
        {
            var att = new OrderAttestationService();
            Assert.True(ShadowClearingTracer.TraceOrFreeze(100.10m, 1.0559m, OrderSide.Buy, Spec(0.01m, 0.0001m), att, out var shadow));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(shadow.Raw > 0);
        }

        [Fact]
        public void Shadow_UnalignedInputs_WithinBand_NoFreeze()
        {
            // 비정렬 원입력 — 정렬 델타가 발생하나 band(틱×수량+스텝×단가) 내라 통과(naive 대비 회귀 가드 비공허).
            var att = new OrderAttestationService();
            Assert.True(ShadowClearingTracer.TraceOrFreeze(100.107m, 1.05599m, OrderSide.Buy, Spec(0.01m, 0.0001m), att, out _));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_NegativeInput_ImportFreezes()
        {
            var att = new OrderAttestationService();
            Assert.False(ShadowClearingTracer.TraceOrFreeze(-1m, 1m, OrderSide.Buy, Spec(0.01m, 0.0001m), att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_OverflowInput_Freezes()
        {
            var att = new OrderAttestationService();
            Assert.False(ShadowClearingTracer.TraceOrFreeze(1_000_000_000_000m, 1m, OrderSide.Buy, Spec(0.01m, 0.0001m), att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_ConservativeReversal_Buy_Freezes()
        {
            // 매수 보수가 정렬 정확곱보다 낮음 = ceil 위반·플랫폼 유리 → 동결.
            var att = new OrderAttestationService();
            Assert.False(ShadowClearingTracer.CompareConservativeOrFreeze(99.99999999m, 100m, OrderSide.Buy, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_ConservativeReversal_Sell_Freezes()
        {
            var att = new OrderAttestationService();
            Assert.False(ShadowClearingTracer.CompareConservativeOrFreeze(100.00000001m, 100m, OrderSide.Sell, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_Conservative_Within1Raw_Passes()
        {
            var att = new OrderAttestationService();
            Assert.True(ShadowClearingTracer.CompareConservativeOrFreeze(100.00000001m, 100m, OrderSide.Buy, att));   // 매수 ceil·1raw 내
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_LegacyBand_Divergence_Freezes()
        {
            // 신·구 명목이 정렬델타 밴드를 초과 발산(엔진 분기) → 동결.
            var att = new OrderAttestationService();
            Assert.False(ShadowClearingTracer.CompareLegacyBandOrFreeze(150m, 100m, 10m, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Shadow_LegacyBand_Within_Passes()
        {
            var att = new OrderAttestationService();
            Assert.True(ShadowClearingTracer.CompareLegacyBandOrFreeze(105m, 100m, 10m, att));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }
    }
}
