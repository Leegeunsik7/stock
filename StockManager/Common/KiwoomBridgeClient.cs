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
        // [v4.48.3] 주입식 클럭 — 기본은 실제 UtcNow. 테스트가 가상 시각으로 타임아웃 판정을 결정론 검증.
        private readonly Func<long>                _now;
        // ★v4.83.0 IPC 핑퐁 — >0 이면 pingIntervalMs 주기로 Ping 송신, Pong 수신이 생존성 갱신(양방향).
        private readonly int                       _pingIntervalMs;
        private readonly SemaphoreSlim             _sendGate = new(1, 1);   // 클라 송신 직렬화(주문·Ping 동시 write 레이스 차단)
        private readonly byte[]                    _pingBuf  = new byte[BridgeSerializer.HeaderSize + sizeof(long)];  // Ping 재사용 버퍼(δ=0·_sendGate 하 단독 접근)
        private long                               _pingSeq;

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
            int                      heartbeatTimeoutMs = DefaultTimeoutMs,
            Func<long>?              nowTicks           = null,
            int                      pingIntervalMs     = 0)
        {
            _attestation        = attestation;
            _pipeName           = pipeName;
            _heartbeatTimeoutMs = heartbeatTimeoutMs;
            _now                = nowTicks ?? (() => DateTime.UtcNow.Ticks);
            _pingIntervalMs     = pingIntervalMs < 0 ? 0 : pingIntervalMs;
            _queue = Channel.CreateBounded<BridgeEnvelope>(
                new BoundedChannelOptions(MaxQueueDepth)
                { FullMode = BoundedChannelFullMode.DropOldest });
            _lastHeartbeatTicks = _now();
        }

        // ── 하트비트 타임아웃 판정 (HeartbeatWatchAsync·ReadLoop 의 결정 로직 = 테스트 검증 대상) ──────
        /// <summary>하트비트 수신 시각(틱)을 기록 → 타임아웃 카운터 리셋. ReadLoop 가 하트비트 메시지마다 호출.</summary>
        public void MarkHeartbeat(long nowTicks) => Interlocked.Exchange(ref _lastHeartbeatTicks, nowTicks);

        /// <summary>주어진 시각(틱) 기준, 마지막 하트비트 이후 경과가 타임아웃(ms)을 '초과(&gt;)'했는지.
        ///   주입 클럭을 인자로 받아 부하·벽시계와 무관하게 결정론적으로 판정/검증된다.</summary>
        public bool IsHeartbeatTimedOut(long nowTicks)
        {
            long last = Interlocked.Read(ref _lastHeartbeatTicks);
            return (nowTicks - last) / (double)TimeSpan.TicksPerMillisecond > _heartbeatTimeoutMs;
        }

        // ── 연결 ──────────────────────────────────────────────────────────────

        public async Task ConnectAsync(string serverName = ".", CancellationToken ct = default)
        {
            _pipe = new NamedPipeClientStream(
                serverName, _pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await _pipe.ConnectAsync(ct);
            _alive = true;
            MarkHeartbeat(_now());
        }

        // ── 주문 전송 (BridgeClient → ProxyHost) ─────────────────────────────

        public async Task SendOrderAsync(OrderRequest req, CancellationToken ct = default)
        {
            if (_pipe is null || !_pipe.IsConnected)
                throw new InvalidOperationException("KiwoomBridgeClient가 연결되지 않았습니다.");
            var buf = BridgeSerializer.PackOrderRequest(in req);
            // 클라 송신 직렬화 — 주문과 Ping 이 동일 파이프에 동시 write 하여 프레임이 섞이는 것을 차단.
            await _sendGate.WaitAsync(ct);
            try { await _pipe.WriteAsync(buf, ct); await _pipe.FlushAsync(ct); }
            finally { _sendGate.Release(); }
        }

        // ── IPC 핑퐁: Ping 송신(클라→프록시) ─────────────────────────────────
        private async Task SendPingAsync(CancellationToken ct)
        {
            var pipe = _pipe;
            if (pipe is null || !pipe.IsConnected) return;
            await _sendGate.WaitAsync(ct);
            try
            {
                // ★δ=0: 재사용 _pingBuf 에 span 오버로드로 pack(프레임당 힙 할당 0). _sendGate 하 단독 접근이라 안전.
                int n = BridgeSerializer.PackSeq(_pingBuf, BridgeMessageType.Ping, Interlocked.Increment(ref _pingSeq));
                await pipe.WriteAsync(_pingBuf.AsMemory(0, n), ct);
                await pipe.FlushAsync(ct);
            }
            finally { _sendGate.Release(); }
        }

        private async Task PingLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_pingIntervalMs, ct);
                    try { await SendPingAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* 전송 실패는 하트비트 감시가 ProxyLost 로 포착(fail-closed) */ }
                }
            }
            catch (OperationCanceledException) { }
        }

        // ── 루프 시작 ─────────────────────────────────────────────────────────

        /// <summary>읽기·디스패치·하트비트 감시 루프를 병렬 시작. CancellationToken 또는 Dispose로 중단.</summary>
        public Task StartAsync(CancellationToken ct = default)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _cts.Token;
            if (_pingIntervalMs > 0)
                return Task.WhenAll(
                    ReadLoopAsync(token),
                    DispatchLoopAsync(token),
                    HeartbeatWatchAsync(token),
                    PingLoopAsync(token));
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
                        MarkHeartbeat(_now());
                        continue;
                    }

                    // Pong 수신 = 프록시 생존 확인(양방향) → 타임아웃 카운터 리셋. 50ms 헬스체크의 핵심.
                    if (type == BridgeMessageType.Pong)
                    {
                        MarkHeartbeat(_now());
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
                    if (IsHeartbeatTimedOut(_now()))   // 결정 로직 단일화(테스트가 동일 메서드로 검증)
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
            // ★_sendGate 는 Dispose 하지 않는다: 취소 직후 in-flight Ping/주문의 WaitAsync/Release 와 레이스하면
            //   ObjectDisposedException(미관측 fault·거짓 동결)을 유발. SemaphoreSlim 은 AvailableWaitHandle 을
            //   건드리지 않는 한 관리 자원만 쓰므로 미해제해도 누수 없음(GC 회수).
        }
    }
}
