// ============================================================================
// ResilientOrderRouter + ZeroAllocOrderIdMap + BrokerHeartbeatBitmaskMonitor — [라우터 하드닝 · 추천 A]
//
//   교정 전(실측): LockFreeBrokerSwitchboard · BrokerFailoverSentinel · PendingOrderCascadeCanceller 는 각각 δ=0 로 완성돼 있었지만
//   "주문 1건"을 기준으로 셋을 묶는 조립이 없었다 — 128비트 주문 ID → (브로커, 세대 핸들) 사상도, 체결 통보가 어느 브로커·어느
//   세대에서 왔는지 대조하는 경로도 없어, 페일오버 순간 ① 옛 브로커의 늦은 체결 ② 다른 브로커가 보낸 오배송 통보 ③ 해제·재사용된
//   슬롯의 옛 통보를 구분할 수 없었다(= 체결 유실 또는 새 주문 오해제).
//
//   구성(전부 MS2E.Core · 힙 0 핫패스):
//     ▸ ZeroAllocOrderIdMap — UInt128 키 선형 탐사 오픈 어드레싱(2의 거듭제곱 고정 용량 · 백워드 시프트 삭제로 묘비 0).
//       생성 시 3개 배열 1회 할당, 이후 TryAdd/TryGet/TryRemove 힙 0. 동시성은 경합 없는 Monitor(할당 0)로 직렬화.
//     ▸ BrokerHeartbeatBitmaskMonitor(방향 A) — 브로커별 마지막 하트비트 tick 배열 + started/dead 비트마스크(≤64).
//       Beat(δ=0 Volatile 쓰기 + Interlocked.Or) · Sweep(δ=0)이 "새로 죽은" 비트만 반환 → 라우터가 센티널에 ProxyDead 로 보고.
//       ★죽은 브로커가 다시 하트비트를 보내도 자동 부활하지 않는다(fail-closed — 재무장 경로에서만 Reset).
//     ▸ ResilientOrderRouter — Route: Disarm/이중장애/블랙리스트/레짐 NaN/가격 비정상/슬리피지 한도/중복 ID 를 전부 원자적으로 거부하고,
//       통과 시 캔슬러 슬롯(세대 핸들) → ID 맵 순으로 등록(맵 실패 시 슬롯 즉시 반납 — 추적되지 않는 주문 0).
//       OnFill: ID 맵으로 발주 브로커와 세대 핸들을 찾아, 보고 브로커 불일치(오배송)는 무변경 거부, 옛 세대는 Stale, 캐스케이드 취소 후
//       도착한 실체결은 FilledAfterCancel(대사 대상)로 반환 — 어떤 경우에도 체결을 조용히 버리지 않는다.
//   ★liveArmed:false: 라우터는 "어느 브로커로 보낼지"의 판정과 추적만 한다 — 소켓/IBroker 참조 0, 실 발주는 상위 게이트(물리 이중키)가 봉인.
//     센티널은 장애 1건에도 주문 경로를 sticky Disarm 하므로, 페일오버 후 새 활성 브로커로의 실제 라우팅은 물리 이중키 재무장 이후다.
// ============================================================================
using System;
using System.Threading;

namespace MS2E.Core
{
    /// <summary>UInt128 주문 ID → (브로커, 캔슬러 세대 핸들) 무할당 사상표. 키 0 은 예약(빈 슬롯).</summary>
    public sealed class ZeroAllocOrderIdMap
    {
        private readonly UInt128[] _keys;
        private readonly long[] _handles;
        private readonly int[] _brokers;
        private readonly int _mask;
        private readonly object _gate = new();
        private int _count;

        public ZeroAllocOrderIdMap(int capacityPow2)
        {
            if (capacityPow2 < 2 || (capacityPow2 & (capacityPow2 - 1)) != 0)
                throw new ArgumentException("용량은 2의 거듭제곱(≥2)이어야 합니다.", nameof(capacityPow2));
            _keys = new UInt128[capacityPow2];
            _handles = new long[capacityPow2];
            _brokers = new int[capacityPow2];
            _mask = capacityPow2 - 1;
        }

        public int Capacity => _mask + 1;
        public int Count { get { lock (_gate) return _count; } }

        // 적재율 상한 7/8 — 선형 탐사가 무한 탐색·군집 폭주로 퇴화하지 않도록(가득 차면 TryAdd 거부 = fail-closed)
        private int MaxLoad => Capacity - (Capacity >> 3);

        private int Home(UInt128 key)
        {
            ulong lo = (ulong)key, hi = (ulong)(key >> 64);
            ulong h = (lo ^ (hi * 0x9E3779B97F4A7C15UL)) * 0xBF58476D1CE4E5B9UL;
            return (int)(h >> 32) & _mask;
        }

        [ZeroAllocHotPath]
        public bool TryAdd(UInt128 key, long handle, int broker)
        {
            if (key == UInt128.Zero) return false;
            lock (_gate)
            {
                if (_count >= MaxLoad) return false;
                int i = Home(key);
                while (true)
                {
                    UInt128 k = _keys[i];
                    if (k == UInt128.Zero) { _keys[i] = key; _handles[i] = handle; _brokers[i] = broker; _count++; return true; }
                    if (k == key) return false;   // 중복 ID — 거부(덮어쓰기 금지: 기존 추적 보존)
                    i = (i + 1) & _mask;
                }
            }
        }

        [ZeroAllocHotPath]
        public bool TryGet(UInt128 key, out long handle, out int broker)
        {
            handle = -1; broker = -1;
            if (key == UInt128.Zero) return false;
            lock (_gate)
            {
                int i = Find(key);
                if (i < 0) return false;
                handle = _handles[i]; broker = _brokers[i];
                return true;
            }
        }

        [ZeroAllocHotPath]
        public bool TryRemove(UInt128 key)
        {
            if (key == UInt128.Zero) return false;
            lock (_gate)
            {
                int i = Find(key);
                if (i < 0) return false;
                // 백워드 시프트 삭제(묘비 없음): 뒤따르는 군집 원소 중 홈이 (i, j] 밖인 것을 i 로 당긴다.
                int j = i;
                while (true)
                {
                    j = (j + 1) & _mask;
                    UInt128 kj = _keys[j];
                    if (kj == UInt128.Zero) break;
                    int h = Home(kj);
                    bool between = i <= j ? (h > i && h <= j) : (h > i || h <= j);
                    if (between) continue;
                    _keys[i] = kj; _handles[i] = _handles[j]; _brokers[i] = _brokers[j];
                    i = j;
                }
                _keys[i] = UInt128.Zero; _handles[i] = 0; _brokers[i] = 0;
                _count--;
                return true;
            }
        }

        private int Find(UInt128 key)
        {
            int i = Home(key);
            for (int n = 0; n <= _mask; n++)
            {
                UInt128 k = _keys[i];
                if (k == UInt128.Zero) return -1;
                if (k == key) return i;
                i = (i + 1) & _mask;
            }
            return -1;
        }
    }

    /// <summary>[방향 A] Zero-Alloc Broker Health Heartbeat Monitor — ≤64 브로커 하트비트를 비트마스크로 µs 단위(주입 tick) 추적.</summary>
    public sealed class BrokerHeartbeatBitmaskMonitor
    {
        private readonly long[] _lastBeat;
        private readonly long _timeoutTicks;
        private readonly int _count;
        private long _started, _dead;

        public BrokerHeartbeatBitmaskMonitor(int brokerCount, long timeoutTicks)
        {
            if (brokerCount < 1 || brokerCount > 64) throw new ArgumentOutOfRangeException(nameof(brokerCount));
            if (timeoutTicks <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutTicks));
            _count = brokerCount;
            _timeoutTicks = timeoutTicks;
            _lastBeat = new long[brokerCount];
        }

        public long StartedMask => Interlocked.Read(ref _started);
        public long DeadMask => Interlocked.Read(ref _dead);
        /// <summary>하트비트를 1회 이상 보냈고 아직 사망 판정되지 않은 브로커.</summary>
        public long HealthyMask => StartedMask & ~DeadMask;

        /// <summary>하트비트(δ=0 · 임의 스레드). 사망 판정된 브로커는 부활하지 않는다(Reset 전용).</summary>
        [ZeroAllocHotPath]
        public void Beat(int broker, long nowTicks)
        {
            if ((uint)broker >= (uint)_count) return;
            Volatile.Write(ref _lastBeat[broker], nowTicks);
            Interlocked.Or(ref _started, 1L << broker);
        }

        /// <summary>침묵 판정 스윕(δ=0). 반환 = 이번 스윕에서 "새로" 사망 판정된 비트(에지 — 같은 사망을 중복 보고하지 않음).</summary>
        [ZeroAllocHotPath]
        public long Sweep(long nowTicks)
        {
            long started = StartedMask, dead = DeadMask, fresh = 0;
            for (int b = 0; b < _count; b++)
            {
                long bit = 1L << b;
                if ((started & bit) == 0 || (dead & bit) != 0) continue;
                if (nowTicks - Volatile.Read(ref _lastBeat[b]) > _timeoutTicks) fresh |= bit;
            }
            if (fresh == 0) return 0;
            long before = Interlocked.Or(ref _dead, fresh);
            return fresh & ~before;   // 동시 스윕 경합 시에도 각 사망은 정확히 1회만 에지로 보고
        }

        /// <summary>재무장(물리 이중키) 경로 전용 — 사망 이력 초기화.</summary>
        public void Reset() { Interlocked.Exchange(ref _dead, 0); Interlocked.Exchange(ref _started, 0); }
    }

    public enum OrderRouteVerdict : byte
    {
        Routed = 0, Disarmed = 1, DualBreakdown = 2, BrokerBlacklisted = 3, InvalidRegimeSignal = 4, InvalidPrice = 5,
        SlippageExceeded = 6, InvalidOrderId = 7, DuplicateOrderId = 8, TrackingFull = 9,
        StaleReference = 10,   // [피드 연동] 참조가 미수신/만료 — 라우터 전방 거부(RouterFeedGate)
        FeedDisconnected = 11, // [피드 연동] 시세/체결 피드 단절 회로 차단
        DepthInsufficient = 12,      // [보호 지정가] 1~3호가 즉시 체결 잔량 0 · 시장가 깊이 미상/만료
        ProtectionUnavailable = 13,  // [보호 지정가] 보호 지정가 산출 불가(포화·가격제한폭 밖 참조가)
        SpreadTooWide = 14,          // [L2] 1호가 스프레드 상한 초과 · 한쪽 결측/교차 — 시장가 송출 거부
        AccountStale = 15,           // [계좌] 예수금/잔고 스냅샷 미수신·만료 — 송출 거부(fail-closed)
        InsufficientCash = 16,       // [계좌] 매수 명목 > 주문가능금액 − 예약분
        InsufficientPosition = 17,   // [계좌] 매도 수량 > 호스트 추정 보유
        ExposureLimit = 18,          // [포트폴리오] 단일 종목 익스포저 상한(총자산 대비) 초과
        SectorConcentration = 19,    // [포트폴리오] 섹터 편중 상한 초과
        ExposureUnknown = 20,        // [포트폴리오] 총자산/보유 평가 미상(예수금 미수신 · 마크가 미상) — fail-closed
    }

    public readonly record struct OrderRouteResult(OrderRouteVerdict Verdict, int Broker, long Handle)
    {
        public bool Accepted => Verdict == OrderRouteVerdict.Routed;
    }

    public enum FillNoticeKind : byte { Filled = 0, FilledAfterCancel = 1, Stale = 2, UnknownOrder = 3, MisroutedBroker = 4 }

    public sealed class ResilientOrderRouter
    {
        private readonly LockFreeBrokerSwitchboard _board;
        private readonly BrokerFailoverSentinel _sentinel;
        private readonly PendingOrderCascadeCanceller _pending;
        private readonly ZeroAllocOrderIdMap _ids;
        private readonly BrokerHeartbeatBitmaskMonitor? _heartbeat;
        private readonly long _maxSlippageBps;
        private long _rejected, _misrouted, _unknown, _aborted, _reclaimed, _terminal;

        public ResilientOrderRouter(LockFreeBrokerSwitchboard board, BrokerFailoverSentinel sentinel, PendingOrderCascadeCanceller pending,
                                    int idMapCapacityPow2, long maxSlippageBps, BrokerHeartbeatBitmaskMonitor? heartbeat = null)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _sentinel = sentinel ?? throw new ArgumentNullException(nameof(sentinel));
            _pending = pending ?? throw new ArgumentNullException(nameof(pending));
            if (maxSlippageBps < 0) throw new ArgumentOutOfRangeException(nameof(maxSlippageBps));
            _ids = new ZeroAllocOrderIdMap(idMapCapacityPow2);
            _maxSlippageBps = maxSlippageBps;
            _heartbeat = heartbeat;
        }

        public long Rejected => Interlocked.Read(ref _rejected);
        public long MisroutedNotices => Interlocked.Read(ref _misrouted);
        public long UnknownNotices => Interlocked.Read(ref _unknown);
        public int TrackedOrders => _ids.Count;
        public long AbortedRoutes => Interlocked.Read(ref _aborted);
        public long MaxSlippageBps => _maxSlippageBps;
        public long ReclaimedSlots => Interlocked.Read(ref _reclaimed);
        public long TerminalReleases => Interlocked.Read(ref _terminal);
        /// <summary>[피드 연동] 외부 전방 판정(참조가 만료·피드 단절)으로 거부된 주문도 같은 거부 누계에 계상.</summary>
        public OrderRouteResult RejectExternal(OrderRouteVerdict verdict) => Reject(verdict, -1);

        /// <summary>
        /// 주문 1건 라우팅 판정(δ=0). 모든 거부는 부작용 0(슬롯·맵 무변경). 통과 시 활성 브로커의 세대 슬롯과 ID 사상을 함께 확정한다.
        /// </summary>
        /// <param name="limitPriceRaw">지정가(정수 raw · 원 단위 등, &gt;0)</param>
        /// <param name="referencePriceRaw">기준가(최근 체결/중간호가, &gt;0) — 슬리피지 산정 기준</param>
        /// <param name="regimeSignal">레짐 신호(유한값 필수 — NaN/∞ 는 센서 이상으로 거부)</param>
        [ZeroAllocHotPath]
        public OrderRouteResult Route(UInt128 orderId, long limitPriceRaw, long referencePriceRaw, double regimeSignal)
        {
            OrderRouteVerdict v =
                orderId == UInt128.Zero ? OrderRouteVerdict.InvalidOrderId :
                _sentinel.DualBreakdown ? OrderRouteVerdict.DualBreakdown :
                _sentinel.IsDisarmed ? OrderRouteVerdict.Disarmed :
                !double.IsFinite(regimeSignal) ? OrderRouteVerdict.InvalidRegimeSignal :
                limitPriceRaw <= 0 || referencePriceRaw <= 0 ? OrderRouteVerdict.InvalidPrice :
                SlippageBps(limitPriceRaw, referencePriceRaw) > _maxSlippageBps ? OrderRouteVerdict.SlippageExceeded :
                OrderRouteVerdict.Routed;
            if (v != OrderRouteVerdict.Routed) return Reject(v, -1);

            int broker = _board.Active;
            if (_board.IsBlacklisted(broker)) return Reject(OrderRouteVerdict.BrokerBlacklisted, broker);

            long handle = _pending.Register(broker);
            if (handle < 0) return Reject(OrderRouteVerdict.TrackingFull, broker);   // 추적 불가 주문은 발주 금지
            if (!_ids.TryAdd(orderId, handle, broker))
            {
                _pending.MarkFilled(handle);   // 슬롯 즉시 반납(원자 롤백 — 추적 누수 0)
                return Reject(_ids.TryGet(orderId, out _, out _) ? OrderRouteVerdict.DuplicateOrderId : OrderRouteVerdict.TrackingFull, broker);
            }
            return new OrderRouteResult(OrderRouteVerdict.Routed, broker, handle);
        }

        private OrderRouteResult Reject(OrderRouteVerdict v, int broker)
        {
            Interlocked.Increment(ref _rejected);
            return new OrderRouteResult(v, broker, -1);
        }

        /// <summary>|limit − ref| / ref × 10⁴ (bps) — Int128 로 곱셈 오버플로 차단, 상한 포화.</summary>
        public static long SlippageBps(long limitPriceRaw, long referencePriceRaw)
        {
            Int128 diff = (Int128)limitPriceRaw - referencePriceRaw;
            if (diff < 0) diff = -diff;
            Int128 bps = diff * 10_000 / referencePriceRaw;
            return bps > long.MaxValue ? long.MaxValue : (long)bps;
        }

        /// <summary>체결 통보(δ=0). 발주 브로커와 다른 브로커의 통보는 무변경 거부(MisroutedBroker) — 오배송이 남의 주문을 해제하지 못함.</summary>
        [ZeroAllocHotPath]
        public FillNoticeKind OnFill(UInt128 orderId, int reportingBroker)
        {
            if (!_ids.TryGet(orderId, out long handle, out int broker)) { Interlocked.Increment(ref _unknown); return FillNoticeKind.UnknownOrder; }
            if (broker != reportingBroker) { Interlocked.Increment(ref _misrouted); return FillNoticeKind.MisroutedBroker; }
            var outcome = _pending.MarkFilled(handle);
            _ids.TryRemove(orderId);
            return outcome switch
            {
                FillOutcome.Filled => FillNoticeKind.Filled,
                FillOutcome.FilledAfterCancel => FillNoticeKind.FilledAfterCancel,
                _ => FillNoticeKind.Stale,
            };
        }

        /// <summary>브로커 취소 확인(δ=0). 캐스케이드로 취소 전환된 주문만 정리 — 그 외(아직 살아있는 주문)는 무변경 false.</summary>
        [ZeroAllocHotPath]
        public bool OnCancelConfirmed(UInt128 orderId, int reportingBroker)
        {
            if (!_ids.TryGet(orderId, out long handle, out int broker) || broker != reportingBroker) return false;
            if (!_pending.ConfirmCancelled(handle)) return false;
            _ids.TryRemove(orderId);
            return true;
        }

        /// <summary>[피드 연동] 송출 전 중단 — Route 통과 후 실 브로커에 도달하지 못한 주문(하류 veto·브로커 거부)의 추적을 원자 롤백(δ=0).
        /// 롤백하지 않으면 그 슬롯은 체결/취소 통보가 영원히 오지 않아 고착된다.</summary>
        [ZeroAllocHotPath]
        public bool Abort(UInt128 orderId)
        {
            if (!_ids.TryGet(orderId, out long handle, out _)) return false;
            _pending.Release(handle, out _);
            _ids.TryRemove(orderId);
            Interlocked.Increment(ref _aborted);
            return true;
        }

        /// <summary>[키움 프로토콜] 브로커 종결 통보(거부 · 비캐스케이드 취소 확인) — 체결/취소 계수 없이 추적 즉시 해제(δ=0).
        /// 거부된 주문이 TTL 까지 슬롯을 점유하던 경로를 0ms 반납으로 대체한다.</summary>
        [ZeroAllocHotPath]
        public bool ReleaseTerminal(UInt128 orderId, int reportingBroker)
        {
            if (!_ids.TryGet(orderId, out long handle, out int broker)) return false;
            if (broker != reportingBroker) { Interlocked.Increment(ref _misrouted); return false; }   // 오배송 통보는 남의 주문을 해제하지 못함
            _pending.Release(handle, out _);
            _ids.TryRemove(orderId);
            Interlocked.Increment(ref _terminal);
            return true;
        }

        /// <summary>[피드 연동 · 방향 A] 응답 타임아웃 회수 — 체결/취소 통보 유실로 수명이 임계를 넘은 슬롯을 해제(δ=0).
        /// 반환 true 시 wasCancelled = 캐스케이드 취소 전환 상태였는지(대사 분류용). 이후 늦게 도착한 통보는 UnknownOrder 로 계수된다.</summary>
        [ZeroAllocHotPath]
        public bool ReclaimExpired(UInt128 orderId, out bool wasCancelled)
        {
            wasCancelled = false;
            if (!_ids.TryGet(orderId, out long handle, out _)) return false;
            _pending.Release(handle, out wasCancelled);
            _ids.TryRemove(orderId);
            Interlocked.Increment(ref _reclaimed);
            return true;
        }

        /// <summary>[방향 A 결선] 하트비트 스윕 → 새로 침묵한 브로커를 센티널에 ProxyDead 로 보고(Disarm·블랙리스트·캐스케이드·페일오버). δ=0.</summary>
        [ZeroAllocHotPath]
        public int SweepHeartbeats(long nowTicks)
        {
            if (_heartbeat is null) return 0;
            long fresh = _heartbeat.Sweep(nowTicks);
            int n = 0;
            for (int b = 0; fresh != 0 && b < 64; b++, fresh >>= 1)
                if ((fresh & 1) != 0) { _sentinel.ReportFault(b, BrokerFaultKind.ProxyDead, 0, nowTicks); n++; }
            return n;
        }
    }
}
