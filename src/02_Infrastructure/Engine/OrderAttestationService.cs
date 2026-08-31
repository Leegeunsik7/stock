// ============================================================================
// OrderAttestationService — S12 #2 거래 증적 감사 추적기
//  · GatingMode 상태 머신 (Normal / EmergencyFreeze)
//  · Reconcile(LocalState, BrokerState) — O(N) 교차 검증
//  · 단 1원·1주 미스매치라도 즉시 EmergencyFreeze 전환
//  · ManualRelease — 사용자 서명 확인 후만 해제
//  · volatile int으로 원자적 상태 읽기 보장 (레이스 컨디션 없음)
//  · 외부 의존성 0 · 순수 결정론 모듈
// ============================================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace MS2E.Infrastructure
{
    // ── 상태 열거 ─────────────────────────────────────────────────────────────

    public enum GatingMode { Normal, EmergencyFreeze }

    // ── 스냅샷 계약 ───────────────────────────────────────────────────────────

    public sealed record LocalState(
        decimal CashKrw,
        IReadOnlyDictionary<string, decimal> Holdings);

    public sealed record BrokerState(
        decimal CashKrw,
        IReadOnlyDictionary<string, decimal> Holdings);

    // ── 미스매치 상세 ─────────────────────────────────────────────────────────

    public sealed record AttestationMismatch(
        string  Field,        // "예수금(KRW)" 또는 종목코드
        decimal LocalValue,
        decimal BrokerValue,
        decimal Delta)        // Local - Broker (부호 있음)
        : MS2E.UI.Portable.Ports.Trading.IMismatchView;   // Phase 94-N: 공변(Field/LocalValue/BrokerValue/Delta 암시)

    // ── 감사 로그 엔트리 ──────────────────────────────────────────────────────

    public sealed record AttestationAuditEntry(
        DateTime                         Timestamp,
        bool                             Passed,
        IReadOnlyList<AttestationMismatch> Mismatches,
        string                           Signature,   // 수동 해제 서명; 자동 통과 시 ""
        string                           Note)
        : MS2E.UI.Portable.Ports.Trading.IAttestationEntryView   // Phase 94-N: 공변(Timestamp/Passed/Signature/Note 암시)
    {
        public int MismatchCount => Mismatches.Count;
        // IReadOnlyList<AttestationMismatch> → IReadOnlyList<IMismatchView> : 공변(명시적 — 동명 반환형 충돌 회피).
        IReadOnlyList<MS2E.UI.Portable.Ports.Trading.IMismatchView>
            MS2E.UI.Portable.Ports.Trading.IAttestationEntryView.Mismatches => Mismatches;
    }

    // ── 핵심 서비스 ───────────────────────────────────────────────────────────

    public sealed class OrderAttestationService
    {
        private volatile int         _modeInt    = (int)GatingMode.Normal;
        private readonly object      _lock       = new();
        private readonly List<AttestationAuditEntry> _log = new();
        // v4.95.0 — 동결 옵저버(블랙박스 봉인·덤프 트리거). 락 밖에서 예외 삼킴 격발(핫패스 무블록·동결 완료 방해 금지).
        //   ★계약: 옵저버는 무할당 링 enqueue + 신호만 — 동기 디스크 I/O 금지(ForceFreeze 는 MMF IPC·50ms 드레인 등 핫패스에서 호출됨).
        private Action<AttestationAuditEntry>? _onFreeze;

        // ─ 허용 한도 (0 = 한 푼도 불일치 불허) ─────────────────────────────
        public decimal CashToleranceKrw  { get; init; } = 0m;
        public decimal QtyTolerance      { get; init; } = 0m;
        public int     MaxLogEntries     { get; init; } = 200;

        // ─ 상태 읽기 (volatile — 락 불필요) ─────────────────────────────────
        public GatingMode Mode => (GatingMode)_modeInt;

        public IReadOnlyList<AttestationAuditEntry> AuditLog
        {
            get { lock (_lock) return _log.ToArray(); }
        }

        // ── 게이팅 인터셉터 (ShadowBroker·CartridgeGenome 진입부에서 호출) ──

        public void AssertNormal(string context)
        {
            if ((GatingMode)_modeInt == GatingMode.EmergencyFreeze)
                throw new InvalidOperationException(
                    $"[GATING FREEZE] {context} 차단 — " +
                    "비상 동결 상태입니다. /broker/gating 에서 수동 정합 서명 후 해제하세요.");
        }

        // ── Reconcile: O(N) 교차 검증 ─────────────────────────────────────

        public AttestationAuditEntry Reconcile(LocalState local, BrokerState broker)
        {
            if (local  == null) throw new ArgumentNullException(nameof(local));
            if (broker == null) throw new ArgumentNullException(nameof(broker));

            var mismatches = new List<AttestationMismatch>();

            // 1. 예수금 검증
            decimal cashDelta = Math.Abs(local.CashKrw - broker.CashKrw);
            if (cashDelta > CashToleranceKrw)
                mismatches.Add(new AttestationMismatch(
                    "예수금(KRW)",
                    local.CashKrw,
                    broker.CashKrw,
                    local.CashKrw - broker.CashKrw));

            // 2. 종목 수량 검증 — O(N) 합집합 순회
            var allSymbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in local.Holdings.Keys)  allSymbols.Add(k);
            foreach (var k in broker.Holdings.Keys) allSymbols.Add(k);

            foreach (var sym in allSymbols)
            {
                decimal localQty  = local.Holdings.TryGetValue(sym,  out var lq) ? lq : 0m;
                decimal brokerQty = broker.Holdings.TryGetValue(sym, out var bq) ? bq : 0m;
                decimal delta     = Math.Abs(localQty - brokerQty);
                if (delta > QtyTolerance)
                    mismatches.Add(new AttestationMismatch(
                        sym, localQty, brokerQty, localQty - brokerQty));
            }

            bool passed = mismatches.Count == 0;
            var entry = new AttestationAuditEntry(
                DateTime.UtcNow,
                passed,
                mismatches.AsReadOnly(),
                "",
                passed ? "자동 정합 통과" : $"미스매치 {mismatches.Count}건 감지 — 비상 동결");

            lock (_lock)
            {
                _log.Insert(0, entry);
                TrimLog();
                if (!passed)
                    _modeInt = (int)GatingMode.EmergencyFreeze;  // volatile write
            }
            // ★C#6: 정합 미스매치 동결도 표준 동결 경로 — 블랙박스 봉인·덤프가 유실되지 않도록 옵저버 격발(락 밖·예외 삼킴).
            if (!passed) FireFreezeObservers(entry);
            return entry;
        }

        // ── ManualRelease: 서명 확인 후 동결 해제 ────────────────────────────

        public AttestationAuditEntry ManualRelease(string signature, string note = "")
        {
            if (string.IsNullOrWhiteSpace(signature))
                throw new ArgumentException(
                    "서명이 없으면 동결을 해제할 수 없습니다.", nameof(signature));

            var entry = new AttestationAuditEntry(
                DateTime.UtcNow,
                true,
                Array.Empty<AttestationMismatch>(),
                signature.Trim(),
                string.IsNullOrWhiteSpace(note)
                    ? "수동 정합 서명으로 동결 해제"
                    : note.Trim());

            lock (_lock)
            {
                _log.Insert(0, entry);
                TrimLog();
                _modeInt = (int)GatingMode.Normal;  // volatile write
            }
            return entry;
        }

        // ── ForceFreeze: 외부 이벤트(파이프 끊김 등)에 의한 강제 동결 ─────────

        /// <summary>파이프 타임아웃 등 외부 이상 이벤트 시 즉시 EmergencyFreeze.</summary>
        public AttestationAuditEntry ForceFreeze(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason))
                reason = "외부 요인에 의한 강제 동결";

            var entry = new AttestationAuditEntry(
                DateTime.UtcNow,
                false,
                Array.Empty<AttestationMismatch>(),
                "",
                reason);

            lock (_lock)
            {
                _log.Insert(0, entry);
                TrimLog();
                _modeInt = (int)GatingMode.EmergencyFreeze;
            }
            // ★동결 옵저버 격발 — 락 밖(AuditLog 독출 직렬화 회피)·예외 삼킴(동결 완료 방해 금지·fail-closed).
            FireFreezeObservers(entry);
            return entry;
        }

        /// <summary>v4.95.0 — 동결 옵저버 등록(멀티캐스트·원자 결합). 블랙박스 덤퍼가 Seal+신호 격발에 사용.
        /// ★옵저버는 무할당·무블록·무I/O 여야 함(ForceFreeze 는 핫패스에서 호출됨).</summary>
        public void RegisterFreezeObserver(Action<AttestationAuditEntry> observer)
        {
            if (observer is null) throw new ArgumentNullException(nameof(observer));
            Action<AttestationAuditEntry>? cur, combined;
            do { cur = _onFreeze; combined = cur + observer; }
            while (Interlocked.CompareExchange(ref _onFreeze, combined, cur) != cur);
        }

        /// <summary>동결 옵저버 격발 — ★U#1: 각 옵저버를 개별 try/catch 로 순회해 한 옵저버 예외가 뒤 옵저버(덤퍼 Seal+신호)를
        /// 차단하지 못하게 한다. 락 밖 호출·무블록 계약(옵저버는 무I/O). ForceFreeze/Reconcile 동결 공용.</summary>
        private void FireFreezeObservers(AttestationAuditEntry entry)
        {
            var obs = _onFreeze;
            if (obs is null) return;
            foreach (var d in obs.GetInvocationList())
            {
                try { ((Action<AttestationAuditEntry>)d)(entry); }
                catch { /* 한 옵저버 예외가 동결·타 옵저버를 막지 못함 */ }
            }
        }

        // ── ProcessFill: 실체결 vs 예상 주문 비교 감사 ───────────────────────────

        /// <summary>
        /// FillPayload와 PendingOrder를 비교해 허용 오차 초과 시 ForceFreeze.
        /// priceTolerance: 체결가 허용 오차 (기본 0.5 %). 시장가(ExpectedPrice=0)는 가격 검사 생략.
        /// </summary>
        public FillAuditResult ProcessFill(
            FillPayload  fill,
            PendingOrder expected,
            double       priceTolerance = 0.005)
        {
            if (expected == null) throw new ArgumentNullException(nameof(expected));

            // 1. StockValidator 게이트
            var v = StockValidator.ValidateFill(fill);
            if (!v.IsValid)
            {
                var reason = $"StockValidator 거부: {v.Summary()}";
                ForceFreeze(reason);
                return new FillAuditResult(false, reason, 0.0, 0L);
            }

            // 2. 매매방향 일치
            int expectedSide = expected.Side == PaperSide.Buy ? 1 : -1;
            if (fill.Side != expectedSide)
            {
                var reason = $"매매방향 불일치: 예상 {(expectedSide == 1 ? "매수" : "매도")}, " +
                             $"수신 {(fill.Side == 1 ? "매수" : "매도")}";
                ForceFreeze(reason);
                return new FillAuditResult(false, reason, 0.0, fill.FillQty - expected.Quantity);
            }

            // 3. 체결가 오차 (시장가 제외)
            double priceDeltaPct = 0.0;
            if (expected.ExpectedPrice > 0.0)
            {
                priceDeltaPct = Math.Abs(fill.FillPrice - expected.ExpectedPrice)
                                / expected.ExpectedPrice;
                if (priceDeltaPct > priceTolerance)
                {
                    var reason = $"체결가 허용 오차 초과: {priceDeltaPct:P2} > {priceTolerance:P2}";
                    ForceFreeze(reason);
                    return new FillAuditResult(false, reason, priceDeltaPct,
                                               fill.FillQty - expected.Quantity);
                }
            }

            // 4. 과체결
            long qtyDelta = fill.FillQty - expected.Quantity;
            if (qtyDelta > 0)
            {
                var reason = $"과체결 감지: 예상 {expected.Quantity}주, 체결 {fill.FillQty}주";
                ForceFreeze(reason);
                return new FillAuditResult(false, reason, priceDeltaPct, qtyDelta);
            }

            // 5. 통과 — 감사 로그 기록
            string note = qtyDelta == 0
                ? $"전량 체결 통과 ({fill.FillQty}주 @ {fill.FillPrice:N0})"
                : $"부분 체결 ({fill.FillQty}/{expected.Quantity}주 @ {fill.FillPrice:N0})";

            var entry = new AttestationAuditEntry(
                DateTime.UtcNow, true, Array.Empty<AttestationMismatch>(), "", note);

            lock (_lock) { _log.Insert(0, entry); TrimLog(); }

            return new FillAuditResult(true, note, priceDeltaPct, qtyDelta);
        }

        // ─ 내부 ──────────────────────────────────────────────────────────────

        private void TrimLog()
        {
            while (_log.Count > MaxLogEntries)
                _log.RemoveAt(_log.Count - 1);
        }
    }
}
