// ============================================================================
// AccountStateSyncAdapter — [정정/취소·계좌 · 추천 A] 키움 계좌 상태 동기화(δ=0) + 잔고 괴리 Disarm(방향 B)
//
//  · 입력: 프록시가 OPW00018(계좌평가잔고내역 · 보유 행)과 OPW00001(예수금상세 · 주문가능금액)을 고정 크기 와이어 프레임
//    (PositionPayload 48B · AccountCashPayload 32B)으로 보내면 KiwoomBridgeClient 가 ReadOnlySpan<byte> → 값 타입으로 투사해
//    OnPosition/OnCash 를 호출한다. 이 어댑터는 고정 배열(종목 용량 capacity)과 값 타입만 쓴다 — 힙 할당 0B.
//    ※ OPW00004(계좌평가현황)에는 '주문가능금액'이 없어 예수금은 OPW00001 로 조회한다(지시서 TR 명칭과의 차이 — 정직 기재).
//  · 주문가능금액 게이트: 최신 스냅샷의 주문가능금액 − (조회 요청 이후 승인된 매수 명목)을 넘는 신규 매수는 거부.
//    스냅샷 부재/만료 = 거부(fail-closed). 매도는 호스트 추정 보유수량 초과 시 거부(공매도·중복 매도 방지).
//  · [방향 B] 정적 구간(조회 요청 전후 quiet 창 안에 체결 0건)에서만 브로커 보유 ↔ 호스트 추정 보유를 1주 단위로 대조 —
//    불일치 = 유실/중복 체결 통보 · 타 경로 주문 혼입 → AccountDivergenceInterlock sticky Disarm(물리 이중키 재무장).
//    체결이 섞인 구간의 배치는 비교 생략(시점 차이를 괴리로 오판하지 않음). 예수금은 수수료·세금·D+2 정산을 모델링하지 않으므로
//    1원 대조 대신 게이트로만 쓴다.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum AccountVerdict : byte { Ok = 0, Stale = 1, Insufficient = 2 }

    public sealed class AccountStateSyncAdapter
    {
        public const long DefaultStaleMicros = 30_000_000;   // 30초 — 조회 주기(≥10초) 3회 누락이면 만료
        public const long DefaultQuietMicros = 2_000_000;    // 2초 — 조회 요청 전후 체결이 없어야 대조

        private readonly object _gate = new();
        private readonly Func<long> _nowMicros;
        private readonly long _staleMicros, _quietMicros;
        private readonly AccountDivergenceInterlock? _interlock;
        private readonly Action<ReconciliationRecord>? _onRecord;

        // 호스트 추정 보유(정렬 없음 · 선형 탐색 — 보유 종목 수십 개 규모)
        private readonly long[] _sym, _qty;
        private int _count;
        private bool _seeded;
        // 수신 중 배치
        private readonly long[] _bSym, _bQty;
        private long _batchId = -1;
        private int _bExpected, _bRows, _bDistinct;
        private bool _bValid;
        // 예수금 스냅샷
        private long _deposit, _d2, _orderable, _cashAt, _positionsAt;
        private long _buySinceSnapshot, _buySinceRequest;
        // 정적 구간 판정
        private long _requestAt, _lastFillAt;

        private long _batches, _skippedNotQuiet, _invalidBatches, _divergences, _overflow, _rejectStale, _rejectCash, _rejectPosition, _faults;

        public AccountStateSyncAdapter(int capacity = 256, long staleMicros = DefaultStaleMicros, long quietMicros = DefaultQuietMicros,
                                       Func<long>? nowMicros = null, AccountDivergenceInterlock? interlock = null,
                                       Action<ReconciliationRecord>? onRecord = null)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (staleMicros <= 0) throw new ArgumentOutOfRangeException(nameof(staleMicros));
            if (quietMicros < 0) throw new ArgumentOutOfRangeException(nameof(quietMicros));
            _staleMicros = staleMicros; _quietMicros = quietMicros;
            _nowMicros = nowMicros ?? ReferencePriceFeedAdapter.MonotonicMicros;
            _interlock = interlock; _onRecord = onRecord;
            _sym = new long[capacity]; _qty = new long[capacity];
            _bSym = new long[capacity]; _bQty = new long[capacity];
            _lastFillAt = long.MinValue / 2;
            _requestAt = long.MinValue / 2;
        }

        public bool Seeded { get { lock (_gate) return _seeded; } }
        public long OrderableCash { get { lock (_gate) return _orderable; } }
        public long Deposit { get { lock (_gate) return _deposit; } }
        public long D2Deposit { get { lock (_gate) return _d2; } }
        /// <summary>최신 스냅샷 이후 승인된 매수 명목(원) — 주문가능금액에서 차감되는 예약분.</summary>
        public long ReservedBuyNotional { get { lock (_gate) return _buySinceSnapshot; } }
        public long Batches => Interlocked.Read(ref _batches);
        public long SkippedNotQuiet => Interlocked.Read(ref _skippedNotQuiet);
        public long InvalidBatches => Interlocked.Read(ref _invalidBatches);
        public long Divergences => Interlocked.Read(ref _divergences);
        public long Overflow => Interlocked.Read(ref _overflow);
        public long RejectStale => Interlocked.Read(ref _rejectStale);
        public long RejectCash => Interlocked.Read(ref _rejectCash);
        public long RejectPosition => Interlocked.Read(ref _rejectPosition);
        public long SinkFaults => Interlocked.Read(ref _faults);

        /// <summary>
        /// [포트폴리오 리스크] 호스트 추정 보유 스냅샷 복사(δ=0 · 호출측 버퍼). 반환 = 복사한 종목 수(버퍼보다 많으면 -1 = 잘림 → 호출측 fail-closed).
        /// 수량 0 종목은 제외.
        /// </summary>
        public int CopyPositions(Span<long> symbols, Span<long> quantities)
        {
            lock (_gate)
            {
                int n = 0;
                for (int i = 0; i < _count; i++)
                {
                    if (_qty[i] == 0) continue;
                    if (n >= symbols.Length || n >= quantities.Length) return -1;
                    symbols[n] = _sym[i]; quantities[n] = _qty[i]; n++;
                }
                return n;
            }
        }

        /// <summary>호스트 추정 보유수량(미보유 = 0).</summary>
        public long PositionOf(long symbolCode)
        {
            lock (_gate) { int i = Find(_sym, _count, symbolCode); return i >= 0 ? _qty[i] : 0; }
        }

        // ── 조회 요청 표지(호스트 타이머가 QueryAccount 직전 호출) ───────────────

        public void MarkQueryRequested()
        {
            long now = _nowMicros();
            lock (_gate) { _requestAt = now; _buySinceRequest = 0; }
        }

        // ── 브리지 입력(KiwoomBridgeClient 이벤트 시그니처 그대로 구독 · δ=0) ────

        public void OnCash(AccountCashPayload cash)
        {
            long now = _nowMicros();
            lock (_gate)
            {
                _deposit = cash.Deposit; _d2 = cash.D2Deposit; _orderable = Math.Max(0, cash.OrderableCash);
                _cashAt = now;
                // 조회 요청 이후 승인된 매수는 응답에 반영됐는지 알 수 없음 — 보수적으로 예약 유지(이중 차감 = 안전 측)
                _buySinceSnapshot = _buySinceRequest;
            }
        }

        public void OnPosition(PositionPayload row)
        {
            long divergenceSym = 0, divergenceQty = 0; bool diverged = false;
            long now = _nowMicros();
            lock (_gate)
            {
                if (row.BatchId != _batchId)
                {
                    if (_batchId >= 0 && _bValid && _bRows != _bExpected) Interlocked.Increment(ref _invalidBatches);   // 미완 배치 폐기
                    _batchId = row.BatchId; _bExpected = row.RowCount; _bRows = 0; _bDistinct = 0; _bValid = row.RowCount >= 0 && row.RowCount <= _bSym.Length;
                    if (row.RowCount > _bSym.Length) Interlocked.Increment(ref _overflow);
                }
                if (!_bValid) return;
                if (row.RowCount != _bExpected || (row.RowCount > 0 && row.RowIndex != _bRows))
                {
                    _bValid = false; Interlocked.Increment(ref _invalidBatches); return;   // 순서 이탈/중복 행 — 배치 전체 무효(비교 생략)
                }
                if (row.RowCount > 0)
                {
                    // 같은 종목 다중 행(신용/현금 구분 등)은 합산
                    _bRows++;
                    int k = Find(_bSym, _bDistinct, row.SymbolCode);
                    if (k >= 0) _bQty[k] += row.Quantity;
                    else { _bSym[_bDistinct] = row.SymbolCode; _bQty[_bDistinct] = row.Quantity; _bDistinct++; }
                    if (_bRows != _bExpected) return;
                }
                // 배치 완결 — 커밋
                _bValid = false;
                Interlocked.Increment(ref _batches);
                _positionsAt = now;
                bool quiet = _lastFillAt < _requestAt - _quietMicros;
                if (!_seeded)
                {
                    Rebase(_bDistinct);                                // 최초 기준선 = 브로커 잔고
                    _seeded = true;
                    return;
                }
                if (!quiet) { Interlocked.Increment(ref _skippedNotQuiet); return; }   // 체결 섞인 구간 — 시점 차이 오판 방지
                // [방향 B] 1주 단위 양방향 대조(브로커 보유 ⊆ 호스트 · 호스트 보유 ⊆ 브로커)
                for (int b = 0; b < _bDistinct && !diverged; b++)
                {
                    int h = Find(_sym, _count, _bSym[b]);
                    long hq = h >= 0 ? _qty[h] : 0;
                    if (hq != _bQty[b]) { diverged = true; divergenceSym = _bSym[b]; divergenceQty = _bQty[b]; }
                }
                for (int h = 0; h < _count && !diverged; h++)
                {
                    if (_qty[h] == 0) continue;
                    if (Find(_bSym, _bDistinct, _sym[h]) < 0) { diverged = true; divergenceSym = _sym[h]; divergenceQty = 0; }
                }
                if (!diverged) Rebase(_bDistinct);
            }
            if (diverged)
            {
                Interlocked.Increment(ref _divergences);
                _interlock?.Trip(divergenceSym, divergenceQty);
                Emit(ReconciliationKind.AccountDivergence, divergenceSym, divergenceQty);
            }
        }

        /// <summary>체결 통보 → 호스트 추정 보유 갱신(매수 Side &gt; 0 · 체결량 0 통보 무시).</summary>
        public void OnFill(FillPayload fill)
        {
            if (fill.FillQty <= 0) return;
            long now = _nowMicros();
            lock (_gate)
            {
                _lastFillAt = now;
                long delta = fill.Side > 0 ? fill.FillQty : -fill.FillQty;
                int i = Find(_sym, _count, fill.SymbolCode);
                if (i >= 0) { _qty[i] += delta; return; }
                if (_count == _sym.Length) { Interlocked.Increment(ref _overflow); _seeded = false; return; }   // 용량 초과 = 추정 불능 → 매도 게이트 fail-closed
                _sym[_count] = fill.SymbolCode; _qty[_count] = delta; _count++;
            }
        }

        // ── 송출 게이트(RouterFeedGate · δ=0) ─────────────────────────────────

        /// <summary>매수 승인 판정 — Ok 이면 명목을 예약(주문가능금액 차감).</summary>
        public AccountVerdict TryReserveBuy(long notionalWon)
        {
            long now = _nowMicros();
            lock (_gate)
            {
                if (_cashAt == 0 || now - _cashAt > _staleMicros) { Interlocked.Increment(ref _rejectStale); return AccountVerdict.Stale; }
                if (notionalWon <= 0 || notionalWon > _orderable - _buySinceSnapshot) { Interlocked.Increment(ref _rejectCash); return AccountVerdict.Insufficient; }
                _buySinceSnapshot += notionalWon; _buySinceRequest += notionalWon;
                return AccountVerdict.Ok;
            }
        }

        /// <summary>승인 후 미송출(라우터 거부·추적 포화) — 예약 반환.</summary>
        public void ReleaseBuy(long notionalWon)
        {
            if (notionalWon <= 0) return;
            lock (_gate)
            {
                _buySinceSnapshot = Math.Max(0, _buySinceSnapshot - notionalWon);
                _buySinceRequest = Math.Max(0, _buySinceRequest - notionalWon);
            }
        }

        /// <summary>매도 승인 판정 — 호스트 추정 보유수량 이하만(기준선 부재/만료 = Stale).</summary>
        public AccountVerdict CheckSell(long symbolCode, long qty)
        {
            long now = _nowMicros();
            lock (_gate)
            {
                if (!_seeded || now - _positionsAt > _staleMicros) { Interlocked.Increment(ref _rejectStale); return AccountVerdict.Stale; }
                int i = Find(_sym, _count, symbolCode);
                long held = i >= 0 ? _qty[i] : 0;
                if (qty <= 0 || qty > held) { Interlocked.Increment(ref _rejectPosition); return AccountVerdict.Insufficient; }
                return AccountVerdict.Ok;
            }
        }

        // ── 내부 ─────────────────────────────────────────────────────────────

        private void Rebase(int rows)
        {
            Array.Copy(_bSym, _sym, rows);
            Array.Copy(_bQty, _qty, rows);
            _count = rows;
        }

        private static int Find(long[] keys, int count, long key)
        {
            for (int i = 0; i < count; i++) if (keys[i] == key) return i;
            return -1;
        }

        private void Emit(ReconciliationKind kind, long symbol, long qty)
        {
            if (_onRecord is null) return;
            try { _onRecord(new ReconciliationRecord(_nowMicros(), kind, 0, symbol, qty)); }
            catch (Exception) { Interlocked.Increment(ref _faults); }
        }
    }

    /// <summary>
    /// [계좌 · 방향 B] Account Divergence Disarm Interlock — 정적 구간 브로커 보유 ≠ 호스트 추정 보유면 sticky Disarm(물리 이중키 재무장).
    /// 마지막 괴리 종목/브로커 수량을 남겨 운용자가 원인을 확인하게 한다.
    /// </summary>
    public sealed class AccountDivergenceInterlock : IArmingBlockSource
    {
        private int _tripped;
        private long _trips, _lastSymbol, _lastBrokerQty;
        private ArmingInterlockLink? _link;

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Trips => Interlocked.Read(ref _trips);
        public long LastSymbol => Interlocked.Read(ref _lastSymbol);
        public long LastBrokerQty => Interlocked.Read(ref _lastBrokerQty);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public void Trip(long symbol, long brokerQty)
        {
            Interlocked.Exchange(ref _lastSymbol, symbol);
            Interlocked.Exchange(ref _lastBrokerQty, brokerQty);
            Interlocked.Increment(ref _trips);
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) == 0) Volatile.Read(ref _link)?.Resync();
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
