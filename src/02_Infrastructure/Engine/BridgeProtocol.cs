// ============================================================================
// BridgeProtocol — S14 #1 키움 COM 브릿지 통신 프로토콜
//
//  KiwoomProxyHost (x86) ↔ KiwoomBridgeClient (64-bit) 사이의
//  NamedPipe 통신에 사용되는 Blittable 구조체 및 직렬화 헬퍼.
//
//  와이어 포맷: [Header 8B: WireType(4)+PayloadLen(4)] [Payload NB]
//  방향:
//   ProxyHost → BridgeClient : WireType 0-5 (시세·체결·잔고·하트비트)
//   BridgeClient → ProxyHost : WireType 10  (OrderRequest)
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS2E.Infrastructure
{
    // ── 와이어 메시지 타입 ────────────────────────────────────────────────────
    public enum BridgeMessageType : int
    {
        Heartbeat      = 0,
        RealtimeTick   = 1,
        OrderFilled    = 2,
        OrderRejected  = 3,
        AccountState   = 4,
        ErrorReport    = 5,
        Ping           = 6,   // BridgeClient → ProxyHost: 50ms 초고속 헬스체크 요청(seq 에코)
        Pong           = 7,   // ProxyHost → BridgeClient: Ping 응답(양방향 생존성·RTT)
        OrderBook      = 8,   // [L2] ProxyHost → BridgeClient: 10호가 매도/매수 가격·잔량(키움 주식호가잔량 FID 41~80)
        ReferencePrice = 9,   // [L2] ProxyHost → BridgeClient: 기준가(전일종가 = 현재가 − 전일대비 FID 11) — 가격제한폭 산정
        OrderRequest   = 10,  // BridgeClient → ProxyHost 방향
        AccountCash    = 11,  // [계좌] ProxyHost → BridgeClient: OPW00001 예수금상세(예수금·D+2·주문가능금액)
        Position       = 12,  // [계좌] ProxyHost → BridgeClient: OPW00018 보유 종목 1행(배치 번호·행 번호·행 수)
    }

    // ── 주문 종류 ─────────────────────────────────────────────────────────────
    public enum OrderRequestType : int
    {
        Buy             = 1,
        Sell            = 2,
        CancelOrder     = 3,
        QueryAccount    = 4,
        SubscribeTick   = 5,
        UnsubscribeTick = 6,
        ModifyOrder     = 7,   // [정정] Price = 정정가 · Quantity = 정정수량(키움 SendOrder 5 매수정정 / 6 매도정정)
    }

    // ── 페이로드 구조체 (전부 Blittable) ──────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct TickPayload          // 40B
    {
        public long   SymbolCode;      // KSE 6자리 숫자코드 (005930 → 5930)
        public double Price;           // 현재가 (원)
        public long   Volume;          // 거래량
        public double ChangeRate;      // 전일 대비 등락률 (%)
        public long   TimestampTicks;  // DateTime.UtcNow.Ticks
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct FillPayload          // 56B([키움 프로토콜] 구 48B + RemainingQty — 구 프레임은 BridgeFillReader.TryReadFill 로 하위 호환)
    {
        public long   OrderId;
        public long   SymbolCode;
        public double FillPrice;
        public long   FillQty;
        public int    Side;            // +1 = 매수, -1 = 매도
        public int    Status;          // [키움 프로토콜] 구 Pad — BridgeFillStatusCodec(상태 코드 · 매직 0xA5 · 잔량 유효 비트). 0 = Legacy
        public long   TimestampTicks;
        public long   RemainingQty;    // [키움 프로토콜] 브로커 미체결 잔량(902 FID) — Status 잔량 유효 비트가 설 때만 의미
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AccountPayload       // 24B
    {
        public long Deposit;           // 예수금 (원)
        public long Equity;            // 평가금액 (원)
        public long UnfilledCount;     // 미체결 주문 수
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ErrorPayload         // 16B
    {
        public int  ErrorCode;
        public int  Pad;
        public long TimestampTicks;
    }

    /// <summary>[계좌] OPW00001 예수금상세 스냅샷(32B · 원).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AccountCashPayload
    {
        public long Deposit;          // 예수금
        public long D2Deposit;        // d+2 추정예수금
        public long OrderableCash;    // 주문가능금액(증거금율 반영 — 브로커 산정)
        public long TimestampTicks;
    }

    /// <summary>[계좌] OPW00018 보유 종목 1행(48B). 한 조회 = 같은 BatchId 의 RowCount 행(RowCount 0 = 보유 없음 · SymbolCode 0).</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PositionPayload
    {
        public long BatchId;
        public int  RowIndex;
        public int  RowCount;
        public long SymbolCode;
        public long Quantity;         // 보유수량(주)
        public long AvgPrice;         // 매입가(원)
        public long TimestampTicks;
    }

    /// <summary>[L2] 10호가 고정 인라인 버퍼(C#12 InlineArray · blittable · 힙 0).</summary>
    [InlineArray(10)]
    public struct OrderBookLevels10 { private long _e0; }

    /// <summary>[L2] 10호가 스냅샷(336B). 가격 = 원(정수) · 잔량 = 주 · 지수 0 = 최우선호가. 0 = 해당 호가 없음.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct OrderBookPayload
    {
        public long SymbolCode;
        public long TimestampTicks;
        public OrderBookLevels10 AskPrice;   // 매도호가 1~10(오름차순)
        public OrderBookLevels10 AskQty;
        public OrderBookLevels10 BidPrice;   // 매수호가 1~10(내림차순)
        public OrderBookLevels10 BidQty;
    }

    /// <summary>[L2] 기준가(전일종가) 통지(24B). 가격 = 원.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ReferencePricePayload
    {
        public long SymbolCode;
        public long BasePrice;
        public long TimestampTicks;
    }

    /// <summary>[L2] 키움 실시간 FID 사상(순수 함수 · 프록시/호스트 공용 · COM 무의존 → 테스트 가능).</summary>
    public static class KiwoomRealFids
    {
        public const int AskPrice1 = 41, BidPrice1 = 51, AskQty1 = 61, BidQty1 = 71;   // 각 10개 연속(41~50 · 51~60 · 61~70 · 71~80)
        public const int CurrentPrice = 10, ChangeFromPrevClose = 11, ChangeRate = 12, AccVolume = 13;

        /// <summary>SetRealReg FID 목록 — 체결(10~13) + 10호가 가격·잔량(41~80).</summary>
        public static string DefaultRegistrationList()
        {
            var sb = new System.Text.StringBuilder("10;11;12;13");
            for (int f = 41; f <= 80; f++) sb.Append(';').Append(f);
            return sb.ToString();
        }

        /// <summary>키움 가격 문자열(부호 = 등락 방향 · 공백 패딩) → 원 절대값. 파싱 실패 = 0(해당 호가 없음).</summary>
        public static long ParsePrice(ReadOnlySpan<char> s)
        {
            s = s.Trim();
            if (s.Length > 0 && (s[0] == '+' || s[0] == '-')) s = s[1..];
            return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) && v > 0 ? v : 0;
        }

        /// <summary>키움 수량 문자열 → 주(음수/실패 = 0).</summary>
        public static long ParseQty(ReadOnlySpan<char> s)
        {
            s = s.Trim();
            if (s.Length > 0 && s[0] == '+') s = s[1..];
            return long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long v) && v > 0 ? v : 0;
        }

        /// <summary>기준가 = |현재가| − 부호있는 전일대비(키움 FID 11 은 상승 +, 하락 −). 비양수 = 0(무효).</summary>
        public static long BasePrice(ReadOnlySpan<char> currentPrice10, ReadOnlySpan<char> change11)
        {
            long cur = ParsePrice(currentPrice10);
            var c = change11.Trim();
            bool neg = c.Length > 0 && c[0] == '-';
            long chg = ParsePrice(c);
            long b = neg ? cur + chg : cur - chg;
            return cur > 0 && b > 0 ? b : 0;
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct OrderRequest         // 48B
    {
        public OrderRequestType Type;
        public int              Pad;
        public long             OrderId;     // 클라이언트 할당 ID
        public long             SymbolCode;  // KSE 6자리 코드 (005930 → 5930)
        public double           Price;       // 0.0 = 시장가
        public long             Quantity;
        public long             GenomeId;    // CartridgeGenome.Id (감사 추적)
    }

    // ── 직렬화 헬퍼 ──────────────────────────────────────────────────────────
    public static class BridgeSerializer
    {
        /// <summary>NamedPipe 이름 — ProxyHost와 BridgeClient가 공유.</summary>
        public const string DefaultPipeName = "MS2E_KIWOOM_BRIDGE_V1";
        public const int    HeaderSize      = 8;  // WireType(4) + PayloadLen(4)
        public const int    MaxPayloadSize  = 4096;              // TryParseHeader 상한과 일치
        public const int    MaxFrameSize    = HeaderSize + MaxPayloadSize;  // 재사용 송신 버퍼 상한

        // ── 직렬화(★δ=0 오버로드: 호출자 제공 버퍼에 기록·무할당) ─────────────────
        //   키움 COM 이벤트 핸들러(고빈도 틱) hot path 에서 프레임마다 byte[] 를 새로 할당하지 않도록,
        //   재사용 버퍼(Span)에 직접 pack 한다. 대상 버퍼 부족은 계약 위반 → throw(경계 명시). 반환=총 바이트 수.

        public static int Pack<T>(Span<byte> dest, BridgeMessageType type, in T payload) where T : unmanaged
        {
            int plen  = Unsafe.SizeOf<T>();
            int total = HeaderSize + plen;
            if (dest.Length < total)
                throw new ArgumentException($"대상 버퍼 부족({dest.Length}<{total}) — pack 불가", nameof(dest));
            BitConverter.TryWriteBytes(dest, (int)type);
            BitConverter.TryWriteBytes(dest.Slice(4), plen);
            MemoryMarshal.Write(dest.Slice(HeaderSize), in payload);
            return total;
        }

        /// <summary>[type][8][seq(long)] 제어 프레임(Heartbeat/Ping/Pong 공용) — 재사용 버퍼에 무할당 기록.</summary>
        public static int PackSeq(Span<byte> dest, BridgeMessageType type, long seq)
        {
            int total = HeaderSize + sizeof(long);
            if (dest.Length < total)
                throw new ArgumentException($"대상 버퍼 부족({dest.Length}<{total}) — seq pack 불가", nameof(dest));
            BitConverter.TryWriteBytes(dest, (int)type);
            BitConverter.TryWriteBytes(dest.Slice(4), sizeof(long));
            BitConverter.TryWriteBytes(dest.Slice(HeaderSize), seq);
            return total;
        }

        public static byte[] PackSeq(BridgeMessageType type, long seq)
        {
            var buf = new byte[HeaderSize + sizeof(long)];
            PackSeq(buf, type, seq);
            return buf;
        }

        public static int PackHeartbeat(Span<byte> dest, long sequence)
            => PackSeq(dest, BridgeMessageType.Heartbeat, sequence);

        // ── 직렬화(할당 오버로드 — span 버전에 위임·후방호환) ────────────────────

        public static byte[] Pack<T>(BridgeMessageType type, in T payload) where T : unmanaged
        {
            var buf = new byte[HeaderSize + Unsafe.SizeOf<T>()];
            Pack(buf, type, in payload);
            return buf;
        }

        public static byte[] PackHeartbeat(long sequence)
        {
            var buf = new byte[HeaderSize + sizeof(long)];
            PackHeartbeat(buf, sequence);
            return buf;
        }

        public static byte[] PackOrderRequest(in OrderRequest req)
            => Pack(BridgeMessageType.OrderRequest, in req);

        // ── 역직렬화 ─────────────────────────────────────────────────────────

        public static bool TryParseHeader(
            ReadOnlySpan<byte>      header,
            out BridgeMessageType   type,
            out int                 payloadLen)
        {
            type       = default;
            payloadLen = 0;
            if (header.Length < HeaderSize) return false;
            int t = BitConverter.ToInt32(header);
            if (!IsValidWireType(t)) return false;
            payloadLen = BitConverter.ToInt32(header.Slice(4));
            if (payloadLen < 0 || payloadLen > 4096) return false;
            type = (BridgeMessageType)t;
            return true;
        }

        public static T Unpack<T>(ReadOnlySpan<byte> payload) where T : unmanaged
            => MemoryMarshal.Read<T>(payload);

        // ── 유틸리티 ─────────────────────────────────────────────────────────

        /// <summary>KSE 6자리 코드 복원 (5930 → "005930").</summary>
        public static string FormatSymbol(long code) => code.ToString("D6");

        /// <summary>KSE 코드 문자열 → long (실패 시 0).</summary>
        public static long ParseSymbol(string? code)
            => long.TryParse(code, out var v) ? v : 0L;

        private static bool IsValidWireType(int t) => t is >= 0 and <= 12;   // [L2] 8·9 · [계좌] 11 AccountCash · 12 Position
    }
}
