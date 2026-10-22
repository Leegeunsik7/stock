// ============================================================================
// IArenaModeEngine — v12.18.59-DEV: 휴먼 vs AI 듀얼 포트폴리오 아레나 모드 공용 계약(국내 대형주 · 이후 해외/크립토 모드)
//
//   진영 인덱스 = ArenaSide(Ai 0 · Human 1). 양 진영은 같은 자본금(기본 1억)으로 시작하는 독립 가상 원장 — 현금/보유/손익 공유 0.
//   판독(스냅샷 · 보유 · 근거 피드)은 seqlock 사본(잠금 · 할당 0). 휴먼 주문은 큐 적재만(체결은 엔진 단일 작성자 스레드).
//   ★liveArmed:false — 계약에 브로커 · 무장 · 실주문 경로 없음(순수 인메모리 가상 대결).
// ============================================================================
using System;

namespace StockManager.Engines.Arena.Common
{
    /// <summary>진영 1개 정산 지표(원 · bp). 평가 = 최우선 매수호가 기준 청산가치.</summary>
    public readonly struct DuelBookStats
    {
        public readonly long CapitalWon, CashWon, EquityWon, RealizedWon, UnrealizedWon, FeesWon, TaxWon, PeakWon, MddBp, Fills, Rejects;
        public readonly int Wins, Losses, OpenPositions;

        public DuelBookStats(long capitalWon, long cashWon, long equityWon, long realizedWon, long unrealizedWon, long feesWon, long taxWon,
            long peakWon, long mddBp, long fills, long rejects, int wins, int losses, int openPositions)
        {
            CapitalWon = capitalWon; CashWon = cashWon; EquityWon = equityWon; RealizedWon = realizedWon; UnrealizedWon = unrealizedWon;
            FeesWon = feesWon; TaxWon = taxWon; PeakWon = peakWon; MddBp = mddBp; Fills = fills; Rejects = rejects;
            Wins = wins; Losses = losses; OpenPositions = openPositions;
        }

        public long ReturnBp => CapitalWon <= 0 ? 0 : (EquityWon - CapitalWon) * 10000 / CapitalWon;
        public long WinRateBp => Wins + Losses == 0 ? 0 : Wins * 10000L / (Wins + Losses);
    }

    /// <summary>아레나 스냅샷(엔진 정산 1회 = 버전 1 증가).</summary>
    public readonly struct DuelSnapshot
    {
        public readonly long Ticks, DroppedQuotes, RationaleCount, LastKstTicks;
        public readonly bool SessionOpen;
        public readonly int MacroBp;
        public readonly DuelBookStats Ai, Human;

        public DuelSnapshot(long ticks, long droppedQuotes, long rationaleCount, long lastKstTicks, bool sessionOpen, int macroBp, in DuelBookStats ai, in DuelBookStats human)
        {
            Ticks = ticks; DroppedQuotes = droppedQuotes; RationaleCount = rationaleCount; LastKstTicks = lastKstTicks;
            SessionOpen = sessionOpen; MacroBp = macroBp; Ai = ai; Human = human;
        }

        public DuelBookStats Side(int side) => side == MS2E.UI.Portable.Ports.Trading.ArenaSide.Human ? Human : Ai;
    }

    /// <summary>보유 1종목(원 · 주). Target/StopLoss = AI 진입 계획(휴먼 = 0).</summary>
    public readonly struct DuelPosition
    {
        public readonly long Symbol, Qty, AvgPriceWon, CostWon, MarkWon, TargetPrice, StopLossPrice;

        public DuelPosition(long symbol, long qty, long avgPriceWon, long costWon, long markWon, long targetPrice, long stopLossPrice)
        {
            Symbol = symbol; Qty = qty; AvgPriceWon = avgPriceWon; CostWon = costWon; MarkWon = markWon; TargetPrice = targetPrice; StopLossPrice = stopLossPrice;
        }
    }

    public interface IArenaModeEngine
    {
        /// <summary>모드 표시명(예: "국내 대형주(KRX)").</summary>
        string ModeName { get; }
        /// <summary>진영별 시작 자본금(원).</summary>
        long CapitalWon { get; }
        /// <summary>최신 게시 버전(0 = 정산 이력 없음 · 짝수 = 안정).</summary>
        long Version { get; }
        /// <summary>야간 글로벌 지표 가중치(bp · ±10000 클램프 · 다음 정산부터 반영).</summary>
        void SetOvernightMacro(int macroBp);
        /// <summary>휴먼 시장가 주문 큐 적재(side +1 매수 · −1 매도). 반환 = KrxArenaReject(접수 단계 · None = 적재됨).</summary>
        int SubmitHuman(long symbol, int side, long qty);
        /// <summary>최신 스냅샷 seqlock 사본(경합 재시도 상한 초과 = false).</summary>
        bool TryReadSnapshot(out DuelSnapshot snapshot);
        /// <summary>진영 보유 사본 — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
        int CopyPositions(int side, Span<DuelPosition> destination);
        /// <summary>최근 AI 근거 피드 사본(과거 → 최신) — 반환 = 복사 개수(경합 상한 초과 = 0).</summary>
        int CopyRationales(Span<AiTradeRationale> destination);
    }
}
