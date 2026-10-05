// ============================================================================
// OrderbookDepthGuard — [보호 지정가 §2] 호가 잔량 깊이 검증(1~3호가) · 수량 축소/원자 거부
// ExtremeSlippageDisarmInterlock — [방향 B] 체결가 ↔ 판정 참조가 극한 괴리(기본 200bps) 기습 체결 → 원자 Disarm(sticky)
//
//  DepthBook(IDepthSink): 종목별 L2Depth(5호가 · raw 10^8) 최신 스냅샷 표 — 슬롯별 seqlock(값 타입 통째 복사 · 찢김 0) ·
//   자체 단조 시계 신선도 · 기록 Monitor 직렬화(비경합 = 힙 0).
//   ★정직(실측): 현 프로덕션에는 L2 호가 피드 생산자가 없다 — TossWebSocketMarketDataGateway 는 seq/symbol/price/qty 만 파싱하고
//    키움 TickPayload 도 현재가·거래량뿐이다. 호가 피드가 결선되기 전까지 DepthBook 은 비어 있고, 정책(requireDepthForMarket)에 따라
//    '깊이 미상' 시장가는 거부된다(미분류 = fail-closed). 지정가는 깊이 미상이어도 가격 자체가 보호선이라 통과(깊이 존재 시만 검사).
//
//  Evaluate(side, limitRaw, orderShares): 반대편 1~3호가 중 지정가 이내(매수: ask ≤ limit · 매도: bid ≥ limit) 잔량 합(주).
//    ≥ 주문 → Ok · 0 < 합 < 주문 → Reduced(allowed = 합) · 합 = 0 → Insufficient · 스냅샷 없음 → Missing · 만료 → Stale.
// ============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Engines
{
    /// <summary>L2 호가 스냅샷 하류 싱크(δ=0 계약). 향후 호가 피드(웹소켓 orderbook 채널 등)가 이 계약으로 결선된다.</summary>
    public interface IDepthSink
    {
        void OnDepth(long symbolHash, in L2Depth depth);
    }

    public enum DepthVerdict : byte { Ok = 0, Reduced = 1, Insufficient = 2, Missing = 3, Stale = 4 }

    public sealed class DepthBook : IDepthSink
    {
        public const int Levels = 3;   // 1~3호가만 즉시 체결 가능 잔량으로 인정
        private const long QtyScale = 100_000_000L;

        private readonly long[] _key, _ver, _at;
        private readonly L2Depth[] _depth;
        private readonly int _mask;
        private readonly long _maxAgeMicros;
        private readonly Func<long> _nowMicros;
        private readonly object _writeGate = new();
        private long _updates, _tableFull;

        public DepthBook(int capacityPow2 = 4096, long maxAgeMicros = 2_000_000, Func<long>? nowMicros = null)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("용량은 2의 거듭제곱", nameof(capacityPow2));
            if (maxAgeMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxAgeMicros));
            _key = new long[capacityPow2]; _ver = new long[capacityPow2]; _at = new long[capacityPow2]; _depth = new L2Depth[capacityPow2];
            _mask = capacityPow2 - 1; _maxAgeMicros = maxAgeMicros;
            _nowMicros = nowMicros ?? (static () => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency));
        }

        public long Updates => Interlocked.Read(ref _updates);
        public long TableFull => Interlocked.Read(ref _tableFull);

        public void OnDepth(long symbolHash, in L2Depth depth)
        {
            long key = symbolHash == 0 ? 1 : symbolHash;
            long now = _nowMicros();
            lock (_writeGate)
            {
                int i = (int)((ulong)key & (ulong)_mask);
                for (int probe = 0; probe <= _mask; probe++, i = (i + 1) & _mask)
                {
                    long k = _key[i];
                    if (k != key && k != 0) continue;
                    Interlocked.Increment(ref _ver[i]);
                    _depth[i] = depth;
                    Volatile.Write(ref _at[i], now);
                    Interlocked.Increment(ref _ver[i]);
                    if (k == 0) Volatile.Write(ref _key[i], key);
                    Interlocked.Increment(ref _updates);
                    return;
                }
                Interlocked.Increment(ref _tableFull);
            }
        }

        /// <summary>즉시 체결 가능 잔량 판정(δ=0). allowedShares = 송출 허용 수량(Ok = 주문 · Reduced = 잔량 · 그 외 0).</summary>
        public DepthVerdict Evaluate(long symbolHash, OrderSide side, long limitRaw, long orderShares, out long allowedShares)
        {
            allowedShares = 0;
            if (orderShares <= 0 || limitRaw <= 0) return DepthVerdict.Insufficient;
            if (!TryRead(symbolHash, out L2Depth d, out long age)) return DepthVerdict.Missing;
            if (age > _maxAgeMicros) return DepthVerdict.Stale;
            Int128 fillRaw = 0;
            for (int lv = 0; lv < Levels; lv++)
            {
                long p = side == OrderSide.Buy ? d.AskPriceRaw[lv] : d.BidPriceRaw[lv];
                long q = side == OrderSide.Buy ? d.AskQtyRaw[lv] : d.BidQtyRaw[lv];
                if (p <= 0 || q <= 0) break;                                            // 호가 공백 이후는 신뢰 불가
                if (side == OrderSide.Buy ? p > limitRaw : p < limitRaw) break;         // 보호선 밖
                fillRaw += q;
            }
            Int128 shares128 = fillRaw / QtyScale;
            long fillable = shares128 > long.MaxValue ? long.MaxValue : (long)shares128;
            if (fillable <= 0) return DepthVerdict.Insufficient;
            if (fillable >= orderShares) { allowedShares = orderShares; return DepthVerdict.Ok; }
            allowedShares = fillable;
            return DepthVerdict.Reduced;
        }

        private bool TryRead(long symbolHash, out L2Depth depth, out long ageMicros)
        {
            depth = default; ageMicros = long.MaxValue;
            long key = symbolHash == 0 ? 1 : symbolHash;
            int i = (int)((ulong)key & (ulong)_mask);
            for (int probe = 0; probe <= _mask; probe++, i = (i + 1) & _mask)
            {
                long k = Volatile.Read(ref _key[i]);
                if (k == 0) return false;
                if (k != key) continue;
                for (int spin = 0; spin < 64; spin++)
                {
                    long v1 = Volatile.Read(ref _ver[i]);
                    if ((v1 & 1) != 0) continue;
                    L2Depth copy = _depth[i];
                    long at = Volatile.Read(ref _at[i]);
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _ver[i]) != v1) continue;
                    depth = copy;
                    ageMicros = Math.Max(0, _nowMicros() - at);
                    return true;
                }
                return false;   // 지속 기록 경합 — 미상 처리(fail-closed)
            }
            return false;
        }
    }

    /// <summary>[방향 B] 체결가가 판정 참조가에서 극한 괴리 → 전체 발주 회로 sticky Disarm(물리 이중키 재무장).</summary>
    public sealed class ExtremeSlippageDisarmInterlock : IArmingBlockSource
    {
        private readonly long _extremeBps;
        private int _tripped;
        private long _trips, _lastBps, _observed;
        private ArmingInterlockLink? _link;

        public ExtremeSlippageDisarmInterlock(long extremeBps = 200)
        {
            if (extremeBps < 1) throw new ArgumentOutOfRangeException(nameof(extremeBps));
            _extremeBps = extremeBps;
        }

        public long ExtremeBps => _extremeBps;
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Trips => Interlocked.Read(ref _trips);
        public long LastAdverseBps => Interlocked.Read(ref _lastBps);
        public long Observed => Interlocked.Read(ref _observed);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>체결 1건 관측(δ=0). 불리 방향 괴리(매수: 체결가 &gt; 참조가 · 매도: 체결가 &lt; 참조가)만 계상. 반환 = 이번에 Trip.</summary>
        public bool Observe(bool isBuy, long referenceRaw, long fillPriceRaw)
        {
            if (referenceRaw <= 0 || fillPriceRaw <= 0) return false;
            Interlocked.Increment(ref _observed);
            Int128 diff = isBuy ? (Int128)fillPriceRaw - referenceRaw : (Int128)referenceRaw - fillPriceRaw;
            Int128 bps = diff * 10_000 / referenceRaw;
            long b = bps > long.MaxValue ? long.MaxValue : bps < long.MinValue ? long.MinValue : (long)bps;
            Interlocked.Exchange(ref _lastBps, b);
            if (b <= _extremeBps) return false;
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return false;
            Interlocked.Increment(ref _trips);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }
    }
}
