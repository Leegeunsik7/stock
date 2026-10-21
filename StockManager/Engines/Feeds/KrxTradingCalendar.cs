// ============================================================================
// KrxTradingCalendar — [v12.18.51-DEV] KRX 휴장일(2024~2027) · 수능일 순연 · 연초 첫 거래일 지연 개장 정적 달력(δ=0)
//
//  왜: 리졸버(§145)는 평일 = 개장으로 가정해 평일 공휴일(설/추석 연휴 · 노동절 5/1 · 연말 폐장일 12/31 · 선거일)을 장중으로 오인했다.
//  표: KRX 지정 휴장일 중 평일만(주말은 요일로 판정) · 오름차순 int YYYYMMDD · 무할당 이진 탐색.
//   출처: 공휴일법 · 관공서 공휴일 규정(2026-04-30 개정 = 제헌절 · 노동절 공휴일 + 대체공휴일 적용) · KRX 휴장 공지(2026 지방선거 · 제헌절).
//   2027 = KRX 연간 공지 전(통상 12월) — 공휴일법 + 대체공휴일 규정 + 연말 폐장일 관행으로 산정. 임시공휴일 지정 시 표 갱신 필요.
//  수능일: 정규장 1시간 순연(10:00 개장 · 16:30 마감). 2027(2028학년도) 수능일 = 미공시 → 미등재(정직: 확정 시 추가).
//  연초 첫 거래일: 개장식으로 1시간 지연 개장(10:00) · 마감 15:30 그대로(KRX 관행 · 표가 아닌 규칙으로 산정).
//  ★표 범위 밖(2023 이전 · 2028 이후) = 판정 불가 → IsHoliday true(fail-closed) · IsCovered false — 표 연장 전까지 거래 차단.
// ============================================================================
using System;

namespace StockManager.Engines.Feeds
{
    public static class KrxTradingCalendar
    {
        public const int FirstYear = 2024, LastYear = 2027;

        // KRX 평일 휴장일(오름차순 · 주말 제외)
        private static readonly int[] Holidays =
        {
            // 2024
            20240101,                                   // 신정
            20240209, 20240212,                         // 설날 · 설날 대체(2/11 일)
            20240301,                                   // 삼일절
            20240410,                                   // 제22대 국회의원 선거
            20240501,                                   // 근로자의 날
            20240506,                                   // 어린이날 대체(5/5 일)
            20240515,                                   // 부처님오신날
            20240606,                                   // 현충일
            20240815,                                   // 광복절
            20240916, 20240917, 20240918,               // 추석 연휴
            20241001,                                   // 국군의 날(임시공휴일)
            20241003,                                   // 개천절
            20241009,                                   // 한글날
            20241225,                                   // 성탄절
            20241231,                                   // 연말 폐장일
            // 2025
            20250101,                                   // 신정
            20250127,                                   // 임시공휴일
            20250128, 20250129, 20250130,               // 설날 연휴
            20250303,                                   // 삼일절 대체(3/1 토)
            20250501,                                   // 근로자의 날
            20250505,                                   // 어린이날 · 부처님오신날
            20250506,                                   // 대체공휴일(어린이날 · 부처님오신날 중복)
            20250603,                                   // 제21대 대통령 선거
            20250606,                                   // 현충일
            20250815,                                   // 광복절
            20251003,                                   // 개천절
            20251006, 20251007,                         // 추석 연휴(10/5 일)
            20251008,                                   // 추석 대체
            20251009,                                   // 한글날
            20251225,                                   // 성탄절
            20251231,                                   // 연말 폐장일
            // 2026
            20260101,                                   // 신정
            20260216, 20260217, 20260218,               // 설날 연휴
            20260302,                                   // 삼일절 대체(3/1 일)
            20260501,                                   // 노동절
            20260505,                                   // 어린이날
            20260525,                                   // 부처님오신날 대체(5/24 일)
            20260603,                                   // 제9회 전국동시지방선거
            20260717,                                   // 제헌절(공휴일 재지정)
            20260817,                                   // 광복절 대체(8/15 토)
            20260924, 20260925,                         // 추석 연휴(9/26 토)
            20261005,                                   // 개천절 대체(10/3 토)
            20261009,                                   // 한글날
            20261225,                                   // 성탄절
            20261231,                                   // 연말 폐장일
            // 2027(KRX 연간 공지 전 · 법정 산정)
            20270101,                                   // 신정
            20270208,                                   // 설날(2/6 토 · 2/7 일)
            20270209,                                   // 설날 대체(2/7 일)
            20270301,                                   // 삼일절
            20270503,                                   // 노동절 대체(5/1 토)
            20270505,                                   // 어린이날
            20270513,                                   // 부처님오신날
            20270719,                                   // 제헌절 대체(7/17 토)
            20270816,                                   // 광복절 대체(8/15 일)
            20270914, 20270915, 20270916,               // 추석 연휴
            20271004,                                   // 개천절 대체(10/3 일)
            20271011,                                   // 한글날 대체(10/9 토)
            20271227,                                   // 성탄절 대체(12/25 토)
            20271231,                                   // 연말 폐장일
        };

        // 대학수학능력시험일(정규장 1시간 순연) — 2027(2028학년도) 미공시
        private static readonly int[] CsatDays = { 20241114, 20251113, 20261119 };

        public static int HolidayCount => Holidays.Length;
        public static int CsatDayCount => CsatDays.Length;

        /// <summary>달력 i 번째 휴장일(YYYYMMDD · 검증용).</summary>
        public static int HolidayAt(int index) => Holidays[index];

        public static int ToKey(DateTime date) => date.Year * 10_000 + date.Month * 100 + date.Day;

        public static bool IsCovered(DateTime date) => date.Year is >= FirstYear and <= LastYear;

        /// <summary>KRX 지정 휴장일(평일 공휴일 · 연말 폐장일) 여부(δ=0). 표 범위 밖 = true(판정 불가 = 휴장 · fail-closed). 주말은 포함하지 않음.</summary>
        public static bool IsHoliday(DateTime date) => !IsCovered(date) || Array.BinarySearch(Holidays, ToKey(date)) >= 0;

        /// <summary>수능일(정규장 10:00~16:30 순연) 여부(δ=0).</summary>
        public static bool IsCsatDay(DateTime date) => Array.BinarySearch(CsatDays, ToKey(date)) >= 0;

        /// <summary>매매일(표 범위 안 · 평일 · 휴장일 아님) 여부(δ=0).</summary>
        public static bool IsTradingDay(DateTime date)
            => IsCovered(date) && date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(date);

        /// <summary>연초 첫 거래일(개장식 · 10:00 지연 개장) 여부(δ=0 · 1월 1일부터 전진 탐색).</summary>
        public static bool IsFirstTradingDayOfYear(DateTime date)
        {
            if (date.Month != 1 || !IsTradingDay(date)) return false;
            for (var d = new DateTime(date.Year, 1, 1); d < date.Date; d = d.AddDays(1))
                if (IsTradingDay(d)) return false;
            return true;
        }
    }
}
