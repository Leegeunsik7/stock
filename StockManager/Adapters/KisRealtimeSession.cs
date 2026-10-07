// ============================================================================
// KisRealtimeSession — [KIS 실배선 · 추천 A] KIS 실시간 WebSocket 세션(접속키 · 구독 · 수신 조립 · PINGPONG 반송 · 단절 인터락)
//
//  흐름(콜드 제어 + 수신 경로 δ=0):
//   ① 접속키 = KisLiveBroker.RequestApprovalKey()(POST /oauth2/Approval) — 실패 = 재시도 대기(미연결 = KisSessionInterlock 차단 유지).
//   ② 접속(실 ws://ops.koreainvestment.com:21000 · 모의 :31000 — 공개 문서 기준) → 구독 2건:
//      체결통보 H0STCNI0(모의 H0STCNI9 · tr_key = HTS ID) + ★심박용 체결가 H0STCNT0(유동 종목 · 기본 005930).
//      체결통보만 구독하면 장중에도 대부분 침묵 → 방향 B(장중 3초 침묵 차단)가 상시 오격발한다. 심박 구독이 침묵 판정의 전제.
//   ③ 수신: 고정(POH) 버퍼에 메시지 조각을 이어 붙여 완성 프레임만 KisOpenApiAdapter.OnFrame — 구독 응답의 key/iv 는 어댑터가 자동 장착,
//      체결통보는 어댑터 onFill(실행 피드 · 자식 매트릭스)로 디스패치. 버퍼 초과 메시지 = 끝까지 폐기(오판독 0).
//   ④ PINGPONG 반송: 수신 버퍼는 다음 수신에 재사용되므로 반송 전용 버퍼로 복사 후 송신 직렬화(동시 송신 금지 계약).
//   ⑤ 단절/예외 = KisSessionInterlock.MarkDisconnected(Trip · 라우터 KIS 장애 보고 · sticky) → 대기 후 재접속(체결 대사 지속용 · 무장은 이중키 재무장 전 차단).
//  ★liveArmed:false 무관 — 이 세션은 수신 전용(주문 송신 0).
// ============================================================================
using System;
using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Adapters
{
    /// <summary>WebSocket 전송 추상(테스트 대역 주입용).</summary>
    public interface IKisWsTransport : IDisposable
    {
        Task ConnectAsync(Uri uri, CancellationToken ct);
        Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct);
        /// <summary>(받은 바이트 수, 메시지 끝, 닫힘).</summary>
        ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct);
    }

    public sealed class ClientWebSocketTransport : IKisWsTransport
    {
        private readonly ClientWebSocket _ws = new();
        public Task ConnectAsync(Uri uri, CancellationToken ct) => _ws.ConnectAsync(uri, ct);
        public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => _ws.SendAsync(payload, WebSocketMessageType.Text, true, ct).AsTask();
        public async ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
        {
            var r = await _ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            return (r.Count, r.EndOfMessage, r.MessageType == WebSocketMessageType.Close);
        }
        public void Dispose() => _ws.Dispose();
    }

    public sealed class KisRealtimeSession : IDisposable
    {
        public static readonly Uri RealUri = new("ws://ops.koreainvestment.com:21000");
        public static readonly Uri VtsUri = new("ws://ops.koreainvestment.com:31000");

        private readonly KisOpenApiAdapter _adapter;
        private readonly KisSessionInterlock _session;
        private readonly Func<IKisWsTransport> _factory;
        private readonly Func<string?> _approval;
        private readonly string _htsId, _heartbeatSymbol;
        private readonly bool _vts;
        private readonly Uri _uri;
        private readonly TimeSpan _reconnect;
        private readonly byte[] _rx = GC.AllocateArray<byte>(256 * 1024, pinned: true), _echo = GC.AllocateArray<byte>(4096, pinned: true);
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private IKisWsTransport? _transport;
        private long _connects, _disconnects, _approvalFailures, _faults, _oversized, _framesIn, _echoes;

        public KisRealtimeSession(KisOpenApiAdapter adapter, KisSessionInterlock session, Func<IKisWsTransport> transportFactory, Func<string?> approvalKey,
                                  string htsId, bool vts, string heartbeatSymbol = "005930", Uri? uri = null, TimeSpan? reconnectDelay = null)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _factory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
            _approval = approvalKey ?? throw new ArgumentNullException(nameof(approvalKey));
            if (string.IsNullOrWhiteSpace(htsId)) throw new ArgumentException("HTS ID 필요(체결통보 구독 키)", nameof(htsId));
            _htsId = htsId; _vts = vts; _heartbeatSymbol = heartbeatSymbol;
            _uri = uri ?? (vts ? VtsUri : RealUri);
            _reconnect = reconnectDelay ?? TimeSpan.FromSeconds(5);
        }

        public long Connects => Interlocked.Read(ref _connects);
        public long Disconnects => Interlocked.Read(ref _disconnects);
        public long ApprovalFailures => Interlocked.Read(ref _approvalFailures);
        public long Faults => Interlocked.Read(ref _faults);
        public long Oversized => Interlocked.Read(ref _oversized);
        public long FramesIn => Interlocked.Read(ref _framesIn);
        public long Echoes => Interlocked.Read(ref _echoes);
        public string NoticeTrId => _vts ? "H0STCNI9" : "H0STCNI0";

        /// <summary>수신 루프(재접속 포함). 취소 토큰으로 종료.</summary>
        public async Task RunAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                string? key = null;
                try { key = _approval(); } catch (Exception) { key = null; }
                if (string.IsNullOrWhiteSpace(key))
                {
                    Interlocked.Increment(ref _approvalFailures);
                    if (!await DelayAsync(ct).ConfigureAwait(false)) break;
                    continue;
                }
                var t = _factory();
                bool connected = false;
                try
                {
                    await t.ConnectAsync(_uri, ct).ConfigureAwait(false);
                    Volatile.Write(ref _transport, t);
                    await t.SendTextAsync(BuildSubscribe(key!, NoticeTrId, _htsId), ct).ConfigureAwait(false);
                    await t.SendTextAsync(BuildSubscribe(key!, "H0STCNT0", _heartbeatSymbol), ct).ConfigureAwait(false);
                    connected = true;
                    Interlocked.Increment(ref _connects);
                    _session.MarkConnected();
                    await ReceiveLoopAsync(t, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception) { Interlocked.Increment(ref _faults); }
                finally
                {
                    Volatile.Write(ref _transport, null);
                    try { t.Dispose(); } catch (Exception) { }
                    if (connected) { Interlocked.Increment(ref _disconnects); _session.MarkDisconnected(); }   // ★단절 = 인터락 Trip(sticky)
                }
                if (ct.IsCancellationRequested || !await DelayAsync(ct).ConfigureAwait(false)) break;
            }
        }

        private async Task ReceiveLoopAsync(IKisWsTransport t, CancellationToken ct)
        {
            int len = 0; bool dropping = false;
            while (!ct.IsCancellationRequested)
            {
                if (len == _rx.Length) { dropping = true; len = 0; Interlocked.Increment(ref _oversized); }   // 버퍼 초과 — 메시지 끝까지 폐기
                var (count, end, closed) = await t.ReceiveAsync(_rx.AsMemory(len), ct).ConfigureAwait(false);
                if (closed) return;
                len += count;
                if (!end) continue;
                if (!dropping)
                {
                    Interlocked.Increment(ref _framesIn);
                    _adapter.OnFrame(_rx.AsMemory(0, len));                           // 체결통보 → onFill(실행 피드 · 자식 매트릭스)
                }
                dropping = false; len = 0;
            }
        }

        /// <summary>PINGPONG 반송(어댑터 send 콜백) — 반송 전용 버퍼로 복사 후 송신 직렬화. 반환 = 송신 성공.</summary>
        public bool TrySendFrame(ReadOnlyMemory<byte> frame)
        {
            var t = Volatile.Read(ref _transport);
            if (t is null || frame.Length > _echo.Length) return false;
            if (!_sendLock.Wait(TimeSpan.FromSeconds(2))) return false;
            try
            {
                frame.Span.CopyTo(_echo);
                bool ok = t.SendTextAsync(_echo.AsMemory(0, frame.Length), CancellationToken.None).Wait(TimeSpan.FromSeconds(5));
                if (ok) Interlocked.Increment(ref _echoes);
                return ok;
            }
            catch (Exception) { return false; }
            finally { _sendLock.Release(); }
        }

        /// <summary>구독 요청 JSON(콜드) — {"header":{approval_key,custtype:P,tr_type:1,content-type},"body":{"input":{tr_id,tr_key}}}.</summary>
        public static byte[] BuildSubscribe(string approvalKey, string trId, string trKey)
        {
            var buf = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject();
                w.WriteStartObject("header");
                w.WriteString("approval_key", approvalKey); w.WriteString("custtype", "P"); w.WriteString("tr_type", "1"); w.WriteString("content-type", "utf-8");
                w.WriteEndObject();
                w.WriteStartObject("body"); w.WriteStartObject("input");
                w.WriteString("tr_id", trId); w.WriteString("tr_key", trKey);
                w.WriteEndObject(); w.WriteEndObject();
                w.WriteEndObject();
            }
            return buf.WrittenSpan.ToArray();
        }

        private async Task<bool> DelayAsync(CancellationToken ct)
        {
            try { await Task.Delay(_reconnect, ct).ConfigureAwait(false); return true; }
            catch (OperationCanceledException) { return false; }
        }

        public void Dispose() => _sendLock.Dispose();
    }
}
