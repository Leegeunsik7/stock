// ============================================================================
// DynamicRiskInterlockTests — v12.2.0 Dynamic Risk Interlock & Cryptographic Forensic Audit Trail 적대검증
//   Section 1: IPortfolioBoardPort 평가액 스트림 → DynamicRiskInterlock(낙폭/일일손실/VaR 3원 서킷 브레이커)
//     결속 → 임계 초과 시 0ms Disarm(sticky) + 미체결 잔량 일괄 취소(0x00 소거) + Disarm 사유 원자적 기록.
//   Think Beyond: StreamingParametricVarCalculator(러닝섬/러닝제곱합 O(1) δ=0 Parametric VaR).
//   ★liveArmed:false: 리스크 판정은 무장 인가가 아님 — Disarm 후 CanEmit=false 로 발주 0 보증만 담당.
//   ※타이밍/δ=0/다중스레드 → [Collection("ConcurrencyStress")](§말미).
// ============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Portfolio;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests
{
    /// <summary>최소 스텁 — DynamicRiskAdapter 가 실제로 소비하는 TotalValue/TodayPct 만 가변, 나머지는 미사용 경로.</summary>
    internal sealed class StubPortfolioBoardPort : IPortfolioBoardPort
    {
        public decimal TotalValue { get; set; }
        public decimal TotalPnl { get; set; }
        public decimal TotalRet { get; set; }
        public decimal TodayPnl { get; set; }
        public decimal TodayPct { get; set; }
        public decimal UsdKrw { get; set; } = 1300m;
        public string Timestamp { get; set; } = string.Empty;
        public int HoldingCount { get; set; }

        public IReadOnlyList<IHoldingView> Holdings => Array.Empty<IHoldingView>();
        public IReadOnlyList<IHoldingView> Deleted => Array.Empty<IHoldingView>();
        public IReadOnlyList<ICategoryView> Categories => Array.Empty<ICategoryView>();
        public IReadOnlyList<IHoldingView> ViewHoldings(string filter, string sortKey, bool sortDesc) => Array.Empty<IHoldingView>();
        public int FilterCount(string filter) => 0;
        public bool TryGetDayPct(string symbol, out decimal pct) { pct = 0; return false; }
        public string SessionOf(string symbol) => string.Empty;
        public bool TryGetPrevPrice(string symbol, out decimal prev) { prev = 0; return false; }
        public double[] Spark(string symbol) => Array.Empty<double>();
        public System.Threading.Tasks.Task LoadSparksAsync(Func<System.Threading.Tasks.Task> onProgress) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task LoadAsync(bool persist = true) => System.Threading.Tasks.Task.CompletedTask;
        public void LoadCustomOrder() { }
        public System.Threading.Tasks.Task SaveHoldingAsync(HoldingDraft draft, bool edited) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task DeleteHoldingAsync(string symbol, string brokerAccount) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task RestoreHoldingAsync(string symbol, string brokerAccount) => System.Threading.Tasks.Task.CompletedTask;
        public System.Threading.Tasks.Task HardDeleteHoldingAsync(string symbol, string brokerAccount) => System.Threading.Tasks.Task.CompletedTask;
        public void MoveCustom(string symbol, int dir, IReadOnlyList<string> viewSymbols) { }
        public IReadOnlyList<ITaxRowView> TaxRows() => Array.Empty<ITaxRowView>();
        public System.Threading.Tasks.Task<PfCorrResult> CalcCorrelationAsync() => System.Threading.Tasks.Task.FromResult(new PfCorrResult(Array.Empty<string>(), null, string.Empty));
        public System.Threading.Tasks.Task LoadDividendsAsync() => System.Threading.Tasks.Task.CompletedTask;
        public IReadOnlyList<IDividendView> Dividends => Array.Empty<IDividendView>();
        public (decimal commission, decimal tax) EstimateFees(string broker, PfMarket market, PfTxType type, decimal qty, decimal price) => (0m, 0m);
        public IReadOnlyList<string> Brokers => Array.Empty<string>();
        public PfAssetClass Classify(PfMarket market) => default;
        public HoldingDraft ToDraft(IHoldingView h) => new();
    }

    public sealed class DynamicRiskInterlockTests
    {
        private readonly ITestOutputHelper _out;
        public DynamicRiskInterlockTests(ITestOutputHelper o) => _out = o;

        private static DynamicRiskInterlock NewInterlock(out RiskOrderPurgeRecorder recorder, Action<RiskBreachSource, long>? onBreach = null)
        {
            recorder = new RiskOrderPurgeRecorder(8);
            return new DynamicRiskInterlock(recorder, onBreach);
        }

        // ── §1 첫 Note = 베이스라인 확정(판정 생략) ─────────────────────────────
        [Fact]
        public void Note_FirstCall_EstablishesBaseline_NoBreach()
        {
            var il = NewInterlock(out _);
            il.Configure(drawdownThresholdBps: 500, dailyLossThresholdBps: 300);
            Assert.False(il.Note(1_000_000));
            Assert.False(il.IsDisarmed);
            Assert.Equal(1_000_000, il.PeakEquity);
        }

        // ── §2 낙폭(Drawdown) 임계 초과 → 0ms 원자 Disarm ───────────────────────
        [Fact]
        public void Note_DrawdownBreach_Trips_Disarm_Sticky()
        {
            var il = NewInterlock(out var rec);
            il.Configure(drawdownThresholdBps: 500, dailyLossThresholdBps: -1);   // 5% 낙폭
            il.Note(1_000_000);                       // 베이스라인(peak=1,000,000)
            Assert.False(il.Note(970_000));            // 3% 낙폭 — 미달
            Assert.False(il.IsDisarmed);
            Assert.True(il.Note(940_000));              // 6% 낙폭 — 초과 → Disarm
            Assert.True(il.IsDisarmed);
            Assert.False(il.CanEmit);
            Assert.True(il.BreachedSource(RiskBreachSource.Drawdown));
            Assert.False(il.BreachedSource(RiskBreachSource.DailyLoss));

            // sticky — 이후 회복해도 재무장 전까지 유지
            il.Note(1_100_000);
            Assert.True(il.IsDisarmed);
            Assert.False(il.CanEmit);
        }

        // ── §3 일일 최대 손실(Daily Loss) 임계 초과 ─────────────────────────────
        [Fact]
        public void Note_DailyLossBreach_Trips_Disarm()
        {
            var il = NewInterlock(out _);
            il.Configure(drawdownThresholdBps: -1, dailyLossThresholdBps: 200);   // 2% 일일손실, 낙폭 축 비활성
            il.Note(1_000_000);                        // dayStart=1,000,000
            Assert.False(il.Note(985_000));             // 1.5% — 미달
            Assert.True(il.Note(970_000));              // 3.0% — 초과 → Disarm
            Assert.True(il.BreachedSource(RiskBreachSource.DailyLoss));
            Assert.False(il.BreachedSource(RiskBreachSource.Drawdown));
        }

        // ── §4 미설정 축(-1)은 fail-closed 로 판정 생략(무동작) — 초과해도 무시 ────
        [Fact]
        public void Note_UnconfiguredAxis_Skipped_NoBreach()
        {
            var il = NewInterlock(out _);
            il.Configure(drawdownThresholdBps: -1, dailyLossThresholdBps: -1);
            il.Note(1_000_000);
            Assert.False(il.Note(1));   // 사실상 전액 손실이어도 축 미설정이면 무동작
            Assert.False(il.IsDisarmed);
        }

        // ── §5 Disarm 동시 미체결 잔량 일괄 취소(0x00 소거) ─────────────────────
        [Fact]
        public void Breach_Purges_PendingOrderBuffer_ToZero()
        {
            var il = NewInterlock(out var rec);
            il.Configure(300, -1);
            rec.StagePending(0, 500_000);
            rec.StagePending(1, 250_000);
            Assert.False(rec.PendingBufferIsZero);

            il.Note(1_000_000);
            il.Note(960_000);   // 4% 낙폭 → breach

            Assert.True(rec.PendingBufferIsZero);
            Assert.True(rec.HasCaptured);
            Assert.Equal(RiskBreachSource.Drawdown, rec.Frozen.Source);
        }

        // ── §6 Disarm 사유 원자적 기록(onBreach 콜백 → 감사 체인 팬인 지점) ──────
        [Fact]
        public void Breach_Invokes_OnBreach_Callback_With_Reason()
        {
            RiskBreachSource? capturedSource = null;
            long capturedValue = -1;
            var il = NewInterlock(out _, (src, val) => { capturedSource = src; capturedValue = val; });
            il.Configure(500, -1);
            il.Note(1_000_000);
            il.Note(930_000);   // 7% 낙폭

            Assert.Equal(RiskBreachSource.Drawdown, capturedSource);
            Assert.True(capturedValue > 500);
        }

        // ── §7 VaR 스파이크(Idea B 결속) — 순간 손실률 미달이어도 분산 급등이면 선제 트립 ──
        [Fact]
        public void NoteVarBreach_TripsOnHighVariance_EvenBelowDrawdownThreshold()
        {
            var il = NewInterlock(out _);
            il.Configure(drawdownThresholdBps: 100_000, dailyLossThresholdBps: -1, varThresholdBps: 300);   // 낙폭 사실상 비활성
            var var95 = new StreamingParametricVarCalculator(16);
            // 큰 변동성(±200bps 진동) 주입 → VaR 급등
            for (int i = 0; i < 10; i++) var95.Push(i % 2 == 0 ? 200 : -200);

            Assert.False(il.IsDisarmed);
            il.NoteVarBreach(var95.VarBps(1645));
            Assert.True(il.IsDisarmed);
            Assert.True(il.BreachedSource(RiskBreachSource.VarSpike));
        }

        // ── §8 ReArm — 상태/Recorder 초기화, Peak/DayStart 베이스라인 보존 ──────
        [Fact]
        public void ReArm_Resets_State_PreservesBaseline()
        {
            var il = NewInterlock(out var rec);
            il.Configure(300, -1);
            il.Note(1_000_000);
            il.Note(960_000);
            Assert.True(il.IsDisarmed);

            il.ReArm();
            Assert.False(il.IsDisarmed);
            Assert.True(il.CanEmit);
            Assert.Equal(1_000_000, il.PeakEquity);   // 베이스라인 유지(재취득 아님)
        }

        // ── §9 NewTradingDay — 일일손실 베이스라인만 재설정(Peak 은 유지) ────────
        [Fact]
        public void NewTradingDay_Resets_DailyLossBaseline_Only()
        {
            var il = NewInterlock(out _);
            il.Configure(drawdownThresholdBps: -1, dailyLossThresholdBps: 200);
            il.Note(1_000_000);
            il.NewTradingDay(900_000);       // 신규 거래일 시작가 하향(전일 낙폭 반영)
            Assert.False(il.Note(895_000));   // (900k-895k)/900k ≈ 0.55% — 미달
            Assert.True(il.Note(870_000));    // (900k-870k)/900k ≈ 3.3% — 초과
        }

        // ── §10 Parametric VaR — 무변동 표본은 VaR=0 ────────────────────────────
        [Fact]
        public void VarCalculator_ZeroVariance_YieldsZeroVar()
        {
            var v = new StreamingParametricVarCalculator(8);
            for (int i = 0; i < 8; i++) v.Push(50);   // 전부 동일값 → 분산 0
            Assert.Equal(0, v.VarianceBpsSq);
            Assert.Equal(0, v.VarBps(1645));
        }

        // ── §11 DynamicRiskAdapter — IPortfolioBoardPort 결속 통합 실증 ─────────
        [Fact]
        public void Adapter_Note_Bridges_PortfolioBoardPort_To_Interlock_And_AuditStream()
        {
            var board = new StubPortfolioBoardPort { TotalValue = 1_000_000m, TodayPct = 0m };
            var interlock = NewInterlock(out _);
            interlock.Configure(drawdownThresholdBps: 400, dailyLossThresholdBps: -1);
            var varCalc = new StreamingParametricVarCalculator(16);
            var audit = new ZeroAllocForensicAuditStream(16);
            var adapter = new DynamicRiskAdapter(board, interlock, varCalc, audit, () => 12345L);

            adapter.Note();                          // 베이스라인 확정
            Assert.False(adapter.IsDisarmed);

            board.TotalValue = 950_000m;              // 5% 낙폭
            adapter.Note();

            Assert.True(adapter.IsDisarmed);
            Assert.Equal("Drawdown", adapter.DisarmReason);
            Assert.True(adapter.DrawdownPct >= 5m);
            Assert.True(audit.WriteHead >= 1);        // RiskViolation 이벤트가 감사 체인에 기록됨
            Assert.True(audit.VerifyIntegrity());
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  §12 적대검증 — δ=0 핫패스 + 락프리 다중스레드 정합
    // ════════════════════════════════════════════════════════════════════════════
    [Collection("ConcurrencyStress")]
    public sealed class DynamicRiskInterlockConcurrencyTests
    {
        private readonly ITestOutputHelper _out;
        public DynamicRiskInterlockConcurrencyTests(ITestOutputHelper o) => _out = o;

        private static readonly StreamingParametricVarCalculator _hotVar = new(1024);
        private static long _hotSeq;
        private static void PushHot() => _hotVar.Push((Interlocked.Increment(ref _hotSeq) % 400) - 200);

        [Fact]
        public void VarCalculator_Push_ZeroAllocation()
        {
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) PushHot();
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) PushHot();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);   // ★러닝섬/러닝제곱합 VaR 산출 힙 0
        }

        private static readonly RiskOrderPurgeRecorder _hotRecorder = new(8);
        private static readonly DynamicRiskInterlock _hotInterlock = new(_hotRecorder);
        private static long _hotEquity = 1_000_000;
        private static void NoteHot()
        {
            long e = Interlocked.Increment(ref _hotEquity);
            _hotInterlock.Note(e);
        }

        [Fact]
        public void Interlock_Note_ZeroAllocation_WhenNoBreach()
        {
            _hotInterlock.Configure(1_000_000, -1);   // 사실상 도달 불가능한 낙폭 → breach 없음(핫패스만 측정)
            long alloc = 0; Exception? err = null;
            var th = new Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 300_000; i++) NoteHot();
                    alloc = ZeroAllocProbe.MinAllocated(() =>
                    {
                        for (int i = 0; i < 100_000; i++) NoteHot();
                    });
                }
                catch (Exception e) { err = e; }
            });
            th.Start();
            Assert.True(th.Join(30_000));
            Assert.Null(err);
            Assert.Equal(0L, alloc);
        }

        // 다중 스레드가 동시에 낙폭 임계를 넘기는 Note 를 호출해도 Disarm 은 정확히 1회 의미상 격발(경합 지연 불가).
        [Fact]
        public void Interlock_ConcurrentBreach_DisarmsExactlyOnce_NoTornState()
        {
            var recorder = new RiskOrderPurgeRecorder(8);
            int breachCallbacks = 0;
            var il = new DynamicRiskInterlock(recorder, (_, _) => Interlocked.Increment(ref breachCallbacks));
            il.Configure(drawdownThresholdBps: 100, dailyLossThresholdBps: -1);
            il.Note(1_000_000);   // 베이스라인

            var start = new ManualResetEventSlim(false);
            var done = new CountdownEvent(8);
            Exception? err = null;
            for (int t = 0; t < 8; t++)
            {
                var th = new Thread(() =>
                {
                    try { start.Wait(); for (int i = 0; i < 1000; i++) il.Note(950_000); }
                    catch (Exception e) { err = e; }
                    finally { done.Signal(); }
                }) { IsBackground = true };
                th.Start();
            }
            start.Set();
            Assert.True(done.Wait(30_000));
            Assert.Null(err);

            Assert.True(il.IsDisarmed);
            Assert.False(il.CanEmit);
            Assert.True(recorder.PendingBufferIsZero);
            _out.WriteLine($"[DynamicRisk] concurrent breach callbacks={breachCallbacks}");
        }
    }
}
