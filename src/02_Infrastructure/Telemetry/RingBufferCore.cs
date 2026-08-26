// ============================================================================
// RingBufferCore — S20 #1: 락-프리 SPMC 링버퍼 (LMAX Disruptor 계열)
//
//  단일 생산자(Single-Producer) → 다중 소비자(Multi-Consumer) 무지연 복사 분배.
//  의존성 0 · 순수 C# · 락 0 (Volatile + seqlock 검증).
//
//  ── 정직 경계 ──────────────────────────────────────────────────────────────
//  · 명분은 "거래 지연(ns)"이 아니다 — 라이브 지연 바닥은 키움 브로커 왕복(~100ms+)이며
//    Native AOT 는 WinForms+WebView2 가 원천 차단한다. 본 프리미티브의 실익은
//    "재사용 가능한 고성능 분배 코어 + 온실 백테스트/틱 처리량"이다.
//  · 캐시 라인 패딩(64B)은 거짓 공유(false sharing) 완화책이며 best-effort 다 —
//    .NET GC 는 객체를 이동시킬 수 있어 C++ 급 물리 정렬 보장은 아니다. StructLayout 으로
//    커서를 단독 캐시 라인에 배치해 생산자·소비자 커서 간 캐시 라인 thrashing 을 줄인다.
//
//  ── torn read 차단 (seqlock) ───────────────────────────────────────────────
//  생산자는 슬롯 덮어쓰기 전 Seq=long.MinValue 로 무효화 → 값 기록 → Seq=시퀀스 커밋.
//  소비자는 Seq 를 읽고(s1) 값을 복사한 뒤 Seq 를 다시 읽어(s2) s1==s2==기대시퀀스일
//  때만 값을 채택한다. 쓰기 도중이면 재시도(bounded), 추월당했으면 fast-forward(드롭 계측).
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Common
{
    /// <summary>64B 캐시 라인에 단독 배치되는 시퀀스 카운터 — 거짓 공유 완화용 패딩 프리미티브.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    public struct PaddedSequence
    {
        [FieldOffset(64)] public long Value;   // 앞뒤 64B 패딩 사이에 단독 배치
    }

    public sealed class RingBufferCore<T> where T : struct
    {
        private const long Writing = long.MinValue;   // 슬롯 쓰기 중 무효화 마커

        private struct Slot { public long Seq; public T Value; }

        private readonly Slot[] _slots;
        private readonly int    _capacity;
        private readonly long   _mask;
        private PaddedSequence  _producer;   // 최고 발행 시퀀스 (단독 캐시 라인)

        /// <param name="capacity">2 이상의 2의 거듭제곱.</param>
        public RingBufferCore(int capacity)
        {
            if (capacity < 2 || (capacity & (capacity - 1)) != 0)
                throw new ArgumentException("capacity 는 2 이상의 2의 거듭제곱이어야 합니다.", nameof(capacity));
            _capacity = capacity;
            _mask     = capacity - 1;
            _slots    = new Slot[capacity];
            for (int i = 0; i < capacity; i++) _slots[i].Seq = -1L;
            _producer.Value = -1L;
        }

        public int  Capacity  => _capacity;
        /// <summary>최고 발행 시퀀스(-1 = 미발행).</summary>
        public long Published => Volatile.Read(ref _producer.Value);

        // ── 생산자 (단일 스레드 전용) ──────────────────────────────────────────

        /// <summary>틱 1건 발행. <b>단일 생산자 전용</b> — 동시 호출 미지원(SPMC).</summary>
        public void Publish(in T item)
        {
            long seq = _producer.Value + 1L;            // 단일 라이터: 자기 커서 비경합 읽기
            int  idx = (int)(seq & _mask);

            Volatile.Write(ref _slots[idx].Seq, Writing);   // 무효화(쓰기 중 표시)
            _slots[idx].Value = item;                        // 값 기록
            Volatile.Write(ref _slots[idx].Seq, seq);        // 커밋(release)
            Volatile.Write(ref _producer.Value, seq);        // 발행
        }

        public RingConsumer<T> CreateConsumer() => new RingConsumer<T>(this);

        // ── 소비자 read 코어 (seqlock 검증) ────────────────────────────────────

        internal bool TryRead(ref long cursor, out T item, out long dropped)
        {
            dropped = 0L;
            long published = Volatile.Read(ref _producer.Value);
            long next      = cursor + 1L;
            if (next > published) { item = default; return false; }   // 신규 없음

            // 추월(lap) 감지 — 생산자가 한 바퀴 이상 앞섰으면 가장 오래된 유효로 점프
            if (published - next >= _capacity)
            {
                long oldest = published - _capacity + 1L;
                dropped = oldest - next;
                next     = oldest;
            }

            for (int attempt = 0; attempt < 32; attempt++)
            {
                int  idx = (int)(next & _mask);
                long s1  = Volatile.Read(ref _slots[idx].Seq);
                if (s1 == next)
                {
                    T    val = _slots[idx].Value;                 // 값 복사
                    long s2  = Volatile.Read(ref _slots[idx].Seq);
                    if (s2 == next) { item = val; cursor = next; return true; }
                    continue;                                     // torn — 재시도
                }

                // 슬롯이 무효화(쓰기 중) 또는 추월당함 → 최신 발행 기준 재평가
                published = Volatile.Read(ref _producer.Value);
                long oldest = published - _capacity + 1L;
                if (oldest < 0L) oldest = 0L;
                if (next < oldest) { dropped += oldest - next; next = oldest; }
                // next >= oldest 이면 일시적 쓰기 중 — 재시도
            }

            item = default; cursor = next; return false;          // 극단 경합 — 이번 호출 포기
        }
    }

    // ── 소비자 (각자 독립 커서 · 단독 캐시 라인) ──────────────────────────────────

    public sealed class RingConsumer<T> where T : struct
    {
        private readonly RingBufferCore<T> _buffer;
        private PaddedSequence _cursor;   // 소비자 전용 커서 (단독 캐시 라인 → 생산자와 거짓 공유 차단)
        private long _dropped;

        internal RingConsumer(RingBufferCore<T> buffer)
        {
            _buffer = buffer;
            _cursor.Value = -1L;
        }

        /// <summary>마지막으로 소비한 시퀀스(-1 = 미소비).</summary>
        public long Cursor  => Volatile.Read(ref _cursor.Value);
        /// <summary>추월로 인해 건너뛴(유실된) 항목 누적 수.</summary>
        public long Dropped => Interlocked.Read(ref _dropped);

        /// <summary>다음 항목을 복사해 반환. 신규 없으면 false. 추월 시 가장 오래된 유효로 전진(Dropped 증가).</summary>
        public bool TryRead(out T item)
        {
            long c  = _cursor.Value;
            bool ok = _buffer.TryRead(ref c, out item, out long dropped);
            _cursor.Value = c;
            if (dropped > 0L) Interlocked.Add(ref _dropped, dropped);
            return ok;
        }
    }
}
