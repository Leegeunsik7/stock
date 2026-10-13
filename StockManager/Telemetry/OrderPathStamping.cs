// ============================================================================
// OrderPathStamping — [v12.17.6 · 실발주 경로 계측] 라우터 게이트 · 브로커 출구 스탬프 데코레이터(프로덕션 네임스페이스)
//
//  v12.17.4-DEV 드라이런 하네스(StockManager.Simulation)에 있던 두 데코레이터를 호스트가 쓸 수 있도록 여기로 이전했다.
//   ShippedBinaryLiveArmedGuard ⑥(시뮬레이션 격리)은 호스트 조립부의 StockManager.Simulation 참조를 위반으로 거부한다 — 호스트 결선은
//   반드시 이 네임스페이스의 형식만 쓴다(하네스는 반대 방향 참조 = 허용).
//
//  StageStampingSplitGate: 판정은 내부 게이트(RouterFeedGate) 그대로 · 승인 반환 직후 MarkGate(S0) · 거부 = 상관 슬롯 즉시 회수(방향 A).
//   이전 부모 종료(EndOrder)는 디스패처 종결 관측자(IOrderPathObserver.OnDispatchEnd)가 정확히 하지만, 관측자 미결선 사용(하네스 단독)을
//   위해 승인 직전 EndOrder 도 유지한다(관측자가 이미 종결했으면 활성 부모 0 = no-op).
//  EgressStageProbe: 출구 초크포인트(LiveArmedEgressSentinel) '앞'에 선다 — 진입 시각 · 판정(전달/veto) 반환 후 MarkEgress(S1 · S2 후보).
//   전달 · veto · 예외는 센티널 그대로(예외는 스탬프 없이 전파 — 드레인 벽이 동결). liveArmed:false 면 센티널이 거부하고 그 '판정까지의 시간'이 기록된다.
//  핫패스 비용: 게이트 = 시계 1회 + 히스토그램 원자 증가 4회 · 출구 = 시계 2회 + 원자 증가 수회 — 할당 0 · 락 0 · 1µs 미만(테스트 실측).
// ============================================================================
using System;
using MS2E.Infrastructure;

namespace StockManager.Telemetry
{
    /// <summary>라우터 게이트 데코레이터 — 판정 위임 · 승인 = MarkGate · 거부 = 슬롯 즉시 회수.</summary>
    public sealed class StageStampingSplitGate : IChildSplitRouteGate
    {
        private readonly IChildSplitRouteGate _inner;
        private readonly OrderPathStageProfiler _stages;

        public StageStampingSplitGate(IChildSplitRouteGate inner, OrderPathStageProfiler stages)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        }

        /// <summary>감싼 실 게이트(진단 · 테스트 판독).</summary>
        public IChildSplitRouteGate Inner => _inner;

        public bool TryAdmitSplit(in OrderProposal proposal, Span<ChildOrderPlan> children, out int count)
        {
            _stages.EndOrder();
            bool ok = _inner.TryAdmitSplit(in proposal, children, out count);
            if (ok) _stages.MarkGate(proposal.OrderId); else _stages.Abandon(proposal.OrderId);
            return ok;
        }

        public bool TryAdmit(in OrderProposal proposal, out OrderAdmission admission)
        {
            _stages.EndOrder();
            bool ok = _inner.TryAdmit(in proposal, out admission);
            if (ok) _stages.MarkGate(proposal.OrderId); else _stages.Abandon(proposal.OrderId);
            return ok;
        }

        public void OnBrokerAccepted(long proposalId, string brokerOrderId) => _inner.OnBrokerAccepted(proposalId, brokerOrderId);
        public void OnNotSent(long proposalId) => _inner.OnNotSent(proposalId);
    }

    /// <summary>출구 데코레이터 — 센티널 앞 진입 시각 · 판정 반환 후 MarkEgress. 판정 · 전달 · 취소 · 보유 조회는 위임.</summary>
    public sealed class EgressStageProbe : IBroker
    {
        private readonly IBroker _inner;
        private readonly OrderPathStageProfiler _stages;

        public EgressStageProbe(IBroker inner, OrderPathStageProfiler stages)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _stages = stages ?? throw new ArgumentNullException(nameof(stages));
        }

        /// <summary>감싼 출구(센티널) — 진단 · 테스트 판독.</summary>
        public IBroker Inner => _inner;
        public string Name => _inner.Name;
        public bool IsLive => _inner.IsLive;

        public (bool ok, string msg) Place(PaperAsset asset, string symbol, string name, PaperSide side, PaperKind kind, decimal qty, decimal limitPrice, decimal currentPrice)
        {
            long t = _stages.Timestamp();
            var r = _inner.Place(asset, symbol, name, side, kind, qty, limitPrice, currentPrice);
            _stages.MarkEgress(t, r.ok);
            return r;
        }

        public void Cancel(string orderId) => _inner.Cancel(orderId);
        public decimal HeldQty(PaperAsset asset, string symbol) => _inner.HeldQty(asset, symbol);
    }
}
