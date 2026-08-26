// ============================================================================
// CrossBrokerFallbackSentinel — v10.2.0: 무할당 증권사 교차-폴백 라우터 (제미나이 보강 과제)
//
//   주 증권사(Primary) WebSocket 끊김·REST Rate Limit 도달 시, δ=0 로 대기 중인 부 증권사(Secondary)로 데이터 소스를
//   원자적 스위칭(<1µs = Interlocked 인덱스 교체)한다. 우선순위 순환으로 다음 '건강한' 브로커를 선택하고, 모두 다운이면
//   활성 유지(fail-closed — 날조 스위치 안 함). 시퀀스 연속성은 CrossBrokerMonotonicTimeFence(Think-Beyond)가 봉인.
//
//   ★불변: δ=0(NoteHealth/스위치 핫패스 힙 0 — Interlocked·고정 배열) · fail-closed(건강한 대안 없으면 스위치 안 함) ·
//     liveArmed:false(순수 소스 라우팅 — 실 발주 유발 0).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Common
{
    public sealed class CrossBrokerFallbackSentinel
    {
        private readonly BrokerId[] _priority;   // 우선순위(콜드 설정·불변)
        private readonly long[] _healthy;        // BrokerId 별 1=정상
        private long _activeIdx;                 // _priority 내 현재 활성 인덱스
        private long _fallbacks;

        public CrossBrokerFallbackSentinel(BrokerId[] priority)
        {
            _priority = priority ?? Array.Empty<BrokerId>();
            _healthy = new long[8];
            for (int i = 0; i < _priority.Length; i++) _healthy[(int)_priority[i] & 7] = 1L;   // 초기 전원 건강
        }

        public BrokerId ActivePrimary => _priority.Length == 0 ? BrokerId.None : _priority[(int)Interlocked.Read(ref _activeIdx)];
        public long Fallbacks         => Interlocked.Read(ref _fallbacks);
        public bool IsHealthy(BrokerId b) => Interlocked.Read(ref _healthy[(int)b & 7]) == 1L;

        /// <summary>브로커 건강 갱신(δ=0). ★건강/불건강 무관 재조정 — 현재 활성이 불건강하면 건강한 브로커로 스위치(다운/복구
        /// 에지 모두 포착 → 세컨더리 복구 시 죽은 프라이머리 고착 방지).</summary>
        public void NoteHealth(BrokerId broker, bool healthy)
        {
            Interlocked.Exchange(ref _healthy[(int)broker & 7], healthy ? 1L : 0L);
            ReconcileActive();
        }

        /// <summary>Rate Limit 도달 = 일시 불건강(폴백 유발).</summary>
        public void NoteRateLimit(BrokerId broker) => NoteHealth(broker, false);

        /// <summary>우선순위 높은 브로커가 건강 회복 시 명시적 승격(옵션·δ=0).</summary>
        public bool PromoteToPrimary(BrokerId broker)
        {
            for (int i = 0; i < _priority.Length; i++)
            {
                if (_priority[i] == broker && Interlocked.Read(ref _healthy[(int)broker & 7]) == 1L)
                {
                    long prev = Interlocked.Exchange(ref _activeIdx, i);
                    if (prev != i) Interlocked.Increment(ref _fallbacks);
                    return true;
                }
            }
            return false;
        }

        // 활성이 불건강하면 우선순위 순환으로 다음 건강한 것으로 원자 스위치(δ=0·<1µs). ★CAS 후 재검(TOCTOU 봉쇄 —
        // 선택한 브로커가 그새 다운되면 루프가 다시 잡음)·건강한 대안 없으면 유지(fail-closed).
        private void ReconcileActive()
        {
            if (_priority.Length == 0) return;
            for (int attempt = 0; attempt <= _priority.Length; attempt++)
            {
                long ai = Interlocked.Read(ref _activeIdx);
                if (Interlocked.Read(ref _healthy[(int)_priority[(int)ai] & 7]) == 1L) return;   // 활성 건강 → 종료
                int chosen = -1;
                for (int step = 1; step <= _priority.Length; step++)
                {
                    int idx = (int)((ai + step) % _priority.Length);
                    if (Interlocked.Read(ref _healthy[(int)_priority[idx] & 7]) == 1L) { chosen = idx; break; }
                }
                if (chosen < 0) return;   // ★건강한 대안 없음 → 활성 유지(fail-closed·날조 스위치 안 함)
                if (Interlocked.CompareExchange(ref _activeIdx, chosen, ai) == ai)   // ★원자 스위치
                    Interlocked.Increment(ref _fallbacks);
                // 성공/실패 무관 재검(선택한 것이 그새 다운됐거나 경합이면 루프가 재조정)
            }
        }
    }
}
