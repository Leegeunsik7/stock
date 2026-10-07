// KiwoomProxyHost — x86 진입점
//
// [키움 실배선] API 선택은 KiwoomApiFactory(KIWOOM_COM 빌드 = ProductionKiwoomApi · 그 외 = NullKiwoomApi · ProxyHello 로 종류 선언).
//   --require-production(또는 MS2E_KIWOOM_REQUIRE_PRODUCTION=1) 인데 실 COM 미포함 빌드면 즉시 종료(exit 2 · 시뮬레이션으로 조용히 대체 금지).
// [키움 실배선] 이중 실행 방지 — 세션 로컬 뮤텍스(Local\MS2E.KiwoomProxyHost). 이미 실행 중이면 exit 3(파이프 선점 경합·이중 COM 로그인 차단).
using System;
using System.Threading;
using StockManager.KiwoomProxy;

using var single = new Mutex(initiallyOwned: true, @"Local\MS2E.KiwoomProxyHost", out bool createdNew);
if (!createdNew)
{
    Console.Error.WriteLine("[KiwoomProxyHost] 이미 실행 중 — 이중 실행 거부");
    return 3;
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("[KiwoomProxyHost] 시작 중 (x86)...");

bool requireProduction = Array.IndexOf(args, "--require-production") >= 0
                      || Environment.GetEnvironmentVariable("MS2E_KIWOOM_REQUIRE_PRODUCTION") == "1";
var api = KiwoomApiFactory.Create(requireProduction, Environment.GetEnvironmentVariable, out string apiReason);
if (api is null)
{
    Console.Error.WriteLine("[KiwoomProxyHost] " + apiReason);
    return 2;
}
Console.WriteLine("[KiwoomProxyHost] API = " + apiReason);
// [헬스 · 방향 B] 호스트 박동(≤200ms)이 끊기면 주문을 키움으로 보내지 않는다(동결 호스트의 낡은 주문 차단 · fail-closed)
using var egressGate = new MS2E.Infrastructure.HostHeartbeatEgressGate();
using var host = new KiwoomProxyHost(api, egressGate: egressGate);

try
{
    Console.WriteLine("[KiwoomProxyHost] 64비트 터미널 연결 대기 중...");
    await host.RunAsync(cts.Token);
    Console.WriteLine("[KiwoomProxyHost] 연결 종료.");
}
catch (OperationCanceledException)
{
    Console.WriteLine("[KiwoomProxyHost] 종료됨.");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[KiwoomProxyHost] 오류: {ex.Message}");
    return 1;
}
finally
{
    single.ReleaseMutex();
}

return 0;
