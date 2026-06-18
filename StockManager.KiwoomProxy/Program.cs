// KiwoomProxyHost — x86 진입점
//
// 프로덕션: NullKiwoomApi → ProductionKiwoomApi(axKHOpenAPI COM 래퍼)로 교체.
// COM STA 메시지 펌프가 필요한 경우 Application.Run() 방식으로 전환.
using System;
using System.Threading;
using StockManager.KiwoomProxy;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("[KiwoomProxyHost] 시작 중 (x86)...");

var api  = new NullKiwoomApi();  // TODO: new ProductionKiwoomApi()
using var host = new KiwoomProxyHost(api);

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

return 0;
