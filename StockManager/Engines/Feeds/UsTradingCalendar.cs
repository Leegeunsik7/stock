// ============================================================================
// UsTradingCalendar — [v12.18.53-DEV] 미국 증시(NYSE · NASDAQ) 휴장일(2024~2027) · 조기 폐장일 · 서머타임(EDT/EST) 정적 달력(δ=0)
//
//  왜: 세션 판정이 KRX(KrxTradingCalendar) 전용이라 해외주식(KIS 해외 모의투자) 장중/휴장을 판정할 근거가 없었다.
//  표: NYSE 지정 휴장일 중 평일만(주말은 요일로 판정) · 오름차순 int YYYYMMDD(★미 동부 날짜 기준) · 무할당 이진 탐색.
//   휴장: 신정 · MLK Day · Presidents' Day · Good Friday(연방 공휴일 아님 · NYSE 휴장) · Memorial Day · Juneteenth · Independence Day ·
//         Labor Day · Thanksgiving · Christmas. 토요일 공휴일 = 직전 금요일 · 일요일 공휴일 = 다음 월요일 대체.
//         예외(NYSE Rule 7.2): 신정이 토요일이면 직전 12/31 대체 없음(회계 연말 · 2027-12-31 정상 개장).
//         Columbus Day · Veterans Day = 연방 공휴일이지만 NYSE 개장(표 미등재).
//         특별 휴장: 2025-01-09 카터 전 대통령 국장(National Day of Mourning).
//   조기 폐장(정규장 13:00 ET · 시간외 17:00 ET): 독립기념일 전일(평일 개장일일 때만) · 추수감사절 다음 날 · 크리스마스 이브(평일 개장일일 때만).
//   출처: NYSE Holidays & Trading Hours(2024~2027 공지). 2027 = NYSE 공지 기준 · 임시 휴장(국장 등) 지정 시 표 갱신 필요.
//  서머타임(DST · 미 연방 규칙 2007~): 3월 둘째 일요일 02:00 EST(= 07:00 UTC) ~ 11월 첫째 일요일 02:00 EDT(= 06:00 UTC).
//   EDT = UTC−4(KST − 13h) · EST = UTC−5(KST − 14h). TimeZoneInfo 미사용(OS tz DB 의존 0 · 규칙 산정 · 무할당).
//  ★표 범위 밖(2023 이전 · 2028 이후) = 판정 불가 → IsHoliday true(fail-closed) · IsCovered false — 표 연장 전까지 거래 차단.
// ============================================================================
using System;

namespace StockManager.Engines.Feeds
{
    public static class UsTradingCalendar
    {
        public const int FirstYear = 2024, LastYear = 2027;

        // NYSE 평일 휴장일(오름차순 · 주말 제외 · 미 동부 날짜)
        private static readonly int[] Holidays =
        {
            // 2024
            20240101,                                   // New Year's Day
            20240115,                                   // Martin Luther King Jr. Day
            20240219,                                   // Presidents' Day
            20240329,                                   // Good Friday
            20240527,                                   // Memorial Day
            20240619,                                   // Juneteenth
            20240704,                                   // Independence Day
            20240902,                                   // Labor Day
            20241128,                                   // Thanksgiving
            20241225,                                   // Christmas
            // 2025
            20250101,                                   // New Year's Day
            20250109,                                   // National Day of Mourning(카터 전 대통령 국장 · 특별 휴장)
            20250120,                                   // Martin Luther King Jr. Day
            20250217,                                   // Presidents' Day
            20250418,                                   // Good Friday
            20250526,                                   // Memorial Day
            20250619,                                   // Juneteenth
            20250704,                                   // Independence Day
            20250901,                                   // Labor Day
            20251127,                                   // Thanksgiving
            20251225,                                   // Christmas
            // 2026
            20260101,                                   // New Year's Day
            20260119,                                   // Martin Luther King Jr. Day
            20260216,                                   // Presidents' Day
            20260403,                                   // Good Friday
            20260525,                                   // Memorial Day
            20260619,                                   // Juneteenth
            20260703,                                   // Independence Day 대체(7/4 토)
            20260907,                                   // Labor Day
            20261126,                                   // Thanksgiving
            20261225,                                   // Christmas
            // 2027
            20270101,                                   // New Year's Day
            20270118,                                   // Martin Luther King Jr. Day
            20270215,                                   // Presidents' Day
            20270326,                                   // Good Friday
            20270531,                                   // Memorial Day
            20270618,                                   // Juneteenth 대체(6/19 토)
            20270705,                                   // Independence Day 대체(7/4 일)
            20270906,                                   // Labor Day
            20271125,                                   // Thanksgiving
            20271224,                                   // Christmas 대체(12/25 토)
        };

        // NYSE 조기 폐장일(정규장 13:00 ET 종료 · 시간외 17:00 ET 종료 · 오름차순 · 미 동부 날짜)
        private static readonly int[] EarlyCloses =
        {
            20240703, 20241129, 20241224,               // 2024: 독립기념일 전일 · 추수감사절 다음 날 · 크리스마스 이브
            20250703, 20251128, 20251224,               // 2025
            20261127, 20261224,                         // 2026: 7/3 = 휴장(대체) → 7/2 조기 폐장 없음
            20271126,                                   // 2027: 7/2 · 12/23 조기 폐장 없음(대체 휴장 연접)
        };

        private const long DstStartUtcHour = 7, DstEndUtcHour = 6;

        public static int HolidayCount => Holidays.Length;
        public static int EarlyCloseCount => EarlyCloses.Length;

        /// <summary>달력 i 번째 휴장일(YYYYMMDD · 검증용).</summary>
        public static int HolidayAt(int index) => Holidays[index];

        /// <summary>달력 i 번째 조기 폐장일(YYYYMMDD · 검증용).</summary>
        public static int EarlyCloseAt(int index) => EarlyCloses[index];

        public static int ToKey(DateTime date) => date.Year * 10_000 + date.Month * 100 + date.Day;

        /// <summary>미 동부 날짜가 표 범위 안인지.</summary>
        public static bool IsCovered(DateTime etDate) => etDate.Year is >= FirstYear and <= LastYear;

        /// <summary>NYSE 지정 휴장일 여부(δ=0 · 미 동부 날짜). 표 범위 밖 = true(판정 불가 = 휴장 · fail-closed). 주말은 포함하지 않음.</summary>
        public static bool IsHoliday(DateTime etDate) => !IsCovered(etDate) || Array.BinarySearch(Holidays, ToKey(etDate)) >= 0;

        /// <summary>조기 폐장일(13:00 ET) 여부(δ=0 · 미 동부 날짜).</summary>
        public static bool IsEarlyClose(DateTime etDate) => Array.BinarySearch(EarlyCloses, ToKey(etDate)) >= 0;

        /// <summary>매매일(표 범위 안 · 평일 · 휴장일 아님) 여부(δ=0 · 미 동부 날짜).</summary>
        public static bool IsTradingDay(DateTime etDate)
            => IsCovered(etDate) && etDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(etDate);

        /// <summary>해당 연도 서머타임 시작 시각(UTC · 3월 둘째 일요일 07:00 UTC = 02:00 EST).</summary>
        public static DateTime DstStartUtc(int year)
        {
            var mar1 = new DateTime(year, 3, 1);
            int firstSunday = 1 + (7 - (int)mar1.DayOfWeek) % 7;
            return new DateTime(year, 3, firstSunday + 7).AddTicks(DstStartUtcHour * TimeSpan.TicksPerHour);
        }

        /// <summary>해당 연도 서머타임 종료 시각(UTC · 11월 첫째 일요일 06:00 UTC = 02:00 EDT).</summary>
        public static DateTime DstEndUtc(int year)
        {
            var nov1 = new DateTime(year, 11, 1);
            int firstSunday = 1 + (7 - (int)nov1.DayOfWeek) % 7;
            return new DateTime(year, 11, firstSunday).AddTicks(DstEndUtcHour * TimeSpan.TicksPerHour);
        }

        /// <summary>UTC 시각에 미 동부가 서머타임(EDT)인지(δ=0). Kind 무시 — 값 자체를 UTC 로 해석.</summary>
        public static bool IsDaylightSaving(DateTime utc)
        {
            long t = utc.Ticks;
            return t >= DstStartUtc(utc.Year).Ticks && t < DstEndUtc(utc.Year).Ticks;
        }

        /// <summary>UTC 대비 미 동부 오프셋(시간 · EDT −4 / EST −5).</summary>
        public static int EasternUtcOffsetHours(DateTime utc) => IsDaylightSaving(utc) ? -4 : -5;

        /// <summary>UTC → 미 동부 벽시계(δ=0). DateTime 범위 하단 5시간 이내 = 최소값 포화(표 범위 밖 = 판정 불가 경로).</summary>
        public static DateTime ToEastern(DateTime utc)
        {
            long off = EasternUtcOffsetHours(utc) * TimeSpan.TicksPerHour;
            long t = utc.Ticks + off;
            return t < 0 ? DateTime.MinValue : new DateTime(t);
        }
    }
}
