// ============================================================================
// LiveWireKisLongSoakTests — [v12.18.27-DEV] KIS 실시간 WebSocket 수신 경로 100만 프레임 LongSoak · δ=0 · 단절/재접속/침묵 복구
//
//  §1 오프라인 결정론 소크(상시 실행): 실 수신 루프 KisRealtimeSession.RunAsync + KisOpenApiAdapter.OnFrame 를 동기 완료 대역 전송으로 구동 —
//     H0STASP0 10호가(38종목) · H0STCNT0 체결가 · PINGPONG(반송 송신) · 조각 메시지 · 버퍼 초과 메시지를 100만 프레임 주입.
//     연결마다 장애 1종 순환: 서버 종료(Close) · 수신 예외(네트워크 리셋) · 핑/퐁 침묵(시계 +61초 → 인터락 Evaluate 트립 + SilentNow → AbortConnection).
//     단언: 크래시 0 · 재접속 횟수 = 장애 횟수 · 단절마다 인터락 트립(sticky) · 프레임 수 정확 · 연결별 정상 상태 구간 스레드 할당 0B(δ=0).
//     [v12.18.30] 측정 하드닝: 별도 어댑터 5,000프레임 사전 파싱(JIT · 티어 승격) → GC 정리 → 전용 측정 스레드(스레드풀 · 동기화 컨텍스트 무관)에서 구동 →
//     창별 할당 기록 · 최솟값 0B(결정론 δ=0) + 과반 창 0B + 튐 창 상한(할당 컨텍스트 양자 수준) — 실 누수(창당 ≥ 48,000프레임)는 전 창에서 드러난다.
//  §2 리얼 와이어(옵트인): 환경변수 MS2E_KIS_VTS_APPKEY · MS2E_KIS_VTS_SECRETKEY · MS2E_KIS_VTS_HTSID 가 모두 있고 KRX 정규장일 때만 실행(아니면 '건너뜀' — 통과 위장 0).
//     ★모의투자(VTS) 도메인 고정: 접속키 = https://openapivts.koreainvestment.com:29443/oauth2/Approval 1회 · WS = ws://ops.koreainvestment.com:31000 시세 구독만.
//     주문 · 잔고 · 토큰 REST 호출 0 · 브로커/디스패처 인스턴스 0(liveArmed:false 무관 — 수신 전용 경로). 100만 틱(호가 + 체결) 또는 MS2E_KIS_SOAK_MINUTES(기본 60) 도달 중 빠른 쪽.
//     δ=0 = 실 수신 프레임을 고정 버퍼에 캡처 → 소크 종료 후 같은 판독 경로(새 어댑터)로 재주입해 스레드 할당 0B 단언. 실 소켓(ClientWebSocket ·
//     스레드풀 비동기)의 프로세스 전체 할당은 프레임당 바이트로 보고만 한다(BCL 소켓 내부 할당 — 판독 핫패스 아님 · 정직).
// ============================================================================
using System;
using System.Buffers;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;
using Xunit.Abstractions;

namespace StockManager.Tests.Integration
{
    /// <summary>KIS VTS 리얼 와이어 옵트인 — 자격증명 환경변수 3종 + KRX 정규장일 때만 실행(아니면 Skip 사유 표기).</summary>
    public sealed class KisVtsLiveFactAttribute : FactAttribute
    {
        public const string AppKeyVar = "MS2E_KIS_VTS_APPKEY", SecretVar = "MS2E_KIS_VTS_SECRETKEY", HtsIdVar = "MS2E_KIS_VTS_HTSID";

        public KisVtsLiveFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppKeyVar)) || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(SecretVar))
                || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(HtsIdVar)))
                Skip = "KIS 모의투자 자격증명 미설정(" + AppKeyVar + " · " + SecretVar + " · " + HtsIdVar + ") — 리얼 와이어 소크 미실행";
            else if (!KisSessionInterlock.IsKrxRegularSession(DateTime.UtcNow))
                Skip = "KRX 정규장(평일 09:00 ~ 15:20 KST) 밖 — 실시간 시세 없음 · 리얼 와이어 소크 미실행";
        }
    }

    [Collection("ConcurrencyStress")]
    public sealed class LiveWireKisLongSoakTests
    {
        private const int TargetFrames = 1_000_000;
        private readonly ITestOutputHelper _out;

        public LiveWireKisLongSoakTests(ITestOutputHelper o) => _out = o;

        private static readonly string[] Symbols =
        {
            "005930", "000660", "373220", "207940", "005380", "000270", "068270", "005490", "035420", "105560", "055550", "012330", "051910",
            "006400", "035720", "028260", "066570", "003670", "096770", "034730", "015760", "032830", "086790", "017670", "003550", "018260",
            "010130", "009150", "011200", "033780", "316140", "024110", "138040", "000810", "047050", "010950", "036570", "251270",
        };

        // ═══════════ 프레임 원천(콜드 생성 · 판독 시 복사만) ═══════════

        private static byte[] BookFrame(string sym, long mid, int variant)
        {
            const long Tick = 100;
            var f = new string[59];
            for (int i = 0; i < f.Length; i++) f[i] = "0";
            f[0] = sym; f[1] = "0930" + (variant % 60).ToString("D2"); f[2] = "0";
            for (int i = 0; i < 10; i++)
            {
                f[3 + i] = (mid + Tick * (i + 1)).ToString();
                f[13 + i] = (mid - Tick * i).ToString();
                f[23 + i] = (100 + variant + i).ToString();
                f[33 + i] = (200 + variant * 3 + i).ToString();
            }
            return Encoding.ASCII.GetBytes("0|H0STASP0|001|" + string.Join('^', f));
        }

        private static byte[] TickFrame(string sym, long price, int variant)
        {
            var f = new string[KisProtocol.H0STCNT0FieldCount];
            for (int i = 0; i < f.Length; i++) f[i] = "0";
            f[0] = sym; f[1] = "0930" + (variant % 60).ToString("D2"); f[2] = price.ToString();
            f[12] = (1 + variant).ToString(); f[13] = (1_000 + variant * 7).ToString(); f[21] = (variant & 1) == 0 ? "1" : "5";
            return Encoding.ASCII.GetBytes("0|H0STCNT0|001|" + string.Join('^', f));
        }

        private static readonly byte[] Ping = Encoding.ASCII.GetBytes("{\"header\":{\"tr_id\":\"PINGPONG\",\"datetime\":\"20261020093000\"}}");

        /// <summary>장애 종류(연결마다 순환).</summary>
        private enum Fault { ServerClose = 0, NetworkReset = 1, PingTimeout = 2 }

        /// <summary>
        /// 동기 완료 대역 전송(δ=0 판독 · 복사만) — 연결 1개당 FramesPerConnection 프레임 후 장애 1종. 수신 호출은 RunAsync 를 호출한 스레드에서 동기 진행하므로
        /// GC.GetAllocatedBytesForCurrentThread 로 판독 경로 할당을 직접 측정한다(연결별 정상 상태 창 = 프레임 1,000 ~ 마지막).
        /// </summary>
        private sealed class SoakWire
        {
            public const int FramesPerConnection = 50_000, WarmFrames = 1_000;
            private readonly byte[][] _pool;
            private readonly byte[] _oversized = new byte[300 * 1024];
            public readonly long[] Clock = { 1_000_000 };
            public KisRealtimeSession? Session;
            public KisSessionInterlock? Interlock;
            public CancellationTokenSource? Stop;
            public int Delivered, Connections, PingsSent, Chunks, OversizedSent;
            public int FaultsInjected;
            public long SteadyAllocated, SteadyWindows;
            public readonly long[] WindowAllocated = new long[(TargetFrames - 1) / FramesPerConnection + 1];          // 연결 수 = 정상 상태 창 수
            public readonly int[] FaultCounts = new int[3];

            public SoakWire(byte[][] pool) => _pool = pool;

            public Transport Next() { Connections++; return new Transport(this); }

            public sealed class Transport : IKisWsTransport
            {
                private readonly SoakWire _w;
                private int _inConn, _chunkRest = -1;
                private long _windowStart;
                private byte[]? _cur;
                public Transport(SoakWire w) => _w = w;

                public Task ConnectAsync(Uri uri, CancellationToken ct) => Task.CompletedTask;
                public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
                {
                    if (payload.Span.IndexOf("PINGPONG"u8) >= 0) _w.PingsSent++;
                    return Task.CompletedTask;
                }

                public ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
                {
                    var w = _w;
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    if (_chunkRest >= 0)                                                        // 조각 메시지 후반부
                    {
                        int n = _cur!.Length - _chunkRest;
                        _cur.AsSpan(_chunkRest, n).CopyTo(buffer.Span);
                        _chunkRest = -1;
                        return new((n, true, false));
                    }
                    if (w.Delivered >= TargetFrames)
                    {
                        CloseWindow();
                        w.Stop!.Cancel();
                        return new((0, true, true));
                    }
                    if (_inConn == FramesPerConnection)
                    {
                        CloseWindow();
                        var kind = (Fault)(w.Connections % 3);
                        w.FaultsInjected++; w.FaultCounts[(int)kind]++;
                        _inConn++;
                        switch (kind)
                        {
                            case Fault.ServerClose: return new((0, true, true));
                            case Fault.NetworkReset: throw new System.Net.WebSockets.WebSocketException("soak: connection reset");
                            default:
                                w.Clock[0] += 61_000_000;                                       // 핑/퐁 61초 침묵(기본 임계 60초)
                                w.Interlock!.Evaluate();                                       // 감시 주기 판정 = 트립
                                if (w.Interlock.SilentNow()) w.Session!.AbortConnection();     // 침묵 판정 → 죽은 소켓 강제 종료
                                throw new OperationCanceledException(ct);
                        }
                    }
                    if (_inConn == WarmFrames) _windowStart = GC.GetAllocatedBytesForCurrentThread();
                    int k = _inConn++;
                    w.Delivered++;
                    w.Clock[0] += 1_000;                                                       // 프레임당 1ms 시장 시각
                    if (k == 7)                                                                 // 연결마다 1회 버퍼 초과(256KB) 메시지 — 끝까지 폐기돼야 함
                    {
                        w.OversizedSent++;
                        if (buffer.Length >= _w._oversized.Length) { _w._oversized.AsSpan().CopyTo(buffer.Span); return new((_w._oversized.Length, true, false)); }
                        _w._oversized.AsSpan(0, buffer.Length).CopyTo(buffer.Span);
                        _cur = _w._oversized; _chunkRest = buffer.Length;
                        return new((buffer.Length, false, false));
                    }
                    var src = (k & 63) == 63 ? Ping : _w._pool[(int)((uint)(k * 2_654_435_761u) % (uint)_w._pool.Length)];
                    if ((k & 15) == 5)                                                          // 16프레임마다 2조각 분할 수신
                    {
                        int half = src.Length / 2;
                        src.AsSpan(0, half).CopyTo(buffer.Span);
                        _cur = src; _chunkRest = half;
                        w.Chunks++;
                        return new((half, false, false));
                    }
                    src.CopyTo(buffer.Span);
                    return new((src.Length, true, false));
                }

                private void CloseWindow()
                {
                    if (_inConn <= WarmFrames) return;
                    long d = GC.GetAllocatedBytesForCurrentThread() - _windowStart;
                    _w.SteadyAllocated += d;
                    if (_w.SteadyWindows < _w.WindowAllocated.Length) _w.WindowAllocated[_w.SteadyWindows] = d;
                    _w.SteadyWindows++;
                    _inConn = FramesPerConnection + 1;
                }

                public void Dispose() { }
            }
        }

        private sealed class Sink
        {
            public long Books, Ticks, BidSum;
            public void OnBook(OrderBookPayload b) { Books++; BidSum += b.BidPrice[0]; }
            public void OnTick(TickPayload t) => Ticks++;
        }

        private static byte[][] BuildPool()
        {
            var pool = new byte[Symbols.Length * 8 * 2][];
            int n = 0;
            for (int s = 0; s < Symbols.Length; s++)
                for (int v = 0; v < 8; v++)
                {
                    long mid = 50_000 + s * 1_000 + v * 100;
                    pool[n++] = BookFrame(Symbols[s], mid, v);
                    pool[n++] = TickFrame(Symbols[s], mid, v);
                }
            return pool;
        }

        /// <summary>창 하나에서 외생 노이즈(타 스레드 GC 겹침 시 할당 컨텍스트 재계수)로 허용하는 상한 — 관측 2,992B · 8,168B. 실 누수는 창당 ≥ 48,000프레임이라 이를 크게 넘는다.</summary>
        private const long NoiseWindowCeiling = 64 * 1024;

        /// <summary>판독 경로 사전 파싱(웜업) — 측정 대상과 분리된 어댑터 · 인터락 · 싱크로 JIT/티어 승격/내부 버퍼를 선완료(본 소크 계수 무오염).</summary>
        private static void WarmupParse(byte[][] pool, int frames)
        {
            var sink = new Sink();
            using var warm = new KisOpenApiAdapter(new KisSessionInterlock(null, 1, static () => 1), send: static _ => true, onTick: sink.OnTick, onBook: sink.OnBook,
                                                   nowTicks: static () => 638_000_000_000_000_000);
            for (int k = 0; k < frames; k++)
                warm.OnFrame((k & 63) == 63 ? Ping : pool[(int)((uint)(k * 2_654_435_761u) % (uint)pool.Length)]);
        }

        // ═══ §1 오프라인 결정론 소크 ═══

        [Fact]
        public async Task Offline_1M_Frame_Soak_Recovers_From_Close_Reset_And_PingTimeout_With_Zero_Alloc_Receive_Path()
        {
            var pool = BuildPool();
            WarmupParse(pool, 5_000);
            var wire = new SoakWire(pool);
            var interlock = new KisSessionInterlock(null, 1, () => wire.Clock[0]);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KisSession);
            interlock.BindInterlock(mask, ArmingInterlockSource.KisSession);
            wire.Interlock = interlock;
            var sink = new Sink();
            Action<OrderBookPayload> onBook = sink.OnBook;
            Action<TickPayload> onTick = sink.OnTick;
            KisRealtimeSession? rt = null;
            using var adapter = new KisOpenApiAdapter(interlock, send: f => rt!.TrySendFrame(f), onTick: onTick, onBook: onBook, nowTicks: () => 638_000_000_000_000_000);
            int approvals = 0;
            rt = new KisRealtimeSession(adapter, interlock, wire.Next, () => { approvals++; return "soak-approval"; }, "soakuser", vts: true,
                                        reconnectDelay: TimeSpan.Zero, bookSymbols: Symbols);
            wire.Session = rt;
            using var stop = new CancellationTokenSource();
            wire.Stop = stop;

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();                         // 선행 테스트 잔여 쓰레기 정리 → 측정 중 GC 겹침 확률 축소
            long t0 = 0, t1 = 0, allocTotal = 0;
            Task? run = null;
            Exception? fault = null;
            var measure = new Thread(() =>                                                      // 전용 측정 스레드: 수신 루프 전 구간이 이 스레드에서 동기 진행
            {
                try
                {
                    long a0 = GC.GetAllocatedBytesForCurrentThread();
                    t0 = Stopwatch.GetTimestamp();
                    run = rt.RunAsync(stop.Token);
                    t1 = Stopwatch.GetTimestamp();
                    allocTotal = GC.GetAllocatedBytesForCurrentThread() - a0;
                }
                catch (Exception e) { fault = e; }
            }, 16 * 1024 * 1024) { IsBackground = true, Name = "KisSoakMeasure" };
            measure.Start();
            measure.Join();
            Assert.Null(fault);
            Assert.True(run!.IsCompleted, "동기 대역 전송인데 수신 루프가 비동기로 넘어감(측정 전제 붕괴)");
            await run;
            double sec = (t1 - t0) / (double)Stopwatch.Frequency;

            int expectedFaults = (TargetFrames - 1) / SoakWire.FramesPerConnection;           // 마지막 연결은 목표 도달 종료
            _out.WriteLine($"[오프라인 소크] {wire.Delivered:N0}프레임 · {sec:F2}s ({wire.Delivered / sec:N0}/s) · 연결 {rt.Connects} · 단절 {rt.Disconnects} · 장애 {wire.FaultsInjected}" +
                           $"(종료 {wire.FaultCounts[0]} · 리셋 {wire.FaultCounts[1]} · 핑타임아웃 {wire.FaultCounts[2]}) · 강제종료 {rt.Aborts} · 예외 {rt.Faults} · 트립 {interlock.Trips}" +
                           $" · 호가 {sink.Books:N0} · 체결 {sink.Ticks:N0} · 핑반송 {wire.PingsSent} · 조각 {wire.Chunks} · 초과폐기 {rt.Oversized}" +
                           $" · 정상구간 할당 {wire.SteadyAllocated}B/{wire.SteadyWindows}창 · 전체 {allocTotal:N0}B(연결당 콜드 {allocTotal / Math.Max(1, rt.Connects):N0}B)" +
                           $" · 창별 [{string.Join(',', wire.WindowAllocated)}]");

            Assert.Equal(TargetFrames, wire.Delivered);
            Assert.Equal(expectedFaults, wire.FaultsInjected);
            Assert.True(wire.FaultCounts[0] > 0 && wire.FaultCounts[1] > 0 && wire.FaultCounts[2] > 0);
            Assert.Equal(expectedFaults + 1, rt.Connects);                                    // 장애마다 재접속 1회
            Assert.Equal(rt.Connects, rt.Disconnects);
            Assert.Equal(rt.Connects, approvals);
            Assert.Equal(wire.FaultCounts[2], rt.Aborts);                                      // 침묵 판정 = 강제 종료
            Assert.Equal(wire.FaultCounts[1] + wire.FaultCounts[2], rt.Faults);                // 리셋 · 침묵 종료 = 수신 예외 계수(크래시 0)
            Assert.True(interlock.Tripped);                                                    // 단절 = sticky 트립(재무장은 이중키 경로만)
            Assert.True(mask.IsRaised(ArmingInterlockSource.KisSession));
            Assert.Equal(wire.OversizedSent, rt.Oversized);
            Assert.Equal(TargetFrames - wire.OversizedSent, rt.FramesIn);
            Assert.Equal(rt.FramesIn, adapter.Frames);
            Assert.Equal(0, adapter.Malformed);
            Assert.Equal(0, adapter.BookRejects);
            Assert.True(sink.Books > 400_000 && sink.Ticks > 400_000, $"books={sink.Books} ticks={sink.Ticks}");
            Assert.Equal(adapter.BooksEmitted, sink.Books);
            Assert.True(wire.PingsSent > 10_000 && wire.PingsSent == adapter.Echoes, $"ping={wire.PingsSent} echo={adapter.Echoes}");
            Assert.Equal(expectedFaults + 1, wire.SteadyWindows);
            Assert.Equal(wire.WindowAllocated.Length, wire.SteadyWindows);
            long minWindow = long.MaxValue, maxWindow = 0;
            int zeroWindows = 0;
            foreach (long d in wire.WindowAllocated)
            {
                Assert.True(d >= 0, $"창 할당 음수 {d}B(측정 전제 붕괴)");
                minWindow = Math.Min(minWindow, d); maxWindow = Math.Max(maxWindow, d);
                if (d == 0) zeroWindows++;
            }
            Assert.Equal(0, minWindow);                                                        // ★δ=0: 수신 조립 · 판독 · 반송 정상 상태 0B(다중 표본 최솟값 · 결정론)
            Assert.True(zeroWindows * 2 > wire.WindowAllocated.Length, $"0B 창 {zeroWindows}/{wire.WindowAllocated.Length} — 과반 미달(상시 할당 의심)");
            Assert.True(maxWindow <= NoiseWindowCeiling, $"최대 창 {maxWindow}B > 노이즈 상한 {NoiseWindowCeiling}B — 실 누수 의심");
        }

        // ═══ §2 리얼 와이어(옵트인 · KIS 모의투자) ═══

        private const string VtsRest = "https://openapivts.koreainvestment.com:29443";

        /// <summary>실 수신 바이트를 고정 버퍼에 복사(최대 CaptureFrames 프레임 · 판독 경로 재주입용) — 전송 데코레이터.</summary>
        private sealed class CapturingTransport : IKisWsTransport
        {
            public const int CaptureFrames = 8_192, CaptureBytes = 16 * 1024 * 1024;
            private readonly ClientWebSocketTransport _inner = new();
            private readonly Capture _cap;
            public CapturingTransport(Capture cap) => _cap = cap;
            public Task ConnectAsync(Uri uri, CancellationToken ct) => _inner.ConnectAsync(uri, ct);
            public Task SendTextAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => _inner.SendTextAsync(payload, ct);
            public async ValueTask<(int Count, bool EndOfMessage, bool Closed)> ReceiveAsync(Memory<byte> buffer, CancellationToken ct)
            {
                var r = await _inner.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (!r.Closed) _cap.Append(buffer.Span[..r.Count], r.EndOfMessage);
                return r;
            }
            public void Dispose() => _inner.Dispose();
        }

        private sealed class Capture
        {
            public readonly byte[] Bytes = GC.AllocateArray<byte>(CapturingTransport.CaptureBytes, pinned: true);
            public readonly int[] Starts = new int[CapturingTransport.CaptureFrames + 1];
            public int Frames, Len, Pending;
            public void Append(ReadOnlySpan<byte> part, bool end)
            {
                if (Frames >= CapturingTransport.CaptureFrames) return;
                if (Len + Pending + part.Length > Bytes.Length) { Pending = 0; return; }
                part.CopyTo(Bytes.AsSpan(Len + Pending));
                Pending += part.Length;
                if (!end) return;
                Starts[Frames] = Len; Len += Pending; Pending = 0; Frames++; Starts[Frames] = Len;
            }
        }

        private static string? RequestVtsApprovalKey(string appKey, string secret)
        {
            var buf = new ArrayBufferWriter<byte>(256);
            using (var w = new Utf8JsonWriter(buf))
            {
                w.WriteStartObject(); w.WriteString("grant_type", "client_credentials"); w.WriteString("appkey", appKey); w.WriteString("secretkey", secret); w.WriteEndObject();
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var content = new ByteArrayContent(buf.WrittenSpan.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var resp = http.PostAsync(VtsRest + "/oauth2/Approval", content).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return doc.RootElement.TryGetProperty("approval_key", out var k) ? k.GetString() : null;
        }

        [KisVtsLiveFact]
        public async Task LiveWire_Vts_Soak_Until_1M_Ticks_Or_Deadline_Without_Crash_And_Zero_Alloc_Parse_Replay()
        {
            string appKey = Environment.GetEnvironmentVariable(KisVtsLiveFactAttribute.AppKeyVar)!.Trim();
            string secret = Environment.GetEnvironmentVariable(KisVtsLiveFactAttribute.SecretVar)!.Trim();
            string htsId = Environment.GetEnvironmentVariable(KisVtsLiveFactAttribute.HtsIdVar)!.Trim();
            int minutes = int.TryParse(Environment.GetEnvironmentVariable("MS2E_KIS_SOAK_MINUTES"), out int m) && m is > 0 and <= 60 ? m : 60;
            Assert.StartsWith("https://openapivts.", VtsRest);                                // 모의 도메인 고정(실계좌 도메인 경로 0)
            Assert.Equal(31000, KisRealtimeSession.VtsUri.Port);

            var interlock = new KisSessionInterlock(null, 1, marketHours: () => KisSessionInterlock.IsKrxRegularSession(DateTime.UtcNow));
            var sink = new Sink();
            KisRealtimeSession? rt = null;
            using var adapter = new KisOpenApiAdapter(interlock, send: f => rt!.TrySendFrame(f), onTick: sink.OnTick, onBook: sink.OnBook);
            var cap = new Capture();
            int approvals = 0;
            rt = new KisRealtimeSession(adapter, interlock, () => new CapturingTransport(cap),
                                        () => { Interlocked.Increment(ref approvals); return RequestVtsApprovalKey(appKey, secret); },
                                        htsId, vts: true, bookSymbols: Symbols, reconnectDelay: TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            GC.Collect(); GC.WaitForPendingFinalizers();
            long alloc0 = GC.GetTotalAllocatedBytes(precise: true);
            int gen0 = GC.CollectionCount(0);
            var sw = Stopwatch.StartNew();
            var run = Task.Run(() => rt.RunAsync(cts.Token));
            var deadline = TimeSpan.FromMinutes(minutes);
            long lastReport = 0;
            while (sw.Elapsed < deadline && sink.Books + sink.Ticks < TargetFrames && !run.IsCompleted)
            {
                await Task.Delay(1_000);
                interlock.Evaluate();
                if (interlock.SilentNow()) rt.AbortConnection();                                // 침묵(half-open) = 강제 종료 → 재접속
                if (sw.ElapsedMilliseconds - lastReport >= 60_000)
                {
                    lastReport = sw.ElapsedMilliseconds;
                    _out.WriteLine($"  {sw.Elapsed:mm\\:ss} 호가 {sink.Books:N0} · 체결 {sink.Ticks:N0} · 연결 {rt.Connects} · 단절 {rt.Disconnects} · 예외 {rt.Faults}");
                }
            }
            cts.Cancel();
            await run;
            long allocTotal = GC.GetTotalAllocatedBytes(precise: true) - alloc0;
            long frames = Math.Max(1, rt.FramesIn);
            _out.WriteLine($"[리얼 와이어 VTS] {sw.Elapsed:hh\\:mm\\:ss} · 프레임 {rt.FramesIn:N0} · 호가 {sink.Books:N0} · 체결 {sink.Ticks:N0} · 구독 응답 {adapter.SubscribeAcks}/거부 {adapter.SubscribeRejects}" +
                           $" · 핑 {interlock.PingPongs} · 반송 {adapter.Echoes} · 연결 {rt.Connects} · 단절 {rt.Disconnects} · 강제종료 {rt.Aborts} · 예외 {rt.Faults} · 접속키 {approvals}" +
                           $" · 형식이탈 {adapter.Malformed} · 호가거부 {adapter.BookRejects} · 비연속 {adapter.BookNonContinuous}" +
                           $" · 프로세스 할당 {allocTotal:N0}B ({allocTotal / frames:N0}B/프레임 · BCL 소켓 포함) · Gen0 {GC.CollectionCount(0) - gen0}");

            Assert.True(rt.Connects >= 1, "접속 0 — 접속키 실패 " + rt.ApprovalFailures);
            Assert.True(adapter.SubscribeAcks >= 1, "구독 응답 0");
            Assert.True(sink.Books + sink.Ticks > 0, "실 시세 수신 0");

            // δ=0: 캡처한 실 프레임을 새 어댑터(같은 판독 경로)로 재주입 — 스레드 할당 0B(3창 최솟값)
            var replayInterlock = new KisSessionInterlock(null, 1, () => 1);
            var replaySink = new Sink();
            Action<OrderBookPayload> rb = replaySink.OnBook; Action<TickPayload> rtck = replaySink.OnTick;
            using var replay = new KisOpenApiAdapter(replayInterlock, send: static _ => true, onTick: rtck, onBook: rb, nowTicks: static () => 638_000_000_000_000_000);
            void Feed() { for (int i = 0; i < cap.Frames; i++) replay.OnFrame(cap.Bytes.AsMemory(cap.Starts[i], cap.Starts[i + 1] - cap.Starts[i])); }
            Feed();
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                Feed();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            _out.WriteLine($"[실 프레임 재주입] {cap.Frames:N0}건 · 호가 {replaySink.Books:N0} · 체결 {replaySink.Ticks:N0} · 판독 할당 {min}B");
            Assert.True(cap.Frames > 0);
            Assert.Equal(0, min);
        }

        // ═══ §3 [v12.18.31] 접속키 발급 백오프 · 재시도 폭풍 차단(가상 시계 · 모의 발급 엔드포인트) ═══

        private const long HourMicros = 3_600_000_000;

        /// <summary>모의 발급 엔드포인트(HTTP 결과를 대역 함수로 재현) + 가상 시계 1시간 구동 — 반환 = 발급 시도 시각(µs) 목록.</summary>
        private static System.Collections.Generic.List<long> SimulateApprovalHour(Func<int, string?> endpoint, ulong seed, out KisRealtimeSession rt, out int factoryCalls,
                                                                                  out KisSessionInterlock interlock)
        {
            long[] clock = { 0 };
            var calls = new System.Collections.Generic.List<long>();
            var backoff = new KisApprovalBackoff(nowMicros: () => clock[0],
                                                 delay: (d, ct) => { clock[0] += (long)d.TotalMicroseconds; return Task.CompletedTask; }, jitterSeed: seed);
            interlock = new KisSessionInterlock(null, 1, () => clock[0]);
            var adapter = new KisOpenApiAdapter(interlock);
            int factory = 0;
            var stop = new CancellationTokenSource();
            var wire = new SoakWire(BuildPool()) { Stop = stop };
            rt = new KisRealtimeSession(adapter, interlock, () => { factory++; stop.Cancel(); return wire.Next(); },   // 첫 접속 = 시뮬레이션 종료(접속 1회 확인)
                                        () =>
                                        {
                                            if (clock[0] >= HourMicros) { stop.Cancel(); return null; }
                                            calls.Add(clock[0]);
                                            return endpoint(calls.Count);
                                        }, "soakuser", vts: true, reconnectDelay: TimeSpan.Zero, approvalBackoff: backoff);
            wire.Session = rt; wire.Interlock = interlock;
            var run = rt.RunAsync(stop.Token);
            Assert.True(run.IsCompleted, "가상 시계 대기가 동기 완료되지 않음(시뮬레이션 전제 붕괴)");
            run.GetAwaiter().GetResult();
            factoryCalls = factory;
            stop.Dispose();
            return calls;
        }

        [Fact]
        public void ApprovalKey_TransientFailures_OneHour_BackoffEscalates_5_10_20_40_Then_60Cap_And_StaysUnder60Calls()
        {
            var calls = SimulateApprovalHour(_ => null, 0x9E37_79B9_7F4A_7C15UL, out var rt, out int factory, out var interlock);   // 발급 실패 연속(5xx · 네트워크)
            var gaps = new long[calls.Count - 1];
            for (int i = 1; i < calls.Count; i++) gaps[i - 1] = calls[i] - calls[i - 1];
            _out.WriteLine($"[접속키 백오프 1시간] 시도 {calls.Count}회(수정 전 실측 682회) · 간격(s) [{string.Join(',', Array.ConvertAll(gaps, g => (g / 1_000_000.0).ToString("F1")))}]");

            long[] bases = { 5_000_000, 10_000_000, 20_000_000, 40_000_000 };
            for (int i = 0; i < bases.Length; i++)                                             // 5 → 10 → 20 → 40s(+12.5~25% 지터)
                Assert.InRange(gaps[i], bases[i] + bases[i] / 8, bases[i] + bases[i] / 4);
            for (int i = bases.Length; i < gaps.Length; i++)                                    // 60s 상한(+12.5~25% 지터)
                Assert.InRange(gaps[i], 67_500_000, 75_000_000);
            Assert.True(calls.Count < 60, $"1시간 발급 시도 {calls.Count}회 ≥ 60(재시도 폭풍)");
            Assert.Equal(0, factory);                                                          // 접속키 없으면 소켓 접속 0
            Assert.True(interlock.ArmingBlocked);                                              // 미연결 = 차단 유지
            Assert.False(rt.ApprovalRejected);                                                 // 일시 실패는 래치 아님(복구 가시성 유지)
            Assert.Equal(calls.Count + 1, rt.ApprovalFailures);                                // 시뮬레이션 종료 호출 1회 포함
        }

        [Fact]
        public void ApprovalKey_HourlyCallCeiling_HoldsForAnyJitterSeed()
        {
            int worst = 0;
            for (ulong seed = 1; seed <= 64; seed++)
                worst = Math.Max(worst, SimulateApprovalHour(_ => null, seed * 0x9E37_79B9UL, out _, out _, out _).Count);
            _out.WriteLine($"[접속키 백오프] 시드 64종 최악 시도 {worst}회/시간");
            Assert.True(worst <= 57, $"최악 {worst}회 > 57(지터 하한 +12.5% 결정론 상한)");
        }

        [Theory]
        [InlineData(401)]
        [InlineData(403)]
        public void ApprovalKey_CredentialRejection_LatchesStickyAndStopsAfterOneCall(int status)
        {
            var calls = SimulateApprovalHour(_ => throw new MS2E.Infrastructure.KisApprovalRejectedException(status), 7, out var rt, out int factory, out var interlock);
            Assert.Single(calls);                                                              // 거부 1회 = 발급 시도 영구 중단
            Assert.True(rt.ApprovalRejected);
            Assert.Equal(status, rt.ApprovalBackoff.LastRejectStatus);
            Assert.Equal(1, rt.ApprovalBackoff.Rejections);
            Assert.Equal(1, rt.ApprovalFailures);
            Assert.Equal(0, factory);
            Assert.True(interlock.ArmingBlocked);
        }

        [Theory]
        [InlineData(401, true)]
        [InlineData(403, true)]
        [InlineData(429, false)]
        [InlineData(500, false)]
        [InlineData(502, false)]
        [InlineData(200, false)]
        public void ApprovalKey_RejectionClassifier_OnlyCredentialStatusesAreSticky(int status, bool sticky)
            => Assert.Equal(sticky, MS2E.Infrastructure.KisApprovalRejectedException.IsCredentialRejection(status));

        [Fact]
        public void ApprovalKey_TransientThenSuccess_ResetsBackoffAndConnects()
        {
            long[] clock = { 0 };
            var b = new KisApprovalBackoff(nowMicros: () => clock[0], jitterSeed: 3);
            Assert.Equal(0, b.RemainingMicros());                                              // 해제 표지(long.MinValue) − 시계 오버플로 회귀 방지
            Assert.Equal(0, new KisApprovalBackoff().RemainingMicros());                       // 실 Stopwatch 시계
            for (int n = 1; n <= 8; n++) b.NoteFailure();
            Assert.Equal(8, b.Consecutive);
            Assert.InRange(b.LastDelayMicros, 67_500_000, 75_000_000);
            Assert.True(b.RemainingMicros() > 0);
            b.NoteSuccess();
            Assert.Equal(0, b.Consecutive);
            Assert.Equal(0, b.RemainingMicros());
            Assert.Equal(5_000_000, b.BaseDelayMicros(1));
            Assert.Equal(40_000_000, b.BaseDelayMicros(4));
            Assert.Equal(60_000_000, b.BaseDelayMicros(5));
            Assert.Equal(60_000_000, b.BaseDelayMicros(long.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() => new KisApprovalBackoff(TimeSpan.Zero));
            Assert.Throws<ArgumentOutOfRangeException>(() => new KisApprovalBackoff(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)));

            var calls = SimulateApprovalHour(n => n <= 3 ? null : "vts-approval", 11, out var rt, out int factory, out _);   // 3회 실패 후 발급 성공 → 접속
            Assert.Equal(4, calls.Count);
            Assert.Equal(1, factory);
            Assert.Equal(1, rt.Connects);
            Assert.Equal(3, rt.ApprovalFailures);
            Assert.Equal(0, rt.ApprovalBackoff.Consecutive);                                   // 성공 = 연속 실패 리셋
        }

        [Fact]
        public void ApprovalKey_BackoffStateMachine_ZeroAlloc()
        {
            long[] clock = { 0 };
            var b = new KisApprovalBackoff(nowMicros: () => clock[0]);
            void Spin()
            {
                for (int i = 0; i < 10_000; i++)
                {
                    b.NoteFailure(); clock[0] += b.RemainingMicros();
                    if ((i & 15) == 15) b.NoteSuccess();
                }
            }
            Spin();
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                Spin();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
            }
            Assert.Equal(0, min);                                                              // ★δ=0: 백오프 계산 · 상태 전이 0B(3창 최솟값)
        }

        // ═══ §4 [v12.18.32] KIS 비표준 거부 — HTTP 200/500 + JSON 본문 코드(error_code · msg_cd) = 1회 즉시 sticky ═══

        [Theory]
        [InlineData(200, "{\"error_description\":\"유효하지 않은 token 입니다.\",\"error_code\":\"EGW00121\"}", "EGW00121")]
        [InlineData(500, "{\"error_description\":\"기간이 만료된 token 입니다.\",\"error_code\":\"EGW00123\"}", "EGW00123")]
        [InlineData(200, "{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00103\",\"msg1\":\"유효하지 않은 AppKey입니다.\"}", "EGW00103")]
        [InlineData(500, "{\"rt_cd\":\"1\",\"msg_cd\":\"OPS00001\",\"msg1\":\"인증 실패\"}", "OPS00001")]
        [InlineData(200, "{ \"msg_cd\" : \"EGW00133\" }", "EGW00133")]
        [InlineData(500, "{\"error_code\":\"EGW00105\"}", "EGW00105")]
        public void ApprovalKey_NonStandardBodyRejection_LatchesStickyAfterOneCall(int status, string body, string code)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            var ex = Assert.Throws<MS2E.Infrastructure.KisApprovalRejectedException>(() => MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(status, bytes));
            Assert.Equal(status, ex.StatusCode);
            Assert.Equal(code, ex.ErrorCode);

            var calls = SimulateApprovalHour(_ => MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(status, bytes), 7, out var rt, out int factory, out var interlock);
            Assert.Single(calls);                                                              // 본문 거부 1회 = 발급 시도 영구 중단(백오프 재시도 0)
            Assert.True(rt.ApprovalRejected);
            Assert.Equal(status, rt.ApprovalBackoff.LastRejectStatus);
            Assert.Equal(1, rt.ApprovalBackoff.Rejections);
            Assert.Equal(0, rt.ApprovalBackoff.Consecutive);                                   // 백오프 경로 미진입
            Assert.Equal(1, rt.ApprovalFailures);
            Assert.Equal(0, factory);
            Assert.True(interlock.ArmingBlocked);                                              // 미연결 = 세션 인터락 차단 유지
        }

        [Theory]
        [InlineData(500, "{\"error_code\":\"EGW00002\",\"error_description\":\"서버 에러\"}")]   // 비치명 코드 = 일시 실패
        [InlineData(500, "<html>Internal Server Error</html>")]                                   // 비JSON
        [InlineData(502, "")]
        [InlineData(200, "{\"approval_key\":\"\"}")]                                              // 빈 키
        [InlineData(200, "{\"nested\":{\"error_code\":\"EGW00121\"}}")]                          // 최상위 아님 = 래치 아님
        [InlineData(200, "{\"error_description\":\"EGW00121\"}")]                                // 코드 필드 아님
        [InlineData(200, "{\"error_code\":\"EGW0012")]                                            // 절단 본문
        public void ApprovalKey_TransientBodies_AreNotSticky_AndBackoff(int status, string body)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(body);
            Assert.Null(MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(status, bytes));
            var calls = SimulateApprovalHour(_ => MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(status, bytes), 7, out var rt, out _, out _);
            Assert.False(rt.ApprovalRejected);
            Assert.True(calls.Count > 1 && calls.Count <= 57);                                 // 일시 실패 = 백오프 재시도(상한 유지)
        }

        [Fact]
        public void ApprovalKey_SuccessBody_ParsesKey_AndConnects()
        {
            var ok = System.Text.Encoding.UTF8.GetBytes("{\"approval_key\":\"a1b2c3d4-vts\",\"rt_cd\":\"0\",\"msg_cd\":\"EGW00000\"}");
            Assert.Equal("a1b2c3d4-vts", MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(200, ok));
            Assert.Throws<MS2E.Infrastructure.KisApprovalRejectedException>(() => MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(401, ok));   // 상태 거부 우선
            var calls = SimulateApprovalHour(_ => MS2E.Infrastructure.KisLiveBroker.ParseApprovalResponse(200, ok), 7, out var rt, out int factory, out _);
            Assert.Single(calls);
            Assert.Equal(1, factory);
            Assert.False(rt.ApprovalRejected);
        }

        [Fact]
        public void ApprovalKey_BodyCodeClassifier_ZeroAlloc()
        {
            byte[] fatal = System.Text.Encoding.UTF8.GetBytes("{\"rt_cd\":\"1\",\"msg_cd\":\"EGW00123\",\"msg1\":\"기간이 만료된 token 입니다.\",\"output\":{\"x\":[1,2]}}");
            byte[] ok = System.Text.Encoding.UTF8.GetBytes("{\"approval_key\":\"a1b2c3d4-e5f6-7890-abcd-ef0123456789\",\"rt_cd\":\"0\",\"msg_cd\":\"EGW00000\"}");
            byte[] junk = System.Text.Encoding.UTF8.GetBytes("<html>500</html>");
            int acc = 0;
            void Spin()
            {
                for (int i = 0; i < 10_000; i++)
                    acc += MS2E.Infrastructure.KisApprovalRejectedException.MatchFatalBodyCode(fatal)
                         + MS2E.Infrastructure.KisApprovalRejectedException.MatchFatalBodyCode(ok);
            }
            Assert.Equal(3, MS2E.Infrastructure.KisApprovalRejectedException.MatchFatalBodyCode(fatal));
            Assert.Equal(-1, MS2E.Infrastructure.KisApprovalRejectedException.MatchFatalBodyCode(ok));
            Assert.Equal(-1, MS2E.Infrastructure.KisApprovalRejectedException.MatchFatalBodyCode(junk));   // 비JSON = JsonException 흡수(콜드 · 측정 외)
            Spin();
            long min = long.MaxValue;
            for (int w = 0; w < 3; w++)
            {
                long a = GC.GetAllocatedBytesForCurrentThread();
                Spin();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - a);
            }
            Assert.Equal(40_000 * 2, acc);                                                      // (3 + −1) × 10,000 × 4 회
            Assert.Equal(0, min);                                                              // ★δ=0: 본문 거부 코드 판독 0B(정상 · 거부 본문 모두 · 3창 최솟값)
        }
    }
}
