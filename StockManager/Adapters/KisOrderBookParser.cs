// ============================================================================
// KisOrderBookParser — [교차 피드 · 추천 A] KIS 실시간 주식호가(H0STASP0 · 10호가) 무할당(δ=0) 판독기
//
//  ★출처·한계(정직): 필드 순서는 KIS Developers 공개 문서(국내주식 실시간호가 KRX)의 알려진 형식 — 0 종목코드 · 1 영업시간(HHMMSS) ·
//    2 시간구분 · 3~12 매도호가1~10 · 13~22 매수호가1~10 · 23~32 매도잔량1~10 · 33~42 매수잔량1~10 · 43 총매도잔량 · 44 총매수잔량 · …(이후 예상체결 등).
//    실서버 프레임 대조 이력 없음 — 모의(VTS) 도메인에서 1건씩 대조 필수. 형식 이탈 = false(fail-closed · 추측 채움 0).
//  ★정직(교차 피드의 의미): 키움과 KIS 는 같은 KRX 호가창을 중계한다 — 두 피드의 호가 차이는 '차익'이 아니라 전송 지연/노후화이며,
//    같은 순간 두 피드가 서로 꼬이면(한쪽 매도가 < 다른 쪽 매수가) 한쪽이 낡았거나 오염됐다는 증거다(QuantSignalProvider 교차 검증).
//  판독: 본문 '^' 구분 1레코드 → OrderBookPayload(원 · 주). 호가 0 = 해당 단계 없음 · 음수/숫자 아님 = 거부 · 최우선 매도/매수 결측 = 거부.
//  δ=0: ReadOnlySpan 인덱스 스캔 + Utf8Parser 만.
// ============================================================================
using System;
using System.Buffers.Text;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public static class KisOrderBookParser
    {
        public const int MinFields = 43;                                          // 0..42(코드 · 시간 · 구분 · 가격 20 · 잔량 20)
        private static ReadOnlySpan<byte> TrBook => "H0STASP0"u8;

        public static bool IsBookTr(ReadOnlySpan<byte> trId) => trId.SequenceEqual(TrBook);

        /// <summary>H0STASP0 본문 1레코드 판독(δ=0). 반환 = 형식 정합 + 최우선 매도/매수 존재.</summary>
        public static bool TryParse(ReadOnlySpan<byte> body, long timestampTicks, out OrderBookPayload book)
        {
            book = default;
            int f = 0;
            while (true)
            {
                int caret = body.IndexOf((byte)'^');
                var field = caret < 0 ? body : body[..caret];
                if (f == 0) { if (!ParseCode(field, out long code)) return false; book.SymbolCode = code; }
                else if (f >= 3 && f <= 42)
                {
                    if (!ParseNonNegative(field, out long v)) return false;
                    int k = f - 3;
                    if (k < 10) book.AskPrice[k] = v;
                    else if (k < 20) book.BidPrice[k - 10] = v;
                    else if (k < 30) book.AskQty[k - 20] = v;
                    else book.BidQty[k - 30] = v;
                }
                f++;
                if (caret < 0 || f > MinFields) break;
                body = body[(caret + 1)..];
            }
            if (f < MinFields) return false;                                       // 필드 부족(잘린 프레임)
            if (book.AskPrice[0] <= 0 || book.BidPrice[0] <= 0) return false;
            book.TimestampTicks = timestampTicks;
            return true;
        }

        private static bool ParseNonNegative(ReadOnlySpan<byte> s, out long v)
        {
            v = 0;
            if (s.IsEmpty) return true;                                           // 빈 필드 = 0(해당 단계 없음)
            return Utf8Parser.TryParse(s, out v, out int used) && used == s.Length && v >= 0;
        }

        private static bool ParseCode(ReadOnlySpan<byte> s, out long code)
        {
            code = 0;
            if (s.Length != 6) return false;
            for (int i = 0; i < 6; i++) { byte c = s[i]; if (c < (byte)'0' || c > (byte)'9') return false; code = code * 10 + (c - (byte)'0'); }
            return code > 0;
        }
    }
}
