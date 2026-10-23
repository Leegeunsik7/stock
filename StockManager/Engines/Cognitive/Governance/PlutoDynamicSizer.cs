// ============================================================================
// PlutoDynamicSizer — v12.18.67-DEV: 플루토 브레인 고도화 — ATR · 확신도 비례 가변 포지션 사이징 + 연패 쿨다운(정수 산술 · δ=0)
//
//   진입 비중(평가 자산 대비 bp) = 5% + 20% × (확신도 − 30)/70 × 변동성 배율 → [5%, 25%].
//     확신도 30 이하 = 5% · 100 = 25%. 변동성 배율 = ATR ≤ 20bp 이면 100% · 초과 시 20bp / ATR(고변동 = 비중 축소 · 하한 5%).
//   연패 가드: 청산(포지션 전량 종료) 손익 ≤ 0 = 연패 +1 · 이익 = 0 으로 리셋(엔진 승패 집계와 같은 기준).
//     연패 ≥ 2 → 진입 비중 50% 강제 축소(하한 2.5%) · 연패 2회째 청산 시각부터 15분 쿨다운(그 사이 신규 진입 0 · 쿨다운 중 추가 손실 = 재기산).
//   상태(PlutoSizerState) = 엔진 필드(정산 스레드 단일 작성자 · 시각 = 엔진 KST ticks — 자체 시계 없음 · 결정론 리플레이).
//   ★δ=0: WeightBp/OnClose/InCooldown 힙 할당 0.
// ============================================================================
using System;

namespace StockManager.Engines.Cognitive.Governance
{
    /// <summary>엔진 1개 사이저 상태(값 타입 · default = 연패 0 · 쿨다운 없음).</summary>
    public struct PlutoSizerState
    {
        public int LossStreak;
        public long CooldownUntilTicks;
        public long Closes, LossCloses;

        public readonly bool InCooldown(long nowTicks) => nowTicks < CooldownUntilTicks;
        public readonly bool StreakCut => LossStreak >= PlutoDynamicSizer.StreakThreshold;

        /// <summary>포지션 전량 청산 1건 반영(손익 ≤ 0 = 손실).</summary>
        public void OnClose(long tradePnl, long nowTicks)
        {
            Closes++;
            if (tradePnl > 0) { LossStreak = 0; return; }
            LossCloses++;
            if (LossStreak < int.MaxValue) LossStreak++;
            if (LossStreak >= PlutoDynamicSizer.StreakThreshold && nowTicks > 0)
                CooldownUntilTicks = Math.Max(CooldownUntilTicks, nowTicks + PlutoDynamicSizer.CooldownTicks);
        }
    }

    public static class PlutoDynamicSizer
    {
        public const int MinWeightBp = 500, MaxWeightBp = 2500;
        public const int MinConfidence = 30;
        public const int TargetAtrCbp = 2000;
        public const int StreakThreshold = 2, StreakCutPct = 50;
        public const long CooldownTicks = 15 * TimeSpan.TicksPerMinute;

        /// <summary>변동성 배율(%) — ATR ≤ 목표 = 100 · 초과 = 목표/ATR.</summary>
        public static int VolatilityPct(int atrCbp) => atrCbp <= TargetAtrCbp ? 100 : (int)((long)TargetAtrCbp * 100 / atrCbp);

        /// <summary>진입 비중 bp(δ=0). 연패 ≥ 2 = 50% 축소.</summary>
        public static int WeightBp(int confidence, int atrCbp, int lossStreak)
        {
            int c = Math.Clamp(confidence, MinConfidence, 100);
            long span = (long)(MaxWeightBp - MinWeightBp) * (c - MinConfidence) / (100 - MinConfidence);
            int w = (int)(MinWeightBp + span * VolatilityPct(atrCbp) / 100);
            return lossStreak >= StreakThreshold ? w * StreakCutPct / 100 : w;
        }
    }
}
