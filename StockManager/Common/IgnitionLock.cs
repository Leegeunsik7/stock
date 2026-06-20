// ============================================================================
// IgnitionLock — v5.0.0-a: 단계적·가역적 점화 제어반 두뇌 (FSM 순수 코어)
//
//  실 자본이 와이어를 타기 위한 '방아쇠'의 상태기계. ⚠️ 이 코어는 실 발주를 하지 않는다 —
//  밸브의 실 위임(-c)은 철저히 격리. 본 클래스는 '점화가 허용되는가'만 판정한다.
//
//  ── 비대칭 안전 ────────────────────────────────────────────────────────────
//  점화(Arm)는 인간 서명 + Locked 상태 + 미동결 일 때만. 후퇴(Lock)는 컨텍스트 무관 즉시.
//  부팅 기본은 항상 Locked. AllowPhysical/TryConsume 은 다중 AND 게이트 — 하나라도 거짓이면 차단.
//
//    Locked ──Arm(서명)──> ProbeReady ──1주 소비──> (자동) Locked
//    Locked ──Arm(서명)──> TimeBoxOpen ──창 만료/위험──> (자동) Locked
//    Locked ──Arm(서명)──> MasterIgnited
//    어느 단계든 ──Lock(3σ·디싱크·ProxyLost·캡·인간취소)──> Locked  (원자적 다운그레이드)
//
//  외부 의존성 0 · 결정론(주입식 시계로 시간상자 테스트 가능) · 사이드 이펙트는 내부 상태뿐.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace StockManager.Common
{
    public enum IgnitionStage { Locked, ProbeReady, TimeBoxOpen, MasterIgnited }

    public sealed record IgnitionAudit(
        DateTime Utc, IgnitionStage From, IgnitionStage To, string Action, string Detail);

    public sealed class IgnitionLock
    {
        private readonly object                   _lock = new();
        private readonly Func<string, bool>       _authorized;
        private readonly OrderAttestationService? _attestation;
        private readonly Func<DateTime>           _now;
        private readonly List<IgnitionAudit>      _log = new();

        private volatile int _stageInt = (int)IgnitionStage.Locked;   // 기본 Locked
        private int          _probeRemaining;   // S1 잔여 발주 (Volatile)
        private long         _windowEndTicks;   // S2 창 만료 UTC ticks (Volatile.Read/Write)

        public int MaxLogEntries { get; init; } = 200;

        public IgnitionLock(
            Func<string, bool>?      signatureValidator = null,
            OrderAttestationService? attestation        = null,
            Func<DateTime>?          clock              = null)
        {
            _authorized  = signatureValidator ?? (s => !string.IsNullOrWhiteSpace(s));
            _attestation = attestation;
            _now         = clock ?? (() => DateTime.UtcNow);
        }

        // ── 락-프리 독출 ──────────────────────────────────────────────────────
        public IgnitionStage Stage          => (IgnitionStage)_stageInt;
        public int           ProbeRemaining => Volatile.Read(ref _probeRemaining);
        public bool          SystemFrozen   => _attestation?.Mode == GatingMode.EmergencyFreeze;
        public IReadOnlyList<IgnitionAudit> AuditLog { get { lock (_lock) return _log.ToArray(); } }

        // ── 인간 매개 점화 (Arm) ──────────────────────────────────────────────

        /// <summary>Locked 에서만, 유효 서명 + 미동결 시에만 단계 진입. 그 외 거부(fail-closed).</summary>
        public bool Arm(IgnitionStage target, string signature, TimeSpan? window = null)
        {
            lock (_lock)
            {
                if (target == IgnitionStage.Locked)
                { Audit(Stage, Stage, "Arm거부", "Arm 으로 Locked 진입 불가(Lock 사용)"); return false; }
                if (Stage != IgnitionStage.Locked)
                { Audit(Stage, Stage, "Arm거부", "Locked 상태에서만 점화 가능"); return false; }
                if (SystemFrozen)
                { Audit(Stage, Stage, "Arm거부", "비상 동결 — 점화 불가(fail-closed)"); return false; }
                if (!_authorized(signature ?? ""))
                { Audit(Stage, Stage, "Arm거부", "미인가·변조 서명"); return false; }

                if (target == IgnitionStage.ProbeReady)
                    Volatile.Write(ref _probeRemaining, 1);
                if (target == IgnitionStage.TimeBoxOpen)
                    Volatile.Write(ref _windowEndTicks, _now().Add(window ?? TimeSpan.FromMinutes(30)).Ticks);

                Audit(Stage, target, "Arm", "인간 서명 점화");
                _stageInt = (int)target;
                return true;
            }
        }

        /// <summary>원자적 비상 다운그레이드 — 컨텍스트 무관 즉시 Locked(자동 후퇴).</summary>
        public void Lock(string reason)
        {
            lock (_lock)
            {
                if (Stage == IgnitionStage.Locked) return;
                Audit(Stage, IgnitionStage.Locked, "Lock", reason ?? "");
                _stageInt = (int)IgnitionStage.Locked;
                Volatile.Write(ref _probeRemaining, 0);
                Volatile.Write(ref _windowEndTicks, 0L);
            }
        }

        // ── 점화 허용 판정 ────────────────────────────────────────────────────

        /// <summary>비소비 조회(UI용). 다중 AND 게이트 — 동결·단계·프로브·창을 모두 확인.</summary>
        public bool AllowPhysical()
        {
            if (SystemFrozen) return false;
            return Stage switch
            {
                IgnitionStage.ProbeReady    => Volatile.Read(ref _probeRemaining) > 0,
                IgnitionStage.TimeBoxOpen   => !WindowExpired(),
                IgnitionStage.MasterIgnited => true,
                _                            => false,   // Locked
            };
        }

        /// <summary>
        /// 실 발주 직전 원자적 소비. 허용 시 true(프로브 1주 소비→자동 잠금 / 창 검사 / 마스터 통과),
        /// 불허 시 false. ⚠️ 이 코어는 실 발주를 하지 않는다 — 호출자(밸브, -c)가 위임을 결속한다.
        /// </summary>
        public bool TryConsumePhysical()
        {
            lock (_lock)
            {
                if (SystemFrozen) return false;
                switch (Stage)
                {
                    case IgnitionStage.ProbeReady:
                        if (Volatile.Read(ref _probeRemaining) <= 0) return false;
                        Volatile.Write(ref _probeRemaining, 0);
                        Audit(Stage, IgnitionStage.Locked, "ProbeConsumed", "1주 프로브 소비 — 자동 재잠금");
                        _stageInt = (int)IgnitionStage.Locked;   // 1체결 즉시 자동 잠금
                        return true;

                    case IgnitionStage.TimeBoxOpen:
                        if (WindowExpired())
                        {
                            Audit(Stage, IgnitionStage.Locked, "WindowExpired", "시간상자 만료 — 자동 잠금");
                            _stageInt = (int)IgnitionStage.Locked;
                            Volatile.Write(ref _windowEndTicks, 0L);
                            return false;
                        }
                        return true;

                    case IgnitionStage.MasterIgnited:
                        return true;

                    default:
                        return false;   // Locked
                }
            }
        }

        // ── 위험 신호 자동 후퇴 배선 ───────────────────────────────────────────

        /// <summary>슬리피지 3σ 경보 발생 시 자동으로 Locked 다운그레이드하도록 구독.</summary>
        public void Attach(SlippageSentinel sentinel)
        {
            if (sentinel is null) throw new ArgumentNullException(nameof(sentinel));
            sentinel.AlertTriggered += OnRiskAlert;
        }

        public void Detach(SlippageSentinel sentinel)
        {
            if (sentinel is null) return;
            sentinel.AlertTriggered -= OnRiskAlert;
        }

        private void OnRiskAlert(SlippageAlert a)
            => Lock($"슬리피지 {a.ZScore:F2}σ 경보 — 자동 후퇴");

        // ── 내부 ──────────────────────────────────────────────────────────────

        private bool WindowExpired()
        {
            long end = Volatile.Read(ref _windowEndTicks);
            return end == 0L || _now().Ticks >= end;
        }

        private void Audit(IgnitionStage from, IgnitionStage to, string action, string detail)
        {
            _log.Insert(0, new IgnitionAudit(_now(), from, to, action, detail));
            while (_log.Count > MaxLogEntries) _log.RemoveAt(_log.Count - 1);
        }
    }
}
