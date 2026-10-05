// ============================================================================
// ExecutionMatrixTracker + ExecutionImpactInterlock — [알고 집행 하드닝 Think Beyond 방향 A+B]
//
//   교정 전(실측): TwapVwapExecutor 는 IBroker.Place 의 "접수(ok)"를 곧 체결로 계상(FilledQty += qty)했다 — 부분 체결·체결 지연·
//   취소 경합 체결이 모 주문 회계에 반영될 곳이 없었고, 슬라이스 체결가 평균을 추적하지 않아 집행 중 슬리피지 누적을 감지할 수 없었다.
//
//   ▸ ExecutionMatrixTracker(방향 A) — 고정 용량 unmanaged 슬롯 배열(ChildSlot: 계획/체결 수량 · Int128 체결대금 · 자식 주문 ID · 상태).
//     생성 시 1회 할당, 이후 Plan/MarkSent/ApplyFill/RequestCancelAllOpen/ConfirmCancelled 힙 0(경합 없는 Monitor 직렬화).
//       · 분할 계획: PlanTwap/PlanVwap(ReadOnlySpan<long> 거래량 가중치) — Int128 누적 목표 배분으로 Σslice = 총량 "정확"(오버필 0 ·
//         반올림 잔량 유실 0 · 음수 슬라이스 0). 가중치 음수/전부 0 → TWAP 폴백 또는 거부.
//       · 부분 체결 수렴: 자식 잔량을 넘는 체결 통보는 잔량까지만 반영하고 초과분은 OverfillRejectedQty 로 격리(모 주문 체결 ≤ 총량 불변).
//         취소 요청 후 도착한 체결은 실체결이므로 반영하되 LateAfterCancel 로 구분(유실 0). 체결대금은 Int128(틱 스케일 곱 오버플로 0).
//       · 평균 체결가·체결률(ppm)·잔량·미체결 오픈 수량을 µs 단위 호출마다 즉시 산출.
//   ▸ ExecutionImpactInterlock(방향 B) — 도착가(arrival) 대비 평균 체결가의 불리 이탈(bps, Int128)이 한도를 넘는 순간:
//       ① 남은 모든 자식 주문 취소 요청(ID 를 사전 할당 버퍼로 추출 → cancelChild 콜백) ② 인터락 Trip(sticky · ArmingBlocked)
//       ③ VolatileMemoryPurger(선택) 0x00 ④ onTrip(킬스위치). 이후 모든 신규 슬라이스 발주 거부(CanSendNext=false).
//   불변: liveArmed:false(판정·추적·취소 요청만 — 발주 경로 참조 0, 무장 부여 불가) · δ=0(핫 호출 힙 0) · 결정론(주입값 · 시계 0).
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    public enum ChildOrderState : byte { Free = 0, Planned = 1, Sent = 2, PartiallyFilled = 3, Filled = 4, CancelRequested = 5, Cancelled = 6 }

    public enum ChildFillVerdict : byte { Applied = 0, ChildCompleted = 1, OverfillClamped = 2, LateAfterCancel = 3, UnknownChild = 4, InvalidFill = 5, NotSent = 6 }

    public struct ChildSlot
    {
        public long PlannedQty;
        public long FilledQty;
        public Int128 FilledNotional;
        public long ChildOrderId;
        public ChildOrderState State;
    }

    public sealed class ExecutionMatrixTracker
    {
        private readonly ChildSlot[] _slots;
        private readonly object _gate = new();
        private int _count;
        private long _parentQty, _filled, _overfillRejected;
        private Int128 _notional;

        public ExecutionMatrixTracker(int maxChildren)
        {
            if (maxChildren < 1) throw new ArgumentOutOfRangeException(nameof(maxChildren));
            _slots = new ChildSlot[maxChildren];
        }

        public int Capacity => _slots.Length;
        public int ChildCount { get { lock (_gate) return _count; } }
        public long ParentQty { get { lock (_gate) return _parentQty; } }
        public long FilledQty { get { lock (_gate) return _filled; } }
        /// <summary>모 주문 미체결 잔량(= 총량 − 체결). 오버필 불가이므로 항상 ≥ 0.</summary>
        public long ResidualQty { get { lock (_gate) return _parentQty - _filled; } }
        public long OverfillRejectedQty { get { lock (_gate) return _overfillRejected; } }
        /// <summary>체결률(ppm = 백만분율).</summary>
        public long FillRatioPpm { get { lock (_gate) return _parentQty <= 0 ? 0 : (long)((Int128)_filled * 1_000_000 / _parentQty); } }
        /// <summary>평균 체결가(raw). 체결 0 이면 0.</summary>
        public long AvgFillPriceRaw { get { lock (_gate) return _filled <= 0 ? 0 : (long)(_notional / _filled); } }

        /// <summary>하위 브로커에 나가 있고 아직 취소 요청되지 않은 자식들의 미체결 합.</summary>
        public long OpenQty
        {
            get
            {
                lock (_gate)
                {
                    long open = 0;
                    for (int i = 0; i < _count; i++)
                        if (_slots[i].State is ChildOrderState.Sent or ChildOrderState.PartiallyFilled)
                            open += _slots[i].PlannedQty - _slots[i].FilledQty;
                    return open;
                }
            }
        }

        public ChildSlot ReadChild(int child) { lock (_gate) return (uint)child < (uint)_count ? _slots[child] : default; }

        // ── 분할 계획(순수 · δ=0) ─────────────────────────────────────────

        /// <summary>균등 분할 — Σ = total 정확(나머지는 앞 슬라이스부터 1씩). 반환 = 슬라이스 수(−1 = 입력 오류).</summary>
        public static int PlanTwap(long totalQty, int slices, Span<long> dst)
        {
            if (totalQty <= 0 || slices < 1 || slices > dst.Length) return -1;
            long per = totalQty / slices, rem = totalQty % slices;
            for (int i = 0; i < slices; i++) dst[i] = per + (i < rem ? 1 : 0);
            return slices;
        }

        /// <summary>거래량 가중 분할 — target_i = ⌊total × Σw_0..i / Σw⌋(Int128), slice_i = target_i − target_{i−1}.
        /// Σ = total 정확 · 슬라이스 ≥ 0. 가중치 음수 → −1(거부), 전부 0 → TWAP 폴백.</summary>
        public static int PlanVwap(long totalQty, ReadOnlySpan<long> volumeWeights, Span<long> dst)
        {
            int n = volumeWeights.Length;
            if (totalQty <= 0 || n < 1 || n > dst.Length) return -1;
            Int128 wsum = 0;
            for (int i = 0; i < n; i++)
            {
                if (volumeWeights[i] < 0) return -1;
                wsum += volumeWeights[i];
            }
            if (wsum == 0) return PlanTwap(totalQty, n, dst);
            Int128 cum = 0;
            long prev = 0;
            for (int i = 0; i < n; i++)
            {
                cum += volumeWeights[i];
                long target = i == n - 1 ? totalQty : (long)((Int128)totalQty * cum / wsum);
                dst[i] = target - prev;
                prev = target;
            }
            return n;
        }

        // ── 집행 추적(δ=0) ───────────────────────────────────────────────

        /// <summary>모 주문 개시 — 계획 슬라이스 합이 총량과 정확히 같아야 한다(아니면 거부: 오버필/잔량 유실 계획 차단).</summary>
        public bool Begin(long parentQty, ReadOnlySpan<long> plannedSlices)
        {
            if (parentQty <= 0 || plannedSlices.Length < 1 || plannedSlices.Length > _slots.Length) return false;
            long sum = 0;
            foreach (long q in plannedSlices)
            {
                if (q < 0 || sum > long.MaxValue - q) return false;
                sum += q;
            }
            if (sum != parentQty) return false;
            lock (_gate)
            {
                Array.Clear(_slots);
                for (int i = 0; i < plannedSlices.Length; i++) { _slots[i].PlannedQty = plannedSlices[i]; _slots[i].State = ChildOrderState.Planned; }
                _count = plannedSlices.Length;
                _parentQty = parentQty; _filled = 0; _notional = 0; _overfillRejected = 0;
                return true;
            }
        }

        public bool MarkSent(int child, long childOrderId)
        {
            lock (_gate)
            {
                if ((uint)child >= (uint)_count || _slots[child].State != ChildOrderState.Planned || _slots[child].PlannedQty <= 0) return false;
                _slots[child].ChildOrderId = childOrderId;
                _slots[child].State = ChildOrderState.Sent;
                return true;
            }
        }

        /// <summary>체결 통보 반영(δ=0). 자식 잔량 초과분은 반영하지 않고 격리 · 취소 요청 후 체결은 LateAfterCancel 로 반영.</summary>
        public ChildFillVerdict ApplyFill(int child, long qty, long priceRaw)
        {
            if (qty <= 0 || priceRaw <= 0) return ChildFillVerdict.InvalidFill;
            lock (_gate)
            {
                if ((uint)child >= (uint)_count) return ChildFillVerdict.UnknownChild;
                ref var s = ref _slots[child];
                if (s.State is ChildOrderState.Planned or ChildOrderState.Free) return ChildFillVerdict.NotSent;   // 발주 전 체결 = 이상 통보
                long remaining = s.PlannedQty - s.FilledQty;
                if (remaining <= 0) { _overfillRejected += qty; return ChildFillVerdict.OverfillClamped; }
                long take = qty > remaining ? remaining : qty;
                if (qty > remaining) _overfillRejected += qty - remaining;
                bool late = s.State is ChildOrderState.CancelRequested or ChildOrderState.Cancelled;
                s.FilledQty += take;
                s.FilledNotional += (Int128)take * priceRaw;
                _filled += take;
                _notional += (Int128)take * priceRaw;
                if (!late) s.State = s.FilledQty == s.PlannedQty ? ChildOrderState.Filled : ChildOrderState.PartiallyFilled;
                if (qty > remaining) return ChildFillVerdict.OverfillClamped;
                if (late) return ChildFillVerdict.LateAfterCancel;
                return s.State == ChildOrderState.Filled ? ChildFillVerdict.ChildCompleted : ChildFillVerdict.Applied;
            }
        }

        /// <summary>발주됐고 미체결이 남은 자식 전부를 취소 요청 상태로 원자 전이하고 그 자식 주문 ID 를 idsOut 에 기록(δ=0).
        /// 아직 발주 전(Planned)인 자식도 취소(이후 발주 불가)로 전이한다. 반환 = idsOut 에 기록한 수.</summary>
        public int RequestCancelAllOpen(Span<long> idsOut)
        {
            lock (_gate)
            {
                int n = 0;
                for (int i = 0; i < _count; i++)
                {
                    ref var s = ref _slots[i];
                    if (s.State == ChildOrderState.Planned) { s.State = ChildOrderState.Cancelled; continue; }
                    if (s.State is not (ChildOrderState.Sent or ChildOrderState.PartiallyFilled)) continue;
                    s.State = ChildOrderState.CancelRequested;
                    if (n < idsOut.Length) idsOut[n++] = s.ChildOrderId;
                }
                return n;
            }
        }

        public bool ConfirmCancelled(int child)
        {
            lock (_gate)
            {
                if ((uint)child >= (uint)_count || _slots[child].State != ChildOrderState.CancelRequested) return false;
                _slots[child].State = ChildOrderState.Cancelled;
                return true;
            }
        }

        /// <summary>다음 발주 가능 자식 인덱스(Planned 중 계획 수량 &gt; 0 인 첫 자식) — 없으면 −1.</summary>
        public int NextPlanned()
        {
            lock (_gate)
            {
                for (int i = 0; i < _count; i++)
                    if (_slots[i].State == ChildOrderState.Planned && _slots[i].PlannedQty > 0) return i;
                return -1;
            }
        }
    }

    /// <summary>[방향 B] Execution Impact Anti-Spill Interlock — 평균 체결가가 도착가 대비 허용 슬리피지를 넘으면 잔여 조각 전량 취소 + Disarm.</summary>
    public sealed class ExecutionImpactInterlock
    {
        private readonly ExecutionMatrixTracker _tracker;
        private readonly bool _isBuy;
        private readonly long _arrivalPriceRaw, _maxAdverseBps;
        private readonly Action<long>? _cancelChild;
        private readonly VolatileMemoryPurger? _purger;
        private readonly Action<long>? _onTrip;
        private readonly long[] _cancelIds;
        private int _tripped;
        private long _cancelRequests, _lastAdverseBps, _faults;

        public ExecutionImpactInterlock(ExecutionMatrixTracker tracker, bool isBuy, long arrivalPriceRaw, long maxAdverseBps,
                                        Action<long>? cancelChild = null, VolatileMemoryPurger? purger = null, Action<long>? onTrip = null)
        {
            _tracker = tracker ?? throw new ArgumentNullException(nameof(tracker));
            if (arrivalPriceRaw <= 0) throw new ArgumentOutOfRangeException(nameof(arrivalPriceRaw));
            if (maxAdverseBps < 0) throw new ArgumentOutOfRangeException(nameof(maxAdverseBps));
            _isBuy = isBuy; _arrivalPriceRaw = arrivalPriceRaw; _maxAdverseBps = maxAdverseBps;
            _cancelChild = cancelChild; _purger = purger; _onTrip = onTrip;
            _cancelIds = new long[tracker.Capacity];   // 1회 할당 — Trip 시 취소 ID 추출 힙 0
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        /// <summary>★blockWhile — 임팩트 초과 이후 무장 차단(sticky).</summary>
        public bool ArmingBlocked => Tripped;
        /// <summary>다음 조각 발주 허용 여부(Trip 후 false).</summary>
        public bool CanSendNext => !Tripped;
        public long CancelRequests => Interlocked.Read(ref _cancelRequests);
        public long LastAdverseBps => Interlocked.Read(ref _lastAdverseBps);
        public long CallbackFaults => Interlocked.Read(ref _faults);

        /// <summary>도착가 대비 불리 이탈(bps · 음수 = 유리). Int128 — 틱 스케일 곱 오버플로 0.</summary>
        public static long AdverseBps(bool isBuy, long arrivalRaw, long avgFillRaw)
        {
            if (arrivalRaw <= 0 || avgFillRaw <= 0) return 0;
            Int128 d = isBuy ? (Int128)avgFillRaw - arrivalRaw : (Int128)arrivalRaw - avgFillRaw;
            Int128 bps = d * 10_000 / arrivalRaw;
            return bps > long.MaxValue ? long.MaxValue : bps < long.MinValue ? long.MinValue : (long)bps;
        }

        /// <summary>체결 1건 반영 + 임팩트 판정(δ=0). 한도 초과 시 즉시 Trip(잔여 조각 전량 취소 요청).</summary>
        public ChildFillVerdict OnFill(int child, long qty, long priceRaw)
        {
            var v = _tracker.ApplyFill(child, qty, priceRaw);
            long adverse = AdverseBps(_isBuy, _arrivalPriceRaw, _tracker.AvgFillPriceRaw);
            Interlocked.Exchange(ref _lastAdverseBps, adverse);
            if (adverse > _maxAdverseBps) Trip(adverse);
            return v;
        }

        private void Trip(long adverse)
        {
            if (Interlocked.CompareExchange(ref _tripped, 1, 0) != 0) return;
            int n = _tracker.RequestCancelAllOpen(_cancelIds);
            for (int i = 0; i < n; i++)
            {
                Interlocked.Increment(ref _cancelRequests);
                try { _cancelChild?.Invoke(_cancelIds[i]); } catch { Interlocked.Increment(ref _faults); }
            }
            try { _purger?.PurgeAll(); } catch { Interlocked.Increment(ref _faults); }
            try { _onTrip?.Invoke(adverse); } catch { Interlocked.Increment(ref _faults); }
        }

        /// <summary>★물리 이중키 재무장.</summary>
        public bool TryRearm(bool valveWired, bool liveArmingWired)
        {
            if (!(valveWired && liveArmingWired)) return false;
            Volatile.Write(ref _tripped, 0);
            return true;
        }
    }
}
