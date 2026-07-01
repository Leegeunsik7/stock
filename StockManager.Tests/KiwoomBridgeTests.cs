// ============================================================================
// KiwoomBridgeTests — S14 #1 키움 COM 브릿지 인프라 단위 테스트
//  · BridgeSerializer: Pack/Unpack 라운드트립, 헤더 파싱, 경계 조건
//  · StockValidator 확장: ValidateTick / ValidateFill 검증 규칙
//  · KiwoomBridgeClient: 인-프로세스 파이프 통합, 하트비트 타임아웃, 검증 게이트
//  · OrderAttestationService: ForceFreeze 추가 메서드
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    public class KiwoomBridgeTests
    {
        // ════════════════════════════════════════════════════════════════════
        // 1. BridgeSerializer — Pack/Unpack 라운드트립
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void Pack_TickPayload_RoundTrip()
        {
            var orig = new TickPayload { SymbolCode = 5930, Price = 85_000.0, Volume = 12_345, ChangeRate = 1.23, TimestampTicks = 637_000_000_000L };
            var buf  = BridgeSerializer.Pack(BridgeMessageType.RealtimeTick, in orig);
            BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out var type, out int plen);
            var restored = BridgeSerializer.Unpack<TickPayload>(buf.AsSpan(BridgeSerializer.HeaderSize, plen));

            Assert.Equal(BridgeMessageType.RealtimeTick, type);
            Assert.Equal(orig.SymbolCode,     restored.SymbolCode);
            Assert.Equal(orig.Price,          restored.Price,      precision: 10);
            Assert.Equal(orig.Volume,         restored.Volume);
            Assert.Equal(orig.ChangeRate,     restored.ChangeRate, precision: 10);
            Assert.Equal(orig.TimestampTicks, restored.TimestampTicks);
        }

        [Fact]
        public void Pack_FillPayload_RoundTrip()
        {
            var orig = new FillPayload { OrderId = 99, SymbolCode = 5930, FillPrice = 84_950.0, FillQty = 10, Side = 1, TimestampTicks = 1L };
            var buf  = BridgeSerializer.Pack(BridgeMessageType.OrderFilled, in orig);
            BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out _, out int plen);
            var restored = BridgeSerializer.Unpack<FillPayload>(buf.AsSpan(BridgeSerializer.HeaderSize, plen));

            Assert.Equal(orig.OrderId,    restored.OrderId);
            Assert.Equal(orig.FillPrice,  restored.FillPrice, precision: 10);
            Assert.Equal(orig.FillQty,    restored.FillQty);
            Assert.Equal(orig.Side,       restored.Side);
        }

        [Fact]
        public void Pack_AccountPayload_RoundTrip()
        {
            var orig = new AccountPayload { Deposit = 5_000_000L, Equity = 5_200_000L, UnfilledCount = 3L };
            var buf  = BridgeSerializer.Pack(BridgeMessageType.AccountState, in orig);
            BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out _, out int plen);
            var r = BridgeSerializer.Unpack<AccountPayload>(buf.AsSpan(BridgeSerializer.HeaderSize, plen));

            Assert.Equal(orig.Deposit,       r.Deposit);
            Assert.Equal(orig.Equity,        r.Equity);
            Assert.Equal(orig.UnfilledCount, r.UnfilledCount);
        }

        [Fact]
        public void PackOrderRequest_RoundTrip()
        {
            var orig = new OrderRequest { Type = OrderRequestType.Buy, OrderId = 42L, SymbolCode = 66570L, Price = 35_000.0, Quantity = 5L, GenomeId = 777L };
            var buf  = BridgeSerializer.PackOrderRequest(in orig);
            BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out var type, out int plen);
            var r = BridgeSerializer.Unpack<OrderRequest>(buf.AsSpan(BridgeSerializer.HeaderSize, plen));

            Assert.Equal(BridgeMessageType.OrderRequest, type);
            Assert.Equal(orig.Type,       r.Type);
            Assert.Equal(orig.OrderId,    r.OrderId);
            Assert.Equal(orig.SymbolCode, r.SymbolCode);
            Assert.Equal(orig.Price,      r.Price,  precision: 10);
            Assert.Equal(orig.Quantity,   r.Quantity);
            Assert.Equal(orig.GenomeId,   r.GenomeId);
        }

        [Fact]
        public void PackHeartbeat_RoundTrip_SequencePreserved()
        {
            long seq = 12_345_678L;
            var  buf = BridgeSerializer.PackHeartbeat(seq);
            BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out var type, out int plen);
            long restored = BitConverter.ToInt64(buf, BridgeSerializer.HeaderSize);

            Assert.Equal(BridgeMessageType.Heartbeat, type);
            Assert.Equal(sizeof(long), plen);
            Assert.Equal(seq, restored);
        }

        // ════════════════════════════════════════════════════════════════════
        // 1b. Pack(Span) — δ=0 무할당 오버로드(v4.82.0): byte[] 버전과 바이트 동일 + 경계
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void PackSpan_MatchesByteArray_Tick()
        {
            var tick = new TickPayload { SymbolCode = 5930, Price = 85_000.0, Volume = 12_345, ChangeRate = 1.23, TimestampTicks = 637_000_000_000L };
            byte[] expected = BridgeSerializer.Pack(BridgeMessageType.RealtimeTick, in tick);

            Span<byte> buf = stackalloc byte[BridgeSerializer.MaxFrameSize];
            int n = BridgeSerializer.Pack(buf, BridgeMessageType.RealtimeTick, in tick);

            Assert.Equal(expected.Length, n);
            Assert.True(buf[..n].SequenceEqual(expected));   // 바이트 동일
        }

        [Fact]
        public void PackHeartbeatSpan_MatchesByteArray()
        {
            byte[] expected = BridgeSerializer.PackHeartbeat(987_654L);
            Span<byte> buf = stackalloc byte[BridgeSerializer.MaxFrameSize];
            int n = BridgeSerializer.PackHeartbeat(buf, 987_654L);
            Assert.Equal(expected.Length, n);
            Assert.True(buf[..n].SequenceEqual(expected));
        }

        [Fact]
        public void PackSpan_BufferTooSmall_Throws()
        {
            var tick = new TickPayload { SymbolCode = 5930, Price = 1.0 };
            Assert.Throws<ArgumentException>(() =>
            {
                Span<byte> tiny = stackalloc byte[BridgeSerializer.HeaderSize];   // 페이로드 공간 없음
                BridgeSerializer.Pack(tiny, BridgeMessageType.RealtimeTick, in tick);
            });
        }

        [Fact]
        public void PackSpan_ReusedBuffer_IndependentFrames()
        {
            // 재사용 버퍼(프록시 hot path 모사) — 소비 후 재pack 시 프레임이 서로 오염되지 않음.
            Span<byte> buf = stackalloc byte[BridgeSerializer.MaxFrameSize];

            var tick = new TickPayload { SymbolCode = 5930, Price = 100.0, Volume = 1, ChangeRate = 0, TimestampTicks = 1 };
            int n1 = BridgeSerializer.Pack(buf, BridgeMessageType.RealtimeTick, in tick);
            BridgeSerializer.TryParseHeader(buf[..BridgeSerializer.HeaderSize], out var t1, out int p1);
            var r1 = BridgeSerializer.Unpack<TickPayload>(buf.Slice(BridgeSerializer.HeaderSize, p1));

            var fill = new FillPayload { OrderId = 7, SymbolCode = 660, FillPrice = 5.0, FillQty = 3, Side = -1, TimestampTicks = 9 };
            int n2 = BridgeSerializer.Pack(buf, BridgeMessageType.OrderFilled, in fill);
            BridgeSerializer.TryParseHeader(buf[..BridgeSerializer.HeaderSize], out var t2, out int p2);
            var r2 = BridgeSerializer.Unpack<FillPayload>(buf.Slice(BridgeSerializer.HeaderSize, p2));

            Assert.Equal(BridgeMessageType.RealtimeTick, t1);
            Assert.Equal(5930L, r1.SymbolCode);
            Assert.Equal(BridgeMessageType.OrderFilled, t2);
            Assert.Equal(7L, r2.OrderId);
            Assert.Equal(-1, r2.Side);
            Assert.Equal(BridgeSerializer.HeaderSize + 40, n1);   // Tick 40B
            Assert.Equal(BridgeSerializer.HeaderSize + 48, n2);   // Fill 48B — 각 헤더가 자기 길이를 정확 기술
        }

        // ════════════════════════════════════════════════════════════════════
        // 2. TryParseHeader — 경계 조건
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void TryParseHeader_ValidTick_Succeeds()
        {
            var buf = BridgeSerializer.Pack(BridgeMessageType.RealtimeTick, new TickPayload { Price = 1.0 });
            var ok  = BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out var t, out int plen);
            Assert.True(ok);
            Assert.Equal(BridgeMessageType.RealtimeTick, t);
            Assert.Equal(Unsafe.SizeOf<TickPayload>(), plen);
        }

        [Fact]
        public void TryParseHeader_ShortBuffer_ReturnsFalse()
        {
            var ok = BridgeSerializer.TryParseHeader(new byte[3], out _, out _);
            Assert.False(ok);
        }

        [Fact]
        public void TryParseHeader_InvalidType_ReturnsFalse()
        {
            var buf = new byte[BridgeSerializer.HeaderSize];
            BitConverter.TryWriteBytes(buf.AsSpan(0), 99);  // 99는 정의되지 않은 타입
            BitConverter.TryWriteBytes(buf.AsSpan(4), 8);
            Assert.False(BridgeSerializer.TryParseHeader(buf, out _, out _));
        }

        [Fact]
        public void FormatSymbol_Pads_ToSixDigits()
        {
            Assert.Equal("005930", BridgeSerializer.FormatSymbol(5930L));
            Assert.Equal("000001", BridgeSerializer.FormatSymbol(1L));
            Assert.Equal("999999", BridgeSerializer.FormatSymbol(999999L));
        }

        // ════════════════════════════════════════════════════════════════════
        // 3. StockValidator 확장 — ValidateTick / ValidateFill
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void ValidateTick_ValidData_Passes()
        {
            var t = new TickPayload { SymbolCode = 5930, Price = 85_000, Volume = 1000 };
            Assert.True(StockValidator.ValidateTick(t).IsValid);
        }

        [Fact]
        public void ValidateTick_NegativePrice_Fails()
        {
            var t = new TickPayload { SymbolCode = 5930, Price = -1.0, Volume = 0 };
            Assert.False(StockValidator.ValidateTick(t).IsValid);
        }

        [Fact]
        public void ValidateTick_ZeroPrice_Fails()
        {
            var t = new TickPayload { SymbolCode = 5930, Price = 0.0, Volume = 100 };
            Assert.False(StockValidator.ValidateTick(t).IsValid);
        }

        [Fact]
        public void ValidateTick_ZeroSymbolCode_Fails()
        {
            var t = new TickPayload { SymbolCode = 0, Price = 10_000 };
            Assert.False(StockValidator.ValidateTick(t).IsValid);
        }

        [Fact]
        public void ValidateTick_SymbolCodeOverMax_Fails()
        {
            var t = new TickPayload { SymbolCode = 1_000_000, Price = 100 };
            Assert.False(StockValidator.ValidateTick(t).IsValid);
        }

        [Fact]
        public void ValidateFill_ValidBuy_Passes()
        {
            var f = new FillPayload { SymbolCode = 5930, FillPrice = 85_000, FillQty = 10, Side = 1 };
            Assert.True(StockValidator.ValidateFill(f).IsValid);
        }

        [Fact]
        public void ValidateFill_InvalidSide_Fails()
        {
            var f = new FillPayload { SymbolCode = 5930, FillPrice = 85_000, FillQty = 10, Side = 0 };
            Assert.False(StockValidator.ValidateFill(f).IsValid);
        }

        [Fact]
        public void ValidateFill_NegativeFillPrice_Fails()
        {
            var f = new FillPayload { SymbolCode = 5930, FillPrice = -1.0, FillQty = 5, Side = -1 };
            Assert.False(StockValidator.ValidateFill(f).IsValid);
        }

        // ════════════════════════════════════════════════════════════════════
        // 4. OrderAttestationService.ForceFreeze
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void ForceFreeze_SetsEmergencyFreezeMode()
        {
            var svc = new OrderAttestationService();
            Assert.Equal(GatingMode.Normal, svc.Mode);
            svc.ForceFreeze("테스트 강제 동결");
            Assert.Equal(GatingMode.EmergencyFreeze, svc.Mode);
        }

        [Fact]
        public void ForceFreeze_LogsEntry()
        {
            var svc = new OrderAttestationService();
            svc.ForceFreeze("파이프 타임아웃");
            Assert.Single(svc.AuditLog);
            Assert.False(svc.AuditLog[0].Passed);
            Assert.Contains("파이프", svc.AuditLog[0].Note);
        }

        // ════════════════════════════════════════════════════════════════════
        // 5. KiwoomBridgeClient — 인-프로세스 파이프 통합 테스트
        // ════════════════════════════════════════════════════════════════════

        private static string UniquePipe() => $"ms2e_test_{Guid.NewGuid():N}";

        // 서버 측에서 정확히 N바이트 읽는 헬퍼
        private static async Task<bool> ReadExactAsync(PipeStream pipe, byte[] buf, CancellationToken ct = default)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = await pipe.ReadAsync(buf.AsMemory(total), ct);
                if (n == 0) return false;
                total += n;
            }
            return true;
        }

        [Fact]
        public async Task BridgeClient_ValidTick_FiresTickReceived()
        {
            var pipeName = UniquePipe();
            var tcs      = new TaskCompletionSource<TickPayload>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();

                var tick = new TickPayload { SymbolCode = 5930, Price = 85_000.0, Volume = 1_000, ChangeRate = 1.5, TimestampTicks = 1L };
                await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.RealtimeTick, in tick));
                await srv.FlushAsync();
                await Task.Delay(500);  // 클라이언트가 읽을 때까지 대기
            });

            using var client = new KiwoomBridgeClient(pipeName: pipeName, heartbeatTimeoutMs: 10_000);
            client.TickReceived += t => tcs.TrySetResult(t);
            await client.ConnectAsync();
            var loopTask = client.StartAsync();

            var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(5930L,   received.SymbolCode);
            Assert.Equal(85_000.0, received.Price, precision: 8);
            Assert.Equal(1_000L,  received.Volume);

            client.Dispose();
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task BridgeClient_InvalidTick_Dropped_EventNotFired()
        {
            var pipeName = UniquePipe();
            bool fired   = false;

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();

                var badTick = new TickPayload { SymbolCode = 5930, Price = -100.0 };  // 음수 가격
                await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.RealtimeTick, in badTick));
                await srv.FlushAsync();
                await Task.Delay(400);
            });

            using var client = new KiwoomBridgeClient(pipeName: pipeName, heartbeatTimeoutMs: 10_000);
            client.TickReceived += _ => fired = true;
            await client.ConnectAsync();
            var loopTask = client.StartAsync();

            await Task.Delay(500);  // 충분히 대기
            Assert.False(fired, "음수 가격 틱은 이벤트를 발행해서는 안 됩니다.");

            client.Dispose();
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task BridgeClient_HeartbeatTimeout_TriggersProxyLost()
        {
            var pipeName = UniquePipe();
            var proxyLostTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                await Task.Delay(3_000);  // 하트비트 없이 연결만 유지
            });

            using var client = new KiwoomBridgeClient(pipeName: pipeName, heartbeatTimeoutMs: 200);
            client.ProxyLost += () => proxyLostTcs.TrySetResult(true);
            await client.ConnectAsync();
            var loopTask = client.StartAsync();

            var fired = await proxyLostTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fired);
            Assert.False(client.IsProxyAlive);

            client.Dispose();
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public void BridgeClient_HeartbeatReceived_PreventsTimeout()
        {
            // ★work-bounded 결정론(부하 무관): 실파이프·실타이머·Task.Delay·서버태스크를 전부 제거하고,
            //   하트비트 타임아웃 '판정 로직'을 주입 클럭(가상 시각 ticks)으로 직접 검증한다. 여기서 호출하는
            //   MarkHeartbeat / IsHeartbeatTimedOut 은 production 의 ReadLoop(하트비트 수신 시 MarkHeartbeat)와
            //   HeartbeatWatchAsync(IsHeartbeatTimedOut 로 판정)가 그대로 쓰는 '단일 결정 경로'다 → 우회 아님.
            //   (과거: 100ms송신/500ms타임아웃/600ms 고정대기 → 25% 플레이키. 이후 5000ms 로 늘려도 잔류 —
            //    부하 의존 타이밍은 인플레이션으로 못 잡는다. 가상 클럭으로 시간을 '결정'해야 0% 가 된다.)
            const long MS = TimeSpan.TicksPerMillisecond;
            using var client = new KiwoomBridgeClient(pipeName: "test-virtual", heartbeatTimeoutMs: 500);

            // 하트비트 수신 직후(t=0). 타임아웃 이내면 살아있음.
            client.MarkHeartbeat(nowTicks: 0);
            Assert.False(client.IsHeartbeatTimedOut(400 * MS), "경과 400ms < 500ms → 타임아웃 아님");
            Assert.False(client.IsHeartbeatTimedOut(500 * MS), "경과 정확히 500ms 는 초과(>) 아님(경계)");

            // ★핵심 불변(PreventsTimeout): 새 하트비트가 들어오면 타임아웃 카운터가 리셋되어 다시 살아난다.
            client.MarkHeartbeat(nowTicks: 450 * MS);
            Assert.False(client.IsHeartbeatTimedOut(900 * MS), "리셋 후 경과 900-450=450ms < 500ms → 살아있음");

            // 반대 케이스: 하트비트 정체 시엔 타임아웃을 발화(판정이 무력하지 않음을 보장).
            Assert.True(client.IsHeartbeatTimedOut(961 * MS), "경과 961-450=511ms > 500ms → 타임아웃");
        }

        [Fact]
        public async Task BridgeClient_SendOrder_ReceivedByServer()
        {
            var pipeName = UniquePipe();
            var reqTcs   = new TaskCompletionSource<OrderRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();

                var header = new byte[BridgeSerializer.HeaderSize];
                if (!await ReadExactAsync(srv, header)) return;
                if (!BridgeSerializer.TryParseHeader(header, out var type, out int plen)) return;
                if (type != BridgeMessageType.OrderRequest) return;

                var payload = new byte[plen];
                if (!await ReadExactAsync(srv, payload)) return;
                reqTcs.TrySetResult(BridgeSerializer.Unpack<OrderRequest>(payload));
                await Task.Delay(300);
            });

            using var client = new KiwoomBridgeClient(pipeName: pipeName, heartbeatTimeoutMs: 10_000);
            await client.ConnectAsync();
            var loopTask = client.StartAsync();

            var order = new OrderRequest { Type = OrderRequestType.Buy, OrderId = 7L, SymbolCode = 66570L, Price = 35_000.0, Quantity = 3L, GenomeId = 42L };
            await client.SendOrderAsync(order);

            var received = await reqTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(OrderRequestType.Buy, received.Type);
            Assert.Equal(7L,       received.OrderId);
            Assert.Equal(66570L,   received.SymbolCode);
            Assert.Equal(35_000.0, received.Price, precision: 8);
            Assert.Equal(3L,       received.Quantity);
            Assert.Equal(42L,      received.GenomeId);

            client.Dispose();
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        // ════════════════════════════════════════════════════════════════════
        // 6. 타입·크기 불변식
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void BridgeEnvelope_IsValueType()
            => Assert.True(typeof(BridgeEnvelope).IsValueType);

        [Fact]
        public void TickPayload_SizeIs_40B()
            => Assert.Equal(40, Unsafe.SizeOf<TickPayload>());

        [Fact]
        public void FillPayload_SizeIs_48B()
            => Assert.Equal(48, Unsafe.SizeOf<FillPayload>());

        [Fact]
        public void AccountPayload_SizeIs_24B()
            => Assert.Equal(24, Unsafe.SizeOf<AccountPayload>());

        [Fact]
        public void OrderRequest_SizeIs_48B()
            => Assert.Equal(48, Unsafe.SizeOf<OrderRequest>());

        // ════════════════════════════════════════════════════════════════════
        // 7. IPC 핑퐁(v4.83.0) — 클라 Ping → 프록시 Pong 생존성 + 무응답 → ProxyLost 연쇄 동결
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void PackSeq_PingPong_RoundTrips()
        {
            var buf = BridgeSerializer.PackSeq(BridgeMessageType.Ping, 4242L);
            Assert.True(BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize), out var t, out int plen));
            Assert.Equal(BridgeMessageType.Ping, t);
            Assert.Equal(sizeof(long), plen);
            Assert.Equal(4242L, BitConverter.ToInt64(buf, BridgeSerializer.HeaderSize));
        }

        [Fact]
        public async Task PingPong_ProxyPongs_ClientStaysAlive()
        {
            var pipeName = UniquePipe();
            int pings = 0;

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                var header = new byte[BridgeSerializer.HeaderSize];
                while (await ReadExactAsync(srv, header))
                {
                    if (!BridgeSerializer.TryParseHeader(header, out var type, out int plen)) break;
                    var payload = plen > 0 ? new byte[plen] : Array.Empty<byte>();
                    if (plen > 0 && !await ReadExactAsync(srv, payload)) break;
                    if (type == BridgeMessageType.Ping)   // 프록시 역할: Pong 에코
                    {
                        Interlocked.Increment(ref pings);
                        await srv.WriteAsync(BridgeSerializer.PackSeq(BridgeMessageType.Pong, BitConverter.ToInt64(payload)));
                        await srv.FlushAsync();
                    }
                }
            });

            var att = new OrderAttestationService();
            // heartbeatTimeoutMs(150) < 관찰창(400ms) → Pong 이 계속 갱신해야만 생존. Ping 20ms.
            using var client = new KiwoomBridgeClient(attestation: att, pipeName: pipeName,
                                                      heartbeatTimeoutMs: 150, pingIntervalMs: 20);
            bool proxyLost = false;
            client.ProxyLost += () => proxyLost = true;
            await client.ConnectAsync();
            var loop = client.StartAsync();

            await Task.Delay(400);

            Assert.True(Volatile.Read(ref pings) >= 1, $"클라가 Ping 을 보내야 함(pings={pings})");
            Assert.True(client.IsProxyAlive, "Pong 수신으로 생존 유지되어야 함");
            Assert.False(proxyLost);
            Assert.Equal(GatingMode.Normal, att.Mode);

            client.Dispose();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task PingPong_ProxyStopsResponding_ProxyLost_RelayFreezes()
        {
            var pipeName = UniquePipe();
            var proxyLostTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                await Task.Delay(2_000);   // ★Ping 을 받아도 Pong 을 주지 않음(응답 정지)
            });

            var att = new OrderAttestationService();
            // 프로덕션 배선 모사: 브리지엔 att 미주입, KiwoomFreezeRelay 가 ProxyLost → ForceFreeze 연쇄.
            using var client = new KiwoomBridgeClient(attestation: null, pipeName: pipeName,
                                                      heartbeatTimeoutMs: 150, pingIntervalMs: 20);
            using var relay = new KiwoomFreezeRelay(att, client);
            relay.Attach();
            client.ProxyLost += () => proxyLostTcs.TrySetResult(true);

            await client.ConnectAsync();
            var loop = client.StartAsync();

            Assert.True(await proxyLostTcs.Task.WaitAsync(TimeSpan.FromSeconds(3)), "무응답 → ProxyLost 발화 필요");
            Assert.True(relay.IsFrozen, "ProxyLost → 릴레이 멱등 동결");
            Assert.Equal(GatingMode.EmergencyFreeze, att.Mode);   // 연쇄 EmergencyFreeze
            Assert.False(client.IsProxyAlive);

            client.Dispose();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task PingPong_RecordsRttAndCounters()
        {
            var pipeName = UniquePipe();
            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                var header = new byte[BridgeSerializer.HeaderSize];
                while (await ReadExactAsync(srv, header))
                {
                    if (!BridgeSerializer.TryParseHeader(header, out var type, out int plen)) break;
                    var payload = plen > 0 ? new byte[plen] : Array.Empty<byte>();
                    if (plen > 0 && !await ReadExactAsync(srv, payload)) break;
                    if (type == BridgeMessageType.Ping)   // 프록시 역할: seq 에코 Pong
                    {
                        await srv.WriteAsync(BridgeSerializer.PackSeq(BridgeMessageType.Pong, BitConverter.ToInt64(payload)));
                        await srv.FlushAsync();
                    }
                }
            });

            using var client = new KiwoomBridgeClient(pipeName: pipeName, heartbeatTimeoutMs: 2000, pingIntervalMs: 20);
            await client.ConnectAsync();
            var loop = client.StartAsync();
            await Task.Delay(300);   // ~15 pings

            Assert.True(client.TotalPingSent    >= 1, $"Ping 송신 계상 필요(sent={client.TotalPingSent})");
            Assert.True(client.TotalPongReceived >= 1, $"Pong 수신 계상 필요(recv={client.TotalPongReceived})");
            Assert.True(client.CurrentIpcRttTicks >= 0, "RTT 는 음수일 수 없음");
            Assert.True(client.PeakIpcRttTicks   >= client.CurrentIpcRttTicks || client.CurrentIpcRttTicks == 0,
                        "Peak 은 Current 이상");

            client.Dispose();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public void ExecutionBridge_CreateFast_UsesFiftyMsClassHealthCheck()
        {
            Assert.Equal(20, ExecutionBridgeBroker.FastPingIntervalMs);
            Assert.Equal(50, ExecutionBridgeBroker.FastHeartbeatTimeoutMs);
            var att = new OrderAttestationService();
            var (broker, bridge) = ExecutionBridgeBroker.CreateFast(att, UniquePipe());
            Assert.NotNull(broker);
            Assert.NotNull(bridge);
            broker.Dispose();
            bridge.Dispose();
        }
    }
}
