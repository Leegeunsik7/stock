// ============================================================================
// TossWebSocketMarketDataGateway — v10.2.0: 토스증권 실시간 시세 WebSocket 게이트웨이 (무할당)
//
//   ClientWebSocket 으로 토스 실시간 호가/체결 스트림을 수신하고, raw byte 패킷을 δ=0 파싱해 최신 틱 상태로 직접
//   인제스트한다(텔레메트리 퍼블리셔가 이 상태를 읽어 링에 반영). 수신 루프는 콜드(백그라운드)이며, 파싱·인제스트
//   핫패스(IngestPacket)는 힙 0. ★SequenceContinuityGuardian(Think-Beyond)로 시퀀스 hole 을 즉시 탐지·잠정 표시한다.
//
//   ★기대 틱 스키마(실 토스 스펙에 맞춰 조정): {"seq":L,"symbol":"005930","price":L,"qty":L,"ts":µs}
//
//   ★불변: δ=0(IngestPacket 핫패스 힙 0 — Utf8JsonReader ref struct·Interlocked) · fail-closed(파싱 실패/seq 결여 →
//     무시·계수) · liveArmed:false(순수 시세 수신/파싱/인제스트 — IBroker·소켓 송신·실 발주 유발 0).
// ============================================================================
using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace StockManager.Common
{
    public sealed class TossWebSocketMarketDataGateway : IDisposable
    {
        private readonly SequenceContinuityGuardian _guardian;
        private readonly Func<long> _nowMicros;

        private long _latestSymbolHash, _latestPriceRaw, _latestQty, _latestSeq;
        private long _lastRttMicros, _ticksIngested, _parseErrors;
        private long _connected;   // 0/1
        private CancellationTokenSource? _cts;
        private Task? _recvLoop;

        public TossWebSocketMarketDataGateway(SequenceContinuityGuardian guardian, Func<long>? nowMicros = null)
        {
            _guardian = guardian ?? throw new ArgumentNullException(nameof(guardian));
            _nowMicros = nowMicros ?? (static () => 0L);
        }

        // ── 읽기(δ=0·텔레메트리용) ──
        public bool Connected      => Volatile.Read(ref _connected) == 1L;
        public long LatestPriceRaw => Interlocked.Read(ref _latestPriceRaw);
        public long LatestSeq      => Interlocked.Read(ref _latestSeq);
        public long LastRttMicros  => Interlocked.Read(ref _lastRttMicros);
        public long TicksIngested  => Interlocked.Read(ref _ticksIngested);
        public long ParseErrors    => Interlocked.Read(ref _parseErrors);
        public SequenceContinuityGuardian Guardian => _guardian;

        /// <summary>
        /// ★raw 시세 패킷 δ=0 인제스트(핫패스). JSON 틱 파싱 → Guardian.Observe(seq) → 최신 틱 상태 갱신 + RTT 계측.
        /// 반환=인제스트 성공. 파싱 실패/seq 결여 → false(fail-closed·계수). 힙 0.
        /// </summary>
        public bool IngestPacket(ReadOnlySpan<byte> packet, long nowMicros)
        {
            long seq = -1L, symHash = 0L, price = 0L, qty = 0L, ts = 0L;
            try
            {
                var reader = new Utf8JsonReader(packet);
                while (reader.Read())
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    if      (reader.ValueTextEquals("seq"u8))    { reader.Read(); TossJsonParser.TryReadLong(ref reader, out seq); }
                    else if (reader.ValueTextEquals("symbol"u8)) { reader.Read(); symHash = FnvSpan(reader.ValueSpan); }
                    else if (reader.ValueTextEquals("price"u8))  { reader.Read(); TossJsonParser.TryReadLong(ref reader, out price); }
                    else if (reader.ValueTextEquals("qty"u8))    { reader.Read(); TossJsonParser.TryReadLong(ref reader, out qty); }
                    else if (reader.ValueTextEquals("ts"u8))     { reader.Read(); TossJsonParser.TryReadLong(ref reader, out ts); }
                    else reader.Skip();
                }
            }
            catch (JsonException)
            {
                Interlocked.Increment(ref _parseErrors);
                return false;
            }
            if (seq < 0L) { Interlocked.Increment(ref _parseErrors); return false; }   // seq 필수(fail-closed)

            _ = _guardian.Observe(seq);   // ★시퀀스 연속성 판정(hole → 잠정)
            Interlocked.Exchange(ref _latestSymbolHash, symHash);
            Interlocked.Exchange(ref _latestPriceRaw, price);
            Interlocked.Exchange(ref _latestQty, qty);
            Interlocked.Exchange(ref _latestSeq, seq);
            if (ts > 0L) Interlocked.Exchange(ref _lastRttMicros, Math.Max(0L, nowMicros - ts));
            Interlocked.Increment(ref _ticksIngested);
            return true;
        }

        /// <summary>실 스트림 수신 시작(콜드·백그라운드). 테스트는 IngestPacket 을 직접 호출한다.</summary>
        public void Start(Uri endpoint, string? bearerToken = null)
        {
            if (_recvLoop is not null) return;
            _cts = new CancellationTokenSource();
            _recvLoop = Task.Run(() => ReceiveLoopAsync(endpoint, bearerToken, _cts.Token));
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            Volatile.Write(ref _connected, 0L);
        }

        private async Task ReceiveLoopAsync(Uri endpoint, string? bearer, CancellationToken ct)
        {
            byte[] buffer = new byte[16 * 1024];   // 콜드: 수신 버퍼 1회 대여
            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    if (!string.IsNullOrEmpty(bearer))
                        ws.Options.SetRequestHeader("Authorization", "Bearer " + bearer);
                    await ws.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    Volatile.Write(ref _connected, 1L);
                    while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        if (result.Count > 0)
                            IngestPacket(buffer.AsSpan(0, result.Count), _nowMicros());   // ★δ=0 인제스트
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 끊김 → 재연결(잠정 워터마크는 Guardian 이 시퀀스로 판정) */ }
                finally { Volatile.Write(ref _connected, 0L); }

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }   // 재연결 백오프
            }
        }

        public void Dispose() { Stop(); try { _cts?.Dispose(); } catch { } }

        private static long FnvSpan(ReadOnlySpan<byte> s)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
                return (long)h;
            }
        }
    }
}
