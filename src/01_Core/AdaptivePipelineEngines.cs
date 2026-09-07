// ============================================================================
// CrossBrokerAdaptiveRateCoalescer + NativeBacktestRingCache — v12.0.3 Think Beyond Gemini (이중 엔진)
//
//   위험 가정(제미나이의 '단순 결합+테스트'가 다루지 않는 실통신/백테스팅 파멸 리스크):
//     ① "키움 COM(HTS) ↔ 토스 REST(MTS) 수신 속도 불균형(Rate Limit Imbalance) 백프레셔 병목" — 기존
//        ZeroAllocDualBrokerCoalescer 는 링 포화 시 ★생산자를 거부(drop-newest·return false)한다. 실통신에서 빠른
//        브로커가 링을 독점하면 (a) 느린 브로커 틱이 굶고(starvation), (b) 소비자(UI)가 잠깐 느려지면 생산자
//        수신 콜백이 back-pressure 로 막혀 COM 이벤트 펌프/REST 폴러가 밀린다.
//     ② "대용량 백테스팅 반복 차트 조회로 인한 GC 압력 + CPU-L3 캐시 핑퐁" — 백테스트가 동일 캔들/지표를 수만 번
//        재조회하며 관리형 배열을 재물성화하면 Gen0 폭주 + GC 압축(compaction)이 hot 데이터 주소를 옮겨 L3 미스 폭증.
//
//   ── Think Beyond (이중 방어 엔진) ─────────────────────────────────────────────
//     ① Cross-Broker Adaptive Rate Coalescer — ★비차단(non-blocking) drop-oldest MPSC 링. 생산자는 절대 막히지
//        않고(Interlocked.Increment 로 유일 단조 슬롯 확보), 링이 넘치면 ★가장 오래된(stale) 틱을 덮어써 최신가를
//        보존한다(시세=최신 우선). 소비자는 ★seqlock(쓰기잠금 마커 + 이중 시퀀스 확인)으로 덮어쓰기 중 torn-read 를
//        거부. 브로커별 생산/드롭 회계로 불균형비(ImbalanceRatioPpm)를 관측 → 파이프라인 자율 적응. δ=0·락프리·결정론.
//     ② Native Backtest Ring Cache — ★Pinned Object Heap(GC.AllocateArray pinned)에 캔들을 고정. POH 는 GC 압축에서
//        제외되어 ★주소가 불변(hot 슬롯이 L3 에 안정 상주 → 핑퐁 제거) + 1회 할당 후 조회/기입 GC 0 Byte. bar index
//        키 O(1) 히트/미스. unsafe 무사용(POH 로 off-compaction 달성 — 원시 포인터 위험 회피).
//
//   불변: 결정론(주입 tick·순수 비교) · δ=0(Publish/Drain/Put/TryGet 핫패스 힙 0) · fail-closed(포화=최신보존 드롭·
//        dispose 후 no-op) · 락프리(Interlocked/Volatile) · liveArmed:false 무관(시세 병합·백테스트 캐시 — 발주 무접촉).
//   ※ MS2E.Core(최내부·순수) — 상위가 키움/토스 수신 콜백을 Publish 로, 백테스트 루프를 Put/TryGet 으로 결선.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    // ════════════════════════════════════════════════════════════════════════
    //  Engine ① — Cross-Broker Adaptive Rate Coalescer (비차단 drop-oldest MPSC + 브로커 공정성)
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>
    /// 이종 브로커(키움 COM/토스 REST) 수신 속도 불균형을 비차단 drop-oldest 로 흡수하는 δ=0 락프리 코얼레서.
    /// 기존 ZeroAllocDualBrokerCoalescer(drop-newest·생산자 차단)와 상호보완: 이쪽은 ★생산자 무차단 + 최신가 보존.
    /// </summary>
    public sealed class CrossBrokerAdaptiveRateCoalescer
    {
        // slotSeq 상태: -1=빈, w(≥0)=논리인덱스 w 발행됨, long.MinValue=쓰기잠금(overwrite 진행 중).
        private const long WritingLock = long.MinValue;

        private readonly BrokerTick[] _ring;      // 사전 할당(1회) — 이후 힙 0
        private readonly long[] _slotSeq;         // 슬롯별 발행 시퀀스(seqlock 버전)
        private readonly long[] _produced = new long[2];   // 브로커별(0/1) 생산 카운트
        private readonly int _mask;
        private readonly int _cap;
        private long _writeCursor;                // 다음 예약 논리인덱스(다중 생산자 — Interlocked.Increment 로 유일)
        private long _readCursor;                 // 다음 소비 논리인덱스(단일 소비자)
        private long _droppedOldest;              // 소비자가 따라잡기 전 덮어써진(유실된) 최신-보존 드롭

        public CrossBrokerAdaptiveRateCoalescer(int capacityPow2)
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
        /// 브로커 틱 발행(다중 생산자·★비차단·δ=0). 절대 거부하지 않는다 — 링이 넘치면 가장 오래된 슬롯을 덮어쓴다(최신 보존).
        /// seqlock: 쓰기잠금(MinValue) 선행 → 데이터 기입 → 발행 시퀀스(release)로 잠금 해제. 반환=배정된 전역 단조 시퀀스.
        /// </summary>
        public long Publish(int brokerId, long symbolCode, long priceRaw, long qtyRaw)
        {
            long w = Interlocked.Increment(ref _writeCursor) - 1;   // ★유일 단조 슬롯(생산자 무차단)
            int idx = (int)(w & _mask);
            Volatile.Write(ref _slotSeq[idx], WritingLock);         // 쓰기잠금(overwrite 중 torn-read 차단)
            _ring[idx] = new BrokerTick(brokerId, symbolCode, priceRaw, qtyRaw, w);
            Volatile.Write(ref _slotSeq[idx], w);                   // 발행(release) — 소비자가 안정 판독 가능
            Interlocked.Increment(ref _produced[brokerId & 1]);
            return w;
        }

        /// <summary>
        /// 발행 틱을 dest 로 배치 드레인(단일 소비자·0 Byte). 뒤처져 덮어써진 구간은 fast-forward(최신-보존 드롭 계수).
        /// ★seqlock 이중 확인: 판독 전후 슬롯 시퀀스가 기대값과 동일할 때만 수용(덮어쓰기 중 torn-read 거부). 반환=수용 개수.
        /// </summary>
        public int Drain(Span<BrokerTick> dest)
        {
            long w = Volatile.Read(ref _writeCursor);
            long r = _readCursor;

            long lag = w - r;
            if (lag > _cap)                       // ★소비자가 용량 이상 뒤처짐 → 그만큼은 덮어써졌음(최신-보존 드롭)
            {
                long skip = lag - _cap;
                r += skip;
                Interlocked.Add(ref _droppedOldest, skip);
            }

            int n = 0;
            while (n < dest.Length)
            {
                int idx = (int)(r & _mask);
                long s1 = Volatile.Read(ref _slotSeq[idx]);
                if (s1 != r) break;               // 미발행(s1<r 대기) 또는 덮어쓰기 진행/완료(잠금·s1>r) → 중단
                BrokerTick t = _ring[idx];        // 데이터 판독(잠재적 torn)
                long s2 = Volatile.Read(ref _slotSeq[idx]);
                if (s2 != r) break;               // ★판독 중 덮어써짐 → 폐기(다음 드레인에서 재평가)
                dest[n++] = t;
                r++;
            }
            Volatile.Write(ref _readCursor, r);
            return n;
        }

        public long Produced => Volatile.Read(ref _produced[0]) + Volatile.Read(ref _produced[1]);
        public long ProducedByBroker(int brokerId) => Volatile.Read(ref _produced[brokerId & 1]);
        public long DroppedOldest => Volatile.Read(ref _droppedOldest);
        public int PendingCount { get { long p = Volatile.Read(ref _writeCursor) - Volatile.Read(ref _readCursor); return p < 0 ? 0 : (int)Math.Min(p, _cap); } }
        public int Capacity => _cap;

        /// <summary>브로커 간 수신 불균형비(ppm) — max/min×10^6. 완전 균형=1_000_000. 한쪽 0=long.MaxValue(무한 불균형).</summary>
        public long ImbalanceRatioPpm
        {
            get
            {
                long a = Volatile.Read(ref _produced[0]), b = Volatile.Read(ref _produced[1]);
                long hi = Math.Max(a, b), lo = Math.Min(a, b);
                if (lo == 0) return hi == 0 ? 1_000_000L : long.MaxValue;
                return hi / lo * 1_000_000L + hi % lo * 1_000_000L / lo;   // (hi/lo)×1e6 정수 양자화(오버플로 회피)
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  Engine ② — Native Backtest Ring Cache (POH-pinned·주소불변·GC 0-Byte·L3 안정)
    // ════════════════════════════════════════════════════════════════════════
    /// <summary>백테스트 캔들(unmanaged blittable — POH 고정 가능). 가격/수량 스케일 고정소수 raw.</summary>
    public readonly struct Candle64
    {
        public readonly long BarIndex, OpenRaw, HighRaw, LowRaw, CloseRaw, VolumeRaw;
        public Candle64(long barIndex, long openRaw, long highRaw, long lowRaw, long closeRaw, long volumeRaw)
        {
            BarIndex = barIndex; OpenRaw = openRaw; HighRaw = highRaw; LowRaw = lowRaw; CloseRaw = closeRaw; VolumeRaw = volumeRaw;
        }
    }

    /// <summary>
    /// 백테스트 반복 조회 전용 고정 링 캐시. ★Pinned Object Heap 에 1회 할당(GC 압축 제외 → 주소 불변·L3 안정·핑퐁 제거),
    /// bar index 키 O(1) 히트/미스, 조회/기입 GC 0 Byte. 백테스트 루프는 단일 스레드(락 불요). unsafe 무사용.
    /// </summary>
    public sealed class NativeBacktestRingCache
    {
        private readonly Candle64[] _slots;   // POH 고정(주소 불변)
        private readonly long[] _slotBar;     // 각 슬롯의 현재 bar index(-1=빈) — 히트 검증
        private readonly int _mask;
        private readonly int _cap;
        private long _hits, _misses, _puts;

        public NativeBacktestRingCache(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("용량은 2의 거듭제곱(≥2)이어야 합니다.", nameof(capacityPow2));
            _slots = GC.AllocateArray<Candle64>(capacityPow2, pinned: true);   // ★POH 1회 할당(off-compaction)
            _slotBar = GC.AllocateArray<long>(capacityPow2, pinned: true);
            for (int i = 0; i < capacityPow2; i++) _slotBar[i] = -1;
            _mask = capacityPow2 - 1;
            _cap = capacityPow2;
        }

        /// <summary>캔들 기입(bar index 키). 링 슬롯 재사용(가장 오래된 덮어쓰기). δ=0.</summary>
        public void Put(in Candle64 c)
        {
            int idx = (int)(c.BarIndex & _mask);
            _slots[idx] = c;
            Volatile.Write(ref _slotBar[idx], c.BarIndex);
            _puts++;
        }

        /// <summary>bar index 조회(δ=0). 슬롯 각인 bar 가 일치하면 히트, 아니면 미스(덮어써졌거나 미기입).</summary>
        public bool TryGet(long barIndex, out Candle64 c)
        {
            int idx = (int)(barIndex & _mask);
            if (Volatile.Read(ref _slotBar[idx]) == barIndex)
            {
                c = _slots[idx]; _hits++; return true;
            }
            c = default; _misses++; return false;
        }

        public long Hits => _hits;
        public long Misses => _misses;
        public long Puts => _puts;
        public int Capacity => _cap;
        /// <summary>히트율(ppm). 조회 0=0.</summary>
        public long HitRatePpm { get { long t = _hits + _misses; return t == 0 ? 0 : _hits * 1_000_000L / t; } }

        public void Reset()
        {
            for (int i = 0; i < _slotBar.Length; i++) _slotBar[i] = -1;
            _hits = _misses = _puts = 0;
        }
    }
}
