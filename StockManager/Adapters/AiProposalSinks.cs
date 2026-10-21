// ============================================================================
// AiProposalSinks — [v12.18.49-DEV] AiTradingPipelineService 승인 후보 인계 어댑터 2종(δ=0)
//
//  · DispatchRingAiProposalSink(호스트 결속): OrderDispatchRing.Publish → 기존 무장 게이트(GatedOrderDispatcher) → OrderDrainWorker →
//    RouterFeedGate.TryAdmit → 브로커. 링이 OrderId 를 새로 발급(호스트 단일 id 공간 · 라우터 키 충돌 0). 링은 단일 생산자 계약을 CAS 로
//    강제(동시 발행 = InvalidOperationException) — 경합 = 그 회차 미인계(false · fail-closed · 재시도/큐잉 없음).
//  · RouterFeedGateAiProposalSink(시뮬레이션 · 테스트 전용): RouterFeedGate.TryAdmit 직결 — 무장 게이트 우회라 호스트에 결속 금지.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using StockManager.Engines.Strategy;

namespace StockManager.Adapters
{
    public sealed class DispatchRingAiProposalSink : IAiProposalSink
    {
        private readonly OrderDispatchRing _ring;
        private long _published, _contended;

        public DispatchRingAiProposalSink(OrderDispatchRing ring) => _ring = ring ?? throw new ArgumentNullException(nameof(ring));

        public long Published => Interlocked.Read(ref _published);
        public long Contended => Interlocked.Read(ref _contended);

        public bool TrySubmit(in OrderProposal proposal)
        {
            try { _ring.Publish(proposal.GenomeId, proposal.SymbolCode, proposal.Side, proposal.KindByte, proposal.Price, proposal.Qty, proposal.TimestampTicks); }
            catch (InvalidOperationException) { Interlocked.Increment(ref _contended); return false; }   // 단일 생산자 경합 = 미인계
            Interlocked.Increment(ref _published);
            return true;
        }
    }

    public sealed class RouterFeedGateAiProposalSink : IAiProposalSink
    {
        private readonly RouterFeedGate _gate;
        private long _admitted, _rejected;
        private OrderAdmission _last;

        public RouterFeedGateAiProposalSink(RouterFeedGate gate) => _gate = gate ?? throw new ArgumentNullException(nameof(gate));

        public long Admitted => Interlocked.Read(ref _admitted);
        public long Rejected => Interlocked.Read(ref _rejected);
        /// <summary>마지막 승인 송출 형태(관제 · 단일 호출자 판독).</summary>
        public OrderAdmission LastAdmission => _last;

        public bool TrySubmit(in OrderProposal proposal)
        {
            if (!_gate.TryAdmit(in proposal, out var admission)) { Interlocked.Increment(ref _rejected); return false; }
            _last = admission;
            Interlocked.Increment(ref _admitted);
            return true;
        }
    }
}
