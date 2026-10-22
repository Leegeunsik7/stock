// ============================================================================
// UsArenaQuoteFeeder — v12.18.61-DEV: US 테크 아레나 시세 주입 경로(KIS 해외 현재체결가 · 합성 데모 시세 → UsTechArenaEngine 단일 작성자)
//
//   ① 생산자(임의 스레드 · 다중 가능) → 종목별 최신값 슬롯(UsTechUniverse 5 · 고정 인덱스)에 슬롯별 다중 작성자 seqlock(짝수 → CAS 홀수 → 기록 → 짝수+2).
//      최신값만 의미 있음(호가 = 상태) — 큐잉 · 재시도 0 · 표 밖 티커/무효 값 = 폐기 계수.
//      OnKisQuoteBody(index, HHDFS00000300 응답 UTF-8) = KisOverseasProtocol.TryParseQuote(시세 계열 거래소 코드 NAS/NYS · rsym 정확 일치) →
//        OnKisQuote: 현재체결가(raw 10^8)를 UsTickAligner.AlignNearest 로 $0.01 격자 정렬 → 센트. ★HHDFS00000300 은 호가 잔량을 주지 않는다 —
//        매수/매도 = 체결가(스프레드 0) · 잔량 = KisLastTradeDepth(가상 유동성 상한 · 호가 불균형 0)로 '체결가 기준 가상 체결'임을 명시한다.
//      OnBook(index, 최우선 매수/매도 센트 · 잔량) = 잔량을 주는 호가 출처(향후 KIS 해외 호가 · 테스트)용.
//   ② 소비자(정산 스레드 = 대시보드 허브 50ms 펌프 1개): Pump(utcTicks) — 버전이 바뀐 슬롯만 엔진 OnQuote · 갱신 0 = Drain(휴먼 큐 · 세션 판정). KST = UTC + 9h.
//   ③ 합성 데모 시세(SelectSource(Synthetic)): KIS 미구성 PC 에서도 대결을 시연하는 결정론 난수 보행(빅테크 5종 · $0.01 격자 ·
//      가격 비례 스텝(≈2.5bp) · 종목별 추세 국면 · 국면 방향 호가 불균형). 가상 세션 = 다음 NYSE 매매일 09:00 ET(프리마켓 30분) → 정규장 09:30~16:00 ET(조기 폐장 13:00) →
//      다음 매매일 09:00 ET. 펌프 1회 = 가상 1초(50ms 펌프 → ×20). 가상 일자마다 시장 맥락(QQQ · SOXX · 10년물) 재추첨 + 프리마켓 갭 점프.
//      ★합성은 UI 가 '합성 시세(데모)'로 명시 렌더(실시세 위장 0) · KIS 슬롯은 합성 중에도 계속 갱신되나 엔진 미주입.
//   ④ 출처 전환 = 다음 펌프에서 엔진 원장 · 종목 상태 초기화(SetCapital) 후 적용 — 출처 간 가격/EWMA/갭 오염 0. KIS 복귀 시 기존 슬롯은 '본 것'(재주입 0).
//
//   [v12.18.64-DEV] 플루토 가상 시계(AttachClock · 선택): 합성 틱마다 가상 UTC 를 TickSyncedVirtualClock.Observe(단조) · 출처 전환 = Reset.
//   ★δ=0: OnKisQuoteBody/OnKisQuote/OnBook/Pump 힙 할당 0(고정 배열 · 값 타입 · Utf8JsonReader 판독). ★liveArmed:false — 시세 수신 · 가상 원장 주입뿐(해외 주문 송출 수단 0).
// ============================================================================
using System;
using System.Threading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Engines.Brokers.Kis;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;

namespace StockManager.Engines.Arena.Us
{
    public sealed class UsArenaQuoteFeeder
    {
        public const int MaxSlots = UsTechUniverse.Count;
        /// <summary>KIS 현재체결가 출처의 가상 유동성 상한(주 · 호가 잔량 미제공).</summary>
        public const long KisLastTradeDepth = 1_000;
        public static readonly long KstOffsetTicks = TimeSpan.FromHours(9).Ticks;
        public static readonly long SyntheticStepTicks = TimeSpan.TicksPerSecond;   // 펌프 1회 = 가상 1초(50ms 펌프 → ×20)
        public static readonly TimeSpan SyntheticDayStartEt = new(9, 0, 0);        // 프리마켓 30분 후 정규장

        private struct Slot { public long Ver, Bid, Ask, BidQty, AskQty; }

        private readonly UsTechArenaEngine _engine;
        private readonly Slot[] _slots = new Slot[MaxSlots];
        private long _dropped, _kisUpdates;
        private int _requested = UsArenaFeed.KisOverseas;

        // 소비자(정산 스레드) 전용
        private readonly long[] _seenVer = new long[MaxSlots];
        private int _applied = UsArenaFeed.KisOverseas;
        private long _pumps;

        // 합성 상태(소비자 전용)
        private readonly long[] _synPx = new long[MaxSlots];
        private readonly int[] _synDrift = new int[MaxSlots], _synRegimeLeft = new int[MaxSlots];
        private ulong _rng;
        private long _virtUtcTicks, _virtEtDay;
        private StockManager.Engines.Cognitive.News.TickSyncedVirtualClock? _clock;

        public UsArenaQuoteFeeder(UsTechArenaEngine engine, ulong seed = 0x5EED_0005_0061UL)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _rng = seed == 0 ? 1 : seed;
        }

        public UsTechArenaEngine Engine => _engine;
        /// <summary>요청된 출처(UsArenaFeed.KisOverseas · Synthetic).</summary>
        public int RequestedSource => Volatile.Read(ref _requested);
        /// <summary>엔진에 적용 중인 출처(다음 펌프에서 요청 반영).</summary>
        public int AppliedSource => Volatile.Read(ref _applied);
        public long Pumps => Interlocked.Read(ref _pumps);
        public long KisUpdates => Interlocked.Read(ref _kisUpdates);
        public long DroppedQuotes => Interlocked.Read(ref _dropped);
        /// <summary>합성 가상 세션 시각(UTC ticks · 합성 미적용 = 0).</summary>
        public long SyntheticUtcTicks => Interlocked.Read(ref _virtUtcTicks);

        /// <summary>[v12.18.64-DEV] 플루토 가상 시계 결속(합성 틱 UTC 동기 · 출처 전환 시 Reset).</summary>
        public void AttachClock(StockManager.Engines.Cognitive.News.TickSyncedVirtualClock clock) => Volatile.Write(ref _clock, clock ?? throw new ArgumentNullException(nameof(clock)));

        /// <summary>출처 선택(KisOverseas · Synthetic). 범위 밖 = false. 적용 = 다음 펌프(원장 초기화 동반).</summary>
        public bool SelectSource(int source)
        {
            if (source != UsArenaFeed.KisOverseas && source != UsArenaFeed.Synthetic) return false;
            Volatile.Write(ref _requested, source);
            return true;
        }

        // ── 생산자(다중 작성자 seqlock) ──────────────────────────────────────────

        /// <summary>KIS HHDFS00000300 응답 본문 판독 → 슬롯 기록. 판독 실패 · rt_cd ≠ 0 · rsym 불일치 · 체결가 부재 = 폐기(false).</summary>
        public bool OnKisQuoteBody(int index, ReadOnlySpan<byte> body)
        {
            if ((uint)index >= MaxSlots) { Interlocked.Increment(ref _dropped); return false; }
            if (!KisOverseasProtocol.TryParseQuote(body, UsTechUniverse.Exchange(index), UsTechUniverse.TickerUtf8(index), out var q))
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            return OnKisQuote(index, in q);
        }

        /// <summary>판독된 KIS 현재체결가 → 슬롯(체결가 기준 · 스프레드 0 · 잔량 KisLastTradeDepth). 사용 불가 · $1 미만 = 폐기(false).</summary>
        public bool OnKisQuote(int index, in KisOverseasQuote q)
        {
            if ((uint)index >= MaxSlots || !q.IsUsable || q.LastRaw < UsTickAligner.OneDollarRaw) { Interlocked.Increment(ref _dropped); return false; }
            long cents = UsTickAligner.AlignNearest(q.LastRaw) / UsTickAligner.PennyRaw;
            if (cents < UsTechArenaEngine.MinPriceCents || cents > UsTechArenaEngine.MaxPriceCents) { Interlocked.Increment(ref _dropped); return false; }
            Write(index, cents, cents, KisLastTradeDepth, KisLastTradeDepth);
            Interlocked.Increment(ref _kisUpdates);
            return true;
        }

        /// <summary>최우선 호가(센트 · 잔량) → 슬롯. 범위 밖 인덱스 · 무효 호가 = 폐기(false).</summary>
        public bool OnBook(int index, long bidCents, long askCents, long bidQty, long askQty)
        {
            if ((uint)index >= MaxSlots || bidCents < UsTechArenaEngine.MinPriceCents || askCents < bidCents || askCents > UsTechArenaEngine.MaxPriceCents
                || bidQty < 0 || askQty < 0 || bidQty > UsTechArenaEngine.MaxBookQty || askQty > UsTechArenaEngine.MaxBookQty)
            {
                Interlocked.Increment(ref _dropped);
                return false;
            }
            Write(index, bidCents, askCents, bidQty, askQty);
            return true;
        }

        private void Write(int index, long bid, long ask, long bidQty, long askQty)
        {
            ref Slot s = ref _slots[index];
            long v = Lock(ref s);
            s.Bid = bid; s.Ask = ask; s.BidQty = bidQty; s.AskQty = askQty;
            Volatile.Write(ref s.Ver, v + 2);
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

        /// <summary>정산 1회(허브 펌프). utcTicks = DateTime.UtcNow.Ticks.</summary>
        public void Pump(long utcTicks)
        {
            int req = Volatile.Read(ref _requested);
            if (req != _applied)
            {
                _engine.SetCapital(_engine.CapitalCents);
                Volatile.Read(ref _clock)?.Reset();
                if (req == UsArenaFeed.Synthetic) InitSynthetic(utcTicks);
                else { MarkAllSeen(); Interlocked.Exchange(ref _virtUtcTicks, 0); }
                Volatile.Write(ref _applied, req);
            }
            Interlocked.Increment(ref _pumps);
            if (_applied == UsArenaFeed.Synthetic) { StepSynthetic(); MarkAllSeen(); return; }

            var kst = new DateTime(utcTicks + KstOffsetTicks);
            bool any = false;
            for (int i = 0; i < MaxSlots; i++)
            {
                if (!TryReadSlot(i, out var q, out long ver) || ver == _seenVer[i]) continue;
                _seenVer[i] = ver;
                _engine.OnQuote(UsTechUniverse.Symbol(i), q.Bid, q.Ask, q.BidQty, q.AskQty, kst);
                any = true;
            }
            if (!any) _engine.Drain(kst);
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
                if (TryReadSlot(i, out _, out long ver)) _seenVer[i] = ver;
        }

        // ── 합성 데모 시세 ───────────────────────────────────────────────────────

        private ulong Next() { ulong x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; return _rng = x; }
        private int NextInt(int lo, int hiExclusive) => lo + (int)(Next() % (ulong)(hiExclusive - lo));

        /// <summary>미 동부 벽시계 → UTC(EST 가정 UTC 로 서머타임 판정 · 전환 시각 02:00 ET 와 먼 장중 시각만 사용 → 모호 0).</summary>
        public static DateTime EasternToUtc(DateTime et)
        {
            var est = new DateTime(et.Ticks + 5 * TimeSpan.TicksPerHour);
            return UsTradingCalendar.IsDaylightSaving(est) ? new DateTime(et.Ticks + 4 * TimeSpan.TicksPerHour) : est;
        }

        /// <summary>etDay 이후(포함) 첫 NYSE 매매일 09:00 ET 의 UTC(달력 범위 밖 = 마지막 탐색일 그대로 · 엔진이 CalendarUncovered 로 차단).</summary>
        public static DateTime NextSyntheticDayStartUtc(DateTime etDay)
        {
            var d = etDay.Date;
            for (int i = 0; i < 14 && !UsTradingCalendar.IsTradingDay(d); i++) d = d.AddDays(1);
            return EasternToUtc(d + SyntheticDayStartEt);
        }

        private void InitSynthetic(long utcTicks)
        {
            for (int k = 0; k < MaxSlots; k++) _synPx[k] = UsTechUniverse.SyntheticStartCents(k);
            Array.Clear(_synDrift);
            Array.Clear(_synRegimeLeft);
            var start = NextSyntheticDayStartUtc(UsTradingCalendar.ToEastern(new DateTime(utcTicks)).Date.AddDays(1));
            Interlocked.Exchange(ref _virtUtcTicks, start.Ticks);
            _virtEtDay = 0;
        }

        private void NewVirtualDay()
        {
            _engine.SetMarketContext(NextInt(-300, 301), NextInt(-450, 451), NextInt(-12, 13));
            for (int k = 0; k < MaxSlots; k++)
            {
                int gapBp = NextInt(-120, 121);
                _synPx[k] = Math.Max(UsTechArenaEngine.MinPriceCents, _synPx[k] * (10000 + gapBp) / 10000);
                _synDrift[k] = gapBp > 40 ? 1 : gapBp < -40 ? -1 : 0;   // 갭 방향 국면으로 개장
                _synRegimeLeft[k] = NextInt(300, 1200);
            }
        }

        private void StepSynthetic()
        {
            var utc = new DateTime(Interlocked.Read(ref _virtUtcTicks));
            var et = UsTradingCalendar.ToEastern(utc);
            long close = UsTradingCalendar.IsEarlyClose(et) ? 13 * TimeSpan.TicksPerHour : 16 * TimeSpan.TicksPerHour;
            if (et.TimeOfDay.Ticks >= close)
            {
                utc = NextSyntheticDayStartUtc(et.Date.AddDays(1));
                et = UsTradingCalendar.ToEastern(utc);
            }
            long day = et.Date.Ticks;
            if (day != _virtEtDay) { _virtEtDay = day; NewVirtualDay(); }
            Volatile.Read(ref _clock)?.Observe(utc.Ticks);
            var kst = new DateTime(utc.Ticks + KstOffsetTicks);
            for (int k = 0; k < MaxSlots; k++)
            {
                if (_synRegimeLeft[k]-- <= 0) { _synDrift[k] = NextInt(-1, 2); _synRegimeLeft[k] = NextInt(300, 1200); }
                long px = _synPx[k];
                long unit = Math.Max(1, px / 4000);                   // 가격 비례 스텝(≈2.5bp · $0.01 격자 정수 센트)
                long step = (NextInt(-3, 4) + (NextInt(0, 3) == 0 ? _synDrift[k] * NextInt(1, 4) : 0)) * unit;
                long start = UsTechUniverse.SyntheticStartCents(k);
                px = Math.Clamp(px + step, start * 7 / 10, start * 13 / 10);
                _synPx[k] = px;
                long baseQty = NextInt(200, 1500);
                long tilt = _synDrift[k] * NextInt(200, 1500);
                long bidQty = Math.Max(20, baseQty + tilt), askQty = Math.Max(20, baseQty - tilt);
                _engine.OnQuote(UsTechUniverse.Symbol(k), px, px + 1, bidQty, askQty, kst);
            }
            Interlocked.Exchange(ref _virtUtcTicks, utc.Ticks + SyntheticStepTicks);
        }
    }
}
