// ============================================================================
// ArenaVwapTracker — [v12.18.15-DEV] 실시간 무할당 VWAP 누적기(단일 종목 · 단일 작성자 = 거버너 정산 스레드 · δ=0)
//
//  ① 누적: Σ(가격 × 체결량) · Σ체결량 = Int128 정수(1M 틱 × 1억 원 × 100만 주에도 오버플로 0 · 반올림 오차 0) → VWAP = Σ(P×V) ÷ ΣV(원 · 반올림).
//  ② 분산: 가중 Welford(체결량 가중 · double) — Σ(P²V) 차분식의 상쇄 오차 없이 VWAP 기준 표준편차 σ(원) 산출 → 밴드 = VWAP ± kσ.
//  ③ 이격률: (가격 − VWAP) ÷ VWAP(bp · 정수 · 0 = VWAP 없음).
//  ④ 수명주기: Reset(매치 초기화 · 리플레이 되감기) · Pause/Resume(입력 무시 — 일시정지 중 유입 틱이 누적을 오염하지 않음) ·
//     Capture/Restore(값 구조체 사본 — 테이프 탐색 · 동기화 체크포인트).
//  게시는 거버너 seqlock 창이 담당(ArenaSnapshot.AiHud) — 이 클래스는 잠금 · 할당 · 대기 0.
// ============================================================================
using System;

namespace StockManager.Engines.Arena
{
    /// <summary>VWAP 누적 상태 사본(값 · 체크포인트).</summary>
    public readonly struct ArenaVwapState
    {
        public readonly Int128 SumPv;
        public readonly long SumVolume, Trades, LastPrice;
        public readonly double Mean, M2;

        public ArenaVwapState(Int128 sumPv, long sumVolume, long trades, long lastPrice, double mean, double m2)
        {
            SumPv = sumPv; SumVolume = sumVolume; Trades = trades; LastPrice = lastPrice; Mean = mean; M2 = m2;
        }
    }

    public sealed class ArenaVwapTracker
    {
        private Int128 _pv;
        private long _vol, _trades, _last;
        private double _mean, _m2;
        private bool _paused;

        public long SumVolume => _vol;
        public Int128 SumPriceVolume => _pv;
        public long Trades => _trades;
        public long LastPrice => _last;
        public bool Paused => _paused;

        /// <summary>체결 1건(원 · 주). 일시정지 · 비양수 = 무시.</summary>
        public void OnTrade(long priceWon, long volume)
        {
            if (_paused || priceWon <= 0 || volume <= 0) return;
            _pv += (Int128)priceWon * volume;
            _vol += volume;
            _trades++;
            _last = priceWon;
            double delta = priceWon - _mean;
            _mean += delta * volume / _vol;
            _m2 += volume * delta * (priceWon - _mean);
        }

        /// <summary>VWAP(원 · 반올림 · 체결 없음 = 0).</summary>
        public long VwapWon => _vol > 0 ? (long)((_pv + _vol / 2) / _vol) : 0;

        /// <summary>VWAP 기준 체결량 가중 표준편차(원 · 반올림).</summary>
        public long SigmaWon => _vol > 0 && _m2 > 0 ? (long)Math.Round(Math.Sqrt(_m2 / _vol)) : 0;

        /// <summary>(가격 − VWAP) ÷ VWAP(bp). VWAP 없음 = 0.</summary>
        public long DeviationBp(long priceWon)
        {
            long v = VwapWon;
            return v > 0 && priceWon > 0 ? (priceWon - v) * 10_000 / v : 0;
        }

        /// <summary>VWAP ± kσ 밴드(k = 1/100 단위 · 예 200 = 2σ). 없음 = 0.</summary>
        public long BandWon(int kCenti, bool upper)
        {
            long v = VwapWon;
            if (v <= 0) return 0;
            long off = SigmaWon * kCenti / 100;
            return upper ? v + off : Math.Max(0, v - off);
        }

        public void Pause() => _paused = true;
        public void Resume() => _paused = false;

        public void Reset()
        {
            _pv = 0; _vol = _trades = _last = 0; _mean = _m2 = 0;
            _paused = false;
        }

        public ArenaVwapState Capture() => new(_pv, _vol, _trades, _last, _mean, _m2);

        public void Restore(in ArenaVwapState s)
        {
            _pv = s.SumPv; _vol = s.SumVolume; _trades = s.Trades; _last = s.LastPrice; _mean = s.Mean; _m2 = s.M2;
        }
    }
}
