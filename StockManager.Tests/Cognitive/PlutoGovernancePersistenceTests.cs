// ============================================================================
// PlutoGovernancePersistenceTests — v12.18.69-DEV: 플루토 프로 거버너 토글 영속화(로컬 DB 설정 키 · 비동기 저장 · 재기동 복원)
//
//  §1 어댑터 — 미저장 = null · 저장/판독 왕복 · 재기동(새 LocalDatabase 인스턴스) 후 유지 · 연속 토글 200회 = 최종값 수렴 · 동시 저장 스레드 수렴 ·
//     고정 키 1개만 기입(타 설정 무변경) · 해석 불가 값 = null · 실패(폐기된 연결) = false + 계수 · 실패 후 다음 저장 재개
//  §2 기동 복원 — 호스트와 같은 RuntimeConfig(환경변수 → DB) 판독 = 저장값 · 미저장 = 기본 true · 호스트 소스 결선(같은 키 · DI 등록)
//  §3 페이지 — 토글 → 비동기 저장(OFF/ON) · 저장 상태 뱃지 · 기동 시 저장값 뱃지 · 저장소 미결선 정직 표시 · 저장 실패 뱃지
//  §4 경계 — UiSettingWriteGuard 허용 목록 미확장 · 포트 표면 = 고정 키 불리언(임의 키 기입 멤버 0) · RCL 페이지/포트 DB 참조 0
// ============================================================================
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MS2E.Infrastructure;
using MS2E.UI.Portable.Pages.Trading;
using MS2E.UI.Portable.Ports.Trading;
using StockManager.Adapters;
using StockManager.Engines.Arena.Crypto;
using StockManager.Engines.Arena.Domestic;
using StockManager.Engines.Arena.Us;
using Xunit;

namespace StockManager.Tests.Cognitive
{
    [Collection("ConcurrencyStress")]
    public sealed class PlutoGovernancePersistenceTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-govpref-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "portfolio.db");

        public PlutoGovernancePersistenceTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ── §1 어댑터 ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Save_Roundtrip_And_Survives_Restart()
        {
            using (var db = new LocalDatabase(DbPath))
            {
                var pref = new ArenaPreferenceAdapter(db);
                Assert.Null(pref.StoredGovernanceEnabled);
                Assert.True(await pref.SaveGovernanceEnabledAsync(false));
                Assert.False(pref.StoredGovernanceEnabled);
                Assert.Equal("false", db.GetSetting(ArenaPreferenceAdapter.GovernanceKey));
                Assert.True(await pref.SaveGovernanceEnabledAsync(true));
                Assert.True(pref.StoredGovernanceEnabled);
                Assert.True(await pref.SaveGovernanceEnabledAsync(false));
            }
            using (var db2 = new LocalDatabase(DbPath))                                  // 재기동
                Assert.False(new ArenaPreferenceAdapter(db2).StoredGovernanceEnabled);
        }

        [Fact]
        public async Task Rapid_Toggles_Converge_To_Last_Value()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            var tasks = new Task<bool>[200];
            for (int i = 0; i < tasks.Length; i++) tasks[i] = pref.SaveGovernanceEnabledAsync(i % 2 == 0);   // 마지막 = i 199 → false
            Assert.All(await Task.WhenAll(tasks), Assert.True);
            Assert.False(pref.StoredGovernanceEnabled);
            Assert.True(await pref.SaveGovernanceEnabledAsync(true));                    // 드레인 종료 후 새 저장 = 새 드레인(유실 0)
            Assert.True(pref.StoredGovernanceEnabled);
            Assert.Equal(0, pref.SaveFailures);
        }

        [Fact]
        public async Task Concurrent_Writers_Converge_To_Final_Request()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            using var go = new ManualResetEventSlim(false);
            var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
            {
                go.Wait();
                for (int i = 0; i < 100; i++) await pref.SaveGovernanceEnabledAsync(((i + w) & 1) == 0);
            })).ToArray();
            go.Set();
            await Task.WhenAll(writers);
            Assert.True(await pref.SaveGovernanceEnabledAsync(true));                    // 정적 상태 이후 최종 요청
            Assert.True(pref.StoredGovernanceEnabled);
            Assert.True(await pref.SaveGovernanceEnabledAsync(false));
            Assert.False(pref.StoredGovernanceEnabled);
        }

        [Fact]
        public async Task Writes_Only_The_Fixed_Key_And_Rejects_Garbage_Read()
        {
            using var db = new LocalDatabase(DbPath);
            db.SetSetting("Broker.LiveArmed", "0");
            db.SetSetting("Arena.UpbitStreamEnabled", "false");
            var pref = new ArenaPreferenceAdapter(db);
            Assert.True(await pref.SaveGovernanceEnabledAsync(true));
            Assert.Equal("0", db.GetSetting("Broker.LiveArmed"));
            Assert.Equal("false", db.GetSetting("Arena.UpbitStreamEnabled"));
            db.SetSetting(ArenaPreferenceAdapter.GovernanceKey, "maybe");
            Assert.Null(pref.StoredGovernanceEnabled);
            db.SetSetting(ArenaPreferenceAdapter.GovernanceKey, " OFF ");
            Assert.False(pref.StoredGovernanceEnabled);
        }

        [Fact]
        public async Task Failure_Is_Counted_And_Next_Save_Resumes()
        {
            var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            db.Dispose();
            Assert.False(await pref.SaveGovernanceEnabledAsync(false));
            Assert.Equal(1, pref.SaveFailures);
            Assert.Null(pref.StoredGovernanceEnabled);                                   // 판독 실패 = null(예외 전파 0)

            using var db2 = new LocalDatabase(DbPath);
            Assert.Null(db2.GetSetting(ArenaPreferenceAdapter.GovernanceKey));          // 실패 = 기록 0
            var pref2 = new ArenaPreferenceAdapter(db2);
            Assert.True(await pref2.SaveGovernanceEnabledAsync(false));
            Assert.False(pref2.StoredGovernanceEnabled);
        }

        // ── §2 기동 복원 ────────────────────────────────────────────────────────

        [Fact]
        public async Task Boot_RuntimeConfig_Restores_Saved_Value_Default_True()
        {
            using (var db = new LocalDatabase(DbPath))
            {
                var cfg = new RuntimeConfig(k => Environment.GetEnvironmentVariable(k) ?? db.GetSetting(k));    // 호스트와 같은 precedence
                Assert.True(cfg.GetBool(ArenaPreferenceAdapter.GovernanceKey, true));    // 미저장 = 기본 켜짐
                Assert.True(await new ArenaPreferenceAdapter(db).SaveGovernanceEnabledAsync(false));
            }
            using (var db = new LocalDatabase(DbPath))
            {
                var cfg = new RuntimeConfig(k => Environment.GetEnvironmentVariable(k) ?? db.GetSetting(k));
                bool restored = cfg.GetBool(ArenaPreferenceAdapter.GovernanceKey, true);
                Assert.False(restored);
                var krx = new KrxArenaEngine();
                krx.EnableGovernance(restored);                                          // 호스트 기동 주입과 같은 경로
                Assert.False(krx.GovernanceEnabled);
            }
        }

        [Fact]
        public void Host_Source_Wires_Same_Key_And_Registers_Port()
        {
            string form = RepoFile("StockManager", "Blazor", "BlazorHomeForm.cs");
            Assert.Contains($"cfg.GetBool(\"{ArenaPreferenceAdapter.GovernanceKey}\", true)", form);
            Assert.Contains("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaPreferencePort>(new StockManager.Adapters.ArenaPreferenceAdapter(db));", form);
        }

        // ── §3 페이지 ───────────────────────────────────────────────────────────

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut, KrxArenaEngine Krx) Render(IArenaPreferencePort? prefs)
        {
            var ctx = new Bunit.TestContext();
            var krx = new KrxArenaEngine();
            ctx.Services.AddSingleton<IKrxArenaPort>(new KrxArenaPortAdapter(krx));
            ctx.Services.AddSingleton<IUsArenaPort>(new UsArenaPortAdapter(new UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton(prefs ?? UnwiredArenaPreferencePort.Instance);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);   // [v12.18.77-DEV] 학습 엔진 미결선
            ctx.Services.AddSingleton<IPlutoRecommendationPort>(UnwiredPlutoRecommendationPort.Instance);   // [v12.18.78-DEV] 추천 엔진 미결선
            return (ctx, ctx.RenderComponent<DualArenaPage>(), krx);
        }

        [Fact]
        public void Page_Toggle_Persists_Asynchronously_And_Shows_Status()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            var (ctx, cut, krx) = Render(pref);
            using (ctx)
            {
                var badge = cut.Find(".da-gov-persist");
                Assert.Contains("저장값 없음", badge.TextContent);

                cut.Find(".da-gov-toggle").Click();                                      // OFF → ON
                cut.WaitForAssertion(() => Assert.Equal("ok", cut.Find(".da-gov-persist").GetAttribute("data-persist")), TimeSpan.FromSeconds(10));
                Assert.Contains("ON 유지", cut.Find(".da-gov-persist").TextContent);
                Assert.True(krx.GovernanceEnabled);
                Assert.True(pref.StoredGovernanceEnabled);

                cut.Find(".da-gov-toggle").Click();                                      // ON → OFF
                cut.WaitForAssertion(() => Assert.Contains("OFF 유지", cut.Find(".da-gov-persist").TextContent), TimeSpan.FromSeconds(10));
                Assert.False(pref.StoredGovernanceEnabled);
                Assert.Equal("false", db.GetSetting(ArenaPreferenceAdapter.GovernanceKey));
            }
        }

        [Fact]
        public async Task Page_Shows_Stored_Value_On_Boot_And_Honest_States()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            Assert.True(await pref.SaveGovernanceEnabledAsync(false));
            var (ctx, cut, _) = Render(pref);
            using (ctx) Assert.Contains("저장값 OFF", cut.Find(".da-gov-persist").TextContent);

            var (ctx2, cut2, krx2) = Render(null);                                       // 저장소 미결선(null 객체)
            using (ctx2)
            {
                Assert.Equal("none", cut2.Find(".da-gov-persist").GetAttribute("data-persist"));
                Assert.Contains("저장소 미결선", cut2.Find(".da-gov-persist").TextContent);
                cut2.Find(".da-gov-toggle").Click();                                     // 토글 자체는 동작(런타임 상태)
                Assert.True(krx2.GovernanceEnabled);
                Assert.Contains("저장소 미결선", cut2.Find(".da-gov-persist").TextContent);
            }

            var broken = new LocalDatabase(Path.Combine(_dir, "broken.db"));
            var brokenPref = new ArenaPreferenceAdapter(broken);
            var (ctx3, cut3, _) = Render(brokenPref);
            using (ctx3)
            {
                broken.Dispose();
                cut3.Find(".da-gov-toggle").Click();
                cut3.WaitForAssertion(() => Assert.Equal("bad", cut3.Find(".da-gov-persist").GetAttribute("data-persist")), TimeSpan.FromSeconds(10));
                Assert.Contains("저장 실패", cut3.Find(".da-gov-persist").TextContent);
                Assert.Equal(1, brokenPref.SaveFailures);
            }
        }

        // ── §4 경계 ─────────────────────────────────────────────────────────────

        private static string RepoFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StockManager.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
        }

        [Fact]
        public async Task Unwired_Null_Object_Never_Writes()
        {
            var u = UnwiredArenaPreferencePort.Instance;
            Assert.False(u.IsWired);
            Assert.Null(u.StoredGovernanceEnabled);
            Assert.False(await u.SaveGovernanceEnabledAsync(true));
            Assert.Equal(0, u.SaveFailures);
            using var db = new LocalDatabase(DbPath);
            Assert.True(((IArenaPreferencePort)new ArenaPreferenceAdapter(db)).IsWired);
        }

        [Fact]
        public void Guard_Not_Widened_And_Port_Surface_Is_Fixed_Key_Boolean()
        {
            Assert.False(UiSettingWriteGuard.IsUiWritable(ArenaPreferenceAdapter.GovernanceKey));   // 범용 UI 기입 포트로는 여전히 거부
            Assert.False(UiSettingWriteGuard.IsUiWritable("Arena.UpbitStreamEnabled"));
            var methods = typeof(IArenaPreferencePort).GetMethods();
            Assert.All(methods, m => Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(string)));
            Assert.DoesNotContain(methods, m => m.Name.Contains("Arm", StringComparison.Ordinal) || m.Name.Contains("Setting", StringComparison.Ordinal));
            foreach (var src in new[] { RepoFile("MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor"), RepoFile("MS2E.UI.Portable", "Ports", "Trading", "ArenaPreferencePort.cs") })
            {
                Assert.DoesNotContain("LocalDatabase", src);
                Assert.DoesNotContain("Sqlite", src);
                Assert.DoesNotContain("StockManager.Adapters", src);
            }
        }
    }
}
