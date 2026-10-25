// ============================================================================
// PlutoLearningPortAdapter — v12.18.77-DEV: 학습 스웜 상태(PlutoLearningStatus) → IPlutoLearningPort DTO 투영(콜드 경로 · 판독 전용)
//   Step 0회(상태 null) = 스웜 가동 여부 + 미결선 · 판정 전. 국면 매칭 없음 = EpisodeName null.
//   [v12.18.82-DEV] 12대 역사적 국면 판정(Regime) → 국면명 · 유사도 ×10 · 해설 · 전략/편향 문구(판정 전 = null/−1).
//   [v12.18.83-DEV] 거시 피드 문구(호스트 결선 상태 · 예 'FRED API 키 미설정 · 관측 대기') 통과(null = 미표기).
// ============================================================================
using System;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Cognitive.Learning;

namespace StockManager.Adapters
{
    public sealed class PlutoLearningPortAdapter : IPlutoLearningPort
    {
        private readonly PlutoContinuousLearningSwarm _swarm;
        private readonly string? _macroFeedText;

        public PlutoLearningPortAdapter(PlutoContinuousLearningSwarm swarm, string? macroFeedText = null)
        {
            _swarm = swarm ?? throw new ArgumentNullException(nameof(swarm));
            _macroFeedText = macroFeedText;
        }

        public bool IsWired => true;

        public PlutoLearningView Current => Map(_swarm.Status, _swarm.Running, _macroFeedText);

        public static PlutoLearningView Map(PlutoLearningStatus? s, bool running, string? macroFeedText = null)
        {
            if (s is null) return new PlutoLearningView(running, PlutoLearningHealth.NoData, null, -1, false, 0, 0, 0, 0, 0, 0, MacroFeedText: macroFeedText);
            var e = s.Match.Found ? s.Episode : null;
            int health = s.Health switch
            {
                PlutoIngestHealth.Live => PlutoLearningHealth.Live,
                PlutoIngestHealth.Stale => PlutoLearningHealth.Stale,
                _ => PlutoLearningHealth.NoData,
            };
            var r = s.Regime;
            var cycle = r.Found ? PlutoRegimeVectorLake.Get(r.CycleId) : null;
            return new PlutoLearningView(running, health, e?.Name, e is null ? -1 : s.Match.SimilarityPct, e?.IsSeed ?? false,
                s.Weights.Generation, s.Weights.MomentumTiltPct, s.Weights.DevilStrictnessPts, s.Samples, s.Swaps, s.RejectedSwaps,
                cycle?.Name, cycle is null ? -1 : r.SimilarityX10, cycle is null ? null : $"{cycle.Period} · {cycle.Note}",
                cycle is null ? null : PlutoRegimeText.Effect(in r), macroFeedText);
        }
    }
}
