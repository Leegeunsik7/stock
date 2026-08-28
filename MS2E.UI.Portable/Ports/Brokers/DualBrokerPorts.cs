// ============================================================================
// DualBrokerPorts — v11.3.0: 키움+토스 이중 실통신 관제 Port & 순수 DTO (조회 전용)
//
//   순수 UI(RCL)가 키움(x86 COM IPC)·토스(OAuth REST) 라인의 실시간 상태를 콘크리트 어댑터/브로커 타입을 0개
//   참조한 채 관제하도록 하는 경계. 호스트 어댑터(DualBrokerStatusAdapter)가 KiwoomBridgeClient·TossTokenService·
//   TossLiveApiGovernor·IBrokerAccountPort 를 이 Port 뒤로 격리한다.
//
//   ★불변: liveArmed:false(★이 Port 에는 매수/매도/발주/변조 메서드가 존재하지 않는다 — 조회·관제 전용) ·
//     fail-closed(미동기화/미인증/서비스 부재 → HasData=false·Health=Offline·N/A(<0)) · δ=0(스팬 기입·값타입 DTO).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Brokers
{
    /// <summary>관제 대상 브로커 라인.</summary>
    public enum BrokerLine { Kiwoom = 0, Toss = 1 }

    /// <summary>브로커 라인 헬스(UI 점등용). SafeHold = 워치독 격리 래치.</summary>
    public enum LineHealth { Offline = 0, Connecting = 1, Online = 2, Degraded = 3, SafeHold = 4 }

    /// <summary>브로커 라인 실시간 상태(값타입). 음수(&lt;0) 필드 = 해당 라인에 N/A(예: 키움엔 토큰만료 없음).</summary>
    public readonly record struct BrokerLineStatus(
        BrokerLine Line,
        LineHealth Health,
        bool Connected,
        long HeartbeatAgeMs,        // 마지막 생존신호 경과(ms). 키움=pong 경과 / 토스=최근 성공 경과. <0 = N/A
        long TokenExpiresInSec,     // 토스 액세스 토큰 만료까지(초). 키움/무토큰 = -1
        long Http429Count,          // 토스 누적 429. 키움/미가용 = -1
        long ConsecutiveFailures,   // 토스 연속 인증 실패. 미가용 = -1
        string Detail);             // 서킷/비고(고정 문자열 — 무할당)

    /// <summary>계좌 비교 라인(양 브로커 병렬표). 금액=표시용 decimal(어댑터가 QFixed raw→환산).</summary>
    public readonly record struct BrokerAccountLine(
        BrokerLine Line,
        bool HasData,
        decimal TotalEval,
        decimal TotalPnl,
        decimal Deposit,
        decimal BuyingPower,
        int HoldingCount);

    /// <summary>보유 종목 비교 라인(값타입). 종목코드=FNV 해시(string 무할당).</summary>
    public readonly record struct BrokerHoldingLine(
        BrokerLine Line,
        long SymbolHash,
        long Quantity,
        decimal AvgPrice,
        decimal CurPrice,
        decimal UnrealizedPnl,
        bool IsOverseas);

    /// <summary>
    /// 이중 브로커 조회/관제 Port. ★발주/변조 메서드 없음(read-only) — liveArmed:false 물리 봉인.
    /// 모든 메서드는 호출부 제공 스팬에 기입(δ=0). 미가용/미동기화는 fail-closed(빈 결과·N/A).
    /// </summary>
    public interface IDualBrokerStatusPort
    {
        /// <summary>라인 상태 스냅샷(키움·토스). 반환=기입 개수(≤2).</summary>
        int GetLineStatus(Span<BrokerLineStatus> dest);

        /// <summary>계좌 비교 스냅샷(키움·토스). 반환=기입 개수(≤2). 미동기화 라인은 HasData=false.</summary>
        int GetAccounts(Span<BrokerAccountLine> dest);

        /// <summary>브로커별 보유 종목. 반환=기입 개수(dest 용량 한도).</summary>
        int GetHoldings(BrokerLine line, Span<BrokerHoldingLine> dest);

        /// <summary>★발주 무장 여부 — 항상 false. 조회/관제가 동작해도 실 주문 패킷은 100% Veto.</summary>
        bool LiveArmed { get; }
    }
}
