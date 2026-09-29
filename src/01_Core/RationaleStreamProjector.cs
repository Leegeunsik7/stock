// ============================================================================
// RationaleStreamProjector — v12.6.0 Think Beyond Gemini Idea A: Zero-Alloc Streaming Rationale Virtualizer
//
//   ★정직 회계: 이 저장소의 AiService(v3.8)는 현재 CompleteAsync 단발 응답만 제공하며 실제 LLM 토큰 단위
//   SSE/스트리밍 API 는 아직 배선되어 있지 않다(확인됨). 따라서 "진짜 실시간 토큰 스트림"을 날조하지 않고,
//   ★스트리밍이 도입될 때 Blazor 쪽이 그대로 재사용할 수 있는 '무할당 버퍼→DOM 폴링' 매핑 메커니즘 자체를
//   제공한다 — ZeroAllocTokenStreamBuffer(v12.4.0, ArrayPool&lt;char&gt;)에 토큰(또는 배치 텍스트를 단어 단위로
//   분할한 조각)을 누적하고, 단조 Version 카운터로 Blazor 컴포넌트가 StateHasChanged() 호출 시점을 델타 감지
//   (매 조각마다 리렌더가 아니라 폴링 간격마다 1회만 최신 View 를 읽어 DOM 가상화 트리에 반영)하게 한다.
//
//   불변: 결정론 · δ=0(AppendToken/Version 정상 경로 힙 0 — 내부 ZeroAllocTokenStreamBuffer 위임) ·
//        liveArmed:false(서술 텍스트 렌더 파이프라인 — 발주 경로 무접촉).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    /// <summary>ZeroAllocTokenStreamBuffer + 단조 Version 카운터 — Blazor 폴링 컴포넌트가 리렌더 시점을 판단하는 데 사용.</summary>
    public sealed class RationaleStreamProjector : IDisposable
    {
        private readonly ZeroAllocTokenStreamBuffer _buf;
        private long _version;

        public RationaleStreamProjector(int initialCapacity = 2048) => _buf = new ZeroAllocTokenStreamBuffer(initialCapacity);

        /// <summary>토큰(또는 단어) 조각 추가(★정상 경로 δ=0) + Version 원자 증가(폴링 컴포넌트 델타 감지용).</summary>
        public void AppendToken(ReadOnlySpan<char> token)
        {
            _buf.Append(token);
            Interlocked.Increment(ref _version);
        }

        /// <summary>단조 Version — 직전 폴링 이후 변경 여부 판단(불일치 시에만 StateHasChanged 호출 권장).</summary>
        public long Version => Interlocked.Read(ref _version);

        /// <summary>현재까지 누적된 전체 텍스트(δ=0 뷰 — 사본 없음).</summary>
        public ReadOnlySpan<char> View => _buf.View;

        /// <summary>새 스트림 개시 — 버퍼 비움(재대여 없음) + Version 리셋.</summary>
        public void Reset()
        {
            _buf.Clear();
            Interlocked.Exchange(ref _version, 0);
        }

        public void Dispose() => _buf.Dispose();
    }
}
