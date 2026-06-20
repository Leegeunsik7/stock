// ============================================================================
// PromotionGateway — S19 #3: 인간 매개형 승급 유한 상태기계 + 서명 검증기
//
//  그린하우스가 배양한 최상위 변이체의 생애주기를 인간 인증 하에 제어한다:
//    Idle → Candidate → PendingApproval → Approved → Active → Sunsetted
//                                    └→ Rejected
//
//  ── 자율 배포 영구 금지 도어락 (§I2 · §3.5) ────────────────────────────────
//  기계가 판정한 최상위 적응도 개체라도, 올바른 인간 서명이 들어오기 전까지
//  절대 Approved 로 전이하지 못한다. S12 ManualRelease(signature) 패턴 재사용.
//  변조·미인가 서명 주입 시 전이를 거부하고 컴플라이언스 경보를 자율 발행한다.
//
//  ── Fail-Closed 계승 (§I4) ─────────────────────────────────────────────────
//  OrderAttestationService 가 EmergencyFreeze 면 승급(Approve) 자체가 차단된다.
//
//  스레드 안전: 인간 페이스의 상태 전이이므로 lock 사용(핫패스 아님).
//  Sunset(안락사)·Reject 는 실거래 축소 방향이므로 자율 허용. 승급만 인간 게이트.
// ============================================================================
using System;
using System.Collections.Generic;

namespace StockManager.Common
{
    public enum PromotionState
    {
        Idle,             // 후보 없음 (초기/사후)
        Candidate,        // 변이체 제출됨 (적응도 평가 전/미달)
        PendingApproval,  // 챔피언 임계 통과 — 인간 승인 대기
        Approved,         // 인간 서명 승인 완료 — 활성화 대기
        Active,           // 핫스왑되어 슬롯에 가동 중
        Rejected,         // 거부됨
        Sunsetted         // 안락사(노후/레짐 붕괴)
    }

    public sealed record PromotionCandidate(StrategySpec Spec, double Fitness, DateTime SubmittedUtc);

    public sealed record PromotionAudit(
        DateTime Utc, PromotionState From, PromotionState To, string Action, string Detail);

    public sealed class PromotionGateway
    {
        private readonly object                   _lock = new();
        private readonly LocalDatabase?           _db;            // 컴플라이언스 경보(옵션)
        private readonly OrderAttestationService? _attestation;   // 동결 게이트(옵션)
        private readonly Func<string, bool>       _isAuthorized;  // 서명 인가 정책
        private readonly List<PromotionAudit>     _log = new();

        private PromotionState      _state = PromotionState.Idle;
        private PromotionCandidate? _current;

        /// <summary>이 적응도 이상이면 Candidate → PendingApproval 자동 승격.</summary>
        public double ChampionThreshold { get; init; } = 0.0;
        public int    MaxLogEntries     { get; init; } = 200;

        public PromotionGateway(
            LocalDatabase?           db                 = null,
            OrderAttestationService? attestation        = null,
            Func<string, bool>?      signatureValidator = null)
        {
            _db          = db;
            _attestation = attestation;
            // 기본 정책: ManualRelease 패턴 — 비어있지 않은 서명이면 인가.
            // 운영자는 더 엄격한 검증기(고정 토큰 일치 등)를 주입할 수 있다.
            _isAuthorized = signatureValidator ?? (s => !string.IsNullOrWhiteSpace(s));
        }

        // ── 상태 독출 ─────────────────────────────────────────────────────────
        public PromotionState      State   { get { lock (_lock) return _state; } }
        public PromotionCandidate? Current { get { lock (_lock) return _current; } }
        public IReadOnlyList<PromotionAudit> AuditLog { get { lock (_lock) return _log.ToArray(); } }

        // ── 전이 ──────────────────────────────────────────────────────────────

        /// <summary>변이체 제출. 적응도가 ChampionThreshold 이상이면 PendingApproval 로 자동 승격.</summary>
        public PromotionState Submit(StrategySpec spec, double fitness)
        {
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            lock (_lock)
            {
                _current = new PromotionCandidate(spec, fitness, DateTime.UtcNow);
                Audit(_state, PromotionState.Candidate, "Submit", $"적응도 {fitness:F4}");
                _state = PromotionState.Candidate;

                if (fitness >= ChampionThreshold)
                {
                    Audit(_state, PromotionState.PendingApproval, "Nominate", "챔피언 임계 통과 — 승인 대기");
                    _state = PromotionState.PendingApproval;
                }
                return _state;
            }
        }

        /// <summary>인간 서명 승인. 비상 동결·잘못된 상태·변조 서명은 거부(+경보).</summary>
        public bool Approve(string signature, string note = "")
        {
            lock (_lock)
            {
                // §I4 — 비상 동결 중 승급 금지 (fail-closed)
                if (_attestation is not null && _attestation.Mode == GatingMode.EmergencyFreeze)
                {
                    Audit(_state, _state, "Approve거부", "비상 동결 상태 — 승급 차단");
                    return false;
                }
                if (_state != PromotionState.PendingApproval)
                {
                    Audit(_state, _state, "Approve거부", "승인 대기 상태가 아님");
                    return false;
                }
                // 자율 배포 영구 금지 도어락 — 올바른 인간 서명만 통과
                if (!_isAuthorized(signature ?? ""))
                {
                    Audit(_state, _state, "Approve거부", "변조·미인가 서명");
                    RaiseCompliance("변조·미인가 승인 서명 거부 — 자율 배포 도어락 작동");
                    return false;
                }
                Audit(_state, PromotionState.Approved, "Approve",
                    string.IsNullOrWhiteSpace(note) ? "인간 서명 승인" : note.Trim());
                _state = PromotionState.Approved;
                return true;
            }
        }

        /// <summary>승인된 후보를 슬롯에 원자 핫스왑하여 가동. Approved 상태에서만 허용.</summary>
        public bool Activate(StrategySlot slot)
        {
            if (slot is null) throw new ArgumentNullException(nameof(slot));
            lock (_lock)
            {
                if (_state != PromotionState.Approved || _current is null)
                {
                    Audit(_state, _state, "Activate거부", "승인 상태가 아님");
                    return false;
                }
                slot.Swap(_current.Spec);   // ⚛ 원자 핫스왑
                Audit(_state, PromotionState.Active, "Activate", $"핫스왑 적용 (gen={slot.Generation})");
                _state = PromotionState.Active;
                return true;
            }
        }

        /// <summary>후보 거부. Candidate/PendingApproval/Approved 에서 허용.</summary>
        public void Reject(string reason)
        {
            lock (_lock)
            {
                if (_state is PromotionState.Candidate or PromotionState.PendingApproval or PromotionState.Approved)
                {
                    Audit(_state, PromotionState.Rejected, "Reject", reason ?? "");
                    _state   = PromotionState.Rejected;
                    _current = null;
                }
            }
        }

        /// <summary>활성 전략 안락사(노후/레짐 붕괴). 실거래 축소 방향이므로 자율 허용.</summary>
        public bool Sunset(string reason)
        {
            lock (_lock)
            {
                if (_state != PromotionState.Active) return false;
                Audit(_state, PromotionState.Sunsetted, "Sunset", reason ?? "");
                _state   = PromotionState.Sunsetted;
                _current = null;
                return true;
            }
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        private void Audit(PromotionState from, PromotionState to, string action, string detail)
        {
            _log.Insert(0, new PromotionAudit(DateTime.UtcNow, from, to, action, detail));
            while (_log.Count > MaxLogEntries) _log.RemoveAt(_log.Count - 1);
        }

        private void RaiseCompliance(string message)
        {
            try { _db?.AddFraudAlert("SYSTEM", "PROMO-GATE", message, "HIGH"); } catch { }
        }
    }
}
