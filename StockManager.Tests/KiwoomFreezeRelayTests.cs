// ============================================================================
// KiwoomFreezeRelayTests — v4.78.0: 키움 파이프 연쇄 동결 릴레이 적대적 검증
//
//  §1 동결 트리거(3) — ProxyLost·치명 ErrorReceived → EmergencyFreeze
//  §2 멱등/구독(3)   — ProxyLost 2회 멱등·Detach 후 무동결·Dispose 후 무동결
//  §3 freeze-storm 회귀(2) — ★FillReceived 미구독 → 평범한 거부가 동결 안 됨(critical 교정 박제)
//  §4 에러 정책(2)   — code=0 기본 무동결·freezeOnAnyError 옵트인 시 동결
//
//  이벤트(KiwoomBridgeClient의 field-like event)는 외부 invoke 불가 →
//  리플렉션으로 백킹 델리게이트를 발생시켜 Relay 구독 동작을 검증한다.
// ============================================================================
using System;
using System.Reflection;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public sealed class KiwoomFreezeRelayTests
    {
        // field-like event 의 백킹 델리게이트를 발생(없으면 no-op — 미구독 증명).
        private static void Raise(KiwoomBridgeClient b, string eventName, params object[] args)
        {
            var f = typeof(KiwoomBridgeClient).GetField(eventName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(f);   // 이벤트 이름 오타 방지
            var del = (Delegate?)f!.GetValue(b);
            del?.DynamicInvoke(args);
        }

        private static (KiwoomFreezeRelay relay, KiwoomBridgeClient bridge, OrderAttestationService att)
            Wire(bool freezeOnAnyError = false)
        {
            var att    = new OrderAttestationService();
            var bridge = new KiwoomBridgeClient();   // attestation=null → 자체 동결 경로 비활성(Relay 격리)
            var relay  = new KiwoomFreezeRelay(att, bridge, com: null, freezeOnAnyError: freezeOnAnyError);
            relay.Attach();
            return (relay, bridge, att);
        }

        // ════════════════════════ §1 동결 트리거 ═══════════════════════════════════

        [Fact]
        public void ProxyLost_TriggersForceFreeze()
        {
            var (relay, bridge, att) = Wire();
            Raise(bridge, "ProxyLost");
            Assert.True(relay.IsFrozen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void FatalError_TriggersForceFreeze()
        {
            var (relay, bridge, att) = Wire();
            Raise(bridge, "ErrorReceived", new ErrorPayload { ErrorCode = 100, TimestampTicks = 1 });
            Assert.True(relay.IsFrozen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void NotFrozen_BeforeAnyEvent()
        {
            var (relay, _, att) = Wire();
            Assert.False(relay.IsFrozen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        // ════════════════════════ §2 멱등/구독 ════════════════════════════════════

        [Fact]
        public void ProxyLost_Twice_Idempotent()
        {
            var (relay, bridge, att) = Wire();
            Raise(bridge, "ProxyLost");
            Raise(bridge, "ProxyLost");   // 두 번째는 CAS 게이트로 멱등 통과(예외 없음)
            Assert.True(relay.IsFrozen);
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }

        [Fact]
        public void Detach_StopsFreezing()
        {
            var (relay, bridge, att) = Wire();
            relay.Detach();
            Raise(bridge, "ProxyLost");   // 구독 해제 후 → 미동결
            Assert.False(relay.IsFrozen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void Dispose_Detaches()
        {
            var (relay, bridge, att) = Wire();
            relay.Dispose();
            Raise(bridge, "ProxyLost");
            Assert.False(relay.IsFrozen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        // ════════════════════════ §3 freeze-storm 회귀 ════════════════════════════
        //  ★critical 교정 박제: Relay 는 FillReceived 를 구독하지 않는다.
        //    KiwoomBridgeClient.Dispatch 는 OrderRejected 도 FillReceived 로 발행하므로,
        //    Relay 가 구독했다면 평범한 거부(FillPrice=0)가 전체 실거래를 동결(freeze-storm).
        //    체결 감사는 ExecutionBridgeBroker(LiveAuditService) 단일 소유 — Relay 무관.

        [Fact]
        public void FillReceived_DoesNotFreeze_FreezeStormRegression()
        {
            var (relay, bridge, att) = Wire();
            // 거부 유사 페이로드(FillPrice=0) — Relay 가 구독했다면 동결됐을 입력.
            Raise(bridge, "FillReceived",
                new FillPayload { OrderId = 1, SymbolCode = 5930, FillPrice = 0, FillQty = 0, Side = 1, TimestampTicks = 1 });
            Assert.False(relay.IsFrozen);                 // Relay 미구독 → 미동결
            Assert.Equal(GatingMode.Normal, att.Mode);    // freeze-storm 없음
        }

        [Fact]
        public void NormalFill_DoesNotFreeze()
        {
            var (relay, bridge, att) = Wire();
            Raise(bridge, "FillReceived",
                new FillPayload { OrderId = 2, SymbolCode = 5930, FillPrice = 70000, FillQty = 1, Side = 1, TimestampTicks = 1 });
            Assert.False(relay.IsFrozen);
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        // ════════════════════════ §4 에러 정책 ════════════════════════════════════

        [Fact]
        public void ZeroErrorCode_DefaultNoFreeze()
        {
            var (relay, bridge, att) = Wire(freezeOnAnyError: false);
            Raise(bridge, "ErrorReceived", new ErrorPayload { ErrorCode = 0, TimestampTicks = 1 });
            Assert.False(relay.IsFrozen);   // IsFatalError(0)=false → 무동결
            Assert.Equal(GatingMode.Normal, att.Mode);
        }

        [Fact]
        public void ZeroErrorCode_FreezeOnAnyError_Freezes()
        {
            var (relay, bridge, att) = Wire(freezeOnAnyError: true);
            Raise(bridge, "ErrorReceived", new ErrorPayload { ErrorCode = 0, TimestampTicks = 1 });
            Assert.True(relay.IsFrozen);    // 옵트인 → 모든 에러 동결
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);
        }
    }
}
