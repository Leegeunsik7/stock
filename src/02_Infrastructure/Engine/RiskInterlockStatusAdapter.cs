// ============================================================================
// RiskInterlockStatusAdapter — v12.11.0 IRiskInterlockStatusPort 구현(Ports/Adapters 결속)
//
//   MultiTelemetryDisarmInterlock + DynamicRiskInterlock + ProxyLivenessProbe(하드웨어 펄스 생존성, Think Beyond
//   B — 기존 v12.0.4 엔진 재사용) 세 엔진의 라이브 상태를 단일 Port 로 합성해 처음으로 UI 에 노출한다.
//   ★ProxyLivenessProbe.IsDead 는 '현재 시각' 이 필요하므로 nowTicks 델리게이트를 주입받는다(자체시계 금지 관례).
// ============================================================================
using System;

namespace MS2E.Infrastructure
{
    public sealed class RiskInterlockStatusAdapter : MS2E.UI.Portable.Ports.Shell.IRiskInterlockStatusPort
    {
        private readonly MultiTelemetryDisarmInterlock _telemetry;
        private readonly DynamicRiskInterlock _risk;
        private readonly MS2E.Core.ProxyLivenessProbe? _pulseProbe;
        private readonly Func<long> _nowTicks;

        public RiskInterlockStatusAdapter(
            MultiTelemetryDisarmInterlock telemetry,
            DynamicRiskInterlock risk,
            MS2E.Core.ProxyLivenessProbe? pulseProbe = null,
            Func<long>? nowTicks = null)
        {
            _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
            _risk = risk ?? throw new ArgumentNullException(nameof(risk));
            _pulseProbe = pulseProbe;
            _nowTicks = nowTicks ?? (() => System.Diagnostics.Stopwatch.GetTimestamp());
        }

        public bool TelemetryDisarmed => _telemetry.IsDisarmed;
        public bool LatencyBreached => _telemetry.BreachedSource(TelemetrySource.Latency);
        public bool Http429Breached => _telemetry.BreachedSource(TelemetrySource.Http429);
        public bool SlippageBreached => _telemetry.BreachedSource(TelemetrySource.Slippage);

        public bool RiskDisarmed => _risk.IsDisarmed;
        public decimal DrawdownPct => _risk.DrawdownBps / 100m;
        public decimal DailyLossPct => _risk.DailyLossBps / 100m;
        public bool DrawdownBreached => _risk.BreachedSource(RiskBreachSource.Drawdown);
        public bool DailyLossBreached => _risk.BreachedSource(RiskBreachSource.DailyLoss);
        public bool VarSpikeBreached => _risk.BreachedSource(RiskBreachSource.VarSpike);

        public bool HardwarePulseSilent => _pulseProbe?.IsDead(_nowTicks()) ?? false;

        public bool AnyDisarmed => TelemetryDisarmed || RiskDisarmed || HardwarePulseSilent;

        public string DisarmSummary
        {
            get
            {
                var reasons = new System.Collections.Generic.List<string>();
                if (LatencyBreached) reasons.Add("지연");
                if (Http429Breached) reasons.Add("HTTP 429");
                if (SlippageBreached) reasons.Add("슬리피지");
                if (DrawdownBreached) reasons.Add("낙폭");
                if (DailyLossBreached) reasons.Add("일일손실");
                if (VarSpikeBreached) reasons.Add("VaR");
                if (HardwarePulseSilent) reasons.Add("하드웨어 펄스 유실");
                return reasons.Count == 0 ? string.Empty : string.Join(" · ", reasons);
            }
        }
    }
}
