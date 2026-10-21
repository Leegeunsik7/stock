// ============================================================================
// UsTradingCalendarTests — [v12.18.53-DEV] 미국 증시 휴장일 · 서머타임(EDT/EST) · 세션 페이즈 상태 머신 · $0.01 호가 정렬(δ=0)
//
//   §1 달력: 표 = 검토 목록 전수 일치(양방향) · 오름차순 · 전부 평일 · Good Friday/추수감사절 휴장 · Columbus/Veterans Day 개장 ·
//      조기 폐장일 표 · 표 범위 밖 = fail-closed
//   §2 서머타임: 연도별 시작/종료(3월 둘째 일요일 07:00 UTC · 11월 첫째 일요일 06:00 UTC) · 경계 1틱 · OS 시간대 DB 전수 대조(30분 격자 4년)
//   §3 페이즈: EDT(KST 17:00 · 22:30 · 05:00 · 09:00) / EST(18:00 · 23:30 · 06:00 · 10:00) 경계 · 22:30 KST 정규장 전이 · 매매일 = 미 동부 날짜 ·
//      공휴일 · 조기 폐장(13:00/17:00 ET) · 서머타임 전환 주간 · 범위 밖 · KST/UTC 진입점 일치(7분 격자 4년)
//   §4 호가: $1 이상 $0.01 · 미만 $0.0001 · 보호 방향 내림/올림 · 경계 · ≤0/오버플로 = 0 · 정렬 결과 = 유효 격자점
//   §5 δ=0: 10만 회 세션 판정 + 호가 정렬 ZeroAllocProbe 0B
// ============================================================================
using System;
using System.Collections.Generic;
using StockManager.Engines.Feeds;
using StockManager.Engines.Pricing;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Feeds
{
    [Collection("ConcurrencyStress")]
    public sealed class UsTradingCalendarTests
    {
        private const long S = UsTickAligner.Scale;
        private readonly ITestOutputHelper _out;
        public UsTradingCalendarTests(ITestOutputHelper o) => _out = o;

        // 검토 목록(NYSE Holidays & Trading Hours 2024~2027) — 평일 휴장일만 · 미 동부 날짜
        private static readonly int[] Expected =
        {
            20240101, 20240115, 20240219, 20240329, 20240527, 20240619, 20240704, 20240902, 20241128, 20241225,
            20250101, 20250109, 20250120, 20250217, 20250418, 20250526, 20250619, 20250704, 20250901, 20251127, 20251225,
            20260101, 20260119, 20260216, 20260403, 20260525, 20260619, 20260703, 20260907, 20261126, 20261225,
            20270101, 20270118, 20270215, 20270326, 20270531, 20270618, 20270705, 20270906, 20271125, 20271224,
        };

        private static readonly int[] ExpectedEarly = { 20240703, 20241129, 20241224, 20250703, 20251128, 20251224, 20261127, 20261224, 20271126 };

        public static IEnumerable<object[]> AllHolidays()
        {
            foreach (int k in Expected) yield return new object[] { k };
        }

        private static DateTime Day(int key) => new DateTime(key / 10_000, key / 100 % 100, key % 100);
        private static DateTime Kst(int key, int h, int m, int s = 0) => Day(key).AddHours(h).AddMinutes(m).AddSeconds(s);
        private static long UtcMicrosFromKst(DateTime kst) => (kst.Ticks - TimeSpan.TicksPerHour * 9 - DateTime.UnixEpoch.Ticks) / 10;

        // ════════════ §1 달력 ════════════

        [Fact]
        public void Holiday_Table_Matches_Reviewed_List_Ascending_Weekdays_Only()
        {
            Assert.Equal(Expected.Length, UsTradingCalendar.HolidayCount);
            for (int i = 0; i < Expected.Length; i++)
            {
                int k = UsTradingCalendar.HolidayAt(i);
                Assert.Equal(Expected[i], k);
                if (i > 0) Assert.True(UsTradingCalendar.HolidayAt(i - 1) < k);
                Assert.True(Day(k).DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday), k + " 주말");
            }
            Assert.Equal(ExpectedEarly.Length, UsTradingCalendar.EarlyCloseCount);
            for (int i = 0; i < ExpectedEarly.Length; i++)
            {
                int k = UsTradingCalendar.EarlyCloseAt(i);
                Assert.Equal(ExpectedEarly[i], k);
                if (i > 0) Assert.True(UsTradingCalendar.EarlyCloseAt(i - 1) < k);
                Assert.True(UsTradingCalendar.IsTradingDay(Day(k)), k + " 조기 폐장일 = 개장일이어야 함");
            }
            // 역방향: 표 범위 전 평일 중 비매매일 = 검토 목록 정확히
            var set = new HashSet<int>(Expected);
            for (var d = new DateTime(2024, 1, 1); d.Year <= 2027; d = d.AddDays(1))
            {
                if (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) { Assert.False(UsTradingCalendar.IsTradingDay(d)); continue; }
                Assert.Equal(set.Contains(UsTradingCalendar.ToKey(d)), UsTradingCalendar.IsHoliday(d));
            }
        }

        [Theory]
        [MemberData(nameof(AllHolidays))]
        public void Every_Holiday_Is_Not_A_Trading_Day(int key)
        {
            Assert.True(UsTradingCalendar.IsHoliday(Day(key)));
            Assert.False(UsTradingCalendar.IsTradingDay(Day(key)));
        }

        [Fact]
        public void Good_Friday_Thanksgiving_Closed_Federal_Only_Holidays_Open_Year_End_Rule()
        {
            foreach (int k in new[] { 20240329, 20250418, 20260403, 20270326 }) Assert.False(UsTradingCalendar.IsTradingDay(Day(k)), "Good Friday " + k);
            foreach (int k in new[] { 20241128, 20251127, 20261126, 20271125 }) Assert.False(UsTradingCalendar.IsTradingDay(Day(k)), "Thanksgiving " + k);
            // Columbus Day · Veterans Day = 연방 공휴일이지만 NYSE 개장
            foreach (int k in new[] { 20241014, 20241111, 20251013, 20251111, 20261012, 20261111, 20271011, 20271111 })
                Assert.True(UsTradingCalendar.IsTradingDay(Day(k)), "NYSE 개장 " + k);
            Assert.True(UsTradingCalendar.IsTradingDay(Day(20271231)));                                 // 2028 신정(토) = 12/31 대체 없음
            Assert.True(UsTradingCalendar.IsTradingDay(Day(20260702)));                                 // 7/3 대체 휴장 전일 = 정상(조기 폐장 아님)
            Assert.False(UsTradingCalendar.IsEarlyClose(Day(20260702)));
        }

        [Fact]
        public void Out_Of_Range_Years_Fail_Closed()
        {
            foreach (var d in new[] { new DateTime(2023, 12, 29), new DateTime(2028, 1, 3), DateTime.MinValue, DateTime.MaxValue.Date })
            {
                Assert.False(UsTradingCalendar.IsCovered(d));
                Assert.True(UsTradingCalendar.IsHoliday(d));
                Assert.False(UsTradingCalendar.IsTradingDay(d));
            }
        }

        // ════════════ §2 서머타임 ════════════

        [Theory]
        [InlineData(2024, 3, 10, 11, 3)]
        [InlineData(2025, 3, 9, 11, 2)]
        [InlineData(2026, 3, 8, 11, 1)]
        [InlineData(2027, 3, 14, 11, 7)]
        public void Dst_Boundaries_Second_Sunday_March_First_Sunday_November(int y, int sm, int sd, int em, int ed)
        {
            var start = UsTradingCalendar.DstStartUtc(y);
            var end = UsTradingCalendar.DstEndUtc(y);
            Assert.Equal(new DateTime(y, sm, sd, 7, 0, 0), start);
            Assert.Equal(new DateTime(y, em, ed, 6, 0, 0), end);
            Assert.Equal(DayOfWeek.Sunday, start.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, end.DayOfWeek);
            Assert.False(UsTradingCalendar.IsDaylightSaving(start.AddTicks(-1)));
            Assert.True(UsTradingCalendar.IsDaylightSaving(start));
            Assert.True(UsTradingCalendar.IsDaylightSaving(end.AddTicks(-1)));
            Assert.False(UsTradingCalendar.IsDaylightSaving(end));
            Assert.Equal(new DateTime(y, sm, sd, 3, 0, 0), UsTradingCalendar.ToEastern(start));                // 02:00 EST → 03:00 EDT
            Assert.Equal(new DateTime(y, sm, sd, 1, 59, 59), UsTradingCalendar.ToEastern(start.AddSeconds(-1)));
            Assert.Equal(new DateTime(y, em, ed, 1, 0, 0), UsTradingCalendar.ToEastern(end));                  // 02:00 EDT → 01:00 EST
        }

        [Fact]
        public void Dst_Rule_Matches_Os_Time_Zone_Database_Every_30_Minutes_2024_2027()
        {
            TimeZoneInfo? tz = null;
            foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
            {
                try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); break; }
                catch (TimeZoneNotFoundException) { }
            }
            Assert.NotNull(tz);
            int n = 0;
            for (var u = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); u.Year <= 2027; u = u.AddMinutes(30), n++)
            {
                var os = TimeZoneInfo.ConvertTimeFromUtc(u, tz!);
                var mine = UsTradingCalendar.ToEastern(u);
                if (os != new DateTime(mine.Ticks)) Assert.Fail($"{u:O} OS {os:O} ≠ 규칙 {mine:O}");
                Assert.Equal(tz!.IsDaylightSavingTime(u), UsTradingCalendar.IsDaylightSaving(u));      // UTC 질의(가을 01:00~02:00 벽시계 중복 구간 모호성 0)
            }
            _out.WriteLine($"[DST] OS 시간대 DB 대조 {n:N0} 격자점 전수 일치");
        }

        // ════════════ §3 세션 페이즈 ════════════

        [Theory]
        // EDT(2026-10-21 수 · 서머타임) — KST 17:00 프리 · 22:30 정규 · 익일 05:00 애프터 · 09:00 마감
        [InlineData(20261021, 16, 59, 59, UsSessionPhase.Closed)]
        [InlineData(20261021, 17, 0, 0, UsSessionPhase.PreMarket)]
        [InlineData(20261021, 22, 29, 59, UsSessionPhase.PreMarket)]
        [InlineData(20261021, 22, 30, 0, UsSessionPhase.ContinuousTrading)]
        [InlineData(20261022, 4, 59, 59, UsSessionPhase.ContinuousTrading)]
        [InlineData(20261022, 5, 0, 0, UsSessionPhase.AfterHours)]
        [InlineData(20261022, 8, 59, 59, UsSessionPhase.AfterHours)]
        [InlineData(20261022, 9, 0, 0, UsSessionPhase.Closed)]
        // EST(2026-11-04 수 · 표준시) — KST 18:00 프리 · 23:30 정규 · 익일 06:00 애프터 · 10:00 마감
        [InlineData(20261104, 17, 59, 59, UsSessionPhase.Closed)]
        [InlineData(20261104, 18, 0, 0, UsSessionPhase.PreMarket)]
        [InlineData(20261104, 22, 30, 0, UsSessionPhase.PreMarket)]
        [InlineData(20261104, 23, 29, 59, UsSessionPhase.PreMarket)]
        [InlineData(20261104, 23, 30, 0, UsSessionPhase.ContinuousTrading)]
        [InlineData(20261105, 5, 59, 59, UsSessionPhase.ContinuousTrading)]
        [InlineData(20261105, 6, 0, 0, UsSessionPhase.AfterHours)]
        [InlineData(20261105, 9, 59, 59, UsSessionPhase.AfterHours)]
        [InlineData(20261105, 10, 0, 0, UsSessionPhase.Closed)]
        public void Kst_Session_Boundaries_Edt_And_Est(int key, int h, int m, int s, UsSessionPhase expected)
        {
            var kst = Kst(key, h, m, s);
            Assert.Equal(expected, UsSessionStateMachine.GetSessionPhase(kst));
            Assert.Equal(expected, UsSessionStateMachine.GetSessionPhaseUtcMicros(UtcMicrosFromKst(kst)));
        }

        [Fact]
        public void Edt_2230_Kst_Transitions_PreMarket_To_ContinuousTrading()
        {
            var open = Kst(20261021, 22, 30, 0);
            Assert.True(UsSessionStateMachine.IsDaylightSavingKst(open));
            Assert.Equal(UsSessionPhase.PreMarket, UsSessionStateMachine.GetSessionPhase(open.AddTicks(-1)));
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(open));
            Assert.True(UsSessionStateMachine.IsContinuousTrading(open));
            Assert.False(UsSessionStateMachine.IsContinuousTrading(open.AddTicks(-1)));
            Assert.Equal(new DateTime(2026, 10, 21, 9, 30, 0), UsTradingCalendar.ToEastern(open.AddHours(-9)));
        }

        [Fact]
        public void Trading_Day_Is_Eastern_Date_Not_Kst_Date()
        {
            // KST 토 03:00 = 미 동부 금 14:00(EDT) → 정규장 · KST 월 03:00 = 미 동부 일 14:00 → 주말
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20261024, 3, 0)));
            Assert.Equal(UsSessionPhase.HolidayOrWeekend, UsSessionStateMachine.GetSessionPhase(Kst(20261026, 3, 0)));
            Assert.Equal(UsSessionPhase.PreMarket, UsSessionStateMachine.GetSessionPhase(Kst(20261026, 17, 0)));
            // KST 화(4/4) 04:00 = 미 동부 금(4/3 Good Friday) 15:00 → 휴장 · KST 4/3 22:30 = Good Friday 09:30 → 휴장
            Assert.Equal(UsSessionPhase.HolidayOrWeekend, UsSessionStateMachine.GetSessionPhase(Kst(20260404, 4, 0)));
            Assert.Equal(UsSessionPhase.HolidayOrWeekend, UsSessionStateMachine.GetSessionPhase(Kst(20260403, 22, 30)));
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20260403, 4, 0)));    // = 4/2 목 15:00 ET
        }

        [Theory]
        [InlineData(20261126, 23, 30)]   // Thanksgiving(EST) 정규장 개장 시각
        [InlineData(20261127, 3, 0)]     // = Thanksgiving 13:00 ET
        [InlineData(20261225, 23, 30)]   // Christmas
        [InlineData(20260703, 22, 30)]   // Independence Day 대체(7/3 금)
        [InlineData(20250109, 23, 30)]   // 카터 국장 특별 휴장
        [InlineData(20270618, 22, 30)]   // Juneteenth 대체(6/18 금)
        public void Us_Holidays_Are_Closed_All_Day(int key, int h, int m)
        {
            Assert.Equal(UsSessionPhase.HolidayOrWeekend, UsSessionStateMachine.GetSessionPhase(Kst(key, h, m)));
            for (int hh = 0; hh < 24; hh++)
            {
                var et = Day(key).AddHours(hh);
                if (UsTradingCalendar.ToKey(et) == key && UsTradingCalendar.IsHoliday(Day(key)))
                    Assert.Equal(UsSessionPhase.HolidayOrWeekend, UsSessionStateMachine.GetSessionPhaseEastern(et));
            }
        }

        [Fact]
        public void Early_Close_Ends_Regular_At_1300_Et_And_After_Hours_At_1700_Et()
        {
            // 2026-11-27(금 · 추수감사절 다음 날 · EST): 정규 KST 23:30 ~ 익일 03:00 · 애프터 03:00 ~ 07:00
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20261127, 23, 30)));
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20261128, 2, 59, 59)));
            Assert.Equal(UsSessionPhase.AfterHours, UsSessionStateMachine.GetSessionPhase(Kst(20261128, 3, 0)));
            Assert.Equal(UsSessionPhase.AfterHours, UsSessionStateMachine.GetSessionPhase(Kst(20261128, 6, 59, 59)));
            Assert.Equal(UsSessionPhase.Closed, UsSessionStateMachine.GetSessionPhase(Kst(20261128, 7, 0)));
            // 2025-07-03(목 · EDT): 정규 22:30 ~ 02:00 KST
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20250704, 1, 59, 59)));
            Assert.Equal(UsSessionPhase.AfterHours, UsSessionStateMachine.GetSessionPhase(Kst(20250704, 2, 0)));
            // 대조군: 평일 13:00 ET = 정규장
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhaseEastern(new DateTime(2026, 11, 25, 13, 0, 0)));
        }

        [Fact]
        public void Dst_Transition_Week_Shifts_Kst_Open_By_One_Hour()
        {
            // 2026-03-06(금 · EST) KST 22:30 = 08:30 ET 프리 · 23:30 = 09:30 정규
            Assert.Equal(UsSessionPhase.PreMarket, UsSessionStateMachine.GetSessionPhase(Kst(20260306, 22, 30)));
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20260306, 23, 30)));
            Assert.False(UsSessionStateMachine.IsDaylightSavingKst(Kst(20260306, 22, 30)));
            // 2026-03-09(월 · DST 첫 매매일 · EDT) KST 22:30 = 09:30 ET 정규
            Assert.Equal(UsSessionPhase.PreMarket, UsSessionStateMachine.GetSessionPhase(Kst(20260309, 22, 29, 59)));
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20260309, 22, 30)));
            Assert.True(UsSessionStateMachine.IsDaylightSavingKst(Kst(20260309, 22, 30)));
            // 가을 전환: 2026-10-30(금 · EDT) 정규 종료 KST 05:00 · 2026-11-02(월 · EST) 정규 종료 KST 06:00
            Assert.Equal(UsSessionPhase.AfterHours, UsSessionStateMachine.GetSessionPhase(Kst(20261031, 5, 0)));
            Assert.Equal(UsSessionPhase.ContinuousTrading, UsSessionStateMachine.GetSessionPhase(Kst(20261103, 5, 0)));
            Assert.Equal(UsSessionPhase.AfterHours, UsSessionStateMachine.GetSessionPhase(Kst(20261103, 6, 0)));
        }

        [Fact]
        public void Out_Of_Range_And_Extreme_Inputs_Are_Calendar_Uncovered_Without_Exceptions()
        {
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhase(Kst(20280103, 23, 30)));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhase(Kst(20231229, 23, 30)));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhase(DateTime.MinValue));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhase(DateTime.MaxValue));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhaseUtcMicros(long.MinValue));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhaseUtcMicros(long.MaxValue));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhaseUtcMicros(0));
            // 경계: 2024-01-01 00:00 EST 직전 = 범위 밖 · 2027-12-31 23:59 EST = 범위 안(장외)
            long from = (new DateTime(2024, 1, 1, 5, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhaseUtcMicros(from - 1));
            Assert.Equal(UsSessionPhase.HolidayOrWeekend, UsSessionStateMachine.GetSessionPhaseUtcMicros(from));            // 신정
            long until = (new DateTime(2028, 1, 1, 5, 0, 0).Ticks - DateTime.UnixEpoch.Ticks) / 10;
            Assert.Equal(UsSessionPhase.Closed, UsSessionStateMachine.GetSessionPhaseUtcMicros(until - 1));
            Assert.Equal(UsSessionPhase.CalendarUncovered, UsSessionStateMachine.GetSessionPhaseUtcMicros(until));
        }

        [Fact]
        public void Kst_And_Utc_Entry_Points_Agree_And_Regular_Session_Matches_Calendar_Across_Four_Years()
        {
            int cont = 0;
            for (var k = new DateTime(2024, 1, 1, 14, 0, 0); k < new DateTime(2028, 1, 1, 14, 0, 0); k = k.AddMinutes(7))
            {
                var p = UsSessionStateMachine.GetSessionPhase(k);
                Assert.Equal(p, UsSessionStateMachine.GetSessionPhaseUtcMicros(UtcMicrosFromKst(k)));
                if (p != UsSessionPhase.ContinuousTrading) continue;
                cont++;
                var et = UsTradingCalendar.ToEastern(k.AddHours(-9));
                Assert.True(UsTradingCalendar.IsTradingDay(et));
                Assert.True(et.TimeOfDay >= new TimeSpan(9, 30, 0) && et.TimeOfDay < new TimeSpan(UsTradingCalendar.IsEarlyClose(et) ? 13 : 16, 0, 0));
            }
            Assert.True(cont > 50_000);
        }

        // ════════════ §4 호가 정렬 ════════════

        [Fact]
        public void Tick_Is_Penny_Above_One_Dollar_And_SubPenny_Below()
        {
            Assert.Equal(1_000_000, UsTickAligner.TickFor(187 * S));
            Assert.Equal(1_000_000, UsTickAligner.TickFor(1 * S));
            Assert.Equal(10_000, UsTickAligner.TickFor(S - 1));
            Assert.Equal(0, UsTickAligner.TickFor(0));
            Assert.Equal(0, UsTickAligner.TickFor(-S));

            long p = 187 * S + 45_678_901;                                                     // $187.45678901
            Assert.Equal(187 * S + 45_000_000, UsTickAligner.AlignDown(p));                    // 매수 상한 $187.45
            Assert.Equal(187 * S + 46_000_000, UsTickAligner.AlignUp(p));                      // 매도 하한 $187.46
            Assert.Equal(187 * S + 46_000_000, UsTickAligner.AlignNearest(p));
            Assert.Equal(187 * S + 45_000_000, UsTickAligner.AlignNearest(187 * S + 45_499_999));
            Assert.Equal(187 * S + 46_000_000, UsTickAligner.AlignNearest(187 * S + 45_500_000));

            long sub = 12_345_678;                                                             // $0.12345678
            Assert.Equal(12_340_000, UsTickAligner.AlignDown(sub));                            // $0.1234
            Assert.Equal(12_350_000, UsTickAligner.AlignUp(sub));                              // $0.1235
            Assert.Equal(S, UsTickAligner.AlignUp(S - 1));                                     // $0.99999999 → $1.00(상위 격자점)
            Assert.Equal(S - 10_000, UsTickAligner.AlignDown(S - 1));                          // → $0.9999
            Assert.True(UsTickAligner.IsAligned(S));
            Assert.True(UsTickAligner.IsAligned(250 * S + 1_000_000));
            Assert.False(UsTickAligner.IsAligned(250 * S + 500_000));
            Assert.False(UsTickAligner.IsAligned(0));
        }

        [Fact]
        public void Tick_Alignment_Fails_Closed_On_NonPositive_And_Overflow()
        {
            foreach (long bad in new[] { 0L, -1L, long.MinValue })
            {
                Assert.Equal(0, UsTickAligner.AlignDown(bad));
                Assert.Equal(0, UsTickAligner.AlignUp(bad));
                Assert.Equal(0, UsTickAligner.AlignNearest(bad));
            }
            Assert.Equal(0, UsTickAligner.AlignUp(long.MaxValue));                             // 올림 = 오버플로 → 무효
            Assert.Equal(long.MaxValue - long.MaxValue % 1_000_000, UsTickAligner.AlignDown(long.MaxValue));
        }

        [Fact]
        public void Aligned_Results_Are_Valid_Grid_Points_And_Bracket_Input()
        {
            var rng = new Random(20261021);
            for (int i = 0; i < 200_000; i++)
            {
                long p = i % 2 == 0 ? rng.NextInt64(1, 3 * S) : rng.NextInt64(1, 5_000 * S);
                long d = UsTickAligner.AlignDown(p), u = UsTickAligner.AlignUp(p);
                Assert.True(d <= p && p <= u);
                Assert.True(u - d < UsTickAligner.TickFor(p) + 1 || u == d);
                Assert.True(u == 0 || UsTickAligner.IsAligned(u));
                Assert.True(d == 0 || UsTickAligner.IsAligned(d));
                if (UsTickAligner.IsAligned(p)) Assert.True(d == p && u == p);
            }
        }

        // ════════════ §5 δ=0 ════════════

        [Fact]
        public void HundredK_Us_Session_Judgments_And_Tick_Alignments_Are_Zero_Alloc()
        {
            // EDT/EST · 프리/정규/애프터/장외 · 휴장 · 조기 폐장 · 주말 · 서머타임 전환 주간 · 범위 밖 순환
            long[] probes =
            {
                UtcMicrosFromKst(Kst(20261021, 17, 0)), UtcMicrosFromKst(Kst(20261021, 22, 30)), UtcMicrosFromKst(Kst(20261022, 5, 0)),
                UtcMicrosFromKst(Kst(20261022, 9, 0)), UtcMicrosFromKst(Kst(20261104, 23, 30)), UtcMicrosFromKst(Kst(20261105, 6, 0)),
                UtcMicrosFromKst(Kst(20260403, 22, 30)), UtcMicrosFromKst(Kst(20261128, 3, 0)), UtcMicrosFromKst(Kst(20261026, 3, 0)),
                UtcMicrosFromKst(Kst(20260309, 22, 30)), UtcMicrosFromKst(Kst(20280103, 23, 30)), long.MaxValue,
            };
            long[] prices = { 187 * S + 45_678_901, 12_345_678, S - 1, S, 0, -5, long.MaxValue, 4_321 * S + 1 };
            long sink = 0;
            void Run()
            {
                for (int i = 0; i < 100_000; i++)
                {
                    long t = probes[i % probes.Length];
                    sink += (long)UsSessionStateMachine.GetSessionPhaseUtcMicros(t);
                    var kst = new DateTime(DateTime.UnixEpoch.Ticks + (t == long.MaxValue ? 0 : t * 10) + TimeSpan.TicksPerHour * 9);
                    sink += (long)UsSessionStateMachine.GetSessionPhase(kst);
                    if (UsSessionStateMachine.IsDaylightSavingKst(kst)) sink++;
                    if (UsTradingCalendar.IsHoliday(kst) || UsTradingCalendar.IsEarlyClose(kst)) sink++;
                    long p = prices[i % prices.Length];
                    sink += (UsTickAligner.AlignDown(p) ^ UsTickAligner.AlignUp(p) ^ UsTickAligner.AlignNearest(p)) & 0xFF;
                    if (UsTickAligner.IsAligned(p)) sink++;
                }
            }
            Run();
            long min = ZeroAllocProbe.MinAllocated(Run);
            _out.WriteLine($"[δ] 10만 회 미국 세션 판정(페이즈×2 · DST · 휴장/조기 폐장) + 호가 정렬(내림 · 올림 · 반올림 · 격자) = {min}B · 체크섬 {sink}");
            Assert.True(sink > 0);
            Assert.Equal(0, min);
        }
    }
}
