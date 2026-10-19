// ============================================================================
// ArenaConsolePort — [v12.18.14-DEV] 아레나 프로 콘솔 계약(DOM 래더 깊이 · 사후 복기) — 순수 UI 경계 · 엔진 타입 참조 0
//
//   ArenaDepth: 포커스 종목 10호가 + 휴먼 대기 지정가(가격별 내 수량 · 맨 앞 내 주문의 앞 잔량) + 보유/매도 가능 수량 — 거버너가 50ms 게시 때 seqlock 사본으로 투영.
//   ArenaDebrief: 진영별 왕복 거래(포지션 0 → 보유 → 0) 단위 사후 분석 — MAE/MFE · 보유 시간 · 슬리피지 · 틸트(연패 직후 주문 주기 급단축/규모 급증).
//   ★읽기 전용 값 — 주문 경로는 IArenaPort(가상 원장 큐)뿐이다.
// ============================================================================
using System;
using System.Runtime.CompilerServices;

namespace MS2E.UI.Portable.Ports.Trading
{
    /// <summary>10단 고정 인라인 버퍼(힙 0).</summary>
    [InlineArray(ArenaDepth.Levels)]
    public struct ArenaLevels10 { private long _e0; }

    /// <summary>DOM 래더 1프레임(값 복사). 지수 0 = 최우선. 가격 0 = 호가 없음. MyAhead = 그 가격 맨 앞 내 주문의 앞 잔량(−1 = 내 주문 없음).</summary>
    public struct ArenaDepth
    {
        public const int Levels = 10;
        public long Symbol, BookVersion, PositionQty, Sellable, PositionCostWon;
        /// <summary>[v12.18.23-DEV] 숏커버 가능 수량(숏 − 커버 예약 · 롱/무포지션 = 0). PositionQty &lt; 0 = 숏 · PositionCostWon = 숏 순매도 대금.</summary>
        public long Coverable;
        public bool HasBook;
        public ArenaLevels10 AskPx, AskQty, BidPx, BidQty, MyAskQty, MyAskAhead, MyBidQty, MyBidAhead;
        /// <summary>래더 밖 가격의 내 대기 지정가 수량(매수 · 매도 합).</summary>
        public long MyOffLadderQty;
        /// <summary>[v12.18.18-DEV] 내 빙산 지정가의 은닉 잔량 합(노출 수량은 MyAskQty/MyBidQty) · 포식자가 1호가 표시 잔량에 얹은 허수(가상 · 체결 불가 · AskQty/BidQty[0] 에 이미 합산).</summary>
        public long MyHiddenQty, PhantomAskQty, PhantomBidQty;

        public readonly long BestAsk => HasBook ? AskPx[0] : 0;
        public readonly long BestBid => HasBook ? BidPx[0] : 0;
    }

    /// <summary>틸트 사유(비트 · 0 = 없음).</summary>
    public static class ArenaTilt
    {
        public const int None = 0, Frequency = 1, Size = 2;
        public static string Label(int reason) => reason switch
        {
            Frequency => "연패 직후 주문 주기 급단축",
            Size => "연패 직후 주문 규모 급증",
            Frequency | Size => "연패 직후 주기 급단축 + 규모 급증",
            _ => "없음",
        };
    }

    /// <summary>진영 1개 사후 복기(원 · ms · bp). MAE ≤ 0(최대 역행) · MFE ≥ 0(최대 순행) — 왕복 거래 단위.</summary>
    public readonly struct ArenaDebrief
    {
        public readonly int Trips, Wins, Losses, OpenTrips, LossStreak, MaxLossStreak, TiltAlerts, LastTiltReason;
        public readonly long AvgHoldMs, MaxHoldMs, MaeWorstWon, MaeAvgWon, MfeBestWon, MfeAvgWon, RealizedWon, SlippageDragWon, TripSlippageWon, CaptureBp;
        public readonly long OpenMaeWon, OpenMfeWon;
        /// <summary>
        /// [v12.18.25-DEV] 포렌식: Gross(마찰 전) · Net(수수료 · 거래세 · 슬리피지 후 = 자산 − 자본금) · 누적 수수료/거래세 · MDD ·
        /// 틱 단위 가격 MAE/MFE(평균 bp) · 승률/손익비(bp · −1 = 손실 0 무한) · 청산 효율(실현 이동 ÷ MFE bp) · MAE 대비 실현(bp) · 최근 거래 1건.
        /// </summary>
        public readonly long GrossPnlWon, NetPnlWon, FeesWon, TaxWon, MddWon, GrossProfitWon, GrossLossWon, WinRateBp, ProfitFactorBp,
                             AvgMaeBp, AvgMfeBp, ExitEfficiencyBp, MaeToRealizedBp, LastEntryPx, LastExitPx, LastMaePx, LastMfePx, LastHoldMs, LastGrossWon, LastNetWon;
        public readonly int MddBp, QualityTrips, LastSide;

        public ArenaDebrief(int trips, int wins, int losses, int openTrips, int lossStreak, int maxLossStreak, int tiltAlerts, int lastTiltReason,
                            long avgHoldMs, long maxHoldMs, long maeWorstWon, long maeAvgWon, long mfeBestWon, long mfeAvgWon, long realizedWon,
                            long slippageDragWon, long tripSlippageWon, long captureBp, long openMaeWon, long openMfeWon,
                            long grossPnlWon = 0, long netPnlWon = 0, long feesWon = 0, long taxWon = 0, long mddWon = 0, int mddBp = 0,
                            int qualityTrips = 0, long winRateBp = 0, long profitFactorBp = 0, long grossProfitWon = 0, long grossLossWon = 0,
                            long avgMaeBp = 0, long avgMfeBp = 0, long exitEfficiencyBp = 0, long maeToRealizedBp = 0,
                            int lastSide = 0, long lastEntryPx = 0, long lastExitPx = 0, long lastMaePx = 0, long lastMfePx = 0, long lastHoldMs = 0,
                            long lastGrossWon = 0, long lastNetWon = 0)
        {
            GrossPnlWon = grossPnlWon; NetPnlWon = netPnlWon; FeesWon = feesWon; TaxWon = taxWon; MddWon = mddWon; MddBp = mddBp;
            QualityTrips = qualityTrips; WinRateBp = winRateBp; ProfitFactorBp = profitFactorBp; GrossProfitWon = grossProfitWon; GrossLossWon = grossLossWon;
            AvgMaeBp = avgMaeBp; AvgMfeBp = avgMfeBp; ExitEfficiencyBp = exitEfficiencyBp; MaeToRealizedBp = maeToRealizedBp;
            LastSide = lastSide; LastEntryPx = lastEntryPx; LastExitPx = lastExitPx; LastMaePx = lastMaePx; LastMfePx = lastMfePx; LastHoldMs = lastHoldMs;
            LastGrossWon = lastGrossWon; LastNetWon = lastNetWon;
            Trips = trips; Wins = wins; Losses = losses; OpenTrips = openTrips; LossStreak = lossStreak; MaxLossStreak = maxLossStreak;
            TiltAlerts = tiltAlerts; LastTiltReason = lastTiltReason; AvgHoldMs = avgHoldMs; MaxHoldMs = maxHoldMs;
            MaeWorstWon = maeWorstWon; MaeAvgWon = maeAvgWon; MfeBestWon = mfeBestWon; MfeAvgWon = mfeAvgWon; RealizedWon = realizedWon;
            SlippageDragWon = slippageDragWon; TripSlippageWon = tripSlippageWon; CaptureBp = captureBp; OpenMaeWon = openMaeWon; OpenMfeWon = openMfeWon;
        }
    }
}
