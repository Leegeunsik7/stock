using System;
using System.Threading;
using Xunit;

namespace StockManager.Tests
{
    // S27-e — 멀티-자산 청산 게이트 실전 결속.
    //  §1 자산별 Clear(symbol) 격자 조회 + 미등록 fail-closed   §2 이종통화 FX 환산 경로 + Mission2 비항등 수렴 펜스
    //  §3 혼재 워크로드 동시성 + δ=0 무할당 + FNV 충돌 fail-fast
    public sealed class S27eMultiAssetGateTests
    {
        // KRW 정수 주식 · USD 소수 주식 · KRW 코인 — 단일 펌프에 극단 교차.
        private static QTickRegistry Registry() => new(new (string, QTickSpec)[]
        {
            ("005930",  new QTickSpec(QPrice.Of(1m),    QQty.Of(1m))),            // 코스피 정수
            ("AAPL",    new QTickSpec(QPrice.Of(0.01m), QQty.Of(0.0001m))),       // 나스닥 소수
            ("KRW-BTC", new QTickSpec(QPrice.Of(1000m), QQty.Of(0.00000001m))),   // 코인(원·사토시)
        });
        private static QFxQuote UsdKrw() => new(QFxRate.Of(1380m), QFxRate.Of(1380.5m));   // Ask≥Bid
        private static QFxBounds Bounds() => QFxBounds.Of(500m, 3000m, 50m);

        // ═══════════════════ §1 자산별 조회 · 미등록 fail-closed ═══════════════════

        [Fact]
        public void Registered_Aligned_ReturnsLegacy_ObservesNoFreeze()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Registry(), att);
            Assert.Equal(70000m * 10m, gate.Clear("005930", 70000m, 10m, OrderSide.Buy));   // 라이브 반환 = 레거시
            Assert.Equal(1, gate.Observed);
            Assert.Equal(0, gate.FrozenCount);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void UnregisteredSymbol_FailClosed_FreezesButReturnsLegacy()
        {
            // ★삼중 단언 — 미등록 심볼은 섀도우 검증 불가 → fail-closed: Observed 카운트·Frozen 트립·EmergencyFreeze 전이.
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Registry(), att);
            decimal ret = gate.Clear("GHOST999", 100m, 1m, OrderSide.Buy);
            Assert.Equal(100m, ret);                              // (1) 라이브 반환은 레거시 불변
            Assert.Equal(1, gate.FrozenCount);                    // (2) 오염 격리 — 동결 카운터 트립
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // (3) EmergencyFreeze 전이
        }

        [Fact]
        public void Disabled_MultiAsset_Inert_NoFreeze_NoObserve()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(false, Registry(), att);
            Assert.Equal(100m * 1m, gate.Clear("GHOST999", 100m, 1m, OrderSide.Buy));   // INERT — 미등록도 무동결
            Assert.Equal(0, gate.Observed);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §2 이종통화 FX 경로 + Mission2 비항등 펜스 ═══════════════════

        [Fact]
        public void FxClear_UsdAsset_Aligned_Parity_NoFreeze_BaseTelemetry()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Registry(), att);
            decimal ret = gate.Clear("AAPL", 100.00m, 2m, OrderSide.Buy, UsdKrw(), Bounds());
            Assert.Equal(200m, ret);                                       // 라이브 반환 = 자산통화 레거시(비권위)
            Assert.Equal(0, gate.FrozenCount);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
            // 텔레메트리는 base(KRW). AAPL 100.00×2 가 격자 정렬 + 보수 환산이 exact(200×Ask 1380.5=276100)와
            // 일치 → 발산은 '정확히 0'. <1.0 밴드는 always-0 회귀를 숨기므로 정확값으로 핀(적대적 리뷰 minor 정정).
            Assert.Equal(0.0, gate.LastDivergence, 6);
        }

        [Fact]
        public void FxClear_DegenerateBounds_FailClosed_Freezes()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Registry(), att);
            gate.Clear("AAPL", 100m, 2m, OrderSide.Buy, UsdKrw(), QFxBounds.Of(0m, 0m, 0m));   // 무효 바운드 → FX fail-closed
            Assert.Equal(1, gate.FrozenCount);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FxClear_SkewedQuote_AskBelowBid_Freezes()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Registry(), att);
            var skew = new QFxQuote(QFxRate.Of(1380m), QFxRate.Of(1370m));   // Ask<Bid 역전(스큐)
            gate.Clear("AAPL", 100m, 2m, OrderSide.Buy, skew, Bounds());
            Assert.Equal(1, gate.FrozenCount);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        // Mission 2 — FX 수렴 펜스가 '비항등'임을 직접 증명: 오염엔 발화, 정당엔 통과(둘 다 성립해야 비공허).
        [Fact]
        public void FxConvergenceFence_FiresOnInflatedDivergence_NotTautology()
        {
            var att = new OrderAttestationService();
            // exact = 10 × 2 = 20. got=1000(스케일 오염) → |1000−20|=980 ≫ band → 발화(동결). ⇒ 항상-참 아님.
            Assert.False(QFxGuard.VerifyConservativeFxOrFreeze(QMoney.Of(1000m), 10m, 2m, OrderSide.Buy, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FxConvergenceFence_BuyCeilViolation_Freezes()
        {
            var att = new OrderAttestationService();
            // 매수 exact=20, got=19 < exact → ceil 위반(플랫폼 유리) → 동결.
            Assert.False(QFxGuard.VerifyConservativeFxOrFreeze(QMoney.Of(19m), 10m, 2m, OrderSide.Buy, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FxConvergenceFence_SellFloorViolation_Freezes()
        {
            var att = new OrderAttestationService();
            // 매도 exact=20, got=21 > exact → floor 위반(플랫폼 유리) → 동결.
            Assert.False(QFxGuard.VerifyConservativeFxOrFreeze(QMoney.Of(21m), 10m, 2m, OrderSide.Sell, att));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FxConvergenceFence_NonPositiveRate_FailClosed_PublicApiSelfDefends()
        {
            // ★적대적 리뷰(minor) — 이 메서드는 public 독립 API. 상위 게이트 없이 직접 호출될 때 음수/0 환율이
            // 방향 검문을 뒤집지 못하도록 자체 fail-closed. (rate=0·rate<0 둘 다 동결, 방향 검문 도달 전.)
            var att0 = new OrderAttestationService();
            Assert.False(QFxGuard.VerifyConservativeFxOrFreeze(QMoney.Of(20m), 10m, 0m, OrderSide.Buy, att0));
            Assert.Equal(GatingMode.EmergencyFreeze, att0.Mode);

            var attNeg = new OrderAttestationService();
            Assert.False(QFxGuard.VerifyConservativeFxOrFreeze(QMoney.Of(20m), 10m, -1380m, OrderSide.Sell, attNeg));
            Assert.Equal(GatingMode.EmergencyFreeze, attNeg.Mode);
        }

        [Fact]
        public void FxConvergenceFence_ExactWithinBand_Passes_NoFreeze_NotAlwaysFalse()
        {
            var att = new OrderAttestationService();
            // exact=20, got=20(정확) → 통과(동결 안 함). ⇒ 항상-거짓 아님. 위 발화 케이스와 함께 '비항등' 입증.
            Assert.True(QFxGuard.VerifyConservativeFxOrFreeze(QMoney.Of(20m), 10m, 2m, OrderSide.Buy, att));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §3 혼재 워크로드 동시성 · δ=0 · FNV 충돌 fail-fast ═══════════════════

        [Fact]
        public void MixedWorkload_KrwIntegerAndUsdDecimal_Concurrent_NoFreeze_ObservedExact()
        {
            // USD 소수 자산 + KRW 정수 자산이 단일 백그라운드 펌프에서 극단 교차. 락-프리 카운트 무손실·동결 0.
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Registry(), att);
            const int threads = 6, per = 20_000;
            var ts = new Thread[threads];
            for (int t = 0; t < threads; t++)
                ts[t] = new Thread(() =>
                {
                    for (int i = 0; i < per; i++)
                    {
                        if ((i & 1) == 0) gate.Clear("005930", 70000m, 10m, OrderSide.Buy);               // KRW 정수(정렬)
                        else              gate.Clear("AAPL", 100.00m, 2m, OrderSide.Buy, UsdKrw(), Bounds()); // USD 소수 + FX(정렬)
                    }
                }) { IsBackground = true };
            foreach (var th in ts) th.Start();
            foreach (var th in ts) Assert.True(th.Join(30000));
            Assert.Equal(threads * per, gate.Observed);   // 무손실
            Assert.Equal(0, gate.FrozenCount);            // 정렬 입력 — 동결 0
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ClearSymbol_RegisteredHotPath_ZeroAllocation()
        {
            var gate = new ShadowCutoverGate(true, Registry(), new OrderAttestationService());
            for (int i = 0; i < 10_000; i++) gate.Clear("005930", 70000m, 10m, OrderSide.Buy);   // 워밍업(tiered 안정화)
            long before = GC.GetAllocatedBytesForCurrentThread();
            decimal sink = 0m;
            for (int i = 0; i < 50_000; i++) sink = gate.Clear("005930", 70000m, 10m, OrderSide.Buy);
            Assert.True(sink > 0m);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);   // 심볼조회 + 청산 핫패스 δ=0
        }

        [Fact]
        public void FxClear_RegisteredHotPath_ZeroAllocation()
        {
            var gate = new ShadowCutoverGate(true, Registry(), new OrderAttestationService());
            var fx = UsdKrw(); var b = Bounds();
            for (int i = 0; i < 10_000; i++) gate.Clear("AAPL", 100.00m, 2m, OrderSide.Buy, fx, b);
            long before = GC.GetAllocatedBytesForCurrentThread();
            decimal sink = 0m;
            for (int i = 0; i < 50_000; i++) sink = gate.Clear("AAPL", 100.00m, 2m, OrderSide.Buy, fx, b);
            Assert.True(sink > 0m);
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);   // FX 환산 + 수렴 펜스 핫패스 δ=0
        }

        [Fact]
        public void Registry_BuildTime_KeyCollision_FailFast()
        {
            // FNV 키 충돌/중복은 빌드 시 fail-fast(조용한 스큐 차단). 동일 심볼 중복 = 동일 키 → throw.
            Assert.Throws<InvalidOperationException>(() => new QTickRegistry(new (string, QTickSpec)[]
            {
                ("005930", new QTickSpec(QPrice.Of(1m), QQty.Of(1m))),
                ("005930", new QTickSpec(QPrice.Of(5m), QQty.Of(1m))),   // 중복 → 키 충돌
            }));
        }
    }
}
