// ============================================================================
// DynamicRiskInterlock + RiskOrderPurgeRecorder — v12.2.0 Dynamic Risk Interlock & Cryptographic Forensic Audit Trail
//
//   Section 1 — Dynamic Portfolio Drawdown & Max Daily Loss Interlock: IPortfolioBoardPort 평가액 스트림(TotalValue)
//   과 StreamingParametricVarCalculator(MS2E.Core) 의 VaR 산출을 결속해, 고점 대비 낙폭(Peak-to-Trough Drawdown)·
//   일일 최대 손실률(Max Daily Loss)·VaR 스파이크 3개 신호 중 하나라도 임계를 넘으면 ★단일 Interlocked.Or 로
//   전역 Disarm 래치를 0ms 원자 격발한다(MultiTelemetryDisarmInterlock 의 비트마스크 패턴을 리스크 도메인에 이식).
//
//   불변: 결정론(주입 equity·자체시계 0) · δ=0(Note/NoteVarBreach/CanEmit 핫패스 힙 0) · fail-closed(미설정 임계·
//        미초기화 peak/dayStart → 무동작이 아니라 첫 Note 로 즉시 베이스라인 확정) · liveArmed:false(재무장 전
//        CanEmit=false → 발주 0) · 락프리(Interlocked.Or/Exchange/Volatile).
//   ★조립: onBreach 콜백으로 ZeroAllocForensicAuditStream(MS2E.Core)에 RiskViolation 이벤트를 결속 가능(DIP —
//     본 파일은 Core 의 구체 타입에 의존하지 않고 Action<RiskBreachSource,long> 팬인만 노출).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    public enum RiskBreachSource { Drawdown = 0, DailyLoss = 1, VarSpike = 2 }

    /// <summary>Disarm 격발 시 고정되는 불변 스냅샷(포렌식).</summary>
    public readonly record struct RiskFlightSnapshot(bool Captured, RiskBreachSource Source, long ValueBps, long Sequence);

    /// <summary>Disarm 동시 미체결 잔량 원자적 일괄 취소 + 0x00 소거(AnomalyFlightRecorder 와 동형 — 리스크 도메인 전용).</summary>
    public sealed class RiskOrderPurgeRecorder
    {
        private readonly long[] _pendingNotional;
        private long _captured;
        private long _seq;
        private RiskFlightSnapshot _frozen = new(false, RiskBreachSource.Drawdown, 0, 0);

        public RiskOrderPurgeRecorder(int pendingSlots = 64)
            => _pendingNotional = new long[pendingSlots > 0 ? pendingSlots : 64];

        /// <summary>미체결 주문 명목가를 스테이징(무장 상태). Disarm 시 전량 소거된다.</summary>
        public void StagePending(int slot, long notional)
        {
            if ((uint)slot < (uint)_pendingNotional.Length) Volatile.Write(ref _pendingNotional[slot], notional);
            Interlocked.Increment(ref _seq);
        }

        /// <summary>★Disarm 동시 처리: 최초 1회 스냅샷 고정 + 미체결 잔량 버퍼 0x00 소거(일괄 취소 실증). 멱등.</summary>
        public void CaptureAndPurge(RiskBreachSource source, long valueBps)
        {
            if (Interlocked.Exchange(ref _captured, 1) == 0)
                _frozen = new RiskFlightSnapshot(true, source, valueBps, Volatile.Read(ref _seq));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_pendingNotional.AsSpan()));
        }

        public bool HasCaptured => Volatile.Read(ref _captured) == 1;
        public RiskFlightSnapshot Frozen => _frozen;

        public bool PendingBufferIsZero
        {
            get { for (int i = 0; i < _pendingNotional.Length; i++) if (Volatile.Read(ref _pendingNotional[i]) != 0) return false; return true; }
        }

        public void Reset()
        {
            Interlocked.Exchange(ref _captured, 0);
            _frozen = new(false, RiskBreachSource.Drawdown, 0, 0);
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_pendingNotional.AsSpan()));
        }
    }

    /// <summary>
    /// 낙폭/일일손실/VaR 스파이크 3원 리스크 인터록(단일 long 비트마스크·0ms 원자·경합 지연 불가). 첫 Note 호출이
    /// 고점(Peak)·일중 시작 평가액(DayStart) 베이스라인을 확정한다(NewTradingDay 로 명시 갱신 가능).
    /// </summary>
    public sealed class DynamicRiskInterlock
    {
        private const long DisarmBit = 1L << 8;

        private long _state;                     // 원자 비트마스크(bit0=Drawdown·bit1=DailyLoss·bit2=VarSpike·bit8=Disarm)
        private long _drawdownThresholdBps = -1;  // <0 = 미설정(fail-closed: 미설정 시 판정 생략)
        private long _dailyLossThresholdBps = -1;
        private long _varThresholdBps = -1;
        private long _peakEquity = long.MinValue; // 미초기화 센티널
        private long _dayStartEquity = long.MinValue;
        private long _lastDrawdownBps, _lastDailyLossBps;
        private readonly RiskOrderPurgeRecorder _recorder;
        private readonly Action<RiskBreachSource, long>? _onBreach;

        public DynamicRiskInterlock(RiskOrderPurgeRecorder recorder, Action<RiskBreachSource, long>? onBreach = null)
        {
            _recorder = recorder;
            _onBreach = onBreach;
        }

        /// <summary>낙폭/일일손실/VaR 임계(bps) 설정. 음수 = 해당 축 비활성(fail-closed: 미설정 축은 판정 생략).</summary>
        public void Configure(long drawdownThresholdBps, long dailyLossThresholdBps, long varThresholdBps = -1)
        {
            Volatile.Write(ref _drawdownThresholdBps, drawdownThresholdBps);
            Volatile.Write(ref _dailyLossThresholdBps, dailyLossThresholdBps);
            Volatile.Write(ref _varThresholdBps, varThresholdBps);
        }

        /// <summary>일중 베이스라인 갱신(명시 호출 — 내부 시계 참조 0). 신규 거래일 개시 시 호출.</summary>
        public void NewTradingDay(long dayStartEquity) => Volatile.Write(ref _dayStartEquity, dayStartEquity);

        /// <summary>
        /// 포트폴리오 평가액 1건 반영(★O(1)·δ=0). 첫 호출은 Peak/DayStart 베이스라인을 확정만 하고 판정은 생략.
        /// 이후 호출마다 Peak 갱신 + 낙폭/일일손실 bps 산출 → 임계 초과 시 단일 Interlocked.Or 로 0ms 원자 Disarm.
        /// </summary>
        public bool Note(long currentEquity)
        {
            if (Volatile.Read(ref _peakEquity) == long.MinValue)
            {
                Volatile.Write(ref _peakEquity, currentEquity);
                if (Volatile.Read(ref _dayStartEquity) == long.MinValue) Volatile.Write(ref _dayStartEquity, currentEquity);
                return IsDisarmed;
            }

            long peak = Volatile.Read(ref _peakEquity);
            if (currentEquity > peak) { Volatile.Write(ref _peakEquity, currentEquity); peak = currentEquity; }
            long dayStart = Volatile.Read(ref _dayStartEquity);

            long drawdownBps = peak > 0 ? (peak - currentEquity) * 10_000 / peak : 0;
            long dailyLossBps = dayStart > 0 ? (dayStart - currentEquity) * 10_000 / dayStart : 0;
            Volatile.Write(ref _lastDrawdownBps, drawdownBps);
            Volatile.Write(ref _lastDailyLossBps, dailyLossBps);

            long ddThr = Volatile.Read(ref _drawdownThresholdBps);
            if (ddThr >= 0 && drawdownBps > ddThr) Trip(RiskBreachSource.Drawdown, drawdownBps);

            long dlThr = Volatile.Read(ref _dailyLossThresholdBps);
            if (dlThr >= 0 && dailyLossBps > dlThr) Trip(RiskBreachSource.DailyLoss, dailyLossBps);

            return IsDisarmed;
        }

        /// <summary>VaR 스파이크 판정 반영(Idea B 결속 지점 — StreamingParametricVarCalculator.VarBps 를 주입). 저장된
        /// VaR 임계(Configure) 초과 시 동일 0ms 원자 Disarm. 임계 미설정(&lt;0) 이면 판정 생략(fail-closed).</summary>
        public bool NoteVarBreach(long varBps)
        {
            long thr = Volatile.Read(ref _varThresholdBps);
            if (thr >= 0 && varBps > thr) Trip(RiskBreachSource.VarSpike, varBps);
            return IsDisarmed;
        }

        private void Trip(RiskBreachSource source, long valueBps)
        {
            long bits = (1L << (int)source) | DisarmBit;
            Interlocked.Or(ref _state, bits);                 // ★0ms 원자 격발(경합 지연 불가)
            _recorder.CaptureAndPurge(source, valueBps);      // 미체결 잔량 일괄 취소 + 0x00 소거
            _onBreach?.Invoke(source, valueBps);               // → 감사 체인(ZeroAllocForensicAuditStream) 팬인
        }

        public bool IsDisarmed => (Volatile.Read(ref _state) & DisarmBit) != 0;
        public bool CanEmit => (Volatile.Read(ref _state) & DisarmBit) == 0;
        public bool BreachedSource(RiskBreachSource s) => (Volatile.Read(ref _state) & (1L << (int)s)) != 0;
        public long RawState => Volatile.Read(ref _state);
        public long DrawdownBps => Volatile.Read(ref _lastDrawdownBps);
        public long DailyLossBps => Volatile.Read(ref _lastDailyLossBps);
        public long PeakEquity => Volatile.Read(ref _peakEquity);

        /// <summary>물리 수동 재무장 — 원인 규명 후에만. 상태·Recorder 초기화(Peak/DayStart 베이스라인은 보존).</summary>
        public void ReArm()
        {
            Interlocked.Exchange(ref _state, 0);
            _recorder.Reset();
        }
    }
}
