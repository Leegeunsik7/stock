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

namespace MS2E.Infrastructure
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
        // v4.92.0 영지식 패킷 절연(옵트인) — 배선 시 주문 프레임 송신 직전 이중키∧점화 검문. 기본 null=레거시 통과(IPC 기구 테스트 무영향).
        private readonly Func<bool>?               _orderArmed;
        private readonly Func<IgnitionStage>?      _ignitionStage;
        // [v4.48.3] 주입식 클럭 — 기본은 실제 UtcNow. 테스트가 가상 시각으로 타임아웃 판정을 결정론 검증.
        private readonly Func<long>                _now;
        // ★v4.83.0 IPC 핑퐁 — >0 이면 pingIntervalMs 주기로 Ping 송신, Pong 수신이 생존성 갱신(양방향).
        private readonly int                       _pingIntervalMs;
        private readonly SemaphoreSlim             _sendGate = new(1, 1);   // 클라 송신 직렬화(주문·Ping 동시 write 레이스 차단)
        private readonly byte[]                    _pingBuf  = new byte[BridgeSerializer.HeaderSize + sizeof(long)];  // Ping 재사용 버퍼(δ=0·_sendGate 하 단독 접근)
        private long                               _pingSeq;
        // ★v4.84.0 RTT 텔레메트리 — seq 슬롯 링(δ=0·고정 배열)으로 Ping 송신시각 기록, Pong 수신시 매칭 RTT 산출.
        private const int                          PingSlotMask = 63;       // 64 슬롯(2^n-1) — 소수 outstanding ping 매칭
        private readonly long[]                    _pingSentAt  = new long[PingSlotMask + 1];
        private long                               _currentRttTicks;
        private long                               _peakRttTicks;
        private long                               _pongReceived;

        private NamedPipeClientStream?             _pipe;
        private readonly Channel<BridgeEnvelope>  _queue;
        private long                               _lastHeartbeatTicks;
        private volatile bool                     _alive;
        private volatile bool                    _disposed;
        private CancellationTokenSource?          _cts;

        // ── [사전 검증] 파이프 서버 신원 ──────────────────────────────────────
        /// <summary>
        /// 연결 직후 서버 PID 를 넘겨 받는 신원 검증기(옵션 · 예: PipeServerIdentity.ImagePathVerifier(프록시 경로)).
        /// false/예외/PID 판독 실패 = 거부: 파이프 파괴 · ServerIdentityRejected 발화 · ConnectAsync 가 UnauthorizedAccessException.
        /// 미설정이면 CurrentUserOnly(서버 소유자 = 현재 사용자)만 강제.
        /// </summary>
        public Func<int, bool>? ServerIdentityVerifier { get; set; }
        public event Action<int>? ServerIdentityRejected;
        private long _identityRejections;
        public long IdentityRejections => Interlocked.Read(ref _identityRejections);

        // ── 이벤트 ────────────────────────────────────────────────────────────
        public event Action<TickPayload>?    TickReceived;
        public event Action<FillPayload>?    FillReceived;
        public event Action<AccountPayload>? AccountReceived;
        public event Action<OrderBookPayload>? OrderBookReceived;            // [L2] 10호가 스냅샷(검증 통과분)
        public event Action<ReferencePricePayload>? ReferencePriceReceived;  // [L2] 기준가(전일종가)
        public event Action<AccountCashPayload>? AccountCashReceived;        // [계좌] OPW00001 예수금상세
        public event Action<PositionPayload>? PositionReceived;              // [계좌] OPW00018 보유 1행
        public event Action<ErrorPayload>?   ErrorReceived;
        /// <summary>하트비트 타임아웃 또는 파이프 단절 시 발행.</summary>
        public event Action?                 ProxyLost;

        public bool IsProxyAlive => _alive;

        // ── IPC 레이턴시 텔레메트리(관제) — LatencyTelemetryPublisher 가 주기 독출 ──────────
        public long CurrentIpcRttTicks => Interlocked.Read(ref _currentRttTicks);   // 최근 Pong RTT(100ns ticks)
        public long PeakIpcRttTicks    => Interlocked.Read(ref _peakRttTicks);      // 관측 최대 RTT
        public long TotalPingSent      => Interlocked.Read(ref _pingSeq);           // 누적 Ping 송신
        public long TotalPongReceived  => Interlocked.Read(ref _pongReceived);      // 누적 Pong 수신

        public KiwoomBridgeClient(
            OrderAttestationService? attestation        = null,
            string                   pipeName           = BridgeSerializer.DefaultPipeName,
            int                      heartbeatTimeoutMs = DefaultTimeoutMs,
            Func<long>?              nowTicks           = null,
            int                      pingIntervalMs     = 0,
            Func<bool>?              orderArmed         = null,
            Func<IgnitionStage>?     ignitionStage      = null)
        {
            _attestation        = attestation;
            _orderArmed         = orderArmed;
            _ignitionStage      = ignitionStage;
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
            // [사전 검증] CurrentUserOnly — 다른 사용자 계정 소유 서버(파이프 선점)에는 연결 자체를 거부
            _pipe = new NamedPipeClientStream(
                serverName, _pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await _pipe.ConnectAsync(ct);
            if (ServerIdentityVerifier is { } verify)
            {
                bool ok;
                int pid = -1;
                try { ok = PipeServerIdentity.TryGetServerProcessId(_pipe.SafePipeHandle, out pid) && verify(pid); }
                catch (Exception) { ok = false; }
                if (!ok)
                {
                    // 같은 사용자 세션의 다른 프로세스가 프록시 이름을 선점 — 첫 바이트도 주고받기 전에 파괴(fail-closed)
                    try { _pipe.Dispose(); } catch { }
                    _pipe = null;
                    _alive = false;
                    Interlocked.Increment(ref _identityRejections);
                    try { ServerIdentityRejected?.Invoke(pid); } catch { }
                    throw new UnauthorizedAccessException($"키움 프록시 파이프 서버 신원 불일치(pid={pid}) — 연결 거부");
                }
            }
            _alive = true;
            MarkHeartbeat(_now());
        }

        // ── 주문 전송 (BridgeClient → ProxyHost) ─────────────────────────────

        public async Task SendOrderAsync(OrderRequest req, CancellationToken ct = default)
        {
            if (_pipe is null || !_pipe.IsConnected)
                throw new InvalidOperationException("KiwoomBridgeClient가 연결되지 않았습니다.");
            var buf = BridgeSerializer.PackOrderRequest(in req);
            // ★v4.92.0 영지식 패킷 절연: 무장 인지가 배선된 경우, 미무장(이중키∧MasterIgnited 아님) 주문 프레임이면 파이프 강제 파괴 +
            //   영구 동결 + throw(삼킴 금지). 상위 게이트웨이 버그로 미무장 주문이 흘러도 최하위에서 물리 차단. 미배선이면 레거시 통과.
            if (_ignitionStage is not null)
                ZeroLeakProtocolIsolator.AssertSendAllowed(buf,
                    _orderArmed?.Invoke() ?? false, _ignitionStage(), _attestation,
                    abort: () => { try { _pipe?.Dispose(); } catch { } _alive = false; });
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
                long seq = Interlocked.Increment(ref _pingSeq);
                Volatile.Write(ref _pingSentAt[(int)(seq & PingSlotMask)], _now());   // RTT 기준 시각(seq 슬롯)
                int n = BridgeSerializer.PackSeq(_pingBuf, BridgeMessageType.Ping, seq);
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

                    // Pong 수신 = 프록시 생존 확인(양방향) → 타임아웃 카운터 리셋 + RTT 산출. 50ms 헬스체크의 핵심.
                    if (type == BridgeMessageType.Pong)
                    {
                        if (payload.Length >= sizeof(long))
                        {
                            long seq    = BitConverter.ToInt64(payload, 0);
                            long sentAt = Volatile.Read(ref _pingSentAt[(int)(seq & PingSlotMask)]);
                            if (sentAt > 0)
                            {
                                long rtt = _now() - sentAt;
                                if (rtt >= 0)   // 클럭 역행 방어
                                {
                                    Volatile.Write(ref _currentRttTicks, rtt);
                                    if (rtt > _peakRttTicks) Volatile.Write(ref _peakRttTicks, rtt);   // ReadLoop 단일 스레드
                                }
                            }
                            Interlocked.Increment(ref _pongReceived);
                        }
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
                    // [키움 프로토콜] 56B 신 프레임 · 48B 구 프레임(잔량 없음) 모두 수용 — 길이 미달 프레임은 폐기(파싱 불가 = 미반영)
                    if (BridgeFillReader.TryReadFill(env.Payload, out var fill)) FillReceived?.Invoke(fill);
                    break;
                case BridgeMessageType.OrderBook:
                    OrderBookReceived?.Invoke(BridgeSerializer.Unpack<OrderBookPayload>(env.Payload));
                    break;
                case BridgeMessageType.ReferencePrice:
                    ReferencePriceReceived?.Invoke(BridgeSerializer.Unpack<ReferencePricePayload>(env.Payload));
                    break;
                case BridgeMessageType.AccountCash:
                    AccountCashReceived?.Invoke(BridgeSerializer.Unpack<AccountCashPayload>(env.Payload));
                    break;
                case BridgeMessageType.Position:
                    PositionReceived?.Invoke(BridgeSerializer.Unpack<PositionPayload>(env.Payload));
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
                        // [키움 프로토콜] 수정 전: 체결량 0 통보(접수·거부·취소확인 — 체결가 0)를 '체결' 규칙으로 검증해 전량 폐기했다
                        //   → 호스트는 거부/취소 확인을 한 번도 받지 못했다. 체결(>0)만 체결 규칙, 나머지는 주문 상태 통보 규칙.
                        if (!BridgeFillReader.TryReadFill(payload, out var f)) return false;
                        return (f.FillQty > 0 ? StockValidator.ValidateFill(f) : StockValidator.ValidateOrderNotice(f)).IsValid;

                    case BridgeMessageType.OrderBook:
                        // [L2] 길이·심볼 범위만 게이트(형식 오류 폐기). 시장 상태 판정(교차·공백·스프레드)은 하류 L2 파서/인터락 몫 —
                        //   여기서 버리면 플래시 크래시 징후 자체가 관측되지 않는다.
                        if (payload.Length < Unsafe.SizeOf<OrderBookPayload>()) return false;
                        var ob = BridgeSerializer.Unpack<OrderBookPayload>(payload);
                        return ob.SymbolCode > 0 && ob.SymbolCode <= 999_999;

                    case BridgeMessageType.AccountCash:
                        return payload.Length >= Unsafe.SizeOf<AccountCashPayload>();

                    case BridgeMessageType.Position:
                        // [계좌] 행 번호/행 수 정합 · 보유 행은 종목 범위 · 수량 비음수(형식 오류 행은 배치 전체를 미완으로 남겨 비교 생략 = 안전 측)
                        if (payload.Length < Unsafe.SizeOf<PositionPayload>()) return false;
                        var pos = BridgeSerializer.Unpack<PositionPayload>(payload);
                        if (pos.RowCount < 0 || pos.RowCount > 10_000 || pos.Quantity < 0) return false;
                        if (pos.RowCount == 0) return pos.RowIndex == 0;
                        return pos.RowIndex >= 0 && pos.RowIndex < pos.RowCount && pos.SymbolCode > 0 && pos.SymbolCode <= 999_999;

                    case BridgeMessageType.ReferencePrice:
                        if (payload.Length < Unsafe.SizeOf<ReferencePricePayload>()) return false;
                        var rp = BridgeSerializer.Unpack<ReferencePricePayload>(payload);
                        return rp.SymbolCode > 0 && rp.SymbolCode <= 999_999 && rp.BasePrice > 0;

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
