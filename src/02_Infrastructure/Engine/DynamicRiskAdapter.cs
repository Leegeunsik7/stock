// ============================================================================
// DynamicRiskAdapter — v12.2.0 IDynamicRiskPort 구현(Ports/Adapters 결속)
//
//   MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort(평가액·손익률) → DynamicRiskInterlock(낙폭/일일손실/VaR
//   3원 서킷 브레이커) + StreamingParametricVarCalculator(Parametric VaR) → ZeroAllocForensicAuditStream(SHA-256
//   해시 체인 감사) 를 하나의 어댑터로 결선한다. ★RCL(Portable)은 이 파일의 구체 타입을 전혀 모른다 — IDynamicRiskPort
//   인터페이스만 소비(0 Concrete Ref 유지).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    public sealed class DynamicRiskAdapter : MS2E.UI.Portable.Ports.Portfolio.IDynamicRiskPort
    {
        private const long ZScoreMilli95 = 1645;   // z=1.645(95% 단측 신뢰수준) × 1000 정수 스케일

        private readonly MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort _board;
        private readonly DynamicRiskInterlock _interlock;
        private readonly MS2E.Core.StreamingParametricVarCalculator _var;
        private readonly MS2E.Core.ZeroAllocForensicAuditStream? _audit;
        private readonly Func<long> _nowTicks;

        public DynamicRiskAdapter(
            MS2E.UI.Portable.Ports.Portfolio.IPortfolioBoardPort board,
            DynamicRiskInterlock interlock,
            MS2E.Core.StreamingParametricVarCalculator varCalculator,
            MS2E.Core.ZeroAllocForensicAuditStream? auditStream = null,
            Func<long>? nowTicks = null)
        {
            _board = board;
            _interlock = interlock;
            _var = varCalculator;
            _audit = auditStream;
            _nowTicks = nowTicks ?? (() => DateTime.UtcNow.Ticks);
        }

        public bool IsDisarmed => _interlock.IsDisarmed;
        public decimal DrawdownPct => _interlock.DrawdownBps / 100m;
        public decimal DailyLossPct => _interlock.DailyLossBps / 100m;
        public decimal VarEstimatePct => _var.VarBps(ZScoreMilli95) / 100m;

        public string DisarmReason
        {
            get
            {
                if (!_interlock.IsDisarmed) return string.Empty;
                if (_interlock.BreachedSource(RiskBreachSource.Drawdown)) return "Drawdown";
                if (_interlock.BreachedSource(RiskBreachSource.DailyLoss)) return "DailyLoss";
                if (_interlock.BreachedSource(RiskBreachSource.VarSpike)) return "VarSpike";
                return "Unknown";
            }
        }

        /// <summary>IPortfolioBoardPort 최신 평가액/손익률을 리스크 파이프라인에 반영. Disarm 격발 시 감사 체인에 기록.</summary>
        public void Note()
        {
            bool wasDisarmed = _interlock.IsDisarmed;

            long equity = decimal.ToInt64(Math.Round(_board.TotalValue, MidpointRounding.AwayFromZero));
            _interlock.Note(equity);

            long retBps = decimal.ToInt64(Math.Round(_board.TodayPct * 100m, MidpointRounding.AwayFromZero));
            _var.Push(retBps);
            _interlock.NoteVarBreach(_var.VarBps(ZScoreMilli95));

            if (!wasDisarmed && _interlock.IsDisarmed)
                _audit?.Append(MS2E.Core.AuditEventKind.RiskViolation, _interlock.RawState, _nowTicks());
        }

        public void NewTradingDay()
        {
            long equity = decimal.ToInt64(Math.Round(_board.TotalValue, MidpointRounding.AwayFromZero));
            _interlock.NewTradingDay(equity);
        }

        public void ReArm()
        {
            _interlock.ReArm();
            _audit?.Append(MS2E.Core.AuditEventKind.Arm, 0, _nowTicks());
        }
    }
}
