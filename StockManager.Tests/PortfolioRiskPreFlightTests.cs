// ============================================================================
// PortfolioRiskPreFlightTests — [사전 검증 · 포트폴리오 리스크 · 추천 A]
//
//  §1 BootPreFlightSentinel — 부팅 시계 역행(앵커) · 앵커 비역행 기록 · 런타임 벽시계↔단조 역행 · 정방향 점프 재기준 ·
//     안전 설정 런타임 변조 지문 · Trip = 비트 Raise + 자격증명 즉시 소거.
//  §2 프록시 파이프 서버 신원 — 실 네임드 파이프: 기대 실행 파일 불일치 = 연결 거부(첫 프레임 전) · 일치 = 연결 · 서버 PID 판독.
//  §3 PortfolioExposureGate — 단일 종목 20% · 섹터 편중 · 승인 대기 누적/체결 감소/반환 · 섹터표 UTF-8 무할당 파싱 · Fail-Closed(평가 미상) ·
//     RouterFeedGate 송출 전 거부(계좌 예약 반환 · 라우터 슬롯 0) · δ=0.
//  §4 [방향 B] CorrelationCrashInterlock — 최소 종목 수 · 폭(breadth) 동시 조건 · 미체결 일괄 취소 · sticky · 이중키 재무장 · δ=0.
//  §5 호스트 결선 · 감시 스레드 처분 경합 · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using StockManager.Engines;
using StockManager.Security;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class PortfolioRiskPreFlightTests : IDisposable
    {
        private readonly string _tmp = Path.Combine(Path.GetTempPath(), "pfrisk_" + Guid.NewGuid().ToString("N"));
        public PortfolioRiskPreFlightTests() => Directory.CreateDirectory(_tmp);
        public void Dispose() { try { Directory.Delete(_tmp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

        private static long MinAllocated(Action body, int windows = 3)
        {
            long min = long.MaxValue;
            for (int w = 0; w < windows && min != 0; w++)
            {
                long b = GC.GetAllocatedBytesForCurrentThread();
                body();
                min = Math.Min(min, GC.GetAllocatedBytesForCurrentThread() - b);
            }
            return min;
        }

        private static readonly long Min = TimeSpan.TicksPerMinute;

        // ════════════ §1 사전 검증 ════════════

        [Fact]
        public void BootClock_Regression_Trips_BitRaised_SecretsZeroized()
        {
            var token = new PoisonedSecretBuffer(new byte[] { 0x41, 0x42, 0x43 });
            var purger = new VolatileMemoryPurger();
            var apiSecret = new byte[] { 9, 9, 9, 9 };
            purger.Register(apiSecret);
            var s = new BootPreFlightSentinel(purger, new[] { token });
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.PreFlightBoot);
            s.BindInterlock(mask, ArmingInterlockSource.PreFlightBoot);
            Assert.True(mask.Seal(out _));
            long anchor = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc).Ticks;
            Assert.Equal(PreFlightFault.None, s.EvaluateBootClock(anchor - 1 * Min, anchor));   // 허용치(2분) 안
            Assert.Equal(PreFlightFault.None, s.EvaluateBootClock(anchor, 0));                   // 앵커 없음(최초 실행)
            Assert.False(mask.Blocked);
            Assert.Equal(PreFlightFault.ClockRegressedAtBoot, s.EvaluateBootClock(anchor - 3 * Min, anchor));
            Assert.True(s.Tripped && mask.IsRaised(ArmingInterlockSource.PreFlightBoot));
            Assert.True(token.IsPoisoned && token.AllZero());                                    // ★발주 토큰 즉시 소거
            Assert.All(apiSecret, b => Assert.Equal(0, b));                                       // ★API Secret 0x00
            Assert.Equal(1, s.Purges);
            s.ReportProxyIdentityRejected();                                                      // 추가 사유 누적 · 소거 1회
            Assert.Equal(PreFlightFault.ClockRegressedAtBoot | PreFlightFault.ProxyIdentityRejected, s.Faults);
            Assert.Equal(1, s.Purges);
        }

        [Fact]
        public void ClockAnchor_NeverMovesBackward_CorruptReadsZero()
        {
            string path = Path.Combine(_tmp, "sub", "preflight.clock");
            Assert.Equal(0, BootPreFlightSentinel.ReadClockAnchor(path));
            Assert.True(BootPreFlightSentinel.AdvanceClockAnchor(path, 1_000));
            Assert.False(BootPreFlightSentinel.AdvanceClockAnchor(path, 500));                   // ★되감긴 시계로 증거 덮어쓰기 금지
            Assert.Equal(1_000, BootPreFlightSentinel.ReadClockAnchor(path));
            Assert.True(BootPreFlightSentinel.AdvanceClockAnchor(path, 2_000));
            File.WriteAllText(path, "not-a-number");
            Assert.Equal(0, BootPreFlightSentinel.ReadClockAnchor(path));
        }

        [Fact]
        public void RuntimeClock_RewindTrips_ForwardJumpRebaselines()
        {
            var s = new BootPreFlightSentinel();
            const long F = 10_000_000;                                         // 단조 주파수 = 100ns 틱과 동일(계산 단순화)
            long wall = 1_000 * Min, mono = 0;
            s.ObserveClock(wall, mono, F);
            wall += 10 * TimeSpan.TicksPerSecond; mono += 10 * F;
            s.ObserveClock(wall, mono, F);
            Assert.False(s.Tripped);
            wall += 60 * Min; mono += 1 * F;                                  // 정방향 점프(NTP · 절전 복귀) — 재기준
            s.ObserveClock(wall, mono, F);
            Assert.False(s.Tripped);
            wall -= 5 * Min; mono += 1 * F;                                   // 실행 중 5분 되감기
            s.ObserveClock(wall, mono, F);
            Assert.True(s.Tripped);
            Assert.Equal(PreFlightFault.ClockRegressedRuntime, s.Faults);
        }

        [Fact]
        public void SafetyConfig_RuntimeChange_Trips_NonSafetyChangeIgnored()
        {
            var cfg = new Dictionary<string, string?> { ["Dispatch.ValveWired"] = "false", ["Exposure.SingleNameCapBps"] = "2000", ["Ui.Theme"] = "dark" };
            Func<string, string?> raw = k => cfg.TryGetValue(k, out var v) ? v : null;
            var s = new BootPreFlightSentinel();
            var fp = new byte[32];
            BootPreFlightSentinel.ComputeConfigFingerprint(raw, fp);
            s.ObserveConfigFingerprint(fp);                                    // 봉인 전 = 무시
            s.SealConfig(fp);
            Assert.True(s.ConfigSealed);
            cfg["Ui.Theme"] = "light";                                         // 비안전 키
            BootPreFlightSentinel.ComputeConfigFingerprint(raw, fp);
            s.ObserveConfigFingerprint(fp);
            Assert.False(s.Tripped);
            cfg["Dispatch.ValveWired"] = "true";                               // ★실행 중 물리 키 설정 변조(이 호스트엔 해당 키 쓰기 UI 없음 = 외부 편집)
            BootPreFlightSentinel.ComputeConfigFingerprint(raw, fp);
            s.ObserveConfigFingerprint(fp);
            Assert.True(s.Tripped);
            Assert.Equal(PreFlightFault.SafetyConfigTampered, s.Faults);
            Assert.Contains("Dispatch.LiveArmingWired", BootPreFlightSentinel.SafetyKeys);
            Assert.Contains("Exposure.SectorCapBps", BootPreFlightSentinel.SafetyKeys);
        }

        // ════════════ §2 프록시 파이프 신원 ════════════

        private static async Task<(bool connected, Exception? error, long rejections, int rejectedPid)> ConnectWith(Func<int, bool>? verifier)
        {
            string name = "PFR_" + Guid.NewGuid().ToString("N");
            using var srv = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var accept = srv.WaitForConnectionAsync();
            using var client = new KiwoomBridgeClient(pipeName: name, heartbeatTimeoutMs: 10_000) { ServerIdentityVerifier = verifier };
            int rejectedPid = 0;
            client.ServerIdentityRejected += pid => rejectedPid = pid;
            try
            {
                await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await accept.WaitAsync(TimeSpan.FromSeconds(5));
                return (client.IsProxyAlive, null, client.IdentityRejections, rejectedPid);
            }
            catch (UnauthorizedAccessException ex) { return (false, ex, client.IdentityRejections, rejectedPid); }
        }

        [Fact]
        public async Task ProxyPipe_SquatterIdentityMismatch_RejectedBeforeFirstFrame()
        {
            // 서버 = 이 테스트 프로세스(사칭 프록시) · 기대 = 설치 폴더의 프록시 실행 파일 → 불일치 = 거부
            var (connected, error, rejections, pid) = await ConnectWith(PipeServerIdentity.ImagePathVerifier(Path.Combine(_tmp, "MS2E.KiwoomProxyHost.exe")));
            Assert.False(connected);
            Assert.IsType<UnauthorizedAccessException>(error);
            Assert.Equal(1, rejections);
            Assert.Equal(Environment.ProcessId, pid);                          // 서버 PID 실측 = 사칭 프로세스
        }

        [Fact]
        public async Task ProxyPipe_ExpectedImage_Connects_ServerPidReadable()
        {
            var (connected, error, rejections, _) = await ConnectWith(PipeServerIdentity.ImagePathVerifier(Environment.ProcessPath!));
            Assert.Null(error);
            Assert.True(connected);
            Assert.Equal(0, rejections);
            Assert.True(PipeServerIdentity.TryGetProcessImagePath(Environment.ProcessId, out var self));
            Assert.Equal(Path.GetFullPath(Environment.ProcessPath!), Path.GetFullPath(self), ignoreCase: true);
            Assert.False(PipeServerIdentity.TryGetProcessImagePath(-5, out _));
            var (c2, e2, r2, _) = await ConnectWith(_ => throw new InvalidOperationException("검증기 예외"));   // 검증기 예외 = 거부
            Assert.False(c2); Assert.NotNull(e2); Assert.Equal(1, r2);
        }

        // ════════════ §3 익스포저 게이트 ════════════

        private static AccountStateSyncAdapter Account(long deposit, params (long sym, long qty)[] held)
        {
            var a = new AccountStateSyncAdapter(64, nowMicros: () => 1_000_000);
            a.OnCash(new AccountCashPayload { Deposit = deposit, OrderableCash = deposit });
            for (int i = 0; i < held.Length; i++)
                a.OnPosition(new PositionPayload { BatchId = 1, RowIndex = i, RowCount = held.Length, SymbolCode = held[i].sym, Quantity = held[i].qty, AvgPrice = 1 });
            if (held.Length == 0) a.OnPosition(new PositionPayload { BatchId = 1, RowIndex = 0, RowCount = 0 });
            return a;
        }

        [Fact]
        public void SingleName_20Percent_Cap_PendingAccumulates_FillAndReleaseReduce()
        {
            var g = new PortfolioExposureGate(Account(10_000_000));            // 총자산 1,000만
            Assert.Equal(ExposureVerdict.Ok, g.TryAdmitBuy(5930, 2_000_000)); // 정확히 20% — 통과(경계 포함)
            Assert.Equal(2_000_000, g.PendingOf(5930));
            Assert.Equal(ExposureVerdict.SingleNameExceeded, g.TryAdmitBuy(5930, 1));   // ★승인 대기 포함 20% 초과
            g.OnFill(new FillPayload { SymbolCode = 5930, FillQty = 10, FillPrice = 70_000, Side = 1 });   // 70만 체결 → 대기 130만
            Assert.Equal(1_300_000, g.PendingOf(5930));
            g.ReleasePending(5930, 300_000);
            Assert.Equal(1_000_000, g.PendingOf(5930));
            Assert.Equal(ExposureVerdict.Ok, g.TryAdmitBuy(5930, 1_000_000));
            Assert.Equal(ExposureVerdict.SingleNameExceeded, g.TryAdmitBuy(5930, 1));
            Assert.Equal(2, g.RejectSingle);
            Assert.Equal(ExposureVerdict.Disabled, new PortfolioExposureGate(Account(10_000_000), 0, 0).TryAdmitBuy(5930, long.MaxValue / 8));
        }

        [Fact]
        public void HeldPositions_MarkedToMarket_SectorConcentration()
        {
            // 예수금 600만 + 삼성전자 50주 × 80,000(400만) = 총자산 1,000만
            var g = new PortfolioExposureGate(Account(6_000_000, (5930, 50)), singleNameCapBps: 2_000, sectorCapBps: 4_500);
            Assert.Equal(3, g.IngestSectorTable(Encoding.UTF8.GetBytes("# 섹터표\n005930,1\r\n000660,1\nA035420,2\nbad-line\n123,\n")));
            Assert.Equal(ExposureVerdict.MarkUnknown, g.TryAdmitBuy(660, 100_000));   // ★보유 종목 마크가 미상 = 총자산 미상 → 거부
            g.OnMark(5930, 80_000);
            Assert.Equal(ExposureVerdict.SingleNameExceeded, g.TryAdmitBuy(5930, 1)); // 이미 40% 보유 > 20%
            Assert.Equal(ExposureVerdict.Ok, g.TryAdmitBuy(660, 400_000));            // 섹터1: 400만 + 40만 = 44% ≤ 45%
            Assert.Equal(ExposureVerdict.SectorExceeded, g.TryAdmitBuy(660, 200_000)); // ★섹터1: 400만 + 40만(대기) + 20만 = 46% > 45%
            Assert.Equal(ExposureVerdict.Ok, g.TryAdmitBuy(35420, 2_000_000));        // 다른 섹터(2) — 20% 이내
            Assert.Equal(1, g.RejectSector);
        }

        [Fact]
        public void FailClosed_NoEquity_TruncatedSnapshot()
        {
            var noCash = new AccountStateSyncAdapter(16, nowMicros: () => 1);
            Assert.Equal(ExposureVerdict.EquityUnknown, new PortfolioExposureGate(noCash).TryAdmitBuy(5930, 1_000));
            var many = new (long, long)[10];
            for (int i = 0; i < many.Length; i++) many[i] = (1000 + i, 1);
            var tiny = new PortfolioExposureGate(Account(10_000_000, many), capacity: 4);       // 보유 10종목 > 작업 버퍼 4
            Assert.Equal(ExposureVerdict.SnapshotTruncated, tiny.TryAdmitBuy(5930, 1_000));
            Assert.Equal(ExposureVerdict.EquityUnknown, new PortfolioExposureGate(Account(10_000_000)).TryAdmitBuy(5930, 0));
        }

        [Fact]
        public void RouterFeedGate_ExposureReject_BeforeRouting_AccountReservationReleased()
        {
            long now = 1_000_000;
            Func<long> clock = () => now;
            var price = new ReferencePriceFeedAdapter(capacityPow2: 64, nowMicros: clock);
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var router = new ResilientOrderRouter(board, new BrokerFailoverSentinel(board, pendingOrders: pending), pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            var exec = new OrderExecutionFeedAdapter(router, 64, 120_000_000, clock);
            var breaker = new FeedDisconnectCircuitBreaker(price, () => true);
            var acct = new AccountStateSyncAdapter(64, nowMicros: clock);
            acct.OnCash(new AccountCashPayload { Deposit = 10_000_000, OrderableCash = 10_000_000 });
            acct.OnPosition(new PositionPayload { BatchId = 1, RowCount = 0 });
            var exposure = new PortfolioExposureGate(acct);
            var gate = new RouterFeedGate(router, price, exec, breaker, requireDepthForMarket: false, account: acct, exposure: exposure);
            price.OnTick(ReferencePriceFeedAdapter.SymbolHashForCode(5930), 70_000, 1);
            breaker.Evaluate();
            var big = new OrderProposal(1, 0, 5930, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(30m), 0);    // 210만 = 21%
            Assert.False(gate.TryAdmit(in big, out _));
            Assert.Equal(OrderRouteVerdict.ExposureLimit, gate.LastVerdict);
            Assert.Equal(0, router.TrackedOrders);                              // ★라우터 슬롯 확보 전 거부
            Assert.Equal(0, acct.ReservedBuyNotional);                          // ★계좌 예약 반환
            Assert.Equal(0, exposure.PendingOf(5930));
            var ok = new OrderProposal(2, 0, 5930, OrderSide.Buy, 1, QPrice.Of(70_000m), QQty.Of(28m), 0);    // 196만 = 19.6%
            Assert.True(gate.TryAdmit(in ok, out _));
            Assert.Equal(1_960_000, exposure.PendingOf(5930));
            Assert.Equal(1, gate.ExposureRejects);
            acct.OnFill(new FillPayload { SymbolCode = 5930, FillQty = 10, FillPrice = 70_000, Side = 1 });   // 체결로 보유 10주(시드 후 배치는 정적 구간만 반영)
            var sell = new OrderProposal(3, 0, 5930, OrderSide.Sell, 1, QPrice.Of(70_000m), QQty.Of(5m), 0);
            Assert.True(gate.TryAdmit(in sell, out _));                         // 매도는 익스포저 축소 — 게이트 무관
        }

        [Fact]
        public void ZeroAlloc_ExposureGate_MarkFillAdmitSectorParse()
        {
            var g = new PortfolioExposureGate(Account(1_000_000_000, (5930, 100), (660, 50)));
            byte[] table = Encoding.UTF8.GetBytes("005930,1\n000660,1\n035420,2\n");
            g.OnMark(5930, 70_000); g.OnMark(660, 120_000); g.OnMark(35420, 200_000);
            var fill = new FillPayload { SymbolCode = 35420, FillQty = 1, FillPrice = 200_000, Side = 1 };
            Action body = () =>
            {
                for (int k = 0; k < 500; k++)
                {
                    g.OnMark(5930, 70_000 + (k & 7));
                    g.IngestSectorTable(table);
                    _ = g.TryAdmitBuy(35420, 200_000);
                    g.OnFill(fill);
                }
            };
            body();
            Assert.Equal(0, MinAllocated(body));
        }

        // ════════════ §4 [방향 B] 상관 붕괴 ════════════

        [Fact]
        public void CorrelationCrash_MinNamesAndBreadth_CancelsAllOpen_Sticky()
        {
            var cancelled = new List<long>();
            var crash = new CorrelationCrashInterlock(dropBps: 300, minNames: 3, breadthBps: 6_000,
                openOrders: buf => { buf.Span[0] = 501; buf.Span[1] = 502; return 2; }, cancel: cancelled.Add);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.CorrelationCrash);
            crash.BindInterlock(mask, ArmingInterlockSource.CorrelationCrash);
            Assert.True(mask.Seal(out _));
            long[] held = { 1, 2, 3, 4, 5 };
            foreach (var c in held) crash.OnTick(c, 100_000);
            crash.OnTick(99, 100_000);
            crash.SetHeld(held);
            crash.OnTick(99, 50_000);                                          // 비보유 폭락 — 무관
            crash.OnTick(1, 96_000); crash.OnTick(2, 96_500);                  // 2종목 −4%·−3.5% (< 최소 3종목)
            crash.OnTick(3, 97_100);                                           // −2.9% (< 3%)
            Assert.False(crash.Tripped);
            Assert.Equal(2, crash.CrashedCount);
            crash.OnTick(3, 97_000);                                           // ★3번째 −3% → 3/5 = 60% ≥ 60%
            Assert.True(crash.Tripped);
            Assert.True(mask.IsRaised(ArmingInterlockSource.CorrelationCrash));
            Assert.Equal(new long[] { 501, 502 }, cancelled.ToArray());        // ★미체결 일괄 취소
            Assert.Equal(2, crash.CancelsIssued);
            crash.OnTick(4, 90_000);                                           // sticky — 재취소 없음
            Assert.Equal(2, cancelled.Count);
            Assert.False(crash.TryRearm(true, false));
            Assert.True(crash.TryRearm(true, true));
            Assert.False(mask.Blocked);
            Assert.Equal(0, crash.CrashedCount);                               // 세션 재설정(고점 = 최근가)
        }

        [Fact]
        public void CorrelationCrash_BreadthGuards_LargePortfolio_And_RecoveryUncounts()
        {
            var crash = new CorrelationCrashInterlock(dropBps: 300, minNames: 3, breadthBps: 6_000);
            var held = new long[10];
            for (int i = 0; i < 10; i++) { held[i] = 100 + i; crash.OnTick(held[i], 10_000); }
            crash.SetHeld(held);
            for (int i = 0; i < 5; i++) crash.OnTick(held[i], 9_000);          // 5/10 = 50% < 60% — 체계적 아님
            Assert.False(crash.Tripped);
            crash.OnTick(held[0], 10_000);                                     // 회복 → 낙폭 해제
            Assert.Equal(4, crash.CrashedCount);
            crash.OnTick(held[5], 9_000); crash.OnTick(held[6], 9_000);        // 6/10
            Assert.True(crash.Tripped);
        }

        [Fact]
        public void CorrelationCrash_WithExecutionFeed_CancelsTrackedOpenOrders_ZeroAllocTicks()
        {
            var board = new LockFreeBrokerSwitchboard(1);
            var pending = new PendingOrderCascadeCanceller(64);
            var router = new ResilientOrderRouter(board, new BrokerFailoverSentinel(board, pendingOrders: pending), pending, idMapCapacityPow2: 256, maxSlippageBps: 50);
            var exec = new OrderExecutionFeedAdapter(router, 64, 120_000_000, () => 1);
            for (long p = 1; p <= 3; p++)
            {
                var key = RouterFeedGate.KeyFor(p);
                Assert.True(router.Route(key, 7_000_000_000_000, 7_000_000_000_000, 0.0).Accepted);
                Assert.True(exec.TryTrack(p, key, 0, 10));
                Assert.True(exec.BindBrokerOrder(p, 900 + p));
            }
            var cancelled = new List<long>();
            var crash = new CorrelationCrashInterlock(minNames: 2, breadthBps: 5_000, openOrders: buf => exec.CopyOpenBrokerOrders(buf.Span), cancel: cancelled.Add);
            crash.SetHeld(new long[] { 1, 2 });
            crash.OnTick(1, 100); crash.OnTick(2, 100);
            Action body = () => { for (int k = 0; k < 1_000; k++) { crash.OnTick(1, 100 + (k & 1)); crash.OnTick(2, 100); } };
            body();
            Assert.Equal(0, MinAllocated(body));                                 // 틱 판정 δ=0
            crash.OnTick(1, 90); crash.OnTick(2, 90);
            Assert.True(crash.Tripped);
            cancelled.Sort();
            Assert.Equal(new long[] { 901, 902, 903 }, cancelled.ToArray());
        }

        // ════════════ §5 결선 · 경합 · 회귀 ════════════

        [Fact]
        public void Host_Wires_PreFlight_Exposure_CorrelationCrash_PipeIdentity()
        {
            const BindingFlags AllF = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            var host = typeof(StockManager.Blazor.BlazorHomeForm);
            var targets = new MethodBase[]
            {
                typeof(BootPreFlightSentinel).GetMethod(nameof(BootPreFlightSentinel.BindInterlock))!,
                typeof(BootPreFlightSentinel).GetMethod(nameof(BootPreFlightSentinel.EvaluateBootClock))!,
                typeof(BootPreFlightSentinel).GetMethod(nameof(BootPreFlightSentinel.SealConfig))!,
                typeof(BootPreFlightSentinel).GetMethod(nameof(BootPreFlightSentinel.ObserveClock))!,
                typeof(BootPreFlightSentinel).GetMethod(nameof(BootPreFlightSentinel.ObserveConfigFingerprint))!,
                typeof(KiwoomBridgeClient).GetProperty(nameof(KiwoomBridgeClient.ServerIdentityVerifier))!.SetMethod!,
                typeof(PortfolioExposureGate).GetConstructors()[0],
                typeof(PortfolioExposureGate).GetMethod(nameof(PortfolioExposureGate.OnMark))!,
                typeof(CorrelationCrashInterlock).GetMethod(nameof(CorrelationCrashInterlock.BindInterlock))!,
                typeof(CorrelationCrashInterlock).GetMethod(nameof(CorrelationCrashInterlock.OnTick))!,
                typeof(CorrelationCrashInterlock).GetMethod(nameof(CorrelationCrashInterlock.SetHeld))!,
            };
            var found = new bool[targets.Length];
            var types = new List<Type> { host };
            types.AddRange(host.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public));
            foreach (var ty in types)
            {
                var all = new List<MethodBase>(ty.GetMethods(AllF));
                all.AddRange(ty.GetConstructors(AllF));
                foreach (var m in all)
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length; i++)
                        if (il[i] is 0x28 or 0x6F or 0x73)
                            try
                            {
                                var mb = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1));
                                for (int k = 0; k < targets.Length; k++) if (mb == targets[k]) found[k] = true;
                            }
                            catch (ArgumentException) { }
                }
            }
            for (int k = 0; k < targets.Length; k++) Assert.True(found[k], $"결선 누락: {targets[k].DeclaringType!.Name}.{targets[k].Name}");
            string src = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("exposure: exposureGate);", src);
            Assert.Contains("MS2E.Core.ArmingInterlockSource.PreFlightBoot,", src);
            Assert.Contains("MS2E.Core.ArmingInterlockSource.CorrelationCrash);", src);
        }

        [Fact]
        public void GenesisPreFlightUpdater_StartDisposeRace_NoProcessCrash()
        {
            // [실결함 교정 회귀] Start 의 _thread 대입 전 Dispose 가 _stop 을 처분할 수 있다 — 루프의 처분 경합 흡수 확인
            for (int i = 0; i < 300; i++)
            {
                var u = new StockManager.Blazor.GenesisPreFlightUpdater(new GenesisPreFlightCertifier(), new AtomicArmingLease(leaseSeconds: 60), intervalMs: 50);
                var t = new Thread(u.Start);
                t.Start();
                u.Dispose();
                t.Join();
            }
            Thread.Sleep(150);
        }

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void FiveBuild_And_Contract_Regression()
        {
            Assert.Equal(16, (int)ArmingInterlockSource.PreFlightBoot);
            Assert.Equal(17, (int)ArmingInterlockSource.CorrelationCrash);
            Assert.Equal((18, 19, 20), ((int)OrderRouteVerdict.ExposureLimit, (int)OrderRouteVerdict.SectorConcentration, (int)OrderRouteVerdict.ExposureUnknown));
            string sln = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager.sln"));
            foreach (var p in new[] { @"StockManager.PrimesBench\StockManager.PrimesBench.csproj", @"StockManager.KiwoomProxy\StockManager.KiwoomProxy.csproj", @"tools\KiwoomComShim\KiwoomComShim.csproj" })
                Assert.Contains(p, sln);
            string proxy = File.ReadAllText(Path.Combine(RepoRoot(), "StockManager.KiwoomProxy", "KiwoomProxyHost.cs"));
            Assert.Contains("PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly", proxy);   // 프록시 서버 DACL = 현재 사용자
            string client = File.ReadAllText(Path.Combine(RepoRoot(), "src", "02_Infrastructure", "Adapters", "Brokers", "KiwoomBridgeClient.cs"));
            Assert.Contains("PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly", client);
        }
    }
}
