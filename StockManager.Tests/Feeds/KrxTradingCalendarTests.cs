// ============================================================================
// KrxTradingCalendarTests — [v12.18.51-DEV] KRX 휴장일 달력(2024~2027) · 세션 페이즈 상태 머신 · 리졸버 결속(δ=0)
//
//   §1 달력: 표 = 검토 목록 전수 일치(양방향) · 오름차순 · 전부 평일 · 설/추석/노동절/연말 폐장일 휴장 · 표 범위 밖 = fail-closed
//   §2 페이즈: 평일 경계(08:30 · 09:00 · 15:20 · 15:20:00.001 · 15:30 · 18:00) · 수능일 1시간 순연 · 연초 첫 거래일 10:00 지연 개장
//   §3 결속: 전 휴장일 · 동시호가 · 시간외 · 수능 09:30 · 범위 밖 = AI 주문 후보 인계 0(대조군 = 직후 매매일 인계) · 적대 4년 스트림
//   §4 δ=0: 10만 회 세션 판정(페이즈 · 휴장 · 리졸버 게이트) ZeroAllocProbe 0B
// ============================================================================
using System;
using System.Collections.Generic;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Engines;
using StockManager.Engines.Feeds;
using StockManager.Engines.Strategy;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Feeds
{
    [Collection("ConcurrencyStress")]
    public sealed class KrxTradingCalendarTests
    {
        private const long S = KrxTickAligner.Scale;
        private const long A = 5930;
        private static readonly long Base = 70_000 * S;
        private readonly ITestOutputHelper _out;
        public KrxTradingCalendarTests(ITestOutputHelper o) => _out = o;

        // 검토 목록(공휴일법 · 관공서 공휴일 규정 2026-04-30 개정 · KRX 휴장 공지) — 평일 휴장일만
        private static readonly int[] Expected =
        {
            20240101, 20240209, 20240212, 20240301, 20240410, 20240501, 20240506, 20240515, 20240606, 20240815,
            20240916, 20240917, 20240918, 20241001, 20241003, 20241009, 20241225, 20241231,
            20250101, 20250127, 20250128, 20250129, 20250130, 20250303, 20250501, 20250505, 20250506, 20250603,
            20250606, 20250815, 20251003, 20251006, 20251007, 20251008, 20251009, 20251225, 20251231,
            20260101, 20260216, 20260217, 20260218, 20260302, 20260501, 20260505, 20260525, 20260603, 20260717,
            20260817, 20260924, 20260925, 20261005, 20261009, 20261225, 20261231,
            20270101, 20270208, 20270209, 20270301, 20270503, 20270505, 20270513, 20270719, 20270816, 20270914,
            20270915, 20270916, 20271004, 20271011, 20271227, 20271231,
        };

        public static IEnumerable<object[]> AllHolidays()
        {
            foreach (int k in Expected) yield return new object[] { k };
        }

        private static DateTime Day(int key) => new DateTime(key / 10_000, key / 100 % 100, key % 100);
        private static DateTime Kst(int key, int h, int m, int s = 0, int ms = 0) => Day(key).AddHours(h).AddMinutes(m).AddSeconds(s).AddMilliseconds(ms);
        private static long Utc(DateTime kst) => (kst.Ticks - TimeSpan.TicksPerHour * 9 - DateTime.UnixEpoch.Ticks) / 10;

        private sealed class CountingSink : IAiProposalSink
        {
            public long Count;
            public bool TrySubmit(in OrderProposal p) { Count++; return true; }
        }

        private sealed class Rig
        {
            public MarketStatusFeedResolver R = null!;
            public AiTradingPipelineService P = null!;
            public CountingSink Sink = null!;
            public long Now;
        }

        // 재동기 보류 0 · 피드 생존 = 세션 페이즈만 남기는 리그
        private static Rig Make(long liveFrom)
        {
            var anomaly = new StrategyAnomalyInterlock();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.StrategyAnomaly);
            anomaly.BindInterlock(mask, ArmingInterlockSource.StrategyAnomaly);
            Assert.True(mask.Seal(out _));
            var sink = new CountingSink();
            var p = new AiTradingPipelineService(new AiExecutionSupervisor(new MultiSymbolAiSignalRouter(anomaly, 16, 1_000, 10), mask), sink);
            Assert.True(p.TryActivate(true, true));
            var g = new Rig { P = p, Sink = sink };
            var r = new MarketStatusFeedResolver(p, null, 0, utcNowMicros: () => System.Threading.Volatile.Read(ref g.Now));
            Assert.True(r.Register(A));
            p.AttachStatusGate(r);
            r.SetFeedAlive(true, liveFrom);
            g.R = r;
            return g;
        }

        private static bool Trade(Rig g, long t)
        {
            g.Now = t;
            g.R.Evaluate(t);
            g.P.OnQuote(A, Base, 69_900 * S, 70_000 * S, MarketTradingStatus.Unknown, t);
            return g.P.OnSignal(new AiSignal(A, 0.9, 0.5, 0.5, t), t) == AiPipelineVerdict.Submitted;
        }

        // ════════════ §1 달력 ════════════

        [Fact]
        public void Table_Matches_Reviewed_List_Exactly_Ascending_Weekdays_Only()
        {
            Assert.Equal(Expected.Length, KrxTradingCalendar.HolidayCount);
            for (int i = 0; i < Expected.Length; i++)
            {
                Assert.Equal(Expected[i], KrxTradingCalendar.HolidayAt(i));
                if (i > 0) Assert.True(Expected[i] > Expected[i - 1]);
                var d = Day(Expected[i]);
                Assert.True(d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday), $"주말 등재 {Expected[i]}");
                Assert.True(KrxTradingCalendar.IsHoliday(d));
                Assert.False(KrxTradingCalendar.IsTradingDay(d));
            }
            // 역방향: 표에 없는 평일 = 휴장 아님(4년 전수)
            int trading = 0;
            for (var d = new DateTime(2024, 1, 1); d.Year <= 2027; d = d.AddDays(1))
            {
                bool weekday = d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
                bool listed = Array.IndexOf(Expected, KrxTradingCalendar.ToKey(d)) >= 0;
                Assert.Equal(listed, KrxTradingCalendar.IsHoliday(d));
                Assert.Equal(weekday && !listed, KrxTradingCalendar.IsTradingDay(d));
                if (weekday && !listed) trading++;
            }
            _out.WriteLine($"[달력] 휴장 {Expected.Length}일 · 매매일 {trading}일(2024~2027)");
        }

        [Theory]
        [InlineData(20240209)] [InlineData(20240212)] [InlineData(20250128)] [InlineData(20250129)] [InlineData(20250130)]
        [InlineData(20260216)] [InlineData(20260217)] [InlineData(20260218)] [InlineData(20270208)] [InlineData(20270209)]   // 설
        [InlineData(20240916)] [InlineData(20240917)] [InlineData(20240918)] [InlineData(20251006)] [InlineData(20251007)]
        [InlineData(20251008)] [InlineData(20260924)] [InlineData(20260925)] [InlineData(20270914)] [InlineData(20270916)]   // 추석
        [InlineData(20240501)] [InlineData(20250501)] [InlineData(20260501)] [InlineData(20270503)]                         // 노동절(2027 대체)
        [InlineData(20241231)] [InlineData(20251231)] [InlineData(20261231)] [InlineData(20271231)]                         // 연말 폐장일
        [InlineData(20260603)] [InlineData(20260717)] [InlineData(20270719)]                                                 // 지방선거 · 제헌절 · 제헌절 대체
        public void Lunar_Holidays_Labor_Day_And_Year_End_Are_Closed_All_Day(int key)
        {
            Assert.True(KrxTradingCalendar.IsHoliday(Day(key)));
            foreach (int h in new[] { 8, 9, 10, 12, 15, 16 })
                Assert.Equal(KrxSessionPhase.HolidayOrWeekend, KrxSessionStateMachine.GetSessionPhase(Kst(key, h, 45)));
        }

        [Fact]
        public void Outside_Table_Range_Is_Fail_Closed()
        {
            foreach (var d in new[] { new DateTime(2023, 12, 28, 10, 0, 0), new DateTime(2028, 1, 4, 10, 0, 0), DateTime.MinValue, DateTime.MaxValue })
            {
                Assert.True(KrxTradingCalendar.IsHoliday(d));
                Assert.False(KrxTradingCalendar.IsTradingDay(d));
                Assert.Equal(KrxSessionPhase.CalendarUncovered, KrxSessionStateMachine.GetSessionPhase(d));
            }
            foreach (long us in new[] { long.MinValue, -1L, 0L, Utc(new DateTime(2023, 12, 31, 23, 59, 59)), Utc(new DateTime(2028, 1, 3, 10, 0, 0)), long.MaxValue })
                Assert.Equal(KrxSessionPhase.CalendarUncovered, KrxSessionStateMachine.GetSessionPhaseUtcMicros(us));
        }

        // ════════════ §2 페이즈 ════════════

        [Theory]
        [InlineData(8, 29, 59, 999, KrxSessionPhase.Closed)]
        [InlineData(8, 30, 0, 0, KrxSessionPhase.PreMarketAuction)]
        [InlineData(8, 45, 0, 0, KrxSessionPhase.PreMarketAuction)]
        [InlineData(8, 59, 59, 999, KrxSessionPhase.PreMarketAuction)]
        [InlineData(9, 0, 0, 0, KrxSessionPhase.ContinuousTrading)]
        [InlineData(15, 19, 59, 999, KrxSessionPhase.ContinuousTrading)]
        [InlineData(15, 20, 0, 0, KrxSessionPhase.ClosingAuction)]
        [InlineData(15, 20, 0, 1, KrxSessionPhase.ClosingAuction)]
        [InlineData(15, 25, 0, 0, KrxSessionPhase.ClosingAuction)]
        [InlineData(15, 30, 0, 0, KrxSessionPhase.PostMarket)]
        [InlineData(17, 59, 59, 999, KrxSessionPhase.PostMarket)]
        [InlineData(18, 0, 0, 0, KrxSessionPhase.Closed)]
        [InlineData(0, 0, 0, 0, KrxSessionPhase.Closed)]
        public void Regular_Day_Phase_Boundaries(int h, int m, int s, int ms, KrxSessionPhase expect)
        {
            var kst = Kst(20261021, h, m, s, ms);
            Assert.Equal(expect, KrxSessionStateMachine.GetSessionPhase(kst));
            Assert.Equal(expect, KrxSessionStateMachine.GetSessionPhaseUtcMicros(Utc(kst)));
        }

        [Theory]
        [InlineData(20241114)] [InlineData(20251113)] [InlineData(20261119)]
        public void Csat_Day_Shifts_Session_By_One_Hour(int key)
        {
            Assert.True(KrxTradingCalendar.IsCsatDay(Day(key)));
            Assert.Equal(KrxSessionPhase.Closed, KrxSessionStateMachine.GetSessionPhase(Kst(key, 9, 0)));
            Assert.Equal(KrxSessionPhase.PreMarketAuction, KrxSessionStateMachine.GetSessionPhase(Kst(key, 9, 30)));       // ★09:30 차단
            Assert.Equal(KrxSessionPhase.PreMarketAuction, KrxSessionStateMachine.GetSessionPhase(Kst(key, 9, 59, 59, 999)));
            Assert.Equal(KrxSessionPhase.ContinuousTrading, KrxSessionStateMachine.GetSessionPhase(Kst(key, 10, 0)));
            Assert.Equal(KrxSessionPhase.ContinuousTrading, KrxSessionStateMachine.GetSessionPhase(Kst(key, 10, 15)));    // ★10:15 허용
            Assert.Equal(KrxSessionPhase.ContinuousTrading, KrxSessionStateMachine.GetSessionPhase(Kst(key, 15, 25)));    // 평일이면 동시호가 · 수능일은 연속
            Assert.Equal(KrxSessionPhase.ClosingAuction, KrxSessionStateMachine.GetSessionPhase(Kst(key, 16, 20)));
            Assert.Equal(KrxSessionPhase.ClosingAuction, KrxSessionStateMachine.GetSessionPhase(Kst(key, 16, 29)));
            Assert.Equal(KrxSessionPhase.PostMarket, KrxSessionStateMachine.GetSessionPhase(Kst(key, 16, 30)));
            Assert.False(KrxTradingCalendar.IsCsatDay(Day(key).AddDays(-1)));                                             // 전날 = 평상
            Assert.Equal(KrxSessionPhase.ContinuousTrading, KrxSessionStateMachine.GetSessionPhase(Day(key).AddDays(-1).AddHours(9).AddMinutes(30)));
        }

        [Theory]
        [InlineData(20240102)] [InlineData(20250102)] [InlineData(20260102)] [InlineData(20270104)]
        public void First_Trading_Day_Of_Year_Opens_At_1000_Closes_As_Usual(int key)
        {
            Assert.True(KrxTradingCalendar.IsFirstTradingDayOfYear(Day(key)));
            Assert.False(KrxTradingCalendar.IsFirstTradingDayOfYear(Day(key).AddDays(key == 20260102 ? 3 : 1)));        // 다음 매매일 = 평상
            Assert.Equal(KrxSessionPhase.Closed, KrxSessionStateMachine.GetSessionPhase(Kst(key, 9, 0)));
            Assert.Equal(KrxSessionPhase.PreMarketAuction, KrxSessionStateMachine.GetSessionPhase(Kst(key, 9, 45)));
            Assert.Equal(KrxSessionPhase.ContinuousTrading, KrxSessionStateMachine.GetSessionPhase(Kst(key, 10, 0)));
            Assert.Equal(KrxSessionPhase.ClosingAuction, KrxSessionStateMachine.GetSessionPhase(Kst(key, 15, 20)));
            Assert.Equal(KrxSessionPhase.PostMarket, KrxSessionStateMachine.GetSessionPhase(Kst(key, 15, 30)));
        }

        // ════════════ §3 리졸버 결속 ════════════

        [Theory]
        [MemberData(nameof(AllHolidays))]
        public void Every_Holiday_Blocks_Ai_Candidates_Next_Trading_Day_Admits(int key)
        {
            var g = Make(Utc(Kst(key, 0, 0)));
            foreach (int h in new[] { 9, 10, 13, 15 })
            {
                long t = Utc(Kst(key, h, 10));
                Assert.False(Trade(g, t));
                Assert.Equal(MarketTradingStatus.Closed, g.R.EffectiveStatus(A, t));
                Assert.Equal(KrxSessionPhase.HolidayOrWeekend, g.R.SessionPhase(t));
            }
            Assert.Equal(0, g.Sink.Count);
            var next = Day(key).AddDays(1);
            while (!KrxTradingCalendar.IsTradingDay(next) && next.Year <= 2027) next = next.AddDays(1);
            if (next.Year > 2027) return;                                                     // 2027-12-31 = 표 마지막 날
            Assert.True(Trade(g, Utc(next.AddHours(11))));                                    // 대조군: 직후 매매일 11:00 = 인계
            Assert.Equal(1, g.Sink.Count);
        }

        [Fact]
        public void Auction_And_Boundary_Instants_Block_Ai_Candidates()
        {
            var g = Make(Utc(Kst(20261021, 0, 0)));
            Assert.False(Trade(g, Utc(Kst(20261021, 8, 45))));                                  // 장 시작 동시호가
            Assert.False(Trade(g, Utc(Kst(20261021, 8, 59, 59, 999))));
            Assert.True(Trade(g, Utc(Kst(20261021, 9, 0))));
            Assert.True(Trade(g, Utc(Kst(20261021, 15, 19, 59, 999))));
            long edge = Utc(Kst(20261021, 15, 20, 0, 1));                                       // ★15:20:00.001 — Evaluate 없이 신호 시점 게이트만으로
            g.Now = edge;
            g.P.OnQuote(A, Base, 69_900 * S, 70_000 * S, MarketTradingStatus.Unknown, edge);
            Assert.Equal(AiPipelineVerdict.StatusGateBlocked, g.P.OnSignal(new AiSignal(A, 0.9, 0.5, 0.5, edge), edge));
            Assert.False(Trade(g, Utc(Kst(20261021, 15, 25))));                                 // 장 마감 동시호가
            Assert.False(Trade(g, Utc(Kst(20261021, 16, 0))));                                  // 시간외
            Assert.Equal(2, g.Sink.Count);

            var c = Make(Utc(Kst(20261119, 0, 0)));                                              // 수능일
            Assert.False(Trade(c, Utc(Kst(20261119, 9, 30))));
            Assert.True(Trade(c, Utc(Kst(20261119, 10, 15))));
            Assert.True(Trade(c, Utc(Kst(20261119, 15, 25))));
            Assert.False(Trade(c, Utc(Kst(20261119, 16, 25))));

            var f = Make(Utc(Kst(20270104, 0, 0)));                                              // 연초 첫 거래일
            Assert.False(Trade(f, Utc(Kst(20270104, 9, 30))));
            Assert.True(Trade(f, Utc(Kst(20270104, 10, 0))));

            var u = Make(Utc(new DateTime(2028, 1, 3)));                                         // 표 범위 밖
            Assert.False(Trade(u, Utc(new DateTime(2028, 1, 3, 11, 0, 0))));
            Assert.Equal(KrxSessionPhase.CalendarUncovered, u.R.SessionPhase(Utc(new DateTime(2028, 1, 3, 11, 0, 0))));
        }

        [Fact]
        public void Adversarial_Four_Year_Stream_Admits_Only_In_Continuous_Trading()
        {
            var rng = new Random(20261021);
            long t = Utc(new DateTime(2023, 12, 30));
            long end = Utc(new DateTime(2028, 1, 5));
            var g = Make(t);
            long submitted = 0;
            var blocked = new long[7];
            while (t < end)
            {
                t += rng.Next(1, 26 * 60) * 60_000_000L + rng.Next(0, 60_000_000);
                var phase = g.R.SessionPhase(t);
                bool ok = Trade(g, t);
                if (ok)
                {
                    Assert.Equal(KrxSessionPhase.ContinuousTrading, phase);                       // ★비정규 세션 인계 0
                    var kst = new DateTime(DateTime.UnixEpoch.Ticks + t * 10 + TimeSpan.TicksPerHour * 9);
                    Assert.True(KrxTradingCalendar.IsTradingDay(kst));
                    Assert.True(Array.IndexOf(Expected, KrxTradingCalendar.ToKey(kst)) < 0);
                    submitted++;
                }
                else blocked[(int)phase]++;
            }
            _out.WriteLine($"[적대] 인계 {submitted} · 차단 휴장/주말 {blocked[0]} · 장외 {blocked[1]} · 장전동시호가 {blocked[2]} · 연속(기타사유) {blocked[3]} · 마감동시호가 {blocked[4]} · 시간외 {blocked[5]} · 범위밖 {blocked[6]}");
            Assert.True(submitted > 300);
            Assert.True(blocked[0] > 0 && blocked[1] > 0 && blocked[5] > 0 && blocked[6] > 0);
        }

        // ════════════ §4 δ=0 ════════════

        [Fact]
        public void HundredK_Session_Judgments_Are_Zero_Alloc()
        {
            // 평일 · 휴장 · 수능 · 연초 첫 거래일 · 1월(전진 탐색 경로) · 범위 밖 순환
            long[] probes =
            {
                Utc(Kst(20261021, 8, 45)), Utc(Kst(20261021, 10, 0)), Utc(Kst(20261021, 15, 20, 0, 1)), Utc(Kst(20261021, 16, 0)),
                Utc(Kst(20260216, 10, 0)), Utc(Kst(20261231, 10, 0)), Utc(Kst(20261119, 9, 30)), Utc(Kst(20261119, 10, 15)),
                Utc(Kst(20270104, 9, 30)), Utc(Kst(20270129, 11, 0)), Utc(Kst(20250501, 10, 0)), Utc(new DateTime(2028, 1, 3, 10, 0, 0)),
            };
            var g = Make(probes[0]);
            long sink = 0;
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    long t = probes[i % probes.Length];
                    sink += (long)KrxSessionStateMachine.GetSessionPhaseUtcMicros(t);
                    sink += (long)KrxSessionStateMachine.GetSessionPhase(new DateTime(DateTime.UnixEpoch.Ticks + t * 10 + TimeSpan.TicksPerHour * 9));
                    if (KrxTradingCalendar.IsHoliday(new DateTime(DateTime.UnixEpoch.Ticks + t * 10))) sink++;
                    g.Now = t;
                    if (g.R.IsTradable(A)) sink++;
                    if ((i & 63) == 0) g.R.Evaluate(t);
                }
            }
            Run();
            long min = ZeroAllocProbe.MinAllocated(Run);
            _out.WriteLine($"[δ] 10만 회 세션 판정(페이즈×2 · 휴장 · 리졸버 게이트 · 평가) = {min}B · 체크섬 {sink}");
            Assert.True(sink > 0);
            Assert.Equal(0, min);
        }
    }
}
