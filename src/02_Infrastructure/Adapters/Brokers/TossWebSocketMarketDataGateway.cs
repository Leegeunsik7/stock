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
//
//   [시세 하드닝] 교정(수정 전 실측):
//     ① 찢긴 최신 틱 — 심볼·가격·수량·시퀀스를 개별 Interlocked.Exchange 로 갱신해, 판독자가 A 종목 심볼 + B 종목 가격을 섞어 볼 수
//        있었다 → 단일 라이터 seqlock 스냅샷(TickSnapshot) 발행 · TryReadLatest 가 일관 창만 반환.
//     ② 역순/중복 시퀀스 덮어쓰기 — 재연결 리플레이·중복 프레임이 최신 값을 옛 가격으로 되돌렸다 → seq 단조 증가만 수용(계수).
//     ③ WebSocket 조각 프레임 — EndOfMessage=false 조각마다 파싱해 조각 전부가 파싱 오류로 버려졌다 → WsMessageAssembler 로
//        메시지 경계까지 고정 버퍼에 재조립(초과 = 그 메시지 전체 폐기·계수, 버퍼 재할당 0).
//     ④ 이상 시세 — 가격 ≤0·틱 점프·가격제한폭 이탈을 판정하는 MarketDataSpikeInterlock(선택) 결속, 격리 종목은 스냅샷 미반영.
// ============================================================================
using System;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MS2E.Infrastructure
{
    /// <summary>[피드 연동] 수용된 틱 1건 하류 사상 싱크(δ=0 계약 — 구현은 무할당이어야 한다). 이상치/역순/파싱 실패 틱은 전달되지 않는다.</summary>
    public interface ITickSink
    {
        void OnTick(long symbolHash, long priceRaw, long seq);
    }

    public sealed class TossWebSocketMarketDataGateway : IDisposable
    {
        private readonly SequenceContinuityGuardian _guardian;
        private readonly Func<long> _nowMicros;

        private readonly MarketDataSpikeInterlock? _spike;
        private readonly ITickSink? _tickSink;   // [피드 연동] 참조가 피드(ReferencePriceFeedAdapter) — 옵션
        private long _sinkFaults;
        // seqlock 발행 스냅샷(단일 라이터 = 수신 루프) — _version 홀수 = 기록 중
        private long _version;
        private long _latestSymbolHash, _latestPriceRaw, _latestQty, _latestSeq, _latestTs;
        private long _lastRttMicros, _ticksIngested, _parseErrors, _outOfOrder, _spikeRejected;
        private long _connected;   // 0/1
        private CancellationTokenSource? _cts;
        private Task? _recvLoop;

        public TossWebSocketMarketDataGateway(SequenceContinuityGuardian guardian, Func<long>? nowMicros = null, MarketDataSpikeInterlock? spikeGuard = null,
                                              ITickSink? tickSink = null)
        {
            _guardian = guardian ?? throw new ArgumentNullException(nameof(guardian));
            _nowMicros = nowMicros ?? (static () => 0L);
            _spike = spikeGuard;
            _tickSink = tickSink;
        }

        public long TickSinkFaults => Interlocked.Read(ref _sinkFaults);

        /// <summary>일관 최신 틱 1건(값 타입).</summary>
        public readonly record struct TickSnapshot(long SymbolHash, long PriceRaw, long Qty, long Seq, long TimestampMicros);

        public long OutOfOrderDropped => Interlocked.Read(ref _outOfOrder);
        public long SpikeRejected => Interlocked.Read(ref _spikeRejected);

        /// <summary>seqlock 일관 판독(δ=0). 기록 경합 시 재시도 — 발행 전(0건)이면 false.</summary>
        public bool TryReadLatest(out TickSnapshot tick)
        {
            for (int spin = 0; spin < 64; spin++)
            {
                long v1 = Volatile.Read(ref _version);
                if ((v1 & 1) != 0) { Thread.SpinWait(8); continue; }
                var t = new TickSnapshot(Volatile.Read(ref _latestSymbolHash), Volatile.Read(ref _latestPriceRaw),
                                         Volatile.Read(ref _latestQty), Volatile.Read(ref _latestSeq), Volatile.Read(ref _latestTs));
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _version) == v1) { tick = t; return v1 != 0; }
            }
            tick = default;
            return false;   // 지속 경합(라이터 폭주) — 찢긴 값 대신 판독 실패(fail-closed)
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
            // [시세 하드닝 ②] 단조 시퀀스만 수용 — 리플레이/중복/역순 프레임이 최신 스냅샷을 과거로 되돌리지 못함
            if (Volatile.Read(ref _version) != 0 && seq <= Volatile.Read(ref _latestSeq)) { Interlocked.Increment(ref _outOfOrder); return false; }
            // [시세 하드닝 ④] 가격 ≤0 · 이상치 · 격리 종목은 하류 미반영
            if (price <= 0L) { Interlocked.Increment(ref _parseErrors); return false; }
            if (_spike is not null && _spike.Observe(symHash, price) != SpikeVerdict.Accepted) { Interlocked.Increment(ref _spikeRejected); return false; }

            _ = _guardian.Observe(seq);   // ★시퀀스 연속성 판정(hole → 잠정)
            // [시세 하드닝 ①] seqlock 발행 — 판독자는 (심볼, 가격, 수량, 시퀀스, ts) 를 한 틱 단위로만 관측
            Interlocked.Increment(ref _version);   // 홀수 = 기록 중
            Volatile.Write(ref _latestSymbolHash, symHash);
            Volatile.Write(ref _latestPriceRaw, price);
            Volatile.Write(ref _latestQty, qty);
            Volatile.Write(ref _latestSeq, seq);
            Volatile.Write(ref _latestTs, ts);
            Interlocked.Increment(ref _version);   // 짝수 = 발행 완료
            if (_tickSink is { } sink)
            {
                try { sink.OnTick(symHash, price, seq); }
                catch (Exception) { Interlocked.Increment(ref _sinkFaults); }   // 싱크 결함이 수신 루프를 죽이지 않음(참조가 미갱신 → 만료로 fail-closed)
            }
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
            byte[] buffer = new byte[16 * 1024];   // 콜드: 수신 조각 버퍼 1회
            var assembler = new WsMessageAssembler(64 * 1024);   // [시세 하드닝 ③] 메시지 재조립 버퍼 1회
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
                        if (assembler.Append(buffer.AsSpan(0, result.Count), result.EndOfMessage))
                            IngestPacket(assembler.Message, _nowMicros());   // ★δ=0 인제스트(완성 메시지만)
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { /* 끊김 → 재연결(잠정 워터마크는 Guardian 이 시퀀스로 판정) */ }
                finally { Volatile.Write(ref _connected, 0L); assembler.Reset(); }   // 연결 경계 — 반쪽 메시지가 다음 연결로 새지 않게

                if (ct.IsCancellationRequested) break;
                try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { break; }   // 재연결 백오프
            }
        }

        public void Dispose() { Stop(); try { _cts?.Dispose(); } catch { } }

        public static long FnvSpan(ReadOnlySpan<byte> s)
        {
            unchecked
            {
                ulong h = 1469598103934665603UL;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
                return (long)h;
            }
        }
    }

    /// <summary>[시세 하드닝 ③] WebSocket 조각 프레임 → 완성 메시지 재조립(고정 버퍼 · 재할당 0). 초과 메시지는 경계까지 통째 폐기.</summary>
    public sealed class WsMessageAssembler
    {
        private readonly byte[] _buf;
        private int _len;
        private bool _overflow;
        private long _oversized;

        public WsMessageAssembler(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _buf = new byte[capacity];
        }

        public long OversizedDropped => Interlocked.Read(ref _oversized);
        /// <summary>직전 Append 가 true 를 반환했을 때의 완성 메시지(다음 Append 전까지 유효).</summary>
        public ReadOnlySpan<byte> Message => _buf.AsSpan(0, _len);

        /// <summary>조각 추가. 메시지 경계(endOfMessage)에서 완성 메시지가 있으면 true. 용량 초과 메시지는 false(폐기·계수).</summary>
        public bool Append(ReadOnlySpan<byte> chunk, bool endOfMessage)
        {
            if (_completed) { _len = 0; _completed = false; }
            if (!_overflow)
            {
                if (_len + chunk.Length > _buf.Length) { _overflow = true; _len = 0; }
                else { chunk.CopyTo(_buf.AsSpan(_len)); _len += chunk.Length; }
            }
            if (!endOfMessage) return false;
            if (_overflow) { _overflow = false; _len = 0; Interlocked.Increment(ref _oversized); return false; }
            _completed = true;
            return _len > 0;
        }

        private bool _completed;

        public void Reset() { _len = 0; _overflow = false; _completed = false; }
    }
}
