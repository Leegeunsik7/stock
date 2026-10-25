// ============================================================================
// PlutoContinuousLearningSwarm — v12.18.77-DEV: 플루토 지속 학습 2-트랙 — Track B 백그라운드 학습 스웜(수집 → 연상 → 회고 학습 → 섀도우 검증 → 원자 핫스왑)
//
//   Track A(체결 핫패스 · 3대 아레나 정산 스레드) = PlutoCognitiveWeightSeqlock.TryRead 만(δ=0 · 잠금 0 · 학습 비용 0).
//   Track B(이 스웜 · 주기 Step 1회 = 콜드):
//     ① 아레나 근거 링 판독(RationaleCopier · 각 엔진 CopyRationales · 오래된 순) → 순번 > 직전 처리 순번만 처리(중복 학습 0).
//        진입 근거(Governance.IsEntry) = 진입 순번 → 최종 확신도 · 당시 엄격도 를 아레나별 고정 링(256)에 기록.
//        최종 청산 회고(PostMortem.IsFinal) = 진입 순번으로 확신도 조회(미상 = −1) → PlutoWeightCalibrator.Observe.
//        엔진 근거 링(128)보다 많이 밀리면 누락분 = 계수(GapSkipped)만(재조회 · 큐잉 0).
//     ② 글로벌 인제스천 허브 직전 안정값 → 에피소딕 기억창고 유사 국면 탐색(0~100%) · 거시 역풍 입력(미 10년물/달러 변화) 을 아레나에 주입(콜백 · 값 없으면 생략).
//     ③ 새 표본 ≥ RecalibrateEvery(10) 누적 시 보정기 제안 → 섀도우 검증 → 통과 시 seqlock 원자 핫스왑(불통과 = 기존 유지 · 판정 기록).
//     ④ 상태 스냅샷(PlutoLearningStatus · 불변 레코드) 게시 — UI 어댑터가 판독(콜드).
//   Start = 백그라운드 PeriodicTimer 루프(예외 = Faulted 계수 · 루프 지속 · 체결 스레드 무간섭) · Dispose = 취소.
//   ★liveArmed:false — 가중치는 가상 원장 진입 판정 입력일 뿐(브로커 · 무장 · 송출 경로 0). ★QuantumVault · 피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Engines.Arena.Common;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>엔진 근거 링 복사(오래된 순 · 반환 = 복사 수). 각 아레나 엔진 CopyRationales 와 같은 형태.</summary>
    public delegate int RationaleCopier(Span<AiTradeRationale> destination);

    /// <summary>학습 상태 스냅샷(불변 · UI 판독용).</summary>
    public sealed record PlutoLearningStatus(
        bool Running, PlutoIngestHealth Health, string? SourceName, PlutoMacroSnapshot Macro,
        PlutoEpisodeMatch Match, PlutoEpisode? Episode, PlutoCognitiveWeights Weights, PlutoShadowVerdict LastVerdict,
        long Cycles, long Samples, long Swaps, long RejectedSwaps, long IngestAccepted, long IngestRejected, long SourceFailures, long Faulted, long UpdatedUtcTicks);

    public sealed class PlutoContinuousLearningSwarm : IDisposable
    {
        public const int CopyCapacity = 256, EntryMapCapacity = 256, RecalibrateEvery = 10;

        private sealed class Arena
        {
            public readonly string Name;
            public readonly RationaleCopier Copy;
            public readonly long[] EntrySeq = new long[EntryMapCapacity];
            public readonly int[] EntryConfidence = new int[EntryMapCapacity];
            public readonly int[] EntryStrictness = new int[EntryMapCapacity];
            public long LastSeq;

            public Arena(string name, RationaleCopier copy) { Name = name; Copy = copy; }
        }

        private readonly PlutoGlobalDataIngestor _ingestor;
        private readonly PlutoEpisodicMemoryStore _memory;
        private readonly PlutoWeightCalibrator _calibrator;
        private readonly PlutoCognitiveWeightSeqlock _weights;
        private readonly List<Arena> _arenas = new();
        private readonly AiTradeRationale[] _buf = new AiTradeRationale[CopyCapacity];
        private readonly object _stepGate = new();
        private readonly Func<long> _utcNow;
        private Action<int, int>? _macroSink;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private PlutoLearningStatus? _status;
        private PlutoShadowVerdict _lastVerdict;
        private long _cycles, _faulted, _gapSkipped, _samplesAtLastCalibration;

        public PlutoContinuousLearningSwarm(PlutoGlobalDataIngestor ingestor, PlutoEpisodicMemoryStore memory, PlutoWeightCalibrator calibrator,
            PlutoCognitiveWeightSeqlock weights, Func<long>? utcNowTicks = null)
        {
            _ingestor = ingestor ?? throw new ArgumentNullException(nameof(ingestor));
            _memory = memory ?? throw new ArgumentNullException(nameof(memory));
            _calibrator = calibrator ?? throw new ArgumentNullException(nameof(calibrator));
            _weights = weights ?? throw new ArgumentNullException(nameof(weights));
            _utcNow = utcNowTicks ?? (() => DateTime.UtcNow.Ticks);
        }

        public PlutoGlobalDataIngestor Ingestor => _ingestor;
        public PlutoEpisodicMemoryStore Memory => _memory;
        public PlutoWeightCalibrator Calibrator => _calibrator;
        public PlutoCognitiveWeightSeqlock Weights => _weights;
        public long Cycles => Interlocked.Read(ref _cycles);
        public long Faulted => Interlocked.Read(ref _faulted);
        public long GapSkipped => Interlocked.Read(ref _gapSkipped);
        public bool Running => Volatile.Read(ref _loop) is { IsCompleted: false };

        /// <summary>최신 상태(Step 0회 = null).</summary>
        public PlutoLearningStatus? Status => Volatile.Read(ref _status);

        /// <summary>학습 대상 아레나 등록(기동 전 · 이름 = 진단용).</summary>
        public void AddArena(string name, RationaleCopier copy)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(copy);
            lock (_stepGate) _arenas.Add(new Arena(name, copy));
        }

        /// <summary>거시 역풍 입력 주입 대상(미 10년물 변화 bp · 달러 인덱스 변화 bp) — 아레나 SetMacroHeadwind.</summary>
        public void SetMacroSink(Action<int, int> sink) => Volatile.Write(ref _macroSink, sink ?? throw new ArgumentNullException(nameof(sink)));

        /// <summary>학습 1주기(동기 · 콜드). 동시 호출 = 직렬화.</summary>
        public PlutoLearningStatus Step()
        {
            lock (_stepGate)
            {
                foreach (var a in _arenas) Harvest(a);

                bool hasMacro = _ingestor.TryRead(out var macro);
                var match = hasMacro ? _memory.FindNearest(in macro) : default;
                if (hasMacro && macro.Has(PlutoMacroField.Us10y | PlutoMacroField.Dollar))
                    Volatile.Read(ref _macroSink)?.Invoke(macro.Us10yChangeBp, macro.DollarIndexChangeBp);

                long observed = _calibrator.Observed;
                if (observed - _samplesAtLastCalibration >= RecalibrateEvery)
                {
                    _samplesAtLastCalibration = observed;
                    _weights.TryRead(out var current);
                    if (_calibrator.Propose(in current, out var candidate))
                    {
                        _lastVerdict = _calibrator.ShadowValidate(in current, ref candidate);
                        if (_lastVerdict.Reason != PlutoShadowReason.NoChange) _weights.TrySwap(in candidate, _lastVerdict.Passed);
                    }
                }

                _weights.TryRead(out var live);
                long cycles = Interlocked.Increment(ref _cycles);
                var status = new PlutoLearningStatus(Running, _ingestor.Health, _ingestor.SourceName, macro, match, match.Found ? _memory.Get(match.EpisodeId) : null,
                    live, _lastVerdict, cycles, observed, _weights.Swaps, _weights.RejectedSwaps, _ingestor.Accepted, _ingestor.Rejected, _ingestor.SourceFailures,
                    Faulted, _utcNow());
                Volatile.Write(ref _status, status);
                return status;
            }
        }

        private void Harvest(Arena a)
        {
            var span = _buf.AsSpan();
            int n = a.Copy(span);
            if (n <= 0) return;
            if (a.LastSeq > 0 && span[0].Seq > a.LastSeq + 1) Interlocked.Add(ref _gapSkipped, span[0].Seq - a.LastSeq - 1);
            _weights.TryRead(out var live);
            for (int i = 0; i < n; i++)
            {
                ref readonly var r = ref span[i];
                if (r.Seq <= a.LastSeq) continue;
                a.LastSeq = r.Seq;
                var g = r.Governance;
                if (g.IsEntry)
                {
                    int slot = (int)(r.Seq % EntryMapCapacity);
                    a.EntrySeq[slot] = r.Seq;
                    a.EntryConfidence[slot] = g.Confidence;
                    a.EntryStrictness[slot] = live.DevilStrictnessPts;
                }
                else if (g.Governed && g.PostMortem.HasReview && g.PostMortem.IsFinal)
                {
                    var pm = g.PostMortem;
                    int slot = (int)(pm.EntrySeq % EntryMapCapacity);
                    bool known = a.EntrySeq[slot] == pm.EntrySeq;
                    _calibrator.Observe(in pm, known ? a.EntryConfidence[slot] : -1, known ? a.EntryStrictness[slot] : live.DevilStrictnessPts);
                }
            }
        }

        /// <summary>백그라운드 학습 루프 기동(중복 기동 = 무시).</summary>
        public void Start(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            lock (_stepGate)
            {
                if (_loop is not null) return;
                _cts = new CancellationTokenSource();
                var ct = _cts.Token;
                _loop = Task.Run(() => LoopAsync(interval, ct));
            }
        }

        private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(interval);
            while (!ct.IsCancellationRequested)
            {
                try { Step(); }
                catch (Exception) { Interlocked.Increment(ref _faulted); }        // 학습 장애 = 계수만(체결 경로 무간섭 · 다음 주기 재시도)
                try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return; }
                catch (OperationCanceledException) { return; }
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_stepGate) { cts = _cts; loop = _loop; _cts = null; }
            if (cts is null) return;
            cts.Cancel();
            try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            cts.Dispose();
        }
    }
}
