// ============================================================================
// AlgoExecutionGate — S19 #5: 라이브 집행 결속 + 3중 강철 안전 회로 차단기
//
//  자율 진화 게놈(StrategySlot)과 실제 주문장(IBroker) 사이의 유선 파이프에 적층되는
//  하드 안전 게이트. 게놈이 폭주하더라도 실거래로 번지지 못하도록 물리적으로 차단한다.
//
//  ── 집행 결정 순서 (가장 보수적인 것부터) ──────────────────────────────────
//    0. 동결 검문   — _algoState 또는 시스템 attestation 이 Frozen 이면 즉시 거부
//    1. 권한 검문   — StrategySlot.CanEnter/CanExit (S19 #3 그레이스 권한 계층)
//    2. 가드3 디싱크 — OrderAttestationService.Reconcile(로컬↔브로커 원장); 불일치 시
//                      즉시 ForceFreeze + AlgoGateState.Frozen (단 1주 불일치도 비상 동결)
//    3. 가드2 크기   — 단일 슬라이스를 자산 MaxOrderPct% 또는 절대 하드캡으로 물리 클램프
//    4. 가드1 드라이런 — IsDryRun(기본 true) 이면 가상 원장만 갱신, 실 브로커 미발주
//
//  ── 안전 기본값 박제 ───────────────────────────────────────────────────────
//  IsDryRun=true 가 하드 기본값. 라이브 발주 경로는 코드/설정 변경 없이는 도달 불가.
//  디싱크 동결은 시스템 attestation 까지 전파되어 ExecutionBridgeBroker 도 함께 차단된다.
//
//  외부 의존성 0 · 동결 상태 volatile 락-프리 독출 · 가상 원장만 lock 보호.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace StockManager.Common
{
    public enum AlgoGateState { Armed, Frozen }

    public enum ExecOutcome
    {
        RejectedFrozen,        // 0 — 이미 동결
        RejectedUnauthorized,  // 1 — 슬롯 권한 없음(예: 청산 전용이 진입 시도)
        RejectedDesync,        // 2 — 원장 불일치 → 비상 동결 발동
        DryRunLogged,          // 4 — 가상 원장 기록(실발주 없음)
        Executed               // 4 — 실 브로커 발주(클램프된 수량)
    }

    public sealed record OrderIntent(
        StrategySpec Strategy, string Symbol, string Name,
        PaperSide Side, PaperKind Kind, decimal Qty, decimal Price, bool IsEntry);

    public sealed record ExecVerdict(ExecOutcome Outcome, decimal ApprovedQty, string Detail);

    public sealed record VirtualFill(
        string Symbol, PaperSide Side, decimal Qty, decimal Price, DateTime Utc);

    public sealed class AlgoExecutionGate
    {
        private readonly StrategySlot            _slot;
        private readonly OrderAttestationService _attestation;
        private readonly IBroker?                _broker;          // null 또는 IsDryRun 이면 미사용
        private readonly object                  _ledgerLock = new();
        private readonly List<VirtualFill>       _virtualLedger = new();

        private volatile int _algoState = (int)AlgoGateState.Armed;

        // ── 3중 강철 가드 설정 (전부 init — 안전 기본값 박제) ───────────────────
        /// <summary>가드1 — 기본 true: 승격 게놈이라도 실 브로커 미발주, 가상 원장만 갱신.</summary>
        public bool    IsDryRun       { get; init; } = true;
        /// <summary>가드2 — 단일 슬라이스 최대 자산 비율 (기본 10%).</summary>
        public decimal MaxOrderPct    { get; init; } = 0.10m;
        /// <summary>가드2 — 단일 슬라이스 절대 수량 하드캡.</summary>
        public long    MaxOrderAbsQty { get; init; } = 1000;
        /// <summary>가드3 — 예수금 허용 오차 (기본 0 = 1원도 불일치 불허).</summary>
        public decimal CashTolerance  { get; init; } = 0m;
        /// <summary>가드3 — 수량 허용 오차 (기본 0 = 1주도 불일치 불허).</summary>
        public decimal QtyTolerance   { get; init; } = 0m;

        public AlgoExecutionGate(
            StrategySlot slot, OrderAttestationService attestation, IBroker? broker = null)
        {
            _slot        = slot        ?? throw new ArgumentNullException(nameof(slot));
            _attestation = attestation ?? throw new ArgumentNullException(nameof(attestation));
            _broker      = broker;
        }

        /// <summary>게이트 동결 상태 — 자체 래치 또는 시스템 비상 동결 중 하나라도 참이면 Frozen.</summary>
        public AlgoGateState State =>
            (_algoState == (int)AlgoGateState.Frozen || _attestation.Mode == GatingMode.EmergencyFreeze)
                ? AlgoGateState.Frozen : AlgoGateState.Armed;

        public IReadOnlyList<VirtualFill> VirtualLedger
        {
            get { lock (_ledgerLock) return _virtualLedger.ToArray(); }
        }

        // ── 집행 제출 (3중 가드 통과) ──────────────────────────────────────────

        /// <summary>
        /// 게놈 주문 의도를 3중 안전 가드에 통과시킨다. 실거래 발주는 IsDryRun=false 이고
        /// 브로커가 결속된 경우에만 일어난다. 디싱크 시 즉시 비상 동결.
        /// </summary>
        public ExecVerdict Submit(
            OrderIntent intent, LocalState local, BrokerState broker, decimal totalAssetValue)
        {
            if (intent is null) throw new ArgumentNullException(nameof(intent));
            if (local  is null) throw new ArgumentNullException(nameof(local));
            if (broker is null) throw new ArgumentNullException(nameof(broker));

            // 0. 동결 검문 (이미 동결이면 어떤 주문도 거부)
            if (State == AlgoGateState.Frozen)
                return new ExecVerdict(ExecOutcome.RejectedFrozen, 0m, "이미 비상 동결 상태");

            // 1. 슬롯 권한 검문 (그레이스 계층 — 청산 전용은 진입 불가 등)
            bool authorized = intent.IsEntry ? _slot.CanEnter(intent.Strategy)
                                             : _slot.CanExit(intent.Strategy);
            if (!authorized)
                return new ExecVerdict(ExecOutcome.RejectedUnauthorized, 0m,
                    $"슬롯 권한 없음 ({(intent.IsEntry ? "진입" : "청산")}) — perm={_slot.PermissionOf(intent.Strategy)}");

            // 2. 가드3 — 디싱크 검문 (로컬 원장 합산 vs 실 계좌). 불일치 시 비상 동결.
            var recon = ReconcileGate(local, broker);
            if (!recon.Passed)
            {
                Interlocked.Exchange(ref _algoState, (int)AlgoGateState.Frozen);
                return new ExecVerdict(ExecOutcome.RejectedDesync, 0m,
                    $"원장 디싱크 {recon.Mismatches.Count}건 — 알고 스택 비상 동결");
            }

            // 3. 가드2 — 최대 주문 크기 물리 클램프
            decimal approved = ClampOrderSize(intent.Qty, intent.Price, totalAssetValue);
            if (approved <= 0m)
                return new ExecVerdict(ExecOutcome.DryRunLogged, 0m, "클램프 결과 0주 — 발주 없음");

            // 4. 가드1 — 드라이런이면 가상 원장만, 아니면 실 브로커 발주
            if (IsDryRun || _broker is null)
            {
                lock (_ledgerLock)
                    _virtualLedger.Add(new VirtualFill(
                        intent.Symbol, intent.Side, approved, intent.Price, DateTime.UtcNow));
                return new ExecVerdict(ExecOutcome.DryRunLogged, approved,
                    $"[DRY-RUN] 가상 원장 기록 {approved}주 @ {intent.Price:N0}");
            }

            decimal limit = intent.Kind == PaperKind.Limit ? intent.Price : 0m;
            var (ok, msg) = _broker.Place(
                PaperAsset.Stock, intent.Symbol, intent.Name,
                intent.Side, intent.Kind, approved, limit, intent.Price);
            return new ExecVerdict(
                ok ? ExecOutcome.Executed : ExecOutcome.RejectedFrozen, ok ? approved : 0m, msg);
        }

        // ── 가드2 클램프 (순수 함수 — 테스트 가능) ─────────────────────────────

        /// <summary>단일 슬라이스를 자산 비율·절대 하드캡으로 물리 클램프.</summary>
        public decimal ClampOrderSize(decimal requestedQty, decimal price, decimal totalAssetValue)
        {
            decimal cap = Math.Min(requestedQty, MaxOrderAbsQty);
            if (price > 0m && totalAssetValue > 0m)
            {
                decimal byPct = Math.Floor(totalAssetValue * MaxOrderPct / price);
                cap = Math.Min(cap, byPct);
            }
            return cap < 0m ? 0m : cap;
        }

        // ── 동결 제어 ─────────────────────────────────────────────────────────

        /// <summary>외부 요인에 의한 게이트 비상 동결 (시스템 attestation 까지 전파).</summary>
        public void Freeze(string reason)
        {
            Interlocked.Exchange(ref _algoState, (int)AlgoGateState.Frozen);
            try { _attestation.ForceFreeze(string.IsNullOrWhiteSpace(reason) ? "알고 게이트 동결" : reason); }
            catch { }
        }

        /// <summary>게이트 래치 재무장 — attestation 은 별도 ManualRelease 로 해제해야 완전 해제.</summary>
        public void Rearm() => Interlocked.Exchange(ref _algoState, (int)AlgoGateState.Armed);

        // ── 내부: 디싱크 검문 (attestation.Reconcile 재사용 — 불일치 시 시스템 동결) ─
        private AttestationAuditEntry ReconcileGate(LocalState local, BrokerState broker)
        {
            var att = new OrderAttestationService
            {
                CashToleranceKrw = CashTolerance,
                QtyTolerance     = QtyTolerance,
            };
            // 검문 전용 인스턴스로 미스매치 판정만 수행(부수효과 격리)…
            var verdict = att.Reconcile(local, broker);
            // …불일치면 실제 시스템 attestation 을 비상 동결(단일 진실 원천)
            if (!verdict.Passed)
                try { _attestation.ForceFreeze($"원장 디싱크 {verdict.Mismatches.Count}건 — 알고 집행 동결"); }
                catch { }
            return verdict;
        }
    }
}
