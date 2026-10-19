// ============================================================================
// ArenaTradeAnalytics — [v12.18.14-DEV] 아레나 사후 복기 분석(진영별 · 단일 작성자 = 원장 정산 스레드 · δ=0)
//
//  왕복 거래(trip) = 종목 포지션 0 → 보유 → 0. 진입 = 첫 매수 체결 · 청산 = 마지막 매도 체결(부분 매도는 같은 trip 의 실현 누적).
//   · 평가 편차(excursion) = 보유 수량 × 최우선 매수호가 − 보유 원가(매수 수수료 포함 · 매도 세금 미차감) — 시가평가마다 갱신.
//   · MAE = trip 중 최소 편차(≤ 0 · 최대 역행) · MFE = 최대 편차(≥ 0 · 최대 순행). 청산 시 최종 실현손익도 편차 후보에 포함.
//   · 보유 시간 = 청산 시각 − 진입 시각(시장 시각 — 라이브 = 정산 시각 · 리플레이 = 테이프 시각).
//   · trip 슬리피지 = 진입 직전 ~ 청산 직후 원장 슬리피지 누적 차(시장가 체결분) · 전체 슬리피지 드래그 = 원장 누적.
//   · 포착률(capture) = Σ 실현 ÷ Σ MFE(bp · MFE 합 0 이면 0).
//  틸트: 연패 2회 이상 상태에서의 신규 주문(시장가 · 지정가 — 취소/청산 제외)이 평온 기준선(연패 0 상태 EWMA α=1/5 · 표본 ≥ 3) 대비
//   주문 간격 &lt; ½ (빈도 급단축) 또는 주문 금액 &gt; 2배(규모 급증)면 경보 1건(사유 비트).
//  ★고정 배열 · 정수 연산만 — 할당 0. 원장 Reset 과 함께 초기화.
// ============================================================================
using System;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena
{
    public sealed class ArenaTradeAnalytics
    {
        public const int Slots = ArenaBook.MaxPositions, TiltStreak = 2, BaselineSamples = 3;

        private readonly long[] _sym = new long[Slots], _open = new long[Slots], _slip0 = new long[Slots],
                                _mae = new long[Slots], _mfe = new long[Slots], _real = new long[Slots];
        private int _n;

        private int _trips, _wins, _losses, _lossStreak, _maxLossStreak, _tiltAlerts, _lastTiltReason;
        private long _sumHold, _maxHold, _maeWorst, _sumMae, _mfeBest, _sumMfe, _sumRealized, _tripSlip, _lastTiltTicks;
        private long _lastSubmit, _baseInterval, _baseNotional;
        private int _calmIntervals, _calmNotionals;

        public int Trips => _trips;
        public int TiltAlerts => _tiltAlerts;
        public int LossStreak => _lossStreak;
        public long LastTiltTicks => _lastTiltTicks;
        /// <summary>[v12.18.18-DEV] 진행 중 trip 의 최악 MAE(원 ≤ 0 · 포식자 역추적 입력 · δ=0).</summary>
        public long OpenMaeWon { get { long m = 0; for (int i = 0; i < _n; i++) m = Math.Min(m, _mae[i]); return m; } }

        public void Reset()
        {
            Array.Clear(_sym); Array.Clear(_open); Array.Clear(_slip0); Array.Clear(_mae); Array.Clear(_mfe); Array.Clear(_real);
            _n = 0;
            _trips = _wins = _losses = _lossStreak = _maxLossStreak = _tiltAlerts = _lastTiltReason = 0;
            _sumHold = _maxHold = _maeWorst = _sumMae = _mfeBest = _sumMfe = _sumRealized = _tripSlip = _lastTiltTicks = 0;
            _lastSubmit = _baseInterval = _baseNotional = 0;
            _calmIntervals = _calmNotionals = 0;
        }

        private int IndexOf(long symbol)
        {
            for (int i = 0; i < _n; i++) if (_sym[i] == symbol) return i;
            return -1;
        }

        /// <summary>신규 주문 접수(시장가 · 지정가). notional = 수량 × 기준가(지정가 · 접수 시 최우선).</summary>
        public void OnSubmit(long nowTicks, long notionalWon)
        {
            long interval = _lastSubmit > 0 && nowTicks > _lastSubmit ? nowTicks - _lastSubmit : -1;
            _lastSubmit = nowTicks;
            if (_lossStreak >= TiltStreak)
            {
                int reason = 0;
                if (_calmIntervals >= BaselineSamples && interval >= 0 && interval * 2 < _baseInterval) reason |= ArenaTilt.Frequency;
                if (_calmNotionals >= BaselineSamples && notionalWon > _baseNotional * 2) reason |= ArenaTilt.Size;
                if (reason != 0) { _tiltAlerts++; _lastTiltReason = reason; _lastTiltTicks = nowTicks; }
                return;
            }
            if (_lossStreak != 0) return;                                       // 연패 1회 = 기준선 동결(오염 방지) · 경보 대상도 아님
            if (interval > 0)
            {
                _baseInterval = _calmIntervals == 0 ? interval : _baseInterval + (interval - _baseInterval) / 5;
                _calmIntervals++;
            }
            if (notionalWon > 0)
            {
                _baseNotional = _calmNotionals == 0 ? notionalWon : _baseNotional + (notionalWon - _baseNotional) / 5;
                _calmNotionals++;
            }
        }

        /// <summary>체결 1건 반영. dir +1 매수 · −1 매도 · posBefore/After = 체결 전후 보유 · realized = 이 매도 체결 실현손익 · slip = 체결 전후 원장 슬리피지 누적.</summary>
        public void OnFill(long symbol, int dir, long posBefore, long posAfter, long realizedWon, long nowTicks, long slipBefore, long slipAfter)
        {
            int i = IndexOf(symbol);
            if (posBefore == 0 && posAfter != 0)                                     // [v12.18.23-DEV] trip 개시 = 무포지션 → 롱(매수) 또는 숏(매도)
            {
                if (i < 0)
                {
                    if (_n >= Slots) return;                                        // 원장 포지션 한도와 같음 — 도달 불가(방어)
                    i = _n++;
                }
                _sym[i] = symbol; _open[i] = nowTicks; _slip0[i] = slipBefore; _mae[i] = 0; _mfe[i] = 0; _real[i] = 0;
                return;
            }
            if (i < 0 || Math.Abs(posAfter) > Math.Abs(posBefore)) return;          // 추가 진입(롱 매수 · 숏 매도) = 같은 trip(편차는 시가평가가 갱신)
            _real[i] += realizedWon;
            if (posAfter != 0) return;
            long realized = _real[i];
            long mae = Math.Min(_mae[i], Math.Min(0, realized)), mfe = Math.Max(_mfe[i], Math.Max(0, realized));
            long hold = Math.Max(0, nowTicks - _open[i]);
            _trips++;
            _sumHold += hold; if (hold > _maxHold) _maxHold = hold;
            _sumMae += mae; if (mae < _maeWorst) _maeWorst = mae;
            _sumMfe += mfe; if (mfe > _mfeBest) _mfeBest = mfe;
            _sumRealized += realized;
            _tripSlip += slipAfter - _slip0[i];
            if (realized > 0) { _wins++; _lossStreak = 0; }
            else { _losses++; _lossStreak++; if (_lossStreak > _maxLossStreak) _maxLossStreak = _lossStreak; }
            int last = --_n;                                                        // trip 종료 = 슬롯 반납(교체 압축)
            _sym[i] = _sym[last]; _open[i] = _open[last]; _slip0[i] = _slip0[last]; _mae[i] = _mae[last]; _mfe[i] = _mfe[last]; _real[i] = _real[last];
            _sym[last] = 0;
        }

        /// <summary>시가평가 편차 갱신(보유 × 최우선 매수 − 원가 · 진행 중 trip 만).</summary>
        public void Mark(long symbol, long excursionWon)
        {
            int i = IndexOf(symbol);
            if (i < 0) return;
            long e = excursionWon + _real[i];                                      // 부분 매도 실현분 포함(trip 누적 손익 기준)
            if (e < _mae[i]) _mae[i] = e;
            if (e > _mfe[i]) _mfe[i] = e;
        }

        /// <summary>복기 사본(δ=0). slippageDragWon = 원장 누적 슬리피지(시장가).</summary>
        public ArenaDebrief Snapshot(long slippageDragWon)
        {
            long openMae = 0, openMfe = 0;
            for (int i = 0; i < _n; i++) { openMae = Math.Min(openMae, _mae[i]); openMfe = Math.Max(openMfe, _mfe[i]); }
            long avgHoldMs = _trips == 0 ? 0 : _sumHold / _trips / TimeSpan.TicksPerMillisecond;
            long capture = _sumMfe > 0 ? (long)((Int128)_sumRealized * 10_000 / _sumMfe) : 0;
            return new ArenaDebrief(_trips, _wins, _losses, _n, _lossStreak, _maxLossStreak, _tiltAlerts, _lastTiltReason,
                                    avgHoldMs, _maxHold / TimeSpan.TicksPerMillisecond, _maeWorst, _trips == 0 ? 0 : _sumMae / _trips,
                                    _mfeBest, _trips == 0 ? 0 : _sumMfe / _trips, _sumRealized, slippageDragWon, _tripSlip, capture, openMae, openMfe);
        }
    }
}
