// ============================================================================
// ArenaExecutionQualityTracker — [v12.18.25-DEV] 아레나 체결 품질 포렌식(가격 기반 MAE/MFE · 거래 기록 · 손익비 · δ=0)
//
//  ArenaTradeAnalytics(v12.18.14)는 '원' 편차를 시가평가 주기(정산 50ms)마다 표본한다. 이 추적기는 호가 갱신(틱)마다 '가격'으로 추적한다:
//   · 표시가(mark) = 롱 → 최우선 매수호가(팔 수 있는 값) · 숏 → 최우선 매도호가(되살 값). 진입 체결가도 첫 표본.
//   · 롱: MAE = 평균 진입가 − 최저 표시가 · MFE = 최고 표시가 − 평균 진입가. 숏: 대칭(MAE = 최고 − 진입 · MFE = 진입 − 최저). 주당 원 · bp(진입가 대비).
//   · trip = 무포지션 → 롱/숏 → 무포지션. 추가 진입 = 평균 진입가 갱신 · 축소 = 평균 청산가 누적. 0 교차는 원장이 금지(한 주문 = 한 방향).
//   · 청산 확정 시 TradeExecutionRecord 1건(고정 링 256 · 덮어씀) — 진입/청산 평균가 · MAE/MFE · 보유 시간 · Gross(마찰 전) · Net(수수료 · 세금 후) · 수수료 · 세금.
//     Gross = Net + 이 trip 의 수수료 + 거래세(항등식 · 반올림 재계산 없음 — 원장이 체결마다 절사한 값 그대로 합산).
//   · 요약: 손익비(Σ 이익 ÷ |Σ 손실| · Net 기준) · 평균 MAE/MFE bp · 청산 효율(실현 가격 이동 ÷ MFE) · MAE 대비 실현(Σ Net ÷ Σ MAE 원).
//  ★스레드: 원장과 같은 단일 작성자(거버너 정산 스레드). 외부 판독은 거버너 seqlock 사본(ArenaDebrief)으로만 — 잠금 0 · 원자성은 seqlock 창이 보장.
//  ★고정 배열 · 정수 연산(Int128 중간값)만 — 할당 0. 브로커/주문 송출 형식 참조 0(IL 허용목록 테스트).
// ============================================================================
using System;

namespace StockManager.Engines.Arena
{
    /// <summary>청산 확정된 왕복 거래 1건(원 · 주 · bp · UTC ticks). Side +1 롱 · −1 숏.</summary>
    public struct TradeExecutionRecord
    {
        public long Symbol, Qty, EntryPx, ExitPx, MaePx, MfePx, MaeBp, MfeBp, MaeWon, MfeWon, OpenTicks, CloseTicks, GrossWon, NetWon, FeesWon, TaxWon;
        public int Side;
        public readonly long HoldTicks => CloseTicks - OpenTicks;
        /// <summary>청산 효율(bp) = 실현 가격 이동 ÷ MFE(주당) · MFE 0 = 0.</summary>
        public readonly long ExitEfficiencyBp => MfePx <= 0 ? 0 : (long)((Int128)((ExitPx - EntryPx) * Side) * 10_000 / MfePx);
    }

    /// <summary>요약(δ=0 계산 · 값 사본).</summary>
    public readonly struct ExecutionQualitySummary
    {
        public readonly int Trips, Wins, Losses, EfficiencySamples;
        public readonly long GrossProfitWon, GrossLossWon, NetWon, GrossWon, FeesWon, TaxWon, SumMaeBp, SumMfeBp, SumEfficiencyBp, SumMaeWon;

        public ExecutionQualitySummary(int trips, int wins, int losses, int effSamples, long grossProfit, long grossLoss, long net, long gross, long fees, long tax,
                                       long sumMaeBp, long sumMfeBp, long sumEffBp, long sumMaeWon)
        {
            Trips = trips; Wins = wins; Losses = losses; EfficiencySamples = effSamples; GrossProfitWon = grossProfit; GrossLossWon = grossLoss;
            NetWon = net; GrossWon = gross; FeesWon = fees; TaxWon = tax; SumMaeBp = sumMaeBp; SumMfeBp = sumMfeBp; SumEfficiencyBp = sumEffBp; SumMaeWon = sumMaeWon;
        }

        /// <summary>손익비(bp · 10000 = 1.00) — 손실 0 · 이익 &gt; 0 = −1(무한) · 거래 0 = 0.</summary>
        public long ProfitFactorBp => GrossLossWon < 0 ? (long)((Int128)GrossProfitWon * 10_000 / -GrossLossWon) : GrossProfitWon > 0 ? -1 : 0;
        public long AvgMaeBp => Trips == 0 ? 0 : SumMaeBp / Trips;
        public long AvgMfeBp => Trips == 0 ? 0 : SumMfeBp / Trips;
        public long AvgExitEfficiencyBp => EfficiencySamples == 0 ? 0 : SumEfficiencyBp / EfficiencySamples;
        /// <summary>MAE 대비 실현(bp) = Σ Net ÷ Σ MAE(원) — 감수한 역행 1원당 얻은 순익(청산 적시성). MAE 합 0 = 0.</summary>
        public long MaeToRealizedBp => SumMaeWon <= 0 ? 0 : (long)((Int128)NetWon * 10_000 / SumMaeWon);
        public long WinRateBp => Trips == 0 ? 0 : (long)Wins * 10_000 / Trips;
    }

    public sealed class ArenaExecutionQualityTracker
    {
        public const int Slots = ArenaBook.MaxPositions, RecordCapacity = 256;

        private readonly long[] _sym = new long[Slots], _open = new long[Slots], _inQty = new long[Slots], _inNotional = new long[Slots],
                                _outQty = new long[Slots], _outNotional = new long[Slots], _worst = new long[Slots], _best = new long[Slots],
                                _fees = new long[Slots], _tax = new long[Slots], _net = new long[Slots];
        private readonly int[] _side = new int[Slots];
        private int _n;
        private readonly TradeExecutionRecord[] _ring = new TradeExecutionRecord[RecordCapacity];
        private long _recorded;
        private int _trips, _wins, _losses, _effN;
        private long _gp, _gl, _netSum, _grossSum, _feeSum, _taxSum, _maeBpSum, _mfeBpSum, _effSum, _maeWonSum, _marks;

        public int OpenTrips => _n;
        public long Recorded => _recorded;
        /// <summary>표시가 갱신 누계(진단 · 틱 단위 추적 증거).</summary>
        public long Marks => _marks;

        public void Reset()
        {
            Array.Clear(_sym); Array.Clear(_open); Array.Clear(_inQty); Array.Clear(_inNotional); Array.Clear(_outQty); Array.Clear(_outNotional);
            Array.Clear(_worst); Array.Clear(_best); Array.Clear(_fees); Array.Clear(_tax); Array.Clear(_net); Array.Clear(_side); Array.Clear(_ring);
            _n = 0; _recorded = 0; _trips = _wins = _losses = _effN = 0;
            _gp = _gl = _netSum = _grossSum = _feeSum = _taxSum = _maeBpSum = _mfeBpSum = _effSum = _maeWonSum = _marks = 0;
        }

        private int IndexOf(long symbol)
        {
            for (int i = 0; i < _n; i++) if (_sym[i] == symbol) return i;
            return -1;
        }

        /// <summary>호가 갱신(틱) — 그 종목 진행 중 trip 의 최저/최고 표시가 갱신(롱 = 매수 1호가 · 숏 = 매도 1호가 · 0 이하 = 무시).</summary>
        public void OnMark(long symbol, long bestBid, long bestAsk)
        {
            int i = IndexOf(symbol);
            if (i < 0) return;
            long m = _side[i] > 0 ? bestBid : bestAsk;
            if (m <= 0) return;
            _marks++;
            if (_side[i] > 0) { if (m < _worst[i]) _worst[i] = m; if (m > _best[i]) _best[i] = m; }
            else { if (m > _worst[i]) _worst[i] = m; if (m < _best[i]) _best[i] = m; }
        }

        /// <summary>
        /// 체결 1건(원장 Fill 직후 · 같은 스레드). posBefore/After = 체결 전후 보유(숏 음수) · notional = 체결 대금 · fee/tax = 이 체결의 절사 수수료/거래세 ·
        /// netRealized = 이 체결의 원장 실현손익(축소 체결만 ≠ 0).
        /// </summary>
        public void OnFill(long symbol, long posBefore, long posAfter, long qty, long notional, long fee, long tax, long netRealized, long nowTicks)
        {
            if (qty <= 0) return;
            int i = IndexOf(symbol);
            if (posBefore == 0 && posAfter != 0)
            {
                if (i < 0) { if (_n >= Slots) return; i = _n++; }
                long px = notional / qty;
                _sym[i] = symbol; _side[i] = posAfter > 0 ? 1 : -1; _open[i] = nowTicks;
                _inQty[i] = qty; _inNotional[i] = notional; _outQty[i] = 0; _outNotional[i] = 0;
                _worst[i] = px; _best[i] = px;
                _fees[i] = fee; _tax[i] = tax; _net[i] = 0;
                return;
            }
            if (i < 0) return;
            _fees[i] += fee; _tax[i] += tax;
            if (Math.Abs(posAfter) > Math.Abs(posBefore)) { _inQty[i] += qty; _inNotional[i] += notional; return; }   // 추가 진입
            _outQty[i] += qty; _outNotional[i] += notional; _net[i] += netRealized;
            if (posAfter == 0) Close(i, nowTicks);
        }

        private void Close(int i, long nowTicks)
        {
            int side = _side[i];
            long entry = _inNotional[i] / _inQty[i], exit = _outQty[i] > 0 ? _outNotional[i] / _outQty[i] : entry;
            long maePx = Math.Max(0, side > 0 ? entry - _worst[i] : _worst[i] - entry);
            long mfePx = Math.Max(0, side > 0 ? _best[i] - entry : entry - _best[i]);
            ref var r = ref _ring[(int)(_recorded % RecordCapacity)];
            r.Symbol = _sym[i]; r.Side = side; r.Qty = _inQty[i]; r.EntryPx = entry; r.ExitPx = exit;
            r.MaePx = maePx; r.MfePx = mfePx;
            r.MaeBp = entry > 0 ? maePx * 10_000 / entry : 0; r.MfeBp = entry > 0 ? mfePx * 10_000 / entry : 0;
            r.MaeWon = maePx * _inQty[i]; r.MfeWon = mfePx * _inQty[i];
            r.OpenTicks = _open[i]; r.CloseTicks = nowTicks;
            r.NetWon = _net[i]; r.FeesWon = _fees[i]; r.TaxWon = _tax[i]; r.GrossWon = _net[i] + _fees[i] + _tax[i];
            _recorded++;
            _trips++;
            if (r.NetWon > 0) { _wins++; _gp += r.NetWon; } else { _losses++; _gl += r.NetWon; }
            _netSum += r.NetWon; _grossSum += r.GrossWon; _feeSum += r.FeesWon; _taxSum += r.TaxWon;
            _maeBpSum += r.MaeBp; _mfeBpSum += r.MfeBp; _maeWonSum += r.MaeWon;
            if (mfePx > 0) { _effSum += r.ExitEfficiencyBp; _effN++; }
            int last = --_n;                                                        // 슬롯 반납(교체 압축)
            _sym[i] = _sym[last]; _side[i] = _side[last]; _open[i] = _open[last]; _inQty[i] = _inQty[last]; _inNotional[i] = _inNotional[last];
            _outQty[i] = _outQty[last]; _outNotional[i] = _outNotional[last]; _worst[i] = _worst[last]; _best[i] = _best[last];
            _fees[i] = _fees[last]; _tax[i] = _tax[last]; _net[i] = _net[last];
            _sym[last] = 0;
        }

        public ExecutionQualitySummary Summary()
            => new(_trips, _wins, _losses, _effN, _gp, _gl, _netSum, _grossSum, _feeSum, _taxSum, _maeBpSum, _mfeBpSum, _effSum, _maeWonSum);

        /// <summary>가장 최근 기록(없으면 false · 값 사본).</summary>
        public bool TryLast(out TradeExecutionRecord record)
        {
            record = default;
            if (_recorded == 0) return false;
            record = _ring[(int)((_recorded - 1) % RecordCapacity)];
            return true;
        }

        /// <summary>최근 기록 사본(최신 → 과거 · 반환 = 복사 수 · δ=0).</summary>
        public int CopyRecent(Span<TradeExecutionRecord> destination)
        {
            long avail = Math.Min(_recorded, RecordCapacity);
            int n = (int)Math.Min(avail, destination.Length);
            for (int k = 0; k < n; k++) destination[k] = _ring[(int)((_recorded - 1 - k) % RecordCapacity)];
            return n;
        }
    }
}
