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
    }

    public readonly record struct ReconciliationRecord(long Micros, ReconciliationKind Kind, long ProposalId, long BrokerOrderId, long Qty);

    public sealed class OrderExecutionFeedAdapter : IArmingBlockSource
    {
        public const long DefaultSlotTtlMicros = 120_000_000;   // 2분 — 무응답 슬롯 회수 임계
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
        private readonly long[] _proposalId, _brokerOrderId, _orderQty, _filled, _admittedMicros;
        private readonly UInt128[] _routerKey;
        private readonly ReconciliationRecord[] _ring = new ReconciliationRecord[RingCap];
        private long _ringWrite;
        private int _tracked, _tripped;
        private long _partials, _zeroQtyIgnored, _unknown, _expired, _trackFull, _faults;
        private ArmingInterlockLink? _interlockLink;

        public OrderExecutionFeedAdapter(ResilientOrderRouter router, int capacity = 1024, long slotTtlMicros = DefaultSlotTtlMicros,
                                         Func<long>? nowMicros = null, Action<ReconciliationRecord>? onRecord = null,
                                         StockManager.Engines.ExtremeSlippageDisarmInterlock? extremeSlippage = null)
        {
            _extreme = extremeSlippage;
            _router = router ?? throw new ArgumentNullException(nameof(router));
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (slotTtlMicros <= 0) throw new ArgumentOutOfRangeException(nameof(slotTtlMicros));
            _ttlMicros = slotTtlMicros;
            _nowMicros = nowMicros ?? ReferencePriceFeedAdapter.MonotonicMicros;
            _onRecord = onRecord;
            _state = new int[capacity]; _broker = new int[capacity]; _acked = new bool[capacity]; _isBuy = new bool[capacity]; _refRaw = new long[capacity];
            _proposalId = new long[capacity]; _brokerOrderId = new long[capacity]; _orderQty = new long[capacity];
            _filled = new long[capacity]; _admittedMicros = new long[capacity]; _routerKey = new UInt128[capacity];
        }

        public int Tracked { get { lock (_gate) return _tracked; } }
        public long PartialFills => Interlocked.Read(ref _partials);
        public long ZeroQtyIgnored => Interlocked.Read(ref _zeroQtyIgnored);
        public long UnknownNotices => Interlocked.Read(ref _unknown);
        public long ExpiredSlots => Interlocked.Read(ref _expired);
        public long TrackFull => Interlocked.Read(ref _trackFull);
        public long SinkFaults => Interlocked.Read(ref _faults);
        public long RecordsWritten => Interlocked.Read(ref _ringWrite);
        public long SlotTtlMicros => _ttlMicros;

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
                    _isBuy[i] = isBuy; _refRaw[i] = referenceRaw;
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
            long extremeFillRaw = 0;
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
                        qty = cum;
                        record = kind != ReconciliationKind.Filled;   // 정상 완전 체결은 대사 이상 아님(계수만 · 링 오염 방지)
                        FreeSlot(i);
                    }
                    else
                    {
                        if (!_router.OnCancelConfirmed(_routerKey[i], _broker[i])) { _acked[i] = true; Interlocked.Increment(ref _zeroQtyIgnored); return; }
                        kind = ReconciliationKind.CancelConfirmed;
                        FreeSlot(i);
                    }
                }
                if (record) WriteRing(kind, proposal, fill.OrderId, qty);
            done:;
            }
            if (extremeFillRaw != 0) Emit(ReconciliationKind.ExtremeSlippage, proposal, fill.OrderId, extremeFillRaw);
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
                    if (_state[i] == Free || now - _admittedMicros[i] <= _ttlMicros) continue;
                    _router.ReclaimExpired(_routerKey[i], out bool wasCancelling);
                    var kind = wasCancelling ? ReconciliationKind.ExpiredWhileCancelling
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
            _state[i] = Free; _proposalId[i] = 0; _brokerOrderId[i] = 0; _routerKey[i] = default; _acked[i] = false;
            _tracked--;
        }

        private void WriteRing(ReconciliationKind kind, long proposal, long brokerId, long qty)
        {
            long w = _ringWrite;
            _ring[(int)(w % RingCap)] = new ReconciliationRecord(_nowMicros(), kind, proposal, brokerId, qty);
            Volatile.Write(ref _ringWrite, w + 1);
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
}
