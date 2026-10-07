// ============================================================================
// ChildSlotTrackingMatrix — [자식 슬롯 분할 · 방향 A+B] 고정 값타입 배열 자식 슬롯 상태 매트릭스 + 체결 불균형 차단기 + 거부 인터락(δ=0)
//
//  방향 A — 부모 그룹(기본 256개) × 자식 슬롯 64개(C#12 InlineArray · blittable 값타입 · 관리 힙 배열 1개에 연속 배치).
//    슬롯 = ChildId · 브로커 · 브로커 주문번호 · 주문/체결/취소 수량 · 상태(Pending→Sent→Accepted→Filled/Cancelled/Rejected/Reclaimed) ·
//    송출/최초 체결/최종 체결 시각(µs). 그룹마다 seqlock 시퀀스 — 작성자는 CAS(짝→홀)로 배타 진입(다중 작성자 안전: 드레인 스레드 · 체결 통보
//    스레드 · 평가 타이머), 판독자는 락 없이 홀수/전후 불일치면 재시도(찢김 없는 부모 스냅샷).
//  수렴(부모 상태): AllFilled(전 자식 체결) · PartialCancelled(체결+취소/회수로 종결) · ChildRejected(거부/송출 실패 포함 종결) · Open.
//  ★Fail-Closed 거부 인터락: 자식 송출 실패/브로커 거부 = 같은 부모의 미송출(Pending) 형제 0ms 회수(Reclaimed · 송출 0) +
//    ChildSlotRejection 비트(23) sticky Raise(Disarm). 이미 접수된 형제는 그대로 추적(체결/취소 통보 수렴) — 방향 B 가 불균형이면 취소.
//  방향 B — 불균형 체결 차단기: 같은 부모에서 어떤 자식이 체결을 시작(최초 체결)한 뒤 임계(기본 500ms) 넘게 체결 0 인 '접수된' 형제가 있으면
//    그 부모의 잔여 자식(미체결 잔량 &gt; 0) 전부 취소 요청(브로커 · 브로커 주문번호 콜백) + 비트 Raise. 취소는 노출 축소 — 무장 상태와 무관.
//    같은 자식에 대한 취소 요청은 1회(Cancelling 표지).
//  ★No Over-Dispatch: Open 시 자식 주수 합 ≤ 부모 수량을 검증(초과 = 거부) · 체결 누적이 자식 주문 수량을 넘으면 Overfill 계수 + 비트 Raise.
// ============================================================================
using System;
using System.Runtime.CompilerServices;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;

namespace StockManager.Engines
{
    public enum ChildSlotState : byte { Empty = 0, Pending = 1, Sent = 2, Accepted = 3, Filled = 4, Cancelled = 5, Rejected = 6, Reclaimed = 7 }
    public enum ParentConvergence : byte { Unknown = 0, Open = 1, AllFilled = 2, PartialCancelled = 3, ChildRejected = 4 }

    public struct ChildSlot
    {
        public long ChildId, BrokerOrderId, OrderQty, FilledQty, CancelledQty, SentMicros, FirstFillMicros, LastFillMicros;
        public long FillValueWon;        // Σ 체결수량 × 체결가(원) — 가중평균 체결가 = FillValueWon / FilledQty
        public int Broker;
        public ChildSlotState State;
        public byte Cancelling;
    }

    [InlineArray(ChildIdSpace.MaxChildren)]
    public struct ChildSlotBlock { private ChildSlot _e0; }

    public struct ParentGroup
    {
        public long Seq;                 // seqlock(짝 = 안정 · 홀 = 작성 중)
        public long ParentId, ParentQty, OpenedMicros;
        public int Count;
        public ChildSlotBlock Children;
    }

    /// <summary>부모 스냅샷(찢김 없는 판독 · 값 타입).</summary>
    public readonly record struct ParentSnapshot(long ParentId, int Children, long OrderQty, long FilledQty, long CancelledQty, int Rejected, int Reclaimed, ParentConvergence State,
                                                 long FillValueWon = 0)
    {
        /// <summary>부모 가중평균 체결가(원 · 체결 0 = 0).</summary>
        public long AvgFillPriceWon => FilledQty > 0 ? FillValueWon / FilledQty : 0;
    }

    public sealed class ChildSlotTrackingMatrix : IChildSlotLedger, IArmingBlockSource
    {
        public const long DefaultImbalanceMicros = 500_000;

        private readonly ParentGroup[] _groups;
        private readonly Func<long> _nowMicros;
        private readonly long _imbalanceMicros;
        private readonly Action<int, long>? _cancel;       // (브로커, 브로커 주문번호) → 취소 송신
        private int _tripped;
        private long _opened, _rejectTrips, _imbalanceTrips, _reclaimed, _cancelRequests, _cancelFaults, _overfills, _unknownFills, _released;
        private ArmingInterlockLink? _link;

        public ChildSlotTrackingMatrix(int parentCapacity = 256, Func<long>? nowMicros = null, long imbalanceMicros = DefaultImbalanceMicros, Action<int, long>? cancel = null)
        {
            if (parentCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(parentCapacity));
            if (imbalanceMicros <= 0) throw new ArgumentOutOfRangeException(nameof(imbalanceMicros));
            _groups = GC.AllocateArray<ParentGroup>(parentCapacity, pinned: true);   // POH 연속 배치(GC 이동 0)
            _nowMicros = nowMicros ?? (() => System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency);
            _imbalanceMicros = imbalanceMicros;
            _cancel = cancel;
        }

        public bool Tripped => Volatile.Read(ref _tripped) == 1;
        public bool ArmingBlocked => Tripped;
        public long Opened => Interlocked.Read(ref _opened);
        public long RejectTrips => Interlocked.Read(ref _rejectTrips);
        public long ImbalanceTrips => Interlocked.Read(ref _imbalanceTrips);
        public long Reclaimed => Interlocked.Read(ref _reclaimed);
        public long CancelRequests => Interlocked.Read(ref _cancelRequests);
        public long CancelFaults => Interlocked.Read(ref _cancelFaults);
        public long Overfills => Interlocked.Read(ref _overfills);
        public long UnknownFills => Interlocked.Read(ref _unknownFills);
        public long ReleasedGroups => Interlocked.Read(ref _released);
        public int Capacity => _groups.Length;

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
            Volatile.Read(ref _link)?.Resync();
            return true;
        }

        // ── IChildSlotLedger ──

        /// <summary>부모 그룹 개설(δ=0). 자식 합 &gt; 부모 수량 · 자식 &gt; 64 · 중복 부모 · 포화 = false.</summary>
        public bool Open(long parentId, ReadOnlySpan<ChildOrderPlan> children) => Open(parentId, long.MaxValue, children);

        public bool Open(long parentId, long parentQty, ReadOnlySpan<ChildOrderPlan> children)
        {
            if (parentId <= 0 || children.Length == 0 || children.Length > ChildIdSpace.MaxChildren) return false;
            long sum = 0;
            for (int i = 0; i < children.Length; i++)
            {
                if (children[i].Shares <= 0 || children[i].ParentId != parentId || ChildIdSpace.ParentOf(children[i].ChildId) != parentId) return false;
                sum += children[i].Shares;
            }
            if (sum > parentQty) return false;                                       // ★No Over-Dispatch
            if (Find(parentId) >= 0) return false;
            long now = _nowMicros();
            for (int g = 0; g < _groups.Length; g++)
            {
                ref var grp = ref _groups[g];
                if (Volatile.Read(ref grp.ParentId) != 0 || !TryBeginWrite(ref grp)) continue;
                if (grp.ParentId != 0) { EndWrite(ref grp); continue; }
                grp.ParentId = parentId; grp.ParentQty = parentQty == long.MaxValue ? sum : parentQty; grp.OpenedMicros = now; grp.Count = children.Length;
                for (int i = 0; i < ChildIdSpace.MaxChildren; i++)
                {
                    ref var c = ref grp.Children[i];
                    if (i < children.Length)
                        c = new ChildSlot { ChildId = children[i].ChildId, Broker = children[i].Broker, OrderQty = children[i].Shares, State = ChildSlotState.Pending };
                    else c = default;
                }
                EndWrite(ref grp);
                Interlocked.Increment(ref _opened);
                return true;
            }
            return false;
        }

        public void Abandon(long parentId)
        {
            int g = Find(parentId);
            if (g < 0) return;
            ref var grp = ref _groups[g];
            int n = 0;
            BeginWrite(ref grp);
            for (int k = 0; k < grp.Count; k++)
                if (grp.Children[k].State == ChildSlotState.Pending) { grp.Children[k].State = ChildSlotState.Reclaimed; n++; }
            EndWrite(ref grp);
            Interlocked.Add(ref _reclaimed, n);
        }

        /// <summary>[미결속 분할] 외부 판정(분할 가능한 브로커 0 · 결속 미완료)으로 분할 회로 Disarm(비트 23 · sticky).</summary>
        public void TripExternal() => Trip(ref _externalTrips);
        private long _externalTrips;
        public long ExternalTrips => Interlocked.Read(ref _externalTrips);

        public void MarkSent(long childId, long brokerOrderId)
        {
            int g = Find(ChildIdSpace.ParentOf(childId)); int i = ChildIdSpace.IndexOf(childId);
            if (g < 0 || i < 0) return;
            ref var grp = ref _groups[g];
            BeginWrite(ref grp);
            ref var c = ref grp.Children[i];
            if (i < grp.Count && c.ChildId == childId && c.State == ChildSlotState.Pending)
            {
                c.State = ChildSlotState.Sent; c.BrokerOrderId = brokerOrderId; c.SentMicros = _nowMicros();
            }
            EndWrite(ref grp);
        }

        public int MarkSendFailed(long childId)
        {
            int g = Find(ChildIdSpace.ParentOf(childId)); int i = ChildIdSpace.IndexOf(childId);
            if (g < 0 || i < 0) { Trip(ref _rejectTrips); return 0; }
            ref var grp = ref _groups[g];
            int reclaimed = 0;
            BeginWrite(ref grp);
            if (i < grp.Count && grp.Children[i].ChildId == childId) grp.Children[i].State = ChildSlotState.Rejected;
            for (int k = 0; k < grp.Count; k++)
            {
                ref var c = ref grp.Children[k];
                if (c.State == ChildSlotState.Pending) { c.State = ChildSlotState.Reclaimed; reclaimed++; }   // ★미송출 형제 0ms 회수(송출 0)
            }
            EndWrite(ref grp);
            Interlocked.Add(ref _reclaimed, reclaimed);
            Trip(ref _rejectTrips);                                                   // ★Disarm(sticky)
            return reclaimed;
        }

        // ── 브로커 통보 수렴(브리지 FillPayload 시그니처 그대로 · δ=0) ──

        public void OnFill(FillPayload fill)
        {
            var st = BridgeFillStatusCodec.Decode(in fill, out _);
            long now = _nowMicros();
            if (!TryLocate(fill.OrderId, out int g, out int i)) { Interlocked.Increment(ref _unknownFills); return; }
            ref var grp = ref _groups[g];
            bool overfill = false, rejected = false;
            BeginWrite(ref grp);
            ref var c = ref grp.Children[i];
            if (fill.FillQty > 0)
            {
                c.FilledQty += fill.FillQty;
                if (double.IsFinite(fill.FillPrice) && fill.FillPrice > 0) c.FillValueWon += fill.FillQty * (long)Math.Round(fill.FillPrice);
                if (c.FirstFillMicros == 0) c.FirstFillMicros = now == 0 ? 1 : now;
                c.LastFillMicros = now;
                if (c.FilledQty >= c.OrderQty) { overfill = c.FilledQty > c.OrderQty; c.State = ChildSlotState.Filled; }
                else if (c.State == ChildSlotState.Sent) c.State = ChildSlotState.Accepted;
            }
            else if (st == BridgeFillStatus.Accepted) { if (c.State == ChildSlotState.Sent) c.State = ChildSlotState.Accepted; }
            else if (st == BridgeFillStatus.CancelConfirmed) { c.CancelledQty = Math.Max(0, c.OrderQty - c.FilledQty); c.State = ChildSlotState.Cancelled; }
            else if (st == BridgeFillStatus.Rejected) { c.State = ChildSlotState.Rejected; rejected = true; }
            if (rejected)
                for (int k = 0; k < grp.Count; k++)
                    if (grp.Children[k].State == ChildSlotState.Pending) { grp.Children[k].State = ChildSlotState.Reclaimed; Interlocked.Increment(ref _reclaimed); }
            EndWrite(ref grp);
            if (overfill) { Interlocked.Increment(ref _overfills); Trip(ref _rejectTrips); }
            if (rejected) Trip(ref _rejectTrips);
        }

        // ── [KIS VTS] 브로커 슬롯 격리(프로토콜 이탈 · δ=0) ──

        private long _isolations;
        public long Isolations => Interlocked.Read(ref _isolations);

        /// <summary>
        /// 브로커 1개 격리: 그 브로커의 미송출(Pending) 자식 = 회수(송출 0) · 송출됐고 미체결 잔량이 있는 자식 = 즉시 취소 요청(1회) ·
        /// Disarm(비트 23 sticky). 반환 = 취소 요청 수. ★프로토콜 이탈 후 그 브로커 통보를 더는 믿을 수 없으므로 불균형 타이머(500ms)를
        /// 기다리지 않고 0ms 에 처리 — 미체결 슬롯 고착 0. 체결 이력은 보존(취소 확인/대사로 수렴).
        /// </summary>
        public int IsolateBroker(int broker)
        {
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0) spin.SpinOnce();   // 공용 취소 버퍼 단일 진입(Evaluate 와 직렬)
            int requested = 0, reclaimed = 0;
            try
            {
                for (int g = 0; g < _groups.Length; g++)
                {
                    ref var grp = ref _groups[g];
                    if (Volatile.Read(ref grp.ParentId) == 0) continue;
                    int fromHere = 0;
                    BeginWrite(ref grp);
                    for (int k = 0; k < grp.Count; k++)
                    {
                        ref var c = ref grp.Children[k];
                        if (c.Broker != broker) continue;
                        if (c.State == ChildSlotState.Pending) { c.State = ChildSlotState.Reclaimed; reclaimed++; continue; }
                        if (c.Cancelling != 0 || c.FilledQty >= c.OrderQty || c.BrokerOrderId == 0) continue;
                        if (c.State != ChildSlotState.Sent && c.State != ChildSlotState.Accepted) continue;
                        c.Cancelling = 1;
                        CancelRequestsBuffer(g, k, c.Broker, c.BrokerOrderId);
                        fromHere++;
                    }
                    EndWrite(ref grp);
                    if (fromHere > 0) { requested += fromHere; FlushCancels(); }
                }
            }
            finally { Volatile.Write(ref _evaluating, 0); }
            Interlocked.Add(ref _reclaimed, reclaimed);
            Interlocked.Increment(ref _isolations);
            Trip(ref _externalTrips);
            return requested;
        }

        // ── 방향 B: 불균형 체결 차단기(주기 호출 · δ=0) ──

        /// <summary>반환 = 이번에 취소 요청한 자식 수.</summary>
        public int Evaluate()
        {
            if (Interlocked.CompareExchange(ref _evaluating, 1, 0) != 0) return 0;   // 단일 진입(공용 취소 버퍼)
            try { return EvaluateCore(); }
            finally { Volatile.Write(ref _evaluating, 0); }
        }

        private int _evaluating;

        private int EvaluateCore()
        {
            long now = _nowMicros();
            int requested = 0;
            for (int g = 0; g < _groups.Length; g++)
            {
                ref var grp = ref _groups[g];
                if (Volatile.Read(ref grp.ParentId) == 0) continue;
                BeginWrite(ref grp);
                long firstFill = 0; bool laggard = false;
                for (int k = 0; k < grp.Count; k++)
                {
                    ref var c = ref grp.Children[k];
                    if (c.FirstFillMicros != 0 && (firstFill == 0 || c.FirstFillMicros < firstFill)) firstFill = c.FirstFillMicros;
                }
                if (firstFill != 0 && now - firstFill > _imbalanceMicros)
                    for (int k = 0; k < grp.Count; k++)
                    {
                        ref var c = ref grp.Children[k];
                        if ((c.State == ChildSlotState.Sent || c.State == ChildSlotState.Accepted) && c.FilledQty == 0) { laggard = true; break; }
                    }
                int fromHere = 0;
                if (laggard)
                {
                    for (int k = 0; k < grp.Count; k++)
                    {
                        ref var c = ref grp.Children[k];
                        if (c.Cancelling != 0 || c.FilledQty >= c.OrderQty || c.BrokerOrderId == 0) continue;
                        if (c.State != ChildSlotState.Sent && c.State != ChildSlotState.Accepted) continue;
                        c.Cancelling = 1;
                        CancelRequestsBuffer(g, k, c.Broker, c.BrokerOrderId);
                        fromHere++;
                    }
                }
                EndWrite(ref grp);
                if (fromHere > 0)
                {
                    requested += fromHere;
                    FlushCancels();
                    Trip(ref _imbalanceTrips);
                }
            }
            return requested;
        }

        // 취소 콜백은 seqlock 밖에서(외부 호출이 그룹 작성 구간을 오래 잡지 않도록) — 고정 버퍼
        private readonly int[] _pendingBroker = new int[ChildIdSpace.MaxChildren];
        private readonly long[] _pendingOrder = new long[ChildIdSpace.MaxChildren];
        private int _pendingCount;
        private void CancelRequestsBuffer(int g, int k, int broker, long order)
        {
            if (_pendingCount < _pendingBroker.Length) { _pendingBroker[_pendingCount] = broker; _pendingOrder[_pendingCount] = order; _pendingCount++; }
        }
        private void FlushCancels()
        {
            for (int k = 0; k < _pendingCount; k++)
            {
                try { _cancel?.Invoke(_pendingBroker[k], _pendingOrder[k]); Interlocked.Increment(ref _cancelRequests); }
                catch (Exception) { Interlocked.Increment(ref _cancelFaults); }
            }
            _pendingCount = 0;
        }

        // ── 판독 · 수명 ──

        /// <summary>부모 스냅샷(락 없는 seqlock 판독 · 찢김 없음 · δ=0).</summary>
        public bool TryGetParent(long parentId, out ParentSnapshot snap)
        {
            snap = default;
            int g = Find(parentId);
            if (g < 0) return false;
            ref var grp = ref _groups[g];
            for (int spin = 0; spin < 64; spin++)
            {
                long s1 = Volatile.Read(ref grp.Seq);
                if ((s1 & 1) != 0) { Thread.SpinWait(4); continue; }
                long pid = grp.ParentId; int n = grp.Count;
                long oq = 0, fq = 0, cq = 0, fv = 0; int rej = 0, rec = 0, open = 0, filled = 0;
                for (int k = 0; k < n && k < ChildIdSpace.MaxChildren; k++)
                {
                    var c = grp.Children[k];
                    oq += c.OrderQty; fq += c.FilledQty; cq += c.CancelledQty; fv += c.FillValueWon;
                    switch (c.State)
                    {
                        case ChildSlotState.Rejected: rej++; break;
                        case ChildSlotState.Reclaimed: rec++; break;
                        case ChildSlotState.Filled: filled++; break;
                        case ChildSlotState.Cancelled: break;
                        default: open++; break;
                    }
                }
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref grp.Seq) != s1 || pid != parentId) continue;
                var state = open > 0 ? ParentConvergence.Open
                          : rej > 0 ? ParentConvergence.ChildRejected
                          : filled == n ? ParentConvergence.AllFilled : ParentConvergence.PartialCancelled;
                snap = new ParentSnapshot(pid, n, oq, fq, cq, rej, rec, state, fv);
                return true;
            }
            return false;
        }

        /// <summary>
        /// [대시보드 · 방향 A] 활성 부모들의 자식 슬롯 복사(UI 스냅샷 · δ=0 · seqlock 낙관 판독). 작성 중/경합 그룹은 그 회차에서 건너뛴다
        /// (재시도 상한 8 — 판독자가 작성자를 기다리게 하지 않음). parentOf[i] = dst[i] 의 부모 ID. 반환 = 복사한 자식 수.
        /// </summary>
        public int CopyChildren(Span<ChildSlot> dst, Span<long> parentOf)
        {
            int n = 0, cap = Math.Min(dst.Length, parentOf.Length);
            for (int g = 0; g < _groups.Length && n < cap; g++)
            {
                ref var grp = ref _groups[g];
                if (Volatile.Read(ref grp.ParentId) == 0) continue;
                for (int spin = 0; spin < 8; spin++)
                {
                    long s1 = Volatile.Read(ref grp.Seq);
                    if ((s1 & 1) != 0) { Thread.SpinWait(4); continue; }
                    long pid = grp.ParentId;
                    int cnt = Math.Min(Math.Min(grp.Count, ChildIdSpace.MaxChildren), cap - n);
                    for (int k = 0; k < cnt; k++) { dst[n + k] = grp.Children[k]; parentOf[n + k] = pid; }
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref grp.Seq) != s1) continue;
                    if (pid != 0) n += cnt;
                    break;
                }
            }
            return n;
        }

        /// <summary>종결된 부모 그룹 반납(Open 이 아닌 그룹). 반환 = 반납 수.</summary>
        public int ReleaseConverged()
        {
            int n = 0;
            for (int g = 0; g < _groups.Length; g++)
            {
                long pid = Volatile.Read(ref _groups[g].ParentId);
                if (pid == 0 || !TryGetParent(pid, out var s) || s.State == ParentConvergence.Open) continue;
                ref var grp = ref _groups[g];
                BeginWrite(ref grp);
                if (grp.ParentId == pid) { grp.ParentId = 0; grp.Count = 0; n++; }
                EndWrite(ref grp);
            }
            Interlocked.Add(ref _released, n);
            return n;
        }

        // ── 내부 ──

        private int Find(long parentId)
        {
            if (parentId <= 0) return -1;
            for (int g = 0; g < _groups.Length; g++) if (Volatile.Read(ref _groups[g].ParentId) == parentId) return g;
            return -1;
        }

        private bool TryLocate(long brokerOrderId, out int group, out int index)
        {
            group = -1; index = -1;
            if (brokerOrderId == 0) return false;
            for (int g = 0; g < _groups.Length; g++)
            {
                ref var grp = ref _groups[g];
                if (Volatile.Read(ref grp.ParentId) == 0) continue;
                for (int k = 0; k < grp.Count && k < ChildIdSpace.MaxChildren; k++)
                    if (grp.Children[k].BrokerOrderId == brokerOrderId) { group = g; index = k; return true; }
            }
            return false;
        }

        private static bool TryBeginWrite(ref ParentGroup grp)
        {
            long s = Volatile.Read(ref grp.Seq);
            return (s & 1) == 0 && Interlocked.CompareExchange(ref grp.Seq, s + 1, s) == s;
        }

        private static void BeginWrite(ref ParentGroup grp)
        {
            var sw = new SpinWait();
            while (!TryBeginWrite(ref grp)) sw.SpinOnce(sleep1Threshold: -1);
        }

        private static void EndWrite(ref ParentGroup grp) => Interlocked.Increment(ref grp.Seq);

        private void Trip(ref long counter)
        {
            Interlocked.Increment(ref counter);
            Volatile.Write(ref _tripped, 1);
            Volatile.Read(ref _link)?.Resync();
        }
    }
}
