// ============================================================================
// LiveAccountingMesh — v4.96.0: 실전 자본 영점 조절 및 무할당 실시간 청산 메쉬
//
//  체결 통지(OnFill) 접수 즉시 이종 브로커·국가별 실전 제비용(증권사 위탁수수료, 거래세, 농어촌특별세,
//  해외 혼합수수료)을 고정소수점 정수(QMath 10^8)로만 산정해 백엔드 장부 평단·가용 예수금을 1raw 오차 없이
//  실시간 영점 조절한다.
//
//  ── 정직 회계(정찰 6렌즈 확정) ──────────────────────────────────────────────
//  체결시점 δ=0 정산·농특세 분리·US 혼합수수료(센트 고정+비율)·정밀 잔여 캐리·2단계 청산 래치·예수금 영점은
//  전무→신규. 수수료율은 기존 4곳(BrokerFeeSchedule·TradingCost·PaperBroker·ZeroAllocTickEngine)에 불일치 중복 →
//  본 메쉬가 단일 소스 FeeTaxSchedule 로 정직 통합(raw). 재사용: QMath.Notional(price×qty)·TryReserveInto CAS 패턴·
//  RingBufferCore seqlock·ForceFreeze 단일 소스. QMath 는 미변경(광범위 재사용) — 잔여는 본 메쉬 내 Int128 인라인.
//
//  ── ★Think Further 1: 정밀 잔여 캐리 가드(Exact Remainder Carry Guard) ────────
//  QMath 비율 나눗셈은 서브-raw 잔여를 floor 절사해 버린다. 하루 수만 체결에서 누적되면 '원인 미상 장부
//  드레이프'가 생긴다. 방어: ★항목·통화별 독립 long 캐리(위탁/거래세/농특세/해외위탁/코인)에 floor 잔여를 이월 →
//  누산 ≥ Scale 이면 그 회차 비용에 +1 spill. 항목별 분리라 각 세목(거래세·농특세) 누적이 자기값으로 0 수렴한다
//  (공유 캐리는 spill 귀속이 계산순서 아티팩트가 되어 분리 세목이 드리프트하므로 금지 — 적대검증 C#1/C#7 교정).
//
//  ── ★Think Further 2: 스펙큘러티브 2단계 청산 래치(Speculative Two-Phase Latch) ─
//  · 1단계(핫·δ=0): 매수는 보수 상향 추정치를 통화별 예수금 셀에 CAS(TryDebit) '즉시' 원자 차감(TOCTOU-free·오버플로/
//    초과 트랩). ★매도는 예수금을 '늘리는' 연산이므로 수수료 선차감 안 함(풀-인베스트 상태 정상 매도 오탐 동결 방지·C#3).
//    체결을 값타입 링(RingBufferCore SPSC)에 무할당 발행 후 즉시 복귀. 가용이 항상 보수적(하향)이라 double-spend 봉쇄.
//  · 2단계(회계 스레드·BelowNormal): 순차 징수 — exact 제비용(항목별 캐리) 산정 → 매수는 추정↔실제 상계, 매도는
//    ★대금 credit '먼저' → 수수료 차감(중간 음수 오탐 없음). 평단 장부 영점·누적 수수료/세목 확정. 링 추월 감지 시 fail-closed.
//
//  ── ★통화 격리(적대검증 C#4 교정) ────────────────────────────────────────────
//  예수금·캐리·누계를 통화별로 분리한다. 국내주식·코인(Upbit KRW-호가)=KRW 셀, 해외주식=USD 셀. 단일 셀 commingling
//  (원·달러 혼합 차감)은 예수금을 무의미하게 만들므로 금지. 라이브 배선은 키움(국내 KRW)이라 KRW 경로만 활성(정직).
//
//  ── liveArmed:false 불변(구조적 절연) ────────────────────────────────────────
//  본 메쉬는 내부 원장(원자 long 셀·per-symbol 장부)만 영점 조절한다 — IBroker·소켓·SendOrderAsync·PostOrder 참조가
//  0건이다. 외부 증권사로 향하는 어떤 아웃바운드(허위 체결 동기화·주문)도 만들 구조적 경로가 없다.
//
//  ── fail-closed ─────────────────────────────────────────────────────────────
//  명목/비용 오버플로·예수금 초과 인출·보유 초과 매도(유령 대금)·링 추월(회계 누락) → 예외 삼킴 없이
//  OrderAttestationService.ForceFreeze 단일 소스로 연쇄 잠금 + StateCode=Frozen.
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>정산 자산 분류 — 국내주식(원화 정수)·해외주식(달러 소수)·코인(원화 호가).</summary>
    public enum AssetKind : byte { Domestic = 0, Foreign = 1, Crypto = 2 }

    /// <summary>메쉬 상태 — 0 유휴/1 가동(영점 동기)/2 동결(디싱크·오버플로).</summary>
    public enum AccountingState { Idle = 0, Active = 1, Frozen = 2 }

    /// <summary>실전 제비용 단일 소스 스케줄(QFixed raw = 비율×10^8). ★4곳 불일치 상수를 정직 통합. 정책 변경 시 주입.</summary>
    public readonly struct FeeTaxSchedule
    {
        public readonly long KrxCommRateRaw;   // 국내 위탁수수료율
        public readonly long KrxStampRateRaw;  // 국내 거래세율(매도)
        public readonly long KrxFarmRateRaw;   // 국내 농어촌특별세율(매도)
        public readonly long UsCommRateRaw;    // 해외 위탁수수료율(양방향)
        public readonly long UsFixedRaw;       // 해외 건당 고정비(센트·양방향)
        public readonly long CryptoRateRaw;    // 코인 수수료율(양방향)
        public FeeTaxSchedule(long krxComm, long krxStamp, long krxFarm, long usComm, long usFixed, long crypto)
        { KrxCommRateRaw = krxComm; KrxStampRateRaw = krxStamp; KrxFarmRateRaw = krxFarm; UsCommRateRaw = usComm; UsFixedRaw = usFixed; CryptoRateRaw = crypto; }

        /// <summary>기본(통합) 스케줄. 국내 위탁 0.015%·거래세 0.05%·농특세 0.15%(매도), 해외 0.025%+$0.01/건, 코인 0.05%.</summary>
        public static FeeTaxSchedule Default => new(
            krxComm: 15_000L, krxStamp: 50_000L, krxFarm: 150_000L,
            usComm: 25_000L, usFixed: 1_000_000L /*0.01×10^8*/, crypto: 50_000L);
    }

    /// <summary>체결 정산 큐 단위(blittable·RingBufferCore 슬롯 복사 전용). EstDebitRaw = 핫패스가 선차감한 추정치(매도는 0).</summary>
    public readonly struct FillEvent
    {
        public readonly long SymbolCode;
        public readonly byte SideByte;    // OrderSide(Buy/Sell)
        public readonly byte KindByte;    // AssetKind
        public readonly long PriceRaw;
        public readonly long QtyRaw;
        public readonly long EstDebitRaw; // 핫패스 CAS 선차감 추정치(매수만·2단계 상계 기준·매도 0)
        public readonly long TimestampTicks;
        public FillEvent(long symbolCode, OrderSide side, AssetKind kind, long priceRaw, long qtyRaw, long estDebitRaw, long ts)
        { SymbolCode = symbolCode; SideByte = (byte)side; KindByte = (byte)kind; PriceRaw = priceRaw; QtyRaw = qtyRaw; EstDebitRaw = estDebitRaw; TimestampTicks = ts; }
        public OrderSide Side => (OrderSide)SideByte;
        public AssetKind Kind => (AssetKind)KindByte;
    }

    /// <summary>실시간 청산 메쉬 — δ=0 OnFill(1단계) + 회계 스레드(2단계) 2단계 청산 래치.</summary>
    public sealed class LiveAccountingMesh : IDisposable
    {
        private readonly OrderAttestationService? _att;
        private readonly FeeTaxSchedule            _sched;
        private readonly RingBufferCore<FillEvent> _ring;
        private readonly RingConsumer<FillEvent>   _consumer;

        private readonly long _seedKrwRaw, _seedUsdRaw;
        private long _availableKrwRaw, _availableUsdRaw;   // ★통화별 예수금 셀(commingling 금지)
        private int  _producing;                            // 단일 생산자 게이트(동시 체결 생산자 fail-closed)
        private int  _stateCode;

        // 회계 스레드 전용(비경합) — ★항목·통화별 독립 정밀 잔여 캐리(공유 금지·C#1/C#7).
        private long _carryComm, _carryStamp, _carryFarm, _carryUsComm, _carryCrypto;
        // 텔레메트리(회계 스레드 write·UI Interlocked read).
        private long _cumFeeKrwRaw, _cumFeeUsdRaw, _cumStampRaw, _cumFarmRaw;
        private long _fillsAccepted, _fillsFinalized, _droppedFills;
        // per-symbol 장부 — 회계 스레드 write + public 독출 → ★_book 락으로 보호(U#1/2/3).
        private readonly object _bookLock = new();
        private readonly Dictionary<long, (long qtyRaw, long costRaw)> _book = new();
        private long _bookValueRaw;

        private Thread?       _thread;
        private volatile bool _stop;
        private readonly int  _spinIdleMs;

        /// <param name="seedKrwRaw">원화 예수금 시드.</param>
        /// <param name="seedUsdRaw">달러 예수금 시드(해외 계좌·기본 0).</param>
        public LiveAccountingMesh(long seedKrwRaw, OrderAttestationService? attestation = null,
                                  FeeTaxSchedule? schedule = null, int capacity = 8192,
                                  int idleSleepMs = 2, long seedUsdRaw = 0)
        {
            if (seedKrwRaw < 0 || seedUsdRaw < 0) throw new ArgumentOutOfRangeException(nameof(seedKrwRaw), "시드 예수금 음수 불가");
            _att       = attestation;
            _sched     = schedule ?? FeeTaxSchedule.Default;
            _ring      = new RingBufferCore<FillEvent>(capacity);
            _consumer  = _ring.CreateConsumer();
            _seedKrwRaw = seedKrwRaw; _seedUsdRaw = seedUsdRaw;
            _availableKrwRaw = seedKrwRaw; _availableUsdRaw = seedUsdRaw;
            _spinIdleMs = idleSleepMs < 1 ? 1 : idleSleepMs;
        }

        private static bool IsKrw(AssetKind k) => k != AssetKind.Foreign;   // 국내·코인(Upbit KRW)=KRW, 해외=USD

        // ── 텔레메트리(락-프리 독출) ────────────────────────────────────────────
        public long SeedDepositRaw      => _seedKrwRaw;
        public long AvailableDepositRaw => Interlocked.Read(ref _availableKrwRaw);   // 주 통화(KRW)
        public long AvailableUsdRaw     => Interlocked.Read(ref _availableUsdRaw);
        public long CumFeeKrwRaw        => Interlocked.Read(ref _cumFeeKrwRaw);
        public long CumFeeUsdRaw        => Interlocked.Read(ref _cumFeeUsdRaw);
        public long CumStampTaxRaw      => Interlocked.Read(ref _cumStampRaw);   // 거래세
        public long CumFarmTaxRaw       => Interlocked.Read(ref _cumFarmRaw);    // 농특세
        public long FillsAccepted       => Interlocked.Read(ref _fillsAccepted);
        public long FillsFinalized      => Interlocked.Read(ref _fillsFinalized);
        public long DroppedFills        => Interlocked.Read(ref _droppedFills);
        public long BookValueRaw        => Interlocked.Read(ref _bookValueRaw);
        /// <summary>★외부 ForceFreeze(타 서브시스템 동결)도 반영 — 거짓 녹색 인디케이터 방지(C#8).</summary>
        public AccountingState State    => IsFrozen ? AccountingState.Frozen : (AccountingState)Volatile.Read(ref _stateCode);
        public bool IsFrozen            => Volatile.Read(ref _stateCode) == (int)AccountingState.Frozen
                                        || (_att is { } a && a.Mode == GatingMode.EmergencyFreeze);
        /// <summary>정밀 잔여 캐리 오차(항목·통화 최대 절대치·raw). 건강하면 항상 &lt; Scale(1.0).</summary>
        public long CarryErrorRaw
        {
            get
            {
                long m = 0;
                m = Math.Max(m, Math.Abs(Interlocked.Read(ref _carryComm)));
                m = Math.Max(m, Math.Abs(Interlocked.Read(ref _carryStamp)));
                m = Math.Max(m, Math.Abs(Interlocked.Read(ref _carryFarm)));
                m = Math.Max(m, Math.Abs(Interlocked.Read(ref _carryUsComm)));
                m = Math.Max(m, Math.Abs(Interlocked.Read(ref _carryCrypto)));
                return m;
            }
        }
        public long LatchQueueDepth     { get { long d = _ring.Published - _consumer.Cursor; return d < 0 ? 0 : d; } }
        public bool DepositZeroSynced   => !IsFrozen && AvailableDepositRaw >= 0 && AvailableUsdRaw >= 0 && CarryErrorRaw < QMath.Scale;

        // UI 소수점 투명 표출 헬퍼(raw→decimal, 표출 직전 1회 변환).
        public decimal CumFeeKrw        => CumFeeKrwRaw / (decimal)QMath.Scale;
        public decimal CumFeeUsd        => CumFeeUsdRaw / (decimal)QMath.Scale;
        public decimal CumStampTax      => CumStampTaxRaw / (decimal)QMath.Scale;
        public decimal CumFarmTax       => CumFarmTaxRaw / (decimal)QMath.Scale;
        public decimal AvailableDeposit => AvailableDepositRaw / (decimal)QMath.Scale;

        /// <summary>회계 스레드 기동.</summary>
        public void Start()
        {
            if (_thread is not null) return;
            _thread = new Thread(DrainLoop) { IsBackground = true, Name = "LiveAccountingDrain", Priority = ThreadPriority.BelowNormal };
            Volatile.Write(ref _stateCode, (int)AccountingState.Active);
            _thread.Start();
        }

        /// <summary>사전 보유 적재(부팅 시 DB Holdings 로부터·매도 정산의 유령 대금 fail-closed 대비). Start 이전·단일 스레드 권장.</summary>
        public void SeedPosition(long symbolCode, long qtyRaw, long costRaw)
        {
            if (qtyRaw <= 0 || costRaw < 0) return;
            lock (_bookLock)
            {
                _book.TryGetValue(symbolCode, out var p);
                _book[symbolCode] = (checked(p.qtyRaw + qtyRaw), checked(p.costRaw + costRaw));
            }
            Interlocked.Add(ref _bookValueRaw, costRaw);
        }

        /// <summary>편의 — FillPayload(브릿지 수신) → OnFill. ★키움 브릿지=국내(KRW). 해외/코인은 별도 브릿지가 kind 명시.</summary>
        public bool OnFillPayload(in FillPayload fill, AssetKind kind = AssetKind.Domestic)
        {
            if (fill.FillQty <= 0) return false;   // ★C#5: OrderRejected/미체결(FillQty≤0)은 회계 이벤트 아님 — 동결 없이 no-op(freeze-storm 방지)
            OrderSide side = fill.Side >= 0 ? OrderSide.Buy : OrderSide.Sell;
            long priceRaw = QMath.FromDecimal((decimal)fill.FillPrice);
            long qtyRaw   = QQty.Of(fill.FillQty).Raw;
            return OnFill(fill.SymbolCode, side, kind, priceRaw, qtyRaw, fill.TimestampTicks);
        }

        /// <summary>
        /// ★1단계(핫·δ=0): 매수는 보수 추정 제비용을 통화별 예수금 셀에 원자 차감(double-spend 봉쇄), 매도는 선차감 없이
        /// 값타입 링 발행 후 복귀. new/박싱/락 0. 정확 정산은 회계 스레드(2단계).
        /// </summary>
        public bool OnFill(long symbolCode, OrderSide side, AssetKind kind, long priceRaw, long qtyRaw, long timestampTicks)
        {
            if (IsFrozen) return false;
            if (Interlocked.CompareExchange(ref _producing, 1, 0) != 0)
            { Freeze("동시 체결 생산자 위반 — 단일 디스패치 전제 붕괴"); return false; }
            try
            {
                if (priceRaw < 0 || qtyRaw <= 0) { Freeze("체결 오염(음수가·비양수량)"); return false; }
                long notional;
                try { notional = QMath.Notional(priceRaw, qtyRaw, roundUp: side == OrderSide.Buy); }
                catch (OverflowException) { Freeze("명목 오버플로"); return false; }

                long estDebit = 0L;
                if (side == OrderSide.Buy)
                {
                    long estFees;
                    try { estFees = EstimateFeesCeil(notional, kind); }
                    catch (OverflowException) { Freeze("추정 제비용 오버플로"); return false; }
                    try { estDebit = checked(notional + estFees); }
                    catch (OverflowException) { Freeze("추정 차감액 오버플로"); return false; }

                    int r = IsKrw(kind) ? TryDebit(ref _availableKrwRaw, estDebit) : TryDebit(ref _availableUsdRaw, estDebit);
                    if (r == 2) { Freeze("예수금 차감 오버플로"); return false; }
                    if (r == 1) { Freeze("예수금 초과 인출 — 체결이 가용 잔고 초과(over-spend)"); return false; }
                }
                // ★매도: 예수금 선차감 없음(대금이 수수료를 항상 상회 — 풀-인베스트 정상 매도 오탐 동결 방지·C#3). 2단계에서 정산.

                _ring.Publish(new FillEvent(symbolCode, side, kind, priceRaw, qtyRaw, estDebit, timestampTicks));
                Interlocked.Increment(ref _fillsAccepted);
                return true;
            }
            finally { Volatile.Write(ref _producing, 0); }
        }

        // ── 2단계: 회계 스레드(순차 징수·확정) ─────────────────────────────────
        private void DrainLoop()
        {
            while (!_stop)
            {
                bool any = false;
                if (!IsFrozen)
                {
                    long dropBefore = _consumer.Dropped;
                    while (_consumer.TryRead(out var ev)) { any = true; Finalize(in ev); if (IsFrozen) break; }
                    if (_consumer.Dropped > dropBefore)
                    {
                        Interlocked.Add(ref _droppedFills, _consumer.Dropped - dropBefore);
                        Freeze("청산 래치 큐 추월 — 회계 미소화 체결 유실(fail-closed)");
                    }
                }
                if (!any) { try { Thread.Sleep(_spinIdleMs); } catch { } }
            }
        }

        private void Finalize(in FillEvent ev)
        {
            long notional;
            try { notional = QMath.Notional(ev.PriceRaw, ev.QtyRaw, roundUp: ev.Side == OrderSide.Buy); }
            catch (OverflowException) { Freeze("확정 명목 오버플로"); return; }

            long exactFees;
            try { exactFees = ChargeFeesWithCarry(notional, ev.Side, ev.Kind); }
            catch (OverflowException) { Freeze("확정 제비용 오버플로"); return; }

            bool krw = IsKrw(ev.Kind);
            try
            {
                if (ev.Side == OrderSide.Buy)
                {
                    long exactDebit = checked(notional + exactFees);
                    // 추정↔실제 상계(est≥exact → 초과 홀드 반납). 통화 셀 라우팅.
                    long after = krw ? Interlocked.Add(ref _availableKrwRaw, ev.EstDebitRaw - exactDebit)
                                     : Interlocked.Add(ref _availableUsdRaw, ev.EstDebitRaw - exactDebit);
                    if (after < 0) { Freeze("확정 후 예수금 음수(매수 상계)"); return; }
                    BookBuy(ev.SymbolCode, ev.QtyRaw, exactDebit);
                }
                else // Sell
                {
                    // ★보유 초과 매도 fail-closed — 유령 대금 credit 봉쇄(C#2). 사전 SeedPosition 또는 선행 매수 필요.
                    long held;
                    lock (_bookLock) { held = _book.TryGetValue(ev.SymbolCode, out var p) ? p.qtyRaw : 0L; }
                    if (ev.QtyRaw > held) { Freeze("매도 수량이 보유 초과 — 유령 예수금 봉쇄"); return; }

                    // ★대금 credit '먼저' → 수수료 차감(중간 음수 오탐 없음·C#3). 순변동 = +명목 - exact수수료.
                    long a1 = krw ? Interlocked.Add(ref _availableKrwRaw, notional) : Interlocked.Add(ref _availableUsdRaw, notional);
                    long a2 = krw ? Interlocked.Add(ref _availableKrwRaw, -exactFees) : Interlocked.Add(ref _availableUsdRaw, -exactFees);
                    if (a2 < 0) { Freeze("확정 후 예수금 음수(매도 정산)"); return; }
                    BookSell(ev.SymbolCode, ev.QtyRaw);
                }
            }
            catch (OverflowException) { Freeze("확정 상계 오버플로"); return; }

            Interlocked.Increment(ref _fillsFinalized);
        }

        // ── 정밀 잔여 캐리 가드(항목·통화별 독립·회계 스레드 전용) ──────────────
        private long ChargeFeesWithCarry(long notional, OrderSide side, AssetKind kind)
        {
            long fee;
            if (kind == AssetKind.Domestic)
            {
                fee = FloorChargeCarry(notional, _sched.KrxCommRateRaw, ref _carryComm);
                if (side == OrderSide.Sell)
                {
                    long stamp = FloorChargeCarry(notional, _sched.KrxStampRateRaw, ref _carryStamp);
                    long farm  = FloorChargeCarry(notional, _sched.KrxFarmRateRaw, ref _carryFarm);
                    fee = checked(fee + stamp + farm);
                    Interlocked.Add(ref _cumStampRaw, stamp);
                    Interlocked.Add(ref _cumFarmRaw, farm);
                }
                Interlocked.Add(ref _cumFeeKrwRaw, fee);
            }
            else if (kind == AssetKind.Foreign)
            {
                fee = checked(FloorChargeCarry(notional, _sched.UsCommRateRaw, ref _carryUsComm) + _sched.UsFixedRaw);   // 혼합: 비율+센트 고정
                Interlocked.Add(ref _cumFeeUsdRaw, fee);
            }
            else // Crypto (KRW 호가)
            {
                fee = FloorChargeCarry(notional, _sched.CryptoRateRaw, ref _carryCrypto);
                Interlocked.Add(ref _cumFeeKrwRaw, fee);
            }
            return fee;
        }

        /// <summary>floor(notional×rate/Scale) + 잔여 캐리 spill. 캐리 ≥ Scale 이면 +1 이월 → 누적오차 0 수렴. δ=0.</summary>
        private static long FloorChargeCarry(long notional, long rateRaw, ref long carry)
        {
            Int128 prod = (Int128)notional * rateRaw;
            long floor = checked((long)(prod / QMath.Scale));
            long rem   = (long)(prod - (Int128)floor * QMath.Scale);   // [0, Scale)
            carry = checked(carry + rem);
            if (carry >= QMath.Scale) { long spill = carry / QMath.Scale; floor = checked(floor + spill); carry -= spill * QMath.Scale; }
            return floor;
        }

        private long EstimateFeesCeil(long notional, AssetKind kind)
        {
            // 매수 전용 보수 상향 추정(선차감용). 매수엔 국내세 없음 → 위탁수수료만(+해외 고정).
            if (kind == AssetKind.Foreign) return checked(CeilCharge(notional, _sched.UsCommRateRaw) + _sched.UsFixedRaw);
            if (kind == AssetKind.Crypto)  return CeilCharge(notional, _sched.CryptoRateRaw);
            return CeilCharge(notional, _sched.KrxCommRateRaw);
        }

        private static long CeilCharge(long notional, long rateRaw)
        {
            Int128 prod = (Int128)notional * rateRaw;
            return checked((long)((prod + (QMath.Scale - 1)) / QMath.Scale));
        }

        // ── 원자 예수금 차감(TOCTOU-free CAS·오버플로/초과 트랩) ────────────────
        private static int TryDebit(ref long cell, long amount)   // 0 ok / 1 over-spend / 2 overflow
        {
            if (amount < 0) return 2;
            long init, next;
            do
            {
                init = Volatile.Read(ref cell);
                next = init - amount;
                if (next > init) return 2;   // amount>0 인데 결과가 커짐 = long 경계 언더플로(래핑)
                if (next < 0) return 1;      // 가용 초과 인출
            }
            while (Interlocked.CompareExchange(ref cell, next, init) != init);
            return 0;
        }

        // ── per-symbol 장부(회계 스레드 write·public 독출 → 락 보호) ────────────
        private void BookBuy(long symbol, long qtyRaw, long costRaw)
        {
            lock (_bookLock)
            {
                _book.TryGetValue(symbol, out var p);
                _book[symbol] = (checked(p.qtyRaw + qtyRaw), checked(p.costRaw + costRaw));
            }
            Interlocked.Add(ref _bookValueRaw, costRaw);
        }
        private void BookSell(long symbol, long qtyRaw)
        {
            lock (_bookLock)
            {
                if (!_book.TryGetValue(symbol, out var p) || p.qtyRaw <= 0) return;
                long sellQty = qtyRaw > p.qtyRaw ? p.qtyRaw : qtyRaw;
                long costOut = (long)((Int128)p.costRaw * sellQty / p.qtyRaw);   // 평단 유지 비례 원가
                if (p.qtyRaw - sellQty <= 0) { _book.Remove(symbol); Interlocked.Add(ref _bookValueRaw, -p.costRaw); }
                else { _book[symbol] = (p.qtyRaw - sellQty, p.costRaw - costOut); Interlocked.Add(ref _bookValueRaw, -costOut); }
            }
        }

        /// <summary>심볼 평단(raw). 보유 없으면 false. ★_book 락 보호(공유 싱글톤 동시 독출 안전·U#1/2/3).</summary>
        public bool TryGetAvgPrice(long symbol, out long avgRaw)
        {
            avgRaw = 0;
            lock (_bookLock)
            {
                if (!_book.TryGetValue(symbol, out var p) || p.qtyRaw <= 0) return false;
                avgRaw = (long)((Int128)p.costRaw * QMath.Scale / p.qtyRaw);
                return true;
            }
        }

        private void Freeze(string reason)
        {
            Volatile.Write(ref _stateCode, (int)AccountingState.Frozen);
            try { _att?.ForceFreeze("LiveAccountingMesh: " + reason); } catch { /* 동결 실패해도 상태 래치 유지 */ }
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread?.Join(2000); } catch { }
        }
    }
}
