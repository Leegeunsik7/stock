// ============================================================================
// ExecutionTokenZeroizationGuard — v11.0.0: 발주 시크릿 제로화 가드 (Zero-Leak)
//
//   증권사 발주 요청(HTTP Header/Payload) 구성 시 API 발주용 Access Token·Secret Key 가 string 힙에 남지 않도록
//   Span<char> 로 재사용 버퍼에 주입하고, 소켓 전송 직후 CryptographicOperations.ZeroMemory 로 0x00 소멸한다. SecureAdmin
//   ViewPresenter(Phase 93-B) 의 char-버퍼-즉시-소멸 철학을 발주 경로에 계승.
//
//   ★불변: δ=0(LoadBearer/ZeroizeAfterSend 핫패스 힙 0 — span 연산·Interlocked·string 미생성) · fail-closed(로드 후
//     미소멸 누적 → HeapContaminated) · liveArmed:false(순수 토큰 버퍼 관리 — 실 발주 유발 0).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace StockManager.Common
{
    public sealed class ExecutionTokenZeroizationGuard
    {
        private long _loads, _zeroizations;

        /// <summary>★"Bearer &lt;token&gt;" 를 dest(재사용 버퍼)에 δ=0 구성(string 미생성). 반환=기입 길이. dest 부족 시 절단.</summary>
        public int LoadBearer(ReadOnlySpan<char> token, Span<char> dest)
        {
            ReadOnlySpan<char> prefix = "Bearer ";
            int n = 0;
            for (int i = 0; i < prefix.Length && n < dest.Length; i++, n++) dest[n] = prefix[i];
            for (int i = 0; i < token.Length && n < dest.Length; i++, n++) dest[n] = token[i];
            Interlocked.Increment(ref _loads);
            return n;
        }

        /// <summary>★소켓 전송 직후 버퍼 0x00 소멸(계약 제거불가·JIT elision 방지·char 버퍼 전체 바이트 커버).</summary>
        public void ZeroizeAfterSend(Span<char> buf)
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(buf));
            Interlocked.Increment(ref _zeroizations);
        }

        public long Loads        => Interlocked.Read(ref _loads);
        public long Zeroizations => Interlocked.Read(ref _zeroizations);

        /// <summary>★힙 오염: 로드된 발주 토큰이 소멸되지 않고 잔류(휴지 시 loads==zeroizations 여야 함 — 차이 &gt;0 = 미소멸
        /// 토큰 잔류·단일 누수도 탐지). 정상 경로는 load→send→zeroize 동기 완결이라 텔레메트리 샘플점에서 항상 0.</summary>
        public bool HeapContaminated => (Interlocked.Read(ref _loads) - Interlocked.Read(ref _zeroizations)) > 0L;
    }
}
