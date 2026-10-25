// ============================================================================
// ArenaAutoCopyPersistenceTests — v12.18.72-DEV: 플루토 자동 미러링 선호 영속화(아레나별 활성 + 배율 · 로컬 DB 설정 키 · 비동기 저장 · 재기동 복원)
//
//  §1 어댑터 — 미저장 = null · 저장/판독 왕복 · 재기동 후 유지 · 아레나 격리 · 연속 저장 = 최종값 수렴 · 범위 밖 아레나/배율 = 기록 0 ·
//     해석 불가/범위 밖 저장값 = null · 실패 = false + 계수 · 거버너 키 무간섭
//  §2 기동 복원 — 호스트와 같은 RuntimeConfig 판독 → 3대 엔진 SetAutoCopy(ON · 배율) · 미저장 = OFF/1.0x · 배율 오염 = 복원 생략(fail-closed) · 호스트 소스 결선
//  §3 페이지 — 토글/배율 → 비동기 저장 · 모드별 저장 뱃지 · 기동 복원값 = 배율 버튼 동기화 · 미결선 정직 표시
//  §4 경계 — 포트 표면(문자열 키 · Arm/Setting 멤버 0) · RCL 페이지/포트 DB 참조 0 · 모드 상수 = 아레나 식별자 · UI 기입 가드 미확장
// ============================================================================
using System;
using System.IO;
using System.Linq;
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

namespace StockManager.Tests.Engines.Arena
{
    [Collection("ConcurrencyStress")]
    public sealed class ArenaAutoCopyPersistenceTests : IDisposable
    {
        private const int Krx = ArenaPreferenceArena.Krx, Us = ArenaPreferenceArena.Us, Crypto = ArenaPreferenceArena.Crypto;
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-copypref-" + Guid.NewGuid().ToString("N"));
        private string DbPath => Path.Combine(_dir, "portfolio.db");

        public ArenaAutoCopyPersistenceTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static RuntimeConfig HostCfg(LocalDatabase db) => new(k => Environment.GetEnvironmentVariable(k) ?? db.GetSetting(k));   // 호스트와 같은 precedence

        // ── §1 어댑터 ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Save_Roundtrip_Survives_Restart_And_Arenas_Are_Isolated()
        {
            using (var db = new LocalDatabase(DbPath))
            {
                var pref = new ArenaPreferenceAdapter(db);
                Assert.Null(pref.StoredAutoCopy(Krx));
                Assert.True(await pref.SaveAutoCopyAsync(Krx, true, 0.5m));
                Assert.True(await pref.SaveAutoCopyAsync(Crypto, false, 1.0m));
                Assert.Equal(new ArenaAutoCopyPreference(true, 0.5m), pref.StoredAutoCopy(Krx));
                Assert.Equal(new ArenaAutoCopyPreference(false, 1.0m), pref.StoredAutoCopy(Crypto));
                Assert.Null(pref.StoredAutoCopy(Us));                                    // 아레나 격리(교차 기입 0)
                Assert.Equal("0.5", db.GetSetting("Arena.Krx.AutoCopyRatio"));
                Assert.Equal("true", db.GetSetting("Arena.Krx.AutoCopyEnabled"));
                Assert.Null(db.GetSetting(ArenaPreferenceAdapter.GovernanceKey));        // 거버너 키 무간섭
            }
            using (var db2 = new LocalDatabase(DbPath))                                  // 재기동
            {
                var pref2 = new ArenaPreferenceAdapter(db2);
                Assert.Equal(new ArenaAutoCopyPreference(true, 0.5m), pref2.StoredAutoCopy(Krx));
                Assert.Equal(new ArenaAutoCopyPreference(false, 1.0m), pref2.StoredAutoCopy(Crypto));
            }
        }

        [Fact]
        public async Task Rapid_Saves_Converge_To_Last_Value_Per_Arena()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            var tasks = new Task<bool>[300];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = pref.SaveAutoCopyAsync(i % 3, i % 2 == 0, i % 4 < 2 ? 0.5m : 1.0m);
            Assert.All(await Task.WhenAll(tasks), Assert.True);
            // 마지막 요청: Krx = i 297(OFF · 0.5x) · Us = i 298(ON · 1.0x) · Crypto = i 299(OFF · 1.0x)
            Assert.Equal(new ArenaAutoCopyPreference(false, 0.5m), pref.StoredAutoCopy(Krx));
            Assert.Equal(new ArenaAutoCopyPreference(true, 1.0m), pref.StoredAutoCopy(Us));
            Assert.Equal(new ArenaAutoCopyPreference(false, 1.0m), pref.StoredAutoCopy(Crypto));
            Assert.True(await pref.SaveAutoCopyAsync(Us, true, 0.5m));                   // 드레인 종료 후 새 저장 = 새 드레인(유실 0)
            Assert.Equal(new ArenaAutoCopyPreference(true, 0.5m), pref.StoredAutoCopy(Us));
            Assert.Equal(0, pref.SaveFailures);
        }

        [Fact]
        public async Task Invalid_Requests_Write_Nothing_And_Garbage_Reads_Null()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            Assert.False(await pref.SaveAutoCopyAsync(3, true, 1.0m));                   // 범위 밖 아레나
            Assert.False(await pref.SaveAutoCopyAsync(-1, true, 1.0m));
            Assert.False(await pref.SaveAutoCopyAsync(Krx, true, 0.05m));                // 배율 0.1x 미만
            Assert.False(await pref.SaveAutoCopyAsync(Krx, true, 3.5m));                 // 배율 3.0x 초과
            Assert.False(await pref.SaveAutoCopyAsync(Krx, true, 0.00001m));             // bp 미만 단위
            Assert.Null(db.GetSetting("Arena.Krx.AutoCopyEnabled"));
            Assert.Null(db.GetSetting("Arena.Krx.AutoCopyRatio"));
            Assert.Equal(0, pref.SaveFailures);
            Assert.Null(pref.StoredAutoCopy(7));

            db.SetSetting("Arena.Us.AutoCopyEnabled", "maybe");
            Assert.Null(pref.StoredAutoCopy(Us));
            db.SetSetting("Arena.Us.AutoCopyEnabled", " ON ");
            Assert.Equal(new ArenaAutoCopyPreference(true, 1.0m), pref.StoredAutoCopy(Us));   // 배율 미저장 = 기본 1.0x
            db.SetSetting("Arena.Us.AutoCopyRatio", "9.0");
            Assert.Null(pref.StoredAutoCopy(Us));                                        // 범위 밖 배율 = 해석 거부
            db.SetSetting("Arena.Us.AutoCopyRatio", "0,5");
            Assert.Null(pref.StoredAutoCopy(Us));                                        // 문화권 의존 표기 거부
        }

        [Fact]
        public async Task Failure_Is_Counted_And_Next_Save_Resumes()
        {
            var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            db.Dispose();
            Assert.False(await pref.SaveAutoCopyAsync(Krx, true, 0.5m));
            Assert.Equal(1, pref.SaveFailures);
            Assert.Null(pref.StoredAutoCopy(Krx));                                       // 판독 실패 = null(예외 전파 0)

            using var db2 = new LocalDatabase(DbPath);
            Assert.Null(db2.GetSetting("Arena.Krx.AutoCopyEnabled"));
            var pref2 = new ArenaPreferenceAdapter(db2);
            Assert.True(await pref2.SaveAutoCopyAsync(Krx, true, 0.5m));
            Assert.Equal(new ArenaAutoCopyPreference(true, 0.5m), pref2.StoredAutoCopy(Krx));
        }

        // ── §2 기동 복원 ────────────────────────────────────────────────────────

        [Fact]
        public async Task Boot_Restore_Injects_Saved_State_Into_Three_Engines()
        {
            using (var db = new LocalDatabase(DbPath))
            {
                var pref = new ArenaPreferenceAdapter(db);
                Assert.True(await pref.SaveAutoCopyAsync(Krx, true, 0.5m));
                Assert.True(await pref.SaveAutoCopyAsync(Us, true, 1.0m));
                Assert.True(await pref.SaveAutoCopyAsync(Crypto, false, 0.5m));
            }
            using (var db = new LocalDatabase(DbPath))                                   // 재기동
            {
                var cfg = HostCfg(db);
                var krx = new KrxArenaEngine();
                var us = new UsTechArenaEngine();
                var crypto = new UpbitCryptoArenaEngine();
                Assert.True(ArenaPreferenceAdapter.RestoreAutoCopy(cfg, Krx, krx.SetAutoCopy));
                Assert.True(ArenaPreferenceAdapter.RestoreAutoCopy(cfg, Us, us.SetAutoCopy));
                Assert.True(ArenaPreferenceAdapter.RestoreAutoCopy(cfg, Crypto, crypto.SetAutoCopy));
                Assert.True(krx.AutoCopyEnabled);
                Assert.Equal(5_000, krx.AutoCopyRatioBp);
                Assert.True(us.AutoCopyEnabled);
                Assert.Equal(10_000, us.AutoCopyRatioBp);
                Assert.False(crypto.AutoCopyEnabled);
                Assert.Equal(5_000, crypto.AutoCopyRatioBp);
            }
        }

        [Fact]
        public void Boot_Restore_Defaults_Off_And_Fails_Closed_On_Corrupt_Ratio()
        {
            using var db = new LocalDatabase(DbPath);
            var krx = new KrxArenaEngine();
            Assert.True(ArenaPreferenceAdapter.RestoreAutoCopy(HostCfg(db), Krx, krx.SetAutoCopy));   // 미저장 = OFF · 1.0x
            Assert.False(krx.AutoCopyEnabled);
            Assert.Equal(10_000, krx.AutoCopyRatioBp);

            db.SetSetting("Arena.Us.AutoCopyEnabled", "true");
            db.SetSetting("Arena.Us.AutoCopyRatio", "50");                               // 범위 밖(50x)
            var us = new UsTechArenaEngine();
            Assert.False(ArenaPreferenceAdapter.RestoreAutoCopy(HostCfg(db), Us, us.SetAutoCopy));
            Assert.False(us.AutoCopyEnabled);                                            // 오염 = ON 복원 안 함(fail-closed)

            db.SetSetting("Arena.Crypto.AutoCopyEnabled", "garbage");
            db.SetSetting("Arena.Crypto.AutoCopyRatio", "0.5");
            var crypto = new UpbitCryptoArenaEngine();
            Assert.True(ArenaPreferenceAdapter.RestoreAutoCopy(HostCfg(db), Crypto, crypto.SetAutoCopy));
            Assert.False(crypto.AutoCopyEnabled);                                        // 활성 해석 불가 = 꺼짐
            Assert.Equal(5_000, crypto.AutoCopyRatioBp);
        }

        [Fact]
        public void Host_Source_Restores_Three_Arenas_After_Port_Registration()
        {
            string form = RepoFile("StockManager", "Blazor", "BlazorHomeForm.cs");
            int reg = form.IndexOf("services.AddSingleton<MS2E.UI.Portable.Ports.Trading.IArenaPreferencePort>(new StockManager.Adapters.ArenaPreferenceAdapter(db));", StringComparison.Ordinal);
            Assert.True(reg > 0);
            foreach (var (arena, engine) in new[] { ("Krx", "krxArena"), ("Us", "usArena"), ("Crypto", "cryptoArena") })
                Assert.True(form.IndexOf($"RestoreAutoCopy(cfg, MS2E.UI.Portable.Ports.Trading.ArenaPreferenceArena.{arena}, {engine}.SetAutoCopy);", StringComparison.Ordinal) > reg);
        }

        // ── §3 페이지 ───────────────────────────────────────────────────────────

        // 아레나 섹션(카피 바 포함) = 펌프 결선 + 정산 틱 > 0 일 때만 렌더 → 피더 펌프 1회 + 정규장 호가 몇 건.
        private static KrxArenaPortAdapter LiveKrx(KrxArenaEngine e)
        {
            var f = new KrxArenaQuoteFeeder(e);
            var t = new DateTime(2026, 10, 22, 10, 0, 0);                                // 목요일 10:00 KST
            f.Pump(t.AddHours(-9).Ticks);
            for (int i = 0; i < 3; i++) e.OnQuote(5930, 70_000, 70_100, 1000, 1000, 100, t = t.AddMilliseconds(100));
            return new KrxArenaPortAdapter(e, f);
        }

        private static UsArenaPortAdapter LiveUs(UsTechArenaEngine e)
        {
            var f = new UsArenaQuoteFeeder(e);
            var t = new DateTime(2026, 10, 22, 23, 0, 0);                                // 미 정규장(EDT) 23:00 KST
            f.Pump(t.AddHours(-9).Ticks);
            for (int i = 0; i < 3; i++) e.OnQuote(UsTechUniverse.Symbol(UsTechUniverse.Nvda), 18_000, 18_010, 1000, 1000, t = t.AddMilliseconds(100));
            return new UsArenaPortAdapter(e, f);
        }

        private static (Bunit.TestContext Ctx, IRenderedComponent<DualArenaPage> Cut) Render(IArenaPreferencePort prefs, KrxArenaEngine krx, UsTechArenaEngine? us = null)
        {
            var ctx = new Bunit.TestContext();
            ctx.Services.AddSingleton<IKrxArenaPort>(LiveKrx(krx));
            ctx.Services.AddSingleton<IUsArenaPort>(LiveUs(us ?? new UsTechArenaEngine()));
            ctx.Services.AddSingleton<ICryptoArenaPort>(new CryptoArenaPortAdapter(new UpbitCryptoArenaEngine()));
            ctx.Services.AddSingleton(prefs);
            ctx.Services.AddSingleton<IPlutoLearningPort>(UnwiredPlutoLearningPort.Instance);   // [v12.18.77-DEV] 학습 엔진 미결선
            return (ctx, ctx.RenderComponent<DualArenaPage>());
        }

        [Fact]
        public void Page_Toggle_And_Ratio_Persist_Asynchronously_With_Badge()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            var krx = new KrxArenaEngine();
            var (ctx, cut) = Render(pref, krx);
            using (ctx)
            {
                Assert.Contains("저장값 없음", cut.Find(".da-krx .da-copy-persist").TextContent);

                cut.Find(".da-krx .da-copy-ratio[data-ratio=\"0.5x\"]").Click();
                cut.WaitForAssertion(() => Assert.Contains("OFF · 0.5x 유지", cut.Find(".da-krx .da-copy-persist").TextContent), TimeSpan.FromSeconds(10));
                Assert.Equal(new ArenaAutoCopyPreference(false, 0.5m), pref.StoredAutoCopy(Krx));

                cut.Find(".da-krx .da-copy-toggle").Click();                             // OFF → ON
                cut.WaitForAssertion(() => Assert.Contains("ON · 0.5x 유지", cut.Find(".da-krx .da-copy-persist").TextContent), TimeSpan.FromSeconds(10));
                Assert.Equal("ok", cut.Find(".da-krx .da-copy-persist").GetAttribute("data-copy-persist"));
                Assert.True(krx.AutoCopyEnabled);
                Assert.Equal(new ArenaAutoCopyPreference(true, 0.5m), pref.StoredAutoCopy(Krx));
                Assert.Null(pref.StoredAutoCopy(Us));                                    // 비활성 모드 무기입
            }
        }

        [Fact]
        public async Task Page_Boot_Reflects_Restored_Engine_And_Stored_Badge()
        {
            using var db = new LocalDatabase(DbPath);
            var pref = new ArenaPreferenceAdapter(db);
            Assert.True(await pref.SaveAutoCopyAsync(Krx, true, 0.5m));
            Assert.True(await pref.SaveAutoCopyAsync(Us, false, 1.0m));
            var krx = new KrxArenaEngine();
            var us = new UsTechArenaEngine();
            var cfg = HostCfg(db);
            ArenaPreferenceAdapter.RestoreAutoCopy(cfg, Krx, krx.SetAutoCopy);           // 호스트 기동 복원과 같은 경로
            ArenaPreferenceAdapter.RestoreAutoCopy(cfg, Us, us.SetAutoCopy);
            var (ctx, cut) = Render(pref, krx, us);
            using (ctx)
            {
                Assert.Contains("ON", cut.Find(".da-krx .da-copy-toggle").TextContent);
                Assert.Contains("on", cut.Find(".da-krx .da-copy-ratio[data-ratio=\"0.5x\"]").ClassName);   // 배율 버튼 = 복원 배율
                Assert.Contains("저장값 ON · 0.5x", cut.Find(".da-krx .da-copy-persist").TextContent);

                cut.Find(".da-mode-tab[data-mode=\"us\"]").Click();                     // 모드 전환 = 해당 엔진 배율로 동기화
                Assert.Contains("OFF", cut.Find(".da-us .da-copy-toggle").TextContent);
                Assert.Contains("on", cut.Find(".da-us .da-copy-ratio[data-ratio=\"1.0x\"]").ClassName);
                Assert.Contains("저장값 OFF · 1.0x", cut.Find(".da-us .da-copy-persist").TextContent);
            }
        }

        [Fact]
        public void Page_Unwired_Store_Is_Honest_And_Toggle_Still_Works()
        {
            var krx = new KrxArenaEngine();
            var (ctx, cut) = Render(UnwiredArenaPreferencePort.Instance, krx);
            using (ctx)
            {
                Assert.Equal("none", cut.Find(".da-krx .da-copy-persist").GetAttribute("data-copy-persist"));
                cut.Find(".da-krx .da-copy-toggle").Click();
                Assert.True(krx.AutoCopyEnabled);                                        // 런타임 상태는 동작
                Assert.Contains("저장소 미결선", cut.Find(".da-krx .da-copy-persist").TextContent);
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
        public async Task Boundary_Port_Surface_Mode_Ids_And_Pure_Rcl()
        {
            Assert.Equal(DualArenaPage.ModeKrx, ArenaPreferenceArena.Krx);
            Assert.Equal(DualArenaPage.ModeUs, ArenaPreferenceArena.Us);
            Assert.Equal(DualArenaPage.ModeCrypto, ArenaPreferenceArena.Crypto);
            var methods = typeof(IArenaPreferencePort).GetMethods();
            Assert.All(methods, m => Assert.DoesNotContain(m.GetParameters(), p => p.ParameterType == typeof(string)));
            Assert.DoesNotContain(methods, m => m.Name.Contains("Arm", StringComparison.Ordinal) || m.Name.Contains("Setting", StringComparison.Ordinal));
            for (int a = Krx; a <= Crypto; a++)
            {
                Assert.False(UiSettingWriteGuard.IsUiWritable(ArenaPreferenceAdapter.AutoCopyEnabledKey(a)));   // 범용 UI 기입 포트로는 여전히 거부
                Assert.False(UiSettingWriteGuard.IsUiWritable(ArenaPreferenceAdapter.AutoCopyRatioKey(a)));
            }
            var u = UnwiredArenaPreferencePort.Instance;
            Assert.Null(u.StoredAutoCopy(Krx));
            Assert.False(await u.SaveAutoCopyAsync(Krx, true, 1.0m));
            foreach (var src in new[] { RepoFile("MS2E.UI.Portable", "Pages", "Trading", "DualArenaPage.razor"), RepoFile("MS2E.UI.Portable", "Ports", "Trading", "ArenaPreferencePort.cs") })
            {
                Assert.DoesNotContain("LocalDatabase", src);
                Assert.DoesNotContain("Sqlite", src);
                Assert.DoesNotContain("StockManager.Adapters", src);
                Assert.DoesNotContain("StockManager.Engines", src);
            }
        }
    }
}
