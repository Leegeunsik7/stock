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
using StockManager.Common;

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
            }
        }

        // ── COM 이벤트 → BridgeMessage (STA 스레드에서 호출) ─────────────────

        private void OnTick(object? sender, TickEventArgs e)
            => SendSync(BridgeSerializer.Pack(BridgeMessageType.RealtimeTick,
                new TickPayload
                {
                    SymbolCode     = e.SymbolCode,
                    Price          = e.Price,
                    Volume         = e.Volume,
                    ChangeRate     = e.ChangeRate,
                    TimestampTicks = DateTime.UtcNow.Ticks,
                }));

        private void OnFill(object? sender, FillEventArgs e)
            => SendSync(BridgeSerializer.Pack(
                e.FillQty > 0 ? BridgeMessageType.OrderFilled : BridgeMessageType.OrderRejected,
                new FillPayload
                {
                    OrderId        = e.OrderId,
                    SymbolCode     = e.SymbolCode,
                    FillPrice      = e.FillPrice,
                    FillQty        = e.FillQty,
                    Side           = e.Side,
                    TimestampTicks = DateTime.UtcNow.Ticks,
                }));

        private void OnAccount(object? sender, AccountEventArgs e)
            => SendSync(BridgeSerializer.Pack(BridgeMessageType.AccountState,
                new AccountPayload
                {
                    Deposit       = e.Deposit,
                    Equity        = e.Equity,
                    UnfilledCount = e.UnfilledCount,
                }));

        // ── 하트비트 루프 ─────────────────────────────────────────────────────
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatIntervalMs, ct);
                await SendAsync(BridgeSerializer.PackHeartbeat(
                    Interlocked.Increment(ref _sequence)));
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

        // ── 내부 헬퍼 ─────────────────────────────────────────────────────────
        private void SendSync(byte[] data)
            => SendAsync(data).GetAwaiter().GetResult();

        private async Task SendAsync(byte[] data)
        {
            if (_pipe is { IsConnected: true })
            {
                await _pipe.WriteAsync(data);
                await _pipe.FlushAsync();
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
