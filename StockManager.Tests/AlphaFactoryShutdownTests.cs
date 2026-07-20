// ============================================================================
// AlphaFactoryShutdownTests — 종료 지연 회귀(운영 버그 보고)
//
//  AutonomousAlphaFactory 워커가 24h 상주 대기(사이클 사이)에 있어도 Dispose 는 인터럽터블 기상으로 즉시 반환해야 한다.
//  회귀 대상: 기존엔 1분 단위 Thread.Sleep 로 대기해 stop 신호를 최대 60초 뒤 관측 → Dispose 의 Join(5s) 타임아웃까지
//  종료가 프리즈됐다. 교정 후 ManualResetEventSlim 기상으로 Join 이 타임아웃 없이 반환.
// ============================================================================
using System;
using System.Diagnostics;
using System.IO;
using StockManager.Common;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class AlphaFactoryShutdownTests
    {
        [Fact]
        public void Dispose_WhileWorkerResident_ReturnsPromptly()
        {
            var dbPath = Path.Combine(Path.GetTempPath(), $"alphashut_{Guid.NewGuid():N}.db");
            try
            {
                using var db = new LocalDatabase(dbPath);
                var gate = new OrderAttestationService();
                var f = new AutonomousAlphaFactory(db, gate);
                f.Start();
                System.Threading.Thread.Sleep(250);   // 첫 사이클 후 24h 상주 대기 진입 유도

                var sw = Stopwatch.StartNew();
                f.Dispose();
                sw.Stop();

                // 교정 전: 워커가 60초 sleep 중이라 Join(5s) 타임아웃까지 ≈5000ms 프리즈. 교정 후: 즉시(<수십 ms).
                Assert.True(sw.ElapsedMilliseconds < 2000,
                    $"Dispose 가 {sw.ElapsedMilliseconds}ms 소요 — 상주 대기가 인터럽터블하지 않음(종료 지연 회귀)");
            }
            finally { try { File.Delete(dbPath); } catch { } }
        }
    }
}
