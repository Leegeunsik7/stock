// ============================================================================
// UniversalOrderExecutionTypes — v11.0.0: 실물 주문 집행 Ports & DTO
//
//   조회 전용 메쉬(v10.2.0) 위에 실자본 발주(매수/매도/취소) 경계를 정의한다. ★모든 발주는 UniversalOrderExecutionAdapter
//   가 GenesisLiveIgnitionGateway.Emit(물리 이중 키 인터락) 을 경유하므로, liveArmed 미충족이면 100% veto(실물 0비트).
//
//   ★불변: liveArmed:false(발주는 이중 물리 키 결속 전 100% 거부) · fail-closed · δ=0(SendOrder 게이트 판정 힙 0).
// ============================================================================
using System;

namespace MS2E.Core
{
    // ★매수/매도 방향은 기존 QFixed.OrderSide(Buy=0, Sell=1) 재사용(엔진 canonical).
    public enum ExecOrderKind : byte { None = 0, Limit = 1, Market = 2, Cancel = 3 }

    /// <summary>발주 요청(δ=0 값타입). 금액/수량=고정소수 raw, 종목=코드(long).</summary>
    public readonly record struct ExecOrderRequest(BrokerId Broker, long SymbolCode, OrderSide Side, ExecOrderKind Kind, long PriceRaw, long QtyRaw);

    /// <summary>발주 결과(δ=0 값타입). Accepted=false 면 VetoCode 에 사유(음수). ClOrdId=클라이언트 주문 ID(단조).</summary>
    public readonly record struct ExecOrderResult(bool Accepted, long ClOrdId, long VetoCode);

    /// <summary>실 증권사 HTTP 발주 델리게이트 — ★GenesisLiveIgnitionGateway.Emit 게이트 하류에서만 호출(liveArmed 충족 시).</summary>
    public delegate void BrokerOrderSend(BrokerId broker, in SealedOrderToken token);

    /// <summary>실물 주문 집행 Port. SendOrder 는 하드웨어 이중 키 미충족/veto 시 Accepted=false(O(1) fail-closed).</summary>
    public interface IOrderExecutionPort
    {
        ExecOrderResult SendOrder(in ExecOrderRequest request);
    }
}
