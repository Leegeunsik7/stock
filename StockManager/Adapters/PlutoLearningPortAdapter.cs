// ============================================================================
// PlutoLearningPortAdapter — v12.18.77-DEV: 학습 스웜 상태(PlutoLearningStatus) → IPlutoLearningPort DTO 투영(콜드 경로 · 판독 전용)
//   Step 0회(상태 null) = 스웜 가동 여부 + 미결선 · 판정 전. 국면 매칭 없음 = EpisodeName null.
//   [v12.18.82-DEV] 12대 역사적 국면 판정(Regime) → 국면명 · 유사도 ×10 · 해설 · 전략/편향 문구(판정 전 = null/−1).
//   [v12.18.83-DEV] 거시 피드 문구(호스트 결선 상태 · 예 'FRED API 키 미설정 · 관측 대기') 통과(null = 미표기).
//   [v12.18.84-DEV] 체크포인트 관리자(결선 시) → 평생 누적 학습 에피소드 · 영속화 상태 코드(미결선 = −1 · Off).
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
        private readonly PlutoCheckpointManager? _checkpoints;

        public PlutoLearningPortAdapter(PlutoContinuousLearningSwarm swarm, string? macroFeedText = null, PlutoCheckpointManager? checkpoints = null)
        {
            _swarm = swarm ?? throw new ArgumentNullException(nameof(swarm));
            _macroFeedText = macroFeedText;
            _checkpoints = checkpoints;
        }

        public bool IsWired => true;

        public PlutoLearningView Current
        {
            get
            {
                var v = Map(_swarm.Status, _swarm.Running, _macroFeedText);
                var c = _checkpoints;
                return c is null ? v : v with { LifetimeEpisodes = c.LifetimeEpisodes, PersistState = PersistCode(c.State) };
            }
        }

        public static int PersistCode(PlutoPersistState s) => s switch
        {
            PlutoPersistState.Fresh or PlutoPersistState.Restored => PlutoLearningPersist.Active,
            PlutoPersistState.RolledBack => PlutoLearningPersist.RolledBack,
            PlutoPersistState.CorruptFallback => PlutoLearningPersist.NeutralFallback,
            PlutoPersistState.StoreFailed => PlutoLearningPersist.StoreFailed,
            _ => PlutoLearningPersist.Off,
        };

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
