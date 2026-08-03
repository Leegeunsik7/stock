// ============================================================================
// SecureAdminViewPresenter — v10.1.0 Phase 93-B: 시크릿 마스킹 뷰 프레젠터 (Gemini 보강 과제)
//
//  Admin 뷰에서 API 키·시크릿을 Plain Text string 으로 힙에 남기지 않고, 재사용 char 버퍼(마스킹 버퍼)를 경유해 마스킹
//  렌더한 뒤 렌더링 직후 버퍼를 0x00 자가 소멸(계약 제거불가 제로화)한다. 어댑터(IAdminSecretPort·DPAPI 금고)가 시크릿을
//  char 버퍼에 직접 기입(string 미생성)하고, 프레젠터가 first-3+last-2 만 노출하는 마스킹 표시를 dest 에 쓴 뒤 plain 버퍼를 소멸.
//
//  ★불변: liveArmed:false(순수 표시 변환 — IBroker·소켓·실 발주 유발 0) · fail-closed(로드 실패 → 버퍼 영점+0 반환) ·
//    ★plain string 힙 미잔류(시크릿은 char 버퍼에만·즉시 0x00). CryptographicOperations.ZeroMemory(JIT elision 방지·이식적).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using MS2E.UI.Portable.Ports;

namespace MS2E.UI.Portable.Security
{
    /// <summary>시크릿 마스킹 뷰 프레젠터. RenderMasked=시크릿을 char 버퍼 경유 마스킹 렌더 후 즉시 0x00 소멸(plain 힙 미잔류).</summary>
    public sealed class SecureAdminViewPresenter
    {
        private readonly IAdminSecretPort _secrets;
        private readonly char[] _plainBuffer;   // 재사용 plain 로드 버퍼(string 미생성·렌더 후 즉시 0x00)
        private long _renders, _zeroizations;

        public SecureAdminViewPresenter(IAdminSecretPort secrets, int bufferLen = 128)
        {
            _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
            _plainBuffer = new char[Math.Clamp(bufferLen, 8, 4096)];
        }

        // ── 읽기 ──
        public long Renders      => Interlocked.Read(ref _renders);
        public long Zeroizations => Interlocked.Read(ref _zeroizations);

        /// <summary>
        /// ★시크릿을 마스킹해 dest 에 기입하고 plain 버퍼를 즉시 0x00 소멸(plain string 힙 미잔류). first-3+last-2 만 노출·중간 '*'.
        /// 반환=마스킹 표시 길이. 로드 실패 시 버퍼 영점+0(fail-closed).
        /// </summary>
        public int RenderMasked(long secretId, Span<char> dest)
        {
            int n;
            try { n = _secrets.LoadSecret(secretId, _plainBuffer); }
            catch { ZeroizeBuffer(); return 0; }   // 로드 예외 → 버퍼 영점(잔류 0)·fail-closed
            n = Math.Clamp(n, 0, _plainBuffer.Length);
            int written = Mask(_plainBuffer.AsSpan(0, n), dest);
            ZeroizeBuffer();                        // ★렌더 직후 plain 버퍼 0x00(잔류 0)
            Interlocked.Increment(ref _renders);
            return written;
        }

        /// <summary>★freeze-safe 퍼지(revoke/ForceFreeze 시 호출) — plain 버퍼 즉시 0x00 소멸.</summary>
        public void PurgeNow() => ZeroizeBuffer();

        // 마스킹: 최대 first-3 + last-2 노출·중간 '*'. ★짧은 시크릿(≤3자)은 전량 마스킹(head=0)·절단(secret>dest) 시 tail 미노출
        //   (가짜 꼬리=내부 문자 노출 방지). 항상 최소 1자 마스킹 보장(전량 평문 노출 봉쇄).
        private static int Mask(ReadOnlySpan<char> plain, Span<char> dest)
        {
            int n = plain.Length;
            if (n == 0 || dest.Length == 0) return 0;
            int outLen = Math.Min(n, dest.Length);
            bool truncated = n > outLen;                                          // 시크릿이 dest 보다 길어 절단 → tail 은 실제 꼬리 아님
            int head = Math.Min(3, outLen / 4);                                   // ≤3자 → 0(전량 마스킹)
            int tail = (!truncated && outLen >= 8) ? Math.Min(2, outLen / 4) : 0; // 절단/단문 시 tail 미노출
            if (head + tail >= outLen) { head = 0; tail = 0; }                    // 항상 ≥1자 마스킹
            for (int i = 0; i < outLen; i++)
                dest[i] = (i < head || i >= outLen - tail) ? plain[i] : '*';
            return outLen;
        }

        private void ZeroizeBuffer()
        {
            // ★계약 제거불가 제로화(JIT elision 방지·이식적): char 버퍼를 byte span 으로 보고 ZeroMemory.
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_plainBuffer.AsSpan()));
            Interlocked.Increment(ref _zeroizations);
        }
    }
}
