// ============================================================================
// PlutoCheckpointManager — v12.18.84-DEV: 플루토 학습 가중치 영속화 · 롤링 체크포인트 레이크 · 기동 안전 복원(콜드 경로 전용)
//
//   PlutoLearningCheckpoint(값 타입 · 행 1건): 국면 ID(판정 전 −1) · 누적 학습 에피소드(재기동 합산 평생 청산 회고 표본) · 가중치 세대 ·
//     모멘텀 tilt% · 악마의 변호인 엄격도 · 섀도우 검증 승률 bp · MDD bp · 표본 · 보정 시각(UTC ticks) · 체크섬(FNV-1a 64 · 필드 전부).
//     ※ 기록 대상 = seqlock 라이브 가중치 = 섀도우 게이트 통과분만(골든) — 미검증 후보 기록 0. 샤프 지표는 보정기가 산출하지 않음(기록 0 · 날조 금지).
//   IPlutoLearningStore = 저장소 경계(호스트 어댑터 = LocalDatabase 전용 테이블). 엔진은 SQL · 연결을 모른다.
//   Restore(기동 1회 · 스웜 Start 전): 최신 순 RestoreScan(64)건 판독 → 첫 무결 행(체크섬 · 범위 · 시각) = 골든 → seqlock.TryRestore(δ=0 원자 주입) ·
//     ※ 저장소 장애 기동 = 평생 기준 미상 → 그 기동 동안 기록 0(누적 에피소드 역행 기록 방지) ·
//     최신 행 손상 = 그 이전 무결 행으로 롤백(RolledBack · 손상 건수 계수) · 전부 손상 = 중립 폴백(CorruptFallback) · 저장소 장애 = 중립(StoreFailed).
//   Checkpoint(백그라운드 주기 · 콜드): 라이브 가중치 + 평생 에피소드가 직전 기록과 다를 때만 1행 추가(합체 기록 · 무변화 = 기록 0) · 레이크 = 최근 LakeCapacity(64)행.
//     실패 = 계수만(자동 재시도 · 큐잉 0 → 다음 주기가 최신 스냅샷으로 다시 시도). Dispose = 루프 정지 후 최종 1회 기록.
//   ★체결 핫패스(Track A)는 이 관리자를 모른다 — seqlock TryRead 만(δ=0 · DB I/O 블로킹 0). ★liveArmed:false · QuantumVault · 피어 접근 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>학습 체크포인트 1건(값 타입 · 체크섬 봉인).</summary>
    public readonly struct PlutoLearningCheckpoint
    {
        public const int NoRegime = -1;

        public readonly int RegimeId;
        public readonly long Episodes;
        public readonly PlutoCognitiveWeights Weights;
        public readonly long CalibratedUtcTicks;
        public readonly long Checksum;

        public PlutoLearningCheckpoint(int regimeId, long episodes, in PlutoCognitiveWeights weights, long calibratedUtcTicks, long checksum)
        {
            RegimeId = regimeId; Episodes = episodes; Weights = weights; CalibratedUtcTicks = calibratedUtcTicks; Checksum = checksum;
        }

        /// <summary>체크섬 자동 봉인 생성.</summary>
        public static PlutoLearningCheckpoint Seal(int regimeId, long episodes, in PlutoCognitiveWeights weights, long calibratedUtcTicks) =>
            new(regimeId, episodes, in weights, calibratedUtcTicks, ComputeChecksum(regimeId, episodes, in weights, calibratedUtcTicks));

        /// <summary>해석 불가 행 표식(무결성 검사 불통과 고정).</summary>
        public static PlutoLearningCheckpoint Corrupt => new(int.MinValue, -1, default, 0, 0);

        public static long ComputeChecksum(int regimeId, long episodes, in PlutoCognitiveWeights w, long calibratedUtcTicks)
        {
            ulong h = 14695981039346656037UL;
            h = Mix(h, 0x504C55544F434B50L);                                     // 도메인 솔트("PLUTOCKP")
            h = Mix(h, regimeId);
            h = Mix(h, episodes);
            h = Mix(h, w.Generation);
            h = Mix(h, w.MomentumTiltPct);
            h = Mix(h, w.DevilStrictnessPts);
            h = Mix(h, w.ShadowWinRateBp);
            h = Mix(h, w.ShadowMddBp);
            h = Mix(h, w.ShadowSamples);
            h = Mix(h, calibratedUtcTicks);
            return (long)h;
        }

        private static ulong Mix(ulong h, long v)
        {
            ulong x = (ulong)v;
            for (int i = 0; i < 8; i++) { h ^= (byte)(x >> (i * 8)); h *= 1099511628211UL; }
            return h;
        }

        /// <summary>무결: 체크섬 일치 · 가중치 범위 · 에피소드 ≥ 0 · 국면 ID ≥ −1 · 시각 &gt; 0.</summary>
        public bool IsIntact =>
            RegimeId >= NoRegime && Episodes >= 0 && CalibratedUtcTicks > 0 && Weights.IsWellFormed
            && Checksum == ComputeChecksum(RegimeId, Episodes, in Weights, CalibratedUtcTicks);
    }

    /// <summary>체크포인트 저장소 경계(호스트 어댑터 구현 · 콜드).</summary>
    public interface IPlutoLearningStore
    {
        /// <summary>최신 순 최대 max 건. 해석 불가 행 = PlutoLearningCheckpoint.Corrupt 로 포함(관리자가 롤백 판정). 장애 = 예외.</summary>
        IReadOnlyList<PlutoLearningCheckpoint> LoadRecent(int max);

        /// <summary>1행 추가 + 최근 keep 행만 유지(원자). 장애 = 예외.</summary>
        void Append(in PlutoLearningCheckpoint checkpoint, int keep);
    }

    public enum PlutoPersistState : byte
    {
        NotRestored = 0,
        /// <summary>저장 기록 없음(첫 기동) · 중립 시작.</summary>
        Fresh = 1,
        /// <summary>최신 체크포인트 복원.</summary>
        Restored = 2,
        /// <summary>최신 손상 → 이전 무결 체크포인트로 롤백 복원.</summary>
        RolledBack = 3,
        /// <summary>전부 손상 → 중립 폴백.</summary>
        CorruptFallback = 4,
        /// <summary>저장소 판독 장애 → 중립 폴백.</summary>
        StoreFailed = 5,
    }

    public sealed class PlutoCheckpointManager : IDisposable
    {
        public const int LakeCapacity = 64, RestoreScan = 64;

        private readonly IPlutoLearningStore _store;
        private readonly PlutoCognitiveWeightSeqlock _weights;
        private readonly Func<long> _observed;
        private readonly Func<int> _regimeId;
        private readonly Func<long> _utcNow;
        private readonly object _gate = new();
        private PlutoLearningCheckpoint _last;
        private int _state;
        private long _baseEpisodes, _saves, _saveFailures, _lastSavedUtcTicks, _corruptSkipped, _faulted;
        private CancellationTokenSource? _cts;
        private Task? _loop;

        /// <param name="observedEpisodes">이번 기동 누적 청산 회고 표본(보정기 Observed).</param>
        /// <param name="regimeId">현재 국면 ID(판정 전 = −1).</param>
        public PlutoCheckpointManager(IPlutoLearningStore store, PlutoCognitiveWeightSeqlock weights, Func<long> observedEpisodes,
            Func<int>? regimeId = null, Func<long>? utcNowTicks = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _weights = weights ?? throw new ArgumentNullException(nameof(weights));
            _observed = observedEpisodes ?? throw new ArgumentNullException(nameof(observedEpisodes));
            _regimeId = regimeId ?? (() => PlutoLearningCheckpoint.NoRegime);
            _utcNow = utcNowTicks ?? (() => DateTime.UtcNow.Ticks);
        }

        public PlutoPersistState State => (PlutoPersistState)Volatile.Read(ref _state);
        /// <summary>평생 누적 학습 에피소드 = 복원 기준 + 이번 기동 관측.</summary>
        public long LifetimeEpisodes => Interlocked.Read(ref _baseEpisodes) + _observed();
        public long Saves => Interlocked.Read(ref _saves);
        public long SaveFailures => Interlocked.Read(ref _saveFailures);
        public long LastSavedUtcTicks => Interlocked.Read(ref _lastSavedUtcTicks);
        /// <summary>복원 시 건너뛴 손상 행 수.</summary>
        public long CorruptSkipped => Interlocked.Read(ref _corruptSkipped);
        public long Faulted => Interlocked.Read(ref _faulted);
        public bool Running => Volatile.Read(ref _loop) is { IsCompleted: false };

        /// <summary>기동 1회 복원(스웜 Start 전 · 콜드). 중복 호출 = 예외.</summary>
        public PlutoPersistState Restore()
        {
            lock (_gate)
            {
                if (State != PlutoPersistState.NotRestored) throw new InvalidOperationException("이미 복원됨");
                IReadOnlyList<PlutoLearningCheckpoint> rows;
                try { rows = _store.LoadRecent(RestoreScan); }
                catch (Exception) { return Set(PlutoPersistState.StoreFailed); }
                if (rows.Count == 0) return Set(PlutoPersistState.Fresh);
                for (int i = 0; i < rows.Count; i++)
                {
                    var cp = rows[i];
                    if (!cp.IsIntact) { Interlocked.Increment(ref _corruptSkipped); continue; }
                    if (!_weights.TryRestore(in cp.Weights)) return Set(PlutoPersistState.Fresh);   // 이미 학습된 라이브 가중치 = 덮어쓰기 0
                    _last = cp;
                    Interlocked.Exchange(ref _baseEpisodes, cp.Episodes);
                    Interlocked.Exchange(ref _lastSavedUtcTicks, cp.CalibratedUtcTicks);
                    return Set(i == 0 ? PlutoPersistState.Restored : PlutoPersistState.RolledBack);
                }
                return Set(PlutoPersistState.CorruptFallback);
            }
        }

        private PlutoPersistState Set(PlutoPersistState s)
        {
            Volatile.Write(ref _state, (int)s);
            return s;
        }

        /// <summary>체크포인트 1회(콜드 · 직렬화). 복원 전/저장소 판독 장애(평생 기준 미상 → 기록 시 누적 역행) · 변화 없음 · 판독 경합 · 저장 실패 = false.</summary>
        public bool Checkpoint()
        {
            lock (_gate)
            {
                if (State is PlutoPersistState.NotRestored or PlutoPersistState.StoreFailed) return false;
                if (!_weights.TryRead(out var w)) return false;
                long episodes = LifetimeEpisodes;
                var l = _last.Weights;
                if (episodes == _last.Episodes && w.Generation == l.Generation && w.MomentumTiltPct == l.MomentumTiltPct && w.DevilStrictnessPts == l.DevilStrictnessPts)
                    return false;
                var cp = PlutoLearningCheckpoint.Seal(_regimeId(), episodes, in w, _utcNow());
                try { _store.Append(in cp, LakeCapacity); }
                catch (Exception) { Interlocked.Increment(ref _saveFailures); return false; }
                _last = cp;
                Interlocked.Increment(ref _saves);
                Interlocked.Exchange(ref _lastSavedUtcTicks, cp.CalibratedUtcTicks);
                return true;
            }
        }

        /// <summary>백그라운드 주기 기록 기동(중복 기동 = 무시).</summary>
        public void Start(TimeSpan interval)
        {
            if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
            lock (_gate)
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
            while (true)
            {
                try { if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return; }
                catch (OperationCanceledException) { return; }
                try { Checkpoint(); }
                catch (Exception) { Interlocked.Increment(ref _faulted); }        // 콜백(국면 · 시계) 장애 = 계수만 · 다음 주기 재시도
            }
        }

        /// <summary>루프 정지 + 최종 1회 기록(종료 시 마지막 학습분 보존).</summary>
        public void Dispose()
        {
            CancellationTokenSource? cts;
            Task? loop;
            lock (_gate) { cts = _cts; loop = _loop; _cts = null; }
            if (cts is not null)
            {
                cts.Cancel();
                try { loop?.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
                cts.Dispose();
            }
            try { Checkpoint(); }
            catch (Exception) { Interlocked.Increment(ref _faulted); }
        }
    }
}
