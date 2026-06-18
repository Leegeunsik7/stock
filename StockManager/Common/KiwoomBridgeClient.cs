// ============================================================================
// KiwoomBridgeClient — S14 #1 64비트 터미널 측 파이프 클라이언트
//
//  ConnectAsync → KiwoomProxyHost(x86)에 NamedPipe 연결.
//  StartAsync   → ReadLoopAsync + DispatchLoopAsync + HeartbeatWatchAsync 병렬 실행.
//
//  읽기 루프: BridgeMessage 파싱 → StockValidator 검증 → Channel<BridgeEnvelope> 큐.
//  디스패치:  큐에서 꺼내 typed 이벤트(TickReceived / FillReceived / ...) 발행.
//  하트비트:  HeartbeatTimeoutMs 초과 → ProxyLost 이벤트 + OrderAttestationService.ForceFreeze.
// ============================================================================
using System;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace StockManager.Common
{
    /// <summary>파이프 큐 항목 — 타입 + 역직렬화 전 raw 페이로드.</summary>
    public readonly record struct BridgeEnvelope(BridgeMessageType Type, byte[] Payload);

    public sealed class KiwoomBridgeClient : IDisposable
    {
        public const int DefaultTimeoutMs   = 5_000;
        private const int MaxQueueDepth     = 4_096;
        private const int MinCheckIntervalMs = 50;

        private readonly string                    _pipeName;
        private readonly int                       _heartbeatTimeoutMs;
        private readonly OrderAttestationService?  _attestation;

        private NamedPipeClientStream?             _pipe;
        private readonly Channel<BridgeEnvelope>  _queue;
        private long                               _lastHeartbeatTicks;
        private volatile bool                     _alive;
        private volatile bool                    _disposed;
        private CancellationTokenSource?          _cts;

        // ── 이벤트 ────────────────────────────────────────────────────────────
        public event Action<TickPayload>?    TickReceived;
        public event Action<FillPayload>?    FillReceived;
        public event Action<AccountPayload>? AccountReceived;
        public event Action<ErrorPayload>?   ErrorReceived;
        /// <summary>하트비트 타임아웃 또는 파이프 단절 시 발행.</summary>
        public event Action?                 ProxyLost;

        public bool IsProxyAlive => _alive;

        public KiwoomBridgeClient(
            OrderAttestationService? attestation        = null,
            string                   pipeName           = BridgeSerializer.DefaultPipeName,
            int                      heartbeatTimeoutMs = DefaultTimeoutMs)
        {
            _attestation        = attestation;
            _pipeName           = pipeName;
            _heartbeatTimeoutMs = heartbeatTimeoutMs;
            _queue = Channel.CreateBounded<BridgeEnvelope>(
                new BoundedChannelOptions(MaxQueueDepth)
                { FullMode = BoundedChannelFullMode.DropOldest });
            _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        }

        // ── 연결 ──────────────────────────────────────────────────────────────

        public async Task ConnectAsync(string serverName = ".", CancellationToken ct = default)
        {
            _pipe = new NamedPipeClientStream(
                serverName, _pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(ct);
            _alive = true;
            _lastHeartbeatTicks = DateTime.UtcNow.Ticks;
        }

        // ── 주문 전송 (BridgeClient → ProxyHost) ─────────────────────────────

        public async Task SendOrderAsync(OrderRequest req, CancellationToken ct = default)
        {
            if (_pipe is null || !_pipe.IsConnected)
                throw new InvalidOperationException("KiwoomBridgeClient가 연결되지 않았습니다.");
            var buf = BridgeSerializer.PackOrderRequest(in req);
            await _pipe.WriteAsync(buf, ct);
            await _pipe.FlushAsync(ct);
        }

        // ── 루프 시작 ─────────────────────────────────────────────────────────

        /// <summary>읽기·디스패치·하트비트 감시 루프를 병렬 시작. CancellationToken 또는 Dispose로 중단.</summary>
        public Task StartAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            return Task.WhenAll(
                ReadLoopAsync(token),
                DispatchLoopAsync(token),
                HeartbeatWatchAsync(token));
        }

        // ── 읽기 루프 ─────────────────────────────────────────────────────────
        private async Task ReadLoopAsync(CancellationToken ct)
        {
            var header = new byte[BridgeSerializer.HeaderSize];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(header, ct)) break;

                    if (!BridgeSerializer.TryParseHeader(header, out var type, out int plen))
                        break;  // 프로토콜 위반 → 연결 종료

                    byte[] payload = plen > 0 ? new byte[plen] : Array.Empty<byte>();
                    if (plen > 0 && !await ReadExactAsync(payload, ct)) break;

                    if (type == BridgeMessageType.Heartbeat)
                    {
                        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);
                        continue;
                    }

                    if (!ValidateIncoming(type, payload)) continue;  // 환각·변조 차단

                    await _queue.Writer.WriteAsync(new BridgeEnvelope(type, payload), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }   // 파이프가 Dispose()로 닫힌 경우
            catch (System.IO.IOException) { }     // 파이프 끊김
            finally
            {
                _alive = false;
                _queue.Writer.TryComplete();
            }
        }

        // ── 디스패치 루프 ─────────────────────────────────────────────────────
        private async Task DispatchLoopAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var env in _queue.Reader.ReadAllAsync(ct))
                    Dispatch(env);
            }
            catch (OperationCanceledException) { }
        }

        private void Dispatch(BridgeEnvelope env)
        {
            switch (env.Type)
            {
                case BridgeMessageType.RealtimeTick:
                    TickReceived?.Invoke(BridgeSerializer.Unpack<TickPayload>(env.Payload));
                    break;
                case BridgeMessageType.OrderFilled:
                case BridgeMessageType.OrderRejected:
                    FillReceived?.Invoke(BridgeSerializer.Unpack<FillPayload>(env.Payload));
                    break;
                case BridgeMessageType.AccountState:
                    AccountReceived?.Invoke(BridgeSerializer.Unpack<AccountPayload>(env.Payload));
                    break;
                case BridgeMessageType.ErrorReport:
                    ErrorReceived?.Invoke(BridgeSerializer.Unpack<ErrorPayload>(env.Payload));
                    break;
            }
        }

        // ── 하트비트 감시 ─────────────────────────────────────────────────────
        private async Task HeartbeatWatchAsync(CancellationToken ct)
        {
            int checkMs = Math.Max(MinCheckIntervalMs, _heartbeatTimeoutMs / 4);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(checkMs, ct);
                    long   last      = Interlocked.Read(ref _lastHeartbeatTicks);
                    double elapsedMs = (DateTime.UtcNow.Ticks - last) / (double)TimeSpan.TicksPerMillisecond;
                    if (elapsedMs > _heartbeatTimeoutMs)
                    {
                        _alive = false;
                        ProxyLost?.Invoke();
                        _attestation?.ForceFreeze("KiwoomProxyHost 하트비트 타임아웃 — 파이프 단절");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ── 검증 게이트 (StockValidator 위임) ────────────────────────────────
        private static bool ValidateIncoming(BridgeMessageType type, byte[] payload)
        {
            try
            {
                switch (type)
                {
                    case BridgeMessageType.RealtimeTick:
                        if (payload.Length < Unsafe.SizeOf<TickPayload>()) return false;
                        return StockValidator.ValidateTick(
                            BridgeSerializer.Unpack<TickPayload>(payload)).IsValid;

                    case BridgeMessageType.OrderFilled:
                    case BridgeMessageType.OrderRejected:
                        if (payload.Length < Unsafe.SizeOf<FillPayload>()) return false;
                        return StockValidator.ValidateFill(
                            BridgeSerializer.Unpack<FillPayload>(payload)).IsValid;

                    default:
                        return true;
                }
            }
            catch { return false; }
        }

        // ── 내부 헬퍼 ─────────────────────────────────────────────────────────
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
            _cts?.Cancel();
            _cts?.Dispose();
            _pipe?.Dispose();
        }
    }
}
