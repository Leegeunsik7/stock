// ============================================================================
// ArenaTickSource — [v12.18.12-DEV] 아레나 리플레이 틱 테이프(녹화 L2 버퍼 · 합성 압축 테이프) — 판독 δ=0
//
//   RecordedTickSource: 녹화된 OrderBookPayload 배열을 그대로 재생(336B/틱 · 시각 = TimestampTicks 단조 증가 검증).
//   SyntheticTickSource: 결정론 합성 테이프(시드 고정) — 틱당 13B 압축(종목 · 최우선 매수호가 · 매수/매도 기준 잔량)으로 보관하고
//     판독 시 10호가(KRX 호가 단위 · 스프레드 1틱 · 잔량 계단)를 out 구조체에 전개한다. 1M 틱 ≈ 13MB(전개 보관 시 336MB 대비).
//     기본 간격 23.4ms = 1M 틱이 정규장 6.5시간(09:00 ~ 15:30). 압력(pressure) 국면이 잔량 불균형과 가격 표류를 함께 만든다 —
//     AI(정량 시그널 엔진)가 실제로 반응하는 입력이지만 실 시장 통계를 재현하지 않는다(정직: 훈련 · 대결용 합성 데이터).
//   [v12.18.13-DEV] 인덱스 1개 = 호가 스냅샷 · 체결 틱 중 하나 또는 둘 다(체결 → 호가 순서로 적용). 합성 테이프 = 매 틱 체결 1건을 저장 0B 로
//     결정론 파생(틱 인덱스 해시 · 수량 1 ~ 300주 · 압력 방향 우세 · 체결가 = 매수 주도 매도 1호가 / 매도 주도 매수 1호가) — 지정가 대기열 소진 입력.
// ============================================================================
using System;
using MS2E.Infrastructure;

namespace StockManager.Engines.Arena
{
    public abstract class ArenaTickSource
    {
        public abstract int Count { get; }
        /// <summary>틱 i 의 시각(UTC ticks · 단조 비감소).</summary>
        public abstract long TimestampOf(int index);
        /// <summary>틱 i 의 10호가 전개(δ=0). 반환 false = 호가 없는 인덱스(체결 전용).</summary>
        public abstract bool Read(int index, out OrderBookPayload book);
        /// <summary>틱 i 의 체결(δ=0 · 없으면 false). 같은 인덱스의 호가보다 먼저 적용.</summary>
        public virtual bool TryReadTrade(int index, out ArenaTrade trade) { trade = default; return false; }
    }

    public sealed class RecordedTickSource : ArenaTickSource
    {
        private readonly OrderBookPayload[] _books;

        public RecordedTickSource(OrderBookPayload[] books)
        {
            _books = books ?? throw new ArgumentNullException(nameof(books));
            for (int i = 1; i < books.Length; i++)
                if (books[i].TimestampTicks < books[i - 1].TimestampTicks) throw new ArgumentException("틱 시각 역행 @" + i, nameof(books));
        }

        public override int Count => _books.Length;
        public override long TimestampOf(int index) => _books[index].TimestampTicks;
        public override bool Read(int index, out OrderBookPayload book) { book = _books[index]; return true; }
    }

    public sealed class SyntheticTickSource : ArenaTickSource
    {
        public const long DefaultIntervalTicks = 234_000;                       // 23.4ms
        private static readonly long[] Symbols = { 5930, 660, 35420 };
        private static readonly long[] StartPrices = { 70_000, 180_000, 200_000 };

        private readonly byte[] _sym;
        private readonly int[] _bid, _bidQ, _askQ;
        private readonly long _start, _interval;

        private SyntheticTickSource(int count, long startTicks, long intervalTicks)
        {
            _sym = new byte[count]; _bid = new int[count]; _bidQ = new int[count]; _askQ = new int[count];
            _start = startTicks; _interval = intervalTicks;
        }

        public override int Count => _sym.Length;
        public long IntervalTicks => _interval;
        public override long TimestampOf(int index) => _start + index * _interval;

        /// <summary>KRX 호가 단위(2023 개편 기준 · 원).</summary>
        public static long TickSize(long price) => price switch
        {
            < 2_000 => 1, < 5_000 => 5, < 20_000 => 10, < 50_000 => 50, < 200_000 => 100, < 500_000 => 500, _ => 1_000,
        };

        public override bool Read(int index, out OrderBookPayload book)
        {
            book = default;
            long bid = _bid[index], tick = TickSize(bid), ask = bid + tick;
            long bq = _bidQ[index], aq = _askQ[index];
            book.SymbolCode = Symbols[_sym[index]];
            book.TimestampTicks = TimestampOf(index);
            for (int l = 0; l < 10; l++)
            {
                book.BidPrice[l] = bid - l * tick; book.BidQty[l] = bq * (10 + l) / 10;
                book.AskPrice[l] = ask + l * tick; book.AskQty[l] = aq * (10 + l) / 10;
            }
            return true;
        }

        /// <summary>틱 i 의 결정론 파생 체결(저장 0B) — 잔량 우세 쪽이 주도할 확률 3/4 · 수량 1 ~ 300.</summary>
        public override bool TryReadTrade(int index, out ArenaTrade trade)
        {
            ulong h = (ulong)(index + 1) * 0x9E3779B97F4A7C15UL;
            h ^= h >> 29; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 32;
            long bid = _bid[index];
            bool bidHeavy = _bidQ[index] >= _askQ[index];
            int side = (h & 3) != 0 == bidHeavy ? 1 : -1;
            trade = new ArenaTrade { Symbol = Symbols[_sym[index]], Qty = 1 + (long)((h >> 8) % 300), Side = side, PriceWon = side > 0 ? bid + TickSize(bid) : bid };
            return true;
        }

        /// <summary>
        /// 결정론 합성(시드 동일 = 테이프 동일). 종목 3개 라운드로빈 · 종목별 압력 p ∈ [−1, 1] 이 목표(−1 · 0 · +1, 틱당 1/400 확률 전환)로 0.12씩 이동 ·
        /// 잔량 = 기준 × (1 ± p) · 가격 = 확률 (0.1 ± 0.1p) 로 1틱 상승/하락(최저 1,000원).
        /// </summary>
        public static SyntheticTickSource Create(int count, int seed = 20261016, long startTicks = 0, long intervalTicks = DefaultIntervalTicks)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (intervalTicks <= 0) throw new ArgumentOutOfRangeException(nameof(intervalTicks));
            if (startTicks <= 0) startTicks = new DateTime(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc).Ticks;   // 09:00 KST
            var t = new SyntheticTickSource(count, startTicks, intervalTicks);
            int n = Symbols.Length;
            var price = (long[])StartPrices.Clone();
            var p = new double[n];
            var target = new double[n];
            ulong x = (ulong)seed * 0x9E3779B97F4A7C15UL | 1UL;
            double Next() { x ^= x << 13; x ^= x >> 7; x ^= x << 17; return (x >> 11) * (1.0 / (1UL << 53)); }
            for (int i = 0; i < count; i++)
            {
                int s = i % n;
                if (Next() < 1.0 / 400) target[s] = Math.Floor(Next() * 3) - 1;
                p[s] += Math.Clamp(target[s] - p[s], -0.12, 0.12);
                double u = Next();
                long tick = TickSize(price[s]);
                if (u < 0.1 + 0.1 * p[s]) price[s] += tick;
                else if (u > 0.9 + 0.1 * p[s] && price[s] - tick >= 1_000) price[s] -= tick;
                int baseQ = 800 + (int)(Next() * 400);
                t._sym[i] = (byte)s;
                t._bid[i] = (int)price[s];
                t._bidQ[i] = Math.Max(1, (int)(baseQ * (1 + p[s])));
                t._askQ[i] = Math.Max(1, (int)(baseQ * (1 - p[s])));
            }
            return t;
        }
    }
}
