// ============================================================================
// TossPortfolioTypes — v10.2.0: 토스증권 포트폴리오 Ports & 순수 DTO
//
//   조회 전용(read-only) 계좌/포트폴리오 동기화의 Port 경계 + δ=0 값타입 DTO. 어댑터(TossPortfolioSyncAdapter)가
//   토스 REST API 를 이 Port 뒤로 추상화한다. ★DTO 는 전부 readonly record struct(값타입·힙0), 금액은 고정소수 raw
//   (QFixed 관례), 종목코드는 FNV 해시(string 무할당).
//
//   ★불변: liveArmed:false(조회 전용 Port — 발주/변조 메서드 자체가 없음·IBroker·소켓 참조 0) · fail-closed(미동기화/
//     파싱 실패 → TryGet false).
// ============================================================================
using System;

namespace MS2E.Core
{
    /// <summary>보유 종목(δ=0 값타입). 금액=고정소수 raw, 종목코드=FNV 해시(string 무할당).</summary>
    public readonly record struct TossHolding(
        long SymbolHash,
        long Quantity,
        long AvgPriceRaw,
        long CurPriceRaw,
        long UnrealizedPnlRaw,
        bool IsOverseas);

    /// <summary>계좌 스냅샷(δ=0 값타입). 총평가·총손익·예수금·매수가능·보유종목수·스냅샷시각(µs).</summary>
    public readonly record struct TossAccountSnapshot(
        long TotalEvalRaw,
        long TotalPnlRaw,
        long DepositRaw,
        long BuyingPowerRaw,
        int  HoldingCount,
        long AsOfMicros);

    /// <summary>조회 전용 브로커 계좌 Port. ★발주/변조 메서드 없음(read-only fail-closed·liveArmed:false).</summary>
    public interface IBrokerAccountPort
    {
        bool TryGetAccount(out TossAccountSnapshot snapshot);   // 미동기화 → false(fail-closed)
        int GetHoldings(Span<TossHolding> dest);                // 반환=기입 개수(δ=0 복사)
    }

    /// <summary>포트폴리오 동기화 Port(콜드 트리거). 실 REST 호출은 어댑터가 담당.</summary>
    public interface IPortfolioSyncPort
    {
        bool SyncNow();          // 1회 동기화 트리거(성공=true). fail-closed(권한/인증 결여 → false·미변경)
        long LastSyncMicros { get; }
        bool Connected { get; }  // 최근 동기화 성공 상태
    }
}
