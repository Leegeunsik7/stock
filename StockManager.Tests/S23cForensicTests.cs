using System;
using System.Threading;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    // S23-c — 비가역 거부 포렌식 검증.
    //   §1 RejectionForensicAuditor: 박제·링 wrap·락-프리 독출·단일라이터 seqlock torn-read 내성
    //   §2 PromotionGateway 결속: stale 거부 시 임계구역 내 기록(Approve/Activate)·후방호환
    //   §3 SystemStatusAggregator: 락-프리 접근자
    public sealed class S23cForensicTests
    {
        private sealed class TestClock
        {
            public DateTime Now = new DateTime(2026, 4, 1, 9, 0, 0, DateTimeKind.Utc);
            public DateTime Get() => Now;
        }

        private static StrategySpec Spec() => GreenhouseActivator.DefaultSeeds()[0];

        // ═══════════════════ §1 RejectionForensicAuditor ═══════════════════

        [Fact]
        public void Empty_TryGetLatest_False()
        {
            Assert.False(new RejectionForensicAuditor().TryGetLatest(out var rec));
            Assert.False(rec.HasValue);
        }

        [Fact]
        public void Record_ThenLatest_ReturnsIt()
        {
            var a = new RejectionForensicAuditor();
            long seq = a.Record(RejectionStage.Activate, asOfTicks: 1000, nowTicks: 1777, windowTicks: 500, fitness: 2.5, specId: "g-1");
            Assert.Equal(1, seq);
            Assert.True(a.TryGetLatest(out var rec));
            Assert.True(rec.HasValue);
            Assert.Equal(RejectionStage.Activate, rec.Stage);
            Assert.Equal(777, rec.LagTicks);
            Assert.Equal("g-1", rec.SpecId);
            Assert.Equal(1, a.Count);
        }

        [Fact]
        public void RingWraps_LatestIsNewest()
        {
            var a = new RejectionForensicAuditor(capacity: 4);
            for (int i = 1; i <= 6; i++)
                a.Record(RejectionStage.Approve, asOfTicks: i * 10, nowTicks: i * 10 + 3, windowTicks: 1, fitness: i, specId: null);
            Assert.Equal(6, a.Count);                 // 누적 시퀀스(용량 초과)
            Assert.True(a.TryGetLatest(out var rec));
            Assert.Equal(6, rec.Seq);                 // 최신 = 6번째(링 wrap 후에도 정확)
            Assert.Equal(6.0, rec.Fitness);
        }

        [Fact]
        public void LagMs_And_WindowMs_Computed()
        {
            var a = new RejectionForensicAuditor();
            long lagTicks = 14 * TimeSpan.TicksPerMillisecond;     // 14ms 지연
            long win      = 5 * TimeSpan.TicksPerMillisecond;
            a.Record(RejectionStage.Activate, 0, lagTicks, win, 0, null);
            Assert.True(a.TryGetLatest(out var rec));
            Assert.Equal(14.0, rec.LagMs, 3);
            Assert.Equal(5.0, rec.WindowMs, 3);
        }

        [Fact]
        public void SingleWriter_ConcurrentReaders_NoTornRead()
        {
            // 단일 라이터(게이트웨이 _lock 계약) + 동시 리더. seqlock 이 torn read 를 격리해야:
            // 모든 성공 독출 레코드는 내부 일관(LagTicks == 상수 777)해야 한다.
            const long Lag = 777;
            const int  N   = 20000;
            var a = new RejectionForensicAuditor(capacity: 16);
            var failures = 0;
            var stop = false;

            void Reader()
            {
                while (!Volatile.Read(ref stop))
                    if (a.TryGetLatest(out var rec) && rec.HasValue && rec.LagTicks != Lag)
                        Interlocked.Increment(ref failures);
            }

            var r1 = new Thread(Reader) { IsBackground = true };
            var r2 = new Thread(Reader) { IsBackground = true };
            r1.Start(); r2.Start();

            for (int i = 1; i <= N; i++)
                a.Record(RejectionStage.Activate, asOfTicks: i, nowTicks: i + Lag, windowTicks: 1, fitness: i, specId: null);

            Volatile.Write(ref stop, true);
            r1.Join(2000); r2.Join(2000);

            Assert.Equal(0, failures);                 // torn read 0
            Assert.Equal(N, a.Count);
            Assert.True(a.TryGetLatest(out var last) && last.Seq == N);
        }

        // ═══════════════════ §2 PromotionGateway 결속 ═══════════════════

        private static PromotionGateway StaleGated(TestClock clk, RejectionForensicAuditor? f)
            => new PromotionGateway(null, null, s => s == "CMDR")
            { ProposalFreshnessWindow = TimeSpan.FromMinutes(5), Clock = clk.Get, Forensics = f };

        [Fact]
        public void Approve_Stale_RecordsForensic_InsideCriticalSection()
        {
            var clk = new TestClock();
            var f   = new RejectionForensicAuditor();
            var gw  = StaleGated(clk, f);
            long asOf = clk.Now.Ticks;
            gw.Submit(Spec(), 1.0, asOf);
            clk.Now = clk.Now.AddMinutes(10);          // 만료
            Assert.False(gw.Approve("CMDR"));
            Assert.Equal(PromotionState.Rejected, gw.State);

            Assert.True(f.TryGetLatest(out var rec));
            Assert.Equal(RejectionStage.Approve, rec.Stage);
            Assert.Equal(asOf, rec.AsOfTicks);
            Assert.True(rec.LagMs > 0);
        }

        [Fact]
        public void Activate_StaleAfterApprove_RecordsActivateForensic()
        {
            var clk = new TestClock();
            var f   = new RejectionForensicAuditor();
            var gw  = StaleGated(clk, f);
            gw.Submit(Spec(), 1.0, clk.Now.Ticks);
            Assert.True(gw.Approve("CMDR"));           // 신선 → Approved
            clk.Now = clk.Now.AddMinutes(10);          // 승인→활성 갭 열화
            Assert.False(gw.Activate(new StrategySlot()));

            Assert.True(f.TryGetLatest(out var rec));
            Assert.Equal(RejectionStage.Activate, rec.Stage);   // 비가역 직전 관문에서 박제
        }

        [Fact]
        public void NoForensics_StaleRejection_StillWorks_NoCrash()
        {
            var clk = new TestClock();
            var gw  = StaleGated(clk, null);           // Forensics 미주입
            gw.Submit(Spec(), 1.0, clk.Now.Ticks);
            clk.Now = clk.Now.AddMinutes(10);
            Assert.False(gw.Approve("CMDR"));          // 거부는 동작, 기록만 생략
            Assert.Equal(PromotionState.Rejected, gw.State);
        }

        [Fact]
        public void FreshApprove_RecordsNothing()
        {
            var clk = new TestClock();
            var f   = new RejectionForensicAuditor();
            var gw  = StaleGated(clk, f);
            gw.Submit(Spec(), 1.0, clk.Now.Ticks);
            Assert.True(gw.Approve("CMDR"));           // 신선 → 승인(거부 아님)
            Assert.Equal(0, f.Count);                  // 포렌식 기록 0
        }

        // ═══════════════════ §3 SystemStatusAggregator ═══════════════════

        [Fact]
        public void Aggregator_NoForensics_ReturnsFalse()
            => Assert.False(new SystemStatusAggregator().TryGetLatestRejection(out _));

        [Fact]
        public void Aggregator_WithForensics_ReturnsLatestRejection()
        {
            var f = new RejectionForensicAuditor();
            f.Record(RejectionStage.Activate, 100, 900, 200, 1.0, "g-x");
            var agg = new SystemStatusAggregator(forensics: f);
            Assert.True(agg.TryGetLatestRejection(out var rec));
            Assert.Equal("g-x", rec.SpecId);
            Assert.Equal(800, rec.LagTicks);
        }
    }
}
