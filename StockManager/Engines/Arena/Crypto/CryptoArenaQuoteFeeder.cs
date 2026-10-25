// ============================================================================
// CryptoArenaQuoteFeeder — v12.18.66-DEV: 크립토 아레나 시세 주입 경로(업비트 공개 WebSocket 프레임 · 합성 데모 시세 → UpbitCryptoArenaEngine 단일 작성자)
//
//   ① 생산자(임의 스레드 · 다중 가능) → 종목별 최신값 슬롯(CryptoArenaUniverse 4 · 고정 인덱스)에 슬롯별 다중 작성자 seqlock(짝수 → CAS 홀수 → 기록 → 짝수+2).
//      호가 = 최신값만 의미(상태) · 체결 = 테이커 매수/매도 누적량(소비자가 차분) — 큐잉 · 재시도 0 · 무효 값 = 폐기 계수.
//      OnUpbitFrame(in UpbitStreamFrame) = UpbitFrameTap 서명 — UpbitWebSocketFeedAdapter(KRW-BTC 구독) 수신 스레드가 판독 직후 동기 호출(δ=0).
//        Orderbook = L1 매수1/매도1 가격 · 잔량(raw 10^8 · 호가 격자 정렬 · 0 < 매수 < 매도) · Trade = 체결가 · 체결량 · ask_bid(BID = 매수 테이커).
//      OnFrame(index, frame) = 같은 판독을 임의 마켓 슬롯에 · OnBook(index, …) = 호가 직접 주입.
//      [v12.18.74-DEV] OnUpbitMarketFrame(index, in frame) = UpbitMarketFrameTap 서명 — 어댑터가 CryptoArenaUniverse.StreamMarkets(4종 · 표 순서 = 슬롯 순서)를
//        구독하고 시장 상태가 수용한 프레임만 통지(재접속 직전 체결 재전송 = 누적 체결량 이중 가산 0).
//      [v12.18.76-DEV] OnUpbitMarketDepthFrame(index, in frame, in depth) = UpbitMarketDepthTap 서명 — 호가 프레임의 15호가 잔량 열을 L1 과 같은 슬롯 쓰기 창에
//        원자 기록(슬롯 = 고정 인라인 버퍼 · 힙 0) → Pump 가 엔진 OnDepthQuote 로 전달(매수벽/매도벽/허매수 실가동). L1 전용 경로(OnFrame/OnBook) = 잔량 열 0단(기존 동작).
//   ② 소비자(정산 스레드 = 대시보드 허브 50ms 펌프 1개): Pump(utcTicks) — 버전이 바뀐 슬롯만 엔진 OnTrade(누적 차분) · OnQuote ·
//      갱신 0 = Drain(휴먼 큐 · 신선도 판정). 엔진 시각 = 펌프 시각(로컬 UTC 마이크로초 — 거래소 시계 편차 무관 · 무수신 2초 = StaleQuote).
//   ③ 합성 데모 시세(SelectSource(Synthetic)): 스트림 미결선 PC 에서도 24/7 대결을 시연하는 결정론 난수 보행(4종 · 업비트 호가 격자 ·
//      가격 비례 스텝(≈2.5bp) · 종목별 추세 국면 · 국면 방향 잔량 기울기 · 국면 편향 테이커 체결). 펌프 1회 = 가상 1초(50ms 펌프 → ×20).
//      가상 1시간마다 시장 맥락(김치프리미엄 · 글로벌 BTC 24h 등락) 재추첨. ★합성은 UI 가 '합성 시세(데모)'로 명시 렌더(실시세 위장 0).
//   ④ 출처 전환 = 다음 펌프에서 엔진 원장 · 종목 상태 초기화(SetCapital) 후 적용 — 출처 간 가격/EWMA/체결 강도 오염 0.
//
//   ★δ=0: OnUpbitFrame/OnFrame/OnBook/Pump 힙 할당 0(고정 배열 · 값 타입). ★liveArmed:false — 시세 수신 · 가상 원장 주입뿐(업비트 주문 송출 수단 0).
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Feeds.Crypto;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Arena.Crypto
{
    public sealed class CryptoArenaQuoteFeeder
    {
        public const int MaxSlots = CryptoArenaUniverse.Count;
        public const long Scale = UpbitTickAligner.Scale;
        public static readonly long SyntheticStepMicros = 1_000_000;                         // 펌프 1회 = 가상 1초(50ms 펌프 → ×20)
        public static readonly long SyntheticContextMicros = 3_600L * 1_000_000;             // 가상 1시간마다 시장 맥락 재추첨
        /// <summary>합성 기준 최우선 잔량(코인 raw · BTC 0.5 · ETH 10 · XRP 30,000 · SOL 100).</summary>
        private static readonly long[] s_synBaseQty = { 50_000_000, 10 * Scale, 30_000 * Scale, 100 * Scale };

        private struct Slot { public long Ver, Bid, Ask, BidQty, AskQty, BuyCum, SellCum, TradePx; public int Tiers; public UpbitDepthLevels BidDepth, AskDepth; }

        private readonly UpbitCryptoArenaEngine _engine;
        private readonly Slot[] _slots = new Slot[MaxSlots];
        private long _dropped, _streamUpdates;
        private int _requested = CryptoArenaFeed.UpbitStream;
        private UpbitWebSocketFeedAdapter? _stream;

        // 소비자(정산 스레드) 전용
        private readonly long[] _seenVer = new long[MaxSlots], _seenBuy = new long[MaxSlots], _seenSell = new long[MaxSlots];
        private int _applied = CryptoArenaFeed.UpbitStream;
        private long _pumps;

        // 합성 상태(소비자 전용)
        private readonly long[] _synPx = new long[MaxSlots];
        private readonly int[] _synDrift = new int[MaxSlots], _synRegimeLeft = new int[MaxSlots];
        private ulong _rng;
        private long _virtUtcMicros, _nextContextMicros;

        public CryptoArenaQuoteFeeder(UpbitCryptoArenaEngine engine, ulong seed = 0x5EED_0012_1866UL)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _rng = seed == 0 ? 1 : seed;
        }

        public UpbitCryptoArenaEngine Engine => _engine;
        /// <summary>요청된 출처(CryptoArenaFeed.UpbitStream · Synthetic).</summary>
        public int RequestedSource => Volatile.Read(ref _requested);
        /// <summary>엔진에 적용 중인 출처(다음 펌프에서 요청 반영).</summary>
        public int AppliedSource => Volatile.Read(ref _applied);
        public long Pumps => Interlocked.Read(ref _pumps);
        public long StreamUpdates => Interlocked.Read(ref _streamUpdates);
        public long DroppedQuotes => Interlocked.Read(ref _dropped);
        /// <summary>합성 가상 시각(UTC 유닉스 마이크로초 · 합성 미적용 = 0).</summary>
        public long SyntheticUtcMicros => Interlocked.Read(ref _virtUtcMicros);
        /// <summary>업비트 스트림 결선 여부(호스트 설정 꺼짐 = false).</summary>
        public bool StreamAttached => Volatile.Read(ref _stream) is not null;
        /// <summary>업비트 스트림 현재 연결 여부(미결선 = false).</summary>
        public bool StreamConnected => Volatile.Read(ref _stream)?.IsConnected == true;

        /// <summary>업비트 공개 WebSocket 어댑터 결속(연결 상태 표시 전용 · 프레임은 어댑터 tap = OnUpbitFrame 으로 유입).</summary>
        public void AttachStream(UpbitWebSocketFeedAdapter stream) => Volatile.Write(ref _stream, stream ?? throw new ArgumentNullException(nameof(stream)));

        /// <summary>출처 선택(UpbitStream · Synthetic). 범위 밖 = false. 적용 = 다음 펌프(원장 초기화 동반).</summary>
        public bool SelectSource(int source)
        {
            if (source != CryptoArenaFeed.UpbitStream && source != CryptoArenaFeed.Synthetic) return false;
            Volatile.Write(ref _requested, source);
            return true;
        }

        // ── 생산자(다중 작성자 seqlock) ──────────────────────────────────────────

        /// <summary>UpbitFrameTap 서명 — 업비트 어댑터(KRW-BTC 구독) 판독 프레임 → KRW-BTC 슬롯.</summary>
        public void OnUpbitFrame(in UpbitStreamFrame frame) => OnFrame(CryptoArenaUniverse.Btc, in frame);

        /// <summary>UpbitMarketFrameTap 서명 — 어댑터 마켓 인덱스(= CryptoArenaUniverse.StreamMarkets 순서 = 슬롯) → 슬롯.</summary>
        public void OnUpbitMarketFrame(int marketIndex, in UpbitStreamFrame frame) => OnFrame(marketIndex, in frame);

        /// <summary>[v12.18.76-DEV] UpbitMarketDepthTap 서명 — 수용 프레임 + 15호가 잔량 열 → 슬롯(Trade = OnFrame 동일).</summary>
        public void OnUpbitMarketDepthFrame(int marketIndex, in UpbitStreamFrame frame, in UpbitOrderbookDepth depth)
        {
            if (frame.Kind != UpbitStreamKind.Orderbook) { OnFrame(marketIndex, in frame); return; }
            int n = Math.Clamp(depth.Tiers, 0, UpbitOrderbookDepth.MaxTiers);
            ReadOnlySpan<long> bids = depth.BidSize, asks = depth.AskSize;
            if (OnDepthBook(marketIndex, frame.BidRaw, frame.AskRaw, frame.BidSizeRaw, frame.AskSizeRaw, bids.Slice(0, n), asks.Slice(0, n)))
                Interlocked.Increment(ref _streamUpdates);
        }

        /// <summary>판독된 업비트 프레임 → 슬롯(Orderbook = L1 호가 · Trade = 테이커 누적량). 무효 = 폐기(false).</summary>
        public bool OnFrame(int index, in UpbitStreamFrame f)
        {
            if ((uint)index >= MaxSlots) { Interlocked.Increment(ref _dropped); return false; }
            if (f.Kind == UpbitStreamKind.Orderbook)
            {
                if (!OnBook(index, f.BidRaw, f.AskRaw, f.BidSizeRaw, f.AskSizeRaw)) return false;
                Interlocked.Increment(ref _streamUpdates);
                return true;
            }
            if (f.Kind != UpbitStreamKind.Trade || f.TradePriceRaw <= 0 || f.TradePriceRaw > UpbitCryptoArenaEngine.MaxPriceRaw
                || f.TradeVolumeRaw <= 0 || f.TradeVolumeRaw > UpbitCryptoArenaEngine.MaxBookQtyRaw || f.TradeSide == UpbitTradeSide.None)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            ref Slot s = ref _slots[index];
            long v = Lock(ref s);
            if (f.TradeSide == UpbitTradeSide.Bid) s.BuyCum += f.TradeVolumeRaw; else s.SellCum += f.TradeVolumeRaw;
            s.TradePx = f.TradePriceRaw;
            Volatile.Write(ref s.Ver, v + 2);
            Interlocked.Increment(ref _streamUpdates);
            return true;
        }

        /// <summary>최우선 호가(원 raw · 코인 raw) → 슬롯. 범위 밖 인덱스 · 격자 비정렬 · 매수 ≥ 매도 · 잔량 범위 밖 = 폐기(false).</summary>
        public bool OnBook(int index, long bidRaw, long askRaw, long bidQtyRaw, long askQtyRaw)
            => OnDepthBook(index, bidRaw, askRaw, bidQtyRaw, askQtyRaw, default, default);

        /// <summary>
        /// [v12.18.76-DEV] OnBook + 호가별 잔량 열(raw · [0] = L1 잔량 · 최대 15단). 잔량 열은 UpbitCryptoArenaEngine.ValidDepthTiers 접두만 기록(불일치 = 0단 · L1 은 그대로).
        /// </summary>
        public bool OnDepthBook(int index, long bidRaw, long askRaw, long bidQtyRaw, long askQtyRaw, ReadOnlySpan<long> bidDepthQtyRaw, ReadOnlySpan<long> askDepthQtyRaw)
        {
            if ((uint)index >= MaxSlots || bidRaw >= askRaw || askRaw > UpbitCryptoArenaEngine.MaxPriceRaw
                || !UpbitTickAligner.IsAligned(bidRaw) || !UpbitTickAligner.IsAligned(askRaw)
                || bidQtyRaw < 0 || askQtyRaw < 0 || bidQtyRaw > UpbitCryptoArenaEngine.MaxBookQtyRaw || askQtyRaw > UpbitCryptoArenaEngine.MaxBookQtyRaw)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            ref Slot s = ref _slots[index];
            long v = Lock(ref s);
            s.Bid = bidRaw; s.Ask = askRaw; s.BidQty = bidQtyRaw; s.AskQty = askQtyRaw;
            int n = UpbitCryptoArenaEngine.ValidDepthTiers(bidQtyRaw, askQtyRaw, bidDepthQtyRaw, askDepthQtyRaw);
            if (n > 0)
            {
                bidDepthQtyRaw.Slice(0, n).CopyTo(s.BidDepth);
                askDepthQtyRaw.Slice(0, n).CopyTo(s.AskDepth);
            }
            s.Tiers = n;
            Volatile.Write(ref s.Ver, v + 2);
            return true;
        }

        private static long Lock(ref Slot s)
        {
            var spin = new SpinWait();
            while (true)
            {
                long v = Volatile.Read(ref s.Ver);
                if ((v & 1) == 0 && Interlocked.CompareExchange(ref s.Ver, v + 1, v) == v) return v;
                spin.SpinOnce(-1);
            }
        }

        // ── 소비자(정산 스레드 단일) ──────────────────────────────────────────────

        /// <summary>UTC ticks → 유닉스 마이크로초(에포크 이전 = 0 → 엔진 세션 무효).</summary>
        public static long UtcMicrosOf(long utcTicks) => utcTicks <= DateTime.UnixEpoch.Ticks ? 0 : (utcTicks - DateTime.UnixEpoch.Ticks) / 10;

        /// <summary>정산 1회(허브 펌프). utcTicks = DateTime.UtcNow.Ticks.</summary>
        public void Pump(long utcTicks)
        {
            int req = Volatile.Read(ref _requested);
            if (req != _applied)
            {
                _engine.SetCapital(_engine.CapitalWon);
                if (req == CryptoArenaFeed.Synthetic) InitSynthetic(utcTicks);
                else { MarkAllSeen(); Interlocked.Exchange(ref _virtUtcMicros, 0); }
                Volatile.Write(ref _applied, req);
            }
            Interlocked.Increment(ref _pumps);
            if (_applied == CryptoArenaFeed.Synthetic) { StepSynthetic(); MarkAllSeen(); return; }

            long now = UtcMicrosOf(utcTicks);
            bool any = false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!TryReadSlot(i, out var q, out long ver) || ver == _seenVer[i]) continue;
                _seenVer[i] = ver;
                long sym = CryptoArenaUniverse.Symbol(i);
                long buy = q.BuyCum - _seenBuy[i], sell = q.SellCum - _seenSell[i];
                _seenBuy[i] = q.BuyCum; _seenSell[i] = q.SellCum;
                if (buy > 0) _engine.OnTrade(sym, q.TradePx, Math.Min(buy, UpbitCryptoArenaEngine.MaxBookQtyRaw), 1, now);
                if (sell > 0) _engine.OnTrade(sym, q.TradePx, Math.Min(sell, UpbitCryptoArenaEngine.MaxBookQtyRaw), -1, now);
                ReadOnlySpan<long> bidDepth = q.BidDepth, askDepth = q.AskDepth;
                if (q.Bid > 0) _engine.OnDepthQuote(sym, q.Bid, q.Ask, q.BidQty, q.AskQty, now, bidDepth.Slice(0, q.Tiers), askDepth.Slice(0, q.Tiers));
                any = true;
            }
            if (!any) _engine.Drain(now);
        }

        private bool TryReadSlot(int i, out Slot q, out long ver)
        {
            ref Slot s = ref _slots[i];
            for (int k = 0; k < 64; k++)
            {
                long v = Volatile.Read(ref s.Ver);
                if ((v & 1) != 0) { Thread.SpinWait(4); continue; }
                q = s;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref s.Ver) == v) { ver = v; return true; }
            }
            q = default; ver = 0;
            return false;
        }

        private void MarkAllSeen()
        {
            for (int i = 0; i < MaxSlots; i++)
                if (TryReadSlot(i, out var q, out long ver)) { _seenVer[i] = ver; _seenBuy[i] = q.BuyCum; _seenSell[i] = q.SellCum; }
        }

        // ── 합성 데모 시세 ───────────────────────────────────────────────────────

        private ulong Next() { ulong x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; return _rng = x; }
        private int NextInt(int lo, int hiExclusive) => lo + (int)(Next() % (ulong)(hiExclusive - lo));

        private void InitSynthetic(long utcTicks)
        {
            for (int k = 0; k < MaxSlots; k++) _synPx[k] = CryptoArenaUniverse.SyntheticStartWon(k) * Scale;
            Array.Clear(_synDrift);
            Array.Clear(_synRegimeLeft);
            long start = Math.Max(1, UtcMicrosOf(utcTicks));
            Interlocked.Exchange(ref _virtUtcMicros, start);
            _nextContextMicros = start;
        }

        private void StepSynthetic()
        {
            long now = Interlocked.Read(ref _virtUtcMicros);
            if (now >= _nextContextMicros)
            {
                _engine.SetMarketContext(NextInt(-100, 401), NextInt(-400, 401));
                _nextContextMicros = now + SyntheticContextMicros;
            }
            for (int k = 0; k < MaxSlots; k++)
            {
                if (_synRegimeLeft[k]-- <= 0) { _synDrift[k] = NextInt(-1, 2); _synRegimeLeft[k] = NextInt(300, 1200); }
                long px = _synPx[k];
                long unit = Math.Max(UpbitTickAligner.TickFor(px), px / 4000);          // 가격 비례 스텝(≈2.5bp · 최소 1호가)
                long step = (NextInt(-3, 4) + (NextInt(0, 3) == 0 ? _synDrift[k] * NextInt(1, 4) : 0)) * unit;
                long start = CryptoArenaUniverse.SyntheticStartWon(k) * Scale;
                px = UpbitTickAligner.AlignNearest(Math.Clamp(px + step, start * 7 / 10, start * 13 / 10));
                _synPx[k] = px;
                long ask = UpbitTickAligner.AlignUp(px + 1);
                long baseQty = s_synBaseQty[k];
                long size = baseQty * NextInt(40, 300) / 100;
                long tilt = _synDrift[k] * (baseQty * NextInt(30, 200) / 100);
                long bidQty = Math.Max(baseQty / 10, size + tilt), askQty = Math.Max(baseQty / 10, size - tilt);
                long sym = CryptoArenaUniverse.Symbol(k);
                int takerSide = NextInt(0, 3) == 0 && _synDrift[k] != 0 ? _synDrift[k] : (NextInt(0, 2) == 0 ? 1 : -1);
                _engine.OnTrade(sym, takerSide > 0 ? ask : px, baseQty * NextInt(5, 60) / 100, takerSide, now);
                _engine.OnQuote(sym, px, ask, bidQty, askQty, now);
            }
            Interlocked.Exchange(ref _virtUtcMicros, now + SyntheticStepMicros);
        }
    }
}
