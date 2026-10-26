// ============================================================================
// PlutoLearningPortAdapter — v12.18.77-DEV: 학습 스웜 상태(PlutoLearningStatus) → IPlutoLearningPort DTO 투영(콜드 경로 · 판독 전용)
//   Step 0회(상태 null) = 스웜 가동 여부 + 미결선 · 판정 전. 국면 매칭 없음 = EpisodeName null.
//   [v12.18.82-DEV] 12대 역사적 국면 판정(Regime) → 국면명 · 유사도 ×10 · 해설 · 전략/편향 문구(판정 전 = null/−1).
//   [v12.18.83-DEV] 거시 피드 문구(호스트 결선 상태 · 예 'FRED API 키 미설정 · 관측 대기') 통과(null = 미표기).
//   [v12.18.85-DEV] 섀도우 검증 요약(직전 판정 · 현행 가중치 섀도우 승률/MDD) → PlutoShadowView(인지 브리핑).
//   [v12.18.84-DEV] 체크포인트 관리자(결선 시) → 평생 누적 학습 에피소드 · 영속화 상태 코드(미결선 = −1 · Off).
//   [v12.18.86-DEV] 인지 텐서(결선 시) → 활성 국면 × 3대 자산군 해석 가중치(Resolve · 미보정 = 전역 가중치 그대로) + 웜스타트 보고 → 자산군별 WarmReady/NoData(PlutoTensorView).
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
        private readonly PlutoCognitiveTensorLake? _tensor;
        private readonly PlutoReplayWarmStarter? _warmStart;

        public PlutoLearningPortAdapter(PlutoContinuousLearningSwarm swarm, string? macroFeedText = null, PlutoCheckpointManager? checkpoints = null,
            PlutoCognitiveTensorLake? tensor = null, PlutoReplayWarmStarter? warmStart = null)
        {
            _swarm = swarm ?? throw new ArgumentNullException(nameof(swarm));
            _macroFeedText = macroFeedText;
            _checkpoints = checkpoints;
            _tensor = tensor;
            _warmStart = warmStart;
        }

        public bool IsWired => true;

        public PlutoLearningView Current
        {
            get
            {
                var v = Map(_swarm.Status, _swarm.Running, _macroFeedText);
                var c = _checkpoints;
                if (c is not null) v = v with { LifetimeEpisodes = c.LifetimeEpisodes, PersistState = PersistCode(c.State) };
                return _tensor is null ? v : v with { Tensor = MapTensor(_tensor, _warmStart?.Report, _swarm.Status?.Weights ?? default) };
            }
        }

        /// <summary>[v12.18.86-DEV] 텐서 활성 슬롯(자산군별 Resolve · 미보정 = 전역 가중치) + 웜스타트 보고(null = 웜스타트 비활성 → 미결선) → 브리핑 DTO.</summary>
        public static PlutoTensorView MapTensor(PlutoCognitiveTensorLake tensor, PlutoWarmStartReport? warm, in PlutoCognitiveWeights global)
        {
            ArgumentNullException.ThrowIfNull(tensor);
            int regime = tensor.ActiveRegime;
            var lanes = new PlutoTensorLaneView[PlutoCognitiveTensorLake.Assets];
            for (int a = 0; a < lanes.Length; a++)
            {
                var asset = (PlutoAssetClass)a;
                var w = global;
                var src = tensor.Resolve(asset, regime, ref w);
                int code = WarmCode(warm, asset, out var lane);
                lanes[a] = new PlutoTensorLaneView(a, (int)src, w.Generation, w.MomentumTiltPct, w.DevilStrictnessPts, code,
                    code == PlutoWarmCode.Ready ? lane.Series : 0, code == PlutoWarmCode.Ready ? lane.Simulations : 0);
            }
            return new PlutoTensorView(regime, regime > 0 ? PlutoRegimeVectorLake.Get(regime)?.Name : null, tensor.CalibratedSlots, lanes);
        }

        /// <summary>자산군 웜스타트 코드: 레인 결속 = Ready · 진행 전/중 = Pending · 장애 = Failed · 예산 초과로 미도달 = TimedOut · 그 외(캔들 없음) = NoData.</summary>
        public static int WarmCode(PlutoWarmStartReport? r, PlutoAssetClass asset, out PlutoWarmStartLane lane)
        {
            lane = default;
            if (r is null) return PlutoWarmCode.Off;
            switch (r.State)
            {
                case PlutoWarmStartState.NotStarted or PlutoWarmStartState.Running: return PlutoWarmCode.Pending;
                case PlutoWarmStartState.Failed: return PlutoWarmCode.Failed;
            }
            foreach (var l in r.Lanes)
                if (l.Asset == asset && l.Series > 0 && l.Simulations > 0) { lane = l; return PlutoWarmCode.Ready; }
            return r.State == PlutoWarmStartState.TimedOut ? PlutoWarmCode.TimedOut : PlutoWarmCode.NoData;
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
                cycle is null ? null : PlutoRegimeText.Effect(in r), macroFeedText, Shadow: MapShadow(s.LastVerdict, s.Weights));
        }

        /// <summary>[v12.18.85-DEV] 직전 섀도우 판정 + 현행 가중치 섀도우 성과 → 브리핑 DTO(판정 코드 = PlutoShadowReason 값 동일 · 미상 = 판정 전).</summary>
        public static PlutoShadowView MapShadow(in PlutoShadowVerdict v, in PlutoCognitiveWeights w)
            => new(PlutoShadowVerdictCode.Normalize((int)v.Reason), v.BaselineWinRateBp, v.ShadowWinRateBp, v.BaselineMddBp, v.ShadowMddBp, v.Holdout,
                w.ShadowWinRateBp, w.ShadowMddBp, w.ShadowSamples);
    }
}
