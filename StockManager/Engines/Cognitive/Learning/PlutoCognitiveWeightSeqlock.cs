// ============================================================================
// PlutoCognitiveWeightSeqlock — v12.18.77-DEV: 플루토 지속 학습 2-트랙 — 학습 가중치 스냅샷(Track B 작성 · Track A 체결 핫패스 판독 δ=0)
//
//   PlutoCognitiveWeights(값 타입 · default = 중립 = 학습 전 기존 동작):
//     MomentumTiltPct(−50~+50): 거버너 국면 재가중 점수에 모멘텀 기여(bp × 엔진 모멘텀 가중/100)의 tilt% 를 추가 가산(+ = 모멘텀 신뢰 · − = 모멘텀 불신).
//     DevilStrictnessPts(−10~+20): 악마의 변호인 원확신도에서 차감(+ = 더 보수적 → 확신도 < 30 거부권이 더 자주 · − = 완화).
//       ※ 감점 6종 상수 · 감점 합 = 반대 논리 감점 합(PlutoGovernanceTag 무결성)은 불변 — 학습은 원확신도 입력만 이동한다.
//     Generation(핫스왑 세대 · 0 = 학습 전) · 섀도우 검증 계수(승률 · MDD · 표본) = UI/감사용.
//   PlutoCognitiveWeightSeqlock: 버전 CAS 잠금(짝수 v → 홀수 v+1) → 값 기록 → v+2 release 발행(다중 작성자 안전 · 실제 작성자 = 학습 스웜 1개).
//     TryRead(체결 정산 스레드): 버전 브래킷 · 홀수 = 재시도 · 상한 MaxReadSpins 초과 = false(호출측 = 중립 가중치 · fail-safe).
//     TrySwap(후보, 섀도우 판정): 섀도우 게이트 불통과 · 범위 밖 후보 = 거부(기존 가중치 유지 · 거부 계수). 통과 = 세대 +1 원자 발행.
//   ★δ=0: TryRead/TrySwap 힙 할당 0(고정 필드 · 값 타입 복사). ★liveArmed:false — 가상 원장 진입 판정 입력만(주문 수단 0).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>학습 가중치 스냅샷(값 타입 · default = 중립).</summary>
    public readonly struct PlutoCognitiveWeights
    {
        public const int MaxMomentumTiltPct = 50, MinStrictnessPts = -10, MaxStrictnessPts = 20;

        public readonly long Generation;
        public readonly int MomentumTiltPct, DevilStrictnessPts;
        /// <summary>섀도우 검증 결과(후보 기준 · 승률 bp · 누적 수익률 곡선 MDD bp · 표본 수).</summary>
        public readonly int ShadowWinRateBp, ShadowMddBp, ShadowSamples;

        public PlutoCognitiveWeights(long generation, int momentumTiltPct, int devilStrictnessPts, int shadowWinRateBp = 0, int shadowMddBp = 0, int shadowSamples = 0)
        {
            Generation = generation; MomentumTiltPct = momentumTiltPct; DevilStrictnessPts = devilStrictnessPts;
            ShadowWinRateBp = shadowWinRateBp; ShadowMddBp = shadowMddBp; ShadowSamples = shadowSamples;
        }

        public bool IsNeutral => MomentumTiltPct == 0 && DevilStrictnessPts == 0;

        public bool IsWellFormed =>
            Generation >= 0 && MomentumTiltPct is >= -MaxMomentumTiltPct and <= MaxMomentumTiltPct
            && DevilStrictnessPts is >= MinStrictnessPts and <= MaxStrictnessPts
            && ShadowWinRateBp is >= 0 and <= 10000 && ShadowMddBp >= 0 && ShadowSamples >= 0;

        public PlutoCognitiveWeights WithGeneration(long generation) =>
            new(generation, MomentumTiltPct, DevilStrictnessPts, ShadowWinRateBp, ShadowMddBp, ShadowSamples);

        /// <summary>모멘텀 tilt 가산(δ=0): adjusted + momentumBp × momentumWeight × tilt / 10000 · ±10000 포화. 중립 = 그대로.</summary>
        public int ApplyMomentumTilt(int adjustedScore, int momentumBp, int momentumWeight)
        {
            if (MomentumTiltPct == 0) return adjustedScore;
            long delta = (long)momentumBp * momentumWeight * MomentumTiltPct / 10000;
            return (int)Math.Clamp(adjustedScore + delta, -10000, 10000);
        }

        /// <summary>악마의 변호인 원확신도 보정(δ=0): raw − strictness · 0~100 포화.</summary>
        public int ApplyStrictness(int rawConfidence) => Math.Clamp(rawConfidence - DevilStrictnessPts, 0, 100);
    }

    public sealed class PlutoCognitiveWeightSeqlock
    {
        public const int MaxReadSpins = 1024;

        private long _ver;
        private PlutoCognitiveWeights _w;
        private long _swaps, _rejectedSwaps, _contended;

        public long Swaps => Interlocked.Read(ref _swaps);
        public long RejectedSwaps => Interlocked.Read(ref _rejectedSwaps);
        public long ContendedReads => Interlocked.Read(ref _contended);
        public long Version => Volatile.Read(ref _ver);

        /// <summary>최신 가중치 판독(δ=0). 경합 상한 초과 = false(weights = default = 중립).</summary>
        public bool TryRead(out PlutoCognitiveWeights weights)
        {
            for (int k = 0; k < MaxReadSpins; k++)
            {
                long v1 = Volatile.Read(ref _ver);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                weights = _w;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v1) return true;
            }
            weights = default;
            Interlocked.Increment(ref _contended);
            return false;
        }

        /// <summary>섀도우 판정 통과 후보만 원자 핫스왑(δ=0). 통과 = 세대 +1 발행 · 불통과/무결성 위반 = 거부(기존 유지).</summary>
        public bool TrySwap(in PlutoCognitiveWeights candidate, bool shadowPassed)
        {
            if (!shadowPassed || !candidate.IsWellFormed)
            {
                Interlocked.Increment(ref _rejectedSwaps);
                return false;
            }
            var spin = new SpinWait();
            long v;
            while (true)
            {
                v = Volatile.Read(ref _ver);
                if ((v & 1) == 0 && Interlocked.CompareExchange(ref _ver, v + 1, v) == v) break;
                spin.SpinOnce(-1);
            }
            _w = candidate.WithGeneration(_w.Generation + 1);
            Volatile.Write(ref _ver, v + 2);
            Interlocked.Increment(ref _swaps);
            return true;
        }
    }
}
