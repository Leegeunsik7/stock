// ============================================================================
// ProtectiveLimitOrderConverter — [보호 지정가 · 추천 A] 시장가 → 참조가 기반 보호 지정가 원자 변환
// KrxTickAligner                — [방향 A] Zero-Alloc Tick-Size Alignment(QPrice raw · ReadOnlySpan 정적 테이블)
//
//  왜(실측): 피드 연동 이후에도 시장가 주문은 OrderDrainWorker 에서 그대로 PaperKind.Market 으로 브로커에 나갔다 — 라우터는
//   '신선한 참조가 존재'만 확인했을 뿐(limit = 참조가로 슬리피지 0 판정) 실 체결가 상한이 없었다. 급변 구간에서 시장가는
//   호가 공백을 끝까지 쓸어 체결가가 무한정 미끄러질 수 있다.
//  교정: 시장가 유입 시 송출 주문 자체를 지정가로 바꾼다 — 매수 상한 = 참조가 × (1 + offset), 매도 하한 = 참조가 × (1 − offset).
//   · 호가 단위 정렬은 '보호 방향'으로: 매수 상한은 내림(더 비싸게 사지 않음) · 매도 하한은 올림(더 싸게 팔지 않음).
//   · 가격제한폭(기준가 ±30%) 주입 시 상한가 이하/하한가 이상으로 클램프(상한가 = 기준가×1.3 내림 정렬 · 하한가 = ×0.7 올림 정렬).
//   · 전 연산 Int128 · 포화(오버플로 = 0 반환 = 변환 실패 = 미송출). 결과 ≤0 · 참조가 ≤0 → 0(fail-closed).
//  ※ 정렬 결과는 슬리피지 상한을 '넘지 않는' 쪽이라 offset 이 호가 단위보다 작으면 매수 상한 < 참조가가 될 수 있다(미체결 = 안전 측).
//
//  KrxTickAligner(방향 A): KrxTickLadder(decimal · 튜플 배열)와 같은 KRX 가격대 규칙을 QPrice raw(10^8) long 으로.
//   정적 readonly long[] 2개(타입 초기화 1회 할당) 위의 ReadOnlySpan 7구간 선형 스캔 — 분기 ≤7 · 나눗셈 1 · 호출당 힙 0.
//   ※ 실측: `static ReadOnlySpan<long> X => new long[]{…}` 는 이 빌드 설정에서 데이터 블롭(RuntimeHelpers.CreateSpan)으로 내려가지
//     않고 호출마다 배열을 새로 만들었다(호출당 144B — δ=0 테스트가 포착). 그래서 정적 필드로 고정.
//   경계값(2천·5천·2만·5만·20만·50만)은 상위 구간 틱의 배수라 내림/올림 결과는 언제나 유효 격자점이다.
// ============================================================================
using System;
using MS2E.Core;

namespace StockManager.Engines
{
    public static class KrxTickAligner
    {
        public const long Scale = 100_000_000L;

        // 구간 상한(미만 · raw) / 구간 호가단위(raw) — KrxTickLadder.Bands 와 동일 규칙(테스트가 전 구간 대조)
        private static readonly long[] s_upperExclusiveRaw =
        {
            2_000L * Scale, 5_000L * Scale, 20_000L * Scale, 50_000L * Scale, 200_000L * Scale, 500_000L * Scale, long.MaxValue,
        };
        private static readonly long[] s_tickRaw =
        {
            1L * Scale, 5L * Scale, 10L * Scale, 50L * Scale, 100L * Scale, 500L * Scale, 1_000L * Scale,
        };
        private static ReadOnlySpan<long> UpperExclusiveRaw => s_upperExclusiveRaw;
        private static ReadOnlySpan<long> TickRaw => s_tickRaw;

        /// <summary>가격(raw)의 KRX 호가단위(raw). 가격 ≤0 → 0(무효).</summary>
        public static long TickFor(long priceRaw)
        {
            if (priceRaw <= 0) return 0;
            ReadOnlySpan<long> up = UpperExclusiveRaw, tick = TickRaw;
            for (int i = 0; i < up.Length; i++)
                if (priceRaw < up[i]) return tick[i];
            return tick[^1];
        }

        /// <summary>호가 격자로 내림(매수 상한 보호 정렬). ≤0 → 0.</summary>
        public static long AlignDown(long priceRaw)
        {
            long t = TickFor(priceRaw);
            return t == 0 ? 0 : priceRaw - priceRaw % t;
        }

        /// <summary>호가 격자로 올림(매도 하한 보호 정렬). ≤0 → 0 · 포화(long.MaxValue 근방 오버플로 = 0 = 무효).</summary>
        public static long AlignUp(long priceRaw)
        {
            long t = TickFor(priceRaw);
            if (t == 0) return 0;
            long r = priceRaw % t;
            if (r == 0) return priceRaw;
            Int128 up = (Int128)priceRaw + (t - r);
            return up > long.MaxValue ? 0 : (long)up;
        }

        public static bool IsAligned(long priceRaw) => priceRaw > 0 && priceRaw % TickFor(priceRaw) == 0;
    }

    public sealed class ProtectiveLimitOrderConverter
    {
        public const long PriceBandBps = 3_000;   // KRX 가격제한폭 ±30%

        private readonly long _offsetBps;
        private long _converted, _failed, _bandClamped;

        /// <param name="offsetBps">참조가 대비 보호 오프셋(1~1000bps · 기본 30).</param>
        public ProtectiveLimitOrderConverter(long offsetBps = 30)
        {
            if (offsetBps < 1 || offsetBps > 1_000) throw new ArgumentOutOfRangeException(nameof(offsetBps), "1~1000bps");
            _offsetBps = offsetBps;
        }

        public long OffsetBps => _offsetBps;
        public long Converted => System.Threading.Interlocked.Read(ref _converted);
        public long Failed => System.Threading.Interlocked.Read(ref _failed);
        public long BandClamped => System.Threading.Interlocked.Read(ref _bandClamped);

        /// <summary>
        /// 보호 지정가(raw) 산출(δ=0). basePriceRaw(전일 종가 기준가) &gt; 0 이면 가격제한폭 클램프. 반환 0 = 변환 불가(미송출).
        /// </summary>
        public long Convert(OrderSide side, long referenceRaw, long basePriceRaw = 0)
        {
            long px = Compute(side, referenceRaw, basePriceRaw, _offsetBps, out bool clamped);
            if (px <= 0) { System.Threading.Interlocked.Increment(ref _failed); return 0; }
            if (clamped) System.Threading.Interlocked.Increment(ref _bandClamped);
            System.Threading.Interlocked.Increment(ref _converted);
            return px;
        }

        /// <summary>순수 산출(δ=0 · 상태 없음). 실패 = 0.</summary>
        public static long Compute(OrderSide side, long referenceRaw, long basePriceRaw, long offsetBps, out bool bandClamped)
        {
            bandClamped = false;
            if (referenceRaw <= 0 || offsetBps < 0) return 0;
            bool buy = side == OrderSide.Buy;
            Int128 raw = (Int128)referenceRaw * (buy ? 10_000 + offsetBps : 10_000 - offsetBps) / 10_000;
            if (raw <= 0 || raw > long.MaxValue) return 0;
            long px = buy ? KrxTickAligner.AlignDown((long)raw) : KrxTickAligner.AlignUp((long)raw);
            if (px <= 0) return 0;
            if (basePriceRaw > 0)
            {
                Int128 upper128 = (Int128)basePriceRaw * (10_000 + PriceBandBps) / 10_000;
                Int128 lower128 = (Int128)basePriceRaw * (10_000 - PriceBandBps) / 10_000;
                long upper = upper128 > long.MaxValue ? 0 : KrxTickAligner.AlignDown((long)upper128);   // 상한가: 내림 정렬
                long lower = KrxTickAligner.AlignUp((long)lower128);                                     // 하한가: 올림 정렬
                if (upper <= 0 || lower <= 0 || lower > upper) return 0;
                // 보호 방향 클램프만 허용: 매수 상한은 상한가로 낮추고, 매도 하한은 하한가로 올린다.
                // 반대 방향 이탈(매수 상한 < 하한가 · 매도 하한 > 상한가)은 참조가 자체가 가격제한폭 밖 = 이상치 → 변환 거부.
                if (buy)
                {
                    if (px < lower) return 0;
                    if (px > upper) { px = upper; bandClamped = true; }
                }
                else
                {
                    if (px > upper) return 0;
                    if (px < lower) { px = lower; bandClamped = true; }
                }
            }
            return px;
        }
    }
}
