// ============================================================================
// SlaJitterRegressionGuard — v10.1.0 Final Packaging: ★Think Beyond Gemini — SLA 측정-무결성 게이트
//   "SLA 퓨즈는 '느린 코드'가 아니라 '흔들리는 시계'에 오작동한다 — 측정 그 자체를 먼저 방어한다."
//
//  ── 왜 제미나이의 SlaJitterRegressionGuard 명세를 초월하는가 ────────────────────────────────
//  제미나이는 "QPC 지터/OS 퀀텀 스위칭 측정 오차를 정규화(Outlier Rejection)하라"고 했다. 그러나 단순 통계적
//  outlier 제거(±kσ)는 두 가지를 구분하지 못한다: (a) '진짜 꼬리 지연'(반드시 P99.99 에 계상돼야 함)과 (b) 'OS
//  디스케줄 아티팩트'(측정 오염 — 계상되면 SLA 퓨즈를 오트립시켜 실물 거래를 영구 동결). 이 가드는 통계가 아니라
//  ★'물리적 개연성'으로 판별한다: 나노-파이프라인 단일 op 이 수십µs 를 소요할 물리적 이유는 없다(그건 OS 프리엠션
//  이다). 따라서 (1)역행/영 시계(raw≤0)와 (2)적응 디스케줄 천장(max(절대, baseline×배수)) 초과 표본만 '오염'으로
//  배제하고, 그 사이는 '진짜 꼬리'로 admit 한다. baseline EWMA 는 ★admit 된 표본으로만 갱신해 오염 면역(오염이
//  baseline 을 부풀려 스스로를 통과시키는 되먹임 차단). 나아가 배제율 자체가 회귀(윈도우 지터율 임계 초과)하면 —
//  즉 '이 호스트에선 SLA 를 신뢰성 있게 측정할 수 없다' — RegressionTripped 를 원자 래치해 릴리스 인증/무장을
//  차단한다. 즉 per-표본 배제(퓨즈 오트립 방지) + 집계 회귀(신뢰불가 환경 인증 거부)의 2단 방어.
//
//  ★불변: δ=0(Admit 핫패스 힙 0·Interlocked/CAS·Int128 값타입) · 결정론(순수 정수·EWMA·벽시계/RNG 무참조 —
//    입력 표본에만 의존) · liveArmed:false(순수 판정 — IBroker·소켓·onSend 참조 0·RegressionTripped 은 tighten-only) ·
//    fail-closed(회귀 래치 비가역 → 재시작 전 인증 불가).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>SLA 측정-무결성 게이트(δ=0). Admit(raw)=클린/오염 판정(디스케줄 아티팩트 배제)·집계 지터율 회귀 시 인증 차단 래치.</summary>
    public sealed class SlaJitterRegressionGuard
    {
        private const long Scale = 1_000_000L;   // 적응 천장 배수 고정소수 스케일
        private const long RateScale = 4096L;    // ★지터율 고정소수 스케일(permille×RateScale 저장) — 정수 EWMA >>shift 양자화 바이어스 제거

        private readonly long _absDescheduleCeilingNanos;   // 절대 디스케줄 천장(이 이상 = OS 프리엠션 아티팩트로 간주)
        private readonly long _relCeilingMultRaw;           // 적응 천장 배수(baseline × mult·Scale 고정소수)
        private readonly int  _baselineShift;               // baseline EWMA α = 1/2^shift
        private readonly int  _jitterShift;                 // 지터율 EWMA α = 1/2^shift
        private readonly long _regressionThresholdPermille; // 지터율 회귀 임계(‰)
        private readonly long _warmupSamples;               // 회귀 판정 유예(초기 표본수)

        private long _baselineNanos;      // admitted 표본 EWMA(ns·오염 면역)
        private long _admitted, _jitterRejects, _totalSeen;
        private long _jitterRateScaled;   // 지터(배제)율 EWMA(permille × RateScale 고정소수·양자화 바이어스 제거)
        private int  _regressionTripped;  // 0/1 — 비가역 래치

        public SlaJitterRegressionGuard(long absDescheduleCeilingNanos = 50_000L, long relCeilingMult = 32L,
                                        int baselineShift = 6, int jitterShift = 8,
                                        long regressionThresholdPermille = 100L, long warmupSamples = 256L)
        {
            _absDescheduleCeilingNanos   = Math.Max(1L, absDescheduleCeilingNanos);
            _relCeilingMultRaw           = Math.Max(1L, relCeilingMult) * Scale;
            _baselineShift               = Math.Clamp(baselineShift, 1, 30);
            _jitterShift                 = Math.Clamp(jitterShift, 1, 30);
            _regressionThresholdPermille = Math.Clamp(regressionThresholdPermille, 1L, 1000L);
            _warmupSamples               = Math.Max(1L, warmupSamples);
        }

        // ── 읽기(δ=0·락프리) ──
        public long Admitted          => Interlocked.Read(ref _admitted);
        public long JitterRejects     => Interlocked.Read(ref _jitterRejects);
        public long TotalSeen         => Interlocked.Read(ref _totalSeen);
        public long BaselineNanos     => Interlocked.Read(ref _baselineNanos);
        public long JitterRatePermille=> Interlocked.Read(ref _jitterRateScaled) / RateScale;
        public bool RegressionTripped => Volatile.Read(ref _regressionTripped) == 1;
        /// <summary>★blockWhile — 측정 환경 신뢰불가(지터율 회귀) 시 릴리스 인증/무장 차단(tighten-only·fail-closed·비가역).</summary>
        public bool RegressionBlocking => Volatile.Read(ref _regressionTripped) == 1;

        /// <summary>
        /// ★지연 표본 1건 판정(δ=0·락프리). true=클린(SLA 히스토그램 기록 대상) · false=오염(역행 시계 or 디스케줄 천장
        /// 초과 → 배제). 물리적 개연성 판별(통계 아님): baseline×배수 적응 천장 초과 = OS 프리엠션 아티팩트로 간주.
        /// </summary>
        public bool Admit(long rawNanos)
        {
            Interlocked.Increment(ref _totalSeen);
            long baseline = Interlocked.Read(ref _baselineNanos);
            long relCeiling = baseline > 0L ? (long)((System.Int128)baseline * _relCeilingMultRaw / Scale) : 0L;
            long ceiling = Math.Max(_absDescheduleCeilingNanos, relCeiling);   // 적응 디스케줄 천장

            bool clean = rawNanos > 0L && rawNanos <= ceiling;
            if (clean)
            {
                Interlocked.Increment(ref _admitted);
                UpdateBaselineEwma(rawNanos);          // ★admit 표본으로만 EWMA(오염 면역)
            }
            else
            {
                Interlocked.Increment(ref _jitterRejects);
            }
            UpdateJitterRateAndRegression(clean ? 0L : 1000L);
            return clean;
        }

        // baseline EWMA(CAS·다중라이터 안전): base += (sample - base) >> shift. admit 표본 전용.
        private void UpdateBaselineEwma(long sample)
        {
            long cur, next;
            do
            {
                cur  = Interlocked.Read(ref _baselineNanos);
                next = cur + ((sample - cur) >> _baselineShift);
            }
            while (Interlocked.CompareExchange(ref _baselineNanos, next, cur) != cur);
        }

        // 지터율 EWMA(고정소수·CAS) + 회귀 래치(warmup 후 임계 초과 → 비가역 트립).
        //   ★양자화 교정: 이진 표본(0/1000)을 RateScale 배 고정소수화 → (sample-cur)>>shift 의 floor 손실이 값 대비 무의미(≪1%).
        //   교정 전(‰ 원단위·shift=8): reject 증분이 1000>>8=3(≈3.9 손실)로 캡되어 EWMA 가 reject율>25%에서만 상승 →
        //   임계 100‰이 실효 ~250‰로 둔감화(20% 지터가 미트립 fail-open). 고정소수화로 실 reject율을 정확 추종.
        private void UpdateJitterRateAndRegression(long sampleRatePermille)
        {
            long sample = sampleRatePermille * RateScale;   // 0 or 1000×RateScale
            long cur, next;
            do
            {
                cur  = Interlocked.Read(ref _jitterRateScaled);
                next = cur + ((sample - cur) >> _jitterShift);
            }
            while (Interlocked.CompareExchange(ref _jitterRateScaled, next, cur) != cur);

            if (Interlocked.Read(ref _totalSeen) >= _warmupSamples
                && Interlocked.Read(ref _jitterRateScaled) > _regressionThresholdPermille * RateScale)
                Interlocked.CompareExchange(ref _regressionTripped, 1, 0);   // ★1회 래치(비가역)
        }
    }
}
