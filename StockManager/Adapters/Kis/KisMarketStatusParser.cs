// ============================================================================
// KisMarketStatusParser — [v12.18.50-DEV] KIS 실시간 국내주식 장운영정보(KRX · H0STMKO0) 무할당(δ=0) 판독기
//
//  필드 순서(공식 공개 예제 koreainvestment/open-trading-api `examples_user/domestic_stock/domestic_stock_functions_ws.py`
//   market_status_krx 컬럼 정의와 대조 · 2026-10-21 · 레코드당 11필드):
//    0 MKSC_SHRN_ISCD(종목) · 1 TRHT_YN(거래정지여부) · 2 TR_SUSP_REAS_CNTT · 3 MKOP_CLS_CODE · 4 ANTC_MKOP_CLS_CODE · 5 MRKT_TRTM_CLS_CODE ·
//    6 DIVI_APP_CLS_CODE · 7 ISCD_STAT_CLS_CODE · 8 VI_CLS_CODE(VI적용구분코드) · 9 OVTM_VI_CLS_CODE · 10 EXCH_CLS_CODE.
//   예제 설명: "연결된 종목의 VI 발동 시와 VI 해제 시에 데이터가 수신됩니다." — 정상 상태 종목은 프레임이 오지 않을 수 있다.
//  ★한계(정직): 코드 '값'의 공식 정의는 이 저장소에서 확인하지 못했다(포털 상세 문서 미판독 · VTS 실수신 캡처 없음). 그래서 사상은 fail-closed:
//    TRHT_YN 'Y' = 정지 · 'N' = 미정지 · 그 외 = Unknown(차단).
//    VI_CLS_CODE 'N' = VI 없음/해제(유일한 정상 값) · '1' = 정적 · '2' = 동적 · '3' = 정적+동적(정적으로 표기) · 그 외/빈 값 = Unknown(차단).
//    → 사상이 실제와 다르면 결과는 '영구 차단'(발주 0) 쪽으로만 틀린다 — 실제 VI 코드를 정상으로 오판하는 경로는 'N' 단 하나.
//  구조 검증(형식 이탈 = 거부): 레코드 필드 < 11 · 종목코드 6자리 숫자 아님. 다건(count) 프레임은 레코드를 순서대로 판독.
// ============================================================================
using System;
using StockManager.Engines.Strategy;

namespace StockManager.Adapters.Kis
{
    public enum KisMarketStatusReject : byte { None = 0, Truncated = 1, BadCode = 2, FrameHeader = 3 }

    public static class KisMarketStatusParser
    {
        public const int FieldsPerRecord = 11;
        private static ReadOnlySpan<byte> TrMarketStatus => "H0STMKO0"u8;

        public static bool IsMarketStatusTr(ReadOnlySpan<byte> trId) => trId.SequenceEqual(TrMarketStatus);

        /// <summary>정지 여부 1바이트 + VI 적용 구분 → 거래 상태(순수 · δ=0 · 미분류 = Unknown).</summary>
        public static MarketTradingStatus MapStatus(ReadOnlySpan<byte> trhtYn, ReadOnlySpan<byte> viClsCode)
        {
            trhtYn = Trim(trhtYn); viClsCode = Trim(viClsCode);
            if (trhtYn.Length != 1) return MarketTradingStatus.Unknown;
            if (trhtYn[0] == (byte)'Y') return MarketTradingStatus.Halted;
            if (trhtYn[0] != (byte)'N') return MarketTradingStatus.Unknown;
            if (viClsCode.Length != 1) return MarketTradingStatus.Unknown;
            return viClsCode[0] switch
            {
                (byte)'N' => MarketTradingStatus.Normal,
                (byte)'1' => MarketTradingStatus.StaticVi,
                (byte)'2' => MarketTradingStatus.DynamicVi,
                (byte)'3' => MarketTradingStatus.StaticVi,
                _ => MarketTradingStatus.Unknown,
            };
        }

        /// <summary>
        /// 본문에서 레코드 1개 판독(δ=0). rest = 다음 레코드 시작(마지막이면 빈 span). 반환 = 구조 정합.
        /// </summary>
        public static bool TryReadRecord(ref ReadOnlySpan<byte> body, out long symbolCode, out MarketTradingStatus status, out KisMarketStatusReject reject)
        {
            symbolCode = 0; status = MarketTradingStatus.Unknown; reject = KisMarketStatusReject.None;
            ReadOnlySpan<byte> trht = default, vi = default;
            int f = 0;
            while (f < FieldsPerRecord)
            {
                int caret = body.IndexOf((byte)'^');
                var field = caret < 0 ? body : body[..caret];
                if (f == 0)
                {
                    if (!ParseCode(field, out symbolCode)) { reject = KisMarketStatusReject.BadCode; return false; }
                }
                else if (f == 1) trht = field;
                else if (f == 8) vi = field;
                f++;
                if (caret < 0) { body = default; break; }
                body = body[(caret + 1)..];
            }
            if (f < FieldsPerRecord) { reject = KisMarketStatusReject.Truncated; return false; }
            status = MapStatus(trht, vi);
            return true;
        }

        private static bool ParseCode(ReadOnlySpan<byte> s, out long code)
        {
            code = 0;
            s = Trim(s);
            if (s.Length != 6) return false;
            for (int i = 0; i < 6; i++)
            {
                int d = s[i] - '0';
                if ((uint)d > 9) return false;
                code = code * 10 + d;
            }
            return code > 0;
        }

        private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> s)
        {
            while (!s.IsEmpty && s[0] == (byte)' ') s = s[1..];
            while (!s.IsEmpty && s[^1] == (byte)' ') s = s[..^1];
            return s;
        }
    }
}
