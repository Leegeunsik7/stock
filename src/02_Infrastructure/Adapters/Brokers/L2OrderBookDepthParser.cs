// ============================================================================
// L2OrderBookDepthParser — [L2 · 추천 A] 브리지 OrderBook 프레임(ReadOnlySpan<byte>) → 검증 · 정규화 L2Depth(δ=0)
//
//  입력: KiwoomProxy 가 키움 '주식호가잔량'(FID 41~80)을 담아 보낸 336B OrderBookPayload(가격 원 · 잔량 주 · 10호가).
//  출력: DepthBook/MicroLiquiditySlicer 공용 L2Depth(raw 10^8 · 상위 5호가 — 보호 지정가 깊이 가드는 1~3호가만 사용).
//
//  판정(L2BookVerdict · 첫 위반에서 종결 · 전부 정수 연산):
//    Ok           — 양측 최우선호가 존재 · 매도 오름차순/매수 내림차순 엄격 단조 · 교차 없음 · 상위 3호가 잔량 공백 없음
//    Empty        — 양측 최우선호가 모두 없음(장 시작 전·거래정지 — 데이터 없음, 이상 아님)
//    OneSided     — 한쪽 최우선호가만 존재(상·하한가 잠김 등) — 스프레드 산정 불가
//    Malformed    — 음수/길이 미달/가격 없는 칸 뒤에 다시 가격(사다리 구멍)
//    NonMonotonic — 같은 측 가격 역전/중복
//    Crossed      — 매수 1호가 ≥ 매도 1호가(교차/잠김 — 데이터 오염 또는 동시호가 예상가)
//    FlashHole    — 상위 3호가 안에 가격은 있는데 잔량 0 인 칸이 있고 그보다 깊은 호가엔 잔량 존재(호가 공백 · 플래시 크래시 징후)
//  spreadBps = (매도1 − 매수1) × 10⁴ / 중간가(Int128) — Ok 일 때만 유효(그 외 −1).
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS2E.Infrastructure
{
    public enum L2BookVerdict : byte { Ok = 0, Empty = 1, OneSided = 2, Malformed = 3, NonMonotonic = 4, Crossed = 5, FlashHole = 6 }

    public static class L2OrderBookDepthParser
    {
        public const int Levels = 10, StoredLevels = 5, GuardLevels = 3;
        private const long Scale = 100_000_000L;
        private static readonly long MaxWon = long.MaxValue / Scale;   // raw 변환 포화 한계

        /// <summary>프레임 페이로드 판독 + 검증(δ=0). 길이 미달 → Malformed.</summary>
        public static L2BookVerdict TryParse(ReadOnlySpan<byte> payload, out OrderBookPayload book, out L2Depth depth, out long spreadBps)
        {
            depth = default; spreadBps = -1;
            if (payload.Length < Unsafe.SizeOf<OrderBookPayload>()) { book = default; return L2BookVerdict.Malformed; }
            book = MemoryMarshal.Read<OrderBookPayload>(payload);
            return Validate(in book, out depth, out spreadBps);
        }

        /// <summary>스냅샷 검증 + raw L2Depth 정규화(δ=0).</summary>
        public static L2BookVerdict Validate(in OrderBookPayload book, out L2Depth depth, out long spreadBps)
        {
            depth = default; spreadBps = -1;
            if (book.SymbolCode <= 0 || book.SymbolCode > 999_999) return L2BookVerdict.Malformed;
            var v = Side(book.AskPrice, book.AskQty, ascending: true);
            if (v != L2BookVerdict.Ok && v != L2BookVerdict.Empty) return v;
            var vb = Side(book.BidPrice, book.BidQty, ascending: false);
            if (vb != L2BookVerdict.Ok && vb != L2BookVerdict.Empty) return vb;
            long ask1 = book.AskPrice[0], bid1 = book.BidPrice[0];
            if (ask1 == 0 && bid1 == 0) return L2BookVerdict.Empty;
            if (ask1 == 0 || bid1 == 0) return L2BookVerdict.OneSided;
            if (bid1 >= ask1) return L2BookVerdict.Crossed;
            if (FlashHole(book.AskPrice, book.AskQty) || FlashHole(book.BidPrice, book.BidQty)) return L2BookVerdict.FlashHole;

            Int128 mid2 = (Int128)ask1 + bid1;                                 // 2 × 중간가
            spreadBps = (long)(((Int128)(ask1 - bid1) * 20_000) / mid2);
            depth.SymbolCode = book.SymbolCode;
            depth.TimestampTicks = book.TimestampTicks;
            for (int i = 0; i < StoredLevels; i++)
            {
                depth.AskPriceRaw[i] = ToRaw(book.AskPrice[i]); depth.AskQtyRaw[i] = ToRaw(book.AskQty[i]);
                depth.BidPriceRaw[i] = ToRaw(book.BidPrice[i]); depth.BidQtyRaw[i] = ToRaw(book.BidQty[i]);
            }
            return L2BookVerdict.Ok;
        }

        private static long ToRaw(long v) => v <= 0 ? 0 : v >= MaxWon ? long.MaxValue : v * Scale;

        // 한쪽 사다리: 음수 → Malformed · 가격 0 칸 이후 가격 재등장 → Malformed(구멍) · 단조 위반 → NonMonotonic
        private static L2BookVerdict Side(in OrderBookLevels10 px, in OrderBookLevels10 qty, bool ascending)
        {
            bool ended = false;
            long prev = 0;
            for (int i = 0; i < Levels; i++)
            {
                long p = px[i], q = qty[i];
                if (p < 0 || q < 0) return L2BookVerdict.Malformed;
                if (p == 0) { ended = true; continue; }
                if (ended) return L2BookVerdict.Malformed;
                if (i > 0 && (ascending ? p <= prev : p >= prev)) return L2BookVerdict.NonMonotonic;
                prev = p;
            }
            return px[0] == 0 ? L2BookVerdict.Empty : L2BookVerdict.Ok;
        }

        // 상위 3호가 안의 잔량 0 칸 + 더 깊은 칸의 잔량 존재 = 호가 공백
        private static bool FlashHole(in OrderBookLevels10 px, in OrderBookLevels10 qty)
        {
            for (int i = 0; i < GuardLevels; i++)
            {
                if (px[i] == 0) return false;
                if (qty[i] != 0) continue;
                for (int j = i + 1; j < Levels && px[j] != 0; j++)
                    if (qty[j] > 0) return true;
            }
            return false;
        }
    }
}
