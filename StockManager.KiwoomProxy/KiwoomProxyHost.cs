// ============================================================================
// KiwoomProxyHost — S14 #1 x86 NamedPipe 서버 · COM 이벤트 터널링 에이전트
//
//  실행 흐름:
//   1. NamedPipeServerStream 생성 → 64비트 터미널의 연결 대기.
//   2. IKiwoomApi.Connect() 호출 → COM 이벤트 핸들러 배선.
//   3. HeartbeatLoopAsync: 1초마다 하트비트 전송.
//   4. ReceiveOrderLoopAsync: OrderRequest 수신 → IKiwoomApi 명령 실행.
//   5. IKiwoomApi 이벤트 → BridgeMessage 직렬화 → 파이프 전송.
// ============================================================================
using System;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.KiwoomProxy
{
    public sealed class KiwoomProxyHost : IDisposable
    {
        private const int HeartbeatIntervalMs = 1_000;

        private readonly IKiwoomApi          _api;
        private readonly string              _pipeName;
        private NamedPipeServerStream?       _pipe;
        private long                         _sequence;
        private bool                         _disposed;

        // ★2단계 GC 방어(스펙 §2.1): COM 이벤트 핸들러 hot path 는 프레임마다 byte[] 를 할당하지 않는다.
        //   재사용 송신 버퍼 + 송신 직렬화 락으로 (a) per-event 할당 제거, (b) STA 이벤트 스레드와 하트비트
        //   태스크가 동일 파이프에 '동시 write' 하여 프레임이 섞이던 잠재 손상 레이스를 함께 차단한다.
        private readonly byte[]              _sendBuf = new byte[BridgeSerializer.MaxFrameSize];
        private readonly object              _sendLock = new();

        public KiwoomProxyHost(
            IKiwoomApi api,
            string     pipeName = BridgeSerializer.DefaultPipeName)
        {
            _api      = api ?? throw new ArgumentNullException(nameof(api));
            _pipeName = pipeName;
        }

        // ── 진입점: 연결 수락 후 루프 실행 ──────────────────────────────────

        public async Task RunAsync(CancellationToken ct = default)
        {
            _pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            await _pipe.WaitForConnectionAsync(ct);

            _api.TickReceived    += OnTick;
            _api.FillReceived    += OnFill;
            _api.AccountReceived += OnAccount;
            _api.OrderBookReceived      += OnOrderBook;        // [L2]
            _api.ReferencePriceReceived += OnReferencePrice;   // [L2]
            _api.ServerErrorReceived    += OnServerError;      // [키움 COM] 중대 서버 오류 → ErrorReport
            _api.AccountCashReceived    += OnAccountCash;      // [계좌] OPW00001 → AccountCash 프레임
            _api.PositionReceived       += OnPosition;         // [계좌] OPW00018 행 → Position 프레임

            _api.Connect();

            try
            {
                await Task.WhenAll(
                    HeartbeatLoopAsync(ct),
                    ReceiveOrderLoopAsync(ct));
            }
            finally
            {
                _api.TickReceived    -= OnTick;
                _api.FillReceived    -= OnFill;
                _api.AccountReceived -= OnAccount;
                _api.OrderBookReceived      -= OnOrderBook;
                _api.ReferencePriceReceived -= OnReferencePrice;
                _api.ServerErrorReceived    -= OnServerError;
                _api.AccountCashReceived    -= OnAccountCash;
                _api.PositionReceived       -= OnPosition;
            }
        }

        // ── COM 이벤트 → BridgeMessage (STA 스레드에서 호출) ─────────────────

        private void OnTick(object? sender, TickEventArgs e)
        {
            // 값타입 페이로드(스택·무힙) → 재사용 버퍼에 pack → 직렬화 송신. 프레임당 힙 할당 0.
            var p = new TickPayload
            {
                SymbolCode     = e.SymbolCode,
                Price          = e.Price,
                Volume         = e.Volume,
                ChangeRate     = e.ChangeRate,
                TimestampTicks = DateTime.UtcNow.Ticks,
            };
            SendFrame(BridgeMessageType.RealtimeTick, in p);
        }

        private void OnFill(object? sender, FillEventArgs e)
        {
            var p = new FillPayload
            {
                OrderId        = e.OrderId,
                SymbolCode     = e.SymbolCode,
                FillPrice      = e.FillPrice,
                FillQty        = e.FillQty,
                Side           = e.Side,
                TimestampTicks = DateTime.UtcNow.Ticks,
                // [키움 프로토콜] 상태 명시 + 미체결 잔량 동봉(매직 0xA5 — 구 호스트는 Pad 로 무시 · 신 호스트는 즉시 구분)
                Status         = BridgeFillStatusCodec.Encode(e.Status, e.RemainingQty >= 0),
                RemainingQty   = e.RemainingQty >= 0 ? e.RemainingQty : 0,
            };
            SendFrame(e.FillQty > 0 ? BridgeMessageType.OrderFilled : BridgeMessageType.OrderRejected, in p);
        }

        // [L2] 10호가 스냅샷 → OrderBook 프레임(336B · 타임스탬프 프록시 수신 시각)
        private void OnOrderBook(object? sender, OrderBookEventArgs e)
        {
            var p = e.Book;
            p.TimestampTicks = DateTime.UtcNow.Ticks;
            SendFrame(BridgeMessageType.OrderBook, in p);
        }

        // [L2] 기준가 → ReferencePrice 프레임(호스트가 MarketDataSpikeInterlock.SetReference 로 가격제한폭 활성)
        private void OnReferencePrice(object? sender, ReferencePriceEventArgs e)
        {
            if (e.SymbolCode <= 0 || e.BasePrice <= 0) return;
            var p = new ReferencePricePayload { SymbolCode = e.SymbolCode, BasePrice = e.BasePrice, TimestampTicks = DateTime.UtcNow.Ticks };
            SendFrame(BridgeMessageType.ReferencePrice, in p);
        }

        // [키움 COM · 방향 B] 중대 서버 오류(비밀번호·계좌 정지·증거금) → ErrorReport — 호스트 KiwoomFreezeRelay 동결 + 인터락 비트
        private void OnServerError(object? sender, ServerErrorEventArgs e)
        {
            if (e.Code == 0) return;
            var p = new ErrorPayload { ErrorCode = e.Code, TimestampTicks = DateTime.UtcNow.Ticks };
            SendFrame(BridgeMessageType.ErrorReport, in p);
        }

        private void OnAccountCash(object? sender, AccountCashEventArgs e)
        {
            var p = e.Cash;
            SendFrame(BridgeMessageType.AccountCash, in p);
        }

        private void OnPosition(object? sender, PositionEventArgs e)
        {
            var p = e.Row;
            SendFrame(BridgeMessageType.Position, in p);
        }

        private void OnAccount(object? sender, AccountEventArgs e)
        {
            var p = new AccountPayload
            {
                Deposit       = e.Deposit,
                Equity        = e.Equity,
                UnfilledCount = e.UnfilledCount,
            };
            SendFrame(BridgeMessageType.AccountState, in p);
        }

        // [키움 COM · 방향 A] STA 펌프 생존 확인 — 동결이면 최초 1회 ErrorReport(StaPumpStall) 송신 후 false(생존 신호 전면 중단)
        private int _stallReported;
        private bool PumpAliveOrReport()
        {
            bool healthy;
            try { healthy = _api.PumpHealthy; } catch { healthy = false; }
            if (healthy) return true;
            if (Interlocked.CompareExchange(ref _stallReported, 1, 0) == 0)
            {
                var p = new ErrorPayload { ErrorCode = KiwoomServerErrorCodes.StaPumpStall, TimestampTicks = DateTime.UtcNow.Ticks };
                try { SendFrame(BridgeMessageType.ErrorReport, in p); } catch { }
            }
            return false;
        }

        // ── 하트비트 루프 ─────────────────────────────────────────────────────
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatIntervalMs, ct);
                if (!PumpAliveOrReport()) continue;   // [키움 COM · 방향 A] STA 동결 — 생존 신호 중단(호스트 하트비트 단절 → ProxyLost 동결)
                SendHeartbeat(Interlocked.Increment(ref _sequence));
            }
        }

        // ── 주문 수신 루프 ────────────────────────────────────────────────────
        private async Task ReceiveOrderLoopAsync(CancellationToken ct)
        {
            var header = new byte[BridgeSerializer.HeaderSize];
            while (!ct.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, ct)) break;
                if (!BridgeSerializer.TryParseHeader(header, out var type, out int plen)) break;

                var payload = plen > 0 ? new byte[plen] : Array.Empty<byte>();
                if (plen > 0 && !await ReadExactAsync(payload, ct)) break;

                // ★50ms 초고속 헬스체크: Ping 즉시 Pong 에코(양방향 생존성·클라 RTT 측정). _sendLock 직렬화.
                if (type == BridgeMessageType.Ping)
                {
                    long seq = payload.Length >= sizeof(long) ? BitConverter.ToInt64(payload) : 0L;
                    if (!PumpAliveOrReport()) continue;   // [키움 COM · 방향 A] STA 동결 — Pong 억제(파이프 스레드가 살아 있어도 COM 은 죽었다)
                    SendControl(BridgeMessageType.Pong, seq);
                    continue;
                }

                if (type != BridgeMessageType.OrderRequest) continue;  // 방향 오류 무시

                var req = BridgeSerializer.Unpack<OrderRequest>(payload);
                ExecuteOrder(req);
            }
        }

        private void ExecuteOrder(OrderRequest req)
        {
            switch (req.Type)
            {
                case OrderRequestType.Buy:
                    _api.SendOrder(req.SymbolCode, +1, req.Price, req.Quantity, req.OrderId);
                    break;
                case OrderRequestType.Sell:
                    _api.SendOrder(req.SymbolCode, -1, req.Price, req.Quantity, req.OrderId);
                    break;
                case OrderRequestType.CancelOrder:
                    _api.CancelOrder(req.OrderId);
                    break;
                case OrderRequestType.ModifyOrder:   // [정정] Price = 정정가 · Quantity = 정정수량
                    _api.ModifyOrder(req.OrderId, req.Price, req.Quantity);
                    break;
                case OrderRequestType.SubscribeTick:
                    _api.SubscribeTick(req.SymbolCode);
                    break;
                case OrderRequestType.UnsubscribeTick:
                    _api.UnsubscribeTick(req.SymbolCode);
                    break;
                case OrderRequestType.QueryAccount:
                    _api.QueryAccount();
                    break;
            }
        }

        // ── 내부 헬퍼(송신 직렬화·무할당) ──────────────────────────────────────
        //   모든 송신은 _sendLock 하에 재사용 버퍼로 pack → 동기 write. STA 이벤트 스레드·하트비트
        //   태스크의 동시 진입을 직렬화해 프레임 인터리빙(손상)을 원천 차단(락 보유 중 await 없음).
        private void SendFrame<T>(BridgeMessageType type, in T payload) where T : unmanaged
        {
            lock (_sendLock)
            {
                int n = BridgeSerializer.Pack(_sendBuf, type, in payload);
                WriteLocked(n);
            }
        }

        private void SendHeartbeat(long sequence)
        {
            lock (_sendLock)
            {
                int n = BridgeSerializer.PackHeartbeat(_sendBuf, sequence);
                WriteLocked(n);
            }
        }

        // 제어 프레임(Pong 등) 송신 — _sendLock 직렬화·재사용 버퍼(무할당).
        private void SendControl(BridgeMessageType type, long seq)
        {
            lock (_sendLock)
            {
                int n = BridgeSerializer.PackSeq(_sendBuf, type, seq);
                WriteLocked(n);
            }
        }

        // _sendLock 보유 가정. 바이트모드 파이프 동기 write+flush(짧고 빠른 구간·await 없음).
        private void WriteLocked(int len)
        {
            var pipe = _pipe;
            if (pipe is { IsConnected: true })
            {
                pipe.Write(_sendBuf, 0, len);
                pipe.Flush();
            }
        }

        private async Task<bool> ReadExactAsync(byte[] buf, CancellationToken ct)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = await _pipe!.ReadAsync(buf.AsMemory(total), ct);
                if (n == 0) return false;
                total += n;
            }
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pipe?.Dispose();
        }
    }
}
