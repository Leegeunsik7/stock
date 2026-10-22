// ============================================================================
// PlutoSentimentFactor — v12.18.63-DEV: 플루토 브레인 1단계 — 종목 1개 뉴스 촉매 판정(불변 값 타입 · 64바이트 인라인 UTF-8 헤드라인)
//
//   Score(−100~+100) · Category · Confidence(0~100) · TimestampUtcTicks(뉴스 발생 UTC) · Source · Seq(버퍼 게시 순번) · 헤드라인 요약(UTF-8 ≤64B 인라인).
//   감쇄: DecayedScore(now) = Score × 2^(−경과/반감기) — 0 방향 절단(|값| 단조 비증가 · 부호 보존) · 24 반감기 이후 0 ·
//     발생 시각이 now 보다 MaxSkew(5분) 넘게 미래 = 0(시계 도메인 불일치 · fail-closed) · 5분 이내 미래 = 경과 0.
//   EffectiveScore(now) = DecayedScore × Confidence / 100(0 방향 절단) — 아레나 시그널에 실리는 값.
//   ★δ=0: 감쇄/판독 = 정수 · double 산술만(할당 0). Headline(문자열 복원)은 콜드 경로(UI · 근거 요약 전용).
//   ★시계: 주입 틱 전용(자체 시계 없음) — now 는 호출 측(아레나 엔진 = 정산 KST → UTC)이 준다.
//   [v12.18.64-DEV] 호출 측 시계 = IVirtualClock(PlutoVirtualClock.cs) 또는 아레나 정산 틱 시각 — 합성/리플레이도 같은 감쇄가 재현된다.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace StockManager.Engines.Cognitive.News
{
    public static class PlutoClock
    {
        /// <summary>뉴스 발생 시각 허용 미래 오차(발행 서버 ↔ 로컬 시계).</summary>
        public static readonly long MaxSkewTicks = TimeSpan.FromMinutes(5).Ticks;
        /// <summary>KST = UTC+9(서머타임 없음) — 아레나 정산 KST → 뉴스 UTC 도메인 변환.</summary>
        public static readonly long KstOffsetTicks = TimeSpan.FromHours(9).Ticks;
        /// <summary>이 반감기 배수 이상 경과 = 0(2^-24 ≈ 6e-8 — 정수 점수에서 이미 0).</summary>
        public const int ExpiryHalfLives = 24;
    }

    /// <summary>헤드라인 요약 UTF-8 인라인 버퍼(64바이트 · 힙 0).</summary>
    [InlineArray(PlutoDigest.Capacity)]
    public struct PlutoDigestBytes
    {
        private byte _b0;
    }

    public static class PlutoDigest
    {
        public const int Capacity = 64;

        /// <summary>헤드라인 → UTF-8 최대 64바이트(코드 포인트 경계에서 절단 · 깨진 서로게이트 = U+FFFD). 반환 = 기록 바이트 수.</summary>
        public static int Encode(ReadOnlySpan<char> headline, Span<byte> destination)
        {
            if (destination.Length > Capacity) destination = destination[..Capacity];
            System.Text.Unicode.Utf8.FromUtf16(headline, destination, out _, out int written, replaceInvalidSequences: true, isFinalBlock: true);
            return written;
        }
    }

    public readonly struct PlutoSentimentFactor
    {
        public readonly long Symbol;
        public readonly long Seq;
        public readonly long TimestampUtcTicks;
        public readonly int Score;
        public readonly int Confidence;
        public readonly CatalystCategory Category;
        public readonly PlutoSource Source;
        public readonly byte DigestLength;
        private readonly PlutoDigestBytes _digest;

        /// <summary>판정 1건. 점수/확신도는 범위로 포화 · Noise 는 점수 0 강제 · 헤드라인 UTF-8 은 64바이트로 절단(호출 측이 경계 절단 권장).</summary>
        public PlutoSentimentFactor(long symbol, long timestampUtcTicks, int score, int confidence, CatalystCategory category, PlutoSource source,
            ReadOnlySpan<byte> headlineUtf8, long seq = 0)
        {
            Symbol = symbol;
            Seq = seq;
            TimestampUtcTicks = timestampUtcTicks;
            Category = category;
            Source = source;
            Score = category == CatalystCategory.Noise ? 0 : Math.Clamp(score, -CatalystText.MaxScore, CatalystText.MaxScore);
            Confidence = Math.Clamp(confidence, 0, 100);
            int n = Math.Min(headlineUtf8.Length, PlutoDigest.Capacity);
            DigestLength = (byte)n;
            _digest = default;
            Span<byte> d = _digest;
            headlineUtf8[..n].CopyTo(d);
        }

        private PlutoSentimentFactor(in PlutoSentimentFactor src, long seq)
        {
            this = src;
            Seq = seq;
        }

        /// <summary>버퍼 게시 순번만 바꾼 사본.</summary>
        internal PlutoSentimentFactor WithSeq(long seq) => new(in this, seq);

        /// <summary>무결성: 종목 · 시각 양수 · 점수가 유형 밴드 안 · 확신도 0~100.</summary>
        public bool IsWellFormed =>
            Symbol > 0 && TimestampUtcTicks > 0 && Confidence is >= 0 and <= 100 && CatalystText.InBand(Category, Score);

        /// <summary>시간 감쇄 점수(0 방향 절단). 노이즈 · 무효 시각 · 과도한 미래 = 0.</summary>
        public int DecayedScore(long nowUtcTicks)
        {
            if (Score == 0 || Category == CatalystCategory.Noise || TimestampUtcTicks <= 0 || nowUtcTicks <= 0) return 0;
            long age = nowUtcTicks - TimestampUtcTicks;
            if (age < -PlutoClock.MaxSkewTicks) return 0;
            if (age <= 0) return Score;
            long hl = CatalystText.HalfLifeTicks(Category);
            if (hl <= 0 || age >= hl * PlutoClock.ExpiryHalfLives) return 0;
            return (int)(Score * Math.Pow(2.0, -(double)age / hl));
        }

        /// <summary>확신도 가중 감쇄 점수(0 방향 절단) — 아레나 시그널 입력.</summary>
        public int EffectiveScore(long nowUtcTicks) => DecayedScore(nowUtcTicks) * Confidence / 100;

        /// <summary>헤드라인 요약 UTF-8 → dest 복사. 반환 = 복사 바이트 수(dest 부족 = 잘린 길이).</summary>
        public int CopyDigest(Span<byte> destination)
        {
            ReadOnlySpan<byte> s = _digest;
            int n = Math.Min(DigestLength, destination.Length);
            s[..n].CopyTo(destination);
            return n;
        }

        /// <summary>헤드라인 요약 문자열(콜드 경로 — 호출마다 할당).</summary>
        public string Headline
        {
            get
            {
                ReadOnlySpan<byte> s = _digest;
                return Encoding.UTF8.GetString(s[..DigestLength]);
            }
        }
    }

    /// <summary>
    /// 아레나 체결 시점에 판독된 뉴스 촉매(근거 카드 결속용 · 값 타입). default = 뉴스 없음.
    /// IsShock = 치명적 구조적 악재 ∧ 실효 점수 ≤ ShockScore → AI 신규 진입 차단 · 보유 즉시 청산.
    /// </summary>
    public readonly struct PlutoNewsTag
    {
        public const int ShockScore = -60;
        /// <summary>실효 점수 1점 = 시그널 100bp(±100 → ±10000bp).</summary>
        public const int BpPerPoint = 100;

        public readonly PlutoSentimentFactor Factor;
        public readonly int DecayedScore;
        public readonly int EffectiveScore;
        public readonly long AgeTicks;

        public PlutoNewsTag(in PlutoSentimentFactor factor, long nowUtcTicks)
        {
            Factor = factor;
            DecayedScore = factor.DecayedScore(nowUtcTicks);
            EffectiveScore = DecayedScore * factor.Confidence / 100;
            AgeTicks = Math.Max(0, nowUtcTicks - factor.TimestampUtcTicks);
        }

        public bool HasCatalyst => EffectiveScore != 0 && Factor.Category != CatalystCategory.Noise;
        public bool IsShock => Factor.Category == CatalystCategory.StructuralNegative && EffectiveScore <= ShockScore;
        public int NewsBp => EffectiveScore * BpPerPoint;
        public int AgeMinutes => (int)Math.Min(int.MaxValue, AgeTicks / TimeSpan.TicksPerMinute);

        /// <summary>무결성: 뉴스 없음(종목 0 · 점수 0) 또는 판정 무결 ∧ |실효| ≤ |감쇄| ≤ |원점수| ∧ 부호 일치.</summary>
        public bool IsWellFormed => Factor.Symbol == 0
            ? DecayedScore == 0 && EffectiveScore == 0
            : Factor.IsWellFormed && Math.Abs(EffectiveScore) <= Math.Abs(DecayedScore) && Math.Abs(DecayedScore) <= Math.Abs(Factor.Score)
              && (long)DecayedScore * Factor.Score >= 0 && (long)EffectiveScore * DecayedScore >= 0;
    }
}
