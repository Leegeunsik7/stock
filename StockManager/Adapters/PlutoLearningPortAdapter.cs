// ============================================================================
// PlutoLearningPortAdapter — v12.18.77-DEV: 학습 스웜 상태(PlutoLearningStatus) → IPlutoLearningPort DTO 투영(콜드 경로 · 판독 전용)
//   Step 0회(상태 null) = 스웜 가동 여부 + 미결선 · 판정 전. 국면 매칭 없음 = EpisodeName null.
//   [v12.18.82-DEV] 12대 역사적 국면 판정(Regime) → 국면명 · 유사도 ×10 · 해설 · 전략/편향 문구(판정 전 = null/−1).
//   [v12.18.83-DEV] 거시 피드 문구(호스트 결선 상태 · 예 'FRED API 키 미설정 · 관측 대기') 통과(null = 미표기).
//   [v12.18.85-DEV] 섀도우 검증 요약(직전 판정 · 현행 가중치 섀도우 승률/MDD) → PlutoShadowView(인지 브리핑).
//   [v12.18.84-DEV] 체크포인트 관리자(결선 시) → 평생 누적 학습 에피소드 · 영속화 상태 코드(미결선 = −1 · Off).
//   [v12.18.86-DEV] 인지 텐서(결선 시) → 활성 국면 × 3대 자산군 해석 가중치(Resolve · 미보정 = 전역 가중치 그대로) + 웜스타트 보고 → 자산군별 WarmReady/NoData(PlutoTensorView).
//   [v12.18.92-DEV] 진화 심사 기준점(스웜 SamplesAtLastCalibration) → EvolveAnchor · ReadTensorCells = 39셀(국면 1~12 → 기준선) 가중치 · 성숙도 · 셀 실측 승패(콜드 · 인스펙터 열림 시만).
//   [v12.18.93-DEV] 셀 사전 가중치 · 리플레이 합성 표본 · 직전 Δ 투영 · 자율 학습 가속기(PlutoFastReplayLearner 결선 시) → CanFastReplay · LastReplay · RunFastReplayAsync(백그라운드).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
        private readonly PlutoFastReplayLearner? _fast;
        private readonly Func<string?>? _macroFeedTextNow;

        /// <param name="macroFeedTextNow">[v12.18.93-DEV] 거시 피드 문구 동적 공급(로컬 캔들 추정 근거 갱신 · null 반환 = 정적 문구 유지).</param>
        public PlutoLearningPortAdapter(PlutoContinuousLearningSwarm swarm, string? macroFeedText = null, PlutoCheckpointManager? checkpoints = null,
            PlutoCognitiveTensorLake? tensor = null, PlutoReplayWarmStarter? warmStart = null, PlutoFastReplayLearner? fastReplay = null, Func<string?>? macroFeedTextNow = null)
        {
            _swarm = swarm ?? throw new ArgumentNullException(nameof(swarm));
            _macroFeedText = macroFeedText;
            _checkpoints = checkpoints;
            _tensor = tensor;
            _warmStart = warmStart;
            _fast = fastReplay;
            _macroFeedTextNow = macroFeedTextNow;
        }

        public bool IsWired => true;

        public PlutoLearningView Current
        {
            get
            {
                var v = Map(_swarm.Status, _swarm.Running, _macroFeedTextNow?.Invoke() ?? _macroFeedText);
                v = v with { EvolveAnchor = _swarm.SamplesAtLastCalibration };
                var c = _checkpoints;
                if (c is not null) v = v with { LifetimeEpisodes = c.LifetimeEpisodes, PersistState = PersistCode(c.State) };
                return _tensor is null ? v : v with { Tensor = MapTensor(_tensor, _warmStart?.Report, _swarm.Status?.Weights ?? default) };
            }
        }

        public IReadOnlyList<PlutoTensorCellView> ReadTensorCells() => _tensor is null ? Array.Empty<PlutoTensorCellView>() : MapCells(_tensor);

        public bool CanFastReplay => _fast is not null;

        public PlutoReplayRunView LastReplay => _fast is null ? PlutoReplayRunView.Unwired : MapReplay(_fast.Report);

        public async Task<PlutoReplayRunView> RunFastReplayAsync()
        {
            if (_fast is null) return PlutoReplayRunView.Unwired;
            var r = await _fast.RunAsync().ConfigureAwait(false);
            return MapReplay(r);
        }

        /// <summary>[v12.18.93-DEV] 가속 리플레이 보고 → UI DTO(상태 코드 · 자산군 라운드 · 교훈 집계 · 대상 셀 국면명).</summary>
        public static PlutoReplayRunView MapReplay(PlutoFastReplayReport r)
        {
            ArgumentNullException.ThrowIfNull(r);
            int state = r.State switch
            {
                PlutoFastReplayState.Running => PlutoReplayStateCode.Running,
                PlutoFastReplayState.Completed => PlutoReplayStateCode.Completed,
                PlutoFastReplayState.NoData => PlutoReplayStateCode.NoData,
                PlutoFastReplayState.Failed => PlutoReplayStateCode.Failed,
                _ => PlutoReplayStateCode.Idle,
            };
            string? regimeName = r.RegimeId > 0 ? PlutoRegimeVectorLake.Get(r.RegimeId)?.Name : null;
            var lanes = new PlutoReplayLaneView[r.Lanes.Count];
            for (int i = 0; i < lanes.Length; i++)
            {
                var l = r.Lanes[i];
                var rounds = new PlutoReplayRoundView[l.Rounds.Count];
                for (int k = 0; k < rounds.Length; k++)
                {
                    var x = l.Rounds[k];
                    rounds[k] = new PlutoReplayRoundView(x.Round, x.Samples, x.Wins, x.Before.Generation, x.After.Generation, x.Before.MomentumTiltPct, x.After.MomentumTiltPct,
                        x.Before.DevilStrictnessPts, x.After.DevilStrictnessPts, PlutoShadowVerdictCode.Normalize((int)x.Verdict.Reason), x.Verdict.BaselineWinRateBp, x.Verdict.ShadowWinRateBp);
                }
                var t = l.Lessons;
                lanes[i] = new PlutoReplayLaneView((int)l.Asset, l.Series, l.Samples, l.Wins,
                    l.TargetRegimeId > 0 ? PlutoRegimeVectorLake.Get(l.TargetRegimeId)?.Name ?? "국면 " + l.TargetRegimeId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "자산 기준선",
                    rounds, new PlutoLessonTallyView(t.TrendWins, t.DisciplinedWins, t.ShockLosses, t.CounterLosses, t.RegimeShiftLosses, t.DecayLosses, t.AvgWinBp, t.AvgLossBp));
            }
            DateTime? at = r.CompletedUtcTicks > 0 ? new DateTime(r.CompletedUtcTicks, DateTimeKind.Utc) : null;
            return new PlutoReplayRunView(state, r.ElapsedMs, regimeName, lanes, at, r.Runs, r.Error);
        }

        /// <summary>[v12.18.92-DEV] 39셀 → 인스펙터 DTO(국면 행 1~12 먼저 · 기준선 행 마지막 · 행 내 KRX · US · Upbit 순).</summary>
        public static PlutoTensorCellView[] MapCells(PlutoCognitiveTensorLake tensor)
        {
            ArgumentNullException.ThrowIfNull(tensor);
            var cells = new PlutoTensorCellView[PlutoCognitiveTensorLake.Cells];
            int k = 0;
            for (int row = 1; row <= PlutoCognitiveTensorLake.Regimes; row++)
                for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++) cells[k++] = MapCell(tensor, row * PlutoCognitiveTensorLake.Assets + a);
            for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++) cells[k++] = MapCell(tensor, a);
            return cells;
        }

        private static PlutoTensorCellView MapCell(PlutoCognitiveTensorLake tensor, int cell)
        {
            var c = tensor.Cell(cell);
            var e = tensor.CellEdge(cell);
            bool baseline = c.RegimeId == PlutoCognitiveTensorLake.BaselineRegime;
            string name = baseline ? "자산 기준선" : PlutoRegimeVectorLake.Get(c.RegimeId)?.Name ?? "국면 " + c.RegimeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new PlutoTensorCellView(baseline ? PlutoTensorCellView.BaselineRegime : c.RegimeId, name, (int)c.Asset, c.Weights.Generation,
                c.Weights.MomentumTiltPct, c.Weights.DevilStrictnessPts, (int)c.Maturity, c.LifetimeEpisodes, e.Wins, e.Losses, e.WinRateBp,
                c.Prior.MomentumTiltPct, c.Prior.DevilStrictnessPts, c.HasPrior, c.ReplaySamples, c.ReplayWins, c.LastDeltaTilt, c.LastDeltaStrict);
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
