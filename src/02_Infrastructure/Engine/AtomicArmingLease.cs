// ============================================================================
// AtomicArmingLease — v7.4.0 시간 제한 무장 임대(원자적 자동 안전 하향)
//
//  liveArmed:true 무장은 '무기한'이어선 안 된다. 운영자가 자리를 비우거나, 주기적 Pre-Flight 재인증
//  텔레메트리가 끊기면(엔진 정지·네트워크 단절·관제 프로세스 사망) 무장은 스스로 만료되어야 한다.
//  이 임대는 무장 성공 시 가동되는 미세 타이머(기본 300초)로, 재인증 Renew() 가 주기적으로 인입되는
//  한에서만 유효(Active)하다. Renew 가 leaseSeconds 안에 다시 오지 않으면 Expired 로 전이하고, 무장 술어
//  blockWhile 이 이를 읽어 무장을 liveArmed:false 로 원자 하향 조율한다(재인증 재개 시 Renew 로 복원 — 가역).
//
//  ★불변: δ=0(Renew/Expired/IsActive 힙 0·Volatile long·주입 QPC) · 결정론(주입 시계) · liveArmed:false
//    (순수 관측 게이트 — IBroker·소켓·onSend 참조 0. blockWhile tighten-only — 무장을 '거둘' 뿐 부여 불가) ·
//    fail-closed(만료·미개시 판정은 항상 '더 안전한 방향'). ★한국색상은 UI 계층.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>시간 제한 무장 임대(δ=0·원자). Start/Renew(재인증) → 유효, leaseSeconds 무갱신 → Expired 자동 하향.</summary>
    public sealed class AtomicArmingLease
    {
        private readonly long        _leaseTicks;     // 임대 폭(QPC ticks)
        private readonly Func<long>  _nowTicks;       // 주입 시계(기본 Stopwatch.GetTimestamp)
        private readonly long        _freq;           // QPC 주파수(ticks/s) — 잔여초 환산

        private long _deadlineTicks;                  // 0 = 미개시(dormant). ≠0 = 이 시각까지 유효.
        private long _renewCount;
        private long _lastRenewTicks;

        public AtomicArmingLease(int leaseSeconds = 300, Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            int s = leaseSeconds < 1 ? 1 : leaseSeconds;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _freq     = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _leaseTicks = (long)s * _freq;
        }

        public long LeaseSeconds => _leaseTicks / _freq;
        public long RenewCount   => Interlocked.Read(ref _renewCount);

        /// <summary>임대 개시/갱신(무장 성공 시 최초 호출 + 주기적 Pre-Flight 재인증마다 호출·δ=0·원자). 데드라인을 now+lease 로 전진.</summary>
        public void Renew()
        {
            long now = _nowTicks();
            Volatile.Write(ref _deadlineTicks, now + _leaseTicks);   // 원자 데드라인 전진
            Volatile.Write(ref _lastRenewTicks, now);
            Interlocked.Increment(ref _renewCount);
        }

        /// <summary>임대 즉시 해제(무장 해제·정지 시). Expired=false·IsActive=false(dormant)로 복귀 — blockWhile 무영향.</summary>
        public void Disarm() { Volatile.Write(ref _deadlineTicks, 0L); }

        /// <summary>임대 유효 여부(δ=0). 개시됨 ∧ now ≤ deadline.</summary>
        public bool IsActive
        {
            get { long d = Volatile.Read(ref _deadlineTicks); return d != 0L && _nowTicks() <= d; }
        }

        /// <summary>★만료 여부(δ=0). 개시됨(≠0) ∧ now > deadline. dormant(미개시)는 false — 임대는 개시 후에만 문다(fail-safe).
        /// blockWhile 이 읽어 만료 시 무장을 tighten-only 로 하향(재Renew 시 자동 복원).</summary>
        public bool Expired
        {
            get { long d = Volatile.Read(ref _deadlineTicks); return d != 0L && _nowTicks() > d; }
        }

        /// <summary>임대 잔여 시간(초·δ=0·UI 텔레메트리). dormant/만료 시 0.</summary>
        public long RemainingSeconds
        {
            get
            {
                long d = Volatile.Read(ref _deadlineTicks);
                if (d == 0L) return 0L;
                long rem = d - _nowTicks();
                return rem <= 0L ? 0L : rem / _freq;
            }
        }

        /// <summary>임대 잔여 ‰(0~1000·UI 게이지·δ=0). dormant 0.</summary>
        public long RemainingPermille
        {
            get
            {
                long d = Volatile.Read(ref _deadlineTicks);
                if (d == 0L || _leaseTicks <= 0L) return 0L;
                long rem = d - _nowTicks();
                if (rem <= 0L) return 0L;
                if (rem >= _leaseTicks) return 1000L;
                return rem * 1000L / _leaseTicks;
            }
        }
    }
}
