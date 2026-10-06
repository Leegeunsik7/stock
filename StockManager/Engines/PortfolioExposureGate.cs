// ============================================================================
// PortfolioExposureGate — [포트폴리오 리스크 · 추천 A] 단일 종목 · 섹터 편중 익스포저 상한 송출 전 거부(δ=0 · Fail-Closed)
//
//  ★수정 전 공백: 계좌 게이트(AccountStateSyncAdapter)는 주문가능금액·보유수량만 보고, VaR 엔진(PortfolioRiskVaREngine)은
//    포트폴리오 전체 손실 분포만 본다 — "한 종목에 총자산의 몇 %가 몰렸나 / 한 섹터에 몇 %가 몰렸나"를 송출 시점에 막는 경로가 0.
//  · 총자산(equity) = 예수금(OPW00001 Deposit) + Σ 보유수량 × 마크가(최근 체결가 · 원).
//  · 신규 매수 1건 승인 조건(정수 bps · Int128 곱):
//      (해당 종목 보유 평가액 + 승인 대기 매수 명목 + 이번 명목) × 10,000 ≤ 단일 종목 상한(bps) × equity
//      (해당 섹터 보유 평가액 합 + 섹터 승인 대기 합 + 이번 명목) × 10,000 ≤ 섹터 상한(bps) × equity
//  · 매도는 익스포저를 줄이므로 항상 통과(계좌 게이트가 보유수량 초과 매도를 별도 차단).
//  · Fail-Closed: 예수금 미수신 · 보유 종목의 마크가 미상 · 보유 스냅샷 잘림 → 거부(모르면 막는다).
//  · 섹터표: 코드 → 섹터 ID(1..255). 미등재 = 0(미분류) — 미분류 종목끼리도 하나의 버킷으로 합산 상한(보수적).
//    IngestSectorTable(ReadOnlySpan<byte>) = "005930,1\n000660,1\n" UTF-8 바이트를 Utf8Parser 로 무할당 파싱.
//  · 승인 대기(pending): 승인 후 체결 전 매수 명목을 종목별로 누적 — 체결(OnFill)로 감소, 미송출(ReleasePending)로 반환.
//    ★정직 한계: 취소/거부된 매수의 대기분은 자동 반환하지 않는다(과대 = 안전 측) — ResetPending(일일/수동) 필요.
//  δ=0: 고정 배열 · 선형 탐색(보유 수십~수백 종목 규모) · 락 1개(경합 없는 Monitor 할당 0).
// ============================================================================
using System;
using System.Buffers.Text;
using System.Threading;
using MS2E.Infrastructure;
using StockManager.Adapters;

namespace StockManager.Engines
{
    public enum ExposureVerdict : byte { Ok = 0, Disabled = 1, EquityUnknown = 2, MarkUnknown = 3, SnapshotTruncated = 4, SingleNameExceeded = 5, SectorExceeded = 6 }

    public sealed class PortfolioExposureGate
    {
        public const long DefaultSingleNameCapBps = 2_000;   // 총자산의 20%
        public const long DefaultSectorCapBps = 4_000;       // 총자산의 40%

        private readonly AccountStateSyncAdapter _account;
        private readonly object _gate = new();
        private readonly long _singleCapBps, _sectorCapBps;
        // 마크가 · 섹터 · 승인 대기(종목 키 공용 표)
        private readonly long[] _code, _markWon, _pending;
        private readonly byte[] _sector;
        private int _count;
        // 보유 스냅샷 작업 버퍼(사전 할당)
        private readonly long[] _heldSym, _heldQty;
        private long _admits, _rejectSingle, _rejectSector, _rejectUnknown, _tableFull;

        /// <param name="singleNameCapBps">단일 종목 상한(bps · 0 = 게이트 비활성).</param>
        /// <param name="sectorCapBps">섹터 상한(bps · 0 = 섹터 검사 생략).</param>
        public PortfolioExposureGate(AccountStateSyncAdapter account, long singleNameCapBps = DefaultSingleNameCapBps,
                                     long sectorCapBps = DefaultSectorCapBps, int capacity = 512)
        {
            _account = account ?? throw new ArgumentNullException(nameof(account));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (singleNameCapBps < 0 || singleNameCapBps > 10_000) throw new ArgumentOutOfRangeException(nameof(singleNameCapBps));
            if (sectorCapBps < 0 || sectorCapBps > 10_000) throw new ArgumentOutOfRangeException(nameof(sectorCapBps));
            _singleCapBps = singleNameCapBps; _sectorCapBps = sectorCapBps;
            _code = new long[capacity]; _markWon = new long[capacity]; _pending = new long[capacity]; _sector = new byte[capacity];
            _heldSym = new long[capacity]; _heldQty = new long[capacity];
        }

        public bool Enabled => _singleCapBps > 0;
        public long SingleNameCapBps => _singleCapBps;
        public long SectorCapBps => _sectorCapBps;
        public long Admits => Interlocked.Read(ref _admits);
        public long RejectSingle => Interlocked.Read(ref _rejectSingle);
        public long RejectSector => Interlocked.Read(ref _rejectSector);
        public long RejectUnknown => Interlocked.Read(ref _rejectUnknown);
        public long TableFull => Interlocked.Read(ref _tableFull);

        // ── 입력(δ=0) ──

        /// <summary>마크가(최근 체결가 · 원) 갱신 — 틱 경로에서 호출.</summary>
        public void OnMark(long symbolCode, long priceWon)
        {
            if (priceWon <= 0) return;
            lock (_gate) { int i = Slot(symbolCode, create: true); if (i >= 0) _markWon[i] = priceWon; }
        }

        public bool SetSector(long symbolCode, int sectorId)
        {
            if (sectorId < 0 || sectorId > 255) return false;
            lock (_gate) { int i = Slot(symbolCode, create: true); if (i < 0) return false; _sector[i] = (byte)sectorId; return true; }
        }

        /// <summary>섹터표 UTF-8 바이트("코드,섹터\n" 반복 · '#' 주석 · CR 허용) 무할당 파싱. 반환 = 반영 행 수(형식 오류 행은 건너뜀).</summary>
        public int IngestSectorTable(ReadOnlySpan<byte> utf8)
        {
            int applied = 0;
            while (!utf8.IsEmpty)
            {
                int nl = utf8.IndexOf((byte)'\n');
                var line = nl < 0 ? utf8 : utf8[..nl];
                utf8 = nl < 0 ? ReadOnlySpan<byte>.Empty : utf8[(nl + 1)..];
                if (!line.IsEmpty && line[^1] == (byte)'\r') line = line[..^1];
                if (line.IsEmpty || line[0] == (byte)'#') continue;
                int comma = line.IndexOf((byte)',');
                if (comma <= 0) continue;
                var codePart = line[..comma];
                if (!codePart.IsEmpty && (codePart[0] == (byte)'A' || codePart[0] == (byte)'a')) codePart = codePart[1..];
                if (Utf8Parser.TryParse(codePart, out long code, out int c1) && c1 == codePart.Length && code > 0 && code <= 999_999
                    && Utf8Parser.TryParse(line[(comma + 1)..], out int sector, out int c2) && c2 == line.Length - comma - 1
                    && SetSector(code, sector))
                    applied++;
            }
            return applied;
        }

        /// <summary>매수 체결 → 승인 대기 감소(체결 명목만큼 · 0 하한).</summary>
        public void OnFill(FillPayload fill)
        {
            if (fill.FillQty <= 0 || fill.Side <= 0 || !(fill.FillPrice > 0)) return;
            long notional = (long)Math.Min(long.MaxValue / 2, (double)fill.FillQty * fill.FillPrice);
            lock (_gate) { int i = Slot(fill.SymbolCode, create: false); if (i >= 0) _pending[i] = Math.Max(0, _pending[i] - notional); }
        }

        public void ReleasePending(long symbolCode, long notionalWon)
        {
            if (notionalWon <= 0) return;
            lock (_gate) { int i = Slot(symbolCode, create: false); if (i >= 0) _pending[i] = Math.Max(0, _pending[i] - notionalWon); }
        }

        public void ResetPending()
        {
            lock (_gate) Array.Clear(_pending, 0, _count);
        }

        public long PendingOf(long symbolCode)
        {
            lock (_gate) { int i = Slot(symbolCode, create: false); return i >= 0 ? _pending[i] : 0; }
        }

        // ── 송출 게이트(δ=0) ──

        /// <summary>신규 매수 승인 판정 — Ok 이면 승인 대기에 명목 누적.</summary>
        public ExposureVerdict TryAdmitBuy(long symbolCode, long notionalWon)
        {
            if (!Enabled) return ExposureVerdict.Disabled;
            if (notionalWon <= 0) return Reject(ExposureVerdict.EquityUnknown, ref _rejectUnknown);
            lock (_gate)
            {
                int n = _account.CopyPositions(_heldSym, _heldQty);
                if (n < 0) return Reject(ExposureVerdict.SnapshotTruncated, ref _rejectUnknown);
                long deposit = _account.Deposit;
                if (deposit <= 0 && n == 0) return Reject(ExposureVerdict.EquityUnknown, ref _rejectUnknown);
                int target = Slot(symbolCode, create: true);
                if (target < 0) return Reject(ExposureVerdict.MarkUnknown, ref _rejectUnknown);
                int targetSector = _sector[target];
                Int128 equity = Math.Max(0, deposit), symbolExp = notionalWon + (Int128)_pending[target], sectorExp = notionalWon;
                for (int k = 0; k < n; k++)
                {
                    int s = Slot(_heldSym[k], create: false);
                    if (s < 0 || _markWon[s] <= 0) return Reject(ExposureVerdict.MarkUnknown, ref _rejectUnknown);   // 보유 종목 평가 불가 = 총자산 미상
                    Int128 value = (Int128)_heldQty[k] * _markWon[s];
                    if (value < 0) value = 0;                                   // 음수 추정 보유(동기화 전 매도) — 평가 0
                    equity += value;
                    if (s == target) symbolExp += value;
                    if (_sector[s] == targetSector) sectorExp += value;
                }
                // 섹터 승인 대기(보유 여부 무관 · 같은 섹터 종목의 대기 명목)
                for (int s = 0; s < _count; s++)
                    if (s != target && _sector[s] == targetSector) sectorExp += _pending[s];
                sectorExp += _pending[target];
                if (equity <= 0) return Reject(ExposureVerdict.EquityUnknown, ref _rejectUnknown);
                if (symbolExp * 10_000 > (Int128)_singleCapBps * equity) return Reject(ExposureVerdict.SingleNameExceeded, ref _rejectSingle);
                if (_sectorCapBps > 0 && sectorExp * 10_000 > (Int128)_sectorCapBps * equity) return Reject(ExposureVerdict.SectorExceeded, ref _rejectSector);
                _pending[target] = (long)Int128.Min((Int128)long.MaxValue / 2, (Int128)_pending[target] + notionalWon);
            }
            Interlocked.Increment(ref _admits);
            return ExposureVerdict.Ok;
        }

        // ── 내부 ──

        private static ExposureVerdict Reject(ExposureVerdict v, ref long counter) { Interlocked.Increment(ref counter); return v; }

        private int Slot(long symbolCode, bool create)
        {
            for (int i = 0; i < _count; i++) if (_code[i] == symbolCode) return i;
            if (!create) return -1;
            if (_count == _code.Length) { Interlocked.Increment(ref _tableFull); return -1; }
            _code[_count] = symbolCode; _markWon[_count] = 0; _pending[_count] = 0; _sector[_count] = 0;
            return _count++;
        }
    }
}
