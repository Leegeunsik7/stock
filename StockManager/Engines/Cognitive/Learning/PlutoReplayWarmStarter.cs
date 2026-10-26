// ============================================================================
// PlutoReplayWarmStarter — v12.18.86-DEV: 플루토 기동 즉시 웜스타트 회고 학습기(최근 로컬 캔들 → 가상 리플레이 100회 → 36슬롯 텐서 섀도우 게이트 핫스왑)
//
//   목적: 재기동 직후 실시간 청산 회고 20표본(MinSamples) 축적 전 콜드 스타트 구간에도 자산군별 최신 시장 감각을 반영.
//   입력 = IPlutoCandleSource(호스트 어댑터 = portfolio.db Candles 테이블 판독 전용) 자산군별 최근 일봉 종가 시계열(오래된 순 · 양수 유한값만 · MinBars 미만 = 제외).
//   리플레이(Replay · 결정적 · 자산군당 Simulations(100)회):
//     후보 진입점 = 모든 시계열의 (봉 e ≥ Lookback(10) ∧ e ≤ 끝−2) → 균등 간격 100개 선택(후보 &lt; 100 = 순환 · 보유 기간 3/5/10봉 회전).
//     진입 모멘텀 = (종가[e] / 종가[e−10] − 1) bp → 진입 확신도 = 50 + 모멘텀/40(0~100 포화) · 롱 전용(가상 원장과 같은 방향).
//     보유 중 진입가 대비 −StopBp(700bp) 도달 = 그 봉 청산(전봉 대비 −ShockBarBp(500bp) 급락 = 외부 충격) · 아니면 보유 만기 청산.
//     수익률 bp = 청산/진입 − 1 − 왕복 비용 RoundTripCostBp(25bp). 교훈 = 승: 추세 순응(모멘텀 &gt; 0) · 규율 청산 / 패: 외부 충격 · 반대 논리 무시(역추세 진입) ·
//     국면 전환(청산 시 10봉 모멘텀 음전) · 시그널 약화. → PlutoLearningSample(진입 당시 엄격도 = 대상 셀 현재값).
//   결속: 자산군마다 임시 보정기(실 슬롯 보정기 · 평생 에피소드 무오염)에 100표본 → 텐서 ApplyReplay(자산 기준선 + 판정된 국면 슬롯) = 기존 섀도우 게이트 그대로
//     (불통과 = 기존 가중치 유지 · 세대당 ±2점/±5% 상한). 합성 표본은 영속 0 — 통과 가중치만 다음 체크포인트에 골든으로 기록.
//   예산: 총 Budget(1초) — 초과 시 남은 자산군 결속 포기(TimedOut · 이미 결속된 자산군 유지). 캔들 0 = NoData(가짜 시계열 생성 0 · 정직 표기).
//   ★백그라운드 Task 전용(UI 스레드 · 체결 핫패스 무간섭 — 체결 스레드는 seqlock 판독만). ★liveArmed:false · 네트워크 · QuantumVault · 피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Cognitive.Governance;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>로컬 캔들 종가 시계열 1건(오래된 순 · 양수 유한값).</summary>
    public sealed record PlutoCandleSeries(string Symbol, PlutoAssetClass Asset, double[] Closes);

    /// <summary>웜스타트 캔들 공급 경계(호스트 어댑터 · 콜드 · 장애 = 예외).</summary>
    public interface IPlutoCandleSource
    {
        IReadOnlyList<PlutoCandleSeries> LoadRecent(int maxSymbolsPerAsset, int bars);
    }

    public enum PlutoWarmStartState : byte
    {
        NotStarted = 0,
        Running = 1,
        /// <summary>리플레이 완주(자산군별 섀도우 판정 결과는 레인 참조).</summary>
        Completed = 2,
        /// <summary>로컬 캔들 없음 — 실시간 학습만(가짜 시계열 0).</summary>
        NoData = 3,
        /// <summary>예산(1초) 초과 — 남은 자산군 결속 포기.</summary>
        TimedOut = 4,
        /// <summary>캔들 판독 장애.</summary>
        Failed = 5,
    }

    /// <summary>자산군 1레인 결과.</summary>
    public readonly record struct PlutoWarmStartLane(PlutoAssetClass Asset, int Series, int Simulations, int Wins, int SwappedCells, PlutoShadowVerdict Verdict);

    /// <summary>웜스타트 보고(불변).</summary>
    public sealed record PlutoWarmStartReport(PlutoWarmStartState State, long ElapsedMs, int Simulations, int SwappedCells, int RegimeId,
        IReadOnlyList<PlutoWarmStartLane> Lanes, string? Error = null)
    {
        public static readonly PlutoWarmStartReport NotStarted = new(PlutoWarmStartState.NotStarted, 0, 0, 0, 0, Array.Empty<PlutoWarmStartLane>());
    }

    public sealed class PlutoReplayWarmStarter
    {
        public const int Simulations = 100, Lookback = 10, MinBars = Lookback + 12, Bars = 120, MaxSymbolsPerAsset = 32;
        public const int RoundTripCostBp = 25, StopBp = 700, ShockBarBp = 500;
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);
        private static readonly int[] s_holds = { 3, 5, 10 };

        private readonly PlutoCognitiveTensorLake _tensor;
        private readonly IPlutoCandleSource _source;
        private readonly Func<int> _regime;
        private int _started;
        private PlutoWarmStartReport _report = PlutoWarmStartReport.NotStarted;

        /// <param name="regimeId">현재 판정 국면(0 = 판정 전 → 자산 기준선만 결속).</param>
        public PlutoReplayWarmStarter(PlutoCognitiveTensorLake tensor, IPlutoCandleSource source, Func<int>? regimeId = null)
        {
            _tensor = tensor ?? throw new ArgumentNullException(nameof(tensor));
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _regime = regimeId ?? (static () => 0);
        }

        public PlutoWarmStartReport Report => Volatile.Read(ref _report);

        /// <summary>백그라운드 1회 기동(중복 = 기존 보고 반환 Task).</summary>
        public Task<PlutoWarmStartReport> StartAsync() =>
            Interlocked.Exchange(ref _started, 1) == 0 ? Task.Run(RunCore) : Task.FromResult(Report);

        /// <summary>동기 1회 실행(테스트 · 중복 = 기존 보고).</summary>
        public PlutoWarmStartReport Run() => Interlocked.Exchange(ref _started, 1) == 0 ? RunCore() : Report;

        private PlutoWarmStartReport RunCore()
        {
            var sw = Stopwatch.StartNew();
            Volatile.Write(ref _report, new PlutoWarmStartReport(PlutoWarmStartState.Running, 0, 0, 0, 0, Array.Empty<PlutoWarmStartLane>()));
            int regime = 0;
            IReadOnlyList<PlutoCandleSeries> all;
            try
            {
                regime = _regime();
                all = _source.LoadRecent(MaxSymbolsPerAsset, Bars);
            }
            catch (Exception ex)
            {
                return Publish(new PlutoWarmStartReport(PlutoWarmStartState.Failed, sw.ElapsedMilliseconds, 0, 0, regime, Array.Empty<PlutoWarmStartLane>(), ex.GetType().Name));
            }
            if (regime is < 1 or > PlutoCognitiveTensorLake.Regimes) regime = 0;

            var lanes = new List<PlutoWarmStartLane>(PlutoCognitiveTensorLake.Assets);
            var samples = new PlutoLearningSample[Simulations];
            var state = PlutoWarmStartState.Completed;
            int sims = 0, swapped = 0;
            for (int a = 0; a < PlutoCognitiveTensorLake.Assets; a++)
            {
                var asset = (PlutoAssetClass)a;
                var series = new List<PlutoCandleSeries>();
                foreach (var s in all)
                    if (s is not null && s.Asset == asset && s.Closes is { Length: >= MinBars }) series.Add(s);
                if (series.Count == 0) continue;
                if (sw.Elapsed > Budget) { state = PlutoWarmStartState.TimedOut; break; }

                int n = Replay(series, _tensor.StrictnessOf(asset, PlutoCognitiveTensorLake.BaselineRegime), samples);
                var cal = new PlutoWeightCalibrator();
                int wins = 0;
                for (int i = 0; i < n; i++) { cal.ObserveSample(in samples[i]); if (samples[i].IsWin) wins++; }
                if (sw.Elapsed > Budget) { state = PlutoWarmStartState.TimedOut; break; }
                int laneSwaps = _tensor.ApplyReplay(asset, PlutoCognitiveTensorLake.BaselineRegime, cal, out var verdict) ? 1 : 0;
                if (regime > 0 && _tensor.ApplyReplay(asset, regime, cal, out var regimeVerdict)) { laneSwaps++; verdict = regimeVerdict; }
                lanes.Add(new PlutoWarmStartLane(asset, series.Count, n, wins, laneSwaps, verdict));
                sims += n;
                swapped += laneSwaps;
            }
            if (lanes.Count == 0 && state == PlutoWarmStartState.Completed) state = PlutoWarmStartState.NoData;
            return Publish(new PlutoWarmStartReport(state, sw.ElapsedMilliseconds, sims, swapped, regime, lanes.AsReadOnly()));
        }

        private PlutoWarmStartReport Publish(PlutoWarmStartReport r)
        {
            Volatile.Write(ref _report, r);
            return r;
        }

        /// <summary>
        /// 결정적 가상 리플레이: 균등 간격 진입점 최대 destination.Length(≤ Simulations)회 → 표본 기록. 반환 = 기록 수(후보 0 = 0).
        /// </summary>
        public static int Replay(IReadOnlyList<PlutoCandleSeries> series, int strictnessAtEntry, Span<PlutoLearningSample> destination)
        {
            ArgumentNullException.ThrowIfNull(series);
            long candidates = 0;
            foreach (var s in series) candidates += Candidates(s);
            if (candidates == 0) return 0;
            int count = Math.Min(destination.Length, Simulations);
            for (int k = 0; k < count; k++)
            {
                long pick = candidates >= count ? (long)k * candidates / count : k % candidates;
                int hold = s_holds[(int)((k + k / candidates) % s_holds.Length)];
                foreach (var s in series)
                {
                    long c = Candidates(s);
                    if (pick >= c) { pick -= c; continue; }
                    destination[k] = Simulate(s.Closes, Lookback + (int)pick, hold, strictnessAtEntry);
                    break;
                }
            }
            return count;
        }

        private static long Candidates(PlutoCandleSeries s) => s?.Closes is { Length: >= MinBars } c ? c.Length - 1 - Lookback : 0;

        /// <summary>진입 봉 e · 보유 hold 봉 1회 시뮬레이션(롱 · 순수 함수).</summary>
        public static PlutoLearningSample Simulate(double[] closes, int e, int hold, int strictnessAtEntry)
        {
            ArgumentNullException.ThrowIfNull(closes);
            if (e < Lookback || e > closes.Length - 2) throw new ArgumentOutOfRangeException(nameof(e));
            double entry = closes[e];
            int momBp = Bp(entry, closes[e - Lookback]);
            int confidence = Math.Clamp(50 + momBp / 40, 0, 100);
            int exit = Math.Min(e + Math.Max(1, hold), closes.Length - 1);
            bool shock = false, stopped = false;
            for (int j = e + 1; j <= exit; j++)
            {
                if (Bp(closes[j], entry) > -StopBp) continue;
                shock = Bp(closes[j], closes[j - 1]) <= -ShockBarBp;
                stopped = true;
                exit = j;
                break;
            }
            int ret = (int)Math.Clamp((long)Bp(closes[exit], entry) - RoundTripCostBp, int.MinValue, int.MaxValue);
            bool win = ret > 0;
            PlutoLesson lesson = win
                ? (momBp > 0 ? PlutoLesson.TrendFollow : PlutoLesson.DisciplinedExit)
                : shock ? PlutoLesson.ExogenousShock
                : momBp <= 0 ? PlutoLesson.RespectCounterThesis
                : !stopped && exit >= Lookback && Bp(closes[exit], closes[exit - Lookback]) < 0 ? PlutoLesson.RegimeShiftExit
                : PlutoLesson.SignalDecay;
            return new PlutoLearningSample(ret, confidence, strictnessAtEntry, lesson, win ? PostMortemVerdict.Win : PostMortemVerdict.Loss);
        }

        private static int Bp(double now, double then) =>
            then > 0 && double.IsFinite(now) && double.IsFinite(then) ? (int)Math.Clamp(Math.Round((now / then - 1) * 10000), -1_000_000, 1_000_000) : 0;
    }
}
