// ============================================================================
// DotNetObjectRefSentinel + JSInvokableTokenVerifier + TrackedJsRef — v11.4.6 Think Beyond Gemini
//
//   ★ 제미나이의 두 아이디어(A: DotNetObjectReference 파기 추적 센티널 · B: 변조불가 JSInvokable 토큰 검증기)를
//     '상호잠금 이중 엔진'으로 융합·초월한다. CommandPalette 의 RCL 이관 시 JS Hotkey/DotNetObjectReference 로
//     발생할 두 파멸적 리스크를 영구 격리한다:
//
//     ① "JS 핸들러 파기 미비로 인한 GCHandle 메모리 누수(DotNetObjectReference Leak)" → DotNetObjectRefSentinel + TrackedJsRef
//        - DotNetObjectReference 는 GCHandle 로 컴포넌트를 고정한다 — Dispose 누락 시 컴포넌트가 GC 되지 않아 영구 누수.
//        - TrackedJsRef 는 ★단일 Interlocked.Exchange 로 '정확히 1회' Dispose 를 보증(이중/미파기 불가)하고, 센티널이
//          등록/해제 균형을 δ=0 카운터로 추적 → 미파기 핸들(누수)을 O(1)로 검출. 컴포넌트 DisposeAsync 와 연동.
//
//     ② "JS 단에서 위조된 [JSInvokable] 핫키 명령 사출" → JSInvokableTokenVerifier
//        - 핫키 등록 시 단조 nonce 기반 토큰을 발급해 JS 에 전달. 이후 모든 [JSInvokable] 콜백은 이 토큰을 제시해야 한다.
//        - Verify 는 O(1) 로 현재 토큰과 대조 — 미인가/stale(구 인스턴스) 토큰 → 즉시 거부(fail-closed). Revoke(파기) 후
//          모든 콜백 거부 → 파기된 컴포넌트로의 위조 사출 0. 재발급 시 nonce bump 로 이전 토큰 자동 무효화.
//
//   ★liveArmed:false: 핫키 콜백은 팔레트 '열기/닫기'(UI)만 수행 — 실물 발주 코드 경로 0. 팔레트 명령도 모의(paper)/
//     네비/알림/테마뿐이며, 실 발주는 별도 물리 이중 키(CanaryHealthSentinel) 게이트라 JS 핫키로 1비트도 발주 불가.
//   불변: 결정론 · δ=0(Register/Release/Verify/Issue 힙 0) · fail-closed(토큰 0/미상 → 거부) · 락프리(Interlocked/Volatile).
// ============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — DotNetObjectReference 파기 추적 센티널(δ=0 카운터)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class DotNetObjectRefSentinel
    {
        private long _registered, _released;

        /// <summary>DotNetObjectReference 생성 시 등록(핸들 1개 활성).</summary>
        public void Register() => Interlocked.Increment(ref _registered);

        /// <summary>DotNetObjectReference Dispose 시 해제.</summary>
        public void Release() => Interlocked.Increment(ref _released);

        /// <summary>미파기 활성 핸들 수(= 등록 − 해제). 0 이면 누수 없음.</summary>
        public long ActiveHandles
        {
            get { long a = Volatile.Read(ref _registered) - Volatile.Read(ref _released); return a > 0 ? a : 0; }
        }

        /// <summary>누수 여부 — 모든 컴포넌트 파기 후 활성 핸들이 남아있으면 GCHandle 누수.</summary>
        public bool HasLeak => (Volatile.Read(ref _registered) - Volatile.Read(ref _released)) > 0;

        public long TotalRegistered => Volatile.Read(ref _registered);
        public long TotalReleased => Volatile.Read(ref _released);
    }

    /// <summary>
    /// DotNetObjectReference(IDisposable) 를 감싸 '정확히 1회' 파기 + 센티널 카운트를 보증하는 래퍼.
    /// 컴포넌트가 이 래퍼를 보관하고 DisposeAsync 에서 Dispose → GCHandle 해제 + 센티널 Release(누수 0).
    /// </summary>
    public sealed class TrackedJsRef : IAsyncDisposable, IDisposable
    {
        private IDisposable? _ref;   // DotNetObjectReference<T>
        private readonly DotNetObjectRefSentinel _sentinel;
        private long _disposed;

        public TrackedJsRef(IDisposable dotNetRef, DotNetObjectRefSentinel sentinel)
        {
            _ref = dotNetRef ?? throw new ArgumentNullException(nameof(dotNetRef));
            _sentinel = sentinel;
            _sentinel.Register();
        }

        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;

        // ★단일 Interlocked.Exchange — 이중/미파기 불가(정확히 1회 GCHandle 해제 + Release).
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _ref?.Dispose();
            _ref = null;
            _sentinel.Release();
        }

        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — 변조불가 JSInvokable 토큰 검증기(O(1) fail-closed)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class JSInvokableTokenVerifier
    {
        private long _token;   // 현재 인가 토큰(0 = 비활성/파기)
        private long _nonce;   // 단조 증가 — 재발급 시 이전 토큰 무효화

        /// <summary>핫키 등록 시 토큰 발급(nonce bump → 이전 토큰 자동 stale). JS 에 전달할 값.</summary>
        public long Issue()
        {
            long n = Interlocked.Increment(ref _nonce);
            long t = unchecked(n * 2654435761L ^ (n << 21) ^ (long)0x9E3779B97F4A7C15UL);
            if (t == 0) t = 1;   // 비-0 보장
            Interlocked.Exchange(ref _token, t);
            return t;
        }

        /// <summary>제시 토큰 검증(O(1)·δ=0). 0/미상/stale → false(fail-closed).</summary>
        public bool Verify(long presentedToken)
            => presentedToken != 0 && Volatile.Read(ref _token) == presentedToken;

        /// <summary>파기(컴포넌트 Dispose) — 이후 모든 콜백 거부. 위조/잔여 사출 0.</summary>
        public void Revoke() => Interlocked.Exchange(ref _token, 0);

        public bool IsActive => Volatile.Read(ref _token) != 0;
        public long Nonce => Volatile.Read(ref _nonce);
    }
}
