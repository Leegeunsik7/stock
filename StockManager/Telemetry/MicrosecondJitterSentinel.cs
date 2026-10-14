// ============================================================================
// MicrosecondJitterSentinel — [v12.18.4-DEV · 방향 A] 무할당 마이크로초 지연 드리프트 센티널(비관리 원형 링 · 정확한 상위 K 꼬리)
//
//  왜: 히스토그램(LatencyHistogram)은 분포를 주지만 '그 스파이크가 언제 · 무엇 때문에' 를 버린다. 100만 표본의 상위 0.001%(= 10건)는
//   백분위 축(‰ 해상도)으로는 보이지도 않는다. 이 센티널은 두 가지를 정확히 남긴다:
//    ① 임계 초과 스파이크(기본 500µs) 전량 → 비관리 원형 링(NativeMemory · GC 밖 · 고정 크기 — 넘치면 가장 오래된 것부터 덮어씀 + Overwritten 계수).
//    ② 전체 표본 중 가장 큰 K 개(기본 16 — 100만 표본의 0.0016%) → 비관리 정렬 배열(삽입 정렬 · K 작음). 임계 미만이어도 꼬리면 남는다.
//   각 레코드 = 순번 · 지연(ns) · 구간 종류(호출자 정의 바이트) · 모의 시각 · 표본 구간 동안 GC 발생 여부(Gen0 수집 횟수 차) · 그 시점 누적 GC 정지(ns).
//  원인 판정(정직 — 관측 가능한 것만): 표본 구간 안에서 GC 수집 횟수가 바뀌었으면 Gc. 아니면 Unattributed(OS 선점 · 캐시/TLB · 타이머 해상도 등 —
//   ETW 없이 프로세스 안에서는 구분 불가 · 추측해서 이름 붙이지 않는다).
//  사용: long t0 = Begin(out int g0); …측정 구간…; End(t0, g0, kind, simMicros). Begin/End = Stopwatch.GetTimestamp + GC.CollectionCount(0)(FCALL · 할당 0).
//  ★스레드 계약: 단일 작성자(측정 스레드). 판독(CopySpikes · CopyTail · 계수)은 같은 스레드 또는 작성 정지 후(콜드).
//  δ=0: Begin · End · Record 힙 0(비관리 메모리 · 인라인 히스토그램 구조체). 생성/Dispose = 콜드(NativeMemory 할당/해제 1회).
//  [v12.18.6-DEV] 관제 사본(JitterSnapshot · seqlock): 드문 경로(스파이크 · 꼬리 진입)와 Reset 에서만 게시 → 임의 스레드 TryReadSnapshot
//   (잠금 · 대기 · 할당 0 · 경합 시 false — 호출자는 직전 값 유지). 정상 표본 경로 명령 추가 0.
// ============================================================================
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace StockManager.Telemetry
{
    public enum JitterCause : byte { Unattributed = 0, Gc = 1 }

    [StructLayout(LayoutKind.Sequential)]
    public struct JitterSpike
    {
        public long Sequence;            // 이 센티널의 표본 순번(1부터)
        public long Nanos;
        public long SimMicros;           // 호출자 모의 시각(실시간 하네스는 0 가능)
        public long GcPauseNanos;        // 기록 시점 누적 GC 정지(스파이크/꼬리 진입 시에만 판독 — 콜드)
        public int GcCollections;        // 표본 구간 동안 Gen0 수집 횟수 차(0 = 구간 안 GC 없음)
        public byte Kind;                // 호출자 정의 구간 종류
        public JitterCause Cause;
    }

    public sealed unsafe class MicrosecondJitterSentinel : IDisposable
    {
        public const long DefaultThresholdNanos = 500_000;
        public const int DefaultRingCapacity = 4096, DefaultTailCapacity = 16;

        private readonly long _thresholdNanos;
        private readonly int _ringCapacity, _tailCapacity;
        private readonly double _nanosPerTick;
        private JitterSpike* _ring, _tail;               // _tail = 내림차순(0 = 최대)
        private long _sequence, _spikes, _gcSpikes, _overwritten, _maxNanos, _sumNanos;
        private int _tailCount;
        private LatencyHistogram _histogram;             // 인라인 108 버킷(관리 배열 0)
        private long _lastSpikeNanos;
        private bool _lastSpikeGc;
        // [v12.18.6-DEV] 관제 seqlock(작성자 단일 · 홀수 = 게시 중) — 관리 필드라 Dispose 뒤에도 판독 안전
        private int _snapSeq, _snapLastGc, _snapTailN, _snapTailGc;
        private long _snapSpikes, _snapGcSpikes, _snapMax, _snapLast, _snapFloor, _snapVersion;

        public MicrosecondJitterSentinel(long thresholdNanos = DefaultThresholdNanos, int ringCapacity = DefaultRingCapacity, int tailCapacity = DefaultTailCapacity)
        {
            if (thresholdNanos <= 0) throw new ArgumentOutOfRangeException(nameof(thresholdNanos));
            if (ringCapacity < 1 || ringCapacity > 1 << 20) throw new ArgumentOutOfRangeException(nameof(ringCapacity));
            if (tailCapacity < 1 || tailCapacity > 1024) throw new ArgumentOutOfRangeException(nameof(tailCapacity));
            _thresholdNanos = thresholdNanos; _ringCapacity = ringCapacity; _tailCapacity = tailCapacity;
            _nanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;
            _ring = (JitterSpike*)NativeMemory.AllocZeroed((nuint)ringCapacity, (nuint)sizeof(JitterSpike));
            _tail = (JitterSpike*)NativeMemory.AllocZeroed((nuint)tailCapacity, (nuint)sizeof(JitterSpike));
        }

        public long ThresholdNanos => _thresholdNanos;
        public int RingCapacity => _ringCapacity;
        public int TailCapacity => _tailCapacity;
        public long Samples => _sequence;
        /// <summary>임계 초과 스파이크 총수(링 덮어씀 포함).</summary>
        public long Spikes => _spikes;
        public long GcSpikes => _gcSpikes;
        public long Overwritten => _overwritten;
        public long MaxNanos => _maxNanos;
        public long MeanNanos => _sequence == 0 ? 0 : _sumNanos / _sequence;
        public int TailCount => _tailCount;
        /// <summary>‰ 백분위(LatencyHistogram 축 — 상대 오차 ≤ 12.5% · 10.5ms 초과는 관측 최대).</summary>
        public long Percentile(int permille) => _histogram.Percentile(permille);

        /// <summary>측정 시작 — 타임스탬프 · 현재 Gen0 수집 횟수(힙 0).</summary>
        public static long Begin(out int gen0)
        {
            gen0 = GC.CollectionCount(0);
            return Stopwatch.GetTimestamp();
        }

        /// <summary>측정 종료 → 기록. 반환 = 이 표본 지연(ns).</summary>
        public long End(long beginTimestamp, int beginGen0, byte kind, long simMicros)
        {
            long ns = (long)((Stopwatch.GetTimestamp() - beginTimestamp) * _nanosPerTick);
            Record(ns, GC.CollectionCount(0) - beginGen0, kind, simMicros);
            return ns;
        }

        /// <summary>이미 잰 지연을 기록(외부 시계 표본 · 테스트 주입).</summary>
        public void Record(long nanos, int gcCollections, byte kind, long simMicros)
        {
            if (_ring == null) throw new ObjectDisposedException(nameof(MicrosecondJitterSentinel));
            if (nanos < 0) nanos = 0;
            long seq = ++_sequence;
            _sumNanos += nanos;
            if (nanos > _maxNanos) _maxNanos = nanos;
            _histogram.Record(nanos);
            bool spike = nanos > _thresholdNanos;
            bool tail = _tailCount < _tailCapacity || nanos > _tail[_tailCount - 1].Nanos;
            if (!spike && !tail) return;                                   // 정상 경로: 여기까지 원자 연산 0 · 판독 2 · 기록 4

            var rec = new JitterSpike
            {
                Sequence = seq, Nanos = nanos, SimMicros = simMicros, Kind = kind, GcCollections = gcCollections,
                Cause = gcCollections != 0 ? JitterCause.Gc : JitterCause.Unattributed,
                GcPauseNanos = GC.GetTotalPauseDuration().Ticks * 100,       // TimeSpan = 구조체(할당 0) · 드문 경로에서만 판독
            };
            if (spike)
            {
                long slot = _spikes % _ringCapacity;
                if (_spikes >= _ringCapacity) _overwritten++;
                _ring[slot] = rec;
                _spikes++;
                if (rec.Cause == JitterCause.Gc) _gcSpikes++;
                _lastSpikeNanos = nanos; _lastSpikeGc = rec.Cause == JitterCause.Gc;
            }
            if (tail)
            {
                // 내림차순 삽입(K ≤ 1024 · 꼬리 진입은 드묾) — 가득 차면 최소(끝) 탈락
                int at = _tailCount < _tailCapacity ? _tailCount++ : _tailCapacity - 1;
                while (at > 0 && _tail[at - 1].Nanos < nanos) { _tail[at] = _tail[at - 1]; at--; }
                _tail[at] = rec;
            }
            PublishSnapshot();
        }

        // 작성자 전용(드문 경로 · Reset). 꼬리 GC 태그 수 = K 회 순회(K 기본 16 · 콜드).
        private void PublishSnapshot()
        {
            int tailGc = 0;
            for (int i = 0; i < _tailCount; i++) if (_tail[i].Cause == JitterCause.Gc) tailGc++;
            Interlocked.Increment(ref _snapSeq);                                    // 홀수(전체 펜스)
            Volatile.Write(ref _snapSpikes, _spikes);
            Volatile.Write(ref _snapGcSpikes, _gcSpikes);
            Volatile.Write(ref _snapMax, _maxNanos);
            Volatile.Write(ref _snapLast, _lastSpikeNanos);
            Volatile.Write(ref _snapLastGc, _lastSpikeGc ? 1 : 0);
            Volatile.Write(ref _snapTailN, _tailCount);
            Volatile.Write(ref _snapFloor, _tailCount == 0 ? 0 : _tail[_tailCount - 1].Nanos);
            Volatile.Write(ref _snapTailGc, tailGc);
            Volatile.Write(ref _snapVersion, _snapVersion + 1);
            Interlocked.Increment(ref _snapSeq);                                    // 짝수(전체 펜스)
        }

        /// <summary>
        /// [v12.18.6-DEV] 관제 사본 판독(임의 스레드 · 잠금/대기/할당 0). 게시 중 경합이 4회 연속이면 false(값 무효 — 호출자는 직전 값 유지).
        /// </summary>
        public bool TryReadSnapshot(out JitterSnapshot snapshot)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                int s1 = Volatile.Read(ref _snapSeq);
                if ((s1 & 1) != 0) continue;
                long spikes = Volatile.Read(ref _snapSpikes), gc = Volatile.Read(ref _snapGcSpikes), max = Volatile.Read(ref _snapMax);
                long last = Volatile.Read(ref _snapLast), floor = Volatile.Read(ref _snapFloor), ver = Volatile.Read(ref _snapVersion);
                int lastGc = Volatile.Read(ref _snapLastGc), tailN = Volatile.Read(ref _snapTailN), tailGc = Volatile.Read(ref _snapTailGc);
                if (Volatile.Read(ref _snapSeq) != s1) continue;
                snapshot = new JitterSnapshot(spikes, gc, max, lastGc != 0, last, tailN, floor, tailGc, ver);
                return true;
            }
            snapshot = default;
            return false;
        }

        /// <summary>최근 스파이크를 오래된 → 최신 순으로 복사(콜드). 반환 = 복사 수.</summary>
        public int CopySpikes(Span<JitterSpike> destination)
        {
            if (_ring == null) return 0;
            long kept = Math.Min(_spikes, _ringCapacity);
            int n = (int)Math.Min(kept, destination.Length);
            long start = _spikes - n;
            for (int i = 0; i < n; i++) destination[i] = _ring[(start + i) % _ringCapacity];
            return n;
        }

        /// <summary>상위 K 꼬리를 큰 순으로 복사(콜드). 반환 = 복사 수.</summary>
        public int CopyTail(Span<JitterSpike> destination)
        {
            if (_tail == null) return 0;
            int n = Math.Min(_tailCount, destination.Length);
            for (int i = 0; i < n; i++) destination[i] = _tail[i];
            return n;
        }

        /// <summary>계수 · 링 · 꼬리 · 히스토그램 초기화(예열 구간을 측정에서 제외할 때 · 콜드).</summary>
        public void Reset()
        {
            _sequence = _spikes = _gcSpikes = _overwritten = _maxNanos = _sumNanos = 0;
            _tailCount = 0;
            _lastSpikeNanos = 0; _lastSpikeGc = false;
            _histogram.Clear();
            PublishSnapshot();
        }

        public void Dispose()
        {
            if (_ring != null) { NativeMemory.Free(_ring); _ring = null; }
            if (_tail != null) { NativeMemory.Free(_tail); _tail = null; }
        }
    }
}
