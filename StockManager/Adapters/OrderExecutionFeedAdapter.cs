// ============================================================================
// OrderExecutionFeedAdapter — [피드 연동 §2] 브로커 체결/취소 통보 → ResilientOrderRouter 추적 슬롯 수명주기 대사
//                              + [방향 A] Zero-Alloc Slot Age Sanitizer(응답 타임아웃 회수 · 리스크 차단 비트)
//
//  실 피드(실측): KiwoomBridgeClient.FillReceived(FillPayload — 브로커 주문번호 long · 체결량 long · 체결가 double).
//   · 프록시는 체결량 0 인 통보(거부 · 접수 · 취소 확인)를 OrderRejected 로 같은 채널에 보낸다 → 체결량 0 은 '종결'로 단정 불가.
//   · 키움 체결 통보는 부분 체결마다 1건 — 수정 전 라우터는 체결 통보 1건에 슬롯을 통째 해제했다(부분 체결 직후 페일오버 시
//     잔량 주문이 캐스케이드 취소 대상에서 빠짐). 여기서는 누적 체결량 ≥ 주문 수량일 때만 Filled 로 해제한다.
//
//  수명주기(전부 단일 Monitor 직렬화 · 비경합 = 힙 0):
//    Admitted(송출 직전 추적 확보) → Accepted(브로커 주문번호 결속) → {완전 체결 · 캐스케이드 취소 확인 · 타임아웃 회수} → Free
//    · 체결량 > 0  : 누적. 완료 시 router.OnFill → Filled / FilledAfterCancel / Stale 대사 기록 후 해제. 초과 체결 = Overfill 대사.
//    · 체결량 = 0  : router.OnCancelConfirmed 가 true(캐스케이드 취소 전환된 슬롯)면 CancelConfirmed 해제 — 아니면 접수/거부 통보로 보고 유지.
//    · 미등록 번호 : UnknownOrder 대사(회수 후 늦게 온 통보 · 타 경로 주문).
//  [방향 A] SweepExpired(now): Admitted/Accepted 슬롯의 수명(자체 단조 µs 시각 배열 스캔)이 TTL 초과면 router.ReclaimExpired 로
//    추적 슬롯 회수(영구 고착 방지) + 대사 기록 + ★sticky ArmingBlocked(응답 없는 실 노출 = 미확인 포지션 리스크 → 물리 이중키 재무장).
//    체결량 0 통보만 받은 슬롯(ExpiredAcknowledged)도 차단한다 — 거부(종결)인지 접수 후 미체결 잔존(실 노출)인지 프로토콜로 구분할 수
//    없으므로 안전 측(정직 한계: 브로커 거부가 잦으면 TTL 마다 재무장이 필요 — Kiwoom 프록시가 거부/접수를 구분 송신해야 해소).
//  대사 로그: 고정 링(256) 값 타입 기록 + 선택 싱크(onRecord · 호스트가 Log 결선) — 싱크는 락 밖에서 호출.
// ============================================================================
using System;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Adapters
{
    public enum ReconciliationKind : byte
    {
        Filled = 0, FilledAfterCancel = 1, StaleFill = 2, Overfill = 3, CancelConfirmed = 4,
        UnknownOrder = 5, Expired = 6, ExpiredWhileCancelling = 7, BrokerIdUnparsed = 8,
        ExpiredAcknowledged = 9,   // 체결량 0 통보(접수/거부 — 프로토콜상 구분 불가)만 받고 TTL 초과
        SlippageRejected = 10,     // [보호 지정가] 라우터 슬리피지 한도 초과 — 송출 전 거부
        DepthRejected = 11,        // [보호 지정가] 1~3호가 즉시 체결 잔량 0 · 깊이 미상/만료 시장가 — 송출 전 거부
        DepthReduced = 12,         // [보호 지정가] 잔량 부족 — 수량 축소 송출(Qty = 송출 수량)
        ExtremeSlippage = 13,      // [보호 지정가 · 방향 B] 체결가 극한 괴리 — 회로 Disarm
        Rejected = 14,             // [키움 프로토콜] 브로커 거부 명시 통보 — 슬롯 즉시 반납(무장 유지)
        BrokerStateMismatch = 15,  // [키움 프로토콜 · 방향 B] 추적 잔량 ≠ 브로커 미체결 잔량 — 회로 Disarm(Qty = 브로커 잔량)
        ExpiredAccepted = 16,      // [키움 프로토콜] 접수 확인된 주문이 장기 TTL 초과
        SpreadRejected = 17,       // [L2] 1호가 스프레드 상한 초과 — 시장가 송출 전 거부
        CancelRaceFilled = 18,     // [정정/취소 · 방향 A] 취소 요청 후 체결이 먼저 도착해 완전 체결 — 체결 반영·슬롯 종결(Qty = 누적 체결)
        AmendBlocked = 19,         // [정정/취소] 전방 게이트 차단(종결/중복/잔량 초과 · Qty = 요청 수량)
        ModifyApplied = 20,        // [정정] 브로커 접수 잔량 = 정정 수량 확인 — 추적 주문 수량 재기준(Qty = 정정 수량)
        AccountDivergence = 21,    // [계좌 · 방향 B] 브로커 잔고 ↔ 호스트 추정 보유 불일치(Qty = 브로커 수량)
        AccountRejected = 22,      // [계좌] 주문가능금액/보유수량 부족·스냅샷 만료 — 송출 전 거부(Qty = 요청 수량)
    }

    public readonly record struct ReconciliationRecord(long Micros, ReconciliationKind Kind, long ProposalId, long BrokerOrderId, long Qty);

    public sealed class OrderExecutionFeedAdapter : IArmingBlockSource, IOrderAmendGate
    {
        /// <summary>[정정/취소] 진행 중 표지 유효 창 — 브로커 확인이 이 시간 안에 오지 않으면 실패로 보고 해제(수동 재요청 허용).</summary>
        public const long AmendWindowMicros = 5_000_000;
        public const long DefaultSlotTtlMicros = 120_000_000;   // 2분 — 무응답(접수 확인 전) 슬롯 회수 임계
        public const long DefaultAcceptedTtlMicros = 8L * 3600 * 1_000_000;   // [키움 프로토콜] 접수 확인 주문(호가 대기) — 8시간
        private const int Free = 0, Admitted = 1, Accepted = 2;
        private const int RingCap = 256;

        private readonly ResilientOrderRouter _router;
        private readonly long _ttlMicros;
        private readonly Func<long> _nowMicros;
        private readonly Action<ReconciliationRecord>? _onRecord;
        private readonly object _gate = new();
        // 슬롯 배열(SoA) — 스캔 친화 · 값 타입만
        private readonly int[] _state, _broker;
        private readonly bool[] _acked, _isBuy;
        private readonly long[] _refRaw;
        private readonly StockManager.Engines.ExtremeSlippageDisarmInterlock? _extreme;
        private readonly bool[] _accepted;
        private readonly long _acceptedTtlMicros;
        private readonly BrokerStateMismatchInterlock? _mismatch;
        private MS2E.Core.ZeroAllocForensicAuditStream? _forensic;
        private long _rejectedReleased, _accepts, _mismatches;
        // [정정/취소] 슬롯별 진행 표지 — 취소 요청 시각 · 정정 대기 수량/시각(0 = 없음)
        private readonly long[] _cancelAt, _modQty, _modAt;
        private long _amendBlocked, _cancelRaceFilled, _modifyApplied, _amendTolerated;
        private readonly long[] _proposalId, _brokerOrderId, _orderQty, _filled, _admittedMicros;
        private readonly UInt128[] _routerKey;
        private readonly ReconciliationRecord[] _ring = new ReconciliationRecord[RingCap];
        private long _ringWrite;
        private int _tracked, _tripped;
        private long _partials, _zeroQtyIgnored, _unknown, _expired, _trackFull, _faults;
        private ArmingInterlockLink? _interlockLink;

        public OrderExecutionFeedAdapter(ResilientOrderRouter router, int capacity = 1024, long slotTtlMicros = DefaultSlotTtlMicros,
                                         Func<long>? nowMicros = null, Action<ReconciliationRecord>? onRecord = null,
                                         StockManager.Engines.ExtremeSlippageDisarmInterlock? extremeSlippage = null,
                                         long acceptedTtlMicros = DefaultAcceptedTtlMicros, BrokerStateMismatchInterlock? mismatch = null)
        {
            _extreme = extremeSlippage;
            _mismatch = mismatch;
            _acceptedTtlMicros = Math.Max(acceptedTtlMicros, slotTtlMicros);   // 접수 TTL 은 응답 TTL 이상(짧게 설정돼도 응답 TTL 로 상향)
            _accepted = new bool[capacity];
            _router = router ?? throw new ArgumentNullException(nameof(router));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (slotTtlMicros <= 0) throw new ArgumentOutOfRangeException(nameof(slotTtlMicros));
            _ttlMicros = slotTtlMicros;
            _nowMicros = nowMicros ?? ReferencePriceFeedAdapter.MonotonicMicros;
            _onRecord = onRecord;
            _state = new int[capacity]; _broker = new int[capacity]; _acked = new bool[capacity]; _isBuy = new bool[capacity]; _refRaw = new long[capacity];
            _proposalId = new long[capacity]; _brokerOrderId = new long[capacity]; _orderQty = new long[capacity];
            _filled = new long[capacity]; _admittedMicros = new long[capacity]; _routerKey = new UInt128[capacity];
            _cancelAt = new long[capacity]; _modQty = new long[capacity]; _modAt = new long[capacity];
        }

        public long AmendBlocked => Interlocked.Read(ref _amendBlocked);
        public long CancelRaceFilled => Interlocked.Read(ref _cancelRaceFilled);
        public long ModifyApplied => Interlocked.Read(ref _modifyApplied);
        /// <summary>정정/취소 진행 중 잔량 불일치를 Disarm 대신 유예한 누계(구 번호 확인 통보 · 정정 반영 전 경합).</summary>
        public long AmendTolerated => Interlocked.Read(ref _amendTolerated);

        // ── [정정/취소] 전방 게이트(IOrderAmendGate · δ=0) ─────────────────────

        /// <summary>취소 시작 — 접수 확인(번호 결속)·잔량 &gt; 0·진행 중 취소 없음일 때만 true(표지 기록).</summary>
        public bool TryBeginCancel(long brokerOrderId)
        {
            long now = _nowMicros(); long proposal = 0;
            lock (_gate)
            {
                int i = FindByBrokerOrder(brokerOrderId);
                if (i >= 0 && _orderQty[i] - _filled[i] > 0 && !InWindow(_cancelAt[i], now))
                {
                    _cancelAt[i] = now == 0 ? 1 : now;
                    return true;
                }
                if (i >= 0) proposal = _proposalId[i];
                WriteRing(ReconciliationKind.AmendBlocked, proposal, brokerOrderId, 0);
            }
            Interlocked.Increment(ref _amendBlocked);
            Emit(ReconciliationKind.AmendBlocked, proposal, brokerOrderId, 0);
            return false;
        }

        /// <summary>정정 시작 — 1 ≤ 정정수량 ≤ 추적 잔량 · 진행 중 취소/정정 없음일 때만 true(정정 대기 수량 기록).</summary>
        public bool TryBeginModify(long brokerOrderId, long newQtyShares, long newPriceWon)
        {
            long now = _nowMicros(); long proposal = 0;
            lock (_gate)
            {
                int i = FindByBrokerOrder(brokerOrderId);
                if (i >= 0 && newQtyShares > 0 && newPriceWon > 0 && newQtyShares <= _orderQty[i] - _filled[i]
                    && !InWindow(_cancelAt[i], now) && !InWindow(_modAt[i], now))
                {
                    _modQty[i] = newQtyShares; _modAt[i] = now == 0 ? 1 : now;
                    return true;
                }
                if (i >= 0) proposal = _proposalId[i];
                WriteRing(ReconciliationKind.AmendBlocked, proposal, brokerOrderId, newQtyShares);
            }
            Interlocked.Increment(ref _amendBlocked);
            Emit(ReconciliationKind.AmendBlocked, proposal, brokerOrderId, newQtyShares);
            return false;
        }

        private static bool InWindow(long at, long now) => at != 0 && now - at <= AmendWindowMicros;

        // 정정/취소 진행 중(창 내) = 브로커 통보가 구 번호/신 번호에 섞여 도착하는 구간 — 잔량 불일치는 Disarm 대신 유예
        private bool AmendInFlight(int i, long now) => InWindow(_cancelAt[i], now) || InWindow(_modAt[i], now);

        public int Tracked { get { lock (_gate) return _tracked; } }
        public long PartialFills => Interlocked.Read(ref _partials);
        public long ZeroQtyIgnored => Interlocked.Read(ref _zeroQtyIgnored);
        public long UnknownNotices => Interlocked.Read(ref _unknown);
        public long ExpiredSlots => Interlocked.Read(ref _expired);
        public long TrackFull => Interlocked.Read(ref _trackFull);
        public long SinkFaults => Interlocked.Read(ref _faults);
        public long RecordsWritten => Interlocked.Read(ref _ringWrite);
        public long SlotTtlMicros => _ttlMicros;
        public long RejectedReleased => Interlocked.Read(ref _rejectedReleased);
        public long AcceptedNotices => Interlocked.Read(ref _accepts);
        public long StateMismatches => Interlocked.Read(ref _mismatches);

        /// <summary>[방향 A] 대사 기록을 포렌식 해시 체인에도 각인(Reconciliation 이벤트 · payload = 종류(8bit)‖브로커 주문번호(56bit)).</summary>
        public void AttachForensicStream(MS2E.Core.ZeroAllocForensicAuditStream stream)
        {
            ArgumentNullException.ThrowIfNull(stream);
            Volatile.Write(ref _forensic, stream);
        }

        /// <summary>★blockWhile — 응답 타임아웃 회수 발생 이후 무장 차단(sticky · 물리 이중키 재무장).</summary>
        public bool ArmingBlocked => Volatile.Read(ref _tripped) == 1;

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _interlockLink, link);
            link.Resync();
        }

        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            Volatile.Read(ref _interlockLink)?.Resync();
            return true;
        }

        /// <summary>최근 대사 기록 복사(최신 우선 아님 · 기록 순). 반환 = 복사 건수(δ=0).</summary>
        public int CopyRecent(Span<ReconciliationRecord> dst)
        {
            lock (_gate)
            {
                long w = _ringWrite;
                int n = (int)Math.Min(Math.Min(w, RingCap), dst.Length);
                for (int k = 0; k < n; k++) dst[k] = _ring[(int)((w - n + k) % RingCap)];
                return n;
            }
        }

        // ── 송출 경로(RouterFeedGate) ─────────────────────────────────────────

        /// <summary>송출 직전 추적 확보(δ=0). 포화 = false(호출측이 라우터 Abort · 미송출).</summary>
        public bool TryTrack(long proposalId, UInt128 routerKey, int broker, long orderQty)
            => TryTrack(proposalId, routerKey, broker, orderQty, isBuy: true, referenceRaw: 0);

        /// <summary>송출 직전 추적 확보 + 판정 참조가 기록(체결가 극한 괴리 감시용 · δ=0).</summary>
        public bool TryTrack(long proposalId, UInt128 routerKey, int broker, long orderQty, bool isBuy, long referenceRaw)
        {
            long now = _nowMicros();
            lock (_gate)
            {
                for (int i = 0; i < _state.Length; i++)
                {
                    if (_state[i] != Free) continue;
                    _state[i] = Admitted; _proposalId[i] = proposalId; _routerKey[i] = routerKey; _broker[i] = broker;
                    _orderQty[i] = orderQty; _filled[i] = 0; _brokerOrderId[i] = 0; _admittedMicros[i] = now; _acked[i] = false;
                    _isBuy[i] = isBuy; _refRaw[i] = referenceRaw; _accepted[i] = false;
                    _tracked++;
                    return true;
                }
            }
            Interlocked.Increment(ref _trackFull);
            return false;
        }

        /// <summary>브로커 접수 — 주문번호 결속(δ=0).</summary>
        public bool BindBrokerOrder(long proposalId, long brokerOrderId)
        {
            lock (_gate)
            {
                int i = FindByProposal(proposalId);
                if (i < 0 || _state[i] != Admitted) return false;
                _brokerOrderId[i] = brokerOrderId;
                _state[i] = Accepted;
                return true;
            }
        }

        /// <summary>[보호 지정가] 송출 전 게이트 판정(슬리피지·깊이) 대사 기록.</summary>
        public void ReportGateEvent(ReconciliationKind kind, long proposalId, long qty) => Record(kind, proposalId, 0, qty);

        /// <summary>브로커 주문번호 파싱 실패(형식 이탈) — 추적은 유지(타임아웃 회수 대상)하고 대사 기록.</summary>
        public void ReportUnparsedBrokerId(long proposalId) => Record(ReconciliationKind.BrokerIdUnparsed, proposalId, 0, 0);

        /// <summary>송출 미도달 롤백 — Admitted(미접수) 슬롯만. 반환 true 시 routerKey 로 라우터 Abort.</summary>
        public bool Untrack(long proposalId, out UInt128 routerKey)
        {
            routerKey = default;
            lock (_gate)
            {
                int i = FindByProposal(proposalId);
                if (i < 0 || _state[i] != Admitted) return false;   // 이미 접수된 주문은 롤백 금지(실 노출)
                routerKey = _routerKey[i];
                FreeSlot(i);
                return true;
            }
        }

        // ── 체결/취소 통보(KiwoomBridgeClient.FillReceived) ────────────────────

        /// <summary>브리지 체결 통보 사상(δ=0 · 이벤트 시그니처 그대로 구독).</summary>
        public void OnFill(FillPayload fill)
        {
            ReconciliationKind kind; long proposal = 0, qty = fill.FillQty; bool record = true;
            long now = _nowMicros(); long modApplied = 0;
            long extremeFillRaw = 0, mismatchRemaining = -1;
            // [키움 프로토콜] 명시 상태(접수/거부/취소확인/체결) + 브로커 미체결 잔량 해독(δ=0). 구 프록시 = Legacy(구 동작).
            var status = BridgeFillStatusCodec.Decode(in fill, out long brokerRemaining);
            lock (_gate)
            {
                int i = FindByBrokerOrder(fill.OrderId);
                if (i < 0) { kind = ReconciliationKind.UnknownOrder; Interlocked.Increment(ref _unknown); }
                else
                {
                    proposal = _proposalId[i];
                    if (fill.FillQty > 0)
                    {
                        // [방향 B] 체결가 ↔ 판정 참조가 괴리 감시(체결 단건마다) — 극한 괴리면 회로 Disarm + 대사 기록
                        if (_extreme is { } ex && _refRaw[i] > 0 && TryPriceRaw(fill.FillPrice, out long fillRaw)
                            && ex.Observe(_isBuy[i], _refRaw[i], fillRaw))
                        {
                            WriteRing(ReconciliationKind.ExtremeSlippage, _proposalId[i], fill.OrderId, fillRaw);
                            extremeFillRaw = fillRaw;
                        }
                        long cum = _filled[i] + fill.FillQty;
                        _filled[i] = cum;
                        // [방향 B] 체결 직후 브로커 미체결 잔량과 추적 잔량 대조 — 불일치 = 상태 분기(유실/중복 통보 · 정정 미반영) → Disarm
                        if (brokerRemaining >= 0 && brokerRemaining != Math.Max(0, _orderQty[i] - cum))
                        {
                            if (AmendInFlight(i, now)) Interlocked.Increment(ref _amendTolerated);   // 정정/취소 경합 — 체결은 반영, 판정은 유예
                            else
                            {
                                mismatchRemaining = brokerRemaining;
                                WriteRing(ReconciliationKind.BrokerStateMismatch, _proposalId[i], fill.OrderId, brokerRemaining);
                            }
                        }
                        if (cum < _orderQty[i])   // 부분 체결 — 슬롯 유지
                        {
                            Interlocked.Increment(ref _partials);
                            record = false; kind = ReconciliationKind.Filled;
                            goto done;
                        }
                        var outcome = _router.OnFill(_routerKey[i], _broker[i]);
                        kind = cum > _orderQty[i] ? ReconciliationKind.Overfill
                             : outcome switch
                               {
                                   FillNoticeKind.Filled => ReconciliationKind.Filled,
                                   FillNoticeKind.FilledAfterCancel => ReconciliationKind.FilledAfterCancel,
                                   _ => ReconciliationKind.StaleFill,
                               };
                        // [방향 A] Cancel-Fill Race 배리어 — 취소 요청이 브로커에 닿기 전 완전 체결: 체결이 진실(노출 반영) · 슬롯 종결 ·
                        //   뒤늦은 취소확인/취소거부 통보는 미추적 번호로 무해 처리(UnknownOrder · 회로 무영향)
                        if (kind == ReconciliationKind.Filled && _cancelAt[i] != 0) { kind = ReconciliationKind.CancelRaceFilled; Interlocked.Increment(ref _cancelRaceFilled); }
                        qty = cum;
                        record = kind != ReconciliationKind.Filled;   // 정상 완전 체결은 대사 이상 아님(계수만 · 링 오염 방지)
                        FreeSlot(i);
                    }
                    else if (status == BridgeFillStatus.Rejected)
                    {
                        // [키움 프로토콜] 거부 = 종결 — TTL 대기 없이 즉시 반납(무장 유지 · OrderSlotAge 비트 미격발)
                        _router.ReleaseTerminal(_routerKey[i], _broker[i]);
                        kind = ReconciliationKind.Rejected;
                        qty = _orderQty[i] - _filled[i];
                        Interlocked.Increment(ref _rejectedReleased);
                        FreeSlot(i);
                    }
                    else if (status == BridgeFillStatus.CancelConfirmed)
                    {
                        // 캐스케이드 취소 전환 슬롯이면 정식 확인, 아니면(운용자/브로커 직접 취소) 종결 해제
                        if (!_router.OnCancelConfirmed(_routerKey[i], _broker[i])) _router.ReleaseTerminal(_routerKey[i], _broker[i]);
                        kind = ReconciliationKind.CancelConfirmed;
                        qty = _filled[i];                                    // 취소 전 경합 체결 누적(부분 체결 후 취소 = 실 노출 기록)
                        FreeSlot(i);
                    }
                    else if (status == BridgeFillStatus.Accepted)
                    {
                        // 접수 확인 = 호가 대기 주문 — 짧은 응답 TTL 대신 장기 TTL · 잔량 대조
                        _accepted[i] = true; _acked[i] = true;
                        Interlocked.Increment(ref _accepts);
                        if (_modQty[i] > 0 && brokerRemaining == _modQty[i] && InWindow(_modAt[i], now))
                        {
                            // [정정] 브로커가 정정 수량으로 접수 — 추적 주문 수량 재기준(누적 체결 + 정정 잔량)
                            modApplied = _modQty[i];
                            _orderQty[i] = _filled[i] + _modQty[i];
                            _modQty[i] = 0; _modAt[i] = 0;
                            WriteRing(ReconciliationKind.ModifyApplied, _proposalId[i], fill.OrderId, modApplied);
                        }
                        else if (brokerRemaining >= 0 && brokerRemaining != _orderQty[i] - _filled[i])
                        {
                            if (AmendInFlight(i, now)) Interlocked.Increment(ref _amendTolerated);
                            else
                            {
                                mismatchRemaining = brokerRemaining;
                                WriteRing(ReconciliationKind.BrokerStateMismatch, _proposalId[i], fill.OrderId, brokerRemaining);
                            }
                        }
                        record = false; kind = ReconciliationKind.Filled;
                        goto done;
                    }
                    else
                    {
                        // Legacy(구 프록시 · 체결량 0 모호 통보) — 구 동작 유지
                        if (!_router.OnCancelConfirmed(_routerKey[i], _broker[i])) { _acked[i] = true; Interlocked.Increment(ref _zeroQtyIgnored); return; }
                        kind = ReconciliationKind.CancelConfirmed;
                        FreeSlot(i);
                    }
                }
                if (record) WriteRing(kind, proposal, fill.OrderId, qty);
            done:;
            }
            if (extremeFillRaw != 0) Emit(ReconciliationKind.ExtremeSlippage, proposal, fill.OrderId, extremeFillRaw);
            if (modApplied != 0) { Interlocked.Increment(ref _modifyApplied); Emit(ReconciliationKind.ModifyApplied, proposal, fill.OrderId, modApplied); }
            if (mismatchRemaining >= 0)
            {
                Interlocked.Increment(ref _mismatches);
                _mismatch?.Trip();
                Emit(ReconciliationKind.BrokerStateMismatch, proposal, fill.OrderId, mismatchRemaining);
            }
            if (record) Emit(kind, proposal, fill.OrderId, qty);
        }

        // ── [방향 A] Slot Age Sanitizer ────────────────────────────────────────

        /// <summary>TTL 초과 슬롯 회수(δ=0 · 주기 호출). 반환 = 이번에 회수한 수. 1건이라도 회수되면 sticky 무장 차단.</summary>
        public int SweepExpired()
        {
            long now = _nowMicros();
            int reclaimed = 0;
            long ringStart, ringEnd;
            lock (_gate)
            {
                ringStart = _ringWrite;
                for (int i = 0; i < _state.Length; i++)
                {
                    if (_state[i] == Free) continue;
                    long ttl = _accepted[i] ? _acceptedTtlMicros : _ttlMicros;   // [키움 프로토콜] 접수 확인 주문은 장기 TTL
                    if (now - _admittedMicros[i] <= ttl) continue;
                    _router.ReclaimExpired(_routerKey[i], out bool wasCancelling);
                    var kind = wasCancelling ? ReconciliationKind.ExpiredWhileCancelling
                             : _accepted[i] ? ReconciliationKind.ExpiredAccepted
                             : _acked[i] ? ReconciliationKind.ExpiredAcknowledged : ReconciliationKind.Expired;
                    WriteRing(kind, _proposalId[i], _brokerOrderId[i], _filled[i]);
                    FreeSlot(i);
                    reclaimed++;
                }
                ringEnd = _ringWrite;
            }
            if (reclaimed > 0)
            {
                Interlocked.Add(ref _expired, reclaimed);
                Volatile.Write(ref _tripped, 1);                      // 플래그 선기록 → 집계 비트
                Volatile.Read(ref _interlockLink)?.Resync();
                // 회수 건별 대사 기록을 싱크로 사출(락 밖 · 링에 남은 범위만 — 한 번에 링 용량 초과 회수 시 최신 RingCap 건)
                for (long k = Math.Max(ringStart, ringEnd - RingCap); k < ringEnd; k++)
                {
                    ReconciliationRecord rec;
                    lock (_gate) rec = _ring[(int)(k % RingCap)];
                    Emit(rec.Kind, rec.ProposalId, rec.BrokerOrderId, rec.Qty);
                }
            }
            return reclaimed;
        }

        // ── 내부 ─────────────────────────────────────────────────────────────

        // 체결가(double 원) → QPrice raw(10^8) — 비유한/음수/범위 초과 = false(관측 생략 · 대사는 정상 진행)
        private static bool TryPriceRaw(double won, out long raw)
        {
            raw = 0;
            if (!double.IsFinite(won) || won <= 0 || won > 90_000_000_000d) return false;
            raw = (long)Math.Round(won * 100_000_000d, MidpointRounding.AwayFromZero);
            return raw > 0;
        }

        private int FindByProposal(long proposalId)
        {
            for (int i = 0; i < _state.Length; i++) if (_state[i] != Free && _proposalId[i] == proposalId) return i;
            return -1;
        }

        private int FindByBrokerOrder(long brokerOrderId)
        {
            if (brokerOrderId == 0) return -1;
            for (int i = 0; i < _state.Length; i++) if (_state[i] == Accepted && _brokerOrderId[i] == brokerOrderId) return i;
            return -1;
        }

        private void FreeSlot(int i)
        {
            _state[i] = Free; _proposalId[i] = 0; _brokerOrderId[i] = 0; _routerKey[i] = default; _acked[i] = false; _accepted[i] = false;
            _cancelAt[i] = 0; _modQty[i] = 0; _modAt[i] = 0;
            _tracked--;
        }

        private void WriteRing(ReconciliationKind kind, long proposal, long brokerId, long qty)
        {
            long w = _ringWrite;
            long micros = _nowMicros();
            _ring[(int)(w % RingCap)] = new ReconciliationRecord(micros, kind, proposal, brokerId, qty);
            Volatile.Write(ref _ringWrite, w + 1);
            // [방향 A] 포렌식 해시 체인 각인(δ=0 · 값 타입) — 대사 이력이 이후 위조되면 ForensicChainDisarmInterlock 이 감지
            if (Volatile.Read(ref _forensic) is { } f)
            {
                try { f.Append(MS2E.Core.AuditEventKind.Reconciliation, ((long)kind << 56) | (brokerId & 0x00FF_FFFF_FFFF_FFFFL), micros); }
                catch { Interlocked.Increment(ref _faults); }
            }
        }

        private void Record(ReconciliationKind kind, long proposal, long brokerId, long qty)
        {
            lock (_gate) WriteRing(kind, proposal, brokerId, qty);
            Emit(kind, proposal, brokerId, qty);
        }

        private void Emit(ReconciliationKind kind, long proposal, long brokerId, long qty)
        {
            if (_onRecord is null) return;
            try { _onRecord(new ReconciliationRecord(_nowMicros(), kind, proposal, brokerId, qty)); }
            catch (Exception) { Interlocked.Increment(ref _faults); }
        }
    }

    /// <summary>
    /// [키움 프로토콜 · 방향 B] Broker-Client State Mismatch Disarm Interlock — 클라이언트 추적 잔량과 브로커 미체결 잔량(902 FID)이
    /// 다르면 전체 발주 회로를 sticky Disarm(물리 이중키 재무장). 불일치 = 체결 통보 유실/중복 · 정정 미반영 · 타 경로 주문 혼입 —
    /// 어느 쪽이든 클라이언트가 실 노출을 모르는 상태이므로 추가 발주를 막는다(미분류 = fail-closed).
    /// </summary>
    public sealed class BrokerStateMismatchInterlock : IArmingBlockSource
    {
        private int _tripped;
        private long _trips;
        private ArmingInterlockLink? _link;

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Trips => Interlocked.Read(ref _trips);

        public void BindInterlock(ArmingInterlockBitmask aggregator, ArmingInterlockSource bit)
        {
            var link = aggregator.Bind(bit, this);
            Volatile.Write(ref _link, link);
            link.Resync();
        }

        public void Trip()
        {
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
