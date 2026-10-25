// ============================================================================
// PlutoRegimeMatcher — v12.18.82-DEV: 플루토 역사적 국면 판정기(Track B 게시 · Track A 체결 정산 판독 δ=0 seqlock) + 거버너 가중치 동적 편향
//
//   PlutoRegimeMatch(값 타입 · default = 판정 전): 매칭 국면 id(1~12) · 유사도 ×10(0~1000) · 코사인 ×1000 · RMS 거리 ×10 · 사용 차원 · 유형 · 전략 · 관측 기준 시각.
//   거버너 편향(Bias · 유사도 ≥ MinBiasSimilarityX10(60.0%) 일 때만 · 유사도 비례):
//     TrendFollow    = 모멘텀 tilt + TrendTiltPct(20%) × 유사도   — 과거 같은 국면에서 추세 추종 승률 우위.
//     MeanReversion  = 모멘텀 tilt − ReversionTiltPct(20%) × 유사도 — 돌파 추격 억제.
//     DevilsAdvocate = 엄격도 + DefensiveStrictnessPts(10점) × 유사도 — 위험 국면(패닉 · 약세) 자동 감점 강화(원확신도 차감 → 거부권 빈도 증가).
//     결과 = 학습 가중치에 가산 후 PlutoCognitiveWeights 상·하한 포화(세대 · 섀도우 계수 보존). ※ 감점 상수 · 태그 무결성 불변(원확신도 입력만 이동).
//   PlutoRegimeMatcher: Publish(관측) = 레이크 FindNearest → 버전 CAS 잠금(짝수 → 홀수) → 기록 → +2 발행(실제 작성자 = 학습 스웜 1개).
//     TryRead: 버전 브래킷 · 홀수 = 재시도 · 상한 MaxReadSpins 초과 = false(호출측 = default = 판정 전 · 편향 0 · fail-safe).
//   ★δ=0: Publish/TryRead/Bias 힙 할당 0(고정 필드 · 값 타입 복사). ★liveArmed:false — 가상 원장 진입 판정 입력만(주문 수단 0).
// ============================================================================
using System;
using System.Globalization;
using System.Threading;

namespace StockManager.Engines.Cognitive.Learning
{
    /// <summary>역사적 국면 매칭 결과(값 타입 · CycleId 0 = 판정 전).</summary>
    public readonly struct PlutoRegimeMatch
    {
        public const int MinBiasSimilarityX10 = 600, TrendTiltPct = 20, ReversionTiltPct = 20, DefensiveStrictnessPts = 10;

        public readonly int CycleId, SimilarityX10, CosineX1000, DistanceX10, Dimensions;
        public readonly PlutoEpisodeKind Kind;
        public readonly PlutoRegimeStrategy Strategy;
        public readonly long AsOfUtcTicks;

        public PlutoRegimeMatch(int cycleId, int similarityX10, int cosineX1000, int distanceX10, int dimensions, PlutoEpisodeKind kind, PlutoRegimeStrategy strategy,
            long asOfUtcTicks)
        {
            CycleId = cycleId; SimilarityX10 = similarityX10; CosineX1000 = cosineX1000; DistanceX10 = distanceX10; Dimensions = dimensions;
            Kind = kind; Strategy = strategy; AsOfUtcTicks = asOfUtcTicks;
        }

        public bool Found => CycleId > 0;

        /// <summary>정수 % 반올림(에피소딕 기억 SimilarityPct 와 같은 척도).</summary>
        public int SimilarityPct => (SimilarityX10 + 5) / 10;

        /// <summary>편향 적용 여부(판정 ∧ 유사도 ≥ 60.0%).</summary>
        public bool Biases => Found && SimilarityX10 >= MinBiasSimilarityX10;

        /// <summary>
        /// 무결성: 판정 전 = 전 필드 default · 판정 = 국면 id 1~12 ∧ 유사도 0~1000 ∧ 코사인 ±1000 ∧ 거리 ≥ 0 ∧ 차원 2~6 ∧ 유형/전략 = 레이크 국면과 일치.
        /// </summary>
        public bool IsWellFormed
        {
            get
            {
                if (!Found)
                    return CycleId == 0 && SimilarityX10 == 0 && CosineX1000 == 0 && DistanceX10 == 0 && Dimensions == 0
                        && Kind == PlutoEpisodeKind.Unknown && Strategy == PlutoRegimeStrategy.None && AsOfUtcTicks == 0;
                var c = PlutoRegimeVectorLake.Get(CycleId);
                return c is not null && SimilarityX10 is >= 0 and <= 1000 && CosineX1000 is >= -1000 and <= 1000 && DistanceX10 >= 0
                    && Dimensions is >= PlutoRegimeVectorLake.MinDimensions and <= PlutoRegimeVectorLake.Dims && Kind == c.Kind && Strategy == c.Strategy;
            }
        }

        /// <summary>모멘텀 tilt 편향 %(δ=0 · 미적용 = 0).</summary>
        public int MomentumTiltBiasPct => !Biases ? 0 : Strategy switch
        {
            PlutoRegimeStrategy.TrendFollow => TrendTiltPct * SimilarityX10 / 1000,
            PlutoRegimeStrategy.MeanReversion => -ReversionTiltPct * SimilarityX10 / 1000,
            _ => 0,
        };

        /// <summary>악마의 변호인 엄격도 편향 점(δ=0 · 미적용 = 0).</summary>
        public int StrictnessBiasPts => Biases && Strategy == PlutoRegimeStrategy.DevilsAdvocate ? DefensiveStrictnessPts * SimilarityX10 / 1000 : 0;

        /// <summary>학습 가중치 + 국면 편향(δ=0 · 상·하한 포화 · 세대/섀도우 계수 보존). 편향 0 = 그대로.</summary>
        public PlutoCognitiveWeights Bias(in PlutoCognitiveWeights learned)
        {
            int tilt = MomentumTiltBiasPct, strict = StrictnessBiasPts;
            if (tilt == 0 && strict == 0) return learned;
            return new PlutoCognitiveWeights(learned.Generation,
                Math.Clamp(learned.MomentumTiltPct + tilt, -PlutoCognitiveWeights.MaxMomentumTiltPct, PlutoCognitiveWeights.MaxMomentumTiltPct),
                Math.Clamp(learned.DevilStrictnessPts + strict, PlutoCognitiveWeights.MinStrictnessPts, PlutoCognitiveWeights.MaxStrictnessPts),
                learned.ShadowWinRateBp, learned.ShadowMddBp, learned.ShadowSamples);
        }
    }

    /// <summary>역사적 국면 한국어 문장(콜드 경로 — 호출마다 문자열 조립).</summary>
    public static class PlutoRegimeText
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Percent(int similarityX10) => string.Create(Inv, $"{similarityX10 / 10}.{similarityX10 % 10}%");

        /// <summary>뱃지: [🧠 과거 국면 매칭: {이름} (유사도 91.4%)] · 판정 전 = "[🧠 과거 국면 매칭: 판정 전]".</summary>
        public static string Badge(in PlutoRegimeMatch m)
        {
            var c = m.Found ? PlutoRegimeVectorLake.Get(m.CycleId) : null;
            return c is null ? "[🧠 과거 국면 매칭: 판정 전]" : $"[🧠 과거 국면 매칭: {c.Name} (유사도 {Percent(m.SimilarityX10)})]";
        }

        public static string StrategyLabel(PlutoRegimeStrategy s) => s switch
        {
            PlutoRegimeStrategy.TrendFollow => "추세 추종 우위",
            PlutoRegimeStrategy.MeanReversion => "평균 회귀 우위",
            PlutoRegimeStrategy.DevilsAdvocate => "악마의 변호인 강화(방어)",
            _ => "전략 미정",
        };

        /// <summary>편향 문구: "추세 추종 우위 → 모멘텀 +18%" · 유사도 미달 = "… → 편향 미적용(유사도 60% 미만)".</summary>
        public static string Effect(in PlutoRegimeMatch m)
        {
            if (!m.Found) return "";
            string label = StrategyLabel(m.Strategy);
            if (!m.Biases) return label + " → 편향 미적용(유사도 60% 미만)";
            int tilt = m.MomentumTiltBiasPct, strict = m.StrictnessBiasPts;
            if (strict != 0) return string.Create(Inv, $"{label} → 엄격도 +{strict}점");
            return string.Create(Inv, $"{label} → 모멘텀 {(tilt >= 0 ? "+" : "")}{tilt}%");
        }

        /// <summary>해설: "{기간} · {해설} · {편향 문구}".</summary>
        public static string Explain(in PlutoRegimeMatch m)
        {
            var c = m.Found ? PlutoRegimeVectorLake.Get(m.CycleId) : null;
            return c is null ? "" : $"{c.Period} · {c.Note} · {Effect(in m)}";
        }
    }

    public sealed class PlutoRegimeMatcher
    {
        public const int MaxReadSpins = 1024;

        private long _ver;
        private PlutoRegimeMatch _m;
        private long _published, _contended;

        public long Published => Interlocked.Read(ref _published);
        public long ContendedReads => Interlocked.Read(ref _contended);
        public long Version => Volatile.Read(ref _ver);

        /// <summary>관측 → 12대 국면 매칭 → 원자 게시(δ=0). 형식 불량 · 차원 부족 = 판정 전(default) 게시.</summary>
        public PlutoRegimeMatch Publish(in PlutoMacroSnapshot observed)
        {
            var m = PlutoRegimeVectorLake.FindNearest(in observed);
            Write(in m);
            return m;
        }

        /// <summary>판정 전으로 초기화(δ=0).</summary>
        public void Clear() => Write(default);

        private void Write(in PlutoRegimeMatch m)
        {
            var spin = new SpinWait();
            long v;
            while (true)
            {
                v = Volatile.Read(ref _ver);
                if ((v & 1) == 0 && Interlocked.CompareExchange(ref _ver, v + 1, v) == v) break;
                spin.SpinOnce(-1);
            }
            _m = m;
            Volatile.Write(ref _ver, v + 2);
            Interlocked.Increment(ref _published);
        }

        /// <summary>최신 매칭 판독(δ=0). 경합 상한 초과 = false(match = default = 판정 전 · 편향 0).</summary>
        public bool TryRead(out PlutoRegimeMatch match)
        {
            for (int k = 0; k < MaxReadSpins; k++)
            {
                long v1 = Volatile.Read(ref _ver);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                match = _m;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _ver) == v1) return true;
            }
            match = default;
            Interlocked.Increment(ref _contended);
            return false;
        }
    }
}
