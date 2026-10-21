// ============================================================================
// KrxSessionStateMachine — [v12.18.51-DEV] KRX 정규장 세션 페이즈 판정(순수 · 무상태 · δ=0)
//
//  페이즈(KST 벽시계 · 매매일 기준 · 경계 = 하한 포함 / 상한 제외):
//   HolidayOrWeekend   주말 · KRX 휴장일(KrxTradingCalendar)
//   CalendarUncovered  달력 표 범위 밖(2024~2027 외) — 판정 불가 = 휴장 취급(fail-closed)
//   Closed             장전 08:30 이전 · 시간외 종료 18:00 이후
//   PreMarketAuction   08:30 ≤ t < 09:00   장 시작 동시호가(단일가)
//   ContinuousTrading  09:00 ≤ t < 15:20   정규 연속매매 — ★AI 주문 후보가 허용되는 유일한 페이즈
//   ClosingAuction     15:20 ≤ t < 15:30   장 마감 동시호가(단일가)
//   PostMarket         15:30 ≤ t < 18:00   장후 시간외
//  수능일: 전 경계 1시간 순연(동시호가 09:30 · 연속 10:00~16:20 · 마감 동시호가 16:20~16:30 · 시간외 ~19:00).
//  연초 첫 거래일: 장 시작 측만 1시간 지연(동시호가 09:30 · 연속 10:00~15:20) · 마감 측 그대로.
//  ★동시호가 · 시간외 단일가는 체결 방식이 연속매매와 달라 신호 시점 가격 근거가 무너진다 — 연속매매 외 전부 차단.
// ============================================================================
using System;

namespace StockManager.Engines.Feeds
{
    public enum KrxSessionPhase : byte
    {
        HolidayOrWeekend = 0,
        Closed = 1,
        PreMarketAuction = 2,
        ContinuousTrading = 3,
        ClosingAuction = 4,
        PostMarket = 5,
        CalendarUncovered = 6,
    }

    public static class KrxSessionStateMachine
    {
        private const long Hour = TimeSpan.TicksPerHour, Minute = TimeSpan.TicksPerMinute;
        private const long PreOpen = 8 * Hour + 30 * Minute, Open = 9 * Hour, ClosingCall = 15 * Hour + 20 * Minute, Close = 15 * Hour + 30 * Minute, PostEnd = 18 * Hour;
        private const long KstOffsetTicks = 9 * Hour;

        // 표 범위(KST 2024-01-01 00:00 ≤ t < 2028-01-01 00:00)의 유닉스 UTC 마이크로초 경계 — 범위 밖 = DateTime 생성 전에 차단(오버플로 · 예외 0)
        private static readonly long CoveredFromUtcMicros = (new DateTime(KrxTradingCalendar.FirstYear, 1, 1).Ticks - KstOffsetTicks - DateTime.UnixEpoch.Ticks) / 10;
        private static readonly long CoveredUntilUtcMicros = (new DateTime(KrxTradingCalendar.LastYear + 1, 1, 1).Ticks - KstOffsetTicks - DateTime.UnixEpoch.Ticks) / 10;

        /// <summary>KST 벽시계 시각의 세션 페이즈(δ=0). Kind 무시 — 값 자체를 KST 로 해석.</summary>
        public static KrxSessionPhase GetSessionPhase(DateTime kstNow)
        {
            if (!KrxTradingCalendar.IsCovered(kstNow)) return KrxSessionPhase.CalendarUncovered;
            if (!KrxTradingCalendar.IsTradingDay(kstNow)) return KrxSessionPhase.HolidayOrWeekend;
            long tod = kstNow.TimeOfDay.Ticks;
            long shift = KrxTradingCalendar.IsCsatDay(kstNow) ? Hour : 0;
            long preOpen = PreOpen + shift, open = Open + shift;
            if (shift == 0 && KrxTradingCalendar.IsFirstTradingDayOfYear(kstNow)) { preOpen += Hour; open += Hour; }
            if (tod < preOpen) return KrxSessionPhase.Closed;
            if (tod < open) return KrxSessionPhase.PreMarketAuction;
            if (tod < ClosingCall + shift) return KrxSessionPhase.ContinuousTrading;
            if (tod < Close + shift) return KrxSessionPhase.ClosingAuction;
            if (tod < PostEnd + shift) return KrxSessionPhase.PostMarket;
            return KrxSessionPhase.Closed;
        }

        /// <summary>유닉스 에포크 UTC 마이크로초의 세션 페이즈(δ=0 · 표 범위 밖 = CalendarUncovered).</summary>
        public static KrxSessionPhase GetSessionPhaseUtcMicros(long utcMicros)
        {
            if (utcMicros < CoveredFromUtcMicros || utcMicros >= CoveredUntilUtcMicros) return KrxSessionPhase.CalendarUncovered;
            return GetSessionPhase(new DateTime(DateTime.UnixEpoch.Ticks + utcMicros * 10 + KstOffsetTicks));
        }

        public static bool IsContinuousTrading(DateTime kstNow) => GetSessionPhase(kstNow) == KrxSessionPhase.ContinuousTrading;

        /// <summary>
        /// [v12.18.52-DEV] 감시 측(피드 정지 · 세션 침묵 워치독 · 호가 이상 인터락) 장중 판정(δ=0): ContinuousTrading = true ·
        /// CalendarUncovered = true(판정 불가 = 엄격 측 · 감시 유지) · 휴장/주말 · 동시호가 · 시간외 · 장외 = false(감시 동결 · 오탐 0).
        /// ★발주 측(리졸버 게이트)은 범위 밖 = 차단 — 두 방향 모두 fail-closed.
        /// </summary>
        public static bool InContinuousWatchWindow(long utcMicros)
            => GetSessionPhaseUtcMicros(utcMicros) is KrxSessionPhase.ContinuousTrading or KrxSessionPhase.CalendarUncovered;

        /// <summary>[v12.18.52-DEV] 감시 측 장중 판정 — 현재 벽시계(DateTime.UtcNow).</summary>
        public static bool InContinuousWatchWindowNow() => InContinuousWatchWindow((DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) / 10);
    }
}
