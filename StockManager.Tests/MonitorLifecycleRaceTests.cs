// ============================================================================
// MonitorLifecycleRaceTests — [출하 차단 실결함] 백그라운드 감시 스레드 시작 ↔ 처분 경합
//
//  실측: release-package.ps1 [3/10] 과 이전 전체 스위트 1회에서 테스트 호스트가 조기 종료(684 · 1,560/3,353).
//  Windows 이벤트 로그 1026: testhost.exe 미처리 ObjectDisposedException(ManualResetEventSlim) ←
//  AssemblyAntiTamperHandshake.MonitorLoop ← StartMonitor. 원인: CAS 로 스레드 게시 후 Start 하는 창에서 다른 스레드의 Dispose 가
//  미시작 스레드(IsAlive=false)를 보고 Join 생략 → _stop 처분 → 시작된 모니터가 처분된 이벤트를 Wait → 프로세스 종료.
//  실제 경로 = BootIntegrityGate 비동기 수렴(Task.Run Converge → StartMonitor) ↔ 게이트 Dispose. 앱에서도 종료 직전 같은 크래시.
//  동반 결함: 게이트가 먼저 처분되면 수렴이 만든 핸드셰이크가 영영 처분되지 않아 아티팩트 파일 핸들(FileShare.Read) 누수.
//  §1 게이트 처분 후 비동기 수렴 → 아티팩트 핸들 해제(독점 열기 성공) · §2·§3 시작/처분 동시 경합 반복 — 프로세스 생존 · 계약 유지.
// ============================================================================
using System;
using System.IO;
using System.Threading;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class MonitorLifecycleRaceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mon_race_" + Guid.NewGuid().ToString("N"));
        private string Host => Path.Combine(_root, "host");
        private string Proxy => Path.Combine(_root, "proxy");

        public MonitorLifecycleRaceTests()
        {
            Directory.CreateDirectory(Host); Directory.CreateDirectory(Proxy);
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.exe"), new byte[] { 5, 6 });
            File.WriteAllBytes(Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe"), new byte[] { 7 });
            AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", Host, Proxy, Path.Combine(_root, BootIntegrityGate.ManifestFileName));
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }

        private bool ArtifactsExclusivelyOpenable(TimeSpan within)
        {
            var deadline = DateTime.UtcNow + within;
            string[] files = { Path.Combine(Host, "MS2E.StockSuite.dll"), Path.Combine(Host, "MS2E.StockSuite.exe"), Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe") };
            while (true)
            {
                try
                {
                    foreach (var f in files) using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    return true;
                }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(20); }
                catch (IOException) { return false; }
            }
        }

        [Fact]
        public void Gate_DisposedBeforeAsyncConverge_ReleasesArtifactHandles()
        {
            // [수정 전 Red] 처분이 수렴의 핸드셰이크 생성보다 먼저면 핸드셰이크 미처분 → 파일 핸들 누수(독점 열기 영구 실패)
            for (int i = 0; i < 25; i++)
            {
                var g = BootIntegrityGate.Start(Host, runAsync: true, monitorInterval: TimeSpan.FromMilliseconds(1));
                g.Dispose();
                Assert.True(g.ArmingBlocked);
                Assert.True(ArtifactsExclusivelyOpenable(TimeSpan.FromSeconds(5)), $"반복 {i}: 처분된 게이트의 아티팩트 핸들 누수");
            }
        }

        [Fact]
        public void Gate_AsyncConverge_vs_Dispose_Race_ProcessSurvives()
        {
            for (int i = 0; i < 200; i++)
            {
                var g = BootIntegrityGate.Start(Host, runAsync: true, monitorInterval: TimeSpan.FromMilliseconds(1));
                Thread.SpinWait(i * 50);                                       // 처분 시점을 수렴 구간 전체에 흩뿌림
                g.Dispose();
            }
            Thread.Sleep(200);                                                 // 늦게 시작된 모니터가 처분된 이벤트를 만질 시간
            Assert.True(ArtifactsExclusivelyOpenable(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void Handshake_StartMonitor_vs_Dispose_ConcurrentRace_NoCrash_ContractKept()
        {
            var region = GC.AllocateArray<byte>(4096, pinned: true);
            for (int i = 0; i < 3_000; i++)
            {
                var h = new AssemblyAntiTamperHandshake(Array.Empty<(string, byte[])>());
                h.RegisterRegion(region);
                using var go = new Barrier(2);
                var starter = new Thread(() =>
                {
                    go.SignalAndWait();
                    try { h.StartMonitor(TimeSpan.FromMilliseconds(1)); } catch (ObjectDisposedException) { /* 처분이 먼저 = 계약대로 거부 */ }
                });
                starter.Start();
                go.SignalAndWait();
                h.Dispose();
                starter.Join();
                Assert.False(h.IsTripped);                                     // 정상 처분은 변조 Trip 아님
                Assert.Throws<ObjectDisposedException>(() => h.StartMonitor(TimeSpan.FromMilliseconds(1)));
            }
            Thread.Sleep(100);
        }

        [Fact]
        public void ForensicChainGuard_StartMonitor_vs_Dispose_ConcurrentRace_NoCrash()
        {
            for (int i = 0; i < 3_000; i++)
            {
                var stream = new ZeroAllocForensicAuditStream(16);
                var guard = new ForensicChainDisarmInterlock(stream);
                using var go = new Barrier(2);
                var starter = new Thread(() =>
                {
                    go.SignalAndWait();
                    try { guard.StartMonitor(TimeSpan.FromMilliseconds(1)); } catch (ObjectDisposedException) { }
                });
                starter.Start();
                go.SignalAndWait();
                guard.Dispose();
                starter.Join();
                Assert.False(guard.Tripped);
            }
            Thread.Sleep(100);
        }
    }
}
