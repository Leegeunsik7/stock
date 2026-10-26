// ============================================================================
// ArenaCouncilVetoGate — v12.18.88-GM: 아레나 AI 진입 경로 × PlutoCognitiveCouncil 리스크 통제관(ChiefRiskOfficer) 비토 결선(3대 아레나 공용 · δ=0)
//
//   AI 진입 후보(원점수 ≥ 진입 임계) 1건마다 Blocks 1회 — 결속 보드(PlutoCouncilBoard · Track B 스크리너가 39셀 게시) 를 (자산군, 12대 국면) 셀로 seqlock 조회 →
//     셀 비토(VIX ≥ 35 · 교차 자산 충격 · 과거 패닉 국면 유사) | 종목 국면 PanicLiquidation(패닉 투매) → 하나라도 = 진입 차단(거버너 · 주문 0).
//     보드 경합 상한 초과(판독 불가) = Unreadable 비토(fail-closed · 진입 차단).
//   기각 로그(체결 타임라인): 종목별 직전 비토 사유와 달라질 때만 고정 링(64건)에 ArenaVetoRecord 1건(같은 사유 연속 = 계수만 증가 · 링 범람 0) ·
//     비토 해제 = 종목 사유 초기화(재발동 = 새 기록). 차단 계수 Blocked = 전 차단 건수(중복 포함).
//   ★단일 작성자: Blocks/Reset 은 엔진 정산 스레드의 seqlock 쓰기 창 안에서만 · Copy/계수 판독은 엔진 seqlock 판독 루프 안에서만(같은 버전 검증).
//   ★δ=0: 고정 배열 · 값 타입 · 보드 TryRead 무할당(LINQ/박싱/클로저/문자열 0). 미결속 = 기존 진입 동작 동일.
//   ★liveArmed:false — 가상 원장 진입 차단 판정뿐(주문 · 무장 경로 0) · QuantumVault/피어 0.
// ============================================================================
using System;
using System.Threading;
using StockManager.Engines.Cognitive.Council;
using StockManager.Engines.Cognitive.Learning;
using MarketRegime = StockManager.Engines.Cognitive.Regime.MarketRegime;   // MS2E.Core.MarketRegime(전역 using) 동명 타입과 구분

namespace StockManager.Engines.Arena.Common
{
    /// <summary>비토 기각 1건(값 타입 · Score = 기각된 진입 원점수 bp · Cell = 39셀 판정 보드 칸).</summary>
    public readonly struct ArenaVetoRecord
    {
        public readonly long Seq, Symbol, KstTicks, BoardSeq;
        public readonly int Score, Cell;
        public readonly PlutoVetoReason Veto;

        public ArenaVetoRecord(long seq, long symbol, long kstTicks, int score, PlutoVetoReason veto, int cell, long boardSeq)
        {
            Seq = seq; Symbol = symbol; KstTicks = kstTicks; Score = score; Veto = veto; Cell = cell; BoardSeq = boardSeq;
        }

        public DateTime AtKst => new(KstTicks, DateTimeKind.Unspecified);
        public bool IsWellFormed => Seq > 0 && Symbol > 0 && KstTicks > 0 && Veto != PlutoVetoReason.None && (uint)Cell < PlutoCouncilBoard.Cells;
    }

    public sealed class ArenaCouncilVetoGate
    {
        public const int Capacity = 64;

        private readonly PlutoAssetClass _asset;
        private readonly PlutoVetoReason[] _last;
        private readonly ArenaVetoRecord[] _ring = new ArenaVetoRecord[Capacity];
        private PlutoCouncilBoard? _board;
        private long _count, _blocked;

        public ArenaCouncilVetoGate(PlutoAssetClass asset, int maxSymbols)
        {
            if (maxSymbols <= 0) throw new ArgumentOutOfRangeException(nameof(maxSymbols));
            _asset = asset;
            _last = new PlutoVetoReason[maxSymbols];
        }

        public void Attach(PlutoCouncilBoard board) => Volatile.Write(ref _board, board ?? throw new ArgumentNullException(nameof(board)));

        public bool Attached => Volatile.Read(ref _board) is not null;

        /// <summary>기록 건수 · 차단 건수(엔진 seqlock 판독 루프 안에서만).</summary>
        public long Count => _count;
        public long Blocked => _blocked;

        /// <summary>진입 후보 비토 판정(쓰기 창 · δ=0): true = 진입 차단(사유 전이 시 기각 로그 1건). 미결속 = false(기존 동작).</summary>
        public bool Blocks(int si, long symbol, MarketRegime symbolRegime, int regimeId, int score, long kstTicks)
        {
            var board = Volatile.Read(ref _board);
            if (board is null) return false;
            var veto = PlutoVetoReason.None;
            int cell = PlutoCognitiveTensorLake.CellOf(_asset, regimeId);
            long boardSeq = 0;
            if (board.TryRead(_asset, regimeId, out var c)) { veto = c.Veto; boardSeq = c.Seq; }
            else veto = PlutoVetoReason.Unreadable;
            if (symbolRegime == MarketRegime.PanicLiquidation) veto |= PlutoVetoReason.PanicLiquidation;
            bool tracked = (uint)si < (uint)_last.Length;
            if (veto == PlutoVetoReason.None)
            {
                if (tracked) _last[si] = PlutoVetoReason.None;
                return false;
            }
            _blocked++;
            if (!tracked || _last[si] != veto)
            {
                if (tracked) _last[si] = veto;
                _count++;
                _ring[(_count - 1) % Capacity] = new ArenaVetoRecord(_count, symbol, kstTicks, score, veto, cell, boardSeq);
            }
            return true;
        }

        /// <summary>원장 초기화 동반(쓰기 창 · 결속 유지).</summary>
        public void Reset()
        {
            Array.Clear(_ring);
            Array.Clear(_last);
            _count = _blocked = 0;
        }

        /// <summary>최근 기각 로그(과거 → 최신 · 엔진 seqlock 판독 루프 안에서만).</summary>
        public int Copy(Span<ArenaVetoRecord> destination)
        {
            long total = _count;
            int n = (int)Math.Min(Math.Min(total, Capacity), destination.Length);
            long first = total - n;
            for (int k = 0; k < n; k++) destination[k] = _ring[(first + k) % Capacity];
            return n;
        }
    }
}
