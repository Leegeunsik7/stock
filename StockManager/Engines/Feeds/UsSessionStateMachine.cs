// ============================================================================
// UsSessionStateMachine — [v12.18.53-DEV] 미국 증시 세션 페이즈 판정(순수 · 무상태 · δ=0 · 서머타임 자동)
//
//  페이즈(미 동부 벽시계 · 매매일 = 미 동부 날짜 · 경계 = 하한 포함 / 상한 제외):
//   HolidayOrWeekend   주말 · NYSE 휴장일(UsTradingCalendar)
//   CalendarUncovered  달력 표 범위 밖(2024~2027 외) — 판정 불가 = 휴장 취급(fail-closed)
//   Closed             04:00 ET 이전 · 시간외 종료 20:00 ET 이후
//   PreMarket          04:00 ≤ t < 09:30 ET   프리마켓
//   ContinuousTrading  09:30 ≤ t < 16:00 ET   정규 연속매매 — ★주문 후보가 허용되는 유일한 페이즈
//   AfterHours         16:00 ≤ t < 20:00 ET   애프터마켓
//  조기 폐장일: 정규장 13:00 ET 종료 · 애프터마켓 13:00~17:00 ET.
//  KST 환산(★매매일은 미 동부 날짜 — KST 자정을 넘는 정규장은 개장 측 날짜의 세션):
//   EDT(서머타임 · KST − 13h): 프리 17:00~22:30 · 정규 22:30~익일 05:00 · 애프터 05:00~09:00 KST
//   EST(표준시   · KST − 14h): 프리 18:00~23:30 · 정규 23:30~익일 06:00 · 애프터 06:00~10:00 KST
//  ★프리/애프터마켓은 유동성 · 호가 공백이 정규장과 달라 신호 시점 가격 근거가 무너진다 — 정규 연속매매 외 전부 차단.
// ============================================================================
using System;

namespace StockManager.Engines.Feeds
{
    public enum UsSessionPhase : byte
    {
        HolidayOrWeekend = 0,
        Closed = 1,
        PreMarket = 2,
        ContinuousTrading = 3,
        AfterHours = 4,
        CalendarUncovered = 5,
    }

    public static class UsSessionStateMachine
    {
        private const long Hour = TimeSpan.TicksPerHour, Minute = TimeSpan.TicksPerMinute;
        private const long PreOpen = 4 * Hour, Open = 9 * Hour + 30 * Minute, Close = 16 * Hour, PostEnd = 20 * Hour;
        private const long EarlyClose = 13 * Hour, EarlyPostEnd = 17 * Hour;
        private const long KstOffsetTicks = 9 * Hour;

        // 표 범위(미 동부 FirstYear-01-01 00:00 EST ≤ t < (LastYear+1)-01-01 00:00 EST — 1월 = 표준시 UTC−5)의 유닉스 UTC 마이크로초 경계
        //  범위 밖 = DateTime 생성 전에 차단(오버플로 · 예외 0)
        private static readonly long CoveredFromUtcMicros = (new DateTime(UsTradingCalendar.FirstYear, 1, 1).Ticks + 5 * Hour - DateTime.UnixEpoch.Ticks) / 10;
        private static readonly long CoveredUntilUtcMicros = (new DateTime(UsTradingCalendar.LastYear + 1, 1, 1).Ticks + 5 * Hour - DateTime.UnixEpoch.Ticks) / 10;

        /// <summary>미 동부 벽시계 시각의 세션 페이즈(δ=0). Kind 무시 — 값 자체를 미 동부로 해석.</summary>
        public static UsSessionPhase GetSessionPhaseEastern(DateTime etNow)
        {
            if (!UsTradingCalendar.IsCovered(etNow)) return UsSessionPhase.CalendarUncovered;
            if (!UsTradingCalendar.IsTradingDay(etNow)) return UsSessionPhase.HolidayOrWeekend;
            long tod = etNow.TimeOfDay.Ticks;
            bool early = UsTradingCalendar.IsEarlyClose(etNow);
            if (tod < PreOpen) return UsSessionPhase.Closed;
            if (tod < Open) return UsSessionPhase.PreMarket;
            if (tod < (early ? EarlyClose : Close)) return UsSessionPhase.ContinuousTrading;
            if (tod < (early ? EarlyPostEnd : PostEnd)) return UsSessionPhase.AfterHours;
            return UsSessionPhase.Closed;
        }

        /// <summary>유닉스 에포크 UTC 마이크로초의 세션 페이즈(δ=0 · 표 범위 밖 = CalendarUncovered).</summary>
        public static UsSessionPhase GetSessionPhaseUtcMicros(long utcMicros)
        {
            if (utcMicros < CoveredFromUtcMicros || utcMicros >= CoveredUntilUtcMicros) return UsSessionPhase.CalendarUncovered;
            return GetSessionPhaseEastern(UsTradingCalendar.ToEastern(new DateTime(DateTime.UnixEpoch.Ticks + utcMicros * 10)));
        }

        /// <summary>KST 벽시계 시각의 세션 페이즈(δ=0). Kind 무시 — 값 자체를 KST 로 해석.</summary>
        public static UsSessionPhase GetSessionPhase(DateTime kstNow)
            => kstNow.Ticks < KstOffsetTicks ? UsSessionPhase.CalendarUncovered : GetSessionPhaseEastern(UsTradingCalendar.ToEastern(new DateTime(kstNow.Ticks - KstOffsetTicks)));

        public static bool IsContinuousTrading(DateTime kstNow) => GetSessionPhase(kstNow) == UsSessionPhase.ContinuousTrading;

        /// <summary>KST 시각에 미 동부가 서머타임(EDT)인지(δ=0).</summary>
        public static bool IsDaylightSavingKst(DateTime kstNow)
            => kstNow.Ticks >= KstOffsetTicks && UsTradingCalendar.IsDaylightSaving(new DateTime(kstNow.Ticks - KstOffsetTicks));

        /// <summary>현재 벽시계(DateTime.UtcNow)의 세션 페이즈.</summary>
        public static UsSessionPhase GetSessionPhaseNow() => GetSessionPhaseUtcMicros((DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10);
    }
}
