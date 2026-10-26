// ============================================================================
// PlutoCouncilHub — v12.18.88-DEV: 위원회 · 교차 자산 파급 · 프랙탈 켈리 Track B 결선 허브 + 체결 핫패스 39셀 판정 보드(seqlock δ=0)
//
//   스크리너 Step 마다 Prepare 1회(콜드): ① 데이터 레이크 → 파급 레인 공급 · 거시 피드 10년물 → 파급 신호 게시 ② 거시(VIX · 12대 국면 판정) 판독 →
//     PlutoCouncilMacro ③ 시장별 PlutoCouncilInputs(국면 · 자산 해석 가중치 · 셀 실측 엣지 · ATR 공급자) → PlutoRecommendationEngine.Rank 에 전달.
//   PlutoCouncilBoard(39셀 · 셀당 seqlock): Publish = 셀 합의 가중치 · 켈리 권장 비중(ATR 미반영 기준값) · 비토 사유 · KRX 선행 점수 →
//     TryRead(자산, 국면) = 체결 핫패스 조회(δ=0 · 잠금 0 · 경합 상한 초과 = false/default).
//   ★실측 없는 입력 = 해당 항목 미산출(켈리 Insufficient · 파급 0 · VIX 미판독 = VIX 비토 미발동) — 가짜 값 0. ★liveArmed:false · QuantumVault/피어 0.
// ============================================================================
using System;
using System.Threading;
using StockManager.Engines.Cognitive.Learning;
using StockManager.Engines.Cognitive.Screening;

namespace StockManager.Engines.Cognitive.Council
{
    /// <summary>시장 1개 위원회 입력(값 타입 · Enabled false = 위원회 미결선 → 기존 랭킹 동작 그대로).</summary>
    public readonly struct PlutoCouncilInputs
    {
        public readonly bool Enabled;
        public readonly PlutoCouncilMacro Macro;
        public readonly int RegimeId;
        public readonly PlutoCognitiveWeights Learned;
        public readonly PlutoEdgeStats Edge;
        public readonly bool EdgeWired, EdgeFromRegime;
        public readonly Func<long, int>? AtrOf;

        public PlutoCouncilInputs(in PlutoCouncilMacro macro, int regimeId, in PlutoCognitiveWeights learned, in PlutoEdgeStats edge, bool edgeWired, bool edgeFromRegime,
            Func<long, int>? atrOf)
        {
            Enabled = true; Macro = macro; RegimeId = regimeId; Learned = learned; Edge = edge; EdgeWired = edgeWired; EdgeFromRegime = edgeFromRegime; AtrOf = atrOf;
        }

        /// <summary>종목 켈리(콜드 · ATR 공급자 없음/미판독 = 배율 100%). 엣지 미결선 = Unwired.</summary>
        public PlutoKellyResult KellyOf(long symbol)
        {
            if (!EdgeWired) return new PlutoKellyResult(PlutoKellyState.Unwired, 0, 0, 0, 0, 100, 0);
            int atr = 0;
            try { atr = AtrOf?.Invoke(symbol) ?? 0; } catch (InvalidOperationException) { atr = 0; }
            return PlutoDynamicKellyEngine.Compute(in Edge, atr);
        }
    }

    /// <summary>39셀 1칸 게시값(값 타입).</summary>
    public readonly struct PlutoCouncilCell
    {
        public readonly PlutoCouncilWeights Weights;
        public readonly PlutoKellyResult Kelly;
        public readonly int LeadPts;
        public readonly PlutoVetoReason Veto;
        public readonly long Seq;

        public PlutoCouncilCell(in PlutoCouncilWeights weights, in PlutoKellyResult kelly, int leadPts, PlutoVetoReason veto, long seq)
        {
            Weights = weights; Kelly = kelly; LeadPts = leadPts; Veto = veto; Seq = seq;
        }

        public PlutoKellyState KellyState => Kelly.State;
        public int KellyBp => Kelly.WeightBp;

        public bool Vetoed => Veto != PlutoVetoReason.None;
    }

    /// <summary>체결 핫패스 판정 보드(39셀 · 셀당 버전 브래킷 seqlock · 단일 작성자 Track B).</summary>
    public sealed class PlutoCouncilBoard
    {
        public const int Cells = PlutoCognitiveCouncil.Cells, MaxReadSpins = 1024;

        private readonly long[] _ver = new long[Cells];
        private readonly PlutoCouncilCell[] _cells = new PlutoCouncilCell[Cells];
        private long _published, _contended;

        public long Published => Interlocked.Read(ref _published);
        public long ContendedReads => Interlocked.Read(ref _contended);

        public void Publish(int cell, in PlutoCouncilCell value)
        {
            if ((uint)cell >= Cells) throw new ArgumentOutOfRangeException(nameof(cell));
            var spin = new SpinWait();
            long v;
            while (true)
            {
                v = Volatile.Read(ref _ver[cell]);
                if ((v & 1) == 0 && Interlocked.CompareExchange(ref _ver[cell], v + 1, v) == v) break;
                spin.SpinOnce(-1);
            }
            _cells[cell] = value;
            Volatile.Write(ref _ver[cell], v + 2);
            Interlocked.Increment(ref _published);
        }

        /// <summary>체결 핫패스 조회(δ=0): 국면 1~12 = 그 행 · 그 외 = 기준선 행. 게시 전 = true · default(Seq 0).</summary>
        public bool TryRead(PlutoAssetClass asset, int regimeId, out PlutoCouncilCell cell)
        {
            int i = PlutoCognitiveTensorLake.CellOf(asset, regimeId);
            for (int k = 0; k < MaxReadSpins; k++)
            {
                long v1 = Volatile.Read(ref _ver[i]);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                cell = _cells[i];
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver[i]) == v1) return true;
            }
            cell = default;
            Interlocked.Increment(ref _contended);
            return false;
        }
    }

    public sealed class PlutoCouncilHub
    {
        private readonly PlutoCrossAssetSpillover _spillover;
        private readonly PlutoCouncilBoard _board;
        private readonly PlutoSpilloverFeeder? _feeder;
        private readonly PlutoMultiAssetDataLake? _lake;
        private readonly Func<(bool Has, PlutoMacroSnapshot Snapshot)> _macro;
        private readonly Func<PlutoRegimeMatch> _regime;
        private readonly PlutoCognitiveTensorLake? _tensor;
        private readonly PlutoOhlcvBar[] _atrBars = new PlutoOhlcvBar[PlutoMultiAssetDataLake.BarsPerSlot];
        private readonly object _atrGate = new();
        private PlutoCouncilMacro _lastMacro = PlutoCouncilMacro.None;
        private int _lastRegime;
        private long _prepared;

        /// <param name="lake">데이터 레이크(파급 레인 · ATR 원천 · null = 파급 레인/ATR 미산출).</param>
        /// <param name="macro">거시 스냅샷 공급자(Has false = 미결선 → VIX/10년물 항목 0).</param>
        /// <param name="regime">12대 국면 판독 공급자(판정 전 = default).</param>
        /// <param name="tensor">인지 텐서(셀 실측 엣지 · null = 켈리 미결선).</param>
        public PlutoCouncilHub(PlutoCrossAssetSpillover spillover, PlutoCouncilBoard board, PlutoMultiAssetDataLake? lake = null,
            Func<(bool Has, PlutoMacroSnapshot Snapshot)>? macro = null, Func<PlutoRegimeMatch>? regime = null, PlutoCognitiveTensorLake? tensor = null)
        {
            _spillover = spillover ?? throw new ArgumentNullException(nameof(spillover));
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _lake = lake;
            _feeder = lake is null ? null : new PlutoSpilloverFeeder(lake);
            _macro = macro ?? (static () => (false, default));
            _regime = regime ?? (static () => default);
            _tensor = tensor;
        }

        public PlutoCrossAssetSpillover Spillover => _spillover;
        public PlutoCouncilBoard Board => _board;
        public long Prepared => Interlocked.Read(ref _prepared);
        public PlutoCouncilMacro LastMacro => Volatile.Read(ref _prepared) == 0 ? PlutoCouncilMacro.None : _lastMacro;
        public int LastRegimeId => Volatile.Read(ref _lastRegime);

        /// <summary>스텝 1회 준비(콜드 · 스크리너 스텝 잠금 안): 파급 공급 · 게시 → 거시/국면 판독.</summary>
        public PlutoCouncilMacro Prepare(long nowUtcTicks)
        {
            _feeder?.Feed(_spillover);
            var (has, snap) = _macro();
            bool hasUs10y = has && (snap.Fields & PlutoMacroField.Us10y) != 0;
            _spillover.ObserveUs10y(hasUs10y, hasUs10y ? snap.Us10yChangeBp : 0);
            var sig = _spillover.Publish(nowUtcTicks);
            var match = _regime();
            int vix = has && (snap.Fields & PlutoMacroField.Vix) != 0 ? snap.VixX100 : -1;
            var m = new PlutoCouncilMacro(vix, match.CycleId > 0 ? match.Kind : PlutoEpisodeKind.Unknown, match.CycleId > 0 ? match.SimilarityPct : 0, in sig);
            _lastMacro = m;
            Volatile.Write(ref _lastRegime, match.CycleId is >= 1 and <= PlutoCognitiveTensorLake.Regimes ? match.CycleId : 0);
            Interlocked.Increment(ref _prepared);
            return m;
        }

        /// <summary>시장 입력(콜드): 자산 해석 가중치(호출자) · 셀 실측 엣지(텐서) · ATR 공급자(레이크).</summary>
        public PlutoCouncilInputs InputsFor(PlutoScreenMarket market, in PlutoCouncilMacro macro, in PlutoCognitiveWeights assetWeights)
        {
            var asset = PlutoRecommendationEngine.AssetOf(market);
            int regime = LastRegimeId;
            PlutoEdgeStats edge = default;
            bool fromRegime = false;
            if (_tensor is not null) edge = _tensor.EdgeOf(asset, regime, out fromRegime);
            Func<long, int>? atr = _lake is null ? null : symbol => AtrOf(market, symbol);
            return new PlutoCouncilInputs(in macro, regime, in assetWeights, in edge, _tensor is not null, fromRegime, atr);
        }

        /// <summary>종목 ATR(cbp · 레이크 봉 · 미판독 = 0).</summary>
        public int AtrOf(PlutoScreenMarket market, long symbol)
        {
            if (_lake is null || !_lake.Layout.TryFindSlot(market, symbol, out int slot)) return 0;
            lock (_atrGate)
            {
                int n = _lake.TryCopyBars(slot, _atrBars, out _);
                return n > 1 ? PlutoDynamicKellyEngine.AtrCbp(_atrBars.AsSpan(0, n)) : 0;
            }
        }

        /// <summary>스텝 결과 → 39셀 보드 게시(콜드): 셀마다 합의 가중치 · 켈리(ATR 미반영) · 거시 비토 · KRX 선행 점수.</summary>
        public void PublishBoard(in PlutoCouncilMacro macro, Func<PlutoAssetClass, PlutoCognitiveWeights> globalByAsset)
        {
            ArgumentNullException.ThrowIfNull(globalByAsset);
            var veto = PlutoCognitiveCouncil.VetoOf(default, in macro);
            long seq = macro.Spillover.Seq;
            for (int cell = 0; cell < PlutoCouncilBoard.Cells; cell++)
            {
                var (regimeId, asset) = PlutoCognitiveTensorLake.KeyOf(cell);
                int rid = regimeId < 0 ? 0 : regimeId;
                var learned = globalByAsset(asset);
                _tensor?.Resolve(asset, rid, ref learned);
                var w = PlutoCognitiveCouncil.Weights(asset, rid, in learned);
                var k = _tensor is null ? new PlutoKellyResult(PlutoKellyState.Unwired, 0, 0, 0, 0, 100, 0)
                    : PlutoDynamicKellyEngine.Compute(_tensor.EdgeOf(asset, rid, out _), 0);
                int lead = asset == PlutoAssetClass.Krx ? macro.Spillover.LeadPts : 0;
                _board.Publish(cell, new PlutoCouncilCell(in w, in k, lead, veto, seq));
            }
        }
    }
}
