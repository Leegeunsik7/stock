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
        OrderRequest   = 10,  // BridgeClient → ProxyHost 방향
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
    public struct FillPayload          // 48B
    {
        public long   OrderId;
        public long   SymbolCode;
        public double FillPrice;
        public long   FillQty;
        public int    Side;            // +1 = 매수, -1 = 매도
        public int    Pad;
        public long   TimestampTicks;
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

        private static bool IsValidWireType(int t) => t is (>= 0 and <= 7) or 10;
    }
}
