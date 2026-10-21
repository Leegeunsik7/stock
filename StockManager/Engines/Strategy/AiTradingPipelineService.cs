// ============================================================================
// AiTradingPipelineService — [v12.18.49-DEV] 호가/거래 상태 → AI 신호 → AiExecutionSupervisor → 발주 싱크(큐리스 · δ=0) + 호스트 결속 단위
//
//  왜: AiExecutionSupervisor(§139)는 후보만 만들고 끝났다 — 호가 피드 · 거래 상태(VI · 정지) · 발주 경로와 이어진 곳이 없었다.
//  구조(생성 시 1회 할당 · 핫패스 δ=0 · 잠금 · 큐 0):
//   · OnQuote(종목, 기준가, 최우선 매수/매도, 거래 상태, 시각) → 감독자 컨텍스트(seqlock · 상태 원자 보관).
//   · OnTradingStatus(종목, 상태, 시각) → 상태 전용 통지(VI 발동/해제 · 정지/재개 · 제한 = 고정 · 호가로 해제 불가).
//   · OnSignal(AI 신호, now) → ① 비활성 = Inactive(감독자 · 라우터 무접촉) ② 감독자(인터락 · 상태 Normal 만 · 신선도 · ±30% · 격자 · 캡)
//     ③ 승인 후보 → 싱크.TrySubmit(같은 스레드 즉시 인계 · 실패 = SinkRejected).
//  ★기본 비활성(IsAutonomousTradingActive = false) — 활성 = 물리 이중키(Valve · LiveArming) 동시 확인 시에만 · 비활성화는 언제든 즉시.
//   비활성 중에도 OnQuote/OnTradingStatus 는 계속 반영(재활성 직후 낡은 컨텍스트로 판정하지 않게).
//  싱크(IAiProposalSink · 구현은 Adapters/AiProposalSinks.cs):
//   · DispatchRingAiProposalSink = 호스트 결속 — OrderDispatchRing 게시 → 기존 무장 게이트(GatedOrderDispatcher · liveArmed:false 면 송출 0) →
//     OrderDrainWorker → RouterFeedGate.TryAdmit → 브로커. 송출 경로는 하나뿐(무장 우회 0).
//   · RouterFeedGateAiProposalSink = TryAdmit 직결(시뮬레이션 · 테스트 전용) — 무장 게이트를 거치지 않으므로 호스트에 결속하지 않는다
//     (TryAdmit 은 라우터 슬롯 확보 + 추적 등록까지 하며 실제 송출은 드레인 워커 몫 — 직결하면 송출 안 된 추적 주문이 남는다).
//  ★정직: 실 AI 모델 피드 · VI/정지 상태 피드 미결선(가짜 배선 금지) — 호스트의 키움 10호가는 상태를 싣지 않으므로 Unknown 으로 반영 →
//   상태 피드가 결선되기 전까지 모든 종목 MarketNotTradable(정상 판정 0). liveArmed:false 불변 · 이 파일에 브로커 참조 0.
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Engines.Strategy
{
    /// <summary>승인 후보 인계 대상(δ=0 · 비throw 권장 · 같은 스레드 즉시 호출). false = 미인계.</summary>
    public interface IAiProposalSink
    {
        bool TrySubmit(in OrderProposal proposal);
    }

    public enum AiPipelineVerdict : byte { Submitted = 0, Inactive = 1, SupervisorRejected = 2, SinkRejected = 3, SinkFault = 4 }

    /// <summary>거래 상태 플래그 → MarketTradingStatus(순수 · δ=0). 우선순위 = 정지 > 정적 VI > 동적 VI > 정상.</summary>
    public static class MarketTradingStatusCodec
    {
        public static MarketTradingStatus FromFlags(bool halted, bool staticVi, bool dynamicVi)
            => halted ? MarketTradingStatus.Halted
             : staticVi ? MarketTradingStatus.StaticVi
             : dynamicVi ? MarketTradingStatus.DynamicVi
             : MarketTradingStatus.Normal;

        /// <summary>발주 허용 = Normal 단 하나(미상 · 미정의 값 포함 전부 차단).</summary>
        public static bool IsTradable(MarketTradingStatus s) => s == MarketTradingStatus.Normal;
    }

    public sealed class AiTradingPipelineService
    {
        private readonly AiExecutionSupervisor _supervisor;
        private readonly IAiProposalSink _sink;
        private int _active;
        private long _signals, _submitted, _inactive, _supervisorRejected, _sinkRejected, _sinkFaults, _quotes, _statusEvents;

        public AiTradingPipelineService(AiExecutionSupervisor supervisor, IAiProposalSink sink)
        {
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        }

        public AiExecutionSupervisor Supervisor => _supervisor;
        /// <summary>자율 매매 활성(기본 false). 활성 = TryActivate(물리 이중키) 만.</summary>
        public bool IsAutonomousTradingActive => Volatile.Read(ref _active) == 1;
        public long Signals => Interlocked.Read(ref _signals);
        public long Submitted => Interlocked.Read(ref _submitted);
        public long InactiveRejects => Interlocked.Read(ref _inactive);
        public long SupervisorRejects => Interlocked.Read(ref _supervisorRejected);
        public long SinkRejects => Interlocked.Read(ref _sinkRejected);
        public long SinkFaults => Interlocked.Read(ref _sinkFaults);
        public long Quotes => Interlocked.Read(ref _quotes);
        public long StatusEvents => Interlocked.Read(ref _statusEvents);

        /// <summary>자율 매매 활성 — 물리 이중키 둘 다 확인될 때만(하나라도 false = 거부 · 상태 무변경).</summary>
        public bool TryActivate(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _active, 1);
            return true;
        }

        /// <summary>즉시 비활성(키 불요 · 다음 신호부터 차단).</summary>
        public void Deactivate() => Volatile.Write(ref _active, 0);

        /// <summary>L1 호가 + 이 피드가 실어 온 거래 상태(상태 모름 = Unknown → 기존 유지). δ=0.</summary>
        public MarketContextVerdict OnQuote(long symbolCode, long basePriceRaw, long bestBidRaw, long bestAskRaw, MarketTradingStatus status, long timestampMicros,
                                           long maxSymbolQty = long.MaxValue)
        {
            Interlocked.Increment(ref _quotes);
            return _supervisor.UpdateMarketContext(symbolCode, basePriceRaw, bestBidRaw, bestAskRaw, status, timestampMicros, maxSymbolQty);
        }

        /// <summary>거래 상태 전용 통지(VI 발동/해제 · 정지/재개). δ=0.</summary>
        public MarketContextVerdict OnTradingStatus(long symbolCode, MarketTradingStatus status, long timestampMicros)
        {
            Interlocked.Increment(ref _statusEvents);
            return _supervisor.UpdateTradingStatus(symbolCode, status, timestampMicros);
        }

        /// <summary>AI 신호 1건 → 감독자 → 싱크(δ=0). Submitted 일 때만 submitted 유효.</summary>
        public AiPipelineVerdict OnSignal(in AiSignal signal, long nowMicros, out OrderProposal submitted, out AiSupervisorVerdict supervisorVerdict)
        {
            submitted = default;
            supervisorVerdict = AiSupervisorVerdict.InterlockBlocked;
            Interlocked.Increment(ref _signals);
            if (Volatile.Read(ref _active) == 0) { Interlocked.Increment(ref _inactive); return AiPipelineVerdict.Inactive; }   // 평가 전(라우터 무접촉)

            supervisorVerdict = _supervisor.ProcessSignal(in signal, nowMicros, out var candidate);
            if (supervisorVerdict != AiSupervisorVerdict.Accepted) { Interlocked.Increment(ref _supervisorRejected); return AiPipelineVerdict.SupervisorRejected; }
            if (Volatile.Read(ref _active) == 0) { Interlocked.Increment(ref _inactive); return AiPipelineVerdict.Inactive; }   // 평가 중 비활성화 = 폐기

            bool ok;
            try { ok = _sink.TrySubmit(in candidate); }
            catch (Exception) { Interlocked.Increment(ref _sinkFaults); return AiPipelineVerdict.SinkFault; }   // 싱크 예외 = 미인계(fail-closed)
            if (!ok) { Interlocked.Increment(ref _sinkRejected); return AiPipelineVerdict.SinkRejected; }
            submitted = candidate;
            Interlocked.Increment(ref _submitted);
            return AiPipelineVerdict.Submitted;
        }

        /// <summary>편의 오버로드.</summary>
        public AiPipelineVerdict OnSignal(in AiSignal signal, long nowMicros) => OnSignal(in signal, nowMicros, out _, out _);
    }
}
