// ============================================================================
// LockFreeTokenBucket — [v12.18.21-DEV] 브로커 호출 상한 락프리 게이트(슬라이딩 창 허가 기록 링 · 단일 태그 CAS)
//
//  왜 '고전 토큰 버킷'이 아닌가: 용량 C · 초당 리필 r 버킷은 만충 버스트 C 직후 1초 동안 r 개를 더 내준다 → 임의 1초 창 최대 C + r
//  (C = r = 5 이면 10건). 증권사 한도(키움 조회/주문 TR 초당 5회 · 1분 100회)는 '임의 창' 기준이라 그 버킷으로는 보증이 안 된다.
//  여기서는 최근 허가 시각을 링에 기록하고, k 번째 허가는 t(k) − t(k − N) ≥ W 일 때만 낸다(창마다 독립 판정 · 최대 2창).
//
//  ── 원자성(태그 CAS) ────────────────────────────────────────────────────
//   머리 워드 = (허가 시각 µs << 16) | (허가 순번 하위 16비트). 허가 = 머리 CAS 1회 — 시각과 순번이 한 워드로 같이 전진하므로
//   기록 시각은 순번 순서로 단조(머리의 직전 시각과 max)다. 링 슬롯도 같은 패킹(시각 | 순번 태그) 한 워드 — CAS 승자가 사후 발행.
//   링 크기 R = N 이상 2의 거듭제곱(≤ 4096) — 16비트 태그 공간을 나누어 순번 랩에도 슬롯 위치가 연속이다(태그 혼동 = R < 65536 으로 불가).
//   판정 대상 슬롯 k − N 이 아직 미발행(태그 불일치 · 승자 선점 중)이면 거부(fail-closed · 대기/재시도 없음).
//
//  ── 보증(수학) ──────────────────────────────────────────────────────────
//   (1) t 는 순번 k 에 대해 비감소(머리 CAS 가 max(now, t(k−1)) 를 기록). (2) 허가 k 는 t(k) − t(k − N) ≥ W 를 확인했다.
//   임의 반개구간 [a, a + W) 에 허가 N + 1 건이 있다고 하자 — 순번 정렬 k0 < … < kN 이면 kN − N ≥ k0 이고 (1)에 의해
//   t(kN − N) ≥ t(k0) ≥ a → t(kN) ≥ t(kN − N) + W ≥ a + W 로 구간 밖. 모순 → 임의 창 허가 ≤ N. 보증 대상은 '허가 시각'(주입 시계)이다.
//   실 송신 시각은 허가 뒤이므로, 호출측은 허가 직후 곧바로 송신해야 한다(게이트 → 송신 사이 대기 금지).
//
//  ── 불변 ────────────────────────────────────────────────────────────────
//   δ=0(TryAcquire · Remaining 힙 0 — long[] 1회 사전 할당) · 락 0 · 큐잉/자동 재시도 0(CLAUDE.md §2 · 거부 = 호출측 즉시 실패) ·
//   시계 역행 = 직전 시각 유지(창이 앞당겨지지 않음 = 보수적) · 47비트 µs(약 4.4년) 소진 = 영구 거부(fail-closed) · liveArmed 무관(발주 유발 0).
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;

namespace MS2E.Infrastructure
{
    /// <summary>임의 창 W 안 허가 ≤ N(최대 2창) 을 보증하는 락프리 슬라이딩 창 레이트 게이트(δ=0 · 큐 없음).</summary>
    public sealed class LockFreeTokenBucket
    {
        public const long SecondMicros = 1_000_000, MinuteMicros = 60_000_000;
        public const int MaxPerWindow = 4_096;
        private const int TagBits = 16;
        private const long TagMask = (1L << TagBits) - 1;
        private const long MaxMicros = (1L << (63 - TagBits)) - 1;        // 47비트 양수 영역(부호 비트 회피)
        private const int CasSpins = 64;

        private readonly long[] _slots;                                    // (시각 µs << 16) | (순번 & 0xFFFF)
        private readonly int _mask;                                        // R − 1(R = 2의 거듭제곱 ≥ max(N1, N2))
        private readonly int _n1, _n2;
        private readonly long _w1, _w2;
        private readonly Func<long> _nowTicks;
        private readonly long _freq, _origin, _base;                       // _base = 최장 창 — 초기 슬롯(시각 0)이 즉시 만료로 보이게
        private long _head;                                                // (마지막 허가 시각 µs << 16) | (다음 순번 & 0xFFFF)
        private long _granted, _denied, _contended, _unpublished;

        /// <param name="perWindow">창 1 허가 상한 N1(≥ 1).</param>
        /// <param name="windowMicros">창 1 길이 W1(µs · ≥ 1).</param>
        /// <param name="perWindow2">창 2 상한 N2(0 = 미사용).</param>
        /// <param name="window2Micros">창 2 길이 W2(µs).</param>
        /// <param name="nowTicks">단조 시계(기본 Stopwatch.GetTimestamp).</param>
        /// <param name="tickFrequency">시계 주파수(기본 Stopwatch.Frequency).</param>
        public LockFreeTokenBucket(int perWindow, long windowMicros, int perWindow2 = 0, long window2Micros = 0,
                                   Func<long>? nowTicks = null, long tickFrequency = 0)
        {
            if (perWindow < 1 || perWindow > MaxPerWindow) throw new ArgumentOutOfRangeException(nameof(perWindow));
            if (windowMicros < 1) throw new ArgumentOutOfRangeException(nameof(windowMicros));
            if (perWindow2 < 0 || perWindow2 > MaxPerWindow) throw new ArgumentOutOfRangeException(nameof(perWindow2));
            if (perWindow2 > 0 && window2Micros < 1) throw new ArgumentOutOfRangeException(nameof(window2Micros));
            _n1 = perWindow; _w1 = windowMicros;
            _n2 = perWindow2; _w2 = perWindow2 > 0 ? window2Micros : 0;
            int cap = 1;
            while (cap < Math.Max(_n1, _n2)) cap <<= 1;
            _mask = cap - 1;
            _nowTicks = nowTicks ?? Stopwatch.GetTimestamp;
            _freq = tickFrequency > 0 ? tickFrequency : Stopwatch.Frequency;
            _origin = _nowTicks();
            _base = Math.Max(_w1, _w2);
            _slots = new long[cap];
            for (int i = 0; i < cap; i++) _slots[i] = (long)(i - cap) & TagMask;     // 시각 0 · 태그 = 순번 i − R(1바퀴 전 '발행 완료')
            _head = _base << TagBits;                                                // 다음 순번 0 · 직전 시각 = _base
        }

        public int PerWindow => _n1;
        public long WindowMicros => _w1;
        public int PerWindow2 => _n2;
        public long Window2Micros => _w2;
        public long Granted => Interlocked.Read(ref _granted);
        public long Denied => Interlocked.Read(ref _denied);
        /// <summary>CAS 경합 한도 초과로 거부한 건수(Denied 에 포함).</summary>
        public long Contended => Interlocked.Read(ref _contended);
        /// <summary>판정 슬롯이 미발행(승자 선점 중)이라 거부한 건수(Denied 에 포함).</summary>
        public long Unpublished => Interlocked.Read(ref _unpublished);

        /// <summary>허가 1건(δ=0 · 락 0). false = 상한 · 경합 · 미발행 · 시계 소진 — 호출측은 즉시 실패(대기열 금지).</summary>
        public bool TryAcquire()
        {
            long now = NowMicros();
            for (int spin = 0; spin < CasSpins; spin++)
            {
                long h = Volatile.Read(ref _head);
                long last = h >> TagBits, seq = h & TagMask;
                long t = now > last ? now : last;                              // 순번 순서 단조(역행 = 직전 유지)
                if (t > MaxMicros) return Deny(ref _denied);
                int verdict = Verdict(seq, t, _n1, _w1);
                if (verdict == Open && _n2 > 0) verdict = Verdict(seq, t, _n2, _w2);
                if (verdict != Open)
                {
                    if (verdict == NotPublished) Interlocked.Increment(ref _unpublished);
                    return Deny(ref _denied);
                }
                long next = (t << TagBits) | ((seq + 1) & TagMask);
                if (Interlocked.CompareExchange(ref _head, next, h) != h) { now = NowMicros(); continue; }
                Volatile.Write(ref _slots[(int)(seq & _mask)], (t << TagBits) | seq);   // 발행(순번 seq 의 허가 시각)
                Interlocked.Increment(ref _granted);
                return true;
            }
            Interlocked.Increment(ref _contended);
            return Deny(ref _denied);
        }

        /// <summary>지금 즉시 낼 수 있는 허가 수(관측 · δ=0). 미발행 슬롯은 사용 중으로 센다(보수적).</summary>
        public int Remaining()
        {
            long h = Volatile.Read(ref _head);
            long now = NowMicros(), last = h >> TagBits, seq = h & TagMask;
            long t = now > last ? now : last;
            int r = Free(seq, t, _n1, _w1);
            if (_n2 > 0) r = Math.Min(r, Free(seq, t, _n2, _w2));
            return r;
        }

        // 순번 seq 허가 판정: 슬롯 (seq − n) 이 발행돼 있고 t − 그 시각 ≥ w.
        private const int Open = 0, Full = 1, NotPublished = 2;
        private int Verdict(long seq, long t, int n, long w)
        {
            long k = seq - n;
            long s = Volatile.Read(ref _slots[(int)(k & _mask)]);
            if ((s & TagMask) != (k & TagMask)) return NotPublished;
            return t - (s >> TagBits) >= w ? Open : Full;
        }

        // 최근 n 허가 중 창 w 안(t − 시각 < w)이거나 미발행인 것 = 사용 중.
        private int Free(long seq, long t, int n, long w)
        {
            int used = 0;
            for (int j = 1; j <= n; j++)
            {
                long k = seq - j;
                long s = Volatile.Read(ref _slots[(int)(k & _mask)]);
                if ((s & TagMask) != (k & TagMask) || t - (s >> TagBits) < w) used++;
            }
            return n - used;
        }

        private long NowMicros()
        {
            long e = _nowTicks() - _origin;
            if (e < 0) e = 0;
            return _base + e / _freq * SecondMicros + e % _freq * SecondMicros / _freq;
        }

        private static bool Deny(ref long counter) { Interlocked.Increment(ref counter); return false; }
    }
}
