// ============================================================================
// LiveTradingTests — S14 #2 실거래 체결 엔진 단위·통합 테스트 (17개)
//
//  대상 클래스:
//   · LiveAuditService   — 원장 등록, 체결 처리, 상태 전환 (10개)
//   · OrderAttestationService.ProcessFill — 감사 비교 로직 (2개)
//   · ExecutionBridgeBroker — Place, HeldQty, EmergencyFreeze (5개)
//
//  브로커 파이프 테스트: 인-프로세스 NamedPipe 사용 (KiwoomBridgeTests와 동일 패턴).
// ============================================================================
using System;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace StockManager.Tests
{
    public class LiveTradingTests
    {
        private static string UniquePipe(
            [CallerMemberName] string caller = "",
            [CallerLineNumber] int    line   = 0)
            => $"LT_{caller}_{line}_{Guid.NewGuid():N}";

        // ════════════════════════════════════════════════════════════════════
        // §1 LiveAuditService — 원장 관리 & 체결 처리
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void RegisterOrder_CreatesPendingOrder()
        {
            var svc = new LiveAuditService(new OrderAttestationService());
            svc.RegisterOrder(1L, "005930", PaperSide.Buy, PaperKind.Limit, 85_000.0, 10L);

            var order = svc.TryGetPending(1L);
            Assert.NotNull(order);
            Assert.Equal("005930",         order!.Symbol);
            Assert.Equal(PaperSide.Buy,    order.Side);
            Assert.Equal(85_000.0,         order.ExpectedPrice);
            Assert.Equal(10L,              order.Quantity);
            Assert.Equal(PendingOrderState.Pending, order.State);
        }

        [Fact]
        public void ProcessFill_ExactMatch_Passed_NoFreeze()
        {
            var attestation = new OrderAttestationService();
            var svc = new LiveAuditService(attestation);
            svc.RegisterOrder(10L, "005930", PaperSide.Buy, PaperKind.Limit, 85_000.0, 10L);

            var fill = new FillPayload
            {
                OrderId        = 10L,
                SymbolCode     = 5930L,
                FillPrice      = 85_000.0,
                FillQty        = 10L,
                Side           = 1,
                TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            Assert.True(result.Passed);
            Assert.Equal(GatingMode.Normal, attestation.Mode);
            Assert.Equal(0L, result.QtyDelta);
            Assert.Equal(PendingOrderState.Filled, svc.TryGetPending(10L)!.State);
        }

        [Fact]
        public void ProcessFill_PriceWithinTolerance_Passed()
        {
            // 0.3% 오차, 0.5% 허용
            var svc = new LiveAuditService(new OrderAttestationService())
                      { PriceTolerance = 0.005 };
            svc.RegisterOrder(11L, "005930", PaperSide.Buy, PaperKind.Limit, 85_000.0, 10L);

            var fill = new FillPayload
            {
                OrderId = 11L, SymbolCode = 5930L,
                FillPrice = 85_255.0,  // +0.3%
                FillQty   = 10L, Side = 1, TimestampTicks = 1L,
            };
            Assert.True(svc.ProcessFill(fill).Passed);
        }

        [Fact]
        public void ProcessFill_PriceExceedsTolerance_TriggersFreeze()
        {
            var attestation = new OrderAttestationService();
            var svc = new LiveAuditService(attestation) { PriceTolerance = 0.005 };
            svc.RegisterOrder(12L, "005930", PaperSide.Buy, PaperKind.Limit, 85_000.0, 10L);

            var fill = new FillPayload
            {
                OrderId = 12L, SymbolCode = 5930L,
                FillPrice = 86_000.0,  // +1.18% > 0.5%
                FillQty = 10L, Side = 1, TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, attestation.Mode);
            Assert.True(result.PriceDeltaPct > 0.005);
        }

        [Fact]
        public void ProcessFill_InvalidFillData_TriggersFreeze()
        {
            var attestation = new OrderAttestationService();
            var svc = new LiveAuditService(attestation);
            svc.RegisterOrder(13L, "005930", PaperSide.Buy, PaperKind.Limit, 85_000.0, 10L);

            // 음수 FillPrice → StockValidator 거부
            var fill = new FillPayload
            {
                OrderId = 13L, SymbolCode = 5930L,
                FillPrice = -1.0, FillQty = 10L, Side = 1, TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, attestation.Mode);
        }

        [Fact]
        public void ProcessFill_SideMismatch_TriggersFreeze()
        {
            var attestation = new OrderAttestationService();
            var svc = new LiveAuditService(attestation);
            svc.RegisterOrder(14L, "005930", PaperSide.Buy, PaperKind.Limit, 85_000.0, 10L);

            var fill = new FillPayload
            {
                OrderId = 14L, SymbolCode = 5930L,
                FillPrice = 85_000.0, FillQty = 10L,
                Side = -1,  // 매도 체결 — 매수 예상과 불일치
                TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, attestation.Mode);
        }

        [Fact]
        public void ProcessFill_MarketOrder_SkipsPriceCheck()
        {
            // 시장가 주문: ExpectedPrice = 0, 체결가와 상관없이 통과
            var attestation = new OrderAttestationService();
            var svc = new LiveAuditService(attestation);
            svc.RegisterOrder(15L, "005930", PaperSide.Buy, PaperKind.Market, 0.0, 10L);

            var fill = new FillPayload
            {
                OrderId = 15L, SymbolCode = 5930L,
                FillPrice = 99_999.0,  // 가격과 무관
                FillQty = 10L, Side = 1, TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            Assert.True(result.Passed);
            Assert.Equal(0.0, result.PriceDeltaPct);
            Assert.Equal(GatingMode.Normal, attestation.Mode);
        }

        [Fact]
        public void ProcessFill_UnknownOrderId_DoesNotFreeze()
        {
            var attestation = new OrderAttestationService();
            var svc = new LiveAuditService(attestation);

            var fill = new FillPayload
            {
                OrderId = 9999L, SymbolCode = 5930L,
                FillPrice = 85_000.0, FillQty = 5L, Side = 1, TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            // 알 수 없는 주문 → Passed=true (무시), FreezE 안됨
            Assert.True(result.Passed);
            Assert.Equal(GatingMode.Normal, attestation.Mode);
        }

        [Fact]
        public void ProcessFill_PartialFill_UpdatesState()
        {
            var svc = new LiveAuditService(new OrderAttestationService());
            svc.RegisterOrder(16L, "005930", PaperSide.Sell, PaperKind.Limit, 85_000.0, 20L);

            var fill = new FillPayload
            {
                OrderId = 16L, SymbolCode = 5930L,
                FillPrice = 85_000.0,
                FillQty = 10L,  // 절반만 체결
                Side = -1, TimestampTicks = 1L,
            };
            var result = svc.ProcessFill(fill);

            Assert.True(result.Passed);
            Assert.Equal(-10L, result.QtyDelta);  // 10주 부족
            Assert.Equal(PendingOrderState.PartialFill, svc.TryGetPending(16L)!.State);
            Assert.Equal(10L, svc.TryGetPending(16L)!.FilledQty);
        }

        [Fact]
        public void CancelOrder_UpdatesState()
        {
            var svc = new LiveAuditService(new OrderAttestationService());
            svc.RegisterOrder(17L, "000660", PaperSide.Buy, PaperKind.Limit, 100_000.0, 5L);

            bool cancelled = svc.CancelOrder(17L);

            Assert.True(cancelled);
            Assert.Equal(PendingOrderState.Cancelled, svc.TryGetPending(17L)!.State);
        }

        // ════════════════════════════════════════════════════════════════════
        // §2 OrderAttestationService.ProcessFill — 감사 비교 직접 테스트
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void AttestationProcessFill_ValidFill_LogsAuditEntry()
        {
            var attestation = new OrderAttestationService();
            var pending = new PendingOrder
            {
                OrderId = 20L, Symbol = "005930", Side = PaperSide.Buy,
                Kind = PaperKind.Limit, ExpectedPrice = 85_000.0, Quantity = 10L,
                SubmittedAt = DateTime.UtcNow,
            };

            var fill = new FillPayload
            {
                OrderId = 20L, SymbolCode = 5930L,
                FillPrice = 85_000.0, FillQty = 10L, Side = 1, TimestampTicks = 1L,
            };
            var result = attestation.ProcessFill(fill, pending);

            Assert.True(result.Passed);
            // 감사 로그 엔트리 기록됨
            var log = attestation.AuditLog;
            Assert.NotEmpty(log);
            Assert.True(log[0].Passed);
        }

        [Fact]
        public void AttestationProcessFill_Overfill_TriggersFreeze()
        {
            var attestation = new OrderAttestationService();
            var pending = new PendingOrder
            {
                OrderId = 21L, Symbol = "005930", Side = PaperSide.Buy,
                Kind = PaperKind.Limit, ExpectedPrice = 85_000.0, Quantity = 10L,
                SubmittedAt = DateTime.UtcNow,
            };

            var fill = new FillPayload
            {
                OrderId = 21L, SymbolCode = 5930L,
                FillPrice = 85_000.0, FillQty = 15L,  // 과체결
                Side = 1, TimestampTicks = 1L,
            };
            var result = attestation.ProcessFill(fill, pending);

            Assert.False(result.Passed);
            Assert.Equal(GatingMode.EmergencyFreeze, attestation.Mode);
            Assert.True(result.QtyDelta > 0);
        }

        // ════════════════════════════════════════════════════════════════════
        // §3 ExecutionBridgeBroker — Place, HeldQty, EmergencyFreeze
        // ════════════════════════════════════════════════════════════════════

        [Fact]
        public void ExecutionBridgeBroker_Place_InvalidSymbol_ReturnsFalse()
        {
            var attestation = new OrderAttestationService();
            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation, UniquePipe());
            using (broker) using (bridge)
            {
                // 알파 기호는 KSE 코드가 아님 → ParseSymbol("AAPL") = 0 → 거부
                var (ok, msg) = broker.Place(
                    PaperAsset.Stock, "AAPL", "Apple", PaperSide.Buy,
                    PaperKind.Limit, 1m, 180m, 180m);

                Assert.False(ok);
                Assert.Contains("종목코드", msg);
            }
        }

        [Fact]
        public void ExecutionBridgeBroker_Place_EmergencyFreeze_Throws()
        {
            var attestation = new OrderAttestationService();
            attestation.ForceFreeze("테스트 강제 동결");
            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation, UniquePipe());
            using (broker) using (bridge)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                                 PaperSide.Buy, PaperKind.Limit, 10m, 85_000m, 85_000m));
            }
        }

        [Fact]
        public void ExecutionBridgeBroker_Place_CoinAsset_ReturnsFalse()
        {
            var attestation = new OrderAttestationService();
            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation, UniquePipe());
            using (broker) using (bridge)
            {
                var (ok, msg) = broker.Place(
                    PaperAsset.Coin, "BTC", "Bitcoin",
                    PaperSide.Buy, PaperKind.Market, 1m, 0m, 80_000_000m);

                Assert.False(ok);
                Assert.Contains("주식", msg);
            }
        }

        [Fact]
        public async Task ExecutionBridgeBroker_Place_SendsOrderRequest_ViaPipe()
        {
            var pipeName    = UniquePipe();
            var attestation = new OrderAttestationService();
            var orderTcs    = new TaskCompletionSource<OrderRequest>(
                                  TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();

                // 헤더 + OrderRequest 읽기
                int reqSize = BridgeSerializer.HeaderSize + Unsafe.SizeOf<OrderRequest>();
                var buf = new byte[reqSize];
                await ReadExact(srv, buf);

                BridgeSerializer.TryParseHeader(buf.AsSpan(0, BridgeSerializer.HeaderSize),
                    out _, out int plen);
                var req = BridgeSerializer.Unpack<OrderRequest>(
                    buf.AsSpan(BridgeSerializer.HeaderSize, plen));
                orderTcs.TrySetResult(req);
                await Task.Delay(2_000);
            });

            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation, pipeName);
            await bridge.ConnectAsync();
            var loopTask = bridge.StartAsync();

            var (ok, idStr) = broker.Place(
                PaperAsset.Stock, "005930", "삼성전자",
                PaperSide.Buy, PaperKind.Limit,
                qty: 10m, limitPrice: 85_000m, currentPrice: 85_000m);

            Assert.True(ok);

            var req = await orderTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(OrderRequestType.Buy, req.Type);
            Assert.Equal(5930L,     req.SymbolCode);
            Assert.Equal(85_000.0,  req.Price, precision: 8);
            Assert.Equal(10L,       req.Quantity);
            Assert.Equal(long.Parse(idStr), req.OrderId);

            broker.Dispose();
            bridge.Dispose();
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task ExecutionBridgeBroker_FillReceived_FullFill_UpdatesHoldings()
        {
            var pipeName    = UniquePipe();
            var attestation = new OrderAttestationService();
            var fillSentTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverTask = Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();

                // Place()가 보낸 OrderRequest 읽기
                int reqSize = BridgeSerializer.HeaderSize + Unsafe.SizeOf<OrderRequest>();
                var reqBuf  = new byte[reqSize];
                await ReadExact(srv, reqBuf);
                BridgeSerializer.TryParseHeader(reqBuf.AsSpan(0, BridgeSerializer.HeaderSize),
                    out _, out int plen);
                var req = BridgeSerializer.Unpack<OrderRequest>(
                    reqBuf.AsSpan(BridgeSerializer.HeaderSize, plen));

                // 체결 페이로드 반환
                var fill = new FillPayload
                {
                    OrderId        = req.OrderId,
                    SymbolCode     = req.SymbolCode,
                    FillPrice      = req.Price,
                    FillQty        = req.Quantity,
                    Side           = 1,
                    TimestampTicks = 1L,
                };
                await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.OrderFilled, in fill));
                await srv.FlushAsync();
                fillSentTcs.SetResult();
                await Task.Delay(2_000);
            });

            var (broker, bridge) = ExecutionBridgeBroker.Create(attestation, pipeName);
            await bridge.ConnectAsync();
            var loopTask = bridge.StartAsync();

            broker.Place(PaperAsset.Stock, "005930", "삼성전자",
                         PaperSide.Buy, PaperKind.Limit,
                         10m, 85_000m, 85_000m);

            await fillSentTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
            // ★디스패치 루프가 체결을 보유에 반영할 때까지 조건 폴링(고정 400ms 대기 제거 — 부하 하 미반영 Actual:0 플레이키 차단).
            decimal held = 0m;
            var dl = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < dl) { held = broker.HeldQty(PaperAsset.Stock, "005930"); if (held == 10m) break; await Task.Delay(20); }
            Assert.Equal(10m, held);

            broker.Dispose();
            bridge.Dispose();
            try { await loopTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        // ── 헬퍼 ──────────────────────────────────────────────────────────────

        private static async Task ReadExact(NamedPipeServerStream pipe, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = await pipe.ReadAsync(buf.AsMemory(total));
                if (n == 0) throw new System.IO.EndOfStreamException();
                total += n;
            }
        }
    }
}
