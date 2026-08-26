// ============================================================================
// NanoPipelineIntegrator — v8.0.0 Phase 8 Task 3: Phase 8 ↔ Phase 9 나노 파이프라인 결속
//
//  ZeroCopyOrderBook(Phase 8) 에서 산출한 ReadOnlySpan<long> 사다리를, 중간 배열/람다/박싱 0으로 Phase 9
//  엔진들에 직결한다:
//    L3 메시지 → ZeroCopyOrderBook.Apply(in-place) → 사다리 스팬 →
//      · ClaudeBlackSwanEngine.TryPrecognize (선지 fast-path: 핑거프린트 적중 시 조립+SIMD검증 동시 스킵)
//      · SpeculativeOrderBook.TryResolve     (추측 캐시 적중)
//      · SimdRiskInterlock.Validate          (SIMD 10호가 검증 + fail-closed Veto 래치; 내부 ValidateCore)
//      · NvmeAuditJournal.TryAppend           (Phase 8 MMF 영속 저널·옵션)
//
//  ※ 명령서 예시의 'ClaudeBlackSwanEngine.Evaluate' 는 실재 API 가 TryPrecognize 이므로(Phase 9 파일 0줄 수정
//    원칙) 그대로 결선한다 — Prime(사전판정)/TryPrecognize(선지 관통)가 정식 계약이다.
//
//  ★불변 제약: δ=0 — 스팬 무복사 전달·값타입 반환·Interlocked. 중간 배열/클로저/boxing 0. 결정론.
//    liveArmed:false(순수 관측·검증 — Veto 신호만 산출). 기존 불변 회로 무접촉(신규 파일·컴포지션).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>나노 파이프라인 1틱 결과(블리터블 값타입·힙 0).</summary>
    public readonly struct PipelineResult
    {
        public readonly bool        Applied;      // L3 메시지 파싱·갱신 성공
        public readonly bool        SwanHit;      // ★선지 격자 적중(조립+SIMD검증 동시 스킵)
        public readonly bool        SpecHit;      // 추측 캐시 적중
        public readonly bool        RiskOk;       // SIMD 리스크 통과
        public readonly RiskAnomaly Reason;
        public readonly long        Fingerprint;
        public readonly bool        Journaled;    // MMF 저널 기입됨
        public PipelineResult(bool applied, bool swanHit, bool specHit, bool riskOk, RiskAnomaly reason, long fp, bool journaled)
        { Applied = applied; SwanHit = swanHit; SpecHit = specHit; RiskOk = riskOk; Reason = reason; Fingerprint = fp; Journaled = journaled; }
    }

    /// <summary>Phase 8↔9 제로카피 오케스트레이터(컴포지션·δ=0). 구성 엔진의 수명은 호출자가 관리한다.</summary>
    public sealed class NanoPipelineIntegrator
    {
        private readonly ZeroCopyOrderBook    _book;
        private readonly SimdRiskInterlock    _risk;
        private readonly SpeculativeOrderBook _spec;
        private readonly ClaudeBlackSwanEngine _swan;
        private readonly NvmeAuditJournal?    _journal;
        private readonly SimdRiskLimits       _limits;

        private long _processed, _swanHits, _specHits, _riskVetoes, _journaled;

        public NanoPipelineIntegrator(ZeroCopyOrderBook book, SimdRiskInterlock risk, SpeculativeOrderBook spec,
                                      ClaudeBlackSwanEngine swan, in SimdRiskLimits limits, NvmeAuditJournal? journal = null)
        {
            _book    = book ?? throw new ArgumentNullException(nameof(book));
            _risk    = risk ?? throw new ArgumentNullException(nameof(risk));
            _spec    = spec ?? throw new ArgumentNullException(nameof(spec));
            _swan    = swan ?? throw new ArgumentNullException(nameof(swan));
            _limits  = limits;
            _journal = journal;
        }

        public ZeroCopyOrderBook Book => _book;
        public long Processed  => Interlocked.Read(ref _processed);
        public long SwanHits   => Interlocked.Read(ref _swanHits);
        public long SpecHits   => Interlocked.Read(ref _specHits);
        public long RiskVetoes => Interlocked.Read(ref _riskVetoes);
        public long Journaled  => Interlocked.Read(ref _journaled);

        /// <summary>
        /// L3 메시지 1건을 파이프라인 관통(δ=0): 갱신 → 사다리 스팬 → Phase 9(선지→추측→SIMD) → Phase 8 저널.
        /// </summary>
        public PipelineResult Process(ReadOnlySpan<byte> l3msg)
        {
            bool applied = _book.Apply(l3msg);

            ReadOnlySpan<long> bidPx = _book.GetBidPriceSpan(), bidQty = _book.GetBidQtySpan();
            ReadOnlySpan<long> askPx = _book.GetAskPriceSpan(), askQty = _book.GetAskQtySpan();
            long fp = SpeculativeOrderBook.FingerprintLadder(bidPx, bidQty, askPx, askQty);

            // ★fail-closed 최우선: 비가역 Veto 가 이미 래치된 상태면 모든 경로(선지 fast-path 포함)가 무조건 not-OK 를
            //   보고한다. 선지 적중이 기존 래치를 무시하고 통과(RiskOk=true)를 산출하던 fail-open 을 원천 차단.
            if (!_risk.IsOrderEmissionAllowed)
            {
                Interlocked.Increment(ref _processed);
                return new PipelineResult(applied, false, false, false, RiskAnomaly.LatchedVeto, fp, false);
            }

            // ★선지 fast-path: 핑거프린트 적중 시 사전판정 재사용(조립+검증 동시 스킵·Effective 0ns).
            if (_swan.TryPrecognize(fp, out bool sOk, out int sReason))
            {
                Interlocked.Increment(ref _swanHits);
                // ★선지 판정이 veto(sOk=false)면 캐시 적중이라도 실제 비가역 하드웨어 퓨즈를 구동한다(fail-closed 복원).
                //   그냥 보고만 하고 넘어가면 리스크 위반이 영구 차단을 트립하지 못한다 — Validate 재실행으로 래치·ForceFreeze·계수 확정.
                if (!sOk)
                {
                    _risk.Validate(bidPx, bidQty, askPx, askQty, _limits);
                    Interlocked.Increment(ref _riskVetoes);
                }
                Interlocked.Increment(ref _processed);
                return new PipelineResult(applied, true, false, sOk, (RiskAnomaly)sReason, fp, false);
            }

            bool specHit = _spec.TryResolve(fp, out _);
            if (specHit) Interlocked.Increment(ref _specHits);

            SimdRiskVerdict v = _risk.Validate(bidPx, bidQty, askPx, askQty, _limits);   // SIMD + 비가역 Veto 래치
            if (!v.Ok) Interlocked.Increment(ref _riskVetoes);

            bool journaled = _journal != null && _journal.TryAppend(fp, _book.Version, Interlocked.Read(ref _processed));
            if (journaled) Interlocked.Increment(ref _journaled);

            Interlocked.Increment(ref _processed);
            return new PipelineResult(applied, false, specHit, v.Ok, v.Reason, fp, journaled);
        }

        /// <summary>현재 호가창으로 추측기·선지 격자를 예열(유휴·δ=0): 상위-3 델타 + 현재책 사전판정.</summary>
        public void PrimeFromCurrent(long tickDelta)
        {
            ReadOnlySpan<long> bidPx = _book.GetBidPriceSpan(), bidQty = _book.GetBidQtySpan();
            ReadOnlySpan<long> askPx = _book.GetAskPriceSpan(), askQty = _book.GetAskQtySpan();
            _spec.PrimeFromTrend(bidPx, bidQty, askPx, askQty, tickDelta);
            long fp = SpeculativeOrderBook.FingerprintLadder(bidPx, bidQty, askPx, askQty);
            _swan.Prime(0, fp, bidPx, bidQty, askPx, askQty, _limits);   // 현재책(불변 후보) 사전판정 봉인
        }
    }
}
