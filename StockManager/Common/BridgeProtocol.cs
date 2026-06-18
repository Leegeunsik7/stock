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

namespace StockManager.Common
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

        // ── 직렬화 ───────────────────────────────────────────────────────────

        public static byte[] Pack<T>(BridgeMessageType type, in T payload) where T : unmanaged
        {
            int plen = Unsafe.SizeOf<T>();
            var buf  = new byte[HeaderSize + plen];
            BitConverter.TryWriteBytes(buf.AsSpan(0), (int)type);
            BitConverter.TryWriteBytes(buf.AsSpan(4), plen);
            MemoryMarshal.Write(buf.AsSpan(HeaderSize), in payload);
            return buf;
        }

        public static byte[] PackHeartbeat(long sequence)
        {
            var buf = new byte[HeaderSize + sizeof(long)];
            BitConverter.TryWriteBytes(buf.AsSpan(0), (int)BridgeMessageType.Heartbeat);
            BitConverter.TryWriteBytes(buf.AsSpan(4), sizeof(long));
            BitConverter.TryWriteBytes(buf.AsSpan(HeaderSize), sequence);
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

        private static bool IsValidWireType(int t) => t is (>= 0 and <= 5) or 10;
    }
}
