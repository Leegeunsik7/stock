// ============================================================================
// CrossBrokerMonotonicTimeFence — v10.2.0: ★Think Beyond Gemini — 증권사 교차 단조 타임펜스
//   "증권사 간 타임스탬프 불일치 찢어짐(Cross-Broker Timestamp Torn Drift)을, '보정한다'가 아니라 '융합 타임라인이
//    절대 역행하지 못하게 봉인한다'(monotonic fail-closed)."
//
//  ── 왜 제미나이 B(Kalman 정규화 펜스)를 초월하는가 ──────────────────────────────────────
//  제미나이 B 는 A·B 증권사 서버 타임스탬프 오차를 Kalman 필터로 나노초 실시간 보정한다. 그러나 Kalman 은 부동소수
//  상태(공분산·이득)를 들고 다녀 δ=0 이 아니고, 필터 발산/튜닝 실패 시 오히려 잘못된 시각을 '자신 있게' 방출한다.
//  ★근본 불변식은 '오차를 매끈하게 보정한다'가 아니라 '융합된 틱 타임라인이 절대 역행하지 않는다'이다. 알파 엔진이
//  두려워하는 건 정밀도가 아니라 '시간이 거꾸로 흐르는' 찢어짐(broker A 틱 뒤에 더 이른 broker B 틱)이다. Fence 는
//  로컬 단조 수신시각(recvMicros)을 기반으로 통합 타임스탬프를 발행하되 ★엄격 단조(항상 직전+1 이상)를 강제하고,
//  브로커별 거래소 시각 역행을 torn 으로 계수한다. 부동소수 0·상태 최소·역행 구조적 불가 — Kalman 보정을 '단조 봉인'으로
//  대체해 찢어짐을 원천 격리(어댑터가 폴백으로 교체돼도 알파 엔진은 단조 시간만 관측).
//
//  ★불변: δ=0(Fence/상태조회 핫패스 힙 0 — 순수 정수 산술 + Interlocked·부동소수 0) · 결정론 · fail-closed(과도 역행 →
//    torn 계수·연속성 신뢰 하향) · liveArmed:false(순수 시간 정규화 — 실 발주 유발 0).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>δ=0 교차 브로커 단조 타임펜스. 융합 타임라인 엄격 단조 보장 + 거래소 시각 역행(torn) 탐지.</summary>
    public sealed class CrossBrokerMonotonicTimeFence
    {
        private readonly long _tornThresholdMicros;
        private readonly long[] _brokerLastExchange = new long[8];   // BrokerId 별 마지막 거래소 시각
        private long _lastUnified;                                    // 마지막 발행 통합 타임스탬프(단조)
        private long _corrections, _tornDrifts, _emitted;

        public CrossBrokerMonotonicTimeFence(long tornThresholdMicros = 1_000_000L)
        {
            _tornThresholdMicros = tornThresholdMicros <= 0 ? 1_000_000L : tornThresholdMicros;
        }

        public long LastUnified  => Interlocked.Read(ref _lastUnified);
        public long Corrections  => Interlocked.Read(ref _corrections);   // 역행 후보를 단조로 보정한 횟수
        public long TornDrifts   => Interlocked.Read(ref _tornDrifts);    // ★브로커 거래소 시각 과도 역행 탐지
        public long Emitted      => Interlocked.Read(ref _emitted);

        /// <summary>
        /// ★융합 단조 타임스탬프 발행(δ=0·다중 생산자 안전[CAS]). recvMicros(로컬 단조 수신) 기반, 항상 직전+1 이상(엄격
        /// 단조). 브로커별 거래소 시각이 임계 이상 역행하면 torn 계수(찢어짐 탐지). 어댑터 폴백 교체·동시 다중 어댑터 수신
        /// 콜백에서도 통합 시간은 절대 역행/중복 안 함(load-compute-store 를 CAS 루프로 원자화).
        /// </summary>
        public long Fence(BrokerId broker, long exchangeMicros, long recvMicros)
        {
            int bi = (int)broker & 7;
            long prevEx = Interlocked.Exchange(ref _brokerLastExchange[bi], exchangeMicros);   // ★원자 교체(#4 lost-update 봉쇄)
            if (prevEx != 0L && exchangeMicros < prevEx - _tornThresholdMicros)
                Interlocked.Increment(ref _tornDrifts);   // ★브로커 내 거래소 시각 과도 역행 = 찢어짐

            // ★다중 생산자 단조 봉인: load→compute→store 를 CAS 루프로 원자화(경합 시 재시도 — 역행/중복 emit 구조적 불가).
            long unified;
            while (true)
            {
                long last = Interlocked.Read(ref _lastUnified);
                unified = recvMicros > last ? recvMicros : last + 1L;
                if (Interlocked.CompareExchange(ref _lastUnified, unified, last) == last) break;
            }
            if (unified != recvMicros) Interlocked.Increment(ref _corrections);   // 역행 후보 → 단조 보정
            Interlocked.Increment(ref _emitted);
            return unified;
        }
    }
}
