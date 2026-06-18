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

namespace StockManager.Common
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
        decimal Delta);       // Local - Broker (부호 있음)

    // ── 감사 로그 엔트리 ──────────────────────────────────────────────────────

    public sealed record AttestationAuditEntry(
        DateTime                         Timestamp,
        bool                             Passed,
        IReadOnlyList<AttestationMismatch> Mismatches,
        string                           Signature,   // 수동 해제 서명; 자동 통과 시 ""
        string                           Note);

    // ── 핵심 서비스 ───────────────────────────────────────────────────────────

    public sealed class OrderAttestationService
    {
        private volatile int         _modeInt    = (int)GatingMode.Normal;
        private readonly object      _lock       = new();
        private readonly List<AttestationAuditEntry> _log = new();

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

        // ─ 내부 ──────────────────────────────────────────────────────────────

        private void TrimLog()
        {
            while (_log.Count > MaxLogEntries)
                _log.RemoveAt(_log.Count - 1);
        }
    }
}
