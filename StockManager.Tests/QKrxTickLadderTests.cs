// ============================================================================
// QKrxTickLadderTests — v4.77.0: 자산별 QTickSpec(KRX 가격대 사다리 + USD + FX) 검증
//
//  §1 KRX 사다리(7)  — 가격대 경계('미만<' 규칙)별 호가단위·음수 fail-closed·USD 센트
//  §2 레지스트리(3)  — 보강 빌드·심볼 중복 fail-fast·KRX+USD 정확 빌드
//  §3 FX fail-closed(5) — ★적대검증 교정 박제: 게이트 INERT(기본 OFF)에서도
//      Ask<Bid·미등록 심볼·음수·바운드 이탈을 독립 검증으로 차단(fail-open 제거),
//      정상 USD→KRW base 보수 floor 청산 통과
// ============================================================================
using System;
using Xunit;

namespace StockManager.Tests
{
    public sealed class QKrxTickLadderTests
    {
        // ════════════════════════ §1 KRX 사다리 ═══════════════════════════════════

        [Theory]
        [InlineData(1_500, 1)]       // ~2천 미만 → 1원
        [InlineData(1_999, 1)]
        [InlineData(2_000, 5)]       // 2천 경계('미만<') → 5원
        [InlineData(4_999, 5)]
        [InlineData(5_000, 10)]      // 5천 → 10원
        [InlineData(19_999, 10)]
        [InlineData(20_000, 50)]     // 2만 → 50원
        [InlineData(49_999, 50)]
        [InlineData(50_000, 100)]    // 5만 → 100원
        [InlineData(199_999, 100)]
        [InlineData(200_000, 500)]   // 20만 → 500원
        [InlineData(499_999, 500)]
        [InlineData(500_000, 1_000)] // 50만 이상 → 1000원
        [InlineData(3_000_000, 1_000)]
        public void TickWonFor_GradientBoundaries(int priceWon, int expectedTick)
            => Assert.Equal(expectedTick, (int)KrxTickLadder.TickWonFor(priceWon));

        [Fact]
        public void TickWonFor_Negative_ThrowsFailClosed()
            => Assert.Throws<ArgumentOutOfRangeException>(() => KrxTickLadder.TickWonFor(-1m));

        [Fact]
        public void TickWonFor_Zero_IsMinTick()
            => Assert.Equal(1m, KrxTickLadder.TickWonFor(0m));   // 0원 → 첫 구간 1원(경계 안전)

        [Fact]
        public void UsdSpec_IsCentGrid()
        {
            var spec = KrxTickLadder.UsdSpec();
            Assert.Equal(0.01m, spec.Tick.ToDecimal());
        }

        [Fact]
        public void SpecFor_UsesGradientTick()
        {
            Assert.Equal(100m, KrxTickLadder.SpecFor(80_000m).Tick.ToDecimal());   // 8만 → 100원
            Assert.Equal(500m, KrxTickLadder.SpecFor(230_000m).Tick.ToDecimal());  // 23만 → 500원
        }

        // ════════════════════════ §2 레지스트리 ═══════════════════════════════════

        [Fact]
        public void BuildAugmentedRegistry_AddsNewSymbol()
        {
            var reg = KrxTickSpecsExtensions.BuildAugmentedRegistry(new[] { ("068270", 180_000m) });
            Assert.True(reg.TryGet("068270", out var spec));
            Assert.Equal(100m, spec.Tick.ToDecimal());     // 18만 → 5만~20만 → 100원
            Assert.True(reg.TryGet("005930", out _));      // DefaultEntries 보존
        }

        [Fact]
        public void BuildAugmentedRegistry_DuplicateSymbol_FailFast()
            // 005930 은 DefaultEntries 에 이미 존재 → FNV 충돌 fail-fast
            => Assert.ThrowsAny<Exception>(
                () => KrxTickSpecsExtensions.BuildAugmentedRegistry(new[] { ("005930", 80_000m) }));

        [Fact]
        public void BuildKrxAccurateRegistry_KrxAndUsd()
        {
            var reg = KrxTickSpecsExtensions.BuildKrxAccurateRegistry(
                krxUniverse: new[] { ("005930", 80_000m) },
                usdUniverse: new[] { "AAPL" });
            Assert.True(reg.TryGet("005930", out var krw));
            Assert.Equal(100m, krw.Tick.ToDecimal());
            Assert.True(reg.TryGet("AAPL", out var usd));
            Assert.Equal(0.01m, usd.Tick.ToDecimal());
        }

        // ════════════════════════ §3 FX 청산 fail-closed ══════════════════════════
        //  ★게이트 INERT(enabled=false·LegacyPrimary, 기본 구성)에서도 독립 검증으로 닫힘을 박제.
        //    적대검증 critical(ClearBase INERT 우회 fail-open) 교정의 회귀잠금.

        private static ShadowCutoverGate InertGate(out OrderAttestationService att)
        {
            att = new OrderAttestationService();
            var reg = KrxTickSpecsExtensions.BuildKrxAccurateRegistry(
                krxUniverse: Array.Empty<(string, decimal)>(),
                usdUniverse: new[] { "AAPL" });
            return new ShadowCutoverGate(enabled: false, reg, att);   // INERT 기본 구성
        }

        [Fact]
        public void Fx_NormalUsd_ConservativeBaseClears()
        {
            var gate = InertGate(out var att);
            var quote  = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1331m));   // Ask≥Bid 정상
            var bounds = QFxBounds.Of(800m, 2000m, 50m);
            bool ok = KrxFxClearing.TryClearUsdToKrwBase(
                gate, att, "AAPL", 195.50m, 10m, OrderSide.Buy, quote, bounds, out var krw);
            Assert.True(ok, "정상 USD 청산이 INERT 게이트에서 통과해야 함");
            Assert.True(krw > 0m);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Fx_InvertedRate_AskBelowBid_FreezesEvenWhenInert()
        {
            var gate = InertGate(out var att);
            var inverted = new QFxQuote(QFxRate.Of(1331m), QFxRate.Of(1330m));  // ★Ask<Bid 역전
            var bounds   = QFxBounds.Of(800m, 2000m, 50m);
            bool ok = KrxFxClearing.TryClearUsdToKrwBase(
                gate, att, "AAPL", 195.50m, 10m, OrderSide.Buy, inverted, bounds, out var krw);
            Assert.False(ok, "환율역전은 INERT 게이트에서도 fail-closed 여야 함(critical 교정)");
            Assert.Equal(0m, krw);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_UnregisteredSymbol_FreezesEvenWhenInert()
        {
            var gate = InertGate(out var att);
            var quote  = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1331m));
            var bounds = QFxBounds.Of(800m, 2000m, 50m);
            bool ok = KrxFxClearing.TryClearUsdToKrwBase(
                gate, att, "ZZZZ", 100m, 10m, OrderSide.Buy, quote, bounds, out _);
            Assert.False(ok, "미등록 심볼은 INERT 게이트에서도 fail-closed");
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_NegativePrice_FreezesEvenWhenInert()
        {
            var gate = InertGate(out var att);
            var quote  = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1331m));
            var bounds = QFxBounds.Of(800m, 2000m, 50m);
            bool ok = KrxFxClearing.TryClearUsdToKrwBase(
                gate, att, "AAPL", -1m, 10m, OrderSide.Buy, quote, bounds, out _);
            Assert.False(ok, "음수 단가는 QAcl 경계에서 fail-closed");
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_OutOfBoundsRate_FreezesEvenWhenInert()
        {
            var gate = InertGate(out var att);
            // 환율 5000 > MaxRate 2000 바운드 이탈
            var quote  = new QFxQuote(QFxRate.Of(4999m), QFxRate.Of(5000m));
            var bounds = QFxBounds.Of(800m, 2000m, 50m);
            bool ok = KrxFxClearing.TryClearUsdToKrwBase(
                gate, att, "AAPL", 100m, 10m, OrderSide.Buy, quote, bounds, out _);
            Assert.False(ok, "바운드 이탈 환율은 INERT 게이트에서도 fail-closed");
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }
    }
}
