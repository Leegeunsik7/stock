// ============================================================================
// MultiBrokerRecoverySoakHarness — [Long-Soak · 추천 A] 다중 브로커 단절 · 자동 복구 · 미체결 대사 가속 장기 구동(결정론 · 동기 · 실 브로커 0)
//
//  LongSoakProfileHarness(단일 경로 모의투자 24h 가속)의 동반 하니스 — 같은 원칙(주입 모의 시계 · 시드 결정론 · 고정 버퍼)으로
//  '세션 복구 사슬'만 24h 압축 재생한다. 실 구성요소: SessionRecoveryManager(키움 수동 · KIS 자동) · KisSessionInterlock(3초 침묵) ·
//  BrokerOpenOrderReconciler(키움 venue = 이 하니스 · KIS = KisOpenOrderQuery TTTC8001R 조회기) · BrokerLimitFeedProvider(세션/대사 격리 마스크) ·
//  RecoveryStateDriftMonitor(MMF 블랙박스) · OrderExecutionFeedAdapter(호스트 미체결 원장 — 키움 2건 · KIS 2건 상시 미체결).
//  ★정직: 소켓 · 파이프 · REST 는 모의다(실 키움 COM/KIS VTS 연결 0). KIS 세션 루프는 KisRealtimeSession 과 '같은 이벤트 순서'
//    (허가 대기 → 접속 → 구독 40건 → OnConnected · 단절 → MarkDisconnected → OnConnectionLost)를 동기로 재현한다 — 비동기 실소켓 경로 자체는
//    SessionAutoRecoveryTests 통합 테스트(half-open → 강제 종료 → 재구독 40건)가 별도로 실측한다.
//
//  1 모의 초: 주입 일정 → KIS 심박(침묵 중 아님) → 관리자 펌프 → 강제 종료 처리 → 재접속 허가 처리(서버 장애면 실패) → 키움 OPT10075 응답 →
//    KIS 조회기 → 게이트 → 포렌식 모니터 → ★발주 탐침.
//  ★Zero Stale Dispatch 탐침(진실 독립 판정): 브로커 b 로 자식 발주 가능 = 한도 피드 Available(b) ∧ 비트 22(대사 불일치/최초 미확정) 해제.
//    진실 '깨끗함' = (키움) 파이프 생존 ∧ 복구 후 키움 대사 확정 · (KIS) 소켓 수립 ∧ 접속 후 KIS 대사 확정 ∧ 불일치/유령 주입 없음.
//    가능한데 진실이 더럽다 = 위반(오발주 1건). 주입된 원장 오염(유령 · 원가 괴리)은 그 원장이 호스트에 '조회된 순간'부터 더러움 —
//    그 전(평시 30초 주기 · 재접속 직후는 즉시)은 호스트가 알 수 없는 탐지 지연이다(정직 보고 · 위반으로 세지 않음). 비트 19(KIS 세션 sticky)는 프로덕션에서 재무장 전 전역 차단이지만 탐침에서는 제외 —
//    '운용자가 재무장했다'는 최악 가정에서도 브로커별 게이트만으로 막히는지 본다.
//  δ=0: RunSeconds 정상 경로(주입 · 복구 · 대사 · 탐침 · 블랙박스) 관리 힙 0 — 페이지/행 버퍼는 생성 시 고정. 유령/불일치 주입(페이지 재작성)만 콜드.
// ============================================================================
using System;
using System.Text;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;

namespace StockManager.Simulation
{
    public readonly record struct RecoverySoakReport(long SimSeconds, long KisSilences, long KisAborts, long KisReconnects, long KisAttemptFailures,
        long Resubscriptions, long KiwoomPipeBreaks, long KiwoomRestores, long KisRecoveries, long KiwoomRecoveries, long ProbeChecks,
        long OrdersEmitted, long StaleDispatchViolations, long DirtyProbesBlocked, long Transitions, long Drifts, long KisCancels, long KiwoomCancels,
        bool KisExhausted, long Exhaustions);

    public sealed class MultiBrokerRecoverySoakHarness : IOpenOrderVenue
    {
        public const int KiwoomBroker = 0, KisBroker = 1;
        public const long Code = 5930, Limit = 70_000;
        private const long Sec = 1_000_000;                                        // µs

        private long _micros = 1_000_000_000_000;
        private ulong _rng;
        public long NowMicros => Volatile.Read(ref _micros);
        public long NowTicks => NowMicros * 10;

        // ── 실 구성요소 ──
        public ResilientOrderRouter Router { get; }
        public OrderExecutionFeedAdapter Ledger { get; }
        public BrokerOpenOrderReconciler Reconciler { get; }
        public ArmingInterlockBitmask Mask { get; }
        public BrokerLimitFeedProvider Limits { get; }
        public KisSessionInterlock KisSession { get; }
        public SessionRecoveryManager KisRecovery { get; }
        public SessionRecoveryManager KiwoomRecovery { get; }
        public KisOpenOrderQuery KisQuery { get; }
        public RecoveryStateDriftMonitor Monitor { get; }
        public MmfFlightRecorder Blackbox { get; }

        // ── 모의 브로커 상태 ──
        private bool _kisSocketUp, _kisSilent, _kisAbortRequested, _pipeAlive = true, _kiwoomQueryPending;
        private int _kisServerDownAttempts;
        private long _pipeRestoreAt = long.MaxValue;
        private bool _kisReconciledSinceConnect, _kiwoomReconciledSinceRestore, _kisPoisoned, _kiwoomPoisoned, _kisPagePoisoned, _kiwoomBookPoisoned;
        private long _kisRestDownUntil;
        private byte[] _kisPage;
        private readonly OpenOrderPayload[] _kiwoomBook = new OpenOrderPayload[16];
        private int _kiwoomRows;
        private long _kiwoomBatch;
        private readonly BrokerSplitCap[] _caps = new BrokerSplitCap[2];
        private long _lastAttemptEventMicros;
        private readonly long[] _gaps = new long[256];
        private int _gapCount;

        // ── 자동 주입 일정(0 = 끔) ──
        public long KisSilenceEverySeconds { get; set; } = 2_213;
        public long KisOutageEverySeconds { get; set; } = 5_101;
        public int KisOutageAttempts { get; set; } = 3;                           // 1·2·4s 실패 후 8s 성공(소진 5 미만)
        public long KiwoomBreakEverySeconds { get; set; } = 7_207;
        public long KiwoomRestoreAfterSeconds { get; set; } = 300;
        /// <summary>재접속 직후 KIS REST 미응답 초(지연 · 속도 제한 모사) — 그동안 조회 실패 → 3초 빠른 재조회 · 대사 확정 전 격리 구간이 초 단위로 관측된다.</summary>
        public long KisRestLatencySeconds { get; set; } = 2;

        private long _simSecond, _kisSilences, _kisAborts, _kisReconnects, _kisAttemptFailures, _resubs, _pipeBreaks, _pipeRestores,
                     _kisRecoveries, _kiwoomRecoveries, _probes, _orders, _violations, _dirtyBlocked, _kisCancels, _kiwoomCancels, _exhaustions;

        public MultiBrokerRecoverySoakHarness(ulong seed = 0x5EED_2026_1008UL, int bookSymbols = KisRealtimeSession.MaxBookSymbols)
        {
            _rng = seed | 1;
            BookSymbols = bookSymbols;
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            Router = new ResilientOrderRouter(board, new BrokerFailoverSentinel(board, pendingOrders: pending), pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            Ledger = new OrderExecutionFeedAdapter(Router, 64, 120_000_000, () => NowTicks);
            Blackbox = new MmfFlightRecorder(65_536, () => NowTicks);
            Limits = new BrokerLimitFeedProvider(new IBrokerAccountView?[] { new SimView(), new SimView() });
            KisQuery = null!;
            Reconciler = new BrokerOpenOrderReconciler(Ledger, this, null, requireKiwoom: true, requireKis: false, capacity: 64,
                                                       nowTicks: () => NowTicks, onPass: OnPass);
            Mask = new ArmingInterlockBitmask(ArmingInterlockSource.OpenOrderDivergence);
            Reconciler.BindInterlock(Mask, ArmingInterlockSource.OpenOrderDivergence);
            Reconciler.AttachKisCanceller((odno, org) => { Interlocked.Increment(ref _kisCancels); return true; });
            Reconciler.AttachCancelArming(() => true);                          // [v12.18.7-DEV] 리허설 = 무장 경로(취소 수신자 = 이 하니스 · 실 브로커 0)
            KisSession = new KisSessionInterlock(null, KisBroker, () => NowMicros, silenceMicros: 3 * Sec);
            KisRecovery = new SessionRecoveryManager(KisBroker, on => Limits.SetSessionIsolated(KisBroker, on),
                requestReconcile: () => Reconciler.RequestReconcile(OpenOrderVenueKind.Kis),
                reconciled: () => Reconciler.IsConfirmed(OpenOrderVenueKind.Kis),
                silent: KisSession.SilentNow, onExhausted: () => Interlocked.Increment(ref _exhaustions), nowTicks: () => NowTicks);
            KisRecovery.BindAbort(() => _kisAbortRequested = true);
            KisSession.SetRearmHold(() => KisRecovery.Held);
            KiwoomRecovery = new SessionRecoveryManager(KiwoomBroker, on => Limits.SetSessionIsolated(KiwoomBroker, on),
                requestReconcile: () => Reconciler.RequestReconcile(OpenOrderVenueKind.Kiwoom),
                reconciled: () => Reconciler.IsConfirmed(OpenOrderVenueKind.Kiwoom),
                linkProbe: () => _pipeAlive, autoReconnect: false, nowTicks: () => NowTicks);
            KisQuery = new KisOpenOrderQuery(Reconciler, FetchKis, Limits, KisRecovery, onDiscrepancy: null, broker: KisBroker, nowTicks: () => NowTicks);
            Monitor = new RecoveryStateDriftMonitor(Blackbox, Limits, () => NowTicks, KiwoomRecovery, KisRecovery);

            // 호스트 미체결 원장(상시 미체결) ↔ 브로커 원장 동일
            Track(1, 9_000_001, 0, kiwoomVenueNo: 50_001, qty: 10);
            Track(2, 9_000_002, 0, kiwoomVenueNo: 50_002, qty: 7, buy: false);
            Track(3, KisOrderIdSpace.Encode(117_001), 0, 0, qty: 6);
            Track(4, KisOrderIdSpace.Encode(117_002), 0, 0, qty: 4, buy: false);
            _kiwoomBook[0] = Row(50_001, 10, 1); _kiwoomBook[1] = Row(50_002, 7, -1); _kiwoomRows = 2;
            _kisPage = BuildKisPage(false, false);
        }

        public int BookSymbols { get; }
        public int SubscriptionsPerConnect => 2 + BookSymbols;                    // 체결통보 + 심박 체결가 + 호가 종목(KisRealtimeSession 과 같은 수)
        public ReadOnlySpan<long> AttemptGapsSeconds => _gaps.AsSpan(0, _gapCount);
        public long SimSecond => _simSecond;
        public bool KisSocketUp => _kisSocketUp;
        public bool PipeAlive => _pipeAlive;

        private sealed class SimView : IBrokerAccountView
        {
            public bool Bound => true;
            public bool SharesGateReservation => false;
            public bool TryGetBuyHeadroomWon(out long won) { won = 1_000_000_000; return true; }
            public bool TryGetSellableShares(long symbolCode, out long shares) { shares = 1_000; return true; }
        }

        private void Track(long proposal, long brokerId, int _, long kiwoomVenueNo, long qty, bool buy = true)
        {
            var key = RouterFeedGate.KeyFor(proposal);
            if (!Router.Route(key, Limit * 100_000_000, Limit * 100_000_000, 0.0).Accepted) throw new InvalidOperationException("라우팅 거부");
            if (!Ledger.TryTrack(proposal, key, 0, qty, buy, Limit * 100_000_000, Code, Limit)) throw new InvalidOperationException("추적 실패");
            if (!Ledger.BindBrokerOrder(proposal, brokerId)) throw new InvalidOperationException("번호 결속 실패");
            if (kiwoomVenueNo > 0) Ledger.OnOrderMapped(new OrderMappedPayload { ClientId = brokerId, VenueOrderNo = kiwoomVenueNo });
        }

        private static OpenOrderPayload Row(long venueNo, long rem, int side, long price = Limit)
            => new() { VenueOrderNo = venueNo, SymbolCode = Code, OrderQty = rem, RemainingQty = rem, OrderPriceWon = price, Side = side };

        private static string KisRow(long odno, long qty, string side, long price = Limit, long org = 91_252)
            => "{\"odno\":\"" + odno.ToString("D10") + "\",\"orgn_odno\":\"\",\"pdno\":\"005930\",\"ord_qty\":\"" + qty + "\",\"rmn_qty\":\"" + qty
               + "\",\"ord_unpr\":\"" + price + "\",\"sll_buy_dvsn_cd\":\"" + side + "\",\"cncl_yn\":\"N\",\"ord_gno_brno\":\"" + org.ToString("D5") + "\"}";

        private static byte[] BuildKisPage(bool ghost, bool priceDivergence)
        {
            var sb = new StringBuilder("{\"ctx_area_fk100\":\"\",\"ctx_area_nk100\":\"\",\"output1\":[");
            sb.Append(KisRow(117_001, 6, "02", priceDivergence ? Limit + 100 : Limit)).Append(',').Append(KisRow(117_002, 4, "01"));
            if (ghost) sb.Append(',').Append(KisRow(118_777, 3, "02", org: 91_300));
            sb.Append("],\"rt_cd\":\"0\"}");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        // ── 모의 전송 ──
        private byte[]? FetchKis(string fk, string nk, bool cont, out bool more)
        {
            more = false;
            if (_simSecond < _kisRestDownUntil) return null;                        // REST 지연(실패 → 조회기 빠른 재조회)
            if (_kisPagePoisoned) _kisPoisoned = true;                               // ★진실 오염 = 증거가 호스트에 '도달한' 순간부터(탐지 지연은 정직 보고)
            return _kisPage;
        }

        int IOpenOrderVenue.VenueKind => OpenOrderVenueKind.Kiwoom;
        bool IOpenOrderVenue.RequestOpenOrders() { if (!_pipeAlive) return false; _kiwoomQueryPending = true; return true; }
        bool IOpenOrderVenue.AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining, long priceWon) => true;
        bool IOpenOrderVenue.CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty) { Interlocked.Increment(ref _kiwoomCancels); return true; }

        private void OnPass(OpenOrderPassResult r)
        {
            KisQuery?.OnPass(r);
            if (r.Verdict != OpenOrderVerdict.Confirmed) return;
            if (r.Venue == OpenOrderVenueKind.Kis && _kisSocketUp) _kisReconciledSinceConnect = true;
            if (r.Venue == OpenOrderVenueKind.Kiwoom && _pipeAlive) _kiwoomReconciledSinceRestore = true;
        }

        // ── 주입 ──
        public void InjectKisSilence() { if (_kisSocketUp && !_kisSilent) { _kisSilent = true; _kisSilences++; } }
        public void SetKisServerDown(int attempts) => _kisServerDownAttempts = attempts;
        public void BreakKiwoomPipe(long restoreAfterSeconds)
        {
            if (!_pipeAlive) return;
            _pipeAlive = false; _kiwoomReconciledSinceRestore = false; _pipeBreaks++;
            _pipeRestoreAt = restoreAfterSeconds < 0 ? long.MaxValue : _simSecond + restoreAfterSeconds;
        }
        /// <summary>KIS 원장에 호스트 미인가 유령 주문 주입(콜드 — 페이지 재작성).</summary>
        public void InjectKisGhost() { _kisPage = BuildKisPage(true, false); _kisPagePoisoned = true; }
        /// <summary>KIS 원장 지정가 괴리(원가 불일치) 주입(콜드).</summary>
        public void InjectKisPriceDivergence() { _kisPage = BuildKisPage(false, true); _kisPagePoisoned = true; }
        /// <summary>키움 원장 유령 주문 주입.</summary>
        public void InjectKiwoomGhost() { _kiwoomBook[_kiwoomRows++] = Row(59_999, 2, 1); _kiwoomBookPoisoned = true; }

        // ── 구동 ──

        /// <summary>최초 접속 · 최초 대사(양 브로커) 수렴까지 구동.</summary>
        public bool Warmup(int maxSeconds = 120)
        {
            for (int i = 0; i < maxSeconds; i++)
            {
                StepCore(autoInject: false);
                if (CleanTruth(KisBroker) && CleanTruth(KiwoomBroker) && !KisRecovery.Held && !KiwoomRecovery.Held && !Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence)) return true;
            }
            return false;
        }

        public void RunSeconds(long seconds, bool autoInject = true)
        {
            for (long i = 0; i < seconds; i++) StepCore(autoInject);
        }

        private void StepCore(bool autoInject)
        {
            _micros += Sec;
            _simSecond++;
            long s = _simSecond;
            if (autoInject)
            {
                if (KisSilenceEverySeconds > 0 && s % KisSilenceEverySeconds == 0) InjectKisSilence();
                if (KisOutageEverySeconds > 0 && s % KisOutageEverySeconds == 0 && _kisSocketUp) { _kisServerDownAttempts = KisOutageAttempts; InjectKisSilence(); }
                if (KiwoomBreakEverySeconds > 0 && s % KiwoomBreakEverySeconds == 0) BreakKiwoomPipe(KiwoomRestoreAfterSeconds + (long)(Next() % 120));
            }
            if (!_pipeAlive && s >= _pipeRestoreAt) { _pipeAlive = true; _pipeRestoreAt = long.MaxValue; _pipeRestores++; }   // 운용자 프록시 재기동
            if (_kisSocketUp && !_kisSilent) KisSession.OnActivity(false);            // 심박(체결가 구독)

            KisSession.Evaluate();
            KisRecovery.Pump();
            KiwoomRecovery.Pump();

            if (_kisAbortRequested)
            {
                _kisAbortRequested = false;
                if (_kisSocketUp)
                {
                    _kisSocketUp = false; _kisSilent = false; _kisReconciledSinceConnect = false;
                    _kisAborts++;
                    KisSession.MarkDisconnected();
                    KisRecovery.OnConnectionLost();
                    _lastAttemptEventMicros = _micros;
                }
            }
            if (!_kisSocketUp && KisRecovery.State == SessionRecoveryState.Connecting)
            {
                var permit = KisRecovery.WaitForAttemptAsync();
                if (permit.IsCompletedSuccessfully && permit.Result)
                {
                    if (_lastAttemptEventMicros != 0 && _gapCount < _gaps.Length) _gaps[_gapCount++] = (_micros - _lastAttemptEventMicros) / Sec;
                    _lastAttemptEventMicros = _micros;
                    if (_kisServerDownAttempts > 0)
                    {
                        _kisServerDownAttempts--;
                        _kisAttemptFailures++;
                        KisRecovery.OnAttemptFailed();
                    }
                    else
                    {
                        bool reconnect = _kisReconnects > 0 || _kisAborts > 0;
                        _kisSocketUp = true;
                        _resubs += SubscriptionsPerConnect;                               // 체결통보 · 심박 · 호가 38 재구독
                        KisSession.MarkConnected();
                        KisRecovery.OnConnected();
                        if (reconnect) { _kisReconnects++; _kisRestDownUntil = s + KisRestLatencySeconds; }
                        _lastAttemptEventMicros = 0;
                    }
                }
            }

            Reconciler.Pump();
            if (_kiwoomQueryPending)
            {
                _kiwoomQueryPending = false;
                _kiwoomBatch++;
                if (_kiwoomBookPoisoned) _kiwoomPoisoned = true;                     // 증거 도달 순간부터 진실 오염
                if (_kiwoomRows == 0) Reconciler.OnKiwoomRow(new OpenOrderPayload { BatchId = _kiwoomBatch, RowIndex = 0, RowCount = 0 });
                for (int r = 0; r < _kiwoomRows; r++)
                {
                    var p = _kiwoomBook[r];
                    p.BatchId = _kiwoomBatch; p.RowIndex = r; p.RowCount = _kiwoomRows;
                    Reconciler.OnKiwoomRow(p);
                }
            }
            KisQuery.Pump();
            KisQuery.RefreshGate();
            Monitor.Pump();
            _kisRecoveries = KisRecovery.Recoveries; _kiwoomRecoveries = KiwoomRecovery.Recoveries;

            // ★발주 탐침(진실 독립 판정)
            _probes++;
            Limits.Fill(_caps, Code, OrderSide.Buy, Limit * 100_000_000, 0);
            bool bit22 = Mask.IsRaised(ArmingInterlockSource.OpenOrderDivergence);
            for (int b = 0; b < 2; b++)
            {
                bool possible = _caps[b].Available && !bit22;
                bool clean = CleanTruth(b);
                if (possible && !clean) _violations++;
                else if (!possible && !clean) _dirtyBlocked++;
                else if (possible && s % 60 == 0) _orders++;
            }
        }

        private bool CleanTruth(int broker)
            => broker == KisBroker ? _kisSocketUp && _kisReconciledSinceConnect && !_kisPoisoned
                                   : _pipeAlive && _kiwoomReconciledSinceRestore && !_kiwoomPoisoned;

        private ulong Next() { _rng ^= _rng << 13; _rng ^= _rng >> 7; _rng ^= _rng << 17; return _rng; }

        public RecoverySoakReport Report() => new(_simSecond, _kisSilences, _kisAborts, _kisReconnects, _kisAttemptFailures, _resubs, _pipeBreaks, _pipeRestores,
            _kisRecoveries, _kiwoomRecoveries, _probes, _orders, _violations, _dirtyBlocked, Monitor.Transitions(0) + Monitor.Transitions(1), Monitor.Drifts,
            Interlocked.Read(ref _kisCancels), Interlocked.Read(ref _kiwoomCancels), KisRecovery.Exhausted, Interlocked.Read(ref _exhaustions));
    }
}
