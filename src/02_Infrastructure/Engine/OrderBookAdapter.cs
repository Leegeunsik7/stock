// ============================================================================
// OrderBookAdapter — v12.3.0 IOrderBookPort 구현(Ports/Adapters 결속)
//
//   OrderBookDepthMatrixCoalescer(MS2E.Core, δ=0 seqlock 링)의 최신 스냅샷을 IOrderBookPort 원시 뷰로 투사한다.
//   ★RCL(Portable)은 이 파일의 구체 타입을 전혀 모른다 — IOrderBookPort 인터페이스만 소비(0 Concrete Ref 유지).
//   심볼당 1 어댑터(단일 책무) — 다중 심볼은 상위(DI 컨테이너/팩토리)가 심볼별 인스턴스를 구성한다.
// ============================================================================
using System;
using System.Collections.Generic;

namespace MS2E.Infrastructure
{
    public sealed class OrderBookAdapter : MS2E.UI.Portable.Ports.Realtime.IOrderBookPort
    {
        private sealed record LevelView(decimal Price, decimal Qty) : MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView;

        private sealed class SnapshotView : MS2E.UI.Portable.Ports.Realtime.IOrderBookSnapshotView
        {
            public string Symbol { get; init; } = string.Empty;
            public bool HasData { get; init; }
            public long SequenceNumber { get; init; }
            public decimal ImbalanceRatio { get; init; }
            public IReadOnlyList<MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView> Bids { get; init; } = Array.Empty<MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView>();
            public IReadOnlyList<MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView> Asks { get; init; } = Array.Empty<MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView>();
        }

        private readonly string _symbol;
        private readonly MS2E.Core.OrderBookDepthMatrixCoalescer _engine;
        private readonly int _depth;
        private readonly decimal _priceScale;   // raw → decimal 나눗셈 스케일(예: 100 = 소수 2자리)

        public OrderBookAdapter(string symbol, MS2E.Core.OrderBookDepthMatrixCoalescer engine, decimal priceScale = 100m)
        {
            _symbol = symbol;
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _depth = engine.Depth;
            _priceScale = priceScale > 0 ? priceScale : 100m;
        }

        public MS2E.UI.Portable.Ports.Realtime.IOrderBookSnapshotView Snapshot()
        {
            var bidPx = new long[_depth]; var bidQty = new long[_depth];
            var askPx = new long[_depth]; var askQty = new long[_depth];

            if (!_engine.TryProjectLatest(bidPx, bidQty, askPx, askQty, out long seq, out long imbalancePpm))
                return new SnapshotView { Symbol = _symbol, HasData = false };

            var bids = new List<MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView>(_depth);
            var asks = new List<MS2E.UI.Portable.Ports.Realtime.IOrderBookLevelView>(_depth);
            for (int i = 0; i < _depth; i++)
            {
                bids.Add(new LevelView(bidPx[i] / _priceScale, bidQty[i] / _priceScale));
                asks.Add(new LevelView(askPx[i] / _priceScale, askQty[i] / _priceScale));
            }

            return new SnapshotView
            {
                Symbol = _symbol,
                HasData = true,
                SequenceNumber = seq,
                ImbalanceRatio = imbalancePpm / 1_000_000m,
                Bids = bids,
                Asks = asks,
            };
        }
    }
}
