// ============================================================================
// IOrderPathObserver — [v12.17.6 · 실발주 경로 계측] 주문 수명 경계 관측 계약(발행 · 종결)
//
//  OrderDispatchRing 이 발행 시(단일 생산자 CAS 안 · 링 게시 직전) OnPublished 를, GatedOrderDispatcher 드레인이 제안 1건의 처분을
//  끝낼 때(미무장 차단 · 블래스트/fat-finger 동결 · 송출 · 하류 veto · throw — 경로 무관 정확히 1회) OnDispatchEnd 를 호출한다.
//  인프라는 계측기 구상 타입을 모른다(호스트 StockManager.Telemetry.OrderPathStageProfiler 가 구현) — 계층 역참조 0.
//  ★구현 계약: δ=0 · 비차단(락/대기 0) · 비throw. 생산자 스레드(OnPublished)와 드레인 스레드(OnDispatchEnd)가 다르다.
// ============================================================================
namespace MS2E.Infrastructure
{
    public interface IOrderPathObserver
    {
        /// <summary>제안 발행(시그널 → 주문 제안 시각). 생산자 스레드.</summary>
        void OnPublished(long orderId);

        /// <summary>드레인이 그 제안의 처분을 끝냄(forwarded = 실 송출 계상 여부). 드레인 스레드.</summary>
        void OnDispatchEnd(long orderId, bool forwarded);
    }
}
