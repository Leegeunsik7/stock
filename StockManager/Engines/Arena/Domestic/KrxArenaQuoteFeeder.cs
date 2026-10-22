// ============================================================================
// KrxArenaQuoteFeeder — v12.18.60-DEV: KRX 아레나 시세 주입 경로(브리지 실시간 호가 · 합성 데모 시세 → KrxArenaEngine 단일 작성자)
//
//   ① 생산자(임의 스레드 · 다중 가능): OnBook(키움 브리지 10호가 중 최우선 1호가) · OnTrade(누적 거래량) → 종목별 최신값 슬롯(32 · 개방 주소 CAS 점유)에
//      슬롯별 다중 작성자 seqlock(짝수 → CAS 홀수 → 기록 → 짝수+2)으로 덮어쓴다. 최신값만 의미 있음(호가 = 상태) — 큐잉 · 재시도 0 · 표 포화 = 폐기 계수.
//   ② 소비자(정산 스레드 = 대시보드 허브 50ms 펌프 1개): Pump(utcTicks) — 버전이 바뀐 슬롯만 엔진 OnQuote(틱 거래량 = 누적 차분 · 감소 = 일 리셋 → 0).
//      갱신 슬롯 0 = Drain(휴먼 큐만 체결). KST = UTC + 9h.
//   ③ 합성 데모 시세(SelectSource(Synthetic)): 브리지 미가동 PC 에서도 대결을 시연하는 결정론 난수 보행(대형주 5종 · KRX 호가단위 정렬 ·
//      종목별 추세 국면 · 국면 방향 호가 불균형 · 간헐 거래량 급증). 가상 세션 시각 = 평일 09:00 부터 펌프 1회 = 1초(×20) · 15:30 도달 = 다음 평일 09:00 ·
//      가상 일자마다 야간 글로벌 가중치 재추첨. ★합성은 UI 가 '합성 시세(데모)'로 명시 렌더(실시세 위장 0) · 브리지 슬롯은 합성 중에도 계속 갱신되나 엔진 미주입.
//   ④ 출처 전환 = 다음 펌프에서 엔진 원장 · 종목 표 초기화(SetCapital) 후 적용 — 출처 간 가격/EWMA 오염 0. 브리지 복귀 시 기존 슬롯은 '본 것'으로 표시(재주입 0).
//
//   ★δ=0: OnBook/OnTrade/Pump 힙 할당 0(고정 배열 · 값 타입). ★liveArmed:false — 시세 수신 · 가상 원장 주입뿐(주문 송출 수단 0).
//   [v12.18.64-DEV] 플루토 가상 시계(AttachClock · 선택): 합성 틱마다 가상 KST − 9h(UTC)를 TickSyncedVirtualClock.Observe(단조) —
//     뉴스 분석기 미래/과거 필터 · 버퍼 우세 · 시나리오 피더가 합성 세션 시각으로 돈다. 출처 전환(브리지 ↔ 합성) = Reset(실시세 = 벽시계 복귀).
// ============================================================================
using System;
using System.Threading;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Domestic
{
    public sealed class KrxArenaQuoteFeeder
    {
        public const int MaxSlots = 32;
        public static readonly long KstOffsetTicks = TimeSpan.FromHours(9).Ticks;
        public static readonly long SyntheticStepTicks = TimeSpan.TicksPerSecond;   // 펌프 1회 = 가상 1초(50ms 펌프 → ×20)

        /// <summary>합성 데모 종목(코드 · 시작가 원): 삼성전자 · SK하이닉스 · LG에너지솔루션 · 삼성바이오로직스 · 현대차.</summary>
        public static readonly long[] SyntheticSymbols = { 5930, 660, 373220, 207940, 5380 };
        private static readonly long[] SyntheticStartPx = { 70_000, 180_000, 380_000, 800_000, 220_000 };

        private struct Slot { public long Symbol, Ver, Bid, Ask, BidQty, AskQty, CumVol; }

        private readonly KrxArenaEngine _engine;
        private readonly Slot[] _slots = new Slot[MaxSlots];
        private long _droppedBooks, _bridgeUpdates;
        private int _requested = KrxArenaFeed.Bridge;

        // 소비자(정산 스레드) 전용
        private readonly long[] _seenVer = new long[MaxSlots];
        private readonly long[] _lastCum = new long[MaxSlots];
        private int _applied = KrxArenaFeed.Bridge;
        private long _pumps;

        // 합성 상태(소비자 전용)
        private readonly long[] _synPx = new long[5];
        private readonly int[] _synDrift = new int[5], _synRegimeLeft = new int[5];
        private ulong _rng;
        private long _virtKstTicks;
        private StockManager.Engines.Cognitive.News.TickSyncedVirtualClock? _clock;

        public KrxArenaQuoteFeeder(KrxArenaEngine engine, ulong seed = 0x5EED_A12E_0060UL)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _rng = seed == 0 ? 1 : seed;
        }

        public KrxArenaEngine Engine => _engine;
        /// <summary>요청된 출처(KrxArenaFeed.Bridge · Synthetic).</summary>
        public int RequestedSource => Volatile.Read(ref _requested);
        /// <summary>엔진에 적용 중인 출처(다음 펌프에서 요청 반영).</summary>
        public int AppliedSource => Volatile.Read(ref _applied);
        public long Pumps => Interlocked.Read(ref _pumps);
        public long BridgeUpdates => Interlocked.Read(ref _bridgeUpdates);
        public long DroppedBooks => Interlocked.Read(ref _droppedBooks);
        /// <summary>합성 가상 세션 시각(KST · 합성 미적용 = 0).</summary>
        public long SyntheticKstTicks => Interlocked.Read(ref _virtKstTicks);

        /// <summary>[v12.18.64-DEV] 플루토 가상 시계 결속(합성 틱 시각 동기 · 출처 전환 시 Reset).</summary>
        public void AttachClock(StockManager.Engines.Cognitive.News.TickSyncedVirtualClock clock) => Volatile.Write(ref _clock, clock ?? throw new ArgumentNullException(nameof(clock)));

        /// <summary>출처 선택(Bridge · Synthetic). 범위 밖 = false. 적용 = 다음 펌프(원장 초기화 동반).</summary>
        public bool SelectSource(int source)
        {
            if (source != KrxArenaFeed.Bridge && source != KrxArenaFeed.Synthetic) return false;
            Volatile.Write(ref _requested, source);
            return true;
        }

        // ── 생산자(다중 작성자 seqlock) ──────────────────────────────────────────

        /// <summary>브리지 10호가 수신(최우선 1호가만 사용). 무효(코드 ≤ 0) · 표 포화 = 폐기.</summary>
        public void OnBook(in OrderBookPayload b)
        {
            int i = Claim(b.SymbolCode);
            if (i < 0) { Interlocked.Increment(ref _droppedBooks); return; }
            ref Slot s = ref _slots[i];
            long v = Lock(ref s);
            s.Bid = b.BidPrice[0]; s.Ask = b.AskPrice[0]; s.BidQty = b.BidQty[0]; s.AskQty = b.AskQty[0];
            Volatile.Write(ref s.Ver, v + 2);
            Interlocked.Increment(ref _bridgeUpdates);
        }

        /// <summary>브리지 체결(누적 거래량). 음수 = 폐기.</summary>
        public void OnTrade(long symbol, long cumulativeVolume)
        {
            if (cumulativeVolume < 0) return;
            int i = Claim(symbol);
            if (i < 0) { Interlocked.Increment(ref _droppedBooks); return; }
            ref Slot s = ref _slots[i];
            long v = Lock(ref s);
            s.CumVol = cumulativeVolume;
            Volatile.Write(ref s.Ver, v + 2);
        }

        private int Claim(long symbol)
        {
            if (symbol <= 0) return -1;
            for (int i = 0; i < MaxSlots; i++)
            {
                long cur = Volatile.Read(ref _slots[i].Symbol);
                if (cur == symbol) return i;
                if (cur == 0)
                {
                    long prev = Interlocked.CompareExchange(ref _slots[i].Symbol, symbol, 0);
                    if (prev == 0 || prev == symbol) return i;
                }
            }
            return -1;
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
                _engine.SetCapital(_engine.CapitalWon);
                Volatile.Read(ref _clock)?.Reset();
                if (req == KrxArenaFeed.Synthetic) InitSynthetic(utcTicks + KstOffsetTicks);
                else { MarkAllSeen(); Interlocked.Exchange(ref _virtKstTicks, 0); }
                Volatile.Write(ref _applied, req);
            }
            Interlocked.Increment(ref _pumps);
            if (_applied == KrxArenaFeed.Synthetic) { StepSynthetic(); MarkAllSeen(); return; }

            var kst = new DateTime(utcTicks + KstOffsetTicks);
            bool any = false;
            for (int i = 0; i < MaxSlots; i++)
            {
                long sym = Volatile.Read(ref _slots[i].Symbol);
                if (sym == 0) break;
                if (!TryReadSlot(i, out var q, out long ver) || ver == _seenVer[i]) continue;
                _seenVer[i] = ver;
                if (q.Bid <= 0 || q.Ask <= 0) continue;   // 체결만 먼저 도착 — 거래량은 다음 호가까지 이월(_lastCum 미갱신)
                long vol = 0;
                if (_lastCum[i] > 0 && q.CumVol >= _lastCum[i]) vol = q.CumVol - _lastCum[i];
                if (q.CumVol > 0) _lastCum[i] = q.CumVol;
                _engine.OnQuote(sym, q.Bid, q.Ask, q.BidQty, q.AskQty, vol, kst);
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
            {
                if (Volatile.Read(ref _slots[i].Symbol) == 0) break;
                if (TryReadSlot(i, out var q, out long ver)) { _seenVer[i] = ver; if (q.CumVol > 0) _lastCum[i] = q.CumVol; }
            }
        }

        // ── 합성 데모 시세 ───────────────────────────────────────────────────────

        private ulong Next() { ulong x = _rng; x ^= x << 13; x ^= x >> 7; x ^= x << 17; return _rng = x; }
        private int NextInt(int lo, int hiExclusive) => lo + (int)(Next() % (ulong)(hiExclusive - lo));

        private void InitSynthetic(long kstTicks)
        {
            Array.Copy(SyntheticStartPx, _synPx, _synPx.Length);
            Array.Clear(_synDrift);
            Array.Clear(_synRegimeLeft);
            Interlocked.Exchange(ref _virtKstTicks, NextSessionOpen(new DateTime(kstTicks).Date).Ticks);
            _engine.SetOvernightMacro(NextInt(-4000, 4001));
        }

        /// <summary>day 이후(포함) 첫 평일 09:00.</summary>
        public static DateTime NextSessionOpen(DateTime day)
        {
            var d = day.Date;
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
            return d + KrxArenaEngine.SessionStart;
        }

        private void StepSynthetic()
        {
            var kst = new DateTime(Interlocked.Read(ref _virtKstTicks));
            if (kst.TimeOfDay >= KrxArenaEngine.SessionEnd)
            {
                kst = NextSessionOpen(kst.Date.AddDays(1));
                _engine.SetOvernightMacro(NextInt(-4000, 4001));   // 새 가상 일자 = 야간 글로벌 재추첨
            }
            Volatile.Read(ref _clock)?.Observe(kst.Ticks - KstOffsetTicks);
            for (int k = 0; k < _synPx.Length; k++)
            {
                if (_synRegimeLeft[k]-- <= 0) { _synDrift[k] = NextInt(-1, 2); _synRegimeLeft[k] = NextInt(40, 160); }
                long px = _synPx[k];
                long tick = (long)KrxTickLadder.TickWonFor(px);
                int step = NextInt(-1, 2) + (NextInt(0, 3) == 0 ? _synDrift[k] : 0);
                long start = SyntheticStartPx[k];
                px = Math.Clamp(px + step * tick, start * 7 / 10, start * 13 / 10);
                tick = (long)KrxTickLadder.TickWonFor(px);
                px = px / tick * tick;
                _synPx[k] = px;
                long baseQty = NextInt(500, 3000);
                long tilt = _synDrift[k] * NextInt(500, 4000);
                long bidQty = Math.Max(50, baseQty + tilt), askQty = Math.Max(50, baseQty - tilt);
                long vol = NextInt(0, 25) == 0 ? NextInt(2000, 8000) : NextInt(50, 400);
                _engine.OnQuote(SyntheticSymbols[k], px, px + tick, bidQty, askQty, vol, kst);
            }
            Interlocked.Exchange(ref _virtKstTicks, kst.Ticks + SyntheticStepTicks);
        }
    }
}
