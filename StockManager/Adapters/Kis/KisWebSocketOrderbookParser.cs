// ============================================================================
// KisWebSocketOrderbookParser — [v12.18.16-DEV] KIS 실시간 WebSocket 시세 전문 프레임 판독기(H0STASP0 호가 · H0STCNT0 체결 · δ=0)
//
//  프레임 "암호(0/1)|TR|건수|본문"(^ 구분) → 헤더 = KisProtocol.TryParseHeader · 호가 본문 = KisOrderBookParser(구조 검증 9종 그대로) ·
//  체결 본문 = KisProtocol.ParseTicks(0 코드 · 1 시각 · 2 현재가 · 12 체결량 · 13 누적량 · 21 CCLD_DVSN 매수/매도) — 필드 규칙은 한 곳(재구현 0).
//  이 판독기가 더하는 것: 전문 시각(호가 = 1 BSOP_HOUR · 체결 = 1 STCK_CNTG_HOUR · HHmmss KST) 추출/검증 → KisTimeSkewGuard 입력.
//  ★정직(한계): 필드 순서는 KIS 공개 예제 컬럼 정의 기준 — 이 저장소에 실 수신 캡처는 없다. 전문 시각 해상도 = 1초(밀리초 필드 없음).
//  δ=0: ReadOnlySpan 인덱스 스캔 + Utf8Parser · out 구조체 · 호출자 Span 버퍼. 문자열 분할/생성 0.
// ============================================================================
using System;
using System.Buffers.Text;
using MS2E.Infrastructure;
using StockManager.Adapters;

namespace StockManager.Adapters.Kis
{
    /// <summary>프레임 판독 결과 종류.</summary>
    public enum KisWsFrameKind : byte { Malformed = 0, Book = 1, Trades = 2, Other = 3, Encrypted = 4 }

    public static class KisWebSocketOrderbookParser
    {
        /// <summary>HHmmss(정수) → 자정 이후 초. 시 0~23 · 분/초 0~59 만 유효.</summary>
        public static bool TryHhmmssToSeconds(long hhmmss, out int secondsOfDay)
        {
            secondsOfDay = 0;
            if (hhmmss < 0 || hhmmss > 235_959) return false;
            long hh = hhmmss / 10_000, mm = hhmmss / 100 % 100, ss = hhmmss % 100;
            if (mm > 59 || ss > 59) return false;
            secondsOfDay = (int)(hh * 3_600 + mm * 60 + ss);
            return true;
        }

        /// <summary>호가 본문 필드 1(BSOP_HOUR · 6자리 숫자) 판독. 형식 이탈 = false.</summary>
        public static bool TryReadBookTime(ReadOnlySpan<byte> body, out long hhmmss)
        {
            hhmmss = 0;
            int c0 = body.IndexOf((byte)'^');
            if (c0 < 0) return false;
            var rest = body[(c0 + 1)..];
            int c1 = rest.IndexOf((byte)'^');
            var field = c1 < 0 ? rest : rest[..c1];
            return field.Length == 6 && Utf8Parser.TryParse(field, out hhmmss, out int used) && used == 6 && TryHhmmssToSeconds(hhmmss, out _);
        }

        /// <summary>
        /// H0STASP0 본문 1레코드 → 10호가(δ=0). 구조 검증 = KisOrderBookParser · 전문 시각 형식 이탈 = 거부(BadHourClass 아님 — timeOk = false 로 구분).
        /// </summary>
        public static bool TryDecodeBook(ReadOnlySpan<byte> body, long timestampTicks, out OrderBookPayload book, out KisBookReject reject,
                                         out byte hourClass, out long hhmmss, out bool timeOk)
        {
            timeOk = TryReadBookTime(body, out hhmmss);
            return KisOrderBookParser.TryParse(body, timestampTicks, out book, out reject, out hourClass, out _, out _);
        }

        /// <summary>H0STCNT0 본문(다건) → dst(δ=0). 반환 = 판독 건수(형식 오류 레코드부터 폐기 — KisProtocol 규칙).</summary>
        public static int DecodeTrades(ReadOnlySpan<byte> body, int count, Span<KisTick> dst) => KisProtocol.ParseTicks(body, count, dst);

        /// <summary>
        /// 시세 프레임 1건 판독(δ=0). Book = book · bookTime 유효 · Trades = trades[0..tradeCount) · 그 외 = 판독 안 함.
        /// 호가 = 평문 1레코드만(암호/다건 = Malformed) · 체결 = 평문만(암호 = Encrypted).
        /// </summary>
        public static KisWsFrameKind Decode(ReadOnlySpan<byte> frame, long timestampTicks, out OrderBookPayload book, out long bookHhmmss,
                                            Span<KisTick> trades, out int tradeCount)
        {
            book = default; bookHhmmss = 0; tradeCount = 0;
            if (!KisProtocol.TryParseHeader(frame, out bool encrypted, out var trId, out int count, out var body)) return KisWsFrameKind.Malformed;
            if (KisOrderBookParser.IsBookTr(trId))
            {
                if (encrypted || count != 1) return KisWsFrameKind.Malformed;
                bool ok = TryDecodeBook(body, timestampTicks, out book, out _, out _, out bookHhmmss, out bool timeOk);
                return ok && timeOk ? KisWsFrameKind.Book : KisWsFrameKind.Malformed;
            }
            if (KisProtocol.IsTickTr(trId))
            {
                if (encrypted) return KisWsFrameKind.Encrypted;
                tradeCount = DecodeTrades(body, count, trades);
                return tradeCount > 0 ? KisWsFrameKind.Trades : KisWsFrameKind.Malformed;
            }
            return encrypted ? KisWsFrameKind.Encrypted : KisWsFrameKind.Other;
        }
    }
}
