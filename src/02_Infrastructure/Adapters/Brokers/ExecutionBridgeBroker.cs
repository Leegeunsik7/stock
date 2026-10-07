// ============================================================================
// ExecutionBridgeBroker — S14 #2 실거래 체결 브로커
//
//  IBroker 직접 구현 (ShadowBroker가 sealed이므로 상속 불가).
//  IsLive = true → 실계좌 연동. Tick()은 파이프로 수신되므로 IBroker.Tick 없음.
//
//  설계:
//   · KiwoomBridgeClient.FillReceived → LiveAuditService.ProcessFill → ForceFreeze
//   · KiwoomBridgeClient.ProxyLost   → attestation.ForceFreeze (500ms 하트비트)
//   · Place()  : AssertNormal → RegisterOrder → fire-and-forget SendOrderAsync
//   · Cancel() : CancelOrder(long) + CancelOrder 파이프 전송
//   · HeldQty  : 체결 이벤트로 누적한 로컬 보유수량
//
//  Create() 팩토리 사용 권장 — heartbeatTimeoutMs=500 자동 적용.
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace MS2E.Infrastructure
{
    public sealed class ExecutionBridgeBroker : IBroker, IDisposable, IOpenOrderVenue
    {
        private readonly KiwoomBridgeClient      _bridge;
        private readonly LiveAuditService        _audit;
        private readonly OrderAttestationService _attestation;

        private readonly ConcurrentDictionary<string, decimal> _holdings
            = new(StringComparer.OrdinalIgnoreCase);

        private long _nextOrderId;

        // ── IBroker ───────────────────────────────────────────────────────────

        /// <summary>보류 주문 원장 접근 (모니터링·테스트 용도).</summary>
        public LiveAuditService Audit => _audit;

        public string Name   => "ExecutionBridge(Live)";
        public bool   IsLive => true;

        // ── 생성자 ─────────────────────────────────────────────────────────────

        /// <param name="bridge">KiwoomBridgeClient — attestation 없이 생성해야 이중 ForceFreeze 방지.</param>
        public ExecutionBridgeBroker(
            KiwoomBridgeClient      bridge,
            LiveAuditService        audit,
            OrderAttestationService attestation)
        {
            _bridge      = bridge      ?? throw new ArgumentNullException(nameof(bridge));
            _audit       = audit       ?? throw new ArgumentNullException(nameof(audit));
            _attestation = attestation ?? throw new ArgumentNullException(nameof(attestation));

            _bridge.FillReceived += OnFillReceived;
            _bridge.ProxyLost    += OnProxyLost;
        }

        // ── 팩토리 — 권장 생성 방법 ─────────────────────────────────────────────

        /// <summary>IPC 헬스체크 간격 상수(과제 §2.2) — 50ms 초고속: 20ms Ping / 50ms 타임아웃.</summary>
        public const int FastPingIntervalMs     = 20;
        public const int FastHeartbeatTimeoutMs = 50;

        /// <summary>LiveAuditService를 내부 생성해 묶음 반환. heartbeatTimeoutMs 기본 500ms(후방호환).
        /// pingIntervalMs>0 이면 클라이언트가 능동 Ping 을 보내고 Pong 수신으로 양방향 생존성을 갱신한다.</summary>
        public static (ExecutionBridgeBroker Broker, KiwoomBridgeClient Bridge) Create(
            OrderAttestationService attestation,
            string pipeName = BridgeSerializer.DefaultPipeName,
            int heartbeatTimeoutMs = 500,
            int pingIntervalMs = 0,
            Func<bool>? orderArmed = null,
            Func<IgnitionStage>? ignitionStage = null)
        {
            // ★v4.97.0 — 영지식 송출 펜스 배선(F1 갭 보강): orderArmed/ignitionStage 를 클라이언트에 주입해야
            //   SendOrderAsync 최하위의 ZeroLeakProtocolIsolator.AssertSendAllowed 가 활성화된다(미주입 시 레거시 바이패스였음).
            var bridge = new KiwoomBridgeClient(
                attestation:        null,    // ProxyLost 이벤트로 처리하므로 이중 호출 방지
                pipeName:           pipeName,
                heartbeatTimeoutMs: heartbeatTimeoutMs,
                nowTicks:           null,
                pingIntervalMs:     pingIntervalMs,
                orderArmed:         orderArmed,
                ignitionStage:      ignitionStage);

            var audit  = new LiveAuditService(attestation);
            var broker = new ExecutionBridgeBroker(bridge, audit, attestation);
            return (broker, bridge);
        }

        /// <summary>과제 §2.2 — 50ms 초고속 IPC 헬스체크 프로덕션 팩토리(20ms Ping·50ms 타임아웃 핑퐁).
        /// 프록시가 50ms 이상 Pong 을 못 주면 ProxyLost → KiwoomFreezeRelay/OnProxyLost → EmergencyFreeze 연쇄.</summary>
        public static (ExecutionBridgeBroker Broker, KiwoomBridgeClient Bridge) CreateFast(
            OrderAttestationService attestation,
            string pipeName = BridgeSerializer.DefaultPipeName,
            Func<bool>? orderArmed = null,
            Func<IgnitionStage>? ignitionStage = null)
            => Create(attestation, pipeName, FastHeartbeatTimeoutMs, FastPingIntervalMs, orderArmed, ignitionStage);

        // ── IBroker.Place ─────────────────────────────────────────────────────

        public (bool ok, string msg) Place(
            PaperAsset asset,  string symbol, string name,
            PaperSide  side,   PaperKind kind,
            decimal    qty,    decimal   limitPrice, decimal currentPrice)
        {
            if (asset != PaperAsset.Stock)
                return (false, "ExecutionBridgeBroker는 주식 전용입니다.");

            _attestation.AssertNormal($"ExecutionBridgeBroker.Place({symbol})");

            if (qty <= 0)
                return (false, "수량은 양수여야 합니다.");

            long symbolCode = BridgeSerializer.ParseSymbol(symbol);
            if (symbolCode <= 0 || symbolCode > 999_999)
                return (false, $"유효하지 않은 KSE 종목코드: {symbol}");

            long orderId = Interlocked.Increment(ref _nextOrderId);
            double price = kind == PaperKind.Market ? 0.0 : (double)limitPrice;

            _audit.RegisterOrder(orderId, symbol, side, kind, price, (long)qty);

            var req = new OrderRequest
            {
                Type       = side == PaperSide.Buy ? OrderRequestType.Buy : OrderRequestType.Sell,
                OrderId    = orderId,
                SymbolCode = symbolCode,
                Price      = price,
                Quantity   = (long)qty,
                GenomeId   = 0L,
            };

            _ = _bridge.SendOrderAsync(req).ContinueWith(t =>
            {
                if (t.IsFaulted)
                    _attestation.ForceFreeze(
                        $"주문 전송 실패({symbol}, orderID={orderId}): " +
                        (t.Exception?.InnerException?.Message ?? "알 수 없는 오류"));
            });

            return (true, orderId.ToString("D"));
        }

        // ── IBroker.Cancel ────────────────────────────────────────────────────

        /// <summary>[정정/취소] 전방 판정 게이트(옵션 · 호스트 추적 원장). 미주입이면 기존 동작(무조건 송신).</summary>
        public IOrderAmendGate? AmendGate { get; set; }
        private long _amendBlocked;
        /// <summary>[정정/취소] 게이트가 송신 전 차단한 취소/정정 누계(이미 종결 · 중복 · 잔량 초과).</summary>
        public long AmendBlocked => Interlocked.Read(ref _amendBlocked);

        /// <summary>[롤오버] 클라이언트 주문 ID 하한 시드 — 승계 슬롯의 최대 ID 이하로는 새 주문 ID 를 발급하지 않는다(충돌 = 오대사).</summary>
        public void SeedOrderIdFloor(long floor)
        {
            long cur;
            while ((cur = Interlocked.Read(ref _nextOrderId)) < floor)
                if (Interlocked.CompareExchange(ref _nextOrderId, floor, cur) == cur) return;
        }

        public long LastIssuedOrderId => Interlocked.Read(ref _nextOrderId);

        public void Cancel(string orderId)
        {
            if (!long.TryParse(orderId, out long id)) return;
            if (AmendGate is { } g && !g.TryBeginCancel(id)) { Interlocked.Increment(ref _amendBlocked); return; }   // 종결/중복 취소 0ms 차단

            _audit.CancelOrder(id);

            var req = new OrderRequest
            {
                Type    = OrderRequestType.CancelOrder,
                OrderId = id,
            };
            _ = _bridge.SendOrderAsync(req);
        }

        /// <summary>[정정] 지정가 정정 — 원주문의 잔량 이하 수량만(게이트 + 프록시 코어 이중 검증). 반환 = 송신 여부.</summary>
        public bool Modify(string orderId, decimal newPrice, decimal newQty)
        {
            if (!long.TryParse(orderId, out long id) || newPrice <= 0 || newQty <= 0 || newQty != decimal.Truncate(newQty)) return false;
            if (AmendGate is { } g && !g.TryBeginModify(id, (long)newQty, (long)newPrice)) { Interlocked.Increment(ref _amendBlocked); return false; }
            _attestation.AssertNormal($"ExecutionBridgeBroker.Modify({orderId})");
            var req = new OrderRequest { Type = OrderRequestType.ModifyOrder, OrderId = id, Price = (double)newPrice, Quantity = (long)newQty };
            _ = _bridge.SendOrderAsync(req);
            return true;
        }

        /// <summary>[계좌] OPW00018·OPW00001 조회 요청(조회 TR 레이트 — 호출 주기 ≥ 10초 유지).</summary>
        public void QueryAccount()
        {
            var req = new OrderRequest { Type = OrderRequestType.QueryAccount };
            _ = _bridge.SendOrderAsync(req);
        }

        // ── [미체결 대사] IOpenOrderVenue(키움) ────────────────────────────────

        public int VenueKind => OpenOrderVenueKind.Kiwoom;

        /// <summary>OPT10075 미체결 조회(조회 TR 1건 — 호출측 주기 ≥ 수 초). 프록시 미연결 = false.</summary>
        public bool RequestOpenOrders()
        {
            if (!_bridge.IsProxyAlive) return false;
            _ = _bridge.SendOrderAsync(new OrderRequest { Type = OrderRequestType.QueryOpenOrders });
            return true;
        }

        /// <summary>승계 확정 주문을 프록시 사상표에 재등록(키움 송신 없음).</summary>
        public bool AdoptMapping(long clientId, long venueOrderNo, long symbolCode, int side, long remaining, long priceWon)
        {
            if (!_bridge.IsProxyAlive || clientId <= 0 || venueOrderNo <= 0 || remaining <= 0) return false;
            _ = _bridge.SendOrderAsync(new OrderRequest
            {
                Type = OrderRequestType.AdoptMapping, OrderId = clientId, SymbolCode = symbolCode, Quantity = remaining,
                Price = priceWon, GenomeId = venueOrderNo, Pad = side > 0 ? 1 : -1,
            });
            return true;
        }

        /// <summary>[방향 B] 키움 번호로 직접 전량 취소(유령 주문 비상 취소 — 취소는 노출 축소라 무장 상태와 무관하게 집행).</summary>
        public bool CancelByVenueNo(long venueOrderNo, long symbolCode, int side, long qty)
        {
            if (!_bridge.IsProxyAlive || venueOrderNo <= 0 || symbolCode <= 0 || qty <= 0) return false;
            _ = _bridge.SendOrderAsync(new OrderRequest
            {
                Type = OrderRequestType.CancelByVenueNo, SymbolCode = symbolCode, Quantity = qty, GenomeId = venueOrderNo, Pad = side > 0 ? 1 : -1,
            });
            return true;
        }

        // ── IBroker.HeldQty ───────────────────────────────────────────────────

        public decimal HeldQty(PaperAsset asset, string symbol)
            => _holdings.TryGetValue(symbol, out var qty) ? qty : 0m;

        // ── 이벤트 핸들러 ──────────────────────────────────────────────────────

        private void OnFillReceived(FillPayload fill)
        {
            var result = _audit.ProcessFill(fill);
            if (!result.Passed) return;

            // 보유수량 갱신
            string symbol  = BridgeSerializer.FormatSymbol(fill.SymbolCode);
            decimal delta  = fill.Side == 1 ? (decimal)fill.FillQty : -(decimal)fill.FillQty;
            _holdings.AddOrUpdate(symbol, Math.Max(0m, delta),
                (_, existing) => Math.Max(0m, existing + delta));
        }

        private void OnProxyLost()
            => _attestation.ForceFreeze(
                "KiwoomProxyHost 연결 끊김 — 하트비트 500ms 초과, 고아 주문 방지");

        // ── IDisposable ───────────────────────────────────────────────────────

        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bridge.FillReceived -= OnFillReceived;
            _bridge.ProxyLost    -= OnProxyLost;
        }
    }
}
