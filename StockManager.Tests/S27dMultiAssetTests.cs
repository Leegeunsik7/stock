using System;
using Xunit;

namespace StockManager.Tests
{
    // S27-d — 멀티-자산 척추. §1 자산별 QTickSpec 룩업(무할당·미등록 fail-closed·키충돌) §2 보수 FX(방향·스큐·바운드)
    //   §3 멀티-자산 카오스(자산별 spec 이 S27-c 단일전역 왜곡을 교정·FX 스큐 오염 즉시 동결).
    public sealed class S27dMultiAssetTests
    {
        private static QTickRegistry Reg() => new(new[]
        {
            ("005930",  new QTickSpec(QPrice.Of(1m),    QQty.Of(1m))),            // KRW 주식 — 1원 정수
            ("AAPL",    new QTickSpec(QPrice.Of(0.01m), QQty.Of(1m))),            // USD — 센트
            ("KRW-BTC", new QTickSpec(QPrice.Of(1000m), QQty.Of(0.00000001m))),   // 코인 — 사토시 수량
        });

        // ═══════════════════ §1 자산별 룩업 ═══════════════════

        [Fact]
        public void Registry_PerAsset_Lookup()
        {
            var reg = Reg();
            Assert.Equal(3, reg.Count);
            Assert.True(reg.TryGet("AAPL", out var s));
            Assert.Equal(QPrice.Of(0.01m), s.Tick);
            Assert.True(reg.TryGet("KRW-BTC", out var c));
            Assert.Equal(QQty.Of(0.00000001m), c.Step);
        }

        [Fact]
        public void Registry_UnknownAsset_FailClosed_False()
            => Assert.False(Reg().TryGet("DOES_NOT_EXIST", out _));   // 미등록 → 추측 spec 금지(fail-closed 책임 이양)

        [Fact]
        public void Registry_DuplicateSymbol_Build_Throws()
            // 동일 심볼 중복(=동일 FNV 키) 빌드 fail-fast. 진짜 서로 다른 심볼의 FNV 충돌(2^-64)은 코드 경로 동일.
            => Assert.Throws<InvalidOperationException>(() => new QTickRegistry(new[]
            {
                ("AAPL", new QTickSpec(QPrice.Of(0.01m), QQty.Of(1m))),
                ("AAPL", new QTickSpec(QPrice.Of(0.1m),  QQty.Of(1m))),
            }));

        [Fact]
        public void Registry_TryGetString_ZeroAllocation()
        {
            // ★실 사용 진입점(string 경로)의 무할당 실측 — HashId 순회 + 심볼 검증(string.Equals) 모두 힙 0.
            var reg = Reg();
            for (int i = 0; i < 10_000; i++) { reg.TryGet("AAPL", out _); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            int hits = 0;
            for (int i = 0; i < 50_000; i++) if (reg.TryGet("AAPL", out _)) hits++;
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(50_000, hits);
            // per-call 힙 할당 0 입증 — per-call 할당이 있었다면 50k×(최소객체 24B) ≥ 1.2MB. 측정 δ 는 그 1/300 미만
            //   (string 경로의 더 복잡한 코드가 측정 윈도우 중 tiered-JIT 재컴파일되며 남기는 일회성 ~수백B 노이즈만).
            Assert.True(delta < 50_000, $"per-call 할당 0(일회성 JIT 노이즈만) — delta={delta}");
        }

        [Fact]
        public void Registry_ZeroAllocation_Lookup()
        {
            var reg = Reg();
            long key = QTickRegistry.Key("AAPL");
            for (int i = 0; i < 10_000; i++) { reg.TryGet(key, out _); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            int hits = 0;
            for (int i = 0; i < 50_000; i++) if (reg.TryGet(key, out _)) hits++;
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(50_000, hits);
            Assert.Equal(0, delta);   // FrozenDictionary long 키·struct out — 박싱·힙 0
        }

        // ═══════════════════ §2 보수 FX 환산 ═══════════════════

        private static QFxBounds KrwUsdBounds() => QFxBounds.Of(500m, 3000m, 50m);

        [Fact]
        public void Fx_Buy_Ask_Sell_Bid_ExchangeFavoured()
        {
            var quote = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1331m));
            var att = new OrderAttestationService();
            Assert.True(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy,  KrwUsdBounds(), att, out var buy));
            Assert.True(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Sell, KrwUsdBounds(), att, out var sell));
            Assert.Equal(133100m, buy.ToDecimal());    // 100 × Ask 1331 (매수 비용↑)
            Assert.Equal(133000m, sell.ToDecimal());   // 100 × Bid 1330 (매도 수령↓)
            Assert.True(buy.Raw > sell.Raw);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_Skew_Inversion_Freezes()
        {
            var att = new OrderAttestationService();
            var quote = new QFxQuote(QFxRate.Of(1331m), QFxRate.Of(1330m));   // Ask<Bid 역전
            Assert.False(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy, KrwUsdBounds(), att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_Skew_NegativeRate_Freezes()
        {
            var att = new OrderAttestationService();
            var quote = new QFxQuote(new QFxRate(-1L), QFxRate.Of(1330m));
            Assert.False(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Sell, KrwUsdBounds(), att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_Skew_AbnormalSpread_Freezes()
        {
            var att = new OrderAttestationService();
            var quote = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1450m));   // 스프레드 120 > max 50
            Assert.False(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy, KrwUsdBounds(), att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_WrongScaleRate_OutOfBounds_Freezes()
        {
            // wrong-scale: 1330 을 raw 1330(=0.0000133)으로 잘못 저장 → 타당성 바운드[500,3000] 이탈 → 동결.
            //   (역산은 자기일관이라 못 잡음 — 바운드가 scale 오염의 실질 방어임을 박제.)
            var att = new OrderAttestationService();
            var quote = new QFxQuote(new QFxRate(1330L), new QFxRate(1331L));
            Assert.False(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy, KrwUsdBounds(), att, out _));
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_DegenerateBounds_Freezes()
        {
            // ★바운드가 wrong-scale 유일 방어이므로 비활성(default/0) 바운드는 무방어 잠복 — fail-closed 거부.
            var att = new OrderAttestationService();
            var quote = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1331m));   // 정상 호가지만
            Assert.False(QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy, default, att, out _));   // 바운드 0
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Fx_ZeroAllocation()
        {
            var quote = new QFxQuote(QFxRate.Of(1330m), QFxRate.Of(1331m));
            var bounds = KrwUsdBounds(); var att = new OrderAttestationService();
            for (int i = 0; i < 10_000; i++) { QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy, bounds, att, out _); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++) { QFxGuard.TryConvertOrFreeze(QMoney.Of(100m), quote, OrderSide.Buy, bounds, att, out _); }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);   // 정상 환산 핫패스 δ=0
        }

        // ═══════════════════ §3 멀티-자산 카오스 ═══════════════════

        [Fact]
        public void MultiAsset_PerAssetSpec_FixesSingleGlobalDistortion()
        {
            // ★S27-d 핵심 — S27-c 단일전역 spec(tick=1원)은 $100.10 를 101 로 왜곡했다. AAPL 의 센트 격자(0.01)를
            //   레지스트리에서 조회해 쓰면 100.10 이 정렬·무왜곡(왜곡 0)이다.
            Reg().TryGet("AAPL", out var cent);
            var att = new OrderAttestationService();
            Assert.True(ShadowClearingTracer.TraceOrFreeze(100.10m, 1m, OrderSide.Buy, cent, att, out var centShadow));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
            Assert.Equal(100.10m, centShadow.ToDecimal());   // 센트 격자 → 왜곡 0

            // ★대조 실행 단언(주석이 아닌) — S27-c 단일전역 spec(tick=1원)은 같은 입력을 101 로 왜곡한다.
            var single = new QTickSpec(QPrice.Of(1m), QQty.Of(1m));
            ShadowClearingTracer.TraceOrFreeze(100.10m, 1m, OrderSide.Buy, single, new OrderAttestationService(), out var singleShadow);
            Assert.Equal(101m, singleShadow.ToDecimal());                 // 단일전역 → 101 왜곡(매수 올림)
            Assert.NotEqual(centShadow.ToDecimal(), singleShadow.ToDecimal());   // 자산별 spec 이 왜곡을 교정함을 대조 박제
        }

        [Fact]
        public void MultiAsset_SubTickAlignment_Directional_BuyUp_SellDown()
        {
            // ★혼재 워크로드 비공허 보강 — 비정렬 단가($213.925)가 ConservativeAlign 으로 실제 이동(매수 213.93·매도 213.92).
            var spec = new QTickSpec(QPrice.Of(0.01m), QQty.Of(1m));
            var ab = new OrderAttestationService(); var asl = new OrderAttestationService();
            Assert.True(ShadowClearingTracer.TraceOrFreeze(213.925m, 3m, OrderSide.Buy,  spec, ab,  out var buy));
            Assert.True(ShadowClearingTracer.TraceOrFreeze(213.925m, 3m, OrderSide.Sell, spec, asl, out var sell));
            Assert.Equal(641.79m, buy.ToDecimal());    // 213.93 × 3 (매수 가격 올림)
            Assert.Equal(641.76m, sell.ToDecimal());   // 213.92 × 3 (매도 가격 내림)
            Assert.True(buy.Raw > sell.Raw);           // 보수 편향 방향 실증(정렬이 실제 이동)
        }

        [Fact]
        public void MultiAsset_MixedWorkload_KrwStock_UsdStock_Coin_NoFreeze()
        {
            // 혼재 워크로드 — 각 자산을 자기 격자로 청산. 어느 것도 거짓 동결 없음(자산별 spec 정합).
            var reg = Reg();
            var att = new OrderAttestationService();
            reg.TryGet("005930",  out var krw);  Assert.True(ShadowClearingTracer.TraceOrFreeze(70000m, 10m, OrderSide.Buy,  krw, att, out _));
            reg.TryGet("AAPL",    out var usd);  Assert.True(ShadowClearingTracer.TraceOrFreeze(213.92m, 3m, OrderSide.Sell, usd, att, out _));
            reg.TryGet("KRW-BTC", out var btc);  Assert.True(ShadowClearingTracer.TraceOrFreeze(98370000m, 0.255m, OrderSide.Buy, btc, att, out _));
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }
    }
}
