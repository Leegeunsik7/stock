// ============================================================================
// ZeroAllocJsonParser — v4.72.0: 무할당 JSON 파서 (TickPayload 특화)
//
//  WebSocket 스트림은 메시지가 여러 프레임으로 쪼개져 도착할 수 있음.
//  두 단편을 조립할 버퍼 전략:
//   · 합산 크기 ≤ 4096 → stackalloc byte[StackThreshold] (스택, 힙 할당 0)
//   · 합산 크기 > 4096 → ArrayPool<byte>.Shared.Rent() (풀, finally Return)
//
//  StackThreshold 는 컴파일 타임 상수 — 가변 크기 stackalloc 이 아니므로
//  스택 오버플로우(SOE) 위험 없음.
//
//  파싱: Utf8JsonReader on ReadOnlySpan<byte> — 문자열 힙 할당 0.
//
//  지원 JSON 형식 (compact tick):
//   {"s":5930,"p":50000.0,"v":12345,"r":-0.52,"t":1719456789000000000}
//   s = SymbolCode(long)  p = Price(double)  v = Volume(long)
//   r = ChangeRate(double) t = TimestampTicks(long)
// ============================================================================
using System.Buffers;
using System.Text.Json;

namespace StockManager.Common
{
    public static class ZeroAllocJsonParser
    {
        private const int StackThreshold = 4096;

        // ── 두 단편 조립 + 파싱 (WebSocket 분할 프레임 처리) ─────────────────
        public static bool TryParseTick(
            System.ReadOnlySpan<byte> fragment,
            System.ReadOnlySpan<byte> continuation,
            out TickPayload tick)
        {
            tick = default;
            int total = fragment.Length + continuation.Length;
            if (total == 0) return false;

            if (total <= StackThreshold)
            {
                // 스택 경로: 컴파일 타임 상수 크기 → SOE 위험 없음
                System.Span<byte> buf = stackalloc byte[StackThreshold];
                fragment.CopyTo(buf);
                continuation.CopyTo(buf[fragment.Length..]);
                return ParseCore(buf[..total], out tick);
            }
            else
            {
                // ArrayPool 경로: 힙에서 빌려 파싱 후 반납
                byte[] rented = ArrayPool<byte>.Shared.Rent(total);
                try
                {
                    fragment.CopyTo(rented);
                    continuation.CopyTo(rented.AsSpan(fragment.Length));
                    return ParseCore(rented.AsSpan(0, total), out tick);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
                }
            }
        }

        // ── 단일 연속 슬라이스 파싱 (조립 불필요 경우) ───────────────────────
        public static bool TryParseTick(System.ReadOnlySpan<byte> json, out TickPayload tick)
            => TryParseTick(json, System.ReadOnlySpan<byte>.Empty, out tick);

        // ── 핵심 파싱 로직 ────────────────────────────────────────────────────
        private static bool ParseCore(System.ReadOnlySpan<byte> json, out TickPayload tick)
        {
            tick = default;
            var reader = new Utf8JsonReader(json);

            long   symbolCode = 0;
            double price      = 0;
            long   volume     = 0;
            double changeRate = 0;
            long   ts         = 0;

            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;

                if      (reader.ValueTextEquals("s"u8)) { reader.Read(); symbolCode = reader.GetInt64(); }
                else if (reader.ValueTextEquals("p"u8)) { reader.Read(); price      = reader.GetDouble(); }
                else if (reader.ValueTextEquals("v"u8)) { reader.Read(); volume     = reader.GetInt64(); }
                else if (reader.ValueTextEquals("r"u8)) { reader.Read(); changeRate = reader.GetDouble(); }
                else if (reader.ValueTextEquals("t"u8)) { reader.Read(); ts         = reader.GetInt64(); }
            }

            // KSE 6자리 범위 외 코드 또는 가격 이상치 → 거부
            if (symbolCode is <= 0 or > 999_999) return false;
            if (price <= 0) return false;

            tick = new TickPayload
            {
                SymbolCode     = symbolCode,
                Price          = price,
                Volume         = volume,
                ChangeRate     = changeRate,
                TimestampTicks = ts,
            };
            return true;
        }
    }
}
