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
            public decimal LastQty, LastLimit, LastCurrent;   // v4.61 — inner 가 받은 실제 인자 캡처(raw vs 정렬 검증)
            public int    Cancels;
            public bool   HeldQueried;
            public string Name   => "SpyInner";
            public bool   IsLive => false;
            public (bool ok, string msg) Place(PaperAsset a, string sym, string n, PaperSide s, PaperKind k,
                                               decimal q, decimal lp, decimal cp)
            { Placed.Add(sym); LastQty = q; LastLimit = lp; LastCurrent = cp; return (true, "INNER-OK"); }
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

        // ═══════════════════ v4.61.0 권위 채택(load-bearing) 컷오버 하드닝 박제 ═══════════════════

        [Fact]
        public void ShadowBroker_WhenLegacyPrimary_SwallowsExceptionAndPassesRawValues()
        {
            // LegacyPrimary(기본·안전 모드): 게이트가 미등록 심볼로 내부 fail-closed 동결돼도(관측 결함) 실주문 경로는
            //   막히지 않고 RAW 단가/수량 그대로 inner 로 위임된다(섀도우 결함이 실거래를 절대 못 막는 기존 계약 불변).
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att);   // LegacyPrimary
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);

            var (ok, msg) = deco.Place(PaperAsset.Stock, "GHOST999", "유령", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m);

            Assert.True(ok);
            Assert.Equal("INNER-OK", msg);      // 레거시 inner 권위 반환(throw 없음)
            Assert.Single(spy.Placed);          // inner 실행됨 — 차단되지 않음
            Assert.Equal(10m, spy.LastQty);     // RAW 수량 그대로(정렬 치환 없음)
            Assert.Equal(70000m, spy.LastLimit);// RAW 단가 그대로
            Assert.Equal(1, gate.FrozenCount);  // 섀도우만 내부 동결(관측 격리)
        }

        [Fact]
        public void ShadowBroker_WhenQFixedPrimary_MutatesProposalToAuthoritativeValues()
        {
            // QFixedPrimary(완전 집권): 서브틱 단가(70000.5)·서브스텝 수량(10.7)이 게이트 보수 정렬값으로 치환돼 송출.
            //   1원 격자 매수 → 단가 ceil 70001, 수량 floor 10. inner 는 RAW 가 아닌 '권위 정렬값'을 받는다.
            var att  = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), att, ClearingAuthority.QFixedPrimary);
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);

            var (ok, msg) = deco.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 10.7m, 70000.5m, 70000.5m);

            Assert.True(ok);
            Assert.Equal("INNER-OK", msg);
            Assert.Single(spy.Placed);
            Assert.Equal(70001m, spy.LastLimit);   // ★매수 보수 정렬 단가(ceil)가 권위로 치환
            Assert.Equal(10m,    spy.LastQty);      // ★step 내림 수량이 권위로 치환
            Assert.NotEqual(GatingMode.EmergencyFreeze, att.Mode);   // 정상 청산 — 무동결
        }

        [Fact]
        public void ShadowBroker_WhenQFixedPrimary_FailsOnClearingError_FailClosed()
        {
            // QFixedPrimary + 미등록 심볼 → 청산 불가 → fail-closed: inner.Place 절대 미호출 + 라이브 attestation
            //   하드 동결 + InvalidOperationException 으로 파이프라인 단절(자산 오염 차단). LegacyPrimary 의 삼킴과 정반대.
            var gateAtt = new OrderAttestationService();
            var liveAtt = new OrderAttestationService();   // 라이브 파이프라인 attestation(데코레이터 주입)
            var gate = new ShadowCutoverGate(true, Reg(), gateAtt, ClearingAuthority.QFixedPrimary);
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate, liveAtt);

            Assert.Throws<InvalidOperationException>(() =>
                deco.Place(PaperAsset.Stock, "GHOST999", "유령", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m));

            Assert.Empty(spy.Placed);                                // ★inner.Place 절대 미호출(하드 카운터 단절)
            Assert.Equal(GatingMode.EmergencyFreeze, liveAtt.Mode);  // ★라이브 파이프라인 동결(에스컬레이션)
            Assert.True(gate.FrozenCount > 0);                       // 게이트도 fail-closed 계상
        }

        [Fact]
        public void ShadowBroker_QFixedPrimary_FailClosed_NoLiveAtt_StillThrows_InnerNotCalled()
        {
            // _liveAtt 미주입(기본 null)이어도 throw + inner 미호출 하드 단절은 유지(footgun 없이 fail-closed).
            var gate = new ShadowCutoverGate(true, Reg(), new OrderAttestationService(), ClearingAuthority.QFixedPrimary);
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate);   // liveAttestation 생략

            Assert.Throws<InvalidOperationException>(() =>
                deco.Place(PaperAsset.Stock, "GHOST999", "유령", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m));
            Assert.Empty(spy.Placed);
        }

        [Fact]
        public void ShadowBroker_LegacyPrimary_WithLiveAtt_NeverFreezesLive_FootgunAvoided()
        {
            // ★v4.49/v4.50 footgun 회귀 방지(거동 박제) — LegacyPrimary 에서는 _liveAtt 가 주입돼 있어도, 미등록 심볼로
            //   섀도우 게이트가 내부 동결돼도, 라이브 attestation 은 절대 동결되지 않는다(FailClosed 는 QFixed 전용).
            var gateAtt = new OrderAttestationService();
            var liveAtt = new OrderAttestationService();
            var gate = new ShadowCutoverGate(true, Reg(), gateAtt);   // LegacyPrimary(기본)
            var spy  = new SpyBroker();
            var deco = new ShadowClearingBroker(spy, gate, liveAtt);   // 라이브 att 주입했지만 Legacy 라 무관

            var (ok, msg) = deco.Place(PaperAsset.Stock, "GHOST999", "유령", PaperSide.Buy, PaperKind.Limit, 10m, 70000m, 70000m);

            Assert.True(ok);
            Assert.Equal("INNER-OK", msg);                             // 실주문 통과(차단 없음)
            Assert.Single(spy.Placed);
            Assert.True(gate.FrozenCount > 0);                         // 섀도우(gateAtt)만 내부 동결
            Assert.Equal(GatingMode.EmergencyFreeze, gateAtt.Mode);    // 게이트 전용 att 는 동결
            Assert.NotEqual(GatingMode.EmergencyFreeze, liveAtt.Mode); // ★라이브 att 는 절대 미동결(footgun 회피)
        }
    }
}
