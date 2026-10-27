// ============================================================================
// PlutoFastReplayLearner — v12.18.93-DEV: 원클릭 플루토 자율 학습 가속기(로컬 과거 캔들 → 자산군당 가상 회고 100회 → 3라운드 섀도우 게이트 진화 G→G+1→G+2)
//
//   목적: 실시간 청산 회고 20표본 축적 전에도 사용자가 버튼 1번으로 자산군별 텐서 셀을 즉시 학습시키고, 진화 과정을 관찰한다.
//   절차(자산군마다 · 결정적): 로컬 일봉(IPlutoCandleSource) → 100회 가상 회고를 Rounds(3) 라운드(34 · 33 · 33)로 분할(PlutoReplayWarmStarter.Replay 위상 이동 ·
//     라운드마다 진입점 · 보유 기간 회전이 다름) → 라운드마다 임시 보정기(표본 ≥ 20 · 홀드아웃 ≥ 10)로 텐서 ApplyReplay(자산 기준선 + 판정 국면 슬롯) →
//     기존 섀도우 게이트 그대로(통과 = 세대 +1 핫스왑 · 불통과 = 현행 유지 · 세대당 ±2점/±5% 상한). 다음 라운드는 직전 라운드 결과 가중치에서 출발.
//   보고 = 자산군별 라운드(전/후 가중치 · 섀도우 홀드아웃 승률 기준 → 후보 · 판정) + 교훈 집계(학습 일지 입력) · 셀 리플레이 계수(RecordReplay · 합성).
//   ★합성 표본 = 실 에피소드 · 슬롯 보정기 링 · 평생 에피소드 · 영속 무오염(통과 가중치만 다음 체크포인트에 기록). 캔들 0 = NoData(가짜 시계열 0).
//   ★백그라운드 Task 전용 · 단일 비행(실행 중 재호출 = 진행 중 Task 공유) · 체결 핫패스 = seqlock 판독만(무간섭). liveArmed:false · 네트워크 · QuantumVault · 피어 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Cognitive.Governance;

namespace StockManager.Engines.Cognitive.Learning
{
    public enum PlutoFastReplayState : byte
    {
        Idle = 0,
        Running = 1,
        Completed = 2,
        /// <summary>로컬 캔들 없음(가짜 시계열 0).</summary>
        NoData = 3,
        /// <summary>캔들 판독 장애.</summary>
        Failed = 4,
    }

    /// <summary>라운드 1회(대상 셀 = 판정 국면 슬롯 · 판정 전 = 자산 기준선). Before/After = 대상 셀 가중치(미보정 = 사전 가중치 출발점).</summary>
    public readonly record struct PlutoFastReplayRound(int Round, int Samples, int Wins, PlutoCognitiveWeights Before, PlutoCognitiveWeights After, bool Swapped,
        PlutoShadowVerdict Verdict);

    /// <summary>교훈 집계(승 = 추세 순응 · 규율 청산 / 패 = 외부 충격 · 반대 논리 무시 · 국면 전환 · 시그널 약화) · 평균 승/패 수익률 bp.</summary>
    public readonly record struct PlutoLessonTally(int TrendWins, int DisciplinedWins, int ShockLosses, int CounterLosses, int RegimeShiftLosses, int DecayLosses,
        int AvgWinBp, int AvgLossBp);

    public sealed record PlutoFastReplayLane(PlutoAssetClass Asset, int Series, int Samples, int Wins, int TargetRegimeId, IReadOnlyList<PlutoFastReplayRound> Rounds,
        PlutoLessonTally Lessons);

    public sealed record PlutoFastReplayReport(PlutoFastReplayState State, long ElapsedMs, int RegimeId, IReadOnlyList<PlutoFastReplayLane> Lanes, long CompletedUtcTicks,
        int Runs, string? Error = null)
    {
        public static readonly PlutoFastReplayReport Idle = new(PlutoFastReplayState.Idle, 0, 0, Array.Empty<PlutoFastReplayLane>(), 0, 0);
    }

    public sealed class PlutoFastReplayLearner
    {
        public const int Simulations = PlutoReplayWarmStarter.Simulations, Rounds = 3;

        private readonly PlutoCognitiveTensorLake _tensor;
        private readonly IPlutoCandleSource _source;
        private readonly Func<int> _regime;
        private readonly Func<long> _utcNow;
        private readonly object _gate = new();
        private Task<PlutoFastReplayReport>? _inflight;
        private PlutoFastReplayReport _report = PlutoFastReplayReport.Idle;
        private int _runs;

        public PlutoFastReplayLearner(PlutoCognitiveTensorLake tensor, IPlutoCandleSource source, Func<int>? regimeId = null, Func<long>? utcNowTicks = null)
        {
            _tensor = tensor ?? throw new ArgumentNullException(nameof(tensor));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _regime = regimeId ?? (static () => 0);
            _utcNow = utcNowTicks ?? (static () => DateTime.UtcNow.Ticks);
        }

        public PlutoFastReplayReport Report => Volatile.Read(ref _report);

        public bool Running { get { lock (_gate) return _inflight is { IsCompleted: false }; } }

        /// <summary>백그라운드 실행(실행 중 = 진행 중 Task 공유).</summary>
        public Task<PlutoFastReplayReport> RunAsync()
        {
            lock (_gate)
            {
                if (_inflight is { IsCompleted: false } t) return t;
                Volatile.Write(ref _report, Report with { State = PlutoFastReplayState.Running });
                return _inflight = Task.Run(RunCore);
            }
        }

        /// <summary>동기 실행(테스트).</summary>
        public PlutoFastReplayReport Run() => RunAsync().GetAwaiter().GetResult();

        private PlutoFastReplayReport RunCore()
        {
            var sw = Stopwatch.StartNew();
            int runs = Interlocked.Increment(ref _runs);
            int regime = 0;
            IReadOnlyList<PlutoCandleSeries> all;
            try
            {
                regime = _regime();
                all = _source.LoadRecent(PlutoReplayWarmStarter.MaxSymbolsPerAsset, PlutoReplayWarmStarter.Bars);
            }
            catch (Exception ex)
            {
                return Publish(new PlutoFastReplayReport(PlutoFastReplayState.Failed, sw.ElapsedMilliseconds, regime, Array.Empty<PlutoFastReplayLane>(), _utcNow(), runs, ex.GetType().Name));
            }
            if (regime is < 1 or > PlutoCognitiveTensorLake.Regimes) regime = 0;
            int target = regime > 0 ? regime : PlutoCognitiveTensorLake.BaselineRegime;

            var lanes = new List<PlutoFastReplayLane>(PlutoCognitiveTensorLake.Assets);
            var samples = new PlutoLearningSample[Simulations];
            for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++)
            {
                var asset = (PlutoAssetClass)a;
                var series = new List<PlutoCandleSeries>();
                foreach (var s in all)
                    if (s is not null && s.Asset == asset && s.Closes is { Length: >= PlutoReplayWarmStarter.MinBars }) series.Add(s);
                if (series.Count == 0) continue;

                var rounds = new PlutoFastReplayRound[Rounds];
                int laneSamples = 0, laneWins = 0;
                long winSum = 0, lossSum = 0;
                int trendW = 0, discW = 0, shockL = 0, counterL = 0, shiftL = 0, decayL = 0, losses = 0;
                for (int r = 0; r < Rounds; r++)
                {
                    int size = Simulations / Rounds + (r < Simulations % Rounds ? 1 : 0);
                    _tensor.TryStartingPoint(asset, target, out var before);
                    var dest = samples.AsSpan(0, size);
                    int n = PlutoReplayWarmStarter.Replay(series, before.DevilStrictnessPts, dest, (long)runs * 7 + r * 3 + 1, r + runs);
                    var cal = new PlutoWeightCalibrator();
                    int wins = 0;
                    for (int i = 0; i < n; i++)
                    {
                        ref readonly var x = ref dest[i];
                        cal.ObserveSample(in x);
                        if (x.IsWin)
                        {
                            wins++;
                            winSum += x.ReturnBp;
                            if (x.Lesson == PlutoLesson.TrendFollow) trendW++; else if (x.Lesson == PlutoLesson.DisciplinedExit) discW++;
                        }
                        else
                        {
                            losses++;
                            lossSum += x.ReturnBp;
                            switch (x.Lesson)
                            {
                                case PlutoLesson.ExogenousShock: shockL++; break;
                                case PlutoLesson.RespectCounterThesis: counterL++; break;
                                case PlutoLesson.RegimeShiftExit: shiftL++; break;
                                case PlutoLesson.SignalDecay: decayL++; break;
                            }
                        }
                    }
                    bool swapped = _tensor.ApplyReplay(asset, PlutoCognitiveTensorLake.BaselineRegime, cal, out var verdict);
                    _tensor.RecordReplay(asset, PlutoCognitiveTensorLake.BaselineRegime, n, wins);
                    if (regime > 0)
                    {
                        swapped = _tensor.ApplyReplay(asset, regime, cal, out verdict);
                        _tensor.RecordReplay(asset, regime, n, wins);
                    }
                    _tensor.TryStartingPoint(asset, target, out var after);
                    rounds[r] = new PlutoFastReplayRound(r + 1, n, wins, before, after, swapped, verdict);
                    laneSamples += n;
                    laneWins += wins;
                }
                var tally = new PlutoLessonTally(trendW, discW, shockL, counterL, shiftL, decayL,
                    laneWins == 0 ? 0 : (int)(winSum / laneWins), losses == 0 ? 0 : (int)(lossSum / losses));
                lanes.Add(new PlutoFastReplayLane(asset, series.Count, laneSamples, laneWins, regime, rounds, tally));
            }
            var state = lanes.Count == 0 ? PlutoFastReplayState.NoData : PlutoFastReplayState.Completed;
            return Publish(new PlutoFastReplayReport(state, sw.ElapsedMilliseconds, regime, lanes.AsReadOnly(), _utcNow(), runs));
        }

        private PlutoFastReplayReport Publish(PlutoFastReplayReport r)
        {
            Volatile.Write(ref _report, r);
            return r;
        }
    }
}
