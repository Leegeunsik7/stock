// ============================================================================
// RiskCircuitAuditTests — v12.11.0 [Step 5/7] 리스크 컨트롤 & 서킷 브레이커 전수 오디팅 적대검증
//   Section 1/2: RiskInterlockStatusAdapter — MultiTelemetryDisarmInterlock(DI엔 있었으나 UI 소비 0건)과
//     DynamicRiskInterlock(DI 등록 자체가 없었음)을 처음으로 UI 에 결속. ProxyLivenessProbe(v12.0.4) 재사용.
//   Think Beyond A: RiskThresholdMatrixEvaluator(δ=0 4축 비트마스크 판정).
//   ★RiskInterlockStatusWidget.razor 의 실제 렌더링은 bUnit 부재로 xUnit 직접 검증 불가 — 사중 빌드 0W/0E 로
//   구조 정합만 확인(Step 1~4 와 동일 한계, 정직 명기).
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    public sealed class RiskCircuitAuditTests
    {
        private readonly ITestOutputHelper _out;
        public RiskCircuitAuditTests(ITestOutputHelper o) => _out = o;

        // ══ RiskThresholdMatrixEvaluator(Think Beyond A) ═══════════════════════
        [Fact]
        public void Evaluate_AllWithinThreshold_NoBreach()
        {
            var v = RiskThresholdMatrixEvaluator.Evaluate(100, 500, 100, 500, 100, 500, 5, 50);
            Assert.False(v.IsAnyBreached);
        }

        [Fact]
        public void Evaluate_EachAxis_DetectedIndependently()
        {
            Assert.True(RiskThresholdMatrixEvaluator.Evaluate(600, 500, 0, 500, 0, 500, 0, 50).DailyLossBreached);
            Assert.True(RiskThresholdMatrixEvaluator.Evaluate(0, 500, 600, 500, 0, 500, 0, 50).DrawdownBreached);
            Assert.True(RiskThresholdMatrixEvaluator.Evaluate(0, 500, 0, 500, 600, 500, 0, 50).VaRBreached);
            Assert.True(RiskThresholdMatrixEvaluator.Evaluate(0, 500, 0, 500, 0, 500, 60, 50).SlippageBreached);
        }

        [Fact]
        public void Evaluate_MultipleAxesSimultaneously_AllFlagged()
        {
            var v = RiskThresholdMatrixEvaluator.Evaluate(600, 500, 600, 500, 100, 500, 5, 50);
            Assert.True(v.DailyLossBreached);
            Assert.True(v.DrawdownBreached);
            Assert.False(v.VaRBreached);
            Assert.False(v.SlippageBreached);
            Assert.True(v.IsAnyBreached);
        }

        [Fact]
        public void Evaluate_DisabledThreshold_SkipsJudgement()
        {
            // 임계 0 = 비활성(fail-closed 관례와 동일 — 미설정 축은 판정 생략).
            var v = RiskThresholdMatrixEvaluator.Evaluate(999_999, 0, 0, 500, 0, 500, 0, 50);
            Assert.False(v.DailyLossBreached);
            Assert.False(v.IsAnyBreached);
        }

        [Fact]
        public void EvaluateBatch_ComputesPerElement()
        {
            long[] dl = { 100, 600 }; long[] dd = { 100, 100 }; long[] var = { 100, 100 }; long[] sl = { 5, 5 };
            var results = new RiskMatrixVerdict[2];
            RiskThresholdMatrixEvaluator.EvaluateBatch(dl, 500, dd, 500, var, 500, sl, 50, results);
            Assert.False(results[0].IsAnyBreached);
            Assert.True(results[1].DailyLossBreached);
        }

        [Fact]
        public void EvaluateBatch_MismatchedLength_Throws()
        {
            long[] a = { 1, 2 }; long[] b = { 1 };
            var results = new RiskMatrixVerdict[2];
            Assert.Throws<ArgumentException>(() => RiskThresholdMatrixEvaluator.EvaluateBatch(a, 1, b, 1, a, 1, a, 1, results));
        }

        // ══ RiskInterlockStatusAdapter — 최초 UI 결속 실증 ══════════════════════
        private static (RiskInterlockStatusAdapter adapter, MultiTelemetryDisarmInterlock tel, DynamicRiskInterlock risk, ProxyLivenessProbe pulse) NewAdapter()
        {
            var tel = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder());
            tel.Configure(latencyMicros: 50_000, http429Count: 5, slippageBps: 20);
            var risk = new DynamicRiskInterlock(new RiskOrderPurgeRecorder());
            risk.Configure(drawdownThresholdBps: 500, dailyLossThresholdBps: 300);
            var pulse = new ProxyLivenessProbe(timeoutTicks: 1000);
            var adapter = new RiskInterlockStatusAdapter(tel, risk, pulse, nowTicks: () => 0L);
            return (adapter, tel, risk, pulse);
        }

        [Fact]
        public void Adapter_InitialState_AllNormal()
        {
            var (adapter, _, _, _) = NewAdapter();
            Assert.False(adapter.AnyDisarmed);
            Assert.False(adapter.TelemetryDisarmed);
            Assert.False(adapter.RiskDisarmed);
            Assert.Equal(string.Empty, adapter.DisarmSummary);
        }

        [Fact]
        public void Adapter_TelemetryBreach_ReflectsInPort()
        {
            var (adapter, tel, _, _) = NewAdapter();
            tel.Note(TelemetrySource.Latency, 60_000);   // > 50,000 임계
            Assert.True(adapter.TelemetryDisarmed);
            Assert.True(adapter.LatencyBreached);
            Assert.True(adapter.AnyDisarmed);
            Assert.Contains("지연", adapter.DisarmSummary);
        }

        [Fact]
        public void Adapter_RiskBreach_ReflectsInPort()
        {
            var (adapter, _, risk, _) = NewAdapter();
            risk.Note(1_000_000);   // 베이스라인
            risk.Note(940_000);     // 6% 낙폭 > 5% 임계
            Assert.True(adapter.RiskDisarmed);
            Assert.True(adapter.DrawdownBreached);
            Assert.True(adapter.DrawdownPct >= 5m);
            Assert.Contains("낙폭", adapter.DisarmSummary);
        }

        [Fact]
        public void Adapter_HardwarePulse_NeverBeaten_ConservativelyAlive()
        {
            var (adapter, _, _, _) = NewAdapter();
            // 하트비트 0건 — 보수적으로 생존 판정(오탐 방지, ProxyLivenessProbe 자체 계약).
            Assert.False(adapter.HardwarePulseSilent);
        }

        [Fact]
        public void Adapter_HardwarePulse_TimeoutExceeded_DetectedSilent()
        {
            var tel = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder());
            var risk = new DynamicRiskInterlock(new RiskOrderPurgeRecorder());
            var pulse = new ProxyLivenessProbe(timeoutTicks: 1000);
            pulse.Beat(nowTicks: 0);
            long now = 5000;   // 5000-0=5000 > 1000 임계
            var adapter = new RiskInterlockStatusAdapter(tel, risk, pulse, nowTicks: () => now);

            Assert.True(adapter.HardwarePulseSilent);
            Assert.True(adapter.AnyDisarmed);
            Assert.Contains("하드웨어 펄스", adapter.DisarmSummary);
        }

        [Fact]
        public void Adapter_NullPulseProbe_NeverSilent()
        {
            var tel = new MultiTelemetryDisarmInterlock(new AnomalyFlightRecorder());
            var risk = new DynamicRiskInterlock(new RiskOrderPurgeRecorder());
            var adapter = new RiskInterlockStatusAdapter(tel, risk);   // pulseProbe 미주입
            Assert.False(adapter.HardwarePulseSilent);
        }

        [Fact]
        public void Adapter_MultipleBreaches_SummaryJoinsAll()
        {
            var (adapter, tel, risk, _) = NewAdapter();
            tel.Note(TelemetrySource.Slippage, 30);   // > 20bps 임계
            risk.Note(1_000_000);
            risk.Note(650_000);   // 35% 낙폭
            Assert.True(adapter.AnyDisarmed);
            Assert.Contains("슬리피지", adapter.DisarmSummary);
            Assert.Contains("낙폭", adapter.DisarmSummary);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §적대검증 — δ=0 핫패스
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class RiskCircuitAuditConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public RiskCircuitAuditConcurrencyTests(ITestOutputHelper o) => _out = o;

        [Fact]
        public void Evaluate_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 50_000; i++) RiskThresholdMatrixEvaluator.Evaluate(100, 500, 100, 500, 100, 500, 5, 50);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20_000; i++) RiskThresholdMatrixEvaluator.Evaluate(100, 500, 100, 500, 100, 500, 5, 50);
                    alloc = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }
    }
}
