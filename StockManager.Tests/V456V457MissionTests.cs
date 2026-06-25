using System;
using System.Linq;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // v4.56.0 Mission A — BuiltInTickSpecs 자산별 격자 레지스트리 + ShadowCutoverGate registry 경로 fail-closed.
    // v4.57.0 Mission B — LiveArmingGate 3중 안전 게이트 실 무장 술어(전부 fail-closed·결정론).
    public sealed class V456V457MissionTests
    {
        // ── Mission A: 기본 격자 레지스트리 ─────────────────────────────────────

        [Fact]
        public void BuiltInTickSpecs_Default_ResolvesPerAssetGrids()
        {
            var reg = BuiltInTickSpecs.BuildDefaultRegistry();
            Assert.True(reg.Count >= 10);   // 대표 표본(주식·USD·코인)

            // KRW 정수 호가(가격대 근사)·1주 스텝
            Assert.True(reg.TryGet("005930", out var sams));
            Assert.Equal(100m, sams.Tick.ToDecimal());      // 삼성전자 5만~20만 → 100원
            Assert.Equal(1m,   sams.Step.ToDecimal());
            Assert.True(reg.TryGet("035720", out var kakao));
            Assert.Equal(50m, kakao.Tick.ToDecimal());      // 카카오 2만~5만 → 50원
            Assert.True(reg.TryGet("069500", out var etf));
            Assert.Equal(5m, etf.Tick.ToDecimal());         // ETF 고정 5원

            // USD 센트 틱·1주 스텝
            Assert.True(reg.TryGet("AAPL", out var aapl));
            Assert.Equal(0.01m, aapl.Tick.ToDecimal());
            Assert.Equal(1m,    aapl.Step.ToDecimal());

            // 코인 — KRW 가격 틱·사토시(1e-8) 스텝
            Assert.True(reg.TryGet("KRW-BTC", out var btc));
            Assert.Equal(0.00000001m, btc.Step.ToDecimal());

            // 미등록 → false(fail-closed, 충돌 안전)
            Assert.False(reg.TryGet("999999", out _));
        }

        [Fact]
        public void Gate_WithRegistry_RegisteredSymbol_AlignedClear_NoFalseFreeze()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: true, BuiltInTickSpecs.BuildDefaultRegistry(), att);

            // 삼성전자: 격자(틱 100·스텝 1)에 '정확 정렬'된 가격·수량 → 신·구 명목 일치 → 거짓 동결 없음.
            decimal got = gate.Clear("005930", 85_000m, 10m, OrderSide.Buy);

            Assert.Equal(850_000m, got);                       // 항상 레거시 반환(liveArmed:false)
            Assert.Equal(GatingMode.Normal, att.Mode);         // 정렬 입력 → 미동결
            Assert.Equal(0L, gate.FrozenCount);
        }

        [Fact]
        public void Gate_WithRegistry_UnregisteredSymbol_FailsClosed_Freezes()
        {
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: true, BuiltInTickSpecs.BuildDefaultRegistry(), att);

            // 미등록 심볼 진입 → 모호 통과 금지 → ForceFreeze + FrozenCount++ + 레거시 백스톱 반환.
            decimal got = gate.Clear("UNREGISTERED_XYZ", 100m, 1m, OrderSide.Buy);

            Assert.Equal(100m, got);                                  // 레거시 백스톱(권위 오염 대신 정직 반환)
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);       // fail-closed 동결
            Assert.Equal(1L, gate.FrozenCount);
        }

        [Fact]
        public void Gate_WithRegistry_Disabled_IsInert_NoFreezeOnUnregistered()
        {
            // 기본 OFF(INERT) — 미등록 심볼이라도 Clear 미수행(레거시 통과·무동결). 후방호환 성벽.
            var att = new OrderAttestationService();
            var gate = new ShadowCutoverGate(enabled: false, BuiltInTickSpecs.BuildDefaultRegistry(), att);

            decimal got = gate.Clear("UNREGISTERED_XYZ", 100m, 1m, OrderSide.Buy);

            Assert.Equal(100m, got);
            Assert.Equal(GatingMode.Normal, att.Mode);   // INERT → 동결 없음
            Assert.Equal(0L, gate.FrozenCount);
        }

        [Fact]
        public void ConservativeAlign_Registry_BuyUp_SellDown()
        {
            // 스펙 계약: 매수=AlignUp↑(비용↑·플랫폼 불리), 매도=AlignDown↓(수령↓). 레지스트리 격자로 검증.
            var reg = BuiltInTickSpecs.BuildDefaultRegistry();
            Assert.True(reg.TryGet("035720", out var kakao));   // 틱 50원
            var tick = kakao.Tick;
            var px   = QPrice.Of(45_021m);   // 50원 격자 비정렬 — 45000/45050 사이

            Assert.Equal(45_050m, px.ConservativeAlign(tick, OrderSide.Buy).ToDecimal());    // 올림
            Assert.Equal(45_000m, px.ConservativeAlign(tick, OrderSide.Sell).ToDecimal());   // 내림
        }

        // ── Mission B: 3중 안전 게이트 실 무장 술어 ─────────────────────────────

        private static IgnitionLock ArmedIgnition(OrderAttestationService att)
        {
            var ign = new IgnitionLock(attestation: att);
            Assert.True(ign.Arm(IgnitionStage.MasterIgnited, "test-sig"));   // Locked → MasterIgnited → AllowPhysical()=true
            return ign;
        }

        [Fact]
        public void LiveArming_AllThreeTrue_IsArmed()
        {
            var att = new OrderAttestationService();          // Normal
            var ign = ArmedIgnition(att);                     // AllowPhysical()=true
            var gate = new LiveArmingGate(ign, att, () => true);   // brokerArmed=true
            Assert.True(gate.IsArmed());
        }

        [Fact]
        public void LiveArming_AttestationFrozen_NotArmed()
        {
            var att = new OrderAttestationService();
            var ign = ArmedIgnition(att);
            att.ForceFreeze("테스트 동결");                   // ① 미동결 위반
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.False(gate.IsArmed());
        }

        [Fact]
        public void LiveArming_IgnitionLocked_NotArmed()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(attestation: att);     // 기본 Locked → AllowPhysical()=false
            var gate = new LiveArmingGate(ign, att, () => true);
            Assert.False(gate.IsArmed());
        }

        [Fact]
        public void LiveArming_BrokerDisarmed_NotArmed()
        {
            var att = new OrderAttestationService();
            var ign = ArmedIgnition(att);
            var gate = new LiveArmingGate(ign, att, () => false);   // ③ 브로커 미무장
            Assert.False(gate.IsArmed());
        }

        [Fact]
        public void LiveArming_BrokerProbeThrows_FailsClosed_NoEscape()
        {
            // 평가 중 예외(db I/O 단선 모사) → 무조건 false, 예외 전파 없음.
            var att = new OrderAttestationService();
            var ign = ArmedIgnition(att);
            var gate = new LiveArmingGate(ign, att, () => throw new InvalidOperationException("db 단선 모사"));
            Assert.False(gate.IsArmed());   // throw → catch → false (예외 escape 시 이 단언 전에 폭발)
        }

        [Fact]
        public void LiveArming_ShortCircuits_BrokerProbeNotCalledWhenFrozen()
        {
            // att 동결이 가장 먼저 판정 → brokerArmed(throw 라이터)는 호출조차 되지 않음(단락·δ=0 db-I/O 회피 증명).
            var att = new OrderAttestationService();
            var ign = ArmedIgnition(att);
            att.ForceFreeze("테스트 동결");
            bool brokerCalled = false;
            var gate = new LiveArmingGate(ign, att, () => { brokerCalled = true; throw new InvalidOperationException(); });

            Assert.False(gate.IsArmed());
            Assert.False(brokerCalled);   // 단락으로 미호출 — 호출됐다면 throw 가 전파됐을 것
        }

        [Fact]
        public void LiveArming_ShortCircuits_BrokerProbeNotCalledWhenIgnitionLocked()
        {
            var att = new OrderAttestationService();          // Normal(통과)
            var ign = new IgnitionLock(attestation: att);     // Locked → ② 에서 단락
            bool brokerCalled = false;
            var gate = new LiveArmingGate(ign, att, () => { brokerCalled = true; return true; });

            Assert.False(gate.IsArmed());
            Assert.False(brokerCalled);   // ②(점화 Locked) 단락 → ③ db 프로브 미호출
        }

        [Fact]
        public void LiveArming_NullArgs_Throw()
        {
            var att = new OrderAttestationService();
            var ign = new IgnitionLock(attestation: att);
            Assert.Throws<ArgumentNullException>(() => new LiveArmingGate(null!, att, () => true));
            Assert.Throws<ArgumentNullException>(() => new LiveArmingGate(ign, null!, () => true));
            Assert.Throws<ArgumentNullException>(() => new LiveArmingGate(ign, att, null!));
        }
    }
}
