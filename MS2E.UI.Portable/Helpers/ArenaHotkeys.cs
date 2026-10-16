// ============================================================================
// ArenaHotkeys — [v12.18.14-DEV] 아레나 프로 콘솔 핫키 · DOM 래더 디스패처(순수 RCL · 엔진 참조 0 · 디스패치 δ=0)
//
//   매핑 = KeyboardEvent.code 우선(자판 배열 무관 — 한글 IME 활성 시 key 는 'ㅠ/ㄴ/ㅊ' 가 되므로 code 'KeyB/KeyS/KeyC' 로 판정) · key 폴백.
//     Digit1~4 / Numpad1~4 = 비중 10 · 25 · 50 · 100% · KeyB = 최우선 매도호가 지정가 매수 · KeyS = 최우선 매수호가 지정가 매도 ·
//     Space = 비상 전량 청산 · KeyC = 미체결 전량 취소. 자동 반복(repeat) = 발주/청산/취소 무시(키 누름 유지로 큐 범람 0 · 비중 선택만 허용).
//   수량 = ⌊자본금 × 비중 ÷ 가격⌋(매도는 매도 가능 수량 상한). 래더 클릭 = 매수호가 열 → 그 가격 지정가 매수 · 매도호가 열 → 지정가 매도.
//   ★목적지 = IArenaPort(휴먼 가상 원장 큐)뿐 — 브로커/무장 경로 없음. 문자열 비교 · 정수 연산만(할당 0 · Blazor 이벤트 인자 생성은 프레임워크 몫).
// ============================================================================
using System;
using MS2E.UI.Portable.Ports.Trading;

namespace MS2E.UI.Portable.Helpers
{
    public static class ArenaHotkeys
    {
        public const int None = 0, Weight = 1, Buy = 2, Sell = 3, Flatten = 4, CancelAll = 5;
        public const int DefaultWeightBp = 1_000;

        public static string WeightLabel(int bp) => bp switch { 1_000 => "10%", 2_500 => "25%", 5_000 => "50%", 10_000 => "100%", _ => (bp / 100) + "%" };

        /// <summary>키 → 동작(비중이면 weightBp 설정). repeat = 비중 외 무시.</summary>
        public static int Classify(string? code, string? key, bool repeat, out int weightBp)
        {
            weightBp = 0;
            int action = code switch
            {
                "Digit1" or "Numpad1" => W(1_000, out weightBp),
                "Digit2" or "Numpad2" => W(2_500, out weightBp),
                "Digit3" or "Numpad3" => W(5_000, out weightBp),
                "Digit4" or "Numpad4" => W(10_000, out weightBp),
                "KeyB" => Buy,
                "KeyS" => Sell,
                "KeyC" => CancelAll,
                "Space" => Flatten,
                _ => key switch
                {
                    "1" => W(1_000, out weightBp), "2" => W(2_500, out weightBp), "3" => W(5_000, out weightBp), "4" => W(10_000, out weightBp),
                    "b" or "B" => Buy, "s" or "S" => Sell, "c" or "C" => CancelAll, " " or "Spacebar" => Flatten,
                    _ => None,
                },
            };
            return repeat && action != Weight ? None : action;
        }

        private static int W(int bp, out int weightBp) { weightBp = bp; return Weight; }

        /// <summary>⌊자본금 × 비중(bp) ÷ 가격⌋ (Int128 · 가격 ≤ 0 = 0).</summary>
        public static long QtyFor(long capitalWon, int weightBp, long priceWon)
            => priceWon <= 0 || capitalWon <= 0 || weightBp <= 0 ? 0 : (long)((Int128)capitalWon * weightBp / 10_000 / priceWon);

        /// <summary>
        /// 동작 실행(δ=0) — 반환 = ArenaReject(None = 큐 적재). Weight 는 호출자가 상태로 보관(여기선 None).
        /// Buy/Sell 은 래더 포커스 종목 최우선 반대 호가 지정가(즉시 시장성) · 수량 0 = Invalid/InsufficientPosition.
        /// </summary>
        public static int Execute(IArenaPort port, int action, in ArenaDepth depth, int weightBp, long capitalWon, out long qty)
        {
            qty = 0;
            switch (action)
            {
                case Buy:
                    if (!depth.HasBook || depth.BestAsk <= 0) return ArenaReject.NoQuote;
                    qty = QtyFor(capitalWon, weightBp, depth.BestAsk);
                    return qty <= 0 ? ArenaReject.Invalid : port.SubmitHumanLimit(depth.Symbol, 1, qty, depth.BestAsk);
                case Sell:
                    if (!depth.HasBook || depth.BestBid <= 0) return ArenaReject.NoQuote;
                    qty = Math.Min(depth.Sellable, QtyFor(capitalWon, weightBp, depth.BestBid));
                    return qty <= 0 ? ArenaReject.InsufficientPosition : port.SubmitHumanLimit(depth.Symbol, -1, qty, depth.BestBid);
                case Flatten:
                    return port.FlattenHuman();
                case CancelAll:
                    return port.CancelHuman();
                default:
                    return ArenaReject.None;
            }
        }

        /// <summary>래더 셀 클릭(δ=0): isAsk = 매도호가 열(지정가 매도 · 매도 가능 상한) · 아니면 매수호가 열(지정가 매수).</summary>
        public static int ClickLadder(IArenaPort port, in ArenaDepth depth, bool isAsk, int level, int weightBp, long capitalWon, out long qty)
        {
            qty = 0;
            if (!depth.HasBook || (uint)level >= ArenaDepth.Levels) return ArenaReject.NoQuote;
            long px = isAsk ? depth.AskPx[level] : depth.BidPx[level];
            if (px <= 0) return ArenaReject.NoQuote;
            qty = QtyFor(capitalWon, weightBp, px);
            if (isAsk) qty = Math.Min(qty, depth.Sellable);
            if (qty <= 0) return isAsk ? ArenaReject.InsufficientPosition : ArenaReject.Invalid;
            return port.SubmitHumanLimit(depth.Symbol, isAsk ? -1 : 1, qty, px);
        }
    }
}
