using System;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S29-c — 청산 권위 역전(Authority Inversion).
    //   §1 후방호환(기본 Legacy 권위·INERT 불변) §2 QFixed 권위 승격(정렬값 반환) §3 레거시 백스톱(오버플로 동결)
    //   §4 레거시 poison 비오염(QFixed 수렴) §5 가역 스위치 §6 무할당 런타임 스왑 §7 자산별·방향성.
    public sealed class S29cAuthorityInversionTests
    {
        private static QTickSpec Cent() => new(QPrice.Of(0.01m), QQty.Of(1m));   // USD 센트
        private static QTickRegistry Reg() => new(new[]
        {
            ("005930", new QTickSpec(QPrice.Of(1m),    QQty.Of(1m))),   // KRW 1원
            ("AAPL",   new QTickSpec(QPrice.Of(0.01m), QQty.Of(1m))),   // USD 센트
        });

        // ═══════════════════ §1 후방호환 ═══════════════════

        [Fact]
        public void DefaultAuthority_IsLegacy_ShadowModeUnchanged()
        {
            // 기본(인자 생략) = LegacyPrimary. enabled 섀도우 모드라도 반환은 레거시 — 기존 의미론 1비트도 불변.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Cent(), att);
            Assert.Equal(ClearingAuthority.LegacyPrimary, gate.Authority);
            Assert.Equal(100.005m, gate.Clear(100.005m, 1m, OrderSide.Buy));   // naive legacy 그대로
            Assert.True(gate.Observed > 0);                                    // 엔진은 병렬 관측(섀도우)
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void DisabledLegacy_StaysInert_NoEngine()
        {
            // 비활성+기본 권위 = 완전 INERT(엔진 미수행). 기존 DI 기본 거동 불변.
            var gate = new ShadowCutoverGate(false, Cent());
            Assert.Equal(100.005m, gate.Clear(100.005m, 1m, OrderSide.Buy));
            Assert.Equal(0, gate.Observed);   // 엔진 미진입
        }

        // ═══════════════════ §2 QFixed 권위 승격 ═══════════════════

        [Fact]
        public void QFixedPrimary_PromotesAlignedValue_NotNaiveLegacy()
        {
            // ★권위 역전 핵심 — QFixed 정렬값(100.01, 매수 올림)이 반환된다. naive legacy(100.005)가 아니다.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Cent(), att, ClearingAuthority.QFixedPrimary);
            decimal result = gate.Clear(100.005m, 1m, OrderSide.Buy);
            Assert.Equal(100.01m, result);                       // QFixed 권위(정렬·보수 올림)
            Assert.NotEqual(100.005m, result);                   // naive legacy 아님
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void QFixedPrimary_OnDisabledGate_StillComputes()
        {
            // QFixed 권위로 올리면 엔진이 돌아야 권위값을 낼 수 있다 → enabled 와 무관하게 계산(opt-in 스위치 활성).
            var gate = new ShadowCutoverGate(false, Cent(), null, ClearingAuthority.QFixedPrimary);
            Assert.Equal(100.01m, gate.Clear(100.005m, 1m, OrderSide.Buy));
            Assert.True(gate.Observed > 0);
        }

        // ═══════════════════ §3 레거시 백스톱 — 오버플로 동결 ═══════════════════

        [Fact]
        public void QFixedPrimary_Overflow_BackstopTripsFreeze()
        {
            // Mission 3.2 — QFixed 허용범위 초과 폭주값(1e11 × 1e8 스케일 > long). naive decimal 은 1e11 을 태연히
            //   계산하지만 QFixed 권위 엔진이 오버플로를 포착 → fail-closed 동결(권위 오염 대신 파이프라인 정지).
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, new QTickSpec(QPrice.Of(1m), QQty.Of(1m)), att, ClearingAuthority.QFixedPrimary);
            decimal result = gate.Clear(100_000_000_000m, 1m, OrderSide.Buy);   // 1e11
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);                 // 백스톱이 폭주 포착 → 동결
            Assert.True(gate.FrozenCount > 0);
            Assert.Equal(100_000_000_000m, result);   // 동결 시 degraded 백스톱(legacy) 반환 — 파이프라인은 정지 상태
        }

        // ═══════════════════ §4 레거시 poison 비오염 ═══════════════════

        [Fact]
        public void QFixedPrimary_PoisonedOffGridLegacy_ConvergesToQFixed()
        {
            // Mission 3.1 — 매도 off-grid 독약(100.005, 센트격자 비정렬). QFixed 권위는 보수 내림(100.00)으로 수렴,
            //   naive legacy 의 sub-cent skew(100.005)가 메인 청산 가치를 오염시키지 못한다.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Cent(), att, ClearingAuthority.QFixedPrimary);
            decimal result = gate.Clear(100.005m, 1m, OrderSide.Sell);
            Assert.Equal(100.00m, result);        // QFixed 매도 내림(거래소 유리) — 격자 수렴
            Assert.NotEqual(100.005m, result);    // off-grid 독약 미통과
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        // ═══════════════════ §5 가역 스위치 ═══════════════════

        [Fact]
        public void Authority_RuntimeSwap_Reversible()
        {
            var gate = new ShadowCutoverGate(true, Cent());
            Assert.Equal(100.005m, gate.Clear(100.005m, 1m, OrderSide.Buy));   // 기본 Legacy

            gate.SetAuthority(ClearingAuthority.QFixedPrimary);
            Assert.Equal(ClearingAuthority.QFixedPrimary, gate.Authority);
            Assert.Equal(100.01m, gate.Clear(100.005m, 1m, OrderSide.Buy));    // QFixed 권위

            gate.SetAuthority(ClearingAuthority.LegacyPrimary);
            Assert.Equal(100.005m, gate.Clear(100.005m, 1m, OrderSide.Buy));   // 되돌림 — Legacy 복귀
        }

        // ═══════════════════ §6 무할당 런타임 스왑 ═══════════════════

        [Fact]
        public void Authority_Swap_And_Clear_ZeroAllocation()
        {
            // 정렬 입력(100.00·동결 없음) + 권위 토글 반복 — 핫패스 δ=0(volatile enum 쓰기·decimal/값타입 연산만).
            var gate = new ShadowCutoverGate(true, Cent());
            for (int i = 0; i < 10_000; i++)
            {
                gate.SetAuthority((i & 1) == 0 ? ClearingAuthority.QFixedPrimary : ClearingAuthority.LegacyPrimary);
                gate.Clear(100.00m, 1m, OrderSide.Buy);
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 50_000; i++)
            {
                gate.SetAuthority((i & 1) == 0 ? ClearingAuthority.QFixedPrimary : ClearingAuthority.LegacyPrimary);
                gate.Clear(100.00m, 1m, OrderSide.Buy);
            }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, delta);   // 권위 스왑+청산 핫패스 무할당
        }

        // ═══════════════════ §7 자산별·방향성 ═══════════════════

        [Fact]
        public void QFixedPrimary_PerAsset_Symbol()
        {
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att, ClearingAuthority.QFixedPrimary);
            Assert.Equal(100.01m, gate.Clear("AAPL",   100.005m, 1m, OrderSide.Buy));   // 센트 격자 올림
            Assert.Equal(70001m,  gate.Clear("005930", 70000.5m, 1m, OrderSide.Buy));   // 1원 격자 올림
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void QFixedPrimary_BuyUp_SellDown_DirectionalAuthority()
        {
            // 보수 편향(매수 올림·매도 내림)이 이제 '권위값'으로 흐른다.
            var gate = new ShadowCutoverGate(true, Cent(), null, ClearingAuthority.QFixedPrimary);
            decimal buy  = gate.Clear(213.925m, 3m, OrderSide.Buy);
            decimal sell = gate.Clear(213.925m, 3m, OrderSide.Sell);
            Assert.Equal(641.79m, buy);    // 213.93 × 3 (매수 올림)
            Assert.Equal(641.76m, sell);   // 213.92 × 3 (매도 내림)
            Assert.True(buy > sell);
        }
    }
}
