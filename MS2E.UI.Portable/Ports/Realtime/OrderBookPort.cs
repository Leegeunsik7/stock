// ============================================================================
// OrderBookPort — v12.3.0 High-Throughput Order Book Depth Matrix Port (순수 UI 경계)
//
//   OrderBookDepthMatrixCoalescer(MS2E.Core)의 소비자 계약 — 최신 L2/L3 호가 스냅샷을 원시 값(decimal/long)만
//   으로 노출한다. ★엔진 타입 참조 0(RCL 순수성) — 어댑터(Infrastructure 계층)만 구체 엔진을 알고 있다.
//   ICryptoStreamPort 와 동일 계층(Ports/Realtime)에 위치해 실시간 스트림 Port 군을 이룬다.
// ============================================================================
using System.Collections.Generic;

namespace MS2E.UI.Portable.Ports.Realtime
{
    /// <summary>호가 1단(레벨) 뷰.</summary>
    public interface IOrderBookLevelView
    {
        decimal Price { get; }
        decimal Qty { get; }
    }

    /// <summary>최신 L2/L3 호가 매트릭스 스냅샷 — 매수/매도 각 depth 단, 잔량 불균형비 포함.</summary>
    public interface IOrderBookSnapshotView
    {
        string Symbol { get; }
        bool HasData { get; }
        long SequenceNumber { get; }
        /// <summary>호가 잔량 불균형비 — -1.0(매도 우위) .. +1.0(매수 우위).</summary>
        decimal ImbalanceRatio { get; }
        IReadOnlyList<IOrderBookLevelView> Bids { get; }
        IReadOnlyList<IOrderBookLevelView> Asks { get; }
    }

    public interface IOrderBookPort
    {
        /// <summary>현재 최신 호가 스냅샷 조회(UI 폴링/타이머에서 호출 — 힙 할당은 이 경계에서만 발생, 엔진 핫패스는 δ=0).</summary>
        IOrderBookSnapshotView Snapshot();
    }
}
