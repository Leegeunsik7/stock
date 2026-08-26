// ============================================================================
// PreTradeRiskShield — v4.94.0 과제1: 발주 전 다요소 실시간 리스크 실드 (락-프리 원자 한도 예약)
//
//  50ms 발주 드레인(OrderDrainWorker.Dispatch) 직전, IBroker.Place 로 나가기 '전'에 계좌 감당력을 초고속
//  검증하는 무할당(δ=0) 방화벽. 증거금(매수여력)·당일 누적 투자한도·무차입 공매도·미수 발생 가능성을 검문한다.
//
//  ── 정직 회계(정찰 렌즈 B 확정) ──────────────────────────────────────────────
//  코드베이스에 증거금·매수여력·당일한도·미수 도메인은 전무했다(LiveRouterBroker._maxKrw 는 단건 명목 상한,
//  PaperAccount.Cash 는 모의·비원자 JSON). 따라서 이 실드가 '예약 원장'을 스스로 소유한다(순수 인메모리 long 셀).
//  재사용: 명목 산출은 QMath.Notional(roundUp:true, 보수=과소계상 금지)·동결은 OrderAttestationService.ForceFreeze
//  단일 소스·강등은 IgnitionLock.Downgrade. 자본 시드(예산)는 호출부가 주입(라이브 예수금 소스는 미보존 = 별도 선결).
//
//  ── ★Think Further: TOCTOU 함정 제거 (Check→Act 레이스) ──────────────────────
//  고빈도 멀티스레드에서 여러 알파가 동시에 자산을 분출할 때, 잔여 예산을 '읽고(Check)→차감(Act)'하는 순진한
//  구현은 두 시점 사이 레이스로 순간 한도 초과 오발주를 낸다. 본 실드는 lock/Semaphore(핫패스 레이턴시 붕괴)를
//  쓰지 않고 'reserve→check→compensate' CAS 재시도 루프로 이를 원천 봉쇄한다:
//    do { init = Volatile.Read(cell); next = init + notional;
//         if (next < init) return 오버플로;              // ★notional>0 인데 합이 감소 = long 경계 초과(래핑) 포착
//         if (next > budget) return 초과; }               // ★스냅샷 init 기준 검증 — TOCTOU 무관(원자 CAS 로 커밋)
//    while (CompareExchange(cell, next, init) != init);   // 경합 시 최신 init 으로 재시도(동시 예약/해제 안전)
//  성공 경로는 절대 불일치 상태를 남기지 않는다(보상 창 없음). 실패는 상태 무변경(반환만). 두 예산(증거금·당일)
//  합성 예약은 증거금 커밋 후 당일 실패 시 증거금만 원자 롤백(우리 소유분 → 음수 불가). 롤백 지연은 '보수적
//  과대거부'(안전 방향)만 유발하고 과대허용은 수학적으로 불가능하다.
//
//  ── 분리된 방어 2단(경량 필터 ↔ 에스컬레이션 펜스) ──────────────────────────
//  ① 경량 필터: 단건 한도 초과는 '그 주문만' 드롭(계좌는 계속 가동). ② 에스컬레이션 펜스: 한도 '연속 N회'(기본 3)
//  위반 시 계좌 전체를 ForceFreeze + IgnitionLock.Downgrade(별도 소스). 성공 admit 은 연속 카운트를 리셋한다.
//  오버플로·명목 파손은 즉시(연속 대기 없이) 에스컬레이션 — 부패 수준 신호.
//
//  ── liveArmed:false 불변 ─────────────────────────────────────────────────────
//  실드는 오직 '허용/거부'만 판정한다 — 1바이트도 발주하지 않는다. 거부는 주문 흐름을 '줄일' 뿐 절대 늘리지 않는다.
//  실드가 admit 해도 실 송출은 하류 이중키(ValveWired∧LiveArmingWired)∧MasterIgnited∧DispatchArming.AlwaysFalse
//  게이트가 지배 → 미무장이면 여전히 수학적 0. 실드는 그 게이트 '앞'의 추가 방벽일 뿐 우회로가 아니다.
//
//  ── δ=0 ─────────────────────────────────────────────────────────────────────
//  admit/release 핫패스는 값 타입 RiskDecision(참조 0)·long CAS·Int128 중간연산만 — 힙 할당 0. 문자열 사유는
//  에스컬레이션(동결) 경로에서만 조건부 생성(QLedgerGuard 관례 계승 — 동결이라 핫패스 아님).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>실드 판정 결과 코드. Admit 외 전부 거부(발주 차단).</summary>
    public enum RiskVerdict
    {
        Admit = 0,          // 통과 — 예약 성립(매수) 또는 위험 없음(매도)
        RejectMargin,       // 증거금(매수여력) 초과 — 미수 방지
        RejectDailyLimit,   // 당일 누적 투자한도 초과
        RejectNakedShort,   // 무차입 공매도(보유 초과 매도)
        RejectMarketNoPrice,// 시장가 기준가 부재 — 위험 산정 불가(fail-closed)
        RejectPoison,       // 오염 입력(수량<=0·음수가) — 상류 버그
        Frozen              // 이미 동결(에스컬레이션·비상) — 전량 거부
    }

    /// <summary>실드 판정 — 값 타입(δ=0). 매수 admit 시 ReservedNotionalRaw>0(체결·취소 시 Release 로 반납).</summary>
    public readonly struct RiskDecision
    {
        public readonly RiskVerdict Verdict;
        public readonly long        ReservedNotionalRaw;   // 이 주문이 예약한 증거금 명목(raw·10^8). 매도/거부는 0.
        public RiskDecision(RiskVerdict verdict, long reservedNotionalRaw)
        { Verdict = verdict; ReservedNotionalRaw = reservedNotionalRaw; }
        public bool Admitted => Verdict == RiskVerdict.Admit;
    }

    /// <summary>
    /// 발주 전 리스크 실드 — 락-프리 원자 한도 예약. 매수는 증거금(revolving)·당일누적(monotone) 이중 예산을
    /// TOCTOU-free CAS 로 예약, 매도는 무차입 공매도만 검문(예산 무소비). 연속 위반 시 계좌 동결.
    /// </summary>
    public sealed class PreTradeRiskShield
    {
        // long 경계 여유(4×) — 예산은 이 상한 이하로 강제해 정상 예약이 절대 오버플로에 근접하지 않게 한다.
        public const long MaxSafeBudgetRaw = long.MaxValue / 4;

        private readonly OrderAttestationService? _att;
        private readonly IgnitionLock?            _ignition;
        private readonly int                      _escalationStrikes;
        private readonly Func<long, long>?        _marketRefPriceRaw;   // 심볼→시장가 기준가 raw(시장가 주문 위험 산정)
        private readonly Func<long, long>?        _heldQtyRaw;          // 심볼→보유수량 raw(무차입 공매도 검문)

        private readonly long _marginBudgetRaw;   // 증거금(매수여력) 총 예산 — 불변
        private readonly long _dailyLimitRaw;     // 당일 누적 투자한도 — 불변(RollTradingDay 로 카운터만 리셋)

        private long _marginReservedRaw;    // 현재 증거금 예약(revolving — 체결/취소 시 Release 로 감소)
        private long _dailyReservedRaw;     // 당일 누적 예약(monotone — RollTradingDay 전까지 감소 안 함)
        private long _admitCount;
        private long _rejectCount;
        private long _consecutiveStrikes;   // 연속 한도 위반(성공 admit 시 0 리셋). >=임계 → 에스컬레이션.
        private int  _escalated;            // 1 = 에스컬레이션(계좌 동결) 래치

        /// <param name="marginBudgetRaw">증거금(매수여력) 예산 raw(10^8). (0, MaxSafeBudgetRaw] 범위 필수.</param>
        /// <param name="dailyLimitRaw">당일 누적 투자한도 raw(10^8). (0, MaxSafeBudgetRaw] 범위 필수.</param>
        /// <param name="marketRefPriceRaw">시장가 주문 기준가 프로브(심볼코드→raw). null 이면 시장가 매수는 fail-closed 거부.</param>
        /// <param name="heldQtyRaw">보유수량 프로브(심볼코드→raw). null 이면 매도 공매도 검문 생략(보유 확인 불가 → 통과).</param>
        /// <param name="blackbox">선택적 — v4.95.0 블랙박스 기록계(null=미배선·완전 no-op). Record 는 δ=0.</param>
        public PreTradeRiskShield(long marginBudgetRaw, long dailyLimitRaw,
                                  OrderAttestationService? attestation = null,
                                  IgnitionLock? ignition = null,
                                  int escalationStrikes = 3,
                                  Func<long, long>? marketRefPriceRaw = null,
                                  Func<long, long>? heldQtyRaw = null,
                                  MmfFlightRecorder? blackbox = null)
        {
            if (marginBudgetRaw <= 0 || marginBudgetRaw > MaxSafeBudgetRaw)
                throw new ArgumentOutOfRangeException(nameof(marginBudgetRaw), "증거금 예산은 (0, long.Max/4] 범위여야 함(오버플로 안전)");
            if (dailyLimitRaw <= 0 || dailyLimitRaw > MaxSafeBudgetRaw)
                throw new ArgumentOutOfRangeException(nameof(dailyLimitRaw), "당일 한도는 (0, long.Max/4] 범위여야 함(오버플로 안전)");
            if (escalationStrikes < 1)
                throw new ArgumentOutOfRangeException(nameof(escalationStrikes), "에스컬레이션 임계는 1 이상");
            _marginBudgetRaw   = marginBudgetRaw;
            _dailyLimitRaw     = dailyLimitRaw;
            _att               = attestation;
            _ignition          = ignition;
            _escalationStrikes = escalationStrikes;
            _marketRefPriceRaw = marketRefPriceRaw;
            _heldQtyRaw        = heldQtyRaw;
            _blackbox          = blackbox;
        }

        private readonly MmfFlightRecorder? _blackbox;   // v4.95.0 — 블랙박스(δ=0·null=no-op)

        // ── 텔레메트리(락-프리 독출·무할당) ────────────────────────────────────
        public long MarginBudgetRaw   => _marginBudgetRaw;
        public long DailyLimitRaw     => _dailyLimitRaw;
        public long MarginReservedRaw => Interlocked.Read(ref _marginReservedRaw);
        public long DailyReservedRaw  => Interlocked.Read(ref _dailyReservedRaw);
        public long AdmitCount        => Interlocked.Read(ref _admitCount);
        public long RejectCount       => Interlocked.Read(ref _rejectCount);
        public long ConsecutiveStrikes=> Interlocked.Read(ref _consecutiveStrikes);
        public bool Escalated         => Volatile.Read(ref _escalated) != 0;
        /// <summary>증거금 포화도 raw(예약/예산 × 10^8, 0..10^8). UI 게이지·텔레메트리용.</summary>
        public long MarginUtilizationRaw
        {
            get
            {
                long reserved = Interlocked.Read(ref _marginReservedRaw);
                if (_marginBudgetRaw <= 0) return 0L;
                if (reserved <= 0) return 0L;
                if (reserved >= _marginBudgetRaw) return QMath.Scale;
                return (long)((Int128)reserved * QMath.Scale / _marginBudgetRaw);
            }
        }

        /// <summary>
        /// 발주 전 검문. 매수는 이중 예산 원자 예약 후 admit, 매도는 공매도 검문. 통과 시 true + decision.Verdict==Admit.
        /// ★admit 이 곧 '발주'는 아니다 — 하류 이중키∧MasterIgnited 게이트가 실 송출을 지배(liveArmed:false).
        /// </summary>
        public bool TryAdmit(in OrderProposal p, out RiskDecision decision)
        {
            // 0) 이미 동결/에스컬레이션 → 전량 거부(fail-closed 단락).
            if (Volatile.Read(ref _escalated) != 0 || (_att is { } a && a.Mode == GatingMode.EmergencyFreeze))
            { decision = new RiskDecision(RiskVerdict.Frozen, 0L); return false; }

            // 1) 오염 입력 방어 — 수량 비양수·음수 지정가. 상류 버그이므로 드롭(스트라이크 없음: 계좌 잘못 아님).
            if (p.QtyRaw <= 0 || p.PriceRaw < 0)
            { decision = SoftReject(RiskVerdict.RejectPoison); return false; }

            // 2) 위험 산정용 가격 확정. 시장가(Kind 0)는 기준가 오라클 필수(부재 시 fail-closed 거부).
            long priceRaw;
            if (p.KindByte == 0)   // Market
            {
                if (_marketRefPriceRaw is null) { decision = SoftReject(RiskVerdict.RejectMarketNoPrice); return false; }
                long refRaw;
                try { refRaw = _marketRefPriceRaw(p.SymbolCode); } catch { refRaw = 0L; }
                if (refRaw <= 0L) { decision = SoftReject(RiskVerdict.RejectMarketNoPrice); return false; }
                priceRaw = refRaw;
            }
            else   // Limit
            {
                if (p.PriceRaw <= 0L) { decision = SoftReject(RiskVerdict.RejectPoison); return false; }
                priceRaw = p.PriceRaw;
            }

            // 3) 보수 명목(매수 비용 과소계상 금지 = roundUp). 오버플로는 즉시 에스컬레이션(부패 신호).
            long notional;
            try { notional = QMath.Notional(priceRaw, p.QtyRaw, roundUp: true); }
            catch (OverflowException) { decision = HardFreeze("명목 오버플로 — 예약 불가"); return false; }
            if (notional <= 0L) { decision = SoftReject(RiskVerdict.RejectPoison); return false; }

            // 4) 매도 — 예산 무소비. 무차입 공매도(보유 초과)만 검문(오라클 있을 때).
            if (p.Side == OrderSide.Sell)
            {
                if (_heldQtyRaw is { } heldProbe)
                {
                    long heldRaw;
                    try { heldRaw = heldProbe(p.SymbolCode); } catch { heldRaw = 0L; }
                    if (p.QtyRaw > heldRaw) { decision = RegisterBreach(RiskVerdict.RejectNakedShort); return false; }
                }
                ClearStrikes();
                Interlocked.Increment(ref _admitCount);
                _blackbox?.Record(FlightEventKind.RiskAdmit, p.SymbolCode, 0L, p.QtyRaw, (long)OrderSide.Sell, 2);
                decision = new RiskDecision(RiskVerdict.Admit, 0L);   // 매도는 증거금 예약 없음
                return true;
            }

            // 5) 매수 — 단건이 전체 예산을 넘으면 즉시 증거금 위반(오버플로 근접 방지 + 명백한 초과).
            if (notional > _marginBudgetRaw) { decision = RegisterBreach(RiskVerdict.RejectMargin); return false; }

            // 6) ★TOCTOU-free 원자 예약: 증거금 → 당일. 당일 실패 시 증거금만 원자 롤백.
            int m = TryReserveInto(ref _marginReservedRaw, _marginBudgetRaw, notional);
            if (m == 2) { decision = HardFreeze("증거금 예약 오버플로"); return false; }
            if (m == 1) { decision = RegisterBreach(RiskVerdict.RejectMargin); return false; }

            int d = TryReserveInto(ref _dailyReservedRaw, _dailyLimitRaw, notional);
            if (d != 0)
            {
                ReleaseFrom(ref _marginReservedRaw, notional);   // 우리 소유 예약분만 롤백(음수 불가·경합 안전)
                if (d == 2) { decision = HardFreeze("당일 누적 예약 오버플로"); return false; }
                decision = RegisterBreach(RiskVerdict.RejectDailyLimit);
                return false;
            }

            ClearStrikes();
            Interlocked.Increment(ref _admitCount);
            _blackbox?.Record(FlightEventKind.RiskAdmit, p.SymbolCode, notional, p.QtyRaw, (long)OrderSide.Buy, 2);
            decision = new RiskDecision(RiskVerdict.Admit, notional);
            return true;
        }

        /// <summary>체결·취소 시 증거금 예약 반납(revolving). 당일 누적은 감소하지 않음. 음수 불가(0 클램프).</summary>
        public void Release(long reservedNotionalRaw) => ReleaseFrom(ref _marginReservedRaw, reservedNotionalRaw);

        /// <summary>거래일 경계 — 당일 누적 예약 리셋(호출부가 장 개시/일자 전환 시 호출). 증거금 예약은 불변.</summary>
        public void RollTradingDay() => Interlocked.Exchange(ref _dailyReservedRaw, 0L);

        // ── 내부: 원자 예약/반납(CAS 재시도) ───────────────────────────────────

        /// <summary>0=커밋 성공, 1=예산 초과(상태 무변경), 2=오버플로(상태 무변경). notional>0 전제.</summary>
        private static int TryReserveInto(ref long cell, long budget, long notional)
        {
            long init, next;
            do
            {
                init = Volatile.Read(ref cell);
                next = init + notional;
                if (next < init) return 2;        // ★init>=0·notional>0 인데 합 감소 = long 경계 초과(래핑)
                if (next > budget) return 1;       // 스냅샷 기준 초과(TOCTOU 무관)
            }
            while (Interlocked.CompareExchange(ref cell, next, init) != init);   // 경합 → 최신 init 재시도
            return 0;
        }

        /// <summary>예약 반납 — 0 클램프(과다 반납·음수 부패 차단). CAS 재시도.</summary>
        private static void ReleaseFrom(ref long cell, long notional)
        {
            if (notional <= 0L) return;
            long init, next;
            do
            {
                init = Volatile.Read(ref cell);
                next = init - notional;
                if (next < 0L) next = 0L;
            }
            while (Interlocked.CompareExchange(ref cell, next, init) != init);
        }

        // ── 내부: 방어 2단(경량 필터 ↔ 에스컬레이션 펜스) ──────────────────────

        /// <summary>한도 위반 — 그 주문만 드롭 + 연속 스트라이크 누적. 임계 도달 시 에스컬레이션(동결).</summary>
        private RiskDecision RegisterBreach(RiskVerdict v)
        {
            Interlocked.Increment(ref _rejectCount);
            long strikes = Interlocked.Increment(ref _consecutiveStrikes);
            if (strikes >= _escalationStrikes)
                Escalate("연속 한도 위반 " + strikes.ToString() + "회 — 계좌 잠금");
            return new RiskDecision(v, 0L);
        }

        /// <summary>구조적 거부(오염·시장가 기준가 부재) — 드롭·계상만. 계좌 잘못이 아니므로 스트라이크 없음.</summary>
        private RiskDecision SoftReject(RiskVerdict v)
        {
            Interlocked.Increment(ref _rejectCount);
            return new RiskDecision(v, 0L);
        }

        /// <summary>부패 수준(오버플로·명목 파손) — 연속 대기 없이 즉시 동결.</summary>
        private RiskDecision HardFreeze(string reason)
        {
            Interlocked.Increment(ref _rejectCount);
            Escalate(reason);
            return new RiskDecision(RiskVerdict.Frozen, 0L);
        }

        private void ClearStrikes() => Interlocked.Exchange(ref _consecutiveStrikes, 0L);

        private void Escalate(string reason)
        {
            Volatile.Write(ref _escalated, 1);
            _blackbox?.Record(FlightEventKind.RiskEscalate, 0L, Interlocked.Read(ref _marginReservedRaw), Interlocked.Read(ref _consecutiveStrikes), 0L, 2);
            try { _att?.ForceFreeze("PreTradeRiskShield: " + reason); } catch { /* 동결 실패해도 거부 유지 */ }
            try { _ignition?.Downgrade("PreTradeRiskShield: " + reason); } catch { }
        }
    }
}
