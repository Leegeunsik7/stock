// ============================================================================
// KisBalanceQueryAdapter — [KIS 실배선 · 추천 A] REST 주문가능현금/보유 잔고 → BrokerLimitFeedProvider 한도 뷰(δ=0 판독) + 실패 인터락
//
//  폴링(콜드 · 기본 10초): 전송 계층(KisLiveBroker.QueryBuyableJson/QueryBalanceJson — HTTP 할당은 전송 몫)이 돌려준 원문 바이트를
//    KisProtocol.TryParseBuyable / ParseHoldings(Utf8JsonReader · 고정 배열)로 판독 → 락 안에서 원자 교체(주문가능현금 · 종목별 매도가능).
//  ★Zero Stale Limits: 조회/판독 실패 '즉시' 한도 무효화(현금 0 · 스냅샷 시각 0 → Bound=false → 분할기 KIS 자식 0).
//    성공 스냅샷도 staleMicros(기본 30초) 지나면 자동 무효. 연속 실패 maxFailures(기본 3)회 = 비트 24(KisLimitFeed) sticky Disarm
//    — 재무장 = 물리 이중키 + 이후 성공 조회.
//  미설정(자격증명 없음)이면 Bound=false · ArmingBlocked=false(필수 비트를 영구 차단하지 않음 — KIS 미사용 배포).
//  SharesGateReservation=false — RouterFeedGate 의 계좌 예약은 키움 계좌 동기화(AccountStateSyncAdapter) 기준이라 KIS 여유와 무관.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum KisQuery : byte { Buyable = 0, Holdings = 1 }

    public sealed class KisBalanceQueryAdapter : IBrokerAccountView, IArmingBlockSource
    {
        public const long DefaultStaleMicros = 30_000_000;
        public const int DefaultMaxFailures = 3;

        private readonly object _gate = new();
        private readonly Func<KisQuery, byte[]?>? _transport;
        private readonly bool _configured;
        private readonly Func<long> _nowMicros;
        private readonly long _staleMicros;
        private readonly int _maxFailures;
        private readonly KisHolding[] _parse, _rows;
        private int _rowCount, _consecutiveFailures, _tripped;
        private long _orderableCash, _snapshotAt, _polls, _successes, _failures, _trips;
        private ArmingInterlockLink? _link;

        public KisBalanceQueryAdapter(Func<KisQuery, byte[]?>? transport, bool configured, Func<long>? nowMicros = null,
                                      long staleMicros = DefaultStaleMicros, int maxFailures = DefaultMaxFailures, int capacity = 256)
        {
            if (staleMicros <= 0) throw new ArgumentOutOfRangeException(nameof(staleMicros));
            if (maxFailures < 1) throw new ArgumentOutOfRangeException(nameof(maxFailures));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _transport = transport; _configured = configured && transport is not null;
            _nowMicros = nowMicros ?? ReferencePriceFeedAdapter.MonotonicMicros;
            _staleMicros = staleMicros; _maxFailures = maxFailures;
            _parse = new KisHolding[capacity]; _rows = new KisHolding[capacity];
        }

        public bool Configured => _configured;
        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Polls => Interlocked.Read(ref _polls);
        public long Successes => Interlocked.Read(ref _successes);
        public long Failures => Interlocked.Read(ref _failures);
        public long Trips => Interlocked.Read(ref _trips);
        public int ConsecutiveFailures => Volatile.Read(ref _consecutiveFailures);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Write(ref _consecutiveFailures, 0);
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── 폴링(콜드 · 타이머 스레드) ──

        /// <summary>조회 1회(주문가능 + 잔고). 반환 = 성공. 미설정 = false(실패 계수 없음).</summary>
        public bool PollOnce()
        {
            if (!_configured) return false;
            Interlocked.Increment(ref _polls);
            byte[]? buyable, holdings;
            try { buyable = _transport!(KisQuery.Buyable); holdings = buyable is null ? null : _transport(KisQuery.Holdings); }
            catch (Exception) { buyable = null; holdings = null; }
            if (buyable is null || holdings is null || !Ingest(buyable, holdings)) { Fail(); return false; }
            return true;
        }

        /// <summary>원문 판독 + 원자 교체(δ=0). 형식 이탈 = false(호출측 실패 처리).</summary>
        public bool Ingest(ReadOnlySpan<byte> buyableJson, ReadOnlySpan<byte> holdingsJson)
        {
            if (!KisProtocol.TryParseBuyable(buyableJson, out long cash) || cash < 0) return false;
            lock (_gate)
            {
                int n = KisProtocol.ParseHoldings(holdingsJson, _parse);
                if (n < 0) return false;
                Array.Copy(_parse, _rows, n);
                _rowCount = n; _orderableCash = cash; _snapshotAt = _nowMicros();
                if (_snapshotAt == 0) _snapshotAt = 1;
            }
            Volatile.Write(ref _consecutiveFailures, 0);
            Interlocked.Increment(ref _successes);
            return true;
        }

        private void Fail()
        {
            Interlocked.Increment(ref _failures);
            lock (_gate) { _orderableCash = 0; _snapshotAt = 0; _rowCount = 0; }      // ★즉시 한도 무효(Zero Stale Limits)
            if (Interlocked.Increment(ref _consecutiveFailures) >= _maxFailures && Interlocked.CompareExchange(ref _tripped, 1, 0) == 0)
            {
                Interlocked.Increment(ref _trips);
                Volatile.Read(ref _link)?.Resync();                                    // ★연속 실패 = Disarm
            }
        }

        // ── IBrokerAccountView(드레인 스레드 · δ=0) ──

        public bool Bound
        {
            get
            {
                if (!_configured || Tripped) return false;
                lock (_gate) return _snapshotAt != 0 && _nowMicros() - _snapshotAt <= _staleMicros;
            }
        }

        public bool SharesGateReservation => false;

        public bool TryGetBuyHeadroomWon(out long won)
        {
            lock (_gate)
            {
                won = 0;
                if (_snapshotAt == 0 || _nowMicros() - _snapshotAt > _staleMicros) return false;
                won = _orderableCash;
                return true;
            }
        }

        public bool TryGetSellableShares(long symbolCode, out long shares)
        {
            lock (_gate)
            {
                shares = 0;
                if (_snapshotAt == 0 || _nowMicros() - _snapshotAt > _staleMicros) return false;
                for (int i = 0; i < _rowCount; i++) if (_rows[i].SymbolCode == symbolCode) { shares = _rows[i].SellableQty; break; }
                return true;
            }
        }
    }
}
