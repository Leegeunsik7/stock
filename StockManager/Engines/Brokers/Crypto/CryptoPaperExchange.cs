// ============================================================================
// CryptoPaperExchange — [v12.18.54-DEV] 업비트 KRW-BTC 인메모리 가상 체결기(순수 모의 · 단일 라이터 · seqlock 판독 · δ=0)
//
//  ★순수 인메모리: 네트워크 · 소켓 · 브로커 어댑터 · 디스패처 · 무장 키 참조 0 — 체결은 메모리 잔고만 바꾼다(liveArmed:false 와 무관하게 송출 경로 자체가 없음).
//  단위(전부 long raw 10^8): KRW 예수금 · 가격(원/BTC) · 수량(BTC · 1 raw = 1 satoshi = 업비트 수량 소수 8자리). 곱셈 = Int128 중간값(1.5억원 × 1BTC = 1.5e24 raw² → long 초과).
//  초기 상태: 예수금 100,000,000원 · BTC 0. 수수료 기본 5bp(0.05% · 업비트 KRW 마켓) · 슬리피지 기본 2bp(체결 깊이 미모델 대체 · 보수적).
//  체결 모델(즉시 체결 또는 거부 · 미체결 대기 주문 없음 · 부분 체결 없음 · L1 잔량 무시):
//   시장가 매수 = AlignUp(매도1호가 × (1 + 슬리피지)) · 시장가 매도 = AlignDown(매수1호가 × (1 − 슬리피지))
//   지정가(IOC) = 호가 격자 정렬 필수 · 매수 L < 매도1호가 / 매도 L > 매수1호가 = NotMarketable · 체결가 = 매수 min(L, 슬립 매도호가) / 매도 max(L, 슬립 매수호가)
//   대금 = 매수 올림 / 매도 내림(원 미만 raw) · 수수료 = 대금 × bp 올림(거래소 유리 = 보수적) · 매수 차감 = 대금 + 수수료 · 매도 입금 = 대금 − 수수료
//  fail-closed 거부(잔고 무변경): 수량 ≤0 · 가격 무효/비정렬 · 호가 없음/교차/비정렬 · 호가 지연 초과/미래 시각 · 세션 무효(시각 ≤0) ·
//   최소 주문 5,000원 미만 · 예수금 부족 · 미보유/보유 초과 매도 · 산술 범위 초과 · ★동시 라이터(CAS 실패 = 대기/재시도 없이 즉시 거부 — 중복 체결 방지).
//  동시성: 라이터(UpdateQuote · Submit · Reset)는 CAS 진입권 1개 · 판독(Snapshot)은 seqlock(짝=안정 · 홀=기입 중) 재시도 — 락 0 · 할당 0.
//  시각: 자체 시계 없음 — 호출자가 UTC 유닉스 마이크로초를 넘긴다(결정론 테스트 · 리플레이 동일 결과).
//  [v12.18.66-DEV] 체결가/대금/수수료 산식을 순수 정적 함수(SlippedMarketPrice · NotionalWithFee)로 분리 — UpbitCryptoArenaEngine 듀얼 원장이 같은 매칭 산식을 공유(동작 불변).
// ============================================================================
using System;
using System.Threading;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Brokers.Crypto
{
    public enum CryptoOrderSide : byte { Buy = 1, Sell = 2 }

    public enum CryptoOrderType : byte { Market = 1, Limit = 2 }    // Limit = IOC(즉시 체결 또는 거부)

    public enum CryptoPaperStatus : byte
    {
        None = 0,                   // default(구조체) ≠ 체결
        Filled = 1,
        InvalidSide = 10,
        InvalidQuantity = 11,
        InvalidPrice = 12,          // 지정가 ≤0 · 호가 격자 비정렬
        NoQuote = 13,
        StaleQuote = 14,            // 호가 시각이 최대 지연 초과 · 또는 현재 시각보다 미래(시계 역행)
        SessionInvalid = 15,
        NotMarketable = 16,
        BelowMinimumOrder = 17,
        InsufficientKrw = 18,
        InsufficientBtc = 19,
        Overflow = 20,
        ConcurrentWriter = 21,
    }

    /// <summary>체결/거부 결과(값 형식 · 거부 시 잔고 필드 = 거부 시점 잔고).</summary>
    public readonly record struct CryptoPaperFill(CryptoPaperStatus Status, CryptoOrderSide Side, long PriceRaw, long QtyRaw, long NotionalRaw, long FeeRaw, long KrwAfterRaw, long BtcAfterRaw)
    {
        public bool Filled => Status == CryptoPaperStatus.Filled;
    }

    /// <summary>가상 잔고 스냅샷(seqlock 일관 판독 — 모든 필드가 같은 기입 세대).</summary>
    public readonly record struct CryptoPaperBalance(long KrwRaw, long BtcRaw, long FeesPaidRaw, long FillCount, long BidRaw, long AskRaw, long QuoteUtcMicros, long LastFillPriceRaw)
    {
        /// <summary>평가 예수금(KRW raw) = 예수금 + 보유 × 매수1호가(청산 측 · 내림). 호가 없음 = 예수금만.</summary>
        public long EquityAtBidRaw()
        {
            if (BidRaw <= 0 || BtcRaw <= 0) return KrwRaw;
            Int128 v = (Int128)KrwRaw + (Int128)BtcRaw * BidRaw / UpbitTickAligner.Scale;
            return v > long.MaxValue ? long.MaxValue : (long)v;
        }
    }

    public sealed class CryptoPaperExchange
    {
        public const long Scale = UpbitTickAligner.Scale;
        public const long InitialKrwRaw = 100_000_000L * Scale;            // 1억 원
        public const int DefaultFeeBp = 5;                                   // 0.05%
        public const int DefaultSlippageBp = 2;
        public const long DefaultMaxQuoteAgeMicros = 2_000_000;              // 2초
        public const long MinOrderKrwRaw = 5_000L * Scale;                   // 업비트 KRW 마켓 최소 주문 금액
        private const int BpDenominator = 10_000;
        private static readonly Int128 NotionalCap = long.MaxValue / 2;    // 대금 + 수수료 · 잔고 가산이 long 을 넘지 않는 상한

        private readonly int _feeBp, _slippageBp;
        private readonly long _maxQuoteAgeMicros;
        private CryptoPaperBalance _state;
        private long _seq;          // seqlock(짝=안정, 홀=기입 중)
        private int _writer;        // 라이터 진입권(0 = 비어 있음)
        private long _rejects;

        public CryptoPaperExchange(int feeBp = DefaultFeeBp, int slippageBp = DefaultSlippageBp, long maxQuoteAgeMicros = DefaultMaxQuoteAgeMicros)
        {
            if (feeBp < 0 || feeBp >= BpDenominator) throw new ArgumentOutOfRangeException(nameof(feeBp));
            if (slippageBp < 0 || slippageBp >= BpDenominator) throw new ArgumentOutOfRangeException(nameof(slippageBp));
            if (maxQuoteAgeMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxQuoteAgeMicros));
            _feeBp = feeBp; _slippageBp = slippageBp; _maxQuoteAgeMicros = maxQuoteAgeMicros;
            _state = new CryptoPaperBalance(InitialKrwRaw, 0, 0, 0, 0, 0, 0, 0);
        }

        public int FeeBp => _feeBp;
        public int SlippageBp => _slippageBp;
        public long RejectCount => Interlocked.Read(ref _rejects);

        /// <summary>일관 스냅샷(δ=0 · seqlock 재시도).</summary>
        public CryptoPaperBalance Snapshot()
        {
            SpinWait sw = default;
            while (true)
            {
                long s1 = Volatile.Read(ref _seq);
                if ((s1 & 1L) != 0L) { sw.SpinOnce(); continue; }       // 기입 중 → 재시도
                CryptoPaperBalance snap = _state;
                Interlocked.MemoryBarrier();                             // 64B 복사가 s2 판독 뒤로 밀리지 않게(획득 의미론만으론 선행 판독 지연 허용)
                long s2 = Volatile.Read(ref _seq);
                if (s1 == s2) return snap;
                sw.SpinOnce();
            }
        }

        /// <summary>L1 호가 갱신. 매수1/매도1 > 0 · 매수 < 매도 · 둘 다 호가 격자 정렬 · 시각 > 0 이 아니면 거부(기존 호가 유지 · false).</summary>
        public bool UpdateQuote(long bidRaw, long askRaw, long utcMicros)
        {
            if (!UpbitTickAligner.IsAligned(bidRaw) || !UpbitTickAligner.IsAligned(askRaw) || bidRaw >= askRaw
                || !CryptoContinuousSession.IsContinuousTradingUtcMicros(utcMicros)) return false;
            if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return false;
            try
            {
                var s = _state;
                Publish(s with { BidRaw = bidRaw, AskRaw = askRaw, QuoteUtcMicros = utcMicros });
                return true;
            }
            finally { Volatile.Write(ref _writer, 0); }
        }

        /// <summary>가상 주문 1건(즉시 체결 또는 거부). 지정가 limitPriceRaw 는 Limit 에서만 사용(Market = 무시).</summary>
        public CryptoPaperFill Submit(CryptoOrderSide side, CryptoOrderType type, long qtyRaw, long limitPriceRaw, long nowUtcMicros)
        {
            if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0)
            {
                var cur = Snapshot();
                return Reject(CryptoPaperStatus.ConcurrentWriter, side, cur);
            }
            try
            {
                var s = _state;
                var st = Evaluate(s, side, type, qtyRaw, limitPriceRaw, nowUtcMicros, out long px, out long notional, out long fee);
                if (st != CryptoPaperStatus.Filled) return Reject(st, side, s);

                long krw = side == CryptoOrderSide.Buy ? s.KrwRaw - (notional + fee) : s.KrwRaw + (notional - fee);
                long btc = side == CryptoOrderSide.Buy ? s.BtcRaw + qtyRaw : s.BtcRaw - qtyRaw;
                Publish(s with { KrwRaw = krw, BtcRaw = btc, FeesPaidRaw = s.FeesPaidRaw + fee, FillCount = s.FillCount + 1, LastFillPriceRaw = px });
                return new CryptoPaperFill(CryptoPaperStatus.Filled, side, px, qtyRaw, notional, fee, krw, btc);
            }
            finally { Volatile.Write(ref _writer, 0); }
        }

        /// <summary>초기 상태 복원(예수금 1억 · BTC 0 · 호가 · 통계 0). 동시 라이터 = false(무변경).</summary>
        public bool Reset()
        {
            if (Interlocked.CompareExchange(ref _writer, 1, 0) != 0) return false;
            try
            {
                Publish(new CryptoPaperBalance(InitialKrwRaw, 0, 0, 0, 0, 0, 0, 0));
                Interlocked.Exchange(ref _rejects, 0);
                return true;
            }
            finally { Volatile.Write(ref _writer, 0); }
        }

        // ── 내부 ──

        private CryptoPaperStatus Evaluate(in CryptoPaperBalance s, CryptoOrderSide side, CryptoOrderType type, long qty, long limit, long now,
                                           out long px, out long notional, out long fee)
        {
            px = 0; notional = 0; fee = 0;
            if (side != CryptoOrderSide.Buy && side != CryptoOrderSide.Sell) return CryptoPaperStatus.InvalidSide;
            if (type != CryptoOrderType.Market && type != CryptoOrderType.Limit) return CryptoPaperStatus.InvalidPrice;
            if (qty <= 0) return CryptoPaperStatus.InvalidQuantity;
            if (type == CryptoOrderType.Limit && !UpbitTickAligner.IsAligned(limit)) return CryptoPaperStatus.InvalidPrice;
            if (!CryptoContinuousSession.IsContinuousTradingUtcMicros(now)) return CryptoPaperStatus.SessionInvalid;
            if (s.BidRaw <= 0 || s.AskRaw <= 0) return CryptoPaperStatus.NoQuote;
            if (now < s.QuoteUtcMicros || now - s.QuoteUtcMicros > _maxQuoteAgeMicros) return CryptoPaperStatus.StaleQuote;
            if (side == CryptoOrderSide.Sell && qty > s.BtcRaw) return CryptoPaperStatus.InsufficientBtc;

            if (side == CryptoOrderSide.Buy && type == CryptoOrderType.Limit && limit < s.AskRaw) return CryptoPaperStatus.NotMarketable;
            if (side == CryptoOrderSide.Sell && type == CryptoOrderType.Limit && limit > s.BidRaw) return CryptoPaperStatus.NotMarketable;
            var st = SlippedMarketPrice(side, s.BidRaw, s.AskRaw, _slippageBp, out long p);
            if (st != CryptoPaperStatus.Filled) return st;
            if (side == CryptoOrderSide.Buy) px = type == CryptoOrderType.Limit && limit < p ? limit : p;
            else px = type == CryptoOrderType.Limit && limit > p ? limit : p;

            st = NotionalWithFee(side, px, qty, _feeBp, out notional, out fee);
            if (st != CryptoPaperStatus.Filled) return st;

            if (side == CryptoOrderSide.Buy)
            {
                if ((Int128)notional + fee > s.KrwRaw) return CryptoPaperStatus.InsufficientKrw;
            }
            else if ((Int128)s.KrwRaw + notional - fee > long.MaxValue) return CryptoPaperStatus.Overflow;
            return CryptoPaperStatus.Filled;
        }

        /// <summary>
        /// [v12.18.66-DEV] 시장가 슬리피지 체결가(순수 · δ=0 · 업비트 아레나 공용): 매수 = AlignUp(매도1 × (1 + bp) 올림) · 매도 = AlignDown(매수1 × (1 − bp) 내림).
        /// 매수 산술 범위 초과/정렬 0 = Overflow · 매도 정렬 0 = InvalidPrice · 성공 = Filled.
        /// </summary>
        public static CryptoPaperStatus SlippedMarketPrice(CryptoOrderSide side, long bidRaw, long askRaw, int slippageBp, out long priceRaw)
        {
            priceRaw = 0;
            if (side == CryptoOrderSide.Buy)
            {
                Int128 slip = ((Int128)askRaw * (BpDenominator + slippageBp) + (BpDenominator - 1)) / BpDenominator;   // 올림
                if (slip > long.MaxValue) return CryptoPaperStatus.Overflow;
                long p = UpbitTickAligner.AlignUp((long)slip);
                if (p <= 0) return CryptoPaperStatus.Overflow;
                priceRaw = p;
                return CryptoPaperStatus.Filled;
            }
            long down = (long)((Int128)bidRaw * (BpDenominator - slippageBp) / BpDenominator);                         // 내림
            long q = UpbitTickAligner.AlignDown(down);
            if (q <= 0) return CryptoPaperStatus.InvalidPrice;
            priceRaw = q;
            return CryptoPaperStatus.Filled;
        }

        /// <summary>
        /// [v12.18.66-DEV] 대금(매수 올림 · 매도 내림 · 원 미만 raw) + 수수료(대금 × bp 올림) — 순수 · δ=0 · 업비트 아레나 공용.
        /// 대금 상한 초과 = Overflow · 최소 주문 5,000원 미만 = BelowMinimumOrder · 성공 = Filled.
        /// </summary>
        public static CryptoPaperStatus NotionalWithFee(CryptoOrderSide side, long priceRaw, long qtyRaw, int feeBp, out long notionalRaw, out long feeRaw)
        {
            notionalRaw = 0; feeRaw = 0;
            Int128 prod = (Int128)priceRaw * qtyRaw;
            Int128 n = side == CryptoOrderSide.Buy ? (prod + (Scale - 1)) / Scale : prod / Scale;                         // 매수 올림 · 매도 내림
            if (n > NotionalCap) return CryptoPaperStatus.Overflow;
            if (n < MinOrderKrwRaw) return CryptoPaperStatus.BelowMinimumOrder;
            Int128 f = (n * feeBp + (BpDenominator - 1)) / BpDenominator;                                                 // 수수료 올림
            notionalRaw = (long)n; feeRaw = (long)f;
            return CryptoPaperStatus.Filled;
        }

        private CryptoPaperFill Reject(CryptoPaperStatus st, CryptoOrderSide side, in CryptoPaperBalance s)
        {
            Interlocked.Increment(ref _rejects);
            return new CryptoPaperFill(st, side, 0, 0, 0, 0, s.KrwRaw, s.BtcRaw);
        }

        // seqlock 기입: 짝→홀→기입→짝(라이터 진입권 보유 중에만 호출).
        private void Publish(in CryptoPaperBalance next)
        {
            Interlocked.Increment(ref _seq);    // → 홀(기입 중)
            _state = next;
            Interlocked.Increment(ref _seq);    // → 짝(안정)
        }
    }
}
