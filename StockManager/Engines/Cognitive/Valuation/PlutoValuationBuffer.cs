// ============================================================================
// PlutoValuationBuffer — v12.18.65-DEV: 플루토 브레인 2단계 — 종목별 최신 밸류에이션 판정 고정 슬롯(다중 작성자 seqlock · 판독 δ=0)
//
//   슬롯 64개(개방 주소 · 선형 탐사). 종목 키 = KRX 종목코드(long ≤ 999999) 또는 미국 티커 Pack(long) — PlutoSentimentBuffer 와 동일 키 도메인.
//   작성(Publish · 임의 스레드 · 평가 엔진): 키 = CAS 선점(0 → symbol) · 슬롯 = 버전 CAS 잠금(짝수 v → 홀수 v+1) → 판정 기록 → v+2 release 발행.
//     우세 규칙 없음(최신 재무 = 최신 판정 · 후기록 우선) — 단 재무 기준 시각이 기존보다 과거인 판정은 Stale 거부(지연 도착 구 재무가 신 재무를 덮지 않음).
//     무결성 위반 = 거부(Rejected).
//   판독(TryRead · 아레나 정산 스레드): 버전 브래킷(홀수 = 기록 중 재시도 · 전후 불일치 = 재시도) · 상한 MaxReadSpins 초과 = false(경합 계수).
//   ★δ=0: Publish/TryRead 힙 할당 0(고정 배열 · 값 타입 복사). ★슬롯 포화 = 폐기(BufferFull · 재시도 큐잉 0).
// ============================================================================
using System;
using System.Threading;

namespace StockManager.Engines.Cognitive.Valuation
{
    public enum PlutoValuationPublishResult : byte
    {
        Published = 0,
        Stale = 1,
        Rejected = 2,
        BufferFull = 3,
    }

    public sealed class PlutoValuationBuffer
    {
        public const int Capacity = 64;
        private const int HashShift = 64 - 6;          // 2^6 = Capacity
        public const int MaxReadSpins = 1024;

        private struct Slot
        {
            public long Ver;
            public PlutoValuationFactor Factor;
        }

        private readonly long[] _keys = new long[Capacity];
        private readonly Slot[] _slots = new Slot[Capacity];
        private long _seq, _published, _stale, _rejected, _full, _contended;

        public long Published => Interlocked.Read(ref _published);
        public long StaleCount => Interlocked.Read(ref _stale);
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

        /// <summary>판정 게시(임의 스레드).</summary>
        public PlutoValuationPublishResult Publish(in PlutoValuationFactor factor)
        {
            if (!factor.IsWellFormed)
            {
                Interlocked.Increment(ref _rejected);
                return PlutoValuationPublishResult.Rejected;
            }
            int i = Claim(factor.Symbol);
            if (i < 0)
            {
                Interlocked.Increment(ref _full);
                return PlutoValuationPublishResult.BufferFull;
            }
            ref Slot s = ref _slots[i];
            long v = Lock(ref s.Ver);
            PlutoValuationPublishResult r;
            if (s.Factor.Symbol == factor.Symbol && factor.TimestampUtcTicks < s.Factor.TimestampUtcTicks)
                r = PlutoValuationPublishResult.Stale;
            else
            {
                s.Factor = factor.WithSeq(Interlocked.Increment(ref _seq));
                r = PlutoValuationPublishResult.Published;
            }
            Volatile.Write(ref s.Ver, v + 2);
            Interlocked.Increment(ref r == PlutoValuationPublishResult.Published ? ref _published : ref _stale);
            return r;
        }

        /// <summary>종목 최신 판정 판독(δ=0). 미기록 · 경합 상한 초과 = false.</summary>
        public bool TryRead(long symbol, out PlutoValuationFactor factor)
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
