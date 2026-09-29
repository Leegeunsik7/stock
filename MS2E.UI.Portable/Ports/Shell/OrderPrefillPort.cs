// ============================================================================
// OrderPrefillPort — v12.6.0 Advisory-Only Order Prep Bridge (순수 UI 경계)
//
//   AI 추천 카드 → 주문 입력 폼 간 "종목코드/목표가"만 전달하는 상태 공유 Port(ISymbolContextPort 와 동형 —
//   이벤트 기반 공유 상태, 엔진 무참조). ★TryStage 는 구현체(OrderPrefillAdapter)가 divergenceBlocked=true 를
//   받으면 거부(false)한다 — "주문 예비 연동 자동 차단"을 UI 배지가 아닌 이 Port 의 단일 초크포인트에서 구조적으로
//   강제한다. 이 Port 는 종목코드/가격 숫자만 다룰 뿐 발주 자체를 수행하지 않는다(liveArmed·물리 키 무접촉).
// ============================================================================
using System;

namespace MS2E.UI.Portable.Ports.Shell
{
    public interface IOrderPrefillSnapshot
    {
        bool HasValue { get; }
        string Symbol { get; }
        decimal TargetPrice { get; }
        decimal StopLossPrice { get; }
    }

    public interface IOrderPrefillPort
    {
        IOrderPrefillSnapshot Current { get; }

        /// <summary>프리필 스테이징 시도. divergenceBlocked=true 면 거부(false, 상태 불변) — 자동 차단 초크포인트.</summary>
        bool TryStage(string symbol, decimal targetPrice, decimal stopLossPrice, bool divergenceBlocked);

        void Clear();

        /// <summary>프리필 변경 알림(소비자 화면이 재조회하도록).</summary>
        event Action? OnChange;
    }
}
