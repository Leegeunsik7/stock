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

    public enum DepthVerdict : byte { Ok = 0, Reduced = 1, Insufficient = 2, Missing = 3, Stale = 4, SpreadTooWide = 5 }

    public sealed class DepthBook : IDepthSink, IDisposable
    {
        public const int Levels = 3;   // 1~3호가만 즉시 체결 가능 잔량으로 인정
        private const long QtyScale = 100_000_000L;

        // [저지연] 저장소 = GC 힙 밖 네이티브 seqlock 표(64B 정렬 슬롯 · SafeHandle 결정론적 해제) — 이전: 관리 배열 4종
        private readonly StockManager.Buffers.NativeSeqlockTable<L2Depth> _table;
        private readonly long _maxAgeMicros;
        private readonly long _maxSpreadBps;   // [L2] 최우선 매도/매수 스프레드 상한(0 = 비활성)
        private readonly Func<long> _nowMicros;
        private long _updates;

        public DepthBook(int capacityPow2 = 4096, long maxAgeMicros = 2_000_000, Func<long>? nowMicros = null, long maxSpreadBps = 0)
        {
            _maxSpreadBps = maxSpreadBps > 0 ? maxSpreadBps : 0;
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0) throw new ArgumentException("용량은 2의 거듭제곱", nameof(capacityPow2));
            if (maxAgeMicros <= 0) throw new ArgumentOutOfRangeException(nameof(maxAgeMicros));
            _table = new StockManager.Buffers.NativeSeqlockTable<L2Depth>(capacityPow2);
            _maxAgeMicros = maxAgeMicros;
            _nowMicros = nowMicros ?? (static () => (long)((Int128)Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency));
        }

        public long Updates => Interlocked.Read(ref _updates);
        public long MaxSpreadBps => _maxSpreadBps;
        public long TableFull => _table.TableFull;
        public bool IsDisposed => _table.IsDisposed;

        public void OnDepth(long symbolHash, in L2Depth depth)
        {
            if (_table.Write(symbolHash, in depth, _nowMicros())) Interlocked.Increment(ref _updates);
        }

        public void Dispose() => _table.Dispose();

        /// <summary>즉시 체결 가능 잔량 판정(δ=0). allowedShares = 송출 허용 수량(Ok = 주문 · Reduced = 잔량 · 그 외 0).</summary>
        public DepthVerdict Evaluate(long symbolHash, OrderSide side, long limitRaw, long orderShares, out long allowedShares)
        {
            allowedShares = 0;
            if (orderShares <= 0 || limitRaw <= 0) return DepthVerdict.Insufficient;
            if (!TryRead(symbolHash, out L2Depth d, out long age)) return DepthVerdict.Missing;
            if (age > _maxAgeMicros) return DepthVerdict.Stale;
            // [L2] 스프레드 상한 — 1호가 간격이 넓으면 보호 지정가라도 미끄러짐 기대값이 크다(얇은 호가) → 시장가 송출 거부
            if (_maxSpreadBps > 0)
            {
                long a1 = d.AskPriceRaw[0], b1 = d.BidPriceRaw[0];
                if ((side == OrderSide.Buy ? a1 : b1) <= 0) return DepthVerdict.Insufficient;   // 체결 측 호가 없음 = 잔량 0
                if (a1 <= 0 || b1 <= 0 || b1 >= a1) return DepthVerdict.SpreadTooWide;          // 반대측 결측·교차 = 스프레드 판정 불가 → 거부
                Int128 bps = (Int128)(a1 - b1) * 20_000 / ((Int128)a1 + b1);
                if (bps > _maxSpreadBps) return DepthVerdict.SpreadTooWide;
            }
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

        /// <summary>[자식 슬롯 분할] 신선한(maxAge 이내) 깊이 사본(δ=0). 미등록/만료/경합 = false.</summary>
        public bool TryGetFreshDepth(long symbolHash, out L2Depth depth)
            => TryRead(symbolHash, out depth, out long age) && age <= _maxAgeMicros;

        private bool TryRead(long symbolHash, out L2Depth depth, out long ageMicros)
        {
            ageMicros = long.MaxValue;
            if (!_table.TryRead(symbolHash, out depth, out long at)) return false;   // 미등록 · 지속 경합 · 처분 후 = 미상(fail-closed)
            ageMicros = Math.Max(0, _nowMicros() - at);
            return true;
        }
    }

    /// <summary>
    /// [L2 · 방향 B] Orderbook Illiquidity &amp; Flash Crash Disarm Interlock — 연속매매 시간 중 호가 데이터가 교차/공백/형식 오류이거나
    /// 1호가 스프레드가 극한(기본 1,000bps)으로 벌어지면 전체 발주 회로 sticky Disarm(물리 이중키 재무장).
    /// 연속매매 외 시간(동시호가 예상가 교차·장전 공백 정상)에는 관측만 계수하고 격발하지 않는다(오격발 방지).
    /// 주문 단위 스프레드 거부(DepthBook.maxSpreadBps · 기본 300bps)와 별개 — 이쪽은 시장 전체 이상 징후 회로 차단.
    /// </summary>
    public sealed class OrderbookIlliquidityInterlock : IArmingBlockSource
    {
        private readonly long _flashSpreadBps;
        private readonly Func<bool> _continuousSession;
        private int _tripped, _lastVerdict;
        private long _trips, _anomaliesOffSession, _observed;
        private ArmingInterlockLink? _link;

        public OrderbookIlliquidityInterlock(long flashSpreadBps = 1_000, Func<bool>? continuousSession = null)
        {
            if (flashSpreadBps < 1) throw new ArgumentOutOfRangeException(nameof(flashSpreadBps));
            _flashSpreadBps = flashSpreadBps;
            _continuousSession = continuousSession ?? (static () => true);
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Trips => Interlocked.Read(ref _trips);
        public long Observed => Interlocked.Read(ref _observed);
        public long AnomaliesOffSession => Interlocked.Read(ref _anomaliesOffSession);
        public L2BookVerdict LastTripVerdict => (L2BookVerdict)Volatile.Read(ref _lastVerdict);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        /// <summary>스냅샷 판정 1건 관측(δ=0). 반환 = 이번에 Trip.</summary>
        public bool Observe(L2BookVerdict verdict, long spreadBps)
        {
            Interlocked.Increment(ref _observed);
            bool anomaly = verdict is L2BookVerdict.Crossed or L2BookVerdict.FlashHole or L2BookVerdict.Malformed or L2BookVerdict.NonMonotonic
                           || (verdict == L2BookVerdict.Ok && spreadBps > _flashSpreadBps);
            if (!anomaly) return false;
            bool session;
            try { session = _continuousSession(); } catch { session = true; }   // 판정 불가 = 연속매매로 간주(격발 측 · fail-closed)
            if (!session) { Interlocked.Increment(ref _anomaliesOffSession); return false; }
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return false;
            Volatile.Write(ref _lastVerdict, (int)verdict);
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

        /// <summary>
        /// KRX 연속매매 감시 창(현재 벽시계) — [v12.18.52-DEV] KrxSessionStateMachine 단일 진실 공급원 위임(휴장일 달력 · 수능 순연 · 연초 지연 개장 ·
        /// 동시호가/장전/장후 제외 · 달력 범위 밖 = 장중 취급(엄격 측)). 피드 정지 워치독 · 호가 이상 인터락 공용.
        /// </summary>
        public static bool KrxContinuousSessionNow() => StockManager.Engines.Feeds.KrxSessionStateMachine.InContinuousWatchWindowNow();
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
