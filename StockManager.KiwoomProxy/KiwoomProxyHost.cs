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
            };
            SendFrame(e.FillQty > 0 ? BridgeMessageType.OrderFilled : BridgeMessageType.OrderRejected, in p);
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

        // ── 하트비트 루프 ─────────────────────────────────────────────────────
        private async Task HeartbeatLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(HeartbeatIntervalMs, ct);
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
