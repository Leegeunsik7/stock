// ============================================================================
// PromotionGateway — S19 #3: 인간 매개형 승급 유한 상태기계 + 서명 검증기
//                     S19 #4: 관제탑용 락-프리 상태 독출 추가
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
//  ── 락-프리 독출 (§S19 #4) ─────────────────────────────────────────────────
//  State·Current 는 volatile 미러로 락 없이 읽힌다(관제탑 SystemStatusAggregator 용).
//  상태 전이(check-then-act)는 여전히 _lock 으로 직렬화한다.
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

    // AsOfTicks — 챔피언이 진화한 캔들 데이터의 마켓 시각(UTC Ticks). 0이면 As-Of 게이팅 없음(현행).
    public sealed record PromotionCandidate(StrategySpec Spec, double Fitness, DateTime SubmittedUtc, long AsOfTicks = 0);

    public sealed record PromotionAudit(
        DateTime Utc, PromotionState From, PromotionState To, string Action, string Detail);

    public sealed class PromotionGateway
    {
        private readonly object                   _lock = new();
        private readonly LocalDatabase?           _db;            // 컴플라이언스 경보(옵션)
        private readonly OrderAttestationService? _attestation;   // 동결 게이트(옵션)
        private readonly Func<string, bool>       _isAuthorized;  // 서명 인가 정책
        private readonly List<PromotionAudit>     _log = new();

        // 락-프리 독출용 volatile 미러 (전이는 _lock 으로 직렬화, 읽기는 무락)
        private volatile int                 _stateInt = (int)PromotionState.Idle;
        private volatile PromotionCandidate? _current;

        /// <summary>이 적응도 이상이면 Candidate → PendingApproval 자동 승격.</summary>
        public double ChampionThreshold { get; init; } = 0.0;
        public int    MaxLogEntries     { get; init; } = 200;

        /// <summary>S23-b 규칙3 — 제안의 As-Of 신선도 한도. null이면 As-Of 게이팅 없음(현행 동작).</summary>
        public TimeSpan?       ProposalFreshnessWindow { get; init; }
        /// <summary>테스트용 주입 클록. null이면 UtcNow.</summary>
        public Func<DateTime>? Clock                   { get; init; }

        private DateTime Now() => (Clock ?? (static () => DateTime.UtcNow))();

        public PromotionGateway(
            LocalDatabase?           db                 = null,
            OrderAttestationService? attestation        = null,
            Func<string, bool>?      signatureValidator = null)
        {
            _db          = db;
            _attestation = attestation;
            // 기본 정책: ManualRelease 패턴 — 비어있지 않은 서명이면 인가.
            _isAuthorized = signatureValidator ?? (s => !string.IsNullOrWhiteSpace(s));
        }

        // ── 락-프리 독출 ──────────────────────────────────────────────────────
        public PromotionState      State   => (PromotionState)_stateInt;
        public PromotionCandidate? Current => _current;
        public IReadOnlyList<PromotionAudit> AuditLog { get { lock (_lock) return _log.ToArray(); } }

        // ── 전이 (lock 직렬화) ─────────────────────────────────────────────────

        /// <summary>변이체 제출. 적응도가 ChampionThreshold 이상이면 PendingApproval 로 자동 승격.</summary>
        public PromotionState Submit(StrategySpec spec, double fitness) => Submit(spec, fitness, 0L);

        /// <summary>변이체 제출(As-Of 박제). asOfTicks=진화한 캔들 데이터의 마켓 시각. 0이면 게이팅 없음.</summary>
        public PromotionState Submit(StrategySpec spec, double fitness, long asOfTicks)
        {
            if (spec is null) throw new ArgumentNullException(nameof(spec));
            lock (_lock)
            {
                _current = new PromotionCandidate(spec, fitness, Now(), asOfTicks);
                Audit(State, PromotionState.Candidate, "Submit", $"적응도 {fitness:F4}");
                _stateInt = (int)PromotionState.Candidate;

                if (fitness >= ChampionThreshold)
                {
                    Audit(State, PromotionState.PendingApproval, "Nominate", "챔피언 임계 통과 — 승인 대기");
                    _stateInt = (int)PromotionState.PendingApproval;
                }
                return State;
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
                    Audit(State, State, "Approve거부", "비상 동결 상태 — 승급 차단");
                    return false;
                }
                if (State != PromotionState.PendingApproval)
                {
                    Audit(State, State, "Approve거부", "승인 대기 상태가 아님");
                    return false;
                }
                // 자율 배포 영구 금지 도어락 — 올바른 인간 서명만 통과
                if (!_isAuthorized(signature ?? ""))
                {
                    Audit(State, State, "Approve거부", "변조·미인가 서명");
                    RaiseCompliance("변조·미인가 승인 서명 거부 — 자율 배포 도어락 작동");
                    return false;
                }
                // S23-b 규칙3 — As-Of 신선도 만료(유령 데이터로 만든 챔피언)는 서명이 올바라도 Rejected 로 튕김
                if (IsProposalStale())
                {
                    Audit(State, PromotionState.Rejected, "Approve거부", "As-Of 신선도 만료 — stale 챔피언 차단");
                    RaiseCompliance("stale 챔피언 승인 차단 — 제안 As-Of 신선도 만료(유령 데이터)");
                    _stateInt = (int)PromotionState.Rejected;
                    _current  = null;
                    return false;
                }
                Audit(State, PromotionState.Approved, "Approve",
                    string.IsNullOrWhiteSpace(note) ? "인간 서명 승인" : note.Trim());
                _stateInt = (int)PromotionState.Approved;
                return true;
            }
        }

        /// <summary>승인된 후보를 슬롯에 원자 핫스왑하여 가동. Approved 상태에서만 허용.</summary>
        public bool Activate(StrategySlot slot)
        {
            if (slot is null) throw new ArgumentNullException(nameof(slot));
            lock (_lock)
            {
                if (State != PromotionState.Approved || _current is null)
                {
                    Audit(State, State, "Activate거부", "승인 상태가 아님");
                    return false;
                }
                // S23-b 규칙3 — 승인→활성 갭 레이스 방어: 비가역 핫스왑 직전 As-Of 를 최종 재검문.
                // 승인 후 마켓 타임이 흘러 신선도가 만료됐으면, 서명이 올발랐어도 swap 대신 Rejected.
                if (IsProposalStale())
                {
                    Audit(State, PromotionState.Rejected, "Activate거부", "As-Of 신선도 만료(승인→활성 갭) — 핫스왑 차단");
                    RaiseCompliance("stale 챔피언 활성화 차단 — 승인-활성 갭 사이 데이터 열화");
                    _stateInt = (int)PromotionState.Rejected;
                    _current  = null;
                    return false;
                }
                slot.Swap(_current.Spec);   // ⚛ 원자 핫스왑
                Audit(State, PromotionState.Active, "Activate", $"핫스왑 적용 (gen={slot.Generation})");
                _stateInt = (int)PromotionState.Active;
                return true;
            }
        }

        /// <summary>후보 거부. Candidate/PendingApproval/Approved 에서 허용.</summary>
        public void Reject(string reason)
        {
            lock (_lock)
            {
                if (State is PromotionState.Candidate or PromotionState.PendingApproval or PromotionState.Approved)
                {
                    Audit(State, PromotionState.Rejected, "Reject", reason ?? "");
                    _stateInt = (int)PromotionState.Rejected;
                    _current  = null;
                }
            }
        }

        /// <summary>활성 전략 안락사(노후/레짐 붕괴). 실거래 축소 방향이므로 자율 허용.</summary>
        public bool Sunset(string reason)
        {
            lock (_lock)
            {
                if (State != PromotionState.Active) return false;
                Audit(State, PromotionState.Sunsetted, "Sunset", reason ?? "");
                _stateInt = (int)PromotionState.Sunsetted;
                _current  = null;
                return true;
            }
        }

        // ── 내부 ──────────────────────────────────────────────────────────────

        // S23-b 규칙3 — 원자 As-Of 검증 수식: now() - AsOfTicks > FreshnessWindow ⟹ stale.
        // _lock 안에서만 호출(상태 전이와 동일 임계구역 → TOCTOU 갭 없음). 게이팅 미설정 시 항상 false.
        private bool IsProposalStale()
        {
            var c = _current;
            if (c is null || c.AsOfTicks == 0)             return false;   // As-Of 미박제 → 게이팅 없음
            if (ProposalFreshnessWindow is not TimeSpan w) return false;   // 윈도우 미설정 → 게이팅 없음
            return Now() - new DateTime(c.AsOfTicks, DateTimeKind.Utc) > w;
        }

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
