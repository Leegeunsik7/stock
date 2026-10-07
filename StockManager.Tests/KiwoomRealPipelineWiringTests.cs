// ============================================================================
// KiwoomRealPipelineWiringTests — [키움 실배선 · 추천 A]
//
//  §1 ProxyHello — 와이어 형식(타입 15 · 24B) · 실 파이프 왕복: ProductionCom = 실 송출 인정 · Simulation/미선언/형식 이탈 = 불인정 ·
//     ★ExecutionBridgeBroker.RequireProductionProxy — 시뮬레이션(NullKiwoomApi) 프록시엔 주문 송출 0(조용한 주문 소실 차단) · 단절 = 즉시 불인정.
//  §2 [방향 B · 실결함] OnEventConnect 오류(−100/−101/−102/−106/미분류) → 중대 서버 오류 통지 → KiwoomServerError 인터락 Disarm.
//  §3 KiwoomBridgeLinkInterlock(비트 25) — 연결 비활성 = 비차단 · 활성 = 링크 수립 전 차단 · 전이 시에만 재동기화.
//  §4 KiwoomProxySupervisor — 실행 파일 없음 · 기동 실패 · ★신원 거부 = 즉시 중단(재시도 0) · 재시도 후 연결 → 수신 시작 1회 · 상한 포기 · 처분 시 기동 프록시 종료.
//  §5 프록시 진입점/팩토리/COM 보호 · 호스트 결선(소스) · 오중 빌드 회귀.
// ============================================================================
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MS2E.Core;
using MS2E.Infrastructure;
using StockManager.Adapters;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class KiwoomRealPipelineWiringTests
    {
        private static string UniquePipe() => "ms2e_kwreal_" + Guid.NewGuid().ToString("N");

        // ════════════ §1 ProxyHello ════════════

        [Fact]
        public void ProxyHello_WireFormat()
        {
            Assert.Equal(24, Unsafe.SizeOf<ProxyHelloPayload>());
            Assert.Equal(15, (int)BridgeMessageType.ProxyHello);
            var h = new ProxyHelloPayload { ApiKind = ProxyApiKind.ProductionCom, ProtocolVersion = ProxyApiKind.ProtocolVersion, ProcessId = 1234 };
            byte[] frame = BridgeSerializer.Pack(BridgeMessageType.ProxyHello, in h);
            Assert.True(BridgeSerializer.TryParseHeader(frame, out var type, out int plen));
            Assert.Equal((BridgeMessageType.ProxyHello, 24), (type, plen));
        }

        // 가짜 프록시: Hello(선택) + 하트비트 유지 후 닫기
        private static Task FakeProxy(string pipe, ProxyHelloPayload? hello, TimeSpan life, ManualResetEventSlim? closeNow = null)
            => Task.Run(async () =>
            {
                using var srv = new NamedPipeServerStream(pipe, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await srv.WaitForConnectionAsync();
                if (hello is { } hv) { await srv.WriteAsync(BridgeSerializer.Pack(BridgeMessageType.ProxyHello, in hv)); await srv.FlushAsync(); }
                var until = DateTime.UtcNow + life;
                long seq = 0;
                while (DateTime.UtcNow < until && !(closeNow?.IsSet ?? false))
                {
                    try { await srv.WriteAsync(BridgeSerializer.PackHeartbeat(++seq)); await srv.FlushAsync(); } catch (IOException) { break; }
                    await Task.Delay(50);
                }
            });

        private static async Task<(ExecutionBridgeBroker Broker, KiwoomBridgeClient Bridge, Task Loop)> Connect(string pipe)
        {
            var (broker, bridge) = ExecutionBridgeBroker.Create(new OrderAttestationService(), pipe, heartbeatTimeoutMs: 400);
            broker.RequireProductionProxy = true;
            await bridge.ConnectAsync();
            var loop = bridge.StartAsync();
            return (broker, bridge, loop);
        }

        [Fact]
        public async Task ProductionHello_AcceptsSend_SimulationAndUndeclared_RefuseSend()
        {
            // 실 COM 프록시
            string p1 = UniquePipe();
            var srv1 = FakeProxy(p1, new ProxyHelloPayload { ApiKind = ProxyApiKind.ProductionCom, ProtocolVersion = ProxyApiKind.ProtocolVersion }, TimeSpan.FromSeconds(2));
            var (b1, c1, l1) = await Connect(p1);
            Assert.True(SpinWait.SpinUntil(() => c1.IsProductionProxy, 3000));
            Assert.True(b1.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000).ok);   // 송출 인정
            c1.Dispose(); await srv1; try { await l1.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

            // 시뮬레이션 프록시(NullKiwoomApi)
            string p2 = UniquePipe();
            var srv2 = FakeProxy(p2, new ProxyHelloPayload { ApiKind = ProxyApiKind.Simulation, ProtocolVersion = ProxyApiKind.ProtocolVersion }, TimeSpan.FromSeconds(2));
            var (b2, c2, l2) = await Connect(p2);
            Assert.True(SpinWait.SpinUntil(() => c2.ProxyApiKind == ProxyApiKind.Simulation, 3000));
            Assert.False(c2.IsProductionProxy);
            var r2 = b2.Place(PaperAsset.Stock, "005930", "삼성전자", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000);
            Assert.False(r2.ok);                                                   // ★시뮬레이션 프록시 = 송출 0
            Assert.Equal(1, b2.SimulationRefusals);
            c2.Dispose(); await srv2; try { await l2.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }

            // 미선언 · 미지 버전
            string p3 = UniquePipe();
            var srv3 = FakeProxy(p3, new ProxyHelloPayload { ApiKind = ProxyApiKind.ProductionCom, ProtocolVersion = 99 }, TimeSpan.FromSeconds(1));
            var (b3, c3, l3) = await Connect(p3);
            await Task.Delay(300);
            Assert.Equal(-1, c3.ProxyApiKind);                                     // 형식 이탈 Hello = 폐기 → 미선언
            Assert.False(b3.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000).ok);
            c3.Dispose(); await srv3; try { await l3.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        [Fact]
        public async Task ProductionLink_LostHeartbeat_ImmediatelyNotProduction()
        {
            string p = UniquePipe();
            using var close = new ManualResetEventSlim(false);
            var srv = FakeProxy(p, new ProxyHelloPayload { ApiKind = ProxyApiKind.ProductionCom, ProtocolVersion = ProxyApiKind.ProtocolVersion }, TimeSpan.FromSeconds(10), close);
            var (broker, bridge, loop) = await Connect(p);
            Assert.True(SpinWait.SpinUntil(() => bridge.IsProductionProxy, 3000));
            close.Set();                                                           // 프록시 하트비트 중단
            Assert.True(SpinWait.SpinUntil(() => !bridge.IsProductionProxy, 5000)); // ★단절 = 즉시 불인정
            Assert.False(broker.Place(PaperAsset.Stock, "005930", "x", PaperSide.Buy, PaperKind.Limit, 1, 70_000, 70_000).ok);
            bridge.Dispose(); await srv; try { await loop.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
        }

        // ════════════ §2 방향 B — 로그인/연결 오류 ════════════

        private sealed class NullOcx : IKiwoomOcx
        {
            public string GetCommRealData(string realKey, int fid) => "";
            public string GetChejanData(int fid) => "";
            public string GetCommData(string trCode, string recordName, int index, string itemName) => "";
            public int SendOrder(string rqName, string screenNo, string accountNo, int orderType, string code, int qty, int price, string hoga, string orgOrderNo) => 0;
            public void SetInputValue(string id, string value) { }
            public int CommRqData(string rqName, string trCode, int prevNext, string screenNo) => 0;
            public int GetRepeatCnt(string trCode, string recordName) => 0;
        }

        [Fact]
        public void OnEventConnect_ErrorCodes_ReportCritical_ServerErrorInterlockDisarms()
        {
            var errors = new List<int>();
            var core = new KiwoomComEventCore(new NullOcx(), null, null, null, null, errors.Add);
            Assert.True(core.OnEventConnect(0));                                   // 로그인 성공 = 통지 없음
            Assert.Empty(errors);
            Assert.False(core.OnEventConnect(-100));
            Assert.False(core.OnEventConnect(-101));
            Assert.False(core.OnEventConnect(-102));
            Assert.False(core.OnEventConnect(-106));
            Assert.False(core.OnEventConnect(-999));                               // 미분류 = 로그인 실패(fail-closed)
            Assert.Equal(new[] { KiwoomServerErrorCodes.LoginFailed, KiwoomServerErrorCodes.ServerConnectFailed, KiwoomServerErrorCodes.VersionFailed,
                                 KiwoomServerErrorCodes.CommDisconnected, KiwoomServerErrorCodes.LoginFailed }, errors.ToArray());
            Assert.Equal(5, core.ConnectFailures);
            foreach (int c in errors) Assert.True(KiwoomServerErrorCodes.IsCritical(c));
            Assert.True(KiwoomServerErrorCodes.IsCritical(KiwoomServerErrorCodes.ComCreateFailed));

            var il = new KiwoomServerErrorInterlock();
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KiwoomServerError);
            il.BindInterlock(mask, ArmingInterlockSource.KiwoomServerError);
            il.OnError(new ErrorPayload { ErrorCode = KiwoomServerErrorCodes.LoginFailed });
            Assert.True(mask.IsRaised(ArmingInterlockSource.KiwoomServerError));  // ★로그인 실패 = Disarm
        }

        // ════════════ §3 링크 인터락 ════════════

        [Fact]
        public void LinkInterlock_NotRequired_NeverBlocks_Required_BlocksUntilLinked()
        {
            bool linked = false;
            var off = new KiwoomBridgeLinkInterlock(() => linked, required: false);
            off.Evaluate();
            Assert.False(off.ArmingBlocked);

            var on = new KiwoomBridgeLinkInterlock(() => linked, required: true);
            var mask = new ArmingInterlockBitmask(ArmingInterlockSource.KiwoomBridgeLink);
            on.BindInterlock(mask, ArmingInterlockSource.KiwoomBridgeLink);
            Assert.True(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));   // 링크 수립 전 차단
            on.Evaluate(); on.Evaluate();
            Assert.Equal(0, on.Transitions);
            linked = true; on.Evaluate();
            Assert.False(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));
            linked = false; on.Evaluate();
            Assert.True(mask.IsRaised(ArmingInterlockSource.KiwoomBridgeLink));   // 단절/시뮬레이션 = 재차단
            Assert.Equal(2, on.Transitions);
            var thrower = new KiwoomBridgeLinkInterlock(() => throw new InvalidOperationException(), required: true);
            thrower.Evaluate();
            Assert.True(thrower.ArmingBlocked);                                    // 판독 불가 = 차단
        }

        // ════════════ §4 감독자 ════════════

        private sealed class Handle : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }

        [Fact]
        public async Task Supervisor_MissingExe_LaunchFailed_IdentityRejected_NoRetry()
        {
            var s1 = new KiwoomProxySupervisor(_ => Task.CompletedTask, _ => Task.CompletedTask, Path.Combine(Path.GetTempPath(), "absent_" + Guid.NewGuid().ToString("N") + ".exe"), autoStart: true);
            await s1.RunAsync(CancellationToken.None);
            Assert.Equal(KiwoomSupervisorState.ProxyMissing, s1.State);

            string exe = Path.GetTempFileName();
            try
            {
                var s2 = new KiwoomProxySupervisor(_ => Task.CompletedTask, _ => Task.CompletedTask, exe, autoStart: true, launcher: _ => null);
                await s2.RunAsync(CancellationToken.None);
                Assert.Equal(KiwoomSupervisorState.LaunchFailed, s2.State);
            }
            finally { File.Delete(exe); }

            int connects = 0;
            var s3 = new KiwoomProxySupervisor(_ => { connects++; throw new UnauthorizedAccessException("신원 불일치"); }, _ => Task.CompletedTask, "", autoStart: false,
                                               retryDelay: TimeSpan.FromMilliseconds(1));
            await s3.RunAsync(CancellationToken.None);
            Assert.Equal((KiwoomSupervisorState.IdentityRejected, 1), (s3.State, connects));   // ★파이프 선점 = 재시도 0
        }

        [Fact]
        public async Task Supervisor_RetriesThenConnects_StartsOnce_GivesUp_DisposeKillsLaunched()
        {
            int connects = 0, starts = 0;
            string exe = Path.GetTempFileName();
            var h = new Handle();
            try
            {
                var s = new KiwoomProxySupervisor(_ => ++connects < 3 ? throw new TimeoutException() : Task.CompletedTask,
                                                  _ => { starts++; return Task.CompletedTask; }, exe, autoStart: true, launcher: _ => h,
                                                  retryDelay: TimeSpan.FromMilliseconds(1));
                await s.RunAsync(CancellationToken.None);
                Assert.Equal((KiwoomSupervisorState.Connected, 3, 1), (s.State, connects, starts));
                s.Dispose();
                Assert.True(h.Disposed);                                           // 자신이 기동한 프록시 종료
            }
            finally { File.Delete(exe); }

            var g = new KiwoomProxySupervisor(_ => throw new IOException(), _ => Task.CompletedTask, "", autoStart: false,
                                              retryDelay: TimeSpan.FromMilliseconds(1), maxAttempts: 4);
            await g.RunAsync(CancellationToken.None);
            Assert.Equal((KiwoomSupervisorState.GaveUp, 4L), (g.State, g.Attempts));
        }

        // ════════════ §5 진입점 · 결선 ════════════

        private static string RepoRoot()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "StockManager.sln"))) d = d.Parent;
            Assert.NotNull(d);
            return d!.FullName;
        }

        [Fact]
        public void ProxyEntry_Factory_ComGuard_Host_Wired()
        {
            string root = RepoRoot();
            string program = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "Program.cs"));
            Assert.Contains(@"new Mutex(initiallyOwned: true, @""Local\MS2E.KiwoomProxyHost"", out bool createdNew)", program);   // 이중 실행 방지
            Assert.Contains("return 3;", program);
            Assert.Contains("KiwoomApiFactory.Create(requireProduction, Environment.GetEnvironmentVariable, out string apiReason)", program);
            Assert.Contains("return 2;", program);
            Assert.DoesNotContain("var api  = new NullKiwoomApi();", program);   // 무조건 시뮬레이션 대체 제거
            string prod = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "ProductionKiwoomApi.cs"));
            Assert.Contains("_ready = _core.OnEventConnect(e.nErrCode)", prod);
            Assert.Contains("Code = KiwoomServerErrorCodes.ComCreateFailed", prod);
            Assert.Contains("public int ApiKind => ProxyApiKind.ProductionCom;", prod);
            Assert.Contains("public static class KiwoomApiFactory", prod);
            string host = File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "KiwoomProxyHost.cs"));
            Assert.Contains("SendFrame(BridgeMessageType.ProxyHello, in hello);", host);
            Assert.Contains("public int ApiKind => ProxyApiKind.Simulation;", File.ReadAllText(Path.Combine(root, "StockManager.KiwoomProxy", "NullKiwoomApi.cs")));
            string form = File.ReadAllText(Path.Combine(root, "StockManager", "Blazor", "BlazorHomeForm.cs"));
            Assert.Contains("broker.RequireProductionProxy = true;", form);
            Assert.Contains("cfg.GetBool(\"Kiwoom.ConnectEnabled\", false)", form);
            Assert.Contains("MS2E.Core.ArmingInterlockSource.KiwoomBridgeLink,", form);
            Assert.Contains("ct => bridge.ConnectAsync(\".\", ct), ct => bridge.StartAsync(ct), proxyExe", form);
            Assert.Contains("AccountSyncBrokerView(accountSync, () => bridge.IsProductionProxy)", form);
            Assert.Equal(25, (int)ArmingInterlockSource.KiwoomBridgeLink);
            string sln = File.ReadAllText(Path.Combine(root, "StockManager.sln"));
            Assert.Contains(@"tools\KiwoomComShim\KiwoomComShim.csproj", sln);   // 쉼이 팩토리 COM 분기 · COM 보호를 컴파일 수검
        }
    }
}
