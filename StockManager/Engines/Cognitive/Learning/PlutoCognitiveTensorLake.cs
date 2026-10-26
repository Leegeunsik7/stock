// ============================================================================
// PlutoCognitiveTensorLake — v12.18.86-DEV: 플루토 36슬롯 인지 텐서 매트릭스(12대 역사적 국면 × 3대 자산군 KRX · US · Upbit 독립 가중치 · Track A δ=0 판독)
//
//   셀 = 사전 할당 PlutoCognitiveWeightSeqlock 1개 + 전용 보정기(PlutoWeightCalibrator · 링 256) — 슬롯끼리 표본 · 가중치 · 세대 완전 독립.
//     국면 슬롯 36개 = 행 1~12(PlutoRegimeVectorLake 국면 ID) × 열 3(PlutoAssetClass) · 추가 자산 기준선 3개 = 행 0(국면 판정 전 · 자산군 전 표본 · 웜스타트 대상).
//     ※ 기준선 행은 국면 판정 전(FRED 키 미설정 · 관측 대기)에도 자산군 특화가 비지 않게 하는 폴백 — 36슬롯 매트릭스 의미는 불변(정직 표기).
//   해석(Resolve · 체결 핫패스 δ=0): 국면 판독값(PlutoRegimeMatch.CycleId) → 셀 인덱스 = 사전 할당 seqlock 배열 참조 전환(포인터 스왑 · 할당 0)
//     → 국면 슬롯 보정됨(세대 > 0) = 그 슬롯 · 아니면 자산 기준선 보정됨 = 기준선 · 아니면 호출자 전역 가중치 그대로(학습 전 = 기존 동작 보존).
//     셀 판독 경합 상한 초과 = 전역 유지(fail-safe). Activate(국면) = Track B 가 활성 국면 포인터 원자 게시(스크리너 · 브리핑 콜드 판독용).
//   학습(Track B 콜드): Observe(자산군, 진입 시 국면, 회고) = 국면 슬롯 + 자산 기준선 보정기 동시 관측 →
//     Recalibrate = 새 표본 ≥ RecalibrateEvery(10) 셀만 제안(학습 절반) → 섀도우 검증(홀드아웃 절반) → 통과 시 그 셀만 원자 핫스왑(불통과 = 기존 유지).
//   웜스타트(ApplyReplay): 가상 리플레이 표본으로 채운 임시 보정기 → 같은 제안 · 섀도우 게이트 → 통과 시 셀 핫스왑(실 에피소드 계수 0 · 합성 표본 영속 0).
//   영속(PlutoCheckpointManager 위임 · 콜드): 셀별 마지막 기록과 (세대 · tilt · 엄격도 · 평생 에피소드) 가 다를 때만 슬롯 행 1건 → (RegimeId, AssetClass) 복합 키 복원.
//   ★δ=0: Resolve/TryRead/Observe/Recalibrate 힙 할당 0(고정 배열 · 값 타입). ★liveArmed:false — 가상 원장 진입 판정 입력만(주문 수단 0) · QuantumVault/피어 0.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using StockManager.Engines.Cognitive.Governance;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>3대 자산군(텐서 열 · 값 = 저장 AssetClass 열).</summary>
    public enum PlutoAssetClass : byte
    {
        Krx = 0,
        Us = 1,
        Upbit = 2,
    }

    /// <summary>해석된 가중치 출처.</summary>
    public enum PlutoTensorSource : byte
    {
        /// <summary>호출자 전역 가중치(슬롯 · 기준선 미보정).</summary>
        Global = 0,
        /// <summary>자산 기준선(국면 판정 전 또는 국면 슬롯 미보정).</summary>
        Baseline = 1,
        /// <summary>국면 × 자산군 전용 슬롯.</summary>
        Regime = 2,
    }

    /// <summary>슬롯 학습 성숙도.</summary>
    public enum PlutoSlotMaturity : byte
    {
        /// <summary>표본 0.</summary>
        Untrained = 0,
        /// <summary>표본 &lt; MinSamples(20) — 축적 중.</summary>
        Accumulating = 1,
        /// <summary>표본 ≥ 20 · 섀도우 통과 전.</summary>
        Calibratable = 2,
        /// <summary>섀도우 통과 핫스왑(세대 &gt; 0).</summary>
        Calibrated = 3,
    }

    /// <summary>셀 1개 상태(콜드 · 브리핑/감사용).</summary>
    public readonly record struct PlutoTensorCell(int RegimeId, PlutoAssetClass Asset, PlutoCognitiveWeights Weights, long LifetimeEpisodes, long SessionSamples,
        long WarmStarts, PlutoSlotMaturity Maturity);

    public sealed class PlutoCognitiveTensorLake
    {
        public const int Regimes = PlutoRegimeVectorLake.Count, Assets = 3, Slots = Regimes * Assets, Cells = (Regimes + 1) * Assets;
        public const int RecalibrateEvery = PlutoContinuousLearningSwarm.RecalibrateEvery;
        /// <summary>자산 기준선 행 국면 ID(저장 RegimeId = −1).</summary>
        public const int BaselineRegime = PlutoLearningCheckpoint.NoRegime;

        private readonly PlutoCognitiveWeightSeqlock[] _cells = new PlutoCognitiveWeightSeqlock[Cells];
        private readonly PlutoWeightCalibrator[] _calibrators = new PlutoWeightCalibrator[Cells];
        private readonly long[] _baseEpisodes = new long[Cells];
        private readonly long[] _calibratedAt = new long[Cells];
        private readonly long[] _warmStarts = new long[Cells];
        private readonly PlutoLearningCheckpoint[] _lastSaved = new PlutoLearningCheckpoint[Cells];
        private readonly PlutoShadowVerdict[] _lastVerdict = new PlutoShadowVerdict[Cells];
        private readonly object _learnGate = new();
        private int _activeRegime;
        private long _swaps, _rejected, _activations;

        public PlutoCognitiveTensorLake()
        {
            for (int i = 0; i < Cells; i++)
            {
                _cells[i] = new PlutoCognitiveWeightSeqlock();
                _calibrators[i] = new PlutoWeightCalibrator();
            }
        }

        /// <summary>셀 인덱스(δ=0): 국면 1~12 = 해당 행 · 그 외(판정 전 · 범위 밖) = 기준선 행 0.</summary>
        public static int CellOf(PlutoAssetClass asset, int regimeId) =>
            (regimeId is >= 1 and <= Regimes ? regimeId : 0) * Assets + ((int)asset % Assets);

        /// <summary>셀 → (국면 ID · 자산군). 기준선 = BaselineRegime(−1).</summary>
        public static (int RegimeId, PlutoAssetClass Asset) KeyOf(int cell)
        {
            if ((uint)cell >= Cells) throw new ArgumentOutOfRangeException(nameof(cell));
            int row = cell / Assets;
            return (row == 0 ? BaselineRegime : row, (PlutoAssetClass)(cell % Assets));
        }

        public long Swaps => Interlocked.Read(ref _swaps);
        public long RejectedSwaps => Interlocked.Read(ref _rejected);
        public long Activations => Interlocked.Read(ref _activations);
        /// <summary>활성 국면(0 = 판정 전).</summary>
        public int ActiveRegime => Volatile.Read(ref _activeRegime);

        /// <summary>Track B: 활성 국면 포인터 원자 게시(범위 밖 = 판정 전). 변경 시만 계수.</summary>
        public void Activate(int regimeId)
        {
            int r = regimeId is >= 1 and <= Regimes ? regimeId : 0;
            if (Interlocked.Exchange(ref _activeRegime, r) != r) Interlocked.Increment(ref _activations);
        }

        /// <summary>셀 가중치 판독(δ=0 · 경합 = false · default).</summary>
        public bool TryRead(PlutoAssetClass asset, int regimeId, out PlutoCognitiveWeights weights) => _cells[CellOf(asset, regimeId)].TryRead(out weights);

        /// <summary>
        /// 체결 핫패스 해석(δ=0): 국면 슬롯(보정됨) → 자산 기준선(보정됨) → weights 그대로(전역). 반환 = 출처.
        /// </summary>
        public PlutoTensorSource Resolve(PlutoAssetClass asset, int regimeId, ref PlutoCognitiveWeights weights)
        {
            int a = (int)asset % Assets;
            if (regimeId is >= 1 and <= Regimes && _cells[regimeId * Assets + a].TryRead(out var slot) && slot.Generation > 0)
            {
                weights = slot;
                return PlutoTensorSource.Regime;
            }
            if (_cells[a].TryRead(out var baseline) && baseline.Generation > 0)
            {
                weights = baseline;
                return PlutoTensorSource.Baseline;
            }
            return PlutoTensorSource.Global;
        }

        /// <summary>
        /// [v12.18.88-DEV] 셀 실측 엣지(프랙탈 켈리 입력 · 콜드): 국면 슬롯 표본 ≥ 켈리 최소 표본 = 그 슬롯 · 아니면 자산 기준선(국면 무관 자산군 전 표본).
        /// fromRegime = 국면 슬롯 출처 여부.
        /// </summary>
        public Council.PlutoEdgeStats EdgeOf(PlutoAssetClass asset, int regimeId, out bool fromRegime)
        {
            int a = (int)asset % Assets;
            if (regimeId is >= 1 and <= Regimes)
            {
                var slot = _calibrators[regimeId * Assets + a].Edge();
                if (slot.Samples >= Council.PlutoDynamicKellyEngine.MinSamples) { fromRegime = true; return slot; }
            }
            fromRegime = false;
            return _calibrators[a].Edge();
        }

        /// <summary>활성 국면 기준 해석(콜드 · 스크리너/브리핑).</summary>
        public PlutoTensorSource ResolveActive(PlutoAssetClass asset, ref PlutoCognitiveWeights weights) => Resolve(asset, ActiveRegime, ref weights);

        /// <summary>Track B: 청산 회고 1건 = 국면 슬롯 + 자산 기준선 관측(δ=0). 무결 회고 아님 = false.</summary>
        public bool Observe(PlutoAssetClass asset, int regimeAtEntry, in PlutoPostMortem pm, int entryConfidence, int strictnessAtEntry)
        {
            int a = (int)asset % Assets;
            if (!_calibrators[a].Observe(in pm, entryConfidence, strictnessAtEntry)) return false;
            if (regimeAtEntry is >= 1 and <= Regimes) _calibrators[regimeAtEntry * Assets + a].Observe(in pm, entryConfidence, strictnessAtEntry);
            return true;
        }

        /// <summary>Track B: 새 표본 ≥ RecalibrateEvery 셀만 제안 → 섀도우 → 통과 셀 원자 핫스왑(δ=0). 반환 = 핫스왑 셀 수.</summary>
        public int Recalibrate()
        {
            int swapped = 0;
            lock (_learnGate)
            {
                for (int i = 0; i < Cells; i++)
                {
                    long observed = _calibrators[i].Observed;
                    if (observed - _calibratedAt[i] < RecalibrateEvery) continue;
                    _calibratedAt[i] = observed;
                    if (Calibrate(i, _calibrators[i])) swapped++;
                }
            }
            return swapped;
        }

        /// <summary>
        /// 웜스타트: 가상 리플레이 표본을 담은 임시 보정기로 셀 1개 제안 → 섀도우 → 통과 시 핫스왑(콜드). 실 에피소드 · 슬롯 보정기 표본 = 불변.
        /// </summary>
        public bool ApplyReplay(PlutoAssetClass asset, int regimeId, PlutoWeightCalibrator replay, out PlutoShadowVerdict verdict)
        {
            ArgumentNullException.ThrowIfNull(replay);
            int cell = CellOf(asset, regimeId);
            lock (_learnGate)
            {
                bool swapped = Calibrate(cell, replay);
                verdict = _lastVerdict[cell];
                if (swapped) Interlocked.Increment(ref _warmStarts[cell]);
                return swapped;
            }
        }

        private bool Calibrate(int cell, PlutoWeightCalibrator calibrator)
        {
            var seq = _cells[cell];
            seq.TryRead(out var current);
            if (!calibrator.Propose(in current, out var candidate)) { _lastVerdict[cell] = default; return false; }
            var verdict = calibrator.ShadowValidate(in current, ref candidate);
            _lastVerdict[cell] = verdict;
            if (verdict.Reason == PlutoShadowReason.NoChange) return false;
            if (seq.TrySwap(in candidate, verdict.Passed)) { Interlocked.Increment(ref _swaps); return true; }
            Interlocked.Increment(ref _rejected);
            return false;
        }

        /// <summary>셀 현재 가중치의 '진입 당시 엄격도' (웜스타트 표본 기록용 · 콜드).</summary>
        public int StrictnessOf(PlutoAssetClass asset, int regimeId) => TryRead(asset, regimeId, out var w) ? w.DevilStrictnessPts : 0;

        /// <summary>셀 평생 에피소드 = 복원 기준 + 이번 기동 관측(합성 리플레이 제외).</summary>
        public long LifetimeEpisodes(int cell) => Interlocked.Read(ref _baseEpisodes[cell]) + _calibrators[cell].Observed;

        public PlutoShadowVerdict LastVerdict(int cell) { lock (_learnGate) return _lastVerdict[cell]; }

        /// <summary>셀 상태 스냅샷(콜드).</summary>
        public PlutoTensorCell Cell(int cell)
        {
            var (regime, asset) = KeyOf(cell);
            _cells[cell].TryRead(out var w);
            long lifetime = LifetimeEpisodes(cell);
            var maturity = w.Generation > 0 ? PlutoSlotMaturity.Calibrated
                : lifetime == 0 ? PlutoSlotMaturity.Untrained
                : lifetime < PlutoWeightCalibrator.MinSamples ? PlutoSlotMaturity.Accumulating
                : PlutoSlotMaturity.Calibratable;
            return new PlutoTensorCell(regime, asset, w, lifetime, _calibrators[cell].Observed, Interlocked.Read(ref _warmStarts[cell]), maturity);
        }

        /// <summary>국면 슬롯 보정 완료 수(0~36 · 기준선 제외).</summary>
        public int CalibratedSlots
        {
            get
            {
                int n = 0;
                for (int i = Assets; i < Cells; i++) if (_cells[i].TryRead(out var w) && w.Generation > 0) n++;
                return n;
            }
        }

        // ── 영속(PlutoCheckpointManager 잠금 하 콜드 호출) ─────────────────────

        /// <summary>
        /// 기동 복원: 행 = 슬롯별 최신 순(LoadSlots). 슬롯마다 첫 무결 행 = 골든(손상 = 같은 슬롯 이전 행 롤백). 반환 = 복원 셀 수 · skipped = 건너뛴 손상 행.
        /// </summary>
        public int Restore(IReadOnlyList<PlutoLearningCheckpoint> rows, out int skipped)
        {
            ArgumentNullException.ThrowIfNull(rows);
            skipped = 0;
            Span<bool> done = stackalloc bool[Cells];
            int restored = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var cp = rows[i];
                if (!cp.IsIntact || !cp.IsSlot) { skipped++; continue; }
                int cell = CellOf((PlutoAssetClass)cp.AssetClass, cp.RegimeId);
                if (done[cell]) continue;
                done[cell] = true;
                if (!_cells[cell].TryRestore(in cp.Weights)) continue;            // 이미 학습된 셀 = 덮어쓰기 0
                Interlocked.Exchange(ref _baseEpisodes[cell], cp.Episodes);
                _lastSaved[cell] = cp;
                restored++;
            }
            return restored;
        }

        /// <summary>셀이 마지막 기록과 다르면 봉인 슬롯 행 생성(now = 지연 시계 1회 판독). 미학습 · 무변화 · 판독 경합 = false.</summary>
        public bool TryDirty(int cell, out PlutoLearningCheckpoint checkpoint, ref long now, Func<long> utcNow)
        {
            checkpoint = default;
            if (!_cells[cell].TryRead(out var w)) return false;
            long episodes = LifetimeEpisodes(cell);
            if (w.Generation == 0 && episodes == 0) return false;               // 학습 흔적 0 = 기록 0
            var l = _lastSaved[cell];
            if (l.CalibratedUtcTicks > 0 && episodes == l.Episodes && w.Generation == l.Weights.Generation
                && w.MomentumTiltPct == l.Weights.MomentumTiltPct && w.DevilStrictnessPts == l.Weights.DevilStrictnessPts) return false;
            if (now == 0) now = utcNow();
            var (regime, asset) = KeyOf(cell);
            checkpoint = PlutoLearningCheckpoint.SealSlot(regime, asset, episodes, in w, now);
            return true;
        }

        public void MarkSaved(int cell, in PlutoLearningCheckpoint checkpoint) => _lastSaved[cell] = checkpoint;
    }
}
