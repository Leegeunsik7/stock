// ============================================================================
// BridgeOrderStatus — [키움 프로토콜 · 추천 A] 체결 통보 상태 명시(접수/거부/취소확인/체결) · 무할당 파싱 · 키움 FID 분류
//
//  ★공유 와이어 계약: 이 파일은 호스트(MS2E.StockSuite)와 x86 KiwoomProxy 양쪽에 컴파일된다(BridgeProtocol.cs 와 같은 방식 링크).
//
//  왜(실측): 프록시는 체결량 0 인 키움 체결 통보(주문 접수 · 거부 · 취소/정정 확인)를 전부 같은 모양의 FillPayload(FillQty=0)로
//   보냈다. 호스트는 셋을 구분할 수 없어 ① 거부된 주문이 추적 슬롯을 TTL(2분)까지 점유한 뒤 회수되며 무장을 차단했고,
//   ② LiveAuditService 는 체결가 0 인 이 통보를 '형식 오류 체결'로 보고 ForceFreeze 했다(접수 통보마다 동결 — 실 COM 결선 시 즉시 발현).
//
//  교정 — FillPayload.Status(int · 구 Pad 자리 · 크기 불변 위치) 비트 레이아웃:
//     bit 0..7   = BridgeFillStatus 코드
//     bit 8..15  = 매직 0xA5 (구 프록시의 Pad=0 이나 쓰레기 값을 '명시 상태'로 오인하지 않게)
//     bit 16     = RemainingQty 유효(브로커 미체결 잔량 동봉 — 902 FID)
//   매직 불일치 · 미지 코드 → Legacy(구 동작: 체결량 0 = 모호 통보). ★미분류 = 안전 측(종결로 단정하지 않음).
//  FillPayload.RemainingQty(long)는 구조체 끝에 추가 — 구 프록시의 48B 프레임은 TryReadFill 이 앞 48B 만 읽고 잔량 무효 처리(하위 호환).
//
//  KiwoomChejanClassifier: 키움 OnReceiveChejanData(gubun "0") FID → 상태(순수 함수 · 테스트 가능 · COM 무의존).
//     911 체결량 > 0                      → Filled
//     913 주문상태 "접수"                  → Accepted
//     913 "확인" ∧ 905 주문구분 에 "취소"  → CancelConfirmed
//     913 "확인"(정정 확인)                → Accepted(잔량 유지 주문)
//     913 "거부" · 그 외                   → Rejected / Legacy
//   거부는 키움에서 주로 SendOrder 반환값(&lt;0)으로 온다 — ProductionKiwoomApi 가 그 경로를 Rejected 통보로 승격한다.
// ============================================================================
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MS2E.Infrastructure
{
    public enum BridgeFillStatus : byte { Legacy = 0, Filled = 1, Accepted = 2, Rejected = 3, CancelConfirmed = 4 }

    public static class BridgeFillStatusCodec
    {
        public const int MagicByte = 0xA5;
        private const int MagicShift = 8, RemainingValidBit = 1 << 16;

        public static int Encode(BridgeFillStatus status, bool remainingKnown)
            => (byte)status | (MagicByte << MagicShift) | (remainingKnown ? RemainingValidBit : 0);

        /// <summary>상태 해독(δ=0). 매직 불일치 · 미지 코드 → Legacy(잔량 무효).</summary>
        public static BridgeFillStatus Decode(int raw, out bool remainingKnown)
        {
            remainingKnown = false;
            if (((raw >> MagicShift) & 0xFF) != MagicByte) return BridgeFillStatus.Legacy;
            int code = raw & 0xFF;
            if (code < (int)BridgeFillStatus.Filled || code > (int)BridgeFillStatus.CancelConfirmed) return BridgeFillStatus.Legacy;
            remainingKnown = (raw & RemainingValidBit) != 0;
            return (BridgeFillStatus)code;
        }

        public static BridgeFillStatus Decode(in FillPayload fill, out long remainingQty)
        {
            var s = Decode(fill.Status, out bool known);
            remainingQty = known && fill.RemainingQty >= 0 ? fill.RemainingQty : -1;
            return s;
        }
    }

    public static class BridgeFillReader
    {
        /// <summary>구 프록시(48B · RemainingQty 없음) 프레임 길이.</summary>
        public const int LegacyFillBytes = 48;

        /// <summary>체결 페이로드 하위 호환 판독(δ=0 · ReadOnlySpan). 48B 구 프레임 = 잔량 무효 · 그 미만 = false.</summary>
        public static bool TryReadFill(ReadOnlySpan<byte> payload, out FillPayload fill)
        {
            int full = Unsafe.SizeOf<FillPayload>();
            if (payload.Length >= full) { fill = MemoryMarshal.Read<FillPayload>(payload); return true; }
            fill = default;
            if (payload.Length < LegacyFillBytes) return false;
            fill.OrderId        = BinaryPrimitives.ReadInt64LittleEndian(payload);
            fill.SymbolCode     = BinaryPrimitives.ReadInt64LittleEndian(payload[8..]);
            fill.FillPrice      = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload[16..]));
            fill.FillQty        = BinaryPrimitives.ReadInt64LittleEndian(payload[24..]);
            fill.Side           = BinaryPrimitives.ReadInt32LittleEndian(payload[32..]);
            fill.Status         = BinaryPrimitives.ReadInt32LittleEndian(payload[36..]);
            fill.TimestampTicks = BinaryPrimitives.ReadInt64LittleEndian(payload[40..]);
            fill.RemainingQty   = 0;
            fill.Status        &= ~(1 << 16);   // 구 프레임에는 잔량 필드가 없다 — 유효 비트가 서 있어도 무효화
            return true;
        }

        /// <summary>바이트에서 상태만 즉시 판독(δ=0) — 프레임 전체 복사 없이 접수/거부/취소확인 구분.</summary>
        public static BridgeFillStatus PeekStatus(ReadOnlySpan<byte> payload)
            => payload.Length < 40 ? BridgeFillStatus.Legacy : BridgeFillStatusCodec.Decode(BinaryPrimitives.ReadInt32LittleEndian(payload[36..]), out _);
    }

    public static class KiwoomChejanClassifier
    {
        /// <summary>키움 체결 통보 FID → 상태(순수 · δ=0). state913 = 주문상태, kind905 = 주문구분.</summary>
        public static BridgeFillStatus Classify(ReadOnlySpan<char> state913, ReadOnlySpan<char> kind905, long fillQty911)
        {
            if (fillQty911 > 0) return BridgeFillStatus.Filled;
            var st = state913.Trim();
            if (st.SequenceEqual("접수")) return BridgeFillStatus.Accepted;
            if (st.SequenceEqual("거부")) return BridgeFillStatus.Rejected;
            if (st.SequenceEqual("확인"))
                return kind905.IndexOf("취소", StringComparison.Ordinal) >= 0 ? BridgeFillStatus.CancelConfirmed : BridgeFillStatus.Accepted;
            return BridgeFillStatus.Legacy;   // "체결"(체결량 0 은 비정상) · 미상 — 종결로 단정하지 않음
        }
    }
}
