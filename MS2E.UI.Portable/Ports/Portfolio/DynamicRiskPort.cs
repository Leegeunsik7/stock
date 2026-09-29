// ============================================================================
// DynamicRiskPort — v12.2.0 Dynamic Risk Interlock & Cryptographic Forensic Audit Trail (순수 UI 경계)
//
//   IPortfolioBoardPort(평가액/손익)의 소비자 계약 — 낙폭(Drawdown)·일일 최대 손실·VaR 서킷 브레이커 상태를
//   원시 값(decimal/bool/string)만으로 노출한다. ★엔진 타입 참조 0(RCL 순수성) — MS2E.Core/Infrastructure 의
//   DynamicRiskInterlock·StreamingParametricVarCalculator 는 어댑터(Infrastructure 계층)만 알고 있다.
// ============================================================================
namespace MS2E.UI.Portable.Ports.Portfolio
{
    public interface IDynamicRiskPort
    {
        /// <summary>전역 Disarm 래치 상태(sticky) — true 면 발주 사출 불가(fail-closed).</summary>
        bool IsDisarmed { get; }

        /// <summary>고점(Peak) 대비 낙폭 — 백분율(%, bps/100).</summary>
        decimal DrawdownPct { get; }

        /// <summary>일중 시작 평가액 대비 손실률 — 백분율(%).</summary>
        decimal DailyLossPct { get; }

        /// <summary>Parametric VaR 추정치 — 백분율(%, 95% 신뢰수준 기본).</summary>
        decimal VarEstimatePct { get; }

        /// <summary>Disarm 사유(포렌식 표시용) — 미격발 시 빈 문자열.</summary>
        string DisarmReason { get; }

        /// <summary>IPortfolioBoardPort 의 최신 평가액/손익률을 반영해 리스크 상태를 재계산(UI 폴링/타이머에서 호출).</summary>
        void Note();

        /// <summary>신규 거래일 개시 — 일일 손실률 베이스라인 재설정.</summary>
        void NewTradingDay();

        /// <summary>물리 수동 재무장(원인 규명 후에만).</summary>
        void ReArm();
    }
}
