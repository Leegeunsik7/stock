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
//  [KIS VTS · 방향 B] 포지션 원장 대사(reconcilePositions=true · 실시간 체결통보 결선 시): 최초 성공 잔고 = 기준선 →
//    이후 체결통보(OnKisFill) 순증감 누적 → 매 잔고 조회마다 '기준선 + 순체결 ≡ 브로커 보유(hldg_qty)' 종목별 대조.
//    불일치 연속 2회 = 비트 24 sticky Disarm + 한도 즉시 무효(KIS 자식 0) — 체결통보 유실(복호 키 없음 · 도메인 이탈 · 단절) 또는
//    앱 밖 거래를 원자적으로 발주 중단으로 바꾼다. ★조회 진행 중 체결 도착 = 판정 보류(그 회차의 REST 스냅샷이 그 체결을 포함했는지
//    알 수 없음 — 기준선도 그 회차엔 잡지 않는다). 원장 용량 초과 = 불일치(fail-closed). 재무장 = 물리 이중키 + 기준선 재수립.
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
        // [방향 B] 포지션 원장(_gate 보호 · 고정 배열 δ=0)
        public const int DefaultMaxMismatches = 2;
        private readonly bool _reconcile;
        private readonly int _maxMismatches;
        private readonly long[] _ledgerSym, _ledgerNet;
        private readonly KisHolding[] _baseline;
        private int _ledgerCount, _baselineCount, _mismatchStreak;
        private bool _baselined, _ledgerOverflow;
        private long _fillSeq, _reconciles, _ambiguousSkips, _discrepancyTrips;

        public KisBalanceQueryAdapter(Func<KisQuery, byte[]?>? transport, bool configured, Func<long>? nowMicros = null,
                                      long staleMicros = DefaultStaleMicros, int maxFailures = DefaultMaxFailures, int capacity = 256,
                                      bool reconcilePositions = false, int maxMismatches = DefaultMaxMismatches)
        {
            if (maxMismatches < 1) throw new ArgumentOutOfRangeException(nameof(maxMismatches));
            _reconcile = reconcilePositions; _maxMismatches = maxMismatches;
            _ledgerSym = new long[capacity]; _ledgerNet = new long[capacity]; _baseline = new KisHolding[capacity];
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
        public bool Reconciling => _reconcile;
        public bool Baselined { get { lock (_gate) return _baselined; } }
        public long Reconciles => Interlocked.Read(ref _reconciles);
        public long AmbiguousSkips => Interlocked.Read(ref _ambiguousSkips);
        public long DiscrepancyTrips => Interlocked.Read(ref _discrepancyTrips);
        public int MismatchStreak { get { lock (_gate) return _mismatchStreak; } }
        public long FillSequence { get { lock (_gate) return _fillSeq; } }

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
            lock (_gate) { _baselined = false; _ledgerCount = 0; _ledgerOverflow = false; _mismatchStreak = 0; }   // 기준선 재수립
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── 폴링(콜드 · 타이머 스레드) ──

        /// <summary>조회 1회(주문가능 + 잔고). 반환 = 성공. 미설정 = false(실패 계수 없음).</summary>
        public bool PollOnce()
        {
            if (!_configured) return false;
            Interlocked.Increment(ref _polls);
            long seqAtQuery = FillSequence;                                            // [방향 B] 조회 시작 시점 체결 순번
            byte[]? buyable, holdings;
            try { buyable = _transport!(KisQuery.Buyable); holdings = buyable is null ? null : _transport(KisQuery.Holdings); }
            catch (Exception) { buyable = null; holdings = null; }
            if (buyable is null || holdings is null || !Ingest(buyable, holdings, seqAtQuery)) { Fail(); return false; }
            return true;
        }

        /// <summary>원문 판독 + 원자 교체(δ=0). 형식 이탈 = false(호출측 실패 처리). 체결 순번 = 현재(판정 보류 없음).</summary>
        public bool Ingest(ReadOnlySpan<byte> buyableJson, ReadOnlySpan<byte> holdingsJson) => Ingest(buyableJson, holdingsJson, FillSequence);

        /// <param name="fillSeqAtQuery">조회 시작 시점 FillSequence — 이후 체결이 도착했으면 이번 회차 대사 보류.</param>
        public bool Ingest(ReadOnlySpan<byte> buyableJson, ReadOnlySpan<byte> holdingsJson, long fillSeqAtQuery)
        {
            if (!KisProtocol.TryParseBuyable(buyableJson, out long cash) || cash < 0) return false;
            bool discrepancy = false;
            lock (_gate)
            {
                int n = KisProtocol.ParseHoldings(holdingsJson, _parse);
                if (n < 0) return false;
                Array.Copy(_parse, _rows, n);
                _rowCount = n; _orderableCash = cash; _snapshotAt = _nowMicros();
                if (_snapshotAt == 0) _snapshotAt = 1;
                if (_reconcile) discrepancy = ReconcileLocked(fillSeqAtQuery);
                if (discrepancy) { _orderableCash = 0; _snapshotAt = 0; _rowCount = 0; }   // ★즉시 한도 무효(KIS 슬롯 격리)
            }
            if (discrepancy)
            {
                Interlocked.Increment(ref _discrepancyTrips);
                if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0) Interlocked.Increment(ref _trips);
                Volatile.Read(ref _link)?.Resync();                                    // ★원자 Disarm(비트 24)
                return true;                                                           // 조회 자체는 성공(실패 계수 대상 아님)
            }
            Volatile.Write(ref _consecutiveFailures, 0);
            Interlocked.Increment(ref _successes);
            return true;
        }

        // ── [방향 B] 포지션 원장 ──

        /// <summary>KIS 체결 1건 원장 반영(WS 수신 스레드 · δ=0). 체결(수량 > 0)만 — 접수/취소/거부 통보는 보유 불변.</summary>
        public void OnKisFill(FillPayload fill)
        {
            if (!_reconcile || fill.FillQty <= 0 || fill.Side is not (1 or -1)) return;
            lock (_gate)
            {
                _fillSeq++;
                long delta = fill.Side * (long)fill.FillQty;
                for (int i = 0; i < _ledgerCount; i++)
                    if (_ledgerSym[i] == fill.SymbolCode) { _ledgerNet[i] += delta; return; }
                if (_ledgerCount == _ledgerSym.Length) { _ledgerOverflow = true; return; }
                _ledgerSym[_ledgerCount] = fill.SymbolCode; _ledgerNet[_ledgerCount] = delta; _ledgerCount++;
            }
        }

        // _gate 보유 상태에서 호출. 반환 = 이번 회차로 불일치 상한 도달(트립).
        private bool ReconcileLocked(long fillSeqAtQuery)
        {
            if (_fillSeq != fillSeqAtQuery) { Interlocked.Increment(ref _ambiguousSkips); return false; }   // 조회 중 체결 = 판정 보류
            if (!_baselined)
            {
                Array.Copy(_rows, _baseline, _rowCount);
                _baselineCount = _rowCount; _ledgerCount = 0; _ledgerOverflow = false; _mismatchStreak = 0; _baselined = true;
                return false;
            }
            Interlocked.Increment(ref _reconciles);
            bool match = !_ledgerOverflow;
            for (int i = 0; match && i < _rowCount; i++)                               // 브로커 보유 행 = 기준선 + 순체결
                match = Expected(_rows[i].SymbolCode) == _rows[i].HeldQty;
            for (int i = 0; match && i < _baselineCount; i++)                          // 기준선에만 있는 종목 = 기대 0(전량 매도)
                if (!InRows(_baseline[i].SymbolCode)) match = Expected(_baseline[i].SymbolCode) == 0;
            for (int i = 0; match && i < _ledgerCount; i++)                            // 원장에만 있는 종목 = 기대 0
                if (!InRows(_ledgerSym[i])) match = Expected(_ledgerSym[i]) == 0;
            if (match) { _mismatchStreak = 0; return false; }
            return ++_mismatchStreak >= _maxMismatches;
        }

        private long Expected(long sym)
        {
            long e = 0;
            for (int i = 0; i < _baselineCount; i++) if (_baseline[i].SymbolCode == sym) { e = _baseline[i].HeldQty; break; }
            for (int i = 0; i < _ledgerCount; i++) if (_ledgerSym[i] == sym) { e += _ledgerNet[i]; break; }
            return e;
        }

        private bool InRows(long sym)
        {
            for (int i = 0; i < _rowCount; i++) if (_rows[i].SymbolCode == sym) return true;
            return false;
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
