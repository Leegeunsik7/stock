// ============================================================================
// QuantComputeInterlock — Phase 94-C: 비동기 퀀트 컴퓨트 인터락 (Think Beyond Gemini)
//
//   ★제미나이 A(CTS-linked 취소)/B(무할당 결과 버퍼)를 '세대(generation) 기반 단일 인터락' 하나로 통합·초월한다.
//   퀀트 UI 가 Port 로 고부하 비동기 연산을 돌리는 동안 발생하는 두 파멸 리스크를 동시 격리:
//     ① 미취소 백그라운드 연산 유출(Uncanceled Compute Task Leak) — 페이지 이탈(Dispose)/파라미터 재설정 시
//        이전 연산의 CancellationToken 을 즉시 취소해 백그라운드 스레드가 조기 중단(자원 회수).
//     ② 파기된 UI 로의 상태 갱신(Disposed Component State Mutation) — 연산이 늦게 끝나 돌아왔을 때, 컴포넌트가
//        이미 파기됐거나(=disposed) 더 새 연산이 시작됐으면(=stale generation) 결과 적용을 '드롭'(fail-closed).
//
//   ── 왜 세대(generation)인가 ──────────────────────────────────────────────
//   OnParametersSet 은 재렌더마다 재발화한다. Begin() 이 매 연산마다 이전을 취소하고 세대를 +1 → 늦게 끝난 '옛'
//   연산의 결과는 TryApply(gen) 에서 현재 세대와 불일치로 드롭된다. 즉 '마지막 연산만 반영'되고 stale UI 갱신 0.
//
//   ★불변: δ=0 핫패스(TryApply/IsCancellationRequested 가드 = Volatile/Interlocked·힙 0. Begin 의 CTS 생성은
//     사용자-트리거 콜드 경로) · 결정론(세대 단조 증가) · fail-closed(파기/stale → 적용 드롭) · liveArmed:false
//     (순수 UI 수명 관리 — 발주/소켓 유발 0). 스레드 안전(Interlocked·Volatile).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    public sealed class QuantComputeInterlock : IDisposable
    {
        private CancellationTokenSource _cts = new();
        private int  _disposed;   // 0=alive, 1=disposed(파기)
        private long _gen;        // 현재 세대(Begin 마다 +1)

        public bool IsDisposed => Volatile.Read(ref _disposed) == 1;
        public long Generation => Interlocked.Read(ref _gen);

        /// <summary>
        /// 새 연산 시작 — 이전 연산 CancellationToken 을 취소(자원 회수)하고 새 토큰·세대를 반환.
        /// 파기 후 호출 시 이미-취소 토큰과 세대 -1 반환(호출부가 즉시 no-op).
        /// </summary>
        public (CancellationToken token, long generation) Begin()
        {
            if (Volatile.Read(ref _disposed) == 1) return (new CancellationToken(true), -1L);

            // ★경합 봉쇄: fresh 를 '발행(Exchange) 전에' 로컬로 소유한 채 토큰을 캡처한다. 이 시점 fresh 는 이
            //   스레드 전용이라 .Token 게터가 절대 ObjectDisposedException 을 던지지 않는다(공유 _cts 재읽기 금지).
            //   이후 더 새 Begin 이 fresh 를 교체·파기해도, 이미 캡처한 토큰 구조체의 IsCancellationRequested 읽기는
            //   안전하며(취소 상태는 Dispose 후에도 보존) '더 새 연산이 왔으니 내 연산은 취소' 의미로 정확히 동작.
            var fresh = new CancellationTokenSource();
            CancellationToken tok = fresh.Token;

            var prev = Interlocked.Exchange(ref _cts, fresh);   // prev 는 교체로 이 스레드가 독점 → 단독 취소·파기
            long g = Interlocked.Increment(ref _gen);
            try { prev.Cancel(); } catch { /* 취소 실패해도 새 세대는 유효 */ }
            try { prev.Dispose(); } catch { }
            // 파기 경합(Begin 도중 Dispose): 방금 만든 fresh 를 취소해 유출 방지하고 no-op 반환.
            if (Volatile.Read(ref _disposed) == 1) { try { fresh.Cancel(); } catch { } return (new CancellationToken(true), -1L); }
            return (tok, g);   // ★공유 필드가 아니라 캡처한 tok 반환(경합 하 ODE 원천 봉쇄)
        }

        /// <summary>
        /// ★결과 적용 가드(δ=0). 파기됨/더-새-세대(stale)면 apply 를 '실행하지 않고' false 반환 — 파기 UI 상태 변조·
        /// 옛 연산 덮어쓰기를 원천 차단. gen 이 현재 세대와 일치하고 미파기일 때만 apply() 실행.
        /// </summary>
        public bool TryApply(long generation, Action apply)
        {
            if (apply is null) return false;
            if (Volatile.Read(ref _disposed) == 1) return false;         // ② 파기 → 드롭
            if (Interlocked.Read(ref _gen) != generation) return false;  // stale 세대 → 드롭
            apply();
            return true;
        }

        /// <summary>파기(컴포넌트 Dispose 시): 진행 중 연산 취소(① 유출 차단) + 이후 모든 TryApply 드롭.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            try { _cts.Cancel(); _cts.Dispose(); } catch { }
        }
    }
}
