// ============================================================================
// RiskInterlockStatusPort — v12.11.0 [Step 5/7] Risk Control & Circuit Breaker Dashboard (순수 UI 경계)
//
//   MultiTelemetryDisarmInterlock(v11.4.4·이미 DI 등록되어 있었으나 UI 소비 0건이었음 — 실측)와
//   DynamicRiskInterlock(v12.2.0·이번 감사 전까지 DI 등록 자체가 없었음 — 실측)의 라이브 상태를 처음으로 UI 에
//   노출한다. 원시 값(bool/decimal/string)만 다뤄 엔진 타입 참조 0(RCL 순수성) 유지.
// ============================================================================
namespace MS2E.UI.Portable.Ports.Shell
{
    public interface IRiskInterlockStatusPort
    {
        // ── MultiTelemetryDisarmInterlock(텔레메트리: 지연/429/슬리피지) ──
        bool TelemetryDisarmed { get; }
        bool LatencyBreached { get; }
        bool Http429Breached { get; }
        bool SlippageBreached { get; }

        // ── DynamicRiskInterlock(낙폭/일일손실/VaR) ──
        bool RiskDisarmed { get; }
        decimal DrawdownPct { get; }
        decimal DailyLossPct { get; }
        bool DrawdownBreached { get; }
        bool DailyLossBreached { get; }
        bool VarSpikeBreached { get; }

        // ── 하드웨어 펄스 생존성(ProxyLivenessProbe 재사용 — Think Beyond B) ──
        bool HardwarePulseSilent { get; }

        bool AnyDisarmed { get; }
        /// <summary>격발 사유 요약(미격발 시 빈 문자열).</summary>
        string DisarmSummary { get; }
    }
}
