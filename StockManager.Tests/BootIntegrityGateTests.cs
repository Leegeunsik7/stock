// ============================================================================
// BootIntegrityGateTests — [F7] 콜드부트 안티탬퍼 핸드셰이크 부팅 결속 · manifest.sig 런타임 수렴
//   릴리스 zip 과 동일 레이아웃(<root>/manifest.sig · host/ · proxy/)을 임시 폴더에 구성해 실행 폴더(host) 기준으로 수렴시킨다.
// ============================================================================
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using MS2E.Core;
using MS2E.Infrastructure;
using Xunit;

namespace StockManager.Tests
{
    [Collection("ConcurrencyStress")]
    public sealed class BootIntegrityGateTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "gm_boot_" + Guid.NewGuid().ToString("N"));
        private string Host => Path.Combine(_root, "host");
        private string Proxy => Path.Combine(_root, "proxy");
        private string Manifest => Path.Combine(_root, BootIntegrityGate.ManifestFileName);

        public BootIntegrityGateTests()
        {
            Directory.CreateDirectory(Host); Directory.CreateDirectory(Proxy);
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.exe"), new byte[] { 5, 6 });
            File.WriteAllBytes(Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe"), new byte[] { 7 });
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { /* 무관 */ } }

        private string Seal() { AssemblySealGenerator.GenerateReleaseSeal("12.14.1-GM", Host, Proxy, Manifest); return Manifest; }

        private static string MasterSeal(string manifest)
        {
            BootIntegrityGate.TryParse(File.ReadAllText(manifest), out _, out var seal, out _);
            return seal;
        }

        [Fact]
        public void Sealed_Install_Converges_To_Sealed_And_Unblocks()
        {
            Seal();
            using var g = BootIntegrityGate.Start(Host, runAsync: false);
            Assert.Equal(BootIntegrityStatus.Sealed, g.Status);
            Assert.False(g.ArmingBlocked);
            Assert.Equal(3, g.ArtifactCount);
            Assert.Equal(Manifest, g.ManifestPath);   // 실행 폴더(host) → 상위 폴더 탐지
        }

        [Fact]
        public void Missing_Manifest_Is_Unsealed_Blocked_Without_Zeroizing()
        {
            var purger = new VolatileMemoryPurger();
            var secret = new byte[] { 9, 9 };
            purger.Register(secret);
            using var g = BootIntegrityGate.Start(Host, purger: purger, runAsync: false);
            Assert.Equal(BootIntegrityStatus.Unsealed, g.Status);
            Assert.True(g.ArmingBlocked);
            Assert.False(purger.IsPurged);            // 개발/비봉인 빌드는 차단만 — 오탐 소거 없음
        }

        [Fact]
        public void Tampered_Binary_Trips_Zeroizes_Secrets_And_Fires_Kill()
        {
            Seal();
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.dll"), new byte[] { 1, 2, 3, 5 });
            var purger = new VolatileMemoryPurger();
            purger.Register(new byte[] { 0xAB, 0xCD });
            var token = new PoisonedSecretBuffer(new byte[] { 0x11 });
            int kills = 0;
            using var g = BootIntegrityGate.Start(Host, purger: purger, secrets: new[] { token }, onTrip: _ => kills++, runAsync: false);
            Assert.Equal(BootIntegrityStatus.Tampered, g.Status);
            Assert.True(g.ArmingBlocked);
            Assert.True(purger.IsPurged && purger.AllZero);
            Assert.True(token.IsPoisoned);
            Assert.Equal(1, kills);
        }

        [Fact]
        public void Artifact_Missing_From_Install_Is_Incomplete_And_Blocked()
        {
            Seal();
            File.Delete(Path.Combine(Proxy, "MS2E.KiwoomProxyHost.exe"));
            using var g = BootIntegrityGate.Start(Host, runAsync: false);
            Assert.Equal(BootIntegrityStatus.Incomplete, g.Status);
            Assert.True(g.ArmingBlocked);
        }

        [Fact]
        public void External_Trust_Anchor_Mismatch_Rejects_Rewritten_Manifest()
        {
            string m = Seal();
            string genuine = MasterSeal(m);
            // 공격자가 바이너리를 바꾸고 매니페스트까지 재생성(내부 일관) — 외부 앵커가 있으면 거부
            File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.dll"), new byte[] { 0xEE });
            Seal();
            using var g = BootIntegrityGate.Start(Host, expectedMasterSeal: genuine, runAsync: false);
            Assert.Equal(BootIntegrityStatus.ManifestInvalid, g.Status);
            Assert.True(g.ArmingBlocked);
            using var ok = BootIntegrityGate.Start(Host, expectedMasterSeal: MasterSeal(m), runAsync: false);   // 앵커 = 현 매니페스트
            Assert.Equal(BootIntegrityStatus.Sealed, ok.Status);
        }

        [Theory]
        [InlineData("host/../../evil.dll")]
        [InlineData("host/sub\\\\evil.dll")]
        [InlineData("other/x.dll")]
        [InlineData("noslash.dll")]
        public void Path_Escaping_Or_Unknown_Role_Names_Are_Rejected(string name)
        {
            Seal();
            string text = File.ReadAllText(Manifest).Replace("\"host/MS2E.StockSuite.exe\"", "\"" + name + "\"");
            File.WriteAllText(Manifest, text);
            using var g = BootIntegrityGate.Start(Host, runAsync: false);
            Assert.True(g.ArmingBlocked);
            Assert.NotEqual(BootIntegrityStatus.Sealed, g.Status);
        }

        [Fact]
        public void Garbage_Manifest_Is_Invalid_Not_A_Crash()
        {
            File.WriteAllText(Manifest, "{ not json");
            using var g = BootIntegrityGate.Start(Host, runAsync: false);
            Assert.Equal(BootIntegrityStatus.ManifestInvalid, g.Status);
        }

        [Fact]
        public void Async_Start_Blocks_Arming_Until_Converged()
        {
            Seal();
            using var g = BootIntegrityGate.Start(Host);   // 스레드풀 검증 — 반환 즉시에는 Verifying(차단) 또는 이미 Sealed
            Assert.True(g.Status is BootIntegrityStatus.Verifying or BootIntegrityStatus.Sealed);
            if (g.Status == BootIntegrityStatus.Verifying) Assert.True(g.ArmingBlocked);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (g.Status == BootIntegrityStatus.Verifying && sw.Elapsed < TimeSpan.FromSeconds(10)) System.Threading.Thread.Sleep(5);
            Assert.Equal(BootIntegrityStatus.Sealed, g.Status);
        }

        [Fact]
        public void Runtime_Binary_Replacement_Is_Refused_While_Sealed()
        {
            Seal();
            var purger = new VolatileMemoryPurger();
            purger.Register(new byte[] { 3 });
            using var g = BootIntegrityGate.Start(Host, purger: purger, monitorInterval: TimeSpan.FromMilliseconds(10), runAsync: false);
            Assert.Equal(BootIntegrityStatus.Sealed, g.Status);
            // 파일은 FileShare.Read 로 보유 중이라 쓰기 교체 불가 — 공유 위반이 곧 방어(교체 시도 자체 차단)
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(Path.Combine(Host, "MS2E.StockSuite.dll"), new byte[] { 0 }));
            Assert.False(g.ArmingBlocked);
        }

        [Fact]
        public void Host_Composition_Root_Binds_Gate_Into_Arming_BlockWhile()
        {
            // BlazorHomeForm 의 blockWhile 람다(클로저 메서드)가 BootIntegrityGate.ArmingBlocked 를 실제로 판독하는지 IL 로 확인.
            var getter = typeof(BootIntegrityGate).GetProperty(nameof(BootIntegrityGate.ArmingBlocked))!.GetMethod!;
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            bool found = false;
            foreach (var t in typeof(StockManager.Blazor.BlazorHomeForm).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                foreach (var m in t.GetMethods(All))
                {
                    var il = m.GetMethodBody()?.GetILAsByteArray();
                    if (il is null) continue;
                    for (int i = 0; i + 4 < il.Length && !found; i++)
                        if (il[i] is 0x28 or 0x6F)
                            try { found = m.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == getter; } catch (ArgumentException) { }
                }
            Assert.True(found, "BlazorHomeForm 무장 술어가 BootIntegrityGate.ArmingBlocked 를 판독하지 않음");
        }
    }
}
