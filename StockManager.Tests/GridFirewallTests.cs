// ============================================================================
// GridFirewallTests — 과제 2.3: 격자 오염 방화벽(QScaleGuard 2중 검증) at FX 청산 경계
//
//  §1 정상 통과(1)   — gate·registry 격자 일치 시 기존 결과와 동일(2중 펜스 통과).
//  §2 방화벽 포착(2) — gate↔registry 격자 디싱크(틱 불일치·미등록) → ForceFreeze + false.
// ============================================================================
using Xunit;

namespace StockManager.Tests
{
    public sealed class GridFirewallTests
    {
        private static QFxQuote Quote(decimal r) => new(QFxRate.Of(r), QFxRate.Of(r));
        private static QFxBounds Bounds() => QFxBounds.Of(1000m, 2000m, 10m);

        [Fact]
        public void Guarded_Valid_MatchesBaseClearing()
        {
            var att = new OrderAttestationService();
            var registry = BuiltInTickSpecs.BuildDefaultRegistry();
            var gate = new ShadowCutoverGate(enabled: false, registry);

            // AAPL(USD 0.01틱) 195.00 × 10 × 1330 = 2,593,500 KRW.
            Assert.True(KrxFxClearing.TryClearUsdToKrwBaseGuarded(
                gate, registry, att, "AAPL", 195.00m, 10m, OrderSide.Buy, Quote(1330m), Bounds(), out var baseKrw));
            Assert.Equal(2_593_500m, baseKrw);
            Assert.Equal(GatingMode.Normal, att.Mode);   // 2중 펜스 모두 통과 — 무동결
        }

        [Fact]
        public void Guarded_GridTickDesync_FirewallFreezes()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: false, BuiltInTickSpecs.BuildDefaultRegistry());  // AAPL 0.01
            // ★방화벽 레지스트리는 AAPL 을 0.07 틱으로 본다(게이트와 격자 디싱크 모사).
            var fwReg = new QTickRegistry(new (string, QTickSpec)[]
            {
                ("AAPL", new QTickSpec(QPrice.Of(0.07m), QQty.Of(1m))),
            });

            Assert.False(KrxFxClearing.TryClearUsdToKrwBaseGuarded(
                gate, fwReg, att, "AAPL", 195.00m, 10m, OrderSide.Buy, Quote(1330m), Bounds(), out var baseKrw));
            Assert.Equal(0m, baseKrw);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 격자 비정렬 → 방화벽 동결
        }

        [Fact]
        public void Guarded_UnregisteredInFirewallRegistry_Freezes()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: false, BuiltInTickSpecs.BuildDefaultRegistry());
            // 방화벽 레지스트리에 AAPL 부재(레지스트리 디싱크).
            var fwReg = new QTickRegistry(new (string, QTickSpec)[]
            {
                ("005930", new QTickSpec(QPrice.Of(100m), QQty.Of(1m))),
            });

            Assert.False(KrxFxClearing.TryClearUsdToKrwBaseGuarded(
                gate, fwReg, att, "AAPL", 195.00m, 10m, OrderSide.Buy, Quote(1330m), Bounds(), out var baseKrw));
            Assert.Equal(0m, baseKrw);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }
    }
}
