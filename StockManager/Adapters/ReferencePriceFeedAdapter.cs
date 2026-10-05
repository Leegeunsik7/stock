// ============================================================================
// ReferencePriceFeedAdapter — [피드 연동 §1] Toss 실시간 틱 → ResilientOrderRouter 참조가(종목별 · 신선도) 사상
//
//  왜: 라우터 Route(limit, reference) 는 참조가가 0 이하면 InvalidPrice 로 전량 거부한다(시장가 포함). TossWebSocketMarketDataGateway
//   는 '전 종목 통틀어 최신 1틱' 스냅샷만 보유해 종목별 참조가로 쓸 수 없었다 → 게이트웨이 ITickSink 로 수용 틱을 받아 종목별 표에 사상.
//
//  설계(δ=0):
//   · 개방 주소 표(2의 거듭제곱 · 선형 탐사) — 키 = 게이트웨이와 같은 FNV-1a(심볼 UTF-8). 키 0 은 빈 슬롯이라 1 로 치환.
//   · 슬롯별 seqlock(홀수 = 기록 중) — 판독자(발주 경로)는 (가격, 수신 시각)을 한 쌍으로만 관측(찢김 0). 기록은 Monitor 직렬화
//     (게이트웨이 수신 루프 단일 생산자가 정상이나 다중 생산자 오배선도 안전 · 비경합 Monitor = 힙 0).
//   · 신선도는 ★자체 단조 시계(Stopwatch)로 판정 — 게이트웨이 ts/nowMicros 는 외부 입력(기본 0 · 서버 시각)이라 신뢰하지 않는다.
//   · 가격 스케일: Toss 틱 price(정수) × tickToRawScale → QPrice raw(10^8). 곱 오버플로 틱은 폐기(계수).
//  ※ 종목 키 정합 가정(정직): 발주 경로의 SymbolCode(KSE 6자리 숫자)는 "D6" ASCII 의 FNV 로 사상한다 — Toss 시세 심볼이 6자리
//    코드 문자열이 아니면(접두사 등) 참조가가 '미수신'으로 판정돼 전량 fail-closed(오발주 아님) 된다.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum ReferencePriceVerdict : byte { Fresh = 0, Missing = 1, Stale = 2 }

    public sealed class ReferencePriceFeedAdapter : ITickSink
    {
        public const long DefaultMaxAgeMicros = 3_000_000;   // 3초 — 초과 = Stale(라우터 전방 거부)

        private readonly long[] _key, _px, _at, _ver;
        private readonly int _mask;
        private readonly long _maxAgeMicros, _scale;
        private readonly Func<long> _nowMicros;
        private readonly object _writeGate = new();
        private int _count;
        private long _lastTickMicros, _ticks, _tableFull, _overflowDropped;

        public ReferencePriceFeedAdapter(int capacityPow2 = 4096, long maxAgeMicros = DefaultMaxAgeMicros,
                                         long tickToRawScale = 100_000_000L,   // QMath.Scale(10^8)
                                         Func<long>? nowMicros = null)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("용량은 2의 거듭제곱", nameof(capacityPow2));
            if (maxAgeMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxAgeMicros));
            if (tickToRawScale <= 0) throw new ArgumentOutOfRangeException(nameof(tickToRawScale));
            _key = new long[capacityPow2]; _px = new long[capacityPow2]; _at = new long[capacityPow2]; _ver = new long[capacityPow2];
            _mask = capacityPow2 - 1; _maxAgeMicros = maxAgeMicros; _scale = tickToRawScale;
            _nowMicros = nowMicros ?? MonotonicMicros;
        }

        public static long MonotonicMicros() => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency);

        public long MaxAgeMicros => _maxAgeMicros;
        public long Ticks => Interlocked.Read(ref _ticks);
        public long TableFull => Interlocked.Read(ref _tableFull);
        public long OverflowDropped => Interlocked.Read(ref _overflowDropped);
        public int Symbols => Volatile.Read(ref _count);
        public bool HasTicks => Interlocked.Read(ref _lastTickMicros) != 0;
        /// <summary>마지막 수용 틱 이후 경과(µs) — 피드 하트비트. 수신 이력 없음 = long.MaxValue.</summary>
        public long FeedSilenceMicros(long nowMicros)
        {
            long last = Interlocked.Read(ref _lastTickMicros);
            return last == 0 ? long.MaxValue : Math.Max(0, nowMicros - last);
        }
        public long Now() => _nowMicros();

        /// <summary>KSE 6자리 코드 → 게이트웨이 심볼 키(FNV-1a · "D6" ASCII · δ=0).</summary>
        public static long SymbolHashForCode(long symbolCode)
        {
            if (symbolCode < 0 || symbolCode > 999_999) return 0;
            Span<byte> d = stackalloc byte[6];
            long v = symbolCode;
            for (int i = 5; i >= 0; i--) { d[i] = (byte)('0' + (int)(v % 10)); v /= 10; }
            return TossWebSocketMarketDataGateway.FnvSpan(d);
        }

        /// <summary>ITickSink — 게이트웨이가 수용한 틱 1건 사상(δ=0).</summary>
        public void OnTick(long symbolHash, long priceRaw, long seq)
        {
            if (priceRaw <= 0) return;
            long raw;
            try { raw = checked(priceRaw * _scale); }
            catch (OverflowException) { Interlocked.Increment(ref _overflowDropped); return; }
            long key = symbolHash == 0 ? 1 : symbolHash;
            long now = _nowMicros();
            lock (_writeGate)
            {
                int i = (int)((ulong)key & (ulong)_mask);
                for (int probe = 0; probe <= _mask; probe++, i = (i + 1) & _mask)
                {
                    long k = _key[i];
                    if (k != key && k != 0) continue;
                    Interlocked.Increment(ref _ver[i]);          // 홀 = 기록 중
                    Volatile.Write(ref _px[i], raw);
                    Volatile.Write(ref _at[i], now);
                    Interlocked.Increment(ref _ver[i]);          // 짝 = 발행
                    if (k == 0) { Volatile.Write(ref _key[i], key); _count++; }   // 값 발행 후 키 공개(판독자는 완성 슬롯만 발견)
                    Volatile.Write(ref _lastTickMicros, now);
                    Interlocked.Increment(ref _ticks);
                    return;
                }
                Interlocked.Increment(ref _tableFull);           // 표 포화 — 신규 종목은 참조가 미수신(=거부 · fail-closed)
            }
        }

        /// <summary>종목 참조가 판독(δ=0 · seqlock). Fresh 일 때만 priceRaw 유효.</summary>
        public ReferencePriceVerdict TryGetReference(long symbolHash, out long priceRaw, out long ageMicros)
        {
            priceRaw = 0; ageMicros = long.MaxValue;
            long key = symbolHash == 0 ? 1 : symbolHash;
            int i = (int)((ulong)key & (ulong)_mask);
            for (int probe = 0; probe <= _mask; probe++, i = (i + 1) & _mask)
            {
                long k = Volatile.Read(ref _key[i]);
                if (k == 0) return ReferencePriceVerdict.Missing;
                if (k != key) continue;
                for (int spin = 0; spin < 64; spin++)
                {
                    long v1 = Volatile.Read(ref _ver[i]);
                    if ((v1 & 1) != 0) continue;
                    long px = Volatile.Read(ref _px[i]), at = Volatile.Read(ref _at[i]);
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _ver[i]) != v1) continue;
                    ageMicros = Math.Max(0, _nowMicros() - at);
                    if (ageMicros > _maxAgeMicros) return ReferencePriceVerdict.Stale;
                    priceRaw = px;
                    return ReferencePriceVerdict.Fresh;
                }
                return ReferencePriceVerdict.Stale;   // 지속 기록 경합 — 찢긴 값 대신 거부
            }
            return ReferencePriceVerdict.Missing;
        }
    }
}
