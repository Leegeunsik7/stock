// ============================================================================
// TossJsonParser — v10.2.0: 무할당(δ=0) 스트리밍 JSON 파서 (토스 포트폴리오 응답)
//
//   JsonSerializer(리플렉션·string 할당) 대신 Utf8JsonReader + ReadOnlySpan<byte> 로 토스 계좌/보유 응답을 δ=0 파싱한다.
//   숫자는 GetInt64 로 직접 판독, 종목코드 문자열은 ValueSpan(UTF-8 바이트) 위에서 FNV 해시(string 미생성). 결과는
//   호출자의 Span<TossHolding> 에 직접 기입 → 초당 수천 회전에도 힙 0.
//
//   ★기대 스키마(실 토스 스펙에 맞춰 프로퍼티명 조정 가능):
//     {"account":{"totalEval":L,"totalPnl":L,"deposit":L,"buyingPower":L},
//      "holdings":[{"symbol":"005930","qty":L,"avgPrice":L,"curPrice":L,"pnl":L,"overseas":false}, ...]}
//
//   ★불변: δ=0(Utf8JsonReader ref struct·u8 리터럴 비교·GetInt64·FNV·caller span — 힙 0) · fail-closed(파싱 예외 → false,
//     부분 결과 미신뢰) · 결정론(순수 바이트 파싱).
// ============================================================================
using System;
using System.Buffers.Text;
using System.Text.Json;

namespace StockManager.Common
{
    public static class TossJsonParser
    {
        /// <summary>
        /// 토스 계좌+보유 JSON(UTF-8) 을 δ=0 파싱. 성공 시 account 채우고 holdings 에 최대 dest 용량만큼 기입(반환=기입 개수).
        /// 파싱 예외/구조 불일치 → false(fail-closed·account 미신뢰). holdings 초과분은 절단(호출자 충분한 span 제공 전제).
        /// </summary>
        public static bool TryParseAccount(ReadOnlySpan<byte> json, long nowMicros,
                                           out TossAccountSnapshot account, Span<TossHolding> holdings, out int holdingCount)
        {
            account = default;
            holdingCount = 0;
            long totalEval = 0, totalPnl = 0, deposit = 0, buyingPower = 0;
            int written = 0;

            try
            {
                var reader = new Utf8JsonReader(json);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;

                    if (reader.ValueTextEquals("account"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) continue;
                            if      (reader.ValueTextEquals("totalEval"u8))   { reader.Read(); TryReadLong(ref reader, out totalEval); }
                            else if (reader.ValueTextEquals("totalPnl"u8))    { reader.Read(); TryReadLong(ref reader, out totalPnl); }
                            else if (reader.ValueTextEquals("deposit"u8))     { reader.Read(); TryReadLong(ref reader, out deposit); }
                            else if (reader.ValueTextEquals("buyingPower"u8)) { reader.Read(); TryReadLong(ref reader, out buyingPower); }
                            else reader.Skip();
                        }
                    }
                    else if (reader.ValueTextEquals("holdings"u8))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray) return false;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject) continue;
                            long symHash = 0, qty = 0, avg = 0, cur = 0, pnl = 0;
                            bool overseas = false;
                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                                if      (reader.ValueTextEquals("symbol"u8))   { reader.Read(); symHash = FnvSpan(reader.ValueSpan); }
                                else if (reader.ValueTextEquals("qty"u8))      { reader.Read(); TryReadLong(ref reader, out qty); }
                                else if (reader.ValueTextEquals("avgPrice"u8)) { reader.Read(); TryReadLong(ref reader, out avg); }
                                else if (reader.ValueTextEquals("curPrice"u8)) { reader.Read(); TryReadLong(ref reader, out cur); }
                                else if (reader.ValueTextEquals("pnl"u8))      { reader.Read(); TryReadLong(ref reader, out pnl); }
                                else if (reader.ValueTextEquals("overseas"u8)) { reader.Read(); overseas = reader.TokenType == JsonTokenType.True; }
                                else reader.Skip();
                            }
                            if (written < holdings.Length)
                                holdings[written++] = new TossHolding(symHash, qty, avg, cur, pnl, overseas);
                        }
                    }
                    else reader.Skip();
                }
            }
            catch (JsonException)
            {
                return false;   // ★fail-closed: 파싱 실패 → 부분 결과 폐기
            }

            holdingCount = written;
            account = new TossAccountSnapshot(totalEval, totalPnl, deposit, buyingPower, written, nowMicros);
            return true;
        }

        /// <summary>
        /// ★토큰 타입 안전 long 판독(δ=0·throw 0·fail-closed). Number(정수)→TryGetInt64, Number(소수/범위초과)→TryGetDouble
        /// 절단, String("12345" — 한국 증권사 REST 관례)→Utf8Parser, 그 외(null/bool)→0. GetInt64 직접 호출의 비-Number
        /// 예외(InvalidOperationException/FormatException — catch(JsonException) 회피)로 인한 fail-closed 붕괴를 봉쇄.
        /// </summary>
        internal static bool TryReadLong(ref Utf8JsonReader reader, out long v)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.Number:
                    if (reader.TryGetInt64(out v)) return true;
                    if (reader.TryGetDouble(out double d)) { v = (long)d; return true; }   // 소수/범위초과 → 절단
                    v = 0L; return false;
                case JsonTokenType.String:
                    return Utf8Parser.TryParse(reader.ValueSpan, out v, out _);            // 숫자-as-string
                default:
                    v = 0L; return false;                                                  // null/bool/기타 → 0(무시)
            }
        }

        // FNV-1a-64 over UTF-8 bytes(종목코드 string 무할당 해시). ★이스케이프 미해제(숫자형 국내 종목코드는 무영향).
        private static long FnvSpan(ReadOnlySpan<byte> s)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
                return (long)h;
            }
        }
    }
}
