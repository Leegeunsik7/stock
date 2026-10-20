// ============================================================================
// SyntheticSpreadRadar — [v12.18.19-DEV] 멀티 브로커 합성 스프레드 레이더(키움 L2 ↔ KIS H0STASP0 · δ=0)
//
//  입력: 같은 종목 10호가 스냅샷(OrderBookPayload)을 venue 0 키움(브리지 MMF) · 1 KIS(WebSocket) 디스패치 스레드가 각자 OnBook.
//   슬롯 = 종목당 1(고정 64 · CAS 선점) · 다중 작성자(2 피드) seqlock(CAS 짝→홀 획득) — 판독자(펌프 · UI)는 잠금 0 · 할당 0.
//  대조(두 피드가 모두 신선 — 수신 시각 차 ≤ MaxSkew): 단위 cbp = 0.01bp.
//   합성 스프레드 = |Mid키움 − MidKIS| / 평균 Mid · 교차 스프레드 = (높은 매수 − 낮은 매도) / 평균 Mid(음수 = 정상 · 0 = 잠김 · 양수 = 역전) ·
//   10호가 잔량 비대칭 = (매수 − 매도) / 합 — 피드별 · 괴리 = |키움 − KIS|.
//  괴리 래치: 합성 스프레드 ≥ 30bp 가 500ms 지속 → sticky 래치 → 비트 22(OpenOrderDivergence) 2차 입력 Raise(비트 재결속 0 · 정의 불변 ·
//   대사기 AttachQuoteGuard) = 무장 차단. 해제 = TryRearm(물리 이중키)뿐.
//  역전(교차 > 0) 상승 에지 = 모의 차익 1건(레이더 자체 가상 원장 — 수량 = 두 최우선 잔량 최소 · 상한 MaxPaperQty · 손익 = 가격 차 × 수량).
//   래치 중 = 기록 거부(정합성 의심 데이터로 손익을 만들지 않음). ★같은 KRX 호가창을 두 증권사가 중계하므로 역전은 대개 한 피드의 노후다 —
//   '모의 차익'은 관측 지표일 뿐 실현 가능 손익이 아니다(정직).
//  방어: venue 별 전문 시각 역전(더 오래된 스냅샷) = 폐기(최신 상태 보존 · 계수) · 무효 호가(결측 · 교차 · 음수 잔량) = 폐기 · 테이블 포화 = 계수.
//  ★브로커 · 디스패처 · 무장 키 참조 0(IL 허용목록 테스트) — 실주문 송출 경로 없음(liveArmed:false 무관 · 구조적 0).
//  [v12.18.33-DEV] 인터락 경화:
//   ① 깊은 역전(교차 ≥ InversionTripCbp · 기본 30bp) = 같은 KRX 호가창 중계로는 불가능한 정합성 파손 → 지속 대기 없이 그 틱에 비트 22 래치.
//      얕은 역전(< 임계) = 기존대로 모의 차익 관측. 래치 틱의 역전 = 모의 기록 거부(래치 판정을 기록보다 먼저).
//   ② 피드 간 L1 전파 지연 = 두 피드 최우선 매수/매도 가격 불일치가 지속된 시간(한 피드가 같은 KRX 변경을 아직 미도달).
//      불일치 경과 > LagBudget(기본 50ms) 을 관측한 틱(진행 중 경과 · 또는 뒤처진 피드 도달로 확정된 지연) = 비트 28(OrderPathLatency) 3차 입력 래치.
//      노후 생략(> MaxSkew) 이전에 판정 — 피드 정지 + 가격 변경 = 산 피드의 다음 틱에 격발. 래치 = sticky · 해제 = TryRearm(두 래치 공통 · 물리 이중키).
//   ★한계(정직): 피드가 서로 다른 시점에서 병합(conflation)하면 급변 구간 불일치가 50ms 를 넘을 수 있다 — 이 경우도 차단(안전 방향).
//      가격이 같은 채로 한 피드만 멈추면 변경 전까지 미포착(관측할 지연이 없음).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arbitrage
{
    /// <summary>비트 22 2차 입력 수용자(호스트 = BrokerOpenOrderReconciler) — 레이더는 대사기 · 취소 경로 형식을 참조하지 않는다.</summary>
    public interface IQuoteGuardHost
    {
        void AttachQuoteGuard(IArmingBlockSource guard);
        void Resync();
    }

    /// <summary>[v12.18.33-DEV] 비트 28 3차 입력 수용자(호스트 = OrderPathLatencyInterlock) — 레이더는 지연 인터락 형식을 참조하지 않는다.</summary>
    public interface IFeedLatencyHost
    {
        void AttachCrossFeedGuard(IArmingBlockSource guard);
        void Resync();
    }

    public sealed class SyntheticSpreadRadar : IArbitrageRadarPort, IArmingBlockSource
    {
        public const int Capacity = 64, SparkLength = ArbRadar.SparkLength;
        public const long DefaultThresholdCbp = 3_000, DefaultSustainMicros = 500_000, DefaultMaxSkewMicros = 1_000_000, DefaultMaxPaperQty = 100;
        public const long DefaultLagBudgetMicros = 50_000, DefaultInversionTripCbp = DefaultThresholdCbp;
        public const int ReasonNone = 0, ReasonDivergence = 1, ReasonInversion = 2;

        /// <summary>비트 28 3차 입력 뷰 — 레이더 자체(비트 22 래치)와 분리된 전파 지연 래치만 노출.</summary>
        private sealed class LagGuard : IArmingBlockSource
        {
            private readonly SyntheticSpreadRadar _r;
            public LagGuard(SyntheticSpreadRadar r) => _r = r;
            public bool ArmingBlocked => _r.LagLatched;
        }

        private struct VenueBook
        {
            public OrderBookPayload Book;
            public long RxMicros, Books;
        }

        private struct Slot
        {
            public long Symbol;                         // 0 = 빈 슬롯 · CAS 선점(이후 불변)
            public int Seq;                             // 홀수 = 작성 중
            public VenueBook K, S;
            public long SpreadCbp, CrossCbp, ImbKCbp, ImbSCbp, Comparisons;
            public long DivergeSinceMicros;             // 0 = 괴리 아님
            public long LagSinceMicros;                 // 0 = L1 일치(전파 지연 없음)
            public int SinceEpoch, LagEpoch, Inverted;
        }

        private readonly Slot[] _slots = new Slot[Capacity];
        private readonly int[] _spark = new int[SparkLength];
        private readonly Func<long> _nowMicros;
        private readonly long _thresholdCbp, _sustainMicros, _maxSkewMicros, _maxPaperQty, _lagBudgetMicros, _inversionTripCbp;
        private readonly LagGuard _lagGuard;
        private long _sparkHead;                        // 펌프 단일 작성자
        private long _focusPinned, _focusAuto;
        private int _latched, _latchReason, _lagLatched, _epoch;
        private long _latchSymbol, _latchCbp, _trips;
        private long _lagSymbol, _lagLatchMicros, _lagTrips, _lagViolations, _maxLagMicros;
        private long _books, _invalid, _tsInversions, _staleSkips, _tableFull, _comparisons;
        private long _opportunities, _paperQty, _paperGrossWon, _paperRefusedLatched, _lastOppSymbol;
        private IQuoteGuardHost? _bit22;
        private IFeedLatencyHost? _bit28;

        public SyntheticSpreadRadar(Func<long>? nowMicros = null, long thresholdCbp = DefaultThresholdCbp, long sustainMicros = DefaultSustainMicros,
                                    long maxSkewMicros = DefaultMaxSkewMicros, long maxPaperQty = DefaultMaxPaperQty,
                                    long lagBudgetMicros = DefaultLagBudgetMicros, long inversionTripCbp = DefaultInversionTripCbp)
        {
            if (thresholdCbp <= 0 || sustainMicros < 0 || maxSkewMicros <= 0 || maxPaperQty <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdCbp));
            if (lagBudgetMicros <= 0) throw new ArgumentOutOfRangeException(nameof(lagBudgetMicros));
            if (inversionTripCbp <= 0) throw new ArgumentOutOfRangeException(nameof(inversionTripCbp));
            _nowMicros = nowMicros ?? StopwatchMicros;
            _thresholdCbp = thresholdCbp; _sustainMicros = sustainMicros; _maxSkewMicros = maxSkewMicros; _maxPaperQty = maxPaperQty;
            _lagBudgetMicros = lagBudgetMicros; _inversionTripCbp = inversionTripCbp;
            _lagGuard = new LagGuard(this);
        }

        private static long StopwatchMicros() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);

        /// <summary>비트 22 2차 입력 결선(콜드 1회) — 대사기 집계 술어에 이 레이더의 래치를 OR 한다.</summary>
        public void BindDivergenceInterlock(IQuoteGuardHost reconciler)
        {
            ArgumentNullException.ThrowIfNull(reconciler);
            reconciler.AttachQuoteGuard(this);
            Volatile.Write(ref _bit22, reconciler);
        }

        /// <summary>[v12.18.33-DEV] 비트 28 3차 입력 결선(콜드 1회) — 지연 인터락 차단 술어에 전파 지연 래치를 OR 한다.</summary>
        public void BindLatencyInterlock(IFeedLatencyHost interlock)
        {
            ArgumentNullException.ThrowIfNull(interlock);
            interlock.AttachCrossFeedGuard(_lagGuard);
            Volatile.Write(ref _bit28, interlock);
        }

        /// <summary>비트 22 래치(괴리 지속 ∨ 깊은 역전).</summary>
        public bool Latched => Volatile.Read(ref _latched) == 1;
        public bool ArmingBlocked => Latched;
        /// <summary>최초 비트 22 래치 원인(ReasonDivergence · ReasonInversion · 미래치 = ReasonNone).</summary>
        public int LatchReason => Volatile.Read(ref _latchReason);
        public long Trips => Interlocked.Read(ref _trips);
        /// <summary>[v12.18.33-DEV] 비트 28 전파 지연 래치.</summary>
        public bool LagLatched => Volatile.Read(ref _lagLatched) == 1;
        public long LagTrips => Interlocked.Read(ref _lagTrips);
        /// <summary>예산 초과 지연 관측 수(래치 중 포함 — 미포착 누락 0).</summary>
        public long LagViolations => Interlocked.Read(ref _lagViolations);
        public long LagLatchSymbol => Volatile.Read(ref _lagSymbol);
        public long LagLatchMicros => Volatile.Read(ref _lagLatchMicros);
        public long MaxLagMicros => Interlocked.Read(ref _maxLagMicros);
        public long LagBudgetMicros => _lagBudgetMicros;
        public long Books => Interlocked.Read(ref _books);
        public long InvalidBooks => Interlocked.Read(ref _invalid);
        public long TimestampInversions => Interlocked.Read(ref _tsInversions);
        public long StaleSkips => Interlocked.Read(ref _staleSkips);
        public long TableFull => Interlocked.Read(ref _tableFull);
        public long Opportunities => Interlocked.Read(ref _opportunities);
        public long PaperQty => Interlocked.Read(ref _paperQty);
        public long PaperGrossWon => Interlocked.Read(ref _paperGrossWon);
        public long PaperRefusedLatched => Interlocked.Read(ref _paperRefusedLatched);

        // ── 입력(피드 디스패치 스레드 · δ=0) ──

        /// <summary>10호가 1건 — venue 0 키움 · 1 KIS. 반환 = 이번 입력으로 두 피드 대조가 수행됐는가.</summary>
        public bool OnBook(int venue, in OrderBookPayload b)
        {
            if ((uint)venue > 1u || !Valid(in b)) { Interlocked.Increment(ref _invalid); return false; }
            int s = Acquire(b.SymbolCode);
            if (s < 0) { Interlocked.Increment(ref _tableFull); return false; }
            Interlocked.Increment(ref _books);
            long now = _nowMicros();
            ref var x = ref _slots[s];
            bool compared = false, trip = false, opp = false, lagTrip = false;
            long oppQty = 0, oppGross = 0, tripCbp = 0, lag = 0;
            int tripReason = ReasonNone;
            BeginWrite(ref x);
            try
            {
                ref var v = ref (venue == ArbRadar.VenueKiwoom ? ref x.K : ref x.S);
                if (v.Books > 0 && b.TimestampTicks > 0 && b.TimestampTicks < v.Book.TimestampTicks)
                {
                    Interlocked.Increment(ref _tsInversions);                           // 더 오래된 스냅샷 = 폐기(최신 상태 보존)
                    return false;
                }
                v.Book = b; v.RxMicros = now == 0 ? 1 : now; v.Books++;
                ref var o = ref (venue == ArbRadar.VenueKiwoom ? ref x.S : ref x.K);
                if (o.Books == 0) return false;
                int epoch = Volatile.Read(ref _epoch);
                if (v.Book.BidPrice[0] == o.Book.BidPrice[0] && v.Book.AskPrice[0] == o.Book.AskPrice[0])
                {
                    if (x.LagSinceMicros != 0 && x.LagEpoch == epoch) lag = v.RxMicros - x.LagSinceMicros;   // 뒤처진 피드 도달 = 이번 전파 지연 확정
                    x.LagSinceMicros = 0;
                }
                else if (x.LagSinceMicros == 0 || x.LagEpoch != epoch) { x.LagSinceMicros = v.RxMicros; x.LagEpoch = epoch; }
                else lag = v.RxMicros - x.LagSinceMicros;                               // 불일치 진행 중 = 경과(피드 정지도 산 피드 틱에서 포착)
                lagTrip = lag > _lagBudgetMicros;
                if (Math.Abs(v.RxMicros - o.RxMicros) > _maxSkewMicros)
                {
                    Interlocked.Increment(ref _staleSkips);                             // 한 피드 노후 = 대조 불가(괴리 타이머 리셋 · 판정 보류)
                    x.DivergeSinceMicros = 0;
                    goto Settle;                                                        // 전파 지연 판정은 노후 생략과 무관하게 반영
                }
                compared = true;
                ref readonly var k = ref x.K.Book;
                ref readonly var q = ref x.S.Book;
                long kb = k.BidPrice[0], ka = k.AskPrice[0], qb = q.BidPrice[0], qa = q.AskPrice[0];
                long mk2 = kb + ka, mq2 = qb + qa, sum = mk2 + mq2;
                long spreadCbp = Math.Abs(mk2 - mq2) * 2_000_000 / sum;
                long hiBid = Math.Max(kb, qb), loAsk = Math.Min(ka, qa);
                long crossCbp = (hiBid - loAsk) * 4_000_000 / sum;
                x.SpreadCbp = spreadCbp; x.CrossCbp = crossCbp;
                x.ImbKCbp = ImbalanceCbp(in k); x.ImbSCbp = ImbalanceCbp(in q);
                x.Comparisons++;
                Interlocked.Increment(ref _comparisons);
                if (spreadCbp >= _thresholdCbp)
                {
                    if (x.DivergeSinceMicros == 0 || x.SinceEpoch != epoch) { x.DivergeSinceMicros = v.RxMicros; x.SinceEpoch = epoch; }
                    else if (v.RxMicros - x.DivergeSinceMicros >= _sustainMicros) { trip = true; tripCbp = spreadCbp; tripReason = ReasonDivergence; }
                }
                else x.DivergeSinceMicros = 0;
                if (!trip && crossCbp >= _inversionTripCbp) { trip = true; tripCbp = crossCbp; tripReason = ReasonInversion; }   // 깊은 역전 = 그 틱 격발
                bool inverted = crossCbp > 0;
                if (inverted && x.Inverted == 0)
                {
                    opp = true;                                                         // 상승 에지 1회(같은 역전 지속 = 중복 0)
                    bool kSells = kb > qa;                                              // 키움 매수 호가 > KIS 매도 호가 = KIS 매수 · 키움 매도
                    long buyPx = kSells ? qa : ka, sellPx = kSells ? kb : qb;
                    long buyQty = kSells ? q.AskQty[0] : k.AskQty[0], sellQty = kSells ? k.BidQty[0] : q.BidQty[0];
                    oppQty = Math.Min(Math.Min(buyQty, sellQty), _maxPaperQty);
                    oppGross = (sellPx - buyPx) * oppQty;
                }
                x.Inverted = inverted ? 1 : 0;
            }
            finally { EndWrite(ref x); }
            Volatile.Write(ref _focusAuto, b.SymbolCode);
        Settle:
            if (lag > 0) MaxUpdate(ref _maxLagMicros, lag);
            if (lagTrip) TripLag(b.SymbolCode, lag);
            if (trip) Trip(b.SymbolCode, tripCbp, tripReason);                         // 래치 판정 먼저 — 래치 틱의 역전 = 모의 기록 거부
            if (opp)
            {
                if (Latched || LagLatched) Interlocked.Increment(ref _paperRefusedLatched);
                else
                {
                    Interlocked.Increment(ref _opportunities);
                    Interlocked.Add(ref _paperQty, oppQty);
                    Interlocked.Add(ref _paperGrossWon, oppGross);
                    Volatile.Write(ref _lastOppSymbol, b.SymbolCode);
                }
            }
            return compared;
        }

        private void Trip(long symbol, long cbp, int reason)
        {
            if (Interlocked.CompareExchange(ref _latched, 1, 0) != 0) return;          // 최초 원인만 기록(sticky)
            Volatile.Write(ref _latchSymbol, symbol);
            Volatile.Write(ref _latchCbp, cbp);
            Volatile.Write(ref _latchReason, reason);
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _bit22)?.Resync();                                        // ★비트 22 Raise — 무장 차단
        }

        private void TripLag(long symbol, long lagMicros)
        {
            Interlocked.Increment(ref _lagViolations);
            if (Interlocked.CompareExchange(ref _lagLatched, 1, 0) != 0) return;       // 최초 원인만 기록(sticky)
            Volatile.Write(ref _lagSymbol, symbol);
            Volatile.Write(ref _lagLatchMicros, lagMicros);
            Interlocked.Increment(ref _lagTrips);
            Volatile.Read(ref _bit28)?.Resync();                                        // ★비트 28 Raise — 무장 차단
        }

        private static void MaxUpdate(ref long target, long value)
        {
            long cur = Interlocked.Read(ref target);
            while (value > cur)
            {
                long prev = Interlocked.CompareExchange(ref target, value, cur);
                if (prev == cur) return;
                cur = prev;
            }
        }

        /// <summary>물리 이중키 재무장 — 두 래치(비트 22 · 28) 해제 + 괴리/지연 타이머 전 슬롯 재시작(지속 이상이면 재래치).</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _latchReason, ReasonNone);
            Volatile.Write(ref _latched, 0);
            Volatile.Write(ref _lagLatched, 0);
            Volatile.Read(ref _bit22)?.Resync();
            Volatile.Read(ref _bit28)?.Resync();
            return true;
        }

        // ── 펌프(허브 50ms · 단일 호출자 · δ=0) ──

        /// <summary>포커스 종목 합성 스프레드를 스파크라인 링에 1표본 기록(대조 전 = −1).</summary>
        public void Sample()
        {
            int s = Find(Focus);
            long cbp = -1;
            if (s >= 0 && TryReadSlot(s, out var snap) && snap.Comparisons > 0) cbp = snap.SpreadCbp;
            long h = _sparkHead;
            Volatile.Write(ref _spark[(int)(h & (SparkLength - 1))], (int)Math.Min(cbp, int.MaxValue));
            Volatile.Write(ref _sparkHead, h + 1);
        }

        // ── IArbitrageRadarPort(판독 전용) ──

        public long Focus { get { long p = Volatile.Read(ref _focusPinned); return p != 0 ? p : Volatile.Read(ref _focusAuto); } }

        public void SetFocus(long symbol) => Volatile.Write(ref _focusPinned, symbol < 0 ? 0 : symbol);

        public ArbitrageRadarStatus Status
        {
            get
            {
                long focus = Focus;
                int s = Find(focus);
                Slot snap = default;
                bool has = s >= 0 && TryReadSlot(s, out snap) && snap.Comparisons > 0;
                int state = Latched ? ArbRadar.Latched
                          : !has ? ArbRadar.Waiting
                          : snap.SpreadCbp >= _thresholdCbp ? ArbRadar.Diverging                 // 위험(괴리) 표시가 기회(역전)보다 우선
                          : snap.CrossCbp > 0 ? ArbRadar.Inverted : ArbRadar.Ok;
                return new ArbitrageRadarStatus(state, focus, has ? snap.SpreadCbp : 0, has ? snap.CrossCbp : 0, has ? snap.ImbKCbp : 0, has ? snap.ImbSCbp : 0,
                    Interlocked.Read(ref _comparisons), Opportunities, PaperQty, PaperGrossWon, PaperRefusedLatched, TimestampInversions, StaleSkips, InvalidBooks,
                    Trips, Volatile.Read(ref _latchSymbol), Volatile.Read(ref _latchCbp), _thresholdCbp, Volatile.Read(ref _sparkHead));
            }
        }

        /// <summary>포커스 종목 venue 10호가 사본(seqlock · 각 Span ≥ 10). 반환 = 해당 venue 수신 이력 있음.</summary>
        public bool CopyDepth(int venue, Span<long> bidPx, Span<long> bidQty, Span<long> askPx, Span<long> askQty)
        {
            if ((uint)venue > 1u || bidPx.Length < 10 || bidQty.Length < 10 || askPx.Length < 10 || askQty.Length < 10) return false;
            int s = Find(Focus);
            if (s < 0 || !TryReadSlot(s, out var snap)) return false;
            ref readonly var v = ref (venue == ArbRadar.VenueKiwoom ? ref snap.K : ref snap.S);
            if (v.Books == 0) return false;
            for (int i = 0; i < 10; i++) { bidPx[i] = v.Book.BidPrice[i]; bidQty[i] = v.Book.BidQty[i]; askPx[i] = v.Book.AskPrice[i]; askQty[i] = v.Book.AskQty[i]; }
            return true;
        }

        /// <summary>스파크라인(오래된 → 최신 · −1 = 대조 전). 반환 = 채운 표본 수.</summary>
        public int CopySpark(Span<int> dst)
        {
            long head = Volatile.Read(ref _sparkHead);
            int n = (int)Math.Min(Math.Min(head, SparkLength), dst.Length);
            for (int i = 0; i < n; i++) dst[i] = Volatile.Read(ref _spark[(int)((head - n + i) & (SparkLength - 1))]);
            return n;
        }

        // ── 내부 ──

        private static bool Valid(in OrderBookPayload b)
        {
            long bid = b.BidPrice[0], ask = b.AskPrice[0];
            if (b.SymbolCode <= 0 || bid <= 0 || ask <= bid) return false;
            long bq = 0, aq = 0;
            for (int i = 0; i < 10; i++)
            {
                if (b.BidQty[i] < 0 || b.AskQty[i] < 0) return false;
                bq += b.BidQty[i]; aq += b.AskQty[i];
            }
            return bq + aq > 0;
        }

        private static long ImbalanceCbp(in OrderBookPayload b)
        {
            long bq = 0, aq = 0;
            for (int i = 0; i < 10; i++) { bq += b.BidQty[i]; aq += b.AskQty[i]; }
            return (bq - aq) * 1_000_000 / (bq + aq);
        }

        private int Acquire(long symbol)
        {
            int start = (int)((ulong)(symbol * unchecked((long)0x9E3779B97F4A7C15UL)) >> 58);   // 상위 6비트 = 64 슬롯
            for (int i = 0; i < Capacity; i++)
            {
                int s = (start + i) & (Capacity - 1);
                long cur = Volatile.Read(ref _slots[s].Symbol);
                if (cur == symbol) return s;
                if (cur == 0)
                {
                    long prev = Interlocked.CompareExchange(ref _slots[s].Symbol, symbol, 0);
                    if (prev == 0 || prev == symbol) return s;
                }
            }
            return -1;
        }

        private int Find(long symbol)
        {
            if (symbol <= 0) return -1;
            int start = (int)((ulong)(symbol * unchecked((long)0x9E3779B97F4A7C15UL)) >> 58);
            for (int i = 0; i < Capacity; i++)
            {
                int s = (start + i) & (Capacity - 1);
                long cur = Volatile.Read(ref _slots[s].Symbol);
                if (cur == symbol) return s;
                if (cur == 0) return -1;
            }
            return -1;
        }

        private static void BeginWrite(ref Slot x)
        {
            var spin = new SpinWait();
            while (true)
            {
                int q = Volatile.Read(ref x.Seq);
                if ((q & 1) == 0 && Interlocked.CompareExchange(ref x.Seq, q + 1, q) == q) return;   // 두 피드 작성자 상호 배제
                spin.SpinOnce(-1);
            }
        }

        private static void EndWrite(ref Slot x) => Volatile.Write(ref x.Seq, x.Seq + 1);

        private bool TryReadSlot(int s, out Slot snap)
        {
            ref var x = ref _slots[s];
            var spin = new SpinWait();
            for (int attempt = 0; attempt < 64; attempt++)
            {
                int q1 = Volatile.Read(ref x.Seq);
                if ((q1 & 1) == 0)
                {
                    snap = x;
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref x.Seq) == q1) return true;
                }
                spin.SpinOnce(-1);
            }
            snap = default;
            return false;                                                               // 작성 폭주 = 이번 판독 생략(찢긴 값 게시 0)
        }
    }
}
