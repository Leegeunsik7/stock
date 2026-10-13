// ============================================================================
// VolatilityAdaptiveCalibrator — [v12.18.1-DEV · 추천 A] 미시 변동성 국면(Regime) 판독 + 확신 임계 동적 상향(정수 · δ=0)
//
//  목적: 고정 확신 임계(0.85)는 횡보(노이즈) 장세에서 '잔량 불균형이 양방향으로 출렁일 때마다' 0.85 를 넘는 신호를 만든다
//   (마이크로 추세 = 10호가 불균형 변화율 EWMA → 불균형이 뒤집히는 순간 확신이 튄다 = 휩소). 이 캘리브레이터는 호가 1장마다
//   국면을 판독해 요구 확신을 올린다: 추세 = 기본(8500bp) · 회귀 = 중간(9000bp) · 횡보/워밍업 = 최대(9500bp).
//
//  산출(호가 1장 · 정수 연산만 — 부동소수점 0 · Math.Pow/Sqrt 0 · 힙 0):
//   r      = 중간가 수익률(bp × 256 고정소수점 Q8) = (mid₂ − mid₂₋₁) × 2,560,000 / mid₂₋₁   (mid₂ = 매수1 + 매도1 — 반 원 단위 정수)
//   ★[방향 A] 지수 이동 평균/분산(EMV) — α = 2^-k(시프트) 근사:
//      d = r − μ ;  μ += d >> k ;  σ² = σ² + (d² >> k) − (σ² >> k)       (≈ (1−α)(σ² + α d²) · α² 항 생략 — 상대 오차 ≤ α)
//      변동성 bp = isqrt(σ²) >> 8 (정수 뉴턴법 · BitOperations 초기값 · 고정 반복)
//      k = 5 → 유효 창 ≈ 2^(k+1) − 1 = 63 호가(기본) · k = 9 → ≈ 1,023 호가(오더의 '최근 1,000틱' 창). 기본을 짧게 둔 이유: 1,000틱 창은
//      국면 전환 감지가 수 분 늦어 횡보 진입 초기의 휩소를 못 막는다(테스트가 두 설정을 모두 실측).
//   효율비 ER(bp) = |EMA(r)| × 10,000 / EMA(|r|)  — 같은 방향으로 꾸준히 = 10,000 · 오르내리며 제자리 = 0(Kaufman 효율비의 EMA 판).
//   불균형 노이즈(bp) = EMA(|Δimb5|) × 10,000 / (|EMA(imb5)| + EMA(|Δimb5|)) — 한쪽 우위 지속 = 0 · 양방향 뒤집힘 = 10,000.
//   스프레드(bp) EMA · 스프레드 표준편차(EMV) — 관제 노출(국면 판정에는 쓰지 않는다 · 근거 없는 휴리스틱 추가 금지).
//
//  국면(우선순위 순):
//   Warmup        표본 < 2^k — 판단 근거 부족 = 최대 임계(fail-closed).
//   Choppy        ER < ChopEr(1500) ∧ 불균형 노이즈 ≥ ChopNoise(6000) — 가격은 제자리 · 잔량 우위는 뒤집힘 = 휩소 장세.
//   Trend         ER ≥ TrendEr(3500) ∧ 불균형 노이즈 < ChopNoise.
//   MeanReverting 그 외.
//   히스테리시스: Choppy '진입'은 즉시(fail-closed) · '이탈'은 비-Choppy 후보가 ExitHold(32) 호가 연속일 때만(국면 깜빡임에 임계가 내려가지 않게).
//   [v12.18.2-DEV · 방향 B] 플리커 감쇄: 연속 계수 도중 Choppy 후보가 1장이라도 끼면 0 부터 다시(추세 ↔ 노이즈 깜빡임 = 횡보 유지).
//    기본 16 → 32: 실측(테스트) — 순 이동 0 깜빡임(분출 10~16 장 ↔ 노이즈 동량 × 40)에서 16 은 임계가 4~321 장 내려갔고 32 는 0.
//    한계: 노이즈보다 훨씬 긴 분출(24 장 ↔ 노이즈 8)은 32 로도 소수 장(실측 2) 내려간다 — 그 구간은 실제 추세에 가깝다.
//    감쇄로 붙잡아 둔 호가 수 = DampenedBooks(관제).
//
//  상태(CalibrationState)는 QuantSignalProvider 의 종목 · 피드 슬롯 안에 있다 — 슬롯 작성 잠금 아래에서만 갱신(단일 작성자).
//  ★정직: 임계값 · 창 길이 · 국면 경계는 보수적 기본값이며 실거래 성과로 보정된 값이 아니다(설정으로 조정). 이 장치는 '확신 낮은
//   주문의 전방 거부'를 강화할 뿐 수익을 보장하지 않는다.
// ============================================================================
using System;
using System.Numerics;
using System.Threading;

namespace StockManager.Engines
{
    public enum MicroRegime : byte { Warmup = 0, Trend = 1, MeanReverting = 2, Choppy = 3 }

    /// <summary>종목 · 피드별 캘리브레이션 상태(값 타입 · 슬롯 내장 · 할당 0).</summary>
    public struct CalibrationState
    {
        internal long PrevMidX2, PrevImbBp;
        internal long RetMeanQ, RetAbsQ, RetVarQ;           // Q8 bp · Q16 bp²
        internal long ImbMeanBp, ImbNoiseBp;
        internal long SpreadMeanQ, SpreadVarQ;              // Q8 bp · Q16 bp²
        internal int Samples, ExitStreak;
        internal MicroRegime RegimeValue;
        internal int RequiredBpValue;

        public readonly int SampleCount => Samples;
        public readonly MicroRegime Regime => RegimeValue;
        /// <summary>현재 요구 확신(bp · 0 = 아직 관측 없음).</summary>
        public readonly int RequiredBp => RequiredBpValue;
        public readonly long VolatilityBp => VolatilityQ8 >> 8;
        /// <summary>변동성(bp × 256 · Q8) — 정밀 판독/검증용.</summary>
        public readonly long VolatilityQ8 => VolatilityAdaptiveCalibrator.ISqrt(RetVarQ);
        public readonly long EfficiencyBp => RetAbsQ <= 0 ? 0 : Math.Min(10_000, Math.Abs(RetMeanQ) * 10_000 / RetAbsQ);
        public readonly long ImbalanceNoiseBp => ImbNoiseBp <= 0 ? 0 : ImbNoiseBp * 10_000 / (Math.Abs(ImbMeanBp) + ImbNoiseBp);
        public readonly long SpreadBp => SpreadMeanQ >> 8;
        public readonly long SpreadVolBp => VolatilityAdaptiveCalibrator.ISqrt(SpreadVarQ) >> 8;
    }

    public sealed class VolatilityAdaptiveCalibrator
    {
        public const int DefaultBaseBp = 8_500, DefaultMaxBp = 9_500, DefaultShift = 5;
        public const int DefaultTrendEfficiencyBp = 3_500, DefaultChopEfficiencyBp = 1_500, DefaultChopNoiseBp = 6_000, DefaultExitHoldBooks = 32;
        private const long MaxAbsRetQ = 1L << 24;           // |r| ≤ 65,536bp(Q8) 클램프 — d² ≤ 2^50 (long 안전)

        private readonly int _baseBp, _shift, _trendEr, _chopEr, _chopNoise, _exitHold, _warmup;
        private int _maxBp, _midBp;                                                   // [v12.18.3] 런타임 상향 전용(RaiseMaxBp)
        private long _observations, _choppyEntries, _choppyBooks, _dampened;

        public VolatilityAdaptiveCalibrator(int baseBp = DefaultBaseBp, int maxBp = DefaultMaxBp, int shift = DefaultShift,
                                            int trendEfficiencyBp = DefaultTrendEfficiencyBp, int chopEfficiencyBp = DefaultChopEfficiencyBp,
                                            int chopNoiseBp = DefaultChopNoiseBp, int exitHoldBooks = DefaultExitHoldBooks)
        {
            if (baseBp < 1 || baseBp > 10_000) throw new ArgumentOutOfRangeException(nameof(baseBp));
            if (maxBp < baseBp || maxBp > 10_000) throw new ArgumentOutOfRangeException(nameof(maxBp), "기본 ≤ 최대 ≤ 10,000");
            if (shift < 1 || shift > 12) throw new ArgumentOutOfRangeException(nameof(shift));
            if (chopEfficiencyBp < 0 || trendEfficiencyBp <= chopEfficiencyBp || trendEfficiencyBp > 10_000) throw new ArgumentOutOfRangeException(nameof(trendEfficiencyBp), "횡보 < 추세 ≤ 10,000");
            if (chopNoiseBp < 1 || chopNoiseBp > 10_000) throw new ArgumentOutOfRangeException(nameof(chopNoiseBp));
            if (exitHoldBooks < 1) throw new ArgumentOutOfRangeException(nameof(exitHoldBooks));
            _baseBp = baseBp; _maxBp = maxBp; _midBp = baseBp + (maxBp - baseBp) / 2; _shift = shift;
            _trendEr = trendEfficiencyBp; _chopEr = chopEfficiencyBp; _chopNoise = chopNoiseBp; _exitHold = exitHoldBooks;
            _warmup = 1 << shift;
        }

        public int BaseBp => _baseBp;
        public int MaxBp => Volatile.Read(ref _maxBp);

        /// <summary>
        /// [v12.18.3-DEV] 최대 임계(횡보 · 워밍업) 런타임 상향 — 올리기만(낮은 값 · 범위 밖 = 무시). 회귀 임계(중간값)도 함께 오른다.
        /// 하향은 재기동으로만(운영자 경로) — 실행 중 느슨해지는 경로 0. 반환 = 상향했는가.
        /// </summary>
        public bool RaiseMaxBp(int bp)
        {
            if (bp > 10_000) bp = 10_000;
            while (true)
            {
                int cur = Volatile.Read(ref _maxBp);
                if (bp <= cur) return false;
                if (Interlocked.CompareExchange(ref _maxBp, bp, cur) == cur)
                {
                    int mid = _baseBp + (bp - _baseBp) / 2;
                    int m;
                    do { m = Volatile.Read(ref _midBp); } while (mid > m && Interlocked.CompareExchange(ref _midBp, mid, m) != m);
                    return true;
                }
            }
        }
        public int Shift => _shift;
        public int WarmupBooks => _warmup;
        public long Observations => Interlocked.Read(ref _observations);
        /// <summary>비-Choppy → Choppy 전이 수(누적 · 전 종목).</summary>
        public long ChoppyEntries => Interlocked.Read(ref _choppyEntries);
        /// <summary>Choppy(또는 워밍업) 국면에서 관측한 호가 수.</summary>
        public long ChoppyBooks => Interlocked.Read(ref _choppyBooks);
        /// <summary>[방향 B] 후보는 비-횡보였지만 히스테리시스로 횡보(최대 임계)를 유지한 호가 수.</summary>
        public long DampenedBooks => Interlocked.Read(ref _dampened);
        public int ExitHoldBooks => _exitHold;

        /// <summary>국면 → 요구 확신(bp).</summary>
        public int ThresholdFor(MicroRegime regime) => regime switch
        {
            MicroRegime.Trend => _baseBp,
            MicroRegime.MeanReverting => Volatile.Read(ref _midBp),
            _ => Volatile.Read(ref _maxBp),                                         // Choppy · Warmup = 최대(fail-closed)
        };

        /// <summary>
        /// 호가 1장 반영(δ=0 · 슬롯 작성 잠금 아래 단일 작성자). 반환 = 요구 확신(bp). 입력 무효(호가 ≤ 0 · 교차 · 잔량 합 0) = 상태 불변 · 최대 임계.
        /// </summary>
        public int Observe(ref CalibrationState s, long bestBid, long bestAsk, long bid5, long ask5)
        {
            if (bestBid <= 0 || bestAsk <= bestBid || bid5 < 0 || ask5 < 0 || bid5 + ask5 <= 0) return Volatile.Read(ref _maxBp);
            Interlocked.Increment(ref _observations);
            int k = _shift;
            long midX2 = bestBid + bestAsk;
            long imbBp = (bid5 - ask5) * 10_000 / (bid5 + ask5);
            long spreadQ = (bestAsk - bestBid) * 5_120_000 / midX2;                 // (ask − bid) / mid × 10⁴ × 256 = × 2 × 2,560,000 / mid₂
            if (s.Samples == 0)
            {
                s.PrevMidX2 = midX2; s.PrevImbBp = imbBp; s.ImbMeanBp = imbBp; s.SpreadMeanQ = spreadQ;
                s.Samples = 1;
            }
            else
            {
                long r = Math.Clamp((midX2 - s.PrevMidX2) * 2_560_000 / s.PrevMidX2, -MaxAbsRetQ, MaxAbsRetQ);
                long d = r - s.RetMeanQ;
                s.RetMeanQ += d >> k;
                s.RetVarQ += ((d * d) >> k) - (s.RetVarQ >> k);
                s.RetAbsQ += (Math.Abs(r) - s.RetAbsQ) >> k;
                long dImb = imbBp - s.PrevImbBp;
                s.ImbMeanBp += (imbBp - s.ImbMeanBp) >> k;
                s.ImbNoiseBp += (Math.Abs(dImb) - s.ImbNoiseBp) >> k;
                long ds = Math.Clamp(spreadQ - s.SpreadMeanQ, -MaxAbsRetQ, MaxAbsRetQ);
                s.SpreadMeanQ += ds >> k;
                s.SpreadVarQ += ((ds * ds) >> k) - (s.SpreadVarQ >> k);
                s.PrevMidX2 = midX2; s.PrevImbBp = imbBp;
                if (s.Samples < int.MaxValue) s.Samples++;
            }

            MicroRegime prev = s.RegimeValue, next;
            if (s.Samples < _warmup) next = MicroRegime.Warmup;
            else
            {
                long er = s.EfficiencyBp, noise = s.ImbalanceNoiseBp;
                MicroRegime candidate = er < _chopEr && noise >= _chopNoise ? MicroRegime.Choppy
                                       : er >= _trendEr && noise < _chopNoise ? MicroRegime.Trend
                                       : MicroRegime.MeanReverting;
                if (candidate == MicroRegime.Choppy) { next = MicroRegime.Choppy; s.ExitStreak = 0; }
                else if (prev == MicroRegime.Choppy)
                {
                    // 횡보 이탈 = 비-횡보 후보 ExitHold 연속(히스테리시스) — 그 전까지는 횡보 유지(최대 임계)
                    next = ++s.ExitStreak >= _exitHold ? candidate : MicroRegime.Choppy;
                    if (next != MicroRegime.Choppy) s.ExitStreak = 0;
                    else Interlocked.Increment(ref _dampened);
                }
                else next = candidate;
            }
            if (next == MicroRegime.Choppy && prev != MicroRegime.Choppy) Interlocked.Increment(ref _choppyEntries);
            if (next is MicroRegime.Choppy or MicroRegime.Warmup) Interlocked.Increment(ref _choppyBooks);
            s.RegimeValue = next;
            return s.RequiredBpValue = ThresholdFor(next);
        }

        /// <summary>정수 제곱근(내림 · 뉴턴법 · 할당 0). v ≤ 0 = 0.</summary>
        public static long ISqrt(long v)
        {
            if (v <= 0) return 0;
            ulong n = (ulong)v;
            int bits = 64 - BitOperations.LeadingZeroCount(n);
            ulong x = 1UL << ((bits + 1) >> 1);                                     // x ≥ √n 에서 출발 → 단조 감소
            while (true)
            {
                ulong y = (x + n / x) >> 1;
                if (y >= x) return (long)x;
                x = y;
            }
        }
    }
}
