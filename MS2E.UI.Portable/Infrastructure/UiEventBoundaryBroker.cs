// ============================================================================
// UiEventBoundaryBroker — v10.1.0 Phase 94-A: ★Think Beyond Gemini — 어셈블리 경계 이벤트 브로커
//   "UI 어셈블리 분리의 두 파멸 리스크(교차 어셈블리 할당 유출 · 미관측 이벤트 핸들러 누수)를 δ=0 struct 링 + poll
//    모델 하나로 통합 격리."
//
//  ── 왜 제미나이 A(멀티플렉서)/B(경계 센티널)를 초월하는가 ─────────────────────────────────
//  UI 컴포넌트가 별도 어셈블리(MS2E.UI.Portable)로 분리되면 Core↔UI 이벤트 전달에 두 리스크가 생긴다: ①C# `event`
//  델리게이트를 매 이벤트 할당/구독하면 UI 핫패스에 GC 압박(교차 어셈블리 할당 유출) ②Core 가 UI 컴포넌트의 델리게이트를
//  강참조로 붙들면 UI Dispose 후에도 좀비 구독이 쌓인다(미관측 이벤트 핸들러 누수 — .NET 고전 누수). 답은 '델리게이트를
//  아예 쓰지 않는 값타입 링 + poll 모델': Core 는 값타입 이벤트를 링에 '발행'만(델리게이트 할당 0 → ①해소), UI 는 자기
//  리듬으로 'Drain(poll)'(→Core 가 UI 참조/델리게이트 0 보유 → ②구조적 불가). 구독을 발행-폴로 뒤집어 두 리스크 동시 봉쇄.
//
//  ── 정확성(lock-free MPSC·적대검증 교정) ─────────────────────────────────────────────
//  ★publish-before-populate 금지: Publish 는 페이로드를 먼저 쓰고 슬롯 Seq 스탬프를 release(Volatile.Write)로 '마지막'에
//  발행한다. Drain 은 슬롯 Seq 를 acquire(Volatile.Read)로 관측해 'Seq==기대 pos'일 때만 발행 완료로 간주하고, 페이로드
//  판독 후 Seq 를 재검(seqlock)해 판독 중 랩-덮어쓰기를 탐지한다. in-flight(클레임됐으나 미발행) 지점에서 연속 배수를
//  정지 → phantom 배달·이중 계수 없음. 소비자가 cap 이상 뒤처진 복구불가 유실만 drop 계수.
//  불변: Drained + Dropped ≤ Published(동시 덮어쓰기 창의 극소수만 미계수·advisory best-effort).
//
//  ★불변: δ=0(Publish/Drain 핫패스 힙 0 — 값타입 struct 링·Interlocked·Volatile·caller span) · 결정론(seq 단조) ·
//    liveArmed:false(순수 UI 이벤트 전달 — IBroker·소켓·실 발주 유발 0).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.UI.Portable.Infrastructure
{
    /// <summary>경계 이벤트(불변 값타입·Drain 출력). Kind=종류, A/B=페이로드, Seq=단조 발행 순번.</summary>
    public readonly record struct UiBoundaryEvent(long Kind, long A, long B, long Seq);

    /// <summary>δ=0 struct 링 + poll 모델 경계 이벤트 브로커. Core 발행(MP·release)·UI 배수(SC·acquire+seqlock). 델리게이트 0.</summary>
    public sealed class UiEventBoundaryBroker
    {
        // 내부 슬롯(가변) — Seq 스탬프를 페이로드와 분리해 release/acquire 발행 펜스로 사용.
        private struct Slot { public long Seq; public long Kind; public long A; public long B; }

        private readonly Slot[] _ring;
        private readonly int _mask;
        private long _writeSeq;   // 클레임 카운터(MP)
        private long _readSeq;    // 소비 커서(단일 소비자)
        private long _published, _drained, _dropped;

        public UiEventBoundaryBroker(int capacityPow2 = 256)
        {
            int cap = RoundUpPow2(Math.Clamp(capacityPow2, 8, 1 << 16));   // ★상한 클램프(오버플로/거대 할당 봉쇄)
            _ring = new Slot[cap];
            for (int i = 0; i < cap; i++) _ring[i].Seq = -1L;              // 미발행 표식(pos=0 과 충돌 방지)
            _mask = cap - 1;
        }

        public long Published => Interlocked.Read(ref _published);
        public long Drained   => Interlocked.Read(ref _drained);
        public long Dropped   => Interlocked.Read(ref _dropped);
        public int  Capacity  => _ring.Length;

        /// <summary>★Core 발행(δ=0·MP). 페이로드 먼저 기입 → Seq 스탬프를 release 로 마지막 발행. 링 랩 시 최오래된 것 덮어씀.</summary>
        public void Publish(long kind, long a, long b)
        {
            long pos = Interlocked.Increment(ref _writeSeq) - 1L;
            int idx = (int)(pos & _mask);
            _ring[idx].Kind = kind;
            _ring[idx].A = a;
            _ring[idx].B = b;
            Volatile.Write(ref _ring[idx].Seq, pos);   // ★release: 이 시점 이후에만 pos 가 '발행됨'으로 관측
            Interlocked.Increment(ref _published);
        }

        /// <summary>
        /// ★UI 배수(δ=0·단일 소비자). 발행 완료(Seq==pos)된 이벤트만 연속 배수, in-flight 지점에서 정지(phantom·이중계수 방지).
        /// 소비자가 cap 이상 뒤처진 복구불가 유실만 drop 계수. 판독 중 랩-덮어쓰기는 seqlock 재검으로 탐지·정지.
        /// </summary>
        public int Drain(Span<UiBoundaryEvent> dest)
        {
            if (dest.Length == 0) return 0;
            long w = Interlocked.Read(ref _writeSeq);
            long r = _readSeq;
            if (w - r <= 0L) return 0;

            long cap = _ring.Length;
            if (w - r > cap)   // 소비자가 cap 이상 뒤처짐 → 복구불가 오래된 것 유실
            {
                long drop = (w - r) - cap;
                r += drop;
                Interlocked.Add(ref _dropped, drop);
            }

            int n = 0;
            while (n < dest.Length && (r + n) < w)
            {
                long pos = r + n;
                int idx = (int)(pos & _mask);
                long s1 = Volatile.Read(ref _ring[idx].Seq);   // acquire
                if (s1 != pos) break;                          // in-flight(미발행)/랩 경계 → 연속 배수 정지
                long kind = _ring[idx].Kind, a = _ring[idx].A, b = _ring[idx].B;
                long s2 = Volatile.Read(ref _ring[idx].Seq);   // seqlock 재검
                if (s2 != pos) break;                          // 판독 중 덮어써짐 → torn 배달 방지·정지
                dest[n] = new UiBoundaryEvent(kind, a, b, pos);
                n++;
            }
            _readSeq = r + n;
            Interlocked.Add(ref _drained, n);
            return n;
        }

        private static int RoundUpPow2(int v)
        {
            v--; v |= v >> 1; v |= v >> 2; v |= v >> 4; v |= v >> 8; v |= v >> 16; v++;
            return v < 8 ? 8 : v;
        }
    }
}
