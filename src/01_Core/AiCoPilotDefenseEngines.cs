// ============================================================================
// ZeroAllocTokenStreamBuffer + AiModelFallbackCircuitBreaker — v12.4.0 Think Beyond Gemini
//
//   위험 가정(제미나이의 '단순 결속'이 다루지 않는 AI/LLM 통합 파멸 리스크):
//     ① "LLM 스트리밍 토큰을 문자열 이어붙이기(string += token)로 누적" — .NET string 은 불변이라 매 토큰마다
//        전체 버퍼를 새로 힙 할당·복사(O(n²) 누적 비용) → 장문 스트리밍에서 Gen0/Gen1 힙 파편화·GC 스톨이
//        UI 렌더링 프레임을 드롭시킨다(체결/호가 렌더 hot-path 까지 GC 압력이 전이될 위험).
//     ② "원격 LLM API 장애/응답 저신뢰 시 추천 기능이 그대로 죽거나, 반대로 저신뢰 출력을 무비판 사출" —
//        네트워크 단절 시 전면 정지는 UX 붕괴, 반대로 아무 게이트 없이 저신뢰(할루시네이션 의심) 시그널을
//        계속 내보내면 사용자가 나쁜 추천을 '정상'으로 오인할 위험이 liveArmed 무장 오인과 유사한 형태로 남는다.
//
//   ── Think Beyond (이중 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Token Streaming Render Buffer(Idea A) — ArrayPool&lt;char&gt; 로 1회 대여한 버퍼에 토큰을
//        Span&lt;char&gt; 슬라이스로 직접 기입. 용량 초과 시에만(드물게) 배증 재대여 — 정상 스트리밍 구간은 힙 0.
//     ② AI Model Fallback & Confidence Circuit Breaker(Idea B) — 원격 실패 감지 시 ★단일 Interlocked.Exchange
//        로 LocalFallback 모드에 0ms 원자 스위칭(상태 전이 자체가 원자적 — 중간 상태 관측 불가). 모델 신뢰도가
//        임계 미만이면 sticky Suspend 래치로 AI 시그널 출력 자체를 원자적으로 차단(Advisory 사슬의 2중 방어선
//        — IAiRecommendationPort 구조적 격리와 별개로 '품질' 게이트를 추가).
//
//   불변: 결정론(주입 신호만 사용·자체시계 0) · δ=0(Append/NoteConfidence/CanEmitSignal 정상 경로 힙 0) ·
//        fail-closed(신뢰도 임계 미달·미설정 → Suspend) · 락프리(Interlocked/Volatile).
//   ※ MS2E.Core(최내부·순수) — 상위(MS2E.Infrastructure)가 AiRecommendationAdapter 로 결선한다.
// ============================================================================
using System;
using System.Buffers;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① (Idea A) — Zero-Alloc Token Streaming Render Buffer
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// ArrayPool&lt;char&gt; 대여 버퍼에 LLM 스트리밍 토큰을 누적하는 무할당 렌더 버퍼. 용량 초과 시에만 배증
    /// 재대여(드문 성장 이벤트 — 정상 스트리밍 구간은 힙 0). ★단일 라이터(Append) 계약. IDisposable — 폐기 시 반납.
    /// </summary>
    public sealed class ZeroAllocTokenStreamBuffer : IDisposable
    {
        private char[] _buf;
        private int _len;
        private long _growthCount;
        private bool _disposed;

        public ZeroAllocTokenStreamBuffer(int initialCapacity = 4096)
            => _buf = ArrayPool<char>.Shared.Rent(initialCapacity > 0 ? initialCapacity : 4096);

        public int Length => _len;
        public long GrowthCount => Interlocked.Read(ref _growthCount);

        /// <summary>토큰(문자 조각) 1건 추가(★정상 경로 δ=0). 잔여 용량 부족 시에만 배증 재대여(드문 경로).</summary>
        public void Append(ReadOnlySpan<char> token)
        {
            ThrowIfDisposed();
            if (token.Length == 0) return;
            EnsureCapacity(_len + token.Length);
            token.CopyTo(_buf.AsSpan(_len, token.Length));
            _len += token.Length;
        }

        /// <summary>현재까지 누적된 전체 텍스트 뷰(δ=0 — 사본 없음, 버퍼 생존 동안만 유효).</summary>
        public ReadOnlySpan<char> View => _buf.AsSpan(0, _len);

        /// <summary>누적분을 비움(버퍼는 유지 — 재대여 없음). 다음 스트림에 재사용.</summary>
        public void Clear() => _len = 0;

        private void EnsureCapacity(int required)
        {
            if (required <= _buf.Length) return;
            int newCap = _buf.Length * 2;
            while (newCap < required) newCap *= 2;
            char[] grown = ArrayPool<char>.Shared.Rent(newCap);
            _buf.AsSpan(0, _len).CopyTo(grown);
            ArrayPool<char>.Shared.Return(_buf);
            _buf = grown;
            Interlocked.Increment(ref _growthCount);   // ★성장(드문 경로)만 계수 — 정상 Append 는 힙 0 실증용
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ZeroAllocTokenStreamBuffer));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ArrayPool<char>.Shared.Return(_buf);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② (Idea B) — AI Model Fallback & Confidence Circuit Breaker
    // ════════════════════════════════════════════════════════════════════════
    public enum AiModelMode : byte { Remote = 0, LocalFallback = 1 }

    /// <summary>
    /// 원격 LLM 장애 시 0ms 원자 로컬 폴백 스위칭 + 모델 신뢰도 sticky Suspend 게이트. CanEmitSignal=false 이면
    /// 상위(AiRecommendationAdapter)는 시그널 출력을 억제해야 한다(구조적 Advisory-Only 격리와 별개의 품질 게이트).
    /// </summary>
    public sealed class AiModelFallbackCircuitBreaker
    {
        private readonly long _confidenceThresholdPpm;   // 0..1,000,000(ppm). 미달 시 Suspend.
        private long _mode;          // AiModelMode
        private long _suspended;     // 0/1 sticky
        private long _confidencePpm = 1_000_000;   // 초기값 = 만점(정보 없음을 저신뢰로 오판하지 않음)
        private long _remoteFailures, _fallbackSwitches;

        public AiModelFallbackCircuitBreaker(long confidenceThresholdPpm = 500_000)   // 기본 50%
            => _confidenceThresholdPpm = confidenceThresholdPpm;

        public AiModelMode Mode => (AiModelMode)Volatile.Read(ref _mode);
        public bool IsSuspended => Volatile.Read(ref _suspended) == 1;
        public long ConfidencePpm => Volatile.Read(ref _confidencePpm);
        public long RemoteFailures => Volatile.Read(ref _remoteFailures);
        public long FallbackSwitches => Volatile.Read(ref _fallbackSwitches);

        /// <summary>시그널 출력 허용 여부 — Suspend 되지 않은 동안만 true(모드는 무관 — Local 도 정상 출력 허용).</summary>
        public bool CanEmitSignal => Volatile.Read(ref _suspended) == 0;

        /// <summary>
        /// 원격 LLM 호출 실패 감지(★0ms 원자 전이). Remote 모드였다면 단일 Interlocked.Exchange 로 LocalFallback
        /// 으로 스위칭 — 중간 상태 없이 즉시 전환(호출 스레드가 실패를 본 순간 이미 폴백 완료 상태 관측 보장).
        /// </summary>
        public AiModelMode NoteRemoteFailure()
        {
            Interlocked.Increment(ref _remoteFailures);
            long prev = Interlocked.Exchange(ref _mode, (long)AiModelMode.LocalFallback);
            if (prev == (long)AiModelMode.Remote) Interlocked.Increment(ref _fallbackSwitches);
            return Mode;
        }

        /// <summary>원격 복구 시 수동 복귀(자동 승격 없음 — 재발 방지를 위한 명시적 재무장 계약).</summary>
        public void RestoreRemote() => Volatile.Write(ref _mode, (long)AiModelMode.Remote);

        /// <summary>
        /// 모델 신뢰도 스코어 반영(δ=0). 임계(ppm) 미달 시 ★sticky Suspend 래치 — 이후 CanEmitSignal=false 로
        /// 고정(ReArm 전까지). 신뢰도 자체는 항상 최신값으로 갱신(관측용).
        /// </summary>
        public bool NoteConfidence(long confidencePpm)
        {
            Volatile.Write(ref _confidencePpm, confidencePpm);
            if (confidencePpm < _confidenceThresholdPpm)
                Interlocked.Exchange(ref _suspended, 1);
            return IsSuspended;
        }

        /// <summary>물리/운영자 재무장(원인 규명 후에만). Suspend 해제 — 모드는 유지(Remote 복귀는 RestoreRemote 별도).</summary>
        public void ReArm() => Interlocked.Exchange(ref _suspended, 0);
    }
}
