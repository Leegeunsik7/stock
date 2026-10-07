// ============================================================================
// KisOrderBookParser — [교차 피드 · 추천 A] KIS 실시간 주식호가(H0STASP0 · 10호가) 무할당(δ=0) 판독기
//
//  필드 순서(공식 공개 예제 koreainvestment/open-trading-api `examples_llm/domestic_stock/asking_price_krx` 컬럼 정의와 대조 · 2026-10-08):
//    0 MKSC_SHRN_ISCD(종목) · 1 BSOP_HOUR · 2 HOUR_CLS_CODE · 3~12 ASKP1~10 · 13~22 BIDP1~10 · 23~32 ASKP_RSQN1~10 · 33~42 BIDP_RSQN1~10 ·
//    43 TOTAL_ASKP_RSQN · 44 TOTAL_BIDP_RSQN · 45~62 시간외 총잔량 · 예상체결 · 누적거래량 · 증감 · 거래구분 · 중간가 · 시장구분(현행 63필드).
//    실전/모의 같은 TR ID. ★한계(정직): 이 저장소에는 VTS 실수신 프레임 캡처가 없다 — 대조 대상은 '공식 예제 컬럼 정의'이지 실패킷이 아니다.
//  ★[실결함 교정] HOUR_CLS_CODE ≠ '0'(장전/장후 예상 · 9시 이후 예상/VI · 시간외 단일가 예상) 호가는 체결 가능한 연속매매 호가가 아닌
//    '예상' 호가다 — 수정 전엔 구분 없이 시그널 엔진으로 들어가 예상 잔량이 불균형/교차 판정에 섞였다. 이제 판독은 하되 HourClass 로 노출 →
//    어댑터가 연속매매('0')만 피드(나머지 = 무시 계수 · 형식 이탈 아님).
//  구조 검증(형식 이탈 = 거부 + 사유 코드): 잘림(필드 &lt; 45) · 종목코드 6자리 숫자 아님 · 숫자 아님(알파벳 섞임) · 음수 · 최우선 매도/매수 결측 ·
//    매도호가 오름차순/매수호가 내림차순 위반 · 결측 단계 뒤 값(구멍) · 가격 없는 단계에 잔량 · 최우선 교차(매도 ≤ 매수 — 연속매매에서 불가).
//  δ=0: ReadOnlySpan 인덱스 스캔 + Utf8Parser 만.
// ============================================================================
using System;
using System.Buffers.Text;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum KisBookReject : byte
    {
        None = 0, Truncated = 1, BadCode = 2, NonNumeric = 3, Negative = 4, MissingTop = 5, NonMonotonic = 6, LevelGap = 7, CrossedTop = 8, BadHourClass = 9,
        FrameHeader = 10,   // 어댑터 판정: 암호 플래그 · 다건(H0STASP0 은 평문 1레코드)
    }

    public static class KisOrderBookParser
    {
        public const int MinFields = 45;                                          // 0..44(코드 · 시간 · 구분 · 가격 20 · 잔량 20 · 총잔량 2)
        public const byte ContinuousSession = (byte)'0';                          // HOUR_CLS_CODE 장중(연속매매)
        private static ReadOnlySpan<byte> TrBook => "H0STASP0"u8;

        public static bool IsBookTr(ReadOnlySpan<byte> trId) => trId.SequenceEqual(TrBook);

        /// <summary>호환 오버로드 — 연속매매 여부 무관 판독(사유 폐기).</summary>
        public static bool TryParse(ReadOnlySpan<byte> body, long timestampTicks, out OrderBookPayload book)
            => TryParse(body, timestampTicks, out book, out _, out _, out _, out _);

        /// <summary>
        /// H0STASP0 본문 1레코드 판독(δ=0). 반환 = 구조 정합. hourClass = HOUR_CLS_CODE 1바이트('0' = 연속매매).
        /// totalAsk/totalBid = 43/44 총잔량(진단 · 판정 미사용 — 10단계 합과의 관계는 공식 문서 미명시).
        /// </summary>
        public static bool TryParse(ReadOnlySpan<byte> body, long timestampTicks, out OrderBookPayload book, out KisBookReject reject,
                                    out byte hourClass, out long totalAsk, out long totalBid)
        {
            book = default; reject = KisBookReject.None; hourClass = 0; totalAsk = 0; totalBid = 0;
            int f = 0;
            while (true)
            {
                int caret = body.IndexOf((byte)'^');
                var field = caret < 0 ? body : body[..caret];
                if (f == 0) { if (!ParseCode(field, out long code)) { reject = KisBookReject.BadCode; return false; } book.SymbolCode = code; }
                else if (f == 2)
                {
                    if (field.Length != 1 || !((field[0] >= (byte)'0' && field[0] <= (byte)'9') || (field[0] >= (byte)'A' && field[0] <= (byte)'Z')))
                    { reject = KisBookReject.BadHourClass; return false; }
                    hourClass = field[0];
                }
                else if (f >= 3 && f <= 44)
                {
                    var r = ParseNonNegative(field, out long v);
                    if (r != KisBookReject.None) { reject = r; return false; }
                    int k = f - 3;
                    if (k < 10) book.AskPrice[k] = v;
                    else if (k < 20) book.BidPrice[k - 10] = v;
                    else if (k < 30) book.AskQty[k - 20] = v;
                    else if (k < 40) book.BidQty[k - 30] = v;
                    else if (k == 40) totalAsk = v;
                    else totalBid = v;
                }
                f++;
                if (caret < 0 || f >= MinFields) break;
                body = body[(caret + 1)..];
            }
            if (f < MinFields) { reject = KisBookReject.Truncated; return false; }
            if (book.AskPrice[0] <= 0 || book.BidPrice[0] <= 0) { reject = KisBookReject.MissingTop; return false; }
            var s = CheckSide(ref book, ask: true);
            if (s == KisBookReject.None) s = CheckSide(ref book, ask: false);
            if (s != KisBookReject.None) { reject = s; return false; }
            if (hourClass == ContinuousSession && book.AskPrice[0] <= book.BidPrice[0]) { reject = KisBookReject.CrossedTop; return false; }
            book.TimestampTicks = timestampTicks;
            return true;
        }

        // 매도 = 오름차순 · 매수 = 내림차순(결측 0 은 뒤쪽에만) · 가격 없는 단계에 잔량 = 구멍
        private static KisBookReject CheckSide(ref OrderBookPayload b, bool ask)
        {
            bool ended = false;
            long prev = 0;
            for (int i = 0; i < 10; i++)
            {
                long px = ask ? b.AskPrice[i] : b.BidPrice[i], qty = ask ? b.AskQty[i] : b.BidQty[i];
                if (px == 0) { ended = true; if (qty != 0) return KisBookReject.LevelGap; continue; }
                if (ended) return KisBookReject.LevelGap;
                if (i > 0 && (ask ? px <= prev : px >= prev)) return KisBookReject.NonMonotonic;
                prev = px;
            }
            return KisBookReject.None;
        }

        private static KisBookReject ParseNonNegative(ReadOnlySpan<byte> s, out long v)
        {
            v = 0;
            if (s.IsEmpty) return KisBookReject.None;                             // 빈 필드 = 0(해당 단계 없음)
            if (s[0] == (byte)'-') return KisBookReject.Negative;
            for (int i = 0; i < s.Length; i++) if (s[i] < (byte)'0' || s[i] > (byte)'9') return KisBookReject.NonNumeric;
            return Utf8Parser.TryParse(s, out v, out int used) && used == s.Length ? KisBookReject.None : KisBookReject.NonNumeric;   // 자릿수 초과 = 형식 이탈
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
