// ============================================================================
// MultiSymbolAiSignalRouter — [v12.18.45-DEV] 다종목 AI 신호 격리 라우터(종목별 AiSignalGovernor) + KRX 호가 보호 정렬(비트 27 결속)
//
//  왜: AiSignalGovernor(§131)는 인스턴스 1개 = 모델 스트림 1개 — 여러 종목 신호를 한 인스턴스에 넣으면 A 매수 → B 매도가 '반전'으로
//   세어져 정상 다종목 운용이 플립플롭 오탐(교차 종목 간섭)으로 래치되고, 시계 역행/시효 판정도 종목 사이에서 섞인다.
//  구조(전부 생성 시 1회 할당 · 핫패스 δ=0):
//   · 슬롯 = 미리 만든 AiSignalGovernor N개(1 ~ 64). 종목 → 슬롯 = 개방 주소 해시(long[] 키 · int[] 값 · 크기 2의 거듭제곱 ≥ 2N · 선형 탐사).
//     처음 보는 종목 = 빈 슬롯 배정(그 회차 · 해제 없음) · 슬롯 소진 = 거부(SlotsExhausted · 기존 종목 무영향). 종목 코드 = KRX 6자리(1 ~ 999,999).
//   · 격리: 플립플롭 링 · 직전 극단 방향 · 시계(역행 판정) · 계수가 슬롯마다 독립 — A 의 반전 이력이 B 판정에 섞이지 않는다.
//   · ★전역 동결: 전 슬롯이 '같은' StrategyAnomalyInterlock 을 공유 — 어느 종목이든 NaN/∞ · 범위 이탈 · 플립플롭 = 비트 27 래치 →
//     다음 회차부터 전 종목 Latched(발주 후보 0). 재무장 = 물리 이중키 + 전 슬롯 링 소거.
//   · 정렬: 승인 후보의 참조 지정가(raw 10^8)를 KrxTickAligner(KrxTickLadder 2023 7구간과 같은 규칙 · 무할당)로 보호 방향 스냅 —
//     매수 = 내림(더 비싸게 사지 않음) · 매도 = 올림(더 싸게 팔지 않음). 참조가 ≤0 · 정렬 불가 = 거부(InvalidPrice · 신호 미평가 → 이력 무변경).
//  [v12.18.46-DEV] 가격제한폭 · 종목별 수량 캡:
//   · 기준가(raw 10^8) 필수 — 상한가 = ×1.3 내림 · 하한가 = ×0.7 올림(ProtectiveLimitOrderConverter.TryPriceBand · 둘 다 격자점).
//     기준가 ≤0 · 포화 = 거부(InvalidBasePrice · 신호 미평가 → 슬롯/이력 무변경).
//   · 보호 방향 이탈은 클램프: 매수 정렬가 > 상한가 → 상한가 · 매도 정렬가 < 하한가 → 하한가(격자점이라 재스냅 불변).
//     반대 방향 이탈(매수 < 하한가 · 매도 > 상한가)은 참조가 자체가 제한폭 밖 이상치 → 거부(PriceBandBreach · 변환기와 같은 규칙 ·
//     방향은 평가 후에야 확정되므로 신호 이력은 반영됨 — 신호는 정상, 가격만 이상).
//   · 수량 = min(사이징 수량, 종목 캡의 로트 내림). 캡 < 로트 = 거부(InvalidQuantityCap · 신호 미평가). 기본 캡 = 무제한(생성 인자만).
//  ★정직: 호스트 DI 미결속(실 AI 모델 피드 부재 — 가짜 배선 금지). 산출 = '정규 주문 후보'일 뿐 무장/송출 판정은 기존 디스패처 경로
//   (liveArmed:false 불변 · 이 파일에 브로커 참조 0).
//  동시성: 호출자 = 단일(모델 스트림 → 라우터 1개). 방어적 CAS 진입 — 경합 = 그 회차 거부(Contended · fail-closed).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Strategy
{
    public enum AiRouteVerdict : byte
    {
        Accepted = 0, Latched = 1, NonFinite = 2, OutOfRange = 3, ClockRegressed = 4, FutureTimestamp = 5, Stale = 6, FlipFlop = 7,
        BelowThreshold = 8, Contended = 9, InvalidSymbol = 10, SlotsExhausted = 11, InvalidPrice = 12,
        InvalidBasePrice = 13, PriceBandBreach = 14, InvalidQuantityCap = 15,
    }

    /// <summary>정규 주문 후보(Side +1 매수 / −1 매도 · LimitRaw = KRX 격자 정렬 지정가 raw 10^8).</summary>
    public readonly record struct AiRoutedOrder(long SymbolCode, int Side, long Quantity, long LimitRaw, int ConfidenceBp, int Slot);

    public sealed class MultiSymbolAiSignalRouter
    {
        public const int MaxSlots = 64;
        public const long MaxSymbolCode = 999_999;

        private readonly StrategyAnomalyInterlock _anomaly;
        private readonly AiSignalGovernor[] _slots;
        private readonly long[] _slotSymbol;
        private readonly long[] _keys;
        private readonly int[] _vals;
        private readonly int _mask, _shift;
        private readonly long _lot;
        private int _used, _busy;
        private long _routed, _accepted, _crossSymbolRejects, _invalidSymbol, _exhausted, _invalidPrice, _contended, _snappedDown, _snappedUp;
        private long _invalidBase, _bandBreach, _invalidCap, _bandClamped, _qtyCapped;

        /// <param name="anomaly">비트 27 소스(전 슬롯 공유 = 전역 동결).</param>
        /// <param name="slots">동시 운용 종목 수 상한(1 ~ 64).</param>
        /// <param name="maxQuantity">확신 1.0 수량(종목 공통 · ≤ 10⁹).</param>
        public MultiSymbolAiSignalRouter(StrategyAnomalyInterlock anomaly, int slots, long maxQuantity, long lotSize = 1,
                                         long flipWindowMicros = AiSignalGovernor.DefaultFlipWindowMicros, int maxFlips = AiSignalGovernor.DefaultMaxFlips,
                                         long maxLatencyMicros = AiSignalGovernor.DefaultMaxLatencyMicros, double flipExtreme = AiSignalGovernor.DefaultFlipExtreme,
                                         int minConfidenceBp = AiSignalGovernor.DefaultMinConfidenceBp)
        {
            _anomaly = anomaly ?? throw new ArgumentNullException(nameof(anomaly));
            if (slots < 1 || slots > MaxSlots) throw new ArgumentOutOfRangeException(nameof(slots));
            _slots = new AiSignalGovernor[slots];
            for (int i = 0; i < slots; i++)
                _slots[i] = new AiSignalGovernor(anomaly, maxQuantity, lotSize, flipWindowMicros, maxFlips, maxLatencyMicros, flipExtreme, minConfidenceBp);
            _slotSymbol = new long[slots];
            _lot = lotSize;
            int bits = 1;
            while ((1 << bits) < slots * 2) bits++;
            _keys = new long[1 << bits];
            _vals = new int[1 << bits];
            _mask = (1 << bits) - 1;
            _shift = 64 - bits;
        }

        public StrategyAnomalyInterlock Anomaly => _anomaly;
        public int Capacity => _slots.Length;
        public int SymbolsInUse => Volatile.Read(ref _used);
        public long Routed => Interlocked.Read(ref _routed);
        public long AcceptedCount => Interlocked.Read(ref _accepted);
        public long InvalidSymbolRejects => Interlocked.Read(ref _invalidSymbol);
        public long SlotsExhaustedRejects => Interlocked.Read(ref _exhausted);
        public long InvalidPriceRejects => Interlocked.Read(ref _invalidPrice);
        public long Contended => Interlocked.Read(ref _contended);
        /// <summary>매수 내림 / 매도 올림으로 가격이 실제로 바뀐 승인 수.</summary>
        public long SnappedDown => Interlocked.Read(ref _snappedDown);
        public long SnappedUp => Interlocked.Read(ref _snappedUp);
        public long InvalidBasePriceRejects => Interlocked.Read(ref _invalidBase);
        public long PriceBandBreaches => Interlocked.Read(ref _bandBreach);
        public long InvalidQuantityCapRejects => Interlocked.Read(ref _invalidCap);
        /// <summary>정렬가가 보호 방향으로 제한폭을 넘어 상·하한가로 고정된 승인 수.</summary>
        public long BandClamped => Interlocked.Read(ref _bandClamped);
        /// <summary>사이징 수량이 종목 캡으로 절삭된 승인 수.</summary>
        public long QuantityCapped => Interlocked.Read(ref _qtyCapped);
        /// <summary>래치 중 거부(전역 동결 — 원인 종목과 다른 종목 포함).</summary>
        public long LatchedRejects => Interlocked.Read(ref _crossSymbolRejects);

        /// <summary>슬롯에 배정된 종목 코드(관제 · 미배정 = 0).</summary>
        public long SymbolAt(int slot) => (uint)slot < (uint)_slotSymbol.Length && slot < SymbolsInUse ? Volatile.Read(ref _slotSymbol[slot]) : 0;

        /// <summary>종목 슬롯의 관문(관제 · 판독 전용). 미배정 = null.</summary>
        public AiSignalGovernor? GovernorFor(long symbolCode)
        {
            int s = Find(symbolCode);
            return s < 0 ? null : _slots[s];
        }

        /// <summary>신호 1건 라우팅(δ=0 · O(1) 기대). Accepted 일 때만 order 유효 — 그 외 order = default(수량 0).</summary>
        /// <param name="referenceLimitRaw">후보 지정가 참조(raw 10^8 · 예: 최우선 호가). 보호 방향으로 KRX 격자 스냅.</param>
        /// <param name="basePriceRaw">가격제한폭 기준가(raw 10^8 · 전일 종가). ≤0 = 평가 전 거부.</param>
        /// <param name="maxSymbolQty">종목별 주문 수량 상한(로트 내림 적용 · 기본 = 무제한 → 생성 인자 상한만).</param>
        public AiRouteVerdict Route(in AiSignal signal, long referenceLimitRaw, long basePriceRaw, long nowMicros, out AiRoutedOrder order,
                                    long maxSymbolQty = long.MaxValue)
        {
            order = default;
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) { Interlocked.Increment(ref _contended); return AiRouteVerdict.Contended; }
            try
            {
                Interlocked.Increment(ref _routed);
                long sym = signal.SymbolCode;
                if (sym < 1 || sym > MaxSymbolCode) { Interlocked.Increment(ref _invalidSymbol); return AiRouteVerdict.InvalidSymbol; }
                if (_anomaly.Tripped) { Interlocked.Increment(ref _crossSymbolRejects); return AiRouteVerdict.Latched; }   // 전역 동결 — 슬롯 배정/평가 0
                if (!ProtectiveLimitOrderConverter.TryPriceBand(basePriceRaw, out long lower, out long upper)) { Interlocked.Increment(ref _invalidBase); return AiRouteVerdict.InvalidBasePrice; }
                long qtyCap = maxSymbolQty - maxSymbolQty % _lot;                               // 로트 내림(음수 캡도 < 로트로 거부)
                if (qtyCap < _lot) { Interlocked.Increment(ref _invalidCap); return AiRouteVerdict.InvalidQuantityCap; }
                // 양방향 정렬 가능성을 '평가 전'에 확인 — 정렬 불가 참조가로 신호 이력(반전 링 · 시계)이 오염되지 않게(방향은 평가 후에야 확정)
                if (KrxTickAligner.TickFor(referenceLimitRaw) == 0 || KrxTickAligner.AlignUp(referenceLimitRaw) <= 0) { Interlocked.Increment(ref _invalidPrice); return AiRouteVerdict.InvalidPrice; }
                int s = FindOrClaim(sym);
                if (s < 0) { Interlocked.Increment(ref _exhausted); return AiRouteVerdict.SlotsExhausted; }

                var v = _slots[s].Evaluate(in signal, nowMicros, out var sized);
                if (v != AiSignalVerdict.Accepted)
                {
                    if (v == AiSignalVerdict.Latched) Interlocked.Increment(ref _crossSymbolRejects);
                    return (AiRouteVerdict)(byte)v;                                         // 0 ~ 9 동일 서수
                }
                bool buy = sized.Side > 0;
                long aligned = buy ? KrxTickAligner.AlignDown(referenceLimitRaw) : KrxTickAligner.AlignUp(referenceLimitRaw);
                if (aligned <= 0) { Interlocked.Increment(ref _invalidPrice); return AiRouteVerdict.InvalidPrice; }   // 방어(사전 확인으로 도달 불가)
                if (buy ? aligned < lower : aligned > upper) { Interlocked.Increment(ref _bandBreach); return AiRouteVerdict.PriceBandBreach; }
                long limit = buy ? Math.Min(aligned, upper) : Math.Max(aligned, lower);         // 상·하한가 = 격자점 → 클램프 후에도 격자
                long qty = Math.Min(sized.Quantity, qtyCap);
                if (aligned != referenceLimitRaw) { if (buy) Interlocked.Increment(ref _snappedDown); else Interlocked.Increment(ref _snappedUp); }
                if (limit != aligned) Interlocked.Increment(ref _bandClamped);
                if (qty != sized.Quantity) Interlocked.Increment(ref _qtyCapped);
                order = new AiRoutedOrder(sym, sized.Side, qty, limit, sized.ConfidenceBp, s);
                Interlocked.Increment(ref _accepted);
                return AiRouteVerdict.Accepted;
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        /// <summary>KRX 티커("005930" · "A005930") → 종목 코드(δ=0). 형식 이탈 = 0(무효).</summary>
        public static long ParseTicker(ReadOnlySpan<char> ticker)
        {
            ticker = ticker.Trim();
            if (ticker.Length == 7 && (ticker[0] == 'A' || ticker[0] == 'a')) ticker = ticker[1..];
            if (ticker.Length != 6) return 0;
            long code = 0;
            for (int i = 0; i < 6; i++)
            {
                int d = ticker[i] - '0';
                if ((uint)d > 9) return 0;
                code = code * 10 + d;
            }
            return code;
        }

        /// <summary>재무장(물리 이중키) — 전 슬롯 반전 링 · 직전 방향 소거 + 공유 인터락 재무장. 종목 ↔ 슬롯 배정은 유지.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            while (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) Thread.SpinWait(8);
            try
            {
                for (int i = 0; i < _slots.Length; i++) _slots[i].TryRearm(valveWired, liveArmingWired);
                return _anomaly.TryRearm(valveWired, liveArmingWired);
            }
            finally { Volatile.Write(ref _busy, 0); }
        }

        private int Home(long sym) => (int)((ulong)(sym * unchecked((long)0x9E3779B97F4A7C15UL)) >> _shift) & _mask;

        private int Find(long sym)
        {
            if (sym < 1 || sym > MaxSymbolCode) return -1;
            for (int h = Home(sym), n = 0; n <= _mask; h = (h + 1) & _mask, n++)
            {
                long k = Volatile.Read(ref _keys[h]);
                if (k == sym) return _vals[h];
                if (k == 0) return -1;
            }
            return -1;
        }

        // 단일 작성자(_busy 보유) — 값 먼저 기록 후 키 공개(판독자 GovernorFor 는 키가 보이면 값도 유효).
        private int FindOrClaim(long sym)
        {
            for (int h = Home(sym), n = 0; n <= _mask; h = (h + 1) & _mask, n++)
            {
                long k = _keys[h];
                if (k == sym) return _vals[h];
                if (k != 0) continue;
                if (_used >= _slots.Length) return -1;                                      // 슬롯 소진(표는 2배라 빈칸은 남음)
                int s = _used;
                _slotSymbol[s] = sym;
                _vals[h] = s;
                Volatile.Write(ref _keys[h], sym);
                Volatile.Write(ref _used, s + 1);
                return s;
            }
            return -1;
        }
    }
}
