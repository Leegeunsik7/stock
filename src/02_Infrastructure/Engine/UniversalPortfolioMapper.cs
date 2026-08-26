// ============================================================================
// UniversalPortfolioMapper — v10.2.0: 스키마 독립적 무할당(δ=0) 포트폴리오 매퍼
//
//   증권사별로 상이한 REST JSON 스키마를 BrokerSchema(필드명 맵)로 흡수해, Utf8JsonReader + ReadOnlySpan<byte> 로
//   힙 할당 없이 브로커 무관 PortfolioSummary + TossHolding[] 로 매핑한다. 숫자는 ★TossJsonParser.TryReadLong(토큰 타입
//   안전·문자열/소수 숫자도 δ=0 파싱·throw 0)로 판독 — 증권사 API 의 숫자-as-문자열/소수 관례에도 fail-closed 사수.
//
//   ★불변: δ=0(Utf8JsonReader ref struct·byte[] 필드명 span 비교·caller span — 힙 0) · fail-closed(파싱 예외 → false) ·
//     결정론(순수 바이트 파싱).
// ============================================================================
using System;
using System.Text.Json;

namespace StockManager.Common
{
    public static class UniversalPortfolioMapper
    {
        /// <summary>
        /// 증권사 JSON(UTF-8) 을 스키마로 δ=0 매핑. 성공 시 summary 채우고 holdings 에 최대 dest 용량 기입(반환=개수).
        /// 파싱 예외/구조 불일치 → false(fail-closed).
        /// </summary>
        public static bool TryMap(ReadOnlySpan<byte> json, BrokerSchema schema, long nowMicros,
                                  out PortfolioSummary summary, Span<TossHolding> holdings, out int holdingCount)
        {
            summary = default;
            holdingCount = 0;
            long totalEval = 0, totalPnl = 0, deposit = 0, buyingPower = 0;
            int written = 0;

            try
            {
                var reader = new Utf8JsonReader(json);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;

                    if (reader.ValueTextEquals(schema.AccountObj))
                    {
                        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName) continue;
                            if      (reader.ValueTextEquals(schema.TotalEval))   { reader.Read(); TossJsonParser.TryReadLong(ref reader, out totalEval); }
                            else if (reader.ValueTextEquals(schema.TotalPnl))    { reader.Read(); TossJsonParser.TryReadLong(ref reader, out totalPnl); }
                            else if (reader.ValueTextEquals(schema.Deposit))     { reader.Read(); TossJsonParser.TryReadLong(ref reader, out deposit); }
                            else if (reader.ValueTextEquals(schema.BuyingPower)) { reader.Read(); TossJsonParser.TryReadLong(ref reader, out buyingPower); }
                            else reader.Skip();
                        }
                    }
                    else if (reader.ValueTextEquals(schema.HoldingsArr))
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
                                if      (reader.ValueTextEquals(schema.Symbol))   { reader.Read(); symHash = FnvSpan(reader.ValueSpan); }
                                else if (reader.ValueTextEquals(schema.Qty))      { reader.Read(); TossJsonParser.TryReadLong(ref reader, out qty); }
                                else if (reader.ValueTextEquals(schema.AvgPrice)) { reader.Read(); TossJsonParser.TryReadLong(ref reader, out avg); }
                                else if (reader.ValueTextEquals(schema.CurPrice)) { reader.Read(); TossJsonParser.TryReadLong(ref reader, out cur); }
                                else if (reader.ValueTextEquals(schema.Pnl))      { reader.Read(); TossJsonParser.TryReadLong(ref reader, out pnl); }
                                else if (reader.ValueTextEquals(schema.Overseas)) { reader.Read(); overseas = OverseasTruthy(ref reader); }
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
                return false;   // ★fail-closed
            }

            holdingCount = written;
            summary = new PortfolioSummary(schema.Broker, totalEval, totalPnl, deposit, buyingPower, written, nowMicros);
            return true;
        }

        // 해외여부: bool true, 또는 "Y"/"1"/"true" 문자열(증권사별 상이)도 수용.
        private static bool OverseasTruthy(ref Utf8JsonReader reader)
        {
            if (reader.TokenType == JsonTokenType.True) return true;
            if (reader.TokenType == JsonTokenType.String)
            {
                ReadOnlySpan<byte> v = reader.ValueSpan;
                return v.Length == 1 ? (v[0] == (byte)'Y' || v[0] == (byte)'1') : v.SequenceEqual("true"u8);
            }
            if (reader.TokenType == JsonTokenType.Number) { return reader.TryGetInt64(out long n) && n != 0; }
            return false;
        }

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
