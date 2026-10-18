// ============================================================================
// IArenaStrategy — [v12.18.15-DEV] 아레나 AI 전략 페르소나 계약(거버너 정산 스레드 = 단일 작성자 · 전 경로 δ=0)
//
//  입력: OnQuote(L2 10호가 · 대상 종목만) · OnTrade(체결가 · 체결량 · 주도 방향) → 내부 지표 갱신.
//  판단: Evaluate(시장 상태 값 구조체) → StrategySignal(관망 · 진입 · 청산 + 사유 + 목표/손절가). 거버너가 원장 상태(무포지션 · 미체결 0)를
//        재확인한 뒤에만 AI 가상 원장에 시장가 주문을 접수한다 — 전략은 원장을 직접 만지지 않는다.
//  Reset: 매치 개시 · 페르소나 교체 · 대상 종목 재지정 시 지표/포지션 기억 초기화.
//  ★공매도 불가 시뮬레이터 → 진입 = 매수 · 청산 = 보유 전량 매도. 브로커 · 디스패처 · 무장 키 참조 0(IL 허용목록 테스트).
// ============================================================================
using MS2E.Infrastructure;
using MS2E.UI.Portable.Ports.Trading;

namespace StockManager.Engines.Arena.Strategy
{
    /// <summary>체결 주도 방향(+1 매수 주도 · −1 매도 주도 · 0 미상).</summary>
    public enum TradeSide { Sell = -1, Unknown = 0, Buy = 1 }

    /// <summary>Evaluate 입력 — 대상 종목 1개의 시장 · AI 원장 상태(원 · 주 · bp · UTC ticks).</summary>
    public readonly struct ArenaMarketState
    {
        public readonly long Symbol, NowTicks, BestBid, BestAsk, LastPx, VwapWon, VwapDevBp, SigmaWon, VwapTrades, PositionQty, PositionCostWon;
        /// <summary>AI 원장에 미완료 주문(지연 대기 · 작동 · 비상 청산 포함)이 하나라도 있으면 true — 결과 대기 중 추가 판단 금지.</summary>
        public readonly bool Busy;

        public ArenaMarketState(long symbol, long nowTicks, long bestBid, long bestAsk, long lastPx, long vwapWon, long vwapDevBp, long sigmaWon,
                                long vwapTrades, long positionQty, long positionCostWon, bool busy)
        {
            Symbol = symbol; NowTicks = nowTicks; BestBid = bestBid; BestAsk = bestAsk; LastPx = lastPx; VwapWon = vwapWon; VwapDevBp = vwapDevBp;
            SigmaWon = sigmaWon; VwapTrades = vwapTrades; PositionQty = positionQty; PositionCostWon = positionCostWon; Busy = busy;
        }

        /// <summary>평균 매입가(원 · 무포지션 = 0).</summary>
        public long AvgCostWon => PositionQty > 0 ? PositionCostWon / PositionQty : 0;
    }

    /// <summary>판단 결과(ArenaAiAction · ArenaAiReason · 목표/손절가 원 · 0 = 없음).</summary>
    public readonly struct StrategySignal
    {
        public readonly int Action, Reason;
        public readonly long TargetPx, StopPx;

        public StrategySignal(int action, int reason, long targetPx = 0, long stopPx = 0)
        {
            Action = action; Reason = reason; TargetPx = targetPx; StopPx = stopPx;
        }

        public static StrategySignal Hold(int reason, long targetPx = 0, long stopPx = 0) => new(ArenaAiAction.Hold, reason, targetPx, stopPx);
        public static StrategySignal Enter(int reason, long targetPx, long stopPx) => new(ArenaAiAction.Enter, reason, targetPx, stopPx);
        public static StrategySignal Exit(int reason, long targetPx = 0, long stopPx = 0) => new(ArenaAiAction.Exit, reason, targetPx, stopPx);
    }

    public interface IArenaStrategy
    {
        /// <summary>ArenaPersona 코드.</summary>
        int Persona { get; }
        void OnQuote(in OrderBookPayload quote);
        void OnTrade(long price, long volume, TradeSide side);
        StrategySignal Evaluate(in ArenaMarketState state);
        void Reset();
    }

    /// <summary>10호가 공용 계산(순수 · δ=0).</summary>
    public static class ArenaBookMath
    {
        /// <summary>매수 총잔량 ÷ (매수 + 매도 총잔량)(bp · 잔량 없음 = 5000).</summary>
        public static int ImbalanceBp(in OrderBookPayload q)
        {
            long b = 0, a = 0;
            for (int l = 0; l < 10; l++) { b += q.BidQty[l]; a += q.AskQty[l]; }
            return b + a <= 0 ? 5_000 : (int)(b * 10_000 / (b + a));
        }

        /// <summary>상위 n단 잔량 합(매수 bid = true).</summary>
        public static long TopQty(in OrderBookPayload q, bool bid, int levels)
        {
            long s = 0;
            for (int l = 0; l < levels && l < 10; l++) s += bid ? q.BidQty[l] : q.AskQty[l];
            return s;
        }

        /// <summary>호가 단위 추정(인접 호가 간격 · 실패 = 1원).</summary>
        public static long TickOf(in OrderBookPayload q)
        {
            long t = q.AskPrice[1] > 0 ? q.AskPrice[1] - q.AskPrice[0] : 0;
            if (t <= 0 && q.BidPrice[1] > 0) t = q.BidPrice[0] - q.BidPrice[1];
            return t > 0 ? t : 1;
        }
    }
}
