// ============================================================================
// BrokerOpenOrderReconciler — [미체결 대사 · 추천 A] 브로커 미체결 원장 ↔ 호스트 추적 원장 1:1 대사 엔진(δ=0)
//
//  문제(실측): 롤오버로 승계한 슬롯은 '이전 프로세스가 그렇게 기억한다'는 주장일 뿐이다. 승계~재연결 사이 체결/취소 통보는 새 프록시의
//    사상표에 없어 유실될 수 있고, 이전 프로세스가 승계 없이 죽었으면(크래시) 그 주문은 브로커에만 남는다(호스트가 모르는 노출).
//    v12.15.0 의 Rollover 비트(20)는 '브로커 대사 전까지 차단'만 했고 그 대사를 수행하는 주체가 없었다(운용자 수동 MarkReconciled).
//
//  ① 조회: 키움 OPT10075(프록시 OpenOrder 프레임 · 배치/행 번호) · KIS TTTC8001R(output1 JSON · 페이지) → BrokerOpenOrderRow 고정 배열.
//     조회 송신 시점의 원장 시퀀스(LedgerSequence)를 기록 — 응답 도착까지 변경된 슬롯(신규 송출·체결·정정·취소)은 비교에서 제외(스냅샷 경합 오판 0).
//  ② 대사(1:1): 호스트 원장 각 건을 브로커 원장 번호(키움 = OrderMapped 통지 번호 · KIS = BrokerOrderId 복호)로 찾아
//     방향 · 종목 · 미체결 수량 · 지정가(±1원)를 대조. 없음/불일치 = Divergence. 브로커 행 중 호스트 원장(최근 종결분 포함)에 없는 행 = Ghost.
//     번호 미상(미결속 · 사상 미도착) 건과 방향·종목이 맞는 행은 '미해결'로 보류(유령 오판 금지).
//  ③ 판정: Ghost > Divergence > Inconclusive(보류 · 다음 패스 재조회) > Confirmed.
//     Confirmed(최초) = 모든 승계 슬롯 확정 → 프록시에 승계 사상 재등록(AdoptMapping) → Rollover 비트 0ms 해제(markReconciled).
//  ④ 방향 A: 패스마다 해시 체인 감사(ZeroAllocForensicAuditStream · Reconciliation) + MMF 블랙박스(OpenOrderReconciled/Divergence/GhostOrderCancel).
//  ⑤ 방향 B: Ghost = OpenOrderDivergence 비트 즉시 Raise(Disarm · sticky) + 브로커 미체결 전 행 비상 취소(원장 번호 직접 취소).
//     취소는 패스당 상한(기본 4건 — 키움 주문 TR 초당 5회 미만)만 송신하고 '대기열'에 넣지 않는다. 남은 행은 다음 패스의 새 조회에
//     여전히 미체결로 나타나면 그때 다시 취소 대상이 된다(상태 구동 — 큐잉·자동 재시도 금지 원칙 준수 · 같은 번호 5초 내 중복 취소 억제).
//  [v12.18.7-DEV · 무장 가드] ★비상 취소는 브로커 상태를 바꾸는 송신이다 — 무장 술어(AttachCancelArming)가 참일 때만 송신(키움 · KIS 공통).
//     거짓 · 미결속 · 예외 = 송신 0 + CancelsSuppressed 계수(비트 22 래치 · Disarm 은 무장과 무관하게 그대로 — 차단은 항상 먼저).
//     ★판정 시점 = 유령 판정 직후 · 비트 22 Resync '이전' 1회 표본: 이 대사기 자신의 Disarm(비트 22 → 집계 마스크 Blocked)이 무장 술어를
//     거짓으로 만들므로, 송신 직전에 재판정하면 무장 상태에서도 취소가 영영 나가지 않는다(설계 검토 중 확인). 같은 패스 안에서만 유효 —
//     다음 패스는 비트 22 sticky 로 무장 거짓 → 남은 유령은 운용자 처리(물리 이중키 재무장 전 자동 송신 0).
//  무장 차단(비트 22) = Ghost ∨ Divergence ∨ (필수 브로커 최초 대사 미확정) — 대사 전 신규 발주 100% 차단(Zero State Divergence).
//  재무장 = 물리 이중키(TryRearm) + 새 대사 확정 필요.
// ============================================================================
using System;
using System.Runtime.InteropServices;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum OpenOrderVerdict : byte { None = 0, Confirmed = 1, Inconclusive = 2, Divergence = 3, Ghost = 4, Incomplete = 5 }

    /// <summary>대사 패스 1회 결과(값 타입).</summary>
    public readonly record struct OpenOrderPassResult(OpenOrderVerdict Verdict, int Venue, int Rows, int Ledger, int Matched, int Mismatched,
                                                      int Ghosts, int Unresolved, int CancelsSent, int AdoptMapped,
                                                      int CancelsSuppressed = 0);   // [v12.18.7-DEV] 무장 거짓으로 억제한 비상 취소 대상 행 수

    public sealed class BrokerOpenOrderReconciler : IArmingBlockSource, StockManager.Engines.Arbitrage.IQuoteGuardHost
    {
        public const int DefaultCapacity = 1024;
        public const int DefaultMaxCancelsPerPass = 4;          // 키움 주문 TR 초당 5회 미만(패스 간격 ≥ 1초)
        public const int DefaultKiwoomPageCap = 100;            // OPT10075 단일 응답 행 상한 가정 — 도달 = 연속 조회 필요 가능성 → 보류
        public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);
        public static readonly TimeSpan DefaultFastInterval = TimeSpan.FromSeconds(3);
        public static readonly TimeSpan DefaultQueryTimeout = TimeSpan.FromSeconds(10);
        private const int CancelMemo = 64;
        private static readonly long CancelDedupTicks = TimeSpan.FromSeconds(5).Ticks;

        private readonly object _gate = new();
        private readonly OrderExecutionFeedAdapter _exec;
        private readonly IOpenOrderVenue? _kiwoom;
        private readonly Action? _markReconciled;
        private readonly Action<OpenOrderPassResult>? _onPass;
        private readonly Func<long> _nowTicks;
        private readonly int _requireMask, _maxCancels, _kiwoomPageCap;
        private readonly long _intervalTicks, _fastTicks, _timeoutTicks;

        // 브로커 행 배치(단일 활성 배치 — 대사기 하나가 한 번에 한 조회만 보유)
        private readonly BrokerOpenOrderRow[] _rows;
        private readonly bool[] _rowSeen, _rowMatched;
        private int _rowCount, _rowsReceived, _expected, _activeVenue;
        private long _batchId;
        private bool _pending;
        private long _snapshotSeq, _queryAtTicks, _nextQueryTicks;

        private readonly OpenOrderLedgerEntry[] _ledger;
        private readonly long[] _memoVenue = new long[CancelMemo], _memoAt = new long[CancelMemo];
        private int _memoNext;

        private int _confirmedMask, _ghost, _divergence, _adoptMapped;
        private long _passes, _confirmed, _inconclusive, _divergences, _ghostPasses, _incomplete, _timeouts, _cancelsSent, _cancelFaults, _rowsIngested, _rowsRejected;
        private ZeroAllocForensicAuditStream? _forensic;
        private MmfFlightRecorder? _flight;
        private ArmingInterlockLink? _link;
        private IArmingBlockSource? _quoteGuard;                                 // [v12.18.19-DEV] 비트 22 2차 입력(합성 스프레드 괴리 래치)

        public BrokerOpenOrderReconciler(OrderExecutionFeedAdapter exec, IOpenOrderVenue? kiwoomVenue = null, Action? markReconciled = null,
                                         bool requireKiwoom = true, bool requireKis = false, int capacity = DefaultCapacity,
                                         int maxCancelsPerPass = DefaultMaxCancelsPerPass, Func<long>? nowTicks = null,
                                         Action<OpenOrderPassResult>? onPass = null, TimeSpan? interval = null, TimeSpan? fastInterval = null,
                                         TimeSpan? queryTimeout = null, int kiwoomPageCap = DefaultKiwoomPageCap)
        {
            _exec = exec ?? throw new ArgumentNullException(nameof(exec));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (maxCancelsPerPass < 1) throw new ArgumentOutOfRangeException(nameof(maxCancelsPerPass));
            _kiwoom = kiwoomVenue; _markReconciled = markReconciled; _onPass = onPass;
            _requireMask = (requireKiwoom ? 1 << OpenOrderVenueKind.Kiwoom : 0) | (requireKis ? 1 << OpenOrderVenueKind.Kis : 0);
            _maxCancels = maxCancelsPerPass; _kiwoomPageCap = Math.Max(1, kiwoomPageCap);
            _nowTicks = nowTicks ?? (() => DateTime.UtcNow.Ticks);
            _intervalTicks = (interval ?? DefaultInterval).Ticks; _fastTicks = (fastInterval ?? DefaultFastInterval).Ticks;
            _timeoutTicks = (queryTimeout ?? DefaultQueryTimeout).Ticks;
            if (_intervalTicks <= 0 || _fastTicks <= 0 || _timeoutTicks <= 0) throw new ArgumentOutOfRangeException(nameof(interval));
            _rows = new BrokerOpenOrderRow[capacity]; _rowSeen = new bool[capacity]; _rowMatched = new bool[capacity];
            _ledger = new OpenOrderLedgerEntry[capacity];
        }

        // ── 상태 ──

        public bool GhostDetected => Volatile.Read(ref _ghost) == 1;
        public bool DivergenceDetected => Volatile.Read(ref _divergence) == 1;
        public bool InitialReconciled => (Volatile.Read(ref _confirmedMask) & _requireMask) == _requireMask;
        public bool ArmingBlocked => GhostDetected || DivergenceDetected || !InitialReconciled || QuoteBlocked;

        /// <summary>[v12.18.19-DEV] 2차 입력(멀티 브로커 호가 괴리) 차단 여부 — 미결선 = false · 판독 예외 = true(fail-closed).</summary>
        public bool QuoteBlocked
        {
            get
            {
                var g = Volatile.Read(ref _quoteGuard);
                if (g is null) return false;
                try { return g.ArmingBlocked; }
                catch { return true; }
            }
        }

        /// <summary>[v12.18.19-DEV] 2차 차단 입력 결선(콜드 1회 · 교체 금지). 2차 입력은 상태 전이 직후 Resync() 를 호출한다.</summary>
        public void AttachQuoteGuard(IArmingBlockSource guard)
        {
            ArgumentNullException.ThrowIfNull(guard);
            if (Interlocked.CompareExchange(ref _quoteGuard, guard, null) is not null)
                throw new InvalidOperationException("비트 22 2차 입력 이미 결선됨 — 교체 금지(fail-closed)");
            Volatile.Read(ref _link)?.Resync();
        }

        /// <summary>현재 차단 술어를 비트 22 에 반영(δ=0 · 미결속 = 무동작).</summary>
        public void Resync() => Volatile.Read(ref _link)?.Resync();
        public bool QueryPending { get { lock (_gate) return _pending; } }
        public long Passes => Interlocked.Read(ref _passes);
        public long ConfirmedPasses => Interlocked.Read(ref _confirmed);
        public long InconclusivePasses => Interlocked.Read(ref _inconclusive);
        public long DivergencePasses => Interlocked.Read(ref _divergences);
        public long GhostPasses => Interlocked.Read(ref _ghostPasses);
        public long IncompleteBatches => Interlocked.Read(ref _incomplete);
        public long QueryTimeouts => Interlocked.Read(ref _timeouts);
        public long CancelsSent => Interlocked.Read(ref _cancelsSent);
        public long CancelFaults => Interlocked.Read(ref _cancelFaults);
        public long RowsIngested => Interlocked.Read(ref _rowsIngested);
        public long RowsRejected => Interlocked.Read(ref _rowsRejected);
        public bool AdoptMappingSent => Volatile.Read(ref _adoptMapped) == 1;
        private long _reconcileRequests, _invalidatedBatches;
        public long ReconcileRequests => Interlocked.Read(ref _reconcileRequests);
        public long InvalidatedBatches => Interlocked.Read(ref _invalidatedBatches);
        /// <summary>그 venue 의 대사가 (마지막 RequestReconcile 이후) 확정됐는가.</summary>
        public bool IsConfirmed(int venue) => (uint)venue < 32 && (Volatile.Read(ref _confirmedMask) & (1 << venue)) != 0;

        /// <summary>
        /// [세션 복구] 브로커 세션 재접속 직후 호출 — 그 venue 의 확정 철회(필수 venue 면 비트 22 즉시 차단) · 진행 중 배치(재접속 '전'
        /// 스냅샷) 무효화 · 키움이면 다음 Pump 에서 즉시 빠른 조회. 단절 동안 발생한 체결/취소를 브로커 원장과 다시 1:1 대조하기 전엔
        /// 그 venue 로 신규 발주가 나갈 수 없다(Zero Stale Order). δ=0(락 + 정수 필드).
        /// </summary>
        public void RequestReconcile(int venue)
        {
            if (venue != OpenOrderVenueKind.Kiwoom && venue != OpenOrderVenueKind.Kis) throw new ArgumentOutOfRangeException(nameof(venue));
            lock (_gate)
            {
                Interlocked.Increment(ref _reconcileRequests);
                int prev = Volatile.Read(ref _confirmedMask);
                Volatile.Write(ref _confirmedMask, prev & ~(1 << venue));
                if (_pending && _activeVenue == venue) { _pending = false; Interlocked.Increment(ref _invalidatedBatches); }
                _nextQueryTicks = 0;
            }
            Volatile.Read(ref _link)?.Resync();
        }

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public void AttachForensics(ZeroAllocForensicAuditStream? stream, MmfFlightRecorder? flight)
        {
            Volatile.Write(ref _forensic, stream);
            Volatile.Write(ref _flight, flight);
        }

        /// <summary>물리 이중키 재무장 — Ghost/Divergence 해제 + 최초 대사 확정 리셋(새 대사 확정 전까지 계속 차단).</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _ghost, 0);
            Volatile.Write(ref _divergence, 0);
            Volatile.Write(ref _confirmedMask, 0);
            lock (_gate) _nextQueryTicks = 0;
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── 조회 구동(주기 타이머 · 1초 권장) ──

        /// <summary>키움 조회 주기 구동: 응답 시한 초과 = 보류 · 기한 도래 = 스냅샷 시퀀스 기록 후 OPT10075 송신. 반환 = 이번에 조회 송신 여부.</summary>
        public bool Pump()
        {
            var venue = _kiwoom;
            if (venue is null) return false;
            long now = _nowTicks();
            lock (_gate)
            {
                if (_pending)
                {
                    if (now - _queryAtTicks <= _timeoutTicks) return false;
                    _pending = false;                                                     // 응답 없음/배치 미완 — 보류(차단 유지)
                    Interlocked.Increment(ref _timeouts);
                }
                if (now < _nextQueryTicks) return false;
                bool fast = GhostDetected || !InitialReconciled;
                _nextQueryTicks = now + (fast ? _fastTicks : _intervalTicks);
                BeginSnapshotLocked(OpenOrderVenueKind.Kiwoom, now);
                bool sent;
                try { sent = venue.RequestOpenOrders(); } catch (Exception) { sent = false; }
                if (!sent) _pending = false;
                return sent;
            }
        }

        /// <summary>
        /// [KIS 미체결 결선] 진행 중 배치가 없을 때만 스냅샷 시작(true). ★대사기의 진행 배치는 venue 공용 1개 — 키움 OPT10075 응답 대기 중
        /// KIS 조회를 시작하면 키움 배치가 덮여 오판독된다. 진행 중 = false(다음 주기 재시도 · 덮어쓰기 0).
        /// </summary>
        public bool TryBeginSnapshot(int venue)
        {
            if (venue != OpenOrderVenueKind.Kiwoom && venue != OpenOrderVenueKind.Kis) throw new ArgumentOutOfRangeException(nameof(venue));
            lock (_gate)
            {
                if (_pending) return false;
                BeginSnapshotLocked(venue, _nowTicks());
                return true;
            }
        }

        /// <summary>[KIS 미체결 결선] 외부 조회 실패(HTTP · 연속 조회 키 이탈 · 페이지 상한) — 그 venue 의 진행 배치 폐기(Incomplete · 차단 유지).</summary>
        public void AbortSnapshot(int venue)
        {
            OpenOrderPassResult result;
            lock (_gate)
            {
                if (!_pending || _activeVenue != venue) return;
                result = AbortBatchLocked();
            }
            Emit(result);
        }

        private Func<long, long, bool>? _kisCancel;
        /// <summary>[방향 B] KIS 유령 주문 비상 취소 결속(odno · ord_gno_brno → 브로커 접수 여부). 미결속 = KIS 유령은 차단만.</summary>
        public void AttachKisCanceller(Func<long, long, bool> cancel) => Volatile.Write(ref _kisCancel, cancel ?? throw new ArgumentNullException(nameof(cancel)));

        private Func<bool>? _cancelArmed;
        private long _cancelsSuppressed, _suppressedPasses;
        /// <summary>
        /// [v12.18.7-DEV · 무장 가드] 비상 취소 송신 허가 술어(호스트: 실전 무장 liveArmed). 미결속 = 송신 0(fail-closed).
        /// ★비차단 · 이 대사기 재호출 금지(대사기 락 안에서 유령 판정 직후 1회 판독).
        /// </summary>
        public void AttachCancelArming(Func<bool> armed) => Volatile.Write(ref _cancelArmed, armed ?? throw new ArgumentNullException(nameof(armed)));
        /// <summary>무장 거짓으로 송신하지 않은 비상 취소 대상 행 누적.</summary>
        public long CancelsSuppressed => Interlocked.Read(ref _cancelsSuppressed);
        /// <summary>억제가 일어난 유령 패스 누적.</summary>
        public long SuppressedPasses => Interlocked.Read(ref _suppressedPasses);

        /// <summary>외부 조회(KIS REST 등) 송신 직전 호출 — 스냅샷 시퀀스 기록 · 행 버퍼 초기화.</summary>
        public void BeginSnapshot(int venue)
        {
            if (venue != OpenOrderVenueKind.Kiwoom && venue != OpenOrderVenueKind.Kis) throw new ArgumentOutOfRangeException(nameof(venue));
            lock (_gate) BeginSnapshotLocked(venue, _nowTicks());
        }

        private void BeginSnapshotLocked(int venue, long now)
        {
            _snapshotSeq = _exec.LedgerSequence;
            _queryAtTicks = now;
            _activeVenue = venue;
            _pending = true;
            _batchId = 0; _rowCount = 0; _rowsReceived = 0; _expected = -1;
        }

        // ── 키움 OPT10075 행 수신(브리지 OpenOrder 프레임) ──

        /// <summary>프레임 페이로드(80B) 직접 판독(δ=0).</summary>
        public void OnKiwoomFrame(ReadOnlySpan<byte> payload)
        {
            if (payload.Length < Marshal.SizeOf<OpenOrderPayload>()) { Interlocked.Increment(ref _rowsRejected); return; }
            var p = MemoryMarshal.Read<OpenOrderPayload>(payload);
            OnKiwoomRow(p);
        }

        /// <summary>브리지 이벤트 시그니처 그대로 구독(δ=0). 배치 완성 시 대사 패스 실행.</summary>
        public void OnKiwoomRow(OpenOrderPayload p)
        {
            OpenOrderPassResult result = default; bool done = false;
            lock (_gate)
            {
                if (!_pending || _activeVenue != OpenOrderVenueKind.Kiwoom) { Interlocked.Increment(ref _rowsRejected); return; }   // 요청 없는 응답 = 스냅샷 기준 없음
                if (p.RowCount < 0 || p.RowCount > _rows.Length) { result = AbortBatchLocked(); done = true; goto emit; }
                if (_expected < 0 || p.BatchId != _batchId)
                {
                    if (_expected >= 0) Interlocked.Increment(ref _incomplete);       // 이전 배치 미완 — 폐기
                    _batchId = p.BatchId; _expected = p.RowCount; _rowCount = p.RowCount; _rowsReceived = 0;
                    Array.Clear(_rowSeen, 0, Math.Max(1, p.RowCount));
                }
                if (p.RowCount != _expected) { result = AbortBatchLocked(); done = true; goto emit; }
                if (p.RowCount == 0)
                {
                    if (p.RowIndex != 0) { result = AbortBatchLocked(); done = true; goto emit; }
                    result = EvaluateLocked(); done = true; goto emit;
                }
                if (p.RowIndex < 0 || p.RowIndex >= p.RowCount || !BrokerOpenOrderRow.TryFromKiwoom(in p, out var row))
                {
                    Interlocked.Increment(ref _rowsRejected);
                    result = AbortBatchLocked(); done = true; goto emit;
                }
                if (_rowSeen[p.RowIndex]) return;                                         // 중복 행 무시
                _rowSeen[p.RowIndex] = true; _rows[p.RowIndex] = row; _rowsReceived++;
                Interlocked.Increment(ref _rowsIngested);
                if (_rowsReceived < _expected) return;
                result = p.RowCount >= _kiwoomPageCap ? InconclusiveLocked() : EvaluateLocked();   // 페이지 상한 도달 = 연속 조회 미지원 → 보류
                done = true;
            emit:;
            }
            if (done) Emit(result);
        }

        // ── KIS TTTC8001R 페이지 수신 ──

        /// <summary>REST 응답 1페이지(δ=0 · Utf8JsonReader). last = 연속 조회 종료(tr_cont 'D'/'E'). 형식 이탈 = 배치 폐기(차단 유지).</summary>
        public void OnKisPage(ReadOnlySpan<byte> json, bool last)
        {
            OpenOrderPassResult result = default; bool done = false;
            lock (_gate)
            {
                if (!_pending || _activeVenue != OpenOrderVenueKind.Kis) { Interlocked.Increment(ref _rowsRejected); return; }
                int n = KisProtocol.ParseOpenOrders(json, _rows.AsSpan(_rowCount));
                if (n < 0) { Interlocked.Increment(ref _rowsRejected); result = AbortBatchLocked(); done = true; }
                else
                {
                    _rowCount += n;
                    Interlocked.Add(ref _rowsIngested, n);
                    if (last) { result = EvaluateLocked(); done = true; }
                }
            }
            if (done) Emit(result);
        }

        // ── 대사 ──

        private OpenOrderPassResult AbortBatchLocked()
        {
            _pending = false;
            Interlocked.Increment(ref _incomplete);
            Interlocked.Increment(ref _passes);
            return new OpenOrderPassResult(OpenOrderVerdict.Incomplete, _activeVenue, _rowCount, 0, 0, 0, 0, 0, 0, 0);
        }

        private OpenOrderPassResult InconclusiveLocked()
        {
            _pending = false;
            Interlocked.Increment(ref _inconclusive);
            Interlocked.Increment(ref _passes);
            return new OpenOrderPassResult(OpenOrderVerdict.Inconclusive, _activeVenue, _rowCount, 0, 0, 0, 0, _rowCount, 0, 0);
        }

        private OpenOrderPassResult EvaluateLocked()
        {
            _pending = false;
            Interlocked.Increment(ref _passes);
            int venue = _activeVenue;
            long snap = _snapshotSeq;
            int ln = _exec.CopyOpenOrderLedger(_ledger);
            if (ln < 0) { Interlocked.Increment(ref _inconclusive); return new OpenOrderPassResult(OpenOrderVerdict.Inconclusive, venue, _rowCount, 0, 0, 0, 0, 1, 0, 0); }

            int rows = _rowCount, matched = 0, mismatched = 0, ghosts = 0, unresolved = 0, adopted = 0, adoptedOk = 0, otherAdopted = 0;
            Array.Clear(_rowMatched, 0, Math.Max(1, rows));
            var flight = Volatile.Read(ref _flight);

            // ① 호스트 원장 → 브로커 행(번호 1:1)
            for (int k = 0; k < ln; k++)
            {
                ref var e = ref _ledger[k];
                bool unbound = (e.Flags & OpenOrderLedgerEntry.FlagUnbound) != 0;
                int ev = unbound ? 0 : KisOrderIdSpace.IsKis(e.BrokerOrderId) ? OpenOrderVenueKind.Kis : OpenOrderVenueKind.Kiwoom;
                bool isAdopted = (e.Flags & OpenOrderLedgerEntry.FlagAdopted) != 0;
                if (ev != 0 && ev != venue) { if (isAdopted) otherAdopted++; continue; }   // 다른 브로커 원장(승계분은 그 브로커 대사 확정 필요)
                if (isAdopted) adopted++;
                bool volatileEntry = e.TouchedSeq > snap || (e.Flags & OpenOrderLedgerEntry.FlagAmending) != 0;
                long vno = unbound ? 0 : ev == OpenOrderVenueKind.Kis ? KisOrderIdSpace.Decode(e.BrokerOrderId) : e.VenueOrderNo;
                if (vno == 0) { if (isAdopted || !volatileEntry) unresolved++; continue; }   // 번호 미상 — 확정 불가(행 쪽은 ③에서 보류 판정)
                int r = FindRow(vno, rows);
                if (r < 0)
                {
                    if (volatileEntry) { unresolved++; continue; }                         // 스냅샷 이후 송출/변경 — 다음 패스
                    mismatched++;
                    flight?.Record(FlightEventKind.OpenOrderDivergence, e.SymbolCode, vno, e.Remaining, -1, venue);
                    continue;
                }
                _rowMatched[r] = true;
                if (volatileEntry) { unresolved++; continue; }
                ref var row = ref _rows[r];
                bool ok = row.Side == e.Side && (e.SymbolCode == 0 || row.SymbolCode == e.SymbolCode) && row.RemainingQty == e.Remaining
                          && (e.LimitPriceWon == 0 || row.PriceWon == 0 || Math.Abs(row.PriceWon - e.LimitPriceWon) <= 1);
                if (!ok)
                {
                    mismatched++;
                    flight?.Record(FlightEventKind.OpenOrderDivergence, row.SymbolCode, vno, e.Remaining, row.RemainingQty, venue);
                    continue;
                }
                matched++;
                if (isAdopted) adoptedOk++;
            }

            // ② 브로커 행 중 미매칭 → 최근 종결 · 번호 미상 후보 · 정정 원번호면 보류, 아니면 유령
            for (int r = 0; r < rows; r++)
            {
                if (_rowMatched[r]) continue;
                ref var row = ref _rows[r];
                long brokerId = venue == OpenOrderVenueKind.Kis ? KisOrderIdSpace.Encode(row.VenueOrderNo) : 0;
                if (_exec.WasFreedSince(snap, brokerId, venue == OpenOrderVenueKind.Kiwoom ? row.VenueOrderNo : 0)) { unresolved++; continue; }
                if (CouldBeUnresolved(in row, ln, venue)) { unresolved++; continue; }
                ghosts++;
            }

            var verdict = ghosts > 0 ? OpenOrderVerdict.Ghost : mismatched > 0 ? OpenOrderVerdict.Divergence
                        : unresolved > 0 ? OpenOrderVerdict.Inconclusive : OpenOrderVerdict.Confirmed;
            int cancels = 0, mapped = 0, suppressed = 0;
            switch (verdict)
            {
                case OpenOrderVerdict.Ghost:
                    Interlocked.Increment(ref _ghostPasses);
                    bool armed = CancelArmedNow();                                         // ★자기 Disarm(비트 22) '이전' 표본
                    Volatile.Write(ref _ghost, 1);
                    Volatile.Read(ref _link)?.Resync();                                    // ★Disarm 선반영(취소 송신보다 먼저 · 무장 무관)
                    if (venue == OpenOrderVenueKind.Kiwoom) mapped = AdoptMatchedLocked(ln);   // 확정 승계분 사상 → 취소 확인 통보가 호스트 슬롯으로 사상
                    if (armed) cancels = CancelRowsLocked(rows, venue, flight);
                    else suppressed = SuppressCancelsLocked(rows);
                    break;
                case OpenOrderVerdict.Divergence:
                    Interlocked.Increment(ref _divergences);
                    Volatile.Write(ref _divergence, 1);
                    Volatile.Read(ref _link)?.Resync();
                    break;
                case OpenOrderVerdict.Inconclusive:
                    Interlocked.Increment(ref _inconclusive);
                    break;
                default:
                    Interlocked.Increment(ref _confirmed);
                    if (venue == OpenOrderVenueKind.Kiwoom && adopted > 0 && Volatile.Read(ref _adoptMapped) == 0) mapped = AdoptMatchedLocked(ln);
                    int prev = Volatile.Read(ref _confirmedMask);
                    Volatile.Write(ref _confirmedMask, prev | (1 << venue));
                    int other = venue == OpenOrderVenueKind.Kiwoom ? OpenOrderVenueKind.Kis : OpenOrderVenueKind.Kiwoom;
                    if (!GhostDetected && !DivergenceDetected && InitialReconciled && adoptedOk == adopted
                        && (otherAdopted == 0 || (prev & (1 << other)) != 0))
                        try { _markReconciled?.Invoke(); } catch (Exception) { }               // ★Rollover 비트(20) 0ms 해제
                    Volatile.Read(ref _link)?.Resync();
                    break;
            }
            flight?.Record(FlightEventKind.OpenOrderReconciled, 0, rows, ln, (long)verdict, venue);
            Volatile.Read(ref _forensic)?.Append(AuditEventKind.Reconciliation, PackTag(verdict, venue, ghosts, mismatched, rows), _nowTicks());
            return new OpenOrderPassResult(verdict, venue, rows, ln, matched, mismatched, ghosts, unresolved, cancels, mapped, suppressed);
        }

        // [v12.18.7-DEV · 무장 가드] 미결속 · 거짓 · 예외 = 거짓(fail-closed). δ=0(델리게이트 호출 1회).
        private bool CancelArmedNow()
        {
            var f = Volatile.Read(ref _cancelArmed);
            if (f is null) return false;
            try { return f(); } catch (Exception) { return false; }
        }

        // 무장 거짓 = 송신 0 · 대상 행(잔량 > 0) 계수만 — 브로커 상태 변형 전문 0. 경고 로깅은 호스트 onPass(CancelsSuppressed > 0).
        private int SuppressCancelsLocked(int rows)
        {
            int n = 0;
            for (int r = 0; r < rows; r++) if (_rows[r].RemainingQty > 0) n++;
            if (n > 0) { Interlocked.Add(ref _cancelsSuppressed, n); Interlocked.Increment(ref _suppressedPasses); }
            return n;
        }

        // 행이 번호 미상 원장 건(미결속 · 사상 미도착)이거나 정정 진행 중 건의 새 번호일 수 있는가
        private bool CouldBeUnresolved(in BrokerOpenOrderRow row, int ln, int venue)
        {
            for (int k = 0; k < ln; k++)
            {
                ref var e = ref _ledger[k];
                bool unbound = (e.Flags & OpenOrderLedgerEntry.FlagUnbound) != 0;
                int ev = unbound ? 0 : KisOrderIdSpace.IsKis(e.BrokerOrderId) ? OpenOrderVenueKind.Kis : OpenOrderVenueKind.Kiwoom;
                if (ev != 0 && ev != venue) continue;
                long vno = unbound ? 0 : ev == OpenOrderVenueKind.Kis ? KisOrderIdSpace.Decode(e.BrokerOrderId) : e.VenueOrderNo;
                if (vno != 0 && row.OrigVenueOrderNo == vno) return true;                  // 정정 새 번호(사상 미도착)
                if (vno == 0 && row.Side == e.Side && (e.SymbolCode == 0 || row.SymbolCode == e.SymbolCode) && row.RemainingQty <= e.Remaining) return true;
            }
            return false;
        }

        private int FindRow(long venueOrderNo, int rows)
        {
            for (int r = 0; r < rows; r++) if (_rows[r].VenueOrderNo == venueOrderNo) return r;
            return -1;
        }

        // 확정(번호 · 수량 일치) 승계 키움 건을 프록시 사상표에 재등록 — 1회
        private int AdoptMatchedLocked(int ln)
        {
            var venue = _kiwoom;
            if (venue is null || Volatile.Read(ref _adoptMapped) == 1) return 0;
            int n = 0;
            for (int k = 0; k < ln; k++)
            {
                ref var e = ref _ledger[k];
                if ((e.Flags & OpenOrderLedgerEntry.FlagAdopted) == 0 || e.VenueOrderNo == 0 || KisOrderIdSpace.IsKis(e.BrokerOrderId)) continue;
                try { if (venue.AdoptMapping(e.BrokerOrderId, e.VenueOrderNo, e.SymbolCode, e.Side, e.Remaining, e.LimitPriceWon)) n++; }
                catch (Exception) { Interlocked.Increment(ref _cancelFaults); }
            }
            Volatile.Write(ref _adoptMapped, 1);
            return n;
        }

        // [방향 B] 미체결 전 행 비상 취소 — 패스당 상한 · 같은 번호 5초 내 중복 억제(상태 구동 · 대기열 없음)
        private int CancelRowsLocked(int rows, int venue, MmfFlightRecorder? flight)
        {
            var v = _kiwoom;
            var kisCancel = Volatile.Read(ref _kisCancel);
            if (venue == OpenOrderVenueKind.Kiwoom ? v is null : kisCancel is null) return 0;   // 취소 경로 미결속 — 차단만
            long now = _nowTicks();
            int sent = 0;
            for (int r = 0; r < rows && sent < _maxCancels; r++)
            {
                ref var row = ref _rows[r];
                if (row.RemainingQty <= 0 || RecentlyCancelled(row.VenueOrderNo, now)) continue;
                bool ok;
                // [방향 B] KIS = 원장 번호(ODNO) + 주문채번지점번호(행 Reserved) 전량 취소(TTTC0803U) · 지점번호 미상 = 송신 불가(실패 계수)
                try { ok = venue == OpenOrderVenueKind.Kiwoom ? v!.CancelByVenueNo(row.VenueOrderNo, row.SymbolCode, row.Side, row.RemainingQty)
                                                              : row.Reserved > 0 && kisCancel!(row.VenueOrderNo, row.Reserved); }
                catch (Exception) { ok = false; }
                if (!ok) { Interlocked.Increment(ref _cancelFaults); continue; }
                int m = _memoNext; _memoNext = (m + 1) % CancelMemo;
                _memoVenue[m] = row.VenueOrderNo; _memoAt[m] = now;
                sent++;
                flight?.Record(FlightEventKind.GhostOrderCancel, row.SymbolCode, row.VenueOrderNo, row.RemainingQty, row.Side, venue);
            }
            Interlocked.Add(ref _cancelsSent, sent);
            return sent;
        }

        private bool RecentlyCancelled(long venueOrderNo, long now)
        {
            for (int m = 0; m < CancelMemo; m++)
                if (_memoVenue[m] == venueOrderNo && now - _memoAt[m] < CancelDedupTicks) return true;
            return false;
        }

        // 감사 태그: verdict(8) | venue(8) | ghosts(16) | mismatched(16) | rows(16)
        internal static long PackTag(OpenOrderVerdict v, int venue, int ghosts, int mismatched, int rows)
            => ((long)v << 56) | ((long)(venue & 0xFF) << 48) | ((long)Math.Min(ghosts, 0xFFFF) << 32) | ((long)Math.Min(mismatched, 0xFFFF) << 16) | (long)Math.Min(rows, 0xFFFF);

        private void Emit(in OpenOrderPassResult r)
        {
            if (_onPass is { } cb) try { cb(r); } catch (Exception) { }
        }
    }
}
