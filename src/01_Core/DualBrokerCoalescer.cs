// ============================================================================
// ZeroAllocDualBrokerCoalescer + LongRunningMemoryGcSentinel — v11.6.0 Phase 94-O Part 3 (Think Beyond Gemini)
//
//   위험 가정(제미나이의 '단순 24h 테스트'가 다루지 않는 스테이징 연속가동 파멸 리스크):
//     ① "이중 브로커(키움 COM 이벤트 + 토스 REST/Webhook 폴링) 비동기 수신 경합으로 인한 GC 폭발" — 두 전송로가
//        서로 다른 스레드에서 마이크로초 간격으로 틱을 밀어넣으면, 순진한 List/Queue<object> 병합은 매 틱 힙 할당 +
//        Gen0→Gen2 승격 폭주 → 장시간 구동 시 GC 스톨/일시정지(발주 지연·UI 프리즈)를 유발한다.
//     ② "장기 체공(Long-Running) 중의 미세 메모리 릭 누적" — 초당 수천 틱이 24시간 흐르면 사소한 누수도 수 GB 로
//        누적되어 OOM 직전까지 간다. GC 압력이 비정상 상승해도 이를 감지·완화할 자율 브레이크가 없다.
//
//   ── Think Beyond (이중 방어 엔진) ─────────────────────────────────────────────
//     ① Zero-Alloc Dual-Broker Stream Coalescer — 사전 할당 고정 링(BrokerTick unmanaged struct)에 두 브로커가
//        락프리 MPSC(다중생산자 CAS 예약 + 슬롯별 발행 시퀀스)로 병합. UI(단일 소비자)는 Span 으로 배치 드레인 —
//        핫패스 힙 할당 0 Byte. 포화 시 드롭(백프레셔·무한 할당 금지). ★데이터 먼저 쓰고 슬롯 시퀀스 release 발행
//        (publish-before-init 방어) · 다중 생산자 CAS(lost-update 방어).
//     ② Long-Running Memory GC Sentinel — 주기 샘플(주입 tick + Gen2 카운트 + 누적 할당)로 Gen2 수집률/할당률을
//        산출, 임계 초과 시 sticky 히스테리시스로 ShouldThrottle 를 세워 상위 퀀트 루프가 OOM '이전'에 자율 감속.
//        판정은 δ=0(Volatile 읽기) · 결정론(주입값만 사용·자체시계 없음).
//
//   불변: 결정론 · δ=0(Publish/Drain/ShouldThrottle 핫패스 힙 0) · fail-closed(포화 드롭·압력 시 감속) · 락프리.
//   ★liveArmed:false: 시세 병합/압력 감시일 뿐 발주 경로 무접촉(감속은 성능 세이프가드, 인가 게이트 아님).
//   ※ MS2E.Core(최내부·순수) — 상위 레이어가 키움 COM/토스 REST 수신 콜백을 Publish 로, UI 렌더를 Drain 으로 결선.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    /// <summary>병합 틱(unmanaged 값 struct — 힙 0). 가격/수량은 스케일 고정소수 raw(δ=0).</summary>
    public readonly struct BrokerTick
    {
        public readonly int BrokerId;      // 0=키움(COM), 1=토스(REST), …
        public readonly long SymbolCode;
        public readonly long PriceRaw;
        public readonly long QtyRaw;
        public readonly long Seq;           // 전역 단조 발행 순번

        public BrokerTick(int brokerId, long symbolCode, long priceRaw, long qtyRaw, long seq)
        {
            BrokerId = brokerId; SymbolCode = symbolCode; PriceRaw = priceRaw; QtyRaw = qtyRaw; Seq = seq;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Zero-Alloc Dual-Broker Stream Coalescer (락프리 MPSC-SC 링)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class ZeroAllocDualBrokerCoalescer
    {
        private readonly BrokerTick[] _ring;      // 사전 할당(1회) — 이후 힙 0
        private readonly long[] _slotSeq;         // 슬롯별 발행 시퀀스(-1=미발행)
        private readonly int _mask;
        private readonly int _cap;
        private long _writeCursor;                // 다음 예약 순번(다중 생산자 CAS)
        private long _readCursor;                 // 다음 소비 순번(단일 소비자)
        private long _dropped;
        private long _published;

        public ZeroAllocDualBrokerCoalescer(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("용량은 2의 거듭제곱(≥2)이어야 합니다.", nameof(capacityPow2));
            _ring = new BrokerTick[capacityPow2];
            _slotSeq = new long[capacityPow2];
            for (int i = 0; i < capacityPow2; i++) _slotSeq[i] = -1;
            _mask = capacityPow2 - 1;
            _cap = capacityPow2;
        }

        /// <summary>
        /// 브로커 틱 발행(다중 생산자·락프리·δ=0). 링 포화면 드롭(백프레셔) 후 false. ★데이터 먼저 쓰고 슬롯 시퀀스를
        /// release 로 마지막 발행 → 소비자가 stale 슬롯 판독 불가(publish-before-init 방어).
        /// </summary>
        public bool Publish(int brokerId, long symbolCode, long priceRaw, long qtyRaw)
        {
            var sw = new SpinWait();
            while (true)
            {
                long w = Volatile.Read(ref _writeCursor);
                long r = Volatile.Read(ref _readCursor);
                if (w - r >= _cap) { Interlocked.Increment(ref _dropped); return false; }   // 포화 → 드롭
                if (Interlocked.CompareExchange(ref _writeCursor, w + 1, w) == w)            // 슬롯 예약(CAS)
                {
                    int idx = (int)(w & _mask);
                    _ring[idx] = new BrokerTick(brokerId, symbolCode, priceRaw, qtyRaw, w);  // 데이터 먼저
                    Volatile.Write(ref _slotSeq[idx], w);                                    // 발행(release) 마지막
                    Interlocked.Increment(ref _published);
                    return true;
                }
                sw.SpinOnce();   // 경합 재시도
            }
        }

        /// <summary>
        /// 발행된 틱을 dest 로 배치 드레인(단일 소비자·0 Byte 할당). 슬롯 시퀀스가 기대 순번과 일치하는 연속분만 판독.
        /// 반환=기입 개수. UI 스레드가 프레임당 1회 호출 → 힙 할당 0 으로 렌더에 전달.
        /// </summary>
        public int Drain(Span<BrokerTick> dest)
        {
            int n = 0;
            long r = _readCursor;   // 단일 소비자 → CAS 불필요
            while (n < dest.Length)
            {
                int idx = (int)(r & _mask);
                if (Volatile.Read(ref _slotSeq[idx]) != r) break;   // 아직 미발행(또는 소진)
                dest[n++] = _ring[idx];
                r++;
            }
            Volatile.Write(ref _readCursor, r);
            return n;
        }

        public long Dropped => Volatile.Read(ref _dropped);
        public long Published => Volatile.Read(ref _published);
        public int PendingCount => (int)(Volatile.Read(ref _writeCursor) - Volatile.Read(ref _readCursor));
        public int Capacity => _cap;
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Long-Running Memory GC Sentinel (Gen2/할당률 압력 감시 + 자율 감속)
    // ════════════════════════════════════════════════════════════════════════
    public sealed class LongRunningMemoryGcSentinel
    {
        private readonly double _gen2PerMinOn;     // 감속 진입 임계(분당 Gen2 수집)
        private readonly double _allocBytesPerSecOn;
        private long _lastTicks;
        private long _lastGen2;
        private long _lastAlloc;
        private long _throttled;   // 0/1 (sticky 히스테리시스)
        private long _samples;
        private long _breaches;
        private double _lastGen2PerMin;
        private double _lastAllocPerSec;

        /// <param name="gen2PerMinThreshold">분당 Gen2 수집 임계(기본 60=초당 1회).</param>
        /// <param name="allocBytesPerSecThreshold">초당 할당 바이트 임계(기본 500MB/s).</param>
        public LongRunningMemoryGcSentinel(double gen2PerMinThreshold = 60, double allocBytesPerSecThreshold = 500_000_000)
        {
            _gen2PerMinOn = gen2PerMinThreshold;
            _allocBytesPerSecOn = allocBytesPerSecThreshold;
        }

        /// <summary>
        /// 주기 샘플(★주입 tick+GC 판독 — 결정론/테스트 가능, 자체시계 없음). 첫 샘플은 기준선만 세팅.
        /// Gen2 수집률·할당률 산출 → 임계 초과 시 감속 진입, 절반 이하로 진정되면 감속 해제(히스테리시스=플래핑 방어). δ=0.
        /// </summary>
        public void Sample(long nowTicks, int gen2Count, long allocBytes)
        {
            long last = Volatile.Read(ref _lastTicks);
            if (last == 0)
            {
                Volatile.Write(ref _lastTicks, nowTicks == 0 ? 1 : nowTicks);
                Volatile.Write(ref _lastGen2, gen2Count);
                Volatile.Write(ref _lastAlloc, allocBytes);
                return;
            }
            long dt = nowTicks - last;
            if (dt <= 0) return;   // 비단조/동일 tick → 무시(fail-safe)

            double elapsedSec = dt / (double)TimeSpan.TicksPerSecond;
            double gen2PerMin = (gen2Count - Interlocked.Read(ref _lastGen2)) / elapsedSec * 60.0;
            double allocPerSec = (allocBytes - Interlocked.Read(ref _lastAlloc)) / elapsedSec;

            Volatile.Write(ref _lastGen2PerMin, gen2PerMin);
            Volatile.Write(ref _lastAllocPerSec, allocPerSec);
            Interlocked.Increment(ref _samples);

            bool over = gen2PerMin >= _gen2PerMinOn || allocPerSec >= _allocBytesPerSecOn;
            bool under = gen2PerMin < _gen2PerMinOn * 0.5 && allocPerSec < _allocBytesPerSecOn * 0.5;

            if (over)
            {
                if (Interlocked.Exchange(ref _throttled, 1) == 0) Interlocked.Increment(ref _breaches);
            }
            else if (under)
            {
                Interlocked.Exchange(ref _throttled, 0);   // 진정 → 감속 해제
            }

            Volatile.Write(ref _lastTicks, nowTicks);
            Volatile.Write(ref _lastGen2, gen2Count);
            Volatile.Write(ref _lastAlloc, allocBytes);
        }

        /// <summary>상위 퀀트 루프가 무거운 연산 전에 확인하는 자율 감속 플래그(δ=0 핫패스).</summary>
        public bool ShouldThrottle => Volatile.Read(ref _throttled) == 1;

        public long Samples => Interlocked.Read(ref _samples);
        public long Breaches => Interlocked.Read(ref _breaches);
        public double LastGen2PerMin => Volatile.Read(ref _lastGen2PerMin);
        public double LastAllocBytesPerSec => Volatile.Read(ref _lastAllocPerSec);

        /// <summary>재초기화(세션 리셋).</summary>
        public void Reset()
        {
            Volatile.Write(ref _lastTicks, 0);
            Volatile.Write(ref _lastGen2, 0);
            Volatile.Write(ref _lastAlloc, 0);
            Volatile.Write(ref _throttled, 0);
            Interlocked.Exchange(ref _samples, 0);
            Interlocked.Exchange(ref _breaches, 0);
        }
    }
}
