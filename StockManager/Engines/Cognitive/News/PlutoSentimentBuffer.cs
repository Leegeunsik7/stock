// ============================================================================
// PlutoSentimentBuffer — v12.18.63-DEV: 플루토 브레인 1단계 — 종목별 최신 뉴스 촉매 고정 슬롯(다중 작성자 seqlock · 판독 δ=0)
//
//   슬롯 64개(개방 주소 · 선형 탐사). 종목 키 = KRX 종목코드(long ≤ 999999) 또는 미국 티커 Pack(long · 상위 바이트 ASCII) — 두 도메인 충돌 0.
//   작성(Publish · 임의 스레드 · 분석 워커): 키 = CAS 선점(0 → symbol) · 슬롯 = 버전 CAS 잠금(짝수 v → 홀수 v+1) → 판정 기록 → v+2 release 발행.
//     우세 규칙: 새 판정은 |새 감쇄 점수| ≥ |기존 감쇄 점수| 일 때만 교체(약한 후속 기사가 강한 공시를 덮지 않음) · 새 구조적 악재는 비구조적-악재 판정을 항상 교체 ·
//     기존이 충격 수준 구조적 악재(실효 ≤ −60)인 동안 호재로는 교체 불가(악재 고착 · fail-closed). 노이즈/무결성 위반 = 거부.
//   판독(TryRead · 아레나 정산 스레드): 버전 브래킷(홀수 = 기록 중 재시도 · 전후 불일치 = 재시도) · 상한 MaxReadSpins 초과 = false(경합 계수).
//   ★δ=0: Publish/TryRead 힙 할당 0(고정 배열 · 값 타입 복사). ★슬롯 포화 = 폐기(BufferFull · 재시도 큐잉 0).
//   [v12.18.64-DEV] 가상 시계 오버로드: Publish(in f, IVirtualClock) · TryReadTag(symbol, IVirtualClock, out PlutoNewsTag) —
//     합성/리플레이 틱 도메인에서도 우세 비교 · 감쇄가 같은 '지금'으로 재현된다(시계 판독 = 인터페이스 정수 게터 · 할당 0).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.News
{
    public enum PlutoPublishResult : byte
    {
        Published = 0,
        Superseded = 1,
        Rejected = 2,
        BufferFull = 3,
    }

    public sealed class PlutoSentimentBuffer
    {
        public const int Capacity = 64;
        private const int HashShift = 64 - 6;          // 2^6 = Capacity
        public const int MaxReadSpins = 1024;

        private struct Slot
        {
            public long Ver;
            public PlutoSentimentFactor Factor;
        }

        private readonly long[] _keys = new long[Capacity];
        private readonly Slot[] _slots = new Slot[Capacity];
        private long _seq, _published, _superseded, _rejected, _full, _contended;

        public long Published => Interlocked.Read(ref _published);
        public long SupersededCount => Interlocked.Read(ref _superseded);
        public long RejectedCount => Interlocked.Read(ref _rejected);
        public long FullCount => Interlocked.Read(ref _full);
        public long ContendedReads => Interlocked.Read(ref _contended);
        public long LastSeq => Interlocked.Read(ref _seq);

        /// <summary>선점된 종목 슬롯 수.</summary>
        public int Count
        {
            get
            {
                int n = 0;
                for (int i = 0; i < Capacity; i++) if (Volatile.Read(ref _keys[i]) != 0) n++;
                return n;
            }
        }

        /// <summary>판정 게시(임의 스레드). nowUtcTicks = 우세 비교 기준 시각(분석기 시계).</summary>
        public PlutoPublishResult Publish(in PlutoSentimentFactor factor, long nowUtcTicks)
        {
            if (!factor.IsWellFormed || factor.Category == CatalystCategory.Noise)
            {
                Interlocked.Increment(ref _rejected);
                return PlutoPublishResult.Rejected;
            }
            int i = Claim(factor.Symbol);
            if (i < 0)
            {
                Interlocked.Increment(ref _full);
                return PlutoPublishResult.BufferFull;
            }
            ref Slot s = ref _slots[i];
            long v = Lock(ref s.Ver);
            PlutoPublishResult r;
            if (s.Factor.Symbol == factor.Symbol && Dominates(in s.Factor, in factor, nowUtcTicks))
                r = PlutoPublishResult.Superseded;
            else
            {
                s.Factor = factor.WithSeq(Interlocked.Increment(ref _seq));
                r = PlutoPublishResult.Published;
            }
            Volatile.Write(ref s.Ver, v + 2);
            Interlocked.Increment(ref r == PlutoPublishResult.Published ? ref _published : ref _superseded);
            return r;
        }

        /// <summary>[v12.18.64-DEV] 가상 시계 기준 게시.</summary>
        public PlutoPublishResult Publish(in PlutoSentimentFactor factor, IVirtualClock clock)
        {
            ArgumentNullException.ThrowIfNull(clock);
            return Publish(in factor, clock.UtcNowTicks);
        }

        /// <summary>[v12.18.64-DEV] 종목 판정 판독 + 가상 시계 기준 감쇄 태그(δ=0). 미기록 · 경합 = false(tag = default).</summary>
        public bool TryReadTag(long symbol, IVirtualClock clock, out PlutoNewsTag tag)
        {
            ArgumentNullException.ThrowIfNull(clock);
            if (!TryRead(symbol, out var f)) { tag = default; return false; }
            tag = new PlutoNewsTag(in f, clock.UtcNowTicks);
            return true;
        }

        /// <summary>종목 최신 판정 판독(δ=0). 미기록 · 경합 상한 초과 = false.</summary>
        public bool TryRead(long symbol, out PlutoSentimentFactor factor)
        {
            factor = default;
            if (symbol <= 0) return false;
            int i = Find(symbol);
            if (i < 0) return false;
            ref Slot s = ref _slots[i];
            for (int k = 0; k < MaxReadSpins; k++)
            {
                long v1 = Volatile.Read(ref s.Ver);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                factor = s.Factor;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref s.Ver) == v1)
                {
                    if (factor.Symbol == symbol) return true;
                    factor = default;            // 키 선점 직후 · 첫 기록 전(publish-before-init 창) = 미기록
                    return false;
                }
            }
            factor = default;
            Interlocked.Increment(ref _contended);
            return false;
        }

        /// <summary>기존 판정이 새 판정을 막는가(true = 기존 유지).</summary>
        private static bool Dominates(in PlutoSentimentFactor current, in PlutoSentimentFactor incoming, long nowUtcTicks)
        {
            int cur = current.DecayedScore(nowUtcTicks);
            bool curStructNeg = current.Category == CatalystCategory.StructuralNegative;
            if (curStructNeg && cur * current.Confidence / 100 <= PlutoNewsTag.ShockScore && incoming.Score > 0)
                return true;                                   // 충격 수준 악재 고착 — 호재로 덮지 않는다
            if (incoming.Category == CatalystCategory.StructuralNegative && !curStructNeg)
                return false;                                  // 구조적 악재는 비(非)구조적 악재 판정을 항상 교체(fail-closed)
            return Math.Abs(incoming.DecayedScore(nowUtcTicks)) < Math.Abs(cur);
        }

        private static long Lock(ref long ver)
        {
            var spin = new SpinWait();
            while (true)
            {
                long v = Volatile.Read(ref ver);
                if ((v & 1) == 0 && Interlocked.CompareExchange(ref ver, v + 1, v) == v) return v;
                spin.SpinOnce(-1);
            }
        }

        private static int Home(long symbol) => (int)(((ulong)symbol * 0x9E3779B97F4A7C15UL) >> HashShift);

        private int Claim(long symbol)
        {
            int h = Home(symbol);
            for (int p = 0; p < Capacity; p++)
            {
                int i = (h + p) & (Capacity - 1);
                long k = Volatile.Read(ref _keys[i]);
                if (k == symbol) return i;
                if (k == 0)
                {
                    long prev = Interlocked.CompareExchange(ref _keys[i], symbol, 0);
                    if (prev == 0 || prev == symbol) return i;
                }
            }
            return -1;
        }

        private int Find(long symbol)
        {
            int h = Home(symbol);
            for (int p = 0; p < Capacity; p++)
            {
                int i = (h + p) & (Capacity - 1);
                long k = Volatile.Read(ref _keys[i]);
                if (k == symbol) return i;
                if (k == 0) return -1;
            }
            return -1;
        }
    }
}
