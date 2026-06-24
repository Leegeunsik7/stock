using System;
using System.Collections.Generic;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // Path B — ShadowClearingBroker: QFixed 청산 척추를 실 브로커 경로에 결속하는 데코레이터.
    //   불변: 레거시 inner 가 권위(반환 불변)·INERT 면 무관측·섀도우 동결도 실주문 절대 미차단.
    public sealed class PathBShadowClearingTests
    {
        private sealed class SpyBroker : IBroker
        {
            public readonly List<string> Placed = new();
            public int    Cancels;
            public bool   HeldQueried;
            public string Name   => "SpyInner";
            public bool   IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string sym, string n, PaperSide s, PaperKind k,
                                               decimal q, decimal lp, decimal cp)
            { Placed.Add(sym); return (true, "INNER-OK"); }
            public void    Cancel(string id)                  => Cancels++;
            public decimal HeldQty(PaperAsset a, string sym)  { HeldQueried = true; return 42m; }
        }

        // 005930 정수 격자(tick 1원·step 1주) — 70000×10 은 정렬되어 무동결.
        private static QTickRegistry Reg() => new(new (string, QTickSpec)[]
        {
            ("005930", new QTickSpec(QPrice.Of(1m), QQty.Of(1m))),
        });

        [Fact]
        public void Disabled_Inert_PassesThrough_NoObserve()
        {
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(false, Reg(), att);   // INERT
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);

            var (ok, msg) = deco.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m);

            Assert.True(ok);
            Assert.Equal("INNER-OK", msg);          // 레거시 권위 반환
            Assert.Single(spy.Placed);              // inner 실행
            Assert.Equal(0, gate.Observed);         // INERT — Clear 미호출(무관측)
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Enabled_Observes_ButReturnsInnerResult()
        {
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att);
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);

            var (ok, msg) = deco.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m);

            Assert.Equal("INNER-OK", msg);          // 신 엔진 값으로 주문을 바꾸지 않음(레거시 권위)
            Assert.Equal(1, gate.Observed);         // 병렬 관측 1회
            Assert.Equal(0, gate.FrozenCount);      // 정렬 입력 — 무동결
            Assert.Single(spy.Placed);
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void ObservationFreeze_DoesNotBlockInner()
        {
            // ★핵심 안전 불변 — 미등록 심볼은 섀도우 게이트를 fail-closed 동결시키지만, 실주문 경로(inner)는
            //   그대로 실행되고 그 결과가 반환된다(섀도우 결함이 실거래를 절대 막지 못함).
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att);
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);

            var (ok, msg) = deco.Place(PaperAsset.Stock, "GHOST999", "유령", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m);

            Assert.True(ok);
            Assert.Equal("INNER-OK", msg);          // 실주문 경로 불변
            Assert.Single(spy.Placed);              // inner 실행됨
            Assert.Equal(1, gate.FrozenCount);      // 섀도우만 동결(관측 격리)
        }

        [Fact]
        public void Cancel_HeldQty_Name_IsLive_Delegate()
        {
            var gate = new ShadowCutoverGate(false, Reg(), new OrderAttestationService());
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);

            deco.Cancel("X-1");
            Assert.Equal(1, spy.Cancels);
            Assert.Equal(42m, deco.HeldQty(PaperAsset.Stock, "005930"));
            Assert.True(spy.HeldQueried);
            Assert.Equal("Shadow(SpyInner)", deco.Name);
            Assert.False(deco.IsLive);
        }

        [Fact]
        public void NullArgs_Throw()
        {
            var gate = new ShadowCutoverGate(false, Reg(), new OrderAttestationService());
            Assert.Throws<ArgumentNullException>(() => new ShadowClearingBroker(null!, gate));
            Assert.Throws<ArgumentNullException>(() => new ShadowClearingBroker(new SpyBroker(), null!));
        }
    }
}
