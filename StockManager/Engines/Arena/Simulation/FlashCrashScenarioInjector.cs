// ============================================================================
// FlashCrashScenarioInjector — [v12.18.26-DEV] 아레나 극한 시나리오 인젝터(플래시 크래시 급락 · 숏 스퀴즈 급등) — 판독 δ=0
//
//   원천 테이프(합성 랜덤 워크 · 녹화 .tape)를 감싸는 ArenaTickSource 데코레이터. 지정 시각(원천 시작 + offset) 이후 첫 호가 틱 앞에
//   패닉 버스트 B틱(기본 8,000틱 · 간격 0.25ms = 4,000틱/초 · 2초)을 삽입하고, 그 뒤 원천 틱은 시각을 B × 간격만큼 민다(시각 단조 비감소 유지).
//   ① 버스트(대상 종목 1개 · 체결 동반): 앞 60% = 급락(급등) 볼록 경로로 깊이 depthBp 도달 · 뒤 40% = 깊이의 retraceBp 만큼 되돌림.
//      급락 국면 = 유동성 진공 — 무너지는 쪽 10호가 잔량 1 ~ 30주 · 2틱 간격 구멍 · 스프레드 2 ~ 6틱 · 반대쪽 패닉 물량 1,500 ~ 4,500주 ·
//      체결 = 매 틱 주도 방향(급락 = 매도 주도 매수 1호가 · 급등 = 매수 주도 매도 1호가) 300 ~ 3,000주. 되돌림 국면 = 스프레드 1틱 · 반대 방향 우세 3/4.
//   ② 여진: 버스트 종료가 − 기준가 편차를 대상 종목 원천 호가 · 체결에 더하고 recoveryTicks(기본 60초) 동안 선형 감쇠 → 0(원천 경로로 복귀).
//      편차 적용 가격은 KRX 호가 단위로 재정렬(매수 내림 · 매도 올림 · 단 역전 시 1틱 밀어냄 — 스프레드 비축소).
//   ③ 블라인드(CreateBlind): 엔트로피 1개에서 종류(급락/급등) · 발동 offset(3 ~ 5분) · 깊이(5 ~ 12%) · 지터 시드 파생 — 호출자(재생 엔진)는 종류 ·
//      시각을 발동 전까지 UI 에 공개하지 않는다. 원천이 offset 보다 짧으면 미발동(Injected = false · 원천 그대로 · 가짜 발동 표시 0).
//   δ=0: TimestampOf · Read · TryReadTrade = 정수 산술 + 원천 위임만(버스트 틱은 저장 0B · 인덱스 해시로 결정론 파생). 생성(콜드)만 원천 판독 탐색.
//   ★Fail-Closed: 시세 원천 변형기일 뿐 — 주문 · 브로커 · 무장 경로 참조 0(리플레이 IL 허용목록 테스트 대상 · 폴더 Simulation/ 이나 네임스페이스는
//     Analytics/ 선례대로 StockManager.Engines.Arena — 허용목록 무변경). 합성 훈련 데이터 — 실 시장 재현 아님.
// ============================================================================
using System;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena
{
    public sealed class FlashCrashScenarioInjector : ArenaTickSource
    {
        public const int DefaultBurstTicks = 8_000, DefaultDepthBp = 800, DefaultRetraceBp = 4_000, MinBurstTicks = 16, MaxDepthBp = 5_000;
        public const long DefaultBurstIntervalTicks = 2_500;                                   // 0.25ms = 4,000틱/초
        public const long MaxBurstIntervalTicks = TimeSpan.TicksPerSecond / 2_000;             // 0.5ms = 2,000틱/초 하한
        public const long DefaultRecoveryTicks = 60 * TimeSpan.TicksPerSecond;
        public const long BlindMinOffsetTicks = 3 * TimeSpan.TicksPerMinute, BlindMaxOffsetTicks = 5 * TimeSpan.TicksPerMinute;
        public const int BlindMinDepthBp = 500, BlindMaxDepthBp = 1_200;

        private readonly ArenaTickSource _base;
        private readonly int _profile, _burst, _plunge, _depthBp, _retraceBp, _start;
        private readonly long _interval, _recovery, _offset, _shift, _t0, _symbol, _anchor, _endOffset;
        private readonly ulong _seed;

        /// <param name="baseTape">원천 테이프(합성 · 녹화).</param>
        /// <param name="profile">ArenaScenarioProfile.FlashCrash · ShortSqueeze.</param>
        /// <param name="offsetTicks">원천 첫 틱 시각 기준 발동 지연(100ns). 그 시각 이후 첫 호가 틱의 종목 · 매수 1호가가 대상 · 기준가.</param>
        /// <param name="seed">버스트 잔량 · 체결 · 지터 해시 시드.</param>
        public FlashCrashScenarioInjector(ArenaTickSource baseTape, int profile, long offsetTicks, int seed = 20261019,
                                          int burstTicks = DefaultBurstTicks, long burstIntervalTicks = DefaultBurstIntervalTicks,
                                          int depthBp = DefaultDepthBp, int retraceBp = DefaultRetraceBp, long recoveryTicks = DefaultRecoveryTicks)
        {
            _base = baseTape ?? throw new ArgumentNullException(nameof(baseTape));
            if (profile is not (ArenaScenarioProfile.FlashCrash or ArenaScenarioProfile.ShortSqueeze)) throw new ArgumentOutOfRangeException(nameof(profile));
            if (offsetTicks < 0) throw new ArgumentOutOfRangeException(nameof(offsetTicks));
            if (burstTicks < MinBurstTicks) throw new ArgumentOutOfRangeException(nameof(burstTicks));
            if (burstIntervalTicks <= 0 || burstIntervalTicks > MaxBurstIntervalTicks) throw new ArgumentOutOfRangeException(nameof(burstIntervalTicks), "버스트 ≥ 2,000틱/초");
            if (depthBp <= 0 || depthBp > MaxDepthBp) throw new ArgumentOutOfRangeException(nameof(depthBp));
            if (retraceBp < 0 || retraceBp > 10_000) throw new ArgumentOutOfRangeException(nameof(retraceBp));
            if (recoveryTicks < 0) throw new ArgumentOutOfRangeException(nameof(recoveryTicks));
            _profile = profile; _burst = burstTicks; _plunge = burstTicks * 3 / 5; _depthBp = depthBp; _retraceBp = retraceBp;
            _interval = burstIntervalTicks; _recovery = recoveryTicks; _offset = offsetTicks;
            _seed = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL | 1UL;
            _start = -1;
            int n = baseTape.Count;
            if (n == 0) return;
            long due = baseTape.TimestampOf(0) + offsetTicks;
            for (int i = LowerBound(baseTape, due); i < n; i++)                                   // 콜드: 발동 시각 이후 첫 호가 틱 탐색
            {
                if (!baseTape.Read(i, out var b) || b.BidPrice[0] <= 0) continue;
                _start = i; _symbol = b.SymbolCode; _anchor = b.BidPrice[0];
                break;
            }
            if (_start < 0) return;
            _t0 = baseTape.TimestampOf(_start);
            _shift = burstTicks * burstIntervalTicks;
            _endOffset = PriceAt(burstTicks - 1, jitter: false) - _anchor;
        }

        /// <summary>
        /// 블라인드 생성 — 엔트로피 1개에서 종류(급락/급등 반반) · 발동 offset(3 ~ 5분 균등) · 깊이(5 ~ 12%) · 시드 파생.
        /// </summary>
        public static FlashCrashScenarioInjector CreateBlind(ArenaTickSource baseTape, ulong entropy)
        {
            ulong h = Mix(entropy);
            int profile = (h & 1) == 0 ? ArenaScenarioProfile.FlashCrash : ArenaScenarioProfile.ShortSqueeze;
            long offset = BlindMinOffsetTicks + (long)((h >> 1) % (ulong)(BlindMaxOffsetTicks - BlindMinOffsetTicks + 1));
            int depth = BlindMinDepthBp + (int)((h >> 48) % (ulong)(BlindMaxDepthBp - BlindMinDepthBp + 1));
            return new FlashCrashScenarioInjector(baseTape, profile, offset, unchecked((int)(Mix(h) >> 32)), depthBp: depth);
        }

        public ArenaTickSource BaseTape => _base;
        public int Profile => _profile;
        /// <summary>발동 여부(false = 원천이 발동 시각보다 짧음 — 원천 그대로 통과).</summary>
        public bool Injected => _start >= 0;
        /// <summary>버스트 첫 틱 인덱스(이 테이프 기준 · 미발동 = −1) · 버스트 끝(배타) 인덱스.</summary>
        public int BurstStart => _start;
        public int BurstEnd => _start < 0 ? -1 : _start + _burst;
        public int BurstTicks => _burst;
        public long OffsetTicks => _offset;
        public long BurstTicksPerSecond => TimeSpan.TicksPerSecond / _interval;
        public long TargetSymbol => _symbol;
        /// <summary>기준가(발동 직전 대상 매수 1호가) · 극값(급락 저점 · 급등 고점 — 지터 제외) · 버스트 종료가 − 기준가.</summary>
        public long AnchorPrice => _anchor;
        public long ExtremePrice => _start < 0 ? 0 : PriceAt(_plunge - 1, jitter: false);
        public long EndOffsetWon => _endOffset;
        public int DepthBp => _depthBp;

        public override int Count => _start < 0 ? _base.Count : _base.Count + _burst;

        public override long TimestampOf(int index)
        {
            if (_start < 0 || index < _start) return _base.TimestampOf(index);
            int k = index - _start;
            return k < _burst ? _t0 + k * _interval : _base.TimestampOf(index - _burst) + _shift;
        }

        public override bool Read(int index, out OrderBookPayload book)
        {
            if (_start < 0 || index < _start) return _base.Read(index, out book);
            int k = index - _start;
            if (k < _burst) { BurstBook(k, out book); return true; }
            int j = index - _burst;
            bool has = _base.Read(j, out book);
            book.TimestampTicks += _shift;
            if (has && book.SymbolCode == _symbol)
            {
                long off = OffsetAt(_base.TimestampOf(j));
                if (off != 0) ShiftBook(ref book, off);
            }
            return has;
        }

        public override bool TryReadTrade(int index, out ArenaTrade trade)
        {
            if (_start < 0 || index < _start) return _base.TryReadTrade(index, out trade);
            int k = index - _start;
            if (k < _burst) { BurstTrade(k, out trade); return true; }
            int j = index - _burst;
            if (!_base.TryReadTrade(j, out trade)) return false;
            if (trade.Symbol == _symbol)
            {
                long off = OffsetAt(_base.TimestampOf(j));
                if (off != 0) trade.PriceWon = Align(Math.Max(1, trade.PriceWon + off), up: trade.Side > 0);
            }
            return true;
        }

        // ════════════ 버스트(δ=0 · 결정론) ════════════

        // 버스트 k 의 '무너지는 쪽' 최우선 가격(급락 = 매수 1호가 · 급등 = 매도 1호가)
        private long PriceAt(int k, bool jitter)
        {
            long devBp;
            if (k < _plunge)
            {
                long r = 10_000 - (long)k * 10_000 / Math.Max(1, _plunge - 1);              // 볼록 하강: 초반 가파르게
                devBp = _depthBp * (100_000_000 - r * r) / 100_000_000;
            }
            else
            {
                long y = (long)(k - _plunge) * 10_000 / Math.Max(1, _burst - 1 - _plunge);
                devBp = _depthBp - (long)_depthBp * _retraceBp / 10_000 * y / 10_000;
            }
            long move = _anchor * devBp / 10_000;
            long p = _profile == ArenaScenarioProfile.FlashCrash ? _anchor - move : _anchor + move;
            if (p < 1) p = 1;
            long tick = SyntheticTickSource.TickSize(p);
            p = Math.Max(tick, p / tick * tick);
            if (jitter) p = Math.Max(tick, p + ((long)(Hash(k) % 3) - 1) * tick);
            return p;
        }

        private void Quote(int k, out long bid, out long ask, out long tick)
        {
            long p = PriceAt(k, jitter: true);
            tick = SyntheticTickSource.TickSize(p);
            long spread = k < _plunge ? 2 + (long)(Hash(k) >> 40) % 5 : 1;
            if (_profile == ArenaScenarioProfile.FlashCrash) { bid = p; ask = p + spread * tick; }
            else { ask = p; bid = Math.Max(0, p - spread * tick); }
        }

        private void BurstBook(int k, out OrderBookPayload book)
        {
            book = default;
            book.SymbolCode = _symbol;
            book.TimestampTicks = _t0 + k * _interval;
            Quote(k, out long bid, out long ask, out long tick);
            ulong h = Hash(k ^ 0x2545F491);
            bool plunge = k < _plunge, crash = _profile == ArenaScenarioProfile.FlashCrash;
            long gap = plunge ? 2 : 1;
            for (int l = 0; l < 10; l++)
            {
                ulong hl = h >> (l * 5);
                long thin = plunge ? 1 + (long)(hl % 30) : 80 + (long)(hl % 400);
                long heavy = plunge ? 1_500 + (long)(hl % 3_001) : 150 + (long)(hl % 500);
                if (crash)
                {
                    book.BidPrice[l] = bid - l * gap * tick; book.BidQty[l] = thin;
                    book.AskPrice[l] = ask + l * tick; book.AskQty[l] = heavy;
                }
                else
                {
                    book.AskPrice[l] = ask + l * gap * tick; book.AskQty[l] = thin;
                    book.BidPrice[l] = bid - l * tick; book.BidQty[l] = heavy;
                }
                if (book.BidPrice[l] <= 0) { book.BidPrice[l] = 0; book.BidQty[l] = 0; }
            }
        }

        private void BurstTrade(int k, out ArenaTrade trade)
        {
            Quote(k, out long bid, out long ask, out _);
            ulong h = Hash(k ^ 0x6A09E667);
            int dir = _profile == ArenaScenarioProfile.FlashCrash ? -1 : 1;
            bool plunge = k < _plunge;
            int side = plunge || (h & 3) == 0 ? dir : -dir;                                    // 되돌림 = 반대 방향 우세 3/4
            long qty = plunge ? 300 + (long)((h >> 8) % 2_701) : 20 + (long)((h >> 8) % 281);
            trade = new ArenaTrade { Symbol = _symbol, Qty = qty, Side = side, PriceWon = side > 0 ? ask : Math.Max(1, bid) };
        }

        // ════════════ 여진(대상 종목 편차 선형 감쇠) ════════════

        private long OffsetAt(long baseTs)
        {
            long dt = baseTs - _t0;
            if (_recovery <= 0 || dt >= _recovery) return 0;
            if (dt < 0) dt = 0;
            return (long)((Int128)_endOffset * (_recovery - dt) / _recovery);
        }

        private static void ShiftBook(ref OrderBookPayload book, long off)
        {
            long prev = 0;
            for (int l = 0; l < 10; l++)
            {
                if (book.BidPrice[l] <= 0) continue;
                long p = book.BidPrice[l] + off;
                if (p <= 0) { book.BidPrice[l] = 0; book.BidQty[l] = 0; continue; }
                p = Align(p, up: false);
                if (prev > 0 && p >= prev) p = prev - SyntheticTickSource.TickSize(prev - 1);
                if (p <= 0) { book.BidPrice[l] = 0; book.BidQty[l] = 0; continue; }
                book.BidPrice[l] = p; prev = p;
            }
            prev = 0;
            for (int l = 0; l < 10; l++)
            {
                if (book.AskPrice[l] <= 0) continue;
                long p = Align(Math.Max(1, book.AskPrice[l] + off), up: true);
                if (prev > 0 && p <= prev) p = prev + SyntheticTickSource.TickSize(prev);
                book.AskPrice[l] = p; prev = p;
            }
        }

        private static long Align(long p, bool up)
        {
            long tick = SyntheticTickSource.TickSize(p);
            long r = p % tick;
            if (r == 0) return p;
            return up ? p - r + tick : Math.Max(tick, p - r);
        }

        private static int LowerBound(ArenaTickSource t, long ts)
        {
            int lo = 0, hi = t.Count;
            while (lo < hi)
            {
                int mid = (int)((uint)(lo + hi) >> 1);
                if (t.TimestampOf(mid) < ts) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        private ulong Hash(int k) => Mix(_seed + (ulong)(uint)k);

        private static ulong Mix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
}
