// ============================================================================
// ZeroAllocTickEngine — S13 #1 제로-할당 틱 파이프라인 (v3.81.0)
//  · CandleFrame: readonly struct 48B (double×4 + long×2), 캐시 라인 친화
//  · RunMaCross: ReadOnlySpan<CandleFrame> + 러닝 섬(O(1)/bar) → GC 힙 할당 0바이트
//  · BacktestEngine.RunMaCross(KOSPI) 와 동일 warmup(longP+1)·비용 모델 복제
//  · [AggressiveOptimization] → JIT가 SIMD·언롤링 최적화 적극 적용
// ============================================================================
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MS2E.Infrastructure
{
    // ── 캔들 원소 구조체 ──────────────────────────────────────────────────────────
    // double 필드 4 + long 필드 2 = 48바이트 (64B 캐시 라인에 최소 1개 완전 적합)
    public readonly struct CandleFrame
    {
        public readonly double Open;
        public readonly double High;
        public readonly double Low;
        public readonly double Close;
        public readonly long   DateTicks;
        public readonly long   Volume;

        public CandleFrame(double o, double h, double l, double c, long dt, long vol)
        {
            Open = o; High = h; Low = l; Close = c; DateTicks = dt; Volume = vol;
        }

        public static CandleFrame From(Candle c) => new(
            (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close,
            c.Date.Ticks, c.Volume);

        public DateTime Date => new(DateTicks, DateTimeKind.Unspecified);
    }

    // ── 결과 값 타입 — 힙 할당 없는 반환 ────────────────────────────────────────
    public readonly record struct ZeroAllocResult(
        double TotalReturnPct,
        double MaxDrawdownPct,
        int    TradeCount);

    // ── 핵심 엔진 ──────────────────────────────────────────────────────────────
    public static class ZeroAllocTickEngine
    {
        // BacktestEngine.TradingCost.Default (KRX KOSPI) 상수 복제
        private const double SlippageRate   = 0.001;    // 0.1%
        private const double CommissionRate = 0.00015;  // 0.015% KRX 양방향
        private const double SellTaxRate    = 0.0018;   // 0.18% KOSPI 매도세

        /// <summary>Candle 목록 → CandleFrame 배열 (일회성 변환 · ReferenceFrames 캐시 전용)</summary>
        public static CandleFrame[] ToFrameArray(IReadOnlyList<Candle> candles)
        {
            var arr = new CandleFrame[candles.Count];
            for (int i = 0; i < candles.Count; i++)
                arr[i] = CandleFrame.From(candles[i]);
            return arr;
        }

        /// <summary>
        /// MA 크로스 백테스트 — GC 힙 할당 0바이트 (핫 루프 기준).
        /// BacktestEngine.RunMaCross(KOSPI) 와 동일 warmup · 비용 모델 · 강제청산.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static ZeroAllocResult RunMaCross(
            ReadOnlySpan<CandleFrame> frames,
            int shortP,
            int longP)
        {
            // 유효성 + 최소 프레임 수 확인 (warmup = longP + 1, 첫 신호는 i = longP + 1)
            if (frames.IsEmpty || shortP < 1 || longP <= shortP || frames.Length <= longP + 1)
                return new ZeroAllocResult(0.0, 0.0, 0);

            int warmup = longP + 1;

            // ── 러닝 섬 초기화 ────────────────────────────────────────────────────
            // shortSum: i = longP 에서 slide 후 window [longP-shortP+1..longP] 가 되도록
            //           초기값 = Σ frames[j] for j in [longP-shortP .. longP-1]
            // longSum : i = longP 에서 slide 후 window [1..longP] 가 되도록
            //           초기값 = Σ frames[j] for j in [0 .. longP-1]
            double shortSum = 0.0, longSum = 0.0;
            for (int j = 0; j < longP; j++)
            {
                if (j >= longP - shortP) shortSum += frames[j].Close;
                longSum += frames[j].Close;
            }

            // ── 거래 상태 (전부 스택 변수 — 힙 할당 0) ──────────────────────────
            const double Initial = 1_000_000.0;
            double equity = Initial;
            double entryP = 0.0;
            int    qty    = 0;
            double peak   = Initial;
            double maxDD  = 0.0;
            int    trades = 0;

            // ── 메인 루프 ──────────────────────────────────────────────────────────
            for (int i = longP; i < frames.Length; i++)
            {
                // 슬라이딩 러닝 섬 — O(1), 힙 할당 없음
                shortSum += frames[i].Close - frames[i - shortP].Close;
                longSum  += frames[i].Close - frames[i - longP].Close;

                if (i < warmup) continue;   // warmup 미달 구간 — 신호 검사 생략

                // 현재 MA · 이전 MA (BacktestEngine.MaCrossSig 동일 수식)
                double shortMa  = shortSum / shortP;
                double longMa   = longSum  / longP;
                // 이전 MA: 러닝 섬에서 현재 원소 제거 + 빠져나간 원소 복원
                double shortMaP = (shortSum - frames[i].Close + frames[i - shortP].Close) / shortP;
                double longMaP  = (longSum  - frames[i].Close + frames[i - longP].Close)  / longP;

                bool golden = shortMaP < longMaP && shortMa > longMa;  // 골든크로스
                bool dead   = shortMaP > longMaP && shortMa < longMa;  // 데드크로스

                double price = frames[i].Close;

                if (golden && qty == 0)
                {
                    double execP  = price * (1.0 + SlippageRate);
                    int    q      = (int)(equity / execP);
                    double amount = q * execP;
                    equity -= amount + amount * CommissionRate;
                    qty     = q;
                    entryP  = execP;
                }
                else if (dead && qty > 0)
                {
                    double execP  = price * (1.0 - SlippageRate);
                    double amount = qty * execP;
                    equity += amount - amount * CommissionRate - amount * SellTaxRate;
                    trades++;
                    qty = 0;
                }

                // 마크투마켓 MDD (BacktestEngine equityCurve.Add(equity + qty * price) 동일)
                double mtm = equity + qty * price;
                if (mtm > peak) peak = mtm;
                double dd  = (peak - mtm) / peak * 100.0;
                if (dd > maxDD) maxDD = dd;
            }

            // ── 마감 강제 청산 (BacktestEngine RunCore 동일) ──────────────────────
            // equityCurve[^1] = equity 덮어쓰기 → 마지막 MDD 재계산
            if (qty > 0 && frames.Length > 0)
            {
                double lastP  = frames[^1].Close * (1.0 - SlippageRate);
                double amount = qty * lastP;
                equity += amount - amount * CommissionRate - amount * SellTaxRate;
                trades++;

                if (equity > peak) peak = equity;
                double dd = (peak - equity) / peak * 100.0;
                if (dd > maxDD) maxDD = dd;
            }

            double totalReturn = (equity - Initial) / Initial * 100.0;
            return new ZeroAllocResult(totalReturn, maxDD, trades);
        }
    }
}
