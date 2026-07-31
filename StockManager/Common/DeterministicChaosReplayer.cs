// ============================================================================
// DeterministicChaosReplayer — v9.1.0 결정론적 카오스 재현기 (Gemini 보강 과제)
//
//  SplitMix64 시드 하나로, 시간축에 따른 결함 주입 순서·강도·조합을 100% 결정론적으로 재현한다. 동일 시드 →
//  동일 결함 시퀀스(무작위처럼 보이나 완전 재현). 실전 재난에서 관측된 카오스 궤적을 시드로 박제해, 회귀 테스트와
//  포렌식 재연(bug 재현)에 쓴다. 시각(벽시계) 무관 — 시퀀스는 오직 시드에서 유도된다.
//
//  ★불변: δ=0(Next 순수 시드 유도·값타입 반환·힙 0) · 결정론(System.Random·Math.Random·벽시계 무참조 —
//    검증된 무할당 DeterministicPrng[SplitMix64] 재사용) · liveArmed:false(스케줄 산출만·발주 유발 0).
//    ★단일 라이터(재현 드라이버 스레드) — _prng 구조체 상태는 단일 소비자 계약.
// ============================================================================
using System;

namespace StockManager.Common
{
    /// <summary>결정론 결함 이벤트(블리터블 값타입). 직전 이벤트로부터의 상대 지연 + 결함 비트 + 강도.</summary>
    public readonly struct ChaosScheduledFault
    {
        public readonly long DelayTicks;         // 직전 이벤트로부터의 상대 지연(시각 무관·틱 델타)
        public readonly long FaultMask;          // 주입 결함 비트(≥1 비트 보장)
        public readonly long IntensityPermille;  // 강도 0~1000
        public readonly bool Catastrophic;       // 파멸(ForceFreeze) 강도 여부
        public ChaosScheduledFault(long delayTicks, long faultMask, long intensityPermille, bool catastrophic)
        { DelayTicks = delayTicks; FaultMask = faultMask; IntensityPermille = intensityPermille; Catastrophic = catastrophic; }
    }

    /// <summary>결정론적 카오스 재현기(δ=0·SplitMix64). Reset(seed)→Next() 반복이 동일 시드에서 동일 시퀀스 100% 재현.</summary>
    public sealed class DeterministicChaosReplayer
    {
        private readonly long _maxDelayTicks;
        private readonly long _faultBits;        // 허용 결함 비트 풀(F_ALL 하위)
        private readonly long _catastrophicPermille;
        private DeterministicPrng _prng;         // 검증된 무할당 SplitMix64(값타입·단일 라이터)
        private ulong _seed;
        private long _generated;

        public DeterministicChaosReplayer(ulong seed = 0xC4A05EEDUL, long maxDelayTicks = 1_000_000L,
                                          long faultBits = ChaosEngineSanitizer.F_ALL, long catastrophicPermille = 950)
        {
            _seed = seed;
            _prng = new DeterministicPrng(seed);
            _maxDelayTicks = Math.Max(1L, maxDelayTicks);
            _faultBits = (faultBits & ChaosEngineSanitizer.F_ALL) == 0L ? ChaosEngineSanitizer.F_ALL : (faultBits & ChaosEngineSanitizer.F_ALL);
            _catastrophicPermille = Math.Clamp(catastrophicPermille, 1, 1000);
        }

        public ulong Seed      => _seed;
        public long  Generated => _generated;

        /// <summary>시드 재설정(δ=0) — 동일 시드는 이후 Next() 시퀀스를 100% 재현. 포렌식 재연·회귀 박제.</summary>
        public void Reset(ulong seed)
        {
            _seed = seed;
            _prng = new DeterministicPrng(seed);
            _generated = 0;
        }

        /// <summary>다음 결함 이벤트 생성(δ=0·순수 시드 유도·시각 무관). 동일 시드 = 동일 시퀀스.</summary>
        public ChaosScheduledFault Next()
        {
            _generated++;
            long delay = 1L + _prng.NextLong(_maxDelayTicks);

            // 결함 마스크: 허용 풀의 각 비트를 독립 확률로 포함(≥1 비트 보장 — 빈 결함 이벤트 방지).
            long mask = 0L;
            for (int b = 0; b < 4; b++)
            {
                long bit = 1L << b;
                if ((_faultBits & bit) != 0L && _prng.NextUnit() < 0.5) mask |= bit;
            }
            if (mask == 0L)
                mask = _faultBits & (-_faultBits);   // 허용 풀 최하위 비트(항상 존재)

            long intensity = _prng.NextLong(1001L);  // 0~1000
            bool cata = intensity >= _catastrophicPermille;
            return new ChaosScheduledFault(delay, mask, intensity, cata);
        }
    }
}
