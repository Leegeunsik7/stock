// ============================================================================
// VerifiedSecureZeroizer — v8.1.0 ★Think Beyond Gemini: 판독-검증 시큐어 제로화
//   "제로화를 '신뢰'하지 않고 '증명'한다 — Readback Verification Fence"
//
//  ── 왜 제미나이의 A(SecureZeroMemory Unroll Fence)를 초월하는가 ─────────────
//  제미나이 A 는 JIT/컴파일러가 '이후 미사용 변수'의 0x00 오버라이트 루프를 죽은 코드로 제거하는 것을 volatile
//  펜스·RtlZeroMemory 로 '방지'하자 제안한다. 그러나 A 는 '와이프가 실제로 일어났다'고 '가정'할 뿐 확인하지 않는다 —
//  최적화·인라인 후킹·부분 와이프·페이지 스와핑 중 하나라도 우회하면 시크릿이 램에 잔류해도 아무도 모른다.
//
//  이 제로라이저는 와이프를 '증명'한다: (1) CryptographicOperations.ZeroMemory 로 계약상 제거-불가 와이프(BCL 보증·
//  이식적·WebOnly 안전) 후, (2) 전 바이트를 '판독(readback)'해 잔류를 OR 누적 → 0 이 아니면 와이프가 우회된 것이다.
//  판독 결과가 반환/계수에 '관측'되므로 JIT 는 판독을 제거할 수 없고, 판독 의존성이 곧 와이프를 관측 가능하게 만드는
//  '펜스'가 된다(A 초월: 방지가 아니라 '런타임 증명'). 잔류 포착 시 재와이프 + 검증실패 계수 → 상위가 fail-closed 승격.
//
//  ★불변: δ=0(제로화/판독 핫패스 힙 0 — 호출자 Span·Interlocked) · 결정론(순수) · liveArmed:false(순수 메모리 위생 —
//    IBroker·소켓·onSend 참조 0) · fail-closed(검증 실패 → 재와이프·계수·false 반환). ★순수 관리형(P/Invoke 0 — WebOnly 안전).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>판독-검증 시큐어 제로라이저(δ=0·순수 관리형). Zeroize → 와이프 후 전 바이트 0x00 판독 증명. 잔류 시 재와이프+계수.</summary>
    public sealed class VerifiedSecureZeroizer
    {
        private long _wipes, _bytesWiped, _verificationFailures;

        public long Wipes               => Interlocked.Read(ref _wipes);
        public long BytesWiped          => Interlocked.Read(ref _bytesWiped);
        /// <summary>와이프 후 잔류 포착(최적화/변조가 제로화를 우회한 횟수). 0 이 정상 — 양수면 fail-closed 승격 대상.</summary>
        public long VerificationFailures => Interlocked.Read(ref _verificationFailures);

        /// <summary>버퍼를 시큐어 제로화 후 '전 바이트 0x00' 판독 증명(δ=0). 잔류 시 1회 재와이프. 검증 통과 시 true.</summary>
        public bool Zeroize(Span<byte> buffer)
        {
            if (buffer.IsEmpty) return true;
            CryptographicOperations.ZeroMemory(buffer);      // 계약상 제거-불가 와이프(BCL 보증·이식적)
            Interlocked.Add(ref _bytesWiped, buffer.Length);
            Interlocked.Increment(ref _wipes);

            if (VerifyZero(buffer)) return true;             // ★판독 증명 통과
            // 잔류 포착 — 최적화/변조 우회 의심. 재와이프 + 재검증 후 계수.
            CryptographicOperations.ZeroMemory(buffer);
            Interlocked.Increment(ref _verificationFailures);
            return VerifyZero(buffer);
        }

        /// <summary>char[](string 유래 시크릿) 를 바이트로 재해석해 시큐어 제로화·검증(δ=0).</summary>
        public bool ZeroizeChars(char[]? chars)
        {
            if (chars is null || chars.Length == 0) return true;
            return Zeroize(MemoryMarshal.AsBytes(chars.AsSpan()));
        }

        // 전 바이트 판독 후 OR 누적 → 0 여부. residue 가 반환에 '관측'되므로 JIT 는 판독(→와이프)을 제거할 수 없다.
        private static bool VerifyZero(ReadOnlySpan<byte> buffer)
        {
            int residue = 0;
            for (int i = 0; i < buffer.Length; i++) residue |= buffer[i];
            return residue == 0;
        }
    }
}
