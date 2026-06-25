using System;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S29-c FX 권위 역전(base 통화 계약) — ClearBase.
    //   §1 Legacy base(naive 자산명목×환율) §2 QFixed base(정렬 shadowBase 승격) §3 FX 오염 백스톱 동결
    //   §4 자산통화 Clear 후방호환 불변 §5 무할당.
    public sealed class S29cFxAuthorityTests
    {
        private static QTickRegistry Reg() => new(new[]
        {
            ("AAPL", new QTickSpec(QPrice.Of(0.01m), QQty.Of(1m))),   // USD 센트
        });
        private static QFxQuote Fx()      => new(QFxRate.Of(1330m), QFxRate.Of(1331m));   // KRW/USD Bid/Ask
        private static QFxBounds Bounds() => QFxBounds.Of(500m, 3000m, 50m);

        // ═══════════════════ §1 Legacy base ═══════════════════

        [Fact]
        public void ClearBase_LegacyPrimary_ReturnsNaiveBase()
        {
            // 기본 권위 = Legacy → base 백스톱(naive 자산명목 × 환율). 매수=Ask 1331.
            var gate = new ShadowCutoverGate(true, Reg(), new OrderAttestationService());
            decimal result = gate.ClearBase("AAPL", 100.005m, 1m, OrderSide.Buy, Fx(), Bounds());
            Assert.Equal(133106.655m, result);   // 100.005 × 1331 (naive base)
        }

        // ═══════════════════ §2 QFixed base ═══════════════════

        [Fact]
        public void ClearBase_QFixedPrimary_PromotesAlignedShadowBase()
        {
            // ★FX 권위 역전 — QFixed: 자산 정렬(100.005→매수올림 100.01) × Ask 1331 = 133113.31(base). naive 아님.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att, ClearingAuthority.QFixedPrimary);
            decimal result = gate.ClearBase("AAPL", 100.005m, 1m, OrderSide.Buy, Fx(), Bounds());
            Assert.Equal(133113.31m, result);     // 100.01 × 1331 (정렬 base 권위)
            Assert.NotEqual(133106.655m, result); // naive base 아님
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ClearBase_QFixedPrimary_SellUsesBid()
        {
            // 매도는 Bid(1330)·자산 내림(100.005→100.00) → 100.00 × 1330 = 133000(base).
            var gate = new ShadowCutoverGate(true, Reg(), new OrderAttestationService(), ClearingAuthority.QFixedPrimary);
            Assert.Equal(133000m, gate.ClearBase("AAPL", 100.005m, 1m, OrderSide.Sell, Fx(), Bounds()));
        }

        // ═══════════════════ §3 FX 오염 백스톱 동결 ═══════════════════

        [Fact]
        public void ClearBase_QFixedPrimary_FxSkew_BackstopFreezes()
        {
            // 환율 역전(Ask<Bid 스큐) → TraceFxOrFreeze 가 fail-closed 동결 → degraded legacyBase 반환·파이프라인 정지.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att, ClearingAuthority.QFixedPrimary);
            var skew = new QFxQuote(QFxRate.Of(1331m), QFxRate.Of(1330m));   // Ask<Bid
            gate.ClearBase("AAPL", 100.00m, 1m, OrderSide.Buy, skew, Bounds());
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
            Assert.True(gate.FrozenCount > 0);
        }

        [Fact]
        public void ClearBase_Unregistered_FailClosed()
        {
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att, ClearingAuthority.QFixedPrimary);
            // 미등록 심볼 → 백스톱 base 반환 + 동결(검증 불가 자산 권위 통과 금지).
            decimal result = gate.ClearBase("UNKNOWN", 100m, 1m, OrderSide.Buy, Fx(), Bounds());
            Assert.Equal(133100m, result);   // 100 × 1331 (degraded base)
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §4 자산통화 Clear 후방호환 ═══════════════════

        [Fact]
        public void AssetCurrencyFxClear_IgnoresAuthority_BackwardCompat()
        {
            // 기존 자산통화 FX Clear 는 권위와 무관하게 '자산통화 legacy' 반환(단위 불변) — S27-e 계약 보존.
            var gate = new ShadowCutoverGate(true, Reg(), new OrderAttestationService(), ClearingAuthority.QFixedPrimary);
            decimal asset = gate.Clear("AAPL", 100.005m, 1m, OrderSide.Buy, Fx(), Bounds());
            Assert.Equal(100.005m, asset);   // 자산통화 naive(base 아님·shadow 아님)
        }

        // ═══════════════════ §5 무할당 ═══════════════════

        [Fact]
        public void ClearBase_ZeroAllocation_HotPath()
        {
            var gate = new ShadowCutoverGate(true, Reg(), new OrderAttestationService(), ClearingAuthority.QFixedPrimary);
            var fx = Fx(); var b = Bounds();
            for (int i = 0; i < 10_000; i++) gate.ClearBase("AAPL", 100.00m, 1m, OrderSide.Buy, fx, b);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++) gate.ClearBase("AAPL", 100.00m, 1m, OrderSide.Buy, fx, b);
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(delta < 50_000, $"통과 경로 per-call 힙 0(JIT 노이즈만) — delta={delta}");
        }

        // ═══════════════════ v4.60.0 Path Alpha 컷오버 배선 핀포인트 박제 ═══════════════════

        [Fact]
        public void AuthorityInversion_WhenQFixedPrimary_UsesQFixedAsAuthority()
        {
            // 스위치 ON(QFixedPrimary) — ClearBase 가 QFixed 보수 정렬값을 최종 권위(base 통화)로 채택. 매수 ceil·매도 floor.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att, ClearingAuthority.QFixedPrimary);

            // 매수: 자산 100.005 → 센트 올림 100.01 × Ask 1331 = 133113.31 (정렬 base 권위, naive 133106.655 아님)
            decimal buy = gate.ClearBase("AAPL", 100.005m, 1m, OrderSide.Buy, Fx(), Bounds());
            Assert.Equal(133113.31m, buy);
            Assert.NotEqual(133106.655m, buy);

            // 매도: 자산 100.005 → 센트 내림 100.00 × Bid 1330 = 133000 (보수 floor)
            decimal sell = gate.ClearBase("AAPL", 100.005m, 1m, OrderSide.Sell, Fx(), Bounds());
            Assert.Equal(133000m, sell);

            Assert.True(buy > sell);                              // 보수 편향(매수↑·매도↓)이 권위값으로 흐름
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void AuthorityInversion_FX_Clear_RemainsBackwardCompatible()
        {
            // 스위치 ON 이라도 일반 자산통화 Clear(fx) 는 권위 역전 미적용 — 자산통화 naive 반환(단위/계약 불변·base 아님).
            var legacy = new ShadowCutoverGate(true, Reg(), new OrderAttestationService());                                 // Legacy
            var qfixed = new ShadowCutoverGate(true, Reg(), new OrderAttestationService(), ClearingAuthority.QFixedPrimary); // QFixed ON

            decimal underLegacy = legacy.Clear("AAPL", 100.005m, 1m, OrderSide.Buy, Fx(), Bounds());
            decimal underQFixed = qfixed.Clear("AAPL", 100.005m, 1m, OrderSide.Buy, Fx(), Bounds());

            Assert.Equal(100.005m, underLegacy);     // 자산통화 naive
            Assert.Equal(100.005m, underQFixed);     // 권위 ON 이어도 동일 — FX Clear 격리 불변식
            Assert.Equal(underLegacy, underQFixed);  // 권위 역전이 자산통화 Clear 에 영향 0
        }
    }
}
